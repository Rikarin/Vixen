// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Vixen.App.Tests;

public class AppArgumentsTests {
    /// <summary>
    ///     One reserved prefix, so an application can define whatever arguments it likes without the
    ///     host needing to know about them.
    /// </summary>
    [Fact]
    public void AnythingNotOursComesBackUntouchedAndInOrder() {
        var parsed = AppArguments.Parse(["--level", "3", "--vixen-headless", "save.dat", "-v"]);

        Assert.Equal(["--level", "3", "save.dat", "-v"], parsed.Remaining);
        Assert.True(parsed.Headless);
    }

    [Theory]
    [InlineData("--vixen-workers", "4")]
    [InlineData("--vixen-workers=4", null)]
    public void AValueIsAcceptedSeparatedOrAttached(string first, string? second) {
        var parsed = AppArguments.Parse(second is null ? [first] : [first, second]);

        Assert.Equal(4, parsed.WorkerCount);
        Assert.Empty(parsed.Remaining);
    }

    [Fact]
    public void EveryHostArgumentIsUnderstood() {
        var parsed = AppArguments.Parse([
            "--vixen-variant", "Development",
            "--vixen-video-driver", "x11",
            "--vixen-workers", "2",
            "--vixen-frame-limit", "144",
            "--vixen-log-level", "Debug",
            "--vixen-log-file", "/tmp/logs",
            "--vixen-loose-content", "/tmp/content",
            "--vixen-capture", "/tmp/shots"
        ]);

        Assert.Equal(BuildVariant.Development, parsed.Variant);
        Assert.Equal("x11", parsed.VideoDriver);
        Assert.Equal(2, parsed.WorkerCount);
        Assert.Equal(144, parsed.FrameRateLimit);
        Assert.Equal(LogLevel.Debug, parsed.LogLevel);
        Assert.Equal("/tmp/logs", parsed.LogFilePath);
        Assert.Equal("/tmp/content", parsed.LooseContentPath);
        Assert.Equal("/tmp/shots", parsed.CapturePath);
        Assert.Empty(parsed.Unrecognised);
    }

    /// <summary>
    ///     The capture directory reaches the graphics options, which is what decides both where the
    ///     picture goes and — through <c>GraphicsHost</c> — which backend opens for it.
    /// </summary>
    [Theory]
    [InlineData("--vixen-capture", "shots")]
    [InlineData("--vixen-capture=shots", null)]
    public void ACaptureDirectoryReachesTheGraphicsOptions(string first, string? second) {
        var config = new AppConfig();
        config.Apply(AppArguments.Parse(second is null ? [first] : [first, second]));

        Assert.Equal("shots", config.Graphics.CapturePath);
    }

    /// <summary>
    ///     <c>--vixen-offscreen</c> is the same request without the picture, and reaches the same
    ///     options object.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>It does not imply <c>--vixen-headless</c>, and must not.</b> The two answer different
    ///     questions — which device opens, and whether a display server is used at all — and a run
    ///     under a virtual X server wants the first without the second.
    /// </remarks>
    [Fact]
    public void TheOffscreenFlagReachesTheGraphicsOptionsAndImpliesNoPictureAndNoHeadless() {
        var config = new AppConfig();
        var parsed = AppArguments.Parse(["--vixen-offscreen"]);

        config.Apply(parsed);

        Assert.True(parsed.Offscreen);
        Assert.True(config.Graphics.Offscreen);
        Assert.Null(config.Graphics.CapturePath);
        Assert.False(parsed.Headless);
        Assert.Empty(parsed.Unrecognised);
    }

    /// <summary>
    ///     ⚠ And a capture run does not become one: the two are separate statements, and a run that
    ///     asked for a picture is not thereby claiming it wants no picture.
    /// </summary>
    [Fact]
    public void ACaptureDoesNotSetTheOffscreenFlagItself() {
        var config = new AppConfig();

        config.Apply(AppArguments.Parse(["--vixen-capture", "shots"]));

        Assert.False(config.Graphics.Offscreen);
    }

    /// <summary>
    ///     ⚠ The same one-way stance every flag here takes: <c>Apply</c> runs before
    ///     <c>OnConfigure</c>, so a measurement head that opens a surfaceless device in code must
    ///     not lose it because this run's command line did not repeat the request.
    /// </summary>
    [Fact]
    public void AnAbsentOffscreenFlagDoesNotClearOneAGameSet() {
        var config = new AppConfig { Graphics = { Offscreen = true } };

        config.Apply(AppArguments.Parse(["--vixen-headless"]));

        Assert.True(config.Graphics.Offscreen);
    }

    /// <summary>
    ///     ⚠ The same stance <c>GpuProfiling</c> takes, and for the same reason: <c>Apply</c> runs
    ///     before <c>OnConfigure</c>, so a head that always captures sets the option there and the
    ///     absence of the flag must not undo it.
    /// </summary>
    [Fact]
    public void AnAbsentCaptureFlagDoesNotClearOneAGameSet() {
        var config = new AppConfig { Graphics = { CapturePath = "artifacts/shots" } };
        config.Apply(AppArguments.Parse(["--vixen-headless"]));

        Assert.Equal("artifacts/shots", config.Graphics.CapturePath);
    }

