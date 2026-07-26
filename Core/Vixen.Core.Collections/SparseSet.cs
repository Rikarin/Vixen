// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;

namespace Vixen.Core.Collections;

/// <summary>
///     A set of non-negative integer keys with a value attached to each: O(1) add, remove and
///     lookup, and — the reason it exists — iteration over a <b>dense, contiguous</b> array of the
///     values, in no particular order.
/// </summary>
/// <typeparam name="T">The value type.</typeparam>
/// <remarks>
///     <para>
///         Two arrays do the work. A sparse array maps a key to its position in the dense array; the
///         dense arrays hold the keys and values packed together. Removing swaps the last entry into
///         the hole, so the dense side never has gaps and a query never touches a cache line it does
///         not need.
///     </para>
///     <para>
///         <b>The sparse array is indexed by key</b>, so memory is proportional to the largest key
///         ever added, not to the number of entries. That is the right trade for entity ids and
///         component indices, which are dense and small by construction, and the wrong one for
///         anything sparse and unbounded — use a dictionary there.
///     </para>
///     <para>
///         Removal reorders. Anything that depends on iteration order needs to sort, and anything
///         holding a dense index across a removal is holding the wrong one.
///     </para>
/// </remarks>
public sealed class SparseSet<T> {
    const int Absent = -1;

    int[] sparse;
    int[] denseKeys;
    T[] denseValues;

    /// <summary>How many entries the set holds.</summary>
    public int Count { get; private set; }

    /// <summary>The largest key the sparse array currently has room for, plus one.</summary>
    public int KeyCapacity => sparse.Length;

    /// <summary>The keys, densely packed, in the set's own order.</summary>
    public ReadOnlySpan<int> Keys => denseKeys.AsSpan(0, Count);

    /// <summary>
    ///     The values, densely packed, in the same order as <see cref="Keys" />. Writable so a system
    ///     can sweep every component in one pass without going through the key lookup.
    /// </summary>
    public Span<T> Values => denseValues.AsSpan(0, Count);

    /// <summary>Creates a set sized for a given key range and entry count.</summary>
    /// <param name="keyCapacity">The largest key expected, plus one.</param>
    /// <param name="capacity">The expected number of entries.</param>
    public SparseSet(int keyCapacity = 64, int capacity = 16) {
        ArgumentOutOfRangeException.ThrowIfNegative(keyCapacity);
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);

        sparse = new int[keyCapacity];
        sparse.AsSpan().Fill(Absent);
        denseKeys = capacity == 0 ? [] : new int[capacity];
        denseValues = capacity == 0 ? [] : new T[capacity];
    }

    /// <summary>Whether a key is in the set.</summary>
    /// <param name="key">The key.</param>
    /// <returns><see langword="true" /> if the key has a value.</returns>
    public bool Contains(int key) => (uint)key < (uint)sparse.Length && sparse[key] != Absent;

    /// <summary>Adds or replaces the value for a key.</summary>
    /// <param name="key">The key. Must not be negative.</param>
    /// <param name="value">The value.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="key" /> is negative.</exception>
    public void Set(int key, T value) {
        ArgumentOutOfRangeException.ThrowIfNegative(key);

        if (key >= sparse.Length) {
            GrowSparse(key + 1);
        }

        var dense = sparse[key];
        if (dense != Absent) {
            denseValues[dense] = value;
            return;
        }

        if (Count == denseKeys.Length) {
            GrowDense();
        }

        sparse[key] = Count;
        denseKeys[Count] = key;
        denseValues[Count] = value;
        Count++;
    }

    /// <summary>Looks up the value for a key.</summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The value, or the default if the key is absent.</param>
    /// <returns><see langword="false" /> if the key is not in the set.</returns>
    public bool TryGetValue(int key, [MaybeNullWhen(false)] out T value) {
        if (Contains(key)) {
            value = denseValues[sparse[key]];
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>
    ///     A reference to the value for a key, for mutating a large struct in place.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <returns>A reference into the dense array. Invalidated by any add or remove.</returns>
    /// <exception cref="KeyNotFoundException">The key is not in the set.</exception>
    public ref T GetReference(int key) {
        if (!Contains(key)) {
            throw new KeyNotFoundException($"Key {key} is not in the set.");
        }

        return ref denseValues[sparse[key]];
    }

    /// <summary>Removes a key.</summary>
    /// <param name="key">The key.</param>
    /// <returns><see langword="false" /> if the key was not in the set.</returns>
    /// <remarks>
    ///     The last entry is swapped into the hole, so the dense arrays stay packed and the order
    ///     changes. Anything holding a dense index across this call is holding the wrong one.
    /// </remarks>
    public bool Remove(int key) {
        if (!Contains(key)) {
            return false;
        }

        var dense = sparse[key];
        var last = Count - 1;

        if (dense != last) {
            var movedKey = denseKeys[last];
            denseKeys[dense] = movedKey;
            denseValues[dense] = denseValues[last];
            sparse[movedKey] = dense;
        }

        sparse[key] = Absent;
        denseKeys[last] = 0;
        denseValues[last] = default!;
        Count--;
        return true;
    }

    /// <summary>Empties the set, keeping the buffers.</summary>
    /// <remarks>
    ///     Walks the dense keys rather than clearing the whole sparse array, so emptying a set of
    ///     ten entries costs ten writes and not one per key the array has ever seen.
    /// </remarks>
    public void Clear() {
        for (var i = 0; i < Count; i++) {
            sparse[denseKeys[i]] = Absent;
        }

        Array.Clear(denseValues, 0, Count);
        Count = 0;
    }

    /// <summary>Enumerates the entries in dense order.</summary>
    /// <returns>An enumerator.</returns>
    public Enumerator GetEnumerator() => new(this);

    void GrowSparse(int required) {
        var size = Math.Max(required, Math.Max(4, sparse.Length * 2));
        var previous = sparse.Length;
        Array.Resize(ref sparse, size);
        sparse.AsSpan(previous).Fill(Absent);
    }

    void GrowDense() {
        var size = Math.Max(4, denseKeys.Length * 2);
        Array.Resize(ref denseKeys, size);
        Array.Resize(ref denseValues, size);
    }

    /// <summary>Walks the dense entries.</summary>
    public struct Enumerator {
        readonly SparseSet<T> set;
        int index;

        internal Enumerator(SparseSet<T> set) {
            this.set = set;
            index = -1;
        }

        /// <summary>The key and value at the current position.</summary>
        public readonly (int Key, T Value) Current => (set.denseKeys[index], set.denseValues[index]);

        /// <summary>Advances to the next entry.</summary>
        /// <returns><see langword="false" /> when there are none left.</returns>
        public bool MoveNext() => ++index < set.Count;
    }
}
