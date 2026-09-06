// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Layout.Tests;

/// <summary>
///     CSS Box Alignment §9 — <c>align-items: baseline</c> where there is no baseline to align to.
/// </summary>
public class BaselineAlignmentTests {
    const float Tolerance = 0.0001f;

    /// <summary>
    ///     A column container's cross axis is its inline axis, so <c>baseline</c> degrades — and the
    ///     edge it degrades to is line-left, which <c>direction</c> does not mirror.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Measured in Chrome, with the control that decides it.</b> The same wrapped column
    ///         under <c>align-items: flex-start</c> and under <c>align-items: start</c> puts the
    ///         30-wide item at x=70 and the 40-wide one at x=10 in RTL — mirrored, exactly as this
    ///         store did for all three keywords. Under <c>align-items: baseline</c> Chrome puts them
    ///         at 50 and 0 instead: every item shares its line's PHYSICAL LEFT edge, in both
    ///         directions, while its own children go on being mirrored one level down. So the
    ///         difference is baseline's alone rather than an artefact of how lines are mirrored, and
    ///         a fourth variant with real text in the items (a participating baseline rather than a
    ///         synthesised one) gives the same four numbers.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>That is line-left, which is a specified concept and not a physical accident.</b>
    ///         CSS Writing Modes §6.3 makes line-left and line-right depend on the writing mode
    ///         alone — <c>direction</c> does not enter — and baselines are line-relative by
    ///         construction: a baseline table is anchored at the line-left edge of the box it
    ///         describes. In <c>horizontal-tb</c> that is physical left in LTR and in RTL alike. So a
    ///         <c>baseline</c> request in a column container degrades to the line-left edge, which is
    ///         <c>flex-start</c> in LTR and <c>flex-end</c> in RTL, where every other alignment
    ///         keyword is flow-relative and mirrors.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The previous reading of these numbers was that no rule produced them.</b> Three
    ///         alignments were checked against Chrome's eight and all three refuted — a synthesised
    ///         baseline at the inline-start edge, one at the inline-end edge, and mirroring the whole
    ///         content box at the end — and the conclusion drawn was that Chrome mirrors at line
    ///         granularity and leaves the offset inside the line alone, "the shape of an artefact
    ///         rather than of a keyword". The control above is what refutes that: an implementation
    ///         that failed to mirror an offset would fail to mirror it for <c>flex-start</c> too, in
    ///         the same container, in the same pass. <c>Rikarin/Vixen#264</c>.
    ///     </para>
    ///     <para>
    ///         The corpus says the same thing in its own two fixtures,
    ///         <c>align_baseline_multiline_column</c> and <c>align_baseline_multiline_column2</c>,
    ///         which is why they are no longer listed in <c>Taffy/KnownGaps.txt</c>. This is written
    ///         out by hand as well because the rule is one sentence and the fixtures are eight
    ///         numbers: the LTR half is the control that keeps the fix from being "mirror nothing".
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData(Direction.Ltr, 0f, 0f, 50f, 50f, 0f, 0f)]
    [InlineData(Direction.Rtl, 50f, 50f, 0f, 0f, 10f, 30f)]
    public void Baseline_in_a_column_container_degrades_to_the_line_left_edge(
        Direction direction,
        float firstX,
        float secondX,
        float thirdX,
        float fourthX,
        float secondChildX,
        float thirdChildX
    ) {
        using var tree = new LayoutTree();

        var root = tree.CreateNode();
        tree.SetFlexDirection(root, FlexDirection.Column);
        tree.SetFlexWrap(root, Wrap.Wrap);
        tree.SetAlignItems(root, Align.Baseline);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(100f));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(100f));

        var first = Item(tree, root, 50f, 50f);
        var second = Item(tree, root, 30f, 50f);
        var third = Item(tree, root, 40f, 70f);
        var fourth = Item(tree, root, 50f, 20f);

        // The items' own children are the control one level down: those ARE mirrored, in the same
        // layout pass, which is what makes the items' placement a rule about baseline rather than a
        // direction that failed to reach this subtree.
        var secondChild = Item(tree, second, 20f, 20f);
        var thirdChild = Item(tree, third, 10f, 10f);

        tree.CalculateLayout(root, float.NaN, float.NaN, direction);

        // The lines mirror: line one is 0..50 in LTR and 50..100 in RTL, and the items follow it.
        Assert.Equal(firstX, tree.GetLeft(first), Tolerance);
        Assert.Equal(secondX, tree.GetLeft(second), Tolerance);
        Assert.Equal(thirdX, tree.GetLeft(third), Tolerance);
        Assert.Equal(fourthX, tree.GetLeft(fourth), Tolerance);

        // The main axis is not in question and is the premise for the above: two lines of two.
        Assert.Equal(0f, tree.GetTop(first), Tolerance);
        Assert.Equal(50f, tree.GetTop(second), Tolerance);
        Assert.Equal(0f, tree.GetTop(third), Tolerance);
        Assert.Equal(70f, tree.GetTop(fourth), Tolerance);

        // And the mirror is alive one box down, where `direction` is flow-relative as usual.
        Assert.Equal(secondChildX, tree.GetLeft(secondChild), Tolerance);
        Assert.Equal(thirdChildX, tree.GetLeft(thirdChild), Tolerance);
    }

    static LayoutNodeId Item(LayoutTree tree, LayoutNodeId parent, float width, float height) {
        var node = tree.CreateNode();
        tree.SetFlexDirection(node, FlexDirection.Column);
        tree.SetDimension(node, Dimension.Width, StyleLength.Points(width));
        tree.SetDimension(node, Dimension.Height, StyleLength.Points(height));
        tree.AddChild(parent, node);

        return node;
    }
}
