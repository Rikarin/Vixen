// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.Diagnostics;
using Vixen.Raven.Artefacts;
using Vixen.Raven.CodeGen.Glsl;
using Vixen.Raven.CodeGen.Spirv;
using Vixen.Raven.IR;
using Vixen.Raven.Symbols;
using Xunit;
using static Tests.LoweringTestBase;

namespace Tests;

/// <summary>
///     <c>[NoContraction]</c>: the author saying this body's arithmetic is a comparison rather than
///     a picture.
/// </summary>
/// <remarks>
///     <para>
///         <b>The one attribute that changes no result any specification names.</b> A target may fold
///         a multiply and an add into one instruction that rounds once, and may decline to; two
///         compilations of one source can therefore land a last-place bit apart with every opcode,
///         every operand and every constant identical. That is what
///         <a href="https://github.com/Rikarin/Vixen/issues/1190">#1190</a> is made of — the GLSL
///         copy of the UI box shader and the Raven one draw an antialiased edge 1/255 apart on
///         lavapipe and bit-identically on three other drivers — and until this attribute existed
///         Raven had no way to say so at all.
///     </para>
///     <para>
///         ⚠ <b>What these fixtures cannot say.</b> No Vulkan SDK was installed on the machine this
///         landed on, so <c>spirv-val</c> did not run — <c>SpirvTestBase.Validate</c> returns
///         silently when the tool is missing — and no device ran either. So this file proves the
///         decoration is emitted, on the right instructions and on no others; it does not prove that
///         a validator accepts it or that the 1/255 goes. Those are the ubuntu leg's, and they are
///         what remains on #1190.
///     </para>
///     <para>
///         ⚠ <b>The negative half is the load-bearing one.</b> An emitter that decorated every
///         instruction it wrote would satisfy "the marked body is decorated" perfectly and produce a
///         module a validator refuses, so every fixture below that asserts a decoration also asserts
///         where there is none: an unmarked neighbour, a callee, and every instruction that is not
///         arithmetic.
///     </para>
/// </remarks>
public class NoContractionTests {
    /// <summary>
    ///     Two functions of the same shape, one marked and one not, so every assertion reads one
    ///     source and a decoration that stopped being scoped cannot hide behind a fixture of its own.
    /// </summary>
    const string Source = """
                          package A

                          shader Edge {
                              [NoContraction]
                              func Fused(a: float, b: float, c: float): float {
                                  return a * b + c
                              }

                              func Free(a: float, b: float, c: float): float {
                                  return a * b + c
                              }

                              [FragmentShader]
                              func Shade(): float4 {
                                  return float4(Fused(1f, 2f, 3f), Free(1f, 2f, 3f), 0f, 1f)
                              }
                          }

                          """;

    /// <summary>The SPIR-V listing of the one stage, with no validator run on this machine.</summary>
    static string Spirv(string source) {
        var bag = new DiagnosticBag();
        var generated = new SpirvBackend().Generate(Lower(source), bag);

        Assert.DoesNotContain(bag.ToArray(), d => d.IsError);

        var unit = Assert.Single(generated, u => u.Stage == ShaderStage.Fragment);

        // Runs spirv-val where it is installed and returns without a word where it is not, which is
        // this machine. Called anyway so the CI leg that has one judges these modules too.
        SpirvTestBase.Validate(unit);

        return unit.Code;
    }

    /// <summary>The lines of one function's body, found by the name the module gave it.</summary>
    /// <remarks>
    ///     ⚠ Attributed to a function rather than counted over the module, because "the marked body
    ///     and nothing else" is the claim, and a module-wide count is satisfied by a decoration on
    ///     the wrong function. The listing is linear — <c>%n = OpFunction</c> to
    ///     <c>OpFunctionEnd</c> — so the boundary is readable without a disassembler.
    /// </remarks>
    static string[] Body(string listing, string name) {
        var id = SpirvTestBase.IdNamed(listing, name);
        var lines = listing.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        var first = Array.FindIndex(lines, line => line.StartsWith($"{id} = OpFunction ", StringComparison.Ordinal));

        Assert.True(first >= 0, $"no OpFunction for {name} ({id})");

        var last = Array.FindIndex(lines, first, lines.Length - first, line => line == "OpFunctionEnd");

        Assert.True(last > first, $"no OpFunctionEnd after {name}");

        return lines[first..last];
    }

