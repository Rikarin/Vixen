// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Layout.Tests;

/// <summary>
///     CSS Text §7.1's <c>text-align</c> proper — where the items on a line box sit along the inline
///     axis.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>There is no fixture for this in either corpus, and the reason is the one
///         <c>InlineKnownGaps.txt</c> opens with.</b> Taffy's <c>display</c> attribute takes five
///         values across all eight files and none of them is inline, so no fixture anywhere has a line
///         box for a value of this property to move anything on. The sixteen block fixtures that do
///         set <c>text-align</c> set <c>-webkit-center</c> and friends, which is
///         <see cref="LegacyTextAlign" /> — a different field governing different boxes.
///     </para>
///     <para>
///         <b>So the numbers here are closed-form rather than recorded.</b> Every case is a container
///         of a stated width holding items of stated widths, which makes the slack an exact
///         subtraction and the expected offset arithmetic anyone can check by hand — the same shape
///         <c>InlineFormattingTests</c> uses for the WPT reftests it re-expresses, and for the same
///         reason: no number here may depend on a font.
///     </para>
///     <para>
///         ⚠ <b>What each test would print on the day the feature is not there</b> is the
///         <c>Start</c> answer — x = 0 for the first item — because that is where every line already
///         sat and it is the value the store defaults to. That is why <see cref="Center" />'s and
///         <see cref="End" />'s expectations are nonzero offsets rather than relations: an assertion
///         that a centred line is "no further left than a start-aligned one" is satisfied by doing
///         nothing at all.
///     </para>
/// </remarks>
public class InlineTextAlignTests {
    const float Tolerance = 0.0001f;

    /// <summary>The initial value: the items begin at the inline start edge.</summary>
    [Fact]
    public void Start_leaves_the_line_where_it_already_was() {
        using var tree = new LayoutTree();
        var (root, first, second) = Line(tree, 300f, 50f);

        tree.SetTextAlign(root, TextAlign.Start);
        tree.CalculateLayout(root, 300f, float.NaN, Direction.Ltr);

        Assert.Equal(0f, tree.GetLeft(first), Tolerance);
        Assert.Equal(50f, tree.GetLeft(second), Tolerance);
    }

    /// <summary>Centred: half the slack, which for 300 less 100 is 100.</summary>
    [Fact]
    public void Center() {
        using var tree = new LayoutTree();
        var (root, first, second) = Line(tree, 300f, 50f);

        tree.SetTextAlign(root, TextAlign.Center);
        tree.CalculateLayout(root, 300f, float.NaN, Direction.Ltr);

        Assert.Equal(100f, tree.GetLeft(first), Tolerance);
        Assert.Equal(150f, tree.GetLeft(second), Tolerance);
    }

    /// <summary>Ended: all of the slack, so the last item's right edge is the content edge.</summary>
    [Fact]
    public void End() {
        using var tree = new LayoutTree();
        var (root, first, second) = Line(tree, 300f, 50f);

        tree.SetTextAlign(root, TextAlign.End);
        tree.CalculateLayout(root, 300f, float.NaN, Direction.Ltr);

        Assert.Equal(200f, tree.GetLeft(first), Tolerance);
        Assert.Equal(250f, tree.GetLeft(second), Tolerance);
    }

    /// <summary>
    ///     ⚠ Each line's own slack, not the container's — so a short line moves further than a full one.
    /// </summary>
    /// <remarks>
    ///     <b>This is what separates a real implementation from one that centres the whole block.</b>
    ///     Three 80-point items in a 200-point container put two on the first line and one on the
    ///     second, so the two lines have 40 and 120 of slack and their offsets are 20 and 60. An
    ///     implementation that computed one offset per container — from the widest line, say, which is
    ///     the obvious wrong answer — would put both lines in the same place.
    /// </remarks>
    [Fact]
    public void Each_line_is_aligned_on_its_own_slack() {
        using var tree = new LayoutTree();
        var root = tree.CreateNode();
        tree.SetDisplay(root, Display.Block);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(200f));
        tree.SetTextAlign(root, TextAlign.Center);

        var a = Item(tree, root, 80f);
        var b = Item(tree, root, 80f);
        var c = Item(tree, root, 80f);

        tree.CalculateLayout(root, 200f, float.NaN, Direction.Ltr);

