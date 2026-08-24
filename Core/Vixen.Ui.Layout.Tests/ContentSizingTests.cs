// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Layout.Tests;

/// <summary>
///     CSS Sizing § 5's content keywords, on the six slots a size can be written in.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every assertion here is on a resolved box and not on a style, because the defect
///         these close was invisible to a style-shaped test.</b> <c>width: min-content</c> reached
///         <c>LayoutStyle.Dimensions</c> as a perfectly good <see cref="LayoutUnit" />, cascaded,
///         round-tripped, and then meant nothing at all: <see cref="StyleLength.Resolve" /> answers
///         NaN for a keyword, so the algorithm read the declaration as its own absence. A test that
///         asserted the keyword had arrived would have passed the whole time.
///     </para>
///     <para>
///         The measure function below is deliberately a wrapper rather than a fixed size: a
///         min-content answer and a max-content one have to be <i>different numbers</i> or half of
///         these cases cannot fail. Three ten-point words make 30 across on one line, 10 across on
///         three, and a height that follows the width.
///     </para>
///     <para>
///         The ported corpora reach none of this — the Taffy fixtures carry content keywords only on
///         <c>&lt;viewport&gt;</c>, where they mean an indefinite available size, and in
///         <c>grid-template-*</c> track lists, which the track sizer reads without going near
///         <see cref="StyleLength" />. So these are hand-written for the reason
///         <c>AutomaticMinimumSizeTests</c> is.
///     </para>
/// </remarks>
public class ContentSizingTests {
    const float Tolerance = 0.0001f;

    /// <summary>Three words of ten points each, wrapped to whatever room they are offered.</summary>
    /// <remarks>
    ///     Max-content is 30 across and 10 down; min-content is 10 across and 30 down. A single
    ///     number for both would make <c>min-content</c> and <c>max-content</c> indistinguishable,
    ///     which is exactly the failure mode being tested for.
    /// </remarks>
    static LayoutSize MeasureThreeWords(in MeasureRequest request) {
        const float word = 10f;
        const float lineHeight = 10f;
        const int words = 3;

        var width = request.WidthMode switch {
            MeasureMode.Exactly => request.AvailableWidth,
            MeasureMode.AtMost => MathF.Max(word, MathF.Min(request.AvailableWidth, words * word)),
            _ => words * word
        };

        var perLine = MathF.Max(1f, MathF.Floor(width / word));
        var lines = MathF.Ceiling(words / perLine);

        return new LayoutSize(width, request.HeightMode == MeasureMode.Exactly ? request.AvailableHeight : lines * lineHeight);
    }

