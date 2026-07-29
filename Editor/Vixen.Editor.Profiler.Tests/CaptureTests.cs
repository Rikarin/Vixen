// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Diagnostics;
using Xunit;

namespace Vixen.Editor.Profiler.Tests;

/// <summary>What a capture holds, what a summary adds up to, and what two captures subtract to.</summary>
public sealed class CaptureTests {
    static readonly ProfilingKey Frame = ProfilingKey.Register("Capture.Frame");
    static readonly ProfilingKey Cull = ProfilingKey.Register("Capture.Cull");
    static readonly ProfilingKey Draw = ProfilingKey.Register("Capture.Draw");

    static ProfilerThreadSamples Thread(string name, params ProfilerSample[] samples) =>
        new(name.GetHashCode(StringComparison.Ordinal), name, samples);

    static ProfilerSample Sample(ProfilingKey key, int depth, long begin, int duration, int frame = 0) =>
        new(key, depth, begin, duration, frame);

    static ProfileCapture OneFrame(int duration = 100) =>
        new(
            "Test",
            [
                Thread(
                    "Main",
                    Sample(Cull, 1, 110, 30),
                    Sample(Draw, 1, 150, 40),
                    Sample(Frame, 0, 100, duration)
                )
            ]
        );

    [Fact]
    public void TheWindowComesFromTheSamplesRatherThanAClock() {
        var capture = OneFrame();

        Assert.Equal(100, capture.BeginTicks);
        Assert.Equal(200, capture.EndTicks);
        Assert.Equal(0f, capture.Fraction(100));
        Assert.Equal(1f, capture.Fraction(200));
        Assert.Equal(0.5f, capture.Fraction(150), 3);
    }

    /// <summary>
    ///     ⚠ A thread that recorded nothing would otherwise be a blank lane. A pool of sixteen
    ///     workers where two did the work is the case this is about.
    /// </summary>
    [Fact]
    public void ThreadsThatRecordedNothingAreLeftOut() {
        ProfileCapture capture = new(
            "Test",
            [Thread("Main", Sample(Frame, 0, 100, 100)), Thread("Worker 3")]
        );

        var thread = Assert.Single(capture.Threads);
        Assert.Equal("Main", thread.ThreadName);
    }

    /// <summary>
    ///     ⚠ Busiest first, measured on the roots. Summing every sample would rank the most deeply
    ///     instrumented thread above the busy one, because a nested microsecond is counted once per
    ///     level.
    /// </summary>
    [Fact]
    public void ThreadsAreOrderedByHowBusyTheyWere() {
        ProfileCapture capture = new(
            "Test",
            [
                Thread("Quiet", Sample(Frame, 0, 100, 10)),
                Thread("Busy", Sample(Frame, 0, 100, 500))
            ]
        );

        Assert.Equal("Busy", capture.Threads[0].ThreadName);
        Assert.Equal("Quiet", capture.Threads[1].ThreadName);
    }

    [Fact]
    public void TheSummaryAggregatesEveryScopeByKey() {
        ProfileCapture capture = new(
            "Test",
            [
                Thread(
                    "Main",
                    Sample(Cull, 1, 110, 30),
                    Sample(Frame, 0, 100, 100),
                    Sample(Cull, 1, 310, 50),
                    Sample(Frame, 0, 300, 100, frame: 1)
                )
            ]
        );

        var cull = Assert.Single(capture.Summary, entry => entry.Key == Cull);

        Assert.Equal(2, cull.Calls);
        Assert.True(cull.MaximumMilliseconds > cull.MeanMilliseconds);
        Assert.Equal(cull.TotalMilliseconds / 2d, cull.MeanMilliseconds, 6);
    }

    /// <summary>Most expensive first, which is the order somebody opens the table to read.</summary>
    [Fact]
    public void TheSummaryIsSortedByTotalTime() {
        var summary = OneFrame().Summary;

        for (var index = 1; index < summary.Count; index++) {
            Assert.True(summary[index - 1].TotalMilliseconds >= summary[index].TotalMilliseconds);
        }
    }

