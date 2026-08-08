// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Numerics;

namespace Vixen.Ui.Layout;

/// <summary>Every node's grid track lists, in one array.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is the one thing grid needed from the store that neither flex nor block did, and
///         it is on the <i>input</i> side.</b> Block's whole cost was three output fields — a
///         collapsible margin at each end and a flag — because a block child has to report what
///         escaped past its edges. Grid reports nothing unusual upward. What it needs is a style field
///         that is not a fixed number of bytes: <c>grid-template-columns</c> is an arbitrary-length
///         list of sizing functions, and <see cref="LayoutStyle" /> is an unmanaged struct in a
///         <c>NativeArray</c> precisely so that a hundred thousand nodes are four allocations.
///     </para>
///     <para>
///         So the four track lists live here and the style carries a
///         <see cref="GridTemplate">(offset, count)</see> into it — which is the same shape, and for
///         the same reason, as <see cref="ChildArena" />. A <c>List&lt;GridTrackSize&gt;</c> per node
///         would be four heap objects per node in a tree where almost every node has no grid
///         properties at all, and that allocation profile is the one ADR-006 rejected the reference
///         flexbox port for. A node that sets no template pays one <c>-1</c>.
///     </para>
///     <para>
///         ⚠ <b>It has to hold rather more than a stylesheet would suggest.</b>
///         <c>repeat(40000, 10px 10px)</c> is a legal declaration and the corpus contains it, so a
///         single expanded list can be eighty thousand entries — which is why fixed repetitions are
///         expanded once, here, at the moment the style is written, rather than on every layout pass.
///         <see cref="LayoutLimits.MaximumGridTracks" /> is what stops such a list from being
///         unbounded, and it is the same clamp Chrome applies.
///     </para>
/// </remarks>
sealed class TrackArena {
    const int MinimumBlock = 4;
    const int MinimumBlockLog2 = 2;
    const int BucketCount = 28;

    readonly List<int>?[] free = new List<int>?[BucketCount];
    GridTrackSize[] storage = new GridTrackSize[64];
    int used;

    /// <summary>The tracks in a block.</summary>
    /// <param name="offset">Where the block starts, or -1 for a node with no block.</param>
    /// <param name="length">How many tracks to take.</param>
    /// <returns>The tracks, or an empty span.</returns>
    /// <remarks>
    ///     The span points into the arena, which moves when it grows. Never hold one across a write.
    /// </remarks>
    public ReadOnlySpan<GridTrackSize> Slice(int offset, int length) =>
        offset < 0 || length <= 0 ? default : storage.AsSpan(offset, length);

    /// <summary>Replaces a node's block with a new list, reusing the block when it still fits.</summary>
    /// <param name="offset">The current block, or -1.</param>
    /// <param name="capacity">How big it currently is.</param>
    /// <param name="tracks">What to store.</param>
    /// <returns>Where the block now is and how big it now is.</returns>
    /// <remarks>
    ///     ⚠ Reusing the block on a same-or-smaller write is what keeps re-assigning a template every
    ///     frame — which a stylesheet-driven consumer does — from walking the arena forward forever.
    ///     The free lists only recycle blocks a node has actually given up.
    /// </remarks>
    public (int Offset, int Capacity) Write(int offset, int capacity, ReadOnlySpan<GridTrackSize> tracks) {
        if (tracks.IsEmpty) {
            Free(offset, capacity);
            return (-1, 0);
        }

        if (offset >= 0 && capacity >= tracks.Length) {
            tracks.CopyTo(storage.AsSpan(offset, tracks.Length));
            return (offset, capacity);
        }

        Free(offset, capacity);

        var wanted = int.Max(MinimumBlock, (int) BitOperations.RoundUpToPowerOf2((uint) tracks.Length));
        var next = Allocate(wanted);
        tracks.CopyTo(storage.AsSpan(next, tracks.Length));

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

/// <summary>A node's track list on one axis: where it is, and where its automatic repetition sits.</summary>
/// <remarks>
///     ⚠ <b>A fixed <c>repeat()</c> is expanded on write and an automatic one cannot be.</b>
///     <c>repeat(3, 40px)</c> is three tracks the moment it is parsed; <c>repeat(auto-fill, 40px)</c>
///     is however many fit, which is not known until the container has a size — and on an indefinite
///     container it is one. So the stored list holds one repetition of the automatic part inline,
///     marked by <see cref="AutoRepeatIndex" />, and §7.2.3.2's count is worked out per pass.
///     CSS admits at most one automatic repetition per axis, which is what makes a single index
///     enough.
/// </remarks>
struct GridTemplate {
    /// <summary>Where this node's tracks start in the arena, or -1.</summary>
    public int Offset;

    /// <summary>How many tracks are stored.</summary>
    public int Count;

    /// <summary>How big the block is.</summary>
    public int Capacity;

    /// <summary>Where the single stored repetition begins, or -1 when there is no automatic repeat.</summary>
    public int AutoRepeatIndex;

    /// <summary>How many tracks one repetition holds.</summary>
    public int AutoRepeatCount;

    /// <summary>Which kind of automatic repetition this is.</summary>
    public GridAutoRepeat AutoRepeatKind;

    /// <summary>An unset template.</summary>
    public static GridTemplate Empty => new() { Offset = -1, AutoRepeatIndex = -1 };

    /// <summary>Whether anything was written.</summary>
    public readonly bool IsDefined => Count > 0;
}
