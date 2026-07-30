// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.Diagnostics;
using Vixen.Raven;
using Vixen.Raven.IR;
using Vixen.Raven.Lowering;
using Vixen.Raven.Reflection;
using Vixen.Raven.Symbols;
using Vixen.Raven.Syntax;
using Xunit;

namespace Tests;

/// <summary>
///     <c>int64</c>, <c>uint64</c>, and the atomics on them.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/22-virtualized-geometry.md § B2 asks for exactly one thing and says why: a single-pass
///         software rasterizer wants <c>atomicMax</c> on a 64-bit word packing depth above id. With
///         32 bits you get depth <em>or</em> a usable id, and the alternative is two passes over the
///         same triangles.
///     </para>
///     <para>
///         The whole feature is optional hardware — <c>VK_KHR_shader_atomic_int64</c> on Vulkan,
///         SM6.6 on D3D12, absent from WebGPU — so the load-bearing part is not that it compiles but
///         that a shader using it <em>says so</em>, in two capabilities rather than one: the type
///         and the atomic are separately optional, and a device may have the first without the
///         second.
///     </para>
/// </remarks>
public class Int64Tests {
    /// <summary>The visibility-buffer resolve, which is the reason the type exists.</summary>
    const string Visibility = """
                              package A

                              shader VisBuffer {
                                  var visibility: RWBuffer<uint64>
                                  var width: uint

                                  [ComputeShader(64)]
                                  func Main([Semantic("SV_DispatchThreadID")] id: uint3) {
                                      val packed = (uint64(id.z) << 32) | uint64(id.y)
                                      val pixel = int(id.y * width + id.x)

                                      val previous = atomicMax(visibility[pixel], packed)
                                  }
                              }

                              """;

    static IReadOnlyList<Diagnostic> Compile(string source) =>
        Compilation.Create("Test", SyntaxTree.ParseText(source, path: "Test.rvn")).GetDiagnostics();

    static IrModule Lower(string source) {
        var compilation = Compilation.Create("Test", SyntaxTree.ParseText(source, path: "Test.rvn"));
        Assert.Empty(compilation.GetDiagnostics());

        var bag = new DiagnosticBag();
        var module = Lowerer.Lower(compilation, bag);
        IrVerifier.Verify(module, bag);
        Assert.True(bag.IsEmpty, string.Join("\n", bag.Select(d => d.ToString())));

        return module;
    }

    static string Kernel(string body) =>
        $$"""
          package A

          shader S {
              var packed: RWBuffer<uint64>
              var signed: RWBuffer<int64>
              var narrow: RWBuffer<uint>

              [ComputeShader(64)]
              func Main([Semantic("SV_DispatchThreadID")] id: uint3) {
                  val i = int(id.x)
          {{body}}
              }
          }

          """;

    // --- The types ---------------------------------------------------------

    /// <summary>
    ///     Named rather than keyworded, which is what makes them free of the lexer and the grammar.
    /// </summary>
    [Theory]
    [InlineData("int64")]
    [InlineData("uint64")]
    public void The_wide_integers_resolve_by_name(string name) {
        var type = Assert.IsType<PrimitiveTypeSymbol>(BuiltInTypes.Lookup(name));

        Assert.Equal(TypeKind.Scalar, type.TypeKind);
        Assert.Equal(name, type.ToDisplayString());
    }

    /// <summary>
    ///     No vectors, and that is a decision rather than an omission: nothing wants a
    ///     <c>uint64_2</c>, both targets' atomics are scalar, and each lane would cost a name, a
    ///     layout rule and a row in the conversion table for a shape with no use.
    /// </summary>
    [Theory]
    [InlineData("int642")]
    [InlineData("uint643")]
    [InlineData("uint644")]
    public void There_are_no_wide_vectors(string name) {
        Assert.Null(BuiltInTypes.Lookup(name));
    }

