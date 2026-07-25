using Vixen.Raven.Symbols;
using Xunit;
using static Tests.SemanticTestBase;

namespace Tests;

/// <summary>
/// Phase 2c: the parts of the semantic model that exist because the target is a
/// GPU — the intrinsic library, entry points, and resource bindings.
/// </summary>
public class ShaderSemanticsTests {
    [Fact]
    public void Stage_attributes_mark_entry_points() {
        var compilation = AssertNoDiagnostics("""
            package A

            shader Lit {
                [VertexShader]
                func Vertex(position: float3): float4 {
                    return float4(position, 1)
                }

                [PixelShader]
                func Pixel(): float4 {
                    return float4(1, 1, 1, 1)
                }

                func Helper(): float {
                    return 1
                }
            }

            """);

        var shader = FindType(compilation, "Lit");

        Assert.Equal(ShaderStage.Vertex, GetMember<MethodSymbol>(shader, "Vertex").Stage);
        Assert.Equal(ShaderStage.Pixel, GetMember<MethodSymbol>(shader, "Pixel").Stage);
        Assert.Equal(ShaderStage.None, GetMember<MethodSymbol>(shader, "Helper").Stage);

        var entryPoints = compilation.GetEntryPoints();
        Assert.Equal(2, entryPoints.Count);
        Assert.Contains(entryPoints, m => m.Stage == ShaderStage.Vertex);
    }

    [Fact]
    public void Two_entry_points_for_one_stage_are_rejected() =>
        AssertDiagnostics("""
            package A

            shader Lit {
                [VertexShader]
                func First(): float4 {
                    return float4(0, 0, 0, 1)
                }

                [VertexShader]
                func Second(): float4 {
                    return float4(0, 0, 0, 1)
                }
            }

            """, "RVN2050");

    [Fact]
    public void A_generic_entry_point_is_rejected() =>
        AssertDiagnostics("""
            package A

            shader Lit {
                [VertexShader]
                func Vertex<T>(value: T): float4 {
                    return float4(0, 0, 0, 1)
                }
            }

            """, "RVN2051");

    [Fact]
    public void A_stage_attribute_outside_a_shader_is_rejected() =>
        AssertDiagnostics("""
            package A

            class Helpers {
                [VertexShader]
                func Vertex(): float4 {
                    return float4(0, 0, 0, 1)
                }
            }

            """, "RVN2052");

    [Fact]
    public void Shader_fields_are_classified_as_bindings() {
        var compilation = AssertNoDiagnostics("""
            package A

            shader Lit {
                const val Bias = 0.001
                var tint: float4
                var albedo: Texture2D
                var linearSampler: Sampler
            }

            """);

        var shader = FindType(compilation, "Lit");

        Assert.Equal(ResourceKind.Uniform, GetMember<FieldSymbol>(shader, "tint").ResourceKind);
        Assert.Equal(ResourceKind.Texture, GetMember<FieldSymbol>(shader, "albedo").ResourceKind);
        Assert.Equal(ResourceKind.Sampler, GetMember<FieldSymbol>(shader, "linearSampler").ResourceKind);

        // A compile-time constant is folded, not bound.
        Assert.Equal(ResourceKind.None, GetMember<FieldSymbol>(shader, "Bias").ResourceKind);
    }

    [Fact]
    public void A_resource_outside_a_shader_is_rejected() =>
        AssertDiagnostics("""
            package A

            struct Material {
                var albedo: Texture2D
            }

            """, "RVN2053");

    [Fact]
    public void Texture_sampling_binds_to_the_built_in_method() =>
        AssertNoDiagnostics("""
            package A

            shader Lit {
                var albedo: Texture2D
                var linearSampler: Sampler

                [PixelShader]
                func Pixel(uv: float2): float4 {
                    return albedo.Sample(linearSampler, uv)
                }
            }

            """);

    [Fact]
    public void Stage_io_semantics_are_read_off_declarations() {
        var compilation = AssertNoDiagnostics("""
            package A

            shader Lit {
                [Semantic("WORLD")]
                var world: mat4

                [PixelShader]
                [Semantic("SV_Target")]
                func Pixel([Semantic("TEXCOORD0")] uv: float2): float4 {
                    return float4(uv, 0, 1)
                }
            }

            """);

        var shader = FindType(compilation, "Lit");

        Assert.Equal("WORLD", GetMember<FieldSymbol>(shader, "world").SemanticName);

        var pixel = GetMember<MethodSymbol>(shader, "Pixel");
        Assert.Equal("SV_Target", pixel.SemanticName);

        // A parameter carries its semantic inline, on the same line as the parameter.
        Assert.Equal("TEXCOORD0", Assert.Single(pixel.Parameters).SemanticName);
    }

    [Fact]
    public void Intrinsics_are_in_scope_without_an_import() {
        Assert.NotEmpty(Intrinsics.Lookup("dot"));
        Assert.NotEmpty(Intrinsics.Lookup("normalize"));
        Assert.NotEmpty(Intrinsics.Lookup("mul"));
        Assert.Empty(Intrinsics.Lookup("definitelyNotAnIntrinsic"));

        AssertNoDiagnostics("""
            package A

            shader Lit {
                func Shade(normal: float3, light: float3): float {
                    return saturate(dot(normalize(normal), normalize(light)))
                }
            }

            """);
    }

    [Fact]
    public void A_realistic_shader_binds_with_no_diagnostics() {
        var compilation = AssertNoDiagnostics("""
            package Vixen.Shaders

            shader Lambert {
                const val Ambient = 0.1

                var world: mat4
                var lightDirection: float3
                var baseColor: float4
                var albedo: Texture2D
                var albedoSampler: Sampler

                func Diffuse(normal: float3): float {
                    val ndotl = dot(normalize(normal), normalize(-lightDirection))
                    return max(ndotl, Ambient)
                }

                [VertexShader]
                func Vertex(position: float3): float4 {
                    return world * float4(position, 1)
                }

                [PixelShader]
                func Pixel(normal: float3, uv: float2): float4 {
                    val sampled = albedo.Sample(albedoSampler, uv)
                    return float4(sampled.rgb * baseColor.rgb * Diffuse(normal), sampled.a)
                }
            }

            """);

        var shader = FindType(compilation, "Lambert");
        Assert.Equal(TypeKind.Shader, shader.TypeKind);
        Assert.Equal(2, compilation.GetEntryPoints().Count);
        Assert.Equal("float4", GetMember<MethodSymbol>(shader, "Pixel").ReturnType.ToDisplayString());
    }
}
