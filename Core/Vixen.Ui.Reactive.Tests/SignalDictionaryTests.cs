// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Testing;
using Xunit;

namespace Vixen.Ui.Reactive.Tests;

/// <summary>
///     The keyed collection. Most of these are about the two claims that made it worth adding a type
///     — an in-place write costs nothing, and a read inside a binding still subscribes — rather than
///     about what a dictionary contains, which <c>Dictionary&lt;K, V&gt;</c> is already tested for.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Sabotage-verified against the shape this replaces.</b> Making every write
///         <i>replace</i> the backing map instead of writing into it — which is what
///         <c>RemoteInspectorClient.counters</c>' <c>Signal&lt;ImmutableDictionary&lt;K, V&gt;&gt;</c>
///         did — leaves all eleven behavioural tests here green and fails exactly the two allocation
///         ones. That is the point, and it is why the row this closes sat on the ledger as a
///         convenience rather than a bug: the old shape was <i>correct</i>, and the only thing wrong
///         with it was what it cost.
///     </para>
///     <para>
///         ⚠ And the mirror of it. Recording no dependency — dropping <c>ProducerAccessed</c> from
///         the readers, which is how a hand-written map fails — leaves both allocation tests green
///         and fails nine, every test whose effect reads the map at all.
///     </para>
/// </remarks>
public class SignalDictionaryTests {
    /// <summary>
    ///     ⚠ <b>The test the type exists for, and the one the ledger's row was about.</b> Reporting a
    ///     counter that has moved is a hash lookup and a store; the map this replaces rebuilt a
    ///     balanced tree's spine to say the same thing, which the control half of this measures
    ///     rather than asserts from memory.
    /// </summary>
    [Fact]
    public void An_in_place_write_allocates_nothing() {
        var counters = new SignalDictionary<string, double>(StringComparer.Ordinal);
        var reading = 0.0;

        // Every pass writes a different value, so the equality short-circuit is not what is being
        // measured — this is the cost of a write that genuinely propagates.
        Measured.NothingAllocated(() => counters["fps"] = reading += 0.25);
        Assert.True(reading > 0);

        var immutable = new Signal<ImmutableDictionary<string, double>>(
            ImmutableDictionary.Create<string, double>(StringComparer.Ordinal)
        );
        var mirror = 0.0;

        Assert.True(
            Measured.Bytes(() => immutable.Value = immutable.Peek().SetItem("fps", mirror += 0.25)) > 0,
            "the shape this replaces is supposed to allocate; if it no longer does, this type has no reason to exist"
        );
    }

    /// <summary>
    ///     ⚠ <b>Removing and clearing allocate nothing either</b>, which is not free by construction:
    ///     a map that rebuilt itself to empty would be the immutable shape again, and
    ///     <c>RemoteInspectorClient.Reset</c> runs on every detach.
    /// </summary>
    [Fact]
    public void Removing_and_clearing_allocate_nothing() {
        var counters = new SignalDictionary<string, double>(StringComparer.Ordinal);

        Measured.NothingAllocated(
            () => {
                counters["fps"] = 60;
                counters.Remove("fps");
                counters["draws"] = 1;
                counters.Clear();
            }
        );
    }

    /// <summary>Reading one key inside a binding is what subscribes to it.</summary>
    [Fact]
    public void Reading_a_key_inside_a_binding_subscribes() {
        var scheduler = new EffectScheduler();
        var counters = new SignalDictionary<string, double>(StringComparer.Ordinal) { ["fps"] = 59.5 };
        var seen = 0.0;

        using var effect = new Effect(() => seen = counters["fps"], scheduler);
        scheduler.Flush();

        Assert.Equal(59.5, seen);

        counters["fps"] = 61.25;
        scheduler.Flush();

        Assert.Equal(61.25, seen);
    }

    /// <summary>
    ///     ⚠ <b>The other four read shapes, because a map has four ways of being asked and a
    ///     subscription that only covers the indexer is the silent half of the bug.</b>
    ///     <c>ContainsKey</c> is the one worth naming: "has the counter arrived yet" is a question
    ///     whose answer changes, and the write that changes it is the one that adds the key — so a
    ///     <c>ContainsKey</c> that did not subscribe would answer "no" for ever.
    /// </summary>
    [Theory]
    [InlineData("count")]
    [InlineData("contains")]
    [InlineData("try")]
    [InlineData("enumerate")]
    public void Every_read_shape_subscribes(string shape) {
        var scheduler = new EffectScheduler();
        var counters = new SignalDictionary<string, double>(StringComparer.Ordinal);
        var answer = double.NaN;

        using var effect = new Effect(() => answer = shape switch {
                "count" => counters.Count,
                "contains" => counters.ContainsKey("fps") ? 1 : 0,
                "try" => counters.TryGetValue("fps", out var reading) ? reading : -1,
                _ => counters.Sum(entry => entry.Value)
            },
            scheduler
        );

        scheduler.Flush();
        var before = answer;

        counters["fps"] = 59.5;
        scheduler.Flush();

        Assert.NotEqual(before, answer);
    }

