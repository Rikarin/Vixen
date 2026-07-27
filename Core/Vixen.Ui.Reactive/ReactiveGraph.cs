// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Ui.Reactive;

/// <summary>
///     The ambient state the signal graph is threaded through: who is currently reading, how many
///     writes have happened, and which thread is allowed to touch any of it.
/// </summary>
/// <remarks>
///     <para>
///         Nothing here is a container of signals. The graph is the nodes and the edges between them;
///         this type holds only what has to be global, which is the currently-evaluating consumer
///         (so that reading a signal can record the edge without anyone passing a context around)
///         and a write counter used as a cheap "nothing has changed since" check.
///     </para>
///     <para>
///         Per ADR-007 the model is Angular's: writing pushes invalidation, reading pulls
///         evaluation. A write marks dependents dirty and evaluates nothing; a read of a
///         <see cref="Computed{T}" /> re-runs it only if a dependency's version actually moved.
///     </para>
/// </remarks>
public static class ReactiveGraph {
    [ThreadStatic] internal static ReactiveNode? ActiveConsumer;

    /// <summary>
    ///     True while a write is walking its dependents. Reading a signal in that window is a bug —
    ///     see <see cref="ReactiveNode.ProducerAccessed" />.
    /// </summary>
    [ThreadStatic] internal static bool InNotificationPhase;

    [ThreadStatic] static int batchDepth;
    [ThreadStatic] static List<EffectScheduler>? deferredFlushes;

    /// <summary>
    ///     Bumped by every write that changed a value. A consumer that was verified clean at some
    ///     epoch and finds the epoch unchanged is clean without checking anything.
    /// </summary>
    internal static uint Epoch = 1;

    /// <summary>
    ///     The thread allowed to read and write signals, or <see langword="null" /> for no check.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Set this once, at boot, to the thread that owns the UI. Every signal access from
    ///         anywhere else then throws where the mistake was made rather than corrupting an edge
    ///         list and failing somewhere unrelated three frames later.
    ///     </para>
    ///     <para>
    ///         Doc 09 calls for a debug-mode assertion. It is a runtime check instead, because the
    ///         cost is one comparison against a static field that is usually null, and because a
    ///         plug-in that touches the document model from a worker thread is exactly the bug you
    ///         want reported from a shipping editor rather than only from a developer's Debug build.
    ///         It is off until set so that a test host — or an editor with more than one independent
    ///         graph — is not forced into a single thread by a library default.
    ///     </para>
    /// </remarks>
    public static Thread? OwningThread { get; set; }

    /// <summary>Whether a <see cref="Batch(Action)" /> is currently open on this thread.</summary>
    public static bool InBatch => batchDepth > 0;

    /// <summary>Reads without subscribing: edges recorded inside <paramref name="read" /> are not.</summary>
    /// <typeparam name="T">The type read.</typeparam>
    /// <param name="read">The read to perform outside of any dependency tracking.</param>
    /// <returns>Whatever <paramref name="read" /> returned.</returns>
    public static T Untracked<T>(Func<T> read) {
        ArgumentNullException.ThrowIfNull(read);

        var previous = ActiveConsumer;
        ActiveConsumer = null;
        try {
            return read();
        } finally {
            ActiveConsumer = previous;
        }
    }

    /// <summary>Runs <paramref name="action" /> without recording any dependency it reads.</summary>
    /// <param name="action">The work to perform outside of any dependency tracking.</param>
    public static void Untracked(Action action) {
        ArgumentNullException.ThrowIfNull(action);

        var previous = ActiveConsumer;
        ActiveConsumer = null;
        try {
            action();
        } finally {
            ActiveConsumer = previous;
        }
    }

    /// <summary>
    ///     Runs <paramref name="action" /> as one unit of change: any effect flush asked for inside
    ///     it happens once, after it returns.
    /// </summary>
    /// <param name="action">The writes to group.</param>
    /// <remarks>
    ///     <para>
    ///         What this does <i>not</i> have to do is coalesce the writes themselves, and that is
    ///         worth stating because every other signal library's <c>batch</c> exists for exactly
    ///         that. Here, effects are queued and drained at a defined point in the frame
    ///         (<see cref="EffectScheduler.Flush" />) rather than run on write, and a computed is
    ///         lazy, so ten writes between two frames already cost one effect run and one
    ///         recomputation without anyone asking for it. What is left for a batch to do is order
    ///         an explicit flush after the group instead of in the middle of it, which is what code
    ///         that drives the graph outside the frame loop — the editor's undo stack, a test — needs.
    ///     </para>
    ///     <para>
    ///         Nested batches flush once, at the outermost exit.
    ///     </para>
    /// </remarks>
    public static void Batch(Action action) {
        ArgumentNullException.ThrowIfNull(action);

        batchDepth++;
        try {
            action();
        } finally {
            batchDepth--;
            if (batchDepth == 0 && deferredFlushes is { Count: > 0 } pending) {
                // Taken and emptied before anything runs: a flush may open a batch of its own, and
                // it must not inherit this one's backlog.
                var schedulers = pending.ToArray();
                pending.Clear();
                foreach (var scheduler in schedulers) {
                    scheduler.Flush();
                }
            }
        }
    }

    /// <summary>Records that a flush was asked for inside a batch, to happen when it closes.</summary>
    /// <param name="scheduler">The scheduler whose flush was deferred.</param>
    /// <returns>Whether the flush was deferred rather than allowed to run now.</returns>
    internal static bool TryDeferFlush(EffectScheduler scheduler) {
        if (batchDepth == 0) {
            return false;
        }

        deferredFlushes ??= [];
        if (!deferredFlushes.Contains(scheduler)) {
            deferredFlushes.Add(scheduler);
        }

        return true;
    }

    internal static void IncrementEpoch() => Epoch++;

    /// <summary>Throws if the caller is not the thread that owns the graph.</summary>
    /// <exception cref="InvalidOperationException">Another thread touched a signal.</exception>
    internal static void AssertOwningThread() {
        var owner = OwningThread;
        if (owner is not null && !ReferenceEquals(owner, Thread.CurrentThread)) {
            Throw(owner);
        }

        static void Throw(Thread owner) =>
            throw new InvalidOperationException(
                $"Signals belong to thread '{owner.Name ?? owner.ManagedThreadId.ToString(CultureInfo.InvariantCulture)}' "
                + $"and were touched from thread {Environment.CurrentManagedThreadId}. Use AsyncComputed for work that "
                + "has to happen off the owning thread."
            );
    }
}
