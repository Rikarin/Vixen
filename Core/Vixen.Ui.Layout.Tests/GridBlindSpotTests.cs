// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using CsCheck;
using Xunit;

namespace Vixen.Ui.Layout.Tests;

/// <summary>
///     The grid rules the Taffy corpus cannot see, whatever it grows to.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Doc 43's exit criterion 5, for grid.</b> <see cref="AutomaticMinimumSizeTests" />
///         exists because deleting CSS Flexbox §4.5 leaves all 534 Yoga fixtures green — an oracle
///         answers the questions it was built to ask, and Yoga's generator emits no fixture that
///         shrinks a measured leaf past its content. Grid arrived with two thousand right answers
///         and no equivalent file, so its blind spots have been argued about and never asserted.
///     </para>
///     <para>
///         The three named below are <b>measured</b> rather than suspected:
///     </para>
///     <para>
///         ⚠ <b>Direction inheritance is invisible to the whole corpus.</b> Every one of Taffy's
///         22 776 nodes states its own <c>direction</c> — the count of ` direction="` across the
///         eight files is 22 776 exactly, against 21 840 <c>&lt;div&gt;</c> and 936
///         <c>&lt;text&gt;</c> — so <see cref="Direction.Inherit" /> is never stored and
///         <see cref="StyleResolution.ResolveDirection" />'s owner argument is never read. Rewriting
///         it to ignore its owner leaves every Taffy fixture green and turns <b>374 of Yoga's 534</b>
///         red, plus two in <c>ScrollbarGutterTests</c> and three across the two inline files — and
///         <b>not one grid test anywhere</b>. That last number is this file's reason to exist.
///     </para>
///     <para>
///         ⚠ <b>Every Taffy fixture is a cold layout</b>, so nothing in it exercises dirty
///         propagation, the measure cache, or a second pass over a tree that changed. A grid caches
///         more than the other three algorithms do — placement, the track arena, the two intrinsic
///         probes — so it has the most to get wrong here and the least coverage of it.
///     </para>
///     <para>
///         ⚠ <b>Every fixture rounds at scale 1 or not at all</b>, so a fractional
///         <see cref="LayoutTree.PointScaleFactor" /> — a retina editor — is untested by it. The
///         oracle for both of these is <see cref="PixelRoundingTests" />'s: a second tree built from
///         scratch with the same styles and laid out cold, which by construction skips no shortcut.
///     </para>
/// </remarks>
public class GridBlindSpotTests {
    const float Tolerance = 0.0001f;