    /// <summary>Eight bytes, and the alignment follows — which is what leaves a hole behind a uint.</summary>
    [Fact]
    public void A_wide_integer_is_eight_bytes() {
        Assert.Equal(8, ShaderLayout.Size(IrScalarType.UInt64));
        Assert.Equal(8, ShaderLayout.Alignment(IrScalarType.UInt64));
        Assert.Equal(8, ShaderLayout.Size(IrScalarType.Int64));

        // uint at 0, then four bytes of padding, then the wide one at 8.
        var (offsets, size) = ShaderLayout.Members(
            [IrScalarType.UInt, IrScalarType.UInt64],
            LayoutRule.Std430
        );

        Assert.Equal([0, 8], offsets);
        Assert.Equal(16, size);
    }

    // --- Conversions -------------------------------------------------------

    [Fact]
    public void The_visibility_resolve_compiles() {
        Assert.Empty(Compile(Visibility));
    }

    /// <summary>
    ///     Nothing widens into 64 bits on its own, and that is deliberate.
    /// </summary>
    /// <remarks>
    ///     A silent <c>uint</c> → <c>uint64</c> would make both the 32-bit and the 64-bit overload
    ///     of every atomic applicable to the same call, and tie-breaking would decide the width of
    ///     an operation whose width is the entire point. So a variable says <c>uint64(x)</c>, which
    ///     it is writing next to the shift anyway.
    /// </remarks>
    [Fact]
    public void A_variable_does_not_widen_on_its_own() {
        Assert.Contains(
            Compile(Kernel("        var wide: uint64 = id.x")),
            d => d.IsError
        );

        Assert.Empty(Compile(Kernel("        var wide: uint64 = uint64(id.x)")));
    }

    /// <summary>
    ///     A literal is the exception, because it has no type of its own to be surprised by — and
    ///     the first argument of an atomic is a place, so it still says which overload is meant.
    /// </summary>
    [Fact]
    public void A_literal_may_widen() {
        Assert.Empty(Compile(Kernel("        val old = atomicAdd(packed[i], 1)")));
        Assert.Empty(Compile(Kernel("        var wide: uint64 = 7")));
    }

    /// <summary>
    ///     The operators an author actually packs a word with: a shift by an ordinary <c>int</c>,
    ///     a bitwise or, and an unsigned comparison.
    /// </summary>
    [Fact]
    public void The_packing_operators_bind() {
        Assert.Empty(
            Compile(
                Kernel(
                    """
                            val a = uint64(id.z) << 32
                            val b = a | uint64(id.y)
                            val c = b > uint64(0)
                            packed[i] = b
                    """
                )
            )
        );
    }

    // --- The atomics -------------------------------------------------------

    [Theory]
    [InlineData("atomicAdd")]
    [InlineData("atomicMin")]
    [InlineData("atomicMax")]
    [InlineData("atomicAnd")]
    [InlineData("atomicOr")]
    [InlineData("atomicXor")]
    [InlineData("atomicExchange")]
    public void Every_atomic_exists_at_both_widths(string name) {
        Assert.Empty(Compile(Kernel($"        val old = {name}(packed[i], uint64(3))")));
        Assert.Empty(Compile(Kernel($"        val old = {name}(signed[i], int64(3))")));
        Assert.Empty(Compile(Kernel($"        val old = {name}(narrow[i], 3u)")));
    }

    [Fact]
    public void Compare_exchange_exists_at_both_widths() {
        Assert.Empty(
            Compile(Kernel("        val old = atomicCompareExchange(packed[i], uint64(0), uint64(7))"))
        );
    }

    /// <summary>
    ///     The IR carries the place at the wide type, and the verifier accepts it — which it would
    ///     not have before, since its rule named the two 32-bit kinds by hand.
    /// </summary>
    [Fact]
    public void The_lowered_atomic_is_a_place_at_the_wide_type() {
        var module = Lower(Visibility);
        var atomic = Assert.Single(
            Flatten(LoweringTestBase.FindFunction(module, "Main").Body).OfType<IrAtomicInstruction>()
        );

        Assert.Equal(IrAtomicOp.Max, atomic.Op);
        Assert.Equal("visibility", atomic.Place.Root.Name);
        Assert.Equal(IrScalarType.UInt64, atomic.Result.Type);
    }

    // --- Capabilities ------------------------------------------------------