    static (LayoutTree Tree, LayoutNodeId Root, LayoutNodeId Box, LayoutNodeId Text) Fixture(float rootWidth) {
        var tree = new LayoutTree();

        var root = tree.CreateNode();
        tree.SetDisplay(root, Display.Block);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(rootWidth));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(200f));

        var box = tree.CreateNode();
        tree.SetDisplay(box, Display.Block);
        tree.AddChild(root, box);

        var text = tree.CreateNode();
        tree.SetMeasureFunction(text, MeasureThreeWords);
        tree.AddChild(box, text);

        return (tree, root, box, text);
    }

    [Theory]
    [InlineData(LayoutUnit.MinContent, 10f)]
    [InlineData(LayoutUnit.MaxContent, 30f)]
    public void A_content_keyword_on_width_sizes_the_box_to_its_content(LayoutUnit keyword, float expected) {
        // ⚠ The container is 500 wide and the box is `display: block`, so CSS 2.1 §10.3.3 would make
        // an `auto` width fill it. Both answers below are therefore a long way from what the
        // declaration used to do, which was nothing: before this, both of these were 500.
        var (tree, root, box, _) = Fixture(500f);
        using var owner = tree;

        tree.SetDimension(box, Dimension.Width, StyleLength.Keyword(keyword));
        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(expected, tree.GetWidth(box), Tolerance);
    }

    [Theory]

    // Room to spare: fit-content is the max-content size.
    [InlineData(500f, 30f)]

    // Squeezed: fit-content is the room on offer.
    [InlineData(20f, 20f)]

    // Squeezed past the content's own floor: fit-content stops at min-content and overflows.
    [InlineData(5f, 10f)]
    public void Fit_content_is_max_content_clamped_to_the_space_on_offer(float rootWidth, float expected) {
        // CSS Sizing § 5.1 in three numbers: `min(max-content, max(min-content, stretch))`. The
        // third case is the one that separates fit-content from a plain clamp — a box asked to be
        // narrower than its longest word overflows rather than shrinking.
        var (tree, root, box, _) = Fixture(rootWidth);
        using var owner = tree;

        tree.SetDimension(box, Dimension.Width, StyleLength.Keyword(LayoutUnit.FitContent));
        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(expected, tree.GetWidth(box), Tolerance);
    }

    [Fact]
    public void A_content_keyword_on_max_width_caps_a_box_that_would_otherwise_fill() {
        var (tree, root, box, _) = Fixture(500f);
        using var owner = tree;

        tree.SetMaxDimension(box, Dimension.Width, StyleLength.Keyword(LayoutUnit.MinContent));
        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(10f, tree.GetWidth(box), Tolerance);
    }

    [Fact]
    public void A_content_keyword_on_min_width_floors_a_box_that_would_otherwise_be_crushed() {
        // ⚠ `min-width: max-content` is the declaration an author reaches for to stop a flex item
        // shrinking into its own text, and it is the one this had to reach as well as the preferred
        // size — eight of the thirteen Sizing roots are a `min-*` or a `max-*`.
        using var tree = new LayoutTree();

        var root = tree.CreateNode();
        tree.SetFlexDirection(root, FlexDirection.Row);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(20f));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(100f));

        var box = tree.CreateNode();
        tree.SetFlexShrink(box, 1f);
        tree.SetFlexBasis(box, StyleLength.Points(0f));
        tree.SetMinDimension(box, Dimension.Width, StyleLength.Keyword(LayoutUnit.MaxContent));
        tree.AddChild(root, box);

        var text = tree.CreateNode();
        tree.SetMeasureFunction(text, MeasureThreeWords);
        tree.AddChild(box, text);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(30f, tree.GetWidth(box), Tolerance);
    }

    [Theory]
    [InlineData(LayoutUnit.MinContent)]
    [InlineData(LayoutUnit.MaxContent)]
    [InlineData(LayoutUnit.FitContent)]
    public void The_three_keywords_agree_on_the_block_axis(LayoutUnit keyword) {
        // ⚠ CSS Sizing § 5.1: there is no narrowest height. A box 30 points wide holds all three
        // words on one line, so all three keywords answer ten.
        //
        // ⚠ The container is a ROW and not the block fixture above, which is what gives the
        // assertion something to fail against: an `auto` height on a flex item is stretched to the
        // line's cross size by §9.4, so the box is 100 tall unless the keyword lands.
        using var tree = new LayoutTree();

        var root = tree.CreateNode();
        tree.SetFlexDirection(root, FlexDirection.Row);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(500f));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(100f));

        var box = tree.CreateNode();
        tree.SetDimension(box, Dimension.Width, StyleLength.Points(30f));
        tree.SetDimension(box, Dimension.Height, StyleLength.Keyword(keyword));
        tree.AddChild(root, box);

        var text = tree.CreateNode();
        tree.SetMeasureFunction(text, MeasureThreeWords);
        tree.AddChild(box, text);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(10f, tree.GetHeight(box), Tolerance);
    }

    [Fact]
    public void The_block_axis_is_measured_at_the_width_the_inline_axis_settled_on() {
        // ⚠ The order in `ResolveContentBasedLengths` is what this pins. `width: min-content` makes
        // the box ten across, which puts one word on each of three lines and makes the max-content
        // height thirty. Resolving the height first — or against the container's 500 — answers ten,
        // and the box then clips two thirds of its own text.
        var (tree, root, box, _) = Fixture(500f);
        using var owner = tree;

        tree.SetDimension(box, Dimension.Width, StyleLength.Keyword(LayoutUnit.MinContent));
        tree.SetDimension(box, Dimension.Height, StyleLength.Keyword(LayoutUnit.MaxContent));
        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(10f, tree.GetWidth(box), Tolerance);
        Assert.Equal(30f, tree.GetHeight(box), Tolerance);
    }

    [Fact]
    public void A_min_height_keyword_floors_a_box_shorter_than_its_content() {
        var (tree, root, box, _) = Fixture(500f);
        using var owner = tree;

        tree.SetDimension(box, Dimension.Width, StyleLength.Points(10f));
        tree.SetDimension(box, Dimension.Height, StyleLength.Points(5f));
        tree.SetMinDimension(box, Dimension.Height, StyleLength.Keyword(LayoutUnit.MaxContent));
        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(30f, tree.GetHeight(box), Tolerance);
    }

    [Fact]
    public void A_max_height_keyword_caps_a_box_taller_than_its_content() {
        var (tree, root, box, _) = Fixture(500f);
        using var owner = tree;

        tree.SetDimension(box, Dimension.Width, StyleLength.Points(30f));
        tree.SetDimension(box, Dimension.Height, StyleLength.Points(120f));
        tree.SetMaxDimension(box, Dimension.Height, StyleLength.Keyword(LayoutUnit.MaxContent));
        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(10f, tree.GetHeight(box), Tolerance);
    }

    [Fact]
    public void The_padding_of_a_content_box_node_is_counted_once() {
        // ⚠ A probe answers with a BORDER box and the slot it is written into is read back through
        // `WithBoxSizing`, which adds the padding again for a `content-box` node. Writing the border
        // box straight in makes the box 30 + 5 + 5 + 5 + 5 = 50 across, which reads as a layout
        // quirk rather than as the arithmetic error it is.
        var (tree, root, box, _) = Fixture(500f);
        using var owner = tree;

        tree.SetBoxSizing(box, BoxSizing.ContentBox);
        tree.SetPadding(box, Edge.Left, StyleLength.Points(5f));
        tree.SetPadding(box, Edge.Right, StyleLength.Points(5f));
        tree.SetDimension(box, Dimension.Width, StyleLength.Keyword(LayoutUnit.MaxContent));
        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(40f, tree.GetWidth(box), Tolerance);
    }

    [Fact]
    public void The_written_style_survives_the_pass_that_measured_it() {
        // ⚠ The keyword is resolved by rewriting the node's own style with the number it measured,
        // so the pass has to put it back. A caller that read `GetStyle` afterwards and saw
        // `30 points` would be reading a measurement as a declaration — and the next pass, over a
        // wider container, would then honour a width nobody wrote.
        var (tree, root, box, _) = Fixture(500f);
        using var owner = tree;

        tree.SetDimension(box, Dimension.Width, StyleLength.Keyword(LayoutUnit.MaxContent));
        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(LayoutUnit.MaxContent, tree.GetStyle(box).Dimensions[(int) Dimension.Width].Unit);
    }

    [Fact]
    public void A_second_pass_over_a_changed_tree_measures_again() {
        // The substitution is keyed on the layout pass, so nothing may survive into the next one. A
        // box whose content grew must not keep the width its old content asked for.
        var (tree, root, box, text) = Fixture(500f);
        using var owner = tree;

        tree.SetDimension(box, Dimension.Width, StyleLength.Keyword(LayoutUnit.MaxContent));
        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);
        Assert.Equal(30f, tree.GetWidth(box), Tolerance);

        tree.SetMeasureFunction(text, static (in MeasureRequest request) =>
            new LayoutSize(request.WidthMode == MeasureMode.Exactly ? request.AvailableWidth : 70f, 10f));

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);
        Assert.Equal(70f, tree.GetWidth(box), Tolerance);
    }

    [Fact]
    public void A_content_keyword_on_a_flex_item_reaches_the_item_and_not_only_the_container() {
        // The block cases above all go through `CalculateBlockLayoutImpl`. This one is the flex path,
        // and it is a COLUMN so that the keyword is on the item's CROSS axis: §9.4 stretches an item
        // whose cross size is `auto` to the line, so the box is 500 across unless `w-max` lands.
        using var tree = new LayoutTree();

        var root = tree.CreateNode();
        tree.SetFlexDirection(root, FlexDirection.Column);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(500f));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(100f));

        var box = tree.CreateNode();
        tree.SetDimension(box, Dimension.Width, StyleLength.Keyword(LayoutUnit.MaxContent));
        tree.AddChild(root, box);

        var text = tree.CreateNode();
        tree.SetMeasureFunction(text, MeasureThreeWords);
        tree.AddChild(box, text);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(30f, tree.GetWidth(box), Tolerance);
    }
}
