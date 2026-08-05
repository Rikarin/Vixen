// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.Reflection;
using Vixen.Raven.Symbols;
using Xunit;
using static Tests.CodeGenTestBase;

namespace Tests;

/// <summary>
///     <c>SV_VertexID</c> and <c>SV_InstanceID</c> — docs/plan/07 § F, "no <c>SV_VertexID</c>
///     semantic".
/// </summary>
/// <remarks>
///     <para>
///         The same mechanism the dispatch ids already used, widened to the vertex stage: a
///         <c>[Semantic]</c> the pipeline supplies rather than the host. What made it more than a
///         table entry is that a graphics stage <em>has</em> located inputs, so a built-in must not
///         consume a location — one sitting between two attributes would otherwise leave a hole in
///         the vertex layout the host binds against.
///     </para>
///     <para>
///         The vertex table is open where the compute one is closed, and deliberately: an
///         unrecognised semantic on a vertex parameter is <c>POSITION</c> or <c>TEXCOORD0</c>, an
///         ordinary attribute, while a compute stage has no attributes at all.
///     </para>
/// </remarks>
public class StageBuiltInTests {
    const string Fullscreen = """
                              package A

                              shader S {
                                  var tint: float4

                                  [VertexShader]
                                  [Semantic("SV_Position")]
                                  func Vertex([Semantic("SV_VertexID")] id: int, offset: float2, [Semantic("SV_InstanceID")] instance: int): float4 {
                                      val x = float((id & 1) * 2) + offset.x + float(instance)
                                      val y = float(id & 2) + offset.y
                                      return float4(x - 1f, y - 1f, 0f, 1f)
                                  }

                                  [FragmentShader]
                                  [Semantic("SV_Target")]
                                  func Fragment(): float4 => tint
                              }

                              """;

    [Theory]
    [InlineData("SV_VertexID", StageBuiltIn.VertexId, "gl_VertexIndex")]
    [InlineData("SV_InstanceID", StageBuiltIn.InstanceId, "gl_InstanceIndex")]
    public void The_table_answers_for_the_vertex_stage_and_not_for_others(
        string semantic,
        StageBuiltIn expected,
        string glsl
    ) {
        var builtIn = StageBuiltIns.Of(semantic, ShaderStage.Vertex);

        Assert.NotNull(builtIn);
        Assert.Equal(expected, builtIn.BuiltIn);
        Assert.Equal(glsl, builtIn.GlslName);

        // Signed in both targets, unlike the dispatch ids and unlike HLSL.
        Assert.Same(BuiltInTypes.Int, builtIn.Type);

        // A stage that does not supply it does not recognise the name.
        Assert.Null(StageBuiltIns.Of(semantic, ShaderStage.Fragment));
        Assert.Null(StageBuiltIns.Of(semantic, ShaderStage.Compute));
    }