    /// <summary>The result ids of the arithmetic a body emitted, in listing order.</summary>
    static string[] Arithmetic(IEnumerable<string> body) =>
        [
            .. body
                .Where(line => line.Contains(" = Op", StringComparison.Ordinal))
                .Where(line => Contractible.Any(op => line.Contains($" = {op} ", StringComparison.Ordinal)))
                .Select(line => line.Split(' ')[0])
        ];

    static readonly string[] Contractible = [
        "OpFAdd", "OpFSub", "OpFMul", "OpFDiv", "OpFRem", "OpFMod", "OpVectorTimesScalar",
        "OpMatrixTimesScalar", "OpVectorTimesMatrix", "OpMatrixTimesVector", "OpMatrixTimesMatrix",
        "OpDot"
    ];

    /// <summary>Every id the listing decorates <c>NoContraction</c>.</summary>
    static string[] Decorated(string listing) =>
        [
            .. listing.Split('\n')
                .Select(line => line.TrimEnd('\r'))
                .Where(line => line.StartsWith("OpDecorate ", StringComparison.Ordinal))
                .Where(line => line.EndsWith(" NoContraction", StringComparison.Ordinal))
                .Select(line => line.Split(' ')[1])
        ];

    // --- The declaration reaches the IR ------------------------------------

    /// <summary>The attribute arrives on the function, and a function without it says false.</summary>
    /// <remarks>
    ///     Asserted before either backend, because a flag that never left the binder would make
    ///     every emitter assertion below fail as "the decoration is missing" rather than as "the
    ///     emitter drops it", and those want different fixes.
    /// </remarks>
    [Fact]
    public void TheDeclaredFlagReachesTheIr() {
        var functions = FindShader(Lower(Source), "Edge").Functions.ToDictionary(f => f.Name, f => f.NoContraction);

        Assert.True(functions["Fused"]);
        Assert.False(functions["Free"]);
        Assert.False(functions["Shade"]);
    }

    // --- SPIR-V: the marked body and nothing else --------------------------

    /// <summary>Every arithmetic result of the marked body is decorated, and its neighbour's is not.</summary>
    [Fact]
    public void SpirvDecoratesTheMarkedBodysArithmetic() {
        var listing = Spirv(Source);
        var fused = Arithmetic(Body(listing, "Fused"));
        var free = Arithmetic(Body(listing, "Free"));
        var decorated = Decorated(listing);

        // The instrument first: both bodies are `a * b + c`, so a listing where either has no
        // arithmetic means the shape under test was optimised or lowered away and every assertion
        // after it would be about nothing.
        Assert.Equal(2, fused.Length);
        Assert.Equal(2, free.Length);

        Assert.All(fused, id => Assert.Contains(id, decorated));
        Assert.All(free, id => Assert.DoesNotContain(id, decorated));
    }

    /// <summary>And nothing that is not arithmetic is decorated.</summary>
    /// <remarks>
    ///     ⚠ <b>The half that stands in for the validator this machine has not got.</b> SPIR-V's
    ///     <c>NoContraction</c> applies to an arithmetic instruction; a module that decorated a load,
    ///     a composite construct or a function parameter is invalid, and the first thing that would
    ///     say so is a CI leg eighteen hours later. Asserted over the whole listing rather than over
    ///     the marked body, so a stray decoration anywhere is caught.
    /// </remarks>
    [Fact]
    public void SpirvDecoratesNothingThatIsNotArithmetic() {
        var listing = Spirv(Source);
        var decorated = Decorated(listing);

        Assert.NotEmpty(decorated);

        foreach (var id in decorated) {
            var definition = Assert.Single(
                listing.Split('\n').Select(line => line.TrimEnd('\r')),
                line => line.StartsWith($"{id} = Op", StringComparison.Ordinal)
            );

            Assert.Contains(Contractible, op => definition.Contains($" = {op} ", StringComparison.Ordinal));
        }
    }

