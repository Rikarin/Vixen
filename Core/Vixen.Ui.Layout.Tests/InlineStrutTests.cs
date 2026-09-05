// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Layout.Tests;

/// <summary>
///     CSS 2.1 §10.8's strut, and the five <c>vertical-align</c> values that are defined against it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This file exists because "this store has no font" turned out to be the wrong reason
///         for refusing five values, and the right reason for refusing to <i>invent</i> them.</b> A
///         strut is an imaginary zero-width inline box carrying the block container's own font and
///         line height; every rule that depends on it — a line that is never shorter than the text
///         on it would be, <c>middle</c>, <c>text-top</c>, <c>text-bottom</c>, <c>sub</c>,
///         <c>super</c> — is arithmetic over five numbers. Only <i>producing</i> the five needs a
///         font. So they are a computed value the layer that owns the fonts writes down, exactly as
///         it already writes down a resolved font size, and this store stayed geometry.
///     </para>
///     <para>
///         ⚠ <b>Every number below is closed form, and the strut is stated rather than measured.</b>
///         There is no inline corpus (see <c>InlineKnownGaps.txt</c>) and a browser reading would be
///         a font reading, so a fixture recorded from Chrome would be recording <i>Chrome's font
///         metrics</i> and not its layout. The metrics here are instead a made-up font whose numbers
///         are round: a 20-point face with a 16/4 ascent and descent, a 10-point x-height, and a
///         30-point <c>line-height</c> — so the half-leading is exactly 5 and the strut's line box is
///         21 above the baseline and 9 below. Each assertion is derived from those in its own remark.
///     </para>
/// </remarks>
public class InlineStrutTests {
    const float Tolerance = 0.0001f;

    /// <summary>
    ///     The made-up 20-point face this file measures against, at <c>line-height: 30</c>.
    /// </summary>
    /// <remarks>
    ///     Ascent 16 and descent 4 make a 20-point content area; the 10 points of leading are split
    ///     into 5 above and 5 below, which is where 21 and 9 come from. The x-height is half the em,
    ///     the subscript drops 4 and the superscript rises 8 — all four the sort of number a real
    ///     face reports, chosen so that no two assertions in this file can accidentally agree.
    /// </remarks>
    static readonly StrutMetrics Face = new(
        Ascent: 21f,
        Descent: 9f,
        TextAscent: 16f,
        TextDescent: 4f,
        XHeight: 10f,
        SubOffset: 4f,
        SuperOffset: 8f
    );

    /// <summary>The same face with <c>line-height: normal</c>, so no leading at all.</summary>
    static readonly StrutMetrics Tight = Face with { Ascent = 16f, Descent = 4f };

    /// <summary>A line box is never shorter than the strut, and the strut holds the baseline.</summary>
    /// <remarks>
    ///     §10.8: a line box's height is the distance between the topmost and bottommost of the boxes
    ///     on it <i>including the strut</i>. A single 10-point box gives an ascent of 10 and a descent
    ///     of 0 — the strut's 21 and 9 both win, so the line is 30 tall and the box hangs from the
    ///     baseline at 21, putting its top at 11. Without a strut this line is 10 tall and the box is
    ///     at 0, which is what every line in this store was before.
    /// </remarks>
    [Fact]
    public void A_line_box_is_never_shorter_than_the_strut() {
        using var tree = new LayoutTree();
        var root = Root(tree, 300f, Face);

        var box = Item(tree, root, 10f, 40f);

        tree.CalculateLayout(root, 300f, float.NaN, Direction.Ltr);

        Assert.Equal(30f, tree.GetHeight(root), Tolerance);
        Assert.Equal(11f, tree.GetTop(box), Tolerance);
    }

