// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Orleans;
using Vixen.Live;
using Vixen.Live.Cluster;

namespace Vixen.Cli;

/// <summary>What `vixen live` asks a running fleet.</summary>
/// <remarks>
///     <para>
///         Doc 27 § Diagnostics wants these operations <i>"in a terminal, because 3 a.m."</i> — the
///         same things the dashboard does, reachable from a machine with no browser and no VPN into
///         whatever hosts the panel.
///     </para>
///     <para>
///         ⚠ <b>A seam, so the verbs are testable without a cluster.</b> The same shape
///         <c>IFleetDirectory</c> takes in the gate and <c>IClusterApi</c> takes in the Kubernetes
///         backend: what is worth asserting is the formatting and the argument handling, and neither
///         needs a silo. Behind it is <see cref="ClusterLiveOperations" /> and a real one.
///     </para>
/// </remarks>
public interface ILiveOperations {
    /// <summary>Every shard of a map.</summary>
    /// <param name="key">Which map, region and version.</param>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>The fleet.</returns>
    Task<IReadOnlyList<ShardReport>> ShardsAsync(ShardKey key, CancellationToken cancellation);

    /// <summary>Tells a shard to move its players out.</summary>
    /// <param name="shard">Which.</param>
    /// <param name="reason">Why, for the log and the fleet view.</param>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>When asked.</returns>
    Task DrainAsync(ShardId shard, string reason, CancellationToken cancellation);

    /// <summary>Why a player went where they went.</summary>
    /// <param name="key">Which map to ask.</param>
    /// <param name="player">Who.</param>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>The account, or a sentence saying nothing is held.</returns>
    Task<string> ExplainAsync(ShardKey key, PlayerKey player, CancellationToken cancellation);

    /// <summary>What a region's fleet is aiming at, and how far it has got.</summary>
    /// <param name="region">Which region.</param>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>The target and the spread.</returns>
    Task<(RealmVersion Target, double Spread)> RolloutAsync(string region, CancellationToken cancellation);

    /// <summary>Points a region's fleet at a version. Rolling back is this with the old pair.</summary>
    /// <param name="region">Which region.</param>
    /// <param name="version">What to aim at.</param>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>When recorded.</returns>
    Task SetTargetAsync(string region, RealmVersion version, CancellationToken cancellation);
}

/// <summary>The real one, over an Orleans cluster client.</summary>
/// <remarks>
///     ⚠ <b><c>Tools/</c> → <c>Live/</c> is deliberately unconstrained</b> — doc 27 § Repository
///     layout says so in as many words, because a CLI that operates the fleet has to link it and
///     pretending otherwise would have made this the exception instead.
/// </remarks>
/// <param name="cluster">The client. Owned by whoever built it.</param>
public sealed class ClusterLiveOperations(IClusterClient cluster) : ILiveOperations {
    readonly IClusterClient cluster = cluster ?? throw new ArgumentNullException(nameof(cluster));

    /// <inheritdoc />
    public async Task<IReadOnlyList<ShardReport>> ShardsAsync(ShardKey key, CancellationToken cancellation) =>
        await cluster.GetGrain<IMapGrain>(Keys.ForMap(key)).Shards().ConfigureAwait(false);

    /// <inheritdoc />
    public Task DrainAsync(ShardId shard, string reason, CancellationToken cancellation) =>
        cluster.GetGrain<IShardGrain>(shard.Value).Drain(reason);

    /// <inheritdoc />
    public Task<string> ExplainAsync(ShardKey key, PlayerKey player, CancellationToken cancellation) =>
        cluster.GetGrain<IMapGrain>(Keys.ForMap(key)).Explain(player);

    /// <inheritdoc />
    public async Task<(RealmVersion Target, double Spread)> RolloutAsync(
        string region,
        CancellationToken cancellation
    ) {
        var fleet = cluster.GetGrain<IFleetGrain>(Keys.ForFleet(region));

        return (await fleet.Target().ConfigureAwait(false), await fleet.VersionSpread().ConfigureAwait(false));
    }

    /// <inheritdoc />
    public Task SetTargetAsync(string region, RealmVersion version, CancellationToken cancellation) =>
        cluster.GetGrain<IFleetGrain>(Keys.ForFleet(region)).SetTarget(version);
}
