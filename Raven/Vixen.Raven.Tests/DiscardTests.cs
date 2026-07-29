// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax;
using Vixen.Raven.Syntax;
using Xunit;
using static Tests.CodeGenTestBase;
using static Tests.LoweringTestBase;

namespace Tests;

/// <summary>
///     <c>discard</c> — docs/plan/07 § F, the last of the small stage intrinsics.
/// </summary>
/// <remarks>
///     <para>
///         A keyword rather than an intrinsic call, and that is the whole reason it was not just a
///         table entry: it is a <em>terminator</em>. A function signature cannot say "control does
///         not come back", so as a call nothing after it would be known to be unreachable, the flow
///         analysis would demand a return on a path that has none, and both emitters would have to
///         guess where the block ended.
///     </para>
///     <para>
///         Which stages may reach it is a call-graph question rather than a syntactic one, because
///         a helper belongs to whichever stages call it — see
///         <see cref="A_discard_reachable_from_a_vertex_stage_is_refused" />.
///     </para>
/// </remarks>
public class DiscardTests {
    const string Cutout = """
                          package A

                          shader S {
                              var opacityMap: Texture2D
                              var opacitySampler: Sampler
                              var alphaCutoff: float = 0.5f

                              [FragmentShader]
                              [Semantic("SV_Target")]
                              func Fragment(uv: float2): float4 {
                                  if (opacityMap.Sample(opacitySampler, uv).a < alphaCutoff) {
                                      discard
                                  }

                                  return float4(1f, 1f, 1f, 1f)
                              }
                          }

                          """;

    // --- Through the pipeline ----------------------------------------------

    [Fact]
    public void It_lowers_to_a_terminator_of_its_own() {
        var module = Lower(Cutout);

        // Not a call, not a return: the IR keeps it as the terminator it is, so both backends can
        // see that the block ends here without inspecting a callee.
        Assert.Contains("discard", PrintFunction(module, "Fragment"), StringComparison.Ordinal);
        Assert.DoesNotContain(module.AllFunctions, function => function.Name == "discard");
    }

    [Fact]
    public void GLSL_spells_it_discard() =>
        Assert.Contains("discard;", GenerateOne(Cutout), StringComparison.Ordinal);

    /// <summary>
    ///     SPIR-V spells it <c>OpKill</c>, which is a block terminator — so nothing follows it and
    ///     no branch to a merge block is emitted.
    /// </summary>
    [Fact]
    public void SPIRV_spells_it_OpKill() =>
        Assert.Contains("OpKill", SpirvTestBase.One(Cutout).Code, StringComparison.Ordinal);

    /// <summary>A <c>discard</c> inside a helper reaches the target through the call.</summary>
    [Theory]
    [InlineData("glsl")]
    [InlineData("spirv")]
    public void It_survives_a_call_boundary(string target) =>
        GenerateClean(
            """
            package A

            shader S {
                var opacityMap: Texture2D
                var opacitySampler: Sampler
                var alphaCutoff: float = 0.5f

                [FragmentShader]
                [Semantic("SV_Target")]
                func Fragment(uv: float2): float4 {
                    Cut(uv)
                    return float4(1f, 1f, 1f, 1f)
                }

                func Cut(uv: float2) {
                    if (opacityMap.Sample(opacitySampler, uv).a < alphaCutoff) {
                        discard
                    }
                }
            }

            """,
            target
        );

    // --- The terminator rule -----------------------------------------------

    /// <summary>
    ///     A path that discards does not fall off the end, so a value-returning function whose
    ///     only other exit is a <c>discard</c> is complete.
    /// </summary>
    /// <remarks>
    ///     The alternative — demanding a <c>return</c> after it — would be a diagnostic on correct
    ///     code, and worse, would ask the author to invent a colour for a fragment that no longer
    ///     exists.
    /// </remarks>
    [Fact]
    public void A_function_whose_last_path_discards_needs_no_return() =>
        Assert.Empty(
            SemanticTestBase.Diagnose(
                """
                package A

                shader S {
                    var tint: float4

                    [FragmentShader]
                    [Semantic("SV_Target")]
                    func Fragment(): float4 {
                        if (tint.a > 0f) {
                            return tint
                        }

                        discard
                    }
                }

                """
            )
        );

