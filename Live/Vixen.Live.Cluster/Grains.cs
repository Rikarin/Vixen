// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;

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

/// <summary>One account's own state, which is not one character's. Doc 28 § Collections.</summary>
/// <remarks>
///     <para>
///         <b>Doc 27 § Grains does not have this one, and G8 is what showed it was missing.</b>
///         <see cref="IPlayerGrain" /> is keyed by <em>account and character</em>, and doc 28 says a
///         collection is <em>"account-wide"</em> — a mount earned on one character is owned by all of
///         them. There is no key on <c>IPlayerGrain</c> that can own that, so the alternative to this
///         grain is the same rows written by five characters at once, which is the one thing the
///         single-writer discipline exists to prevent.
///     </para>
///     <para>
///         <b>What stays per character is what the character <em>shows</em>.</b> Doc 28's wardrobe —
///         transmog overrides, hidden slots, the worn title — is per character and rides in
///         <c>PlayerRecord.Profile</c>. That split is the one G8 already built, and it happens to
///         land exactly on the grain boundary.
///     </para>
///     <para>
///         ⚠ <b>It knows nothing about collectibles.</b> The vocabulary is
///         <see cref="AccountUnlock" /> — an address, a source and an order — because that is all doc
///         28's mechanism is, and because a game that declined the gameplay libraries still has
///         accounts. Turning one into a <c>CollectionRecord</c> is <c>Vixen.Live.Gameplay</c>'s.
///     </para>
///     <para>
///         ⚠ <b>No lease.</b> A character's durable state is fenced by ADR-021's lease because two
///         realms can each believe they hold the character. An account is written from wherever its
///         characters happen to be, so the single writer is the grain's own turn and nothing else —
///         which is why <see cref="Unlock" /> is idempotent on the address rather than on an epoch.
///     </para>
/// </remarks>
public interface IAccountGrain : IGrainWithGuidKey {
    /// <summary>Everything this account owns.</summary>
    /// <returns>The collection.</returns>
    Task<AccountHoldings> Holdings();

    /// <summary>Gives the account something.</summary>
    /// <param name="unlock">What, and where it came from. Its <c>Order</c> is assigned here and ignored.</param>
    /// <returns>Whether it was new. False is success — see the remarks on the type.</returns>
    /// <remarks>
    ///     ⚠ <b>Idempotent on the address, so a retry is free and needs no key.</b> Two realms racing
    ///     to grant the same mount to two characters of one account is ordinary, not exceptional, and
    ///     the second must be a no-op rather than a second row.
    /// </remarks>
    Task<bool> Unlock(AccountUnlock unlock);

    /// <summary>Records an achievement and what it is worth.</summary>
    /// <param name="address">Which achievement.</param>
    /// <param name="points">What it is worth.</param>
    /// <returns>Whether it was new.</returns>
    /// <remarks>
    ///     ⚠ <b>Never un-earned, and there is deliberately no method for it.</b> Doc 28's rule: a
    ///     refund, a sale or a patch that raises a threshold must not take back something somebody
    ///     already did.
    /// </remarks>
    Task<bool> Earn(string address, int points);

    /// <summary>Takes something back — a refund, a season ending, a mistake.</summary>
    /// <param name="address">What.</param>
    /// <returns>Whether they had it.</returns>
    /// <remarks>
    ///     Unlocks only. An achievement has no counterpart here on purpose, and doc 28's wardrobe
    ///     re-checks every unlock as it resolves so that a revoked appearance falls back to the real
    ///     item rather than leaving somebody invisible.
    /// </remarks>
    Task<bool> Revoke(string address);
}

/// <summary>One guild's roster and ranks. Doc 27 § Grains, and doc 28 § Social.</summary>
/// <remarks>
///     <para>
///         <b>Left undeclared at L1 on purpose, and this is what was being waited for.</b> Doc 27's
///         reason was that <em>"declaring an interface nobody implements is a promise rather than a
///         contract"</em> — the feature belonged to doc 28 and doc 28 had not built it. G4 did.
///     </para>
///     <para>
///         ⚠ <b>The grain decides ordering; the caller decides permission.</b> A charter's
///         permissions are tags on a compiled <c>GuildCharter</c>, so <em>"may this officer
///         kick"</em> is a content question and the realm answers it with the same code the client
///         greys the button with. What the realm <em>cannot</em> answer is whether the roster still
///         looks that way by the time the write lands, because two officers demoting each other at
///         once is a race no local check can win. So the grain re-checks the one part that is
///         intrinsic — rank is an integer and you may not act on somebody at or above your own — and
///         that needs no content at all.
///     </para>
///     <para>
///         That split is <c>HousePlot</c>'s, one tier up: the ladder comparison is arithmetic and the
///         permissions are tags.
///     </para>
///     <para>
///         ⚠ <b>Rank 0 is the leader and there is always exactly one.</b> Removing the last member is
///         how a guild ends; demoting the only rank-0 member is refused, because a guild with no
///         leader is one nobody can ever administer again.
///     </para>
/// </remarks>
public interface IGuildGrain : IGrainWithGuidKey {
    /// <summary>What the guild looks like.</summary>
    /// <returns>The record, or <see cref="GuildRecord.None" /> if it was never founded.</returns>
    Task<GuildRecord> Read();

