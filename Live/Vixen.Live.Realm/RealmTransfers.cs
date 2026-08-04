// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using Vixen.Live.Transfer;

namespace Vixen.Live.Realms;

/// <summary>Every transfer this realm is part of — the ones leaving and the ones arriving.</summary>
/// <remarks>
///     <para>
///         The join between <c>Vixen.Live.Transfer</c>'s state machines and a running realm.
///         <see cref="SourceTransfer" /> knows the protocol and nothing about a frame;
///         <see cref="RealmHost" /> has the frame and knows nothing about the protocol. This is where
///         the two meet, and it is stepped exactly once per update so that a transfer expiring and a
///         player being simulated cannot happen at the same time on two threads.
///     </para>
///     <para>
///         ⚠ <b>It decides nothing about <em>where</em> anybody goes.</b> That is
///         <c>IMapGrain.Place</c>'s answer arriving through <c>RealmDirectory</c>, and this only ever
///         holds the machine that the answer is fed into. A realm that chose its own destinations
///         would be a realm making placement decisions with no view of the fleet.
///     </para>
/// </remarks>
public sealed class RealmTransfers {
    readonly ConcurrentDictionary<PlayerKey, SourceTransfer> leaving = new();
    readonly TransferDeadlines deadlines;

    /// <summary>Builds one.</summary>
    /// <param name="deadlines">How long each step of a transfer may take.</param>
    public RealmTransfers(TransferDeadlines? deadlines = null) => this.deadlines = deadlines ?? new();

    /// <summary>The slots held for people on their way here.</summary>
    public TransferBoard Arriving { get; } = new();

    /// <summary>What transfers are costing this realm.</summary>
    public TransferMetrics Metrics { get; } = new();

    /// <summary>Everybody currently on their way out.</summary>
    public IReadOnlyCollection<SourceTransfer> Leaving => [.. leaving.Values];

    /// <summary>How many are in flight, which is the number a drain watches.</summary>
    public int InFlight => leaving.Count;

    /// <summary>Starts moving somebody.</summary>
    /// <param name="player">Who.</param>
    /// <param name="destination">Which map they asked for.</param>
    /// <param name="now">The realm's clock.</param>
    /// <param name="reason">Why — a portal, a party join, a drain.</param>
    /// <returns>The transfer, or the one already in flight for them.</returns>
    /// <remarks>
    ///     ⚠ <b>One transfer per player, and a second request returns the first.</b> Two in flight
    ///     would mean two tickets, two reservations and two lease epochs for one person — and the
    ///     loser's <c>CommitTransfer</c> would still be a message the client could obey.
    /// </remarks>
    public SourceTransfer Begin(PlayerKey player, string destination, DateTimeOffset now, string reason = "") =>
        leaving.GetOrAdd(player, key => new(key, destination, now, deadlines, reason));

    /// <summary>The transfer in flight for somebody, if there is one.</summary>
    /// <param name="player">Who.</param>
    /// <param name="transfer">It.</param>
    /// <returns>Whether there was one.</returns>
    public bool TryGet(PlayerKey player, out SourceTransfer? transfer) => leaving.TryGetValue(player, out transfer);

    /// <summary>Whether somebody is on their way out.</summary>
    /// <param name="player">Who.</param>
    /// <returns>Whether a transfer is in flight for them.</returns>
    public bool IsLeaving(PlayerKey player) => leaving.ContainsKey(player);

    /// <summary>Holds a slot for somebody the orchestrator is sending here.</summary>
    /// <param name="ticket">What they will present.</param>
    /// <param name="epoch">The lease epoch this arrival is for.</param>
    /// <param name="now">The realm's clock.</param>
    /// <param name="population">How many are here already.</param>
    /// <param name="capacity">How many this shard will take.</param>
    /// <param name="draining">Whether it takes anybody new.</param>
    /// <returns>Why not, or <see cref="ReservationRefusal.None" />.</returns>
    /// <remarks>
    ///     ⚠ <b>The room check counts the pending arrivals as well as the population</b>, which is the
    ///     whole reason a reservation exists: without it a shard at its cap could promise the same
    ///     last slot to everybody in flight and refuse all but one of them at the door.
    /// </remarks>
    public ReservationRefusal Expect(
        TransferTicket ticket,
        long epoch,
        DateTimeOffset now,
        int population,
        ShardCapacity capacity,
        bool draining
    ) =>
        Arriving.Reserve(ticket, epoch, now, capacity.Admits(population + Arriving.Pending), draining);

    /// <summary>Forgets a transfer, however it ended.</summary>
    /// <param name="player">Who.</param>
    /// <returns>Whether there was one.</returns>
    public bool Forget(PlayerKey player) => leaving.TryRemove(player, out _);

    /// <summary>One turn of the clock for every transfer this realm is part of.</summary>
    /// <param name="now">The realm's clock.</param>
    /// <returns>The transfers that ended this turn — committed and aborted alike.</returns>
    /// <remarks>
    ///     ⚠ <b>The finished ones are returned rather than swept silently.</b> A committed transfer
    ///     means the caller must despawn the player and an aborted one means it must not, and those
    ///     are the two things a realm cannot be allowed to get wrong. Handing them back makes the
    ///     decision the caller's, once, at a defined point in the frame.
    /// </remarks>
    public IReadOnlyList<SourceTransfer> Step(DateTimeOffset now) {
        List<SourceTransfer> finished = [];

        foreach (var transfer in leaving.Values) {
            transfer.Step(now);

            if (transfer.Done && leaving.TryRemove(transfer.Player, out _)) {
                Metrics.Record(transfer);
                finished.Add(transfer);
            }
        }

        // A slot nobody came for is capacity nobody can use, and a realm that leaked them would
        // refuse arrivals while standing empty.
        Arriving.Sweep(now);

        return finished;
    }
}
