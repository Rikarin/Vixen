// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Diagnostics;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Editor.Profiler.Tests;

/// <summary>The chart, against a document that has actually laid itself out.</summary>
/// <remarks>
///     ⚠ <b>Every assertion here needs a real layout pass, and that is not incidental.</b> A bar's
///     width is a fraction of the control's, which is zero until something has measured it — so a
///     test that asserted on <c>Show</c> alone would pass against a chart that draws nothing.
/// </remarks>
public sealed class FlameChartViewTests : IDisposable {
    readonly UiTest test = UiTest.Create();
    readonly FlameChartView chart;

    static readonly ProfilingKey Frame = ProfilingKey.Register("Chart.Frame");
    static readonly ProfilingKey Wide = ProfilingKey.Register("Chart.Wide");
    static readonly ProfilingKey Sliver = ProfilingKey.Register("Chart.Sliver");
    static readonly ProfilingKey Other = ProfilingKey.Register("Chart.Other");

    public FlameChartViewTests() {
        ControlTheme.Install(test.Document);
        AdvancedTheme.Install(test.Document);
        ProfilerTheme.Install(test.Document);

        chart = test.Document.Root.Add<FlameChartView>();
        test.Frames(2);
    }

    static ProfilerSample Sample(ProfilingKey key, int depth, long begin, int duration) =>
        new(key, depth, begin, duration, 0);

    void Show(params ProfilerSample[] samples) {
        chart.Show(FlameNode.Build(samples));
        test.Frames(2);
    }

    [Fact]
    public void ABarIsRealisedPerVisibleScope() {
        Show(Sample(Wide, 1, 100, 400), Sample(Frame, 0, 100, 1000));

        Assert.Equal(2, chart.BarCount);
        Assert.Equal(1, chart.Rows);
    }

    /// <summary>
    ///     ⚠ A sub-pixel bar is dropped with its subtree, because a child is never wider than its
    ///     parent. Ten thousand of them is a subtree the style engine cannot walk in a frame.
    /// </summary>
    [Fact]
    public void SubPixelScopesAreDroppedWithWhateverIsInsideThem() {
        // The sliver is a thousandth of the frame across a 1280-wide document, so about one pixel —
        // and the scope nested inside it is narrower still.
        Show(
            Sample(Sliver, 2, 100, 1),
            Sample(Sliver, 1, 100, 1),
            Sample(Frame, 0, 100, 100_000)
        );

        Assert.Equal(1, chart.BarCount);
    }

    /// <summary>
    ///     Clicks the middle of the nested bar.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>By coordinate rather than by selector, because a bar has no identity a selector
    ///     could name.</b> Every one of them is a <c>flame-bar</c> out of the same pool with a
    ///     colour class hashed from a name, and the row a bar is on is an inline <c>top</c> — so
    ///     "the nested one" is a position and nothing else. Which is also how a user reaches it.
    /// </remarks>
    void ClickNested() {
        // The nested scope covers ticks 500..700 of a window of 100..1100, so it starts 40% across
        // and is a fifth wide; it is at level 1, which is one row down.
        test.At(640f, FlameChartView.RowHeight + 6f).Click();
        test.Frames(2);
    }

    [Fact]
    public void ClickingABarSelectsItAndZoomsToIt() {
        Show(Sample(Wide, 1, 500, 200), Sample(Frame, 0, 100, 1000));

        FlameNode? chosen = null;
        chart.Chosen += (_, node) => chosen = node;

        Assert.False(chart.IsZoomed);

        ClickNested();

        Assert.NotNull(chosen);
        Assert.Equal("Chart.Wide", chosen.Name);
        Assert.Same(chosen, chart.Selected);
        Assert.True(chart.IsZoomed);
    }

    /// <summary>
    ///     Clicking the bar you are already zoomed into is the way back out, so changing your mind
    ///     does not need a button somewhere else on the panel.
    /// </summary>
    [Fact]
    public void ClickingTheSelectedBarAgainZoomsBackOut() {
        Show(Sample(Wide, 1, 500, 200), Sample(Frame, 0, 100, 1000));

        ClickNested();
        Assert.True(chart.IsZoomed);

        // The bar now fills the chart, so the same point is still inside it.
        test.At(640f, FlameChartView.RowHeight + 6f).Click();
        test.Frames(2);

        Assert.False(chart.IsZoomed);
    }

    [Fact]
    public void ResettingPutsTheWholeCaptureBack() {
        Show(Sample(Wide, 1, 500, 200), Sample(Frame, 0, 100, 1000));

        ClickNested();
        Assert.True(chart.IsZoomed);

        chart.Reset();
        test.Frames(2);

        Assert.False(chart.IsZoomed);
    }

    /// <summary>
    ///     ⚠ Hashed on the name rather than the key's id, so a scope keeps its colour between two
    ///     runs of the same program — the only thing colour is for here.
    /// </summary>
    [Fact]
    public void AScopesColourFollowsItsNameAndIsStable() {
        Assert.Equal(FlameChartView.HueOf("Render.Culling"), FlameChartView.HueOf("Render.Culling"));
        Assert.InRange(FlameChartView.HueOf("Render.Culling"), 0, FlameChartView.HueCount - 1);
        Assert.InRange(FlameChartView.HueOf(""), 0, FlameChartView.HueCount - 1);
    }

    /// <summary>
    ///     ⚠ <b>And the hue reaches the bar, which for the whole life of the control it did not.</b>
    /// </summary>
    /// <remarks>
    ///     <see cref="AScopesColourFollowsItsNameAndIsStable" /> asks <c>HueOf</c> what number it
    ///     chose and stops there, so it passed every day the chart drew eight identical grey bars:
    ///     <c>ProfilerTheme</c> declared <c>flame-hue-0 … flame-hue-7</c> as <i>type</i> selectors and
    ///     <c>Place</c> applies them with <c>AddClass</c>, so the eight rules named a tag nothing has
    ///     and the eight classes matched nothing. Nothing could have noticed: an unstyled element
    ///     still renders, and a bar with no fill is a bar. So the assertion is on the resolved
    ///     colour — the only reading that can tell the two apart.
    /// </remarks>
    [Fact]
    public void ABarIsPaintedTheColourItsHueClassDeclares() {
        // ⚠ `Other` rather than `Wide`, and the reason is worth a line: `Chart.Frame`, `Chart.Wide`
        // and `Chart.Sliver` all hash to hue 4, so the pair every other test in this file uses could
        // not have told a working palette from a broken one.
        Show(Sample(Other, 1, 500, 200), Sample(Frame, 0, 100, 1000));

        var colours = test.Get("flame-bar")
            .Elements
            .Select(bar => test.ColorOf(bar, "background-color"))
            .ToList();

        Assert.Equal(2, colours.Count);
        Assert.All(colours, colour => Assert.NotNull(colour));

        // The two scopes hash to different hues, so the chart is doing the one thing colour is here
        // for: saying which bar is which. `HueOf` is asked rather than asserted against a literal —
        // the hash is that test's subject, and this one's is that the class it picks paints.
        Assert.NotEqual(FlameChartView.HueOf("Chart.Frame"), FlameChartView.HueOf("Chart.Other"));
        Assert.NotEqual(colours[0], colours[1]);
    }

    [Fact]
    public void ShowingNothingLeavesNoBars() {
        Show();

        Assert.Equal(0, chart.BarCount);
        Assert.Null(chart.Selected);
    }

    public void Dispose() => test.Dispose();
}
