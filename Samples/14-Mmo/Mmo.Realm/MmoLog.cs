// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;

namespace Vixen.Samples.Mmo.Realms;

/// <summary>What the shard logs, with ids from the sample range in <c>docs/manual/log-events.md</c>.</summary>
/// <remarks>
///     ⚠ <b>Not the launcher protocol.</b> <c>RealmHost</c> writes its ready/draining/stopped lines
///     through <c>RealmHostOptions.Output</c> because a placement backend parses them; these are
///     ordinary log records and a launcher must never be made to read one.
/// </remarks>
static partial class MmoLog {
    [LoggerMessage(
        EventId = 14140,
        Level = LogLevel.Information,
        Message = "Composed {Modules} module(s) over {Definitions} definition(s) from {Addresses} address(es); "
        + "{Camps} camp(s) standing."
    )]
    public static partial void Composed(ILogger logger, int modules, int definitions, int addresses, int camps);

    [LoggerMessage(
        EventId = 14141,
        Level = LogLevel.Warning,
        Message = "This build shipped no content mount, so the shard has no definitions and every gameplay "
        + "library is empty. Run the content build."
    )]
    public static partial void NoContent(ILogger logger);

    [LoggerMessage(
        EventId = 14142,
        Level = LogLevel.Warning,
        Message = "Nothing in this build carries the '{Label}' label, so no definition was found. The "
        + "content build ran and the .vxgroup does not label its definitions."
    )]
    public static partial void NoDefinitions(ILogger logger, string label);

    [LoggerMessage(
        EventId = 14143,
        Level = LogLevel.Information,
        Message = "Spawned {Issued} order(s) across {Camps} camp(s); {Alive} alive at t={Seconds:0.0}s."
    )]
    public static partial void Spawned(ILogger logger, long issued, int camps, int alive, double seconds);
}
