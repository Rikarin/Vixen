// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Layout.Tests;

/// <summary>
///     CSS 2.1 §8.3.1 — adjoining vertical margins collapsing into one, which is the rule that makes
///     <see cref="Display.Block" /> a second algorithm rather than a flag on the first.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The block corpus is 884 fixtures and it still cannot see three of the rules below</b>,
///         which is the same story <see cref="AutomaticMinimumSizeTests" /> tells about §4.5 and the
///         reason that file exists. Each of these was confirmed by sabotage — deleting the rule and
///         watching both corpora stay green — rather than assumed:
///     </para>
///     <list type="bullet">
///         <item>
///             <b><c>overflow</c> other than <c>visible</c> blocks a collapse.</b> Chrome's own
///             fixtures for it exist — 48 of them, the <c>collapse_*_blocked_by_overflow_*</c>
///             families — and every one is <i>refused</i> by the harness for <c>scrollbar-width</c>,
///             a property this store has no field for. So the corpus contains the test, states the
///             right answer, and cannot run it.
///         </item>
///         <item>
///             <b>A flex container's margins do not collapse with its contents.</b> The 28
///             <c>blockflex</c> fixtures mix the two algorithms and not one of them puts a vertical
///             margin on both sides of the seam.
///         </item>
///         <item>
///             <b>A positive and a negative margin <i>add</i>.</b> The corpus does cover this one —
///             so this test is not closing a hole, it is pinning the single decision that
///             <see cref="CollapsibleMargin" /> exists for, next to the two that no fixture reaches.
///         </item>
///     </list>
/// </remarks>
public class MarginCollapsingTests {
    const float Tolerance = 0.0001f;

    /// <summary>Two adjoining margins between siblings become the larger, not the sum.</summary>
    [Fact]
    public void Adjoining_sibling_margins_become_the_larger_of_the_two() {
        using var tree = new LayoutTree();
        var root = Container(tree);

        var first = Box(tree, root, height: 10f);
        tree.SetMargin(first, Edge.Bottom, StyleLength.Points(20f));

        var second = Box(tree, root, height: 10f);
        tree.SetMargin(second, Edge.Top, StyleLength.Points(30f));

        tree.CalculateLayout(root, 100f, float.NaN, Direction.Ltr);

        // 10 + max(20, 30), not 10 + 20 + 30.
        Assert.Equal(40f, tree.GetTop(second), Tolerance);
        Assert.Equal(50f, tree.GetHeight(root), Tolerance);
    }

    /// <summary>
    ///     A positive and a negative margin are added, which no running maximum can produce.
    /// </summary>
    /// <remarks>
    ///     ⚠ This is the whole reason <see cref="CollapsibleMargin" /> keeps two numbers. §8.3.1: when
    ///     the adjoining margins are of mixed sign the collapsed margin is the sum of the largest
    ///     positive and the most negative. Implementing collapse as <c>MathF.Max</c> gives 30 here and
    ///     is right for every all-positive case, which is most of them.
    /// </remarks>
    [Fact]
    public void A_positive_and_a_negative_margin_add_rather_than_maximise() {
        using var tree = new LayoutTree();
        var root = Container(tree);

        var first = Box(tree, root, height: 10f);
        tree.SetMargin(first, Edge.Bottom, StyleLength.Points(30f));

        var second = Box(tree, root, height: 10f);
        tree.SetMargin(second, Edge.Top, StyleLength.Points(-12f));

        tree.CalculateLayout(root, 100f, float.NaN, Direction.Ltr);

        // 10 + (30 + −12) = 28. A maximum would say 40; the most negative alone would say −2.
        Assert.Equal(28f, tree.GetTop(second), Tolerance);
    }

    /// <summary>A first child's top margin escapes its parent when nothing separates them.</summary>
    [Fact]
    public void A_first_child_s_margin_escapes_a_parent_with_no_border_or_padding() {
        using var tree = new LayoutTree();
        var root = Container(tree);

        var wrapper = Box(tree, root, height: float.NaN);
        tree.SetDisplay(wrapper, Display.Block);

        var inner = Box(tree, wrapper, height: 10f);
        tree.SetMargin(inner, Edge.Top, StyleLength.Points(25f));

        tree.CalculateLayout(root, 100f, float.NaN, Direction.Ltr);

        // The inner box sits at the top of the wrapper; the margin moved outside the wrapper and
        // pushed the wrapper itself down instead.
        Assert.Equal(0f, tree.GetTop(inner), Tolerance);
        Assert.Equal(25f, tree.GetTop(wrapper), Tolerance);
        Assert.Equal(10f, tree.GetHeight(wrapper), Tolerance);
    }

