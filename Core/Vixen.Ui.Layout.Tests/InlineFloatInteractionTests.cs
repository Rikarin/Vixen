// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Layout.Tests;

/// <summary>
///     CSS 2.1 §9.5's main clause: a float shortens the line boxes beside it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every expected number in this file was read out of a real Chrome, and the reason it
///         had to be is the whole story of the bug.</b> Floats were implemented against Taffy's 84
///         float fixtures and all 84 pass — but <c>grep -c '&lt;text' Corpus/float.xml</c> is 0, so
///         the corpus named after the feature contains no inline content and cannot test the one
///         behaviour a non-specialist would name if asked what a float does. <c>WalkInlineLines</c>
///         asked the exclusion list nothing, and a paragraph beside a float ran straight under it.
///         See <c>InlineKnownGaps.txt</c>, entry <i>float interaction with lines</i>, and
///         <c>Taffy/FloatKnownGaps.txt</c>, which is a page about the same hole.
///     </para>
///     <para>
///         <b>How the numbers were obtained, so that the next reader can redo it rather than trust
///         it.</b> Each case below was written as an HTML fixture, served over <c>http://localhost</c>
///         and laid out by <b>Chrome 148.0.7778.280</b>, and every box's
///         <c>getBoundingClientRect()</c> was differenced against its container's to give the
///         container-relative rectangle this store reports. Not one number here was computed from
///         Vixen's own answer — a test whose expectation came from the implementation it is testing
///         proves only that the implementation is deterministic.
///     </para>
///     <para>
///         ⚠ <b>Two properties of the fixtures are load-bearing and neither is decoration.</b> First,
///         every box carries an explicit <c>width</c> and <c>height</c>, so no number depends on a
///         font — the same property that let <c>css-flexbox/inline-flex.html</c> cross into
///         <see cref="InlineFormattingTests" /> and stopped the rest of WPT's inline suite. Second,
///         each fixture sets <c>font-size: 0; line-height: 0</c>, which zeroes §10.8's <i>strut</i>.
///         This store has no font and therefore no strut, so a Chrome line box is only comparable
///         with a Vixen one once the strut has been taken out of it. Leaving it in makes every line
///         a few points taller in Chrome and every expectation here wrong by a font metric.
///     </para>
///     <para>
///         ⚠ <b>And each fixture is a <c>display: flow-root</c>, which is not cosmetic either.</b> A
///         float overflows the bottom of a plain block that does not contain it, and goes on
///         narrowing the NEXT sibling's line boxes — correct CSS, and it silently contaminated the
///         first run of this oracle: two cases came back with their float 50 points to the right of
///         where their own markup put it. Every root here contains its own floats, which is also what
///         a <see cref="LayoutTree" /> root does.
///     </para>
/// </remarks>
public class InlineFloatInteractionTests {
    const float Tolerance = 0.0001f;

    /// <summary>Three 50-wide items fit beside a 50-wide float in a 200-wide box, not four.</summary>
    /// <remarks>
    ///     The headline case. Chrome: <c>x = 50, 100, 150</c> on the first line and <c>50, 100</c> on
    ///     the second, both of them shortened because the float is 50 tall and the two lines are 20
    ///     each. Before this landed, Vixen answered <c>0, 50, 100, 150</c> and then a second line
    ///     holding the fifth item — four items on a line the float leaves room for three of.
    /// </remarks>
    [Fact]
    public void A_line_box_beside_a_left_float_is_shortened_to_the_band() {
        using var tree = new LayoutTree();
        var root = Root(tree, 200f);

        Float(tree, root, FloatSide.Left, 50f, 50f);
        var a = Inline(tree, root, 50f, 20f);
        var b = Inline(tree, root, 50f, 20f);
        var c = Inline(tree, root, 50f, 20f);
        var d = Inline(tree, root, 50f, 20f);
        var e = Inline(tree, root, 50f, 20f);

        tree.CalculateLayout(root, 200f, float.NaN, Direction.Ltr);

        AssertAt(tree, a, 50f, 0f);
        AssertAt(tree, b, 100f, 0f);
        AssertAt(tree, c, 150f, 0f);
        AssertAt(tree, d, 50f, 20f);
        AssertAt(tree, e, 100f, 20f);

        // The container is as tall as the float it contains, not as its two 20-point lines.
        Assert.Equal(50f, tree.GetHeight(root), Tolerance);
    }

