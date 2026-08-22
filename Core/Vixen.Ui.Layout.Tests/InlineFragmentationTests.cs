// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Testing;
using Xunit;

namespace Vixen.Ui.Layout.Tests;

/// <summary>
///     CSS Display §2.2's non-replaced <c>inline</c> box: one node, several boxes.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is the invariant four algorithms preserved without ever stating it, and the
///         file that breaks it on purpose.</b> A <see cref="LayoutResult" /> holds one rectangle, and
///         a <c>span</c> crossing a line break is one rectangle per line — with the horizontal border
///         and padding drawn at the two real ends and not at the breaks. Everything here is either an
///         assertion that the several boxes exist and are in the right places, or an assertion that
///         the <i>one</i>-box case did not change, which is the half that protects every existing
///         consumer of <c>GetLeft</c>.
///     </para>
///     <para>
///         ⚠ <b>There is no oracle for any of this and the arithmetic is the substitute.</b> Neither
///         Taffy nor Yoga has a single inline fixture — verified by enumeration in
///         <c>InlineKnownGaps.txt</c> — and WPT's inline suite is implicitly a font suite because a
///         line box's height comes from the strut. So every box below carries an explicit size, which
///         makes each expected number a sum of stated lengths that can be checked by hand rather than
///         a value read back off the implementation. Where a number is not obvious the comment does
///         the addition.
///     </para>
///     <para>
///         ⚠ <b>The container is padded in most of these deliberately.</b> A fragment is stored
///         relative to its own node and the node's box is the union of its fragments, so a union whose
///         origin is (0, 0) tests none of the rebasing — and rebasing is the step that, omitted, makes
///         everything right at the origin and wrong everywhere else.
///     </para>
/// </remarks>
public class InlineFragmentationTests {
    const float Tolerance = 0.0001f;

    /// <summary>
    ///     A span holding three boxes that do not fit on one line becomes two boxes, one per line.
    /// </summary>
    /// <remarks>
    ///     The container is 100 wide with 10 of padding, so its lines are 80 wide and start at x = 10.
    ///     Three 40-wide children put two on the first line (80, which fits exactly) and one on the
    ///     second. The span therefore has a fragment on each line: 80 × 20 and 40 × 20.
    /// </remarks>
    [Fact]
    public void A_span_crossing_a_line_break_becomes_one_box_per_line() {
        using var tree = new LayoutTree();
        var root = PaddedRoot(tree, width: 100f, padding: 10f);
        var span = Span(tree, root);

        Item(tree, span, 40f, 20f);
        Item(tree, span, 40f, 20f);
        Item(tree, span, 40f, 20f);

        tree.CalculateLayout(root, 100f, float.NaN, Direction.Ltr);

        Assert.Equal(2, tree.GetFragmentCount(span));

        var (firstLeft, firstTop, firstWidth, firstHeight, firstEnds) = tree.GetFragment(span, 0);
        Assert.Equal(0f, firstLeft, Tolerance);
        Assert.Equal(0f, firstTop, Tolerance);
        Assert.Equal(80f, firstWidth, Tolerance);
        Assert.Equal(20f, firstHeight, Tolerance);

        var (secondLeft, secondTop, secondWidth, secondHeight, secondEnds) = tree.GetFragment(span, 1);
        Assert.Equal(0f, secondLeft, Tolerance);
        Assert.Equal(20f, secondTop, Tolerance);
        Assert.Equal(40f, secondWidth, Tolerance);
        Assert.Equal(20f, secondHeight, Tolerance);

        // ⚠ The horizontal ends are on the fragments that really are ends, and the break between them
        // is not an edge of the box. This is the flag a painter reads to decide which vertical border
        // to stroke, and drawing both on both is what a naive fragmenter does.
        Assert.Equal(LayoutFragmentEnds.Start, firstEnds);
        Assert.Equal(LayoutFragmentEnds.End, secondEnds);
    }

