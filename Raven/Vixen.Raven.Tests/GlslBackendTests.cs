// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

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
        var generated = GenerateClean(
            """
            package A

            shader Lit {
                [VertexShader]
                func Vertex(position: float3): float4 {
                    return float4(position, 1)
                }

                [FragmentShader]
                func Fragment(): float4 {
                    return float4(1, 1, 1, 1)
                }
            }

            """
        );

        Assert.Equal(2, generated.Count);
        Assert.Equal(["Lit.vert", "Lit.frag"], generated.Select(g => g.Name));
        Assert.Equal([ShaderStage.Vertex, ShaderStage.Fragment], generated.Select(g => g.Stage));
        Assert.All(generated, unit => Assert.StartsWith("#version 450", unit.Code));
    }

    [Fact]
    public void A_unit_only_carries_the_functions_its_stage_reaches() {
        var generated = GenerateClean(
            """
            package A

            shader Lit {
                func OnlyVertex(): float {
                    return 1
                }

                func OnlyFragment(): float {
                    return 2
                }

                [VertexShader]
                func Vertex(): float4 {
                    return float4(OnlyVertex(), 0, 0, 1)
                }

                [FragmentShader]
                func Fragment(): float4 {
                    return float4(OnlyFragment(), 0, 0, 1)
                }
            }

            """
        );

        var vertex = generated.Single(g => g.Stage == ShaderStage.Vertex).Code;
        var fragment = generated.Single(g => g.Stage == ShaderStage.Fragment).Code;

        Assert.Contains("OnlyVertex", vertex);
        Assert.DoesNotContain("OnlyFragment", vertex);
        Assert.Contains("OnlyFragment", fragment);
        Assert.DoesNotContain("OnlyVertex", fragment);
    }

    [Fact]
    public void Uniforms_go_into_a_std140_block_and_textures_get_their_own_binding() {
        var code = GeneratePixel(
            "        return albedo.Sample(linear, uv) * tint",
            "    var tint: float4\n    var albedo: Texture2D\n    var linear: Sampler\n",
            "func Fragment(uv: float2): float4"
        );

        Assert.Contains("layout(std140, set = 2, binding = 0) uniform SPerMaterialUniforms {", code);
        Assert.Contains("vec4 tint;", code);
        Assert.Contains("layout(set = 2, binding = 1) uniform texture2D albedo;", code);
    }

    /// <summary>
    ///     Vulkan GLSL keeps the texture and the sampler as two bindings and pairs them at the
    ///     sample site, exactly as SPIR-V does. That is what makes the two backends agree about
    ///     binding indices — a combined <c>sampler2D</c> would consume one binding where SPIR-V
    ///     consumes two.
    /// </summary>
    [Fact]
    public void A_texture_and_a_sampler_stay_two_bindings_and_combine_at_the_sample() {
        var code = GeneratePixel(
            "        return albedo.Sample(linear, uv)",
            "    var albedo: Texture2D\n    var linear: Sampler\n",
            "func Fragment(uv: float2): float4"
        );

        Assert.Contains("layout(set = 2, binding = 0) uniform texture2D albedo;", code);
        Assert.Contains("layout(set = 2, binding = 1) uniform sampler linear;", code);
        Assert.Contains("texture(sampler2D(albedo, linear),", code);

        // Nothing is dropped any more, so nothing is reported as dropped.
        Assert.DoesNotContain("sampler2D albedo", code);
    }

    /// <summary>
    ///     A fetch by integer coordinate has no sampler to pair with, which is what the
    ///     extension is for — declared only in the units that need it, because a driver may
    ///     reject an extension the shader does not use.
    /// </summary>
    [Fact]
    public void A_texel_fetch_declares_the_samplerless_extension() {
        var fetching = GeneratePixel(
            "        return albedo.Load(int3(1, 2, 0))",
            "    var albedo: Texture2D\n",
            "func Fragment(): float4"
        );

        Assert.Contains("#extension GL_EXT_samplerless_texture_functions : require", fetching);
        Assert.Contains("texelFetch(albedo,", fetching);

        var sampling = GeneratePixel(
            "        return albedo.Sample(linear, uv)",
            "    var albedo: Texture2D\n    var linear: Sampler\n",
            "func Fragment(uv: float2): float4"
        );

        Assert.DoesNotContain("GL_EXT_samplerless_texture_functions", sampling);
    }

    /// <summary>
    ///     One <c>layout(set = …)</c> per marker, from the same plan the SPIR-V backend reads.
    /// </summary>
    [Fact]
    public void Every_set_gets_its_own_block_named_for_the_set() {
        var code = GeneratePixel(
            "        return tint * time",
            "    [PerFrame] var time: float\n    var tint: float4\n",
            "func Fragment(): float4"
        );

        Assert.Contains("layout(std140, set = 0, binding = 0) uniform SPerFrameUniforms {", code);
        Assert.Contains("layout(std140, set = 2, binding = 0) uniform SPerMaterialUniforms {", code);
    }

    [Fact]
    public void A_vertex_position_goes_to_gl_Position_rather_than_an_out_variable() {
        var code = GenerateOne(
            """
            package A

            shader S {
                [VertexShader]
                [Semantic("SV_Position")]
                func Vertex(position: float3): float4 {
                    return float4(position, 1)
                }
            }

            """
        );

        Assert.Contains("layout(location = 0) in vec3 in_position;", code);
        Assert.Contains("gl_Position = Vertex(in_position);", code);
        Assert.DoesNotContain("out vec4", code);
    }

    [Fact]
    public void A_pixel_result_becomes_a_located_out_variable_keeping_its_semantic() {
        var code = GeneratePixel("        return float4(1, 1, 1, 1)");

        Assert.Contains("layout(location = 0) out vec4 out_result;", code);
        Assert.Contains("out_result = Fragment();", code);
    }

    [Theory]
    [InlineData("int", "int")]
    [InlineData("uint", "uint")]
    [InlineData("float", "float")]
    [InlineData("double", "double")]
    [InlineData("float3", "vec3")]
    [InlineData("int4", "ivec4")]
    [InlineData("uint2", "uvec2")]
    [InlineData("double2", "dvec2")]
    [InlineData("mat3", "mat3")]
    public void Types_map_onto_their_glsl_spelling(string raven, string glsl) {
        var code = GeneratePixel(
            "        return float4(0, 0, 0, 1)",
            $"    var probe: {raven}\n"
        );

        Assert.Contains($"{glsl} probe;", code);
    }

    /// <summary>
    ///     The boolean spellings, asked of the namer rather than of a uniform block.
    /// </summary>
    /// <remarks>
    ///     Not a row of the theory above, because the theory declares its probe as a binding and a
    ///     binding cannot hold a boolean — <c>RVN2137</c>. The mapping is still worth pinning, and
    ///     it is worth pinning <em>here</em>, because a boolean does reach GLSL: as a local, as a
    ///     <c>groupshared</c>, and as the result of every comparison the language has. Same shape as
    ///     <see cref="A_matrix_flips_to_glsl_column_major_naming" />, which asks the namer directly
    ///     for the case a block cannot show either.
    /// </remarks>
    [Fact]
    public void Boolean_types_map_onto_their_glsl_spelling() {
        Assert.Equal("bool", GlslTypes.Name(IrScalarType.Bool));
        Assert.Equal("bvec3", GlslTypes.Name(new IrVectorType(IrScalarType.Bool, 3)));
    }

    [Fact]
    public void A_matrix_flips_to_glsl_column_major_naming() {
        // Raven `mat2x3` is 2 rows by 3 columns; GLSL writes that as `mat3x2`.
        Assert.Equal("mat3x2", GlslTypes.Name(new IrMatrixType(IrScalarType.Float, 2, 3)));
        Assert.Equal("mat3", GlslTypes.Name(new IrMatrixType(IrScalarType.Float, 3, 3)));

        // Which is what keeps `m * v` meaning the same thing in both languages.
        var code = GeneratePixel(
            "        return float4(m * v, 1)",
            "    var m: mat3x4\n    var v: float4\n"
        );

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
            "    var v: float3\n    var f: float\n"
        );

        Assert.Contains(expected, code);
    }

    [Fact]
    public void Saturate_expands_because_glsl_has_no_such_builtin() {
        var code = GeneratePixel(
            "        val probe = saturate(f)\n        return float4(0, 0, 0, 1)",
            "    var f: float\n"
        );

        Assert.Contains("clamp(", code);
        Assert.Contains("float(0.0), float(1.0)", code);
    }

    [Fact]
    public void Comparing_vectors_uses_glsls_component_wise_functions() {
        var code = GeneratePixel(
            "        val mask = v < v\n        return float4(0, 0, 0, 1)",
            "    var v: float3\n"
        );

        // `a < b` on vectors is a function in GLSL, and it yields a bvec.
        Assert.Contains("bvec3", code);
        Assert.Contains("lessThan(", code);
    }

    [Fact]
    public void A_counted_loop_runs_its_step_where_continue_can_reach_it() {
        var code = GeneratePixel(
            """
                    var total = 0f
                    for (i in 0 .. 3) {
                        total += 1f
                    }

                    return float4(total, 0, 0, 1)
            """
        );

        // The step is hoisted to the top of the body behind a first-iteration
        // flag, because GLSL's `continue` jumps there.
        Assert.Contains("bool _loop0_first = true;", code);
        Assert.Contains("while (true) {", code);
        Assert.Contains("if (_loop0_first) {", code);
        Assert.Contains("break;", code);
    }

    [Fact]
    public void An_if_statement_survives_as_an_if_statement() {
        // A uniform `float` compared, rather than the `bool` uniform this used to declare: a binding
        // cannot hold a boolean (RVN2137). The condition is still a value the compiler cannot fold,
        // which is the only thing the shape of the branch depends on.
        var code = GeneratePixel(
            """
                    if (level > 0f) {
                        return float4(1, 0, 0, 1)
                    } else {
                        return float4(0, 1, 0, 1)
                    }
            """,
            "    var level: float\n"
        );

        // The read is loaded into a temporary first, as every read is, and the comparison into
        // another — which is what the condition of the `if` then names.
        Assert.Contains("float _0 = level;", code);
        Assert.Contains("bool _2 = (_0 > 0.0);", code);
        Assert.Contains("if (_2) {", code);
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
        var code = GeneratePixel("        val a = 1f\n        val b = 2u\n        return float4(0, 0, 0, 1)");

        Assert.Contains("= 1.0;", code);
        Assert.Contains("= 2u;", code);
    }

    [Fact]
    public void Identifiers_that_collide_with_glsl_keywords_are_mangled() {
        var code = GeneratePixel(
            "        return float4(sample, 0, 0, 1)",
            "    var sample: float\n"
        );

        // `sample` is a GLSL keyword.
        Assert.Contains("float sample_;", code);
        Assert.DoesNotContain("float sample;", code);
    }

    [Fact]
    public void A_struct_and_its_methods_come_through_with_an_explicit_receiver() {
        var code = GenerateOne(
            """
            package A

            struct Ray {
                var origin: float3
                var direction: float3

                func At(t: float): float3 => origin + direction * t
            }

            shader S {
                [FragmentShader]
                func Fragment(): float4 {
                    var ray: Ray
                    ray.origin = float3(0, 0, 0)
                    ray.direction = float3(0, 0, 1)
                    return float4(ray.At(1f), 1)
                }
            }

            """
        );

        Assert.Contains("struct Ray {", code);
        Assert.Contains("vec3 origin;", code);
        Assert.Contains("vec3 At(Ray self, float t)", code);
    }

    /// <summary>
    ///     An unsized array is still refused here, though nothing written in Raven reaches it.
    /// </summary>
    /// <remarks>
    ///     <c>RVN2126</c> now catches the declaration, which is where the fix is. This stays as a
    ///     backstop for the one route that skips the binder — an unsized array decoded out of a
    ///     <c>.rvnlib</c> — and is built from the IR directly, because there is no longer any source
    ///     that produces one.
    /// </remarks>
    [Fact]
    public void An_unsized_array_is_rejected_rather_than_emitted() {
        Assert.Contains(
            UnsizedArrayDiagnostics("glsl"),
            d => d.Id == "RVN4001" && d.IsError
        );
    }

    /// <summary>
    ///     A compute stage declares its workgroup size and nothing else: no locations, because
    ///     nothing feeds a compute invocation and nothing takes its result.
    /// </summary>
    [Fact]
    public void A_compute_entry_point_declares_its_workgroup_size() {
        var glsl = GenerateOne(
            """
            package A

            shader S {
                [ComputeShader(8, 4, 2)]
                func Main() { }
            }

            """
        );

        Assert.Contains(
            "layout(local_size_x = 8, local_size_y = 4, local_size_z = 2) in;",
            glsl,
            StringComparison.Ordinal
        );

        Assert.DoesNotContain("layout(location", glsl, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A dimension left off is 1 — what a 1-D dispatch means, and what both targets default
    ///     to, so <c>[ComputeShader(64)]</c> does not have to spell the other two.
    /// </summary>
    [Fact]
    public void AnOmittedWorkgroupDimensionIsOne() {
        var glsl = GenerateOne(
            """
            package A

            shader S {
                [ComputeShader(64)]
                func Main() { }
            }

            """
        );

        Assert.Contains(
            "layout(local_size_x = 64, local_size_y = 1, local_size_z = 1) in;",
            glsl,
            StringComparison.Ordinal
        );
    }

    /// <summary>
    ///     Each dispatch built-in reaches the GLSL variable that carries it, passed straight into
    ///     the entry point rather than copied through a declared input.
    /// </summary>
    [Theory]
    [InlineData("SV_DispatchThreadID", "uint3", "gl_GlobalInvocationID")]
    [InlineData("SV_GroupID", "uint3", "gl_WorkGroupID")]
    [InlineData("SV_GroupThreadID", "uint3", "gl_LocalInvocationID")]
    [InlineData("SV_GroupIndex", "uint", "gl_LocalInvocationIndex")]
    public void EachDispatchBuiltInReachesItsGlslVariable(string semantic, string type, string expected) {
        var glsl = GenerateOne(
            $$"""
              package A

              shader S {
                  [ComputeShader(64)]
                  func Main([Semantic("{{semantic}}")] id: {{type}}) { }
              }

              """
        );

        Assert.Contains($"Main({expected});", glsl, StringComparison.Ordinal);
    }
}
