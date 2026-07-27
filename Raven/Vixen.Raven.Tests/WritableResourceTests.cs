// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.Diagnostics;
using Vixen.Raven;
using Vixen.Raven.Artefacts;
using Vixen.Raven.CodeGen;
using Vixen.Raven.Diagnostics;
using Vixen.Raven.IR;
using Vixen.Raven.Lowering;
using Vixen.Raven.Reflection;
using Vixen.Raven.Symbols;
using Vixen.Raven.Syntax;
using Xunit;

namespace Tests;

/// <summary>
///     Storage buffers: <c>Buffer&lt;T&gt;</c> read-only, <c>RWBuffer&lt;T&gt;</c> read-write — the
///     first thing a Raven shader can write to.
/// </summary>
/// <remarks>
///     <para>
///         Until this existed, a shader could compute and not persist: the compute stage had nothing to
///         store into, and assigning to a uniform was refused by nobody while both reference compilers
///         rejected the store. Those two facts were the same gap seen from either end, and closing one
///         is what made the other worth reporting — <c>RVN2119</c> can now say what to write instead.
///     </para>
///     <para>
///         Written with angle brackets and <em>not</em> generic. It is a structural type the binder
///         constructs directly, the same treatment <c>T[4]</c> gets — which is why it worked before
///         monomorphisation existed and why it needs none now: there is no declaration to
///         instantiate and no body to substitute through.
///     </para>
/// </remarks>
public class WritableResourceTests {
    const string Simulate = """
                            package A

                            struct Particle {
                                var position: float3
                                var life: float
                                var velocity: float3
                                var size: float
                            }

                            shader Simulate {
                                var deltaTime: float = 0.016f
                                var gravity: float3

                                var particles: RWBuffer<Particle>
                                var spawn: Buffer<Particle>

                                [ComputeShader(64)]
                                func Main([Semantic("SV_DispatchThreadID")] id: uint3) {
                                    val i = int(id.x)
                                    if (i >= particles.Length) {
                                        return
                                    }

                                    var p = particles[i]

                                    if (p.life <= 0f) {
                                        p = spawn[i]
                                    }

                                    p.velocity = p.velocity + gravity * deltaTime
                                    p.position = p.position + p.velocity * deltaTime
                                    p.life = p.life - deltaTime

                                    particles[i] = p
                                }
                            }

                            """;

    // --- The front end ----------------------------------------------------

    [Fact]
    public void ABufferParsesAndRoundTrips() {
        var tree = SyntaxTree.ParseText(Simulate, path: "Test.rvn");
        Assert.Empty(tree.Diagnostics);
        Assert.Equal(Simulate, tree.GetRoot().ToFullString());
    }

    [Fact]
    public void TheElementTypeAndDirectionReachTheSymbol() {
        var compilation = Compilation.Create("Test", SyntaxTree.ParseText(Simulate, path: "Test.rvn"));
        Assert.Empty(compilation.GetDiagnostics());

        var writable = Assert.IsType<BufferTypeSymbol>(FieldOf(compilation, "particles").Type);
        Assert.True(writable.IsWritable);
        Assert.True(writable.IsWritableResource);
        Assert.Equal("Particle", writable.ElementType.Name);
        Assert.Equal("RWBuffer<A.Particle>", writable.ToDisplayString());

        var readOnly = Assert.IsType<BufferTypeSymbol>(FieldOf(compilation, "spawn").Type);
        Assert.False(readOnly.IsWritable);
        Assert.False(readOnly.IsWritableResource);
        Assert.Equal("Buffer<A.Particle>", readOnly.ToDisplayString());

        // Read-only and read-write are different types: one descriptor, but a store into the first
        // is an error, so making them equal would make the error unreachable.
        Assert.NotEqual(readOnly, writable);
        Assert.Equal(ResourceKind.StorageBuffer, writable.ResourceKind);
    }

    /// <summary>
    ///     A buffer's element has to be something the host can lay out in memory.
    /// </summary>
    /// <remarks>
    ///     A texture or a sampler is a descriptor rather than a value and has no bytes to lay out; a
    ///     nested buffer is a second descriptor, which is what a pointer would be and Raven has none.
    /// </remarks>
    [Theory]
    [InlineData("Buffer<Texture2D>")]
    [InlineData("Buffer<Sampler>")]
    [InlineData("RWBuffer<Texture2D>")]
    public void AnElementWithNoLayoutIsRefused(string type) {
        Assert.Contains(Declare($"var data: {type}"), d => d.Id == "RVN2118" && d.IsError);
    }

