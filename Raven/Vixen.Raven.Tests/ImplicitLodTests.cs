// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;
using static Tests.LoweringTestBase;

namespace Tests;

/// <summary>
///     <c>RVN3013</c> — a derivative-implied <c>Sample</c> reached from a stage with no derivatives.
/// </summary>
/// <remarks>
///     <para>
///         The one stage rule in this family that is a warning. <c>RVN3008</c> and <c>RVN3012</c>
///         are errors because the alternative to reporting them is <c>spirv-val</c> rejecting a
///         module; here the emitters already substitute level 0 and the module is valid. That is
///         exactly why it needs saying: the failure is silent, and it is silent in the direction of
///         looking like it worked.
///     </para>
///     <para>
///         It found one in the shipped library on the day it was written. <c>VisibilityResolve</c> is
///         a compute dispatch that shades, it reaches <c>Lighting.ShadowTap</c> through
///         <c>ClusteredShading.SampleCascade</c>, and that tap had been an implicit-LOD sample since
///         it was written.
///     </para>
/// </remarks>
public class ImplicitLodTests {
    [Theory]
    [InlineData("[ComputeShader(64)]\n    func Main(id: uint3)", "compute")]
    [InlineData("[VertexShader]\n    func Vertex(uv: float2)", "vertex")]
    public void A_stage_without_derivatives_is_warned_about(string entry, string stage) {
        var diagnostics = LoweringDiagnosticsOf(
            $$"""
              package A

              shader S {
                  var albedo: Texture2D
                  var linear: Sampler
                  var output: RWBuffer<float>

                  {{entry}} {
                      output[0] = albedo.Sample(linear, float2(0f, 0f)).r
                  }
              }

              """
        );

        var warning = Assert.Single(diagnostics, d => d.Id == "RVN3013");

        Assert.False(warning.IsError);
        Assert.Contains($"{stage} entry point", warning.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>A fragment stage is the one that has them, so it is the one that is not warned.</summary>
    [Fact]
    public void A_fragment_stage_is_left_alone() {
        var diagnostics = LoweringDiagnosticsOf(
            """
            package A

            shader S {
                var albedo: Texture2D
                var linear: Sampler

                [FragmentShader]
                [Semantic("SV_Target")]
                func Fragment(uv: float2): float4 {
                    return albedo.Sample(linear, uv)
                }
            }

            """
        );

        Assert.DoesNotContain(diagnostics, d => d.Id == "RVN3013");
    }

    /// <summary>Stating the level is the fix, and it is one word.</summary>
    [Fact]
    public void An_explicit_level_says_what_the_author_meant() {
        var diagnostics = LoweringDiagnosticsOf(
            """
            package A

            shader S {
                var albedo: Texture2D
                var linear: Sampler
                var output: RWBuffer<float>

                [ComputeShader(64)]
                func Main(id: uint3) {
                    output[0] = albedo.SampleLevel(linear, float2(0f, 0f), 0f).r
                }
            }

            """
        );

        Assert.DoesNotContain(diagnostics, d => d.Id == "RVN3013");
    }

    /// <summary>
    ///     Reachability, not where the call is written — <c>RVN3008</c>'s rule, and the reason the
    ///     library case was invisible: the sample is in <c>Lighting.rvn</c> and the stage is three
    ///     files away.
    /// </summary>
    [Fact]
    public void The_stage_rule_follows_the_call_graph() {
        var diagnostics = LoweringDiagnosticsOf(
            """
            package A

            shader S {
                var albedo: Texture2D
                var linear: Sampler
                var output: RWBuffer<float>

                func Tap(uv: float2): float {
                    return albedo.Sample(linear, uv).r
                }

                [ComputeShader(64)]
                func Main(id: uint3) {
                    output[0] = Tap(float2(0f, 0f))
                }
            }

            """
        );

        Assert.Contains(diagnostics, d => d.Id == "RVN3013");
    }
}
