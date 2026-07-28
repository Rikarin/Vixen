// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using CsCheck;
using Vixen.Testing;
using Xunit;

namespace Vixen.Ui.Reactive.Tests;

/// <summary>
///     The properties of the graph itself rather than of any one node: what edges exist, what the
///     steady state costs, and whether the incremental answer is the answer.
/// </summary>
public class GraphTests {
    [Fact]
    public void A_computed_nobody_watches_registers_no_edge_back_from_its_dependencies() {
        // Liveness. Without it every computed ever created is retained forever by whatever signal it
        // read once, which is the classic way a reactive graph turns into a memory leak.
        var source = new Signal<int>(1);
        var derived = new Computed<int>(() => source.Value * 2);

        _ = derived.Value;

        Assert.Equal(1, derived.DependencyCount);
        Assert.Equal(0, source.LiveConsumerCount);
    }

    [Fact]
    public void Watching_the_far_end_of_a_chain_makes_the_whole_chain_live() {
        var scheduler = new EffectScheduler();
        var source = new Signal<int>(1);
        var middle = new Computed<int>(() => source.Value * 2);
        var top = new Computed<int>(() => middle.Value + 1);

        _ = top.Value;

        Assert.Equal(0, source.LiveConsumerCount);

        using var effect = new Effect(() => _ = top.Value, scheduler);
        scheduler.Flush();

        Assert.Equal(1, source.LiveConsumerCount);
        Assert.Equal(1, middle.LiveConsumerCount);
        Assert.Equal(1, top.LiveConsumerCount);
    }

    [Fact]
    public void Disposing_the_watcher_takes_the_liveness_back_out_of_the_whole_chain() {
        var scheduler = new EffectScheduler();
        var source = new Signal<int>(1);
        var middle = new Computed<int>(() => source.Value * 2);
        var top = new Computed<int>(() => middle.Value + 1);

        var effect = new Effect(() => _ = top.Value, scheduler);
        scheduler.Flush();
        effect.Dispose();

        Assert.Equal(0, top.LiveConsumerCount);
        Assert.Equal(0, middle.LiveConsumerCount);
        Assert.Equal(0, source.LiveConsumerCount);
    }

    [Fact]
    public void Two_watchers_of_one_computed_leave_it_live_until_both_are_gone() {
        var scheduler = new EffectScheduler();
        var source = new Signal<int>(1);
        var shared = new Computed<int>(() => source.Value * 2);

        var first = new Effect(() => _ = shared.Value, scheduler);
        var second = new Effect(() => _ = shared.Value, scheduler);
        scheduler.Flush();

        Assert.Equal(2, shared.LiveConsumerCount);

        first.Dispose();

        Assert.Equal(1, shared.LiveConsumerCount);
        Assert.Equal(1, source.LiveConsumerCount);

        second.Dispose();

        Assert.Equal(0, shared.LiveConsumerCount);
        Assert.Equal(0, source.LiveConsumerCount);
    }

    [Fact]
    public void Removing_the_middle_of_a_live_consumer_list_keeps_every_other_edge_addressable() {
        // The swap-remove fixes up the twin index of whichever edge it moved. Getting that wrong
        // corrupts an unrelated subscription and shows up nowhere near the cause, so it is worth a
        // test that removes from every position.
        var scheduler = new EffectScheduler();
        var source = new Signal<int>(0);
        var seen = new int[5];
        var effects = new Effect[5];
        for (var i = 0; i < effects.Length; i++) {
            var index = i;
            effects[i] = new Effect(() => seen[index] = source.Value, scheduler);
        }

        scheduler.Flush();

        Assert.Equal(5, source.LiveConsumerCount);

        // Middle, then first, then last of what is left.
        effects[2].Dispose();
        effects[0].Dispose();
        effects[4].Dispose();

        Assert.Equal(2, source.LiveConsumerCount);

        source.Value = 42;
        scheduler.Flush();

        Assert.Equal(new[] { 0, 42, 0, 42, 0 }, seen);

        effects[1].Dispose();
        effects[3].Dispose();

        Assert.Equal(0, source.LiveConsumerCount);
    }

    [Fact]
    public void A_settled_graph_allocates_nothing_per_frame() {
        // Doc 09's gate. Everything a frame touches — the effect queue, the edge lists, the dirty
        // walk — is either pooled or already sized by the time the second frame starts.
        var scheduler = new EffectScheduler();
        var source = new Signal<int>(0);
        var doubled = new Computed<int>(() => source.Value * 2);
        var total = 0;

        using var effect = new Effect(() => total += doubled.Value, scheduler);

        var next = 0;

        Assert.Equal(0, Measured.Bytes(Frame, warmUp: 100, passes: 1_000));
        Assert.True(total > 0);

        return;

        void Frame() {
            source.Value = next++;
            scheduler.Flush();
        }
    }