    /// <summary>A line that starts below the float's bottom edge gets the whole width back.</summary>
    /// <remarks>
    ///     Chrome, with ten 50×30 items beside a 50×50 float in a 200-wide box: three on the line at
    ///     <c>y = 0</c>, three on the line at <c>y = 30</c>, and then <b>four</b> at <c>y = 60</c>,
    ///     which is past the float. This is the half a clamp would get wrong — narrowing every line
    ///     in the container rather than only the ones the float crosses.
    /// </remarks>
    [Fact]
    public void A_line_below_the_float_goes_back_to_full_width() {
        using var tree = new LayoutTree();
        var root = Root(tree, 200f);

        Float(tree, root, FloatSide.Left, 50f, 50f);
        var items = new LayoutNodeId[10];
        for (var i = 0; i < items.Length; i++) {
            items[i] = Inline(tree, root, 50f, 30f);
        }

        tree.CalculateLayout(root, 200f, float.NaN, Direction.Ltr);

        AssertAt(tree, items[0], 50f, 0f);
        AssertAt(tree, items[1], 100f, 0f);
        AssertAt(tree, items[2], 150f, 0f);
        AssertAt(tree, items[3], 50f, 30f);
        AssertAt(tree, items[4], 100f, 30f);
        AssertAt(tree, items[5], 150f, 30f);
        AssertAt(tree, items[6], 0f, 60f);
        AssertAt(tree, items[7], 50f, 60f);
        AssertAt(tree, items[8], 100f, 60f);
        AssertAt(tree, items[9], 150f, 60f);

        Assert.Equal(90f, tree.GetHeight(root), Tolerance);
    }

    /// <summary>A right float takes the far end of the line rather than its start.</summary>
    /// <remarks>
    ///     Chrome puts the 60-wide right float at <c>x = 140</c> in a 200-wide box and answers
    ///     <c>x = 0, 50</c> for the items: the line still begins at the content edge and simply
    ///     runs out 60 points early.
    /// </remarks>
    [Fact]
    public void A_right_float_takes_the_far_end_of_the_line() {
        using var tree = new LayoutTree();
        var root = Root(tree, 200f);

        Float(tree, root, FloatSide.Right, 60f, 40f);
        var a = Inline(tree, root, 50f, 20f);
        var b = Inline(tree, root, 50f, 20f);
        var c = Inline(tree, root, 50f, 20f);
        var d = Inline(tree, root, 50f, 20f);

        tree.CalculateLayout(root, 200f, float.NaN, Direction.Ltr);

        AssertAt(tree, a, 0f, 0f);
        AssertAt(tree, b, 50f, 0f);
        AssertAt(tree, c, 0f, 20f);
        AssertAt(tree, d, 50f, 20f);
        Assert.Equal(40f, tree.GetHeight(root), Tolerance);
    }

    /// <summary>Floats on both sides narrow a line from both ends at once.</summary>
    /// <remarks>
    ///     A 40-wide left float and a 30-wide right float in a 200-wide box leave a band of 130, so
    ///     Chrome fits two 50-wide items at <c>x = 40</c> and <c>x = 90</c> and puts the third on the
    ///     next line at <c>x = 40</c>.
    /// </remarks>
    [Fact]
    public void Floats_on_both_sides_narrow_a_line_from_both_ends() {
        using var tree = new LayoutTree();
        var root = Root(tree, 200f);

        Float(tree, root, FloatSide.Left, 40f, 60f);
        Float(tree, root, FloatSide.Right, 30f, 60f);
        var a = Inline(tree, root, 50f, 20f);
        var b = Inline(tree, root, 50f, 20f);
        var c = Inline(tree, root, 50f, 20f);

        tree.CalculateLayout(root, 200f, float.NaN, Direction.Ltr);

        AssertAt(tree, a, 40f, 0f);
        AssertAt(tree, b, 90f, 0f);
        AssertAt(tree, c, 40f, 20f);
        Assert.Equal(60f, tree.GetHeight(root), Tolerance);
    }

