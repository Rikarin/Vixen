// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Layout.Tests;

/// <summary>
///     CSS 2.1 §9.4.2 line boxes, §10.3.9 shrink-to-fit and §10.8.1 vertical alignment — the fourth
///     algorithm, and the first one with no corpus behind it at all.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Neither corpus contains a single inline fixture, and that was verified rather than
///         assumed.</b> Across all eight of Taffy's files, every occurrence of the string
///         <c>inline</c> is a <i>test name</i> about a grid's inline axis
///         (<c>grid_overflow_inline_axis_*</c>) and every occurrence of <c>vertical</c> is a test name
///         too (<c>rounding_inner_node_controversy_vertical</c>, <c>grid_relayout_vertical_text</c>).
///         Not one of the 5 524 sets <c>display: inline*</c>, and not one sets
///         <c>vertical-align</c>. Yoga's 534 are the same. So unlike block and grid — which arrived
///         with two thousand right answers apiece the day their keyword was mapped — this algorithm
///         has no external judge unless one is fetched, and fetching one is what this file is.
///     </para>
///     <para>
///         <b>The oracle is <c>web-platform-tests</c>, BSD-3-Clause</b>, re-expressed rather than
///         translated in exactly the way <see cref="OrderTests" /> re-expresses WPT's <c>order</c>
///         reftests, and for the same two reasons: this store has no renderer, and three quarters of
///         WPT's <c>css/</c> is reftests. What is usable is the <c>check-layout-th.js</c> family,
///         which asserts geometry from <c>data-expected-*</c> attributes written inline in the HTML —
///         statically parseable, no browser needed, structurally the same artefact as Taffy's XML.
///         Each test below names the file it came from.
///     </para>
///     <para>
///         ⚠ <b>And most of WPT's inline tests are still unusable here, for a reason worth writing
///         down.</b> A line box's height and baseline depend on the <i>strut</i> — the block
///         container's own font ascent and descent — so almost every inline test in WPT is implicitly
///         a font test. <c>Vixen.Ui.Layout</c> has no font and no font size; it is geometry, and
///         <c>FontRegistry</c> lives a layer out in <c>Vixen.Ui</c>. The tests that survive the
///         crossing are the ones whose every box carries an explicit size, which is why
///         <c>css-flexbox/inline-flex.html</c> is the single most valuable file found: three boxes,
///         all 50×50, and the assertion is purely where they sit.
///     </para>
/// </remarks>
public class InlineFormattingTests {
    const float Tolerance = 0.0001f;

    /// <summary>
    ///     Three inline-level boxes sit side by side on one line, and the middle one is a flex
    ///     container that still lays its own children out.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Re-expressed from <c>wpt/css/css-flexbox/inline-flex.html</c></b>, whose own prose
    ///     says it "checks that inline-flex generates a flex container box that is inline-level when
    ///     placed in flow layout". Its three children are 50×50 and carry
    ///     <c>data-offset-x</c> of 0, 50 and 100 with <c>data-offset-y</c> of 0 throughout; the
    ///     <c>inline-flex</c> holds two <c>flex: 1</c> children with
    ///     <c>data-expected-width="25"</c> each. Every number below is one of those, and none of them
    ///     depends on a font — which is the property that let this one cross and stopped the rest.
    ///     <para>
    ///         This is the whole point of B3 in one assertion. Before it, the three keywords were
    ///         refused rather than aliased precisely because <c>inline-block</c> mapped onto
    ///         <see cref="Display.Block" /> takes the whole line: the second box would have been at
    ///         y = 50 and the third at y = 100.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Three_inline_level_boxes_share_one_line_and_the_flex_one_lays_out_inside() {
        using var tree = new LayoutTree();
        var root = Root(tree, 300f);

        var first = Item(tree, root, Display.InlineBlock, 50f, 50f);
        var middle = Item(tree, root, Display.InlineFlex, 50f, 50f);
        var last = Item(tree, root, Display.InlineBlock, 50f, 50f);

        // ⚠ Stated, because CSS's initial `flex-direction` is `row` and this store's is `column` —
        // `FlexDirection.Column` is the zero value, so a defaulted node is a column. The fixture's
        // two children are side by side and it never says so, which is the ordinary hazard of
        // re-expressing a browser test: the declaration that matters is the one the browser supplied.
        tree.SetFlexDirection(middle, FlexDirection.Row);

        var left = Grower(tree, middle);
        var right = Grower(tree, middle);

