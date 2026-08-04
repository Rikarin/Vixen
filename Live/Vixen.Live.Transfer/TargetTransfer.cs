// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Globalization;

namespace Vixen.Live.Transfer;

/// <summary>How far an arriving player has got, on the realm they are arriving at.</summary>
public enum ArrivalState : byte {
    /// <summary>A slot is held. Nobody has connected.</summary>
    Reserved = 0,

    /// <summary>
    ///     Connected and admitted, and <b>dormant</b>: no ownership, no input, no camera.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Dormancy is what stops the player existing twice.</b> They have a session here and
    ///     are receiving interest so their client can load the map, and the source is still
    ///     simulating them. A target that spawned them live at this point would put two of them in
    ///     the world for as long as the overlap lasts.
    /// </remarks>
    Dormant = 1,

    /// <summary>The lease is held and the volatile state has been applied. They are ours.</summary>
    Live = 2,

    /// <summary>The reservation aged out, or the source gave up. The slot is free.</summary>
    Lapsed = 3
}

/// <summary>One arriving player, on the realm receiving them.</summary>
/// <param name="Player">Who.</param>
/// <param name="Ticket">What they will present.</param>
/// <param name="Epoch">The lease epoch this arrival is for.</param>
/// <param name="Reserved">When the slot was held.</param>
/// <param name="State">How far they have got.</param>
public sealed record Arrival(
    PlayerKey Player,
    TransferTicket Ticket,
    long Epoch,
    DateTimeOffset Reserved,
    ArrivalState State
) {
    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Player} arriving at epoch {Epoch}: {State}");
}

/// <summary>Why a realm would not take somebody.</summary>
public enum ReservationRefusal : byte {
    /// <summary>It will.</summary>
    None = 0,

    /// <summary>This shard is full to its hard cap.</summary>
    Full = 1,

    /// <summary>This shard is draining and takes nobody new.</summary>
    Draining = 2,

    /// <summary>The ticket is not for this shard, is not signed by this cluster, or has expired.</summary>
    BadTicket = 3,

    /// <summary>They are already here, or already arriving.</summary>
    AlreadyHere = 4
}

/// <summary>The slots a realm is holding for people who are on their way. Doc 27 § The overlap, t1.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A pre-reservation is capacity that is spent before anybody has connected</b>, and
///         that is the point: without it, a map at 99 % could promise the same last slot to twenty
///         players in flight and refuse nineteen of them at the door — after each had loaded the
///         map. The reservation is what makes <c>PlaceStatus.Placed</c> a promise rather than a
///         guess.
///     </para>
///     <para>
///         ⚠ <b>It therefore has to expire.</b> A held slot whose client never arrives is capacity
///         nobody can use, and a realm that leaked them would refuse arrivals while standing empty.
///         <see cref="Sweep" /> is what a realm calls once per update.
///     </para>
/// </remarks>
public sealed class TransferBoard {
    readonly ConcurrentDictionary<PlayerKey, Arrival> arrivals = new();