    /// <summary>
    ///     A float shorter than the line box still narrows it, which a zero-height probe would miss.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is the case that decides how the band is asked for, and it is why the walk
    ///     probes twice.</b> <c>FloatBandAt</c> deliberately excludes a float whose top edge is
    ///     exactly the slice's top — that strictness is what lets a cleared box sit flush against
    ///     what it cleared — so a zero-height probe at the first line's top sees no float whatsoever.
    ///     Chrome puts three items at <c>x = 50, 100, 150</c> beside a float only 10 points tall, and
    ///     the fourth at <c>x = 0</c> on the line below it. A single-probe implementation answers
    ///     four items at <c>0, 50, 100, 150</c> and is wrong on the very first line of the very first
    ///     case anybody would try.
    /// </remarks>
    [Fact]
    public void A_float_shorter_than_the_line_still_narrows_it() {
        using var tree = new LayoutTree();
        var root = Root(tree, 200f);

        Float(tree, root, FloatSide.Left, 50f, 10f);
        var a = Inline(tree, root, 50f, 20f);
        var b = Inline(tree, root, 50f, 20f);
        var c = Inline(tree, root, 50f, 20f);
        var d = Inline(tree, root, 50f, 20f);

        tree.CalculateLayout(root, 200f, float.NaN, Direction.Ltr);

        AssertAt(tree, a, 50f, 0f);
        AssertAt(tree, b, 100f, 0f);
        AssertAt(tree, c, 150f, 0f);
        AssertAt(tree, d, 0f, 20f);
        Assert.Equal(40f, tree.GetHeight(root), Tolerance);
    }

    /// <summary>The exclusion is the float's margin box, not its border box.</summary>
    /// <remarks>
    ///     A 40-wide float with <c>margin: 0 20px 0 10px</c> sits at <c>x = 10</c> and excludes
    ///     <c>[0, 70]</c>. Chrome answers <c>x = 70, 120</c> for two 50-wide items and puts the third
    ///     at <c>x = 70</c> on the next line — the 130 points left over take two, not three.
    /// </remarks>
    [Fact]
    public void The_exclusion_is_the_floats_margin_box() {
        using var tree = new LayoutTree();
        var root = Root(tree, 200f);

        var box = Float(tree, root, FloatSide.Left, 40f, 40f);
        tree.SetMargin(box, Edge.Left, StyleLength.Points(10f));
        tree.SetMargin(box, Edge.Right, StyleLength.Points(20f));

        var a = Inline(tree, root, 50f, 20f);
        var b = Inline(tree, root, 50f, 20f);
        var c = Inline(tree, root, 50f, 20f);

        tree.CalculateLayout(root, 200f, float.NaN, Direction.Ltr);

        Assert.Equal(10f, tree.GetLeft(box), Tolerance);
        AssertAt(tree, a, 70f, 0f);
        AssertAt(tree, b, 120f, 0f);
        AssertAt(tree, c, 70f, 20f);
    }

