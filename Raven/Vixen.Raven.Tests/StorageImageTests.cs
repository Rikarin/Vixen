// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.IR;
using Vixen.Raven.Reflection;
using Vixen.Raven.Symbols;
using Xunit;
using static Tests.CodeGenTestBase;

namespace Tests;

/// <summary>
///     Storage images — docs/plan/07's "a writable <em>texture</em>, which a compute post-process
///     pass writes into".
/// </summary>
/// <remarks>
///     <para>
///         <c>RWBuffer&lt;T&gt;</c> unblocked everything that reads a number back; this is the other
///         half — a pass that produces a render target rather than a number. The two things that
///         make it more than "a buffer with two indices" are both here: a storage image is
///         <em>not sampled</em>, so it has no sampler, no filtering and no mips; and its texel
///         format is part of its declaration, because it is part of the type in SPIR-V.
///     </para>
///     <para>
///         Only the read-write form exists. A texture a shader merely reads is a
///         <c>Texture2D</c> — a sampled image, which filters and mips — so there is no
///         read-only storage image to choose wrongly between.
///     </para>
/// </remarks>
public class StorageImageTests {
    const string Post = """
                        package A

                        shader Post {
                            var source: Texture2D
                            var linear: Sampler

                            [Format("rgba16f")] var target: RWTexture2D<float4>

                            [ComputeShader(8, 8, 1)]
                            func Main([Semantic("SV_DispatchThreadID")] id: uint3) {
                                val size = target.GetDimensions()
                                val coord = int2(int(id.x), int(id.y))
                                val previous = target.Load(coord)
                                val uv = float2(float(coord.x) / float(size.x), float(coord.y) / float(size.y))
                                target.Store(coord, source.SampleLevel(linear, uv, 0f) + previous * 0.5f)
                            }
                        }

                        """;

    [Fact]
    public void An_image_binds_as_its_own_descriptor_type_carrying_its_format() {
        var shader = LoweringTestBase.FindShader(LoweringTestBase.Lower(Post), "Post");
        var binding = Assert.Single(shader.Bindings, b => b.Kind == IrBindingKind.StorageImage);

        var image = Assert.IsType<IrStorageImageType>(binding.Type);
        Assert.Equal("rgba16f", image.Format);
        Assert.Equal(IrTextureDimension.Texture2D, image.Dimension);
        Assert.True(binding.IsWritable);

        // A sampled texture and a storage image are two descriptor types, because the host has to
        // create the view with different usage for each.
        var descriptors = ReflectionBuilder.Describe(shader).Sets.SelectMany(s => s.Bindings);
        Assert.Contains(descriptors, b => b.Type == DescriptorType.StorageImage && b.Name == "target");
    }

    [Fact]
    public void The_capability_is_reported_so_a_host_can_gate_the_pipeline() {
        var shader = LoweringTestBase.FindShader(LoweringTestBase.Lower(Post), "Post");
        Assert.Contains(IrCapability.StorageImage, IrCapabilities.Of(shader));
    }

    [Fact]
    public void GLSL_declares_the_format_in_the_layout_and_stores_as_a_statement() {
        var unit = Assert.Single(GenerateClean(Post));

        Assert.Contains("layout(rgba16f, set = 2, binding = 2) uniform image2D target;", unit.Code, StringComparison.Ordinal);
        Assert.Contains("imageLoad(target,", unit.Code, StringComparison.Ordinal);
        Assert.Contains("imageSize(target)", unit.Code, StringComparison.Ordinal);

        // A store produces nothing, so it is a statement rather than an assignment — a `vec4 _n =
        // imageStore(...)` would not be GLSL at all.
        Assert.Contains("    imageStore(target,", unit.Code, StringComparison.Ordinal);
        Assert.DoesNotContain("= imageStore", unit.Code, StringComparison.Ordinal);
    }

