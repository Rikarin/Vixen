// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Reactive;

/// <summary>
///     A node in the signal graph — something that produces a value, consumes one, or both.
/// </summary>
/// <remarks>
///     <para>
///         The whole propagation model lives here, and it is the one from ADR-007: writing pushes
///         invalidation without evaluating anything, and reading pulls evaluation only as far as it
///         has to go. A <see cref="Signal{T}" /> is a producer only, an <see cref="Effect" /> is a
///         consumer only, and a <see cref="Computed{T}" /> is both — which is why the two halves are
///         on one type rather than two.
///     </para>
///     <para>
///         <b>Liveness is the subtle part.</b> A producer only pushes to consumers that are
///         <i>live</i>, meaning an effect, or a computed that some live consumer is (transitively)
///         watching. A computed nobody is watching registers no edge back from its dependencies at
///         all, so it neither costs anything on write nor keeps itself alive through them — it is
///         re-verified by polling on the next read instead. Without this, every computed ever created
///         would be retained forever by whatever signal it happened to read once, which is the
///         classic way a reactive graph turns into a memory leak.
///     </para>
/// </remarks>
public abstract class ReactiveNode {
    Edge[] producers = [];
    Edge[] liveConsumers = [];
    int producerCount;
    int liveConsumerCount;
    int nextProducerIndex;

    /// <summary>Bumped whenever this node's value actually changed.</summary>
    /// <remarks>
    ///     Only ever bumped on a real change: a computed that re-runs and produces an equal value
    ///     leaves this alone, and that is the whole of the glitch-free / equality short-circuit
    ///     property — a dependent comparing versions sees nothing moved and does not re-run.
    /// </remarks>
    internal uint Version;

    /// <summary>The <see cref="ReactiveGraph.Epoch" /> at which this node was last verified clean.</summary>
    internal uint LastCleanEpoch;

    /// <summary>Whether a producer has said it may have changed. Set by push, cleared by pull.</summary>
    internal bool Dirty;

    /// <summary>Whether this node tracks what it reads. False for a plain signal, which reads nothing.</summary>
    internal virtual bool TracksDependencies => false;

    /// <summary>Whether this node is watched whether or not anything watches it. True for effects.</summary>
    internal virtual bool IsAlwaysLive => false;

    /// <summary>Whether writing a signal from inside this node's computation is allowed.</summary>
    internal virtual bool AllowsSignalWrites => true;

    /// <summary>Whether a recomputation is required regardless of what the dependencies say.</summary>
    internal virtual bool MustRecompute => false;

    /// <summary>Whether anything is watching, directly or through a chain of live consumers.</summary>
    internal bool IsLive => IsAlwaysLive || liveConsumerCount > 0;

    /// <summary>How many producers this node read on its last completed run.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Zero on a node that tracks is a node nothing can ever wake, and that is a
    ///         silent failure rather than an idle one.</b> A <c>Computed</c> or an <c>Effect</c>
    ///         whose computation read no signal is finished, permanently, having produced one value
    ///         that will never be revised. Nothing throws and nothing is logged, because nothing
    ///         went wrong — the graph is simply not the shape its author thought they wrote.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Internal until <c>BuildContext.TwoWay</c> needed it, which is the whole reason
    ///         it is public now.</b> A <c>bind:</c> over a plain property has exactly this shape and
    ///         was indistinguishable from a working one; the question "did that expression subscribe
    ///         to anything" is the runtime's to answer and nothing outside this assembly could ask
    ///         it.
    ///     </para>
    ///     <para>
    ///         Always zero for a plain <see cref="Signal{T}" />, which reads nothing, and zero for a
    ///         node that has not run yet.
    ///     </para>
    /// </remarks>
    public int DependencyCount => producerCount;

    /// <summary>How many live consumers are registered on this node. For tests and diagnostics.</summary>
    internal int LiveConsumerCount => liveConsumerCount;

    /// <summary>Records that <paramref name="producer" /> was read by whoever is computing.</summary>
    /// <param name="producer">The node that was read.</param>
    /// <exception cref="InvalidOperationException">A signal was read while dependents were being notified.</exception>
    internal static void ProducerAccessed(ReactiveNode producer) {
        if (ReactiveGraph.InNotificationPhase) {
            throw new InvalidOperationException(
                "A signal was read while a write was notifying its dependents. That means a change "
                + "callback is reading the graph mid-propagation, where half of it has been marked dirty "
                + "and half has not, and what it reads is arbitrary. Move the read into an effect."
            );
        }

        ReactiveGraph.ActiveConsumer?.RecordDependency(producer);
    }

