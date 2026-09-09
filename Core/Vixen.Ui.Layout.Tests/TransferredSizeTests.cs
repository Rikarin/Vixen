// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Layout.Tests;

/// <summary>
///     CSS Sizing §4.1's <i>transferred size</i> inside an intrinsic INLINE pass: a box with a
///     preferred aspect ratio and a definite block size is as wide as the ratio says, and its
///     container's max-content width has to know that.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is <c>chrome_issue_325928327</c> with the grid taken out, and taking the grid out
///         is the whole finding.</b> <c>GridKnownGaps.txt</c> called that fixture "genuinely cyclic"
///         for four generations and a fifth audit softened it to "a phase, not a fact — the single
///         <c>auto</c> row would have to be stretched before the column pass". Both put the defect
///         inside CSS Grid §12. It is not there: the same wrong number comes out of a flex row and out
///         of a block container, with no grid anywhere in the tree.
///     </para>
///     <para>
///         The shape is three boxes. A container with a definite height holds a box whose height is
///         <c>100%</c> of it and whose <c>aspect-ratio</c> is 1. §4.1 makes that box's inline size
///         definite — 40 across, because its block size is 40 — so the container's max-content width
///         is 40. Both intrinsic passes read only what they were OFFERED on the block axis, which in
///         an inline measurement is nothing, so the ratio had no block size to transfer and the
///         container measured <b>zero</b> wide. The box was then laid out 40 × 40 inside a zero-wide
///         parent, which is why the corpus fixture's only two mismatches were the container's own
///         width and an offset computed from it.
///     </para>
///     <para>
///         ⚠ <b>Every case below is paired with a control that removes the ratio and states the width
///         instead.</b> Same tree, same 40, and it PASSED before this work — which is what says the
///         40 asserted here is the ratio's answer and not an artefact of how the fixture is built.
///         Without the pair, a container that reported 40 for some unrelated reason would satisfy
///         these tests.
///     </para>
/// </remarks>
public class TransferredSizeTests {
    const float Tolerance = 0.0001f;

    /// <summary>
    ///     A flex container's max-content width sees through the ratio to its own stated height.
    /// </summary>
    /// <remarks>
    ///     The flex path's own reading. <c>CalculateFlexLayoutImpl</c> derived
    ///     <c>availableInnerHeight</c> from the offer alone, so a container asked only for its inline
    ///     size passed <c>NaN</c> down as the percentage basis while holding a <c>height</c> of its
    ///     own — and <c>ComputeFlexBasisForChild</c>'s ratio transfer, which needs the item's block
    ///     size to be definite, never fired.
    /// </remarks>
    [Fact]
    public void A_flex_container_with_a_stated_height_is_as_wide_as_its_ratios_child() {
        using var tree = new LayoutTree();
        var (root, box, inner) = Stack(tree, Display.Flex, ratio: true);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(40f, tree.GetWidth(root), Tolerance);
        Assert.Equal(40f, tree.GetWidth(box), Tolerance);
        Assert.Equal(40f, tree.GetWidth(inner), Tolerance);
        Assert.Equal(40f, tree.GetHeight(inner), Tolerance);
    }

    /// <summary>
    ///     A block container's max-content width does the same.
    /// </summary>
    /// <remarks>
    ///     The block path's own reading, and a separate line of code.
    ///     <c>DetermineBlockContentWidth</c> asked such a child for a max-content width, which for a
    ///     childless box is its padding and border; <c>ResolveBlockChildBox</c> has always done the
    ///     transfer for the child's LAYOUT, one screen further down, so the box was placed at the
    ///     ratio's width inside a container sized as though it were not there.
    /// </remarks>
    [Fact]
    public void A_block_container_with_a_stated_height_is_as_wide_as_its_ratios_child() {
        using var tree = new LayoutTree();
        var (root, box, inner) = Stack(tree, Display.Block, ratio: true);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(40f, tree.GetWidth(root), Tolerance);
        Assert.Equal(40f, tree.GetWidth(box), Tolerance);
        Assert.Equal(40f, tree.GetWidth(inner), Tolerance);
        Assert.Equal(40f, tree.GetHeight(inner), Tolerance);
    }

    /// <summary>
    ///     The same two trees with the ratio replaced by a stated width, which were already right.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The control, and it is what stops the two tests above passing for the wrong reason.</b>
    ///     Both assert a container 40 wide, and a store that ignored the ratio but happened to report
    ///     40 — from the child's own declaration, from the viewport, from a default — would satisfy
    ///     them. Here the 40 is stated on the child and the transfer is not involved at all, so the
    ///     pair separates "the ratio was read" from "the number is 40".
    /// </remarks>
    [Theory]
    [InlineData(Display.Flex)]
    [InlineData(Display.Block)]
    public void The_same_tree_with_a_stated_width_answers_the_same_and_always_did(Display display) {
        using var tree = new LayoutTree();
        var (root, box, inner) = Stack(tree, display, ratio: false);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(40f, tree.GetWidth(root), Tolerance);
        Assert.Equal(40f, tree.GetWidth(box), Tolerance);
        Assert.Equal(40f, tree.GetWidth(inner), Tolerance);
    }

