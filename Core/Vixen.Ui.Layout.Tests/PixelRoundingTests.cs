// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using CsCheck;
using Xunit;

namespace Vixen.Ui.Layout.Tests;

/// <summary>
///     Pixel-grid rounding, and the fact that skipping it for unchanged subtrees does not change
///     what comes out.
/// </summary>
/// <remarks>
///     The rounding pass stops descending into a subtree whose algorithm did not run and whose
///     absolute offset has not moved. That is an argument, and an argument is not evidence: the
///     oracle here is a second tree built from scratch with the same styles and laid out cold, which
///     by construction rounds everything. If the two ever disagree the shortcut is wrong.
/// </remarks>
public class PixelRoundingTests {
    [Fact]
    public void An_incrementally_rounded_tree_matches_one_rounded_from_cold() {
        // Fractional sizes at a 2× scale, which is where rounding actually does something: a
        // 33.3-point row on a retina display lands between pixels and its neighbours have to agree
        // about where the seam is.
        Gen.Select(Gen.Int[2, 5], Gen.Int[1, 4], Gen.Int[0, 999].Array[1, 12]).Sample(shape => {
                var (rows, cells, mutations) = shape;

                var spec = new PanelSpec(rows, cells);
                using var incremental = new LayoutTree { PointScaleFactor = 2f };
                var incrementalRoot = spec.Build(incremental);
                incremental.CalculateLayout(incrementalRoot, 320.5f, 240.25f, Direction.Ltr);

                foreach (var mutation in mutations) {
                    spec.Mutate(mutation);
                    spec.Apply(incremental, incrementalRoot, mutation);
                    incremental.CalculateLayout(incrementalRoot, 320.5f, 240.25f, Direction.Ltr);

                    using var cold = new LayoutTree { PointScaleFactor = 2f };
                    var coldRoot = spec.Build(cold);
                    cold.CalculateLayout(coldRoot, 320.5f, 240.25f, Direction.Ltr);

                    AssertSameLayout(incremental, incrementalRoot, cold, coldRoot, "root");
                }
            }
        );
    }

    [Fact]
    public void A_subtree_that_only_moves_is_still_rounded_against_its_new_offset() {
        // The case the shortcut is most likely to get wrong: nothing inside the subtree changed, so
        // its algorithm did not run — but an ancestor grew by half a pixel, and every descendant's
        // rounded edge depends on where it now sits.
        using var tree = new LayoutTree { PointScaleFactor = 2f };
        var root = tree.CreateNode();
        tree.SetFlexDirection(root, FlexDirection.Column);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(200f));

        var spacer = tree.CreateNode();
        tree.SetDimension(spacer, Dimension.Height, StyleLength.Points(10f));
        tree.AddChild(root, spacer);

        var group = tree.CreateNode();
        tree.SetFlexDirection(group, FlexDirection.Column);
        tree.AddChild(root, group);

        var inner = tree.CreateNode();
        tree.SetDimension(inner, Dimension.Height, StyleLength.Points(10.25f));
        tree.AddChild(group, inner);

        tree.CalculateLayout(root, 200f, 400f, Direction.Ltr);

        // Its *height*, not its top: positions are rounded relative to the parent, so `inner` sits
        // at 0 either way. What moving half a pixel changes is where its far edge lands on the grid,
        // and therefore the distance between its two rounded edges.
        var before = tree.GetHeight(inner);

        // Half a pixel at 2× scale: enough to move where the seam falls, and nothing inside `group`
        // is touched by it.
        tree.SetDimension(spacer, Dimension.Height, StyleLength.Points(10.25f));
        tree.CalculateLayout(root, 200f, 400f, Direction.Ltr);

        using var cold = new LayoutTree { PointScaleFactor = 2f };
        var coldRoot = cold.CreateNode();
        cold.SetFlexDirection(coldRoot, FlexDirection.Column);
        cold.SetDimension(coldRoot, Dimension.Width, StyleLength.Points(200f));
        var coldSpacer = cold.CreateNode();
        cold.SetDimension(coldSpacer, Dimension.Height, StyleLength.Points(10.25f));
        cold.AddChild(coldRoot, coldSpacer);
        var coldGroup = cold.CreateNode();
        cold.SetFlexDirection(coldGroup, FlexDirection.Column);
        cold.AddChild(coldRoot, coldGroup);
        var coldInner = cold.CreateNode();
        cold.SetDimension(coldInner, Dimension.Height, StyleLength.Points(10.25f));
        cold.AddChild(coldGroup, coldInner);
        cold.CalculateLayout(coldRoot, 200f, 400f, Direction.Ltr);

