// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Layout.Tests;

/// <summary>
///     CSS Flexbox §9.7 — resolving flexible lengths, and the two sums it is built on.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The rule under test is that §9.3's sum and §9.7's are different numbers.</b> Lines
///         are broken by each item's outer HYPOTHETICAL main size — its flex base clamped by its used
///         min and max — while the free space handed to the distribution passes subtracts the frozen
///         items' target sizes and the unfrozen items' unclamped FLEX BASE sizes. One field served
///         both, so a clamp was charged to the pool twice: once by shrinking the pool it came out of,
///         and again by the pass that re-applied it to the item.
///     </para>
///     <para>
///         Hand-written for the reason <see cref="AutomaticMinimumSizeTests" /> gives: the corpus is
///         not allowed to be the only witness to a load-bearing rule. Each of these fails on the
///         arithmetic that was there before, and each names the Taffy or Yoga fixture that agrees.
///     </para>
/// </remarks>
public class FlexibleLengthResolutionTests {
    const float Tolerance = 0.0001f;

    [Fact]
    public void An_item_that_cannot_flex_is_still_floored_by_its_automatic_minimum() {
        // Taffy's `flex_basis_smaller_than_content_row`. `flex-basis: 50px` on a box wrapping a
        // 100pt child, nothing to grow into and nothing to shrink: §9.2 step 9 makes the hypothetical
        // main size 100, and §9.7 step 2 freezes the item there.
        //
        // ⚠ The floor used to be consulted only inside the two distribution passes, so an item that
        // never flexed never saw its own. This stayed 50 wide around a 100pt box.
        using var tree = new LayoutTree();
        var root = tree.CreateNode();
        tree.SetFlexDirection(root, FlexDirection.Row);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(100f));

        var item = tree.CreateNode();
        tree.SetFlexDirection(item, FlexDirection.Column);
        tree.SetFlexBasis(item, StyleLength.Points(50f));
        tree.AddChild(root, item);