    /// <summary>One point of padding is enough to stop it.</summary>
    [Fact]
    public void A_single_point_of_padding_keeps_the_first_child_s_margin_inside() {
        using var tree = new LayoutTree();
        var root = Container(tree);

        var wrapper = Box(tree, root, height: float.NaN);
        tree.SetDisplay(wrapper, Display.Block);
        tree.SetPadding(wrapper, Edge.Top, StyleLength.Points(1f));

        var inner = Box(tree, wrapper, height: 10f);
        tree.SetMargin(inner, Edge.Top, StyleLength.Points(25f));

        tree.CalculateLayout(root, 100f, float.NaN, Direction.Ltr);

        Assert.Equal(0f, tree.GetTop(wrapper), Tolerance);
        Assert.Equal(26f, tree.GetTop(inner), Tolerance);
        Assert.Equal(36f, tree.GetHeight(wrapper), Tolerance);
    }

    /// <summary>
    ///     A box with nothing between its two margins is transparent to the collapse.
    /// </summary>
    /// <remarks>
    ///     §8.3.1's collapse-through. The empty box does not advance the stack at all, so its
    ///     neighbours' margins meet each other across it and the larger of the three wins.
    /// </remarks>
    [Fact]
    public void Margins_collapse_through_an_empty_box() {
        using var tree = new LayoutTree();
        var root = Container(tree);

        var first = Box(tree, root, height: 10f);
        tree.SetMargin(first, Edge.Bottom, StyleLength.Points(10f));

        var empty = Box(tree, root, height: float.NaN);
        tree.SetDisplay(empty, Display.Block);
        tree.SetMargin(empty, Edge.Top, StyleLength.Points(10f));
        tree.SetMargin(empty, Edge.Bottom, StyleLength.Points(10f));

        var last = Box(tree, root, height: 10f);
        tree.SetMargin(last, Edge.Top, StyleLength.Points(10f));

        tree.CalculateLayout(root, 100f, float.NaN, Direction.Ltr);

        // One 10-point gap in total, not three, and the empty box sits inside it.
        Assert.Equal(0f, tree.GetTop(first), Tolerance);
        Assert.Equal(20f, tree.GetTop(empty), Tolerance);
        Assert.Equal(0f, tree.GetHeight(empty), Tolerance);
        Assert.Equal(20f, tree.GetTop(last), Tolerance);
        Assert.Equal(30f, tree.GetHeight(root), Tolerance);
    }

    /// <summary>A height, however small, stops the box being collapsed through.</summary>
    [Fact]
    public void A_box_with_a_height_is_not_collapsed_through() {
        using var tree = new LayoutTree();
        var root = Container(tree);

        var first = Box(tree, root, height: 10f);
        tree.SetMargin(first, Edge.Bottom, StyleLength.Points(10f));

        var middle = Box(tree, root, height: 1f);
        tree.SetDisplay(middle, Display.Block);
        tree.SetMargin(middle, Edge.Top, StyleLength.Points(10f));
        tree.SetMargin(middle, Edge.Bottom, StyleLength.Points(10f));

        var last = Box(tree, root, height: 10f);
        tree.SetMargin(last, Edge.Top, StyleLength.Points(10f));

        tree.CalculateLayout(root, 100f, float.NaN, Direction.Ltr);

        Assert.Equal(20f, tree.GetTop(middle), Tolerance);
        Assert.Equal(31f, tree.GetTop(last), Tolerance);
    }

    /// <summary>
    ///     A box that clips its overflow establishes its own formatting context, and margins do not
    ///     cross into it.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Neither corpus can run this, and Chrome's own fixtures for it are sitting in the
    ///     tree.</b> The 48 <c>block_margin_y_*_blocked_by_overflow_*</c> fixtures all set
    ///     <c>scrollbar-width</c>, which <c>TaffyStyleMap</c> refuses, so they are counted as
    ///     unsupported and never reach any arithmetic. Deleting the overflow term from
    ///     <c>EstablishesBlockFormattingContext</c> leaves all 884 block, all 2 352 flex and all 534
    ///     Yoga fixtures green, and breaks this.
    /// </remarks>
    [Fact]
    public void A_clipping_box_does_not_let_its_child_s_margin_escape() {
        using var tree = new LayoutTree();
        var root = Container(tree);

        var wrapper = Box(tree, root, height: float.NaN);
        tree.SetDisplay(wrapper, Display.Block);
        tree.SetOverflow(wrapper, Overflow.Hidden);

        var inner = Box(tree, wrapper, height: 10f);
        tree.SetMargin(inner, Edge.Top, StyleLength.Points(25f));

        tree.CalculateLayout(root, 100f, float.NaN, Direction.Ltr);

        Assert.Equal(0f, tree.GetTop(wrapper), Tolerance);
        Assert.Equal(25f, tree.GetTop(inner), Tolerance);
        Assert.Equal(35f, tree.GetHeight(wrapper), Tolerance);
    }

