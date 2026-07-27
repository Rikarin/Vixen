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
    /// <summary>
    ///     Realistic shading, two stages, a texture and a matrix in one block — plus a struct built
    ///     positionally, so <c>glslc</c> keeps checking that Raven's use of GLSL's own implicit
    ///     struct constructor stays legal.
    /// </summary>
    const string Lambert = """
                           package A

                           struct Light {
                               var direction: float3
                               var colour: float3
                           }

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
                                   val light = Light(float3(0, 1, 0), float3(1, 1, 1))
                                   val shade = max(dot(normalize(normal), light.direction), 0.1f)
                                   val tint = float4(light.colour, 1)
                                   return albedo.Sample(albedoSampler, uv) * baseColor * shade * tint
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

    /// <summary>
    ///     A non-square matrix, indexed. This is the fixture whose absence let the indexing
    ///     convention stay wrong: on a square matrix a row and a column have the same lane count, so
    ///     confusing them is invisible. On a <c>mat2x3</c> it is a type error.
    /// </summary>
    const string Matrices = """
                            package A

                            shader S {
                                var oblique: mat2x3
                                var world: mat4

                                [PixelShader]
                                func Pixel(): float4 {
                                    val column = oblique[0]
                                    return world * float4(column, 0, 1)
                                }
                            }

                            """;

    /// <summary>
    ///     The three constructs Tier B finished — a switch statement, a user-defined operator and a
    ///     tuple return — so the oracle keeps checking that what they emit stays legal in both.
    /// </summary>
    const string Finished = """
                            package A

                            struct Spectrum {
                                var r: float
                                var g: float

                                Spectrum operator +(a: Spectrum, b: Spectrum) => Spectrum(a.r + b.r, a.g + b.g)
                            }

                            shader S {
                                var mode: int
                                var tint: float4

                                func Split(v: float4): (rgb: float3, a: float) {
                                    return (float3(v.x, v.y, v.z), v.w)
                                }

                                [PixelShader]
                                func Pixel(): float4 {
                                    val parts = Split(tint)
                                    var scale = 1f
                                    switch (mode) {
                                        case 0:
                                        case 1:
                                            scale = 2f
                                            break
                                        default:
                                            scale = 3f
                                    }

                                    val sum = Spectrum(parts.rgb.x, parts.a) + Spectrum(1f, 2f)
                                    return float4(sum.r * scale, sum.g, 0, 1)
                                }
                            }

                            """;

    /// <summary>
    ///     A guarded index, which lowers to a branch <em>inside</em> an expression. Here so both
    ///     emitters keep agreeing that is legal: GLSL has to hoist the local above the <c>if</c>,
    ///     and SPIR-V has to structure the merge.
    /// </summary>
    const string ShortCircuit = """
                                package A

                                shader S {
                                    var weights: float[4]
                                    var count: int

                                    [PixelShader]
                                    func Pixel(): float4 {
                                        var total = 0f
                                        for (i in 0 .. 3) {
                                            if (i < count && weights[i] > 0f) {
                                                total += weights[i]
                                            }
                                        }

                                        return float4(total, 0, 0, 1)
                                    }
                                }

                                """;

    /// <summary>
    ///     The intrinsics that reach the two targets by different routes: an explicit-level sample
    ///     (<c>textureLod</c> against <c>ImageSampleExplicitLod</c>), a size query (which needs a
    ///     GLSL extension on one side and a capability on the other), and a bit cast.
    /// </summary>
    const string Queries = """
                           package A

                           shader S {
                               var albedo: Texture2D
                               var linear: Sampler
                               var packed: Buffer<uint>

                               [PixelShader]
                               func Pixel(uv: float2): float4 {
                                   val size = albedo.GetDimensions(0)
                                   val texel = float2(1f / float(size.x), 1f / float(size.y))
                                   val tint = asfloat(packed[0])
                                   return albedo.SampleLevel(linear, uv + texel, 0f) * tint
                               }
                           }

                           """;

    [Theory]
    [InlineData("lambert", Lambert)]
    [InlineData("four sets", FourSets)]
    [InlineData("std140 packing", Packing)]
    [InlineData("texel fetch", Fetch)]
    [InlineData("non-square matrices", Matrices)]
    [InlineData("switch, operators, tuples", Finished)]
    [InlineData("short-circuit guard", ShortCircuit)]
    [InlineData("texture queries and bit casts", Queries)]
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
    [InlineData("non-square matrices", Matrices)]
    [InlineData("switch, operators, tuples", Finished)]
    [InlineData("short-circuit guard", ShortCircuit)]
    [InlineData("texture queries and bit casts", Queries)]
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
