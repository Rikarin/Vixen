// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay.Movement;

/// <summary>Somebody got on or off.</summary>
/// <param name="Player">Who.</param>
/// <param name="Seat">Which seat, or −1 when they got off.</param>
/// <param name="IsDriver">Whether that seat steers.</param>
public readonly record struct SeatChange(PlayerId Player, int Seat, bool IsDriver);

/// <summary>One vehicle in the world: who is in which seat.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The transform half of this is blocked, and doc 28 predicted it.</b> § Movement says
///         mounts and vehicles are where doc 16's owed <em>parent-relative replication</em> stops
///         being optional, <em>"because a passenger replicating world coordinates fights the
///         vehicle's own"</em>. That is item 69 in <c>docs/overview.md</c> and it is still owed, so
///         nothing here touches a position: what is built is the seat model, which is the half that
///         is not waiting on anything.
///     </para>
///     <para>
///         ⚠ <b>The driver leaving does not delete the vehicle or eject anybody.</b> It becomes
///         driverless, and a passenger may take the wheel if the definition allows. A taxi nobody may
///         steer and a raft anybody may steer are both real, so it is a policy rather than a rule.
///     </para>
/// </remarks>
public sealed class VehicleInstance {
    readonly PlayerId[] occupants;

    /// <summary>Makes an empty one.</summary>
    /// <param name="vehicle">What it is.</param>
    public VehicleInstance(Vehicle vehicle) {
        ArgumentNullException.ThrowIfNull(vehicle);

        Vehicle = vehicle;
        occupants = new PlayerId[vehicle.Seats.Length];
    }

    /// <summary>What it is.</summary>
    public Vehicle Vehicle { get; }

    /// <summary>How many are aboard.</summary>
    public int Count => occupants.Count(player => player.IsSome);

    /// <summary>Whether nobody is.</summary>
    public bool IsEmpty => Count == 0;

    /// <summary>Who is steering, or <see cref="PlayerId.None" />.</summary>
    public PlayerId Driver {
        get {
            for (var seat = 0; seat < occupants.Length; seat++) {
                if (occupants[seat].IsSome && Vehicle.Seats[seat].Controls) {
                    return occupants[seat];
                }
            }

            return PlayerId.None;
        }
    }

    /// <summary>Whether anybody is steering it.</summary>
    public bool IsDriven => Driver.IsSome;

    /// <summary>Raised whenever somebody gets on or off.</summary>
    public event Action<SeatChange>? Changed;

    /// <summary>Who is in a seat.</summary>
    /// <param name="seat">Which one.</param>
    /// <returns>Them, or <see cref="PlayerId.None" />.</returns>
    public PlayerId Occupant(int seat) => (uint)seat < (uint)occupants.Length ? occupants[seat] : PlayerId.None;

    /// <summary>Which seat somebody is in.</summary>
    /// <param name="player">Who.</param>
    /// <returns>Its index, or −1.</returns>
    public int SeatOf(PlayerId player) => Array.IndexOf(occupants, player);

    /// <summary>Whether somebody is aboard at all.</summary>
    /// <param name="player">Who.</param>
    /// <returns>Whether they are.</returns>
    public bool Carries(PlayerId player) => player.IsSome && SeatOf(player) >= 0;

    /// <summary>Whether somebody may take a seat, and why not.</summary>
    /// <param name="player">Who.</param>
    /// <param name="seat">Which seat.</param>
    /// <param name="context">What their requirements are evaluated against, or null to skip them.</param>
    /// <returns>The refusal, or <see cref="SeatRefusal.None" />.</returns>
    public SeatRefusal CanMount(PlayerId player, int seat, IRequirementContext? context = null) {
        if (!player.IsSome || (uint)seat >= (uint)occupants.Length) {
            return SeatRefusal.Unknown;
        }

        if (Carries(player)) {
            return SeatRefusal.AlreadyAboard;
        }

        if (occupants[seat].IsSome) {
            return SeatRefusal.Occupied;
        }

        if (Vehicle.Seats[seat].Controls && !Vehicle.PassengersMaySteer && Count > 0) {
            return SeatRefusal.Forbidden;
        }

        if (context is not null && !Vehicle.Seats[seat].Requirements.IsMetBy(context)) {
            return SeatRefusal.Requirements;
        }

        return SeatRefusal.None;
    }

    /// <summary>Gets on.</summary>
    /// <param name="player">Who.</param>
    /// <param name="seat">Which seat.</param>
    /// <param name="context">What their requirements are evaluated against.</param>
    /// <param name="grantTo">Where the tags being aboard grants go, or null.</param>
    /// <returns>The refusal, or <see cref="SeatRefusal.None" />.</returns>
    public SeatRefusal Mount(PlayerId player, int seat, IRequirementContext? context = null, GameplayTagSet? grantTo = null) {
        var refusal = CanMount(player, seat, context);

        if (refusal != SeatRefusal.None) {
            return refusal;
        }

        occupants[seat] = player;

        if (grantTo is not null) {
            Grant(grantTo, seat);
        }

        Changed?.Invoke(new(player, seat, Vehicle.Seats[seat].Controls));

        return SeatRefusal.None;
    }

