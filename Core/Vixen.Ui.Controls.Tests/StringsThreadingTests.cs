// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Reactive;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>The language is process-wide; the graph node that announces it is not.</summary>
/// <remarks>
///     <para>
///         ⚠ <b><a href="https://github.com/Rikarin/Vixen/issues/1413">#1413</a>, and the last
///         piece of ambient state two correctly single-threaded graphs still shared.</b>
///         <c>ReactiveGraph.OwningThread</c> is off by default precisely so that a test host — or an
///         editor with more than one graph — may run independent graphs on independent threads, and
///         #1030 made <c>ReactiveGraph.Epoch</c> safe for exactly that. <c>Strings</c> was one
///         <c>static readonly Signal&lt;StringCatalog&gt;</c>, and every <c>@expr</c> showing a word
///         is an effect over it — so every test class in a UI test assembly, each on its own xunit
///         thread, was adding and removing live consumers on one node's arrays at once, and
///         <c>RemoveLiveConsumerAt</c> writes the moved twin index into the <em>consumer's</em>
///         producer array, which belongs to another thread's graph. A layer-stack panel alone adds
///         two consumers to it.
///     </para>
///     <para>
///         Measured before the fix, with eight threads each creating and disposing an effect over
///         one shared signal twenty thousand times: 159 825 of the 160 000 iterations threw
///         <c>IndexOutOfRangeException</c> out of the edge arrays; the same loop over a signal per
///         thread threw none. A torn edge array in a private graph is how a live <c>Computed</c>
///         misses the push that should have dirtied it — which is the shape of #1413's
///         <c>Depth</c> reading 0 immediately after <c>IsDirty</c> read true.
///     </para>
/// </remarks>
[Collection(SharedCatalogue.Name)]
public class StringsThreadingTests {
    static readonly StringId Probe = new("tests.strings.threading.probe", "Probe");

    /// <summary>A language change made on one thread writes nothing into a graph on another.</summary>
    /// <remarks>
    ///     ⚠ <b>Deterministic, where the corruption it prevents is a race.</b> The race needs two
    ///     threads inside one node at once; the <em>write</em> that makes the race possible does not —
    ///     before the fix, <c>Use</c> on this thread marked an effect built on another thread dirty and
    ///     queued it on that thread's scheduler, which is a cross-graph write whatever the timing.
    /// </remarks>
    [Fact]
    public void A_language_change_on_one_thread_queues_nothing_on_another() {
        EffectScheduler? elsewhere = null;
        Effect? bound = null;
        string? seen = null;

        var other = new Thread(() => {
            elsewhere = new EffectScheduler();
            bound = new Effect(() => seen = Probe.Text, elsewhere);
            elsewhere.Flush();
        });

        other.Start();
        other.Join();

        Assert.Equal("Probe", seen);
        Assert.Equal(0, elsewhere!.PendingCount);

        try {
            Strings.Use(new StringCatalog("xx").Set(Probe.Id, "Sonde"));

            Assert.Equal(0, elsewhere.PendingCount);

            // ⚠ And the value is still the process's: a read on any thread says the new word. What
            // is per-thread is only the node that announces a change, not the language.
            Assert.Equal("Sonde", Probe.Text);

            string? read = null;
            var reader = new Thread(() => read = Probe.Text);

            reader.Start();
            reader.Join();

            Assert.Equal("Sonde", read);
        } finally {
            Strings.Use(null);
        }

        GC.KeepAlive(bound);
    }

