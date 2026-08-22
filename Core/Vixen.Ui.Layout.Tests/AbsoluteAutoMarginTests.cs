// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Layout.Tests;

/// <summary>
///     CSS 2.1 §10.3.7 and §10.6.4 — the slack in an over-constrained inset equation going to
///     whichever margin said <c>auto</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This file exists because the rule has no oracle, which is a different situation from
///         the one <see cref="MarginCollapsingTests" /> is in.</b> That file pins rules the corpus
///         <i>contains</i> and cannot run. These are rules no corpus contains at all: Yoga does not
///         implement out-of-flow auto margins, the flex corpus has no fixture for them, and the 22
///         that exist live only under <c>block/</c>. Those 22 are green — they are the reason the
///         implementation is trusted at all — and every case below is one they do not reach, judged
///         against the specification's own wording and WPT's <c>css/css-position/</c> rather than
///         against a recorded number.
///     </para>
///     <para>
///         ⚠ <b>The two axes are deliberately not symmetric, and that is the single decision here
///         most likely to look like a bug.</b> §10.3.7 splits the slack evenly between two auto
///         margins "unless this would make them negative, in which case, when the direction of the
///         containing block is <c>ltr</c> (<c>rtl</c>), set <c>margin-left</c>
///         (<c>margin-right</c>) to zero and solve for <c>margin-right</c> (<c>margin-left</c>)".
///         §10.6.4 states the block-axis rule with no such carve-out. So an over-wide box is pinned
///         to its inline-start edge and an over-tall one overflows equally at both ends.
///     </para>
/// </remarks>
public class AbsoluteAutoMarginTests {
    const float Tolerance = 0.0001f;

    /// <summary>
    ///     §10.3.7's negative carve-out, mirrored: in RTL the zeroed margin is <c>margin-right</c>.
    /// </summary>
    [Fact]
    public void Two_auto_inline_margins_with_negative_slack_pin_the_box_to_the_inline_start_edge() {
        using var tree = new LayoutTree();
        var root = Container(tree, Direction.Rtl);
        var child = Absolute(tree, root, width: 72f, height: 20f);

        tree.SetPosition(child, Edge.Left, StyleLength.Points(10f));
        tree.SetPosition(child, Edge.Right, StyleLength.Points(20f));
        tree.SetMargin(child, Edge.Left, StyleLength.Auto);
        tree.SetMargin(child, Edge.Right, StyleLength.Auto);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Rtl);

        // Slack is 52 − 10 − 20 − 72 = −50. RTL zeroes `margin-right`, so the box's right edge sits
        // at the `right` inset — 52 − 20 = 32 — and it overflows to the left of the container.
        Assert.Equal(-40f, tree.GetLeft(child), Tolerance);
    }

    /// <summary>The same slack with one auto margin goes to that margin, negative and all.</summary>
    [Fact]
    public void One_auto_inline_margin_takes_negative_slack_whole() {
        using var tree = new LayoutTree();
        var root = Container(tree, Direction.Rtl);
        var child = Absolute(tree, root, width: 72f, height: 20f);

        tree.SetPosition(child, Edge.Left, StyleLength.Points(10f));
        tree.SetPosition(child, Edge.Right, StyleLength.Points(20f));
        tree.SetMargin(child, Edge.Right, StyleLength.Auto);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Rtl);

        // `right` is the inline-start inset in RTL and it wins, so the box's right edge is at
        // 52 − 20 − (−50) = 82 and its left edge lands ten points past the container's right one.
        Assert.Equal(10f, tree.GetLeft(child), Tolerance);
    }

    /// <summary>
    ///     §10.6.4 has no negative carve-out, so two auto block margins stay equal and both go
    ///     negative.
    /// </summary>
    [Fact]
    public void Two_auto_block_margins_split_negative_slack_evenly() {
        using var tree = new LayoutTree();
        var root = Container(tree, Direction.Ltr);
        var child = Absolute(tree, root, width: 20f, height: 72f);

        tree.SetPosition(child, Edge.Top, StyleLength.Points(10f));
        tree.SetPosition(child, Edge.Bottom, StyleLength.Points(20f));
        tree.SetMargin(child, Edge.Top, StyleLength.Auto);
        tree.SetMargin(child, Edge.Bottom, StyleLength.Auto);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        // Slack is 52 − 10 − 20 − 72 = −50, halved to −25 and added to the `top` inset.
        Assert.Equal(-15f, tree.GetTop(child), Tolerance);
    }

    /// <summary>
    ///     §10.3.7 rule 3: with an auto width there is nothing over-constrained, so an auto margin is
    ///     zero and the width takes the slack instead.
    /// </summary>
    [Fact]
    public void An_auto_width_between_two_insets_leaves_an_auto_margin_at_zero() {
        using var tree = new LayoutTree();
        var root = Container(tree, Direction.Ltr);
        var child = Absolute(tree, root, width: float.NaN, height: 20f);

        tree.SetPosition(child, Edge.Left, StyleLength.Points(10f));
        tree.SetPosition(child, Edge.Right, StyleLength.Points(20f));
        tree.SetMargin(child, Edge.Left, StyleLength.Auto);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(10f, tree.GetLeft(child), Tolerance);
        Assert.Equal(22f, tree.GetWidth(child), Tolerance);
    }

    /// <summary>
    ///     Without both insets the equation is not over-constrained, so an auto margin is zero and the
    ///     box stays at its static position. This is the half the corpus's twelve
    ///     <c>*_without_inset</c> families already assert; it is here so that the precondition is
    ///     stated next to the rule rather than only in another file.
    /// </summary>
    [Fact]
    public void An_auto_margin_without_both_insets_resolves_to_zero() {
        using var tree = new LayoutTree();
        var root = Container(tree, Direction.Ltr);
        var child = Absolute(tree, root, width: 20f, height: 20f);

        tree.SetPosition(child, Edge.Left, StyleLength.Points(10f));
        tree.SetMargin(child, Edge.Right, StyleLength.Auto);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(10f, tree.GetLeft(child), Tolerance);
    }

    static LayoutNodeId Container(LayoutTree tree, Direction direction) {
        var root = tree.CreateNode();
        tree.SetDisplay(root, Display.Block);
        tree.SetDirection(root, direction);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(52f));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(52f));

        return root;
    }

    static LayoutNodeId Absolute(LayoutTree tree, LayoutNodeId parent, float width, float height) {
        var node = tree.CreateNode();
        tree.SetDisplay(node, Display.Block);
        tree.SetPositionType(node, PositionType.Absolute);

        if (!float.IsNaN(width)) {
            tree.SetDimension(node, Dimension.Width, StyleLength.Points(width));
        }

        tree.SetDimension(node, Dimension.Height, StyleLength.Points(height));
        tree.AddChild(parent, node);

        return node;
    }
}
