using Vixen.Raven.CodeGen;
using Vixen.Raven.CodeGen.Glsl;
using Vixen.Raven.IR;
using Vixen.Raven.Symbols;
using Xunit;
using static Tests.CodeGenTestBase;

namespace Tests;

/// <summary>Phase 4: the IR generates GLSL, one translation unit per stage.</summary>
public class GlslBackendTests {
    [Fact]
    public void The_backend_is_reachable_by_name() {
        Assert.Contains("glsl", TargetBackends.Names);

        var backend = TargetBackends.Create("GLSL");
        Assert.NotNull(backend);
        Assert.Equal("glsl", backend.Name);
        Assert.Equal(".glsl", backend.FileExtension);

        Assert.Null(TargetBackends.Create("nope"));
    }

    [Fact]
    public void Each_entry_point_becomes_its_own_unit() {
        var generated = GenerateClean("""
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
            }

            """);

        Assert.Equal(2, generated.Count);
        Assert.Equal(["Lit.vert", "Lit.frag"], generated.Select(g => g.Name));
        Assert.Equal([ShaderStage.Vertex, ShaderStage.Pixel], generated.Select(g => g.Stage));
        Assert.All(generated, unit => Assert.StartsWith("#version 450", unit.Code));
    }

    [Fact]
    public void A_unit_only_carries_the_functions_its_stage_reaches() {
        var generated = GenerateClean("""
            package A

            shader Lit {
                func OnlyVertex(): float {
                    return 1
                }

                func OnlyPixel(): float {
                    return 2
                }

                [VertexShader]
                func Vertex(): float4 {
                    return float4(OnlyVertex(), 0, 0, 1)
                }

                [PixelShader]
                func Pixel(): float4 {
                    return float4(OnlyPixel(), 0, 0, 1)
                }
            }

            """);

        var vertex = generated.Single(g => g.Stage == ShaderStage.Vertex).Code;
        var pixel = generated.Single(g => g.Stage == ShaderStage.Pixel).Code;

        Assert.Contains("OnlyVertex", vertex);
        Assert.DoesNotContain("OnlyPixel", vertex);
        Assert.Contains("OnlyPixel", pixel);
        Assert.DoesNotContain("OnlyVertex", pixel);
    }

    [Fact]
    public void Uniforms_go_into_a_std140_block_and_textures_get_their_own_binding() {
        var code = GeneratePixel(
            "        return albedo.Sample(linear, uv) * tint",
            "    var tint: float4\n    var albedo: Texture2D\n    var linear: Sampler\n",
            "func Pixel(uv: float2): float4");

        Assert.Contains("layout(std140, binding = 0) uniform SUniforms {", code);
        Assert.Contains("vec4 tint;", code);
        Assert.Contains("layout(binding = 1) uniform sampler2D albedo;", code);

        // GLSL has no standalone sampler object, so the sampler binding folds away.
        Assert.DoesNotContain("uniform sampler linear", code);
        Assert.Contains("texture(albedo,", code);
    }

    [Fact]
    public void Dropping_the_sampler_binding_is_reported_rather_than_silent() {
        Generate("""
            package A

            shader S {
                var albedo: Texture2D
                var linear: Sampler

                [PixelShader]
                func Pixel(uv: float2): float4 {
                    return albedo.Sample(linear, uv)
                }
            }

            """, out var diagnostics);

        var dropped = Assert.Single(diagnostics.Where(d => d.Id == "RVN4003").Distinct());
        Assert.Contains("linear", dropped.GetMessage());
        Assert.False(dropped.IsError);
    }

    [Fact]
    public void A_vertex_position_goes_to_gl_Position_rather_than_an_out_variable() {
        var code = GenerateOne("""
            package A

            shader S {
                [VertexShader]
                [Semantic("SV_Position")]
                func Vertex(position: float3): float4 {
                    return float4(position, 1)
                }
            }

            """);

        Assert.Contains("layout(location = 0) in vec3 in_position;", code);
        Assert.Contains("gl_Position = Vertex(in_position);", code);
        Assert.DoesNotContain("out vec4", code);
    }

    [Fact]
    public void A_pixel_result_becomes_a_located_out_variable_keeping_its_semantic() {
        var code = GeneratePixel("        return float4(1, 1, 1, 1)");

        Assert.Contains("layout(location = 0) out vec4 out_result;", code);
        Assert.Contains("out_result = Pixel();", code);
    }

    [Theory]
    [InlineData("bool", "bool")]
    [InlineData("int", "int")]
    [InlineData("uint", "uint")]
    [InlineData("float", "float")]
    [InlineData("double", "double")]
    [InlineData("float3", "vec3")]
    [InlineData("int4", "ivec4")]
    [InlineData("uint2", "uvec2")]
    [InlineData("bool3", "bvec3")]
    [InlineData("double2", "dvec2")]
    [InlineData("mat3", "mat3")]
    public void Types_map_onto_their_glsl_spelling(string raven, string glsl) {
        var code = GeneratePixel(
            "        return float4(0, 0, 0, 1)",
            $"    var probe: {raven}\n");

        Assert.Contains($"{glsl} probe;", code);
    }