    /// <summary>An item's own margins are charged against the band, not against the container.</summary>
    /// <remarks>
    ///     Three 50-wide items with <c>margin: 0 10px</c> are 70 points of line apiece. Chrome fits
    ///     two in the 150-point band beside a 50-wide float — border edges at <c>x = 60</c> and
    ///     <c>x = 130</c> — and moves the third to the next line at <c>x = 60</c>.
    /// </remarks>
    [Fact]
    public void An_items_own_margins_count_against_the_band() {
        using var tree = new LayoutTree();
        var root = Root(tree, 200f);

        Float(tree, root, FloatSide.Left, 50f, 50f);

        var items = new LayoutNodeId[3];
        for (var i = 0; i < items.Length; i++) {
            items[i] = Inline(tree, root, 50f, 20f);
            tree.SetMargin(items[i], Edge.Left, StyleLength.Points(10f));
            tree.SetMargin(items[i], Edge.Right, StyleLength.Points(10f));
        }

        tree.CalculateLayout(root, 200f, float.NaN, Direction.Ltr);

        AssertAt(tree, items[0], 60f, 0f);
        AssertAt(tree, items[1], 130f, 0f);
        AssertAt(tree, items[2], 60f, 20f);
    }

    /// <summary>The band is measured from the container's content box, padding and border included.</summary>
    /// <remarks>
    ///     ⚠ <b>Three coordinate systems meet here and the arithmetic is silent when it is wrong.</b>
    ///     The exclusion list is in the formatting context root's <i>content</i> coordinates and a
    ///     line box is addressed in the container's, so the conversion has to subtract the container's
    ///     own inset. Chrome, with a 5-point border and 10/15 padding round a 200-wide content box:
    ///     the float lands at <c>(20, 15)</c> and the items at <c>x = 70, 120, 170</c> —
    ///     <c>20 + 50</c>, not <c>50</c> and not <c>70 + 20</c>.
    /// </remarks>
    [Fact]
    public void The_band_is_measured_from_the_container_content_box() {
        using var tree = new LayoutTree();
        var root = Root(tree, 240f);
        tree.SetBorder(root, Edge.All, StyleLength.Points(5f));
        tree.SetPadding(root, Edge.Left, StyleLength.Points(15f));
        tree.SetPadding(root, Edge.Right, StyleLength.Points(15f));
        tree.SetPadding(root, Edge.Top, StyleLength.Points(10f));
        tree.SetPadding(root, Edge.Bottom, StyleLength.Points(10f));

        var box = Float(tree, root, FloatSide.Left, 50f, 50f);
        var a = Inline(tree, root, 50f, 20f);
        var b = Inline(tree, root, 50f, 20f);
        var c = Inline(tree, root, 50f, 20f);
        var d = Inline(tree, root, 50f, 20f);

        tree.CalculateLayout(root, 240f, float.NaN, Direction.Ltr);

        AssertAt(tree, box, 20f, 15f);
        AssertAt(tree, a, 70f, 15f);
        AssertAt(tree, b, 120f, 15f);
        AssertAt(tree, c, 170f, 15f);
        AssertAt(tree, d, 70f, 35f);
        Assert.Equal(80f, tree.GetHeight(root), Tolerance);
    }

    /// <summary>A run that starts below a block sibling asks for the band at its own top.</summary>
    /// <remarks>
    ///     The anonymous block box is not at the container's top edge here, so the line's block
    ///     position has to be threaded into the query rather than assumed to be zero. Chrome puts a
    ///     25-tall block first, then the float at <c>y = 25</c>, then three items at <c>y = 25</c> and
    ///     the fourth at <c>y = 45</c>, all of them at <c>x = 50</c> onwards.
    /// </remarks>
    [Fact]
    public void A_run_below_a_block_sibling_asks_for_the_band_at_its_own_top() {
        using var tree = new LayoutTree();
        var root = Root(tree, 200f);

        var header = tree.CreateNode();
        tree.SetDisplay(header, Display.Block);
        tree.SetDimension(header, Dimension.Height, StyleLength.Points(25f));
        tree.AddChild(root, header);

        var box = Float(tree, root, FloatSide.Left, 50f, 50f);
        var a = Inline(tree, root, 50f, 20f);
        var b = Inline(tree, root, 50f, 20f);
        var c = Inline(tree, root, 50f, 20f);
        var d = Inline(tree, root, 50f, 20f);

        tree.CalculateLayout(root, 200f, float.NaN, Direction.Ltr);

        AssertAt(tree, box, 0f, 25f);
        AssertAt(tree, a, 50f, 25f);
        AssertAt(tree, b, 100f, 25f);
        AssertAt(tree, c, 150f, 25f);
        AssertAt(tree, d, 50f, 45f);
        Assert.Equal(75f, tree.GetHeight(root), Tolerance);
    }

