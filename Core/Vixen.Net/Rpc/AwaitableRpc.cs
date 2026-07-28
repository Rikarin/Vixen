// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Net.Messaging;
using Vixen.Net.Sessions;

namespace Vixen.Net.Rpc;

/// <summary>Writes a call's arguments, or a reply's.</summary>
/// <param name="writer">Where they go.</param>
public delegate void RpcArguments(ref BitWriter writer);

/// <summary>Reads what a reply carried.</summary>
/// <typeparam name="T">The answer's type.</typeparam>
/// <param name="reader">Where it comes from.</param>
/// <param name="value">The answer, if it decoded.</param>
/// <returns>Whether it did.</returns>
public delegate bool RpcResult<T>(ref BitReader reader, out T value);

/// <summary>Why an awaited call did not produce an answer.</summary>
public enum RpcFailure : byte {
    /// <summary>Nobody answered inside the timeout.</summary>
    /// <remarks>
    ///     The ordinary one, and the reason a timeout is not optional. A peer that never replies —
    ///     because it dropped the packet, because the handler forgot, because it is written by
    ///     somebody else — would otherwise leave the caller awaiting for the life of the process.
    /// </remarks>
    TimedOut = 0,

    /// <summary>The peer went away before answering.</summary>
    Disconnected = 1,

    /// <summary>The answer arrived and did not decode.</summary>
    Malformed = 2,

    /// <summary>The call was never sent — this peer is not the side that may send it.</summary>
    NotSent = 3
}

/// <summary>An awaited call that did not produce an answer.</summary>
/// <param name="Failure">Why.</param>
/// <param name="Method">Which call.</param>
public sealed class RpcFailedException(RpcFailure Failure, string Method)
    : Exception($"'{Method}' did not answer: {Failure}.") {
    /// <summary>Why it did not answer.</summary>
    public RpcFailure Failure { get; } = Failure;

    /// <summary>Which call.</summary>
    public string Method { get; } = Method;
}

/// <summary>The calls this peer is waiting for answers to.</summary>
/// <remarks>
///     <para>
///         <b>A correlation id, not a channel.</b> Several calls can be outstanding at once, replies
///         arrive in whatever order the answers were ready, and a reply has to find the exact
///         <c>await</c> that is waiting for it. An id in a table is how; matching on the method or
///         the object would join two calls that happened to be about the same thing.
///     </para>
///     <para>
///         <b>Ids are never reused while a call is outstanding, and zero is not an id.</b> Zero means
///         "no reply expected", so a fire-and-forget call and an awaited one are told apart without
///         a flag. The counter wraps, and the table is what actually decides — a wrapped id that
///         collides with a live one would be a reply delivered to the wrong await, so it is skipped
///         rather than assumed unlikely.
///     </para>
///     <para>
///         <b>Everything completes exactly once.</b> A reply, a timeout and a disconnect can all
///         reach the same call, and two of them completing it is an exception on whichever thread
///         the continuation happened to be on. Removal from the table is the claim: whoever takes
///         the entry out owns completing it.
///     </para>
/// </remarks>
sealed class PendingCalls {
    readonly Dictionary<uint, Pending> outstanding = [];
    readonly List<uint> expiring = [];

    uint next;

    /// <summary>How many calls are waiting for an answer.</summary>
    public int Count => outstanding.Count;

    /// <summary>Reserves an id and the completion behind it.</summary>
    /// <typeparam name="T">The answer's type.</typeparam>
    /// <param name="method">Which call, for the failure message.</param>
    /// <param name="peer">Who is being asked, so a disconnect can cancel it.</param>
    /// <param name="result">How to read the answer.</param>
    /// <param name="timeout">How long to wait.</param>
    /// <param name="task">The task the caller awaits.</param>
    /// <returns>The correlation id to put on the wire.</returns>
    public uint Add<T>(
        RpcMethod method,
        PlayerId peer,
        RpcResult<T> result,
        TimeSpan timeout,
        out Task<T> task
    ) {
        var id = Reserve();
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        outstanding[id] = new(
            method,
            peer,
            timeout.TotalSeconds,
            (ref BitReader reader) => {
                if (!result(ref reader, out var value)) {
                    return false;
                }

                completion.TrySetResult(value);

                return true;
            },
            failure => completion.TrySetException(new RpcFailedException(failure, method.ToString()))
        );

        task = completion.Task;

        return id;
    }

    /// <summary>Completes the call a reply is addressed to.</summary>
    /// <param name="id">The correlation id.</param>
    /// <param name="reader">The rest of the reply.</param>
    /// <returns>Whether there was such a call and it decoded.</returns>
    public bool Complete(uint id, ref BitReader reader) {
        // Removed first. Whoever holds the entry owns completing it, so a reply that arrives while a
        // timeout is being swept cannot both finish it.
        if (!outstanding.Remove(id, out var pending)) {
            return false;
        }

        if (pending.Read(ref reader)) {
            return true;
        }

        pending.Fail(RpcFailure.Malformed);

        return false;
    }

    /// <summary>Fails everything waiting on a peer that has gone.</summary>
    /// <param name="peer">Who went.</param>
    /// <returns>How many were waiting.</returns>
    public int Cancel(PlayerId peer) {
        expiring.Clear();

        foreach (var (id, pending) in outstanding) {
            if (pending.Peer == peer) {
                expiring.Add(id);
            }
        }

        foreach (var id in expiring) {
            if (outstanding.Remove(id, out var pending)) {
                pending.Fail(RpcFailure.Disconnected);
            }
        }

        return expiring.Count;
    }

    /// <summary>Fails everything that has waited long enough.</summary>
    /// <param name="elapsed">Time since the last call.</param>
    /// <returns>How many timed out.</returns>
    public int Advance(TimeSpan elapsed) {
        if (outstanding.Count == 0) {
            return 0;
        }

        expiring.Clear();

        foreach (var (id, pending) in outstanding) {
            var remaining = pending.Remaining - elapsed.TotalSeconds;

            if (remaining <= 0) {
                expiring.Add(id);
            } else {
                outstanding[id] = pending with { Remaining = remaining };
            }
        }

        foreach (var id in expiring) {
            if (outstanding.Remove(id, out var pending)) {
                pending.Fail(RpcFailure.TimedOut);
            }
        }

        return expiring.Count;
    }

    /// <summary>Fails everything, for a session that is stopping.</summary>
    public void Clear() {
        foreach (var pending in outstanding.Values) {
            pending.Fail(RpcFailure.Disconnected);
        }

        outstanding.Clear();
    }

    uint Reserve() {
        // Zero is "no reply expected", and a live id is never handed out twice. The table decides
        // rather than the counter, because the counter wraps and a collision would deliver an answer
        // to the wrong await.
        do {
            next++;
        } while (next == 0 || outstanding.ContainsKey(next));

        return next;
    }

    delegate bool ReplyReader(ref BitReader reader);

    readonly record struct Pending(
        RpcMethod Method,
        PlayerId Peer,
        double Remaining,
        ReplyReader Read,
        Action<RpcFailure> Fail
    );
}
