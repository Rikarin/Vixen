// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Vixen.Core;

namespace Vixen.Engine.Coroutines;

/// <summary>
///     Where suspended coroutines wait, and the thing that decides when they carry on.
/// </summary>
/// <remarks>
///     <para>
///         <b>It is a queue per resume point, drained on the loop thread, and nothing else.</b> There
///         is no timer, no thread pool and no synchronisation context anywhere in a coroutine's life:
///         a continuation goes into a list, a drain at a known frame point walks that list in the
///         order things were added, and whatever is ready runs. That is what makes a coroutine as
///         deterministic as a system — the same sequence of frame deltas produces the same sequence
///         of resumptions, which the determinism exit criterion of this phase requires and which
///         nothing built on <see cref="Task" /> and the thread pool could promise.
///     </para>
///     <para>
///         <b>Nothing resumes in the frame it suspended in.</b> Every wait records the tick it was
///         made on and becomes eligible on the next one, so <c>await Seconds(0f)</c> costs a frame
///         and <c>while (true) await NextFrame();</c> is a loop rather than a hang. The tick is the
///         frame for three of the resume points and the fixed step for
///         <see cref="ResumePoint.FixedStep" />, which is why a frame owing three steps resumes a
///         step-waiting coroutine three times.
///     </para>
///     <para>
///         <b>Steady state allocates nothing.</b> The waiting entries are structs in a reused list;
///         the continuation delegate is the one the pooled state machine box caches; the state
///         machines themselves come from <see cref="PoolingAsyncValueTaskMethodBuilder" />'s pool;
///         and the bookkeeping object behind a running coroutine comes from a free list here. A
///         behaviour that starts a coroutine every frame for an hour allocates for the first few and
///         then stops. That is measured, not asserted — see <c>CoroutineAllocationTests</c>.
///     </para>
/// </remarks>
public sealed class CoroutineScheduler {
    const int PointCount = 4;

    readonly List<Entry>[] waiting = [[], [], [], []];
    readonly List<Entry> ready = [];
    readonly List<RunningCoroutine> slots = [];
    readonly Stack<int> free = new();

    // Only ResumePoint-crossing arrivals from another thread go through here — a coroutine coming
    // back from real async I/O. Everything else is added straight to the list, on the loop thread,
    // with no synchronisation at all, because that is the overwhelmingly common path.
    readonly ConcurrentQueue<Entry> arrivals = new();

    long unscaledTicks;
    int loopThread;
    bool draining;
    ExceptionDispatchInfo? fault;

    /// <summary>How many frames have begun.</summary>
    public long Frame { get; private set; }

    /// <summary>How many fixed steps have begun.</summary>
    public long Step { get; private set; }

    /// <summary>The clock as of the last <see cref="BeginFrame" />.</summary>
    public GameTime Time { get; private set; } = GameTime.Zero;

    /// <summary>
    ///     The resume point currently being drained, and <see cref="ResumePoint.Update" /> when none
    ///     is. This is what an <c>await NextFrame()</c> with no explicit point means.
    /// </summary>
    public ResumePoint CurrentPoint { get; private set; } = ResumePoint.Update;

    /// <summary>How many coroutines have been started and not yet finished.</summary>
    public int RunningCount => slots.Count - free.Count;

    /// <summary>
    ///     What to do with an exception that escapes a coroutine. <see langword="null" /> rethrows it
    ///     out of the drain that observed it.
    /// </summary>
    /// <remarks>
    ///     Rethrowing by default rather than logging, because a coroutine is fire-and-forget and an
    ///     unobserved failure in one would otherwise be a bug that never surfaces — the failure mode
    ///     that <c>async void</c> is notorious for and that <c>Forget()</c> exists to make
    ///     deliberate. A game that would rather keep running installs a handler here; the default is
    ///     the one that makes the mistake visible the first time it happens.
    /// </remarks>
    public Action<Exception>? UnhandledException { get; set; }

    /// <summary>Whether the continuation being resumed right now is being cancelled.</summary>
    /// <remarks>
    ///     A field on the scheduler rather than something threaded through the awaiter, because the
    ///     drain runs one continuation at a time on one thread and <c>GetResult</c> is the very first
    ///     thing the resumed state machine does. There is no window in which this could be read by
    ///     anyone else.
    /// </remarks>
    internal bool IsResumingCancelled { get; private set; }

