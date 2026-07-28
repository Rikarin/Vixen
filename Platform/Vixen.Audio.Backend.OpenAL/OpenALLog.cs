// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;

namespace Vixen.Audio.Backend.OpenAL;

/// <summary>What the OpenAL backend logs, with the ids from docs/manual/log-events.md.</summary>
static partial class OpenALLog {
    [LoggerMessage(
        EventId = 9110,
        Level = LogLevel.Warning,
        Message = "OpenAL could not be loaded ({Reason}). The backend reports itself unavailable and "
            + "selection moves on; a published build carries OpenAL Soft in runtimes/<rid>/native, so "
            + "this in a shipped game means the layout is wrong rather than the machine is."
    )]
    public static partial void LibraryMissing(ILogger logger, string reason);

    [LoggerMessage(
        EventId = 9111,
        Level = LogLevel.Error,
        Message = "The OpenAL pump thread threw and has stopped; the process keeps running and is silent."
    )]
    public static partial void PumpFailed(ILogger logger, Exception exception);
}