    /// <summary>A float narrows the lines of a nested block it is not the parent of.</summary>
    /// <remarks>
    ///     ⚠ <b>This is what makes a float different from every other box in this store.</b> §9.5's
    ///     unit is the block formatting context, not the box, so a float placed by one container
    ///     shortens the line boxes of a <i>cousin</i>. The inner block is a plain <c>block</c> that
    ///     shares its parent's formatting context: Chrome leaves it at <c>x = 0</c> and full width —
    ///     it does not move aside, because only a formatting context root does that — and shortens
    ///     its lines anyway, answering <c>x = 50, 100, 150</c> for the first three items.
    /// </remarks>
    [Fact]
    public void A_float_narrows_the_lines_of_a_nested_block_it_is_not_the_parent_of() {
        using var tree = new LayoutTree();
        var root = Root(tree, 200f);

        Float(tree, root, FloatSide.Left, 50f, 50f);

        var paragraph = tree.CreateNode();
        tree.SetDisplay(paragraph, Display.Block);
        tree.AddChild(root, paragraph);

        var a = Inline(tree, paragraph, 50f, 20f);
        var b = Inline(tree, paragraph, 50f, 20f);
        var c = Inline(tree, paragraph, 50f, 20f);
        var d = Inline(tree, paragraph, 50f, 20f);

        tree.CalculateLayout(root, 200f, float.NaN, Direction.Ltr);

        AssertAt(tree, paragraph, 0f, 0f);
        Assert.Equal(200f, tree.GetWidth(paragraph), Tolerance);
        AssertAt(tree, a, 50f, 0f);
        AssertAt(tree, b, 100f, 0f);
        AssertAt(tree, c, 150f, 0f);
        AssertAt(tree, d, 50f, 20f);
    }

    /// <summary>
    ///     A formatting context root that has already moved aside does not shorten its lines as well.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The float would otherwise be counted twice, and the second count is the bug.</b> §9.5
    ///     gives a box that establishes a block formatting context the stronger treatment: it may not
    ///     overlap the float's margin box at all, so the parent's walk slides the whole box out from
    ///     under it. Its own line boxes are then in a formatting context the float is not in, and must
    ///     be full width. Chrome puts the 200-wide <c>overflow: hidden</c> box at <c>x = 50</c> in a
    ///     260-wide parent and then fits <b>four</b> 50-wide items on its first line, at
    ///     <c>x = 0, 50, 100, 150</c> inside it.
    /// </remarks>
    [Fact]
    public void A_formatting_context_root_does_not_shorten_lines_by_a_float_it_has_cleared() {
        using var tree = new LayoutTree();
        var root = Root(tree, 260f);

        Float(tree, root, FloatSide.Left, 50f, 50f);

        var paragraph = tree.CreateNode();
        tree.SetDisplay(paragraph, Display.Block);
        tree.SetOverflow(paragraph, Overflow.Hidden);
        tree.SetDimension(paragraph, Dimension.Width, StyleLength.Points(200f));
        tree.AddChild(root, paragraph);

        var a = Inline(tree, paragraph, 50f, 20f);
        var b = Inline(tree, paragraph, 50f, 20f);
        var c = Inline(tree, paragraph, 50f, 20f);
        var d = Inline(tree, paragraph, 50f, 20f);
        var e = Inline(tree, paragraph, 50f, 20f);

        tree.CalculateLayout(root, 260f, float.NaN, Direction.Ltr);

        AssertAt(tree, paragraph, 50f, 0f);
        AssertAt(tree, a, 0f, 0f);
        AssertAt(tree, b, 50f, 0f);
        AssertAt(tree, c, 100f, 0f);
        AssertAt(tree, d, 150f, 0f);
        AssertAt(tree, e, 0f, 20f);
    }

