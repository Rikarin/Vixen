// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Numerics;

namespace Vixen.Ui.Layout;

/// <summary>Every node's child list, in one array.</summary>
/// <remarks>
///     <para>
///         A node's children are a contiguous run of ids, so addressing the <i>i</i>th child is an
///         add and a load and iterating a line of them is sequential. The alternative — a
///         <c>List&lt;int&gt;</c> per node — is a heap object per node, which is the exact cost
///         ADR-006 rejected the reference port for.
///     </para>
///     <para>
///         Blocks are powers of two with a free list per size, so a node that gains a fifth child
///         moves once into a block of eight and the block of four goes back for the next node that
///         needs one. Freed blocks are never compacted: a tree that has been at ten thousand nodes
///         keeps the arena for ten thousand, which for a UI that scrolled a long list once is the
///         right trade against moving live blocks around.
///     </para>
/// </remarks>
sealed class ChildArena {
    const int MinimumBlock = 4;
    const int MinimumBlockLog2 = 2;
    const int BucketCount = 24;

    readonly List<int>?[] free = new List<int>?[BucketCount];
    int[] storage = new int[256];
    int used;

    /// <summary>The ids in a block.</summary>
    /// <param name="offset">Where the block starts, or -1 for a node with no block yet.</param>
    /// <param name="length">How many ids to take.</param>
    /// <returns>The ids, or an empty span.</returns>
    /// <remarks>
    ///     The span points into the arena, which moves when it grows. Never hold one across an
    ///     insertion.
    /// </remarks>
    public Span<int> Slice(int offset, int length) =>
        offset < 0 || length <= 0 ? default : storage.AsSpan(offset, length);

    /// <summary>Moves a block into the next size up, copying what is in it.</summary>
    /// <param name="offset">The current block, or -1 if there is none.</param>
    /// <param name="count">How many ids are live in it.</param>
    /// <param name="capacity">How big it currently is.</param>
    /// <returns>Where the block now is and how big it now is.</returns>
    public (int Offset, int Capacity) Grow(int offset, int count, int capacity) {
        var wanted = capacity == 0 ? MinimumBlock : capacity * 2;
        var next = Allocate(wanted);

        if (offset >= 0) {
            storage.AsSpan(offset, count).CopyTo(storage.AsSpan(next, count));
            Free(offset, capacity);
        }

        return (next, wanted);
    }

    /// <summary>Hands a block back.</summary>
    /// <param name="offset">Where it starts, or -1.</param>
    /// <param name="capacity">How big it is.</param>
    public void Free(int offset, int capacity) {
        if (offset < 0 || capacity < MinimumBlock) {
            return;
        }

        var bucket = BucketOf(capacity);
        if (bucket >= BucketCount) {
            return;
        }

        (free[bucket] ??= []).Add(offset);
    }

    int Allocate(int size) {
        var bucket = BucketOf(size);
        if (bucket < BucketCount && free[bucket] is { Count: > 0 } available) {
            var offset = available[^1];
            available.RemoveAt(available.Count - 1);
            return offset;
        }

        if (used + size > storage.Length) {
            Array.Resize(ref storage, int.Max(storage.Length * 2, used + size));
        }

        var allocated = used;
        used += size;
        return allocated;
    }

    static int BucketOf(int size) => BitOperations.TrailingZeroCount((uint) size) - MinimumBlockLog2;
}
