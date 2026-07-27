// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using BenchmarkDotNet.Attributes;
using Vixen.Ui.Layout;

namespace Vixen.Benchmarks.Ui;

/// <summary>
///     What a layout pass costs at the scale doc 09 names, and — the part that matters more — what
///     it costs when almost nothing changed.
/// </summary>
/// <remarks>
///     A UI framework is not judged on how fast it lays out a tree from cold. It is judged on the
///     frame after that, when one row's height changed and everything else is where it was. The
///     ratio between <see cref="ColdLayout" /> and <see cref="OneLeafChanged" /> is the number that
///     decides whether a panel of ten thousand elements can be animated.
/// </remarks>
[MemoryDiagnoser]
public class LayoutBenchmarks {
    LayoutTree tree = null!;
    LayoutNodeId root;
    LayoutNodeId movingLeaf;
    int frame;

    /// <summary>How many rows the tree has. Each row holds ten cells.</summary>
    [Params(100, 1_000, 10_000)]
    public int Rows { get; set; }

    [GlobalSetup]
    public void Setup() {
        tree = new LayoutTree();
        root = tree.CreateNode();
        tree.SetFlexDirection(root, FlexDirection.Column);
        tree.SetPadding(root, Edge.All, StyleLength.Points(8f));

        for (var r = 0; r < Rows; r++) {
            var row = tree.CreateNode();
            tree.SetFlexDirection(row, FlexDirection.Row);
            tree.SetJustifyContent(row, Justify.SpaceBetween);
            tree.SetDimension(row, Dimension.Height, StyleLength.Points(24f));
            tree.SetMargin(row, Edge.Bottom, StyleLength.Points(2f));
            tree.AddChild(root, row);

            for (var c = 0; c < 10; c++) {
                var cell = tree.CreateNode();
                tree.SetFlexGrow(cell, c == 0 ? 0f : 1f);
                tree.SetDimension(cell, Dimension.Width, StyleLength.Points(40f));
                tree.SetMargin(cell, Edge.Right, StyleLength.Points(4f));
                tree.AddChild(row, cell);
            }
        }

        movingLeaf = tree.GetChild(tree.GetChild(root, Rows / 2), 3);
        tree.CalculateLayout(root, 1200f, 800f, Direction.Ltr);
    }

    [GlobalCleanup]
    public void Cleanup() => tree.Dispose();

    /// <summary>Everything dirty: the cost of the first frame, or of a full restyle.</summary>
    [Benchmark]
    public float ColdLayout() {
        for (var r = 0; r < Rows; r++) {
            tree.SetDimension(tree.GetChild(root, r), Dimension.Height, StyleLength.Points(24f + (frame % 2)));
        }

        frame++;
        tree.CalculateLayout(root, 1200f, 800f, Direction.Ltr);
        return tree.GetHeight(root);
    }

    /// <summary>One leaf changed: the cost of a frame in a running application.</summary>
    [Benchmark]
    public float OneLeafChanged() {
        tree.SetDimension(movingLeaf, Dimension.Width, StyleLength.Points(40f + (frame++ % 8)));
        tree.CalculateLayout(root, 1200f, 800f, Direction.Ltr);
        return tree.GetHeight(root);
    }

    /// <summary>Nothing changed: what a static panel costs to leave on the screen.</summary>
    [Benchmark]
    public float NothingChanged() {
        tree.CalculateLayout(root, 1200f, 800f, Direction.Ltr);
        return tree.GetHeight(root);
    }

    /// <summary>
    ///     The same single-leaf change with pixel rounding turned off — a diagnostic rather than a
    ///     configuration anybody should ship.
    /// </summary>
    /// <remarks>
    ///     The difference against <see cref="OneLeafChanged" /> is the price of the rounding pass,
    ///     which walks the whole tree every frame regardless of what changed, because a node's
    ///     rounded edges depend on its absolute offset and an ancestor moving changes that. It is
    ///     here to keep that number honest and visible rather than buried in the total.
    /// </remarks>
    [Benchmark]
    public float OneLeafChangedWithoutRounding() {
        tree.PointScaleFactor = 0f;
        try {
            tree.SetDimension(movingLeaf, Dimension.Width, StyleLength.Points(40f + (frame++ % 8)));
            tree.CalculateLayout(root, 1200f, 800f, Direction.Ltr);
            return tree.GetHeight(root);
        } finally {
            tree.PointScaleFactor = 1f;
        }
    }
}
