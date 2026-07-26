// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.Diagnostics;
using Vixen.Raven;
using Vixen.Raven.Artefacts;
using Vixen.Raven.IR;
using Vixen.Raven.Lowering;
using Vixen.Raven.Symbols;
using Vixen.Raven.Syntax;
using Xunit;
using static Tests.CodeGenTestBase;

namespace Tests;

/// <summary>
///     <c>inout</c>: a parameter the caller passes by reference, with copy-in/copy-out semantics.
/// </summary>
/// <remarks>
///     <para>
///         Written for <c>Material/MaterialSurface.rvn</c>, the composable material interface whose
///         contract docs/plan/07 specifies as
///         <c>protocol IMaterialSurface { func Compute(inout MaterialData d) }</c> — a feature
///         accumulates into a shared surface, which is Stride's model and reads as a mutation rather
///         than a fold.
///     </para>
///     <para>
///         Copy-in/copy-out rather than aliasing, and that is a specification: GLSL defines its own
///         <c>inout</c> the same way, and SPIR-V has no reference type, so a promise of aliasing
///         could not have been kept on either target. The lowering makes the copies explicit in the
///         IR rather than leaning on each language's rules and hoping they agree.
///     </para>
/// </remarks>
public class InOutTests {
    const string Surface = """
                           package A

                           struct Surface {
                               var color: float3
                               var roughness: float
                           }

                           struct Feature {
                               static func Apply(inout s: Surface, tint: float3) {
                                   s.color = s.color * tint
                                   s.roughness = saturate(s.roughness + 0.25f)
                               }
                           }

                           shader Lit {
                               var baseColor: float3

                               [PixelShader]
                               [Semantic("SV_Target")]
                               func Pixel(): float4 {
                                   var s: Surface
                                   s.color = baseColor
                                   s.roughness = 0.5f

                                   Feature.Apply(s, float3(0.5f, 1f, 2f))

                                   return float4(s.color, s.roughness)
                               }
                           }

                           """;

    // --- The front end ----------------------------------------------------

    [Fact]
    public void AnInOutParameterParsesAndRoundTrips() {
        var tree = SyntaxTree.ParseText(Surface, path: "Test.rvn");
        Assert.Empty(tree.Diagnostics);
        Assert.Equal(Surface, tree.GetRoot().ToFullString());
    }

    [Fact]
    public void TheDirectionReachesTheSymbol() {
        var compilation = Compilation.Create("Test", SyntaxTree.ParseText(Surface, path: "Test.rvn"));
        Assert.Empty(compilation.GetDiagnostics());

        var apply = FindMethod(compilation, "Feature", "Apply");

        Assert.Equal(RefKind.InOut, apply.Parameters[0].RefKind);
        Assert.Equal(RefKind.None, apply.Parameters[1].RefKind);

        // And it shows in the signature, so a diagnostic naming the method says which way it passes.
        Assert.Contains("inout s: A.Surface", apply.ToDisplayString(), StringComparison.Ordinal);
    }

    // --- What it refuses --------------------------------------------------

    /// <summary>
    ///     A literal has no storage to write back to.
    /// </summary>
    /// <remarks>
    ///     Reported after overload resolution rather than as part of it, so the message names the
    ///     parameter. Folding it into applicability would say "no overload applies", which is true
    ///     and useless.
    /// </remarks>
    [Fact]
    public void ALiteralArgumentIsRefused() {
        Assert.Contains(Diagnose("Take(1f)"), d => d.Id == "RVN2110" && d.IsError);
    }

    /// <summary>A `val` is assignable nowhere, and reuses the read-only message.</summary>
    [Fact]
    public void AReadOnlyArgumentIsRefusedWithTheAssignmentsReason() {
        var diagnostics = Diagnose("val locked = 2f\n        Take(locked)");
        Assert.Contains(diagnostics, d => d.Id == "RVN2040" && d.IsError);
    }