    /// <summary>
    ///     A nested buffer is refused by the element rule, not by the shift token.
    /// </summary>
    /// <remarks>
    ///     Worth its own test because it used to be the other way round:
    ///     <c>Buffer&lt;Buffer&lt;float&gt;&gt;</c> ends in a <c>&gt;&gt;</c>, the parser did not
    ///     split it, and a type error came back as <c>RVN1001</c> — a syntax error about a
    ///     construct whose syntax was fine. Now that the parser splits it
    ///     (<see cref="NestedGenericTests" />), the diagnostic is about the type, which is what
    ///     the author has to change.
    /// </remarks>
    [Fact]
    public void ANestedBufferIsRefusedByTheElementCheck() {
        var diagnostics = Declare("var data: Buffer<Buffer<float>>");

        Assert.Contains(diagnostics, d => d.Id == "RVN2118" && d.IsError);
        Assert.DoesNotContain(diagnostics, d => d.Id == "RVN1001");
    }

    [Fact]
    public void ABufferNeedsExactlyOneElementType() {
        var error = Assert.Single(Declare("var data: Buffer<float, int>"), d => d.Id == "RVN2004");
        Assert.True(error.IsError);
    }

    // --- What may be written ----------------------------------------------

    /// <summary>
    ///     Assigning to a binding the host uploads, which was refused by nobody until now.
    /// </summary>
    /// <remarks>
    ///     Pre-existing and stage-independent: every stage emitted the store, and GLSL's read-only
    ///     uniform and SPIR-V's non-writable <c>Uniform</c> pointer both rejected it. It went
    ///     unreported for as long as it did because a shader with nothing writable had no correct
    ///     alternative to point at.
    /// </remarks>
    [Theory]
    [InlineData("tint = float4(1f)")]
    [InlineData("tint.r = 1f")]
    [InlineData("tint.rgb = float3(1f)")]
    [InlineData("readOnly[0] = 1f")]
    [InlineData("sized[0] = 1f")]
    public void AWriteToAReadOnlyBindingIsRefused(string statement) {
        var diagnostics = Body(statement);
        var error = Assert.Single(diagnostics, d => d.Id == "RVN2119");
        Assert.True(error.IsError);
    }

