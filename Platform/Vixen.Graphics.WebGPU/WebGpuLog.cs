// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;

namespace Vixen.Graphics.WebGPU;

/// <summary>What the WebGPU backend logs, with the ids from docs/manual/log-events.md.</summary>
static partial class WebGpuLog {
    /// <summary>The one line worth having in every bug report from this backend.</summary>
    /// <remarks>
    ///     Which adapter, what kind, what the implementation calls itself, and whether there is a
    ///     window. On the web three of those four are usually "unknown" — a browser will not name the
    ///     GPU — and knowing that they are unknown rather than unlogged is itself the useful part.
    /// </remarks>
    [LoggerMessage(
        EventId = 2100,
        Level = LogLevel.Information,
        Message = "WebGPU device created on '{Adapter}' ({Kind}, {Driver}), {Mode}."
    )]
    public static partial void DeviceCreated(
        ILogger logger,
        string adapter,
        string kind,
        string driver,
        string mode
    );

    [LoggerMessage(
        EventId = 2101,
        Level = LogLevel.Warning,
        Message = "WebGPU reported an error the backend could not attribute to a call: {Message}"
    )]
    public static partial void UncapturedError(ILogger logger, string message);

    /// <summary>
    ///     A wait that could not be waited.
    /// </summary>
    /// <remarks>
    ///     A browser tab has one thread and it is the one that would have to run the callback, so
    ///     blocking on the queue there is a deadlock rather than a wait. Logged rather than thrown:
    ///     the calls that reach here are shutdown and swapchain recreation, both of which are correct
    ///     without the wait on WebGPU — the implementation will not destroy anything work still names.
    /// </remarks>
    [LoggerMessage(
        EventId = 2102,
        Level = LogLevel.Debug,
        Message = "WaitIdle did nothing: this WebGPU surface cannot block on the queue."
    )]
    public static partial void CannotWaitIdle(ILogger logger);

    [LoggerMessage(
        EventId = 2103,
        Level = LogLevel.Warning,
        Message = "wgpu-native or Dawn could not be loaded ({Reason}), so the WebGPU backend reports "
            + "itself unavailable and selection moves on."
    )]
    public static partial void LibraryMissing(ILogger logger, string reason);

    [LoggerMessage(
        EventId = 2104,
        Level = LogLevel.Warning,
        Message = "WebGPU device lost ({Reason}). Everything has to be recreated."
    )]
    public static partial void DeviceLost(ILogger logger, string reason);
}
