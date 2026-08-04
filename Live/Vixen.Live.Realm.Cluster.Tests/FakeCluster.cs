// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Live.Cluster;
using Vixen.Live.Orchestration;

namespace Vixen.Live.Realms.Cluster.Tests;

/// <summary>A cluster with no cluster in it: the real grain logic, reached without a silo.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>What is faked is the network and the scheduler, not the logic on the other side.</b>
///         The <see cref="ShardLifecycle" /> and <see cref="PlayerLeaseState" /> behind these calls
///         are the ones the orchestrator runs; a fake that answered differently would be a test of
///         nothing. That is the whole reason those state machines are plain classes with the grains
///         as adapters.
///     </para>
///     <para>
///         <see cref="Latency" /> is what makes the interesting assertion possible: a call that
///         completes on a later turn is what proves the realm did not wait for it.
///     </para>
/// </remarks>
sealed class FakeCluster : IRealmGrains {
    readonly Dictionary<Guid, FakeShard> shards = [];
    readonly Dictionary<PlayerKey, FakePlayer> players = [];
    readonly List<(PlayerKey Player, ShardId Shard)> departures = [];

    /// <summary>How long every call takes to come back. Zero completes synchronously.</summary>
    public TimeSpan Latency { get; set; }

    /// <summary>Whether the next call throws, which is how a partition is injected.</summary>
    public bool Unreachable { get; set; }

    /// <summary>Everybody the realm has said left.</summary>
    public IReadOnlyList<(PlayerKey Player, ShardId Shard)> Departures => departures;

    /// <summary>How many calls have been made in total.</summary>
    public int Calls { get; private set; }

    /// <inheritdoc />
    public IShardGrain Shard(ShardId shard) {
        if (!shards.TryGetValue(shard.Value, out var entry)) {
            shards[shard.Value] = entry = new(this, new(shard));
        }

        return entry;
    }

    /// <inheritdoc />
    public IMapGrain Map(ShardKey key) => new FakeMap(this, departures);

    /// <inheritdoc />
    public IPlayerGrain Player(PlayerKey player) {
        if (!players.TryGetValue(player, out var entry)) {
            players[player] = entry = new(this, new());
        }

        return entry;
    }

    /// <summary>Puts a shard into a state, as the orchestrator would have.</summary>
    /// <param name="shard">Which shard.</param>
    /// <returns>Its lifecycle, to drive directly.</returns>
    public ShardLifecycle Lifecycle(ShardId shard) {
        Shard(shard);

        return shards[shard.Value].State;
    }

    /// <summary>The lease state for a character, to drive directly.</summary>
    /// <param name="player">Which character.</param>
    /// <returns>Its lease.</returns>
    public PlayerLeaseState Lease(PlayerKey player) {
        Player(player);

        return players[player].Lease;
    }

    async Task<T> Answer<T>(Func<T> call) {
        Calls++;

        if (Latency > TimeSpan.Zero) {
            await Task.Delay(Latency).ConfigureAwait(false);
        }

        if (Unreachable) {
            throw new InvalidOperationException("the cluster is not answering");
        }

        return call();
    }

    sealed class FakeShard(FakeCluster cluster, ShardLifecycle state) : IShardGrain {
        public ShardLifecycle State => state;

        public Task<ShardReport> Report() => cluster.Answer(state.Report);

        public Task Requested(ShardKey key, ShardCapacity capacity) =>
            cluster.Answer<bool>(() => { state.Requested(key, capacity); return true; });

        public Task Starting(RealmInstanceId instance, RealmEndpoint endpoint) =>
            cluster.Answer<bool>(() => { state.Starting(instance, endpoint); return true; });

        public Task Ready(RealmEndpoint endpoint) =>
            cluster.Answer<bool>(() => { state.Ready(endpoint); return true; });

        public Task<ShardState> Heartbeat(ShardHeartbeat sample) => cluster.Answer(() => state.Heartbeat(sample));

        public Task Drain(string reason) => cluster.Answer<bool>(() => { state.Drain(); return true; });

        public Task Stopped() => cluster.Answer<bool>(() => { state.Stopped(); return true; });

        public Task Lost(string detail) => cluster.Answer<bool>(() => { state.Lost(); return true; });
    }

    sealed class FakePlayer(FakeCluster cluster, PlayerLeaseState lease) : IPlayerGrain {
        public PlayerLeaseState Lease => lease;

        public Task<PlayerLease> AcquireLease(ShardId shard) => cluster.Answer(() => lease.Acquire(shard));

        public Task<PlayerLease> RenewLease(ShardId shard, long epoch) =>
            cluster.Answer(() => lease.Renew(shard, epoch));

        public Task ReleaseLease(ShardId shard, long epoch) =>
            cluster.Answer<bool>(() => { lease.Release(shard, epoch); return true; });

        Task<PlayerLease> IPlayerGrain.Lease() => cluster.Answer(lease.Current);

        public Task<ShardId> Where() => cluster.Answer(() => lease.Holder);
    }

    sealed class FakeMap(FakeCluster cluster, List<(PlayerKey, ShardId)> departures) : IMapGrain {
        public Task<PlaceResult> Place(PlaceRequest request) =>
            cluster.Answer(() => new PlaceResult(PlaceStatus.Refused, ShardId.None, RealmEndpoint.None, "faked"));

        public Task<ShardReport[]> Shards() => cluster.Answer(Array.Empty<ShardReport>);

        public Task ShardChanged(ShardReport report) => cluster.Answer(() => true);

        public Task PlayerLeft(PlayerKey player, ShardId shard) =>
            cluster.Answer<bool>(() => { departures.Add((player, shard)); return true; });

        /// <inheritdoc />
        public Task<string> Explain(PlayerKey player) => Task.FromResult($"nothing is held about {player}");

        public Task<string> Tick(DateTimeOffset now) => cluster.Answer(() => "");
    }
}
