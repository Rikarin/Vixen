// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;

namespace Vixen.Core.Collections;

/// <summary>
///     A growable array built from fixed-size chunks, so that <b>a reference into it stays valid
///     when it grows</b>.
/// </summary>
/// <typeparam name="T">The element type.</typeparam>
/// <remarks>
///     <para>
///         The property a <c>List&lt;T&gt;</c> cannot offer: growing it reallocates and every
///         outstanding <c>ref</c> points at the old array. Anything that hands out references into
///         its storage and also grows — an ECS chunk store, a node pool, an interned table — needs
///         this instead.
///     </para>
///     <para>
///         The cost is that the elements are not contiguous, so there is no whole-collection
///         <c>Span</c>. Iterate chunk by chunk with <see cref="GetChunk" />; each chunk <i>is</i>
///         contiguous, and that is the granularity a vectorised sweep wants anyway.
///     </para>
/// </remarks>
public sealed class ChunkedArray<T> {
    readonly int chunkSize;
    readonly int chunkShift;
    readonly int chunkMask;

    T[]?[] chunks;
    int materialised;

    /// <summary>How many elements the array holds.</summary>
    public int Count { get; private set; }

    /// <summary>How many elements each chunk holds. Always a power of two.</summary>
    public int ChunkSize => chunkSize;

    /// <summary>How many chunks currently exist.</summary>
    public int ChunkCount => (Count + chunkSize - 1) / chunkSize;

    /// <summary>Creates an array with the given chunk size.</summary>
    /// <param name="chunkSize">
    ///     Elements per chunk. Rounded up to a power of two so the index split is a shift and a mask
    ///     rather than a division.
    /// </param>
    public ChunkedArray(int chunkSize = 1024) {
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkSize, 1);

        this.chunkSize = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)chunkSize);
        chunkShift = System.Numerics.BitOperations.TrailingZeroCount((uint)this.chunkSize);
        chunkMask = this.chunkSize - 1;
        chunks = [];
    }

    /// <summary>A reference to the element at <paramref name="index" />.</summary>
    /// <param name="index">The index, checked against <see cref="Count" />.</param>
    /// <returns>A reference that stays valid however much the array grows.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index" /> is out of range.</exception>
    public ref T this[int index] {
        get {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
            return ref chunks[index >> chunkShift]![index & chunkMask];
        }
    }

    /// <summary>Appends an element.</summary>
    /// <param name="item">The element to append.</param>
    /// <returns>The index it landed at.</returns>
    public int Add(T item) {
        var index = Count;
        EnsureChunk(index);
        chunks[index >> chunkShift]![index & chunkMask] = item;
        Count++;
        return index;
    }

    /// <summary>Grows the array to <paramref name="count" /> elements, defaulting the new ones.</summary>
    /// <param name="count">The new element count. Must not be smaller than the current one.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count" /> would shrink the array.</exception>
    public void Grow(int count) {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, Count);

        if (count > 0) {
            EnsureChunk(count - 1);
        }

        Count = count;
    }

    /// <summary>One chunk's worth of elements, contiguous.</summary>
    /// <param name="chunkIndex">The chunk, from 0 to <see cref="ChunkCount" /> exclusive.</param>
    /// <returns>The chunk's live elements — shorter than <see cref="ChunkSize" /> for the last one.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="chunkIndex" /> is out of range.</exception>
    public Span<T> GetChunk(int chunkIndex) {
        ArgumentOutOfRangeException.ThrowIfNegative(chunkIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(chunkIndex, ChunkCount);

        var start = chunkIndex << chunkShift;
        return chunks[chunkIndex].AsSpan(0, Math.Min(chunkSize, Count - start));
    }

    /// <summary>Empties the array. Keeps the chunks allocated for reuse.</summary>
    public void Clear() {
        for (var i = 0; i < ChunkCount; i++) {
            GetChunk(i).Clear();
        }

        Count = 0;
    }

    /// <summary>Drops every chunk, releasing the memory.</summary>
    public void Reset() {
        chunks = [];
        materialised = 0;
        Count = 0;
    }

    /// <summary>Enumerates the elements in index order.</summary>
    /// <returns>An enumerator.</returns>
    public Enumerator GetEnumerator() => new(this);

    void EnsureChunk(int index) {
        var chunk = index >> chunkShift;

        if (chunk < materialised) {
            return;
        }

        if (chunk >= chunks.Length) {
            Array.Resize(ref chunks, Math.Max(chunk + 1, Math.Max(4, chunks.Length * 2)));
        }

        // Fill the gap, not just the target. Grow can jump several chunks ahead, and leaving the
        // ones in between null makes every index into them fail long after the call that skipped
        // them.
        while (materialised <= chunk) {
            chunks[materialised++] = new T[chunkSize];
        }
    }

    /// <summary>Walks the elements, chunk by chunk.</summary>
    public struct Enumerator {
        readonly ChunkedArray<T> array;
        int index;

        internal Enumerator(ChunkedArray<T> array) {
            this.array = array;
            index = -1;
        }

        /// <summary>The element at the current position.</summary>
        [UnscopedRef]
        public readonly ref T Current => ref array[index];

        /// <summary>Advances to the next element.</summary>
        /// <returns><see langword="false" /> when there are none left.</returns>
        public bool MoveNext() => ++index < array.Count;
    }
}
