// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Live.Cluster;

/// <summary>One map's shards: who goes where, and when the map grows or shrinks.</summary>
/// <remarks>
///     <para>
///         Keyed by <see cref="Keys.ForMap" /> — the map address, the region and the version pair,
///         because two shards are only interchangeable when all three agree and a grain that spanned
///         versions would be answering for two fleets at once during every rollout.
///     </para>
///     <para>
///         ⚠ <b>Single-threaded by construction, which is the correctness property rather than a
///         performance one.</b> Two players zoning in at the same instant are two turns of the same
///         grain, so the fleet cannot decide twice that it is short of capacity — which is doc 27's
///         named twenty-shards failure expressed as a scheduling guarantee instead of a lock.
///     </para>
/// </remarks>
public interface IMapGrain : IGrainWithStringKey {
    /// <summary>Puts a player somewhere, or says a shard is on its way.</summary>
    /// <param name="request">Who is asking, and for what.</param>
    /// <returns>Where they are going.</returns>
    Task<PlaceResult> Place(PlaceRequest request);

    /// <summary>Every shard of this map, in any state.</summary>
    /// <returns>The fleet.</returns>
    Task<ShardReport[]> Shards();

    /// <summary>Told by a shard when its state changes, so the map does not have to poll.</summary>
    /// <param name="report">What the shard now is.</param>
    /// <returns>When recorded.</returns>
    Task ShardChanged(ShardReport report);

    /// <summary>Told when a player leaves a shard, so the affinity counts stay honest.</summary>
    /// <param name="player">Who left.</param>
    /// <param name="shard">Where from.</param>
    /// <returns>When recorded.</returns>
    Task PlayerLeft(PlayerKey player, ShardId shard);

    /// <summary>Why a player went where they went, if this map still remembers.</summary>
    /// <param name="player">Who is being asked about.</param>
    /// <returns>The account, or a sentence saying nothing is held.</returns>
    /// <remarks>
    ///     ⚠ <b>Asked after the fact, by somebody who was not there.</b> The explanation has always
    ///     existed — <c>PlaceResult.Reason</c> carries it back to whoever asked for the placement —
    ///     but that is the only moment it exists, and § Diagnostics' complaint ("why am I not with my
    ///     guild") arrives hours later from a person who never saw it. What is kept is bounded and
    ///     per map; see <c>PlacementLog</c>.
    /// </remarks>
    Task<string> Explain(PlayerKey player);

    /// <summary>One turn of the spawn and merge heuristics.</summary>
    /// <param name="now">The cluster's clock.</param>
    /// <returns>What the fleet decided, for whoever is acting on it.</returns>
    /// <remarks>
    ///     Driven by a timer in the silo rather than by a player's request: a decision to grow a map
    ///     should not be something one arrival waits for, and the projection needs to run when
    ///     nobody is arriving at all.
    /// </remarks>
    Task<string> Tick(DateTimeOffset now);
}

/// <summary>One realm process, from requested to gone. Doc 27 § Grains' spine.</summary>
/// <remarks>
///     Keyed by the <see cref="ShardId" />. The state machine is
///     <c>Requested → Starting → Ready → Draining → Stopping → Stopped</c>, with <c>Failed</c> and
///     <c>Lost</c> off the side of it, and every transition below is one edge of that.
/// </remarks>
public interface IShardGrain : IGrainWithGuidKey {
    /// <summary>What this shard is.</summary>
    /// <returns>The report.</returns>
    Task<ShardReport> Report();

    /// <summary>A decision that this shard should exist.</summary>
    /// <param name="key">What it is for.</param>
    /// <param name="capacity">How full it may get.</param>
    /// <returns>When recorded.</returns>
    Task Requested(ShardKey key, ShardCapacity capacity);

    /// <summary>The placement backend created something.</summary>
    /// <param name="instance">Its handle.</param>
    /// <param name="endpoint">Where it will be.</param>
    /// <returns>When recorded.</returns>
    Task Starting(RealmInstanceId instance, RealmEndpoint endpoint);

    /// <summary>The realm loaded its map and is accepting sessions.</summary>
    /// <param name="endpoint">Where it actually bound.</param>
    /// <returns>When recorded.</returns>
    /// <remarks>
    ///     The realm's word wins over what it was told to bind. They agree in every ordinary case;
    ///     where they do not, the realm is right, because it is the one holding the socket.
    /// </remarks>
    Task Ready(RealmEndpoint endpoint);

    /// <summary>The two-second sample, and the shard's own state back.</summary>
    /// <param name="sample">What it is costing.</param>
    /// <returns>What the cluster now thinks this shard is.</returns>
    /// <remarks>
    ///     ⚠ <b>The answer is the point.</b> A realm learns that it should be draining by hearing it
    ///     in the reply to a heartbeat it was sending anyway — so the control plane needs no way to
    ///     call <em>into</em> a realm, which is a whole direction of connectivity, authentication and
    ///     firewall rules that does not have to exist.
    /// </remarks>
    Task<ShardState> Heartbeat(ShardHeartbeat sample);

    /// <summary>Stop taking arrivals and move everyone out at safe moments.</summary>
    /// <param name="reason">Why, for the log and the fleet view.</param>
    /// <returns>When recorded.</returns>
    Task Drain(string reason);

    /// <summary>It ended the way it was asked to.</summary>
    /// <returns>When recorded.</returns>
    Task Stopped();

    /// <summary>It went away without being asked.</summary>
    /// <param name="detail">What the backend saw.</param>
    /// <returns>When recorded.</returns>
    /// <remarks>
    ///     Recovery is a placement rather than a resurrection: the shard is gone and its volatile
    ///     state with it, and the players on it are placed again from scratch.
    /// </remarks>
    Task Lost(string detail);
}

