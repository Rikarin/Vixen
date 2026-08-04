// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

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