    /// <summary>How long a slot is held for somebody who has not connected.</summary>
    /// <remarks>
    ///     Shorter than the source's overlap deadline, deliberately: the source waits for a client
    ///     that may be downloading a map, and this waits only for it to open a socket. A slot held
    ///     for the whole download would make a busy map's capacity a function of its slowest player's
    ///     connection.
    /// </remarks>
    public TimeSpan ReservationLifetime { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>How long a dormant arrival is held before it is assumed abandoned.</summary>
    public TimeSpan DormantLifetime { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Everybody being held a slot for, in any state.</summary>
    public IReadOnlyCollection<Arrival> Arrivals => [.. arrivals.Values];

    /// <summary>How many slots are spoken for — reserved and dormant, not yet live.</summary>
    /// <remarks>
    ///     Added to the population when deciding whether there is room, which is the whole reason
    ///     this number exists separately.
    /// </remarks>
    public int Pending =>
        arrivals.Values.Count(arrival => arrival.State is ArrivalState.Reserved or ArrivalState.Dormant);

    /// <summary>Holds a slot for somebody the orchestrator is sending.</summary>
    /// <param name="ticket">What they will present. Already validated by the caller's signer.</param>
    /// <param name="epoch">The lease epoch this arrival is for.</param>
    /// <param name="now">The realm's clock.</param>
    /// <param name="room">Whether there is capacity, population and pending arrivals included.</param>
    /// <param name="draining">Whether this shard takes anybody new.</param>
    /// <returns>Why not, or <see cref="ReservationRefusal.None" />.</returns>
    public ReservationRefusal Reserve(
        TransferTicket ticket,
        long epoch,
        DateTimeOffset now,
        bool room,
        bool draining
    ) {
        ArgumentNullException.ThrowIfNull(ticket);

        if (draining) {
            return ReservationRefusal.Draining;
        }

        if (!room) {
            return ReservationRefusal.Full;
        }

        if (ticket.Expires <= now) {
            return ReservationRefusal.BadTicket;
        }

        // A second reservation for the same player at a HIGHER epoch replaces the first: it is the
        // same person being re-placed after their first attempt died, and refusing it would leave
        // them held out by the corpse of their own earlier transfer.
        var replacing = arrivals.TryGetValue(ticket.Player, out var existing) && existing.Epoch < epoch;

        if (arrivals.ContainsKey(ticket.Player) && !replacing) {
            return ReservationRefusal.AlreadyHere;
        }

        arrivals[ticket.Player] = new(ticket.Player, ticket, epoch, now, ArrivalState.Reserved);

        return ReservationRefusal.None;
    }

    /// <summary>The client connected and was admitted dormant. t3.</summary>
    /// <param name="player">Who.</param>
    /// <param name="epoch">Which epoch their ticket named.</param>
    /// <returns>Whether a slot was being held for them at that epoch.</returns>
    public bool Arrived(PlayerKey player, long epoch) =>
        Move(player, epoch, ArrivalState.Reserved, ArrivalState.Dormant);

    /// <summary>The lease was acquired and the payload applied. t5.</summary>
    /// <param name="player">Who.</param>
    /// <param name="epoch">Which epoch.</param>
    /// <returns>Whether they were dormant here at that epoch.</returns>
    public bool Woke(PlayerKey player, long epoch) =>
        Move(player, epoch, ArrivalState.Dormant, ArrivalState.Live);

    /// <summary>Gives the slot back.</summary>
    /// <param name="player">Who.</param>
    /// <returns>Whether there was one.</returns>
    public bool Release(PlayerKey player) => arrivals.TryRemove(player, out _);

    /// <summary>Drops the slots nobody came for.</summary>
    /// <param name="now">The realm's clock.</param>
    /// <returns>Who was dropped.</returns>
    public IReadOnlyList<PlayerKey> Sweep(DateTimeOffset now) {
        List<PlayerKey> lapsed = [];

        foreach (var arrival in arrivals.Values) {
            var lifetime = arrival.State switch {
                ArrivalState.Reserved => ReservationLifetime,
                ArrivalState.Dormant => DormantLifetime,
                _ => TimeSpan.Zero
            };

            // A ticket that has expired takes its reservation with it: the client can no longer be
            // admitted with it, so holding the slot is holding it for nobody.
            var stale = lifetime > TimeSpan.Zero
                && (now - arrival.Reserved >= lifetime || arrival.Ticket.Expires <= now);

            if (stale && arrivals.TryRemove(arrival.Player, out _)) {
                lapsed.Add(arrival.Player);
            }
        }

        return lapsed;
    }

    bool Move(PlayerKey player, long epoch, ArrivalState from, ArrivalState to) {
        if (!arrivals.TryGetValue(player, out var arrival) || arrival.State != from || arrival.Epoch != epoch) {
            return false;
        }

        return arrivals.TryUpdate(player, arrival with { State = to }, arrival);
    }
}
