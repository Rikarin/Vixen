// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using CsCheck;
using Xunit;

namespace Vixen.Ui.Layout.Tests;

/// <summary>
///     The inline rules no corpus can see, whatever it grows to.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Doc 43's exit criterion 5, for inline — and inline starts further back than grid
///         does.</b> <see cref="InlineFormattingTests" /> already records that neither corpus
///         contains a single inline fixture: not one of Taffy's 5 524 tests sets
///         <c>display: inline*</c> and neither does any of Yoga's 534. So every inline assertion in
///         this store is hand-written already, and what this file adds is the two questions a
///         fixture file cannot ask <i>however many fixtures it has</i>.
///     </para>
///     <para>
///         ⚠ <b>Every fixture in every corpus is a COLD layout.</b> Nothing there re-lays a tree that
///         changed, so dirty propagation, the measure cache and line-box reuse are asserted by
///         nothing. Measured: breaking <c>MarkDirtyAndPropagate</c> so that it marks the node and
///         stops — never reaching an ancestor — leaves all 2 408 flex fixtures, both grid corpora,
///         block, float and the four mixed corpora green, and takes down eight hand-written tests.
///     </para>
///     <para>
///         ⚠ <b>Every fixture rounds at scale 1 or declares <c>use-rounding="false"</c></b>, so a
///         fractional <see cref="LayoutTree.PointScaleFactor" /> — a retina editor — is untested by
///         them. Inline is where that matters most, because a line box's advance is a running sum:
///         an error in one box's rounded width moves every box after it on the line.
///     </para>
///     <para>
///         The oracle for both is <see cref="PixelRoundingTests" />'s and
///         <see cref="GridBlindSpotTests" />'s: a second tree built from scratch with the same
///         styles and laid out cold, which by construction takes no shortcut.
///     </para>
/// </remarks>
public class InlineBlindSpotTests {
    const float Tolerance = 0.0001f;

    [Fact]
    public void A_line_relaid_out_after_a_change_matches_one_laid_out_cold() {
        // ⚠ The first thing anywhere that asks an inline formatting context to run twice. A change
        // to one box's width re-breaks the lines after it, so a stale line box is visible as a box
        // on the wrong row rather than as a rounding difference.
        Gen.Select(Gen.Int[3, 9], Gen.Int[0, 999].Array[1, 10])
            .Sample(shape => {
                    var (boxes, mutations) = shape;

                    var spec = new RunSpec(boxes);
                    using var incremental = new LayoutTree();
                    var incrementalRoot = spec.Build(incremental);
                    incremental.CalculateLayout(incrementalRoot, 200f, float.NaN, Direction.Ltr);

                    foreach (var mutation in mutations) {
                        spec.Mutate(mutation);
                        spec.Apply(incremental, incrementalRoot, mutation);
                        incremental.CalculateLayout(incrementalRoot, 200f, float.NaN, Direction.Ltr);

                        using var cold = new LayoutTree();
                        var coldRoot = spec.Build(cold);
                        cold.CalculateLayout(coldRoot, 200f, float.NaN, Direction.Ltr);

                        AssertSameLayout(incremental, incrementalRoot, cold, coldRoot);
                    }
                }
            );
    }

    [Fact]
    public void A_line_on_a_fractional_pixel_grid_matches_one_laid_out_cold() {
        // The same oracle at 2× with a fractional container, which is the case no fixture in either
        // corpus is written at.
        Gen.Select(Gen.Int[3, 9], Gen.Int[0, 999].Array[1, 10])
            .Sample(shape => {
                    var (boxes, mutations) = shape;

                    var spec = new RunSpec(boxes);
                    using var incremental = new LayoutTree { PointScaleFactor = 2f };
                    var incrementalRoot = spec.Build(incremental);
                    incremental.CalculateLayout(incrementalRoot, 200.5f, float.NaN, Direction.Ltr);

                    foreach (var mutation in mutations) {
                        spec.Mutate(mutation);
                        spec.Apply(incremental, incrementalRoot, mutation);
                        incremental.CalculateLayout(incrementalRoot, 200.5f, float.NaN, Direction.Ltr);

                        using var cold = new LayoutTree { PointScaleFactor = 2f };
                        var coldRoot = spec.Build(cold);
                        cold.CalculateLayout(coldRoot, 200.5f, float.NaN, Direction.Ltr);

                        AssertSameLayout(incremental, incrementalRoot, cold, coldRoot);
                    }
                }
            );
    }

    [Fact]
    public void Boxes_on_one_line_do_not_round_into_a_seam() {
        // ⚠ A line box's advance is a running sum, which is the one structural difference from the
        // block and grid rounding cases: rounding each box's width independently and then laying
        // them out end to end accumulates, and the last box on a line of five 33.3-point boxes ends
        // up a whole pixel from where the cold pass puts it. Each box must start exactly where its
        // predecessor ended.
        using var tree = new LayoutTree { PointScaleFactor = 2f };
        var root = tree.CreateNode();
        tree.SetDisplay(root, Display.Block);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(200f));

        var boxes = new LayoutNodeId[5];
        for (var i = 0; i < boxes.Length; i++) {
            boxes[i] = tree.CreateNode();
            tree.SetDisplay(boxes[i], Display.InlineBlock);
            tree.SetDimension(boxes[i], Dimension.Width, StyleLength.Points(33.3f));
            tree.SetDimension(boxes[i], Dimension.Height, StyleLength.Points(10f));
            tree.AddChild(root, boxes[i]);
        }

        tree.CalculateLayout(root, 200f, float.NaN, Direction.Ltr);

        for (var i = 1; i < boxes.Length; i++) {
            // Only boxes that stayed on the same line are adjacent; a wrapped one starts the row
            // again, which is a different assertion and not this one's.
            if (tree.GetTop(boxes[i]) != tree.GetTop(boxes[i - 1])) {
                continue;
            }

            var previousRight = tree.GetLeft(boxes[i - 1]) + tree.GetWidth(boxes[i - 1]);
            Assert.Equal(previousRight, tree.GetLeft(boxes[i]), Tolerance);
        }
    }

    /// <summary>A run of fractionally-sized inline-level boxes that wraps.</summary>
    /// <remarks>
    ///     The widths are chosen so the run is wider than the container and the break points move as
    ///     a mutation lands — a spec whose lines never re-break would assert the measure cache and
    ///     not the line breaker.
    /// </remarks>
    sealed class RunSpec(int boxes) {
        readonly float[] widths = CreateSizes(boxes, 41.3f, 7.7f);
        readonly float[] heights = CreateSizes(boxes, 11.4f, 2.3f);

        public LayoutNodeId Build(LayoutTree tree) {
            var root = tree.CreateNode();
            tree.SetDisplay(root, Display.Block);
            tree.SetPadding(root, Edge.All, StyleLength.Points(2.5f));

            for (var i = 0; i < boxes; i++) {
                var box = tree.CreateNode();
                tree.SetDisplay(box, Display.InlineBlock);
                tree.SetDimension(box, Dimension.Width, StyleLength.Points(widths[i]));
                tree.SetDimension(box, Dimension.Height, StyleLength.Points(heights[i]));
                tree.AddChild(root, box);
            }

            return root;
        }

        public void Mutate(int mutation) {
            if (mutation % 2 == 0) {
                widths[mutation % widths.Length] = 23f + (mutation % 29 * 1.31f);
            } else {
                heights[mutation % heights.Length] = 9f + (mutation % 13 * 0.77f);
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
