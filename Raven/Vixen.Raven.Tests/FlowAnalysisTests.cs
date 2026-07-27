// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Tests;

/// <summary>
///     Definite assignment and reachability — docs/plan/07 § I, "flow analysis".
/// </summary>
/// <remarks>
///     <para>
///         Reading an unassigned local on a GPU is not an exception and not a zero: it is whatever
///         was in the register, which differs between drivers, between invocations, and between
///         debug and release. That is why it is an error rather than a warning — it is the shape of
///         bug that reproduces on one machine and nowhere else.
///     </para>
///     <para>
///         Sound and deliberately incomplete: a read is refused only when <em>no</em> path to it
///         assigns the local. What the analysis will not claim is where the false positives would
///         be, and each of those boundaries is pinned below.
///     </para>
/// </remarks>
public class FlowAnalysisTests {
    static IReadOnlyList<Vixen.Core.Syntax.Diagnostics.Diagnostic> Body(string body, string members = "") =>
        SemanticTestBase.Diagnose(
            $$"""
              package A

              shader S {
                  var tint: float4
                  var mode: int
              {{members}}
                  [PixelShader]
                  [Semantic("SV_Target")]
                  func Pixel(): float4 {
              {{body}}
                  }
              }

              """
        );

    static void Clean(string body, string members = "") => Assert.Empty(Body(body, members));

    static void Reports(string id, string body, string members = "") =>
        Assert.Contains(Body(body, members), d => d.Id == id);

    // --- Definite assignment -----------------------------------------------

    [Fact]
    public void A_local_read_before_any_assignment_is_refused() =>
        Reports(
            "RVN2127",
            """
                    var x: float
                    return float4(x, 0, 0, 1)
            """
        );

    /// <summary>One mistake is one diagnostic, however many times the local is read.</summary>
    [Fact]
    public void It_is_said_once_per_local() {
        var reported = Body(
            """
                    var x: float
                    var y: float
                    return float4(x, x, y, 1)
            """
        );

        Assert.Equal(2, reported.Count(d => d.Id == "RVN2127"));
    }

    [Theory]
    // Assigned before the read, whole or by field.
    [InlineData("        var x: float\n        x = 1f\n        return float4(x, 0, 0, 1)")]
    [InlineData("        var x = 1f\n        return float4(x, 0, 0, 1)")]

    // Filling a struct field by field is how a value is initialised in a language with no
    // constructor requirement, so writing through a local assigns it rather than reading it.
    [InlineData("        var v: float4\n        v.x = 1f\n        return v")]

    // Both arms assign, so it is assigned after.
    [InlineData("        var x: float\n        if (mode > 0) {\n            x = 1f\n        } else {\n            x = 2f\n        }\n        return float4(x, 0, 0, 1)")]

    // The arm that does not fall through contributes nothing to take away.
    [InlineData("        var x: float\n        if (mode > 0) {\n            return tint\n        }\n        x = 1f\n        return float4(x, 0, 0, 1)")]

    // A `repeat` body runs before the first test, so what it assigns is assigned after.
    [InlineData("        var x: float\n        repeat {\n            x = 1f\n        } while (mode > 0)\n        return float4(x, 0, 0, 1)")]

    // Every section returns and there is a default, so the switch never falls through.
    [InlineData("        switch (mode) {\n            case 0:\n                return tint\n            default:\n                return tint * 2f\n        }")]
    public void What_the_analysis_accepts(string body) => Clean(body);

    /// <summary>
    ///     One arm is not every path. The same rule C# applies, and the reason it is worth applying:
    ///     the other path reads a register nobody wrote.
    /// </summary>
    [Fact]
    public void A_local_assigned_in_only_one_arm_is_not_assigned_after() =>
        Reports(
            "RVN2127",
            """
                    var x: float
                    if (mode > 0) {
                        x = 1f
                    }

                    return float4(x, 0, 0, 1)
            """
        );

    /// <summary>A loop body may run zero times, so nothing it assigns is assigned after it.</summary>
    [Theory]
    [InlineData("        var x: float\n        while (mode > 0) {\n            x = 1f\n        }\n        return float4(x, 0, 0, 1)")]
    [InlineData("        var x: float\n        for (i in 0 .. mode) {\n            x = 1f\n        }\n        return float4(x, 0, 0, 1)")]
    public void A_loop_body_does_not_assign_after_the_loop(string body) => Reports("RVN2127", body);