    /// <summary>
    ///     A grid with one <c>auto</c> row and a definite height gives its column pass that height.
    /// </summary>
    /// <remarks>
    ///     <c>chrome_issue_325928327</c>'s own tree, written out. The grid's third of the fix is one
    ///     number rather than a reordering: with exactly one row and <c>align-content</c> stretching,
    ///     the row is the container's inner height whatever the column pass decides, so §5.2.1's
    ///     "the containing block's size is not yet known" is false and the item's <c>height: 100%</c>
    ///     has something to resolve against.
    /// </remarks>
    [Fact]
    public void A_grid_with_one_stretched_row_measures_its_column_against_its_own_height() {
        using var tree = new LayoutTree();

        var root = tree.CreateNode();
        tree.SetDisplay(root, Display.Grid);
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(40f));

        var item = tree.CreateNode();
        tree.SetDisplay(item, Display.Flex);
        tree.SetDimension(item, Dimension.Height, StyleLength.Percent(100f));
        tree.AddChild(root, item);

        var inner = tree.CreateNode();
        tree.SetDisplay(inner, Display.Flex);
        tree.SetDimension(inner, Dimension.Height, StyleLength.Percent(100f));
        tree.SetAspectRatio(inner, 1f);
        tree.AddChild(item, inner);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(40f, tree.GetWidth(root), Tolerance);
        Assert.Equal(40f, tree.GetWidth(item), Tolerance);
        Assert.Equal(40f, tree.GetWidth(inner), Tolerance);
        Assert.Equal(0f, tree.GetLeft(item), Tolerance);
    }

    /// <summary>
    ///     A grid whose rows are not one stretched row is left alone, because its area is not known.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The other half of the grid clause, and the reason it is a clause and not a rule.</b>
    ///     Two rows share the container's height between them by a rule the column pass has not run
    ///     yet, so the area's block size really is unknown and §5.2.1 really does make the percentage
    ///     behave as <c>auto</c>. This asserts the store still says so — a version that handed the
    ///     whole container height to every item regardless would give 40 here, and would be wrong by
    ///     a factor of the row count.
    /// </remarks>
    [Fact]
    public void Two_rows_leave_the_area_indefinite_and_the_ratio_transfers_nothing() {
        using var tree = new LayoutTree();

        var root = tree.CreateNode();
        tree.SetDisplay(root, Display.Grid);
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(40f));

        for (var i = 0; i < 2; i++) {
            var item = tree.CreateNode();
            tree.SetDisplay(item, Display.Flex);
            tree.SetDimension(item, Dimension.Height, StyleLength.Percent(100f));
            tree.SetGridPlacement(item, Edge.Top, GridPlacement.Line(i + 1));
            tree.AddChild(root, item);

            var inner = tree.CreateNode();
            tree.SetDisplay(inner, Display.Flex);
            tree.SetDimension(inner, Dimension.Height, StyleLength.Percent(100f));
            tree.SetAspectRatio(inner, 1f);
            tree.AddChild(item, inner);
        }

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(2, tree.GetChildCount(root));
        Assert.Equal(0f, tree.GetWidth(root), Tolerance);
    }

    /// <summary>A container of the given display holding a 40-point-tall ratio box, or a stated one.</summary>
    static (LayoutNodeId Root, LayoutNodeId Box, LayoutNodeId Inner) Stack(
        LayoutTree tree,
        Display display,
        bool ratio
    ) {
        // ⚠ A BLOCK root, and the keyword is load-bearing. A flex parent hands a definite height DOWN
        // to an item it is about to size, so a flex root measures the box with a stretch-fit block
        // axis and the transfer fires on machinery that was already there. A block container asks its
        // children how wide they want to be and offers nothing on the block axis, which is the
        // question both intrinsic passes used to answer with zero.
        var root = tree.CreateNode();
        tree.SetDisplay(root, Display.Block);

        var box = tree.CreateNode();
        tree.SetDisplay(box, display);
        tree.SetDimension(box, Dimension.Height, StyleLength.Points(40f));
        tree.AddChild(root, box);

        var inner = tree.CreateNode();
        tree.SetDisplay(inner, Display.Flex);
        tree.AddChild(box, inner);

        if (ratio) {
            tree.SetDimension(inner, Dimension.Height, StyleLength.Percent(100f));
            tree.SetAspectRatio(inner, 1f);
        } else {
            tree.SetDimension(inner, Dimension.Height, StyleLength.Points(40f));
            tree.SetDimension(inner, Dimension.Width, StyleLength.Points(40f));
        }

        return (root, box, inner);
    }
}