    /// <summary>
    ///     A read-only buffer's message names the writable form, because that is the whole fix.
    /// </summary>
    [Fact]
    public void TheReadOnlyBufferMessageNamesTheAlternative() {
        var error = Assert.Single(Body("readOnly[0] = 1f"), d => d.Id == "RVN2119");
        Assert.Contains("RWBuffer", error.GetMessage(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("writable[0] = 1f")]
    [InlineData("var local = 1f\n        local = 2f")]
    public void AWriteToSomethingWritableIsAllowed(string statement) {
        Assert.DoesNotContain(Body(statement), d => d.Id == "RVN2119");
    }

    /// <summary>
    ///     The check finds the binding at the <em>root</em> of the access chain, not at the target.
    /// </summary>
    /// <remarks>
    ///     <c>particles[i].position.x = 1f</c> is a write to <c>particles</c>, and only the innermost
    ///     expression says which binding that is. Checking the target alone would have let every
    ///     member write through.
    /// </remarks>
    [Fact]
    public void TheCheckWalksToTheRootOfTheChain() {
        var source = """
                     package A

                     struct P {
                         var position: float3
                     }

                     shader S {
                         var readOnly: Buffer<P>
                         var writable: RWBuffer<P>

                         [ComputeShader(1)]
                         func Main([Semantic("SV_DispatchThreadID")] id: uint3) {
                             writable[0].position.x = 1f
                             readOnly[0].position.x = 1f
                         }
                     }

                     """;

        var error = Assert.Single(Compile(source), d => d.Id == "RVN2119");
        Assert.Contains("'readOnly'", error.GetMessage(), StringComparison.Ordinal);
    }

    // --- Lowering ---------------------------------------------------------

    /// <summary>
    ///     A buffer lowers to a runtime-sized array, which is the one position the IR allows one.
    /// </summary>
    /// <remarks>
    ///     Exactly the spec's rule — an unsized array may only be a storage block's last member — so
    ///     the IR now expresses what the targets allow rather than a superset. Everywhere else an
    ///     unsized array stays <c>RVN4001</c>.
    /// </remarks>
    [Fact]
    public void ABufferLowersToARuntimeArray() {
        var module = Lower(Simulate);
        var particles = FindBinding(module, "particles");

        Assert.Equal(IrBindingKind.StorageBuffer, particles.Kind);
        Assert.True(particles.IsWritable);

        var array = Assert.IsType<IrArrayType>(particles.Type);
        Assert.Null(array.Length);
        Assert.Equal("Particle", array.Element.Name);

        Assert.False(FindBinding(module, "spawn").IsWritable);
    }

    /// <summary>
    ///     <c>buffer.Length</c> is an operation on the place, not on a value.
    /// </summary>
    /// <remarks>
    ///     Forced rather than chosen: an unsized array cannot be loaded, so there is nothing to hand
    ///     an intrinsic. Both targets agree — GLSL's <c>data.length()</c> and SPIR-V's
    ///     <c>OpArrayLength</c> each name the block member. This was the defect the first probe found:
    ///     the length silently folded to 0 because the fold only matched a sized array.
    /// </remarks>
    [Fact]
    public void LengthLowersToAPlaceOperation() {
        var module = Lower(Simulate);
        var length = Assert.Single(Flatten(FindFunction(module, "Main").Body).OfType<IrArrayLengthInstruction>());

        Assert.Equal("particles", length.Place.Root.Name);
        Assert.Equal(IrScalarType.Int, length.Result.Type);
        Assert.Contains("length @particles", IrPrinter.Print(module), StringComparison.Ordinal);
    }

    // --- Layout and reflection --------------------------------------------

    /// <summary>
    ///     A storage buffer is laid out std430, and that is the reason it is not just a bigger uniform.
    /// </summary>
    /// <remarks>
    ///     std140 rounds an array's stride up to 16, std430 does not — so an array of <c>float</c>
    ///     costs four bytes per element in a buffer and sixteen in a uniform block. The whole point is
    ///     that a host-side <c>Particle[]</c> uploads as a straight memcpy.
    /// </remarks>
    [Fact]
    public void TheReflectionReportsStd430AndTheHostsCount() {
        var reflection = ReflectionBuilder.Describe(Lower(Simulate).Shaders.Single());
        var bindings = reflection.Sets.SelectMany(set => set.Bindings).ToArray();

        var particles = bindings.Single(b => b.Name == "particles");
        Assert.Equal(DescriptorType.StorageBuffer, particles.Type);
        Assert.True(particles.IsWritable);

        // Count 0 is this schema's spelling for "the host decides"; Size is one element's stride,
        // because the block has no size of its own.
        Assert.Equal(0, particles.Count);
        Assert.Equal(32, particles.Size);

        // Offsets are the element's, relative to the start of one element — which is what the host
        // needs, since the stride is what gets it from one to the next.
        Assert.Equal([0, 0, 12, 16, 28], particles.Members.Select(m => m.Offset));

        Assert.False(bindings.Single(b => b.Name == "spawn").IsWritable);
    }

    /// <summary>
    ///     The same struct laid out both ways gets two sets of offsets, not one.
    /// </summary>
    /// <remarks>
    ///     This is why the SPIR-V backend carries a <c>LayoutRule</c> rather than an is-laid-out flag.
    ///     A <c>float[4]</c> member has a 16-byte stride in a uniform block and a 4-byte one in a
    ///     storage buffer; one "laid out" variant would have given the buffer the block's offsets and
    ///     nothing would have said so.
    /// </remarks>
    [Fact]
    public void Std140AndStd430DisagreeAboutAnArrayMember() {
        var array = new IrArrayType(IrScalarType.Float, 4);

        Assert.Equal(16, ShaderLayout.ArrayStride(array, LayoutRule.Std140));
        Assert.Equal(4, ShaderLayout.ArrayStride(array, LayoutRule.Std430));

        Assert.Equal(64, ShaderLayout.Size(array, LayoutRule.Std140));
        Assert.Equal(16, ShaderLayout.Size(array, LayoutRule.Std430));
    }

    // --- Both backends ----------------------------------------------------

    [Fact]
    public void GlslDeclaresAStd430BlockAndMarksTheReadOnlyOne() {
        var glsl = Generate(Simulate, "glsl").Single().Code;

        Assert.Contains("layout(std430, set = 2, binding = 1) buffer particlesBlock {", glsl, StringComparison.Ordinal);
        Assert.Contains("Particle particles[];", glsl, StringComparison.Ordinal);

        // `readonly` on the buffer the shader never stores into, which is what lets a driver hoist
        // a load out of a loop.
        Assert.Contains("readonly buffer spawnBlock {", glsl, StringComparison.Ordinal);

        Assert.Contains("particles.length()", glsl, StringComparison.Ordinal);
        Assert.Contains("particles[", glsl, StringComparison.Ordinal);
    }

    /// <summary>
    ///     SPIR-V's <c>BufferBlock</c> form, which is the one Vulkan 1.0 takes without an extension.
    /// </summary>
    /// <remarks>
    ///     <c>Block</c> plus the <c>StorageBuffer</c> storage class spells the same thing but needs
    ///     <c>SPV_KHR_storage_buffer_storage_class</c> in SPIR-V 1.0. This form needs nothing, and it
    ///     is the form <c>glslc</c> produces for the same GLSL — which is what keeps the differential
    ///     comparing like with like.
    /// </remarks>
    [Fact]
    public void SpirvDeclaresABufferBlockWithARuntimeArray() {
        var unit = Generate(Simulate, "spirv").Single();

        Assert.Contains("OpTypeRuntimeArray", unit.Code, StringComparison.Ordinal);
        Assert.Contains("BufferBlock", unit.Code, StringComparison.Ordinal);
        Assert.Contains("ArrayStride 32", unit.Code, StringComparison.Ordinal);
        Assert.Contains("NonWritable", unit.Code, StringComparison.Ordinal);
        Assert.Contains("OpArrayLength", unit.Code, StringComparison.Ordinal);

        SpirvTestBase.Validate(unit);
    }

    /// <summary>
    ///     Writing a whole struct into a buffer is a member-by-member store, not one <c>OpStore</c>.
    /// </summary>
    /// <remarks>
    ///     The mirror of reading one out, and forced for the same reason: the laid-out struct is a
    ///     different SPIR-V type from the plain one and there is no conversion between them.
    ///     <c>spirv-val</c> is what said so — "OpStore Pointer's type does not match Object's type".
    /// </remarks>
    [Fact]
    public void SpirvStoresAStructMemberByMember() {
        var unit = Generate(Simulate, "spirv").Single();

        // Four leaves in `Particle`, so a whole-struct write is four stores through four chains.
        Assert.True(
            unit.Code.Split("OpStore").Length - 1 >= 4,
            "Expected a member-by-member store:\n" + unit.Code
        );

        SpirvTestBase.Validate(unit);
    }

    [Fact]
    public void ReferenceToolsAcceptBothTargets() {
        Assert.SkipUnless(ReferenceCompiler.Available, "glslc is not on PATH (brew install shaderc).");

        foreach (var unit in Generate(Simulate, "glsl")) {
            Assert.NotEmpty(ReferenceCompiler.GlslToSpirv(unit.Code, unit.Stage));
        }

        foreach (var unit in Generate(Simulate, "spirv")) {
            SpirvTestBase.Validate(unit);
        }
    }

    // --- Across a .rvnlib boundary ----------------------------------------

    /// <summary>
    ///     A buffer type survives compilation to a library and back, direction included.
    /// </summary>
    /// <remarks>
    ///     The direction has to be in the metadata: a signature that lost it would let a store into a
    ///     read-only buffer bind, and the refusal is the only thing standing between that and a module
    ///     with a <c>NonWritable</c> decoration contradicting its own store.
    /// </remarks>
    [Fact]
    public void ABufferTypeCrossesALibraryBoundary() {
        var library = BuildLibrary(
            """
            package A.Lib

            struct Sum {
                static func Of(data: Buffer<float>, count: int): float {
                    var total = 0f
                    for (i in 0 .. count - 1) {
                        total = total + data[i]
                    }

                    return total
                }
            }

            """
        );

        var compilation = Compilation.Create(
            "Use",
            [RavenReference.FromLibrary(CompiledLibraryReader.Read(CompiledLibraryWriter.Write(library)))],
            [SyntaxTree.ParseText("package A.Use\n\nimport A.Lib\n", path: "Use.rvn")]
        );

        Assert.Empty(compilation.GetDiagnostics());

        var of = Assert.Single(
            Assert.Single(compilation.GetReferencedTypes(), type => type.Name == "Sum")
                .GetMembers("Of")
                .OfType<MethodSymbol>()
        );

        var buffer = Assert.IsType<BufferTypeSymbol>(of.Parameters[0].Type);
        Assert.False(buffer.IsWritable);
        Assert.Equal(SpecialType.Float, buffer.ElementType.SpecialType);
    }

    // --- The shipped library ----------------------------------------------

    /// <summary>
    ///     <c>Vfx/ParticleUpdate.rvn</c> is the pass this feature existed for.
    /// </summary>
    /// <remarks>
    ///     And the split between it and <c>ParticleSimulate.rvn</c> is the point: the arithmetic is
    ///     free functions over a <c>Particle</c> value and touches no binding, so doc 06's CPU/GPU
    ///     bit-for-bit comparison is a transliteration. This asserts the split rather than merely the
    ///     compile — a force that read a binding would break the comparison silently.
    /// </remarks>
    [Fact]
    public void TheParticlePassKeepsItsArithmeticBindingFree() {
        var simulate = File.ReadAllText(LibraryPath("Vfx/ParticleSimulate.rvn"))
            .Split('\n')
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .ToArray();

        Assert.DoesNotContain(simulate, line => line.StartsWith("shader ", StringComparison.Ordinal));
        Assert.DoesNotContain(simulate, line => line.Contains("Buffer<", StringComparison.Ordinal));

        var update = File.ReadAllText(LibraryPath("Vfx/ParticleUpdate.rvn"));
        Assert.Contains("RWBuffer<Particle>", update, StringComparison.Ordinal);
        Assert.Contains("ComputeShader(64)", update, StringComparison.Ordinal);
    }

    // --- Helpers ----------------------------------------------------------

    static string LibraryPath(string relative) =>
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Library", relative);

    static IReadOnlyList<Diagnostic> Compile(string source) =>
        Compilation.Create("Test", SyntaxTree.ParseText(source, path: "Test.rvn")).GetDiagnostics();

    /// <summary>Diagnostics for a shader whose members are the given text.</summary>
    static IReadOnlyList<Diagnostic> Declare(string members) =>
        Compile($"package A\n\nshader S {{\n    {members}\n}}\n");

    /// <summary>Diagnostics for a compute body over a fixed set of bindings.</summary>
    static IReadOnlyList<Diagnostic> Body(string statements) =>
        Compile(
            $$"""
              package A

              shader S {
                  var tint: float4
                  var sized: float[4]
                  var readOnly: Buffer<float>
                  var writable: RWBuffer<float>

                  [ComputeShader(1)]
                  func Main([Semantic("SV_DispatchThreadID")] id: uint3) {
                      {{statements}}
                  }
              }

              """
        );

    static FieldSymbol FieldOf(Compilation compilation, string name) =>
        compilation.GetAllTypes()
            .SelectMany(type => type.GetMembers())
            .OfType<FieldSymbol>()
            .Single(field => field.Name == name);

    static IrModule Lower(string source) {
        var compilation = Compilation.Create("Test", SyntaxTree.ParseText(source, path: "Test.rvn"));
        Assert.Empty(compilation.GetDiagnostics());

        var bag = new DiagnosticBag();
        var module = Lowerer.Lower(compilation, bag);
        Assert.True(IrVerifier.Verify(module, bag), string.Join("\n", bag.Select(d => d.ToString())));
        Assert.True(bag.IsEmpty, string.Join("\n", bag.Select(d => d.ToString())));
        return module;
    }

    static IReadOnlyList<GeneratedSource> Generate(string source, string target) {
        var bag = new DiagnosticBag();
        var generated = TargetBackends.Create(target)!.Generate(Lower(source), bag);

        var errors = bag.ToArray().Where(d => d.IsError).ToArray();
        Assert.True(errors.Length == 0, string.Join("\n", errors.Select(d => d.ToString())));

        return generated;
    }

    static IrBinding FindBinding(IrModule module, string name) =>
        module.Shaders.SelectMany(shader => shader.Bindings).Single(binding => binding.Name == name);

    static IrFunction FindFunction(IrModule module, string name) =>
        module.Shaders
            .SelectMany(shader => shader.Functions)
            .Concat(module.Functions)
            .Single(function => function.Name.EndsWith(name, StringComparison.Ordinal));

    static IEnumerable<IrStatement> Flatten(IrStatement statement) {
        yield return statement;

        switch (statement) {
            case IrBlock block:
                foreach (var nested in block.Statements.SelectMany(Flatten)) {
                    yield return nested;
                }

                break;

            case IrIfStatement conditional: {
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

            case IrLoopStatement loop: {
                IrBlock?[] parts = [loop.Condition, loop.Body, loop.Continue];

                foreach (var nested in parts.Where(p => p is not null).SelectMany(p => Flatten(p!))) {
                    yield return nested;
                }

                break;
            }
        }
    }

    static CompiledLibrary BuildLibrary(string source) {
        var compilation = Compilation.Create("Lib", SyntaxTree.ParseText(source, path: "Lib.rvn"));
        Assert.Empty(compilation.GetDiagnostics());

        var bag = new DiagnosticBag();
        var library = LibraryBuilder.Build(compilation, Lowerer.LowerWithLinks(compilation, bag), bag);
        Assert.DoesNotContain(bag.ToArray(), d => d.IsError);
        return library;
    }
}