    /// <summary>A marked function does not mark what it calls.</summary>
    /// <remarks>
    ///     The rule a reader can check from one declaration, and the difference from GLSL's
    ///     <c>precise</c>, which propagates backwards through everything that contributed to a marked
    ///     value. Making the attribute transitive would be a defensible language and a different one;
    ///     what is not defensible is leaving which of the two it is to whoever reads the emitter.
    /// </remarks>
    [Fact]
    public void ItDoesNotReachACallee() {
        const string source = """
                              package A

                              shader Edge {
                                  func Helper(a: float, b: float): float {
                                      return a * b
                                  }

                                  [NoContraction]
                                  func Fused(a: float, b: float, c: float): float {
                                      return Helper(a, b) + c
                                  }

                                  [FragmentShader]
                                  func Shade(): float4 {
                                      return float4(Fused(1f, 2f, 3f), 0f, 0f, 1f)
                                  }
                              }

                              """;

        var listing = Spirv(source);
        var decorated = Decorated(listing);

        var fused = Arithmetic(Body(listing, "Fused"));
        var helper = Arithmetic(Body(listing, "Helper"));

        var fusedOnly = Assert.Single(fused);
        var helperOnly = Assert.Single(helper);

        Assert.Contains(fusedOnly, decorated);
        Assert.DoesNotContain(helperOnly, decorated);
    }

    /// <summary>A marked method of a generic struct is still marked in every instantiation.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The path the flag was likeliest to be lost on, and the only one of the three the
    ///         first landing did not cover.</b> A generic's body is bound once, against the open
    ///         definition, and lowered once per instantiation through a substitution — so the symbol
    ///         the lowering reads is a <c>SubstitutedMethodSymbol</c> and not the one the file was
    ///         written for. Sabotaging that symbol's forwarder left the whole 2049-test suite
    ///         unmoved, which is what this fixture is here to stop: nothing reached it.
    ///     </para>
    ///     <para>
    ///         Asserted through SPIR-V rather than through the IR flag, because the claim is about
    ///         what the instantiated body emits. Two methods of the same shape again, so the
    ///         instantiation is compared against its own neighbour and not against another fixture's
    ///         module.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AnInstantiationKeepsTheDeclarationsFlag() {
        const string source = """
                              package A

                              struct Box<T> {
                                  var value: T

                                  [NoContraction]
                                  func Fused(a: float, b: float): float {
                                      return a * b + a
                                  }

                                  func Free(a: float, b: float): float {
                                      return a * b + a
                                  }
                              }

                              shader Edge {
                                  [FragmentShader]
                                  func Shade(): float4 {
                                      var box: Box<float>
                                      box.value = 2f
                                      return float4(box.Fused(1f, 3f), box.Free(1f, 3f), 0f, 1f)
                                  }
                              }

                              """;

        // The instrument first: the open definition is emitted nowhere, so a listing naming `Fused`
        // rather than the instantiation would mean monomorphisation did not happen and the fixture
        // is about nothing. ⚠ Off the module and not off the shader: an instantiation of a
        // struct's method is a module-level function, which is the whole point of the name.
        var names = Lower(source).Functions.Select(f => f.Name).ToArray();

        Assert.Contains("Box_float_Fused", names);
        Assert.DoesNotContain("Fused", names);

        var listing = Spirv(source);
        var decorated = Decorated(listing);

        var fused = Arithmetic(Body(listing, "Box_float_Fused"));
        var free = Arithmetic(Body(listing, "Box_float_Free"));

        Assert.Equal(2, fused.Length);
        Assert.Equal(2, free.Length);

