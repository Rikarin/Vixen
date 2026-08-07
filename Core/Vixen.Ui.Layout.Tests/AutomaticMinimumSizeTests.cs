// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Layout.Tests;

/// <summary>
///     CSS Flexbox §4.5 — the automatic minimum main size a flex item has even when nothing
///     declares one.
/// </summary>
/// <remarks>
///     These are hand-written because the ported Yoga suite does not reach this code: sabotaging the
///     floor leaves all 534 fixtures green. That is a fact about the fixtures rather than about the
///     rule — Yoga's generator emits no fixture that shrinks a measured leaf past its content — and
///     an implementation of a specification section with no test over it is the thing doc 14 warns
///     about. So the rule gets a test that fails without it.
/// </remarks>
public class AutomaticMinimumSizeTests {
    const float Tolerance = 0.0001f;

    [Fact]
    public void A_shrinking_item_does_not_go_below_the_size_its_content_needs() {
        // 300 points of text in a 100-point row. Without §4.5 the item shrinks to 100 and the text
        // is clipped; with it the item stops at its min-content size and the row overflows instead,
        // which is what a browser does.
        using var tree = new LayoutTree();
        var root = tree.CreateNode();
        tree.SetFlexDirection(root, FlexDirection.Row);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(100f));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(50f));

        var child = tree.CreateNode();
        tree.SetFlexShrink(child, 1f);
        tree.SetFlexBasis(child, StyleLength.Points(300f));
        tree.SetContext(child, 300f);
        tree.SetMeasureFunction(child, MeasureFixedContent);
        tree.AddChild(root, child);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(300f, tree.GetWidth(child), Tolerance);
    }

    [Fact]
    public void An_explicit_minimum_of_zero_opts_out_of_the_automatic_one() {
        // The specification's own escape hatch: writing any min-width, including 0, means the
        // author has said what the minimum is and the automatic one does not apply.
        using var tree = new LayoutTree();
        var root = tree.CreateNode();
        tree.SetFlexDirection(root, FlexDirection.Row);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(100f));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(50f));

        var child = tree.CreateNode();
        tree.SetFlexShrink(child, 1f);
        tree.SetFlexBasis(child, StyleLength.Points(300f));
        tree.SetMinDimension(child, Dimension.Width, StyleLength.Points(0f));
        tree.SetContext(child, 300f);
        tree.SetMeasureFunction(child, MeasureFixedContent);
        tree.AddChild(root, child);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(100f, tree.GetWidth(child), Tolerance);
    }

    [Fact]
    public void An_item_that_clips_its_own_overflow_opts_out_too() {
        // §4.5 again: an item that handles overflow itself does not need a content-based floor.
        using var tree = new LayoutTree();
        var root = tree.CreateNode();
        tree.SetFlexDirection(root, FlexDirection.Row);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(100f));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(50f));

        var child = tree.CreateNode();
        tree.SetFlexShrink(child, 1f);
        tree.SetFlexBasis(child, StyleLength.Points(300f));
        tree.SetOverflow(child, Overflow.Hidden);
        tree.SetContext(child, 300f);
        tree.SetMeasureFunction(child, MeasureFixedContent);
        tree.AddChild(root, child);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(100f, tree.GetWidth(child), Tolerance);
    }

    [Theory]
    [InlineData(Overflow.Hidden, Overflow.Visible, 100f)]
    [InlineData(Overflow.Visible, Overflow.Hidden, 300f)]
    [InlineData(Overflow.Visible, Overflow.Scroll, 300f)]
    public void The_opt_out_is_the_main_axis_s_own_overflow(Overflow horizontal, Overflow vertical, float expected) {
        // ⚠ §4.5's escape hatch is per axis in the specification — "overflow other than visible in
        // the main axis" — and the container here is a row, so only `overflow-x` can open it. An item
        // that clips what hangs *below* it has said nothing about being squeezed sideways, and a
        // reading that collapsed the two would silently drop the floor from every panel in the editor
        // that scrolls vertically.
        using var tree = new LayoutTree();
        var root = tree.CreateNode();
        tree.SetFlexDirection(root, FlexDirection.Row);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(100f));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(50f));

        var child = tree.CreateNode();
        tree.SetFlexShrink(child, 1f);
        tree.SetFlexBasis(child, StyleLength.Points(300f));
        tree.SetOverflow(child, horizontal, vertical);
        tree.SetContext(child, 300f);
        tree.SetMeasureFunction(child, MeasureFixedContent);
        tree.AddChild(root, child);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(expected, tree.GetWidth(child), Tolerance);
    }

    [Fact]
    public void The_floor_never_exceeds_what_the_item_asked_for() {
        // The floor is min(content, specified): an item with a 150-point width does not get a
        // 300-point floor just because its content is that wide.
        using var tree = new LayoutTree();
        var root = tree.CreateNode();
        tree.SetFlexDirection(root, FlexDirection.Row);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(100f));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(50f));

        var child = tree.CreateNode();
        tree.SetFlexShrink(child, 1f);
        tree.SetDimension(child, Dimension.Width, StyleLength.Points(150f));
        tree.SetContext(child, 300f);
        tree.SetMeasureFunction(child, MeasureFixedContent);
        tree.AddChild(root, child);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(150f, tree.GetWidth(child), Tolerance);
    }

    /// <summary>A leaf whose content is a fixed width however little room it is offered.</summary>
    /// <remarks>
    ///     Standing in for a single unbreakable word, which is the case §4.5 exists for. It answers
    ///     the same width under every measure mode, so what the tests observe is the floor rather
    ///     than the measurer being clever.
    /// </remarks>
    static LayoutSize MeasureFixedContent(in MeasureRequest request) =>
        new((float) (request.Context ?? 0f), 20f);
}
