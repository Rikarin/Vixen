// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Layout.Tests;

/// <summary>
///     CSS 2 §10.3.7 shrink-to-fit for an absolutely positioned box with <c>width: auto</c>, under
///     every kind of parent (#1447).
/// </summary>
/// <remarks>
///     <para>
///         The width is <c>min(max-content, max(min-content, available))</c>, and the available width
///         is the containing block less whichever of <c>left</c> and <c>right</c> is written. Yoga caps
///         the box at its containing block only under a COLUMN parent, and then at the full width; under
///         a row parent or a block one — the root is a row — the box was laid out at max-content and
///         its text never wrapped. #1400's toast ran 1528 px wide in a 400 px window because of it.
///     </para>
///     <para>
///         Closed-form: a 400-point containing block, ten-point words, a sixteen-point inset.
///     </para>
/// </remarks>
public class AbsoluteShrinkToFitTests {
    const float Word = 10f;

    public enum Parent {
        Row,
        Column,
        Block
    }

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

    static (LayoutTree Tree, LayoutNodeId Box) Layout(Parent parent, int words, Edge? inset) {
        var tree = new LayoutTree();

        var root = tree.CreateNode();
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(400f));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(300f));
        switch (parent) {
            case Parent.Row:
                tree.SetFlexDirection(root, FlexDirection.Row);
                break;
            case Parent.Column:
                tree.SetFlexDirection(root, FlexDirection.Column);
                break;
            default:
                tree.SetDisplay(root, Display.Block);
                break;
        }

        var box = tree.CreateNode();
        tree.SetPositionType(box, PositionType.Absolute);
        tree.SetPosition(box, Edge.Top, StyleLength.Points(0f));
        if (inset is { } edge) {
            tree.SetPosition(box, edge, StyleLength.Points(16f));
        }

        tree.AddChild(root, box);

        var text = tree.CreateNode();
        tree.SetMeasureFunction(text, Words(words));
        tree.AddChild(box, text);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        return (tree, box);
    }

    /// <summary>
    ///     ⚠ <b>The defect as filed</b>: a hundred words at <c>left: 16px</c> are 1000 points of one
    ///     line. The box has 384 points before the right edge, so it is 384 wide and the text wraps —
    ///     under a column parent it used to be capped at the full 400 and overhang by 16.
    /// </summary>
    [Theory]
    [InlineData(Parent.Row)]
    [InlineData(Parent.Column)]
    [InlineData(Parent.Block)]
    public void A_box_pinned_left_shrinks_to_the_space_right_of_its_inset(Parent parent) {
        var (tree, box) = Layout(parent, 100, Edge.Left);

        Assert.Equal(384f, tree.GetWidth(box));
        Assert.Equal(16f, tree.GetLeft(box));

        // 384 points hold 38 words a line, so a hundred take three.
        Assert.Equal(30f, tree.GetHeight(box));
    }

    /// <summary>
    ///     Pinned right: the same 384, and the box's left edge at the containing block's start rather
    ///     than off it — #1400's toast was at x −1144.
    /// </summary>
    [Theory]
    [InlineData(Parent.Row)]
    [InlineData(Parent.Column)]
    [InlineData(Parent.Block)]
    public void A_box_pinned_right_shrinks_to_the_space_left_of_its_inset(Parent parent) {
        var (tree, box) = Layout(parent, 100, Edge.Right);

        Assert.Equal(384f, tree.GetWidth(box));
        Assert.Equal(0f, tree.GetLeft(box));
    }

    /// <summary>The <c>min(max-content, …)</c> half: a box whose content fits is its content's width.</summary>
    [Theory]
    [InlineData(Parent.Row)]
    [InlineData(Parent.Column)]
    [InlineData(Parent.Block)]
    public void A_box_whose_content_fits_is_as_wide_as_its_content(Parent parent) {
        var (tree, box) = Layout(parent, 5, Edge.Left);

        Assert.Equal(50f, tree.GetWidth(box));
        Assert.Equal(10f, tree.GetHeight(box));
    }

    /// <summary>
    ///     The <c>max(min-content, …)</c> half: an inset that leaves less room than the longest word
    ///     does not squeeze the box below that word — it overflows the containing block instead.
    /// </summary>
    [Theory]
    [InlineData(Parent.Row)]
    [InlineData(Parent.Column)]
    [InlineData(Parent.Block)]
    public void A_box_is_never_narrower_than_its_longest_word(Parent parent) {
        var tree = new LayoutTree();

        var root = tree.CreateNode();
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(400f));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(300f));
        tree.SetFlexDirection(root, parent == Parent.Column ? FlexDirection.Column : FlexDirection.Row);
        if (parent == Parent.Block) {
            tree.SetDisplay(root, Display.Block);
        }

        var box = tree.CreateNode();
        tree.SetPositionType(box, PositionType.Absolute);
        tree.SetPosition(box, Edge.Left, StyleLength.Points(395f));
        tree.AddChild(root, box);

        var text = tree.CreateNode();
        tree.SetMeasureFunction(text, Words(3));
        tree.AddChild(box, text);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(10f, tree.GetWidth(box));
        Assert.Equal(30f, tree.GetHeight(box));
    }
}
