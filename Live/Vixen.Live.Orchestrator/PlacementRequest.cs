// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Live.Orchestration;

/// <summary>Somebody asking to be put somewhere. Doc 27 § Placement.</summary>
/// <remarks>
///     <para>
///         Everything the megaserver weighs, and nothing it does not. The three social terms arrive
///         as identities rather than as counts because it is the <em>candidate</em> that knows how
///         many of them are already on it — see <see cref="ShardCandidate" /> — and the split is what
///         keeps <see cref="PlacementDirector" /> a pure function of numbers.
///     </para>
///     <para>
///         ⚠ <b><see cref="CameFrom" /> is what makes a transfer stick.</b> A player who has just been
///         moved off a shard scores −5 000 against going back to it, which is more than a guild and
///         every friend they have put together. Without it a drain and a placement disagree politely
///         with each other for as long as the player is patient.
///     </para>
/// </remarks>
public sealed record PlacementRequest {
    /// <summary>Who is asking.</summary>
    public PlayerKey Player { get; init; }

    /// <summary>The map, region and version they may be placed on. Every hard filter is this value.</summary>
    public ShardKey Key { get; init; }

    /// <summary>Their party, or <see cref="Guid.Empty" />.</summary>
    public Guid Party { get; init; }

    /// <summary>Their guild, or <see cref="Guid.Empty" />.</summary>
    public Guid Guild { get; init; }

    /// <summary>Their language tag — <c>en-GB</c>, <c>de</c>. Compared verbatim, never interpreted.</summary>
    /// <remarks>
    ///     Guild Wars 2's most-underrated placement term, and one the engine has no business having
    ///     an opinion about beyond "the same string scores".
    /// </remarks>
    public string Locale { get; init; } = "";

    /// <summary>The shard they were just moved off, if any.</summary>
    public ShardId CameFrom { get; init; }

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Player} → {Key}");
}

/// <summary>A shard as placement sees it: a few numbers, and no roster.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The affinity counts are inputs, not something this layer computes.</b> Which of a
///         player's friends are on a shard is a question only the thing holding the fleet's roster
///         can answer — <c>IMapGrain</c> — and asking it here would make the scoring function need a
///         database. So the grain counts and the director scores, which is what makes the scoring
///         a pure function that a property test can hammer a million times.
///     </para>
///     <para>
///         <see cref="Age" /> rather than a start time, so that a test does not have to construct a
///         clock to say "this shard is old".
///     </para>
/// </remarks>
public sealed record ShardCandidate {
    /// <summary>Which shard.</summary>
    public ShardId Shard { get; init; }

    /// <summary>What it is for.</summary>
    public ShardKey Key { get; init; }

    /// <summary>Where it is in its life. Only <see cref="ShardState.Ready" /> is placeable.</summary>
    public ShardState State { get; init; }

    /// <summary>Where clients reach it.</summary>
    public RealmEndpoint Endpoint { get; init; }

    /// <summary>How many are on it.</summary>
    public int Population { get; init; }

    /// <summary>How many it will take.</summary>
    public ShardCapacity Capacity { get; init; }

    /// <summary>How long it has been up.</summary>
    public TimeSpan Age { get; init; }

    /// <summary>Who is admitted.</summary>
    public ShardKind Kind { get; init; } = ShardKind.Public;

    /// <summary>
    ///     Whether this shard's access list admits the requester — always true for a public one.
    /// </summary>
    public bool Admits { get; init; } = true;

    /// <summary>How many of the requester's party are already here.</summary>
    public int PartyMembers { get; init; }

    /// <summary>How many of their guild.</summary>
    public int GuildMembers { get; init; }

    /// <summary>How many of their friends.</summary>
    public int Friends { get; init; }

    /// <summary>The language most of this shard is speaking.</summary>
    public string Locale { get; init; } = "";

    /// <summary>How full it is, as a percentage of its soft cap.</summary>
    public double FillPercent => Capacity.SoftCap <= 0 ? 0 : Population * 100.0 / Capacity.SoftCap;

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Shard} {State} {Population}/{Capacity}");
}