        Assert.Equal(cold.GetTop(coldInner), tree.GetTop(inner));
        Assert.Equal(cold.GetHeight(coldInner), tree.GetHeight(inner));
        Assert.NotEqual(before, tree.GetHeight(inner));
    }

    [Fact]
    public void Adjacent_boxes_do_not_round_into_a_seam() {
        // What the whole pass exists for. Three stacked 33.3-point rows at 2× scale: their rounded
        // heights differ from each other, and they still tile the space exactly.
        using var tree = new LayoutTree { PointScaleFactor = 2f };
        var root = tree.CreateNode();
        tree.SetFlexDirection(root, FlexDirection.Column);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(100f));

        var boxes = new LayoutNodeId[3];
        for (var i = 0; i < boxes.Length; i++) {
            boxes[i] = tree.CreateNode();
            tree.SetDimension(boxes[i], Dimension.Height, StyleLength.Points(33.3f));
            tree.AddChild(root, boxes[i]);
        }

        tree.CalculateLayout(root, 100f, 200f, Direction.Ltr);

        for (var i = 1; i < boxes.Length; i++) {
            var previousBottom = tree.GetTop(boxes[i - 1]) + tree.GetHeight(boxes[i - 1]);
            Assert.Equal(previousBottom, tree.GetTop(boxes[i]));
        }
    }

    /// <summary>A panel of rows of cells, and the current fractional sizes of each.</summary>
    sealed class PanelSpec(int rows, int cells) {
        readonly float[] cellWidths = CreateSizes(rows * cells, 17.3f, 4.7f);
        readonly float[] rowHeights = CreateSizes(rows, 21.4f, 3.3f);

        public LayoutNodeId Build(LayoutTree tree) {
            var root = tree.CreateNode();
            tree.SetFlexDirection(root, FlexDirection.Column);
            tree.SetPadding(root, Edge.All, StyleLength.Points(3.5f));

            for (var r = 0; r < rows; r++) {
                var row = tree.CreateNode();
                tree.SetFlexDirection(row, FlexDirection.Row);
                tree.SetDimension(row, Dimension.Height, StyleLength.Points(rowHeights[r]));
                tree.AddChild(root, row);

                for (var c = 0; c < cells; c++) {
                    var cell = tree.CreateNode();
                    tree.SetDimension(cell, Dimension.Width, StyleLength.Points(cellWidths[(r * cells) + c]));
                    tree.SetMargin(cell, Edge.Right, StyleLength.Points(1.25f));
                    tree.AddChild(row, cell);
                }
            }

            return root;
        }

        public void Mutate(int mutation) {
            if (mutation % 2 == 0) {
                cellWidths[mutation % cellWidths.Length] = 11f + (mutation % 23 * 0.37f);
            } else {
                rowHeights[mutation % rowHeights.Length] = 13f + (mutation % 17 * 0.41f);
            }
        }

        public void Apply(LayoutTree tree, LayoutNodeId root, int mutation) {
            if (mutation % 2 == 0) {
                var index = mutation % cellWidths.Length;
                var cell = tree.GetChild(tree.GetChild(root, index / cells), index % cells);
                tree.SetDimension(cell, Dimension.Width, StyleLength.Points(cellWidths[index]));
                return;
            }

            var rowIndex = mutation % rowHeights.Length;
            tree.SetDimension(tree.GetChild(root, rowIndex), Dimension.Height, StyleLength.Points(rowHeights[rowIndex]));
        }

        static float[] CreateSizes(int count, float start, float step) {
            var sizes = new float[count];
            for (var i = 0; i < count; i++) {
                sizes[i] = start + (i * step % 19f);
            }

            return sizes;
        }
    }

    static void AssertSameLayout(LayoutTree left, LayoutNodeId leftNode, LayoutTree right, LayoutNodeId rightNode, string path) {
        Assert.Equal(right.GetLeft(rightNode), left.GetLeft(leftNode));
        Assert.Equal(right.GetTop(rightNode), left.GetTop(leftNode));
        Assert.Equal(right.GetWidth(rightNode), left.GetWidth(leftNode));
        Assert.Equal(right.GetHeight(rightNode), left.GetHeight(leftNode));
        Assert.Equal(right.GetChildCount(rightNode), left.GetChildCount(leftNode));

        for (var i = 0; i < left.GetChildCount(leftNode); i++) {
            AssertSameLayout(left, left.GetChild(leftNode, i), right, right.GetChild(rightNode, i), $"{path}/{i}");
        }
    }
}