    /// <summary>Starts a frame. Call once per frame, before anything drains.</summary>
    /// <param name="time">The frame's clock.</param>
    /// <remarks>
    ///     Separate from the drains so that a coroutine started in <c>Start()</c> — which runs before
    ///     any drain — still counts as having been started this frame, and its first
    ///     <c>await NextFrame()</c> therefore lands on the next one. Folding this into the first
    ///     drain would make "next frame" mean "later today" for everything launched from the
    ///     lifecycle queues.
    /// </remarks>
    public void BeginFrame(in GameTime time) {
        loopThread = Environment.CurrentManagedThreadId;
        Frame++;
        Time = time;
        unscaledTicks += time.UnscaledElapsed.Ticks;
    }

    /// <summary>Starts a fixed step. Call once per step, before <see cref="ResumePoint.FixedStep" /> drains.</summary>
    public void BeginStep() => Step++;

    /// <summary>Starts a coroutine and watches it to the end.</summary>
    /// <param name="coroutine">The coroutine, already running up to its first suspension.</param>
    /// <returns>A handle that reports whether it is still going.</returns>
    /// <remarks>
    ///     An <c>async</c> method has already begun by the time its <see cref="Coroutine" /> reaches
    ///     here — that is how C# works, and it is the behaviour users want: the code before the first
    ///     <c>await</c> runs at the call site, in order, like any other call. What this adds is the
    ///     watch: without it a failure would sit unobserved in a pooled state machine for ever.
    /// </remarks>
    public CoroutineHandle Run(Coroutine coroutine) {
        var running = Rent();
        var handle = new CoroutineHandle(this, running.Index, running.Version);

        running.Attach(coroutine.AsValueTask());

        // Not while draining: throwing here would unwind out of the middle of the drain loop and
        // strand the continuations it had already taken off the waiting list.
        if (!draining) {
            ThrowPendingFault();
        }

        return handle;
    }

    /// <summary>Resumes everything waiting on a resume point that is ready to go.</summary>
    /// <param name="point">The point.</param>
    /// <exception cref="InvalidOperationException">A drain is already in progress.</exception>
    /// <remarks>
    ///     <para>
    ///         Three passes, and the split matters. The first decides who is ready, in the order they
    ///         suspended. The second compacts the survivors, keeping that order. Only then does the
    ///         third run anything — so a coroutine that suspends again the instant it resumes lands
    ///         at the back of a list that is no longer being walked, and cannot be resumed twice in
    ///         one drain.
    ///     </para>
    ///     <para>
    ///         Order-preserving compaction rather than the swap-back this codebase uses elsewhere.
    ///         Swap-back is the right removal everywhere the order is meaningless; here it is the
    ///         whole contract.
    ///     </para>
    /// </remarks>
    public void Drain(ResumePoint point) {
        if (draining) {
            throw new InvalidOperationException("A coroutine drain is already running; it cannot be re-entered.");
        }

        loopThread = Environment.CurrentManagedThreadId;

        while (arrivals.TryDequeue(out var arrived)) {
            waiting[(int)arrived.Point].Add(arrived);
        }

        var list = waiting[(int)point];
        ready.Clear();

        var keep = 0;

        for (var index = 0; index < list.Count; index++) {
            if (IsReady(list[index])) {
                ready.Add(list[index]);
            } else {
                list[keep++] = list[index];
            }
        }

        list.RemoveRange(keep, list.Count - keep);

        if (ready.Count == 0) {
            ThrowPendingFault();
            return;
        }

        var previousPoint = CurrentPoint;
        draining = true;
        CurrentPoint = point;

        try {
            foreach (var entry in ready) {
                IsResumingCancelled = IsCancelled(entry);
                entry.Continuation();
            }
        } finally {
            IsResumingCancelled = false;
            CurrentPoint = previousPoint;
            draining = false;
            ready.Clear();
        }

        ThrowPendingFault();
    }

    /// <summary>How many coroutines are waiting on a resume point.</summary>
    /// <param name="point">The point.</param>
    /// <returns>How many.</returns>
    public int WaitingCount(ResumePoint point) => waiting[(int)point].Count;

    /// <summary>Waits for the next occurrence of a resume point.</summary>
    /// <param name="point">Where to come back, or <see langword="null" /> for wherever this is running.</param>
    /// <param name="owner">What may cancel it.</param>
    /// <returns>The wait.</returns>
    public CoroutineAwaitable NextFrame(ResumePoint? point = null, ICoroutineOwner? owner = null) =>
        new(this, owner, point ?? CurrentPoint, WaitKind.Tick, 0, null);

    /// <summary>Waits an amount of scaled game time. A paused game does not advance it.</summary>
    /// <param name="seconds">How long.</param>
    /// <param name="point">Where to come back, or <see langword="null" /> for wherever this is running.</param>
    /// <param name="owner">What may cancel it.</param>
    /// <returns>The wait.</returns>
    public CoroutineAwaitable Seconds(float seconds, ResumePoint? point = null, ICoroutineOwner? owner = null) =>
        new(this, owner, point ?? CurrentPoint, WaitKind.ScaledTime, Time.Total.Ticks + ToTicks(seconds), null);

