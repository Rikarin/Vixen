// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.Symbols;
using Xunit;
using static Tests.CodeGenTestBase;

namespace Tests;

/// <summary>
///     The texture intrinsics docs/plan/07 § F listed as missing: an explicit level of detail, and
///     a size query.
/// </summary>
/// <remarks>
///     Both are about what a shader outside the fragment stage can do. <c>Sample</c> takes its
///     level from derivatives, which only a fragment stage has, so a vertex shader reading a
///     heightmap had no way to say which mip it wanted — the SPIR-V backend picked level zero for
///     it, and GLSL's <c>texture()</c> quietly meant something else in every other stage.
/// </remarks>
public class TextureIntrinsicTests {
    const string Vertex = """
                          package A

                          shader S {
                              var height: Texture2D
                              var linear: Sampler

                              [VertexShader]
                              [Semantic("SV_Position")]
                              func Vertex(position: float3): float4 {
                                  val h = height.SampleLevel(linear, position.xy, 2f)
                                  return float4(position.x, h.r, position.z, 1)
                              }
                          }

                          """;

    const string Size = """
                        package A

                        shader S {
                            var albedo: Texture2D
                            var linear: Sampler

                            [FragmentShader]
                            func Fragment(uv: float2): float4 {
                                val size = albedo.GetDimensions(0)
                                val texel = float2(1f / float(size.x), 1f / float(size.y))
                                return albedo.Sample(linear, uv + texel)
                            }
                        }

                        """;

    [Theory]
    [InlineData("Texture2D", "SampleLevel", 3)]
    [InlineData("Texture3D", "SampleLevel", 3)]
    [InlineData("TextureCube", "SampleLevel", 3)]
    [InlineData("Texture2D", "GetDimensions", 1)]
    [InlineData("Texture3D", "GetDimensions", 1)]
    [InlineData("TextureCube", "GetDimensions", 1)]
    public void Every_texture_type_carries_the_new_members(string type, string name, int parameters) {
        var texture = Assert.IsType<BuiltInNamedTypeSymbol>(BuiltInTypes.Lookup(type));
        var method = Assert.Single(texture.GetMembers().OfType<MethodSymbol>(), m => m.Name == name);

        Assert.Equal(parameters, method.Parameters.Count);
    }

    /// <summary>A cube is square and uniform across its faces, so its size is two numbers.</summary>
    [Fact]
    public void A_size_query_has_one_component_per_dimension_the_texture_indexes() {
        Assert.Equal("int2", Returns("Texture2D", "GetDimensions"));
        Assert.Equal("int3", Returns("Texture3D", "GetDimensions"));
        Assert.Equal("int2", Returns("TextureCube", "GetDimensions"));
    }

    [Fact]
    public void An_explicit_level_reaches_GLSL_as_textureLod() {
        var unit = Assert.Single(GenerateClean(Vertex));

        Assert.Contains("textureLod(sampler2D(height, linear), _2, 2.0)", unit.Code, StringComparison.Ordinal);
        Assert.DoesNotContain("texture(sampler2D", unit.Code, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A size query takes the plain image, not a combined sampler — the same reason
    ///     <c>Load</c> needs the extension, and the reason it is asked for here rather than
    ///     declared unconditionally.
    /// </summary>
    [Fact]
    public void A_size_query_reaches_GLSL_as_samplerless_textureSize() {
        var unit = Assert.Single(GenerateClean(Size));

        Assert.Contains("textureSize(albedo, 0)", unit.Code, StringComparison.Ordinal);
        Assert.Contains("GL_EXT_samplerless_texture_functions", unit.Code, StringComparison.Ordinal);
    }

    [Fact]
    public void An_explicit_level_reaches_SPIR_V_as_an_explicit_lod_sample() {
        Assert.SkipUnless(SpirvTestBase.ValidatorAvailable, "spirv-val is not on PATH (brew install spirv-tools).");

        var listing = ReferenceCompiler.Disassemble(SpirvTestBase.One(Vertex).Binary!);

        // Lod is image-operands bit 1, and the operand that follows is the author's level rather
        // than the zero the implicit-lod fallback used to fabricate.
        Assert.Contains("OpImageSampleExplicitLod", listing, StringComparison.Ordinal);
        Assert.Contains("Lod", listing, StringComparison.Ordinal);
    }

    [Fact]
    public void A_size_query_reaches_SPIR_V_as_a_declared_image_query() {
        Assert.SkipUnless(SpirvTestBase.ValidatorAvailable, "spirv-val is not on PATH (brew install spirv-tools).");

        var listing = ReferenceCompiler.Disassemble(SpirvTestBase.One(Size).Binary!);

        // The capability is not optional: spirv-val rejects the instruction without it, which is
        // what One() has already checked by the time this reads the listing.
        Assert.Contains("OpCapability ImageQuery", listing, StringComparison.Ordinal);
        Assert.Contains("OpImageQuerySizeLod", listing, StringComparison.Ordinal);
    }

    [Fact]
    public void A_shader_with_no_query_does_not_declare_the_capability() {
        Assert.SkipUnless(SpirvTestBase.ValidatorAvailable, "spirv-val is not on PATH (brew install spirv-tools).");

        var listing = ReferenceCompiler.Disassemble(SpirvTestBase.One(Vertex).Binary!);
        Assert.DoesNotContain("OpCapability ImageQuery", listing, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("glsl")]
    [InlineData("spirv")]
    public void Sampling_a_cube_and_a_volume_by_level_generates(string target) {
        GenerateClean(
            """
            package A

            shader S {
                var volume: Texture3D
                val sky: TextureCube
                var linear: Sampler

                [FragmentShader]
                func Fragment(uvw: float3): float4 {
                    return volume.SampleLevel(linear, uvw, 1f) + sky.SampleLevel(linear, uvw, 1f)
                }
            }

            """,
            target
        );
    }

    static string Returns(string type, string name) {
        var texture = Assert.IsType<BuiltInNamedTypeSymbol>(BuiltInTypes.Lookup(type));
        return Assert.Single(texture.GetMembers().OfType<MethodSymbol>(), m => m.Name == name)
            .ReturnType.ToDisplayString();
    }
}
