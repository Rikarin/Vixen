// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Reactive.Tests;

/// <summary>
///     Effects, and the scheduler that decides when they happen. The point of the design is that a
///     write never runs one — it queues, and the frame decides. Nearly every test here is really a
///     test of that.
/// </summary>
public class EffectTests {
    [Fact]
    public void An_effect_does_not_run_until_the_frame_flushes() {
        var scheduler = new EffectScheduler();
        var runs = 0;

        using var effect = new Effect(() => runs++, scheduler);

        Assert.Equal(0, runs);
        Assert.Equal(1, scheduler.PendingCount);

        scheduler.Flush();

        Assert.Equal(1, runs);
    }

    [Fact]
    public void An_effect_re_runs_when_what_it_read_changes() {
        var scheduler = new EffectScheduler();
        var source = new Signal<int>(1);
        var seen = 0;

        using var effect = new Effect(() => seen = source.Value, scheduler);
        scheduler.Flush();

        Assert.Equal(1, seen);

        source.Value = 7;
        scheduler.Flush();

        Assert.Equal(7, seen);
    }

    [Fact]
    public void Several_writes_between_two_frames_cost_one_run() {
        var scheduler = new EffectScheduler();
        var source = new Signal<int>(0);
        var runs = 0;

        using var effect = new Effect(() => {
                runs++;
                _ = source.Value;
            },
            scheduler
        );

        scheduler.Flush();

        for (var i = 1; i <= 100; i++) {
            source.Value = i;
        }

        Assert.Equal(1, scheduler.PendingCount);

        scheduler.Flush();

        Assert.Equal(2, runs);
    }

    [Fact]
    public void An_effect_sees_a_change_through_a_chain_of_computeds() {
        var scheduler = new EffectScheduler();
        var source = new Signal<int>(1);
        var doubled = new Computed<int>(() => source.Value * 2);
        var labelled = new Computed<string>(() => $"={doubled.Value}");
        var seen = string.Empty;

        using var effect = new Effect(() => seen = labelled.Value, scheduler);
        scheduler.Flush();

        Assert.Equal("=2", seen);

        source.Value = 4;
        scheduler.Flush();

        Assert.Equal("=8", seen);
    }

    [Fact]
    public void An_intermediate_that_did_not_change_does_not_wake_the_effect() {
        var scheduler = new EffectScheduler();
        var source = new Signal<int>(0);
        var parity = new Computed<int>(() => source.Value % 2);
        var runs = 0;

        using var effect = new Effect(() => {
                runs++;
                _ = parity.Value;
            },
            scheduler
        );

        scheduler.Flush();

        Assert.Equal(1, runs);

        source.Value = 2;
        scheduler.Flush();

        // The effect was queued — a live consumer is told its dependency *may* have changed — but the
        // poll on the way in found the version unmoved, so the body did not run.
        Assert.Equal(1, runs);

        source.Value = 3;
        scheduler.Flush();

        Assert.Equal(2, runs);
    }

    [Fact]
    public void Disposing_an_effect_unhooks_it_from_everything_it_read() {
        var scheduler = new EffectScheduler();
        var source = new Signal<int>(1);
        var runs = 0;

        var effect = new Effect(() => {
                runs++;
                _ = source.Value;
            },
            scheduler
        );

        scheduler.Flush();

        Assert.Equal(1, source.LiveConsumerCount);

        effect.Dispose();

        Assert.Equal(0, source.LiveConsumerCount);
        Assert.Equal(0, effect.DependencyCount);

        source.Value = 2;
        scheduler.Flush();

        Assert.Equal(1, runs);
    }

    [Fact]
    public void A_disposed_effect_that_was_already_queued_does_not_run() {
        var scheduler = new EffectScheduler();
        var runs = 0;
        var effect = new Effect(() => runs++, scheduler);

        effect.Dispose();
        scheduler.Flush();

        Assert.Equal(0, runs);
    }

    [Fact]
    public void An_effect_that_re_triggers_itself_is_suspended_rather_than_hanging_the_frame() {
        var scheduler = new EffectScheduler { MaximumRunsPerEffect = 5 };
        var source = new Signal<int>(0);
        var runs = 0;

        using var effect = new Effect(() => {
                runs++;
                source.Value++;
            },
            scheduler
        );

        scheduler.Flush();

        Assert.Equal(5, runs);
        Assert.True(effect.IsSuspended);

        // And the rest of the UI keeps working.
        var otherRuns = 0;
        using var other = new Effect(() => otherRuns++, scheduler);
        scheduler.Flush();

        Assert.Equal(1, otherRuns);
        Assert.Equal(5, runs);
    }

    [Fact]
    public void A_suspended_effect_runs_again_once_it_is_resumed() {
        var scheduler = new EffectScheduler { MaximumRunsPerEffect = 3 };
        var source = new Signal<int>(0);
        var loop = true;
        var runs = 0;

        using var effect = new Effect(() => {
                runs++;
                if (loop) {
                    source.Value++;
                }
            },
            scheduler
        );

        scheduler.Flush();

        Assert.True(effect.IsSuspended);

        loop = false;
        effect.Resume();
        scheduler.Flush();

        Assert.False(effect.IsSuspended);
        Assert.Equal(4, runs);
    }