    /// <summary>Takes the first seat that will have them.</summary>
    /// <param name="player">Who.</param>
    /// <param name="context">What their requirements are evaluated against.</param>
    /// <param name="grantTo">Where the tags being aboard grants go.</param>
    /// <returns>The seat they took, or −1.</returns>
    /// <remarks>
    ///     In seat order, which is authored order, so a designer decides what "get in" means by
    ///     listing the driver's seat first — or not, for a bus.
    /// </remarks>
    public int MountAny(PlayerId player, IRequirementContext? context = null, GameplayTagSet? grantTo = null) {
        for (var seat = 0; seat < occupants.Length; seat++) {
            if (Mount(player, seat, context, grantTo) == SeatRefusal.None) {
                return seat;
            }
        }

        return -1;
    }

    /// <summary>Gets off.</summary>
    /// <param name="player">Who.</param>
    /// <param name="revokeFrom">Where the tags being aboard granted are taken back from, or null.</param>
    /// <returns>The refusal, or <see cref="SeatRefusal.None" />.</returns>
    public SeatRefusal Dismount(PlayerId player, GameplayTagSet? revokeFrom = null) {
        var seat = SeatOf(player);

        if (seat < 0) {
            return SeatRefusal.NotAboard;
        }

        occupants[seat] = PlayerId.None;

        if (revokeFrom is not null) {
            Revoke(revokeFrom, seat);
        }

        Changed?.Invoke(new(player, -1, Vehicle.Seats[seat].Controls));

        return SeatRefusal.None;
    }

    /// <summary>Moves somebody to another seat.</summary>
    /// <param name="player">Who.</param>
    /// <param name="seat">Which seat.</param>
    /// <param name="context">What their requirements are evaluated against.</param>
    /// <param name="tags">Where the tags are kept, or null.</param>
    /// <returns>The refusal, or <see cref="SeatRefusal.None" />.</returns>
    /// <remarks>
    ///     ⚠ <b>Checked before anybody moves.</b> Getting off and then failing to get back on is how
    ///     somebody ends up standing in the road at sixty miles an hour.
    /// </remarks>
    public SeatRefusal MoveTo(PlayerId player, int seat, IRequirementContext? context = null, GameplayTagSet? tags = null) {
        var from = SeatOf(player);

        if (from < 0) {
            return SeatRefusal.NotAboard;
        }

        if (from == seat) {
            return SeatRefusal.None;
        }

        if ((uint)seat >= (uint)occupants.Length) {
            return SeatRefusal.Unknown;
        }

        if (occupants[seat].IsSome) {
            return SeatRefusal.Occupied;
        }

        if (Vehicle.Seats[seat].Controls && !Vehicle.PassengersMaySteer) {
            return SeatRefusal.Forbidden;
        }

        if (context is not null && !Vehicle.Seats[seat].Requirements.IsMetBy(context)) {
            return SeatRefusal.Requirements;
        }

        occupants[from] = PlayerId.None;
        occupants[seat] = player;

        if (tags is not null) {
            Revoke(tags, from);
            Grant(tags, seat);
        }

        Changed?.Invoke(new(player, seat, Vehicle.Seats[seat].Controls));

        return SeatRefusal.None;
    }

    /// <summary>Empties it. What despawning does.</summary>
    /// <param name="revokeFrom">Where each occupant's tags are taken back from, keyed by player.</param>
    /// <returns>How many got off.</returns>
    public int Eject(IReadOnlyDictionary<PlayerId, GameplayTagSet>? revokeFrom = null) {
        var ejected = 0;

        for (var seat = 0; seat < occupants.Length; seat++) {
            if (!occupants[seat].IsSome) {
                continue;
            }

            var player = occupants[seat];

            occupants[seat] = PlayerId.None;

            if (revokeFrom is not null && revokeFrom.TryGetValue(player, out var tags)) {
                Revoke(tags, seat);
            }

            Changed?.Invoke(new(player, -1, Vehicle.Seats[seat].Controls));
            ejected++;
        }

        return ejected;
    }

    void Grant(GameplayTagSet tags, int seat) {
        if (Vehicle.Tag.IsSome) {
            tags.Add(Vehicle.Tag);
        }

        if (Vehicle.Seats[seat].Tag.IsSome) {
            tags.Add(Vehicle.Seats[seat].Tag);
        }
    }

    void Revoke(GameplayTagSet tags, int seat) {
        if (Vehicle.Tag.IsSome) {
            tags.Remove(Vehicle.Tag);
        }

        if (Vehicle.Seats[seat].Tag.IsSome) {
            tags.Remove(Vehicle.Seats[seat].Tag);
        }
    }
}
