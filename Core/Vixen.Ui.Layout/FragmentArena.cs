// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Numerics;

namespace Vixen.Ui.Layout;

/// <summary>Every fragmented node's extra boxes, in one array.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Grid needed variable-length <i>input</i> and got <see cref="TrackArena" />; this is the
///         same shape on the <i>output</i> side, and it is the third and last thing the store has had
///         to grow for an algorithm.</b> Block cost three output fields, grid cost an input arena,
///         inline cost one output field — and then this, because CSS Display §2.2 lets one node
///         produce an arbitrary number of boxes and <see cref="LayoutResult" /> is a fixed-size
///         unmanaged struct in a <c>NativeArray</c> precisely so that a hundred thousand nodes are a
///         handful of allocations.
///     </para>
///     <para>
///         ⚠ <b>A node that does not fragment pays one <c>-1</c> and never touches this.</b> That is
///         not a micro-optimisation, it is what keeps the change additive: <c>FragmentCount == 0</c>
///         means "one box, and it is <see cref="LayoutResult.Position" /> as it has always been", so
///         every existing consumer of <c>GetLeft</c> keeps reading exactly what it read before and no
///         caller anywhere had to change. Fragmentation is rare — it needs a non-atomic inline box
///         that actually crosses a line — so the overwhelmingly common answer stays the cheap one.
///     </para>
///     <para>
///         ⚠ <b>Blocks are reused on a same-or-smaller rewrite, and that is load-bearing rather than
///         tidy.</b> <c>LayoutPassTests</c> asserts that re-laying a tree every frame allocates
///         <i>zero</i> bytes, and a fragmented span re-fragments on every single pass. Growing the
///         arena each time would fail that gate within twenty frames. So the steady state — same
///         span, same two fragments, new numbers — writes into the block it already has and touches
///         neither the free lists nor the watermark.
///     </para>
/// </remarks>
sealed class FragmentArena {
    const int MinimumBlock = 2;
    const int MinimumBlockLog2 = 1;
    const int BucketCount = 24;

    readonly List<int>?[] free = new List<int>?[BucketCount];
    LayoutFragment[] storage = new LayoutFragment[16];
    int used;

    /// <summary>The fragments in a block.</summary>
    /// <param name="offset">Where the block starts, or -1 for a node that did not fragment.</param>
    /// <param name="length">How many fragments to take.</param>
    /// <returns>The fragments, or an empty span.</returns>
    /// <remarks>
    ///     Mutable, because the rounding pass writes each fragment's rounded rectangle back into it —
    ///     the same two-rectangles-per-box arrangement <see cref="LayoutResult" /> has. The span points
    ///     into the arena, which moves when it grows; never hold one across a write.
    /// </remarks>
    public Span<LayoutFragment> Slice(int offset, int length) =>
        offset < 0 || length <= 0 ? default : storage.AsSpan(offset, length);

    /// <summary>Replaces a node's block with a new run, reusing the block when it still fits.</summary>
    /// <param name="offset">The current block, or -1.</param>
    /// <param name="capacity">How big it currently is.</param>
    /// <param name="fragments">What to store.</param>
    /// <returns>Where the block now is and how big it now is.</returns>
    public (int Offset, int Capacity) Write(int offset, int capacity, ReadOnlySpan<LayoutFragment> fragments) {
        if (fragments.IsEmpty) {
            Free(offset, capacity);
            return (-1, 0);
        }

        if (offset >= 0 && capacity >= fragments.Length) {
            fragments.CopyTo(storage.AsSpan(offset, fragments.Length));
            return (offset, capacity);
        }

        Free(offset, capacity);

        var wanted = int.Max(MinimumBlock, (int) BitOperations.RoundUpToPowerOf2((uint) fragments.Length));
        var next = Allocate(wanted);
        fragments.CopyTo(storage.AsSpan(next, fragments.Length));

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
