// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Globalization;
using Vixen.Live.Cluster;

namespace Vixen.Live.Orchestration;

/// <summary>One queue, as a state machine a test can drive.</summary>
/// <remarks>
///     <para>
///         Grains over state machines, a seventh time. The matching itself is
///         <c>Vixen.Live.Matchmaking</c>'s and stays a pure function of tickets and a clock; what is
///         here is the bookkeeping a grain is for — who is waiting, what has been formed, and which
///         seats a backfill has opened.
///     </para>
///     <para>
///         ⚠ <b>Deliberately not holding a <c>Matchmaker</c>.</b> Its queue is the thing this type
///         replaces: doc 28 says a ticket is *"a grain-held record"*, so the tickets live here and
///         the matcher is handed a snapshot of them per cycle. Two queues — one in the grain and one
///         in the matchmaker — is two places a ticket can be, and cancelling would have to hit both.
///     </para>
///     <para>
///         ⚠ <b>Formed is not started.</b> A roster still needs a shard and allocating one can fail,
///         so its tickets are held rather than released and the caller confirms or abandons.
///     </para>
/// </remarks>
public sealed class QueueState {
    readonly Dictionary<string, QueueTicket> tickets = new(StringComparer.Ordinal);
    readonly Dictionary<Guid, QueueMatch> open = [];
    readonly Dictionary<Guid, int> backfills = [];
    readonly IQueueMatcher matcher;

    long minted;

    /// <summary>Makes one.</summary>
    /// <param name="matcher">What decides who plays with whom.</param>
    public QueueState(IQueueMatcher? matcher = null) => this.matcher = matcher ?? new PairMatcher();

    /// <summary>How many tickets are waiting.</summary>
    public int Waiting => tickets.Values.Count(ticket => ticket.State == QueueTicketState.Waiting);

    /// <summary>How many matches are formed and not yet started.</summary>
    public int Open => open.Count;

    /// <summary>Joins the queue.</summary>
    /// <param name="entry">What they are asking for.</param>
    /// <returns>The ticket.</returns>
    public QueueTicket Enqueue(QueueEntry entry) {
        ArgumentNullException.ThrowIfNull(entry);

        var id = (++minted).ToString(CultureInfo.InvariantCulture);
        var ticket = new QueueTicket(id, entry, QueueTicketState.Waiting, Guid.Empty);

        tickets.Add(id, ticket);

        return ticket;
    }

    /// <summary>Gives up.</summary>
    /// <param name="ticket">Which.</param>
    /// <returns>Whether it was still waiting.</returns>
    /// <remarks>
    ///     ⚠ <b>A ticket already in a formed match is not cancellable.</b> Letting it go would leave
    ///     the other side of a roster waiting for a shard for a match that can no longer be played —
    ///     which is what <see cref="Abandon" /> is for, and it puts everybody back rather than one.
    /// </remarks>
    public bool Cancel(string ticket) =>
        tickets.TryGetValue(ticket ?? "", out var found)
        && found.State == QueueTicketState.Waiting
        && tickets.Remove(ticket!);

    /// <summary>Where a ticket has got to.</summary>
    /// <param name="ticket">Which.</param>
    /// <returns>It, or null.</returns>
    public QueueTicket? Ticket(string ticket) => tickets.GetValueOrDefault(ticket ?? "");

    /// <summary>Forms whatever can be formed.</summary>
    /// <param name="now">The clock.</param>
    /// <returns>The rosters.</returns>
    public ImmutableArray<QueueMatch> Cycle(DateTimeOffset now) {
        var formed = ImmutableArray.CreateBuilder<QueueMatch>();

        // Backfills first, and that ordering is the decision. A match already running with an empty
        // seat is a worse experience than a match that has not started, so a waiting ticket goes to
        // the running game before it goes to a new one.
        foreach (var (match, seats) in backfills.OrderBy(pair => pair.Key)) {
            var filling = Take(seats, now);

            if (filling.Length == 0) {
                continue;
            }

            foreach (var ticket in filling) {
                tickets[ticket] = tickets[ticket] with { State = QueueTicketState.Matched, Match = match };
            }

            formed.Add(new(match, [new(filling)], 1d, now, true));

            if (filling.Length >= seats) {
                backfills.Remove(match);
            } else {
                backfills[match] = seats - filling.Length;
            }
        }

        foreach (var proposal in matcher.Form([.. Available()], now)) {
            var id = Guid.NewGuid();
            var teams = ImmutableArray.CreateBuilder<QueueTeam>(proposal.Length);

            foreach (var team in proposal) {
                foreach (var ticket in team) {
                    tickets[ticket] = tickets[ticket] with { State = QueueTicketState.Matched, Match = id };
                }

                teams.Add(new(team));
            }

            var match = new QueueMatch(id, teams.DrainToImmutable(), 1d, now, false);

            open.Add(id, match);
            formed.Add(match);
        }

        return formed.DrainToImmutable();
    }

    /// <summary>Says a formed match got its shard.</summary>
    /// <param name="match">Which.</param>
    /// <returns>Whether it was open.</returns>
    public bool Start(Guid match) {
        if (!open.Remove(match, out var started)) {
            return false;
        }

        foreach (var ticket in started.Teams.SelectMany(team => team.Tickets)) {
            tickets.Remove(ticket);
        }

        return true;
    }

