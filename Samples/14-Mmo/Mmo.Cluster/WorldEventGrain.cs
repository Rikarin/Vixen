// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Orleans;
using Vixen.Live;

namespace Vixen.Samples.Mmo.Cluster;

/// <summary>What one participant has done towards a world event.</summary>
/// <param name="Player">Who.</param>
/// <param name="Contribution">How much, in whatever the event counts.</param>
[GenerateSerializer]
[Immutable]
public readonly record struct EventContribution(
    [property: Id(0)] PlayerKey Player,
    [property: Id(1)] int Contribution
);

/// <summary>A world event's state, fleet-wide.</summary>
/// <param name="Event">Which event, by address.</param>
/// <param name="Running">Whether it is up now.</param>
/// <param name="Started">When it went up.</param>
/// <param name="Ends">When it comes down, whether or not it is finished.</param>
/// <param name="Progress">How far along, in tenths of a percent.</param>
/// <param name="Contributions">Who has done what, in player order.</param>
/// <param name="Revision">The optimistic fence, as <c>GuildRow</c>'s is.</param>
[GenerateSerializer]
[Immutable]
public sealed record WorldEventRecord(
    [property: Id(0)] string Event,
    [property: Id(1)] bool Running,
    [property: Id(2)] DateTimeOffset Started,
    [property: Id(3)] DateTimeOffset Ends,
    [property: Id(4)] int Progress,
    [property: Id(5)] ImmutableArray<EventContribution> Contributions,
    [property: Id(6)] uint Revision
) {
    /// <summary>An event nobody has started.</summary>
    public static WorldEventRecord None { get; } = new(string.Empty, false, default, default, 0, [], 0);

    /// <summary>Whether two say the same thing.</summary>
    /// <param name="other">The other.</param>
    /// <returns>Whether they do.</returns>
    /// <remarks>
    ///     ⚠ Hand-written, for the trap doc 27 § Slice two records and this repository has now hit
    ///     five times: a record's generated equality compares an <see cref="ImmutableArray{T}" /> by
    ///     <em>reference</em>, so a record read back never equals the one written.
    /// </remarks>
    public bool Equals(WorldEventRecord? other) =>
        other is not null
        && string.Equals(Event, other.Event, StringComparison.Ordinal)
        && Running == other.Running
        && Started == other.Started
        && Ends == other.Ends
        && Progress == other.Progress
        && Revision == other.Revision
        && Contributions.SequenceEqual(other.Contributions);

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine(Event, Running, Started, Ends, Progress, Revision, Contributions.Length);
}

/// <summary>One world event, across every shard of its map.</summary>
/// <remarks>
///     <para>
///         <b>The one grain this sample adds, and the argument for it is the same one doc 27 makes
///         for <c>IInstanceGrain</c>.</b> The Rootbound Colossus is on <c>maps/thornwood</c>, and
///         Thornwood is several shards at peak. An event whose schedule each shard kept would come up
///         at a different minute on each of them — so a player who zoned would find it already dead
///         on one and not yet started on another, and the fleet would pay out three times.
///     </para>
///     <para>
///         ⚠ <b>Contribution is fleet-wide for the same reason.</b> Doc 28's contribution tiers pay a
///         latecomer differently from somebody who was there the whole way, and "the whole way" spans
///         a transfer: a player who helped for four minutes, zoned, and helped for four more has done
///         eight minutes of work and one shard saw half of it.
///     </para>
///     <para>
///         ⚠ <b>What it deliberately does not hold is the fight.</b> Health, positions and threat are
///         a realm's, at sixty hertz; this grain hears about progress at whatever cadence the realm
///         chooses to report it, and ADR-016 is why — a boss whose health bar was a grain call would
///         have a p99 measured in milliseconds.
///     </para>
/// </remarks>
public interface IWorldEventGrain : IGrainWithStringKey {
    /// <summary>What the event looks like.</summary>
    /// <returns>The record, or <see cref="WorldEventRecord.None" />.</returns>
    Task<WorldEventRecord> Read();

    /// <summary>Starts it, if it is not already up.</summary>
    /// <param name="address">Which event.</param>
    /// <param name="now">When.</param>
    /// <param name="ends">When it comes down regardless.</param>
    /// <returns>Whether this call is what started it.</returns>
    /// <remarks>
    ///     ⚠ <b>Idempotent by design, because every shard of the map will call it.</b> Five shards
    ///     noticing the schedule at the same second is five calls, and four of them have to be
    ///     no-ops rather than four more events.
    /// </remarks>
    Task<bool> Start(string address, DateTimeOffset now, DateTimeOffset ends);

    /// <summary>Reports what a shard has seen since it last reported.</summary>
    /// <param name="progress">The event's progress as that shard measures it, in tenths of a percent.</param>
    /// <param name="contributions">What each player on that shard has added.</param>
    /// <returns>The record after.</returns>
    /// <remarks>
    ///     ⚠ <b>Contributions are a <em>delta</em> and progress is a <em>level</em>.</b> Two shards
    ///     each reporting their own players' work sums correctly; two shards each reporting "the boss
    ///     is at 40%" does not, and adding those would finish the boss at half health.
    /// </remarks>
    Task<WorldEventRecord> Report(int progress, ImmutableArray<EventContribution> contributions);

    /// <summary>Ends it and freezes the contributions for payout.</summary>
    /// <param name="succeeded">Whether it was finished or merely ran out.</param>
    /// <returns>The final record, which is what the payout reads.</returns>
    Task<WorldEventRecord> Finish(bool succeeded);
}
