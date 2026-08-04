// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;

namespace Vixen.Live.Realms;

/// <summary>The only place a realm ever calls the control plane. ADR-016's rule, made a type.</summary>
/// <remarks>
///     <para>
///         <b>Orleans is asked, not awaited.</b> A grain call is a network round trip with a
///         scheduler in front of it: a frame that awaits one has a p99 measured in milliseconds and a
///         p99.9 measured in seconds. So a realm posts a request, keeps simulating, and applies the
///         answer at a defined point in a later frame — which is this class. One inbox, one outbox,
///         drained once per update.
///     </para>
///     <para>
///         This is not a new pattern in this repository. <c>ISessionAuthenticator</c> is already
///         shaped exactly this way — answering <c>Pending</c> and being asked again next update — and
///         doc 16 records why: "a completion on a thread-pool thread would make every layer it
///         touches thread-safe for the sake of an event that happens twice a minute". This is that
///         pattern with a bigger surface.
///     </para>
///     <para>
///         ⚠ <b>The <c>apply</c> callback runs on the realm's thread, inside
///         <see cref="Drain" />.</b> That is the entire value of the type: everything it touches — the
///         world, the session, the admission list — is single-threaded, and stays that way. The
///         <c>call</c> delegate runs wherever the task ran, and must touch none of them.
///     </para>
///     <para>
///         ⚠ <b>Nothing here knows what a grain is.</b> L0 has no orchestrator and this class is
///         still the right shape, because what it enforces is the <em>threading</em> discipline
///         rather than the transport. L1 hands the call an <c>IGrainFactory</c>; the drain does not
///         change.
///     </para>
/// </remarks>
public sealed class RealmDirectory : IDisposable {
    readonly ConcurrentQueue<(bool Faulted, Action Apply)> answers = new();
    readonly CancellationTokenSource cancellation = new();

    int pending;
    long answered;
    long faulted;
    bool disposed;

    /// <summary>How many questions are outstanding.</summary>
    /// <remarks>
    ///     Worth a metric rather than only a field: a number that climbs and does not come down is
    ///     what a control plane that has stopped answering looks like from inside a realm, and the
    ///     realm is otherwise perfectly happy — it is still simulating, because that is the point.
    /// </remarks>
    public int Pending => Volatile.Read(ref pending);

    /// <summary>How many answers have been applied.</summary>
    public long AnsweredCount => Interlocked.Read(ref answered);

    /// <summary>How many questions came back as an exception.</summary>
    public long FaultedCount => Interlocked.Read(ref faulted);

    /// <summary>Asks something, and says what to do with the answer.</summary>
    /// <typeparam name="TAnswer">What comes back.</typeparam>
    /// <param name="call">
    ///     The call. Runs off the realm's thread and must touch nothing the realm owns.
    /// </param>
    /// <param name="apply">
    ///     What to do with the answer. Runs on the realm's thread, inside <see cref="Drain" />.
    /// </param>
    /// <param name="onFault">
    ///     What to do when the call throws. Runs on the realm's thread too. Null means the fault is
    ///     counted and otherwise ignored, which is right for anything the realm can survive not
    ///     knowing.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="call" /> or <paramref name="apply" /> is null.</exception>
    public void Ask<TAnswer>(
        Func<CancellationToken, Task<TAnswer>> call,
        Action<TAnswer> apply,
        Action<Exception>? onFault = null
    ) {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(apply);

        if (disposed) {
            return;
        }

        Interlocked.Increment(ref pending);

        _ = RunAsync(call, apply, onFault);
    }

    /// <summary>Applies every answer that has arrived since the last time.</summary>
    /// <returns>How many were applied.</returns>
    /// <remarks>
    ///     Called once per update, before anything else — the realm's <c>PreUpdate</c>. A callback
    ///     that throws is not allowed to take the frame with it: it is counted as a fault and the
    ///     rest of the queue is still drained, because losing one answer is survivable and losing the
    ///     tick is not.
    /// </remarks>
    public int Drain() {
        var applied = 0;

        while (answers.TryDequeue(out var answer)) {
            applied++;

            try {
                answer.Apply();

                if (answer.Faulted) {
                    Interlocked.Increment(ref faulted);
                } else {
                    Interlocked.Increment(ref answered);
                }
            } catch (Exception failure) when (failure is not (OutOfMemoryException or StackOverflowException)) {
                Interlocked.Increment(ref faulted);
            }
        }

        return applied;
    }

    /// <summary>Stops asking, and abandons what is outstanding.</summary>
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        cancellation.Cancel();
        cancellation.Dispose();
        answers.Clear();
    }

    async Task RunAsync<TAnswer>(
        Func<CancellationToken, Task<TAnswer>> call,
        Action<TAnswer> apply,
        Action<Exception>? onFault
    ) {
        try {
            var answer = await call(cancellation.Token).ConfigureAwait(false);

            answers.Enqueue((false, () => apply(answer)));
        } catch (Exception failure) when (failure is not (OutOfMemoryException or StackOverflowException)) {
            answers.Enqueue((true, () => onFault?.Invoke(failure)));
        } finally {
            Interlocked.Decrement(ref pending);
        }
    }
}