/// <summary>One character's durable state, and the lease that says who may write it. ADR-021.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>This grain being single-threaded is the reason item duplication is not expressible.</b>
///         Not a performance property — a correctness one. Two realms cannot both hold epoch <i>n</i>
///         because acquiring is a grain turn, and a grain takes one turn at a time.
///     </para>
///     <para>
///         Keyed by <see cref="Keys.ForPlayer" />, which is account and character: two characters on
///         one account are two sets of inventory, and a lease keyed by account would make playing the
///         second one a duplication bug rather than a Tuesday.
///     </para>
/// </remarks>
public interface IPlayerGrain : IGrainWithStringKey {
    /// <summary>Takes the lease for a shard, superseding whoever held it.</summary>
    /// <param name="shard">Which shard is asking.</param>
    /// <returns>The lease, always granted to the asker — the previous holder's is now dead.</returns>
    /// <remarks>
    ///     ⚠ <b>Acquiring always succeeds, and that is the design.</b> A transfer must be able to
    ///     take the lease from a realm that has crashed, and the cluster cannot tell a crashed realm
    ///     from a slow one. So the epoch moves, the old holder discovers it has lost on its next
    ///     renewal, and every durable write names the epoch it was made under — which makes a late
    ///     write from the old holder a no-op rather than a conflict.
    /// </remarks>
    Task<PlayerLease> AcquireLease(ShardId shard);

    /// <summary>Says the holder is still alive.</summary>
    /// <param name="shard">Which shard claims to hold it.</param>
    /// <param name="epoch">Which epoch it thinks it has.</param>
    /// <returns>The lease. <c>Granted</c> is false when it has been superseded.</returns>
    Task<PlayerLease> RenewLease(ShardId shard, long epoch);

    /// <summary>Gives it back.</summary>
    /// <param name="shard">Which shard held it.</param>
    /// <param name="epoch">Which epoch.</param>
    /// <returns>When released. A stale epoch is ignored rather than refused.</returns>
    Task ReleaseLease(ShardId shard, long epoch);

    /// <summary>Who holds it, without taking it.</summary>
    /// <returns>The lease as it stands.</returns>
    Task<PlayerLease> Lease();

    /// <summary>Which shard this character is on, as far as the cluster knows.</summary>
    /// <returns>The shard, or none.</returns>
    Task<ShardId> Where();
}

/// <summary>A region's whole fleet: what exists, and what a rollout is aiming at.</summary>
/// <remarks>
///     Keyed by the region. The singleton doc 27 § Grains describes, and the thing an escalation
///     reaches when a drain cannot finish — which ends in a human or a maintenance window rather than
///     in a kick.
/// </remarks>
public interface IFleetGrain : IGrainWithStringKey {
    /// <summary>Every shard in the region.</summary>
    /// <returns>The fleet.</returns>
    Task<ShardReport[]> Shards();

    /// <summary>Told by a map grain when one of its shards changes.</summary>
    /// <param name="report">What the shard now is.</param>
    /// <returns>When recorded.</returns>
    Task ShardChanged(ShardReport report);

    /// <summary>A drain that could not finish.</summary>
    /// <param name="shard">Which shard.</param>
    /// <param name="reason">What is holding it.</param>
    /// <returns>When recorded.</returns>
    /// <remarks>
    ///     ⚠ <b>An alert rather than a kill.</b> Doc 27 § Drain: nothing is force-disconnected, and
    ///     the escalation path ends in a person deciding, not in a timeout deciding for them.
    /// </remarks>
    Task Escalate(ShardId shard, string reason);

    /// <summary>The version new shards are started on.</summary>
    /// <returns>The target, or nothing set.</returns>
    Task<RealmVersion> Target();

    /// <summary>Points the fleet at a version. Rolling back is the same call with the old pair.</summary>
    /// <param name="version">What to aim at.</param>
    /// <returns>When recorded.</returns>
    Task SetTarget(RealmVersion version);

    /// <summary>
    ///     The fraction of shards not on <see cref="Target" /> — doc 27 § Upgrades' watched number.
    /// </summary>
    /// <returns>Zero when the rollout is complete.</returns>
    Task<double> VersionSpread();
}

/// <summary>How a grain is addressed. One place, so two callers cannot spell a key differently.</summary>
/// <remarks>
///     ⚠ <b>A grain key is an identity, and two spellings of one identity are two grains.</b> A gate
///     asking for <c>maps/queensdale|eu|0.1.0+c0ffee</c> and an orchestrator asking for
///     <c>maps/queensdale|EU|0.1.0+c0ffee</c> would be two fleets for one map, each unaware of the
///     other — which presents as players who cannot find each other and is diagnosed in an afternoon.
/// </remarks>
public static class Keys {
    /// <summary>The key of the map grain for a shard key.</summary>
    /// <param name="key">The map, region and version.</param>
    /// <returns>The grain key.</returns>
    public static string ForMap(ShardKey key) => $"{key.Map}|{key.Region}|{key.Version}";

    /// <summary>The key of a character's player grain.</summary>
    /// <param name="player">Account and character.</param>
    /// <returns>The grain key.</returns>
    public static string ForPlayer(PlayerKey player) => player.ToString();

    /// <summary>The key of a region's fleet grain.</summary>
    /// <param name="region">The latency zone.</param>
    /// <returns>The grain key.</returns>
    public static string ForFleet(string region) => region ?? "";
}