    /// <summary>
    ///     Without a <c>default</c> the governing value may match nothing, so falling past the
    ///     statement is a path of its own — and it assigns nothing.
    /// </summary>
    [Fact]
    public void A_switch_with_no_default_is_one_more_path() =>
        Reports(
            "RVN2127",
            """
                    var x: float
                    switch (mode) {
                        case 0:
                            x = 1f
                            break
                    }

                    return float4(x, 0, 0, 1)
            """
        );

    /// <summary>
    ///     An <c>inout</c> argument counts as written rather than read.
    /// </summary>
    /// <remarks>
    ///     Strictly it is both — <c>inout</c> is copy-in/copy-out — but filling a value is what it
    ///     is for: Raven has no <c>out</c>, and <c>MaterialSurface</c>'s contract is a feature
    ///     accumulating into a surface the caller declared. Requiring the caller to zero it first
    ///     would be a diagnostic on correct code.
    /// </remarks>
    [Fact]
    public void An_inout_argument_is_treated_as_written() =>
        Clean(
            """
                    var x: float
                    Fill(x)
                    return float4(x, 0, 0, 1)
            """,
            "    func Fill(inout v: float) {\n        v = 1f\n    }\n"
        );

    /// <summary>An index inside a write target is still read, because it decides where the write goes.</summary>
    [Fact]
    public void An_index_in_a_write_target_is_still_a_read() =>
        Reports(
            "RVN2127",
            """
                    var i: int
                    var data: float[4]
                    data[i] = 1f
                    return float4(data[0], 0, 0, 1)
            """
        );

    // --- Reachability ------------------------------------------------------

    [Theory]
    [InlineData("return tint", "a 'return'")]
    public void A_statement_after_a_jump_is_reported(string jump, string reason) {
        var warning = Assert.Single(
            Body($"        {jump}\n        var x = 1f\n"),
            d => d.Id == "RVN2128"
        );

        Assert.False(warning.IsError);
        Assert.Contains(reason, warning.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>Said once per run, because a block of dead code is one mistake.</summary>
    [Fact]
    public void A_run_of_unreachable_statements_is_one_diagnostic() =>
        Assert.Single(
            Body(
                """
                        return tint
                        var x = 1f
                        var y = 2f
                        var z = 3f
                """
            ),
            d => d.Id == "RVN2128"
        );

    [Fact]
    public void Unreachable_code_inside_a_loop_after_a_break_is_reported() =>
        Reports(
            "RVN2128",
            """
                    for (i in 0 .. 4) {
                        break
                        tint = tint
                    }

                    return tint
            """
        );

    // --- Falling off the end -----------------------------------------------

    /// <summary>
    ///     The same undefined value seen from the other end: a function that promises a value and
    ///     reaches its end hands the caller whatever the target had.
    /// </summary>
    [Fact]
    public void A_value_returning_function_that_can_reach_its_end_is_refused() {
        var error = Assert.Single(
            Body(
                """
                        if (mode > 0) {
                            return tint
                        }
                """
            ),
            d => d.Id == "RVN2129"
        );

        Assert.True(error.IsError);
    }

    [Fact]
    public void A_void_function_may_reach_its_end() =>
        Clean(
            """
                    Noop()
                    return tint
            """,
            "    func Noop() {\n    }\n"
        );

    /// <summary>
    ///     A constructor is exempt: it hands back the value it built, and lowering supplies the
    ///     return rather than the author.
    /// </summary>
    [Fact]
    public void A_constructor_need_not_return() =>
        Assert.Empty(
            SemanticTestBase.Diagnose(
                """
                package A

                struct Ray {
                    var origin: float3

                    init(o: float3) {
                        origin = o
                    }
                }

                shader S {
                    [PixelShader]
                    [Semantic("SV_Target")]
                    func Pixel(): float4 => float4(Ray(float3(0, 0, 0)).origin, 1)
                }

                """
            )
        );
}
