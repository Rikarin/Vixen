// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using Vixen.Live.Cluster;

namespace Vixen.Live.Orchestration;

/// <summary>A region's whole fleet, and what a rollout is aiming at. Doc 27 § Grains, § Upgrades.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>It is a register and an alert channel, not a controller.</b> The decisions about one
///         map belong to that map's grain, which is where the roster and the arrival rate are;
///         collecting them here would make one grain the serialisation point for every placement in a
///         region, which is precisely the bottleneck the per-map keying exists to avoid.
///     </para>
///     <para>
///         What it does own is the version every new shard is started on — because that is a decision
///         about the region rather than about a map — and the escalation that a drain which cannot
///         finish arrives at. Doc 27 § Drain: nothing is force-disconnected, and the escalation ends
///         in a person deciding rather than in a timeout deciding for them.
///     </para>
/// </remarks>
public sealed class FleetGrain : Grain, IFleetGrain {
    readonly Dictionary<ShardId, ShardReport> shards = [];
    readonly ILogger<FleetGrain> log;

    RealmVersion target;

    /// <summary>Stands one up.</summary>
    /// <param name="log">Where escalations go.</param>
    /// <exception cref="ArgumentNullException"><paramref name="log" /> is null.</exception>
    public FleetGrain(ILogger<FleetGrain> log) {
        ArgumentNullException.ThrowIfNull(log);

        this.log = log;
    }

    /// <inheritdoc />
    public Task<ShardReport[]> Shards() => Task.FromResult(shards.Values.ToArray());

    /// <inheritdoc />
    public Task ShardChanged(ShardReport report) {
        ArgumentNullException.ThrowIfNull(report);

        if (report.State is ShardState.Stopped or ShardState.Lost or ShardState.Failed) {
            shards.Remove(report.Shard);
        } else {
            shards[report.Shard] = report;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task Escalate(ShardId shard, string reason) {
        // A log line is the whole of it, and deliberately so. Whatever a deployment wants to happen
        // next — a page, a ticket, a dashboard turning amber — is a thing it already has, and an
        // engine that shipped its own alerting would be one more system to configure and turn off.
        OrchestratorLog.DrainStuck(log, shard, reason);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<RealmVersion> Target() => Task.FromResult(target);

    /// <inheritdoc />
    public Task SetTarget(RealmVersion version) {
        // Rolling back is this call with the old pair. Nothing about the mechanism is directional,
        // which is doc 27 § Upgrades' sixth step and the reason a rollout is not a special mode.
        target = version;

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<double> VersionSpread() {
        if (!target.IsValid || shards.Count == 0) {
            return Task.FromResult(0.0);
        }

        // Weighted by population rather than by shard count: doc 27 § Upgrades watches how many
        // *players* are not on the target, because that is what fragmentation means to the people
        // who cannot find each other. Ten empty old shards are not a problem; one full one is.
        var total = shards.Values.Sum(shard => shard.Population);

        if (total == 0) {
            return Task.FromResult(0.0);
        }

        var stale = shards.Values
            .Where(shard => !target.Admits(shard.Key.Version))
            .Sum(shard => shard.Population);

        return Task.FromResult((double)stale / total);
    }
}
