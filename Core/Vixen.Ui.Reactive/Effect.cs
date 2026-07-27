// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Vixen.Ui.Reactive;

/// <summary>Work that re-runs when anything it read changes.</summary>
/// <remarks>
///     <para>
///         The only place in the graph where something actually happens. A signal holds a value and a
///         computed derives one; an effect is what assigns to an element's property, and in the
///         generated UI code there is one per dynamic expression in the markup — which is the whole
///         of ADR-010's "no VDOM": setting a signal invalidates exactly the effects that read it, and
///         each assigns exactly the property it was written for. No tree walk, no diff.
///     </para>
///     <para>
///         An effect never runs on the write. It queues, and <see cref="EffectScheduler.Flush" />
///         runs it at the point in the frame the UI system chose. That includes the first run: a new
///         effect is queued, not executed, so constructing a subtree does not run half of it before
///         the rest exists.
///     </para>
/// </remarks>
public sealed class Effect : ReactiveNode, IDisposable {
    readonly Action action;
    readonly string? originFile;
    readonly int originLine;
    readonly EffectScheduler scheduler;
    uint lastFlushId;
    int runsInFlush;
    bool queued;
    bool hasRun;

    /// <summary>Creates an effect and queues its first run.</summary>
    /// <param name="action">The work. Whatever it reads becomes its dependency set.</param>
    /// <param name="scheduler">Where it queues. Defaults to <see cref="EffectScheduler.Default" />.</param>
    /// <param name="origin">
    ///     Filled in by the compiler; what a suspended-effect log line names so that the message
    ///     points at the effect rather than at this file.
    /// </param>
    /// <param name="line">Filled in by the compiler; the line half of <paramref name="origin" />.</param>
    public Effect(
        Action action,
        EffectScheduler? scheduler = null,
        [CallerFilePath] string? origin = null,
        [CallerLineNumber] int line = 0
    ) {
        ArgumentNullException.ThrowIfNull(action);
        ReactiveGraph.AssertOwningThread();

        this.action = action;
        this.scheduler = scheduler ?? EffectScheduler.Default;
        originFile = origin;
        originLine = line;

        Schedule();
    }

    /// <summary>Where this effect was declared, formatted only when something is about to log it.</summary>
    /// <remarks>
    ///     Built on demand rather than in the constructor. A component tree stands up one effect per
    ///     dynamic expression, and formatting a string for each of them would be a per-element
    ///     allocation paid for a message almost none of them will ever produce.
    /// </remarks>
    string Origin => originFile is null
        ? "an unknown location"
        : $"{Path.GetFileName(originFile)}:{originLine.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Whether this effect has been suspended after misbehaving.</summary>
    /// <remarks>
    ///     Set by the runaway detector or by an unhandled exception. A suspended effect keeps its
    ///     dependencies and its place in the graph and simply does not run, so resuming it is one
    ///     call rather than a rebuild.
    /// </remarks>
    public bool IsSuspended { get; private set; }

    /// <summary>Whether <see cref="Dispose" /> has been called.</summary>
    public bool IsDisposed { get; private set; }

    internal override bool TracksDependencies => true;

    /// <summary>
    ///     An effect is live whether or not anything watches it — it <i>is</i> what the graph exists
    ///     to reach, so its dependencies push to it rather than waiting to be polled.
    /// </summary>
    internal override bool IsAlwaysLive => true;

    /// <summary>Unhooks the effect from everything it reads. It will not run again.</summary>
    /// <remarks>
    ///     This is what <c>ctx.If</c> calls when a branch leaves the tree, and it has to be complete:
    ///     an effect that keeps one edge alive keeps its whole closure, and therefore its part of the
    ///     element tree, alive with it.
    /// </remarks>
    public void Dispose() {
        if (IsDisposed) {
            return;
        }

        IsDisposed = true;
        DetachFromGraph();
    }

    /// <summary>Runs the effect again at the next flush, whether or not anything changed.</summary>
    /// <remarks>
    ///     The forced run is expressed by forgetting that the effect ever ran, because that is
    ///     precisely what has to be defeated: the poll on the way in would otherwise find every
    ///     dependency unmoved and skip the body.
    /// </remarks>
    public void Invalidate() {
        Dirty = true;
        hasRun = false;
        Schedule();
    }

    /// <summary>Lets a suspended effect run again.</summary>
    /// <remarks>
    ///     Deliberately manual. Automatic resumption would mean the runaway detector fires every
    ///     frame, logs every frame, and the application is still hung — just noisily.
    /// </remarks>
    public void Resume() {
        if (!IsSuspended) {
            return;
        }

        IsSuspended = false;
        runsInFlush = 0;
        Schedule();
    }

    internal override void OnDependencyMayHaveChanged() => Schedule();

    /// <summary>Runs the effect if it still should be. Called only by the scheduler.</summary>
    /// <param name="owner">The scheduler draining the queue, for its limits.</param>
    /// <param name="flushId">Which flush this is, so that run counts are per flush.</param>
    /// <param name="logger">Where misbehaviour is reported.</param>
    /// <returns>Whether the action actually ran.</returns>
    internal bool RunFromScheduler(EffectScheduler owner, uint flushId, ILogger logger) {
        queued = false;

        if (IsDisposed || IsSuspended) {
            return false;
        }

        Dirty = false;

        // Being woken means a dependency *may* have changed. Polling is what decides whether one
        // did, and it is how the equality short-circuit reaches all the way out here: a computed
        // that re-ran and produced the same answer wakes this effect and does not run it.
        if (hasRun && !PollProducersForChange()) {
            return false;
        }

        if (lastFlushId != flushId) {
            lastFlushId = flushId;
            runsInFlush = 0;
        }

        if (runsInFlush >= owner.MaximumRunsPerEffect) {
            IsSuspended = true;
            ReactiveLog.EffectSuspended(logger, Origin, runsInFlush);
            return false;
        }

        runsInFlush++;
        hasRun = true;

        var previousConsumer = BeforeComputation();
        try {
            action();
        } catch (Exception exception) {
            // Left running and reported. A UI framework where one bad binding takes the whole
            // window down is one nobody can develop against — the same argument doc 09 makes for
            // hot reload, and the same answer.
            IsSuspended = true;
            ReactiveLog.EffectThrew(logger, Origin, exception);
        } finally {
            AfterComputation(previousConsumer);
        }

        return true;
    }

    void Schedule() {
        if (queued || IsDisposed || IsSuspended) {
            return;
        }

        queued = true;
        scheduler.Enqueue(this);
    }
}