    /// <summary>
    ///     ⚠ <b>A box taller than the strut still grows the line, which is what makes the strut a
    ///     participant rather than a floor.</b>
    /// </summary>
    /// <remarks>
    ///     A 50-point box on a line whose strut is 21/9 gives a 59-point line box, not a 50-point one:
    ///     the box wins the ascent and the strut still wins the descent, because §10.8.1 maxes the two
    ///     sides <i>separately</i>. Clamping the finished height to the strut instead — the obvious
    ///     cheap reading of "never shorter than" — gives 50 here and puts the baseline 9 points wrong.
    /// </remarks>
    [Fact]
    public void A_box_taller_than_the_strut_grows_the_line_on_its_own_side_only() {
        using var tree = new LayoutTree();
        var root = Root(tree, 300f, Face);

        var box = Item(tree, root, 50f, 40f);

        tree.CalculateLayout(root, 300f, float.NaN, Direction.Ltr);

        Assert.Equal(59f, tree.GetHeight(root), Tolerance);
        Assert.Equal(0f, tree.GetTop(box), Tolerance);
    }

    /// <summary>Leading moves the baseline, which is what <c>line-height</c> on a container does.</summary>
    /// <remarks>
    ///     The two struts differ only in their leading — 21/9 against 16/4 — so the same 10-point box
    ///     hangs at 11 under the first and at 6 under the second, and the line is 30 tall against 20.
    ///     This is the rule <c>InlineKnownGaps.txt</c> listed as "line-height on the container needs
    ///     the strut"; nothing else in the walk reads a line height.
    /// </remarks>
    [Fact]
    public void The_containers_leading_moves_the_baseline_and_the_line_height() {
        using var tree = new LayoutTree();
        var root = Root(tree, 300f, Tight);

        var box = Item(tree, root, 10f, 40f);

        tree.CalculateLayout(root, 300f, float.NaN, Direction.Ltr);

        Assert.Equal(20f, tree.GetHeight(root), Tolerance);
        Assert.Equal(6f, tree.GetTop(box), Tolerance);
    }

    /// <summary>⚠ The strut is the <i>container's</i>, and an item's own copy of it is inert.</summary>
    /// <remarks>
    ///     §10.8's strut belongs to the block container that established the inline formatting
    ///     context. The field is on every style because the store has one style type, so this pins
    ///     that a strut written on a box <i>on</i> the line changes nothing — the alternative reading,
    ///     where each item brought its own, would make a line's height depend on which of its boxes
    ///     was asked.
    /// </remarks>
    [Fact]
    public void A_strut_written_on_an_item_rather_than_its_container_is_inert() {
        using var tree = new LayoutTree();
        var root = Root(tree, 300f, default);

        var box = Item(tree, root, 10f, 40f);
        tree.SetStrut(box, Face);

        tree.CalculateLayout(root, 300f, float.NaN, Direction.Ltr);

        Assert.Equal(10f, tree.GetHeight(root), Tolerance);
        Assert.Equal(0f, tree.GetTop(box), Tolerance);
    }

    /// <summary><c>vertical-align: middle</c> centres the box on the baseline plus half an x-height.</summary>
    /// <remarks>
    ///     §10.8.1: align the vertical midpoint of the box with the baseline of the parent box plus
    ///     half the x-height of the parent. The baseline is at 21 and half the x-height is 5, so the
    ///     20-point box's midpoint goes to 16 and its top to 6. Baseline alignment would put it at 1,
    ///     and the difference — five points on a twenty-point box — is exactly the kind of "looks
    ///     nearly right" that made falling back silently the wrong answer.
    /// </remarks>
    [Fact]
    public void Vertical_align_middle_centres_the_box_on_half_the_x_height() {
        using var tree = new LayoutTree();
        var root = Root(tree, 300f, Face);

        var box = Item(tree, root, 20f, 40f);
        tree.SetVerticalAlign(box, VerticalAlign.Middle);

        tree.CalculateLayout(root, 300f, float.NaN, Direction.Ltr);

        Assert.Equal(6f, tree.GetTop(box), Tolerance);
        Assert.Equal(30f, tree.GetHeight(root), Tolerance);
    }