    /// <summary>
    ///     The span's own rectangle is the union of its fragments, and what is inside it is measured
    ///     from that union.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The union is CSS 2.1 §10.1's answer rather than a convenience.</b> The containing
    ///     block of an absolutely positioned descendant of an inline box is the bounding box of its
    ///     first and last fragments, so the union is what the absolute walk wants — which is why this
    ///     store can put it in <c>Position</c> and leave the absolute walk alone entirely.
    /// </remarks>
    [Fact]
    public void The_spans_own_box_is_the_union_and_its_children_are_relative_to_it() {
        using var tree = new LayoutTree();
        var root = PaddedRoot(tree, width: 100f, padding: 10f);
        var span = Span(tree, root);

        var first = Item(tree, span, 40f, 20f);
        var second = Item(tree, span, 40f, 20f);
        var third = Item(tree, span, 40f, 20f);

        tree.CalculateLayout(root, 100f, float.NaN, Direction.Ltr);

        // The union starts at the container's padding origin and is as wide as the widest line.
        Assert.Equal(10f, tree.GetLeft(span), Tolerance);
        Assert.Equal(10f, tree.GetTop(span), Tolerance);
        Assert.Equal(80f, tree.GetWidth(span), Tolerance);
        Assert.Equal(40f, tree.GetHeight(span), Tolerance);

        // ⚠ And the children are relative to the span, not to the container. Both are 10 in absolute
        // terms; if the rebasing were missing, these would read 10 and the absolute walk would add the
        // span's own 10 on top of them.
        Assert.Equal(0f, tree.GetLeft(first), Tolerance);
        Assert.Equal(0f, tree.GetTop(first), Tolerance);
        Assert.Equal(40f, tree.GetLeft(second), Tolerance);
        Assert.Equal(0f, tree.GetTop(second), Tolerance);
        Assert.Equal(0f, tree.GetLeft(third), Tolerance);
        Assert.Equal(20f, tree.GetTop(third), Tolerance);

        // The container grew to hold both lines plus its own padding: 10 + 20 + 20 + 10.
        Assert.Equal(60f, tree.GetHeight(root), Tolerance);
    }

    /// <summary>
    ///     A span's horizontal padding is drawn at its two real ends and not at the break.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is the entry <c>InlineKnownGaps.txt</c> filed one level below fragmentation, and
    ///     it is the same fact.</b> The line is 100 wide. The span's <c>padding-left</c> of 12 pushes
    ///     its first child to x = 12 and is part of the first fragment; the 8 of
    ///     <c>padding-right</c> hangs off the far end of the last fragment and nothing is added at the
    ///     break. So the first line is 12 + 40 + 40 = 92 and the second is 40 + 8 = 48.
    /// </remarks>
    [Fact]
    public void Horizontal_padding_lands_on_the_real_ends_and_not_on_the_break() {
        using var tree = new LayoutTree();
        var root = PaddedRoot(tree, width: 100f, padding: 0f);
        var span = Span(tree, root);
        tree.SetPadding(span, Edge.Left, StyleLength.Points(12f));
        tree.SetPadding(span, Edge.Right, StyleLength.Points(8f));

        var first = Item(tree, span, 40f, 20f);
        Item(tree, span, 40f, 20f);
        Item(tree, span, 40f, 20f);

        tree.CalculateLayout(root, 100f, float.NaN, Direction.Ltr);

        Assert.Equal(2, tree.GetFragmentCount(span));

        var (firstLeft, _, firstWidth, _, _) = tree.GetFragment(span, 0);
        Assert.Equal(0f, firstLeft, Tolerance);
        Assert.Equal(92f, firstWidth, Tolerance);

        var (secondLeft, _, secondWidth, _, _) = tree.GetFragment(span, 1);
        Assert.Equal(0f, secondLeft, Tolerance);
        Assert.Equal(48f, secondWidth, Tolerance);

        // The padding pushed the first child in, and it is the only child it pushed.
        Assert.Equal(12f, tree.GetLeft(first), Tolerance);

        // The union is as wide as the wider of the two fragments.
        Assert.Equal(92f, tree.GetWidth(span), Tolerance);
    }

