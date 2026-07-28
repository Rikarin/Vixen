// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Audio.Streaming;

/// <summary>
///     A fixed-size float queue with one writer and one reader, and no lock between them.
/// </summary>
/// <remarks>
///     <para>
///         The join between a thread that can block and a thread that must not. A decoder fills it
///         from a disk or a network; the mixer drains it inside the audio callback. Neither takes a
///         lock, so a decoder stalled on a slow read cannot stall the callback — the callback finds
///         the buffer empty, writes silence, and counts it.
///     </para>
///     <para>
///         <b>Monotonic counters rather than wrapped indices.</b> The two cursors only ever increase,
///         and the position in the array is the counter modulo the capacity. That makes "full" and
///         "empty" different states without sacrificing a slot, and it means a torn read of a cursor
///         is impossible on any platform .NET runs on: both are <see cref="long" /> accessed through
///         <see cref="Volatile" />, and each is written by exactly one thread.
///     </para>
///     <para>
///         <b>One writer and one reader is a requirement, not a nicety.</b> Two writers race on the
///         write cursor. The pump enforces it by owning each provider's buffer alone.
///     </para>
/// </remarks>
public sealed class AudioRingBuffer {
    readonly float[] buffer;
    long readCursor;
    long writeCursor;

    /// <summary>A buffer that holds a number of floats.</summary>
    /// <param name="capacity">How many floats — frames times channels, not frames.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity" /> is not positive.</exception>
    public AudioRingBuffer(int capacity) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        buffer = new float[capacity];
    }

    /// <summary>How many floats it can hold.</summary>
    public int Capacity => buffer.Length;

    /// <summary>How many floats are waiting to be read.</summary>
    public int Count => (int)(Volatile.Read(ref writeCursor) - Volatile.Read(ref readCursor));

    /// <summary>How much room there is to write into.</summary>
    public int Free => Capacity - Count;

    /// <summary>Writes as much as will fit.</summary>
    /// <param name="source">What to write.</param>
    /// <returns>How many floats were taken. Fewer than offered means it is full.</returns>
    /// <remarks>Called by the producer, and only by the producer.</remarks>
    public int Write(ReadOnlySpan<float> source) {
        var write = Volatile.Read(ref writeCursor);
        var free = Capacity - (int)(write - Volatile.Read(ref readCursor));
        var count = Math.Min(free, source.Length);

        if (count <= 0) {
            return 0;
        }

        var offset = (int)(write % Capacity);
        var first = Math.Min(count, Capacity - offset);
        source[..first].CopyTo(buffer.AsSpan(offset));

        if (first < count) {
            source.Slice(first, count - first).CopyTo(buffer);
        }

        // Published last: a reader that sees the new cursor is guaranteed to see the writes above
        // it, because Volatile.Write is a release.
        Volatile.Write(ref writeCursor, write + count);
        return count;
    }

    /// <summary>Reads as much as is there.</summary>
    /// <param name="destination">Where to put it.</param>
    /// <returns>How many floats were read. Fewer than asked for means it is empty.</returns>
    /// <remarks>Called by the consumer, and only by the consumer.</remarks>
    public int Read(Span<float> destination) {
        var read = Volatile.Read(ref readCursor);
        var available = (int)(Volatile.Read(ref writeCursor) - read);
        var count = Math.Min(available, destination.Length);

        if (count <= 0) {
            return 0;
        }

        var offset = (int)(read % Capacity);
        var first = Math.Min(count, Capacity - offset);
        buffer.AsSpan(offset, first).CopyTo(destination);

        if (first < count) {
            buffer.AsSpan(0, count - first).CopyTo(destination[first..]);
        }

        Volatile.Write(ref readCursor, read + count);
        return count;
    }

    /// <summary>Throws everything in it away.</summary>
    /// <remarks>
    ///     Safe only when neither side is running — after a stop, or before a start. Called between
    ///     the two it will lose or duplicate frames, which is why seeking a stream goes through the
    ///     provider rather than reaching in here.
    /// </remarks>
    public void Clear() {
        Volatile.Write(ref readCursor, 0);
        Volatile.Write(ref writeCursor, 0);
        Array.Clear(buffer);
    }
}
