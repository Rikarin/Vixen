// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;

namespace Vixen.Live.Orchestration;

/// <summary>Everything the orchestrator logs, with the stable ids it logs them under.</summary>
/// <remarks>
///     The same arrangement as <c>HostLog</c>, and here the <em>ids</em> are most of the value: these
///     four lines are the whole of what an operator sees a fleet do, and a number in a log survives
///     the message being reworded. Doc 27 § Diagnostics' fleet view reads the same events.
/// </remarks>
static partial class OrchestratorLog {
    [LoggerMessage(
        EventId = 27001,
        Level = LogLevel.Information,
        Message = "Spawning shard {Shard} for {Map}: {Reason}"
    )]
    public static partial void Spawning(ILogger logger, ShardId shard, ShardKey map, string reason);

    [LoggerMessage(
        EventId = 27002,
        Level = LogLevel.Error,
        Message = "Could not start shard {Shard} for {Map}"
    )]
    public static partial void SpawnFailed(ILogger logger, Exception failure, ShardId shard, ShardKey map);

    [LoggerMessage(EventId = 27003, Level = LogLevel.Information, Message = "Draining shard {Shard}: {Reason}")]
    public static partial void Draining(ILogger logger, ShardId shard, string reason);

    [LoggerMessage(
        EventId = 27004,
        Level = LogLevel.Warning,
        Message = "Shard {Shard} cannot finish draining: {Reason}"
    )]
    public static partial void DrainStuck(ILogger logger, ShardId shard, string reason);

    [LoggerMessage(EventId = 27005, Level = LogLevel.Warning, Message = "Shard {Shard} missed its heartbeats")]
    public static partial void ShardLost(ILogger logger, ShardId shard);
}
