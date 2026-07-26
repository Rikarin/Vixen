// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Vixen.Core.Collections;

/// <summary>
///     A fixed-capacity queue that overwrites its oldest entry when full: the log ring, the
///     profiler's sample history, the input event queue.
/// </summary>
/// <typeparam name="T">The element type.</typeparam>
/// <remarks>
///     <para>
///         Overwriting rather than growing is the point. A log that grows is a memory leak with a
///         long fuse; a log that keeps the last ten thousand lines and drops the rest is a diagnostic
///         tool with a fixed cost. <see cref="TryEnqueue" /> is there for the callers that would
///         rather be told than lose the oldest entry.
///     </para>
///     <para>
///         Not thread-safe. The profiler writes from one thread per ring and reads them after a
///         barrier; a lock on the write path would cost more than the samples are worth.
///     </para>
/// </remarks>
public sealed class RingBuffer<T> {
    readonly T[] items;
    int head;

    /// <summary>How many elements the buffer can hold.</summary>
    public int Capacity => items.Length;

    /// <summary>How many elements it currently holds.</summary>
    public int Count { get; private set; }

    /// <summary>Whether the buffer is at capacity, so the next write overwrites.</summary>
    public bool IsFull => Count == items.Length;

    /// <summary>Whether the buffer is empty.</summary>
    public bool IsEmpty => Count == 0;

    /// <summary>How many elements have been dropped to make room, since the buffer was created.</summary>
    /// <remarks>
    ///     Worth surfacing rather than hiding: "the log is missing the beginning" is a different
    ///     conversation from "nothing was logged", and a caller can only tell them apart if the
    ///     buffer says how much it threw away.
    /// </remarks>
    public long OverwrittenCount { get; private set; }

    /// <summary>Creates a buffer holding at most <paramref name="capacity" /> elements.</summary>
    /// <param name="capacity">The capacity. Must be positive.</param>
    public RingBuffer(int capacity) {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        items = new T[capacity];
    }

    /// <summary>The element at <paramref name="index" />, counting from the oldest.</summary>
    /// <param name="index">0 is the oldest element, <see cref="Count" /> − 1 the newest.</param>
    /// <returns>A reference to the element.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index" /> is out of range.</exception>
    public ref T this[int index] {
        get {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
            return ref items[(head + index) % items.Length];
        }
    }

    /// <summary>Appends an element, dropping the oldest if the buffer is full.</summary>
    /// <param name="item">The element.</param>
    public void Enqueue(T item) {
        if (IsFull) {
            items[head] = item;
            head = (head + 1) % items.Length;
            OverwrittenCount++;
            return;
        }

        items[(head + Count) % items.Length] = item;
        Count++;
    }

    /// <summary>Appends an element unless the buffer is full.</summary>
    /// <param name="item">The element.</param>
    /// <returns><see langword="false" /> if the buffer was full and nothing was written.</returns>
    public bool TryEnqueue(T item) {
        if (IsFull) {
            return false;
        }

        Enqueue(item);
        return true;
    }

    /// <summary>Removes and returns the oldest element.</summary>
    /// <param name="item">The oldest element, or the default if the buffer was empty.</param>
    /// <returns><see langword="false" /> if the buffer was empty.</returns>
    public bool TryDequeue([MaybeNullWhen(false)] out T item) {
        if (IsEmpty) {
            item = default;
            return false;
        }

        item = items[head]!;

        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) {
            items[head] = default!;
        }

        head = (head + 1) % items.Length;
        Count--;
        return true;
    }

    /// <summary>Reads the oldest element without removing it.</summary>
    /// <param name="item">The oldest element, or the default if the buffer was empty.</param>
    /// <returns><see langword="false" /> if the buffer was empty.</returns>
    public bool TryPeek([MaybeNullWhen(false)] out T item) {
        if (IsEmpty) {
            item = default;
            return false;
        }

        item = items[head]!;
        return true;
    }

    /// <summary>Empties the buffer. Does not reset <see cref="OverwrittenCount" />.</summary>
    public void Clear() {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) {
            Array.Clear(items);
        }

        head = 0;
        Count = 0;
    }

    /// <summary>Copies the elements out in order, oldest first.</summary>
    /// <param name="destination">A span of at least <see cref="Count" /> elements.</param>
    /// <returns>How many elements were copied.</returns>
    /// <exception cref="ArgumentException"><paramref name="destination" /> is too short.</exception>
    public int CopyTo(Span<T> destination) {
        if (destination.Length < Count) {
            throw new ArgumentException("The destination is shorter than the buffer's contents.", nameof(destination));
        }

        // At most two runs, because the contents either sit contiguously or wrap once.
        var first = Math.Min(Count, items.Length - head);
        items.AsSpan(head, first).CopyTo(destination);
        items.AsSpan(0, Count - first).CopyTo(destination[first..]);
        return Count;
    }

    /// <summary>Enumerates the elements oldest first.</summary>
    /// <returns>An enumerator.</returns>
    public Enumerator GetEnumerator() => new(this);

    /// <summary>Walks the buffer from oldest to newest.</summary>
    public struct Enumerator {
        readonly RingBuffer<T> buffer;
        int index;

        internal Enumerator(RingBuffer<T> buffer) {
            this.buffer = buffer;
            index = -1;
        }

        /// <summary>The element at the current position.</summary>
        [UnscopedRef]
        public readonly ref T Current => ref buffer[index];

        /// <summary>Advances to the next element.</summary>
        /// <returns><see langword="false" /> when there are none left.</returns>
        public bool MoveNext() => ++index < buffer.Count;
    }
}