    /// <summary>Waits an amount of unscaled time, which a pause does not stop.</summary>
    /// <param name="seconds">How long.</param>
    /// <param name="point">Where to come back, or <see langword="null" /> for wherever this is running.</param>
    /// <param name="owner">What may cancel it.</param>
    /// <returns>The wait.</returns>
    public CoroutineAwaitable UnscaledSeconds(
        float seconds,
        ResumePoint? point = null,
        ICoroutineOwner? owner = null
    ) =>
        new(this, owner, point ?? CurrentPoint, WaitKind.UnscaledTime, unscaledTicks + ToTicks(seconds), null);

    /// <summary>Waits until a predicate is true, testing it once per occurrence of the resume point.</summary>
    /// <param name="predicate">The test.</param>
    /// <param name="point">Where to come back, or <see langword="null" /> for wherever this is running.</param>
    /// <param name="owner">What may cancel it.</param>
    /// <returns>The wait.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="predicate" /> is <see langword="null" />.</exception>
    /// <remarks>
    ///     The one waiting form that allocates: a lambda that closes over anything is an object, once
    ///     per wait. Unavoidable, and cheap next to what it saves; a coroutine that must not allocate
    ///     at all writes the test out as a <c>while</c> around <c>await NextFrame()</c>.
    /// </remarks>
    public CoroutineAwaitable Until(Func<bool> predicate, ResumePoint? point = null, ICoroutineOwner? owner = null) {
        ArgumentNullException.ThrowIfNull(predicate);
        return new(this, owner, point ?? CurrentPoint, WaitKind.Until, 0, predicate);
    }

    /// <summary>Waits while a predicate is true.</summary>
    /// <param name="predicate">The test.</param>
    /// <param name="point">Where to come back, or <see langword="null" /> for wherever this is running.</param>
    /// <param name="owner">What may cancel it.</param>
    /// <returns>The wait.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="predicate" /> is <see langword="null" />.</exception>
    public CoroutineAwaitable While(Func<bool> predicate, ResumePoint? point = null, ICoroutineOwner? owner = null) {
        ArgumentNullException.ThrowIfNull(predicate);
        return new(this, owner, point ?? CurrentPoint, WaitKind.While, 0, predicate);
    }

    /// <summary>Comes back to the loop thread from wherever the coroutine currently is.</summary>
    /// <param name="point">Where to come back. Defaults to <see cref="ResumePoint.Update" />.</param>
    /// <param name="owner">What may cancel it.</param>
    /// <returns>The wait.</returns>
    /// <remarks>
    ///     <para>
    ///         The way back from real asynchrony. A coroutine that awaits a file read or an HTTP
    ///         response resumes on a thread pool thread, where it must not touch the world at all —
    ///         chunk iteration is running there, and nothing in the ECS is thread-safe against a
    ///         writer. <c>await ResumeOnLoop()</c> parks the continuation until the next drain of a
    ///         point, and everything after it is back on the loop thread.
    ///     </para>
    ///     <para>
    ///         The only wait that may be made from another thread, and the only one that does not
    ///         cost a frame — it resumes at the very next drain of the point, which may be this
    ///         frame's.
    ///     </para>
    /// </remarks>
    public CoroutineAwaitable ResumeOnLoop(ResumePoint point = ResumePoint.Update, ICoroutineOwner? owner = null) =>
        new(this, owner, point, WaitKind.Immediate, 0, null);

    internal void Suspend(
        Action continuation,
        ICoroutineOwner? owner,
        int generation,
        ResumePoint point,
        WaitKind kind,
        long resumeTime,
        Func<bool>? predicate
    ) {
        var entry = new Entry(
            continuation,
            owner,
            generation,
            point,
            kind,
            kind == WaitKind.Immediate ? long.MinValue : Tick(point) + 1,
            resumeTime,
            predicate
        );

        if (Environment.CurrentManagedThreadId == loopThread) {
            waiting[(int)point].Add(entry);
            return;
        }

        if (kind != WaitKind.Immediate) {
            throw new InvalidOperationException(
                $"A coroutine suspended on {kind} from thread {Environment.CurrentManagedThreadId}, which is not the "
                + $"loop thread ({loopThread}). Only ResumeOnLoop may be awaited off the loop thread — everything "
                + "else reads the clock and the frame counter, which are the loop thread's."
            );
        }

        arrivals.Enqueue(entry);
    }

    internal bool IsRunning(int index, int version) =>
        (uint)index < (uint)slots.Count && slots[index].Version == version;