    [Fact]
    public void SPIR_V_declares_a_storage_image_rather_than_a_sampled_one() {
        if (!SpirvTestBase.ValidatorAvailable) {
            return;
        }

        var listing = ReferenceCompiler.Disassemble(Assert.Single(GenerateClean(Post, "spirv")).Binary!);

        // Sampled = 2 says "read and written directly"; a stated format is what keeps the module
        // from needing StorageImageReadWithoutFormat.
        Assert.Contains("OpTypeImage %float 2D 0 0 0 2 Rgba16f", listing, StringComparison.Ordinal);

        // And the sampled texture in the same shader is still Sampled = 1 with no format.
        Assert.Contains("OpTypeImage %float 2D 0 0 0 1 Unknown", listing, StringComparison.Ordinal);

        Assert.Contains("OpImageRead", listing, StringComparison.Ordinal);
        Assert.Contains("OpImageWrite", listing, StringComparison.Ordinal);
        Assert.Contains("OpImageQuerySize ", listing, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("glsl")]
    [InlineData("spirv")]
    public void A_volume_image_and_the_integer_formats_generate(string target) {
        GenerateClean(
            """
            package A

            shader S {
                [Format("rgba32f")] var volume: RWTexture3D<float4>
                [Format("rgba8ui")] var mask: RWTexture2D<uint4>
                [Format("r32i")] var counter: RWTexture2D<int4>

                [ComputeShader(4)]
                func Main([Semantic("SV_DispatchThreadID")] id: uint3) {
                    val xy = int2(int(id.x), int(id.y))
                    volume.Store(int3(xy.x, xy.y, 0), float4(1, 0, 0, 1))
                    mask.Store(xy, uint4(1, 2, 3, 4))
                    counter.Store(xy, counter.Load(xy) + int4(1, 0, 0, 0))
                }
            }

            """,
            target
        );
    }

    // --- What the declaration has to say ----------------------------------

    [Fact]
    public void An_image_needs_a_format() {
        var error = Assert.Single(
            SemanticTestBase.Diagnose("package A\n\nshader S {\n    var target: RWTexture2D<float4>\n}\n"),
            d => d.Id == "RVN2123"
        );

        Assert.True(error.IsError);

        // The message lists what it will accept, because there is no way to guess otherwise.
        Assert.Contains("rgba16f", error.GetMessage(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("rgba16", "is not a format Raven admits")]
    [InlineData("rgba32i", "is read as 'int4'")]
    public void A_format_has_to_be_recognised_and_to_match_the_element(string format, string because) {
        var error = Assert.Single(
            SemanticTestBase.Diagnose(
                $"package A\n\nshader S {{\n    [Format(\"{format}\")] var target: RWTexture2D<float4>\n}}\n"
            ),
            d => d.Id == "RVN2124"
        );

        Assert.True(error.IsError);
        Assert.Contains(because, error.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>
    ///     Four components whatever the format stores, because that is what both targets do: an
    ///     <c>r32f</c> image reads as <c>(r, 0, 0, 1)</c>. A narrower declared type would be a
    ///     shape neither target has.
    /// </summary>
    [Theory]
    [InlineData("float")]
    [InlineData("float2")]
    [InlineData("Texture2D")]
    public void The_element_has_to_be_a_four_lane_texel(string element) {
        Assert.Contains(
            SemanticTestBase.Diagnose(
                $"package A\n\nshader S {{\n    [Format(\"rgba16f\")] var target: RWTexture2D<{element}>\n}}\n"
            ),
            d => d.Id == "RVN2122" && d.IsError
        );
    }

    [Fact]
    public void A_format_on_something_with_no_texels_says_nothing() {
        var warning = Assert.Single(
            SemanticTestBase.Diagnose(
                "package A\n\nshader S {\n    [Format(\"rgba16f\")] var albedo: Texture2D\n}\n"
            ),
            d => d.Id == "RVN2125"
        );

        Assert.False(warning.IsError);
    }

    /// <summary>
    ///     The format is part of the type in SPIR-V, so a parameter says which one it takes —
    ///     otherwise a function taking an <c>rgba16f</c> image could be handed an <c>rgba8</c> one
    ///     and the module would not type-check.
    /// </summary>
    [Theory]
    [InlineData("glsl")]
    [InlineData("spirv")]
    public void An_image_can_be_passed_to_a_function_when_the_parameter_names_the_same_format(string target) {
        GenerateClean(
            """
            package A

            shader S {
                [Format("rgba16f")] var target: RWTexture2D<float4>

                func Clear([Format("rgba16f")] image: RWTexture2D<float4>, at: int2) {
                    image.Store(at, float4(0, 0, 0, 1))
                }

                [ComputeShader(8, 8)]
                func Main([Semantic("SV_DispatchThreadID")] id: uint3) {
                    Clear(target, int2(int(id.x), int(id.y)))
                }
            }

            """,
            target
        );
    }

    [Fact]
    public void An_image_is_still_not_assignable_as_a_whole() {
        Assert.Contains(
            SemanticTestBase.Diagnose(
                """
                package A

                shader S {
                    [Format("rgba16f")] var a: RWTexture2D<float4>
                    [Format("rgba16f")] var b: RWTexture2D<float4>

                    [ComputeShader(1)]
                    func Main([Semantic("SV_DispatchThreadID")] id: uint3) {
                        a = b
                    }
                }

                """
            ),
            d => d.Id == "RVN2119" && d.IsError
        );
    }

    [Fact]
    public void The_type_displays_as_it_was_written() {
        Assert.Equal(
            "RWTexture3D<float4>",
            new StorageImageTypeSymbol(BuiltInTypes.Float4, true).ToDisplayString()
        );
    }
}
