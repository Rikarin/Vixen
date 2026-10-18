// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Layout.Tests;

/// <summary>
///     A flex row offered "at most" a width is a fit-content box, and its items shrink into it (#1448).
/// </summary>
/// <remarks>
///     <para>
///         CSS Sizing §5.2 sizes a fit-content box at <c>min(max-content, max(min-content, available))</c>
///         and Flexbox §9.7 then lays the items out at that size, shrinking whatever has
///         <c>flex-shrink</c>. Yoga's STEP 5 stopped a content-sized row only at its own
///         <c>max-width</c>: an offer of "at most W" was not a bound, so the row came out as wide as
///         its items' bases and widened every container above it. The editor worked around it with
///         <c>max-width: 100%</c> on the asset-editor side columns (#1411) and on the toast (#1400).
///     </para>
///     <para>
///         The expected numbers are closed-form: words of ten points, a 24-point icon, and an offer
///         chosen so the content cannot fit.
///     </para>
/// </remarks>
public class FitContentRowTests {
    const float Word = 10f;
    const float LineHeight = 10f;

    /// <summary><paramref name="words" /> ten-point words, wrapped to whatever room they are offered.</summary>
    static MeasureFunction Words(int words) =>
        (in MeasureRequest request) => {
            var width = request.WidthMode switch {
                MeasureMode.Exactly => request.AvailableWidth,
                MeasureMode.AtMost => MathF.Max(Word, MathF.Min(request.AvailableWidth, words * Word)),
                _ => words * Word
            };

            var perLine = MathF.Max(1f, MathF.Floor(width / Word));
            var lines = MathF.Ceiling(words / perLine);

            return new LayoutSize(width, request.HeightMode == MeasureMode.Exactly ? request.AvailableHeight : lines * LineHeight);
        };