        tree.CalculateLayout(root, 300f, float.NaN, Direction.Ltr);

        Assert.Equal(0f, tree.GetLeft(first), Tolerance);
        Assert.Equal(50f, tree.GetLeft(middle), Tolerance);
        Assert.Equal(100f, tree.GetLeft(last), Tolerance);

        Assert.Equal(0f, tree.GetTop(first), Tolerance);
        Assert.Equal(0f, tree.GetTop(middle), Tolerance);
        Assert.Equal(0f, tree.GetTop(last), Tolerance);

        // The inline-flex is a real flex container, not a box that merely sits on a line.
        Assert.Equal(25f, tree.GetWidth(left), Tolerance);
        Assert.Equal(25f, tree.GetWidth(right), Tolerance);
    }

    /// <summary>
    ///     ⚠ <b>The inverted proof: the same three boxes as <c>block</c> take a line each.</b>
    /// </summary>
    /// <remarks>
    ///     There is no WPT file for this because it is not a rule anybody doubts — it is the control.
    ///     It is here because the test above passes trivially against an implementation that stacks
    ///     nothing at all, and because the failure this whole plan item exists to prevent is an alias
    ///     that makes these two tests agree.
    /// </remarks>
    [Fact]
    public void The_same_three_boxes_as_block_level_take_a_line_each() {
        using var tree = new LayoutTree();
        var root = Root(tree, 300f);

        var first = Item(tree, root, Display.Block, 50f, 50f);
        var middle = Item(tree, root, Display.Block, 50f, 50f);
        var last = Item(tree, root, Display.Block, 50f, 50f);

        tree.CalculateLayout(root, 300f, float.NaN, Direction.Ltr);

        Assert.Equal(0f, tree.GetTop(first), Tolerance);
        Assert.Equal(50f, tree.GetTop(middle), Tolerance);
        Assert.Equal(100f, tree.GetTop(last), Tolerance);

        Assert.Equal(0f, tree.GetLeft(middle), Tolerance);
    }

    /// <summary>An inline-block with no stated width is as wide as its contents, not as its line.</summary>
    /// <remarks>
    ///     ⚠ <b>CSS 2.1 §10.3.9, and the difference from §10.3.3 is the entire keyword.</b> A
    ///     block-level box with <c>width: auto</c> solves the equation by filling the containing
    ///     block; an inline-level one shrink-to-fits. The two children below are 30 and 40 wide inside
    ///     a 300-point container, so a §10.3.3 reading gives both of them 300 and this one gives them
    ///     their contents.
    ///     <para>
    ///         The mechanism is <see cref="SizingMode.FitContent" />, which this store has understood
    ///         since Yoga's 534. What was missing was never the arithmetic — it was a caller that
    ///         asked for it, which is worth knowing because doc 43 § F4 read the absence of the
    ///         keyword as the absence of the sizing.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_inline_block_shrinks_to_fit_rather_than_filling_the_line() {
        using var tree = new LayoutTree();
        var root = Root(tree, 300f);

        var narrow = Item(tree, root, Display.InlineBlock, float.NaN, float.NaN);
        var wider = Item(tree, root, Display.InlineBlock, float.NaN, float.NaN);

        Item(tree, narrow, Display.Block, 10f, 30f);
        Item(tree, wider, Display.Block, 10f, 40f);

        tree.CalculateLayout(root, 300f, float.NaN, Direction.Ltr);

        Assert.Equal(30f, tree.GetWidth(narrow), Tolerance);
        Assert.Equal(40f, tree.GetWidth(wider), Tolerance);

        // …and they are still on one line, at their own widths rather than at 300 apiece.
        Assert.Equal(0f, tree.GetLeft(narrow), Tolerance);
        Assert.Equal(30f, tree.GetLeft(wider), Tolerance);
    }

    /// <summary>A line that runs out of room starts another one.</summary>
    /// <remarks>
    ///     §9.4.2: boxes are placed along the line until the next one does not fit, and then a new
    ///     line box is generated below. Four 40-point boxes in a 100-point container is two per line.
    /// </remarks>
    [Fact]
    public void Items_that_do_not_fit_start_a_new_line() {
        using var tree = new LayoutTree();
        var root = Root(tree, 100f);

        var one = Item(tree, root, Display.InlineBlock, 10f, 40f);
        var two = Item(tree, root, Display.InlineBlock, 10f, 40f);
        var three = Item(tree, root, Display.InlineBlock, 10f, 40f);
        var four = Item(tree, root, Display.InlineBlock, 10f, 40f);

        tree.CalculateLayout(root, 100f, float.NaN, Direction.Ltr);

        Assert.Equal((0f, 0f), (tree.GetLeft(one), tree.GetTop(one)));
        Assert.Equal((40f, 0f), (tree.GetLeft(two), tree.GetTop(two)));
        Assert.Equal((0f, 10f), (tree.GetLeft(three), tree.GetTop(three)));
        Assert.Equal((40f, 10f), (tree.GetLeft(four), tree.GetTop(four)));

        Assert.Equal(20f, tree.GetHeight(root), Tolerance);
    }

    /// <summary>
    ///     ⚠ <b>An item wider than the whole line still goes on it.</b>
    /// </summary>
    /// <remarks>
    ///     §9.4.2 again: a line box that cannot hold its single atomic item overflows rather than
    ///     producing an empty line and overflowing anyway. This is a hazard test as much as a
    ///     conformance one — an implementation that breaks before placing anything loops forever, so
    ///     the assertion that matters most here is that the call returns.
    /// </remarks>
    [Fact]
    public void An_item_wider_than_the_line_overflows_it_rather_than_looping() {
        using var tree = new LayoutTree();
        var root = Root(tree, 50f);

        var huge = Item(tree, root, Display.InlineBlock, 10f, 400f);
        var after = Item(tree, root, Display.InlineBlock, 10f, 20f);

        tree.CalculateLayout(root, 50f, float.NaN, Direction.Ltr);

        Assert.Equal(0f, tree.GetTop(huge), Tolerance);
        Assert.Equal(400f, tree.GetWidth(huge), Tolerance);

        // The next item did not join it — the line was already over-full.
        Assert.Equal(10f, tree.GetTop(after), Tolerance);
    }

    /// <summary>Boxes of different heights hang from a common baseline.</summary>
    /// <remarks>
    ///     ⚠ <b>§10.8.1 synthesises an empty inline-block's baseline at its bottom margin edge</b>,
    ///     which makes this case exactly checkable without a font: three boxes of 20, 40 and 30
    ///     points all sit with their <i>bottoms</i> level, so the line is 40 tall and their tops are
    ///     at 20, 0 and 10. That is also what makes a row of differently-sized badges line up along
    ///     their undersides in a browser, which is the everyday shape of this rule.
    /// </remarks>
    [Fact]
    public void Boxes_of_different_heights_align_on_their_baselines() {
        using var tree = new LayoutTree();
        var root = Root(tree, 300f);

        var short_ = Item(tree, root, Display.InlineBlock, 20f, 10f);
        var tall = Item(tree, root, Display.InlineBlock, 40f, 10f);
        var middling = Item(tree, root, Display.InlineBlock, 30f, 10f);

        tree.CalculateLayout(root, 300f, float.NaN, Direction.Ltr);

        Assert.Equal(20f, tree.GetTop(short_), Tolerance);
        Assert.Equal(0f, tree.GetTop(tall), Tolerance);
        Assert.Equal(10f, tree.GetTop(middling), Tolerance);

        Assert.Equal(40f, tree.GetHeight(root), Tolerance);
    }

    /// <summary><c>vertical-align: top</c> and <c>bottom</c> leave the baseline alone.</summary>
    /// <remarks>
    ///     §10.8.1: <c>top</c> aligns the box's top edge with the top of the line box and
    ///     <c>bottom</c> its bottom edge with the bottom. Neither takes part in fixing the baseline,
    ///     which the 40-point box below still owns — so the short box pinned to the top sits at 0
    ///     rather than at 20 where the baseline would have put it.
    /// </remarks>
    [Fact]
    public void Vertical_align_top_and_bottom_are_measured_from_the_line_box_edges() {
        using var tree = new LayoutTree();
        var root = Root(tree, 300f);

        var anchor = Item(tree, root, Display.InlineBlock, 40f, 10f);
        var pinnedTop = Item(tree, root, Display.InlineBlock, 12f, 10f);
        var pinnedBottom = Item(tree, root, Display.InlineBlock, 16f, 10f);

        tree.SetVerticalAlign(pinnedTop, VerticalAlign.Top);
        tree.SetVerticalAlign(pinnedBottom, VerticalAlign.Bottom);

        tree.CalculateLayout(root, 300f, float.NaN, Direction.Ltr);

        Assert.Equal(40f, tree.GetHeight(root), Tolerance);
        Assert.Equal(0f, tree.GetTop(anchor), Tolerance);
        Assert.Equal(0f, tree.GetTop(pinnedTop), Tolerance);
        Assert.Equal(40f - 16f, tree.GetTop(pinnedBottom), Tolerance);
    }

    /// <summary>
    ///     ⚠ <b>An inline-block's baseline is its <i>last</i> line box, not its first.</b>
    /// </summary>
    /// <remarks>
    ///     §10.8.1, and the word "last" is the whole test — a two-line inline-block hangs the text
    ///     beside it off its <i>second</i> line, which is why a multi-line block pushes its
    ///     neighbours down rather than lifting them. Getting it backwards is invisible on every
    ///     single-line case, which is most of them.
    ///     <para>
    ///         The subject here is a 30-point inline-block containing two stacked 15-point
    ///         inline-blocks. Each inner one synthesises its baseline at its own bottom edge, so the
    ///         outer box's last line box has its baseline at 30 — its own bottom — and a 10-point
    ///         neighbour therefore sits at 20. Had the <i>first</i> line been used the baseline would
    ///         be 15 and the neighbour would be at 5.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_inline_blocks_baseline_comes_from_its_last_line_box() {
        using var tree = new LayoutTree();
        var root = Root(tree, 40f);

        var subject = Item(tree, root, Display.InlineBlock, float.NaN, 20f);
        Item(tree, subject, Display.InlineBlock, 15f, 20f);
        Item(tree, subject, Display.InlineBlock, 15f, 20f);

        var neighbour = Item(tree, root, Display.InlineBlock, 10f, 10f);

        tree.CalculateLayout(root, 40f, float.NaN, Direction.Ltr);

        Assert.Equal(30f, tree.GetHeight(subject), Tolerance);
        Assert.Equal(0f, tree.GetTop(subject), Tolerance);
        Assert.Equal(20f, tree.GetTop(neighbour), Tolerance);
    }

    /// <summary>
    ///     ⚠ <b>…unless it clips, in which case the baseline is its bottom margin edge.</b>
    /// </summary>
    /// <remarks>
    ///     The clause of §10.8.1 that is most often dropped, and it is not an edge case: a card, a
    ///     badge and a chip all declare <c>overflow: hidden</c>, so this branch fires constantly. A
    ///     box whose content can scroll away has no business hanging its neighbours off a line inside
    ///     it.
    ///     <para>
    ///         Same tree as the test above with one declaration added, and the neighbour moves —
    ///         which is the only honest way to assert a rule whose effect is a fallback.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_clipping_inline_block_synthesises_its_baseline_at_its_bottom_edge() {
        using var tree = new LayoutTree();
        var root = Root(tree, 40f);

        var subject = Item(tree, root, Display.InlineBlock, float.NaN, 20f);
        tree.SetOverflow(subject, Overflow.Hidden, Overflow.Hidden);

        Item(tree, subject, Display.InlineBlock, 15f, 20f);
        Item(tree, subject, Display.InlineBlock, 15f, 20f);

        var neighbour = Item(tree, root, Display.InlineBlock, 10f, 10f);

        tree.CalculateLayout(root, 40f, float.NaN, Direction.Ltr);

        // The baseline is the bottom edge either way here, so what moves is nothing — the point is
        // that it is reached by the *fallback* and not by reading the inner line box. The inner
        // boxes are 15 apiece, so a first-line reading would put the neighbour at 5.
        Assert.Equal(30f, tree.GetHeight(subject), Tolerance);
        Assert.Equal(20f, tree.GetTop(neighbour), Tolerance);
    }

    /// <summary>
    ///     A run of inline-level children beside a block-level sibling shares one line, because
    ///     §9.2.1.1 wraps it in an anonymous block box.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This test used to assert the opposite, and inverting it is what closing the gap looks
    ///     like.</b> It was written as
    ///     <c>Mixed_content_stacks_because_there_are_no_anonymous_boxes</c> and it said the two boxes
    ///     below get a line each — which is what this store did while a block container could only
    ///     stack. §9.2.1.1 wraps each run of inline-level children in an <i>anonymous block box</i>,
    ///     and the two of them now share a line at y = 10 exactly as a browser puts them.
    ///     <para>
    ///         ⚠ The assertion is deliberately about <b>where the boxes are</b> and not about whether
    ///         a run was detected. A rule that resolves and then draws nothing is this codebase's
    ///         recurring defect, and the only test that catches it is one that reads geometry back
    ///         out: <c>secondInline</c> is at x = 20 <i>on the same line</i>, which is a number that
    ///         cannot come out of stacking.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Mixed_content_wraps_each_inline_run_in_an_anonymous_block_box() {
        using var tree = new LayoutTree();
        var root = Root(tree, 300f);

        var blockLevel = Item(tree, root, Display.Block, 10f, 20f);
        var firstInline = Item(tree, root, Display.InlineBlock, 10f, 20f);
        var secondInline = Item(tree, root, Display.InlineBlock, 10f, 20f);

        tree.CalculateLayout(root, 300f, float.NaN, Direction.Ltr);

        Assert.Equal(0f, tree.GetTop(blockLevel), Tolerance);

        Assert.Equal(10f, tree.GetTop(firstInline), Tolerance);
        Assert.Equal(0f, tree.GetLeft(firstInline), Tolerance);

        Assert.Equal(10f, tree.GetTop(secondInline), Tolerance);
        Assert.Equal(20f, tree.GetLeft(secondInline), Tolerance);

        // Ten for the block-level box and ten for the one line the run needed, rather than thirty.
        Assert.Equal(20f, tree.GetHeight(root), Tolerance);
    }

    /// <summary>
    ///     <c>&lt;div&gt;text&lt;p&gt;para&lt;/p&gt;more text&lt;/div&gt;</c> — two anonymous block
    ///     boxes with a real one between them.
    /// </summary>
    /// <remarks>
    ///     The case §9.2.1.1 is actually written for, and the one the old stacking answer got most
    ///     visibly wrong. There are three block-level boxes in the container and only one of them has
    ///     a node: the run before the paragraph, the paragraph, and the run after it.
    /// </remarks>
    [Fact]
    public void An_inline_run_on_either_side_of_a_block_sibling_is_two_anonymous_boxes() {
        using var tree = new LayoutTree();
        var root = Root(tree, 300f);

        var leadIn = Item(tree, root, Display.InlineBlock, 20f, 30f);
        var alsoLeadIn = Item(tree, root, Display.InlineBlock, 20f, 30f);
        var paragraph = Item(tree, root, Display.Block, 10f, 20f);
        var tail = Item(tree, root, Display.InlineBlock, 15f, 30f);

        tree.CalculateLayout(root, 300f, float.NaN, Direction.Ltr);

        // First anonymous box: one line, twenty tall.
        Assert.Equal(0f, tree.GetTop(leadIn), Tolerance);
        Assert.Equal(0f, tree.GetLeft(leadIn), Tolerance);
        Assert.Equal(0f, tree.GetTop(alsoLeadIn), Tolerance);
        Assert.Equal(30f, tree.GetLeft(alsoLeadIn), Tolerance);

        // The real block-level box stacks after it, and the second run stacks after that.
        Assert.Equal(20f, tree.GetTop(paragraph), Tolerance);
        Assert.Equal(30f, tree.GetTop(tail), Tolerance);
        Assert.Equal(0f, tree.GetLeft(tail), Tolerance);

        Assert.Equal(45f, tree.GetHeight(root), Tolerance);
    }

    /// <summary>An anonymous block box breaks its run onto as many lines as the run needs.</summary>
    /// <remarks>
    ///     ⚠ <b>The height of an anonymous block box is the only thing about it that anything else can
    ///     see</b>, so this is the assertion that matters most: the block-level box <i>after</i> the
    ///     run has to move down by three line boxes and not by one. A run that flowed correctly and
    ///     then reported a single line's height would place every box in this test right and the
    ///     container's own height wrong.
    /// </remarks>
    [Fact]
    public void An_anonymous_block_box_is_as_tall_as_the_lines_its_run_broke_onto() {
        using var tree = new LayoutTree();
        var root = Root(tree, 100f);

        var head = Item(tree, root, Display.Block, 10f, 100f);
        var first = Item(tree, root, Display.InlineBlock, 20f, 60f);
        var second = Item(tree, root, Display.InlineBlock, 20f, 60f);
        var third = Item(tree, root, Display.InlineBlock, 20f, 60f);
        var tail = Item(tree, root, Display.Block, 10f, 100f);

        tree.CalculateLayout(root, 100f, float.NaN, Direction.Ltr);

        Assert.Equal(0f, tree.GetTop(head), Tolerance);

        // Sixty apiece against a hundred-point line, so one to a line.
        Assert.Equal(10f, tree.GetTop(first), Tolerance);
        Assert.Equal(30f, tree.GetTop(second), Tolerance);
        Assert.Equal(50f, tree.GetTop(third), Tolerance);

        Assert.Equal(70f, tree.GetTop(tail), Tolerance);
        Assert.Equal(80f, tree.GetHeight(root), Tolerance);
    }

    /// <summary>
    ///     An anonymous block box has no margins of its own, and is a barrier all the same.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Both halves are load-bearing and they pull in opposite directions.</b> An anonymous
    ///     block box takes initial values for every non-inherited property, so its own margins are
    ///     zero and it never adds to an adjoining set — but it is not <i>transparent</i>: it holds a
    ///     line box by construction, which is precisely what §8.3.1 means by something separating a
    ///     box's two margins. So the twenty above it and the thirty below it are spent separately and
    ///     do not meet. Get the second half wrong and the run's neighbours collapse through it, which
    ///     puts the trailing block box at y = 40 instead of y = 70.
    /// </remarks>
    [Fact]
    public void An_anonymous_block_box_separates_the_margins_on_either_side_of_it() {
        using var tree = new LayoutTree();
        var root = Root(tree, 300f);

        var head = Item(tree, root, Display.Block, 10f, 20f);
        tree.SetMargin(head, Edge.Bottom, StyleLength.Points(20f));

        var run = Item(tree, root, Display.InlineBlock, 10f, 20f);

        var tail = Item(tree, root, Display.Block, 10f, 20f);
        tree.SetMargin(tail, Edge.Top, StyleLength.Points(30f));

        tree.CalculateLayout(root, 300f, float.NaN, Direction.Ltr);

        Assert.Equal(0f, tree.GetTop(head), Tolerance);
        Assert.Equal(30f, tree.GetTop(run), Tolerance);
        Assert.Equal(70f, tree.GetTop(tail), Tolerance);
        Assert.Equal(80f, tree.GetHeight(root), Tolerance);
    }

    /// <summary>A <c>display: none</c> child does not split a run in two.</summary>
    /// <remarks>
    ///     §9.2.1.1 breaks a run at <i>block-level</i> content, and a box that generates no box at all
    ///     is not that. Splitting on one would put the two inline-level boxes below on separate lines
    ///     for a sibling that is not there.
    /// </remarks>
    [Fact]
    public void A_hidden_child_does_not_split_an_anonymous_block_box() {
        using var tree = new LayoutTree();
        var root = Root(tree, 300f);

        Item(tree, root, Display.Block, 10f, 20f);

        var before = Item(tree, root, Display.InlineBlock, 10f, 20f);
        Item(tree, root, Display.None, 10f, 20f);
        var after = Item(tree, root, Display.InlineBlock, 10f, 20f);

        tree.CalculateLayout(root, 300f, float.NaN, Direction.Ltr);

        Assert.Equal(10f, tree.GetTop(before), Tolerance);
        Assert.Equal(10f, tree.GetTop(after), Tolerance);
        Assert.Equal(20f, tree.GetLeft(after), Tolerance);
    }

    /// <summary>An anonymous block box runs from the right edge in a right-to-left container.</summary>
    /// <remarks>
    ///     The anonymous box's content box is the container's, so the line walk's inline-start edge is
    ///     the container's right one — the same mirroring <c>PlaceLine</c> applies for a pure inline
    ///     formatting context, reached over a sub-range this time.
    /// </remarks>
    [Fact]
    public void A_right_to_left_anonymous_block_box_starts_at_the_right_edge() {
        using var tree = new LayoutTree();
        var root = Root(tree, 100f);

        var head = Item(tree, root, Display.Block, 10f, 20f);
        var first = Item(tree, root, Display.InlineBlock, 10f, 30f);
        var second = Item(tree, root, Display.InlineBlock, 10f, 30f);

        tree.CalculateLayout(root, 100f, float.NaN, Direction.Rtl);

        Assert.Equal(80f, tree.GetLeft(head), Tolerance);

        Assert.Equal(10f, tree.GetTop(first), Tolerance);
        Assert.Equal(70f, tree.GetLeft(first), Tolerance);
        Assert.Equal(10f, tree.GetTop(second), Tolerance);
        Assert.Equal(40f, tree.GetLeft(second), Tolerance);
    }

    /// <summary>
    ///     A shrink-to-fit mixed container is as wide as its widest anonymous block box, not as wide
    ///     as its widest single child.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The intrinsic-size half, and the one that is easy to leave out because nothing looks
    ///     broken until a container has to size itself.</b> A block container's content width is a
    ///     <i>maximum</i> over its children, which is the right operator over block-level boxes and
    ///     the wrong one over a run that shares a line: it answers the run's <i>minimum</i>. Here the
    ///     three 25-point boxes want 75 between them; taking the maximum instead gives 25, and the run
    ///     then wraps onto three lines inside a container it fits on one line of.
    /// </remarks>
    [Fact]
    public void A_shrink_to_fit_mixed_container_is_as_wide_as_its_run_wants() {
        using var tree = new LayoutTree();
        var root = Root(tree, 300f);

        // Inline-level, so the root asks it for its own width rather than imposing one.
        var host = Item(tree, root, Display.InlineBlock, float.NaN, float.NaN);

        Item(tree, host, Display.Block, 10f, 20f);
        var first = Item(tree, host, Display.InlineBlock, 10f, 25f);
        Item(tree, host, Display.InlineBlock, 10f, 25f);
        var last = Item(tree, host, Display.InlineBlock, 10f, 25f);

        tree.CalculateLayout(root, 300f, float.NaN, Direction.Ltr);

        Assert.Equal(75f, tree.GetWidth(host), Tolerance);

        // …and having asked for 75 it uses all of it: one line, not three.
        Assert.Equal(10f, tree.GetTop(first), Tolerance);
        Assert.Equal(10f, tree.GetTop(last), Tolerance);
        Assert.Equal(50f, tree.GetLeft(last), Tolerance);
        Assert.Equal(20f, tree.GetHeight(host), Tolerance);
    }

    /// <summary>
    ///     A mixed container whose last content is a run hangs from that run's last line box.
    /// </summary>
    /// <remarks>
    ///     §10.8.1 puts a flow container's baseline on its last line box in normal flow, and a mixed
    ///     container has line boxes now. The run below is deliberately one whose baseline is
    ///     <i>not</i> its bottom edge — a <c>vertical-align: top</c> box grows the line downwards
    ///     without moving the baseline — so synthesising from the bottom margin edge instead, which is
    ///     what a block container without line boxes must do, gives a different answer and this test
    ///     sees it.
    ///     <para>
    ///         ⚠ Only when the run is <i>last</i>. After a real block-level child the last line box in
    ///         normal flow is somewhere inside that child, and nothing here can reach it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_mixed_container_ending_in_a_run_hangs_from_that_run_s_baseline() {
        using var tree = new LayoutTree();
        var root = Root(tree, 300f);

        var subject = Item(tree, root, Display.InlineBlock, float.NaN, float.NaN);
        Item(tree, subject, Display.Block, 20f, 10f);
        Item(tree, subject, Display.InlineBlock, 10f, 10f);

        var pinnedTop = Item(tree, subject, Display.InlineBlock, 30f, 10f);
        tree.SetVerticalAlign(pinnedTop, VerticalAlign.Top);

        var neighbour = Item(tree, root, Display.InlineBlock, 40f, 10f);

        tree.CalculateLayout(root, 300f, float.NaN, Direction.Ltr);

        // Twenty for the block child plus a thirty-tall line box, whose baseline is only ten below
        // its own top because the tall box on it is anchored to the line's top edge.
        Assert.Equal(50f, tree.GetHeight(subject), Tolerance);

        // So the subject's baseline is at 30 and the neighbour's at 40: the deeper of the two fixes
        // the line, and the subject drops ten. Reading the bottom margin edge instead would make the
        // subject the deeper one and swap these two numbers.
        Assert.Equal(0f, tree.GetTop(neighbour), Tolerance);
        Assert.Equal(10f, tree.GetTop(subject), Tolerance);
    }

    /// <summary>A line runs from the right in a right-to-left container.</summary>
    /// <remarks>
    ///     The inline axis is what a line advances along, so the first item on a line is the rightmost
    ///     one under <see cref="Direction.Rtl" />. Nothing about the vertical alignment changes.
    /// </remarks>
    [Fact]
    public void A_right_to_left_line_places_its_first_item_at_the_right_edge() {
        using var tree = new LayoutTree();
        var root = Root(tree, 100f);

        var first = Item(tree, root, Display.InlineBlock, 10f, 30f);
        var second = Item(tree, root, Display.InlineBlock, 10f, 20f);

        tree.CalculateLayout(root, 100f, float.NaN, Direction.Rtl);

        Assert.Equal(70f, tree.GetLeft(first), Tolerance);
        Assert.Equal(50f, tree.GetLeft(second), Tolerance);
    }

    /// <summary>
    ///     ⚠ <b>The five font-relative <c>vertical-align</c> values fall back to
    ///     <see cref="VerticalAlign.Baseline" />, and that is asserted rather than left to be
    ///     discovered.</b>
    /// </summary>
    /// <remarks>
    ///     <c>middle</c>, <c>text-top</c>, <c>text-bottom</c>, <c>sub</c> and <c>super</c> are each
    ///     defined against the parent's strut — its font's x-height, ascent or descent — and this
    ///     project has no font to ask; <c>FontRegistry</c> is a layer out. A layout engine has to do
    ///     <i>something</i> with a value it cannot honour, and falling back is the only safe choice;
    ///     what it must not do is let the layer above call the family supported. So this test pins the
    ///     fallback, <c>LayoutStyleBuilder</c> maps only the three that work, and the utilities that
    ///     emit the other five stay in the editor's inert inventory with a task number against them.
    /// </remarks>
    [Theory]
    [InlineData(VerticalAlign.Middle)]
    [InlineData(VerticalAlign.TextTop)]
    [InlineData(VerticalAlign.TextBottom)]
    [InlineData(VerticalAlign.Sub)]
    [InlineData(VerticalAlign.Super)]
    public void A_font_relative_vertical_align_falls_back_to_the_baseline(VerticalAlign requested) {
        using var tree = new LayoutTree();
        var root = Root(tree, 300f);

        Item(tree, root, Display.InlineBlock, 40f, 10f);
        var subject = Item(tree, root, Display.InlineBlock, 20f, 10f);
        tree.SetVerticalAlign(subject, requested);

        tree.CalculateLayout(root, 300f, float.NaN, Direction.Ltr);

        // Baseline-aligned against a 40-point anchor: bottoms level, so the top is at 20. `middle`
        // would be at 10 plus half an x-height, and `text-top` at 0.
        Assert.Equal(20f, tree.GetTop(subject), Tolerance);
    }

    /// <summary>
    ///     An inline formatting context is a barrier to margin collapsing, in both directions.
    /// </summary>
    /// <remarks>
    ///     §8.3.1 collapses the vertical margins of <i>block-level</i> boxes in the same formatting
    ///     context. Inline-level boxes are neither, so nothing here collapses with anything — and an
    ///     inline-level box is itself a formatting context root, which is what stops an empty one
    ///     letting the margins on either side of it meet through it.
    /// </remarks>
    [Fact]
    public void Margins_do_not_collapse_through_an_inline_formatting_context() {
        using var tree = new LayoutTree();
        var outer = Root(tree, 300f);

        var host = Item(tree, outer, Display.Block, float.NaN, float.NaN);
        var inline = Item(tree, host, Display.InlineBlock, 10f, 10f);
        tree.SetMargin(inline, Edge.Top, StyleLength.Points(20f));

        tree.CalculateLayout(outer, 300f, float.NaN, Direction.Ltr);

        // The 20-point margin stays inside: it did not escape past the host's top edge, so the host
        // begins at 0 and is 30 tall rather than beginning at 20 and being 10 tall.
        Assert.Equal(0f, tree.GetTop(host), Tolerance);
        Assert.Equal(30f, tree.GetHeight(host), Tolerance);
    }

    static LayoutNodeId Root(LayoutTree tree, float width) {
        var root = tree.CreateNode();
        tree.SetDisplay(root, Display.Block);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(width));

        return root;
    }

    static LayoutNodeId Item(LayoutTree tree, LayoutNodeId parent, Display display, float height, float width) {
        var node = tree.CreateNode();
        tree.SetDisplay(node, display);

        if (!float.IsNaN(height)) {
            tree.SetDimension(node, Dimension.Height, StyleLength.Points(height));
        }

        if (!float.IsNaN(width)) {
            tree.SetDimension(node, Dimension.Width, StyleLength.Points(width));
        }

        tree.AddChild(parent, node);

        return node;
    }

    /// <summary>A <c>flex: 1</c> child, which is what the WPT fixture puts inside its inline-flex.</summary>
    static LayoutNodeId Grower(LayoutTree tree, LayoutNodeId parent) {
        var node = tree.CreateNode();
        tree.SetFlexGrow(node, 1f);
        tree.SetFlexShrink(node, 1f);
        tree.SetFlexBasis(node, StyleLength.Points(0f));
        tree.AddChild(parent, node);

        return node;
    }
}