        Assert.All(fused, id => Assert.Contains(id, decorated));
        Assert.All(free, id => Assert.DoesNotContain(id, decorated));
    }

    // --- GLSL: the target that cannot say it -------------------------------

    /// <summary>The GLSL backend reports what it is dropping rather than dropping it quietly.</summary>
    /// <remarks>
    ///     ⚠ <b>Info rather than an error, and a report rather than an emission.</b> GLSL's
    ///     <c>precise</c> is a different rule — it propagates from an output backwards — so writing
    ///     the keyword here would be a different request under the same name, against a target this
    ///     compiler could not run a validator over on the machine that landed it. A shader that asked
    ///     for bit-stable arithmetic and was compiled to GLSL is told it did not get it.
    /// </remarks>
    [Fact]
    public void TheGlslBackendSaysItDroppedIt() {
        var bag = new DiagnosticBag();
        new GlslBackend().Generate(Lower(Source), bag);

        var reported = Assert.Single(bag.ToArray(), d => d.Id == "RVN4003");

        Assert.False(reported.IsError);
        Assert.Contains("Fused", reported.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("precise", reported.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>And says nothing about a shader that asked for nothing.</summary>
    [Fact]
    public void TheGlslBackendIsSilentWithoutTheAttribute() {
        const string source = """
                              package A

                              shader Edge {
                                  func Free(a: float, b: float, c: float): float {
                                      return a * b + c
                                  }

                                  [FragmentShader]
                                  func Shade(): float4 {
                                      return float4(Free(1f, 2f, 3f), 0f, 0f, 1f)
                                  }
                              }

                              """;

        var bag = new DiagnosticBag();
        new GlslBackend().Generate(Lower(source), bag);

        Assert.DoesNotContain(bag.ToArray(), d => d.Id == "RVN4003");
    }

    // --- The library, which is where a flag gets lost ----------------------

    /// <summary>A function written to a library and read back still forbids fusing.</summary>
    /// <remarks>
    ///     ⚠ <b>The consumer has no declaration to read it from.</b> A linked function is decoded
    ///     from the artefact and lowered from nothing, so a flag the codec dropped would mean a
    ///     library function its author marked is fused anyway in every consumer — and a request about
    ///     the last bit of a float is precisely the kind that fails no test.
    ///     <para>
    ///         ⚠ <b>Through the real bytes and not only the codec.</b> The codec is half the path:
    ///         a <c>.rvnlib</c> is <c>System.Text.Json</c> over the encoded record, and an in-memory
    ///         encode/decode pair says nothing about a property the serializer does not carry. So
    ///         this writes and reads the artefact as well. ⚠ The earlier claim that "an artefact
    ///         written before this existed says false by absence, so an older reader can only lose
    ///         it" is <b>moot</b>: <c>CompiledLibraryReader</c> compares the version for exact
    ///         equality, so a reader of any other version refuses the file outright rather than
    ///         misreading it. What the property being additive actually buys is that
    ///         <c>Version</c> did not have to move.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ALibraryCarriesTheFlagAcross() {
        var function = new IrFunction("Fused", IrScalarType.Float) { NoContraction = true };
        var plain = new IrFunction("Free", IrScalarType.Float);

        var encoded = LibraryIrEncoder.Encode(
            [],
            [function, plain],
            s => s.Name,
            s => s.Name,
            f => "Test::" + f.Name
        );

        Assert.True(Assert.Single(encoded.Functions, f => f.Name == "Fused").NoContraction);
        Assert.False(Assert.Single(encoded.Functions, f => f.Name == "Free").NoContraction);

        var decoder = new LibraryIrDecoder(name => name);
        decoder.DecodeStructs(encoded);
        decoder.DecodeFunctions();

        Assert.True(decoder.Functions.Values.Single(f => f.Name == "Fused").NoContraction);
        Assert.False(decoder.Functions.Values.Single(f => f.Name == "Free").NoContraction);

        // And the same encoded record through the bytes a consumer is actually handed, because the
        // serializer is the layer that drops a property nobody listed.
        var written = CompiledLibraryWriter.Write(new() { Name = "Test", Ir = encoded });
        var read = CompiledLibraryReader.Read(written);

        Assert.True(Assert.Single(read.Ir.Functions, f => f.Name == "Fused").NoContraction);
        Assert.False(Assert.Single(read.Ir.Functions, f => f.Name == "Free").NoContraction);
    }
}
