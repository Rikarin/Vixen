// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;

namespace Vixen.Live.Cluster;

/// <summary>What a gate asks for when a player wants to be somewhere.</summary>
/// <remarks>
///     The service plane's half of doc 27 § Placement. The social terms are identities rather than
///     counts — the map grain is what knows who is where, and turning the identities into counts is
///     its job rather than the caller's.
/// </remarks>
/// <param name="Player">Who.</param>
/// <param name="Key">Which map, region and version pair.</param>
/// <param name="Party">Their party, or <see cref="Guid.Empty" />.</param>
/// <param name="Guild">Their guild, or <see cref="Guid.Empty" />.</param>
/// <param name="Locale">Their language tag.</param>
/// <param name="CameFrom">The shard they were just moved off, if any.</param>
[GenerateSerializer]
[Immutable]
public sealed record PlaceRequest(
    [property: Id(0)] PlayerKey Player,
    [property: Id(1)] ShardKey Key,
    [property: Id(2)] Guid Party,
    [property: Id(3)] Guid Guild,
    [property: Id(4)] string Locale,
    [property: Id(5)] ShardId CameFrom
);

/// <summary>Where a player is going, or that they are waiting.</summary>
public enum PlaceStatus : byte {
    /// <summary>There is a shard, and it is ready. <see cref="PlaceResult.Endpoint" /> reaches it.</summary>
    Placed = 0,

    /// <summary>
    ///     A shard is being started for them. Ask again shortly; do not start another.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>An answer rather than an error, and the distinction matters at the gate.</b> A client
    ///     told "starting" shows a short wait; a client told "refused" shows a failure. Conflating
    ///     them is how an elastic fleet's ordinary behaviour becomes a support ticket.
    /// </remarks>
    Starting = 1,

    /// <summary>Nowhere, and not for a reason waiting will fix — see <see cref="PlaceResult.Reason" />.</summary>
    Refused = 2
}

/// <summary>The answer, with the argument for it attached.</summary>
/// <param name="Status">Whether anywhere.</param>
/// <param name="Shard">Which shard, when placed.</param>
/// <param name="Endpoint">Where it is, so the gate needs nothing else to mint a ticket.</param>
/// <param name="Reason">
///     Why, in a sentence — the explanation doc 27 § Diagnostics makes non-optional, flattened so it
///     can cross a grain call and be printed by <c>vixen live explain</c>.
/// </param>
[GenerateSerializer]
[Immutable]
public sealed record PlaceResult(
    [property: Id(0)] PlaceStatus Status,
    [property: Id(1)] ShardId Shard,
    [property: Id(2)] RealmEndpoint Endpoint,
    [property: Id(3)] string Reason
);

/// <summary>What a realm says about itself every two seconds. Doc 27 § Health.</summary>
/// <remarks>
///     Every number here is already an instrument in <c>Vixen.Net.Telemetry</c>; this is a sample of
///     the meter rather than a second measurement system, so a shard's health and its traces cannot
///     disagree about what its tick cost.
/// </remarks>
/// <param name="Population">How many players are on it.</param>
/// <param name="TickP99Milliseconds">The tail, which is the number placement watches.</param>
/// <param name="TickMeanMilliseconds">The middle, which is the number that flatters.</param>
/// <param name="Blocked">How many players a drain could not move.</param>
/// <param name="At">When the sample was taken, by the realm's clock.</param>
[GenerateSerializer]
[Immutable]
public sealed record ShardHeartbeat(
    [property: Id(0)] int Population,
    [property: Id(1)] double TickP99Milliseconds,
    [property: Id(2)] double TickMeanMilliseconds,
    [property: Id(3)] int Blocked,
    [property: Id(4)] DateTimeOffset At
);

/// <summary>A shard as the cluster describes it to anybody who asks.</summary>
/// <param name="Shard">Which shard.</param>
/// <param name="Key">What it is for.</param>
/// <param name="State">Where it is in its life.</param>
/// <param name="Endpoint">Where clients reach it.</param>
/// <param name="Instance">The placement backend's handle, for an operator.</param>
/// <param name="Population">How many are on it, as of its last heartbeat.</param>
/// <param name="Capacity">How many it will take.</param>
/// <param name="StartedAt">When it was requested.</param>
/// <param name="LastHeartbeat">When it last said anything.</param>
[GenerateSerializer]
[Immutable]
public sealed record ShardReport(
    [property: Id(0)] ShardId Shard,
    [property: Id(1)] ShardKey Key,
    [property: Id(2)] ShardState State,
    [property: Id(3)] RealmEndpoint Endpoint,
    [property: Id(4)] RealmInstanceId Instance,
    [property: Id(5)] int Population,
    [property: Id(6)] ShardCapacity Capacity,
    [property: Id(7)] DateTimeOffset StartedAt,
    [property: Id(8)] DateTimeOffset LastHeartbeat
);