    // ── §9.5's other clause: a line with no room for content moves down ─────────────────────────

    /// <summary>A line box with no room for its first item is moved below the float.</summary>
    /// <remarks>
    ///     §9.5: "if a shortened line box is too small to contain any content, then it is shifted
    ///     downward until either it fits or there are no more floats present". Chrome, with a 60-wide
    ///     float in a 100-wide box and 50-wide items: the band beside the float is 40 and the whole
    ///     line goes to <c>y = 40</c>, where both items fit at <c>x = 0</c> and <c>x = 50</c>. Without
    ///     this the line overflows the band instead and the items sit on top of the float.
    /// </remarks>
    [Fact]
    public void A_line_with_no_room_for_its_first_item_moves_below_the_float() {
        using var tree = new LayoutTree();
        var root = Root(tree, 100f);

        Float(tree, root, FloatSide.Left, 60f, 40f);
        var a = Inline(tree, root, 50f, 20f);
        var b = Inline(tree, root, 50f, 20f);

        tree.CalculateLayout(root, 100f, float.NaN, Direction.Ltr);

        AssertAt(tree, a, 0f, 40f);
        AssertAt(tree, b, 50f, 40f);
        Assert.Equal(60f, tree.GetHeight(root), Tolerance);
    }

    /// <summary>The line drops past each float in turn until one of the bands fits.</summary>
    /// <remarks>
    ///     Two stacked left floats — 70 wide over 60 wide, because the second cannot fit beside the
    ///     first in a 100-wide box — leave bands of 30 and then 40, neither of which holds a 50-wide
    ///     item. Chrome drops the line twice and answers <c>y = 60</c>. A single drop answers 30.
    /// </remarks>
    [Fact]
    public void A_line_drops_past_each_float_in_turn_until_one_fits() {
        using var tree = new LayoutTree();
        var root = Root(tree, 100f);

        var first = Float(tree, root, FloatSide.Left, 70f, 30f);
        var second = Float(tree, root, FloatSide.Left, 60f, 30f);
        var a = Inline(tree, root, 50f, 20f);

        tree.CalculateLayout(root, 100f, float.NaN, Direction.Ltr);

        AssertAt(tree, first, 0f, 0f);
        AssertAt(tree, second, 0f, 30f);
        AssertAt(tree, a, 0f, 60f);
        Assert.Equal(80f, tree.GetHeight(root), Tolerance);
    }

    /// <summary>With nothing below it, the line drops all the way to the float's bottom edge.</summary>
    /// <remarks>
    ///     A 200-tall float is not something a line can get beside in a 100-wide box, so Chrome puts
    ///     the whole line at <c>y = 200</c> and lets the container come out 220 tall. The clause is
    ///     "shifted downward until it fits", and here the only place it fits is past the end.
    /// </remarks>
    [Fact]
    public void A_line_drops_to_the_bottom_of_a_float_it_cannot_get_beside() {
        using var tree = new LayoutTree();
        var root = Root(tree, 100f);

        Float(tree, root, FloatSide.Left, 60f, 200f);
        var a = Inline(tree, root, 50f, 20f);
        var b = Inline(tree, root, 50f, 20f);

        tree.CalculateLayout(root, 100f, float.NaN, Direction.Ltr);

        AssertAt(tree, a, 0f, 200f);
        AssertAt(tree, b, 50f, 200f);
        Assert.Equal(220f, tree.GetHeight(root), Tolerance);
    }

