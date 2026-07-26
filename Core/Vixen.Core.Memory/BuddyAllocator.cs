// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Vixen.Core.Memory;

/// <summary>
///     Suballocates offsets within a fixed region by repeatedly halving it. Owns no memory of its
///     own — it hands out <see cref="long" /> offsets, and what those index into is the caller's
///     business.
/// </summary>
/// <remarks>
///     <para>
///         Written for device memory. A Vulkan driver gives out a handful of large heaps and charges
///         for every allocation from them, so the engine takes a few big ones and carves resources
///         out of them itself. Because this deals only in offsets, it has no Vulkan in it and can be
///         tested exhaustively without a GPU — which is the whole reason it lives here rather than in
///         the backend.
///     </para>
///     <para>
///         <b>Why buddy and not a free list.</b> Merging is the hard part of a general allocator: two
///         adjacent free blocks have to be found and joined, or the heap fragments until a large
///         allocation fails while plenty is free. A buddy allocator makes that lookup arithmetic — a
///         block's partner is its offset with one bit flipped — so a free is O(log n) with no search
///         and no bookkeeping list to walk.
///     </para>
///     <para>
///         The cost is internal fragmentation: every request rounds up to a power of two, so a 33 KiB
///         resource occupies 64 KiB. That is the trade, it is bounded at under 2×, and it is why the
///         backend sends allocations above a threshold straight to the driver instead.
///     </para>
/// </remarks>
public sealed class BuddyAllocator {
    readonly long minimumBlockSize;
    readonly int levelCount;
    readonly HashSet<long>[] freeBlocks;
    readonly Dictionary<long, int> allocations = [];

    /// <summary>The size of the region being carved up.</summary>
    public long TotalSize { get; }

    /// <summary>How many bytes are handed out, counting the rounding-up.</summary>
    public long AllocatedBytes { get; private set; }

    /// <summary>How many bytes are not handed out.</summary>
    public long FreeBytes => TotalSize - AllocatedBytes;

    /// <summary>How many allocations are outstanding.</summary>
    public int AllocationCount => allocations.Count;

    /// <summary>
    ///     The largest single allocation that can currently succeed. Below <see cref="FreeBytes" />
    ///     whenever the region is fragmented, and the number worth watching.
    /// </summary>
    public long LargestFreeBlock {
        get {
            for (var level = levelCount - 1; level >= 0; level--) {
                if (freeBlocks[level].Count > 0) {
                    return BlockSize(level);
                }
            }

            return 0;
        }
    }

    /// <summary>Creates an allocator over a region.</summary>
    /// <param name="totalSize">The region size. Rounded up to a power of two.</param>
    /// <param name="minimumBlockSize">
    ///     The smallest block handed out. Rounded up to a power of two. Smaller means less waste per
    ///     allocation and more levels to walk on free; 256 bytes suits device memory.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">A size is out of range.</exception>
    public BuddyAllocator(long totalSize, long minimumBlockSize = 256) {
        ArgumentOutOfRangeException.ThrowIfLessThan(totalSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumBlockSize, 1);

        this.minimumBlockSize = (long)BitOperations.RoundUpToPowerOf2((ulong)minimumBlockSize);
        TotalSize = Math.Max((long)BitOperations.RoundUpToPowerOf2((ulong)totalSize), this.minimumBlockSize);

        levelCount = BitOperations.TrailingZeroCount((ulong)TotalSize)
            - BitOperations.TrailingZeroCount((ulong)this.minimumBlockSize)
            + 1;

        freeBlocks = new HashSet<long>[levelCount];
        for (var level = 0; level < levelCount; level++) {
            freeBlocks[level] = [];
        }

        // The whole region starts as one free block at the top level.
        freeBlocks[levelCount - 1].Add(0);
    }

    /// <summary>Reserves a range.</summary>
    /// <param name="size">How many bytes. Rounded up to a power of two.</param>
    /// <param name="alignment">
    ///     The required alignment. Satisfied for free: a block is always aligned to its own size, so
    ///     asking for more alignment than size just selects a larger block.
    /// </param>
    /// <param name="offset">The offset of the reserved range, or 0 on failure.</param>
    /// <returns><see langword="false" /> if no block large enough is free.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range.</exception>
    public bool TryAllocate(long size, long alignment, out long offset) {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(alignment, 1);

        offset = 0;

        var required = Math.Max(Math.Max(size, alignment), minimumBlockSize);
        required = (long)BitOperations.RoundUpToPowerOf2((ulong)required);

        if (required > TotalSize) {
            return false;
        }

        var level = LevelOf(required);

        // Find the smallest free block that is big enough, then halve it down to size. Each split
        // puts one half on the free list of the level below and keeps the other.
        var found = level;
        while (found < levelCount && freeBlocks[found].Count == 0) {
            found++;
        }

        if (found == levelCount) {
            return false;
        }

        var block = Take(found);

        while (found > level) {
            found--;
            var buddy = block + BlockSize(found);
            freeBlocks[found].Add(buddy);
        }

        allocations[block] = level;
        AllocatedBytes += BlockSize(level);
        offset = block;
        return true;
    }

    /// <summary>Releases a range, merging it back with its neighbour wherever it can.</summary>
    /// <param name="offset">An offset previously returned by <see cref="TryAllocate" />.</param>
    /// <returns><see langword="false" /> if the offset was not an outstanding allocation.</returns>
    public bool Free(long offset) {
        if (!allocations.Remove(offset, out var level)) {
            return false;
        }

        AllocatedBytes -= BlockSize(level);

        // A block's buddy is its offset with one bit flipped — the bit for this level's size. If the
        // buddy is also free the two combine into the block they were split from, and the same test
        // applies one level up. This is the arithmetic that makes merging cost nothing to look up.
        while (level < levelCount - 1) {
            var buddy = offset ^ BlockSize(level);

            if (!freeBlocks[level].Remove(buddy)) {
                break;
            }

            offset = Math.Min(offset, buddy);
            level++;
        }

        freeBlocks[level].Add(offset);
        return true;
    }

    /// <summary>Whether an offset is currently allocated.</summary>
    /// <param name="offset">The offset.</param>
    /// <returns><see langword="true" /> if it names an outstanding allocation.</returns>
    public bool IsAllocated(long offset) => allocations.ContainsKey(offset);

    /// <summary>How many bytes an allocation actually reserved, rounding included.</summary>
    /// <param name="offset">The offset.</param>
    /// <param name="size">The reserved size.</param>
    /// <returns><see langword="false" /> if the offset was not an outstanding allocation.</returns>
    public bool TryGetSize(long offset, out long size) {
        if (allocations.TryGetValue(offset, out var level)) {
            size = BlockSize(level);
            return true;
        }

        size = 0;
        return false;
    }

    /// <summary>Releases everything, returning the region to one free block.</summary>
    public void Reset() {
        allocations.Clear();

        foreach (var level in freeBlocks) {
            level.Clear();
        }

        freeBlocks[levelCount - 1].Add(0);
        AllocatedBytes = 0;
    }

    long BlockSize(int level) => minimumBlockSize << level;

    int LevelOf(long size) =>
        BitOperations.TrailingZeroCount((ulong)size) - BitOperations.TrailingZeroCount((ulong)minimumBlockSize);

    long Take(int level) {
        // HashSet has no "remove any", so take the first the enumerator offers. Order does not
        // matter — every block at a level is interchangeable, which is the point of the structure.
        long block = 0;
        foreach (var candidate in freeBlocks[level]) {
            block = candidate;
            break;
        }

        freeBlocks[level].Remove(block);
        return block;
    }
}