/// <summary>Whether a realm may write a player's durable state. ADR-021.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The epoch is the whole mechanism.</b> Two realms cannot both hold epoch <i>n</i>: the
///         second asker gets <i>n+1</i> and the first is told its lease is dead, and a grain taking
///         one turn at a time is what makes that atomic rather than merely likely. Every durable
///         write names its epoch, so a duplicate arriving late is a no-op rather than a second grant.
///     </para>
///     <para>
///         A realm that has lost its lease keeps simulating — doc 27 is explicit that a lease loss
///         mid-combat must be survivable — and buffers its durable mutations until the lease returns
///         or the transfer hands them over.
///     </para>
/// </remarks>
/// <param name="Granted">Whether the asker holds it.</param>
/// <param name="Epoch">Which epoch. Monotonic per player, and never reused.</param>
/// <param name="Holder">Which shard holds it, granted or not.</param>
/// <param name="Expires">When it lapses without a renewal.</param>
[GenerateSerializer]
[Immutable]
public sealed record PlayerLease(
    [property: Id(0)] bool Granted,
    [property: Id(1)] long Epoch,
    [property: Id(2)] ShardId Holder,
    [property: Id(3)] DateTimeOffset Expires
);

/// <summary>One thing an account has, and where it came from.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>An address and a number, and deliberately nothing about pets or mounts.</b> Doc 28
///         § Collections says its whole mechanism is <em>"a set of unlocked <c>DefId</c>s with an
///         unlock source recorded"</em> — so that is exactly what the control plane carries, and
///         <c>IAccountGrain</c> never learns what a collectible is. A game that declined doc 28's
///         libraries still has accounts, and the cluster contract should not make it link them.
///     </para>
///     <para>
///         The address rather than the hash, because a <c>DefId</c> is one-way: support asking "what
///         is <c>0x9A3C1F04</c>" of a durable row has nowhere to look, and the row outlives the build
///         that could have told them.
///     </para>
/// </remarks>
/// <param name="Address">What was unlocked — <c>collect/mount/gryphon</c>.</param>
/// <param name="Source">How they came by it, as doc 28's <c>UnlockSource</c> names it.</param>
/// <param name="From">What exactly — the boss, the quest, the achievement. Empty for nothing in particular.</param>
/// <param name="Order">The nth thing this account unlocked.</param>
[GenerateSerializer]
[Immutable]
public sealed record AccountUnlock(
    [property: Id(0)] string Address,
    [property: Id(1)] string Source,
    [property: Id(2)] string From,
    [property: Id(3)] int Order
);

/// <summary>Everything an account owns, as one answer.</summary>
/// <param name="Unlocks">What it has, in the order it got them.</param>
/// <param name="Achievements">What it has earned, in the order it earned them.</param>
/// <param name="Points">What those achievements are worth.</param>
/// <param name="Revision">How many times this has changed. What an optimistic write checks.</param>
[GenerateSerializer]
[Immutable]
public sealed record AccountHoldings(
    [property: Id(0)] ImmutableArray<AccountUnlock> Unlocks,
    [property: Id(1)] ImmutableArray<string> Achievements,
    [property: Id(2)] int Points,
    [property: Id(3)] uint Revision
) {
    /// <summary>An account that has nothing.</summary>
    public static AccountHoldings Empty { get; } = new([], [], 0, 0);

    /// <summary>Whether two records say the same thing.</summary>
    /// <param name="other">The other one.</param>
    /// <returns>Whether they are equal.</returns>
    /// <remarks>
    ///     ⚠ Hand-written for <see cref="GuildRecord.Equals(GuildRecord)" />'s reason: a record's
    ///     generated equality compares an <see cref="ImmutableArray{T}" /> by reference, so a
    ///     round-tripped account never equals the one it came from.
    /// </remarks>
    public bool Equals(AccountHoldings? other) =>
        other is not null
        && Points == other.Points
        && Revision == other.Revision
        && Unlocks.SequenceEqual(other.Unlocks)
        && Achievements.SequenceEqual(other.Achievements, StringComparer.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Points, Revision, Unlocks.Length, Achievements.Length);
}

/// <summary>Somebody in a guild, and where they stand in it.</summary>
/// <param name="Player">Who.</param>
/// <param name="Rank">Which rank. Zero is the leader; higher is further down.</param>
/// <param name="Joined">When they did.</param>
[GenerateSerializer]
[Immutable]
public sealed record GuildMember(
    [property: Id(0)] PlayerKey Player,
    [property: Id(1)] int Rank,
    [property: Id(2)] DateTimeOffset Joined
);