    /// <summary>Says a formed match did not get one.</summary>
    /// <param name="match">Which.</param>
    /// <param name="now">The clock.</param>
    /// <returns>Whether it was open.</returns>
    /// <remarks>
    ///     ⚠ <b>The tickets keep their original enqueue time.</b> A ticket sent to the back of the
    ///     queue would be punished for a failure that was the fleet's — and the widening a long wait
    ///     earns is the thing that gets an unusual party matched at all.
    /// </remarks>
    public bool Abandon(Guid match, DateTimeOffset now) {
        _ = now;

        if (!open.Remove(match, out var abandoned)) {
            return false;
        }

        foreach (var ticket in abandoned.Teams.SelectMany(team => team.Tickets)) {
            if (tickets.TryGetValue(ticket, out var found)) {
                tickets[ticket] = found with { State = QueueTicketState.Waiting, Match = Guid.Empty };
            }
        }

        return true;
    }

    /// <summary>Says somebody left a running match.</summary>
    /// <param name="match">Which.</param>
    /// <param name="seats">How many opened.</param>
    /// <returns>Whether it was recorded.</returns>
    public bool Backfill(Guid match, int seats) {
        if (match == Guid.Empty || seats <= 0) {
            return false;
        }

        backfills[match] = backfills.GetValueOrDefault(match) + seats;

        return true;
    }

    /// <summary>What the queue looks like.</summary>
    /// <param name="now">The clock.</param>
    /// <returns>The snapshot.</returns>
    public QueueSnapshot Read(DateTimeOffset now) {
        var waiting = tickets.Values.Where(ticket => ticket.State == QueueTicketState.Waiting).ToArray();

        return new(
            waiting.Length,
            waiting.Sum(ticket => ticket.Entry.Players.Length),
            open.Count,
            waiting.Length == 0 ? TimeSpan.Zero : now - waiting.Min(ticket => ticket.Entry.Enqueued)
        );
    }

    IEnumerable<QueueTicket> Available() =>
        tickets.Values
            .Where(ticket => ticket.State == QueueTicketState.Waiting)
            .OrderBy(ticket => ticket.Entry.Enqueued)
            .ThenBy(ticket => ticket.Id, StringComparer.Ordinal);

    ImmutableArray<string> Take(int seats, DateTimeOffset now) {
        _ = now;

        var taken = ImmutableArray.CreateBuilder<string>();
        var filled = 0;

        foreach (var ticket in Available()) {
            if (filled + ticket.Entry.Players.Length > seats) {
                continue;
            }

            taken.Add(ticket.Id);
            filled += ticket.Entry.Players.Length;

            if (filled == seats) {
                break;
            }
        }

        return taken.DrainToImmutable();
    }
}

/// <summary>What decides who plays with whom.</summary>
/// <remarks>
///     The seam that keeps the grain testable and the matching replaceable. A game supplies one over
///     <c>Vixen.Live.Matchmaking</c>'s <c>Matchmaker</c>, an <c>IMatchFunction</c> of its own, or
///     anything else — the grain only needs to be told which tickets go together.
/// </remarks>
public interface IQueueMatcher {
    /// <summary>Forms whatever can be formed out of what is waiting.</summary>
    /// <param name="waiting">The tickets, oldest first.</param>
    /// <param name="now">The clock.</param>
    /// <returns>One entry per match, each a list of teams, each a list of ticket ids.</returns>
    IEnumerable<ImmutableArray<ImmutableArray<string>>> Form(ImmutableArray<QueueTicket> waiting, DateTimeOffset now);
}

/// <summary>Two tickets, two teams of one. What a 1v1 ladder wants and what a test can reason about.</summary>
/// <remarks>
///     ⚠ <b>Shipped, but small on purpose.</b> Doc 28 § Matchmaking's real functions are the rating
///     models and <c>IMatchFunction</c>, which live in <c>Vixen.Live.Matchmaking</c> and are already
///     built. This exists so a queue grain has a default that does something honest rather than
///     nothing, in the way <c>DevelopmentAuthority</c> does for the gate.
/// </remarks>
public sealed class PairMatcher : IQueueMatcher {
    /// <inheritdoc />
    public IEnumerable<ImmutableArray<ImmutableArray<string>>> Form(
        ImmutableArray<QueueTicket> waiting,
        DateTimeOffset now
    ) {
        _ = now;

        // Solo tickets only: a party of three cannot be half of a 1v1, and quietly splitting it is
        // the one thing every matchmaker in doc 28 is forbidden to do.
        var solo = waiting.Where(ticket => ticket.Entry.Players.Length == 1).ToArray();

        for (var index = 0; index + 1 < solo.Length; index += 2) {
            yield return [[solo[index].Id], [solo[index + 1].Id]];
        }
    }
}

/// <summary>One queue, keyed by its definition's address.</summary>
public sealed class QueueGrain : Grain, IQueueGrain {
    readonly QueueState queue = new();

    /// <inheritdoc />
    public Task<QueueTicket> Enqueue(QueueEntry entry) => Task.FromResult(queue.Enqueue(entry));

    /// <inheritdoc />
    public Task<bool> Cancel(string ticket) => Task.FromResult(queue.Cancel(ticket));

    /// <inheritdoc />
    public Task<QueueTicket?> Ticket(string ticket) => Task.FromResult(queue.Ticket(ticket));

    /// <inheritdoc />
    public Task<ImmutableArray<QueueMatch>> Cycle(DateTimeOffset now) => Task.FromResult(queue.Cycle(now));

    /// <inheritdoc />
    public Task<bool> Start(Guid match) => Task.FromResult(queue.Start(match));

    /// <inheritdoc />
    public Task<bool> Abandon(Guid match, DateTimeOffset now) => Task.FromResult(queue.Abandon(match, now));

    /// <inheritdoc />
    public Task<bool> Backfill(Guid match, int seats) => Task.FromResult(queue.Backfill(match, seats));

    /// <inheritdoc />
    public Task<QueueSnapshot> Read() => Task.FromResult(queue.Read(DateTimeOffset.UtcNow));
}