    /// <summary>Founds it.</summary>
    /// <param name="founder">Who becomes rank 0.</param>
    /// <param name="charter">Which charter, by address.</param>
    /// <param name="name">What it is called.</param>
    /// <param name="capacity">How many its charter allows. The caller reads that off the compiled charter.</param>
    /// <returns>The outcome.</returns>
    Task<GuildOutcome> Found(PlayerKey founder, string charter, string name, int capacity);

    /// <summary>Adds somebody.</summary>
    /// <param name="by">Who is inviting. The caller has already checked that they may.</param>
    /// <param name="player">Who joins.</param>
    /// <param name="rank">At what rank. Must be below the inviter's own.</param>
    /// <returns>The outcome.</returns>
    Task<GuildOutcome> Add(PlayerKey by, PlayerKey player, int rank);

    /// <summary>Removes somebody, or lets them leave when they are their own actor.</summary>
    /// <param name="by">Who is removing.</param>
    /// <param name="player">Who goes.</param>
    /// <returns>The outcome.</returns>
    Task<GuildOutcome> Remove(PlayerKey by, PlayerKey player);

    /// <summary>Moves somebody up or down.</summary>
    /// <param name="by">Who is promoting.</param>
    /// <param name="player">Who moves.</param>
    /// <param name="rank">To what rank.</param>
    /// <returns>The outcome.</returns>
    /// <remarks>
    ///     ⚠ <b>Promoting somebody to rank 0 hands the guild over</b> — the old leader drops to rank
    ///     1, because two leaders is a state no rule in this interface could resolve afterwards.
    /// </remarks>
    Task<GuildOutcome> SetRank(PlayerKey by, PlayerKey player, int rank);

    /// <summary>Renames a rank, for this guild only.</summary>
    /// <param name="by">Who. Must be rank 0.</param>
    /// <param name="rank">Which rank.</param>
    /// <param name="name">What to call it. Empty puts the charter's own name back.</param>
    /// <returns>The outcome.</returns>
    Task<GuildOutcome> RenameRank(PlayerKey by, int rank, string name);
}

/// <summary>One saved instance: its roster, its lockout and what is dead in it. Doc 27 § Shard kinds.</summary>
/// <remarks>
///     <para>
///         <b>The second of the three doc 27 left undeclared at L1</b>, on the same rule: the feature
///         belonged to doc 28 and doc 28 had not built it. G6 did.
///     </para>
///     <para>
///         ⚠ <b>A lockout is fleet-wide, which is the whole reason it is a grain and not a realm's
///         table.</b> Doc 28 says so directly — <em>"a lockout one shard knew about is a lockout a
///         player evades by zoning"</em>. There is exactly one place that decides whether somebody is
///         saved to this instance, and it is here.
///     </para>
///     <para>
///         ⚠ <b>Progress belongs to the instance and not to each player.</b> Somebody bound late
///         inherits the bosses that are already down, because the alternative is a raid re-killing
///         its first boss for every latecomer — which is both the exploit and the tedium the mechanic
///         exists to prevent.
///     </para>
///     <para>
///         ⚠ <b>Binding cannot be undone, and there is deliberately no method for it.</b> That is
///         what a lockout <em>is</em>: a save you cannot leave. What ends one is the reset, which is
///         an absolute boundary the caller's <c>LockoutPolicy</c> computes and hands over as
///         <c>Expires</c> — never a timer from when somebody entered, or every player's reset drifts
///         to wherever their first run fell.
///     </para>
/// </remarks>
public interface IInstanceGrain : IGrainWithGuidKey {
    /// <summary>What it looks like.</summary>
    /// <returns>The record, or <see cref="InstanceRecord.None" /> if it was never opened.</returns>
    Task<InstanceRecord> Read();

    /// <summary>Opens it.</summary>
    /// <param name="instance">Which instance, by address.</param>
    /// <param name="difficulty">Which difficulty.</param>
    /// <param name="roster">Who may enter. Empty admits anybody, which is what a public dungeon finder wants.</param>
    /// <param name="capacity">How many may be bound.</param>
    /// <param name="now">The clock.</param>
    /// <param name="expires">When the lockout lifts, as the caller's policy computed it.</param>
    /// <returns>The outcome.</returns>
    Task<InstanceOutcome> Open(
        string instance,
        string difficulty,
        ImmutableArray<PlayerKey> roster,
        int capacity,
        DateTimeOffset now,
        DateTimeOffset expires
    );

    /// <summary>Saves somebody to it.</summary>
    /// <param name="player">Who.</param>
    /// <param name="now">The clock, so a lapsed lockout is seen without a timer here.</param>
    /// <returns>The outcome.</returns>
    Task<InstanceOutcome> Bind(PlayerKey player, DateTimeOffset now);

    /// <summary>Records that something is dead.</summary>
    /// <param name="encounter">Which, by address.</param>
    /// <param name="now">The clock.</param>
    /// <returns>The outcome. <see cref="InstanceWrite.Unchanged" /> for one already reported.</returns>
    Task<InstanceOutcome> Defeat(string encounter, DateTimeOffset now);

    /// <summary>Ends it early — a disband, a reset, an operator.</summary>
    /// <returns>The outcome.</returns>
    /// <remarks>
    ///     ⚠ <b>Closing does not release anybody's lockout.</b> The shard goes away; the save does
    ///     not, until its reset. Otherwise disbanding is how a group runs a raid twice.
    /// </remarks>
    Task<InstanceOutcome> Close();
}