    /// <summary>
    ///     A flex container's margins do not collapse with its contents, and its children's do not
    ///     collapse with each other.
    /// </summary>
    /// <remarks>
    ///     ⚠ CSS Flexbox §9.5, in as many words. This is the assertion that distinguishes a real
    ///     block layout from a stretch flex column, and <b>no fixture in either corpus makes it</b> —
    ///     the 28 <c>blockflex</c> fixtures mix the algorithms but never put a vertical margin on both
    ///     sides of the boundary. Making <c>CalculateLayoutImpl</c>'s default margin outputs report
    ///     the first child's set instead of the node's own leaves every one of the 5 524 green.
    /// </remarks>
    [Fact]
    public void A_flex_container_is_a_barrier_to_collapsing() {
        using var tree = new LayoutTree();
        var root = Container(tree);

        var flex = Box(tree, root, height: float.NaN);
        tree.SetDisplay(flex, Display.Flex);
        tree.SetFlexDirection(flex, FlexDirection.Column);
        tree.SetMargin(flex, Edge.Top, StyleLength.Points(10f));

        var inner = Box(tree, flex, height: 10f);
        tree.SetMargin(inner, Edge.Top, StyleLength.Points(25f));

        tree.CalculateLayout(root, 100f, float.NaN, Direction.Ltr);

        // The container's own 10 is what the block parent spends; the child's 25 stays inside and
        // does not merge with it.
        Assert.Equal(10f, tree.GetTop(flex), Tolerance);
        Assert.Equal(25f, tree.GetTop(inner), Tolerance);
        Assert.Equal(35f, tree.GetHeight(flex), Tolerance);
    }

    /// <summary>An in-flow block child fills the inline axis without being told to.</summary>
    /// <remarks>
    ///     CSS 2.1 §10.3.3. Not the same as <c>align-items: stretch</c>, which a child can opt out of
    ///     with <c>align-self</c>; there is no opting out of this one short of stating a width.
    /// </remarks>
    [Fact]
    public void An_in_flow_block_child_fills_the_inline_axis() {
        using var tree = new LayoutTree();
        var root = Container(tree);

        var child = Box(tree, root, height: 10f);
        tree.SetMargin(child, Edge.Left, StyleLength.Points(15f));

        tree.CalculateLayout(root, 100f, float.NaN, Direction.Ltr);

        Assert.Equal(85f, tree.GetWidth(child), Tolerance);
        Assert.Equal(15f, tree.GetLeft(child), Tolerance);
    }

    /// <summary>
    ///     A block container's width, when nobody imposes one, is its widest child rather than the
    ///     sum of them.
    /// </summary>
    [Fact]
    public void An_intrinsically_sized_block_container_is_as_wide_as_its_widest_child() {
        using var tree = new LayoutTree();
        var root = tree.CreateNode();
        tree.SetDisplay(root, Display.Block);

        Box(tree, root, height: 10f, width: 30f);
        Box(tree, root, height: 10f, width: 70f);
        Box(tree, root, height: 10f, width: 50f);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(70f, tree.GetWidth(root), Tolerance);
        Assert.Equal(30f, tree.GetHeight(root), Tolerance);
    }

    /// <summary>Two auto inline margins centre a block box.</summary>
    [Fact]
    public void Two_auto_inline_margins_centre_a_block_box() {
        using var tree = new LayoutTree();
        var root = Container(tree);

        var child = Box(tree, root, height: 10f, width: 40f);
        tree.SetMargin(child, Edge.Left, StyleLength.Auto);
        tree.SetMargin(child, Edge.Right, StyleLength.Auto);

        tree.CalculateLayout(root, 100f, float.NaN, Direction.Ltr);

        Assert.Equal(30f, tree.GetLeft(child), Tolerance);
        Assert.Equal(40f, tree.GetWidth(child), Tolerance);
    }

    static LayoutNodeId Container(LayoutTree tree) {
        var root = tree.CreateNode();
        tree.SetDisplay(root, Display.Block);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(100f));

        return root;
    }

    static LayoutNodeId Box(LayoutTree tree, LayoutNodeId parent, float height, float width = float.NaN) {
        var node = tree.CreateNode();
        tree.SetDisplay(node, Display.Block);

        if (!float.IsNaN(height)) {
            tree.SetDimension(node, Dimension.Height, StyleLength.Points(height));
        }

        if (!float.IsNaN(width)) {
            tree.SetDimension(node, Dimension.Width, StyleLength.Points(width));
        }

        tree.AddChild(parent, node);

        return node;
    }
}