    [Fact]
    public void A_computed_whose_dependencies_come_and_go_allocates_nothing_once_it_has_seen_them_all() {
        var scheduler = new EffectScheduler();
        var toggle = new Signal<bool>(true);
        var left = new Signal<int>(1);
        var right = new Signal<int>(2);
        var either = new Computed<int>(() => toggle.Value ? left.Value : right.Value);
        var seen = 0;

        using var effect = new Effect(() => seen = either.Value, scheduler);

        Assert.Equal(0, Measured.Bytes(Frame, warmUp: 50, passes: 500));
        Assert.True(seen is 1 or 2);

        return;

        void Frame() {
            toggle.Value = !toggle.Value;
            scheduler.Flush();
        }
    }

    [Fact]
    public void Edge_storage_is_handed_back_and_reused_rather_than_re_allocated() {
        // Building and tearing down the same shape over and over is what a virtualised list does as
        // rows scroll past. The measurement is a difference rather than a threshold: two cycles that
        // differ only in whether the effects subscribe to anything. Everything else — the Effect
        // objects, the delegates, the closure — is identical and cancels, so what is left is the
        // edge storage, and pooled edge storage leaves nothing.
        var source = new Signal<int>(1);
        var scheduler = new EffectScheduler();

        for (var i = 0; i < 200; i++) {
            Cycle(source, scheduler, subscribing: false);
            Cycle(source, scheduler, subscribing: true);
        }

        // Both readings are non-zero by construction, so unlike every other measurement here neither
        // survives a collection landing in it — which is exactly the case Measured re-measures.
        var withoutEdges = Measured.Bytes(Peeking, warmUp: 0, passes: 200);
        var withEdges = Measured.Bytes(Subscribing, warmUp: 0, passes: 200);

        Assert.Equal(withoutEdges, withEdges);

        return;

        void Peeking() => Cycle(source, scheduler, subscribing: false);

        void Subscribing() => Cycle(source, scheduler, subscribing: true);

        static void Cycle(Signal<int> source, EffectScheduler scheduler, bool subscribing) {
            // Peek reads the same value through the same closure and records no edge, which is what
            // makes the two cycles comparable.
            var first = subscribing
                ? new Effect(() => _ = source.Value, scheduler)
                : new Effect(() => _ = source.Peek(), scheduler);
            var second = subscribing
                ? new Effect(() => _ = source.Value, scheduler)
                : new Effect(() => _ = source.Peek(), scheduler);

            scheduler.Flush();
            first.Dispose();
            second.Dispose();
        }
    }

    [Fact]
    public void The_incremental_answer_is_the_answer_a_full_recomputation_would_give() {
        // The oracle. Random DAGs, random writes, and a brute-force evaluator that knows nothing
        // about versions, dirty bits or liveness — every value the graph serves has to match what
        // recomputing the whole thing from the leaves would produce.
        Gen.Select(
                Gen.Int[2, 5],
                Gen.Int[0, 30].Array[2, 16],
                Gen.Int[0, 50].Array[1, 12]
            )
            .Sample(shape => {
                    var (sourceCount, wiring, writes) = shape;
                    var derivedCount = wiring.Length / 2;

                    var sources = new Signal<int>[sourceCount];
                    var sourceValues = new int[sourceCount];
                    for (var i = 0; i < sourceCount; i++) {
                        sources[i] = new Signal<int>(i);
                        sourceValues[i] = i;
                    }

                    // Each derived node reads two earlier nodes, so the graph is a DAG by
                    // construction and every shape the generator produces is legal.
                    var derived = new Computed<int>[derivedCount];
                    var inputs = new (int Left, int Right)[derivedCount];
                    for (var i = 0; i < derivedCount; i++) {
                        var available = sourceCount + i;
                        var left = wiring[i * 2] % available;
                        var right = wiring[(i * 2) + 1] % available;
                        inputs[i] = (left, right);

                        var index = i;
                        derived[i] = new Computed<int>(() => Read(sources, derived, inputs[index].Left)
                            + Read(sources, derived, inputs[index].Right)
                        );
                    }

                    for (var step = 0; step < writes.Length; step++) {
                        var target = writes[step] % sourceCount;
                        var value = writes[step];
                        sources[target].Value = value;
                        sourceValues[target] = value;

                        for (var i = 0; i < derivedCount; i++) {
                            Assert.Equal(Expected(sourceValues, inputs, i), derived[i].Value);
                        }
                    }
                }
            );

        static int Read(Signal<int>[] sources, Computed<int>[] derived, int index) =>
            index < sources.Length ? sources[index].Value : derived[index - sources.Length].Value;

        static int Expected(int[] sources, (int Left, int Right)[] inputs, int index) {
            var left = inputs[index].Left;
            var right = inputs[index].Right;
            return Value(sources, inputs, left) + Value(sources, inputs, right);
        }

        static int Value(int[] sources, (int Left, int Right)[] inputs, int index) =>
            index < sources.Length ? sources[index] : Expected(sources, inputs, index - sources.Length);
    }
}
