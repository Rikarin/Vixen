// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Layout.Tests;

/// <summary>What the #1412 re-entry costs, counted in work rather than time (#1441).</summary>
/// <remarks>
///     <para>
///         A content-sized, non-wrapping column whose <c>min-width</c> is wider than its items lays
///         itself out a second time at the bounded width. Every <c>scroll-content</c> narrower than its
///         viewport is one: <c>align-self: flex-start; min-width: 100%</c>. The fixture is that shape —
///         a column offered 300 with <c>min-width: 100%</c>, holding five-word texts 50 wide — and the
///         oracle is the same tree with the column given <c>width: 300px</c>, which never re-enters
///         and must lay out identically. Every number is a difference against that oracle, measured on
///         the same tree at the same moment: <see cref="LayoutTree.FlexAlgorithmRuns" />,
///         <see cref="LayoutTree.CrossSizeReentries" /> and a counting measure function.
///     </para>
///     <para>
///         ⚠ <b>The review's claim that the second pass "should hit the cache" is half refuted.</b> The
///         re-entry returns before STEP 7, so there are no first-pass stretched layouts for it to
///         reuse; it asks each item one question the first pass never asked — its width EXACTLY 300
///         where the first asked AT MOST 300 — and pays one measurement per item for it on a cold
///         layout. A settled relayout does hit: one dirty item costs one extra measurement, not one per
///         sibling. And nesting does not compound: an inner <c>min-width: 100%</c> column reports the
///         full width, so however deep the stack, two columns re-enter and the extra measurements stay
///         the same.
///     </para>
///     <para>
///         ⚠ <b>The re-entry is not avoidable by deletion.</b> Since #1440 and #1448 none of the four
///         <c>ZeroBasisWrapHeightTests</c> it was written for needs it — they are green with it
///         disabled — so a stretched aspect-ratio item is what pins its purpose now: measured at its
///         content width it is 0 tall, and only laying the column out again at its bounded width gives
///         it the 150 its ratio implies. Avoiding the double pass where the minimum is at least the
///         offer would need the column's min-content before its items are measured, which
///         <c>ComputeMinContentSize</c> answers without gutters — so it was not done.
///     </para>
/// </remarks>
public class CrossSizeReentryWorkTests {
    const float Word = 10f;
    const int Items = 8;

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

    sealed record Fixture(LayoutTree Tree, LayoutNodeId Root, LayoutNodeId[] Items, Counter Counter) {
        public (long Runs, long Reentries, int Calls) Work => (Tree.FlexAlgorithmRuns, Tree.CrossSizeReentries, Counter.Calls);

        public (long Runs, long Reentries, int Calls) Layout() {
            var before = Work;
            Tree.CalculateLayout(Root, float.NaN, float.NaN, Direction.Ltr);
            var after = Work;

            return (after.Runs - before.Runs, after.Reentries - before.Reentries, after.Calls - before.Calls);
        }
    }

