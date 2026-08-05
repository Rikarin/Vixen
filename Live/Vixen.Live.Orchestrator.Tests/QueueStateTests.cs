// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Live.Cluster;
using Xunit;

namespace Vixen.Live.Orchestration.Tests;

/// <summary>
///     The last of the three grains doc 27 left undeclared at L1. Doc 28 § Matchmaking is specific
///     about the shape: a ticket is "a grain-held record", where <c>Matchmaker</c> is an in-memory
///     queue.
/// </summary>
public sealed class QueueStateTests {
    static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch;

    readonly QueueState queue = new();

    static QueueEntry Solo(DateTimeOffset at) => new([new(Guid.NewGuid(), Guid.NewGuid())], 1500d, 200d, [], at);

    static QueueEntry Party(int size, DateTimeOffset at) =>
        new([.. Enumerable.Range(0, size).Select(_ => new PlayerKey(Guid.NewGuid(), Guid.NewGuid()))], 1500d, 200d, [], at);

    [Fact]
    public void JoiningGivesBackATicketThatCancelsIt() {
        var ticket = queue.Enqueue(Solo(Start));

        Assert.Equal(QueueTicketState.Waiting, ticket.State);
        Assert.Equal(1, queue.Waiting);

        Assert.True(queue.Cancel(ticket.Id));
        Assert.Equal(0, queue.Waiting);
        Assert.Null(queue.Ticket(ticket.Id));
    }

    [Fact]
    public void CancellingSomethingThatIsNotThereSaysSo() {
        Assert.False(queue.Cancel("never-issued"));
        Assert.False(queue.Cancel(""));
    }

    [Fact]
    public void TwoSoloTicketsBecomeAMatch() {
        var one = queue.Enqueue(Solo(Start));
        var two = queue.Enqueue(Solo(Start));

        var match = Assert.Single(queue.Cycle(Start));

        Assert.Equal(2, match.Teams.Length);
        Assert.False(match.IsBackfill);
        Assert.Equal(QueueTicketState.Matched, queue.Ticket(one.Id)!.State);
        Assert.Equal(match.Id, queue.Ticket(two.Id)!.Match);
        Assert.Equal(0, queue.Waiting);
    }

    [Fact]
    public void APartyIsNeverSplit() {
        // ⚠ The one thing every matchmaker in doc 28 is forbidden to do. The shipped pair matcher
        // takes solo tickets only rather than quietly halving a party of three.
        queue.Enqueue(Party(3, Start));
        queue.Enqueue(Party(3, Start));

        Assert.Empty(queue.Cycle(Start));
        Assert.Equal(2, queue.Waiting);
    }

    // ── Formed is not started ─────────────────────────────────────────────────────────────────

    [Fact]
    public void ATicketInAFormedMatchIsNotCancellable() {
        // ⚠ Letting one go leaves the other side waiting for a shard for a match that can no longer
        // be played. Abandon is what puts everybody back, and it puts *everybody* back.
        var one = queue.Enqueue(Solo(Start));

        queue.Enqueue(Solo(Start));
        queue.Cycle(Start);

        Assert.False(queue.Cancel(one.Id));
    }

    [Fact]
    public void StartingAMatchTakesItsTicketsOutOfTheQueue() {
        var one = queue.Enqueue(Solo(Start));

        queue.Enqueue(Solo(Start));

        var match = Assert.Single(queue.Cycle(Start));

        Assert.True(queue.Start(match.Id));
        Assert.Equal(0, queue.Open);
        Assert.Null(queue.Ticket(one.Id));
    }

    [Fact]
    public void AbandoningPutsThemBackWithTheTimeTheyAlreadyWaited() {
        // ⚠ A ticket sent to the back of the queue is punished for a failure that was the fleet's,
        // and the widening a long wait earns is the thing that gets an unusual party matched at all.
        var one = queue.Enqueue(Solo(Start));

        queue.Enqueue(Solo(Start));

        var match = Assert.Single(queue.Cycle(Start));

        Assert.True(queue.Abandon(match.Id, Start.AddMinutes(5)));
        Assert.Equal(2, queue.Waiting);
        Assert.Equal(QueueTicketState.Waiting, queue.Ticket(one.Id)!.State);
        Assert.Equal(Start, queue.Ticket(one.Id)!.Entry.Enqueued);
        Assert.Equal(Guid.Empty, queue.Ticket(one.Id)!.Match);
    }