    internal void ReportFault(Exception failure) {
        if (UnhandledException is { } handler) {
            handler(failure);
            return;
        }

        // Only the first is kept. A frame in which two coroutines threw has one bug worth looking at
        // and one that is probably a consequence of it, and an aggregate would bury the first.
        fault ??= ExceptionDispatchInfo.Capture(failure);
    }

    internal void Release(RunningCoroutine running) {
        running.Version++;
        free.Push(running.Index);
    }

    long Tick(ResumePoint point) => point == ResumePoint.FixedStep ? Step : Frame;

    static long ToTicks(float seconds) =>
        seconds <= 0f ? 0L : (long)(seconds * TimeSpan.TicksPerSecond);

    static bool IsCancelled(in Entry entry) =>
        entry.Owner is { } owner && (owner.IsDestroyed || owner.CoroutineGeneration != entry.Generation);

    bool IsReady(in Entry entry) {
        // Before anything else, and regardless of what it was waiting for: a coroutine whose owner is
        // gone must not sit on a ten second timer before its finally blocks run.
        if (IsCancelled(entry)) {
            return true;
        }

        if (entry.Kind == WaitKind.Immediate) {
            return true;
        }

        if (Tick(entry.Point) < entry.ResumeTick) {
            return false;
        }

        return entry.Kind switch {
            WaitKind.Tick => true,
            WaitKind.ScaledTime => Time.Total.Ticks >= entry.ResumeTime,
            WaitKind.UnscaledTime => unscaledTicks >= entry.ResumeTime,
            WaitKind.Until => entry.Predicate!(),
            WaitKind.While => !entry.Predicate!(),
            _ => true
        };
    }

    RunningCoroutine Rent() {
        if (free.Count > 0) {
            return slots[free.Pop()];
        }

        var created = new RunningCoroutine(this, slots.Count);
        slots.Add(created);
        return created;
    }

    void ThrowPendingFault() {
        if (fault is not { } pending) {
            return;
        }

        fault = null;
        pending.Throw();
    }

    /// <summary>One suspended coroutine, and what it is waiting for.</summary>
    /// <param name="Continuation">What to call to resume it.</param>
    /// <param name="Owner">What may cancel it, if anything.</param>
    /// <param name="Generation">The owner's stop counter when the wait was made.</param>
    /// <param name="Point">Where it comes back.</param>
    /// <param name="Kind">What kind of wait it is.</param>
    /// <param name="ResumeTick">The frame, or step, it becomes eligible on.</param>
    /// <param name="ResumeTime">The clock reading it becomes eligible at, in ticks.</param>
    /// <param name="Predicate">The test, for the predicate waits.</param>
    readonly record struct Entry(
        Action Continuation,
        ICoroutineOwner? Owner,
        int Generation,
        ResumePoint Point,
        WaitKind Kind,
        long ResumeTick,
        long ResumeTime,
        Func<bool>? Predicate
    );
}

/// <summary>The scheduler's bookkeeping for one started coroutine.</summary>
/// <remarks>
///     A class, pooled, with its completion callback allocated once in the constructor — which is the
///     only reason it is not a struct. <c>ValueTask</c> wants an <see cref="Action" /> to call when
///     it finishes, and a fresh lambda per coroutine would allocate on exactly the path this is
///     supposed to keep clean.
/// </remarks>
sealed class RunningCoroutine {
    readonly CoroutineScheduler scheduler;
    readonly Action completed;

    ConfiguredValueTaskAwaitable.ConfiguredValueTaskAwaiter awaiter;

    internal int Index { get; }

    internal int Version { get; set; } = 1;

    internal RunningCoroutine(CoroutineScheduler scheduler, int index) {
        this.scheduler = scheduler;
        Index = index;
        completed = Finish;
    }

    internal void Attach(ValueTask task) {
        // ConfigureAwait(false) is not decoration here: it is what stops the completion being posted
        // to a synchronisation context. There is none in a game, and there is one under the test
        // runner, and a coroutine that finished on the runner's context would finish on another
        // thread.
        awaiter = task.ConfigureAwait(false).GetAwaiter();

        if (awaiter.IsCompleted) {
            Finish();
        } else {
            awaiter.UnsafeOnCompleted(completed);
        }
    }

    void Finish() {
        try {
            awaiter.GetResult();
        } catch (OperationCanceledException) {
            // Cancellation is how a coroutine ends when its owner goes away, which is the ordinary
            // end of most coroutines. Not a failure, and not worth telling anyone about.
        } catch (Exception failure) {
            scheduler.ReportFault(failure);
        } finally {
            scheduler.Release(this);
        }
    }
}