    /// <summary>
    ///     Two capabilities, not one, because they are two device features.
    /// </summary>
    /// <remarks>
    ///     Reporting only <c>Int64</c> for a shader that atomically maxes a 64-bit word is a
    ///     pipeline that creates on a device whose <c>shaderBufferInt64Atomics</c> is false — and a
    ///     dispatch that then does not do what the shader says. The split is the whole point of
    ///     reporting capabilities at all.
    /// </remarks>
    [Fact]
    public void A_wide_atomic_requires_both_the_type_and_the_atomic() {
        var shader = Assert.Single(Lower(Visibility).Shaders);
        var required = IrCapabilities.Of(shader);

        Assert.Contains(IrCapability.Int64, required);
        Assert.Contains(IrCapability.Int64Atomics, required);
    }

    /// <summary>
    ///     And a shader that packs a word without an atomic asks only for the type, which is what
    ///     makes the split worth having rather than two names for one thing.
    /// </summary>
    [Fact]
    public void Packing_without_an_atomic_asks_only_for_the_type() {
        var shader = Assert.Single(
            Lower(
                """
                package A

                shader S {
                    var packed: RWBuffer<uint64>

                    [ComputeShader(64)]
                    func Main([Semantic("SV_DispatchThreadID")] id: uint3) {
                        packed[int(id.x)] = (uint64(id.z) << 32) | uint64(id.y)
                    }
                }

                """
            ).Shaders
        );

        var required = IrCapabilities.Of(shader);

        Assert.Contains(IrCapability.Int64, required);
        Assert.DoesNotContain(IrCapability.Int64Atomics, required);
    }

    /// <summary>A shader with no wide integer in it asks for neither.</summary>
    [Fact]
    public void A_shader_without_one_asks_for_neither() {
        var shader = Assert.Single(
            Lower(
                """
                package A

                shader S {
                    var counts: RWBuffer<uint>

                    [ComputeShader(64)]
                    func Main([Semantic("SV_DispatchThreadID")] id: uint3) {
                        val old = atomicAdd(counts[int(id.x)], 1u)
                    }
                }

                """
            ).Shaders
        );

        var required = IrCapabilities.Of(shader);

        Assert.DoesNotContain(IrCapability.Int64, required);
        Assert.DoesNotContain(IrCapability.Int64Atomics, required);
    }

    // --- The stage interface -----------------------------------------------

    /// <summary>
    ///     A 64-bit component is refused across a stage boundary, because it does not fit one
    ///     location: Vulkan's interface slots are four 32-bit components wide, so a wide one
    ///     consumes two and stops matching the numbers <c>StreamPlan</c> assigned.
    /// </summary>
    [Fact]
    public void A_wide_integer_cannot_cross_a_stage_boundary() {
        Assert.False(StageInterface.CanCarry(IrScalarType.UInt64));
        Assert.False(StageInterface.CanCarry(IrScalarType.Int64));

        // The same rule, and the same defect it always had: a `double` varying was accepted and
        // silently took two locations.
        Assert.False(StageInterface.CanCarry(IrScalarType.Double));
        Assert.True(StageInterface.CanCarry(IrScalarType.UInt));
    }

    // --- The backends ------------------------------------------------------

