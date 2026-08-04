// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Microsoft.Extensions.Logging;
using Vixen.Live.Cluster;

namespace Vixen.Live.Orchestration;

/// <summary>What a map grain needs from the world outside the cluster.</summary>
/// <param name="Placement">How a realm process comes into existence.</param>
/// <param name="Weights">The game's placement weights, or null for doc 27's defaults.</param>
/// <param name="Policy">The fleet's thresholds, or null for doc 27's defaults.</param>
/// <param name="Executable">
///     What a spawned realm runs, threaded into every <see cref="RealmSpec" /> this map produces.
/// </param>
/// <param name="Capacity">How full a shard of this map may get.</param>
/// <param name="TickRate">Its simulation rate.</param>
public sealed record MapOptions(
    IRealmPlacement Placement,
    PlacementWeights? Weights,
    FleetPolicy? Policy,
    string Executable,
    ShardCapacity Capacity,
    int TickRate
) {
    /// <summary>How often a map looks at its own fleet.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A grain timer rather than a service that walks every map, and rather than a
    ///         reminder.</b> A background service ticking every map in a region makes one thread the
    ///         serialisation point for every fleet decision in it, which is the bottleneck the
    ///         per-map keying exists to avoid. A reminder would be the idiomatic answer for work that
    ///         must survive deactivation — and this work must not: a map nobody has asked about for
    ///         hours has no fleet worth observing, and its shards' own idle grace has already retired
    ///         them.
    ///     </para>
    ///     <para>
    ///         Five seconds because the two decisions it drives are debounced in units of twenty
    ///         seconds and two minutes. Ticking faster buys nothing; ticking slower delays a spawn a
    ///         crowd is already waiting on.
    ///     </para>
    /// </remarks>
    public TimeSpan TickInterval { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>One map's shards, hosted. Doc 27 § Placement, § Grains.</summary>
/// <remarks>
///     <para>
///         The adapter over <see cref="MapCoordinator" />: it turns the coordinator's decisions into
///         grain calls and placement-backend calls, and supplies the property the coordinator cannot
///         give itself — that it is never re-entered.
///     </para>
///     <para>
///         ⚠ <b>Two players zoning in at the same instant are two turns of this grain.</b> That is
///         what makes doc 27's twenty-shards failure a scheduling guarantee rather than something the
///         fleet heuristics have to be clever about: the fleet cannot decide twice, in parallel, that
///         it is short of capacity, because there is no parallel.
///     </para>
///     <para>
///         ⚠ <b>Spawning is fire-and-forget from the caller's point of view, and has to be.</b>
///         <c>StartAsync</c> is seconds; a player waiting on it would be a player watching a
///         connection time out. So the placement answer is <see cref="PlaceStatus.Starting" /> and
///         the client asks again — which is also exactly what it will do when the fleet is at its
///         ceiling and nothing is coming.
///     </para>
/// </remarks>
public sealed class MapGrain : Grain, IMapGrain {
    readonly OrchestratorOptions cluster;
    readonly ILogger<MapGrain> log;

    MapCoordinator? map;
    MapOptions? resolved;

    /// <summary>Stands one up.</summary>
    /// <param name="cluster">Every map the orchestrator knows about.</param>
    /// <param name="log">Where decisions go.</param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    /// <remarks>
    ///     ⚠ <b>The whole configuration is injected and the grain looks itself up in it.</b> Orleans
    ///     resolves a grain's dependencies before it knows its key, so a per-map <c>MapOptions</c>
    ///     cannot be injected directly — and the alternative, a static table the grain reads, is what
    ///     makes two clusters in one process impossible. Which is exactly what an integration test is.
    /// </remarks>
    public MapGrain(OrchestratorOptions cluster, ILogger<MapGrain> log) {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(log);

        this.cluster = cluster;
        this.log = log;
    }

    MapCoordinator Map => map ??= new(ParseKey(this.GetPrimaryKeyString()), Settings.Weights, Settings.Policy);

    MapOptions Settings =>
        resolved ??= cluster.Maps.GetValueOrDefault(this.GetPrimaryKeyString())
            ?? cluster.Default
            ?? throw new InvalidOperationException(
                $"No map is configured for `{this.GetPrimaryKeyString()}` and this orchestrator has no "
                + "default. OrchestratorOptions.Maps is what says which maps exist and what a shard of "
                + "each one costs."
            );

    /// <inheritdoc />
    public override Task OnActivateAsync(CancellationToken cancellationToken) {
        // The map observes its own fleet. See MapOptions.TickInterval for why this is a grain timer
        // rather than a reminder or a service that walks every map in the region.
        this.RegisterGrainTimer(
            token => token.IsCancellationRequested ? Task.CompletedTask : Tick(DateTimeOffset.UtcNow),
            Settings.TickInterval,
            Settings.TickInterval
        );

        return base.OnActivateAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<PlaceResult> Place(PlaceRequest request) {
        ArgumentNullException.ThrowIfNull(request);

        var result = Map.Place(request, DateTimeOffset.UtcNow);

        if (result.Status != PlaceStatus.Placed) {
            // Nowhere to go is the fleet's cue, not an error. Asking it now rather than waiting for
            // the next tick is what makes the first player onto an empty map wait seconds instead of
            // a tick interval.
            await Act(Map.Tick(DateTimeOffset.UtcNow)).ConfigureAwait(true);

            return result with { Status = PlaceStatus.Starting };
        }

        return result;
    }

    /// <inheritdoc />
    public Task<ShardReport[]> Shards() => Task.FromResult(Map.Shards.ToArray());

    /// <inheritdoc />
    public Task ShardChanged(ShardReport report) {
        Map.ShardChanged(report);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task PlayerLeft(PlayerKey player, ShardId shard) {
        Map.PlayerLeft(player, shard);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<string> Explain(PlayerKey player) => Task.FromResult(Map.Placements.Explain(player));

    /// <inheritdoc />
    public async Task<string> Tick(DateTimeOffset now) {
        // Silence is the map's to notice. A thousand shard grains each holding a two-second timer is
        // a thousand wake-ups a second to answer a question their map is already asking.
        foreach (var shard in Map.Shards.ToList()) {
            if (now - shard.LastHeartbeat > HealthOptions.Default.Patience
                && shard.State is ShardState.Ready or ShardState.Draining) {
                OrchestratorLog.ShardLost(log, shard.Shard);

                await GrainFactory.GetGrain<IShardGrain>(shard.Shard.Value)
                    .Lost("no heartbeat")
                    .ConfigureAwait(true);

                Map.ShardChanged(shard with { State = ShardState.Lost });
            }
        }

        var action = Map.Tick(now);

        await Act(action).ConfigureAwait(true);

        return action.ToString();
    }

    async Task Act(FleetAction action) {
        switch (action.Kind) {
            case FleetActionKind.Spawn:
                await Spawn(action.Reason).ConfigureAwait(true);

                break;

            case FleetActionKind.Drain:
                OrchestratorLog.Draining(log, action.Shard, action.Reason);

                await GrainFactory.GetGrain<IShardGrain>(action.Shard.Value)
                    .Drain(action.Reason)
                    .ConfigureAwait(true);

                Map.ShardChanged(
                    Map.Shards.Single(shard => shard.Shard == action.Shard) with { State = ShardState.Draining }
                );

                break;

            default:
                break;
        }
    }

    async Task Spawn(string reason) {
        var shard = ShardId.New();
        var grain = GrainFactory.GetGrain<IShardGrain>(shard.Value);

        await grain.Requested(Map.Key, Settings.Capacity).ConfigureAwait(true);

        Map.ShardChanged(await grain.Report().ConfigureAwait(true));

        OrchestratorLog.Spawning(log, shard, Map.Key, reason);

        var spec = new RealmSpec {
            Shard = shard,
            Key = Map.Key,
            Capacity = Settings.Capacity,
            TickRate = Settings.TickRate,
            Options = new Dictionary<string, string>(StringComparer.Ordinal) { ["executable"] = Settings.Executable }
        };

        try {
            var instance = await Settings.Placement.StartAsync(spec, CancellationToken.None).ConfigureAwait(true);

            await grain.Starting(instance.Id, instance.Endpoint).ConfigureAwait(true);

            Map.ShardChanged(await grain.Report().ConfigureAwait(true));
        } catch (Exception failure) when (failure is InvalidOperationException or OperationCanceledException) {
            // A backend that would not start something is a fleet that stays the size it was. The
            // shard is marked failed rather than left Requested for ever, because a Requested shard
            // counts as capacity on its way and would suppress the next spawn indefinitely — a fleet
            // that stopped growing and could not say why.
            OrchestratorLog.SpawnFailed(log, failure, shard, Map.Key);

            await grain.Lost(failure.Message).ConfigureAwait(true);

            Map.ShardChanged(await grain.Report().ConfigureAwait(true));
        }
    }

    /// <summary>Reads a map grain's key back into the shard key it was made from.</summary>
    /// <param name="key">What <c>Keys.ForMap</c> wrote.</param>
    /// <returns>The shard key.</returns>
    /// <exception cref="ArgumentException">It is not one.</exception>
    public static ShardKey ParseKey(string key) {
        ArgumentNullException.ThrowIfNull(key);

        var parts = key.Split('|');

        if (parts.Length != 3 || !RealmVersion.TryParse(parts[2], out var version)) {
            throw new ArgumentException(
                $"`{key}` is not a map grain key. Keys.ForMap is what writes one.",
                nameof(key)
            );
        }

        return new(parts[0], parts[1], version);
    }

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"map {this.GetPrimaryKeyString()}");
}