    [Fact]
    public void A_built_in_consumes_no_location_so_the_attributes_keep_theirs() {
        var unit = Assert.Single(GenerateClean(Fullscreen), u => u.Name.EndsWith(".vert", StringComparison.Ordinal));

        // `offset` is the second parameter but the only attribute, so it is location 0 — not 1.
        Assert.Contains("layout(location = 0) in vec2 in_offset;", unit.Code, StringComparison.Ordinal);
        Assert.DoesNotContain("in_id", unit.Code, StringComparison.Ordinal);
        Assert.DoesNotContain("in_instance", unit.Code, StringComparison.Ordinal);

        // main threads GLSL's own variables straight through, in parameter order.
        Assert.Contains(
            "Vertex(gl_VertexIndex, in_offset, gl_InstanceIndex)",
            unit.Code,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void SPIR_V_decorates_it_BuiltIn_rather_than_Location() {
        if (!SpirvTestBase.ValidatorAvailable) {
            return;
        }

        var listing = ReferenceCompiler.Disassemble(
            Assert.Single(GenerateClean(Fullscreen, "spirv"), u => u.Name.EndsWith(".vert", StringComparison.Ordinal))
                .Binary!
        );

        // The two decorations are mutually exclusive, which is why the location had to be skipped
        // rather than merely unused.
        Assert.Contains("OpDecorate %in_id BuiltIn VertexIndex", listing, StringComparison.Ordinal);
        Assert.Contains("OpDecorate %in_instance BuiltIn InstanceIndex", listing, StringComparison.Ordinal);
        Assert.Contains("OpDecorate %in_offset Location 0", listing, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The reflection list <em>is</em> the vertex layout, so a value the host does not supply
    ///     is absent from it rather than listed with a placeholder location.
    /// </summary>
    [Fact]
    public void The_vertex_layout_holds_only_what_the_host_binds() {
        var module = LoweringTestBase.Lower(Fullscreen);
        var reflection = ReflectionBuilder.Describe(LoweringTestBase.FindShader(module, "S"));

        var input = Assert.Single(reflection.VertexInputs);
        Assert.Equal("offset", input.Name);
        Assert.Equal(0, input.Location);
    }

    [Fact]
    public void A_built_in_declared_at_the_wrong_type_is_refused() {
        var error = Assert.Single(
            SemanticTestBase.Diagnose(
                """
                package A

                shader S {
                    [VertexShader]
                    func Vertex([Semantic("SV_VertexID")] id: uint): float4 => float4(float(id), 0, 0, 1)
                }

                """
            ),
            d => d.Id == "RVN2109"
        );

        Assert.True(error.IsError);
        Assert.Contains("int", error.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>
    ///     An unrecognised semantic on a vertex parameter is an ordinary attribute — the whole
    ///     reason the vertex table is open where the compute one is closed.
    /// </summary>
    [Fact]
    public void An_unrecognised_vertex_semantic_is_still_an_attribute() {
        var unit = Assert.Single(
            GenerateClean(
                """
                package A

                shader S {
                    [VertexShader]
                    [Semantic("SV_Position")]
                    func Vertex([Semantic("POSITION")] p: float3, [Semantic("NORMAL")] n: float3): float4 {
                        return float4(p + n, 1)
                    }
                }

                """
            )
        );

        Assert.Contains("layout(location = 0) in vec3 in_p;", unit.Code, StringComparison.Ordinal);
        Assert.Contains("layout(location = 1) in vec3 in_n;", unit.Code, StringComparison.Ordinal);
    }

    [Fact]
    public void The_dispatch_ids_still_work_the_way_they_did() {
        var unit = Assert.Single(
            GenerateClean(
                """
                package A

                shader S {
                    var data: RWBuffer<float>

                    [ComputeShader(8)]
                    func Main([Semantic("SV_DispatchThreadID")] id: uint3) {
                        data[int(id.x)] = 1f
                    }
                }

                """
            )
        );

        Assert.Contains("Main(gl_GlobalInvocationID)", unit.Code, StringComparison.Ordinal);
    }

    [Fact]
    public void A_shader_can_be_drawn_with_no_vertex_buffer_at_all() {
        // The point of SV_VertexID: a full-screen triangle binds nothing and derives everything.
        const string Triangle = """
                                package A

                                shader S {
                                    stream var uv: float2

                                    [VertexShader]
                                    [Semantic("SV_Position")]
                                    func Vertex([Semantic("SV_VertexID")] id: int): float4 {
                                        uv = float2(float((id & 1) * 2), float(id & 2))
                                        return float4(uv.x * 2f - 1f, uv.y * 2f - 1f, 0f, 1f)
                                    }

                                    [FragmentShader]
                                    [Semantic("SV_Target")]
                                    func Fragment(): float4 => float4(uv.x, uv.y, 0f, 1f)
                                }

                                """;

        var module = LoweringTestBase.Lower(Triangle);
        var reflection = ReflectionBuilder.Describe(LoweringTestBase.FindShader(module, "S"));

        // Nothing at all: `uv` is a stream this stage *writes*, so it is an output, and the only
        // input is a built-in the pipeline supplies. That empty list is the vertex layout.
        Assert.Empty(reflection.VertexInputs);

        GenerateClean(Triangle);
        GenerateClean(Triangle, "spirv");
    }

    /// <summary>
    ///     <c>SV_IsFrontFace</c>, which is the fragment stage's entry in the same table — and the
    ///     one built-in whose type is <c>bool</c>.
    /// </summary>
    /// <remarks>
    ///     A two-sided pipeline has no other way to shade the inside of an open shape: the normal
    ///     arrives pointing away from the viewer and only the rasterizer knows which winding it
    ///     saw. What it cost beyond a table row is the exemption below — <c>StageInterface</c>
    ///     refuses a boolean because a <em>located</em> interface variable has no boolean
    ///     representation, and a built-in has no location for that rule to be about.
    /// </remarks>
    const string TwoSided = """
                            package A

                            shader S {
                                stream var normalWS: float3

                                [VertexShader]
                                [Semantic("SV_Position")]
                                func Vertex(position: float3, normal: float3): float4 {
                                    normalWS = normal
                                    return float4(position, 1f)
                                }

                                [FragmentShader]
                                [Semantic("SV_Target")]
                                func Fragment([Semantic("SV_IsFrontFace")] frontFacing: bool): float4 {
                                    var surface = normalize(normalWS)

                                    if (!frontFacing) {
                                        surface = -surface
                                    }

                                    return float4(surface, 1f)
                                }
                            }

                            """;

    [Fact]
    public void The_table_answers_for_the_fragment_stage_and_not_for_others() {
        var builtIn = StageBuiltIns.Of("SV_IsFrontFace", ShaderStage.Fragment);

        Assert.NotNull(builtIn);
        Assert.Equal(StageBuiltIn.IsFrontFace, builtIn.BuiltIn);
        Assert.Equal("gl_FrontFacing", builtIn.GlslName);
        Assert.Same(BuiltInTypes.Bool, builtIn.Type);

        // The vertex table is open, so an unrecognised name there is an ordinary attribute rather
        // than this built-in reached from the wrong stage.
        Assert.Null(StageBuiltIns.Of("SV_IsFrontFace", ShaderStage.Vertex));
        Assert.Null(StageBuiltIns.Of("SV_IsFrontFace", ShaderStage.Compute));
    }

    [Fact]
    public void GLSL_threads_gl_FrontFacing_through_and_declares_no_input_for_it() {
        var unit = Assert.Single(GenerateClean(TwoSided), u => u.Name.EndsWith(".frag", StringComparison.Ordinal));

        Assert.Contains("Fragment(gl_FrontFacing)", unit.Code, StringComparison.Ordinal);
        Assert.DoesNotContain("in_frontFacing", unit.Code, StringComparison.Ordinal);

        // The stream keeps location 0: a built-in consumed nothing, here as on the vertex stage.
        Assert.Contains("layout(location = 0) in vec3 in_normalWS;", unit.Code, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A boolean is refused as a <em>located</em> input and accepted as a built-in, which is
    ///     the whole of the distinction <c>StageInterface</c> now draws.
    /// </summary>
    [Fact]
    public void SPIR_V_decorates_the_boolean_BuiltIn_FrontFacing() {
        if (!SpirvTestBase.ValidatorAvailable) {
            return;
        }

        var listing = ReferenceCompiler.Disassemble(
            Assert.Single(GenerateClean(TwoSided, "spirv"), u => u.Name.EndsWith(".frag", StringComparison.Ordinal))
                .Binary!
        );

        Assert.Contains("OpDecorate %in_frontFacing BuiltIn FrontFacing", listing, StringComparison.Ordinal);
        Assert.DoesNotContain("OpDecorate %in_frontFacing Location", listing, StringComparison.Ordinal);
    }

    [Fact]
    public void A_boolean_that_is_not_a_built_in_is_still_refused() {
        var error = Assert.Single(
            SemanticTestBase.Diagnose(
                """
                package A

                shader S {
                    stream var lit: bool

                    [VertexShader]
                    [Semantic("SV_Position")]
                    func Vertex(position: float3): float4 {
                        lit = position.x > 0f
                        return float4(position, 1f)
                    }
                }

                """
            ),
            d => d.Id == "RVN2103"
        );

        Assert.True(error.IsError);
    }

    [Fact]
    public void It_is_absent_from_the_vertex_layout_the_host_binds() {
        var module = LoweringTestBase.Lower(TwoSided);
        var reflection = ReflectionBuilder.Describe(LoweringTestBase.FindShader(module, "S"));

        // Only what a vertex buffer feeds. A fragment built-in was never a candidate, but the
        // list is the vertex layout and a claim about it is worth pinning.
        Assert.Equal(["position", "normal"], reflection.VertexInputs.Select(input => input.Name));
    }

    /// <summary>
    ///     <c>SV_Position</c> on a fragment parameter — the window coordinate, not a varying.
    /// </summary>
    /// <remarks>
    ///     The gap the terrain's grass found: the semantic a vertex stage <em>returns</em> under
    ///     had no fragment-stage entry, so a fragment declaring it got an ordinary located input —
    ///     one location past the streams, which the vertex stage never writes. Some desktop
    ///     drivers absorb the dangling input; Metal refuses the pipeline. The stipple pattern that
    ///     wants pixel coordinates has no other source for them.
    /// </remarks>
    const string Stippled = """
                            package A

                            shader S {
                                stream var fade: float

                                [VertexShader]
                                [Semantic("SV_Position")]
                                func Vertex(position: float3): float4 {
                                    fade = position.y
                                    return float4(position, 1f)
                                }

                                [FragmentShader]
                                [Semantic("SV_Target")]
                                func Fragment([Semantic("SV_Position")] fragment: float4): float4 {
                                    val noise = frac(52.9829189f * frac(dot(fragment.xy, float2(0.06711056f, 0.00583715f))))

                                    if (noise >= fade) {
                                        discard
                                    }

                                    return float4(1f, 1f, 1f, 1f)
                                }
                            }

                            """;

    [Fact]
    public void The_fragment_position_answers_for_the_fragment_stage_and_not_for_others() {
        var builtIn = StageBuiltIns.Of("SV_Position", ShaderStage.Fragment);

        Assert.NotNull(builtIn);
        Assert.Equal(StageBuiltIn.FragmentPosition, builtIn.BuiltIn);
        Assert.Equal("gl_FragCoord", builtIn.GlslName);
        Assert.Same(BuiltInTypes.Float4, builtIn.Type);

        // The vertex table stays open — SV_Position *there* is the output semantic, and an input
        // spelled that way is an ordinary attribute the way POSITION is.
        Assert.Null(StageBuiltIns.Of("SV_Position", ShaderStage.Vertex));
        Assert.Null(StageBuiltIns.Of("SV_Position", ShaderStage.Compute));
    }

    [Fact]
    public void GLSL_threads_gl_FragCoord_through_and_declares_no_input_for_it() {
        var unit = Assert.Single(GenerateClean(Stippled), u => u.Name.EndsWith(".frag", StringComparison.Ordinal));

        Assert.Contains("Fragment(gl_FragCoord)", unit.Code, StringComparison.Ordinal);
        Assert.DoesNotContain("in_fragment", unit.Code, StringComparison.Ordinal);

        // The stream keeps location 0: the built-in consumed nothing.
        Assert.Contains("layout(location = 0) in float in_fade;", unit.Code, StringComparison.Ordinal);
    }

    [Fact]
    public void SPIR_V_decorates_the_fragment_position_BuiltIn_FragCoord() {
        if (!SpirvTestBase.ValidatorAvailable) {
            return;
        }

        var listing = ReferenceCompiler.Disassemble(
            Assert.Single(GenerateClean(Stippled, "spirv"), u => u.Name.EndsWith(".frag", StringComparison.Ordinal))
                .Binary!
        );

        Assert.Contains("OpDecorate %in_fragment BuiltIn FragCoord", listing, StringComparison.Ordinal);
        Assert.DoesNotContain("OpDecorate %in_fragment Location", listing, StringComparison.Ordinal);
    }
}
