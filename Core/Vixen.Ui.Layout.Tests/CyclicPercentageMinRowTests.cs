// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Layout.Tests;

/// <summary>
///     A percentage <c>min-width</c> on an item of a ROW is a fraction of that row's content box — and,
///     when the row is sized from its content, of its final width rather than of the offer (#1474, the
///     main-axis half of #1470).
/// </summary>
/// <remarks>
///     CSS readings stated per test; Chrome not measured. Every number is closed-form: a 50-point item
///     whose minimum is a percentage of a box that is either stated or made of that item.
/// </remarks>
public class CyclicPercentageMinRowTests {
    /// <summary>
    ///     A 400-point column holding a <c>flex-start</c> row — or <paramref name="depth" /> of them,
    ///     each the only item of the one above with <c>min-width: 100%</c> — and in the innermost a
    ///     <c>50x10</c> unshrinkable item with <c>min-width</c> of <paramref name="percent" />.
    /// </summary>
    static (LayoutTree Tree, LayoutNodeId Root, LayoutNodeId[] Rows, LayoutNodeId Item) Build(
        float percent = 100f,
        float rowWidth = float.NaN,
        int depth = 1
    ) {
        var tree = new LayoutTree();

        var root = tree.CreateNode();
        tree.SetFlexDirection(root, FlexDirection.Column);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(400f));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(1000f));

        var rows = new LayoutNodeId[depth];
        var parent = root;
        for (var level = 0; level < depth; level++) {
            var row = tree.CreateNode();
            tree.SetFlexDirection(row, FlexDirection.Row);
            tree.SetAlignSelf(row, Align.FlexStart);
            tree.SetFlexShrink(row, 0f);
            if (level == 0 && !float.IsNaN(rowWidth)) {
                tree.SetDimension(row, Dimension.Width, StyleLength.Points(rowWidth));
            }

            if (level > 0) {
                tree.SetMinDimension(row, Dimension.Width, StyleLength.Percent(100f));
            }

            tree.AddChild(parent, row);
            rows[level] = row;
            parent = row;
        }

        var item = tree.CreateNode();
        tree.SetDimension(item, Dimension.Width, StyleLength.Points(50f));
        tree.SetDimension(item, Dimension.Height, StyleLength.Points(10f));
        tree.SetFlexShrink(item, 0f);
        tree.SetMinDimension(item, Dimension.Width, StyleLength.Percent(percent));
        tree.AddChild(parent, item);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);
        return (tree, root, rows, item);
    }

    /// <summary>
    ///     ⚠ <b>The defect as filed</b>: row 400, item 400. The item's contribution treats its cyclic
    ///     minimum as zero (CSS Sizing §5.2.1), so the row is the item's 50 — and the item is then 100%
    ///     of 50.
    /// </summary>
    [Fact]
    public void A_percentage_minimum_inside_a_content_sized_row_resolves_against_its_final_width() {
        var (tree, _, rows, item) = Build();

        Assert.Equal(50f, tree.GetWidth(rows[0]));
        Assert.Equal(50f, tree.GetWidth(item));
    }

    /// <summary>
    ///     ⚠ <b>The basis, where the row's width is stated</b>: <c>min-width: 50%</c> of a 200-point row
    ///     is 100. It came out 200 — half of the 400-point column ABOVE the row, because the row clamped
    ///     its items against its own containing block (Yoga's <c>mainAxisOwnerSize</c>) and not its
    ///     content box. No fixture in either corpus distinguishes the two.
    /// </summary>
    [Fact]
    public void A_percentage_minimum_in_a_row_of_stated_width_is_a_fraction_of_that_row() {
        var (tree, _, rows, item) = Build(percent: 50f, rowWidth: 200f);

        Assert.Equal(200f, tree.GetWidth(rows[0]));
        Assert.Equal(100f, tree.GetWidth(item));
    }

    /// <summary>
    ///     A percentage the item's own width exceeds once resolved changes nothing: 20% of the row's 50
    ///     is 10, so the row and the item are 50. ⚠ This was not a control before the fix — it came out
    ///     80, 20% of the 400-point offer, which is the defect at a percentage nobody would notice as
    ///     "full width".
    /// </summary>
    [Fact]
    public void A_minimum_smaller_than_the_item_leaves_the_row_its_content() {
        var (tree, _, rows, item) = Build(percent: 20f);

        Assert.Equal(50f, tree.GetWidth(rows[0]));
        Assert.Equal(50f, tree.GetWidth(item));
    }

    /// <summary>
    ///     ⚠ <b>The re-entry, which the filed case cannot see</b>: there the item is the row's whole
    ///     content, so 100% of the row and the item's own width are both 50 and an implementation that
    ///     only withheld would pass. Here a 50-point item sits beside a 20-point one with
    ///     <c>min-width: 50%</c>: the row is sized from 50 + 20 = 70 with the percentage withheld, and
    ///     the second item is then 50% of 70 = 35 — so the line is 85 in a 70-point row and overflows,
    ///     because neither item shrinks and the box was already decided.
    /// </summary>
    [Fact]
    public void The_row_lays_its_items_out_again_where_the_percentage_resolves() {
        var tree = new LayoutTree();

        var root = tree.CreateNode();
        tree.SetFlexDirection(root, FlexDirection.Column);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(400f));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(1000f));

        var row = tree.CreateNode();
        tree.SetFlexDirection(row, FlexDirection.Row);
        tree.SetAlignSelf(row, Align.FlexStart);
        tree.AddChild(root, row);

        var wide = tree.CreateNode();
        tree.SetDimension(wide, Dimension.Width, StyleLength.Points(50f));
        tree.SetDimension(wide, Dimension.Height, StyleLength.Points(10f));
        tree.SetFlexShrink(wide, 0f);
        tree.AddChild(row, wide);

        var narrow = tree.CreateNode();
        tree.SetDimension(narrow, Dimension.Width, StyleLength.Points(20f));
        tree.SetDimension(narrow, Dimension.Height, StyleLength.Points(10f));
        tree.SetFlexShrink(narrow, 0f);
        tree.SetMinDimension(narrow, Dimension.Width, StyleLength.Percent(50f));
        tree.AddChild(row, narrow);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(70f, tree.GetWidth(row));
        Assert.Equal(50f, tree.GetWidth(wide));
        Assert.Equal(35f, tree.GetWidth(narrow));
        Assert.Equal(50f, tree.GetLeft(narrow));
    }

    /// <summary>
    ///     Rows nested <paramref name="depth" /> deep, each <c>min-width: 100%</c> of the one above:
    ///     every one is the item's 50, and the work grows linearly with depth — each row's re-entry is
    ///     skipped while that row is itself being measured withheld for its parent, which is the
    ///     column's #1470 discipline applied to the main axis.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Nested_rows_each_resolve_against_their_parents_final_width_without_compounding(int depth) {
        var (tree, _, rows, item) = Build(depth: depth);

        foreach (var row in rows) {
            Assert.Equal(50f, tree.GetWidth(row));
        }

        Assert.Equal(50f, tree.GetWidth(item));

        // Measured: from depth two on, each level adds three re-entries and sixteen flex runs. Without
        // the skip an inner row also re-enters inside every pass in which its parent measures it
        // withheld — 6, 13 and 20 re-entries at depths two to four, work thrown away because the
        // parent lays it out again.
        Assert.Equal(depth == 1 ? 2 : (3 * depth) - 2, tree.MainSizeReentries);
        Assert.Equal(depth == 1 ? 5 : (16 * depth) - 17, tree.FlexAlgorithmRuns);
    }
}