    /// <summary>Brings this node's <see cref="Version" /> up to date, recomputing only if it must.</summary>
    internal void UpdateValueVersion() {
        if (IsLive && !Dirty) {
            // Live nodes are told when something changes, so a clean one is genuinely clean.
            return;
        }

        if (!Dirty && LastCleanEpoch == ReactiveGraph.Epoch) {
            // Nothing anywhere has been written since this was last verified.
            return;
        }

        if (!MustRecompute && !PollProducersForChange()) {
            Dirty = false;
            LastCleanEpoch = ReactiveGraph.Epoch;
            return;
        }

        RecomputeValue();
        Dirty = false;
        LastCleanEpoch = ReactiveGraph.Epoch;
    }

    /// <summary>Recomputes the value. Only called when something actually moved.</summary>
    internal virtual void RecomputeValue() { }

    /// <summary>Called when a dependency may have changed. The push half of the model.</summary>
    internal virtual void OnDependencyMayHaveChanged() { }

    /// <summary>Tells every live consumer that this node's value moved.</summary>
    internal void NotifyConsumers() {
        var previous = ReactiveGraph.InNotificationPhase;
        ReactiveGraph.InNotificationPhase = true;
        try {
            // The count is re-read each turn: marking a consumer dirty can unhook it, and iterating
            // over a stale bound would walk a slot that has already been swapped out.
            for (var i = 0; i < liveConsumerCount; i++) {
                var consumer = liveConsumers[i].Node;
                if (consumer is { Dirty: false }) {
                    consumer.Dirty = true;
                    consumer.OnDependencyMayHaveChanged();
                }
            }
        } finally {
            ReactiveGraph.InNotificationPhase = previous;
        }
    }

    /// <summary>Makes this node the active consumer for the duration of a computation.</summary>
    /// <returns>The consumer that was active before, to be handed back to <see cref="AfterComputation" />.</returns>
    internal ReactiveNode? BeforeComputation() {
        nextProducerIndex = 0;
        var previous = ReactiveGraph.ActiveConsumer;
        ReactiveGraph.ActiveConsumer = this;
        return previous;
    }

    /// <summary>Restores the previous consumer and drops the dependencies that were not read again.</summary>
    /// <param name="previous">The value <see cref="BeforeComputation" /> returned.</param>
    internal void AfterComputation(ReactiveNode? previous) {
        ReactiveGraph.ActiveConsumer = previous;

        for (var i = nextProducerIndex; i < producerCount; i++) {
            ref var edge = ref producers[i];
            if (IsLive && edge.Node is not null) {
                edge.Node.RemoveLiveConsumerAt(edge.TwinIndex);
            }

            edge = default;
        }

        producerCount = nextProducerIndex;
    }

    /// <summary>Unhooks this node from the graph and gives its edge storage back.</summary>
    /// <remarks>
    ///     Both directions have to go: the dependencies stop pushing to this node, and anything still
    ///     watching this node is left holding an edge to something inert rather than to something
    ///     that will keep firing.
    /// </remarks>
    private protected void DetachFromGraph() {
        for (var i = 0; i < producerCount; i++) {
            ref var edge = ref producers[i];
            if (IsLive && edge.Node is not null) {
                edge.Node.RemoveLiveConsumerAt(edge.TwinIndex);
            }

            edge = default;
        }

        producerCount = 0;
        nextProducerIndex = 0;

        if (producers.Length > 0) {
            EdgePool.Return(producers);
            producers = [];
        }

        // Whatever is still watching is told to stop. Their producer slot is cleared rather than
        // removed, so their indices — and everyone else's twin indices — stay put.
        for (var i = 0; i < liveConsumerCount; i++) {
            var edge = liveConsumers[i];
            if (edge.Node is not null) {
                edge.Node.producers[edge.TwinIndex].Node = null;
            }

            liveConsumers[i] = default;
        }

        liveConsumerCount = 0;
        if (liveConsumers.Length > 0) {
            EdgePool.Return(liveConsumers);
            liveConsumers = [];
        }
    }

    /// <summary>Records an edge from this consumer to <paramref name="producer" />.</summary>
    /// <remarks>
    ///     Dependencies are recorded positionally, in read order, and overwritten in place on the
    ///     next run. A computation whose branches read different signals therefore costs no
    ///     bookkeeping at all in the common case where the shape did not change, and the general
    ///     case degrades to replacing one slot.
    /// </remarks>
    void RecordDependency(ReactiveNode producer) {
        var index = nextProducerIndex++;
        EnsureProducerCapacity(index + 1);

        ref var edge = ref producers[index];
        if (!ReferenceEquals(edge.Node, producer)) {
            if (index < producerCount && edge.Node is not null && IsLive) {
                edge.Node.RemoveLiveConsumerAt(edge.TwinIndex);
            }

            edge.Node = producer;
            edge.TwinIndex = IsLive ? producer.AddLiveConsumer(this, index) : 0;
        }

        edge.SeenVersion = producer.Version;

        if (index >= producerCount) {
            producerCount = index + 1;
        }
    }

