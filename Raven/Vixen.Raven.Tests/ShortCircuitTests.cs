// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;
using static Tests.LoweringTestBase;

namespace Tests;

/// <summary>
///     <c>&amp;&amp;</c>, <c>||</c> and <c>?:</c> evaluate their guarded operand only when
///     control flow reaches it — docs/plan/07 § I, "&amp;&amp; and || do not short-circuit".
/// </summary>
/// <remarks>
///     The rule is conditional on purpose, so both halves of it are pinned here: an operand that
///     can index, call or assign is put behind a branch, and everything else keeps the cheaper
///     branch-free form. A GPU pays for a branch with the whole warp, and an implicit-LOD sample
///     moved under one would have undefined derivatives — so "always branch" would be a
///     regression dressed as a fix.
/// </remarks>
public class ShortCircuitTests {
    const string Guarded = """
                           package A

                           shader S {
                               var data: Buffer<float>
                               var count: int

                               func Read(i: int): bool {
                                   return i < count && data[i] > 0f
                               }
                           }

                           """;

    [Fact]
    public void An_indexing_right_operand_of_and_runs_only_when_the_left_one_held() {
        var module = Lower(Guarded);

        // The index is inside the `if`, which is the whole point: `data[i]` never runs
        // for an `i` the bound rejected.
        Assert.Equal(
            """
            func Read($i : i32) : bool
              local !and : bool
              %0 = load $i : i32
              %1 = load @count : i32
              %2 = lessThan %0, %1 : bool
              store !and, %2
              %3 = load !and : bool
              if %3
                %4 = load $i : i32
                %5 = load @data[%4] : f32
                %6 = const 0f : f32
                %7 = greaterThan %5, %6 : bool
                store !and, %7
              end
              %8 = load !and : bool
              return %8
            end

            """,
            PrintFunction(module, "Read")
        );
    }

    [Fact]
    public void An_indexing_right_operand_of_or_runs_only_when_the_left_one_did_not_hold() {
        var module = Lower(
            """
            package A

            shader S {
                var data: Buffer<float>
                var count: int

                func Read(i: int): bool {
                    return i >= count || data[i] > 0f
                }
            }

            """
        );

        var body = PrintFunction(module, "Read");

        // `||` runs its right operand when the left one was false, so the test is negated
        // rather than the branches being swapped.
        Assert.Contains("%4 = not %3 : bool", body, StringComparison.Ordinal);
        Assert.Contains("if %4", body, StringComparison.Ordinal);
        Assert.Contains("load @data", body[body.IndexOf("if %4", StringComparison.Ordinal)..], StringComparison.Ordinal);
        Assert.DoesNotContain("logicalOr", body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_call_on_the_right_is_guarded_because_it_may_store() {
        var module = Lower(
            """
            package A

            shader S {
                var results: RWBuffer<float>

                func Record(i: int): bool {
                    results[i] = 1f
                    return true
                }

                func Run(enabled: bool, i: int): bool {
                    return enabled && Record(i)
                }
            }

            """
        );

        var body = PrintFunction(module, "Run");
        Assert.Contains("if ", body, StringComparison.Ordinal);
        Assert.DoesNotContain("logicalAnd", body, StringComparison.Ordinal);
    }

    [Fact]
    public void An_assignment_on_the_right_is_guarded_because_its_effect_is_the_point() {
        var module = Lower(
            """
            package A

            shader S {
                func Run(a: bool): bool {
                    var seen = false
                    if (a && (seen = true)) {
                        return seen
                    }

                    return seen
                }
            }

            """
        );

        Assert.DoesNotContain("logicalAnd", PrintFunction(module, "Run"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_pure_right_operand_keeps_the_branch_free_form() {
        var module = Lower(
            """
            package A

            shader S {
                var albedo: Texture2D
                var linear: Sampler

                func Run(a: float, b: float, uv: float2): bool {
                    return a > 0f && b < 1f || dot(albedo.Sample(linear, uv).rgb, float3(1, 1, 1)) > 2f
                }
            }

            """
        );

        var body = PrintFunction(module, "Run");

        // Arithmetic, swizzles and the intrinsic library are pure, and a sample must stay in
        // uniform control flow for its derivatives to be defined.
        Assert.Contains("logicalAnd", body, StringComparison.Ordinal);
        Assert.Contains("logicalOr", body, StringComparison.Ordinal);
        Assert.DoesNotContain("if ", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Chained_operators_nest_one_branch_per_guarded_operand() {
        var module = Lower(
            """
            package A

            shader S {
                var data: Buffer<float>
                var count: int

                func Read(i: int): bool {
                    return i >= 0 && i < count && data[i] > 0f
                }
            }

            """
        );

        var body = PrintFunction(module, "Read");

        // `(i >= 0 && i < count) && data[i] > 0f`: only the outer operator has a guarded
        // right operand, so the inner one still folds into a single logicalAnd.
        Assert.Single(body.Split("if ")[1..]);
        Assert.Contains("logicalAnd", body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_guarded_conditional_becomes_a_branch_rather_than_a_select() {
        var module = Lower(
            """
            package A

            shader S {
                var data: Buffer<float>
                var count: int

                func Read(i: int): float {
                    return i < count ? data[i] : 0f
                }
            }

            """
        );

        var body = PrintFunction(module, "Read");
        Assert.Contains("if ", body, StringComparison.Ordinal);
        Assert.DoesNotContain("select", body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pure_conditional_stays_a_select() {
        var module = Lower(
            """
            package A

            shader S {
                func Pick(a: float, b: float, c: bool): float {
                    return c ? a * 2f : b - 1f
                }
            }

            """
        );

        var body = PrintFunction(module, "Pick");
        Assert.Contains("select", body, StringComparison.Ordinal);
        Assert.DoesNotContain("if ", body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_guard_inside_a_loop_condition_lowers_into_the_condition_block() {
        // The condition of a loop is its own block, so an operator that has to emit a branch
        // has somewhere to put it. Verified end to end rather than assumed: the IR verifier
        // runs on everything Lower() returns.
        var module = Lower(
            """
            package A

            shader S {
                var data: Buffer<float>
                var count: int

                func Sum(): float {
                    var total = 0f
                    var i = 0
                    while (i < count && data[i] > 0f) {
                        total += data[i]
                        i += 1
                    }

                    return total
                }
            }

            """
        );

        var body = PrintFunction(module, "Sum");
        Assert.Contains("cond", body, StringComparison.Ordinal);
        Assert.DoesNotContain("logicalAnd", body, StringComparison.Ordinal);
    }
}