    /// <summary>
    ///     ⚠ Per frame, because two captures are almost never the same length and comparing totals
    ///     says the shorter run is faster.
    /// </summary>
    [Fact]
    public void ComparisonNormalisesByFrameCount() {
        ProfileCapture before = new(
            "Before",
            [Thread("Main", Sample(Frame, 0, 100, 100), Sample(Frame, 0, 300, 100, frame: 1))]
        );

        ProfileCapture after = new("After", [Thread("Main", Sample(Frame, 0, 100, 100))]);

        var delta = Assert.Single(CaptureComparison.Compare(before, after), entry => entry.Key == Frame);

        // Both are 100 ticks per frame, so a comparison that ignored the frame count would report the
        // two-frame capture as twice as slow.
        Assert.Equal(0d, delta.TotalDelta, 6);
        Assert.Equal(0d, delta.Ratio ?? double.NaN, 6);
    }

    /// <summary>
    ///     ⚠ Null rather than infinity, so a newly-instrumented scope does not sort above every real
    ///     regression.
    /// </summary>
    [Fact]
    public void AScopeWithNoBaselineHasNoRatio() {
        ProfileCapture before = new("Before", [Thread("Main", Sample(Frame, 0, 100, 100))]);

        ProfileCapture after = new(
            "After",
            [Thread("Main", Sample(Draw, 1, 110, 40), Sample(Frame, 0, 100, 100))]
        );

        var delta = Assert.Single(CaptureComparison.Compare(before, after), entry => entry.Key == Draw);

        Assert.Null(delta.Ratio);
        Assert.Null(delta.Before);
        Assert.True(delta.TotalDelta > 0d);
    }

    [Fact]
    public void AScopeThatWentAwayIsStillReported() {
        ProfileCapture before = new(
            "Before",
            [Thread("Main", Sample(Draw, 1, 110, 40), Sample(Frame, 0, 100, 100))]
        );

        ProfileCapture after = new("After", [Thread("Main", Sample(Frame, 0, 100, 100))]);

        var delta = Assert.Single(CaptureComparison.Compare(before, after), entry => entry.Key == Draw);

        Assert.Null(delta.After);
        Assert.True(delta.TotalDelta < 0d);
    }

    /// <summary>Regressions first, improvements last.</summary>
    [Fact]
    public void ComparisonPutsTheWorstRegressionFirst() {
        ProfileCapture before = new(
            "Before",
            [Thread("Main", Sample(Cull, 1, 110, 90), Sample(Draw, 1, 210, 10), Sample(Frame, 0, 100, 200))]
        );

        ProfileCapture after = new(
            "After",
            [Thread("Main", Sample(Cull, 1, 110, 10), Sample(Draw, 1, 210, 90), Sample(Frame, 0, 100, 200))]
        );

        var deltas = CaptureComparison.Compare(before, after);

        Assert.Equal(Draw, deltas[0].Key);
        Assert.Equal(Cull, deltas[^1].Key);
    }

    [Fact]
    public void AFrameFilterKeepsOnlyThatFramesRoots() {
        ProfileCapture capture = new(
            "Test",
            [Thread("Main", Sample(Frame, 0, 100, 50), Sample(Frame, 0, 200, 50, frame: 7))]
        );

        var thread = Assert.Single(capture.Threads);

        Assert.Single(ProfileCapture.Frame(thread, 7));
        Assert.Empty(ProfileCapture.Frame(thread, 3));
        Assert.Equal(0, capture.FirstFrame);
        Assert.Equal(7, capture.LastFrame);
        Assert.Equal(8, capture.FrameCount);
    }

    [Fact]
    public void AnEmptyCaptureHasAWindowOfZeroAndDoesNotDivideByIt() {
        Assert.True(ProfileCapture.Empty.IsEmpty);
        Assert.Equal(0f, ProfileCapture.Empty.Fraction(500));
        Assert.Equal(1, ProfileCapture.Empty.FrameCount);
        Assert.Equal(0d, ProfileCapture.Empty.DurationMilliseconds);
    }
}
