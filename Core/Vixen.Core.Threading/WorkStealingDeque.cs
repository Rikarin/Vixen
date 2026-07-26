// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Vixen.Core.Threading;

/// <summary>
///     A bounded Chase–Lev deque: its owning thread pushes and pops one end, every other thread
///     steals from the other.
/// </summary>
/// <remarks>
///     <para>
///         The owner works from the bottom, LIFO, which is the whole reason the structure is shaped
///         this way. The most recently pushed job is the one whose data is still in cache, and it is
///         also the deepest part of the graph — so working it first keeps the working set small and
///         drives the graph towards completion. Thieves take from the top, which is the oldest and
///         therefore the coldest and the most likely to have work hanging off it.
///     </para>
///     <para>
///         Owner pushes and pops are lock-free and, in the uncontended case, do not use an
///         interlocked operation at all: only the last remaining element has to be fought over,
///         because that is the only one a thief can also be taking. Stealing is a single
///         compare-and-swap.
///     </para>
///     <para>
///         Bounded rather than growable. A growable Chase–Lev deque has to keep old buffers alive
///         for thieves that are mid-steal, which means either a GC dependency in the middle of the
///         hot path or a hazard-pointer scheme. The bound is instead handled where the answer is
///         obvious: a push that does not fit returns <see langword="false" />, and the scheduler
///         puts the item on its shared queue. Work does not get lost; it loses its locality, which
///         is the correct thing to give up when a thread already has a thousand jobs queued and no
///         time to look at them.
///     </para>
///     <para>
///         Items are <see cref="long" />, and this type assumes 64-bit aligned <see cref="long" />
///         reads and writes are atomic — true on every platform Vixen targets, all of which are
///         64-bit.
///     </para>
/// </remarks>
sealed class WorkStealingDeque {
    readonly long[] items;
    readonly int mask;

    // Only the owner writes `bottom`; anyone may CAS `top`. Padded apart so the owner's ordinary
    // writes to one do not invalidate the other's cache line for every thief in the system.
    PaddedLong bottom;
    PaddedLong top;

    /// <summary>How many items the deque can hold.</summary>
    internal int Capacity => items.Length;

    /// <summary>
    ///     An estimate of how many items are queued. Racy by construction — both ends move — so it
    ///     is for "is there anything to do" and for diagnostics, not for control flow that has to be
    ///     right.
    /// </summary>
    internal int ApproximateCount {
        get {
            var count = Volatile.Read(ref bottom.Value) - Volatile.Read(ref top.Value);
            return count <= 0 ? 0 : (int)count;
        }
    }

    /// <summary>Creates a deque holding <paramref name="capacity" /> items.</summary>
    /// <param name="capacity">The capacity. Rounded up to a power of two so the index wrap is a mask.</param>
    internal WorkStealingDeque(int capacity) {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        var rounded = (int)BitOperations.RoundUpToPowerOf2((uint)capacity);
        items = new long[rounded];
        mask = rounded - 1;
    }

    /// <summary>Pushes an item. Owner thread only.</summary>
    /// <param name="item">The item.</param>
    /// <returns><see langword="false" /> if the deque is full and nothing was pushed.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryPush(long item) {
        var b = Volatile.Read(ref bottom.Value);
        var t = Volatile.Read(ref top.Value);

        if (b - t >= items.Length) {
            return false;
        }

        items[b & mask] = item;

        // Release: the item write above must be visible to a thief before the index that exposes it.
        Volatile.Write(ref bottom.Value, b + 1);
        return true;
    }

    /// <summary>Takes the most recently pushed item. Owner thread only.</summary>
    /// <param name="item">The item, or 0 if there was none.</param>
    /// <returns><see langword="false" /> if the deque was empty, or a thief won the last item.</returns>
    internal bool TryPop(out long item) {
        var b = Volatile.Read(ref bottom.Value) - 1;
        Volatile.Write(ref bottom.Value, b);

        // The claim above and the read below must not be reordered, or the owner and a thief can
        // both conclude they have the last item.
        Interlocked.MemoryBarrier();
        var t = Volatile.Read(ref top.Value);

        if (t > b) {
            Volatile.Write(ref bottom.Value, b + 1);
            item = 0;
            return false;
        }

        item = items[b & mask];

        if (t != b) {
            return true;
        }

        // Exactly one item was left, so a thief may be taking the same one. Whoever wins the CAS on
        // `top` has it; either way the deque is now empty.
        var won = Interlocked.CompareExchange(ref top.Value, t + 1, t) == t;
        Volatile.Write(ref bottom.Value, b + 1);

        if (won) {
            return true;
        }

        item = 0;
        return false;
    }

    /// <summary>Takes the least recently pushed item. Any thread.</summary>
    /// <param name="item">The item, or 0 if there was none.</param>
    /// <returns>
    ///     <see langword="false" /> if the deque was empty or another thread took the item first. A
    ///     failed steal is not evidence that the deque is empty.
    /// </returns>
    internal bool TrySteal(out long item) {
        var t = Volatile.Read(ref top.Value);
        Interlocked.MemoryBarrier();
        var b = Volatile.Read(ref bottom.Value);

        if (t >= b) {
            item = 0;
            return false;
        }

        // Read before the CAS. The read can only be stale if the owner has already overwritten the
        // slot, which needs a full lap of the buffer — and TryPush refuses to lap a live item.
        var candidate = items[t & mask];

        if (Interlocked.CompareExchange(ref top.Value, t + 1, t) != t) {
            item = 0;
            return false;
        }

        item = candidate;
        return true;
    }

    [StructLayout(LayoutKind.Explicit, Size = 128)]
    struct PaddedLong {
        [FieldOffset(64)] internal long Value;
    }
}
