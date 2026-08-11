// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

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