    /// <summary>A span that fits on one line is one box, and it carries both ends.</summary>
    /// <remarks>
    ///     ⚠ The control, and it is not decoration: an implementation that fragments eagerly passes
    ///     every test above and breaks every span in the engine into a fragment per child.
    /// </remarks>
    [Fact]
    public void A_span_that_fits_on_one_line_is_a_single_box_carrying_both_ends() {
        using var tree = new LayoutTree();
        var root = PaddedRoot(tree, width: 200f, padding: 0f);
        var span = Span(tree, root);

        Item(tree, span, 40f, 20f);
        Item(tree, span, 40f, 20f);

        tree.CalculateLayout(root, 200f, float.NaN, Direction.Ltr);

        Assert.Equal(1, tree.GetFragmentCount(span));

        var (left, top, width, height, ends) = tree.GetFragment(span, 0);
        Assert.Equal(0f, left, Tolerance);
        Assert.Equal(0f, top, Tolerance);
        Assert.Equal(80f, width, Tolerance);
        Assert.Equal(20f, height, Tolerance);
        Assert.Equal(LayoutFragmentEnds.Both, ends);
    }

    /// <summary>
    ///     An ordinary node — every node in the engine that is not a fragmented inline box — reports
    ///     exactly one box, and it is the box it always had.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is the compatibility assertion, and it is the reason the change was additive
    ///     rather than a migration.</b> A node with no fragment block answers "one", and that one is
    ///     <c>GetWidth</c> and <c>GetHeight</c> at offset (0, 0) — so a consumer written against the
    ///     fragment API and a consumer written against <c>GetLeft</c> agree everywhere except on the
    ///     boxes that really did split.
    /// </remarks>
    [Fact]
    public void A_node_that_did_not_fragment_reports_exactly_one_box() {
        using var tree = new LayoutTree();
        var root = PaddedRoot(tree, width: 200f, padding: 0f);
        var plain = Item(tree, root, 60f, 30f);

        tree.CalculateLayout(root, 200f, float.NaN, Direction.Ltr);

        Assert.Equal(1, tree.GetFragmentCount(plain));

        var (left, top, width, height, ends) = tree.GetFragment(plain, 0);
        Assert.Equal(0f, left, Tolerance);
        Assert.Equal(0f, top, Tolerance);
        Assert.Equal(tree.GetWidth(plain), width, Tolerance);
        Assert.Equal(tree.GetHeight(plain), height, Tolerance);
        Assert.Equal(LayoutFragmentEnds.Both, ends);

        Assert.Throws<ArgumentOutOfRangeException>(() => tree.GetFragment(plain, 1));
    }

    /// <summary>Widening the container until the span stops splitting takes the second box away.</summary>
    /// <remarks>
    ///     ⚠ <b>The direction that leaks.</b> Fragments are written by the parent's line walk, so a
    ///     span that stops fragmenting has nobody to write it an empty list unless the store clears it
    ///     first. Get this wrong and the second box is still there, still painted, on a line that no
    ///     longer exists — and only ever after a resize, which is the kind of bug that never
    ///     reproduces from a cold start.
    /// </remarks>
    [Fact]
    public void A_span_that_stops_fragmenting_stops_reporting_a_second_box() {
        using var tree = new LayoutTree();
        var root = PaddedRoot(tree, width: 100f, padding: 0f);
        var span = Span(tree, root);

        Item(tree, span, 40f, 20f);
        Item(tree, span, 40f, 20f);
        Item(tree, span, 40f, 20f);

        tree.CalculateLayout(root, 100f, float.NaN, Direction.Ltr);
        Assert.Equal(2, tree.GetFragmentCount(span));

        tree.SetDimension(root, Dimension.Width, StyleLength.Points(400f));
        tree.CalculateLayout(root, 400f, float.NaN, Direction.Ltr);

        Assert.Equal(1, tree.GetFragmentCount(span));
        Assert.Equal(120f, tree.GetWidth(span), Tolerance);
        Assert.Equal(20f, tree.GetHeight(span), Tolerance);
        Assert.Equal(LayoutFragmentEnds.Both, tree.GetFragment(span, 0).Ends);
    }

