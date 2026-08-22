// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Layout;

/// <summary>Reading and writing the boxes of a node that produced more than one.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The invariant this relaxes was never written down, which is exactly why it held for
///         four algorithms: one node produces one box.</b> A flex item, a block-level child and a grid
///         item are each one rectangle, so a node's geometry was four floats at a known offset. CSS
///         Display §2.2's non-replaced <c>inline</c> box is not: a <c>span</c> crossing a line break is
///         one box per line, with the horizontal border and padding drawn at the two real ends and not
///         at the breaks.
///     </para>
///     <para>
///         ⚠ <b>The relaxation is deliberately one-directional: a node may have <i>more</i> boxes than
///         one, and it may not have <i>fewer</i> or <i>none</i>.</b> That matters because the two
///         things people put in the same sentence as this invariant are not the same problem.
///         Fragmentation is one node to many boxes and lives here. An <b>anonymous</b> block box
///         (§9.2.1.1) and a <b>generated</b> box (<c>::before</c>, doc 43's A12) are the other
///         direction — a box with no node behind it — and neither is served by this file. The
///         difference is not pedantry, and it is what let one of the two land without touching this
///         arena at all: an anonymous block box takes initial values for every non-inherited
///         property, so it has no background, no border and no event target and never needs to be
///         stored — it is a line walk over a sub-range of a container's children, and it lives in
///         <c>LayoutTree.Block</c> and <c>LayoutTree.Inline</c>. A generated box has a style of its
///         own, which is a second style slot rather than a second rectangle, and is still open.
///         Storing either as a fragment of its originating node would give it that node's style,
///         which is wrong for both. See <c>InlineKnownGaps.txt</c>.
///     </para>
/// </remarks>
public sealed partial class LayoutTree {
    /// <summary>How many boxes this node was laid out as.</summary>
    /// <param name="node">The node.</param>
    /// <returns>
    ///     One for the ordinary case, or the number of fragments for a box that crossed a line break.
    ///     Never zero for a laid-out node.
    /// </returns>
    /// <remarks>
    ///     ⚠ <b>One rather than zero for an unfragmented node, so that a consumer has one shape of
    ///     loop rather than two.</b> The store distinguishes "no fragment block" from "one fragment"
    ///     internally because the first costs nothing; a caller should not have to, and a caller that
    ///     did would be the caller that forgets and paints the union of a two-line span as one
    ///     rectangle across the middle of the paragraph.
    /// </remarks>
    public int GetFragmentCount(LayoutNodeId node) {
        var index = Validate(node);

        return results[index].FragmentCount == 0 ? 1 : results[index].FragmentCount;
    }

    /// <summary>One of the boxes this node was laid out as.</summary>
    /// <param name="node">The node.</param>
    /// <param name="fragment">Which one, from 0 to <see cref="GetFragmentCount" /> exclusive.</param>
    /// <returns>
    ///     Its rounded rectangle, as an offset from the node's own border-box origin, together with
    ///     which of the box's real ends it carries.
    /// </returns>
    /// <remarks>
    ///     ⚠ <b>Relative to the node, not to the node's parent, and the asymmetry with
    ///     <see cref="GetLeft" /> is deliberate.</b> A consumer reaches a fragment by walking to the
    ///     node first — that is what painting and hit testing both already do, accumulating an
    ///     absolute offset as they descend — so a fragment offset that is relative to the node adds
    ///     to what the walk already has. Relative to the parent it would have to be un-added first,
    ///     and the single-fragment case would stop being (0, 0).
    /// </remarks>
    public (float Left, float Top, float Width, float Height, LayoutFragmentEnds Ends) GetFragment(
        LayoutNodeId node,
        int fragment
    ) {
        var index = Validate(node);
        var count = results[index].FragmentCount;

        if (count == 0) {
            return fragment == 0
                ? (0f, 0f, results[index].RoundedDimensions[(int) Dimension.Width],
                    results[index].RoundedDimensions[(int) Dimension.Height], LayoutFragmentEnds.Both)
                : throw new ArgumentOutOfRangeException(nameof(fragment));
        }

        if ((uint) fragment >= (uint) count) {
            throw new ArgumentOutOfRangeException(nameof(fragment));
        }

        ref var box = ref fragments.Slice(results[index].FragmentOffset, count)[fragment];

        return (box.RoundedLeft, box.RoundedTop, box.RoundedWidth, box.RoundedHeight, box.Ends);
    }

    /// <summary>This node's fragments, or an empty span for the ordinary one-box case.</summary>
    internal Span<LayoutFragment> FragmentsOf(int index) =>
        fragments.Slice(results[index].FragmentOffset, results[index].FragmentCount);

    /// <summary>Replaces a node's fragments, or clears them when the run is empty.</summary>
    internal void WriteFragments(int index, ReadOnlySpan<LayoutFragment> run) {
        if (run.IsEmpty && results[index].FragmentCount == 0) {
            return;
        }

        var (offset, capacity) = fragments.Write(results[index].FragmentOffset, results[index].FragmentCapacity, run);

        results[index].FragmentOffset = offset;
        results[index].FragmentCapacity = capacity;
        results[index].FragmentCount = run.Length;
    }

    /// <summary>Hands a node's fragment block back to the arena.</summary>
    void ReleaseFragments(int index) {
        fragments.Free(results[index].FragmentOffset, results[index].FragmentCapacity);
        results[index].FragmentOffset = -1;
        results[index].FragmentCapacity = 0;
        results[index].FragmentCount = 0;
    }
}
