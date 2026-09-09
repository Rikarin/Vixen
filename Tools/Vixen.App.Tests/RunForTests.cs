// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Testing;
using Xunit;

namespace Vixen.App.Tests;

/// <summary>
///     <c>--vixen-run-for</c>: the duration form of <c>--vixen-frames</c>, and what
///     <c>vixen trace record --duration 10s</c> is built on.
/// </summary>
/// <remarks>
///     ⚠ <b>The stop is measured on the frame clock, which is the only thing that makes it
///     assertable.</b> A wall-clock budget is this repository's largest flake source and fifteen
///     agents share this machine; under <c>--vixen-fixed-step</c> — which <see cref="TestApp" />
///     always passes — frame <i>N</i> is the same instant of simulated time whatever the box is
///     doing, so "it stopped within one frame of the mark" is a property of the code rather than of
///     the load.
/// </remarks>
public sealed class RunForTests {
    /// <summary>A game that does nothing, so the only thing moving is the clock.</summary>
    sealed class Silent : Game {
        protected internal override void OnConfigure(AppConfig config) {
            config.Name = "RunFor";
            config.Window = null;
        }
    }

    /// <summary>The duration reaches the configuration, in seconds.</summary>
    [Fact]
    public void TheFlagIsReadInSeconds() =>
        Assert.Equal(TimeSpan.FromSeconds(2.5), AppArguments.Parse(["--vixen-run-for", "2.5"]).RunFor);

    /// <summary>
    ///     Zero, a negative and a word are refused rather than taken, and end up where every other
    ///     unusable argument does.
    /// </summary>
    /// <remarks>
    ///     ⚠ Zero especially: taken literally it stops the loop before its first frame, so the trace
    ///     the flag exists to produce would be a well-formed document with no events in it — which
    ///     reads as a game that did nothing rather than as a command line that asked for nothing.
    /// </remarks>
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("soon")]
    public void ADurationThatCannotBeRunIsRefused(string value) {
        var parsed = AppArguments.Parse(["--vixen-run-for", value]);

        Assert.Null(parsed.RunFor);
        Assert.Contains("--vixen-run-for", parsed.Unrecognised);
    }

    /// <summary>The loop stops within one frame of the duration, and not before it.</summary>
    /// <remarks>
    ///     Both halves, because either alone is satisfiable by something broken: a run that never
    ///     stopped would pass the first, and a run that stopped at frame one would pass the second.
    ///     The ceiling on the loop is a hang check and not a bound — three frames are expected.
    /// </remarks>
    [Fact]
    public void TheLoopStopsWithinOneFrameOfTheDuration() {
        var duration = TimeSpan.FromSeconds(0.05);

        using var app = TestApp.Create(new Silent(), "--vixen-run-for", "0.05");

        // One through the harness, which initialises and complains if the frame did not simulate.
        app.RunFrames(1);

        var frames = 1;

        while (!app.Application.IsStopping && frames < 10_000) {
            app.Application.RunFrame();
            frames++;
        }

        Assert.True(frames < 10_000, "the application never stopped, so --vixen-run-for did nothing");

        Assert.True(
            app.Time.Total >= duration,
            $"the loop stopped at {app.Time.Total} of a {duration} run, which is short of the mark"
        );

        Assert.True(
            app.Time.Total - app.Step < duration,
            $"the loop reached {app.Time.Total} of a {duration} run, which is a whole frame past the "
            + "mark — the condition is being read a frame late, or not at all"
        );
    }

    /// <summary>Without the flag the same game runs as long as it is asked to.</summary>
    /// <remarks>
    ///     The control. Without it the assertion above is satisfied by an application that stops for
    ///     any reason at all — a closed window, a lifecycle quit — and says nothing about the flag.
    /// </remarks>
    [Fact]
    public void WithoutTheFlagNothingStopsTheLoop() {
        using var app = TestApp.Create(new Silent());

        app.RunFrames(8);

        Assert.False(app.Application.IsStopping);
        Assert.Equal(8, app.Time.FrameCount);
    }
}
