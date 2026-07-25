// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.CodeGen;
using Xunit;

namespace Tests;

/// <summary>
///     The differential oracle of docs/plan/07 § C: two independent paths from one source to one
///     target, diffed against each other.
/// </summary>
/// <remarks>
///     <para>
///         <c>.rvn → IR → Raven's SPIR-V</c> is what the engine consumes.
///         <c>.rvn → IR → Raven's GLSL → shaderc → SPIR-V</c> is the oracle. Both start from the
///         same lowered IR, so what this compares is the two <em>emitters</em>: a disagreement
///         means one of them is wrong, and the diff usually says which.
///     </para>
///     <para>
///         This is stronger than validating either alone. <c>spirv-val</c> proves a module is
///         well-formed, not that it means what the source said; a golden listing detects change,
///         not incorrectness. The differential catches the case where an emitter is internally
///         consistent and semantically wrong.
///     </para>
///     <para>
///         What it compares is the host-visible interface — descriptors, member offsets and
///         strides, stage locations, execution model — and, just as importantly, that
///         <c>glslc</c> accepts Raven's GLSL at all: a full GLSL front end reading every line
///         the emitter produced. It does not compare instruction streams; glslang's are
///         structured differently for the same meaning, so a body-level diff would be noise. The
///         numeric BRDF tests (CPU port vs GPU readback) are what cover arithmetic, and a bug in
///         the shared IR shows up in both paths and stays invisible here — which is why both
///         techniques are in the plan.
///     </para>
/// </remarks>
public class SpirvDifferentialTests(ITestOutputHelper output) {
    /// <summary>Realistic shading, two stages, a texture and a matrix in one block.</summary>
    const string Lambert = """
                           package A

                           shader S {
                               var world: mat4
                               var baseColor: float4
                               var albedo: Texture2D
                               var albedoSampler: Sampler

                               [VertexShader]
                               [Semantic("SV_Position")]
                               func Vertex(position: float3): float4 {
                                   return world * float4(position, 1)
                               }

                               [PixelShader]
                               func Pixel(normal: float3, uv: float2): float4 {
                                   val shade = max(dot(normalize(normal), float3(0, 1, 0)), 0.1f)
                                   return albedo.Sample(albedoSampler, uv) * baseColor * shade
                               }
                           }

                           """;

    /// <summary>All four sets, so the descriptor layout is compared and not just one set of it.</summary>
    const string FourSets = """
                            package A

                            shader S {
                                [PerFrame] var time: float
                                [PerView] var viewProjection: mat4
                                var tint: float4
                                var albedo: Texture2D
                                var linear: Sampler
                                [PerDraw] var world: mat4

                                [PixelShader]
                                func Pixel(uv: float2): float4 {
                                    return albedo.Sample(linear, uv) * tint * time + viewProjection * world * tint
                                }
                            }

                            """;

    /// <summary>
    ///     The std140 shapes doc 07 § D warns about: a <c>float3</c> that aligns to 16 while
    ///     occupying 12, a scalar wedged between vectors, a matrix's stride. If Raven's layout
    ///     engine and glslang's disagree anywhere, it is here.
    /// </summary>
    /// <remarks>
    ///     No array, because Raven cannot yet declare one with a length — its
    ///     <c>array_rank_specifier</c> is <c>[]</c> only — and an unsized array is not legal in a
    ///     uniform block. So <c>ArrayStride</c> is covered by <c>ShaderLayoutTests</c> against
    ///     the spec, but not yet against a second implementation.
    /// </remarks>
    const string Packing = """
                           package A

                           shader S {
                               var tint: float4
                               var roughness: float
                               var direction: float3
                               var transform: mat4

                               [PixelShader]
                               func Pixel(): float4 {
                                   return transform * tint * roughness + float4(direction, 1)
                               }
                           }

                           """;

    /// <summary>A fetch by integer coordinate — the samplerless path, with no sampler binding.</summary>
    const string Fetch = """
                         package A

                         shader S {
                             var albedo: Texture2D

                             [PixelShader]
                             func Pixel(): float4 {
                                 return albedo.Load(int3(1, 2, 0))
                             }
                         }

                         """;

