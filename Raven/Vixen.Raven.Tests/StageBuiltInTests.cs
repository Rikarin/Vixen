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
}
