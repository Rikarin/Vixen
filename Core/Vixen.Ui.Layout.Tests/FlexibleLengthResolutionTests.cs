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
}