        var content = tree.CreateNode();
        tree.SetDimension(content, Dimension.Width, StyleLength.Points(100f));
        tree.SetDimension(content, Dimension.Height, StyleLength.Points(100f));
        tree.AddChild(item, content);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(100f, tree.GetWidth(item), Tolerance);
    }

    [Fact]
    public void An_item_frozen_by_its_maximum_leaves_the_whole_pool_to_its_sibling() {
        // Yoga's Child_min_max_width_flexing. The `max-width: 20` item freezes at 20 the moment
        // §9.7 step 2 runs, so the other 100 belongs entirely to its sibling.
        //
        // ⚠ Step 2 removes the frozen item's grow FACTOR as well as its size. Leaving the factor in
        // splits the 100 two ways, the sibling's 50 then violates its own 60 minimum, and both items
        // end up frozen — which left the second pass dividing by a total of zero and returning NaN.
        using var tree = new LayoutTree();
        var root = tree.CreateNode();
        tree.SetPositionType(root, PositionType.Absolute);
        tree.SetFlexDirection(root, FlexDirection.Row);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(120f));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(50f));

        var floored = tree.CreateNode();
        tree.SetFlexGrow(floored, 1f);
        tree.SetFlexBasis(floored, StyleLength.Points(0f));
        tree.SetMinDimension(floored, Dimension.Width, StyleLength.Points(60f));
        tree.AddChild(root, floored);

        var capped = tree.CreateNode();
        tree.SetFlexGrow(capped, 1f);
        tree.SetFlexBasis(capped, StyleLength.Percent(50f));
        tree.SetMaxDimension(capped, Dimension.Width, StyleLength.Points(20f));
        tree.AddChild(root, capped);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(100f, tree.GetWidth(floored), Tolerance);
        Assert.Equal(20f, tree.GetWidth(capped), Tolerance);
    }

    [Fact]
    public void An_item_aligned_to_the_cross_start_of_its_line_keeps_its_leading_margin() {
        // Taffy's bevy_issue_8082, reduced. Two 50pt boxes with `margin: 10px` under
        // `align-items: flex-start` on a wrapping row: the line is 70 tall because the margins are
        // counted in it, and the item sits at 10 rather than hard against the line's edge.
        //
        // ⚠ Only the flex-start case dropped it. Stretch adds the leading margin and flex-end
        // subtracts the trailing one, so the omission was invisible next to two neighbours that
        // looked like it.
        using var tree = new LayoutTree();
        var root = tree.CreateNode();
        tree.SetFlexDirection(root, FlexDirection.Row);
        tree.SetFlexWrap(root, Wrap.Wrap);
        tree.SetAlignItems(root, Align.FlexStart);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(200f));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(200f));

        for (var i = 0; i < 2; i++) {
            var box = tree.CreateNode();
            tree.SetDimension(box, Dimension.Width, StyleLength.Points(50f));
            tree.SetDimension(box, Dimension.Height, StyleLength.Points(50f));
            tree.SetMargin(box, Edge.All, StyleLength.Points(10f));
            tree.AddChild(root, box);
        }

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(10f, tree.GetTop(tree.GetChild(root, 0)), Tolerance);
        Assert.Equal(10f, tree.GetTop(tree.GetChild(root, 1)), Tolerance);
    }

    [Fact]
    public void A_minimum_floors_the_hypothetical_size_and_leaves_the_flex_base_at_zero() {
        // Taffy's `min_width`. Two `flex-grow: 1` items in a 100pt row, `min-width: 60px` on the
        // first. Both flex BASE sizes are zero — §9.2 step 3E sizes the item under a max-content
        // constraint and an empty div wants nothing — so the pool is the whole 100. The first pass
        // splits it evenly, the first item violates its 60 minimum and freezes there, and the 40 that
        // is left all goes to its sibling.
        //
        // ⚠ The base used to be read back out of the trial layout's MeasuredDimensions, which had
        // already been through BoundAxis, so it came back as 60. With base == hypothetical §9.7
        // step 2 can never freeze the item, and 80 and 20 is what no amount of redistribution can
        // recover from. See LayoutResult.UnclampedMeasuredDimensions.
        using var tree = new LayoutTree();
        var root = tree.CreateNode();
        tree.SetFlexDirection(root, FlexDirection.Row);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(100f));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(100f));

        var floored = tree.CreateNode();
        tree.SetFlexGrow(floored, 1f);
        tree.SetMinDimension(floored, Dimension.Width, StyleLength.Points(60f));
        tree.AddChild(root, floored);

        var free = tree.CreateNode();
        tree.SetFlexGrow(free, 1f);
        tree.AddChild(root, free);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(60f, tree.GetWidth(floored), Tolerance);
        Assert.Equal(40f, tree.GetWidth(free), Tolerance);
    }

    [Theory]
    [InlineData(100f, 50f, 50f)]
    [InlineData(60f, 30f, 30f)]
    [InlineData(40f, 20f, 20f)]
    [InlineData(30f, 20f, 10f)]
    [InlineData(25f, 20f, 5f)]
    [InlineData(20f, 20f, 0f)]
    [InlineData(10f, 20f, 0f)]
    public void One_floor_between_two_shrinking_siblings_does_not_stop_the_other_one_shrinking(
        float containerWidth,
        float expectedFloored,
        float expectedFree
    ) {
        // Two `flex-basis: 60px; flex-shrink: 1` items, `min-width: 20px` on the first only. Chrome
        // is the oracle for every row: above 40 nothing violates, at 40 the first lands exactly on
        // its floor, and below it the first stays at 20 while the second keeps paying the rest.
        //
        // ⚠ THE ORACLE IS CLOSED-FORM, not a number chosen to match: the two widths sum to the
        // container on every row where the 20pt floor leaves that possible (30 = 20 + 10,
        // 25 = 20 + 5, 20 = 20 + 0), and only the last row overflows, by exactly the floor.
        //
        // ⚠ The window is bounded on BOTH sides, which is what makes this arithmetic rather than a
        // missing clause. §9.7 step 4 distributes every unfrozen item from ONE pool and ONE factor
        // sum and only then freezes the violators; the first pass took the second item's factor out
        // of the divisor the moment the first item was frozen, while still handing it the full,
        // un-repaid pool. The second item's share was computed against a divisor half the size, it
        // shot past its own zero floor, and it was frozen too — the pool the first pass handed back
        // then went POSITIVE, the second pass took the grow branch, found `flex-grow: 0`, and gave
        // both items their unshrunk 60pt bases. 60 / 60 in a 30pt row.
        using var tree = new LayoutTree();
        var root = tree.CreateNode();
        tree.SetFlexDirection(root, FlexDirection.Row);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(containerWidth));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(50f));

        var floored = tree.CreateNode();
        tree.SetFlexShrink(floored, 1f);
        tree.SetFlexBasis(floored, StyleLength.Points(60f));
        tree.SetMinDimension(floored, Dimension.Width, StyleLength.Points(20f));
        tree.AddChild(root, floored);

        var free = tree.CreateNode();
        tree.SetFlexShrink(free, 1f);
        tree.SetFlexBasis(free, StyleLength.Points(60f));
        tree.SetMinDimension(free, Dimension.Width, StyleLength.Points(0f));
        tree.AddChild(root, free);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(expectedFloored, tree.GetWidth(floored), Tolerance);
        Assert.Equal(expectedFree, tree.GetWidth(free), Tolerance);

        // The same rows again as a property rather than a table: nothing is left on the floor while
        // an item can still shrink, and nothing overflows while the floor still allows a fit.
        if (containerWidth >= 20f) {
            Assert.Equal(containerWidth, tree.GetWidth(floored) + tree.GetWidth(free), Tolerance);
        }
    }

    [Fact]
    public void A_content_sized_item_shrinks_from_its_max_content_size_and_not_from_the_room_it_was_offered() {
        // Taffy's `measure_child_with_flex_shrink_hidden`, and CSS Flexbox §9.2 step 3E: an item with
        // no declared basis and no declared main size has its flex base size measured under a
        // MAX-CONTENT constraint. The offer this store hands the child instead is the container's
        // available space, and the two agree for everything that fits — which is the entire
        // population §9.7 never has to shrink.
        //
        // 500 points of unbreakable content and a 50-point box in a 100-point row. Bases 500 and 50
        // shrink in proportion to themselves, so the text pays ten times what the box pays and the
        // answer is 90.909 and 9.0909. Measured at the 100-point offer the text reports a base of
        // 100, the two bases become 100 and 50, and they come out 66.7 and 33.3 — the pool is
        // computed from the room rather than from the content, and BOTH items get the wrong share.
        //
        // ⚠ THE ORACLE IS TWO CLOSED-FORM PROPERTIES, not the two numbers: the widths sum to the
        // container, and §9.7's shrink is scaled BY THE BASE, so the amounts the two items give up
        // must be in the same ratio as their bases — a tenth, here. Both are asserted, and the
        // wrong-base answer satisfies the first alone.
        //
        // ⚠ The content item CLIPS, which is not decoration: without it §4.5's automatic minimum
        // floors it at its own 500 points of content and no distribution happens at all.
        using var tree = new LayoutTree();

        // ⚠ Unrounded, because the property being asserted is an exact ratio and the pixel grid is
        // not exact: Chrome answers this fixture 9 and 91, and 9 + 91 is 100 while 10 x (50 - 9) is
        // 410 against the 409 the text really gives up. The rounding is right and it is not what is
        // under test.
        tree.PointScaleFactor = 0f;

        var root = tree.CreateNode();
        tree.SetFlexDirection(root, FlexDirection.Row);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(100f));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(50f));

        var box = tree.CreateNode();
        tree.SetFlexShrink(box, 1f);
        tree.SetDimension(box, Dimension.Width, StyleLength.Points(50f));
        tree.SetDimension(box, Dimension.Height, StyleLength.Points(50f));
        tree.AddChild(root, box);

        var text = tree.CreateNode();
        tree.SetFlexShrink(text, 1f);
        tree.SetOverflow(text, Overflow.Hidden);
        tree.SetMeasureFunction(text, MeasureWrappingContent);
        tree.AddChild(root, text);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        var boxWidth = tree.GetWidth(box);
        var textWidth = tree.GetWidth(text);

        Assert.Equal(50f - (450f * 50f / 550f), boxWidth, Tolerance);
        Assert.Equal(500f - (450f * 500f / 550f), textWidth, Tolerance);

        Assert.Equal(100f, boxWidth + textWidth, Tolerance);
        Assert.Equal(10f * (50f - boxWidth), 500f - textWidth, Tolerance);
    }

    /// <summary>500 points of content that fills whatever it is offered and wraps to fit.</summary>
    /// <remarks>
    ///     ⚠ <b>A measurer that answers the same width under every mode cannot see this rule</b>, and
    ///     that is why <see cref="AutomaticMinimumSizeTests" />' fixed-width one is not reused here:
    ///     the whole difference between a max-content measurement and an offered one is a measurer
    ///     that reports less when it is given less. This one is a paragraph in the only sense that
    ///     matters — it fills the offer and grows taller for it.
    /// </remarks>
    static LayoutSize MeasureWrappingContent(in MeasureRequest request) {
        const float content = 500f;

        if (request.WidthMode == MeasureMode.Undefined || float.IsNaN(request.AvailableWidth)) {
            return new LayoutSize(content, 10f);
        }

        var width = request.WidthMode == MeasureMode.Exactly
            ? request.AvailableWidth
            : MathF.Min(content, request.AvailableWidth);

        return new LayoutSize(width, width <= 0f ? 10f : MathF.Ceiling(content / width) * 10f);
    }

    [Fact]
    public void Whether_the_main_axis_overflows_is_asked_of_the_hypothetical_sizes_not_the_bases() {
        // Taffy's `gap_column_gap_wrap_align_stretch` and Yoga's Column_gap_wrap_align_stretch. Five
        // `flex-grow: 1; min-width: 60px` items in a 300pt wrapping row with a 5pt column gap: four
        // fit on the first line and the fifth wraps, and `align-content: stretch` halves the 300pt
        // height between the two lines.
        //
        // ⚠ THIS IS THE OTHER HALF OF THE TEST ABOVE and it fails in the opposite direction. STEP 3
        // decides whether the main axis overflows, and it used to add up the items' flex BASES. That
        // is §9.3's question and §9.3 asks it of the outer HYPOTHETICAL sizes; the two agreed only
        // while the base was the clamped measurement. With real bases the sum is 20pt of gap, nothing
        // appears to overflow, every item is stretched to the container's full height, and both lines
        // come out 300 tall inside a 300pt box.
        using var tree = new LayoutTree();
        var root = tree.CreateNode();
        tree.SetFlexDirection(root, FlexDirection.Row);
        tree.SetFlexWrap(root, Wrap.Wrap);
        tree.SetAlignContent(root, Align.Stretch);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(300f));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(300f));
        tree.SetGap(root, Gutter.Column, StyleLength.Points(5f));

        for (var i = 0; i < 5; i++) {
            var item = tree.CreateNode();
            tree.SetFlexGrow(item, 1f);
            tree.SetMinDimension(item, Dimension.Width, StyleLength.Points(60f));
            tree.AddChild(root, item);
        }

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        for (var i = 0; i < 4; i++) {
            Assert.Equal(150f, tree.GetHeight(tree.GetChild(root, i)), Tolerance);
        }

        Assert.Equal(150f, tree.GetTop(tree.GetChild(root, 4)), Tolerance);
        Assert.Equal(300f, tree.GetWidth(tree.GetChild(root, 4)), Tolerance);
    }
}
