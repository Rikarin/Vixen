// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Layout.Tests;

/// <summary>
///     A percentage <c>min-height</c> or <c>max-height</c> on an item of a COLUMN is a fraction of that
///     column's content box where its height is definite, and nothing where it is not (#1475, the
///     column's half of #1474).
/// </summary>
/// <remarks>
///     <para>
///         CSS readings stated per test; Chrome not measured. Every number is closed-form: a 10-point
///         item whose minimum is a percentage of a box that is stated, stretched, flexed or made of the
///         item.
///     </para>
///     <para>
///         ⚠ <b>Not the row's rule.</b> A row withholds its items' cyclic <c>min-width</c> while it sizes
///         itself and re-enters to resolve it (CSS Sizing §5.2.1). A block-axis percentage against an
///         indefinite height is not cyclic in that sense — CSS 2.1 §10.7 treats it as zero for
///         <c>min-height</c> and <c>none</c> for <c>max-height</c>, for good — so a content-sized
///         column withholds and never re-enters. Its work is unchanged by construction.
///     </para>
/// </remarks>
public class PercentageMinColumnTests {
    /// <summary>
    ///     A 400x400 row root holding one column (configured by <paramref name="column" />), holding one
    ///     <c>50x</c><paramref name="itemHeight" /> unshrinkable item configured by <paramref name="item" />.
    /// </summary>
    static (LayoutTree Tree, LayoutNodeId Column, LayoutNodeId Item) Build(
        Action<LayoutTree, LayoutNodeId> column,
        Action<LayoutTree, LayoutNodeId> item,
        float itemHeight = 10f
    ) {
        var tree = new LayoutTree();

        var root = tree.CreateNode();
        tree.SetFlexDirection(root, FlexDirection.Row);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(400f));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(400f));

        var col = tree.CreateNode();
        tree.SetFlexDirection(col, FlexDirection.Column);
        column(tree, col);
        tree.AddChild(root, col);

        var leaf = tree.CreateNode();
        tree.SetDimension(leaf, Dimension.Width, StyleLength.Points(50f));
        tree.SetDimension(leaf, Dimension.Height, StyleLength.Points(itemHeight));
        tree.SetFlexShrink(leaf, 0f);
        item(tree, leaf);
        tree.AddChild(col, leaf);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);
        return (tree, col, leaf);
    }

    /// <summary>
    ///     ⚠ <b>The defect as filed</b>: <c>min-height: 50%</c> in a 200-point column is 100. It came out
    ///     200 — half of the 400-point row ABOVE the column, because the column clamped its items against
    ///     its own containing block (Yoga's <c>mainAxisOwnerSize</c>) and not its content box.
    /// </summary>
    [Fact]
    public void A_percentage_minimum_in_a_column_of_stated_height_is_a_fraction_of_that_column() {
        var (tree, column, item) = Build(
            (t, c) => {
                t.SetDimension(c, Dimension.Height, StyleLength.Points(200f));
                t.SetAlignSelf(c, Align.FlexStart);
            },
            (t, i) => t.SetMinDimension(i, Dimension.Height, StyleLength.Percent(50f))
        );

        Assert.Equal(200f, tree.GetHeight(column));
        Assert.Equal(100f, tree.GetHeight(item));
    }

    /// <summary>
    ///     The same basis caps: <c>max-height: 50%</c> of a 600-point column holds a 500-point item to
    ///     300. Against the 400-point row above it was 200. ⚠ The column is TALLER than the row on
    ///     purpose — the item's own sizing already clamps against the right basis, so where the wrong
    ///     one is the larger, the smaller cap wins either way and the test cannot fail; its first
    ///     version (a 200-point column) was green against the old code.
    /// </summary>
    [Fact]
    public void A_percentage_maximum_in_a_column_of_stated_height_is_a_fraction_of_that_column() {
        var (tree, _, item) = Build(
            (t, c) => {
                t.SetDimension(c, Dimension.Height, StyleLength.Points(600f));
                t.SetAlignSelf(c, Align.FlexStart);
            },
            (t, i) => t.SetMaxDimension(i, Dimension.Height, StyleLength.Percent(50f)),
            itemHeight: 500f
        );

        Assert.Equal(300f, tree.GetHeight(item));
    }

    /// <summary>
    ///     The content box, not the border box: 100 points of top padding leave the 200-point column a
    ///     100-point content box, so 50% is 50.
    /// </summary>
    [Fact]
    public void Padding_comes_off_the_basis() {
        var (tree, _, item) = Build(
            (t, c) => {
                t.SetDimension(c, Dimension.Height, StyleLength.Points(200f));
                t.SetBoxSizing(c, BoxSizing.BorderBox);
                t.SetPadding(c, Edge.Top, StyleLength.Points(100f));
                t.SetAlignSelf(c, Align.FlexStart);
            },
            (t, i) => t.SetMinDimension(i, Dimension.Height, StyleLength.Percent(50f))
        );

        Assert.Equal(50f, tree.GetHeight(item));
    }

    /// <summary>
    ///     ⚠ <b>A content-sized column has no basis</b>: <c>align-self: flex-start</c> leaves its height
    ///     indefinite, and CSS 2.1 §10.7 treats the percentage as zero, so the item keeps its own 10 and
    ///     the column is 10. It came out 200 for both — the row's 400 again. (Zero and §5.2.1's "behaves
    ///     as auto" agree here, since the automatic minimum of an unshrinkable 10-point item is 10.)
    /// </summary>
    [Fact]
    public void A_percentage_minimum_in_a_content_sized_column_resolves_to_nothing() {
        var (tree, column, item) = Build(
            (t, c) => t.SetAlignSelf(c, Align.FlexStart),
            (t, i) => t.SetMinDimension(i, Dimension.Height, StyleLength.Percent(50f))
        );

        Assert.Equal(10f, tree.GetHeight(column));
        Assert.Equal(10f, tree.GetHeight(item));
    }

    /// <summary>
    ///     A STRETCHED column's height is definite (Flexbox §9.8 rule 1 — the item stretches in a
    ///     container of definite cross size), so 50% of its content box resolves: 400 less 100 of
    ///     padding is 300, and the item is 150. Against the row above it was 200.
    /// </summary>
    [Fact]
    public void A_stretched_column_is_a_definite_basis() {
        var (tree, column, item) = Build(
            (t, c) => t.SetPadding(c, Edge.Top, StyleLength.Points(100f)),
            (t, i) => t.SetMinDimension(i, Dimension.Height, StyleLength.Percent(50f))
        );

        Assert.Equal(400f, tree.GetHeight(column));
        Assert.Equal(150f, tree.GetHeight(item));
    }

    /// <summary>
    ///     A FLEXED column's height is definite too (Flexbox §9.8 rule 1, a flex item's main size after
    ///     flexing in a definite container): a 400-point column root gives a grown column 300 beside a
    ///     100-point sibling, and the item is 50% of 300. Against the root it was 200.
    /// </summary>
    [Fact]
    public void A_flexed_column_is_a_definite_basis() {
        var tree = new LayoutTree();

        var root = tree.CreateNode();
        tree.SetFlexDirection(root, FlexDirection.Column);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(400f));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(400f));

        var sibling = tree.CreateNode();
        tree.SetDimension(sibling, Dimension.Height, StyleLength.Points(100f));
        tree.AddChild(root, sibling);

        var column = tree.CreateNode();
        tree.SetFlexDirection(column, FlexDirection.Column);
        tree.SetFlexGrow(column, 1f);
        tree.SetFlexBasis(column, StyleLength.Points(0f));
        tree.AddChild(root, column);

        var item = tree.CreateNode();
        tree.SetDimension(item, Dimension.Width, StyleLength.Points(50f));
        tree.SetDimension(item, Dimension.Height, StyleLength.Points(10f));
        tree.SetFlexShrink(item, 0f);
        tree.SetMinDimension(item, Dimension.Height, StyleLength.Percent(50f));
        tree.AddChild(column, item);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(300f, tree.GetHeight(column));
        Assert.Equal(150f, tree.GetHeight(item));
    }
}
