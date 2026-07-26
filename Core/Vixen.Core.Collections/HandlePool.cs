// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Vixen.Core.Collections;

/// <summary>
///     Stores items in a contiguous table and hands out <see cref="Handle{T}" />s to them. Removing
///     an item frees its slot for reuse and bumps that slot's generation, so every handle taken
///     before the removal is detectably stale.
/// </summary>
/// <typeparam name="T">The stored type.</typeparam>
/// <remarks>
///     <para>
///         The engine's answer to "how do I refer to a GPU resource". Use-after-free becomes a
///         detected generation mismatch instead of a native crash, and it costs one comparison on
///         the lookup path.
///     </para>
///     <para>
///         Not thread-safe. The RHI's resource tables are written from one thread and read from many
///         after a barrier; a pool that locked on every lookup would be the wrong trade for that.
///     </para>
/// </remarks>
public sealed class HandlePool<T> {
    // A slot is live when Generation is odd. Removing increments, so a freed slot's generation is
    // even and matches no handle — including the zeroed one, whose generation is 0.
    T?[] items;
    uint[] generations;
    int[] freeSlots;
    int freeCount;
    int capacity;

    /// <summary>How many items are stored.</summary>
    public int Count { get; private set; }

    /// <summary>How many slots the table holds, live and free together.</summary>
    public int Capacity => capacity;

    /// <summary>Creates a pool with room for <paramref name="capacity" /> items before it grows.</summary>
    /// <param name="capacity">The initial slot count.</param>
    public HandlePool(int capacity = 16) {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        items = capacity == 0 ? [] : new T?[capacity];
        generations = capacity == 0 ? [] : new uint[capacity];
        freeSlots = [];
    }

    /// <summary>Stores an item and returns a handle to it.</summary>
    /// <param name="item">The item to store.</param>
    /// <returns>A handle that stays valid until the item is removed.</returns>
    public Handle<T> Add(T item) {
        int slot;

        if (freeCount > 0) {
            slot = freeSlots[--freeCount];
        } else {
            if (capacity == items.Length) {
                Grow();
            }

            slot = capacity++;
            generations[slot] = 0;
        }

        // Odd means live. A freed slot was left even, so this is the increment that opens it.
        generations[slot]++;
        items[slot] = item;
        Count++;

        return new((uint)slot, generations[slot]);
    }

    /// <summary>Whether a handle still refers to a stored item.</summary>
    /// <param name="handle">The handle to check.</param>
    /// <returns><see langword="false" /> for a null or stale handle.</returns>
    /// <remarks>
    ///     The oddness test is not redundant with the equality: a handle is a public struct that
    ///     anything can construct, and without it a forged handle carrying a freed slot's even
    ///     generation would read as live.
    /// </remarks>
    public bool Contains(Handle<T> handle) =>
        (handle.Generation & 1) == 1
        && handle.Index < (uint)capacity
        && generations[handle.Index] == handle.Generation;

    /// <summary>Looks up the item a handle refers to.</summary>
    /// <param name="handle">The handle.</param>
    /// <param name="item">The item, or the default for a stale handle.</param>
    /// <returns><see langword="false" /> if the handle was null or stale.</returns>
    public bool TryGet(Handle<T> handle, [MaybeNullWhen(false)] out T item) {
        if (Contains(handle)) {
            item = items[handle.Index]!;
            return true;
        }

        item = default;
        return false;
    }

    /// <summary>Looks up the item a handle refers to, or throws.</summary>
    /// <param name="handle">The handle.</param>
    /// <returns>The item.</returns>
    /// <exception cref="InvalidOperationException">The handle is null or stale.</exception>
    public T Get(Handle<T> handle) =>
        TryGet(handle, out var item)
            ? item
            : throw new InvalidOperationException(
                $"{handle} does not refer to a live {typeof(T).Name}. It was either never valid or the slot has been reused since."
            );

    /// <summary>
    ///     A reference to the stored item, for mutating a large struct in place without copying it
    ///     out and back.
    /// </summary>
    /// <param name="handle">The handle.</param>
    /// <returns>A reference to the slot. Invalidated by anything that grows the pool.</returns>
    /// <exception cref="InvalidOperationException">The handle is null or stale.</exception>
    public ref T? GetReference(Handle<T> handle) {
        if (!Contains(handle)) {
            throw new InvalidOperationException($"{handle} does not refer to a live {typeof(T).Name}.");
        }

        return ref items[handle.Index];
    }

    /// <summary>Removes the item a handle refers to and frees its slot.</summary>
    /// <param name="handle">The handle.</param>
    /// <returns><see langword="false" /> if the handle was already null or stale.</returns>
    public bool Remove(Handle<T> handle) {
        if (!Contains(handle)) {
            return false;
        }

        var slot = (int)handle.Index;

        // Bump first: every outstanding handle to this slot is stale from here on, including the
        // one that was just used to remove it.
        generations[slot]++;

        // Wrapping would make a very old handle valid again. It takes two billion reuses of one
        // slot to get here, and skipping the wrap costs one branch on a cold path.
        if (generations[slot] == 0) {
            generations[slot] = 2;
        }

        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) {
            items[slot] = default;
        }

        if (freeCount == freeSlots.Length) {
            Array.Resize(ref freeSlots, Math.Max(4, freeSlots.Length * 2));
        }

        freeSlots[freeCount++] = slot;
        Count--;
        return true;
    }

    /// <summary>Removes everything. Outstanding handles all become stale.</summary>
    public void Clear() {
        for (var slot = 0; slot < capacity; slot++) {
            if ((generations[slot] & 1) == 1) {
                Remove(new((uint)slot, generations[slot]));
            }
        }
    }

    /// <summary>Enumerates the live handles and their items, in slot order.</summary>
    /// <returns>An enumerator.</returns>
    public Enumerator GetEnumerator() => new(this);

    void Grow() {
        var size = Math.Max(4, items.Length * 2);
        Array.Resize(ref items, size);
        Array.Resize(ref generations, size);
    }

    /// <summary>Walks the live slots, skipping the free ones.</summary>
    public struct Enumerator {
        readonly HandlePool<T> pool;
        int slot;

        internal Enumerator(HandlePool<T> pool) {
            this.pool = pool;
            slot = -1;
        }

        /// <summary>The handle and item at the current position.</summary>
        public readonly (Handle<T> Handle, T Item) Current =>
            (new((uint)slot, pool.generations[slot]), pool.items[slot]!);

        /// <summary>Advances to the next live slot.</summary>
        /// <returns><see langword="false" /> when there are none left.</returns>
        public bool MoveNext() {
            while (++slot < pool.capacity) {
                if ((pool.generations[slot] & 1) == 1) {
                    return true;
                }
            }

            return false;
        }
    }
}