    /// <summary>An item wider than the container drops past the float and then overflows.</summary>
    /// <remarks>
    ///     ⚠ <b>"…or there are no more floats present" is the half that stops this being a clamp.</b>
    ///     A 150-wide item never fits a 100-wide box, so the search has to terminate somewhere. Chrome
    ///     terminates it below the last float: <c>x = 0, y = 40</c>, hanging 50 points off the right
    ///     of the container. Dropping the shift entirely answers <c>y = 0</c>; looping without the
    ///     no-more-floats exit does not answer at all.
    /// </remarks>
    [Fact]
    public void An_item_wider_than_the_container_drops_past_the_float_and_then_overflows() {
        using var tree = new LayoutTree();
        var root = Root(tree, 100f);

        Float(tree, root, FloatSide.Left, 60f, 40f);
        var a = Inline(tree, root, 150f, 20f);

        tree.CalculateLayout(root, 100f, float.NaN, Direction.Ltr);

        AssertAt(tree, a, 0f, 40f);
        Assert.Equal(150f, tree.GetWidth(a), Tolerance);
        Assert.Equal(60f, tree.GetHeight(root), Tolerance);
    }

    // ── Right-to-left, where the band is physical and the line's start edge is not ───────────────

    /// <summary>An RTL line beside a left float keeps its start edge and loses width.</summary>
    /// <remarks>
    ///     ⚠ <b>The float's side is physical and the line's start edge is not, which is why these are
    ///     two tests rather than one with a mirrored expectation.</b> A left float in an RTL container
    ///     takes the far end of the line, so the line still begins at the right content edge: Chrome
    ///     answers <c>x = 150, 100, 50</c> — three items, one short of the four an unshortened line
    ///     would have held — and puts the fourth back at <c>x = 150</c>.
    /// </remarks>
    [Fact]
    public void A_right_to_left_line_beside_a_left_float_keeps_its_start_edge() {
        using var tree = new LayoutTree();
        var root = Root(tree, 200f);
        tree.SetDirection(root, Direction.Rtl);

        Float(tree, root, FloatSide.Left, 50f, 50f);
        var a = Inline(tree, root, 50f, 20f);
        var b = Inline(tree, root, 50f, 20f);
        var c = Inline(tree, root, 50f, 20f);
        var d = Inline(tree, root, 50f, 20f);

        tree.CalculateLayout(root, 200f, float.NaN, Direction.Rtl);

        AssertAt(tree, a, 150f, 0f);
        AssertAt(tree, b, 100f, 0f);
        AssertAt(tree, c, 50f, 0f);
        AssertAt(tree, d, 150f, 20f);
        Assert.Equal(50f, tree.GetHeight(root), Tolerance);
    }

    /// <summary>An RTL line beside a right float has its start edge moved instead.</summary>
    /// <remarks>
    ///     The mirror of the previous test, and the one that exercises the non-zero start inset: a
    ///     60-wide right float lands at <c>x = 140</c> and the RTL line now begins there rather than
    ///     at 200. Chrome answers <c>x = 90, 40</c> on each line — two items, because the band is 140
    ///     wide, and both of them measured leftward from the float's left edge.
    /// </remarks>
    [Fact]
    public void A_right_to_left_line_beside_a_right_float_moves_its_start_edge() {
        using var tree = new LayoutTree();
        var root = Root(tree, 200f);
        tree.SetDirection(root, Direction.Rtl);

        var box = Float(tree, root, FloatSide.Right, 60f, 50f);
        var a = Inline(tree, root, 50f, 20f);
        var b = Inline(tree, root, 50f, 20f);
        var c = Inline(tree, root, 50f, 20f);
        var d = Inline(tree, root, 50f, 20f);

        tree.CalculateLayout(root, 200f, float.NaN, Direction.Rtl);

        Assert.Equal(140f, tree.GetLeft(box), Tolerance);
        AssertAt(tree, a, 90f, 0f);
        AssertAt(tree, b, 40f, 0f);
        AssertAt(tree, c, 90f, 20f);
        AssertAt(tree, d, 40f, 20f);
        Assert.Equal(50f, tree.GetHeight(root), Tolerance);
    }

    // ── What is NOT done, pinned so that it is a decision rather than an omission ────────────────

