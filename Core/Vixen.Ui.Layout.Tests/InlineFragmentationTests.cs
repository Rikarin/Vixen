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
///         ⚠ <b>Four cases at the end of the file are the exception, and had to be.</b> Whether an
///         inline box's closing edge takes part in the fit test for the item before it is a question
///         arithmetic cannot answer — both answers are self-consistent and they disagree about where
///         a line breaks — so those four were read out of Chrome 148.0.7778.280 the way
///         <see cref="InlineFloatInteractionTests" /> reads its numbers, with the same
///         <c>font-size: 0; line-height: 0</c> fixture that takes §10.8's strut out of a Chrome line
///         box. They are named individually below.
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

        Measured.NothingAllocated(Layout, warmUp: 20, passes: 200);

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

        Measured.NothingAllocated(Layout, warmUp: 20, passes: 200);

        return;

        void Layout() {
            tree.SetDimension(toggle, Dimension.Height, StyleLength.Points(20f + (frame++ % 3)));
            tree.CalculateLayout(root, 100f, float.NaN, Direction.Ltr);
        }
    }

    /// <summary>
    ///     ⚠ <b>A span inside a span fragments too, and this test is the inverted form of the one
    ///     that used to pin the opposite.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The premise moved rather than the assertion being weakened, and it is worth saying
    ///         which premise.</b> This was <c>A_span_inside_a_span_is_still_atomic</c>, whose own
    ///         remark said it would go red and be inverted the day nesting landed — the same
    ///         arrangement <c>Mixed_content_stacks_because_there_are_no_anonymous_boxes</c> had before
    ///         anonymous boxes. What it blamed was wrong: it said the missing piece was "the rebasing
    ///         of a union inside a union", and that was already free, because an inner box commits
    ///         first and the outer's commit then rebases it like any other child. What actually held
    ///         was the fragment scratch — one box's fragments were a contiguous slice, and two boxes
    ///         open at one line's end both want a continuation fragment.
    ///     </para>
    ///     <para>
    ///         ⚠ The geometry is closed-form and every number is forced: three 40-wide items in a
    ///         100-wide root put two on the first line and one on the second, so both spans are cut in
    ///         exactly the same place and both come out as an 80 and a 40. What distinguishes them is
    ///         <see cref="A_nested_span_carries_its_own_edges_and_the_outer_carries_the_outer_ones" />
    ///         below, which gives each one padding.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_span_inside_a_span_fragments_with_it() {
        using var tree = new LayoutTree();
        var root = PaddedRoot(tree, width: 100f, padding: 0f);
        var outer = Span(tree, root);
        var inner = Span(tree, outer);

        Item(tree, inner, 40f, 20f);
        Item(tree, inner, 40f, 20f);
        Item(tree, inner, 40f, 20f);

        tree.CalculateLayout(root, 100f, float.NaN, Direction.Ltr);

        Assert.Equal(2, tree.GetFragmentCount(inner));
        Assert.Equal(2, tree.GetFragmentCount(outer));

        // Both are the union of an 80 and a 40 stacked, so both unions are 80 by 40 at the origin.
        Assert.Equal(0f, tree.GetLeft(outer), Tolerance);
        Assert.Equal(0f, tree.GetTop(outer), Tolerance);
        Assert.Equal(80f, tree.GetWidth(outer), Tolerance);
        Assert.Equal(40f, tree.GetHeight(outer), Tolerance);

        // ⚠ And the inner span's own position is measured from the outer's union rather than from the
        // container's content edge, which is the rebasing the old remark said was missing. The two
        // unions coincide here, so the answer is zero — and it is zero for a reason rather than
        // because nothing was subtracted, which the padded case below is what proves.
        Assert.Equal(0f, tree.GetLeft(inner), Tolerance);
        Assert.Equal(0f, tree.GetTop(inner), Tolerance);

        foreach (var span in new[] { outer, inner }) {
            var (firstLeft, firstTop, firstWidth, _, firstEnds) = tree.GetFragment(span, 0);
            Assert.Equal(0f, firstLeft, Tolerance);
            Assert.Equal(0f, firstTop, Tolerance);
            Assert.Equal(80f, firstWidth, Tolerance);
            Assert.Equal(LayoutFragmentEnds.Start, firstEnds);

            var (secondLeft, secondTop, secondWidth, _, secondEnds) = tree.GetFragment(span, 1);
            Assert.Equal(0f, secondLeft, Tolerance);
            Assert.Equal(20f, secondTop, Tolerance);
            Assert.Equal(40f, secondWidth, Tolerance);
            Assert.Equal(LayoutFragmentEnds.End, secondEnds);
        }
    }

    /// <summary>
    ///     Each span in a nest draws its own two edges, at the two ends of its own content, and the
    ///     continuation line carries neither.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The test that would fail if the reopening loop did nesting arithmetic.</b> On the
    ///         second line the outer span contributes no start edge — CSS 2.1 §9.2.1.1 draws border
    ///         and padding at the two ends of the whole box and not at the ends of each piece — so the
    ///         inner span reopens at the line's content start and <i>not</i> ten points inside it. An
    ///         implementation that reopened each box inside the one around it would put the inner
    ///         fragment at 10 on the second line and pass every count-only assertion above.
    ///     </para>
    ///     <para>
    ///         The numbers: a 200-wide root, the outer padded 10 and the inner padded 6, three 70-wide
    ///         items. The first line spends 10 + 6 + 70 + 70 = 156 and cannot take a third 70, so the
    ///         outer's first fragment is 156 wide and the inner's is 146, starting at the outer's own
    ///         left padding. The second line opens at zero and the last item closes both boxes:
    ///         70 + 6 = 76 for the inner and 70 + 6 + 10 = 86 for the outer.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_nested_span_carries_its_own_edges_and_the_outer_carries_the_outer_ones() {
        using var tree = new LayoutTree();
        var root = PaddedRoot(tree, width: 200f, padding: 0f);
        var outer = Span(tree, root);
        tree.SetPadding(outer, Edge.Left, StyleLength.Points(10f));
        tree.SetPadding(outer, Edge.Right, StyleLength.Points(10f));

        var inner = Span(tree, outer);
        tree.SetPadding(inner, Edge.Left, StyleLength.Points(6f));
        tree.SetPadding(inner, Edge.Right, StyleLength.Points(6f));

        Item(tree, inner, 70f, 20f);
        Item(tree, inner, 70f, 20f);
        Item(tree, inner, 70f, 20f);

        tree.CalculateLayout(root, 200f, float.NaN, Direction.Ltr);

        Assert.Equal(2, tree.GetFragmentCount(outer));
        Assert.Equal(2, tree.GetFragmentCount(inner));

        var (outerFirstLeft, _, outerFirstWidth, _, outerFirstEnds) = tree.GetFragment(outer, 0);
        Assert.Equal(0f, outerFirstLeft, Tolerance);
        Assert.Equal(156f, outerFirstWidth, Tolerance);
        Assert.Equal(LayoutFragmentEnds.Start, outerFirstEnds);

        var (outerSecondLeft, _, outerSecondWidth, _, outerSecondEnds) = tree.GetFragment(outer, 1);
        Assert.Equal(0f, outerSecondLeft, Tolerance);
        Assert.Equal(86f, outerSecondWidth, Tolerance);
        Assert.Equal(LayoutFragmentEnds.End, outerSecondEnds);

        // Measured from the outer's union, whose left edge is the container's content edge, so the
        // inner's first fragment starts at the outer's own left padding and its second at zero.
        var (innerFirstLeft, _, innerFirstWidth, _, innerFirstEnds) = tree.GetFragment(inner, 0);
        Assert.Equal(10f, innerFirstLeft, Tolerance);
        Assert.Equal(146f, innerFirstWidth, Tolerance);
        Assert.Equal(LayoutFragmentEnds.Start, innerFirstEnds);

        var (innerSecondLeft, _, innerSecondWidth, _, innerSecondEnds) = tree.GetFragment(inner, 1);
        Assert.Equal(0f, innerSecondLeft, Tolerance);
        Assert.Equal(76f, innerSecondWidth, Tolerance);
        Assert.Equal(LayoutFragmentEnds.End, innerSecondEnds);
    }

    /// <summary>Three levels, because two could be a special case of one.</summary>
    /// <remarks>
    ///     ⚠ <b>The depth guard on the flattening, and the reason it is a separate test.</b> The
    ///     change that lifted the one-level rule was one deleted condition, and a producer that
    ///     flattened <i>two</i> levels rather than any number would satisfy both tests above. Three
    ///     spans deep also puts three boxes on the open stack when the line ends, which is the first
    ///     arrangement in which the continuation loop's order could matter and the chain per owner is
    ///     what makes it not.
    /// </remarks>
    [Fact]
    public void Flattening_recurses_rather_than_going_one_level_deeper() {
        using var tree = new LayoutTree();
        var root = PaddedRoot(tree, width: 100f, padding: 0f);
        var first = Span(tree, root);
        var second = Span(tree, first);
        var third = Span(tree, second);

        Item(tree, third, 40f, 20f);
        Item(tree, third, 40f, 20f);
        Item(tree, third, 40f, 20f);

        tree.CalculateLayout(root, 100f, float.NaN, Direction.Ltr);

        Assert.Equal(2, tree.GetFragmentCount(first));
        Assert.Equal(2, tree.GetFragmentCount(second));
        Assert.Equal(2, tree.GetFragmentCount(third));

        // Two lines of one line-height each, at every depth — a nest that had gone atomic anywhere
        // would report one 120-wide box there and a 120-wide line above it.
        Assert.Equal(40f, tree.GetHeight(first), Tolerance);
        Assert.Equal(80f, tree.GetWidth(first), Tolerance);
    }

    /// <summary>
    ///     ⚠ <b>A span with an out-of-flow child fragments, and its child is still placed.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This test was the refusal and is now its inversion, and what it asserted was
    ///         true of the code rather than of CSS.</b> The absolute walk descends only through
    ///         children that are not containing blocks — and this store's default
    ///         <see cref="PositionType" /> is <see cref="PositionType.Relative" />, Yoga's rather
    ///         than CSS's <c>static</c> — so a span is one and the container's walk stopped at it,
    ///         while a flattened span's own <c>CalculateLayoutImpl</c> never runs and started no walk
    ///         either. The child was sized, given a static position, and positioned by nobody, so the
    ///         span stayed atomic. <c>LayoutFlattenedInlineAbsolutes</c> is the walk that was missing.
    ///     </para>
    ///     <para>
    ///         Chrome 148.0.7778.280, on this fixture with the span made a containing block — which
    ///         this store's default position type makes it anyway: the span has <b>two</b> rectangles
    ///         and the child sits at the union's origin plus its own insets. §10.1 is why the union
    ///         is the rectangle to offset from — the containing block of an absolutely positioned
    ///         descendant of an inline box IS the bounding box of its first and last fragments.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_span_with_an_out_of_flow_child_fragments_and_the_child_is_still_placed() {
        using var tree = new LayoutTree();
        var root = PaddedRoot(tree, width: 100f, padding: 10f);
        var span = Span(tree, root);

        Item(tree, span, 40f, 20f);
        Item(tree, span, 40f, 20f);
        Item(tree, span, 40f, 20f);

        var inset = Item(tree, span, 10f, 10f);
        tree.SetPositionType(inset, PositionType.Absolute);
        tree.SetPosition(inset, Edge.Left, StyleLength.Points(5f));
        tree.SetPosition(inset, Edge.Top, StyleLength.Points(7f));

        tree.CalculateLayout(root, 100f, float.NaN, Direction.Ltr);

        // The whole of what the refusal cost: two lines, two boxes. 80 wide then 40, exactly what the
        // same three children give with no absolute one beside them.
        Assert.Equal(2, tree.GetFragmentCount(span));
        Assert.Equal(80f, tree.GetFragment(span, 0).Width, Tolerance);
        Assert.Equal(40f, tree.GetFragment(span, 1).Width, Tolerance);

        // The union is at the container's padding origin, and the child's insets are measured from
        // it — a position in this store is parent-relative and the child's parent is the span.
        Assert.Equal(10f, tree.GetLeft(span), Tolerance);
        Assert.Equal(10f, tree.GetTop(span), Tolerance);
        Assert.Equal(5f, tree.GetLeft(inset), Tolerance);
        Assert.Equal(7f, tree.GetTop(inset), Tolerance);
    }

    /// <summary>
    ///     An out-of-flow child of a fragmented span lands at §10.6.4's static position — where its
    ///     hypothetical box would have gone, on the line the walk had reached.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This test was the defect written down and is now its inversion, and the premise
    ///         that moved is the READER rather than the recording.</b> It used to assert 0 with a
    ///         remark saying so was wrong: <c>HideAndPositionOutOfFlow</c> wrote a
    ///         <c>BlockStaticLeft</c> for this child all along, and <c>LayoutAbsoluteChild</c> read
    ///         that pair only for a <c>block</c> or <c>flow-root</c> parent — an inline box is
    ///         neither, so the child fell through to the ALIGNMENT branch and resolved its axes from
    ///         the span's <c>flex-direction</c> and <c>justify-content</c>, properties that mean
    ///         nothing on an inline box. It landed at the union's inline start whatever the flow had
    ///         done. That is also why rebasing the recorded pair onto the union measured as dead code
    ///         when it was first written: nothing read it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both axes are un-inset now, which the old remark said could not distinguish a
    ///         placed child from a vanished one — and that is exactly what has changed.</b> A child
    ///         nobody positions reads (0, 0); §10.6.4's answer here is (40, 20), so the two are no
    ///         longer the same number and the fixture needs no inset to prove the walk ran.
    ///     </para>
    ///     <para>
    ///         Chrome 148.0.7778.280 on this fixture: (40, 20). The arithmetic behind it is forced —
    ///         the container's lines are 80 wide, so three 40-wide items are two on the first line
    ///         and one on the second, the pen stands at 40 into the second line when the walk passes
    ///         the out-of-flow child, and the second line's top is 20 below the union's. Both numbers
    ///         are measured from the union because §10.1 makes the union this child's containing
    ///         block, and a stored position in this store is parent-relative.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_out_of_flow_child_with_no_inset_lands_where_its_hypothetical_box_would_have_gone() {
        using var tree = new LayoutTree();
        var root = PaddedRoot(tree, width: 100f, padding: 10f);
        var span = Span(tree, root);

        Item(tree, span, 40f, 20f);
        Item(tree, span, 40f, 20f);
        Item(tree, span, 40f, 20f);

        var loose = Item(tree, span, 10f, 10f);
        tree.SetPositionType(loose, PositionType.Absolute);

        tree.CalculateLayout(root, 100f, float.NaN, Direction.Ltr);

        Assert.Equal(2, tree.GetFragmentCount(span));
        Assert.Equal(10f, tree.GetLeft(span), Tolerance);
        Assert.Equal(10f, tree.GetTop(span), Tolerance);

        Assert.Equal(40f, tree.GetLeft(loose), Tolerance);
        Assert.Equal(20f, tree.GetTop(loose), Tolerance);
    }

    /// <summary>
    ///     The static position is the pen where the child was written, not the end of the flow.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The one assertion the fixture above cannot make, because there the child is last.</b>
    ///     "After every in-flow sibling before it" and "after all of them" agree for a trailing child
    ///     and disagree for every other one, so an implementation that recorded the pen once at the
    ///     end of the walk — or that hoisted out-of-flow children out of the stream and appended them
    ///     — passes the trailing case and fails this. The child sits between the first and second
    ///     items, so its hypothetical box is at x = 40 on the FIRST line: (40, 0).
    /// </remarks>
    [Fact]
    public void The_static_position_is_the_pen_at_the_child_and_not_at_the_end_of_the_flow() {
        using var tree = new LayoutTree();
        var root = PaddedRoot(tree, width: 100f, padding: 10f);
        var span = Span(tree, root);

        Item(tree, span, 40f, 20f);

        var loose = Item(tree, span, 10f, 10f);
        tree.SetPositionType(loose, PositionType.Absolute);

        Item(tree, span, 40f, 20f);
        Item(tree, span, 40f, 20f);

        tree.CalculateLayout(root, 100f, float.NaN, Direction.Ltr);

        // Unmoved by the out-of-flow child in the middle of it: still two lines, 80 then 40.
        Assert.Equal(2, tree.GetFragmentCount(span));
        Assert.Equal(80f, tree.GetWidth(span), Tolerance);
        Assert.Equal(40f, tree.GetHeight(span), Tolerance);

        Assert.Equal(40f, tree.GetLeft(loose), Tolerance);
        Assert.Equal(0f, tree.GetTop(loose), Tolerance);
    }

    /// <summary>
    ///     Under RTL the static position is the same pen, mirrored — and it is the child's own
    ///     inline-start edge that is placed there.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The half of the fix with a behaviour change in it, and it is a behaviour change
    ///         for an ATOMIC span too.</b> Reaching the static-position branch at all required adding
    ///         <see cref="Display.Inline" /> to <c>isPhysicalParent</c>, because the branch above it
    ///         resolved an inline box's axes through <c>FlexAxis.Resolve(styles[node].FlexDirection)</c>
    ///         — and a `row` left in the style by the initial value resolves to `row-reverse` under
    ///         RTL, which sends an un-inset child to the wrong physical edge of the union.
    ///     </para>
    ///     <para>
    ///         The line's start edge is its right one here, so the first item occupies 40..80 of the
    ///         union's 80 and the second 0..40; the third is alone on line two at 40..80. The pen
    ///         stands 40 in from the right when the walk passes the child, and the child's own left
    ///         edge is its 10 of width further in again: 80 − 40 − 10 = 30.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_rtl_spans_out_of_flow_child_is_placed_from_the_lines_right_edge() {
        using var tree = new LayoutTree();
        var root = PaddedRoot(tree, width: 100f, padding: 10f);
        var span = Span(tree, root);

        Item(tree, span, 40f, 20f);
        Item(tree, span, 40f, 20f);
        Item(tree, span, 40f, 20f);

        var loose = Item(tree, span, 10f, 10f);
        tree.SetPositionType(loose, PositionType.Absolute);

        tree.CalculateLayout(root, 100f, float.NaN, Direction.Rtl);

        Assert.Equal(2, tree.GetFragmentCount(span));
        Assert.Equal(80f, tree.GetWidth(span), Tolerance);

        Assert.Equal(30f, tree.GetLeft(loose), Tolerance);
        Assert.Equal(20f, tree.GetTop(loose), Tolerance);
    }

    /// <summary>
    ///     No span at all: a block container's own out-of-flow child gets §10.6.4's position on a
    ///     line too.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is the half of the fix that changes an answer no span is involved in, and it
    ///         is worth its own case because the two recordings were never the same thing.</b> A
    ///         block container reading its children as a block flow has recorded a real §10.6.4
    ///         cursor all along — <c>WalkBlockChildren</c> advances it per child. A block container
    ///         whose children are all inline-level runs the line walk instead, and there the
    ///         recording was <c>HideAndPositionOutOfFlow</c>'s single answer for every child in the
    ///         run: the container's content edge. So a child written after two lines' worth of items
    ///         claimed the corner.
    ///     </para>
    ///     <para>
    ///         Nothing in the 6 465 cases of this project asserted the old answer, which is what says
    ///         it had never been looked at rather than that it had been decided. Here the lines are
    ///         80 wide inside 10 of padding, so the pen is 40 into the second line and the second
    ///         line's top is 20 below the first's: (10 + 40, 10 + 20).
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_block_containers_own_out_of_flow_child_is_placed_on_the_line_it_was_written_on() {
        using var tree = new LayoutTree();
        var root = PaddedRoot(tree, width: 100f, padding: 10f);

        Item(tree, root, 40f, 20f);
        Item(tree, root, 40f, 20f);
        Item(tree, root, 40f, 20f);

        var loose = Item(tree, root, 10f, 10f);
        tree.SetPositionType(loose, PositionType.Absolute);

        tree.CalculateLayout(root, 100f, float.NaN, Direction.Ltr);

        Assert.Equal(50f, tree.GetLeft(loose), Tolerance);
        Assert.Equal(30f, tree.GetTop(loose), Tolerance);
    }

    /// <summary>
    ///     An <i>atomic</i> <c>inline</c> box's out-of-flow child gets §10.6.4's static position from
    ///     the box's own line walk, with no union and no rebase anywhere in it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>One reader serves two paths and this is the only fixture that separates them.</b>
    ///         The four cases above all reach <c>LayoutAbsoluteChild</c>'s
    ///         <see cref="Display.Inline" /> branch through a FLATTENED span, whose static position is
    ///         written by the container's <c>PlaceLine</c> in the container's coordinates and then
    ///         rebased onto the union by <c>CommitInlineBoxFragments</c>. A box that stays atomic runs
    ///         its own <c>CalculateInlineLayoutImpl</c>, so its own <c>PlaceLine</c> writes the pen in
    ///         its own coordinates and there is nothing to rebase — the same branch, reached with the
    ///         other half of the machinery switched off.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>There is a FOURTH way to hold such a box atomic, and it is the only one that is
    ///         not degenerate: give it a parent that runs no inline formatting context at all.</b>
    ///         <c>IsNonAtomicInline</c> is consulted only from <c>BuildInlineItems</c> and
    ///         <c>LayoutFlattenedInlineAbsolutes</c> — that is, only from a parent already walking
    ///         lines — so a span that is a FLEX ITEM is never offered for flattening. It is laid out
    ///         by <c>CalculateLayoutImpl</c> like any other item and
    ///         <c>EstablishesInlineFormattingContext</c> then sends it to its own line walk. The three
    ///         routes recorded on <c>IsNonAtomicInline</c> each destroy the fixture — a measure
    ///         function makes the node a leaf whose children are never laid out, a floated child sends
    ///         the span's width through shrink-to-fit against a float context, and a span with no
    ///         in-flow children has no flow for §10.6.4 to be after, so its answer is the padding
    ///         corner and a fixture on it would pass against the defect. This one destroys nothing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The width is the witness that the box really did stay atomic.</b> A flattened
    ///         box's rectangle is the union of its fragments and its own <c>width</c> is ignored; an
    ///         atomic one is a box that was told what it is. The same four nodes under a <c>block</c>
    ///         parent give a span 125 wide on ONE line — 5 of padding plus three 40s, all of which fit
    ///         across the container's 180 — so the 90 asserted below is not a number the flattened
    ///         path can produce.
    ///     </para>
    ///     <para>
    ///         The arithmetic is forced: the span's content box is 85 wide starting 5 in, so two items
    ///         fit on the first line and the third opens a second one at y = 20. The out-of-flow child
    ///         is written after all three, so the pen stands 40 into that second line, and both
    ///         numbers carry the padding: (5 + 40, 5 + 20). ⚠ Both axes move under the defect and they
    ///         move to the SAME place — a child that falls through to the alignment branch below is
    ///         put at the padding corner (5, 5) by <c>SetFlexStartLayoutPosition</c> — which is what
    ///         makes one assertion enough here.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>No Chrome reading is possible for this fixture, and that is a fact about CSS
    ///         rather than about the harness.</b> CSS Display §2.7 blockifies a flex item, so
    ///         <c>display: inline</c> on one computes to <c>block</c> in every browser — as it does on
    ///         a grid item, a float, an absolutely positioned box and the root element, which between
    ///         them are every way to hold such a box out of an inline formatting context. This store
    ///         does not blockify at all (#1149). So what is pinned here is the store's own model, and
    ///         the property pinned is that the model is CONSISTENT: the atomic path answers §10.6.4
    ///         with the same rule the flattened path does, measured from the box's own padding origin
    ///         rather than from a union's.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the numbers below turn out to be Chrome's after all, which is a correction to
    ///         the paragraph above rather than a reason to delete it.</b>
    ///         <see cref="BlockificationTests" /> measured §2.7's rewrite over 156 shapes covering all
    ///         five of those contexts and it moves nothing here — a blockified <c>block</c> holding
    ///         only inline-level children runs the same line walk this span runs, so 90 wide and two
    ///         lines is what a browser gives too. What has no Chrome reading is the <i>route</i>: the
    ///         box CSS sends through block layout arrives at these numbers through
    ///         <c>IsNonAtomicInline</c> answering no, and that is the model this fixture pins.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_atomic_spans_out_of_flow_child_is_placed_by_its_own_line_walk() {
        using var tree = new LayoutTree();

        var root = tree.CreateNode();
        tree.SetDisplay(root, Display.Flex);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(200f));
        tree.SetPadding(root, Edge.Left, StyleLength.Points(10f));
        tree.SetPadding(root, Edge.Top, StyleLength.Points(10f));

        var span = tree.CreateNode();
        tree.SetDisplay(span, Display.Inline);
        tree.SetDimension(span, Dimension.Width, StyleLength.Points(90f));
        tree.SetPadding(span, Edge.Left, StyleLength.Points(5f));
        tree.SetPadding(span, Edge.Top, StyleLength.Points(5f));
        tree.AddChild(root, span);

        Item(tree, span, 40f, 20f);
        Item(tree, span, 40f, 20f);
        Item(tree, span, 40f, 20f);

        var loose = Item(tree, span, 10f, 10f);
        tree.SetPositionType(loose, PositionType.Absolute);

        tree.CalculateLayout(root, 200f, float.NaN, Direction.Ltr);

        // Atomic, and these three numbers are what say so: one box, the declared width honoured, and
        // a height of 5 + two lines of 20 because the third item wrapped at 85.
        Assert.Equal(1, tree.GetFragmentCount(span));
        Assert.Equal(90f, tree.GetWidth(span), Tolerance);
        Assert.Equal(45f, tree.GetHeight(span), Tolerance);

        Assert.Equal(45f, tree.GetLeft(loose), Tolerance);
        Assert.Equal(25f, tree.GetTop(loose), Tolerance);
    }

    /// <summary>
    ///     The rectangle such a child resolves its insets against is the span's union, not the
    ///     container's content box — and a span inside an anonymous block box is walked too.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The two rectangles are the same size in every other fixture in this file, which
    ///         is why this one has a block-level sibling.</b> A span holding three 40-wide items in a
    ///         100-wide container has a union exactly as wide and as tall as the container's content
    ///         box, so a trailing inset resolved against the wrong one of the two answers correctly.
    ///         The 30-tall sibling below takes the container's content height to 70 and leaves the
    ///         union at 40, and the two answers separate: 40 − 4 − 10 = 26 against the union and
    ///         70 − 4 − 10 = 56 against the container.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the sibling is what makes the container run §9.2.1.1's anonymous block box</b>,
    ///         so this is the BLOCK path's copy of the walk rather than the inline one's. The two call
    ///         sites are separate lines in separate files and an implementation that adds one passes
    ///         every other case here.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_containing_block_of_such_a_child_is_the_union_and_the_block_path_walks_it_too() {
        using var tree = new LayoutTree();
        var root = PaddedRoot(tree, width: 100f, padding: 10f);
        var span = Span(tree, root);

        Item(tree, span, 40f, 20f);
        Item(tree, span, 40f, 20f);
        Item(tree, span, 40f, 20f);

        var trailing = Item(tree, span, 10f, 10f);
        tree.SetPositionType(trailing, PositionType.Absolute);
        tree.SetPosition(trailing, Edge.Right, StyleLength.Points(6f));
        tree.SetPosition(trailing, Edge.Bottom, StyleLength.Points(4f));

        BlockBox(tree, root, 100f, 30f);

        tree.CalculateLayout(root, 100f, float.NaN, Direction.Ltr);

        // The union is unchanged by the sibling: two lines of 20, 80 wide.
        Assert.Equal(2, tree.GetFragmentCount(span));
        Assert.Equal(80f, tree.GetWidth(span), Tolerance);
        Assert.Equal(40f, tree.GetHeight(span), Tolerance);

        // 80 − 6 − 10 and 40 − 4 − 10, both measured from the union.
        Assert.Equal(64f, tree.GetLeft(trailing), Tolerance);
        Assert.Equal(26f, tree.GetTop(trailing), Tolerance);
    }

    /// <summary>An out-of-flow child of a span nested inside another span is placed too.</summary>
    /// <remarks>
    ///     ⚠ <b>The case the container's own walk cannot reach even once the outer one is fixed.</b>
    ///     Both spans are containing blocks, so the container's walk stops at the outer and the
    ///     outer's walk stops at the inner. Descending into every flattened box — rather than only
    ///     into the ones that were themselves worth a call — is what covers it, and an implementation
    ///     that starts a walk per flattened box without recursing passes the two cases above and
    ///     leaves this child positioned by nobody.
    /// </remarks>
    [Fact]
    public void An_out_of_flow_child_of_a_nested_span_is_placed_by_the_nested_spans_own_walk() {
        using var tree = new LayoutTree();
        var root = PaddedRoot(tree, width: 100f, padding: 0f);
        var outer = Span(tree, root);
        var inner = Span(tree, outer);

        Item(tree, inner, 40f, 20f);
        Item(tree, inner, 40f, 20f);
        Item(tree, inner, 40f, 20f);

        var nested = Item(tree, inner, 10f, 10f);
        tree.SetPositionType(nested, PositionType.Absolute);
        tree.SetPosition(nested, Edge.Left, StyleLength.Points(3f));
        tree.SetPosition(nested, Edge.Top, StyleLength.Points(4f));

        tree.CalculateLayout(root, 100f, float.NaN, Direction.Ltr);

        Assert.Equal(2, tree.GetFragmentCount(outer));
        Assert.Equal(2, tree.GetFragmentCount(inner));

        Assert.Equal(3f, tree.GetLeft(nested), Tolerance);
        Assert.Equal(4f, tree.GetTop(nested), Tolerance);
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

    /// <summary>
    ///     The end edge a span will spend when it closes decides whether the item before it fits.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Read out of Chrome 148.0.7778.280, and it is the first number in this file that
    ///         was.</b> The rest of the file is closed-form arithmetic because no browser reading was
    ///         taken; this one had to be, because the arithmetic alone cannot say <i>which</i> of two
    ///         defensible rules a browser implements — and the two differ, see
    ///         <see cref="A_box_that_carries_on_past_the_break_spends_no_end_edge_on_the_line" />.
    ///         Fixture: a 200-wide <c>flow-root</c> at <c>font-size: 0; line-height: 0</c>, a
    ///         <c>display: inline</c> span with <c>padding: 0 12px</c>, three 60×20 <c>inline-block</c>
    ///         children, and every rectangle differenced against the container's.
    ///     </para>
    ///     <para>
    ///         Chrome puts <b>two</b> items on the first line: 12 + 60 + 60 = 132 and the third would
    ///         take the line to 192, which fits — until the span's closing 12 takes it to 204. Before
    ///         this was charged, Vixen placed all three and then overflowed the box by 4, silently.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_closing_edge_that_will_not_fit_wraps_the_item_before_it() {
        using var tree = new LayoutTree();
        var root = PaddedRoot(tree, width: 200f, padding: 0f);
        var span = Span(tree, root);
        tree.SetPadding(span, Edge.Left, StyleLength.Points(12f));
        tree.SetPadding(span, Edge.Right, StyleLength.Points(12f));

        var first = Item(tree, span, 60f, 20f);
        var second = Item(tree, span, 60f, 20f);
        var third = Item(tree, span, 60f, 20f);

        tree.CalculateLayout(root, 200f, float.NaN, Direction.Ltr);

        // Chrome: (12, 0), (72, 0), (0, 20) — and the union starts at the container's origin, so the
        // children's own rectangles are those numbers unchanged.
        Assert.Equal(12f, tree.GetLeft(first), Tolerance);
        Assert.Equal(0f, tree.GetTop(first), Tolerance);
        Assert.Equal(72f, tree.GetLeft(second), Tolerance);
        Assert.Equal(0f, tree.GetTop(second), Tolerance);
        Assert.Equal(0f, tree.GetLeft(third), Tolerance);
        Assert.Equal(20f, tree.GetTop(third), Tolerance);

        // Chrome's two span rectangles are 132 and 72 wide: the start padding plus two items, then
        // one item plus the end padding.
        Assert.Equal(2, tree.GetFragmentCount(span));
        Assert.Equal(132f, tree.GetFragment(span, 0).Width, Tolerance);
        Assert.Equal(72f, tree.GetFragment(span, 1).Width, Tolerance);

        // And the line no longer hangs off the box, which is what the overflow was.
        Assert.Equal(132f, tree.GetWidth(span), Tolerance);
    }

    /// <summary>A span that carries on to the next line draws no end edge at the break, and is charged none.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is the case that refutes the obvious fix.</b> "Charge the end edges of every
    ///         box that is open" is one line shorter and wrong: §9.2.1.1 draws an inline box's two
    ///         horizontal edges at the two ends of the <i>whole</i> box, so a span whose content
    ///         continues below spends nothing at the break — and charging it would wrap items Chrome
    ///         keeps.
    ///     </para>
    ///     <para>
    ///         Chrome 148.0.7778.280, same fixture with <c>padding-right: 30px</c> and four children:
    ///         <b>three</b> on the first line at x = 0, 60, 120, the fourth on the second — 180 on a
    ///         200-wide line, with the 30 spent on the second line where the span really ends. A
    ///         pessimistic rule would have stopped at two, because 120 + 60 + 30 is 210.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_box_that_carries_on_past_the_break_spends_no_end_edge_on_the_line() {
        using var tree = new LayoutTree();
        var root = PaddedRoot(tree, width: 200f, padding: 0f);
        var span = Span(tree, root);
        tree.SetPadding(span, Edge.Right, StyleLength.Points(30f));

        var first = Item(tree, span, 60f, 20f);
        var second = Item(tree, span, 60f, 20f);
        var third = Item(tree, span, 60f, 20f);
        var fourth = Item(tree, span, 60f, 20f);

        tree.CalculateLayout(root, 200f, float.NaN, Direction.Ltr);

        Assert.Equal(0f, tree.GetLeft(first), Tolerance);
        Assert.Equal(60f, tree.GetLeft(second), Tolerance);
        Assert.Equal(120f, tree.GetLeft(third), Tolerance);
        Assert.Equal(0f, tree.GetTop(third), Tolerance);
        Assert.Equal(0f, tree.GetLeft(fourth), Tolerance);
        Assert.Equal(20f, tree.GetTop(fourth), Tolerance);

        // Chrome's two span rectangles: 180 with no end padding on it, then 60 + 30.
        Assert.Equal(2, tree.GetFragmentCount(span));
        Assert.Equal(180f, tree.GetFragment(span, 0).Width, Tolerance);
        Assert.Equal(90f, tree.GetFragment(span, 1).Width, Tolerance);
    }

    /// <summary>With two spans open, the one that closes here is charged and the one that does not is not.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The case the two rules above only look like they cover between them.</b> An outer
    ///         span padded 40 at its end holds an inner span padded 6 at its end over the first two
    ///         items, then two more items of its own. When the third item is fitted, one box would
    ///         close before it and one would not — so exactly 6 of the 46 open end edges is the line's
    ///         to spend.
    ///     </para>
    ///     <para>
    ///         Chrome 148.0.7778.280: items at x = 0, 60, 126 on the first line and the fourth on the
    ///         second; the outer span is 186 then 100 wide, the inner a single 126. The 126 is the
    ///         inner span's 6 landing between the second item and the third, which is the whole of
    ///         what makes this case different from the previous one.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Only_the_boxes_that_close_before_the_next_item_are_charged_to_the_line() {
        using var tree = new LayoutTree();
        var root = PaddedRoot(tree, width: 200f, padding: 0f);

        var outer = Span(tree, root);
        tree.SetPadding(outer, Edge.Right, StyleLength.Points(40f));

        var inner = Span(tree, outer);
        tree.SetPadding(inner, Edge.Right, StyleLength.Points(6f));

        var first = Item(tree, inner, 60f, 20f);
        var second = Item(tree, inner, 60f, 20f);
        var third = Item(tree, outer, 60f, 20f);
        var fourth = Item(tree, outer, 60f, 20f);

        tree.CalculateLayout(root, 200f, float.NaN, Direction.Ltr);

        // The first two are relative to the inner span's union, which starts at the container's own
        // origin; the last two are relative to the outer's, which starts there too.
        Assert.Equal(0f, tree.GetLeft(first), Tolerance);
        Assert.Equal(60f, tree.GetLeft(second), Tolerance);
        Assert.Equal(126f, tree.GetLeft(third), Tolerance);
        Assert.Equal(0f, tree.GetTop(third), Tolerance);
        Assert.Equal(0f, tree.GetLeft(fourth), Tolerance);
        Assert.Equal(20f, tree.GetTop(fourth), Tolerance);

        Assert.Equal(1, tree.GetFragmentCount(inner));
        Assert.Equal(126f, tree.GetWidth(inner), Tolerance);

        Assert.Equal(2, tree.GetFragmentCount(outer));
        Assert.Equal(186f, tree.GetFragment(outer, 0).Width, Tolerance);
        Assert.Equal(100f, tree.GetFragment(outer, 1).Width, Tolerance);
    }

    /// <summary>The end edge is charged at the boundary itself: 200 on a 200-wide line still fits.</summary>
    /// <remarks>
    ///     ⚠ <b>Both halves, because a rule that is one point out passes either of them alone.</b>
    ///     Chrome 148.0.7778.280, three 60×20 children in a span whose <c>padding-right</c> is the
    ///     only variable: at 20 all three stay on the line and the span is exactly 200 wide — the
    ///     line is full and is not over — and at 21 the third wraps. This is also what pins
    ///     <c>LineFitTolerance</c> to the side it is on: a line that fits exactly is not a break.
    /// </remarks>
    [Fact]
    public void The_end_edge_is_charged_at_the_boundary_and_an_exact_fit_is_not_a_break() {
        using var exact = new LayoutTree();
        var exactRoot = PaddedRoot(exact, width: 200f, padding: 0f);
        var exactSpan = Span(exact, exactRoot);
        exact.SetPadding(exactSpan, Edge.Right, StyleLength.Points(20f));

        Item(exact, exactSpan, 60f, 20f);
        Item(exact, exactSpan, 60f, 20f);
        var exactThird = Item(exact, exactSpan, 60f, 20f);

        exact.CalculateLayout(exactRoot, 200f, float.NaN, Direction.Ltr);

        Assert.Equal(1, exact.GetFragmentCount(exactSpan));
        Assert.Equal(200f, exact.GetWidth(exactSpan), Tolerance);
        Assert.Equal(120f, exact.GetLeft(exactThird), Tolerance);
        Assert.Equal(0f, exact.GetTop(exactThird), Tolerance);

        using var over = new LayoutTree();
        var overRoot = PaddedRoot(over, width: 200f, padding: 0f);
        var overSpan = Span(over, overRoot);
        over.SetPadding(overSpan, Edge.Right, StyleLength.Points(21f));

        Item(over, overSpan, 60f, 20f);
        Item(over, overSpan, 60f, 20f);
        var overThird = Item(over, overSpan, 60f, 20f);

        over.CalculateLayout(overRoot, 200f, float.NaN, Direction.Ltr);

        Assert.Equal(2, over.GetFragmentCount(overSpan));
        Assert.Equal(120f, over.GetFragment(overSpan, 0).Width, Tolerance);
        Assert.Equal(81f, over.GetFragment(overSpan, 1).Width, Tolerance);
        Assert.Equal(0f, over.GetLeft(overThird), Tolerance);
        Assert.Equal(20f, over.GetTop(overThird), Tolerance);
    }

    /// <summary>
    ///     A span whose opening tag is the last thing that would fit starts on the next line instead.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The mirror image of
    ///         <see cref="A_box_that_carries_on_past_the_break_spends_no_end_edge_on_the_line" />, and
    ///         it is a different rule rather than the same one read backwards.</b> An end edge is not
    ///         charged at a break because the box carries on; a <i>start</i> edge cannot be charged to
    ///         a line the box has no content on at all, because there is no such thing as a fragment
    ///         with neither an end of the box nor any of its children in it. So the line ends
    ///         <i>before</i> the opening tag and the box begins on the next one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Every other fragmentation case in this file opens the span on a line that also
    ///         holds one of its children</b>, which is exactly why none of them could see this: it
    ///         takes an opening tag that is the last stream entry on a line.
    ///     </para>
    ///     <para>
    ///         Chrome 148.0.7778.280, a 200-wide <c>flow-root</c> at <c>font-size: 0; line-height: 0</c>
    ///         with two bare 60×20 <c>inline-block</c> items and then a <c>display: inline</c> span
    ///         padded 30 at its start holding a third: the span has <b>one</b> rectangle,
    ///         <c>left 0, width 90</c>, on the second line, and its child sits at <c>x = 30</c>. Vixen
    ///         gave the span two rectangles — 30 of padding hanging off the end of the first line at
    ///         x = 120, and the child at x = 0 on the second with its start padding nowhere near it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_opening_tag_that_ends_a_line_moves_to_the_next_line_with_its_content() {
        using var tree = new LayoutTree();
        var root = PaddedRoot(tree, width: 200f, padding: 0f);

        Item(tree, root, 60f, 20f);
        Item(tree, root, 60f, 20f);

        var span = Span(tree, root);
        tree.SetPadding(span, Edge.Left, StyleLength.Points(30f));

        var child = Item(tree, span, 60f, 20f);

        tree.CalculateLayout(root, 200f, float.NaN, Direction.Ltr);

        // One rectangle, because not one of the box's children is on the first line — 30 + 60, with
        // both real ends on it.
        Assert.Equal(1, tree.GetFragmentCount(span));

        var (left, top, width, _, ends) = tree.GetFragment(span, 0);
        Assert.Equal(0f, left, Tolerance);
        Assert.Equal(0f, top, Tolerance);
        Assert.Equal(90f, width, Tolerance);
        Assert.Equal(LayoutFragmentEnds.Both, ends);

        // The union is that one rectangle, on the second line.
        Assert.Equal(0f, tree.GetLeft(span), Tolerance);
        Assert.Equal(20f, tree.GetTop(span), Tolerance);
        Assert.Equal(90f, tree.GetWidth(span), Tolerance);

        // And the child is inset by the padding that opened the box — the half that was lost, since
        // the padding was spent on the line above and the child started at the band's own edge.
        Assert.Equal(30f, tree.GetLeft(child), Tolerance);
        Assert.Equal(0f, tree.GetTop(child), Tolerance);

        // Two lines, not three: the first still holds both bare items.
        Assert.Equal(40f, tree.GetHeight(root), Tolerance);
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
