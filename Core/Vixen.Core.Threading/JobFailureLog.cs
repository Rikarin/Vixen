// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.ExceptionServices;

namespace Vixen.Core.Threading;

/// <summary>The last few job failures, keyed by the handle that would want to hear about them.</summary>
/// <remarks>
///     <para>
///         A slot goes back on the free list the moment its job finishes, which is what keeps a
///         thousand slots enough for a program that schedules millions of jobs. It also means the
///         slot stops being able to answer questions about the job that just left it — including
///         "did it throw", which is the one question whose answer must not depend on how quickly the
///         caller got around to asking.
///     </para>
///     <para>
///         So failures move here on the way out. <see cref="JobScheduler.Complete(JobHandle)" />
///         consults this rather than the slot, and a job scheduled against a handle that has already
///         failed inherits the failure from here instead of finding a finished slot and concluding
///         that all was well. Failure is the rare path, so a dictionary and a lock are the right
///         price for making it reliable.
///     </para>
///     <para>
///         Bounded at <see cref="Capacity" /> — the ring the rest of the scheduler is built from
///         would be a strange thing to abandon here. A program that has failed more than this many
///         jobs without anyone completing their handles has a bigger problem than the oldest of them
///         being forgotten.
///     </para>
/// </remarks>
sealed class JobFailureLog {
    /// <summary>How many failures are remembered before the oldest is dropped.</summary>
    internal const int Capacity = 64;

    readonly Lock gate = new();
    readonly Dictionary<long, ExceptionDispatchInfo> byHandle = [];
    readonly Queue<long> order = new(Capacity);

    /// <summary>Remembers what a job threw.</summary>
    /// <param name="slot">The slot it ran in.</param>
    /// <param name="version">The generation of that slot.</param>
    /// <param name="failure">What it threw.</param>
    internal void Record(int slot, int version, ExceptionDispatchInfo failure) {
        var key = Key(slot, version);

        lock (gate) {
            if (!byHandle.TryAdd(key, failure)) {
                return;
            }

            order.Enqueue(key);

            if (order.Count > Capacity) {
                byHandle.Remove(order.Dequeue());
            }
        }
    }

    /// <summary>Looks up what a job threw, if it is still remembered.</summary>
    /// <param name="slot">The slot it ran in.</param>
    /// <param name="version">The generation of that slot.</param>
    /// <returns>What it threw, or <see langword="null" />.</returns>
    internal ExceptionDispatchInfo? Find(int slot, int version) {
        lock (gate) {
            return byHandle.GetValueOrDefault(Key(slot, version));
        }
    }

    static long Key(int slot, int version) => ((long)slot << 32) | (uint)version;
}