    [Fact]
    public void An_effect_that_runs_once_a_frame_forever_is_not_mistaken_for_a_runaway() {
        // The run count is per flush. An animation binding that changes every frame is correct and
        // must never trip the detector, however long the application runs.
        var scheduler = new EffectScheduler { MaximumRunsPerEffect = 4 };
        var frame = new Signal<int>(0);
        var runs = 0;

        using var effect = new Effect(() => {
                runs++;
                _ = frame.Value;
            },
            scheduler
        );

        for (var i = 0; i < 50; i++) {
            frame.Value = i;
            scheduler.Flush();
        }

        Assert.False(effect.IsSuspended);
        Assert.Equal(50, runs);
    }

    [Fact]
    public void An_effect_that_throws_is_suspended_and_the_flush_continues() {
        var scheduler = new EffectScheduler();
        var otherRuns = 0;

        using var thrower = new Effect(() => throw new InvalidOperationException("boom"), scheduler);
        using var other = new Effect(() => otherRuns++, scheduler);

        scheduler.Flush();

        Assert.True(thrower.IsSuspended);
        Assert.Equal(1, otherRuns);
    }

    [Fact]
    public void A_flush_inside_a_batch_happens_once_the_batch_closes() {
        var scheduler = new EffectScheduler();
        var first = new Signal<int>(0);
        var second = new Signal<int>(0);
        var observed = new List<(int First, int Second)>();

        using var effect = new Effect(() => observed.Add((first.Value, second.Value)), scheduler);
        scheduler.Flush();
        observed.Clear();

        ReactiveGraph.Batch(() => {
                first.Value = 1;
                scheduler.Flush();
                second.Value = 2;
                scheduler.Flush();
            }
        );

        // One run, and never with the first write applied and the second not.
        var only = Assert.Single(observed);
        Assert.Equal((1, 2), only);
    }

    [Fact]
    public void A_flush_from_inside_a_flush_is_absorbed_by_the_one_already_running() {
        var scheduler = new EffectScheduler();
        var runs = 0;

        using var effect = new Effect(() => {
                runs++;
                Assert.Equal(0, scheduler.Flush());
            },
            scheduler
        );

        scheduler.Flush();

        Assert.Equal(1, runs);
    }

    [Fact]
    public void The_per_flush_budget_defers_the_remainder_rather_than_dropping_it() {
        var scheduler = new EffectScheduler { MaximumRunsPerFlush = 3 };
        var runs = 0;
        var effects = new List<Effect>();
        for (var i = 0; i < 10; i++) {
            effects.Add(new Effect(() => runs++, scheduler));
        }

        try {
            Assert.Equal(3, scheduler.Flush());
            Assert.Equal(3, runs);
            Assert.Equal(7, scheduler.PendingCount);

            scheduler.MaximumRunsPerFlush = 100;
            scheduler.Flush();

            Assert.Equal(10, runs);
        } finally {
            foreach (var effect in effects) {
                effect.Dispose();
            }
        }
    }

    /// <summary>
    ///     ⚠ <b>Issue #365, and the detach is where it happened.</b> Every read and write in this
    ///     assembly asserts the owning thread and a detach did not, which is the more dangerous
    ///     omission rather than the less: <c>RemoveLiveConsumerAt</c> does
    ///     <c>--liveConsumerCount</c> unguarded, so two threads unhooking from one producer at once
    ///     drive the count negative and the next detach indexes <c>liveConsumers[-1]</c>. The
    ///     symptom was an <c>IndexOutOfRangeException</c> on a line that reads exactly like an
    ///     off-by-one, in a suite that passed when run alone.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The second assertion is the one that matters.</b> Throwing is worth little if the
    ///     edge came out on the way: the point of refusing is that the producer's list is exactly as
    ///     it was, so the mistake is reported rather than half-applied.
    /// </remarks>
    [Fact]
    public void An_effect_is_only_detachable_from_the_thread_that_owns_the_graph() {
        var scheduler = new EffectScheduler();
        var source = new Signal<int>(1);

        ReactiveGraph.OwningThread = Thread.CurrentThread;
        try {
            var effect = new Effect(() => _ = source.Value, scheduler);
            scheduler.Flush();

            Assert.Equal(1, source.LiveConsumerCount);

            Exception? fromOtherThread = null;
            var thread = new Thread(() => {
                    try {
                        effect.Dispose();
                    } catch (Exception exception) {
                        fromOtherThread = exception;
                    }
                }
            );

            thread.Start();
            thread.Join();

            Assert.IsType<InvalidOperationException>(fromOtherThread);
            Assert.False(effect.IsDisposed);
            Assert.Equal(1, source.LiveConsumerCount);

            // And the owning thread's detach still works, so what was refused was the thread and
            // not the call.
            effect.Dispose();

            Assert.Equal(0, source.LiveConsumerCount);
        } finally {
            ReactiveGraph.OwningThread = null;
        }
    }
}