        // Two items fit on the first line (160 of 200) and the third takes a line of its own.
        Assert.Equal(20f, tree.GetLeft(a), Tolerance);
        Assert.Equal(100f, tree.GetLeft(b), Tolerance);
        Assert.Equal(60f, tree.GetLeft(c), Tolerance);
        Assert.Equal(0f, tree.GetTop(a), Tolerance);
        Assert.Equal(20f, tree.GetTop(c), Tolerance);
    }

    /// <summary>
    ///     ⚠ <c>start</c> and <c>end</c> flip with <see cref="Direction" /> and <c>left</c> and
    ///     <c>right</c> do not.
    /// </summary>
    /// <remarks>
    ///     <b>The pair of assertions is the test, not either one of them.</b> In an RTL container the
    ///     start edge is the right one, so <c>start</c> and <c>right</c> agree and <c>start</c> and
    ///     <c>left</c> do not — which means a store that treated the physical keywords as logical
    ///     would pass any single-value check here and fail this one.
    /// </remarks>
    [Theory]
    [InlineData(TextAlign.Start, 275f)]
    [InlineData(TextAlign.Right, 275f)]
    [InlineData(TextAlign.End, 25f)]
    [InlineData(TextAlign.Left, 25f)]
    [InlineData(TextAlign.Center, 150f)]
    public void The_physical_keywords_do_not_flip_in_an_rtl_container(TextAlign textAlign, float expected) {
        using var tree = new LayoutTree();
        var (root, first, _) = Line(tree, 300f, 25f);

        tree.SetTextAlign(root, textAlign);
        tree.CalculateLayout(root, 300f, float.NaN, Direction.Rtl);

        // The FIRST item on an RTL line is the rightmost one, so its left edge is 300 less its own
        // width (25) less however far the alignment pushed the line towards the inline end — which
        // is leftwards here. Two 25-point items leave 250 of slack, so `end` and `left` land the
        // line flush against x = 0 and `start` and `right` leave it flush against x = 300.
        Assert.Equal(expected, tree.GetLeft(first), Tolerance);
    }

    /// <summary>
    ///     ⚠ Negative slack is left alone: content wider than its line still starts at the start edge.
    /// </summary>
    /// <remarks>
    ///     The same rule <c>Vixen.Ui</c>'s <c>TextAlignShift</c> states for a line of glyphs, and for
    ///     the same reason — centring an over-wide line hides its beginning, which is the part that
    ///     says what has been cut off. Written as a test because the arithmetic that produces it
    ///     (<c>slack * 0.5</c>) happily returns a negative number.
    /// </remarks>
    [Fact]
    public void An_item_wider_than_its_line_is_not_pulled_backwards() {
        using var tree = new LayoutTree();
        var root = tree.CreateNode();
        tree.SetDisplay(root, Display.Block);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(100f));
        tree.SetTextAlign(root, TextAlign.Center);

        var wide = Item(tree, root, 400f);

        tree.CalculateLayout(root, 100f, float.NaN, Direction.Ltr);

        Assert.Equal(0f, tree.GetLeft(wide), Tolerance);
    }

    /// <summary>
    ///     ⚠ A margin is part of what the line takes up, so it comes out of the slack.
    /// </summary>
    /// <remarks>
    ///     The offset is computed from a second walk over the line, and the failure this pins is that
    ///     walk disagreeing with the placement loop about what an item costs. A line measured narrower
    ///     than it places is pushed past its own end edge by the difference.
    /// </remarks>
    [Fact]
    public void The_line_extent_counts_margins() {
        using var tree = new LayoutTree();
        var root = tree.CreateNode();
        tree.SetDisplay(root, Display.Block);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(200f));
        tree.SetTextAlign(root, TextAlign.End);

        var item = Item(tree, root, 50f);
        tree.SetMargin(item, Edge.Left, StyleLength.Points(30f));
        tree.SetMargin(item, Edge.Right, StyleLength.Points(20f));

        tree.CalculateLayout(root, 200f, float.NaN, Direction.Ltr);

        // The item costs 100 on the line, so the slack is 100; its own left margin then puts its
        // border box 30 further along.
        Assert.Equal(130f, tree.GetLeft(item), Tolerance);
    }

    /// <summary>A container of <paramref name="width" /> holding two inline-block items.</summary>
    static (LayoutNodeId Root, LayoutNodeId First, LayoutNodeId Second) Line(
        LayoutTree tree,
        float width,
        float itemWidth
    ) {
        var root = tree.CreateNode();
        tree.SetDisplay(root, Display.Block);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(width));

        return (root, Item(tree, root, itemWidth), Item(tree, root, itemWidth));
    }

    static LayoutNodeId Item(LayoutTree tree, LayoutNodeId parent, float width) {
        var node = tree.CreateNode();
        tree.SetDisplay(node, Display.InlineBlock);
        tree.SetDimension(node, Dimension.Width, StyleLength.Points(width));
        tree.SetDimension(node, Dimension.Height, StyleLength.Points(20f));
        tree.AddChild(parent, node);

        return node;
    }
}