    /// <summary>A right-to-left span fragments from the right edge inwards.</summary>
    /// <remarks>
    ///     The line is 100 wide and holds two 40-wide children, so the first fragment covers the
    ///     rightmost 80 — physical left 20 — and the second covers the rightmost 40, at physical left
    ///     60. Both are then relative to the union, whose origin is the leftmost of the two.
    /// </remarks>
    [Fact]
    public void A_right_to_left_span_fragments_from_the_right_edge() {
        using var tree = new LayoutTree();
        var root = PaddedRoot(tree, width: 100f, padding: 0f);
        var span = Span(tree, root);

        Item(tree, span, 40f, 20f);
        Item(tree, span, 40f, 20f);
        Item(tree, span, 40f, 20f);

        tree.CalculateLayout(root, 100f, float.NaN, Direction.Rtl);

        Assert.Equal(2, tree.GetFragmentCount(span));

        // The union spans the whole 80 the first line used, starting 20 in from the left.
        Assert.Equal(20f, tree.GetLeft(span), Tolerance);
        Assert.Equal(80f, tree.GetWidth(span), Tolerance);

        var (firstLeft, _, firstWidth, _, _) = tree.GetFragment(span, 0);
        Assert.Equal(0f, firstLeft, Tolerance);
        Assert.Equal(80f, firstWidth, Tolerance);

        // ⚠ The second line's single child hangs off the RIGHT, which is where a line starts in RTL —
        // so its fragment is at the far end of the union, not at its origin.
        var (secondLeft, _, secondWidth, _, _) = tree.GetFragment(span, 1);
        Assert.Equal(40f, secondLeft, Tolerance);
        Assert.Equal(40f, secondWidth, Tolerance);
    }

    /// <summary>A span inside an anonymous block box still fragments across its lines.</summary>
    /// <remarks>
    ///     ⚠ <b>The two features meet here, and the assertion is about <i>boxes</i> rather than about
    ///     either mechanism firing.</b> An anonymous block box (§9.2.1.1) is a line walk over a
    ///     sub-range of a mixed container's children; a fragmenting span (Display §2.2) is one node
    ///     producing several boxes on that walk. Nothing had to be taught about the combination — the
    ///     run is flowed by the same <c>WalkInlineLines</c>, so the span's fragments come out in the
    ///     container's coordinates and are rebased onto the span exactly as they are without a block
    ///     sibling. What this test would catch is the run being flowed from the container's top inset
    ///     instead of from the anonymous box's, which every fragment's <c>Top</c> would show.
    /// </remarks>
    [Fact]
    public void A_span_inside_an_anonymous_block_box_still_fragments() {
        using var tree = new LayoutTree();
        var root = PaddedRoot(tree, width: 100f, padding: 0f);

        var head = BlockBox(tree, root, width: 100f, height: 10f);
        var span = Span(tree, root);

        Item(tree, span, 40f, 20f);
        Item(tree, span, 40f, 20f);
        Item(tree, span, 40f, 20f);

        tree.CalculateLayout(root, 100f, float.NaN, Direction.Ltr);

        Assert.Equal(0f, tree.GetTop(head), Tolerance);

        // Two of the three fit on a hundred-point line, so the span is two boxes — and the anonymous
        // box holding it starts ten points down, under the block-level sibling.
        Assert.Equal(2, tree.GetFragmentCount(span));
        Assert.Equal(10f, tree.GetTop(span), Tolerance);
        Assert.Equal(40f, tree.GetHeight(span), Tolerance);

        var (firstLeft, firstTop, firstWidth, firstHeight, firstEnds) = tree.GetFragment(span, 0);
        Assert.Equal((0f, 0f, 80f, 20f), (firstLeft, firstTop, firstWidth, firstHeight));
        Assert.Equal(LayoutFragmentEnds.Start, firstEnds);

        var (secondLeft, secondTop, secondWidth, secondHeight, secondEnds) = tree.GetFragment(span, 1);
        Assert.Equal((0f, 20f, 40f, 20f), (secondLeft, secondTop, secondWidth, secondHeight));
        Assert.Equal(LayoutFragmentEnds.End, secondEnds);

        Assert.Equal(50f, tree.GetHeight(root), Tolerance);
    }

