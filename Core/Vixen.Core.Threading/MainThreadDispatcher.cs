// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace Vixen.Core.Threading;

/// <summary>Work that has to happen on one particular thread, queued until that thread drains it.</summary>
/// <remarks>
///     <para>
///         Some calls are not thread-agnostic no matter how good the job system is. A GL context
///         belongs to the thread that made it current; the platform window APIs on macOS and Windows
///         insist on the thread that owns the message loop; .NET Hot Reload applies its deltas from
///         one place. This is where that work goes, and <see cref="Drain" /> at a defined frame point
///         is where it happens.
///     </para>
///     <para>
///         Draining at defined points rather than opportunistically is the reason this exists at all
///         instead of a lock. A queue that is drained between the simulation and the render is a
///         queue whose effects land in one predictable place, which is what makes a race between a
///         worker's request and the main thread's state impossible rather than unlikely.
///     </para>
/// </remarks>
public sealed class MainThreadDispatcher {
    readonly ConcurrentQueue<WorkItem> queue = new();

    /// <summary>The thread this dispatcher drains on.</summary>
    public int ThreadId { get; }

    /// <summary>Whether the calling thread is the one this dispatcher drains on.</summary>
    public bool IsMainThread => Environment.CurrentManagedThreadId == ThreadId;

    /// <summary>How many items are waiting. Racy; for overlays and assertions.</summary>
    public int PendingCount => queue.Count;

    /// <summary>Creates a dispatcher bound to the calling thread.</summary>
    public MainThreadDispatcher() : this(Environment.CurrentManagedThreadId) { }

    /// <summary>Creates a dispatcher bound to a chosen thread.</summary>
    /// <param name="threadId">The <see cref="Environment.CurrentManagedThreadId" /> to bind to.</param>
    public MainThreadDispatcher(int threadId) => ThreadId = threadId;

    /// <summary>Queues work for the main thread and returns immediately.</summary>
    /// <param name="action">What to run.</param>
    /// <exception cref="ArgumentNullException"><paramref name="action" /> is <see langword="null" />.</exception>
    /// <remarks>
    ///     Posting from the main thread queues rather than running inline. Running inline would mean
    ///     the same call has two different orderings depending on who made it, which is the kind of
    ///     difference that shows up once, in a build nobody can reproduce.
    /// </remarks>
    public void Post(Action action) {
        ArgumentNullException.ThrowIfNull(action);
        queue.Enqueue(new(static state => ((Action)state!)(), action, null));
    }

    /// <summary>Queues work for the main thread, with state, and returns immediately.</summary>
    /// <typeparam name="TState">The state type.</typeparam>
    /// <param name="action">What to run.</param>
    /// <param name="state">What to run it with.</param>
    /// <exception cref="ArgumentNullException"><paramref name="action" /> is <see langword="null" />.</exception>
    /// <remarks>
    ///     A value-typed <typeparamref name="TState" /> is boxed to reach the queue. That is one
    ///     small allocation per post, on a path that carries platform calls rather than frame work —
    ///     if something needs to post per frame per entity, it wants a job, not this.
    /// </remarks>
    public void Post<TState>(Action<TState> action, TState state) {
        ArgumentNullException.ThrowIfNull(action);
        queue.Enqueue(new(static boxed => ((Tuple<Action<TState>, TState>)boxed!).Item1(((Tuple<Action<TState>, TState>)boxed).Item2), Tuple.Create(action, state), null));
    }

    /// <summary>Queues work for the main thread and waits for it, rethrowing whatever it threw.</summary>
    /// <param name="action">What to run.</param>
    /// <exception cref="ArgumentNullException"><paramref name="action" /> is <see langword="null" />.</exception>
    /// <exception cref="InvalidOperationException">Called from the main thread, which would wait forever.</exception>
    /// <remarks>
    ///     Blocking, so it is for setup and teardown rather than for a frame. A worker that sends
    ///     rather than posts has stopped being a worker until the next drain.
    /// </remarks>
    public void Send(Action action) {
        ArgumentNullException.ThrowIfNull(action);

        if (IsMainThread) {
            throw new InvalidOperationException(
                "Send was called on the main thread, which would wait for a drain that only this "
                + "thread can perform. Call the work directly, or Post it."
            );
        }

        using var completed = new ManualResetEventSlim(false);
        var item = new WorkItem(static state => ((Action)state!)(), action, completed);
        queue.Enqueue(item);
        completed.Wait();
        item.Failure?.Throw();
    }

    /// <summary>Runs everything queued so far. Main thread only.</summary>
    /// <returns>How many items ran.</returns>
    /// <exception cref="InvalidOperationException">Called from a thread other than the bound one.</exception>
    /// <remarks>
    ///     Drains a snapshot, not until empty: an item that posts another item would otherwise be
    ///     able to hold the drain open indefinitely, and a frame point that can run forever is not a
    ///     frame point. Anything posted during a drain runs on the next one.
    /// </remarks>
    public int Drain() {
        AssertMainThread();
        var budget = queue.Count;
        var ran = 0;

        while (ran < budget && queue.TryDequeue(out var item)) {
            item.Run();
            ran++;
        }

        return ran;
    }

    /// <summary>Throws unless the calling thread is the bound one.</summary>
    /// <exception cref="InvalidOperationException">The calling thread is not the bound one.</exception>
    public void AssertMainThread() {
        if (!IsMainThread) {
            throw new InvalidOperationException(
                $"This must run on the main thread (managed thread {ThreadId}), but ran on "
                + $"{Environment.CurrentManagedThreadId}."
            );
        }
    }

    sealed class WorkItem(Action<object?> action, object? state, ManualResetEventSlim? completed) {
        internal ExceptionDispatchInfo? Failure { get; private set; }

        internal void Run() {
            try {
                action(state);
            } catch (Exception exception) when (completed is not null) {
                // Only captured when somebody is waiting to receive it. A posted item has no such
                // somebody, so its exception propagates out of Drain to the frame loop, where an
                // unhandled exception belongs.
                Failure = ExceptionDispatchInfo.Capture(exception);
            } finally {
                completed?.Set();
            }
        }
    }
}
