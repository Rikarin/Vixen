// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Layout.Tests;

/// <summary>
///     An item that cannot shrink takes its MAX-CONTENT size as its flex base, offer or no offer, so a
///     <c>flex-shrink: 0</c> row of shrinkable items lays them out unsqueezed (#1510).
/// </summary>
/// <remarks>
///     <para>
///         CSS Flexbox §9.2 step 3E sizes a content-based flex base under a max-content constraint.
///         This store asked that question only of an item that could shrink, and measured an
///         unshrinkable one at the container's offer instead. The two answers agreed for every row
///         until #1448 made a row offered "at most" a width a fit-content box: from then on an
///         unshrinkable ROW measured at the offer came back at <c>max(offer, min-content)</c> with its
///         own shrinkable items squeezed into that, and the outer line, unable to shrink the row, kept
///         the squeezed answer. That is the editor's dock strip — a <c>flex-shrink: 0</c> list of tabs
///         in a clipping viewport — whose two-word titles wrapped to their min-content.
///     </para>
///     <para>
///         ⚠ <b>The shape needs the TABS to be shrinkable.</b> The reproduction filed with #1510 built
///         the strip with this store's Yoga-default <c>flex-shrink: 0</c> on everything it did not set,
///         so the capped row had nothing it could shrink and came out at its content width. A
///         <c>.vcss</c> document writes CSS's initial value of 1 (#628), which is what the control tree
///         has and the filed shape lacked.
///     </para>
///     <para>
///         Closed-form numbers: a label is two 45-point words — 90 on one line, 45 wrapped to two of
///         ten points each — beside a 21-point box, inside 9 points of padding a side.
///     </para>
/// </remarks>
public class UnshrinkableFlexBaseTests {
    const float WordWidth = 45f;
    const float LineHeight = 10f;

    /// <summary>Two 45-point words that wrap one to a line when offered less than both.</summary>
    static MeasureFunction TwoWords() =>
        (in MeasureRequest request) => {
            var width = request.WidthMode switch {
                MeasureMode.Exactly => request.AvailableWidth,
                MeasureMode.AtMost => MathF.Max(WordWidth, MathF.Min(request.AvailableWidth, 2f * WordWidth)),
                _ => 2f * WordWidth
            };

            var lines = width >= 2f * WordWidth ? 1f : 2f;

            return new LayoutSize(width, request.HeightMode == MeasureMode.Exactly ? request.AvailableHeight : lines * LineHeight);
        };

    /// <summary>
    ///     A 184-point column holding a tab strip: a 28-point arrow and a clipping, zero-basis viewport
    ///     around a <c>flex-shrink: <paramref name="listShrink" /></c> list of two tabs. Every node the
    ///     test does not shrink-proof is given CSS's initial <c>flex-shrink: 1</c>, as a stylesheet does.
    /// </summary>
    static (LayoutTree Tree, LayoutNodeId List, LayoutNodeId[] Tabs, LayoutNodeId[] Labels) Strip(float listShrink) {
        var tree = new LayoutTree();

        var root = tree.CreateNode();
        tree.SetFlexDirection(root, FlexDirection.Column);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(184f));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(300f));

        var strip = tree.CreateNode();
        tree.SetFlexDirection(strip, FlexDirection.Row);
        tree.SetFlexShrink(strip, 1f);
        tree.AddChild(root, strip);

        var arrow = tree.CreateNode();
        tree.SetDimension(arrow, Dimension.Width, StyleLength.Points(28f));
        tree.SetDimension(arrow, Dimension.Height, StyleLength.Points(10f));
        tree.SetFlexShrink(arrow, 0f);
        tree.AddChild(strip, arrow);

        var viewport = tree.CreateNode();
        tree.SetFlexDirection(viewport, FlexDirection.Row);
        tree.SetFlexGrow(viewport, 1f);
        tree.SetFlexShrink(viewport, 1f);
        tree.SetFlexBasis(viewport, StyleLength.Points(0f));
        tree.SetMinDimension(viewport, Dimension.Width, StyleLength.Points(0f));
        tree.SetOverflow(viewport, Overflow.Hidden);
        tree.AddChild(strip, viewport);

        var list = tree.CreateNode();
        tree.SetFlexDirection(list, FlexDirection.Row);
        tree.SetFlexShrink(list, listShrink);
        tree.SetAlignItems(list, Align.Stretch);
        tree.AddChild(viewport, list);

        var tabs = new LayoutNodeId[2];
        var labels = new LayoutNodeId[2];
        for (var i = 0; i < tabs.Length; i++) {
            tabs[i] = tree.CreateNode();
            tree.SetFlexDirection(tabs[i], FlexDirection.Row);
            tree.SetAlignItems(tabs[i], Align.Center);
            tree.SetFlexShrink(tabs[i], 1f);
            tree.SetPadding(tabs[i], Edge.Left, StyleLength.Points(9f));
            tree.SetPadding(tabs[i], Edge.Right, StyleLength.Points(9f));
            tree.AddChild(list, tabs[i]);

            labels[i] = tree.CreateNode();
            tree.SetMeasureFunction(labels[i], TwoWords());
            tree.SetFlexShrink(labels[i], 1f);
            tree.AddChild(tabs[i], labels[i]);

            var box = tree.CreateNode();
            tree.SetDimension(box, Dimension.Width, StyleLength.Points(21f));
            tree.SetDimension(box, Dimension.Height, StyleLength.Points(10f));
            tree.SetFlexShrink(box, 0f);
            tree.AddChild(tabs[i], box);
        }

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        return (tree, list, tabs, labels);
    }

    /// <summary>
    ///     ⚠ <b>The defect as filed</b>: the list cannot shrink, so its base is its max-content width —
    ///     two tabs of 9 + 90 + 21 + 9 = 129 — and it overflows its 156-point viewport at 258 with
    ///     every label on one line. Before the fix it came out 192, the tabs' min-content sum, with both
    ///     labels wrapped to 45 by 20.
    /// </summary>
    [Fact]
    public void An_unshrinkable_list_of_shrinkable_tabs_keeps_every_title_on_one_line() {
        var (tree, list, tabs, labels) = Strip(listShrink: 0f);

        Assert.Equal(258f, tree.GetWidth(list));
        foreach (var tab in tabs) {
            Assert.Equal(129f, tree.GetWidth(tab));
        }

        foreach (var label in labels) {
            Assert.Equal(90f, tree.GetWidth(label));
            Assert.Equal(LineHeight, tree.GetHeight(label));
        }
    }

    /// <summary>
    ///     The control: the same list allowed to shrink IS squeezed, to its content's floor — the
    ///     tabs' min-content of 9 + 45 + 21 + 9 = 84 each, since a flex item does not go below its
    ///     automatic minimum — so the fix is a statement about unshrinkable items and not a change to
    ///     what an offer means.
    /// </summary>
    [Fact]
    public void A_shrinkable_list_of_the_same_tabs_is_squeezed_to_its_floor() {
        var (tree, list, _, labels) = Strip(listShrink: 1f);

        Assert.Equal(168f, tree.GetWidth(list));
        foreach (var label in labels) {
            Assert.Equal(WordWidth, tree.GetWidth(label));
            Assert.Equal(2f * LineHeight, tree.GetHeight(label));
        }
    }
}