    /// <summary>Whether any dependency's version has moved since this node last read it.</summary>
    private protected bool PollProducersForChange() {
        for (var i = 0; i < producerCount; i++) {
            var producer = producers[i].Node;
            if (producer is null) {
                continue;
            }

            var seen = producers[i].SeenVersion;
            if (seen != producer.Version) {
                return true;
            }

            // The producer may itself be a stale computed. Ask it to settle, then look again.
            producer.UpdateValueVersion();
            if (seen != producer.Version) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Registers a live consumer, making this node — and its dependencies — live.</summary>
    /// <param name="consumer">The node that is now watching.</param>
    /// <param name="twinIndex">The index of the mirror edge in <paramref name="consumer" />'s producer list.</param>
    /// <returns>The index of the edge that was added, for the consumer to record as its twin.</returns>
    int AddLiveConsumer(ReactiveNode consumer, int twinIndex) {
        if (liveConsumerCount == 0 && TracksDependencies) {
            // This node was being polled and is now being watched, so it has to start watching in
            // turn — otherwise a write three levels down would never reach the effect at the top.
            for (var i = 0; i < producerCount; i++) {
                ref var edge = ref producers[i];
                if (edge.Node is not null) {
                    edge.TwinIndex = edge.Node.AddLiveConsumer(this, i);
                }
            }
        }

        EnsureLiveConsumerCapacity(liveConsumerCount + 1);
        liveConsumers[liveConsumerCount] = new Edge { Node = consumer, TwinIndex = twinIndex };
        return liveConsumerCount++;
    }

    /// <summary>Removes the live-consumer edge at <paramref name="index" />, in O(1).</summary>
    /// <remarks>
    ///     ⚠ <b><c>--liveConsumerCount</c> is not an off-by-one and no guard belongs on it.</b> It
    ///     was read as one: issue #365 landed as an <c>IndexOutOfRangeException</c> on the line
    ///     below, in a suite that passed when run alone, and the obvious story was that a second
    ///     detach reached <c>liveConsumers[-1]</c>. It cannot: the only caller of
    ///     <see cref="DetachFromGraph" /> is <see cref="Effect.Dispose" />, which returns on
    ///     <c>IsDisposed</c>, and twenty thousand randomised single-threaded programs over this
    ///     graph never produced a negative count. Two threads did, in milliseconds. The decrement is
    ///     unguarded because this assembly is single-threaded by contract, and the answer to a
    ///     negative count is to find out who called it from the wrong thread — which is what
    ///     <see cref="ReactiveGraph.OwningThread" /> is for, and why <see cref="Effect.Dispose" />
    ///     asserts it. An interlocked decrement here would keep the count non-negative and leave the
    ///     two edge lists disagreeing about each other's twin indices, which is the same corruption
    ///     with the crash removed.
    /// </remarks>
    void RemoveLiveConsumerAt(int index) {
        var last = --liveConsumerCount;
        if (index < last) {
            var moved = liveConsumers[last];
            liveConsumers[index] = moved;

            // The edge that was swapped into this slot has a twin that still believes it lives at
            // the end of the list. Nothing else knows where it went.
            if (moved.Node is not null) {
                moved.Node.producers[moved.TwinIndex].TwinIndex = index;
            }
        }

        liveConsumers[last] = default;

        if (liveConsumerCount == 0 && TracksDependencies) {
            // Nothing is watching any more, so stop watching. This is what keeps a discarded
            // computed from being retained by the signals it read.
            for (var i = 0; i < producerCount; i++) {
                ref var edge = ref producers[i];
                if (edge.Node is not null) {
                    edge.Node.RemoveLiveConsumerAt(edge.TwinIndex);
                    edge.TwinIndex = 0;
                }
            }
        }
    }

    void EnsureProducerCapacity(int required) {
        if (producers.Length >= required) {
            return;
        }

        var grown = EdgePool.Rent(required);
        producers.AsSpan(0, producerCount).CopyTo(grown);
        if (producers.Length > 0) {
            EdgePool.Return(producers);
        }

        producers = grown;
    }

    void EnsureLiveConsumerCapacity(int required) {
        if (liveConsumers.Length >= required) {
            return;
        }

        var grown = EdgePool.Rent(required);
        liveConsumers.AsSpan(0, liveConsumerCount).CopyTo(grown);
        if (liveConsumers.Length > 0) {
            EdgePool.Return(liveConsumers);
        }

        liveConsumers = grown;
    }
}
