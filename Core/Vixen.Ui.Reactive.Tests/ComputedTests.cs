// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Reactive.Tests;

/// <summary>
///     Derivation. The tests that matter are the ones counting evaluations rather than checking
///     values: a graph that produces the right answers by recomputing everything is not the thing
///     being built.
/// </summary>
public class ComputedTests {
    [Fact]
    public void A_computed_nobody_reads_is_never_evaluated() {
        var runs = 0;
        var source = new Signal<int>(1);
        var derived = new Computed<int>(() => {
                runs++;
                return source.Value * 2;
            }
        );

        source.Value = 2;
        source.Value = 3;

        Assert.Equal(0, runs);
        Assert.Equal(6, derived.Value);
        Assert.Equal(1, runs);
    }

    [Fact]
    public void A_computed_is_memoised_until_a_dependency_moves() {
        var runs = 0;
        var source = new Signal<int>(1);
        var derived = new Computed<int>(() => {
                runs++;
                return source.Value * 2;
            }
        );

        _ = derived.Value;
        _ = derived.Value;
        _ = derived.Value;

        Assert.Equal(1, runs);

        source.Value = 2;
        _ = derived.Value;

        Assert.Equal(2, runs);
    }

    [Fact]
    public void A_diamond_evaluates_its_join_exactly_once_per_change() {
        // The canonical glitch test from doc 09: a → b, a → c, b + c → d. A naive push-evaluate
        // graph runs d twice per write to a, and briefly with one input updated and the other not.
        var joins = 0;
        var a = new Signal<int>(1);
        var b = new Computed<int>(() => a.Value + 1);
        var c = new Computed<int>(() => a.Value * 10);
        var d = new Computed<int>(() => {
                joins++;
                return b.Value + c.Value;
            }
        );

        Assert.Equal(12, d.Value);
        Assert.Equal(1, joins);

        a.Value = 2;

        Assert.Equal(23, d.Value);
        Assert.Equal(2, joins);
    }

    [Fact]
    public void An_unchanged_result_stops_the_invalidation_where_it_is() {
        // The equality short-circuit. `parity` changes only every other write, and `downstream`
        // must not re-run on the writes where it did not.
        var downstreamRuns = 0;
        var source = new Signal<int>(0);
        var parity = new Computed<int>(() => source.Value % 2);
        var downstream = new Computed<string>(() => {
                downstreamRuns++;
                return parity.Value == 0 ? "even" : "odd";
            }
        );

        Assert.Equal("even", downstream.Value);
        Assert.Equal(1, downstreamRuns);

        source.Value = 2;

        Assert.Equal("even", downstream.Value);
        Assert.Equal(1, downstreamRuns);

        source.Value = 3;

        Assert.Equal("odd", downstream.Value);
        Assert.Equal(2, downstreamRuns);
    }

    [Fact]
    public void The_dependency_set_narrows_as_well_as_widens() {
        var expensiveReads = 0;
        var enabled = new Signal<bool>(true);
        var expensive = new Signal<int>(1);
        var guarded = new Computed<int>(() => {
                if (!enabled.Value) {
                    return -1;
                }

                expensiveReads++;
                return expensive.Value;
            }
        );

        Assert.Equal(1, guarded.Value);
        Assert.Equal(2, guarded.DependencyCount);

        enabled.Value = false;

        Assert.Equal(-1, guarded.Value);
        Assert.Equal(1, guarded.DependencyCount);

        // No longer read, so no longer a dependency: writing it must not wake the computed up.
        expensive.Value = 99;

        Assert.Equal(-1, guarded.Value);
        Assert.Equal(1, expensiveReads);
    }

    [Fact]
    public void A_computed_that_reads_itself_is_reported_rather_than_looped() {
        Computed<int>? self = null;
        self = new Computed<int>(() => self!.Value + 1);

        Assert.Throws<InvalidOperationException>(() => self.Value);
    }

    [Fact]
    public void A_failed_computation_rethrows_on_every_read_until_its_inputs_change() {
        var source = new Signal<int>(0);
        var derived = new Computed<int>(() => 10 / source.Value);

        Assert.Throws<DivideByZeroException>(() => derived.Value);
        Assert.Throws<DivideByZeroException>(() => derived.Value);

        source.Value = 5;

        Assert.Equal(2, derived.Value);
    }

    [Fact]
    public void A_failure_propagates_to_dependents_rather_than_being_swallowed() {
        var source = new Signal<int>(1);
        var failing = new Computed<int>(() => source.Value == 0 ? throw new InvalidOperationException("no") : source.Value);
        var downstream = new Computed<int>(() => failing.Value + 1);

        Assert.Equal(2, downstream.Value);

        source.Value = 0;

        Assert.Throws<InvalidOperationException>(() => downstream.Value);
    }

    [Fact]
    public void A_chain_only_re_evaluates_the_part_that_a_write_reaches() {
        var leftRuns = 0;
        var rightRuns = 0;
        var left = new Signal<int>(1);
        var right = new Signal<int>(1);
        var leftDerived = new Computed<int>(() => {
                leftRuns++;
                return left.Value + 1;
            }
        );

        var rightDerived = new Computed<int>(() => {
                rightRuns++;
                return right.Value + 1;
            }
        );

        _ = leftDerived.Value;
        _ = rightDerived.Value;

        left.Value = 5;

        Assert.Equal(6, leftDerived.Value);
        Assert.Equal(2, rightDerived.Value);
        Assert.Equal(2, leftRuns);
        Assert.Equal(1, rightRuns);
    }
}