    /// <summary>
    ///     A float declared between two inline items still ends that run, rather than joining the
    ///     line it was written on.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This test asserts what Vixen does, and Chrome does something else. It should FAIL and
    ///     be inverted when floats inside an inline formatting context land</b> — the same shape as
    ///     <c>A_span_inside_a_span_is_still_atomic</c> in <see cref="InlineFragmentationTests" />.
    ///     <para>
    ///         §9.5's rule 6 places a float declared mid-line at the top of the <i>current</i> line,
    ///         where it then shortens that very line. <b>Chrome answers <c>a</c> at
    ///         <c>x = 50, y = 0</c></b> — the float appears after <c>a</c> in the source and moves it
    ///         anyway — with <c>b</c> and <c>c</c> at 100 and 150 beside it and <c>d</c> at
    ///         <c>(50, 20)</c>, for a container 50 tall.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Vixen cannot answer that today, and the reason is one level above this walk.</b>
    ///         CSS 2.1 §9.7 makes a floated box block-level whatever its <c>display</c> says, so
    ///         <c>StartsAnonymousRun</c> ends the run at the float and <c>WalkBlockChildren</c> places
    ///         it at the cursor <i>after</i> the run — which is 20, below the line it was written on.
    ///         Reaching Chrome's answer means a float being an entry in the inline item stream that
    ///         the line walk places as it goes, which is a change to the producer in
    ///         <c>LayoutTree.InlineItems.cs</c> and to who owns float placement, not a change to the
    ///         band query this file is about. Sized rather than half-built; see
    ///         <c>InlineKnownGaps.txt</c>.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_float_between_two_items_still_ends_the_run_rather_than_joining_the_line() {
        using var tree = new LayoutTree();
        var root = Root(tree, 200f);

        var a = Inline(tree, root, 50f, 20f);
        var box = Float(tree, root, FloatSide.Left, 50f, 50f);
        var b = Inline(tree, root, 50f, 20f);
        var c = Inline(tree, root, 50f, 20f);
        var d = Inline(tree, root, 50f, 20f);

        tree.CalculateLayout(root, 200f, float.NaN, Direction.Ltr);

        // Chrome: a at (50, 0), the float at (0, 0), b/c at (100, 0)/(150, 0), d at (50, 20).
        AssertAt(tree, a, 0f, 0f);
        AssertAt(tree, box, 0f, 20f);
        AssertAt(tree, b, 50f, 20f);
        AssertAt(tree, c, 100f, 20f);
        AssertAt(tree, d, 150f, 20f);
    }

    // ── Fixture helpers ─────────────────────────────────────────────────────────────────────────

    static LayoutNodeId Root(LayoutTree tree, float width) {
        var root = tree.CreateNode();
        tree.SetDisplay(root, Display.Block);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(width));

        return root;
    }

    /// <summary>One 50-point-ish atomic inline: an <c>inline-block</c> with both axes stated.</summary>
    static LayoutNodeId Inline(LayoutTree tree, LayoutNodeId parent, float width, float height) {
        var node = tree.CreateNode();
        tree.SetDisplay(node, Display.InlineBlock);
        tree.SetDimension(node, Dimension.Width, StyleLength.Points(width));
        tree.SetDimension(node, Dimension.Height, StyleLength.Points(height));
        tree.AddChild(parent, node);

        return node;
    }

    static LayoutNodeId Float(LayoutTree tree, LayoutNodeId parent, FloatSide side, float width, float height) {
        var node = tree.CreateNode();
        tree.SetDisplay(node, Display.Block);
        tree.SetFloat(node, side);
        tree.SetDimension(node, Dimension.Width, StyleLength.Points(width));
        tree.SetDimension(node, Dimension.Height, StyleLength.Points(height));
        tree.AddChild(parent, node);

        return node;
    }

    static void AssertAt(LayoutTree tree, LayoutNodeId node, float left, float top) {
        Assert.Equal(left, tree.GetLeft(node), Tolerance);
        Assert.Equal(top, tree.GetTop(node), Tolerance);
    }
}
