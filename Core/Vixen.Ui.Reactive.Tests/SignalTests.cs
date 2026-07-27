// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Reactive.Tests;

/// <summary>
///     The writable end of the graph. Most of what matters here is what a write does <i>not</i> do:
///     an equal value changes nothing, and no value is ever computed as a side effect of setting one.
/// </summary>
public class SignalTests {
    [Fact]
    public void A_signal_holds_what_it_was_given() {
        var count = new Signal<int>(3);

        Assert.Equal(3, count.Value);

        count.Value = 5;

        Assert.Equal(5, count.Value);
    }

    [Fact]
    public void Writing_an_equal_value_does_not_move_the_version() {
        // The property everything downstream is built on: a text box re-assigning the same string on
        // every keystroke must not repaint the window.
        var name = new Signal<string>("hello");
        var before = name.Version;

        name.Value = "hel" + "lo";

        Assert.Equal(before, name.Version);

        name.Value = "goodbye";

        Assert.NotEqual(before, name.Version);
    }

    [Fact]
    public void A_comparer_that_never_reports_equal_makes_every_write_propagate() {
        var runs = 0;
        var source = new Signal<int>(1, SignalComparer.Never<int>());
        var derived = new Computed<int>(() => {
                runs++;
                return source.Value;
            }
        );

        _ = derived.Value;
        source.Value = 1;
        _ = derived.Value;

        Assert.Equal(2, runs);
    }

    [Fact]
    public void Invalidate_propagates_a_change_made_in_place() {
        var items = new List<string> { "a" };
        var source = new Signal<List<string>>(items);
        var count = new Computed<int>(() => source.Value.Count);

        Assert.Equal(1, count.Value);

        items.Add("b");

        // The instance is the same, so the setter would have decided nothing changed. Nothing did,
        // as far as the signal can see — which is exactly why this needs saying out loud.
        Assert.Equal(1, count.Value);

        source.Invalidate();

        Assert.Equal(2, count.Value);
    }

    [Fact]
    public void Peek_reads_without_becoming_a_dependency() {
        var tracked = new Signal<int>(1);
        var peeked = new Signal<int>(10);
        var runs = 0;
        var sum = new Computed<int>(() => {
                runs++;
                return tracked.Value + peeked.Peek();
            }
        );

        Assert.Equal(11, sum.Value);

        peeked.Value = 20;

        Assert.Equal(11, sum.Value);
        Assert.Equal(1, runs);

        tracked.Value = 2;

        Assert.Equal(22, sum.Value);
        Assert.Equal(2, runs);
    }

    [Fact]
    public void Untracked_covers_a_whole_block() {
        var tracked = new Signal<int>(1);
        var hidden = new Signal<int>(10);
        var sum = new Computed<int>(() => tracked.Value + ReactiveGraph.Untracked(() => hidden.Value));

        Assert.Equal(11, sum.Value);

        hidden.Value = 20;

        Assert.Equal(11, sum.Value);
    }

    [Fact]
    public void Update_reads_the_current_value_without_subscribing_to_it() {
        var count = new Signal<int>(1);
        var scheduler = new EffectScheduler();
        var runs = 0;

        // An effect that increments the signal it is reading would loop forever; one that increments
        // a signal it only writes must not. Update's read is untracked, which is what makes the
        // second case expressible at all.
        var trigger = new Signal<int>(0);
        using var effect = new Effect(() => {
                runs++;
                _ = trigger.Value;
                count.Update(static value => value + 1);
            },
            scheduler
        );

        scheduler.Flush();

        Assert.Equal(1, runs);
        Assert.Equal(2, count.Peek());

        scheduler.Flush();

        Assert.Equal(1, runs);
    }

    [Fact]
    public void Writing_a_signal_from_inside_a_computed_is_refused() {
        var source = new Signal<int>(1);
        var target = new Signal<int>(0);
        var bad = new Computed<int>(() => {
                target.Value = source.Value;
                return source.Value;
            }
        );

        var thrown = Assert.Throws<InvalidOperationException>(() => bad.Value);

        Assert.Contains("computed", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_signal_is_only_reachable_from_the_thread_that_owns_the_graph() {
        var signal = new Signal<int>(1);
        ReactiveGraph.OwningThread = Thread.CurrentThread;
        try {
            Assert.Equal(1, signal.Value);

            Exception? fromOtherThread = null;
            var thread = new Thread(() => {
                    try {
                        _ = signal.Value;
                    } catch (Exception exception) {
                        fromOtherThread = exception;
                    }
                }
            );

            thread.Start();
            thread.Join();

            Assert.IsType<InvalidOperationException>(fromOtherThread);
        } finally {
            ReactiveGraph.OwningThread = null;
        }
    }
}