    /// <summary>Anything after it is dead, and says so — naming the <c>discard</c>, not a return.</summary>
    [Fact]
    public void A_statement_after_it_is_unreachable() {
        var warning = Assert.Single(
            SemanticTestBase.Diagnose(
                """
                package A

                shader S {
                    var tint: float4

                    [FragmentShader]
                    [Semantic("SV_Target")]
                    func Fragment(): float4 {
                        discard
                        return tint
                    }
                }

                """
            ),
            d => d.Id == "RVN2128"
        );

        Assert.False(warning.IsError);
        Assert.Contains("a 'discard'", warning.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>
    ///     Both arms leaving means the statement leaves, whichever way each one does it.
    /// </summary>
    [Fact]
    public void An_if_whose_arms_return_and_discard_leaves_the_function() =>
        Assert.Empty(
            SemanticTestBase.Diagnose(
                """
                package A

                shader S {
                    var tint: float4

                    [FragmentShader]
                    [Semantic("SV_Target")]
                    func Fragment(): float4 {
                        if (tint.a > 0f) {
                            return tint
                        } else {
                            discard
                        }
                    }
                }

                """
            )
        );

    /// <summary>
    ///     GLSL is given a <c>return</c> it will never run, because glslang refuses a
    ///     value-returning function whose end its own flow analysis can reach — and a
    ///     <c>discard</c> does not stop that analysis the way <c>OpKill</c> stops a block.
    /// </summary>
    [Fact]
    public void The_GLSL_backend_closes_a_function_the_kill_left_open() {
        const string Tail = """
                            package A

                            shader S {
                                var tint: float4

                                [FragmentShader]
                                [Semantic("SV_Target")]
                                func Fragment(): float4 {
                                    if (tint.a > 0f) {
                                        return tint
                                    }

                                    discard
                                }
                            }

                            """;

        var glsl = GenerateOne(Tail);
        Assert.Contains("discard;", glsl, StringComparison.Ordinal);
        Assert.Contains("_discarded0;", glsl, StringComparison.Ordinal);

        // SPIR-V needs none of it, and needs nothing in its place either: OpKill terminates the
        // block it is in, which here is the function's last, so the function is already complete.
        // `SpirvTestBase.One` runs spirv-val, so this also pins that the module is well formed.
        var listing = SpirvTestBase.One(Tail).Code;
        Assert.Contains("OpKill", listing, StringComparison.Ordinal);
        Assert.DoesNotContain("_discarded", listing, StringComparison.Ordinal);
    }

    /// <summary>A function that cannot discard gets no dead return, so nothing else grows one.</summary>
    [Fact]
    public void A_function_that_cannot_discard_is_left_alone() =>
        Assert.DoesNotContain("_discarded", GenerateOne(Cutout.Replace("discard", "return float4(0f, 0f, 0f, 0f)")),
            StringComparison.Ordinal);

    // --- Which stages may reach it -----------------------------------------

    /// <summary>
    ///     Reachability rather than where the keyword is written: the helper below is fine for the
    ///     fragment stage that calls it and wrong for the vertex stage that also does, and the file
    ///     it lives in cannot tell.
    /// </summary>
    [Fact]
    public void A_discard_reachable_from_a_vertex_stage_is_refused() {
        var error = Assert.Single(
            LoweringDiagnosticsOf(
                """
                package A

                shader S {
                    var cutoff: float = 0.5f

                    [VertexShader]
                    [Semantic("SV_Position")]
                    func Vertex(position: float3): float4 {
                        Cut(position.x)
                        return float4(position, 1f)
                    }

                    [FragmentShader]
                    [Semantic("SV_Target")]
                    func Fragment(): float4 {
                        Cut(cutoff)
                        return float4(1f, 1f, 1f, 1f)
                    }

                    func Cut(v: float) {
                        if (v < cutoff) {
                            discard
                        }
                    }
                }

                """
            ),
            d => d.Id == "RVN3008"
        );

        Assert.True(error.IsError);

        // The span is the keyword, not the entry point: that is where the fix goes.
        Assert.Contains("vertex entry point 'Vertex'", error.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_discard_in_a_compute_stage_is_refused() =>
        Assert.Contains(
            LoweringDiagnosticsOf(
                """
                package A

                shader S {
                    var output: RWBuffer<float>

                    [ComputeShader(64)]
                    func Main(threadId: uint3) {
                        if (output[int(threadId.x)] < 0f) {
                            discard
                        }

                        output[int(threadId.x)] = 1f
                    }
                }

                """
            ),
            d => d.Id == "RVN3008"
        );

    /// <summary>One function that discards is one diagnostic, however many bad stages reach it.</summary>
    [Fact]
    public void It_is_said_once_per_function() =>
        Assert.Single(
            LoweringDiagnosticsOf(
                """
                package A

                shader S {
                    var cutoff: float = 0.5f
                    var output: RWBuffer<float>

                    [VertexShader]
                    [Semantic("SV_Position")]
                    func Vertex(position: float3): float4 {
                        Cut(position.x)
                        return float4(position, 1f)
                    }

                    [ComputeShader(64)]
                    func Main(threadId: uint3) {
                        Cut(output[int(threadId.x)])
                    }

                    func Cut(v: float) {
                        if (v < cutoff) {
                            discard
                        }
                    }
                }

                """
            ),
            d => d.Id == "RVN3008"
        );

    // --- Syntax -------------------------------------------------------------

    /// <summary>
    ///     A statement, so it takes a line of its own like <c>break</c> — not an expression that
    ///     could hide inside a condition.
    /// </summary>
    [Fact]
    public void It_is_a_statement_rather_than_an_expression() =>
        Assert.NotEmpty(
            SyntaxTree.ParseText(
                """
                package A

                shader S {
                    [FragmentShader]
                    func Fragment(): float4 {
                        return discard
                    }
                }

                """
            ).Diagnostics
        );

    /// <summary>The keyword round-trips, so the tree still reproduces the file byte for byte.</summary>
    [Fact]
    public void It_round_trips_through_the_tree() {
        var tree = SyntaxTree.ParseText(Cutout);

        Assert.Empty(tree.Diagnostics);
        Assert.Equal(Cutout, tree.GetRoot().ToFullString());
        Assert.Single(tree.GetRoot().DescendantNodes().OfType<DiscardStatementSyntax>());
    }
}