    /// <summary>
    ///     A widening conversion is refused, which is the rule people are surprised by.
    /// </summary>
    /// <remarks>
    ///     `int` to `inout float` would have to narrow back on the way out, losing whatever the
    ///     callee wrote. Overload resolution lets the implicit conversion through — correct for a
    ///     by-value parameter — so this is checked separately and reports the operand's type rather
    ///     than the wrapper's.
    /// </remarks>
    [Fact]
    public void AWidenedArgumentIsRefused() {
        var diagnostics = Diagnose("var i = 3\n        Take(i)");
        var error = Assert.Single(diagnostics, d => d.Id == "RVN2111");
        Assert.Contains("'int'", error.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("'float'", error.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnInOutParameterCannotHaveADefault() {
        var diagnostics = Compile(
            """
            package A

            struct S {
                static func Take(inout x: float = 1f) { }
            }

            """
        );

        Assert.Contains(diagnostics, d => d.Id == "RVN2113" && d.IsError);
    }

    [Fact]
    public void AnOperatorParameterCannotBeInOut() {
        var diagnostics = Compile(
            """
            package A

            struct S {
                var v: float

                S operator +(inout a: S, b: S) => a
            }

            """
        );

        Assert.Contains(diagnostics, d => d.Id == "RVN2114" && d.IsError);
    }

    /// <summary>
    ///     An entry point's parameters come from the pipeline, which has nowhere to copy back to.
    /// </summary>
    [Fact]
    public void AnEntryPointParameterCannotBeInOut() {
        var diagnostics = Compile(
            """
            package A

            shader S {
                [PixelShader]
                [Semantic("SV_Target")]
                func Pixel(inout uv: float2): float4 {
                    return float4(uv, 0f, 1f)
                }
            }

            """
        );

        Assert.Contains(diagnostics, d => d.Id == "RVN2112" && d.IsError);
    }

    // --- Lowering ---------------------------------------------------------

    /// <summary>
    ///     The call site copies in, passes a temp by reference, and copies out.
    /// </summary>
    /// <remarks>
    ///     The temp is not an optimisation choice. SPIR-V requires a pointer argument to
    ///     <c>OpFunctionCall</c> to be a memory object declaration, so an access chain such as
    ///     <c>d.color</c> could not be handed over at all, and a global's storage class could never
    ///     match the parameter's <c>Function</c>. Pinned here because a later change that "optimised
    ///     away" the temp would produce SPIR-V the validator rejects.
    /// </remarks>
    [Fact]
    public void TheCallSiteCopiesThroughALocalTemp() {
        var module = Lower(Surface);
        var pixel = FindFunction(module, "Pixel");

        var temp = Assert.Single(pixel.Locals, local => local.Name.Contains("#inout", StringComparison.Ordinal));

        var call = Assert.Single(Calls(pixel.Body));
        var reference = Assert.Single(call.Arguments, a => a.IsByReference);
        Assert.Same(temp, reference.Reference);

        // The other argument stays by value: direction is per parameter, not per call.
        Assert.Contains(call.Arguments, a => !a.IsByReference);
    }

    /// <summary>The callee's parameter is marked by-reference, and prints as one.</summary>
    [Fact]
    public void TheCalleeParameterIsByReference() {
        var module = Lower(Surface);
        var apply = FindFunction(module, "Apply");

        Assert.True(apply.Parameters[0].IsByReference);
        Assert.False(apply.Parameters[1].IsByReference);

        // `&` in a dump, so which parameters the caller sees writes through is readable.
        Assert.Contains("&s", IrPrinter.Print(module), StringComparison.Ordinal);
    }

    /// <summary>
    ///     The verifier refuses a call whose direction disagrees with the callee's.
    /// </summary>
    /// <remarks>
    ///     A value where a reference belongs loses the callee's write; a reference where a value
    ///     belongs is a pointer SPIR-V would reject. Both are caught before a backend sees them.
    /// </remarks>
    [Fact]
    public void TheVerifierRefusesMismatchedDirection() {
        var shader = new IrShader("S");

        var callee = new IrFunction("Callee", IrScalarType.Void);
        callee.AddParameter("x", IrScalarType.Float, true);
        callee.Body.Add(new IrReturnStatement(null));

        var caller = new IrFunction("Caller", IrScalarType.Void);
        var value = new IrValue(0, IrScalarType.Float);
        caller.Body.Add(new IrConstantInstruction(value, 1f));

        // By value, where the callee wants a reference.
        caller.Body.Add(new IrCallInstruction(null, callee, [IrArgument.Of(value)]));
        caller.Body.Add(new IrReturnStatement(null));

        shader.Add(callee);
        shader.Add(caller);

        var module = new IrModule("Test");
        module.Add(shader);

        var bag = new DiagnosticBag();
        Assert.False(IrVerifier.Verify(module, bag));
        Assert.Contains(bag.ToArray(), d => d.GetMessage().Contains("by value", StringComparison.Ordinal));
    }

    // --- Both backends ----------------------------------------------------

    /// <summary>GLSL uses its own <c>inout</c>, whose meaning is the same.</summary>
    [Fact]
    public void GlslDeclaresInOutNatively() {
        var glsl = GenerateOne(Surface);

        Assert.Contains("void Apply(inout Surface s, vec3 tint)", glsl, StringComparison.Ordinal);

        // And the copies are there rather than left to GLSL to infer.
        Assert.Contains("s_inout = ", glsl, StringComparison.Ordinal);
        Assert.Contains("Apply(s_inout,", glsl, StringComparison.Ordinal);
    }

    /// <summary>
    ///     SPIR-V passes a pointer into function storage, which is the only shape it accepts.
    /// </summary>
    [Fact]
    public void SpirvPassesAFunctionStoragePointer() {
        var unit = SpirvTestBase.One(Surface);

        Assert.Contains("OpTypePointer Function", unit.Code, StringComparison.Ordinal);

        // The verdict that matters: a pointer argument has to be a memory object declaration and the
        // storage classes have to match, and only the validator knows whether they do.
        SpirvTestBase.Validate(unit);
    }

    /// <summary>
    ///     Both reference tools accept it, which is the claim the design rests on.
    /// </summary>
    [Theory]
    [InlineData("glsl")]
    [InlineData("spirv")]
    public void InOutReachesBothBackends(string target) {
        var generated = GenerateClean(Surface, target);

        if (target == "spirv") {
            Assert.All(generated, SpirvTestBase.Validate);
        }
    }

    // --- The mutation is actually observed --------------------------------

    /// <summary>
    ///     The caller reads back what the callee wrote — the whole point, checked structurally.
    /// </summary>
    /// <remarks>
    ///     Structurally rather than numerically because there is no device to run on: what is
    ///     checkable is that the copy-out store lands in the caller's own storage, after the call,
    ///     and that the value read afterwards comes from there. A numeric check is the GPU-readback
    ///     test in § G.
    /// </remarks>
    [Fact]
    public void TheCopyOutStoresIntoTheCallersStorage() {
        var module = Lower(Surface);
        var pixel = FindFunction(module, "Pixel");

        var statements = Flatten(pixel.Body).ToArray();
        var callIndex = Array.FindIndex(statements, s => s is IrCallInstruction);
        Assert.True(callIndex >= 0);

        var temp = Assert.Single(pixel.Locals, local => local.Name.Contains("#inout", StringComparison.Ordinal));
        var target = Assert.Single(pixel.Locals, local => local.Name == "s");

        // After the call: a load of the temp, then a store into the caller's own local.
        var copyOut = statements
            .Skip(callIndex + 1)
            .OfType<IrStoreInstruction>()
            .FirstOrDefault(store => ReferenceEquals(store.Place.Root, target));

        Assert.NotNull(copyOut);

        var loaded = statements
            .Skip(callIndex + 1)
            .OfType<IrLoadInstruction>()
            .FirstOrDefault(load => ReferenceEquals(load.Place.Root, temp));

        Assert.NotNull(loaded);
        Assert.Equal(loaded.Result.Id, copyOut.Value.Id);
    }

    // --- Through a .rvnlib ------------------------------------------------

    /// <summary>
    ///     A library's <c>inout</c> function binds and links the same as its source.
    /// </summary>
    /// <remarks>
    ///     Both halves have to carry the direction: the symbol side so the consumer's binder still
    ///     demands assignable storage, and the IR side so the linked body still declares a
    ///     by-reference parameter. Getting one and not the other would produce a module the verifier
    ///     rejects — which is what makes this worth a test rather than an assumption.
    /// </remarks>
    [Fact]
    public void AnInOutFunctionSurvivesALibraryRoundTrip() {
        const string Library = """
                               package Lib

                               struct Surface {
                                   var color: float3
                                   var roughness: float
                               }

                               struct Feature {
                                   static func Apply(inout s: Surface, tint: float3) {
                                       s.color = s.color * tint
                                       s.roughness = saturate(s.roughness + 0.25f)
                                   }
                               }

                               """;

        var diagnostics = new DiagnosticBag();
        var compilation = Compilation.Create("Lib", SyntaxTree.ParseText(Library, path: "Lib.rvn"));
        Assert.Empty(compilation.GetDiagnostics());

        var built = LibraryBuilder.Build(compilation, Lowerer.LowerWithLinks(compilation, diagnostics), diagnostics);
        Assert.DoesNotContain(diagnostics.ToArray(), d => d.IsError);

        // Through the bytes, so the direction is being read back rather than remembered.
        var reloaded = CompiledLibraryReader.Read(CompiledLibraryWriter.Write(built));

        var apply = reloaded.Types
            .Single(type => type.Name == "Feature")
            .Methods
            .Single(method => method.Name == "Apply");

        Assert.Equal(RefKind.InOut, apply.Parameters[0].RefKind);
        Assert.Equal(RefKind.None, apply.Parameters[1].RefKind);

        // And a consumer binds against it, lowers, verifies and reaches a backend.
        var path = Path.Combine(Directory.CreateTempSubdirectory("raven-inout").FullName, "Lib.rvnlib");
        CompiledLibraryWriter.WriteFile(path, built);

        const string Consumer = """
                                package A

                                import Lib

                                shader Lit {
                                    var baseColor: float3

                                    [PixelShader]
                                    [Semantic("SV_Target")]
                                    func Pixel(): float4 {
                                        var s: Surface
                                        s.color = baseColor
                                        s.roughness = 0.5f
                                        Feature.Apply(s, float3(2f))
                                        return float4(s.color, s.roughness)
                                    }
                                }

                                """;

        var consumer = Compilation.Create(
            "Consumer",
            [RavenReference.FromFile(path)],
            [SyntaxTree.ParseText(Consumer, path: "Consumer.rvn")]
        );

        var consumerDiagnostics = consumer.GetDiagnostics();
        Assert.True(
            consumerDiagnostics.Count == 0,
            string.Join("\n", consumerDiagnostics.Select(d => d.ToString()))
        );

        var bag = new DiagnosticBag();
        var module = Lowerer.Lower(consumer, bag);
        Assert.True(IrVerifier.Verify(module, bag), string.Join("\n", bag.Select(d => d.ToString())));
        Assert.True(bag.IsEmpty, string.Join("\n", bag.Select(d => d.ToString())));

        Assert.True(FindFunction(module, "Apply").Parameters[0].IsByReference);
    }

    /// <summary>
    ///     A consumer of a library's <c>inout</c> function is held to the same argument rules.
    /// </summary>
    [Fact]
    public void ALibrarysInOutStillDemandsAssignableStorage() {
        const string Library = """
                               package Lib

                               struct Feature {
                                   static func Bump(inout x: float) {
                                       x = x + 1f
                                   }
                               }

                               """;

        var diagnostics = new DiagnosticBag();
        var compilation = Compilation.Create("Lib", SyntaxTree.ParseText(Library, path: "Lib.rvn"));
        var built = LibraryBuilder.Build(compilation, Lowerer.LowerWithLinks(compilation, diagnostics), diagnostics);

        var path = Path.Combine(Directory.CreateTempSubdirectory("raven-inout-bad").FullName, "Lib.rvnlib");
        CompiledLibraryWriter.WriteFile(path, built);

        const string Consumer = """
                                package A

                                import Lib

                                struct Caller {
                                    static func Go() {
                                        Feature.Bump(1f)
                                    }
                                }

                                """;

        var consumer = Compilation.Create(
            "Consumer",
            [RavenReference.FromFile(path)],
            [SyntaxTree.ParseText(Consumer, path: "Consumer.rvn")]
        );

        Assert.Contains(consumer.GetDiagnostics(), d => d.Id == "RVN2110" && d.IsError);
    }

    // --- Plumbing ---------------------------------------------------------

    /// <summary>Compiles a body that calls a `Take(inout x: float)` helper.</summary>
    static IReadOnlyList<Diagnostic> Diagnose(string body) =>
        Compile(
            $$"""
              package A

              struct S {
                  static func Take(inout x: float) {
                      x = 1f
                  }

                  static func Uses() {
                      {{body}}
                  }
              }

              """
        );

    static IReadOnlyList<Diagnostic> Compile(string source) =>
        Compilation.Create("Test", SyntaxTree.ParseText(source, path: "Test.rvn")).GetDiagnostics();

    static IrModule Lower(string source) {
        var compilation = Compilation.Create("Test", SyntaxTree.ParseText(source, path: "Test.rvn"));
        Assert.Empty(compilation.GetDiagnostics());

        var bag = new DiagnosticBag();
        var module = Lowerer.Lower(compilation, bag);
        Assert.True(IrVerifier.Verify(module, bag), string.Join("\n", bag.Select(d => d.ToString())));
        Assert.True(bag.IsEmpty, string.Join("\n", bag.Select(d => d.ToString())));
        return module;
    }

    static MethodSymbol FindMethod(Compilation compilation, string type, string method) =>
        compilation.GetAllTypes()
            .Single(candidate => candidate.Name == type)
            .GetMembers()
            .OfType<MethodSymbol>()
            .Single(candidate => candidate.Name == method);

    static IrFunction FindFunction(IrModule module, string name) =>
        module.Shaders
            .SelectMany(shader => shader.Functions)
            .Concat(module.Functions)
            .Single(function => function.Name.EndsWith(name, StringComparison.Ordinal));

    static IEnumerable<IrCallInstruction> Calls(IrBlock block) => Flatten(block).OfType<IrCallInstruction>();

    static IEnumerable<IrStatement> Flatten(IrStatement statement) {
        yield return statement;

        switch (statement) {
            case IrBlock block:
                foreach (var nested in block.Statements.SelectMany(Flatten)) {
                    yield return nested;
                }

                break;

            case IrIfStatement conditional:
                foreach (var nested in Flatten(conditional.Then)) {
                    yield return nested;
                }

                if (conditional.Else is { } otherwise) {
                    foreach (var nested in Flatten(otherwise)) {
                        yield return nested;
                    }
                }

                break;
        }
    }
}