    [Fact]
    public void AnAbandonedMatchCanBeFormedAgain() {
        queue.Enqueue(Solo(Start));
        queue.Enqueue(Solo(Start));

        var first = Assert.Single(queue.Cycle(Start));

        queue.Abandon(first.Id, Start);

        var second = Assert.Single(queue.Cycle(Start));

        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void StartingOrAbandoningSomethingThatIsNotOpenSaysSo() {
        Assert.False(queue.Start(Guid.NewGuid()));
        Assert.False(queue.Abandon(Guid.NewGuid(), Start));
    }

    // ── Backfill ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ABackfillFillsTheMatchItNamesRatherThanFormingANewOne() {
        // Doc 28 names backfill and nothing did it. The match it answers with carries the id being
        // filled, so the caller sends the player to the shard that is already running.
        var running = Guid.NewGuid();

        Assert.True(queue.Backfill(running, seats: 1));

        var joining = queue.Enqueue(Solo(Start));
        var match = Assert.Single(queue.Cycle(Start));

        Assert.True(match.IsBackfill);
        Assert.Equal(running, match.Id);
        Assert.Equal([joining.Id], Assert.Single(match.Teams).Tickets);
    }

    [Fact]
    public void ABackfillIsPreferredToANewMatch() {
        // ⚠ The ordering is the decision: a match already running with an empty seat is a worse
        // experience than one that has not started, so a waiting ticket goes to the running game.
        var running = Guid.NewGuid();

        queue.Backfill(running, seats: 1);

        // Three, because the backfill takes one and a pair needs the other two — which is itself the
        // preference being demonstrated: with only two waiting, the running match gets one of them
        // and the new match does not form at all.
        queue.Enqueue(Solo(Start));
        queue.Enqueue(Solo(Start));
        queue.Enqueue(Solo(Start));

        var formed = queue.Cycle(Start);

        Assert.Equal(2, formed.Length);
        Assert.True(formed[0].IsBackfill);
        Assert.Equal(running, formed[0].Id);
        Assert.False(formed[1].IsBackfill);
    }

    [Fact]
    public void APartialBackfillKeepsAskingForTheRest() {
        var running = Guid.NewGuid();

        queue.Backfill(running, seats: 3);
        queue.Enqueue(Solo(Start));

        Assert.Single(queue.Cycle(Start));

        queue.Enqueue(Solo(Start));
        queue.Enqueue(Solo(Start));

        var second = queue.Cycle(Start);

        Assert.Contains(second, match => match.IsBackfill && match.Id == running);
    }

    [Fact]
    public void APartyTooBigForTheSeatsIsNotSqueezedIn() {
        var running = Guid.NewGuid();

        queue.Backfill(running, seats: 1);
        queue.Enqueue(Party(3, Start));

        Assert.Empty(queue.Cycle(Start));
    }

    [Fact]
    public void NoSeatsIsNotABackfill() {
        Assert.False(queue.Backfill(Guid.NewGuid(), 0));
        Assert.False(queue.Backfill(Guid.Empty, 1));
    }

    // ── The snapshot ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheSnapshotCountsPeopleRatherThanTickets() {
        queue.Enqueue(Party(3, Start));
        queue.Enqueue(Solo(Start));

        var snapshot = queue.Read(Start);

        Assert.Equal(2, snapshot.Waiting);
        Assert.Equal(4, snapshot.Players);
        Assert.Equal(0, snapshot.Open);
    }

    [Fact]
    public void TheLongestWaitIsTheOldestWaitingTicket() {
        queue.Enqueue(Solo(Start));
        queue.Enqueue(Solo(Start.AddMinutes(4)));

        Assert.Equal(TimeSpan.FromMinutes(5), queue.Read(Start.AddMinutes(5)).LongestWait);
        Assert.Equal(TimeSpan.Zero, new QueueState().Read(Start).LongestWait);
    }

    [Fact]
    public void AFormedMatchCountsAsOpenAndItsTicketsAsNeitherWaitingNorGone() {
        queue.Enqueue(Solo(Start));
        queue.Enqueue(Solo(Start));
        queue.Cycle(Start);

        var snapshot = queue.Read(Start);

        Assert.Equal(0, snapshot.Waiting);
        Assert.Equal(1, snapshot.Open);
    }

    [Fact]
    public void OldestFirstIsTheOrderTicketsAreOfferedIn() {
        var first = queue.Enqueue(Solo(Start));

        queue.Enqueue(Solo(Start.AddMinutes(10)));

        var running = Guid.NewGuid();

        queue.Backfill(running, seats: 1);

        Assert.Equal([first.Id], Assert.Single(Assert.Single(queue.Cycle(Start.AddMinutes(11))).Teams).Tickets);
    }
}
