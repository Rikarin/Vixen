// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Vixen.Ui.Reactive;

/// <summary>The queue of effects waiting to run, drained at a defined point in the frame.</summary>
/// <remarks>
///     <para>
///         This is the difference ADR-007 gives for not depending on SignalsDotnet, and it is worth
///         restating: a game engine's UI has to flush effects at a <i>precise</i> point in the frame
///         — after input, before layout, never mid-render — on a known thread, with a hard budget.
///         An effect that ran the moment a signal was written would mutate the element tree while the
///         renderer was walking it.
///     </para>
///     <para>
///         So writing a signal only ever queues. <see cref="Flush" /> is what runs anything, and the
///         UI system calls it once per frame in one place.
///     </para>
/// </remarks>
public sealed class EffectScheduler {
    [ThreadStatic] static EffectScheduler? threadDefault;

    readonly ILogger logger;
    readonly ConcurrentQueue<Action> posted = new();
    readonly Queue<Effect> queue = new();
    uint flushId;

    /// <summary>Creates a scheduler.</summary>
    /// <param name="logger">Where a suspended or throwing effect is reported. Optional.</param>
    public EffectScheduler(ILogger? logger = null) => this.logger = logger ?? NullLogger.Instance;

    /// <summary>The scheduler an effect uses when it is not given one.</summary>
    /// <remarks>
    ///     Per-thread, so that a test host running collections in parallel — or an editor with a
    ///     background document graph — does not have two unrelated UIs draining each other's queues.
    ///     Code that owns a frame loop should hold its own scheduler and pass it explicitly; this
    ///     exists so that the common case needs no ceremony.
    /// </remarks>
    public static EffectScheduler Default => threadDefault ??= new EffectScheduler();

    /// <summary>How many times one effect may run in a single flush before it is suspended.</summary>
    /// <remarks>
    ///     An effect that re-dirties itself is a bug — usually a write to something it also reads.
    ///     Left alone it hangs the application inside the flush, with no frame drawn and nothing in
    ///     the log, which is the worst possible way to find out. Suspending it costs one broken
    ///     binding and keeps everything else running. The count is per flush and not per lifetime:
    ///     an effect that runs once a frame forever is correct.
    /// </remarks>
    public int MaximumRunsPerEffect { get; set; } = 16;

    /// <summary>How many effect runs one flush may perform before deferring the rest to the next.</summary>
    /// <remarks>
    ///     The per-frame budget doc 09 asks for. Distinct from <see cref="MaximumRunsPerEffect" />:
    ///     this one is about a legitimately enormous amount of pending work rather than a single
    ///     effect misbehaving, and so it defers rather than suspends.
    /// </remarks>
    public int MaximumRunsPerFlush { get; set; } = 100_000;

    /// <summary>How many effects are waiting to run.</summary>
    public int PendingCount => queue.Count;

    /// <summary>Whether a flush is running right now.</summary>
    public bool IsFlushing { get; private set; }

    /// <summary>Runs every queued effect, and anything they queue in turn.</summary>
    /// <returns>How many effect runs happened.</returns>
    /// <remarks>
    ///     Inside a <see cref="ReactiveGraph.Batch(Action)" /> this queues the flush for the end of
    ///     the batch instead and returns zero, so that a group of writes is never observed half-done.
    ///     Re-entering a flush from inside one does nothing, for the same reason: the outer drain
    ///     picks the work up.
    /// </remarks>
    public int Flush() {
        ReactiveGraph.AssertOwningThread();

        if (IsFlushing || ReactiveGraph.TryDeferFlush(this)) {
            return 0;
        }

        IsFlushing = true;
        flushId++;
        try {
            // Work that arrived from another thread is applied first, so that anything it writes is
            // seen by the effects about to run rather than a frame later.
            while (posted.TryDequeue(out var work)) {
                work();
            }

            var runs = 0;
            while (queue.Count > 0) {
                if (runs == MaximumRunsPerFlush) {
                    ReactiveLog.FlushBudgetExhausted(logger, MaximumRunsPerFlush);
                    break;
                }

                if (queue.Dequeue().RunFromScheduler(this, flushId, logger)) {
                    runs++;
                }
            }

            return runs;
        } finally {
            IsFlushing = false;
        }
    }

    /// <summary>Runs <paramref name="work" /> on the owning thread at the start of the next flush.</summary>
    /// <param name="work">What to run. Called once, on the thread that owns the graph.</param>
    /// <remarks>
    ///     The only member of this type that may be called from another thread, and the whole of how
    ///     off-thread results get into the graph — <see cref="AsyncComputed{TRequest,T}" /> is built
    ///     on it. Everything else in the reactive layer stays single-threaded, which is what lets the
    ///     edge lists be plain arrays with no interlocked anything.
    /// </remarks>
    public void Post(Action work) {
        ArgumentNullException.ThrowIfNull(work);
        posted.Enqueue(work);
    }

    internal void Enqueue(Effect effect) => queue.Enqueue(effect);
}
