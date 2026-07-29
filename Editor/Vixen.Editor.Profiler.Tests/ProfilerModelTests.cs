// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Diagnostics;
using Xunit;

namespace Vixen.Editor.Profiler.Tests;

/// <summary>
///     What the panel does between Record and Stop, against a source that hands over exactly what a
///     test says.
/// </summary>
/// <remarks>
///     ⚠ <b>Not <see cref="LocalProfileSource" />, deliberately.</b> That one reads the process's
///     static rings, which every other test in the run is also writing into — a model test against
///     it would be a test whose expected sample count depends on what xunit scheduled beside it.
/// </remarks>
public sealed class ProfilerModelTests {
    static readonly ProfilingKey Frame = ProfilingKey.Register("Model.Frame");

    static ProfilerSample Sample(long begin, int duration, int frame = 0) => new(Frame, 0, begin, duration, frame);

    [Fact]
    public void TheFirstSourceAddedIsTheSelection() {
        ProfilerModel model = new();
        BufferedProfileSource editor = new("Editor");

        model.Add(editor);
        model.Add(new BufferedProfileSource("Game"));

        Assert.Same(editor, model.Selected);
    }

    /// <summary>
    ///     ⚠ Samples are accumulated across every tick, because <c>Collect</c> empties the rings —
    ///     a model that rebuilt the capture per tick would show the last few milliseconds only.
    /// </summary>
    [Fact]
    public void ARecordingAccumulatesAcrossTicks() {
        ProfilerModel model = new();
        BufferedProfileSource source = new("Editor");

        model.Add(source);
        model.Start();

        source.Offer(new(1, "Main", [Sample(100, 50)]));
        model.Tick();

        source.Offer(new(1, "Main", [Sample(200, 50, frame: 1)]));
        model.Tick();

        source.Offer(new(1, "Main", [Sample(300, 50, frame: 2)]));
        model.Stop();

        Assert.Equal(ProfilerState.Idle, model.State);
        Assert.Equal(3, model.Capture.SampleCount);

        // ⚠ One lane, not three. Every tick produced its own `ProfilerThreadSamples` for the same
        // thread, and left as they came the chart would draw a fragment of the frame per lane.
        Assert.Single(model.Capture.Threads);
        Assert.Equal(3, model.Capture.FrameCount);
    }

    /// <summary>
    ///     ⚠ The rings are emptied by <c>Start</c>, so a capture holds what happened after the press
    ///     rather than that plus whatever history was lying about.
    /// </summary>
    [Fact]
    public void StartingDiscardsWhateverWasAlreadyBuffered() {
        ProfilerModel model = new();
        BufferedProfileSource source = new("Editor");

        model.Add(source);
        source.Offer(new(1, "Main", [Sample(1, 10)]));

        model.Start();
        source.Offer(new(1, "Main", [Sample(100, 50)]));
        model.Stop();

        Assert.Equal(1, model.Capture.SampleCount);
        Assert.Equal(100, model.Capture.BeginTicks);
    }

    [Fact]
    public void TheDroppedCountIsWhatWasLostDuringTheCaptureOnly() {
        ProfilerModel model = new();
        BufferedProfileSource source = new("Editor");

        model.Add(source);
        source.Offer(new(1, "Main", [Sample(1, 10)]), dropped: 500);

        model.Start();
        source.Offer(new(1, "Main", [Sample(100, 50)]), dropped: 7);
        model.Stop();

        Assert.Equal(7, model.Capture.Dropped);
    }

    /// <summary>
    ///     ⚠ Changing source mid-capture finishes it rather than merging two processes' samples into
    ///     one chart, which would be a picture nobody could interpret and which would not announce
    ///     itself.
    /// </summary>
    [Fact]
    public void ChangingTheSourceStopsTheCaptureInProgress() {
        ProfilerModel model = new();
        BufferedProfileSource editor = new("Editor");
        BufferedProfileSource game = new("Game");

        model.Add(editor);
        model.Add(game);
        model.Start();

        editor.Offer(new(1, "Main", [Sample(100, 50)]));
        model.Selected = game;

        Assert.Equal(ProfilerState.Idle, model.State);
        Assert.Equal("Editor", model.Capture.Source);
        Assert.Equal(1, model.Capture.SampleCount);
    }

    [Fact]
    public void MarkingABaselineProducesDeltasAndClearingItRemovesThem() {
        ProfilerModel model = new();
        BufferedProfileSource source = new("Editor");

        model.Add(source);

        model.Start();
        source.Offer(new(1, "Main", [Sample(100, 50)]));
        model.Stop();

        model.MarkBaseline();
        Assert.NotNull(model.Baseline);

        model.Start();
        source.Offer(new(1, "Main", [Sample(100, 90)]));
        model.Stop();

        var delta = Assert.Single(model.Deltas);
        Assert.True(delta.TotalDelta > 0d);

        model.ClearBaseline();
        Assert.Empty(model.Deltas);
    }

    [Fact]
    public void ABaselineIsNotSetFromAnEmptyCapture() {
        ProfilerModel model = new();
        model.Add(new BufferedProfileSource("Editor"));

        model.MarkBaseline();

        Assert.Null(model.Baseline);
    }

    /// <summary>The frame filter is what the chart draws, and "all frames" is the default.</summary>
    [Fact]
    public void RootsFollowTheFrameFilter() {
        ProfilerModel model = new();
        BufferedProfileSource source = new("Editor");

        model.Add(source);
        model.Start();
        source.Offer(new(1, "Main", [Sample(100, 50), Sample(200, 50, frame: 4)]));
        model.Stop();

        Assert.Null(model.Frame);
        Assert.Equal(2, model.Roots.Count);

        model.Frame = 4;
        Assert.Single(model.Roots);
    }

    [Fact]
    public void RemovingTheSelectedSourceFallsBackToAnother() {
        ProfilerModel model = new();
        BufferedProfileSource editor = new("Editor");
        BufferedProfileSource game = new("Game");

        model.Add(editor);
        model.Add(game);

        Assert.True(model.Remove(editor));
        Assert.Same(game, model.Selected);

        Assert.True(model.Remove(game));
        Assert.Null(model.Selected);
    }

    [Fact]
    public void StoppingWithoutStartingDoesNothing() {
        ProfilerModel model = new();
        model.Add(new BufferedProfileSource("Editor"));

        model.Stop();

        Assert.True(model.Capture.IsEmpty);
        Assert.Equal(ProfilerState.Idle, model.State);
    }
}