    /// <summary>
    ///     A <paramref name="width" />-point column whose row is <c>align-self: flex-start</c> — so
    ///     offered "at most" the column — holding a fixed 24-point icon and a shrinkable text.
    /// </summary>
    static (LayoutTree Tree, LayoutNodeId Root, LayoutNodeId Row, LayoutNodeId Icon, LayoutNodeId Text) Fixture(
        float width,
        int words,
        float textShrink = 1f,
        FlexDirection rowDirection = FlexDirection.Row,
        float gap = 0f
    ) {
        var tree = new LayoutTree();

        var root = tree.CreateNode();
        tree.SetFlexDirection(root, FlexDirection.Column);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(width));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(400f));

        var row = tree.CreateNode();
        tree.SetFlexDirection(row, rowDirection);
        tree.SetAlignSelf(row, Align.FlexStart);
        tree.SetGap(row, Gutter.Column, StyleLength.Points(gap));
        tree.AddChild(root, row);

        var icon = tree.CreateNode();
        tree.SetDimension(icon, Dimension.Width, StyleLength.Points(24f));
        tree.SetDimension(icon, Dimension.Height, StyleLength.Points(10f));
        tree.SetFlexShrink(icon, 0f);
        tree.AddChild(row, icon);

        var text = tree.CreateNode();
        tree.SetMeasureFunction(text, Words(words));
        tree.SetFlexShrink(text, textShrink);
        tree.AddChild(row, text);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        return (tree, root, row, icon, text);
    }

    /// <summary>
    ///     ⚠ <b>The defect as filed</b>: twenty words are 200 points, the icon 24 more, and the row is
    ///     offered 150. Before the fix the row came out 174 wide — the icon plus the text's basis, which
    ///     was itself measured "at most" 150 — and the text kept one line.
    /// </summary>
    [Fact]
    public void A_row_offered_at_most_a_width_is_that_wide_and_its_text_wraps() {
        var (tree, _, row, icon, text) = Fixture(150f, 20);

        Assert.Equal(150f, tree.GetWidth(row));
        Assert.Equal(24f, tree.GetWidth(icon));
        Assert.Equal(126f, tree.GetWidth(text));

        // 126 points hold twelve words a line, so twenty take two lines.
        Assert.Equal(20f, tree.GetHeight(text));
        Assert.Equal(20f, tree.GetHeight(row));
    }

    /// <summary>A row whose content fits is its content's width, not the offer's: the min in the clamp.</summary>
    [Fact]
    public void A_row_whose_content_fits_is_as_wide_as_its_content() {
        var (tree, _, row, _, text) = Fixture(150f, 5);

        Assert.Equal(74f, tree.GetWidth(row));
        Assert.Equal(50f, tree.GetWidth(text));
        Assert.Equal(10f, tree.GetHeight(text));
    }

    /// <summary>
    ///     The max in the clamp: the text's automatic minimum is one word, so the row cannot be
    ///     narrower than 34 however little it is offered, and overflows its 20-point column instead.
    /// </summary>
    [Fact]
    public void A_row_is_never_narrower_than_its_min_content() {
        var (tree, _, row, _, text) = Fixture(20f, 20);

        Assert.Equal(34f, tree.GetWidth(row));
        Assert.Equal(10f, tree.GetWidth(text));
    }

    /// <summary>
    ///     The same clamp with a 10-point <c>column-gap</c>: the floor is the icon, the gutter and one
    ///     word, 24 + 10 + 10 = 44, with the gutter counted once.
    /// </summary>
    /// <remarks>
    ///     ⚠ The floor used to add the gutters itself because <c>ComputeMinContentSize</c> left them
    ///     out; once the probe counted them (#1468) the compensation would make this 54.
    /// </remarks>
    [Fact]
    public void A_gapped_row_is_never_narrower_than_its_min_content_and_its_gutter_counts_once() {
        var (tree, _, row, _, text) = Fixture(20f, 20, gap: 10f);

        Assert.Equal(44f, tree.GetWidth(row));
        Assert.Equal(10f, tree.GetWidth(text));
        Assert.Equal(34f, tree.GetLeft(text));
    }

    /// <summary>
    ///     Nothing can shrink, so the min-content contribution of each item is its base and the row
    ///     keeps its content width — the cap is a bound on what flexes, not a clip.
    /// </summary>
    /// <remarks>
    ///     The text's base is whatever this store measures it at; the assertion is only that the row
    ///     holds both items and overflows the offer rather than squeezing them.
    /// </remarks>
    [Fact]
    public void A_row_of_unshrinkable_items_keeps_its_content_width() {
        var (tree, _, row, _, text) = Fixture(150f, 20, textShrink: 0f);

        Assert.True(tree.GetWidth(text) > 126f, $"the text was shrunk to {tree.GetWidth(text)}");
        Assert.Equal(24f + tree.GetWidth(text), tree.GetWidth(row));
    }

    /// <summary>
    ///     ⚠ <b>The shape #1411 actually had, and the one the first draft of the fix missed</b>: the
    ///     row is not offered "at most" a width directly but STRETCHED across a column that is — a
    ///     <c>scroll-content</c>, <c>align-self: flex-start</c>, holding a list of rows of a 72-point
    ///     stage and a growable message.
    /// </summary>
    /// <remarks>
    ///     The column measures the row with its height already exact, which is the case Yoga skips
    ///     the flex step for and measures the line from its bases. The fix declined the skip, but
    ///     <c>JustifyMainAxis</c> re-derived "skipped" from the same two conditions instead of being
    ///     told, so it summed the unshrunk bases and the row reported 1072 while its text had been
    ///     shrunk to 228. With and without the list in between, and with and without
    ///     <c>min-width: 100%</c>, which is what the #1412 re-entry reads.
    /// </remarks>
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void A_row_stretched_across_a_fit_content_column_is_capped_with_it(bool minWidth, bool withList) {
        var tree = new LayoutTree();

        var root = tree.CreateNode();
        tree.SetFlexDirection(root, FlexDirection.Column);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(300f));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(400f));

        var content = tree.CreateNode();
        tree.SetFlexDirection(content, FlexDirection.Column);
        tree.SetAlignSelf(content, Align.FlexStart);
        tree.SetFlexShrink(content, 0f);
        if (minWidth) {
            tree.SetMinDimension(content, Dimension.Width, StyleLength.Percent(100f));
        }

        tree.AddChild(root, content);

        var list = content;
        if (withList) {
            list = tree.CreateNode();
            tree.SetFlexDirection(list, FlexDirection.Column);
            tree.SetFlexShrink(list, 1f);
            tree.AddChild(content, list);
        }

        var row = tree.CreateNode();
        tree.SetFlexDirection(row, FlexDirection.Row);
        tree.SetFlexShrink(row, 1f);
        tree.AddChild(list, row);

        var stage = tree.CreateNode();
        tree.SetDimension(stage, Dimension.Width, StyleLength.Points(72f));
        tree.SetDimension(stage, Dimension.Height, StyleLength.Points(10f));
        tree.SetFlexShrink(stage, 0f);
        tree.AddChild(row, stage);

        var message = tree.CreateNode();
        tree.SetMeasureFunction(message, Words(100));
        tree.SetFlexShrink(message, 1f);
        tree.SetFlexGrow(message, 1f);
        tree.SetMinDimension(message, Dimension.Width, StyleLength.Points(0f));
        tree.AddChild(row, message);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(300f, tree.GetWidth(content));
        Assert.Equal(300f, tree.GetWidth(row));
        Assert.Equal(228f, tree.GetWidth(message));

        // 228 points hold twenty-two words a line, so a hundred take five.
        Assert.Equal(50f, tree.GetHeight(message));
        Assert.Equal(50f, tree.GetHeight(content));
    }

    /// <summary>
    ///     A growable <c>flex-basis: 0</c> item in a row offered "at most" 300 points (#1440): its
    ///     content counts toward the row's size, and once that size is decided its base is the
    ///     declared zero again and it grows into what is left.
    /// </summary>
    /// <remarks>
    ///     CSS Flexbox §9.9.1: a growable item's max-content contribution is its content; the flex
    ///     base size clamps it from above only when the item is not growable. Before the fix the
    ///     declared zero was trusted under a fit-content offer, so the row was the stage alone, 72
    ///     wide, and the message 0 — only a max-content offer reached the content branch.
    /// </remarks>
    [Theory]
    [InlineData(10, 172f, 100f, 10f)] // fits: the row is its content, 72 + 100
    [InlineData(50, 300f, 228f, 30f)] // overflows: capped at the offer (#1448), 22 words a line
    public void A_growable_zero_basis_item_counts_its_content_under_a_fit_content_offer(
        int words,
        float rowWidth,
        float messageWidth,
        float messageHeight
    ) {
        var tree = new LayoutTree();

        var root = tree.CreateNode();
        tree.SetFlexDirection(root, FlexDirection.Column);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(300f));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(400f));

        var row = tree.CreateNode();
        tree.SetFlexDirection(row, FlexDirection.Row);
        tree.SetAlignSelf(row, Align.FlexStart);
        tree.AddChild(root, row);

        var stage = tree.CreateNode();
        tree.SetDimension(stage, Dimension.Width, StyleLength.Points(72f));
        tree.SetDimension(stage, Dimension.Height, StyleLength.Points(10f));
        tree.SetFlexShrink(stage, 0f);
        tree.AddChild(row, stage);

        var message = tree.CreateNode();
        tree.SetMeasureFunction(message, Words(words));
        tree.SetFlexGrow(message, 1f);
        tree.SetFlexShrink(message, 0f);
        tree.SetFlexBasis(message, StyleLength.Points(0f));
        tree.SetMinDimension(message, Dimension.Width, StyleLength.Points(0f));
        tree.AddChild(row, message);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(rowWidth, tree.GetWidth(row));
        Assert.Equal(messageWidth, tree.GetWidth(message));
        Assert.Equal(messageHeight, tree.GetHeight(message));
    }

    /// <summary>
    ///     ⚠ <b>The contributions size the row and the bases flex it, and they are two numbers.</b>
    ///     Two <c>flex: 1 0 0</c> texts of 5 and 15 words in a row offered "at most" 300: the row is
    ///     their contributions, 50 + 150, and the 200 is then shared from the declared zeros — 100
    ///     each. Leaving the measured contributions in place as bases keeps them at 50 and 150, which
    ///     sums to the same row and is wrong per item (#1440, AdoptDeclaredFlexBases).
    /// </summary>
    [Fact]
    public void A_fit_content_row_is_sized_by_contributions_and_flexed_from_its_declared_bases() {
        var tree = new LayoutTree();

        var root = tree.CreateNode();
        tree.SetFlexDirection(root, FlexDirection.Column);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(300f));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(400f));

        var row = tree.CreateNode();
        tree.SetFlexDirection(row, FlexDirection.Row);
        tree.SetAlignSelf(row, Align.FlexStart);
        tree.AddChild(root, row);

        var items = new LayoutNodeId[2];
        int[] words = [5, 15];
        for (var i = 0; i < items.Length; i++) {
            items[i] = tree.CreateNode();
            tree.SetMeasureFunction(items[i], Words(words[i]));
            tree.SetFlexGrow(items[i], 1f);
            tree.SetFlexShrink(items[i], 0f);
            tree.SetFlexBasis(items[i], StyleLength.Points(0f));
            tree.SetMinDimension(items[i], Dimension.Width, StyleLength.Points(0f));
            tree.AddChild(row, items[i]);
        }

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(200f, tree.GetWidth(row));
        Assert.Equal(100f, tree.GetWidth(items[0]));
        Assert.Equal(100f, tree.GetWidth(items[1]));
    }

    /// <summary>
    ///     ⚠ <b>Only the inline axis.</b> A column's main size is its height, and CSS never sizes a
    ///     block size by shrink-to-fit: an auto height is the content's, offer or no offer. The same
    ///     tree turned into a column inside a 40-point-tall offer must keep its content height.
    /// </summary>
    [Fact]
    public void A_column_offered_at_most_a_height_keeps_its_content_height() {
        var tree = new LayoutTree();

        var root = tree.CreateNode();
        tree.SetFlexDirection(root, FlexDirection.Row);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(400f));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(40f));

        var column = tree.CreateNode();
        tree.SetFlexDirection(column, FlexDirection.Column);
        tree.SetAlignSelf(column, Align.FlexStart);
        tree.AddChild(root, column);

        for (var i = 0; i < 3; i++) {
            var item = tree.CreateNode();
            tree.SetDimension(item, Dimension.Width, StyleLength.Points(10f));
            tree.SetDimension(item, Dimension.Height, StyleLength.Points(30f));
            tree.SetFlexShrink(item, 1f);
            tree.AddChild(column, item);
        }

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(90f, tree.GetHeight(column));
    }
}
