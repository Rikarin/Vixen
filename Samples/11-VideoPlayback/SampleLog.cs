// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using Vixen.Graphics;

namespace Vixen.Samples.VideoPlayback;

/// <summary>What the sample logs, with the ids from <c>docs/manual/log-events.md</c>.</summary>
/// <remarks>
///     A sample uses the same generated call sites the engine does. It would be easy to argue that a
///     demo may call <c>LogInformation</c> directly — and then the one place a reader looks to learn
///     how to write against Vixen would be showing them the thing the analyzer forbids everywhere
///     else.
/// </remarks>
static partial class SampleLog {
    [LoggerMessage(
        EventId = 14001,
        Level = LogLevel.Information,
        Message = "Running on {Adapter} ({Kind}), presenting {Format} at {Width}×{Height} "
            + "with {Images} images."
    )]
    public static partial void DeviceReady(
        ILogger logger,
        string adapter,
        AdapterKind kind,
        PixelFormat format,
        int width,
        int height,
        int images
    );

    [LoggerMessage(
        EventId = 14002,
        Level = LogLevel.Error,
        Message = "There is no window to present to."
    )]
    public static partial void NoWindow(ILogger logger);

    [LoggerMessage(
        EventId = 14003,
        Level = LogLevel.Error,
        Message = "The device was lost."
    )]
    public static partial void DeviceLost(ILogger logger);

    [LoggerMessage(
        EventId = 14005,
        Level = LogLevel.Information,
        Message = "Generated {Width}×{Height} at {Rate:F2} Hz, {Duration:F1} s, {Megabytes} MB uncompressed."
    )]
    public static partial void VideoOpened(
        ILogger logger,
        int width,
        int height,
        double rate,
        double duration,
        int megabytes
    );

    [LoggerMessage(
        EventId = 14007,
        Level = LogLevel.Information,
        Message = "Sound on {Device} at {Rate} Hz, {Codec} — the picture follows it."
    )]
    public static partial void AudioReady(ILogger logger, string device, int rate, string codec);

    [LoggerMessage(
        EventId = 14008,
        Level = LogLevel.Information,
        Message = "No sound ({Reason}); the picture runs on the frame delta instead."
    )]
    public static partial void NoAudio(ILogger logger, string reason);

    [LoggerMessage(
        EventId = 14009,
        Level = LogLevel.Information,
        Message = "Reached {Position:F2} s in {Wall:F2} s: {Shown} frame(s) shown, {Dropped} dropped, {Stalls} stall(s); sound {Delivered:F2} s, {StreamUnderruns} stream and {DeviceUnderruns} device underrun(s)."
    )]
    public static partial void PlaybackSummary(
        ILogger logger,
        double position,
        double wall,
        long shown,
        long dropped,
        long stalls,
        double delivered,
        long streamUnderruns,
        long deviceUnderruns
    );

    [LoggerMessage(
        EventId = 14006,
        Level = LogLevel.Information,
        Message = "Bound {Planes} plane(s) of a {Width}×{Height} picture."
    )]
    public static partial void PlanesBound(ILogger logger, int width, int height, int planes);
}