    /// <summary>
    ///     Re-laying a mixed container every frame allocates nothing either.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>An anonymous block box is one more caller of the watermarked stream, and a second
    ///     caller is exactly how a watermark gets broken.</b> A mixed container calls
    ///     <c>WalkInlineLines</c> once per run rather than once per container, so a restore that
    ///     rewound to the wrong base — or a run that abandoned an open box without committing its
    ///     fragments — would show up as an arena growing a little on every frame rather than as a
    ///     wrong number anywhere. This tree has two runs with a block-level box between them and a
    ///     span fragmenting inside each, which is the shape that exercises both watermarks.
    /// </remarks>
    [Fact]
    public void A_mixed_container_re_laid_every_frame_allocates_nothing() {
        using var tree = new LayoutTree();
        var root = PaddedRoot(tree, width: 100f, padding: 0f);

        var leading = Span(tree, root);
        BlockBox(tree, root, width: 100f, height: 10f);
        var trailing = Span(tree, root);

        for (var i = 0; i < 5; i++) {
            Item(tree, leading, 40f, 20f);
            Item(tree, trailing, 40f, 20f);
        }

        var toggle = tree.GetChild(leading, 0);
        var frame = 0;

        Assert.Equal(0, Measured.Bytes(Layout, warmUp: 20, passes: 200));

        return;

        void Layout() {
            tree.SetDimension(toggle, Dimension.Height, StyleLength.Points(20f + (frame++ % 3)));
            tree.CalculateLayout(root, 100f, float.NaN, Direction.Ltr);
        }
    }

    /// <summary>
    ///     Re-laying a tree with a fragmenting span in it every frame allocates nothing.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The gate <c>LayoutPassTests</c> holds for flex, held for the thing that made a line
    ///     stop being a range of children.</b> A fragmented span rewrites its fragments on every
    ///     single pass, so an arena that grew on each write would fail this within twenty frames — and
    ///     the two scratch buffers behind the flattened stream would too if they were not watermarked
    ///     and reused. This is the test that says the representation is affordable, which was the
    ///     whole argument for a side arena over a list per node.
    /// </remarks>
    [Fact]
    public void A_fragmenting_span_re_laid_every_frame_allocates_nothing() {
        using var tree = new LayoutTree();
        var root = PaddedRoot(tree, width: 100f, padding: 0f);
        var span = Span(tree, root);

        for (var i = 0; i < 9; i++) {
            Item(tree, span, 40f, 20f);
        }

        var toggle = tree.GetChild(span, 0);
        var frame = 0;

        Assert.Equal(0, Measured.Bytes(Layout, warmUp: 20, passes: 200));

        return;

        void Layout() {
            tree.SetDimension(toggle, Dimension.Height, StyleLength.Points(20f + (frame++ % 3)));
            tree.CalculateLayout(root, 100f, float.NaN, Direction.Ltr);
        }
    }

    /// <summary>
    ///     ⚠ <b>Scope, pinned so that it fails the day it is lifted.</b> A non-atomic inline box
    ///     inside another one is laid out atomically — one level of flattening, not a recursion.
    /// </summary>
    /// <remarks>
    ///     This is a limit of the producer and not of the representation: the arena holds any number
    ///     of fragments for any node, and what a nested span needs is the rebasing of a union inside a
    ///     union. Written as an assertion rather than a comment so that the entry in
    ///     <c>InlineKnownGaps.txt</c> has something holding it honest — when nesting lands, this test
    ///     goes red and gets inverted, exactly as
    ///     <c>Mixed_content_stacks_because_there_are_no_anonymous_boxes</c> did when anonymous boxes
    ///     landed and became
    ///     <c>InlineFormattingTests.Mixed_content_wraps_each_inline_run_in_an_anonymous_block_box</c>.
    /// </remarks>
    [Fact]
    public void A_span_inside_a_span_is_still_atomic() {
        using var tree = new LayoutTree();
        var root = PaddedRoot(tree, width: 100f, padding: 0f);
        var outer = Span(tree, root);
        var inner = Span(tree, outer);

        Item(tree, inner, 40f, 20f);
        Item(tree, inner, 40f, 20f);
        Item(tree, inner, 40f, 20f);

        tree.CalculateLayout(root, 100f, float.NaN, Direction.Ltr);

        // The inner span ran its own inline formatting context and came back as one box, so the outer
        // one has nothing to split around and is one box too.
        Assert.Equal(1, tree.GetFragmentCount(inner));
        Assert.Equal(1, tree.GetFragmentCount(outer));
    }