    /// <summary>
    ///     A grid item's automatic minimum inline size is its COLUMNS, not the sum of everything in
    ///     it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The intrinsic probe read a grid container as a flex row.</b>
    ///         <c>ComputeMinContentSizeUncached</c> takes a box's <c>flex-direction</c> and adds its
    ///         children up along it, which for a grid counts two items in the same column twice.
    ///         <c>gridflex_row_integration</c> is the corpus's version — four 20-wide texts in a 2×2
    ///         grid, 40 in Chrome and 80 from the probe — and it is one fixture against a rule that
    ///         applies to every grid inside every flex container.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The declared <c>flex-basis</c> is load-bearing and is why this is written by
    ///         hand.</b> <c>ComputeAutoMinMainSize</c> caps the floor at the item's own flex basis
    ///         whenever that basis was MEASURED, which hides every over-reported floor the probe
    ///         produces; a DECLARED basis carries no such guarantee, so the number reaches the
    ///         algorithm. See <c>Rikarin/Vixen#265</c>.
    ///     </para>
    ///     <para>
    ///         The oracle is closed-form rather than recorded: four items of the same width in
    ///         <i>c</i> columns fill every column for any <i>c</i> up to four, so the smallest the
    ///         grid can be is exactly <i>c</i> times one item. The item count never enters it —
    ///         which is precisely what the old answer of 80 got wrong.
    ///     </para>
    ///     <para>
    ///         ⚠ The old reading answers <b>20</b> here rather than the corpus's 80, and the two are
    ///         the same defect: this grid states no <c>flex-direction</c>, so the flex reading takes
    ///         its cross-axis maximum where the corpus's — which states <c>row</c> on every node —
    ///         takes the main-axis sum. A number read off a property a grid does not have can land
    ///         on either side of the truth.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData(1, 20f)]
    [InlineData(2, 40f)]
    [InlineData(4, 80f)]
    public void A_grid_item_is_floored_at_its_columns_and_not_at_the_sum_of_its_items(int columns, float expected) {
        using var tree = new LayoutTree();

        // One point wide, so nothing but §4.5's floor decides how wide the grid comes out.
        var root = tree.CreateNode();
        tree.SetFlexDirection(root, FlexDirection.Row);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(1f));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(40f));

        var template = new GridTrackSize[columns];
        Array.Fill(template, GridTrackSize.Auto);

        var grid = tree.CreateNode();
        tree.SetDisplay(grid, Display.Grid);
        tree.SetGridTemplateColumns(grid, template);
        tree.SetFlexShrink(grid, 1f);
        tree.SetFlexBasis(grid, StyleLength.Points(0f));
        tree.AddChild(root, grid);

        for (var cell = 0; cell < 4; cell++) {
            var item = tree.CreateNode();
            tree.SetDimension(item, Dimension.Width, StyleLength.Points(20f));
            tree.SetDimension(item, Dimension.Height, StyleLength.Points(10f));
            tree.AddChild(grid, item);
        }

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(expected, tree.GetWidth(grid), Tolerance);
    }

    [Fact]
    public void An_inherited_rtl_reaches_a_grid_item_that_never_states_one() {
        // A two-column grid whose container says nothing about direction. In `rtl` the first item
        // belongs in the RIGHT-hand track, and the only thing that can tell it so is the direction
        // threaded down from the layout call.
        //
        // ⚠ The corpus states `direction` on every node, so this can only be asked by hand — and the
        // sabotage that proves it (ResolveDirection ignoring its owner) leaves all 2 408 flex, all
        // the grid and all the block fixtures green.
        using var tree = new LayoutTree();
        var root = tree.CreateNode();
        tree.SetDisplay(root, Display.Grid);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(100f));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(20f));
        tree.SetGridTemplateColumns(
            root,
            [GridTrackSize.Single(GridSizingFunction.Points(40f)), GridTrackSize.Single(GridSizingFunction.Points(60f))]
        );

        var first = tree.CreateNode();
        tree.SetDimension(first, Dimension.Height, StyleLength.Points(10f));
        tree.AddChild(root, first);

        var second = tree.CreateNode();
        tree.SetDimension(second, Dimension.Height, StyleLength.Points(10f));
        tree.AddChild(root, second);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Rtl);

        Assert.Equal(60f, tree.GetLeft(first), Tolerance);
        Assert.Equal(40f, tree.GetWidth(first), Tolerance);
        Assert.Equal(0f, tree.GetLeft(second), Tolerance);
        Assert.Equal(60f, tree.GetWidth(second), Tolerance);

        // The same tree the other way round, so the assertion is about the inheritance and not about
        // one hard-coded edge.
        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(0f, tree.GetLeft(first), Tolerance);
        Assert.Equal(40f, tree.GetWidth(first), Tolerance);
        Assert.Equal(40f, tree.GetLeft(second), Tolerance);
        Assert.Equal(60f, tree.GetWidth(second), Tolerance);
    }

    [Fact]
    public void An_inherited_direction_crosses_a_silent_grid_between_two_that_speak() {
        // ⚠ One level is not enough to catch an implementation that reads the *root's* direction
        // instead of the owner's. Here an `ltr` grid holds an rtl one, which holds a grid that
        // states nothing: the innermost items must go right-to-left, because the direction they
        // inherit is their parent's and not the tree's.
        using var tree = new LayoutTree();
        var outer = tree.CreateNode();
        tree.SetDisplay(outer, Display.Grid);
        tree.SetDirection(outer, Direction.Ltr);
        tree.SetDimension(outer, Dimension.Width, StyleLength.Points(100f));
        tree.SetDimension(outer, Dimension.Height, StyleLength.Points(20f));

        var middle = tree.CreateNode();
        tree.SetDisplay(middle, Display.Grid);
        tree.SetDirection(middle, Direction.Rtl);
        tree.AddChild(outer, middle);

        var inner = tree.CreateNode();
        tree.SetDisplay(inner, Display.Grid);
        tree.SetGridTemplateColumns(
            inner,
            [GridTrackSize.Single(GridSizingFunction.Points(30f)), GridTrackSize.Single(GridSizingFunction.Points(70f))]
        );
        tree.AddChild(middle, inner);

        var first = tree.CreateNode();
        tree.SetDimension(first, Dimension.Height, StyleLength.Points(10f));
        tree.AddChild(inner, first);

        var second = tree.CreateNode();
        tree.SetDimension(second, Dimension.Height, StyleLength.Points(10f));
        tree.AddChild(inner, second);

        tree.CalculateLayout(outer, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(70f, tree.GetLeft(first), Tolerance);
        Assert.Equal(0f, tree.GetLeft(second), Tolerance);
    }

    [Fact]
    public void A_grid_relaid_out_after_a_change_matches_one_laid_out_cold() {
        // ⚠ The whole Taffy corpus is cold, so this is the first thing that asks a grid to lay
        // itself out twice. Placement, the track arena and the two intrinsic probes are all cached
        // across passes; the oracle is a second tree with the same styles, built from scratch.
        Gen.Select(Gen.Int[1, 3], Gen.Int[1, 3], Gen.Int[0, 999].Array[1, 10])
            .Sample(shape => {
                var (columns, rows, mutations) = shape;

                var spec = new GridSpec(columns, rows);
                using var incremental = new LayoutTree();
                var incrementalRoot = spec.Build(incremental);
                incremental.CalculateLayout(incrementalRoot, 220f, 160f, Direction.Ltr);

                foreach (var mutation in mutations) {
                    spec.Mutate(mutation);
                    spec.Apply(incremental, incrementalRoot, mutation);
                    incremental.CalculateLayout(incrementalRoot, 220f, 160f, Direction.Ltr);

                    using var cold = new LayoutTree();
                    var coldRoot = spec.Build(cold);
                    cold.CalculateLayout(coldRoot, 220f, 160f, Direction.Ltr);

                    AssertSameLayout(incremental, incrementalRoot, cold, coldRoot);
                }
            }
            );
    }

    [Fact]
    public void A_grid_on_a_fractional_pixel_grid_matches_one_laid_out_cold() {
        // ⚠ Every Taffy fixture rounds at scale 1 or declares `use-rounding="false"`, so the retina
        // case — where a track edge lands between pixels and its neighbour has to agree about the
        // seam — is asked here for the first time on a grid. Same oracle, at 2× with fractional
        // available space.
        Gen.Select(Gen.Int[1, 3], Gen.Int[1, 3], Gen.Int[0, 999].Array[1, 10])
            .Sample(shape => {
                var (columns, rows, mutations) = shape;

                var spec = new GridSpec(columns, rows);
                using var incremental = new LayoutTree { PointScaleFactor = 2f };
                var incrementalRoot = spec.Build(incremental);
                incremental.CalculateLayout(incrementalRoot, 220.5f, 160.25f, Direction.Ltr);

                foreach (var mutation in mutations) {
                    spec.Mutate(mutation);
                    spec.Apply(incremental, incrementalRoot, mutation);
                    incremental.CalculateLayout(incrementalRoot, 220.5f, 160.25f, Direction.Ltr);

                    using var cold = new LayoutTree { PointScaleFactor = 2f };
                    var coldRoot = spec.Build(cold);
                    cold.CalculateLayout(coldRoot, 220.5f, 160.25f, Direction.Ltr);

                    AssertSameLayout(incremental, incrementalRoot, cold, coldRoot);
                }
            }
            );
    }

    [Fact]
    public void Adjacent_grid_tracks_do_not_round_into_a_seam() {
        // What the rounding pass exists for, on the algorithm that had no test of it. Three
        // 33.3-point rows at 2× scale: their rounded heights differ from each other and they still
        // tile the container exactly.
        using var tree = new LayoutTree { PointScaleFactor = 2f };
        var root = tree.CreateNode();
        tree.SetDisplay(root, Display.Grid);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(100f));
        tree.SetGridTemplateRows(
            root,
            [
                GridTrackSize.Single(GridSizingFunction.Points(33.3f)),
                GridTrackSize.Single(GridSizingFunction.Points(33.3f)),
                GridTrackSize.Single(GridSizingFunction.Points(33.3f))
            ]
        );

        var boxes = new LayoutNodeId[3];
        for (var i = 0; i < boxes.Length; i++) {
            boxes[i] = tree.CreateNode();
            tree.AddChild(root, boxes[i]);
        }

        tree.CalculateLayout(root, 100f, 200f, Direction.Ltr);

        for (var i = 1; i < boxes.Length; i++) {
            var previousBottom = tree.GetTop(boxes[i - 1]) + tree.GetHeight(boxes[i - 1]);
            Assert.Equal(previousBottom, tree.GetTop(boxes[i]));
        }
    }

    /// <summary>A grid of fractionally-sized items, and the current size of each.</summary>
    /// <remarks>
    ///     Deliberately a mix of the three track kinds: a fixed column, a fractional one and an
    ///     <c>auto</c> one whose size comes from the items in it, so that a mutation to an item can
    ///     move a track and not only itself.
    /// </remarks>
    sealed class GridSpec(int columns, int rows) {
        readonly float[] widths = CreateSizes(columns * rows, 17.3f, 4.7f);
        readonly float[] heights = CreateSizes(columns * rows, 21.4f, 3.3f);

        public LayoutNodeId Build(LayoutTree tree) {
            var root = tree.CreateNode();
            tree.SetDisplay(root, Display.Grid);
            tree.SetPadding(root, Edge.All, StyleLength.Points(3.5f));
            tree.SetGap(root, Gutter.Column, StyleLength.Points(1.25f));
            tree.SetGap(root, Gutter.Row, StyleLength.Points(2.75f));

            var tracks = new GridTrackSize[columns];
            for (var c = 0; c < columns; c++) {
                tracks[c] = (c % 3) switch {
                    0 => GridTrackSize.Single(GridSizingFunction.Points(31.5f)),
                    1 => GridTrackSize.Single(GridSizingFunction.Flex(1f)),
                    _ => GridTrackSize.Single(GridSizingFunction.Auto)
                };
            }

            tree.SetGridTemplateColumns(root, tracks);

            for (var i = 0; i < columns * rows; i++) {
                var cell = tree.CreateNode();
                tree.SetDimension(cell, Dimension.Width, StyleLength.Points(widths[i]));
                tree.SetDimension(cell, Dimension.Height, StyleLength.Points(heights[i]));
                tree.AddChild(root, cell);
            }

            return root;
        }

        public void Mutate(int mutation) {
            if (mutation % 2 == 0) {
                widths[mutation % widths.Length] = 11f + (mutation % 23 * 0.37f);
            } else {
                heights[mutation % heights.Length] = 13f + (mutation % 17 * 0.41f);
            }
        }

        public void Apply(LayoutTree tree, LayoutNodeId root, int mutation) {
            if (mutation % 2 == 0) {
                var index = mutation % widths.Length;
                tree.SetDimension(tree.GetChild(root, index), Dimension.Width, StyleLength.Points(widths[index]));

                return;
            }

            var at = mutation % heights.Length;
            tree.SetDimension(tree.GetChild(root, at), Dimension.Height, StyleLength.Points(heights[at]));
        }

        static float[] CreateSizes(int count, float start, float step) {
            var sizes = new float[count];
            for (var i = 0; i < count; i++) {
                sizes[i] = start + (i * step % 19f);
            }

            return sizes;
        }
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(65_534, 65_536)]
    [InlineData(70_000, 80_000)]
    [InlineData(-70_000, 2)]
    public void Two_items_past_the_track_ceiling_keep_their_own_cells(int firstLine, int secondLine) {
        // ⚠ <b>The ceiling on how many tracks this store will allocate used to decide which items
        // SHARE A CELL.</b> Both clamps in PlaceGridItems saturated, so two items whose authored
        // lines were both past LayoutLimits.MaximumGridTracks were given the same start and merged:
        // this grid came out 50 wide with both items at x=0 for lines 70 000 and 80 000, where lines
        // 65 534 and 65 536 — one of which survives the clamp — gave the right 100.
        //
        // ⚠ THE ORACLE IS A CONSERVATION LAW rather than a table: two 50-point items that do not
        // share a cell make a 100-point max-content grid whatever lies between them, because every
        // track between them is empty and an empty auto track is zero wide. So the same two numbers
        // are the answer on all four rows, and a row that merges the items cannot produce them.
        // The first row is the ordinary grid that never reaches the collapse at all, and the last
        // walks the map leftwards from the explicit origin rather than rightwards.
        //
        // ⚠ The items need a SIZE. A probe built from empty boxes reports x=0 for two zero-width
        // tracks exactly as it does for one shared track, and cannot tell the defect from its fix.
        using var tree = new LayoutTree();
        var root = tree.CreateNode();
        tree.SetDisplay(root, Display.Grid);

        var first = tree.CreateNode();
        tree.SetDimension(first, Dimension.Width, StyleLength.Points(50f));
        tree.SetDimension(first, Dimension.Height, StyleLength.Points(20f));
        tree.SetGridPlacement(first, Edge.Left, GridPlacement.Line(firstLine));
        tree.AddChild(root, first);

        var second = tree.CreateNode();
        tree.SetDimension(second, Dimension.Width, StyleLength.Points(50f));
        tree.SetDimension(second, Dimension.Height, StyleLength.Points(20f));
        tree.SetGridPlacement(second, Edge.Left, GridPlacement.Line(secondLine));
        tree.AddChild(root, second);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(100f, tree.GetWidth(root), Tolerance);
        Assert.Equal(0f, tree.GetLeft(first), Tolerance);
        Assert.Equal(50f, tree.GetLeft(second), Tolerance);
    }

    static void AssertSameLayout(LayoutTree left, LayoutNodeId leftNode, LayoutTree right, LayoutNodeId rightNode) {
        Assert.Equal(right.GetLeft(rightNode), left.GetLeft(leftNode));
        Assert.Equal(right.GetTop(rightNode), left.GetTop(leftNode));
        Assert.Equal(right.GetWidth(rightNode), left.GetWidth(leftNode));
        Assert.Equal(right.GetHeight(rightNode), left.GetHeight(leftNode));
        Assert.Equal(right.GetChildCount(rightNode), left.GetChildCount(leftNode));

        for (var i = 0; i < left.GetChildCount(leftNode); i++) {
            AssertSameLayout(left, left.GetChild(leftNode, i), right, right.GetChild(rightNode, i));
        }
    }
}