    /// <summary>A language change still re-labels what the changing thread's graph shows.</summary>
    /// <remarks>
    ///     The half the per-thread node must not cost: <c>LocalisationTests</c> asserts it through a
    ///     real control; this asserts it at the node, so a <c>Use</c> that stopped notifying its own
    ///     thread fails here before it fails there. And twice to the same catalog runs nothing, the
    ///     comparer behaviour <see cref="Strings" /> documents.
    /// </remarks>
    [Fact]
    public void A_language_change_re_runs_this_threads_effects_once() {
        var scheduler = new EffectScheduler();
        var runs = 0;
        string? seen = null;

        using var bound = new Effect(
            () => {
                seen = Probe.Text;
                runs++;
            },
            scheduler
        );

        scheduler.Flush();

        Assert.Equal(1, runs);

        try {
            var czech = new StringCatalog("cs").Set(Probe.Id, "Sonda");

            Strings.Use(czech);
            scheduler.Flush();

            Assert.Equal("Sonda", seen);
            Assert.Equal(2, runs);

            Strings.Use(czech);
            scheduler.Flush();

            Assert.Equal(2, runs);
        } finally {
            Strings.Use(null);
        }

        scheduler.Flush();

        Assert.Equal("Probe", seen);
        Assert.Equal(3, runs);
    }

    /// <summary>
    ///     ⚠ A language changed on another thread and changed back on this one re-labels what this
    ///     thread drew in between.
    /// </summary>
    /// <remarks>
    ///     The one order a per-thread node could get wrong. This thread's node was never told about the
    ///     other thread's <c>Use</c>, so it still holds the source catalog while this thread's effect
    ///     reads — and shows — the other language; changing back to the source catalog here is then a
    ///     write the node's comparer calls equal, and without the explicit invalidation nothing
    ///     re-runs and the label keeps the language the process has just left.
    /// </remarks>
    [Fact]
    public void Changing_back_on_this_thread_re_labels_what_another_threads_change_drew() {
        var scheduler = new EffectScheduler();
        string? seen = null;

        try {
            var elsewhere = new Thread(() => Strings.Use(new StringCatalog("cs").Set(Probe.Id, "Sonda")));

            elsewhere.Start();
            elsewhere.Join();

            using var bound = new Effect(() => seen = Probe.Text, scheduler);

            scheduler.Flush();

            Assert.Equal("Sonda", seen);

            Strings.Use(null);
            scheduler.Flush();

            Assert.Equal("Probe", seen);
        } finally {
            Strings.Use(null);
        }
    }

    /// <summary>
    ///     And the damage itself: effects over the language, made and dropped on several threads at
    ///     once, leave every thread's graph intact.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A race, so red before the fix by overwhelming odds rather than by construction</b> —
    ///     the deterministic statement is the test above. Kept because it is the failure itself
    ///     rather than its precondition: each thread also keeps a command stack's shape (a revision
    ///     signal, a depth and a dirty flag, a live effect following the depth), and a push lost to a
    ///     torn edge array shows as a depth that disagrees with its own dirty flag.
    /// </remarks>
    [Fact]
    public void Effects_over_the_language_on_several_threads_leave_each_graph_intact() {
        const int Threads = 8;
        const int Iterations = 5_000;

        var failures = 0;
        string? first = null;
        List<Thread> threads = [];

        for (var t = 0; t < Threads; t++) {
            threads.Add(new Thread(() => {
                try {
                    var scheduler = new EffectScheduler();
                    var count = 0;
                    var revision = new Signal<int>(0);
                    var depth = new Computed<int>(() => {
                        _ = revision.Value;
                        return count;
                    });
                    var dirty = new Computed<bool>(() => {
                        _ = revision.Value;
                        return count != 0;
                    });
                    var watched = -1;

                    using var watch = new Effect(() => watched = depth.Value, scheduler);

                    for (var i = 1; i <= Iterations; i++) {
                        using var label = new Effect(() => _ = Probe.Text, scheduler);

                        scheduler.Flush();

                        count = i;
                        revision.Value = i;
                        scheduler.Flush();

                        if (depth.Value != i || !dirty.Value || watched != i) {
                            throw new InvalidOperationException(
                                $"depth {depth.Value}, dirty {dirty.Value}, watched {watched} after {i} edits"
                            );
                        }
                    }
                } catch (Exception failure) {
                    if (Interlocked.Increment(ref failures) == 1) {
                        first = failure.GetType().Name + ": " + failure.Message;
                    }
                }
            }));
        }

        foreach (var thread in threads) {
            thread.Start();
        }

        foreach (var thread in threads) {
            thread.Join();
        }

        Assert.True(failures == 0, $"{failures} of {Threads} threads' graphs broke; the first: {first}");
    }
}
