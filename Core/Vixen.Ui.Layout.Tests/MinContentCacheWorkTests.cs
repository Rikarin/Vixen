// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Layout.Tests;

/// <summary>What a CLEAN leaf's min-content probe costs, counted in measurements rather than time (#1469).</summary>
/// <remarks>
///     <para>
///         A column given <c>width: 300px</c> in a 300-point root holds four five-word texts, and the
///         second of them has a counting measure function. The first item is then dirtied and the tree
///         laid out again. The sibling did not change, so its §4.5 floor did not either, and the
///         min-content cache exists to make that free.
///     </para>
///     <para>
///         ⚠ <b>The filed reading — "the same question asked three times" — is right about the cost
///         and slightly wrong about the question.</b> The three were three different keys: the
///         sibling's own §4.5 floor under the column's offered height (1000), the root's probe of the
///         column under §5.2.1's zero, and the second flex pass under the column's resolved height
///         (40). A leaf's min-content size reads none of those heights, so all three were one answer,
///         and the one-entry cache, wiped at every key change, measured the text once per change —
///         four measurements on every relayout of a leaf nothing touched.
///     </para>
///     <para>
///         ⚠ <b>Dropping the height from the key closes the filed shape and not the next one.</b> Give
///         the column <c>min-width: 100%</c> instead of a width and the root probes it under its OWN
///         width, which is not a length — §5.2.1's zero — while the item's own floor asks under 300.
///         Those two are genuinely different keys (a percentage edge on the leaf would answer
///         differently), so the cache keeps two entries: each is computed once and both survive.
///     </para>
/// </remarks>
public class MinContentCacheWorkTests {
    const float Word = 10f;
    const int Items = 4;

    sealed class Counter {
        public int Calls;

        public MeasureFunction Words(int words) =>
            (in MeasureRequest request) => {
                Calls++;
                var width = request.WidthMode switch {
                    MeasureMode.Exactly => request.AvailableWidth,
                    MeasureMode.AtMost => MathF.Max(Word, MathF.Min(request.AvailableWidth, words * Word)),
                    _ => words * Word
                };

                var perLine = MathF.Max(1f, MathF.Floor(width / Word));
                var lines = MathF.Ceiling(words / perLine);

                return new LayoutSize(width, request.HeightMode == MeasureMode.Exactly ? request.AvailableHeight : lines * 10f);
            };
    }

    static (LayoutTree Tree, LayoutNodeId Root, LayoutNodeId Column, LayoutNodeId[] Items, Counter Sibling) Build(bool definiteColumn = true) {
        var sibling = new Counter();
        var tree = new LayoutTree();

        var root = tree.CreateNode();
        tree.SetFlexDirection(root, FlexDirection.Column);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(300f));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(1000f));

        var column = tree.CreateNode();
        tree.SetFlexDirection(column, FlexDirection.Column);
        if (definiteColumn) {
            tree.SetDimension(column, Dimension.Width, StyleLength.Points(300f));
        } else {
            tree.SetMinDimension(column, Dimension.Width, StyleLength.Percent(100f));
        }

        tree.AddChild(root, column);

        var items = new LayoutNodeId[Items];
        for (var i = 0; i < Items; i++) {
            items[i] = tree.CreateNode();
            tree.SetMeasureFunction(items[i], i == 1 ? sibling.Words(5) : new Counter().Words(5));
            tree.AddChild(column, items[i]);
        }

        return (tree, root, column, items, sibling);
    }

    static int Layout(LayoutTree tree, LayoutNodeId root, Counter counter) {
        var before = counter.Calls;
        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);
        return counter.Calls - before;
    }

    /// <summary>
    ///     A cold layout measures the sibling once per distinct question — its layout, and one
    ///     min-content probe per axis and percentage basis — and a relayout with only its neighbour
    ///     dirty measures it not at all.
    /// </summary>
    /// <remarks>
    ///     Before the fix, for the definite column: five on the cold layout and four on the relayout.
    ///     The probes past the first in each were the same answer under owner heights the leaf never
    ///     reads. For the <c>min-width: 100%</c> column the height fix alone still measures the clean
    ///     leaf on the relayout, the two percentage bases evicting each other (and five times cold);
    ///     the second entry makes it zero.
    /// </remarks>
    [Theory]
    [InlineData(true, 3)]
    [InlineData(false, 4)]
    public void A_clean_sibling_is_not_measured_again_when_its_neighbour_changes(bool definiteColumn, int coldMeasurements) {
        var (tree, root, column, items, sibling) = Build(definiteColumn);

        Assert.Equal(coldMeasurements, Layout(tree, root, sibling));

        tree.MarkDirty(items[0]);
        Assert.Equal(0, Layout(tree, root, sibling));

        // The instrument: a dirty node IS measured again, so the zero above is a cache hit and not a
        // counter that cannot move.
        tree.MarkDirty(items[1]);
        Assert.True(Layout(tree, root, sibling) > 0);

        Assert.Equal(300f, tree.GetWidth(items[1]));
        Assert.Equal(10f, tree.GetHeight(items[1]));
        Assert.Equal(40f, tree.GetHeight(column));
    }

    /// <summary>
    ///     ⚠ <b>The same layout with the cache keyed on the owner height that it no longer reads gives
    ///     the same geometry</b>, so the narrower key changes cost and not answers: the sibling's
    ///     §4.5 floor under the shared key is the one it would have computed.
    /// </summary>
    [Fact]
    public void A_relayout_from_the_cache_lays_out_as_a_cold_layout_does() {
        var (warm, warmRoot, warmColumn, warmItems, _) = Build();
        warm.CalculateLayout(warmRoot, float.NaN, float.NaN, Direction.Ltr);
        warm.MarkDirty(warmItems[0]);
        warm.CalculateLayout(warmRoot, float.NaN, float.NaN, Direction.Ltr);

        var (cold, coldRoot, coldColumn, coldItems, _) = Build();
        cold.CalculateLayout(coldRoot, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(cold.GetHeight(coldColumn), warm.GetHeight(warmColumn));
        for (var i = 0; i < Items; i++) {
            Assert.Equal(cold.GetTop(coldItems[i]), warm.GetTop(warmItems[i]));
            Assert.Equal(cold.GetHeight(coldItems[i]), warm.GetHeight(warmItems[i]));
        }
    }
}