    /// <summary>
    ///     ⚠ <c>text-top</c> and <c>text-bottom</c> are the <i>content area's</i> edges and not the
    ///     line box's.
    /// </summary>
    /// <remarks>
    ///     The distinction is invisible at <c>line-height: normal</c> and is five points on each side
    ///     here: the line box runs from 0 to 30 and the content area from 5 to 25. So the
    ///     <c>text-top</c> box sits at 5 rather than at 0, and the <c>text-bottom</c> box's bottom
    ///     edge lands at 25 rather than at 30 — top 15 for a 10-point box. A strut that carried one
    ///     ascent instead of two would answer 0 and 20.
    /// </remarks>
    [Fact]
    public void Text_top_and_text_bottom_align_to_the_content_area() {
        using var tree = new LayoutTree();
        var root = Root(tree, 300f, Face);

        var top = Item(tree, root, 20f, 40f);
        var bottom = Item(tree, root, 10f, 40f);
        tree.SetVerticalAlign(top, VerticalAlign.TextTop);
        tree.SetVerticalAlign(bottom, VerticalAlign.TextBottom);

        tree.CalculateLayout(root, 300f, float.NaN, Direction.Ltr);

        Assert.Equal(5f, tree.GetTop(top), Tolerance);
        Assert.Equal(15f, tree.GetTop(bottom), Tolerance);
        Assert.Equal(30f, tree.GetHeight(root), Tolerance);
    }

    /// <summary><c>sub</c> and <c>super</c> move the box's own baseline by the face's offsets.</summary>
    /// <remarks>
    ///     A 10-point box baseline-aligned would sit at 11. Lowered by the face's 4-point subscript it
    ///     is at 15; raised by its 8-point superscript it is at 3. Both are the same one-line shift as
    ///     a length <c>vertical-align</c> — the only thing the font supplies is how far.
    /// </remarks>
    [Fact]
    public void Sub_and_super_shift_the_boxs_baseline_by_the_faces_own_offsets() {
        using var tree = new LayoutTree();
        var root = Root(tree, 300f, Face);

        var sub = Item(tree, root, 10f, 40f);
        var super = Item(tree, root, 10f, 40f);
        tree.SetVerticalAlign(sub, VerticalAlign.Sub);
        tree.SetVerticalAlign(super, VerticalAlign.Super);

        tree.CalculateLayout(root, 300f, float.NaN, Direction.Ltr);

        Assert.Equal(15f, tree.GetTop(sub), Tolerance);
        Assert.Equal(3f, tree.GetTop(super), Tolerance);
    }

    /// <summary>
    ///     ⚠ A length <c>vertical-align</c> needs no strut at all, and a lowered box grows the line.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         §10.8.1's <c>&lt;length&gt;</c> raises the box by that distance <i>from its baseline
    ///         position</i>, which is pure geometry — so it is the one of the six new values that is
    ///         honoured on a container that never supplied a font, and this test deliberately sets no
    ///         strut to say so.
    ///     </para>
    ///     <para>
    ///         The 40-point anchor fixes the baseline at 40. Raised 5 points, the 10-point box's top
    ///         goes from 30 to 25 and the line is unchanged at 40, because raising it cannot reach
    ///         past an ascent of 40. Lowered 6, it goes to 36 and hangs 6 points below the anchor's
    ///         bottom — so the line box grows to 46, which is the half a shift that only moved boxes
    ///         would get wrong.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_length_vertical_align_raises_the_box_without_a_strut() {
        using var tree = new LayoutTree();
        var raisedRoot = Root(tree, 300f, default);

        Item(tree, raisedRoot, 40f, 40f);
        var raised = Item(tree, raisedRoot, 10f, 40f);
        tree.SetVerticalAlign(raised, VerticalAlign.Offset);
        tree.SetVerticalAlignOffset(raised, 5f);

