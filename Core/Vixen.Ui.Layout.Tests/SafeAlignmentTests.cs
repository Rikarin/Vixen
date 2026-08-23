// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Layout.Tests;

/// <summary>
///     CSS Box Alignment §4.4's <c>safe</c> overflow fallback, on the two properties Taffy's corpus
///     does not write it on.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Four of the six <c>*Overflow</c> fields on <see cref="LayoutStyle" /> have 76
///         browser-recorded fixtures behind them and two have none, and the two with none are the
///         ones a reader would assume were covered by the other four.</b> The corpus writes
///         <c>safe</c> on <c>align-self</c>, <c>align-content</c>, <c>justify-content</c> and
///         <c>justify-self</c>; it never writes it on <c>align-items</c> or <c>justify-items</c>.
///         Those two are the CONTAINER-level halves, and they are reached by a different line of
///         code — the one that resolves <c>align-self: auto</c> against the container — so deleting
///         it leaves every one of the 76 green. This file is that line's only oracle.
///     </para>
///     <para>
///         Each rule is two assertions rather than one. A <c>safe</c> alignment that falls back
///         answers exactly what <c>start</c> answers, so an implementation that dropped the property
///         on the floor would pass the first assertion; the <c>unsafe</c> twin is what says the
///         declaration was read at all.
///     </para>
/// </remarks>
public class SafeAlignmentTests {
    const float Tolerance = 0.0001f;

    /// <summary>A 150-point item in a 100-point flex container, aligned by its container.</summary>
    /// <remarks>
    ///     The child states no <c>align-self</c>, so both halves of the declaration have to come from
    ///     the container: reading the position from <c>align-items</c> and the prefix from the
    ///     child's own <see cref="LayoutStyle.AlignSelfOverflow" /> — which is what a
    ///     resolution that forgot the pairing would do — leaves the prefix at
    ///     <see cref="OverflowAlignment.Unsafe" /> and the item at −50.
    /// </remarks>
    /// <param name="overflow">Which prefix the container writes.</param>
    /// <param name="expected">Where the item's top edge ends up.</param>
    [Theory]
    [InlineData(OverflowAlignment.Safe, 0f)]
    [InlineData(OverflowAlignment.Unsafe, -50f)]
    public void A_container_s_align_items_carries_its_own_safe_prefix_to_a_child(OverflowAlignment overflow, float expected) {
        using var tree = new LayoutTree();
        var root = tree.CreateNode();
        tree.SetFlexDirection(root, FlexDirection.Row);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(100f));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(100f));
        tree.SetAlignItems(root, Align.FlexEnd, overflow);

        var child = tree.CreateNode();
        tree.SetDimension(child, Dimension.Width, StyleLength.Points(50f));
        tree.SetDimension(child, Dimension.Height, StyleLength.Points(150f));
        tree.AddChild(root, child);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(expected, tree.GetTop(child), Tolerance);
    }

    /// <summary>The same on a grid's inline axis, where the container property is <c>justify-items</c>.</summary>
    /// <remarks>
    ///     A grid item is placed by <c>LayoutTree.Grid.AlignInArea</c> rather than by the flex path,
    ///     and it resolves <c>auto</c> against the container in a second place. Two sites, so two
    ///     tests: closing one and not the other is the shape of mistake this pair exists to catch.
    /// </remarks>
    /// <param name="overflow">Which prefix the container writes.</param>
    /// <param name="expected">Where the item's left edge ends up.</param>
    [Theory]
    [InlineData(OverflowAlignment.Safe, 0f)]
    [InlineData(OverflowAlignment.Unsafe, -50f)]
    public void A_grid_s_justify_items_carries_its_own_safe_prefix_to_an_item(OverflowAlignment overflow, float expected) {
        using var tree = new LayoutTree();
        var root = tree.CreateNode();
        tree.SetDisplay(root, Display.Grid);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(100f));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(100f));
        tree.SetGridTemplateColumns(root, [GridTrackSize.Single(GridSizingFunction.Points(100f))]);
        tree.SetGridTemplateRows(root, [GridTrackSize.Single(GridSizingFunction.Points(100f))]);
        tree.SetJustifyItems(root, Align.FlexEnd, overflow);

        var child = tree.CreateNode();
        tree.SetDimension(child, Dimension.Width, StyleLength.Points(150f));
        tree.SetDimension(child, Dimension.Height, StyleLength.Points(50f));
        tree.AddChild(root, child);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(expected, tree.GetLeft(child), Tolerance);
    }

    /// <summary>
    ///     An item's own <c>align-self</c> beats its container's <c>align-items</c>, prefix included.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The prefix is part of the value, so it is overridden with the value rather than
    ///     merged into it.</b> A container saying <c>safe end</c> and a child saying <c>end</c> is a
    ///     child that overflows: the child's declaration replaces the container's whole value, and an
    ///     implementation that took the position from one and the prefix from the other would keep
    ///     the container's <c>safe</c> and answer 0.
    /// </remarks>
    [Fact]
    public void A_child_s_own_align_self_replaces_the_container_s_prefix_as_well_as_its_position() {
        using var tree = new LayoutTree();
        var root = tree.CreateNode();
        tree.SetFlexDirection(root, FlexDirection.Row);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(100f));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(100f));
        tree.SetAlignItems(root, Align.FlexEnd, OverflowAlignment.Safe);

        var child = tree.CreateNode();
        tree.SetAlignSelf(child, Align.FlexEnd);
        tree.SetDimension(child, Dimension.Width, StyleLength.Points(50f));
        tree.SetDimension(child, Dimension.Height, StyleLength.Points(150f));
        tree.AddChild(root, child);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(-50f, tree.GetTop(child), Tolerance);
    }

    /// <summary>
    ///     <c>safe</c> asks about the free space and not about the sign of the offset it would have
    ///     produced.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The reason this is not a clamp.</b> With room to spare, a <c>safe</c> alignment is
    ///     indistinguishable from an <c>unsafe</c> one and must not be quietly weakened into "never
    ///     move the item backwards" — so the same declaration that answers 0 above answers 50 here,
    ///     from a container the item fits in.
    /// </remarks>
    [Fact]
    public void A_safe_alignment_with_room_to_spare_is_an_ordinary_one() {
        using var tree = new LayoutTree();
        var root = tree.CreateNode();
        tree.SetFlexDirection(root, FlexDirection.Row);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(100f));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(100f));
        tree.SetAlignItems(root, Align.FlexEnd, OverflowAlignment.Safe);

        var child = tree.CreateNode();
        tree.SetDimension(child, Dimension.Width, StyleLength.Points(50f));
        tree.SetDimension(child, Dimension.Height, StyleLength.Points(50f));
        tree.AddChild(root, child);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(50f, tree.GetTop(child), Tolerance);
    }
}
