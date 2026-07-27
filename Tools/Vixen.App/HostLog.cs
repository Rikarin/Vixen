// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using Vixen.Core.IO;

namespace Vixen.App;

/// <summary>Everything the host logs, with the stable ids it logs them under.</summary>
/// <remarks>
///     Generated call sites rather than <c>logger.LogInformation(…)</c>: the interpolation and the
///     boxing of every argument happen only if the level is enabled, which for a line on a hot path
///     is the difference between free and not. Here it mostly buys the <em>ids</em> — a number in a
///     player's log survives the message being reworded, which is the whole argument for the
///     register in <c>docs/manual/log-events.md</c>.
/// </remarks>
static partial class HostLog {
    [LoggerMessage(
        EventId = 13001,
        Level = LogLevel.Information,
        Message = "Vixen {Variant} on {Platform}, {Workers} workers."
    )]
    public static partial void Started(ILogger logger, BuildVariant variant, string platform, int workers);

    [LoggerMessage(EventId = 13002, Level = LogLevel.Warning, Message = "No window: {Reason}")]
    public static partial void NoWindow(ILogger logger, string reason);

    [LoggerMessage(
        EventId = 13003,
        Level = LogLevel.Warning,
        Message = "LOOSE CONTENT — reading from {Path} instead of bundles."
    )]
    public static partial void LooseContent(ILogger logger, VirtualPath path);

    [LoggerMessage(
        EventId = 13004,
        Level = LogLevel.Warning,
        Message = "Unrecognised engine argument {Argument} — it was ignored."
    )]
    public static partial void UnrecognisedArgument(ILogger logger, string argument);

    [LoggerMessage(EventId = 13005, Level = LogLevel.Information, Message = "Stopping after {Frames} frames.")]
    public static partial void Stopping(ILogger logger, long frames);

    [LoggerMessage(
        EventId = 13006,
        Level = LogLevel.Critical,
        Message = "The frame loop threw and the application is stopping."
    )]
    public static partial void FrameLoopFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 13007,
        Level = LogLevel.Information,
        Message = "Content mounted from {Root}: {Addresses} addresses."
    )]
    public static partial void ContentMounted(ILogger logger, VirtualPath root, int addresses);

    /// <summary>
    ///     Information rather than a warning. An application with nothing to load is ordinary — a
    ///     sample, a batch tool, a test — but "my asset was not found" is a five-second diagnosis
    ///     with this line and an afternoon without it.
    /// </summary>
    [LoggerMessage(EventId = 13008, Level = LogLevel.Information, Message = "No content: {Reason}")]
    public static partial void NoContent(ILogger logger, string reason);

    /// <summary>
    ///     Said again every minute, because doc 17 Q5b's trade is only acceptable while it is
    ///     visible and one line at startup scrolls away.
    /// </summary>
    [LoggerMessage(
        EventId = 13009,
        Level = LogLevel.Warning,
        Message = "LOOSE CONTENT — still reading from {Path} instead of bundles."
    )]
    public static partial void LooseContentStill(ILogger logger, VirtualPath path);
}
