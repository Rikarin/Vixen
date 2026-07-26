// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;

namespace Vixen.Core.Collections;

/// <summary>
///     A dense array whose removed slots are recycled, addressed by plain <see cref="int" /> index.
/// </summary>
/// <typeparam name="T">The stored type.</typeparam>
/// <remarks>
///     <para>
///         The difference from <see cref="HandlePool{T}" /> is who owns the identity. Use a free list
///         where the index never leaves the structure that holds it — the nodes of a tree, the
///         entries of a graph, a scheduler's task table — so a stale index is impossible by
///         construction. Use a handle pool wherever the reference is handed to somebody else, where
///         staleness is not only possible but expected and needs detecting.
///     </para>
///     <para>
///         There is no generation counter, so an index kept past its release will silently read
///         whatever landed there next. What <i>is</i> caught is releasing the same index twice, which
///         would otherwise put one slot on the free list twice and hand it to two callers — a
///         corruption that shows up arbitrarily far from its cause.
///     </para>
/// </remarks>
public sealed class FreeList<T> {
    T?[] items;
    bool[] live;
    int[] freeSlots;
    int freeCount;

    /// <summary>How many slots have ever been used, live and free together.</summary>
    public int SlotCount { get; private set; }

    /// <summary>How many slots hold a live item.</summary>
    public int Count => SlotCount - freeCount;

    /// <summary>Creates a list with room for <paramref name="capacity" /> items before it grows.</summary>
    /// <param name="capacity">The initial slot count.</param>
    public FreeList(int capacity = 16) {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        items = capacity == 0 ? [] : new T?[capacity];
        live = capacity == 0 ? [] : new bool[capacity];
        freeSlots = [];
    }

    /// <summary>The item at <paramref name="index" />.</summary>
    /// <param name="index">A live index.</param>
    /// <returns>A reference to the slot, so a large struct can be mutated in place.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index" /> was never allocated.</exception>
    public ref T? this[int index] {
        get {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, SlotCount);
            return ref items[index];
        }
    }

    /// <summary>Whether a slot currently holds an item.</summary>
    /// <param name="index">The index to check.</param>
    /// <returns><see langword="false" /> for a free or never-allocated slot.</returns>
    public bool IsLive(int index) => (uint)index < (uint)SlotCount && live[index];

    /// <summary>Stores an item in the first available slot.</summary>
    /// <param name="item">The item.</param>
    /// <returns>The index it landed at, valid until it is released.</returns>
    public int Add(T item) {
        int slot;

        if (freeCount > 0) {
            slot = freeSlots[--freeCount];
        } else {
            if (SlotCount == items.Length) {
                var size = Math.Max(4, items.Length * 2);
                Array.Resize(ref items, size);
                Array.Resize(ref live, size);
            }

            slot = SlotCount++;
        }

        items[slot] = item;
        live[slot] = true;
        return slot;
    }

    /// <summary>Releases a slot for reuse.</summary>
    /// <param name="index">The index to release.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index" /> was never allocated.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="index" /> is already free.</exception>
    public void Release(int index) {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, SlotCount);

        if (!live[index]) {
            throw new InvalidOperationException(
                $"Slot {index} is already free. Releasing it twice would queue it for reuse twice and hand one slot to two callers."
            );
        }

        live[index] = false;

        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) {
            items[index] = default;
        }

        if (freeCount == freeSlots.Length) {
            Array.Resize(ref freeSlots, Math.Max(4, freeSlots.Length * 2));
        }

        freeSlots[freeCount++] = index;
    }

    /// <summary>Empties the list, keeping the buffers.</summary>
    public void Clear() {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) {
            Array.Clear(items, 0, SlotCount);
        }

        Array.Clear(live, 0, SlotCount);
        SlotCount = 0;
        freeCount = 0;
    }

    /// <summary>Enumerates the live slots and their items, in index order.</summary>
    /// <returns>An enumerator.</returns>
    public Enumerator GetEnumerator() => new(this);

    /// <summary>Walks the live slots, skipping the free ones.</summary>
    public struct Enumerator {
        readonly FreeList<T> list;
        int index;

        internal Enumerator(FreeList<T> list) {
            this.list = list;
            index = -1;
        }

        /// <summary>The index and item at the current position.</summary>
        public readonly (int Index, T Item) Current => (index, list.items[index]!);

        /// <summary>Advances to the next live slot.</summary>
        /// <returns><see langword="false" /> when there are none left.</returns>
        public bool MoveNext() {
            while (++index < list.SlotCount) {
                if (list.live[index]) {
                    return true;
                }
            }

            return false;
        }
    }
}