    /// <summary>
    ///     The extensions, and both of them, because a driver may reject a unit that declares one it
    ///     does not use and will certainly reject one that uses a word it never asked for.
    /// </summary>
    [Fact]
    public void Glsl_declares_the_two_extensions_and_spells_the_wide_type() {
        var code = CodeGenTestBase.GenerateOne(Visibility);

        Assert.Contains("GL_EXT_shader_explicit_arithmetic_types_int64", code, StringComparison.Ordinal);
        Assert.Contains("GL_EXT_shader_atomic_int64", code, StringComparison.Ordinal);
        Assert.Contains("uint64_t visibility[]", code, StringComparison.Ordinal);
        Assert.Contains("atomicMax(visibility[", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Glsl_asks_for_the_atomic_extension_only_when_something_is_atomic() {
        var code = CodeGenTestBase.GenerateOne(
            """
            package A

            shader S {
                var packed: RWBuffer<uint64>

                [ComputeShader(64)]
                func Main([Semantic("SV_DispatchThreadID")] id: uint3) {
                    packed[int(id.x)] = uint64(id.y)
                }
            }

            """
        );

        Assert.Contains("GL_EXT_shader_explicit_arithmetic_types_int64", code, StringComparison.Ordinal);
        Assert.DoesNotContain("GL_EXT_shader_atomic_int64", code, StringComparison.Ordinal);
    }

    /// <summary>
    ///     SPIR-V picks the <em>unsigned</em> max, which is the assertion that matters most here.
    /// </summary>
    /// <remarks>
    ///     A packed depth-above-id key uses its top bit as data. A signed compare reads that bit as
    ///     a sign, so <c>OpAtomicSMax</c> would resolve the far half of the depth range backwards —
    ///     correct for every scene that never gets there, and wrong for the ones that do.
    /// </remarks>
    [Fact]
    public void Spirv_uses_the_unsigned_max_and_declares_both_capabilities() {
        var listing = SpirvTestBase.One(Visibility).Code;

        Assert.Contains("OpCapability Int64", listing, StringComparison.Ordinal);
        Assert.Contains("OpCapability Int64Atomics", listing, StringComparison.Ordinal);
        Assert.Contains("OpTypeInt 64 0", listing, StringComparison.Ordinal);
        Assert.Contains("OpAtomicUMax", listing, StringComparison.Ordinal);
        Assert.DoesNotContain("OpAtomicSMax", listing, StringComparison.Ordinal);
    }

    [Fact]
    public void Spirv_uses_the_signed_max_for_a_signed_word() {
        var listing = SpirvTestBase.One(Kernel("        val old = atomicMax(signed[i], int64(3))")).Code;

        Assert.Contains("OpTypeInt 64 1", listing, StringComparison.Ordinal);
        Assert.Contains("OpAtomicSMax", listing, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Widening is <c>OpUConvert</c>, which changes width and not signedness — so a
    ///     <c>uint</c> reaching a <c>uint64</c> is zero-extended rather than sign-extended.
    /// </summary>
    /// <remarks>
    ///     The wrong instruction here is the quietest bug in the feature: <c>OpSConvert</c> on a
    ///     <c>uint</c> whose top bit is set fills the upper word with ones, and every id above
    ///     2³¹ resolves as larger than every depth.
    /// </remarks>
    [Fact]
    public void Widening_preserves_the_source_signedness() {
        var listing = SpirvTestBase.One(Visibility).Code;

        Assert.Contains("OpUConvert", listing, StringComparison.Ordinal);
        Assert.DoesNotContain("OpSConvert", listing, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A conversion that changes both width and signedness is two instructions, because SPIR-V
    ///     has no one that does both: widen in the source's signedness, then reinterpret.
    /// </summary>
    [Fact]
    public void A_conversion_across_width_and_signedness_takes_two_steps() {
        var listing = SpirvTestBase.One(Kernel("        val wide = int64(id.x)")).Code;

        // id.x is a uint, so the widen is unsigned and the reinterpretation follows it.
        Assert.Contains("OpUConvert", listing, StringComparison.Ordinal);
        Assert.Contains("OpBitcast", listing, StringComparison.Ordinal);
        Assert.DoesNotContain("OpSConvert", listing, StringComparison.Ordinal);
    }

    /// <summary>
    ///     And a real GLSL front end takes what came out — which for this feature is the check that
    ///     matters, since every word of it (<c>uint64_t</c>, the 64-bit <c>atomicMax</c>, both
    ///     extension names) is one <c>glslc</c> rejects outright if it is wrong.
    /// </summary>
    [Fact]
    public void A_real_front_end_accepts_the_emitted_glsl() {
        Assert.SkipUnless(ReferenceCompiler.Available, ReferenceCompiler.HowToInstall);

        foreach (var unit in CodeGenTestBase.GenerateClean(Visibility)) {
            Assert.NotEmpty(ReferenceCompiler.GlslToSpirv(unit.Code, unit.Stage));
        }
    }

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

                foreach (var nested in parts.Where(part => part is not null).SelectMany(part => Flatten(part!))) {
                    yield return nested;
                }

                break;
            }
        }
    }
}
