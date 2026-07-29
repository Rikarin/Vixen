// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;

namespace Vixen.Xr.OpenXR;

/// <summary>What the OpenXR backend logs, with the ids from docs/manual/log-events.md.</summary>
static partial class OpenXrLog {
    [LoggerMessage(
        EventId = 16001,
        Level = LogLevel.Information,
        Message = "OpenXR on {Runtime}: {System}, {Views} view(s) at {Width}×{Height}, {Samples} sample(s)."
    )]
    public static partial void SessionReady(
        ILogger logger,
        string runtime,
        string system,
        int views,
        int width,
        int height,
        int samples
    );

    [LoggerMessage(
        EventId = 16002,
        Level = LogLevel.Information,
        Message = "No OpenXR: {Reason}"
    )]
    public static partial void Unavailable(ILogger logger, string reason);

    [LoggerMessage(
        EventId = 16003,
        Level = LogLevel.Warning,
        Message = "The OpenXR runtime dropped {Events} event(s): the queue overflowed between polls, which "
            + "means a frame took long enough for the runtime to give up on the application hearing about "
            + "a state change."
    )]
    public static partial void EventsLost(ILogger logger, int events);

    [LoggerMessage(
        EventId = 16004,
        Level = LogLevel.Information,
        Message = "The OpenXR session moved to {State}."
    )]
    public static partial void StateChanged(ILogger logger, XrSessionState state);

    [LoggerMessage(
        EventId = 16005,
        Level = LogLevel.Warning,
        Message = "The active interaction profile changed; bindings have been re-resolved by the runtime."
    )]
    public static partial void InteractionProfileChanged(ILogger logger);

    [LoggerMessage(
        EventId = 16006,
        Level = LogLevel.Error,
        Message = "The OpenXR runtime reports the device is being lost. Everything must be recreated."
    )]
    public static partial void InstanceLossPending(ILogger logger);

    [LoggerMessage(
        EventId = 16007,
        Level = LogLevel.Warning,
        Message = "The runtime offers no swapchain format this engine knows; {Format} was requested and "
            + "{Chosen} was taken instead."
    )]
    public static partial void FormatFallback(ILogger logger, string format, string chosen);
}