    /// <summary>
    ///     ⚠ <b>A span with an out-of-flow child is refused, and the child is the reason.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The absolute walk descends from the node it was called on and recurses only through
    ///         <see cref="PositionType.Static" /> children — and this store's default is
    ///         <see cref="PositionType.Relative" />, which is Yoga's default and not CSS's. So a
    ///         flattened span, whose own <c>CalculateLayoutImpl</c> never runs, is exactly where that
    ///         walk stops.
    ///     </para>
    ///     <para>
    ///         ⚠ The assertion that matters is the second one. Refusing to flatten leaves a span
    ///         un-split, which is a visible imperfection; flattening it anyway leaves the
    ///         absolutely positioned child sized, given a static position, and then positioned by
    ///         nobody — at whatever coordinates the previous pass left on it. A child that quietly
    ///         does not move is worse than a box that quietly does not split.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_span_with_an_out_of_flow_child_is_not_flattened_and_the_child_is_still_placed() {
        using var tree = new LayoutTree();
        var root = PaddedRoot(tree, width: 100f, padding: 0f);
        var span = Span(tree, root);

        Item(tree, span, 40f, 20f);
        Item(tree, span, 40f, 20f);
        Item(tree, span, 40f, 20f);

        var floating = Item(tree, span, 10f, 10f);
        tree.SetPositionType(floating, PositionType.Absolute);
        tree.SetPosition(floating, Edge.Left, StyleLength.Points(5f));
        tree.SetPosition(floating, Edge.Top, StyleLength.Points(7f));

        tree.CalculateLayout(root, 100f, float.NaN, Direction.Ltr);

        Assert.Equal(1, tree.GetFragmentCount(span));

