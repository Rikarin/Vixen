// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Layout.Tests;

/// <summary>
///     A percentage <c>min-width</c> inside a content-sized box is a fraction of that box's final size,
///     not of the space the box was offered (#1470).
/// </summary>
public class CyclicPercentageMinSizeTests {
    const float Word = 10f;

    static MeasureFunction Words(int words) =>
        (in MeasureRequest request) => {
            var width = request.WidthMode switch {
                MeasureMode.Exactly => request.AvailableWidth,
                MeasureMode.AtMost => MathF.Max(Word, MathF.Min(request.AvailableWidth, words * Word)),
                _ => words * Word
            };

            var perLine = MathF.Max(1f, MathF.Floor(width / Word));
            var lines = MathF.Ceiling(words / perLine);

            return new LayoutSize(width, request.HeightMode == MeasureMode.Exactly ? request.AvailableHeight : lines * 10f);
        };

    static (LayoutTree Tree, LayoutNodeId Outer, LayoutNodeId Inner, LayoutNodeId[] Texts) Build(bool withInner, bool widthNotMin = false, bool outerMinimum = true) {
        var tree = new LayoutTree();

        var root = tree.CreateNode();
        tree.SetFlexDirection(root, FlexDirection.Column);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(400f));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(1000f));

        var outer = tree.CreateNode();
        tree.SetFlexDirection(outer, FlexDirection.Column);
        tree.SetAlignSelf(outer, Align.FlexStart);
        if (outerMinimum) {
            tree.SetMinDimension(outer, Dimension.Width, StyleLength.Points(300f));
        }

        tree.AddChild(root, outer);

        var parent = outer;
        var inner = LayoutNodeId.Invalid;
        if (withInner) {
            inner = tree.CreateNode();
            tree.SetFlexDirection(inner, FlexDirection.Column);
            tree.SetAlignSelf(inner, Align.FlexStart);
            if (widthNotMin) {
                tree.SetDimension(inner, Dimension.Width, StyleLength.Percent(100f));
            } else {
                tree.SetMinDimension(inner, Dimension.Width, StyleLength.Percent(100f));
            }
            tree.AddChild(outer, inner);
            parent = inner;
        }

        var texts = new LayoutNodeId[8];
        for (var i = 0; i < texts.Length; i++) {
            texts[i] = tree.CreateNode();
            tree.SetMeasureFunction(texts[i], Words(5));
            tree.AddChild(parent, texts[i]);
        }

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);
        return (tree, outer, inner, texts);
    }

    /// <summary>
    ///     ⚠ <b>The defect as filed</b>: <c>outer</c> came out 400 — the offer — because
    ///     <c>inner</c>'s <c>min-width: 100%</c> was resolved against it while <c>outer</c> was still
    ///     being sized from its content. Under §5.2.1 the percentage is zero for <c>inner</c>'s
    ///     contribution, so <c>outer</c> is max(300, 50) = 300, and then <c>inner</c> is 100% of 300.
    /// </summary>
    [Fact]
    public void A_percentage_minimum_inside_a_content_sized_column_resolves_against_its_final_width() {
        var (tree, outer, inner, texts) = Build(withInner: true);

        Assert.Equal(300f, tree.GetWidth(outer));
        Assert.Equal(300f, tree.GetWidth(inner));
        Assert.Equal(300f, tree.GetWidth(texts[0]));
    }

    /// <summary>
    ///     ⚠ <b>Two of them, nested, and the measurement cache is what this one is about.</b> The
    ///     inner-most column is asked "at most 300" twice — once while its parent sizes itself, with its
    ///     percentage withheld, and once after, with it given — and the cache is keyed on the offer, so
    ///     without the withheld bit in the key the second question is answered with the first's 50.
    /// </summary>
    [Fact]
    public void Nested_percentage_minimums_each_resolve_against_their_parents_final_width() {
        var tree = new LayoutTree();

        var root = tree.CreateNode();
        tree.SetFlexDirection(root, FlexDirection.Column);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(400f));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(1000f));

        var outer = tree.CreateNode();
        tree.SetFlexDirection(outer, FlexDirection.Column);
        tree.SetAlignSelf(outer, Align.FlexStart);
        tree.SetMinDimension(outer, Dimension.Width, StyleLength.Points(300f));
        tree.AddChild(root, outer);

        var parent = outer;
        var columns = new LayoutNodeId[3];
        for (var i = 0; i < columns.Length; i++) {
            columns[i] = tree.CreateNode();
            tree.SetFlexDirection(columns[i], FlexDirection.Column);
            tree.SetAlignSelf(columns[i], Align.FlexStart);
            tree.SetMinDimension(columns[i], Dimension.Width, StyleLength.Percent(100f));
            tree.AddChild(parent, columns[i]);
            parent = columns[i];
        }

        var text = tree.CreateNode();
        tree.SetMeasureFunction(text, Words(5));
        tree.AddChild(parent, text);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(300f, tree.GetWidth(outer));
        foreach (var column in columns) {
            Assert.Equal(300f, tree.GetWidth(column));
        }
    }

    /// <summary>
    ///     The second half of §5.2.1 where no minimum widened the column, so only the re-entry for the
    ///     cyclic item lays it out again: a 50-point text beside a <c>min-width: 100%</c> box holding
    ///     20 points of text. The column is its content's 50, and the box is then 100% of 50 — not its
    ///     own 20, which is what it was measured at with the percentage withheld.
    /// </summary>
    [Fact]
    public void A_percentage_minimum_resolves_where_nothing_widened_the_column() {
        var tree = new LayoutTree();

        var root = tree.CreateNode();
        tree.SetFlexDirection(root, FlexDirection.Column);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(400f));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(1000f));

        var column = tree.CreateNode();
        tree.SetFlexDirection(column, FlexDirection.Column);
        tree.SetAlignSelf(column, Align.FlexStart);
        tree.SetAlignItems(column, Align.FlexStart);
        tree.AddChild(root, column);

        var wide = tree.CreateNode();
        tree.SetMeasureFunction(wide, Words(5));
        tree.AddChild(column, wide);

        var box = tree.CreateNode();
        tree.SetFlexDirection(box, FlexDirection.Column);
        tree.SetMinDimension(box, Dimension.Width, StyleLength.Percent(100f));
        tree.AddChild(column, box);

        var narrow = tree.CreateNode();
        tree.SetMeasureFunction(narrow, Words(2));
        tree.AddChild(box, narrow);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(50f, tree.GetWidth(column));
        Assert.Equal(50f, tree.GetWidth(box));
    }

    /// <summary>The control: the same column with its texts directly inside it is 300.</summary>
    [Fact]
    public void Without_the_inner_column_the_outer_one_is_its_minimum() {
        var (tree, outer, _, _) = Build(withInner: false);

        Assert.Equal(300f, tree.GetWidth(outer));
    }

    /// <summary>
    ///     <c>width: 100%</c> already took this path — a percentage preferred size against a
    ///     content-sized container is measured at max-content — and the minimum now agrees with it.
    /// </summary>
    [Fact]
    public void A_percentage_width_and_a_percentage_minimum_agree() {
        var (tree, outer, inner, _) = Build(withInner: true, widthNotMin: true);

        Assert.Equal(300f, tree.GetWidth(outer));
        Assert.Equal(300f, tree.GetWidth(inner));
    }

    /// <summary>
    ///     With no minimum on <c>outer</c> it is its content's 50, and <c>inner</c> is 100% of that —
    ///     not the offer's 400, which is what it and <c>outer</c> both were.
    /// </summary>
    [Fact]
    public void A_content_sized_column_with_no_minimum_is_its_content_and_the_percentage_follows_it() {
        var (tree, outer, inner, _) = Build(withInner: true, outerMinimum: false);

        Assert.Equal(50f, tree.GetWidth(outer));
        Assert.Equal(50f, tree.GetWidth(inner));
    }
}