/// <summary>One guild's durable shape.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The charter's <em>address</em>, never its ranks.</b> A charter is content — its rank
///         list, its permissions and its member cap are a <c>.vxdef</c> — so a guild that stored them
///         would be a guild that kept last patch's rules after the patch. What is genuinely per-guild
///         is the names a leader typed over them, which is <see cref="RankNames" />.
///     </para>
///     <para>
///         ⚠ <b>The bank is a ledger account and not a field.</b> Doc 27 § Persistence's invariant is
///         that every movement of value is a row that sums to zero, and a guild bank held as a number
///         here would be the one balance in the world outside it. It is
///         <c>LedgerAccount.Of("guild/" + id)</c>, so a deposit is an ordinary two-legged transfer and
///         the conservation oracle covers it for free.
///     </para>
/// </remarks>
/// <param name="Charter">Which charter it was founded under.</param>
/// <param name="Name">What it is called.</param>
/// <param name="Members">Who is in it, in join order.</param>
/// <param name="RankNames">What a leader renamed a rank to, by rank index. Absent means the charter's own.</param>
/// <param name="Founded">When.</param>
/// <param name="Revision">How many times it has changed.</param>
[GenerateSerializer]
[Immutable]
public sealed record GuildRecord(
    [property: Id(0)] string Charter,
    [property: Id(1)] string Name,
    [property: Id(2)] ImmutableArray<GuildMember> Members,
    [property: Id(3)] ImmutableDictionary<int, string> RankNames,
    [property: Id(4)] DateTimeOffset Founded,
    [property: Id(5)] uint Revision
) {
    /// <summary>A guild that does not exist yet.</summary>
    public static GuildRecord None { get; } =
        new("", "", [], ImmutableDictionary<int, string>.Empty, default, 0);

    /// <summary>Whether it has been founded.</summary>
    public bool Exists => Charter.Length > 0;

    /// <summary>Whether two records say the same thing.</summary>
    /// <param name="other">The other one.</param>
    /// <returns>Whether they are equal.</returns>
    /// <remarks>
    ///     ⚠ <b>Hand-written, because a record's generated equality compares an
    ///     <see cref="ImmutableArray{T}" /> by <em>reference</em>.</b> Two records holding the same
    ///     members in the same order are otherwise unequal, so a caller asking "has this changed"
    ///     always hears yes and a round trip never matches its source. This is the same trap doc 27
    ///     § Slice two records for <c>RealmEndpoint</c>, found the same way — by a test that restored
    ///     something and compared it.
    /// </remarks>
    public bool Equals(GuildRecord? other) =>
        other is not null
        && string.Equals(Charter, other.Charter, StringComparison.Ordinal)
        && string.Equals(Name, other.Name, StringComparison.Ordinal)
        && Founded == other.Founded
        && Revision == other.Revision
        && Members.SequenceEqual(other.Members)
        && RankNames.Count == other.RankNames.Count
        && RankNames.All(pair => other.RankNames.TryGetValue(pair.Key, out var name)
            && string.Equals(pair.Value, name, StringComparison.Ordinal));

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Charter, Name, Founded, Revision, Members.Length, RankNames.Count);
}

/// <summary>What the grain made of a write.</summary>
/// <remarks>
///     ⚠ <b>Deliberately not <c>Vixen.Gameplay.Social</c>'s <c>GuildRefusal</c>, and not a copy of
///     it.</b> That enum answers <em>"may this player do this"</em>, which needs the compiled charter
///     and is the caller's question. This one answers only what the grain can decide without content:
///     whether the roster still looks the way the caller thought it did. Two enums that meant the
///     same thing would be the drift the three-assembly split exists to prevent; two that mean
///     different things are two questions.
/// </remarks>
public enum GuildWrite {
    /// <summary>It happened.</summary>
    Applied,

    /// <summary>Nothing changed, and nothing was wrong. A member added at the rank they already had.</summary>
    Unchanged,

    /// <summary>There is no guild here, or it has not been founded.</summary>
    NotFound,

    /// <summary>The actor is not in this guild.</summary>
    NotAMember,

    /// <summary>The target is not in this guild.</summary>
    NoSuchMember,

    /// <summary>The actor does not outrank the target, or is reaching above themselves.</summary>
    Outranked,

    /// <summary>It already holds as many as its charter allows.</summary>
    Full,

    /// <summary>It has been founded already.</summary>
    Founded
}

/// <summary>What came of a write, and what the guild looks like now.</summary>
/// <param name="Write">How it was received.</param>
/// <param name="Revision">The revision after it, so a caller can tell a no-op from a change.</param>
[GenerateSerializer]
[Immutable]
public sealed record GuildOutcome([property: Id(0)] GuildWrite Write, [property: Id(1)] uint Revision) {
    /// <summary>Whether the guild is now in the state the caller asked for.</summary>
    public bool Ok => Write is GuildWrite.Applied or GuildWrite.Unchanged;
}

