// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Platform;

/// <summary>
///     The buffer between whoever produces platform events and the frame that consumes them.
///     Double-buffered, so a drain is a pointer swap rather than a copy and the consumer reads
///     without holding a lock.
/// </summary>
/// <remarks>
///     <para>
///         Every platform implementation shares this rather than writing its own: the concurrency
///         is the same problem everywhere, and it is not a problem worth solving five times. SDL
///         delivers on the thread that pumps, but Android's lifecycle callbacks arrive on the UI
///         thread and a browser's on the JS thread, so <see cref="Post" /> has to be safe from
///         any thread even where today's backend only uses one.
///     </para>
///     <para>
///         <see cref="Drain" /> is for the owner — the thread running the frame loop — and calling
///         it from two threads at once is a bug the type does not try to make safe, because two
///         threads consuming input is not a thing an engine should be able to do by accident.
///     </para>
/// </remarks>
public sealed class PlatformEventBuffer {
    /// <summary>How many events may pile up before the oldest are dropped.</summary>
    /// <remarks>
    ///     A bound rather than unbounded growth, for the same reason
    ///     <c>Vixen.Core.IO.Watch.FileWatcher</c> has one: a consumer that stops draining — a hung
    ///     frame, a modal resize loop on Windows — must cost a bounded amount of memory and report
    ///     that it lost something, rather than growing until the process dies. At a generous
    ///     thousand events a second this is eight seconds of input.
    /// </remarks>
    public const int Capacity = 8192;

    readonly Lock gate = new();

    PlatformEvent[] incoming = new PlatformEvent[64];
    PlatformEvent[] outgoing = new PlatformEvent[64];
    int count;
    int drained;
    long dropped;

    /// <summary>How many events are waiting for the next <see cref="Drain" />.</summary>
    public int PendingCount {
        get {
            lock (gate) {
                return count;
            }
        }
    }

    /// <summary>
    ///     How many events have been dropped because the queue was full, over the lifetime of the
    ///     queue.
    /// </summary>
    /// <remarks>
    ///     Non-zero means input was lost. Worth logging once rather than silently: dropped events
    ///     are how a key gets stuck down, because the release was the one that did not fit.
    /// </remarks>
    public long DroppedCount {
        get {
            lock (gate) {
                return dropped;
            }
        }
    }

    /// <summary>Adds an event. Safe from any thread.</summary>
    /// <param name="platformEvent">The event to add.</param>
    /// <returns><see langword="false" /> if the queue was full and the event was dropped.</returns>
    public bool Post(in PlatformEvent platformEvent) {
        lock (gate) {
            if (count == Capacity) {
                dropped++;
                return false;
            }

            if (count == incoming.Length) {
                Array.Resize(ref incoming, Math.Min(incoming.Length * 2, Capacity));
            }

            incoming[count++] = platformEvent;
            return true;
        }
    }

    /// <summary>
    ///     Takes everything enqueued since the last call, in the order it arrived.
    /// </summary>
    /// <returns>
    ///     A span valid until the next <see cref="Drain" /> or <see cref="Clear" />. It is not
    ///     copied, so holding on to it across a frame boundary reads the following frame's events.
    /// </returns>
    public ReadOnlySpan<PlatformEvent> Drain() {
        int taken;

        lock (gate) {
            // Hand the producer the buffer the consumer has finished with and keep the full one.
            // The events themselves never move, which is what makes this allocation-free once the
            // two buffers have grown to a frame's worth of input.
            (incoming, outgoing) = (outgoing, incoming);
            taken = count;
            count = 0;

            // `incoming` is now the buffer the last drain handed out. Wiping the slots it still
            // holds releases the strings a TextInput or DropFile event carried, which would
            // otherwise stay reachable until something happened to overwrite that exact index.
            Array.Clear(incoming, 0, Math.Min(drained, incoming.Length));
            drained = taken;
        }

        return outgoing.AsSpan(0, taken);
    }

    /// <summary>Throws away everything pending and everything from the last drain.</summary>
    public void Clear() {
        lock (gate) {
            Array.Clear(incoming);
            Array.Clear(outgoing);
            count = 0;
            drained = 0;
        }
    }
}
