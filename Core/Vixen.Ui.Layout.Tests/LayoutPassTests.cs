// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Testing;
using Xunit;

namespace Vixen.Ui.Layout.Tests;

/// <summary>
///     What a layout pass costs, rather than what it computes: what it allocates, what it skips, and
///     what it re-measures.
/// </summary>
/// <remarks>
///     The conformance suite says the numbers are right. It says nothing at all about how they were
///     arrived at, and a flexbox implementation that is correct and re-measures the whole tree every
///     frame is not one a UI can be built on.
/// </remarks>
public class LayoutPassTests {
    [Fact]
    public void Laying_out_a_tree_that_did_not_change_allocates_nothing() {
        using var tree = new LayoutTree();
        var root = BuildPanel(tree, rows: 20, columnsPerRow: 5);

        Measured.NothingAllocated(Layout, warmUp: 20, passes: 200);

        return;

        void Layout() => tree.CalculateLayout(root, 800f, 600f, Direction.Ltr);
    }

    [Fact]
    public void Laying_out_a_tree_whose_styles_keep_changing_allocates_nothing() {
        // The realistic steady state: one thing moves every frame and the whole tree is re-laid.
        // This is the gate that matters, and it is the reason a flex line is a range of children
        // rather than a list of them.
        using var tree = new LayoutTree();
        var root = BuildPanel(tree, rows: 20, columnsPerRow: 5);
        var moving = tree.GetChild(tree.GetChild(root, 0), 0);
        var frame = 0;

        Measured.NothingAllocated(Layout, warmUp: 20, passes: 200);

        return;

        void Layout() {
            tree.SetDimension(moving, Dimension.Width, StyleLength.Points(10f + (frame++ % 40)));
            tree.CalculateLayout(root, 800f, 600f, Direction.Ltr);
        }
    }

    [Fact]
    public void A_wrapping_container_allocates_nothing_per_line() {
        // Lines are where an implementation is most tempted to allocate: Yoga collects each one into
        // a vector. Wrapping thirty items into six lines, every frame, has to cost nothing.
        using var tree = new LayoutTree();
        var root = tree.CreateNode();
        tree.SetFlexDirection(root, FlexDirection.Row);
        tree.SetFlexWrap(root, Wrap.Wrap);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(500f));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(500f));

        for (var i = 0; i < 30; i++) {
            var child = tree.CreateNode();
            tree.SetDimension(child, Dimension.Width, StyleLength.Points(90f));
            tree.SetDimension(child, Dimension.Height, StyleLength.Points(40f));
            tree.AddChild(root, child);
        }

        var toggle = tree.GetChild(root, 0);
        var frame = 0;

        Measured.NothingAllocated(Layout, warmUp: 20, passes: 200);

        return;

        void Layout() {
            tree.SetDimension(toggle, Dimension.Height, StyleLength.Points(40f + (frame++ % 3)));
            tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);
        }
    }

    [Fact]
    public void A_static_subtree_is_not_re_measured() {
        // Dirty propagation, measured rather than assumed: changing one leaf must not send the
        // measure function of an untouched sibling anywhere near a second call.
        using var tree = new LayoutTree();
        var root = tree.CreateNode();
        tree.SetFlexDirection(root, FlexDirection.Column);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(200f));

        var measured = 0;
        var quiet = tree.CreateNode();
        tree.SetContext(quiet, 50f);
        tree.SetMeasureFunction(quiet, Count);
        tree.AddChild(root, quiet);

        var noisy = tree.CreateNode();
        tree.SetDimension(noisy, Dimension.Height, StyleLength.Points(10f));
        tree.AddChild(root, noisy);

        tree.CalculateLayout(root, 200f, 400f, Direction.Ltr);
        var afterFirstPass = measured;

        Assert.True(afterFirstPass > 0, "the measure function should have been called at least once");

        for (var i = 1; i <= 10; i++) {
            tree.SetDimension(noisy, Dimension.Height, StyleLength.Points(10f + i));
            tree.CalculateLayout(root, 200f, 400f, Direction.Ltr);
        }

        Assert.Equal(afterFirstPass, measured);

        LayoutSize Count(in MeasureRequest request) {
            measured++;
            return new LayoutSize((float) (request.Context ?? 0f), 20f);
        }
    }

    [Fact]
    public void An_unchanged_tree_is_skipped_entirely() {
        var measured = 0;
        using var tree = new LayoutTree();
        var root = tree.CreateNode();
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(200f));

        var leaf = tree.CreateNode();
        tree.SetContext(leaf, 50f);
        tree.SetMeasureFunction(leaf, Count);
        tree.AddChild(root, leaf);

        tree.CalculateLayout(root, 200f, 400f, Direction.Ltr);
        var afterFirstPass = measured;

        for (var i = 0; i < 10; i++) {
            tree.CalculateLayout(root, 200f, 400f, Direction.Ltr);
        }

        Assert.Equal(afterFirstPass, measured);

        LayoutSize Count(in MeasureRequest request) {
            measured++;
            return new LayoutSize((float) (request.Context ?? 0f), 20f);
        }
    }

    [Fact]
    public void A_hundred_thousand_nodes_lay_out() {
        // Not a benchmark — a statement that the store scales to the size doc 09 names without the
        // recursion or the arena falling over. The timing belongs in Benchmarks/.
        using var tree = new LayoutTree();
        var root = tree.CreateNode();
        tree.SetFlexDirection(root, FlexDirection.Column);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(1000f));

        const int rows = 500;
        const int perRow = 200;

        for (var r = 0; r < rows; r++) {
            var row = tree.CreateNode();
            tree.SetFlexDirection(row, FlexDirection.Row);
            tree.SetDimension(row, Dimension.Height, StyleLength.Points(20f));
            tree.AddChild(root, row);

            for (var c = 0; c < perRow; c++) {
                var cell = tree.CreateNode();
                tree.SetFlexGrow(cell, 1f);
                tree.AddChild(row, cell);
            }
        }

        Assert.Equal((rows * perRow) + rows + 1, tree.NodeCount);

        tree.CalculateLayout(root, 1000f, float.NaN, Direction.Ltr);

        Assert.Equal(1000f, tree.GetWidth(root));
        Assert.Equal(rows * 20f, tree.GetHeight(root));
        Assert.Equal(5f, tree.GetWidth(tree.GetChild(tree.GetChild(root, 0), 0)));
        Assert.Equal(20f * (rows - 1), tree.GetTop(tree.GetChild(root, rows - 1)));
    }

    static LayoutNodeId BuildPanel(LayoutTree tree, int rows, int columnsPerRow) {
        var root = tree.CreateNode();
        tree.SetFlexDirection(root, FlexDirection.Column);
        tree.SetPadding(root, Edge.All, StyleLength.Points(8f));

        for (var r = 0; r < rows; r++) {
            var row = tree.CreateNode();
            tree.SetFlexDirection(row, FlexDirection.Row);
            tree.SetJustifyContent(row, Justify.SpaceBetween);
            tree.SetDimension(row, Dimension.Height, StyleLength.Points(24f));
            tree.SetMargin(row, Edge.Bottom, StyleLength.Points(4f));
            tree.AddChild(root, row);

            for (var c = 0; c < columnsPerRow; c++) {
                var cell = tree.CreateNode();
                tree.SetFlexGrow(cell, c == 0 ? 0f : 1f);
                tree.SetDimension(cell, Dimension.Width, StyleLength.Points(40f));
                tree.AddChild(row, cell);
            }
        }

        return root;
    }
}