    /// <summary>
    ///     ⚠ <b>Asking for a picture is asking for one that can be compared with another.</b> Two
    ///     headless runs of one build at <c>--vixen-frames 511</c> put sample 13's camera four pixels
    ///     apart, which flipped a quarter of a million pixels — more than two consecutive frames of a
    ///     single run flip — so every per-pixel diff taken that way was measuring the clock.
    /// </summary>
    [Fact]
    public void ACaptureRunGetsAFixedClockWithoutAnybodyTypingOne() {
        var config = new AppConfig();
        config.Apply(AppArguments.Parse(["--vixen-capture", "shots", "--vixen-frames", "8"]));
        config.ImplyCaptureFrameTime();

        Assert.Equal(AppConfig.DefaultCaptureFrameTime, config.FixedFrameTime);
    }

    /// <summary>
    ///     ⚠ After <c>OnConfigure</c>, because a screenshot-tool head asks for its capture directory
    ///     in code and never sees the flag — and wants the same reproducible clock.
    /// </summary>
    [Fact]
    public void AGameThatAsksForACaptureDirectoryGetsTheFixedClockToo() {
        var config = new AppConfig();
        config.Apply(AppArguments.Parse(["--vixen-headless"]));

        Assert.Null(config.FixedFrameTime);

        config.Graphics.CapturePath = "artifacts/shots";
        config.ImplyCaptureFrameTime();

        Assert.Equal(AppConfig.DefaultCaptureFrameTime, config.FixedFrameTime);
    }

    /// <summary>
    ///     Zero is a value and not an absence — the one way to say "I want a wall-clock picture", and
    ///     the reason the implication cannot simply test the property for null.
    /// </summary>
    [Fact]
    public void AnExplicitZeroTakesTheFixedClockBackOffACaptureRun() {
        var config = new AppConfig();
        config.Apply(AppArguments.Parse(["--vixen-capture", "shots", "--vixen-fixed-step", "0"]));
        config.ImplyCaptureFrameTime();

        Assert.Null(config.FixedFrameTime);
    }

    [Theory]
    [InlineData("--vixen-fixed-step", "0.02")]
    [InlineData("--vixen-fixed-step=0.02", null)]
    public void AFixedStepIsReadInSeconds(string first, string? second) {
        var parsed = AppArguments.Parse(second is null ? [first] : [first, second]);

        Assert.Equal(TimeSpan.FromSeconds(0.02), parsed.FixedFrameTime);
        Assert.Empty(parsed.Unrecognised);
    }

    /// <summary>
    ///     Invariant culture, because a launch script written on a machine with a comma decimal
    ///     separator has to mean the same thing on the build agent that runs it. Built by hand rather
    ///     than named, because the test host runs in globalization-invariant mode and
    ///     <c>new CultureInfo("de-DE")</c> throws there.
    /// </summary>
    [Fact]
    public void AFixedStepIsReadTheSameWayInEveryCulture() {
        var comma = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        comma.NumberFormat.NumberDecimalSeparator = ",";

        var previous = CultureInfo.CurrentCulture;

        try {
            CultureInfo.CurrentCulture = comma;

            Assert.Equal(
                TimeSpan.FromSeconds(0.02),
                AppArguments.Parse(["--vixen-fixed-step", "0.02"]).FixedFrameTime
            );
        } finally {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void ANegativeFixedStepIsATypoRatherThanTimeRunningBackwards() {
        var parsed = AppArguments.Parse(["--vixen-fixed-step", "-0.02"]);

        Assert.Null(parsed.FixedFrameTime);
        Assert.Equal(["--vixen-fixed-step"], parsed.Unrecognised);
    }

    /// <summary>
    ///     A typo in a launch script that silently does nothing is how a QA build runs for a week
    ///     without the profiler somebody thought they had switched on.
    /// </summary>
    [Fact]
    public void AMisspeltEngineArgumentIsCollectedRatherThanIgnored() {
        var parsed = AppArguments.Parse(["--vixen-headles", "--vixen-workers", "1"]);

        Assert.Equal(["--vixen-headles"], parsed.Unrecognised);
        Assert.Equal(1, parsed.WorkerCount);
    }

    [Fact]
    public void AValueThatIsNotOneIsNotSilentlyAccepted() {
        var parsed = AppArguments.Parse(["--vixen-variant", "Nonsense"]);

        Assert.Null(parsed.Variant);
        Assert.Equal(["--vixen-variant"], parsed.Unrecognised);
    }

    /// <summary>
    ///     <c>--vixen-workers 0</c> is a supported and tested configuration, not an error: the
    ///     browser without <c>SharedArrayBuffer</c> has no workers and every subsystem has to
    ///     survive it.
    /// </summary>
    [Fact]
    public void ZeroWorkersIsALegitimateRequest() =>
        Assert.Equal(0, AppArguments.Parse(["--vixen-workers", "0"]).WorkerCount);

    [Fact]
    public void NoArgumentsIsNotAnError() {
        Assert.Empty(AppArguments.Parse(null).Remaining);
        Assert.Empty(AppArguments.Parse([]).Remaining);
    }

    /// <summary>
    ///     A flag whose value is missing must not swallow the next flag: <c>--vixen-workers
    ///     --vixen-headless</c> is a mistake, and eating the second one would turn it into a
    ///     different mistake that is much harder to see.
    /// </summary>
    [Fact]
    public void AMissingValueDoesNotConsumeTheNextArgument() {
        var parsed = AppArguments.Parse(["--vixen-workers", "--vixen-headless"]);

        Assert.Null(parsed.WorkerCount);
        Assert.True(parsed.Headless);
        Assert.Equal(["--vixen-workers"], parsed.Unrecognised);
    }
}
