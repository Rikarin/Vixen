// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Graphics.Vulkan;

/// <summary>One free run inside a block.</summary>
/// <param name="Offset">Where it starts.</param>
/// <param name="Size">How long it is.</param>
readonly record struct FreeRun(long Offset, long Size) {
    /// <summary>One past its last byte.</summary>
    public long End => Offset + Size;
}

/// <summary>
///     Suballocation within a single block of memory, with no Vulkan anywhere in it.
/// </summary>
/// <remarks>
///     <para>
///         A GPU allocator is one of the few places in a renderer where the interesting logic is
///         entirely arithmetic — first fit, alignment, coalescing — and none of it needs a device.
///         Split out so that fragmentation behaviour can be tested directly rather than inferred from
///         an out-of-memory three hours into a session, which is how allocator bugs usually present.
///     </para>
///     <para>
///         First fit over an address-ordered free list, coalescing on release. Not the cleverest
///         policy available; it is the one whose failure mode is easy to reason about, and the
///         alternative (buddy, or a size-bucketed pool) buys speed the RHI does not yet need and
///         costs fragmentation characteristics that are much harder to explain.
///     </para>
/// </remarks>
sealed class Suballocator {
    readonly List<FreeRun> free;

    /// <summary>Starts with everything free.</summary>
    /// <param name="size">The block's size in bytes.</param>
    public Suballocator(long size) {
        Size = size;
        free = [new(0, size)];
    }

    /// <summary>The block's size in bytes.</summary>
    public long Size { get; }

    /// <summary>How many bytes are handed out.</summary>
    public long Used { get; private set; }

    /// <summary>Whether nothing is handed out.</summary>
    public bool IsEmpty => Used == 0;

    /// <summary>The largest single allocation that would currently succeed, ignoring alignment.</summary>
    public long LargestFreeRun {
        get {
            var largest = 0L;

            foreach (var run in free) {
                largest = Math.Max(largest, run.Size);
            }

            return largest;
        }
    }

    /// <summary>Takes a run.</summary>
    /// <param name="size">How many bytes.</param>
    /// <param name="alignment">What the offset has to be a multiple of.</param>
    /// <param name="offset">Where it starts, when there was room.</param>
    /// <returns>Whether there was room.</returns>
    public bool TryAllocate(long size, long alignment, out long offset) {
        offset = 0;

        if (size <= 0) {
            return false;
        }

        for (var index = 0; index < free.Count; index++) {
            var run = free[index];
            var aligned = Align(run.Offset, alignment);
            var padding = aligned - run.Offset;

            if (run.Size - padding < size) {
                continue;
            }

            offset = aligned;
            Used += size;
            Replace(index, run, aligned, size);
            return true;
        }

        return false;
    }

    /// <summary>Returns a run.</summary>
    /// <param name="offset">Where it started.</param>
    /// <param name="size">How many bytes it was.</param>
    /// <remarks>
    ///     Coalesces with the neighbours it touches. Without that, a block that has been cycled
    ///     through a few thousand allocations is a list of ten-thousand adjacent free runs that
    ///     together could hold anything and individually can hold nothing — fragmentation that is
    ///     entirely bookkeeping rather than real.
    /// </remarks>
    public void Free(long offset, long size) {
        if (size <= 0) {
            return;
        }

        Used -= size;
        var insert = free.Count;

        for (var index = 0; index < free.Count; index++) {
            if (free[index].Offset > offset) {
                insert = index;
                break;
            }
        }

        free.Insert(insert, new(offset, size));

        // Merge forwards first: merging backwards would shift the index of the run we are about to
        // look at, which is the kind of off-by-one that shows up as a lost byte per thousand frees.
        if (insert + 1 < free.Count && free[insert].End == free[insert + 1].Offset) {
            free[insert] = new(free[insert].Offset, free[insert].Size + free[insert + 1].Size);
            free.RemoveAt(insert + 1);
        }

        if (insert > 0 && free[insert - 1].End == free[insert].Offset) {
            free[insert - 1] = new(free[insert - 1].Offset, free[insert - 1].Size + free[insert].Size);
            free.RemoveAt(insert);
        }
    }

    /// <summary>Rounds an offset up to an alignment.</summary>
    /// <param name="value">The offset.</param>
    /// <param name="alignment">The alignment, which Vulkan guarantees is a power of two.</param>
    public static long Align(long value, long alignment) =>
        alignment <= 1 ? value : (value + alignment - 1) / alignment * alignment;

    /// <summary>Replaces a free run with whatever is left of it either side of an allocation.</summary>
    void Replace(int index, FreeRun run, long allocated, long size) {
        var head = allocated - run.Offset;
        var tail = run.End - (allocated + size);

        // The padding an alignment left behind stays free rather than being folded into the
        // allocation: on a block whose resources have mixed alignments, silently absorbing it leaks
        // a little memory per allocation and never gives it back.
        if (head > 0 && tail > 0) {
            free[index] = new(run.Offset, head);
            free.Insert(index + 1, new(allocated + size, tail));
            return;
        }

        if (head > 0) {
            free[index] = new(run.Offset, head);
            return;
        }

        if (tail > 0) {
            free[index] = new(allocated + size, tail);
            return;
        }

        free.RemoveAt(index);
    }
}