    /// <summary>
    ///     ⚠ <b>The granularity, asserted rather than left to be discovered.</b> One node covers the
    ///     whole map, so a binding that read one key is woken by a write to a different one. That is
    ///     the coarse edge <see cref="CollectionSignal{T}" /> also takes: it over-approximates the
    ///     dependency, which costs a re-run and cannot cost a stale answer.
    /// </summary>
    [Fact]
    public void A_write_to_one_key_wakes_a_binding_that_read_another() {
        var scheduler = new EffectScheduler();
        var counters = new SignalDictionary<string, double>(StringComparer.Ordinal) { ["fps"] = 59.5 };
        var runs = 0;

        using var effect = new Effect(() => {
                runs++;
                _ = counters["fps"];
            },
            scheduler
        );

        scheduler.Flush();
        Assert.Equal(1, runs);

        counters["draws"] = 1204;
        scheduler.Flush();

        Assert.Equal(2, runs);
    }

    /// <summary>
    ///     ⚠ <b>Equality stops propagation, per key</b>, and this is the property a per-frame poll
    ///     depends on: a build that reports the same frame rate again must cost the panel nothing.
    /// </summary>
    [Fact]
    public void Writing_a_value_the_map_already_holds_wakes_nobody() {
        var scheduler = new EffectScheduler();
        var counters = new SignalDictionary<string, double>(StringComparer.Ordinal) { ["fps"] = 59.5 };
        var runs = 0;

        using var effect = new Effect(() => {
                runs++;
                _ = counters.Count;
            },
            scheduler
        );

        scheduler.Flush();

        counters["fps"] = 59.5;
        counters.Remove("draws");
        counters.Clear();
        counters.Clear();
        scheduler.Flush();

        // Three of those four changed nothing. The one that did is the first Clear.
        Assert.Equal(2, runs);
    }

    /// <summary>A write queues the effect; it does not run it. ADR-007, unchanged here.</summary>
    [Fact]
    public void A_write_queues_rather_than_runs() {
        var scheduler = new EffectScheduler();
        var counters = new SignalDictionary<string, double>(StringComparer.Ordinal);
        var runs = 0;

        using var effect = new Effect(() => {
                runs++;
                _ = counters.Count;
            },
            scheduler
        );

        scheduler.Flush();
        Assert.Equal(1, runs);

        counters["fps"] = 59.5;

        // The write is done. Nothing has run, and the scheduler is holding the effect.
        Assert.Equal(1, runs);
        Assert.Equal(1, scheduler.PendingCount);

        scheduler.Flush();
        Assert.Equal(2, runs);
    }

    /// <summary>Peeking reads the current contents and takes no dependency on them.</summary>
    [Fact]
    public void Peeking_does_not_subscribe() {
        var scheduler = new EffectScheduler();
        var counters = new SignalDictionary<string, double>(StringComparer.Ordinal) { ["fps"] = 59.5 };
        var runs = 0;

        using var effect = new Effect(() => {
                runs++;
                _ = counters.TryPeek("fps", out _);
                _ = counters.Peek().Count;
            },
            scheduler
        );

        scheduler.Flush();
        counters["fps"] = 61.25;
        scheduler.Flush();

        Assert.Equal(1, runs);
    }

    /// <summary>
    ///     ⚠ <b>The thread check is the whole map's, not the value's</b> — the same runtime opt-in
    ///     <see cref="Signal{T}" /> and <see cref="CollectionSignal{T}" /> take, so a plug-in that
    ///     reports a counter from a worker thread throws where the mistake was made.
    /// </summary>
    [Fact]
    public void Touching_the_map_from_another_thread_throws() {
        var counters = new SignalDictionary<string, double>(StringComparer.Ordinal) { ["fps"] = 59.5 };

        ReactiveGraph.OwningThread = Thread.CurrentThread;
        try {
            // Caught on the worker rather than joined and rethrown: an exception nobody handles on a
            // background thread takes the test host down with it instead of failing a test.
            Exception? thrown = null;
            var worker = new Thread(() => {
                    try {
                        counters["fps"] = 61.25;
                    } catch (Exception exception) {
                        thrown = exception;
                    }
                }
            );

            worker.Start();
            worker.Join();

            Assert.IsType<InvalidOperationException>(thrown);
            Assert.Equal(59.5, counters["fps"]);
        } finally {
            ReactiveGraph.OwningThread = null;
        }
    }

    /// <summary>The key comparer is honoured, and the initial contents are copied in.</summary>
    [Fact]
    public void The_comparers_are_the_callers() {
        var counters = new SignalDictionary<string, double>(
            [new("FPS", 59.5)],
            StringComparer.OrdinalIgnoreCase
        );

        Assert.True(counters.ContainsKey("fps"));
        Assert.Single(counters.Keys);

        // A value comparer that reports nothing equal is the escape hatch for a mutable value, and
        // it turns the short-circuit off rather than working around it.
        var scheduler = new EffectScheduler();
        var always = new SignalDictionary<string, double>(StringComparer.Ordinal, SignalComparer.Never<double>()) {
            ["fps"] = 59.5
        };
        var runs = 0;

        using var effect = new Effect(() => {
                runs++;
                _ = always.Count;
            },
            scheduler
        );

        scheduler.Flush();
        always["fps"] = 59.5;
        scheduler.Flush();

        Assert.Equal(2, runs);
    }
}