    /// <summary>
    ///     <paramref name="depth" /> nested content-sized columns in a 300-point root, the innermost
    ///     holding <see cref="Items" /> five-word texts; each column either has <c>min-width: 100%</c>
    ///     or, for the oracle, <c>width: 300px</c>.
    /// </summary>
    static Fixture Build(bool given, int depth = 1) {
        var counter = new Counter();
        var tree = new LayoutTree();

        var root = tree.CreateNode();
        tree.SetFlexDirection(root, FlexDirection.Column);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(300f));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(1000f));

        var parent = root;
        for (var level = 0; level < depth; level++) {
            var column = tree.CreateNode();
            tree.SetFlexDirection(column, FlexDirection.Column);
            tree.SetAlignSelf(column, Align.FlexStart);
            tree.SetFlexShrink(column, 0f);
            if (given) {
                tree.SetDimension(column, Dimension.Width, StyleLength.Points(300f));
            } else {
                tree.SetMinDimension(column, Dimension.Width, StyleLength.Percent(100f));
            }

            tree.AddChild(parent, column);
            parent = column;
        }

        var nodes = new LayoutNodeId[Items];
        for (var i = 0; i < Items; i++) {
            nodes[i] = tree.CreateNode();
            tree.SetMeasureFunction(nodes[i], counter.Words(5));
            tree.AddChild(parent, nodes[i]);
        }

        return new Fixture(tree, root, nodes, counter);
    }

    /// <summary>
    ///     A cold layout: the column re-enters once in the measuring pass and once in the laying-out
    ///     pass, runs the flex algorithm once more in each, and measures every item once more than the
    ///     oracle — and comes out exactly as the oracle does.
    /// </summary>
    [Fact]
    public void A_column_its_minimum_widens_runs_once_more_per_pass_and_measures_each_item_once_more() {
        var widened = Build(given: false);
        var oracle = Build(given: true);

        var work = widened.Layout();
        var oracleWork = oracle.Layout();

        // The instrument: the oracle never re-enters, and the widened column does.
        Assert.Equal(0, oracleWork.Reentries);
        Assert.Equal(2, work.Reentries);

        Assert.Equal(oracleWork.Runs + 2, work.Runs);
        Assert.Equal(oracleWork.Calls + Items, work.Calls);

        for (var i = 0; i < Items; i++) {
            Assert.Equal(oracle.Tree.GetWidth(oracle.Items[i]), widened.Tree.GetWidth(widened.Items[i]));
            Assert.Equal(oracle.Tree.GetHeight(oracle.Items[i]), widened.Tree.GetHeight(widened.Items[i]));
        }
    }

    /// <summary>
    ///     A settled relayout with one item changed: the re-entry still runs, but the clean siblings'
    ///     answers at the bounded width are cached, so only the changed item pays the extra measurement.
    /// </summary>
    [Fact]
    public void A_settled_relayout_pays_the_extra_measurement_only_for_what_changed() {
        var widened = Build(given: false);
        var oracle = Build(given: true);
        widened.Layout();
        oracle.Layout();

        widened.Tree.MarkDirty(widened.Items[0]);
        oracle.Tree.MarkDirty(oracle.Items[0]);

        var work = widened.Layout();
        var oracleWork = oracle.Layout();

        Assert.Equal(2, work.Reentries);
        Assert.Equal(oracleWork.Runs + 2, work.Runs);
        Assert.Equal(oracleWork.Calls + 1, work.Calls);

        // And a relayout with nothing changed is no work at all, re-entry or not.
        Assert.Equal((0L, 0L, 0), widened.Layout());
    }

    /// <summary>
    ///     ⚠ <b>Nesting does not compound.</b> Each inner <c>min-width: 100%</c> column reports the full
    ///     width, so at any depth the same two columns re-enter, twice each, and the extra measurements
    ///     are the same number at depth two as at depth four.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Nested_minimum_columns_do_not_compound(int depth) {
        var widened = Build(given: false, depth);
        var oracle = Build(given: true, depth);

        var work = widened.Layout();
        var oracleWork = oracle.Layout();

        Assert.Equal(4, work.Reentries);
        Assert.Equal(oracleWork.Calls + (4 * Items), work.Calls);
        Assert.Equal(oracleWork.Runs + depth + 2, work.Runs);
        Assert.Equal(300f, widened.Tree.GetWidth(widened.Items[0]));
    }

    /// <summary>
    ///     ⚠ <b>What the re-entry is still for.</b> A stretched <c>aspect-ratio: 2</c> item with no
    ///     size of its own contributes no width, so the column's content is 0 and its minimum makes it
    ///     300. The item is measured at its content width, 0 wide and so 0 tall; only laying the column
    ///     out again at 300 lets the ratio give it 150, and the column the same. With the re-entry
    ///     disabled both are 0 tall.
    /// </summary>
    [Fact]
    public void The_reentry_is_what_gives_a_stretched_ratio_item_its_height() {
        var tree = new LayoutTree();

        var root = tree.CreateNode();
        tree.SetFlexDirection(root, FlexDirection.Column);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(400f));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(1000f));

        var column = tree.CreateNode();
        tree.SetFlexDirection(column, FlexDirection.Column);
        tree.SetAlignSelf(column, Align.FlexStart);
        tree.SetFlexShrink(column, 0f);
        tree.SetMinDimension(column, Dimension.Width, StyleLength.Points(300f));
        tree.AddChild(root, column);

        var item = tree.CreateNode();
        tree.SetAspectRatio(item, 2f);
        tree.AddChild(column, item);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(300f, tree.GetWidth(item));
        Assert.Equal(150f, tree.GetHeight(item));
        Assert.Equal(300f, tree.GetWidth(column));
        Assert.Equal(150f, tree.GetHeight(column));
    }
}