        Assert.Equal(5f, tree.GetLeft(floating), Tolerance);
        Assert.Equal(7f, tree.GetTop(floating), Tolerance);
    }

    /// <summary>A span's own <c>position: relative</c> offset moves it and everything inside it.</summary>
    /// <remarks>
    ///     ⚠ Applied to the union rather than to each fragment, which is the only reading that is
    ///     right: the children are already expressed relative to the union's origin, so shifting each
    ///     fragment instead would move the boxes and leave their contents behind — visible only as a
    ///     background that has slid out from under its own text.
    /// </remarks>
    [Fact]
    public void A_relative_offset_on_a_fragmented_span_moves_the_union_and_not_the_fragments() {
        using var tree = new LayoutTree();
        var root = PaddedRoot(tree, width: 100f, padding: 0f);
        var span = Span(tree, root);
        tree.SetPositionType(span, PositionType.Relative);
        tree.SetPosition(span, Edge.Left, StyleLength.Points(5f));
        tree.SetPosition(span, Edge.Top, StyleLength.Points(3f));

        var first = Item(tree, span, 40f, 20f);
        Item(tree, span, 40f, 20f);
        Item(tree, span, 40f, 20f);

        tree.CalculateLayout(root, 100f, float.NaN, Direction.Ltr);

        Assert.Equal(2, tree.GetFragmentCount(span));

        // The box moved.
        Assert.Equal(5f, tree.GetLeft(span), Tolerance);
        Assert.Equal(3f, tree.GetTop(span), Tolerance);

        // The fragments and the contents did not move relative to it, so they move with it.
        Assert.Equal(0f, tree.GetFragment(span, 0).Left, Tolerance);
        Assert.Equal(0f, tree.GetFragment(span, 0).Top, Tolerance);
        Assert.Equal(0f, tree.GetLeft(first), Tolerance);
        Assert.Equal(0f, tree.GetTop(first), Tolerance);
    }

    /// <summary>Every fragment is snapped to the device pixel grid, like every other box.</summary>
    /// <remarks>
    ///     ⚠ <b>Worth its own test because the whole-number cases above cannot fail it.</b> At the
    ///     default scale of one, with sizes like 40 and 20, rounding is the identity — so a fragment
    ///     that was never rounded at all would pass every assertion in this file. Here the sizes are
    ///     40.3 and 10.1 at two device pixels per point, so an unrounded fragment reports 10.1 where
    ///     the grid says 10 and a fragment rounded on its own <i>size</i> rather than on its absolute
    ///     edges drifts off the half-point grid — which is the seam the rounding pass exists to stop,
    ///     and it is most visible between two fragments of the same span.
    /// </remarks>
    [Fact]
    public void Fragments_are_snapped_to_the_pixel_grid() {
        using var tree = new LayoutTree();
        var root = PaddedRoot(tree, width: 100f, padding: 0f);
        tree.PointScaleFactor = 2f;

        var span = Span(tree, root);

        Item(tree, span, 40.3f, 10.1f);
        Item(tree, span, 40.3f, 10.1f);
        Item(tree, span, 40.3f, 10.1f);

        tree.CalculateLayout(root, 100f, float.NaN, Direction.Ltr);

        Assert.Equal(2, tree.GetFragmentCount(span));

        for (var i = 0; i < 2; i++) {
            var (left, top, width, height, _) = tree.GetFragment(span, i);

            Assert.Equal(0f, left * 2f % 1f, Tolerance);
            Assert.Equal(0f, top * 2f % 1f, Tolerance);
            Assert.Equal(0f, width * 2f % 1f, Tolerance);
            Assert.Equal(0f, height * 2f % 1f, Tolerance);
        }

        // ⚠ And it actually moved: the second line's raw top is 10.1, which is not on the grid.
        Assert.Equal(10f, tree.GetFragment(span, 1).Top, Tolerance);
    }

    static LayoutNodeId PaddedRoot(LayoutTree tree, float width, float padding) {
        var root = tree.CreateNode();
        tree.SetDisplay(root, Display.Block);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(width));

        if (padding > 0f) {
            tree.SetPadding(root, Edge.Left, StyleLength.Points(padding));
            tree.SetPadding(root, Edge.Right, StyleLength.Points(padding));
            tree.SetPadding(root, Edge.Top, StyleLength.Points(padding));
            tree.SetPadding(root, Edge.Bottom, StyleLength.Points(padding));
        }

        return root;
    }

    /// <summary>A non-replaced <c>inline</c> box — the thing that fragments.</summary>
    static LayoutNodeId Span(LayoutTree tree, LayoutNodeId parent) {
        var node = tree.CreateNode();
        tree.SetDisplay(node, Display.Inline);
        tree.AddChild(parent, node);

        return node;
    }

    /// <summary>A block-level sibling — what makes a container's content <i>mixed</i>.</summary>
    static LayoutNodeId BlockBox(LayoutTree tree, LayoutNodeId parent, float width, float height) {
        var node = tree.CreateNode();
        tree.SetDisplay(node, Display.Block);
        tree.SetDimension(node, Dimension.Width, StyleLength.Points(width));
        tree.SetDimension(node, Dimension.Height, StyleLength.Points(height));
        tree.AddChild(parent, node);

        return node;
    }

    /// <summary>An atomic inline-level box with an explicit size, so no number depends on a font.</summary>
    static LayoutNodeId Item(LayoutTree tree, LayoutNodeId parent, float width, float height) {
        var node = tree.CreateNode();
        tree.SetDisplay(node, Display.InlineBlock);
        tree.SetDimension(node, Dimension.Width, StyleLength.Points(width));
        tree.SetDimension(node, Dimension.Height, StyleLength.Points(height));
        tree.AddChild(parent, node);

        return node;
    }
}
