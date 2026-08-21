// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.IR;
using Vixen.Raven.Reflection;
using Vixen.Raven.Symbols;
using Xunit;
using static Tests.CodeGenTestBase;

namespace Tests;

/// <summary>
///     Integer-sampled textures — <c>Texture2D&lt;uint4&gt;</c>, the sampled image a shader
///     declares when the host binds an integer view.
/// </summary>
/// <remarks>
///     <para>
///         The component type is part of the descriptor: Vulkan checks a sampled image's declared
///         component against the bound view's format, so an <c>R32_UINT</c> visibility buffer
///         behind a float-sampled <c>Texture2D</c> is a validation error however the shader then
///         bitcasts what it fetched. The angle-bracket form states the component, which is the
///         whole of what it adds — the plain <c>Texture2D</c> stays the float texture every
///         material reads.
///     </para>
///     <para>
///         <c>Load</c> and <c>GetDimensions</c> only: integer formats are not filterable, so a
///         <c>Sample</c> would be a member that cannot work on any view the type is for.
///     </para>
/// </remarks>
public class SampledTextureTests {
    const string Bin = """
                       package A

                       shader Bin {
                           var identities: Texture2D<uint4>

                           var counts: RWBuffer<uint>

                           [ComputeShader(8, 8)]
                           func Main([Semantic("SV_DispatchThreadID")] id: uint3) {
                               val size = identities.GetDimensions(0)
                               val coord = int2(int(id.x), int(id.y))

                               if (coord.x < size.x && coord.y < size.y) {
                                   counts[coord.y * size.x + coord.x] = identities.Load(int3(coord.x, coord.y, 0)).x
                               }
                           }
                       }

                       """;

    [Fact]
    public void A_uint_texture_binds_as_a_sampled_texture_with_a_uint_sampled_type() {
        var shader = LoweringTestBase.FindShader(LoweringTestBase.Lower(Bin), "Bin");
        var binding = Assert.Single(shader.Bindings, b => b.Kind == IrBindingKind.Texture);

        var texture = Assert.IsType<IrTextureType>(binding.Type);
        Assert.Equal(IrTextureDimension.Texture2D, texture.Dimension);
        Assert.Equal(IrScalarType.UInt, texture.SampledType.ComponentType);

        // Still a sampled texture to the host: the view is created with sampled usage, and only
        // the component type — not the descriptor type — distinguishes it from a float one.
        var descriptors = ReflectionBuilder.Describe(shader).Sets.SelectMany(s => s.Bindings);
        Assert.Contains(descriptors, b => b.Type == DescriptorType.SampledTexture && b.Name == "identities");
    }

    [Fact]
    public void GLSL_declares_the_prefixed_texture_and_fetches_without_a_sampler() {
        var unit = Assert.Single(GenerateClean(Bin));

        Assert.Contains("uniform utexture2D identities;", unit.Code, StringComparison.Ordinal);
        Assert.Contains("texelFetch(identities,", unit.Code, StringComparison.Ordinal);
        Assert.Contains("textureSize(identities,", unit.Code, StringComparison.Ordinal);
    }

    [Fact]
    public void SPIR_V_declares_a_uint_sampled_image_and_fetches_from_it() {
        Assert.SkipUnless(SpirvTestBase.ValidatorAvailable, "spirv-val is not on PATH (brew install spirv-tools).");

        var listing = ReferenceCompiler.Disassemble(Assert.Single(GenerateClean(Bin, "spirv")).Binary!);

        // Sampled = 1 and no format, exactly as the float Texture2D declares — the sampled type
        // is the one word that changes, and the one word the descriptor check reads.
        Assert.Contains("OpTypeImage %uint 2D 0 0 0 1 Unknown", listing, StringComparison.Ordinal);
        Assert.Contains("OpImageFetch", listing, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("glsl")]
    [InlineData("spirv")]
    public void An_int_texture_generates_too(string target) {
        GenerateClean(
            """
            package A

            shader S {
                var stencil: Texture2D<int4>

                var flags: RWBuffer<int>

                [ComputeShader(4)]
                func Main([Semantic("SV_DispatchThreadID")] id: uint3) {
                    flags[int(id.x)] = stencil.Load(int3(int(id.x), 0, 0)).x
                }
            }

            """,
            target
        );
    }

    // --- What the declaration has to say ----------------------------------

    /// <summary>
    ///     Four lanes of integer, or nothing. A float element would be the built-in spelled twice
    ///     — one of them without <c>Sample</c> — and a scalar is a shape neither target's fetch
    ///     has, for the reason <c>RVN2122</c> gives on a storage image.
    /// </summary>
    [Theory]
    [InlineData("float4")]
    [InlineData("uint")]
    [InlineData("Texture2D")]
    public void The_element_has_to_be_an_integer_texel(string element) {
        Assert.Contains(
            SemanticTestBase.Diagnose(
                $"package A\n\nshader S {{\n    var identities: Texture2D<{element}>\n}}\n"
            ),
            d => d.Id == "RVN2136" && d.IsError
        );
    }

    [Fact]
    public void The_bare_name_is_still_the_float_texture() {
        var shader = LoweringTestBase.FindShader(
            LoweringTestBase.Lower(
                """
                package A

                shader S {
                    var albedo: Texture2D
                    var linear: Sampler

                    var luma: RWBuffer<float>

                    [ComputeShader(4)]
                    func Main([Semantic("SV_DispatchThreadID")] id: uint3) {
                        luma[int(id.x)] = albedo.SampleLevel(linear, float2(0.5f, 0.5f), 0f).x
                    }
                }

                """
            ),
            "S"
        );

        var binding = Assert.Single(shader.Bindings, b => b.Kind == IrBindingKind.Texture);
        var texture = Assert.IsType<IrTextureType>(binding.Type);
        Assert.Equal(IrScalarType.Float, texture.SampledType.ComponentType);
    }

    [Fact]
    public void The_type_displays_as_it_was_written() {
        Assert.Equal(
            "Texture2D<uint4>",
            new SampledTextureTypeSymbol(BuiltInTypes.UInt4).ToDisplayString()
        );
    }
}
