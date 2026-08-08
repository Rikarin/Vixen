// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Layout;

/// <summary>One track, as CSS Grid §12.1's algorithm carries it.</summary>
/// <remarks>
///     ⚠ <b>A base size and a growth limit, not a size.</b> §12 never computes a track's size in one
///     go: it grows a floor and a ceiling towards each other through five numbered phases, and the
///     used size only exists at the end. The two increase fields are §12.5.1's scratch — space is
///     planned across a set of tracks and only committed once the whole set has been considered,
///     because distributing it as you go makes the result depend on the order the items were visited.
/// </remarks>
struct GridTrackState {
    /// <summary>The sizing function this track was given.</summary>
    public GridTrackSize Size;

    /// <summary>§12.1's base size: the floor, which only ever grows.</summary>
    public float BaseSize;

    /// <summary>§12.1's growth limit: the ceiling, which may be infinite.</summary>
    public float GrowthLimit;

    /// <summary>§12.5.1's planned increase, held back until the whole set is known.</summary>
    public float PlannedIncrease;

    /// <summary>§12.5.1's per-item increase, folded into the planned one after each item.</summary>
    public float ItemIncurredIncrease;

    /// <summary>Whether <c>auto-fit</c> emptied this track, per §7.2.3.2.</summary>
    public bool IsCollapsed;

    /// <summary>Whether this track was marked as a candidate by the current distribution step.</summary>
    public bool IsMarked;

    /// <summary>Where the track's start edge ends up, relative to the container's content box.</summary>
    public float Offset;

    /// <summary>The used size, once §12 has finished.</summary>
    public readonly float UsedSize => BaseSize;
}

/// <summary>One in-flow child of a grid container, and the area it was placed into.</summary>
/// <remarks>
///     Spans are stored as a start and a count in final track coordinates — after §8's implicit
///     tracks have been prepended, so index 0 really is the first track and no consumer has to know
///     that the explicit grid started somewhere else.
/// </remarks>
struct GridItem {
    /// <summary>The child's node index.</summary>
    public int Node;

    /// <summary>The first column track the item occupies.</summary>
    public int ColumnStart;

    /// <summary>How many column tracks it occupies. Always at least one.</summary>
    public int ColumnSpan;

    /// <summary>The first row track the item occupies.</summary>
    public int RowStart;

    /// <summary>How many row tracks it occupies. Always at least one.</summary>
    public int RowSpan;

    /// <summary>The inline size the item was given once its columns were sized.</summary>
    public float ResolvedInlineSize;

    /// <summary>One past the last column.</summary>
    public readonly int ColumnEnd => ColumnStart + ColumnSpan;

    /// <summary>One past the last row.</summary>
    public readonly int RowEnd => RowStart + RowSpan;

    /// <summary>The item's start on one axis.</summary>
    /// <param name="inline">Whether the inline axis is wanted.</param>
    /// <returns>The first track.</returns>
    public readonly int StartOn(bool inline) => inline ? ColumnStart : RowStart;

    /// <summary>The item's span on one axis.</summary>
    /// <param name="inline">Whether the inline axis is wanted.</param>
    /// <returns>How many tracks.</returns>
    public readonly int SpanOn(bool inline) => inline ? ColumnSpan : RowSpan;
}

/// <summary>The per-pass working storage grid layout needs, reused across frames.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A bump allocator with a watermark, because a grid can contain a grid.</b> Track
///         sizing measures its items, and measuring an item runs the whole algorithm on it — which
///         may be another grid container wanting working storage of its own, while the outer one's
///         is still live. A single shared buffer would be overwritten underneath the outer pass. So
///         each entry takes a <see cref="Mark" />, allocates above it, and restores on the way out.
///     </para>
///     <para>
///         ⚠ <b>Indices, not spans, and that is not a stylistic choice.</b> The backing arrays grow
///         when an inner grid asks for more room than is left, and a <c>Span</c> taken before that
///         happens points into the array that was replaced. Every access therefore goes through
///         <see cref="Track" /> and <see cref="Item" />, which re-read the field. This is the same
///         hazard, and the same answer, as <see cref="ChildArena.Slice" />'s "never hold one across
///         an insertion".
///     </para>
///     <para>
///         The store's steady-state allocation gate is what makes this a pool rather than a
///         <c>new[]</c> per pass: a tree that laid a grid out last frame lays it out this frame
///         without allocating, which is the same bargain the child arena and the measure cache made.
///     </para>
/// </remarks>
sealed class GridScratch {
    GridTrackState[] trackStore = new GridTrackState[64];
    GridItem[] itemStore = new GridItem[32];
    int[] spanStore = new int[32];

    int tracksUsed;
    int itemsUsed;
    int spansUsed;

    /// <summary>Where the three stacks currently stand.</summary>
    public (int Tracks, int Items, int Spans) Mark => (tracksUsed, itemsUsed, spansUsed);

    /// <summary>Gives everything allocated since a mark back.</summary>
    /// <param name="mark">What <see cref="Mark" /> returned on the way in.</param>
    public void Restore((int Tracks, int Items, int Spans) mark) =>
        (tracksUsed, itemsUsed, spansUsed) = mark;

    /// <summary>Reserves a run of tracks, zeroed.</summary>
    /// <param name="count">How many.</param>
    /// <returns>Where the run starts.</returns>
    public int AllocateTracks(int count) {
        var at = tracksUsed;
        Ensure(ref trackStore, at + count);
        trackStore.AsSpan(at, count).Clear();
        tracksUsed = at + count;

        return at;
    }

    /// <summary>Reserves a run of items, zeroed.</summary>
    /// <param name="count">How many.</param>
    /// <returns>Where the run starts.</returns>
    public int AllocateItems(int count) {
        var at = itemsUsed;
        Ensure(ref itemStore, at + count);
        itemStore.AsSpan(at, count).Clear();
        itemsUsed = at + count;

        return at;
    }

    /// <summary>Reserves a run of integers, zeroed. §12.5's per-item track lists live here.</summary>
    /// <param name="count">How many.</param>
    /// <returns>Where the run starts.</returns>
    public int AllocateSpans(int count) {
        var at = spansUsed;
        Ensure(ref spanStore, at + count);
        spanStore.AsSpan(at, count).Clear();
        spansUsed = at + count;

        return at;
    }

    /// <summary>One track.</summary>
    /// <param name="at">Its absolute index.</param>
    /// <returns>A reference to it.</returns>
    public ref GridTrackState Track(int at) => ref trackStore[at];

    /// <summary>One item.</summary>
    /// <param name="at">Its absolute index.</param>
    /// <returns>A reference to it.</returns>
    public ref GridItem Item(int at) => ref itemStore[at];

    /// <summary>One integer.</summary>
    /// <param name="at">Its absolute index.</param>
    /// <returns>A reference to it.</returns>
    public ref int Span(int at) => ref spanStore[at];

    static void Ensure<T>(ref T[] array, int wanted) {
        if (array.Length >= wanted) {
            return;
        }

        Array.Resize(ref array, int.Max(array.Length * 2, wanted));
    }
}