/// <summary>Somebody a saved instance is bound to.</summary>
/// <remarks>
///     ⚠ <b><c>[GenerateSerializer]</c> because it crosses a grain call inside another record, and
///     this file's own warning is what caught it missing:</b> a type added to the vocabulary and not
///     given a codec fails at the first call that carries it, not at compile time.
///     <c>ClusterSerializationTests</c> is the reason that was a red test rather than a support
///     ticket.
/// </remarks>
/// <param name="Player">Who.</param>
/// <param name="Bound">When they were saved to it.</param>
[GenerateSerializer]
[Immutable]
public readonly record struct InstanceBinding(
    [property: Id(0)] PlayerKey Player,
    [property: Id(1)] DateTimeOffset Bound
);

/// <summary>One saved instance: who is bound to it, and what is already dead in it.</summary>
/// <remarks>
///     ⚠ <b>Progress is the <em>instance's</em>, not each player's, and that is what a lockout
///     means.</b> A player bound to it late inherits the bosses that are already down, which is
///     right — the alternative is a raid re-killing its first boss for every latecomer, which is
///     both the exploit and the tedium the mechanic exists to prevent.
/// </remarks>
/// <param name="Instance">Which instance, by address.</param>
/// <param name="Difficulty">Which difficulty. Two of these are two lockouts.</param>
/// <param name="Bindings">Who is saved to it, in binding order.</param>
/// <param name="Defeated">What is already dead, by encounter address.</param>
/// <param name="Opened">When it was opened.</param>
/// <param name="Expires">When the lockout lifts. An absolute boundary, computed by the caller's policy.</param>
/// <param name="Closed">Whether it has ended.</param>
/// <param name="Revision">How many times it has changed.</param>
[GenerateSerializer]
[Immutable]
public sealed record InstanceRecord(
    [property: Id(0)] string Instance,
    [property: Id(1)] string Difficulty,
    [property: Id(2)] ImmutableArray<InstanceBinding> Bindings,
    [property: Id(3)] ImmutableArray<string> Defeated,
    [property: Id(4)] DateTimeOffset Opened,
    [property: Id(5)] DateTimeOffset Expires,
    [property: Id(6)] bool Closed,
    [property: Id(7)] uint Revision
) {
    /// <summary>An instance that has not been opened.</summary>
    public static InstanceRecord None { get; } = new("", "", [], [], default, default, false, 0);

    /// <summary>Whether it has been opened.</summary>
    public bool Exists => Instance.Length > 0;

    /// <summary>Whether two records say the same thing.</summary>
    /// <param name="other">The other one.</param>
    /// <returns>Whether they are equal.</returns>
    /// <remarks>
    ///     ⚠ Hand-written for <see cref="GuildRecord.Equals(GuildRecord)" />'s reason: a record's
    ///     generated equality compares an <see cref="ImmutableArray{T}" /> by reference.
    /// </remarks>
    public bool Equals(InstanceRecord? other) =>
        other is not null
        && string.Equals(Instance, other.Instance, StringComparison.Ordinal)
        && string.Equals(Difficulty, other.Difficulty, StringComparison.Ordinal)
        && Opened == other.Opened
        && Expires == other.Expires
        && Closed == other.Closed
        && Revision == other.Revision
        && Bindings.SequenceEqual(other.Bindings)
        && Defeated.SequenceEqual(other.Defeated, StringComparer.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine(Instance, Difficulty, Opened, Expires, Closed, Revision, Bindings.Length, Defeated.Length);
}

/// <summary>What the grain made of a write to an instance.</summary>
public enum InstanceWrite {
    /// <summary>It happened.</summary>
    Applied,

    /// <summary>Nothing changed, and nothing was wrong. A boss reported dead twice.</summary>
    Unchanged,

    /// <summary>It has not been opened.</summary>
    NotOpen,

    /// <summary>It has been opened already.</summary>
    Open,

    /// <summary>It has ended, or its lockout has lifted.</summary>
    Expired,

    /// <summary>They are not on its access list.</summary>
    NotAdmitted,

    /// <summary>It already holds as many as it allows.</summary>
    Full
}

/// <summary>What came of a write, and what the instance looks like now.</summary>
/// <param name="Write">How it was received.</param>
/// <param name="Revision">The revision after it.</param>
[GenerateSerializer]
[Immutable]
public sealed record InstanceOutcome([property: Id(0)] InstanceWrite Write, [property: Id(1)] uint Revision) {
    /// <summary>Whether the instance is now in the state the caller asked for.</summary>
    public bool Ok => Write is InstanceWrite.Applied or InstanceWrite.Unchanged;
}