    [Fact]
    public void A_matrix_flips_to_glsl_column_major_naming() {
        // Raven `mat2x3` is 2 rows by 3 columns; GLSL writes that as `mat3x2`.
        Assert.Equal("mat3x2", GlslTypes.Name(new IrMatrixType(IrScalarType.Float, 2, 3)));
        Assert.Equal("mat3", GlslTypes.Name(new IrMatrixType(IrScalarType.Float, 3, 3)));

        // Which is what keeps `m * v` meaning the same thing in both languages.
        var code = GeneratePixel(
            "        return float4(m * v, 1)",
            "    var m: mat3x4\n    var v: float4\n");

        Assert.Contains("mat4x3 m;", code);
    }

    [Theory]
    [InlineData("normalize(v)", "normalize(")]
    [InlineData("lerp(v, v, 0.5f)", "mix(")]
    [InlineData("frac(f)", "fract(")]
    [InlineData("rsqrt(f)", "inversesqrt(")]
    [InlineData("ddx(f)", "dFdx(")]
    [InlineData("atan2(f, f)", "atan(")]
    [InlineData("trunc(f)", "trunc(")]
    public void Intrinsics_map_onto_glsl_builtins(string expression, string expected) {
        var code = GeneratePixel(
            $"        val probe = {expression}\n        return float4(0, 0, 0, 1)",
            "    var v: float3\n    var f: float\n");

        Assert.Contains(expected, code);
    }

    [Fact]
    public void Saturate_expands_because_glsl_has_no_such_builtin() {
        var code = GeneratePixel(
            "        val probe = saturate(f)\n        return float4(0, 0, 0, 1)",
            "    var f: float\n");

        Assert.Contains("clamp(", code);
        Assert.Contains("float(0.0), float(1.0)", code);
    }

    [Fact]
    public void Comparing_vectors_uses_glsls_component_wise_functions() {
        var code = GeneratePixel(
            "        val mask = v < v\n        return float4(0, 0, 0, 1)",
            "    var v: float3\n");

        // `a < b` on vectors is a function in GLSL, and it yields a bvec.
        Assert.Contains("bvec3", code);
        Assert.Contains("lessThan(", code);
    }

    [Fact]
    public void A_counted_loop_runs_its_step_where_continue_can_reach_it() {
        var code = GeneratePixel("""
                    var total = 0f
                    for (i in 0 .. 3) {
                        total += 1f
                    }

                    return float4(total, 0, 0, 1)
            """);

        // The step is hoisted to the top of the body behind a first-iteration
        // flag, because GLSL's `continue` jumps there.
        Assert.Contains("bool _loop0_first = true;", code);
        Assert.Contains("while (true) {", code);
        Assert.Contains("if (_loop0_first) {", code);
        Assert.Contains("break;", code);
    }

    [Fact]
    public void An_if_statement_survives_as_an_if_statement() {
        var code = GeneratePixel("""
                    if (flag) {
                        return float4(1, 0, 0, 1)
                    } else {
                        return float4(0, 1, 0, 1)
                    }
            """, "    var flag: bool\n");

        // The condition is loaded into a temporary first, as every read is.
        Assert.Contains("bool _0 = flag;", code);
        Assert.Contains("if (_0) {", code);
        Assert.Contains("} else {", code);
    }

    [Fact]
    public void Constants_are_inlined_rather_than_named() {
        var code = GeneratePixel("        return float4(1, 2, 3, 4)");

        // A constant is pure, so it needs no temporary.
        Assert.Contains("vec4(1.0, 2.0, 3.0, 4.0)", code);
        Assert.DoesNotContain("float _0 = 1.0;", code);
    }

    [Fact]
    public void Float_literals_keep_a_decimal_point_and_uints_keep_their_suffix() {
        var code = GeneratePixel(
            "        val a = 1f\n        val b = 2u\n        return float4(0, 0, 0, 1)");

        Assert.Contains("= 1.0;", code);
        Assert.Contains("= 2u;", code);
    }

    [Fact]
    public void Identifiers_that_collide_with_glsl_keywords_are_mangled() {
        var code = GeneratePixel(
            "        return float4(sample, 0, 0, 1)",
            "    var sample: float\n");

        // `sample` is a GLSL keyword.
        Assert.Contains("float sample_;", code);
        Assert.DoesNotContain("float sample;", code);
    }

    [Fact]
    public void A_struct_and_its_methods_come_through_with_an_explicit_receiver() {
        var code = GenerateOne("""
            package A

            struct Ray {
                var origin: float3
                var direction: float3

                func At(t: float): float3 => origin + direction * t
            }

            shader S {
                [PixelShader]
                func Pixel(): float4 {
                    var ray: Ray
                    return float4(ray.At(1f), 1)
                }
            }

            """);

        Assert.Contains("struct Ray {", code);
        Assert.Contains("vec3 origin;", code);
        Assert.Contains("vec3 At(Ray self, float t)", code);
    }

    [Fact]
    public void An_unsized_array_is_rejected_rather_than_emitted() {
        Generate("""
            package A

            shader S {
                var lookup: int[]

                [PixelShader]
                func Pixel(): float4 {
                    return float4(lookup[0], 0, 0, 1)
                }
            }

            """, out var diagnostics);

        Assert.Contains(diagnostics, d => d.Id == "RVN4001" && d.IsError);
    }

    [Fact]
    public void A_compute_entry_point_is_reported_rather_than_guessed_at() {
        Generate("""
            package A

            shader S {
                [ComputeShader]
                func Main() { }
            }

            """, out var diagnostics);

        // A workgroup size has to come from somewhere, and nothing declares one.
        Assert.Contains(diagnostics, d => d.Id == "RVN4002" && d.IsError);
    }
}