        tree.CalculateLayout(raisedRoot, 300f, float.NaN, Direction.Ltr);

        Assert.Equal(25f, tree.GetTop(raised), Tolerance);
        Assert.Equal(40f, tree.GetHeight(raisedRoot), Tolerance);

        using var second = new LayoutTree();
        var loweredRoot = Root(second, 300f, default);

        Item(second, loweredRoot, 40f, 40f);
        var lowered = Item(second, loweredRoot, 10f, 40f);
        second.SetVerticalAlign(lowered, VerticalAlign.Offset);
        second.SetVerticalAlignOffset(lowered, -6f);

        second.CalculateLayout(loweredRoot, 300f, float.NaN, Direction.Ltr);

        Assert.Equal(36f, second.GetTop(lowered), Tolerance);
        Assert.Equal(46f, second.GetHeight(loweredRoot), Tolerance);
    }

    /// <summary>The strut survives a line break, because every line box begins with one.</summary>
    /// <remarks>
    ///     Two 10-point boxes 200 wide in a 300-wide container take a line each; each line is the
    ///     strut's 30 rather than the box's 10, so the second box's top is 30 + 11 and the container
    ///     is 60 tall. A strut applied once per <i>container</i> rather than once per <i>line</i>
    ///     answers 40 here.
    /// </remarks>
    [Fact]
    public void Every_line_box_begins_with_its_own_strut() {
        using var tree = new LayoutTree();
        var root = Root(tree, 300f, Face);

        var first = Item(tree, root, 10f, 200f);
        var second = Item(tree, root, 10f, 200f);

        tree.CalculateLayout(root, 300f, float.NaN, Direction.Ltr);

        Assert.Equal(11f, tree.GetTop(first), Tolerance);
        Assert.Equal(41f, tree.GetTop(second), Tolerance);
        Assert.Equal(60f, tree.GetHeight(root), Tolerance);
    }

    /// <summary>
    ///     ⚠ <c>top</c> and <c>bottom</c> still ignore the baseline, and now measure against a line
    ///     the strut sized.
    /// </summary>
    /// <remarks>
    ///     The two edge-relative values are the pair that never asked a font anything, and the strut
    ///     reaches them only by changing what the line's edges are: the line here is the strut's 30
    ///     tall although its tallest box is 10, so <c>bottom</c> puts a 10-point box at 20 where it
    ///     used to be at 0. Nothing in the second round of <c>MeasureLine</c> changed.
    /// </remarks>
    [Fact]
    public void Top_and_bottom_measure_against_the_line_the_strut_sized() {
        using var tree = new LayoutTree();
        var root = Root(tree, 300f, Face);

        var top = Item(tree, root, 10f, 40f);
        var bottom = Item(tree, root, 10f, 40f);
        tree.SetVerticalAlign(top, VerticalAlign.Top);
        tree.SetVerticalAlign(bottom, VerticalAlign.Bottom);

        tree.CalculateLayout(root, 300f, float.NaN, Direction.Ltr);

        Assert.Equal(0f, tree.GetTop(top), Tolerance);
        Assert.Equal(20f, tree.GetTop(bottom), Tolerance);
        Assert.Equal(30f, tree.GetHeight(root), Tolerance);
    }

    static LayoutNodeId Root(LayoutTree tree, float width, StrutMetrics strut) {
        var root = tree.CreateNode();
        tree.SetDisplay(root, Display.Block);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(width));
        tree.SetStrut(root, strut);

        return root;
    }

    static LayoutNodeId Item(LayoutTree tree, LayoutNodeId parent, float height, float width) {
        var node = tree.CreateNode();
        tree.SetDisplay(node, Display.InlineBlock);
        tree.SetDimension(node, Dimension.Height, StyleLength.Points(height));
        tree.SetDimension(node, Dimension.Width, StyleLength.Points(width));
        tree.AddChild(parent, node);

        return node;
    }
}