    [Theory]
    [InlineData("lambert", Lambert)]
    [InlineData("four sets", FourSets)]
    [InlineData("std140 packing", Packing)]
    [InlineData("texel fetch", Fetch)]
    public void The_two_paths_agree_on_the_interface(string what, string source) {
        if (!ReferenceCompiler.Available) {
            output.WriteLine($"{what}: {ReferenceCompiler.HowToInstall}");
            return;
        }

        var glsl = CodeGenTestBase.GenerateClean(source, "glsl");
        var spirv = CodeGenTestBase.GenerateClean(source, "spirv");

        Assert.Equal(
            spirv.Select(unit => unit.Stage).Order(),
            glsl.Select(unit => unit.Stage).Order()
        );

        foreach (var mine in spirv) {
            var theirs = Assert.Single(glsl, unit => unit.Stage == mine.Stage);

            var ravens = SpirvInterface.Read(ReferenceCompiler.Disassemble(mine.Binary!));
            var oracles = SpirvInterface.Read(
                ReferenceCompiler.Disassemble(ReferenceCompiler.GlslToSpirv(theirs.Code, theirs.Stage))
            );

            output.WriteLine($"{what} ({mine.Stage}): {ravens.Descriptors.Count} descriptors compared");

            Assert.Equal(ravens.ExecutionModel, oracles.ExecutionModel);
            Assert.Equal(ravens.Descriptors, oracles.Descriptors);
            Assert.Equal(ravens.Locations, oracles.Locations);
            Assert.Equal(ravens.Members, oracles.Members);
        }
    }

    /// <summary>
    ///     Worth its own assertion: the offsets the engine writes constant buffers by are the
    ///     ones a reference GLSL compiler computes for the same block. This is the requirement
    ///     doc 07 § D calls "get the packing rules pinned or every backend disagrees about
    ///     <c>float3</c> padding".
    /// </summary>
    [Fact]
    public void The_two_paths_agree_on_every_member_offset_and_stride() {
        if (!ReferenceCompiler.Available) {
            output.WriteLine(ReferenceCompiler.HowToInstall);
            return;
        }

        var mine = Assert.Single(CodeGenTestBase.GenerateClean(Packing, "spirv"));
        var theirs = Assert.Single(CodeGenTestBase.GenerateClean(Packing, "glsl"));

        var block = Assert.Single(SpirvInterface.Read(ReferenceCompiler.Disassemble(mine.Binary!)).Members);
        var oracle = Assert.Single(
            SpirvInterface
                .Read(ReferenceCompiler.Disassemble(ReferenceCompiler.GlslToSpirv(theirs.Code, theirs.Stage)))
                .Members
        );

        // The numbers themselves, so a failure reads as numbers rather than as a set diff.
        Assert.Equal("0", block.Value["0.Offset"]);
        Assert.Equal("16", block.Value["1.Offset"]);
        Assert.Equal("32", block.Value["2.Offset"]);
        Assert.Equal("48", block.Value["3.Offset"]);
        Assert.Equal("16", block.Value["3.MatrixStride"]);

        Assert.Equal(block.Key, oracle.Key);
        Assert.Equal(block.Value, oracle.Value);
    }

    /// <summary>
    ///     The precondition for everything above, asserted on its own so a failure says
    ///     "the GLSL does not compile" rather than "the interfaces differ".
    /// </summary>
    [Theory]
    [InlineData("lambert", Lambert)]
    [InlineData("four sets", FourSets)]
    [InlineData("std140 packing", Packing)]
    [InlineData("texel fetch", Fetch)]
    public void A_reference_compiler_accepts_Ravens_GLSL(string what, string source) {
        if (ReferenceCompiler.Glslc is null) {
            output.WriteLine($"{what}: {ReferenceCompiler.HowToInstall}");
            return;
        }

        foreach (var unit in CodeGenTestBase.GenerateClean(source, "glsl")) {
            var module = ReferenceCompiler.GlslToSpirv(unit.Code, unit.Stage);

            // A SPIR-V magic word, so a zero-length output cannot pass for success.
            Assert.Equal<byte[]>([0x03, 0x02, 0x23, 0x07], module[..4]);
        }
    }
}
