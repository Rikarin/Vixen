// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Layout.Tests;

/// <summary>The room a scroll container keeps for its own scrollbar.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>180 Taffy fixtures cover the arithmetic, and not one of them covers anything in this
///         file.</b> That corpus is the reason the feature exists and it is worth being precise about
///         what it cannot reach: every fixture writes <c>direction</c> on every node, so an
///         <i>inherited</i> writing direction is invisible to it — and the gutter changes sides with
///         the direction, which makes this the one property where that blind spot has teeth. Every
///         fixture is also a cold layout, so nothing there exercises invalidation; and no fixture can
///         call a public getter.
///     </para>
///     <para>
///         The two traps are asserted in the corpus and restated here only where a hand-written case
///         is the cheaper demonstration: the axes cross — <c>overflow-y</c> reserves <i>width</i> —
///         and the gutter shrinks the content box without raising the node's minimum size.
///     </para>
/// </remarks>
public class ScrollbarGutterTests {
    const float Tolerance = 0.0001f;

    static LayoutTree Port(out LayoutNodeId root, out LayoutNodeId child, Overflow overflow) {
        var tree = new LayoutTree();

        root = tree.CreateNode();
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(100f));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(100f));
        tree.SetOverflow(root, overflow);
        tree.SetScrollbarWidth(root, 15f);

        child = tree.CreateNode();
        tree.SetDimension(child, Dimension.Height, StyleLength.Points(10f));
        tree.AddChild(root, child);

        return tree;
    }

    [Fact]
    public void An_inherited_rtl_puts_the_gutter_on_the_left() {
        // ⚠ The corpus cannot ask this. Every one of its 22 776 nodes states its own `direction`, so
        // `Direction.Inherit` is never stored — and `grid_scrollbar_rtl`, the fixture that pins the
        // side the bar sits on, states `rtl` on the container itself. Here the container says
        // nothing and takes the direction from the layout call, which is what a real tree does.
        using var tree = Port(out var root, out var child, Overflow.Scroll);
        tree.SetAlignItems(root, Align.Stretch);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Rtl);

        Assert.Equal(15f, tree.GetLeft(child), Tolerance);
        Assert.Equal(85f, tree.GetWidth(child), Tolerance);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(0f, tree.GetLeft(child), Tolerance);
        Assert.Equal(85f, tree.GetWidth(child), Tolerance);
    }

    [Fact]
    public void The_computed_gutter_is_reported_on_the_one_edge_that_has_it() {
        // The public half of the same fact: a renderer that wants to draw the bar in the space
        // layout kept for it needs to be told which edge, and `GetComputedPadding` cannot say.
        using var tree = Port(out var root, out _, Overflow.Scroll);
        tree.SetOverflow(root, Overflow.Visible, Overflow.Scroll);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(15f, tree.GetComputedScrollbarGutter(root, Edge.Right), Tolerance);
        Assert.Equal(0f, tree.GetComputedScrollbarGutter(root, Edge.Left), Tolerance);
        Assert.Equal(0f, tree.GetComputedScrollbarGutter(root, Edge.Top), Tolerance);
        Assert.Equal(0f, tree.GetComputedScrollbarGutter(root, Edge.Bottom), Tolerance);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Rtl);

        Assert.Equal(15f, tree.GetComputedScrollbarGutter(root, Edge.Left), Tolerance);
        Assert.Equal(0f, tree.GetComputedScrollbarGutter(root, Edge.Right), Tolerance);
    }

    [Fact]
    public void Changing_the_width_relays_out_the_tree() {
        // ⚠ Every corpus fixture is a cold layout, so nothing there would notice a setter that
        // stored the value and forgot to mark the node dirty — the second pass would simply answer
        // from the first one's cache and be right by accident on a tree nobody had changed.
        using var tree = Port(out var root, out var child, Overflow.Scroll);
        tree.SetAlignItems(root, Align.Stretch);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);
        Assert.Equal(85f, tree.GetWidth(child), Tolerance);

        tree.SetScrollbarWidth(root, 30f);
        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(70f, tree.GetWidth(child), Tolerance);
    }

    [Fact]
    public void The_axes_cross() {
        // `overflow-y` is the vertical bar, and a vertical bar takes WIDTH. Stated once here because
        // getting it backwards transposes every scroll container in the tree at once and the
        // symptom — everything is slightly the wrong size — names no particular cause.
        using var tree = Port(out var root, out var child, Overflow.Visible);
        tree.SetAlignItems(root, Align.Stretch);
        tree.SetOverflow(root, Overflow.Visible, Overflow.Scroll);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(85f, tree.GetWidth(child), Tolerance);
        Assert.Equal(10f, tree.GetHeight(child), Tolerance);
    }

    [Fact]
    public void A_box_may_be_smaller_than_its_scrollbar_but_not_than_its_padding() {
        // ⚠ The distinction that split `StyleResolution`'s inset helpers in two. Padding and border
        // are a floor a box can never go below; a scrollbar is not, because a bar wider than its box
        // simply covers the whole thing. Chrome answers 4 for both of these.
        using var tree = new LayoutTree();

        var bar = tree.CreateNode();
        tree.SetDimension(bar, Dimension.Width, StyleLength.Points(4f));
        tree.SetOverflow(bar, Overflow.Scroll);
        tree.SetScrollbarWidth(bar, 15f);
        tree.CalculateLayout(bar, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(4f, tree.GetWidth(bar), Tolerance);

        var padded = tree.CreateNode();
        tree.SetDimension(padded, Dimension.Width, StyleLength.Points(4f));
        tree.SetPadding(padded, Edge.Left, StyleLength.Points(15f));
        tree.CalculateLayout(padded, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(15f, tree.GetWidth(padded), Tolerance);
    }
}
