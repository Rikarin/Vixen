// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Xunit;

namespace Vixen.Live.Transfer.Tests;

/// <summary>The receiving realm: slots held, dormancy, and the slots nobody comes for.</summary>
public class ArrivalTests : IDisposable {
    static readonly DateTimeOffset Noon = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);
    static readonly ShardId Here = ShardId.New();

    readonly TransferTicketSigner signer = new(Encoding.UTF8.GetBytes("a cluster key that is long enough."));

    [Fact]
    public void A_slot_is_held_then_taken_then_woken() {
        var board = new TransferBoard();
        var who = Somebody();

        Assert.Equal(ReservationRefusal.None, board.Reserve(Ticket(who), 5, Noon, true, false));
        Assert.Equal(1, board.Pending);

        Assert.True(board.Arrived(who, 5));
        Assert.Equal(1, board.Pending);
        Assert.Equal(ArrivalState.Dormant, Assert.Single(board.Arrivals).State);

        Assert.True(board.Woke(who, 5));
        Assert.Equal(0, board.Pending);
        Assert.Equal(ArrivalState.Live, Assert.Single(board.Arrivals).State);
    }

    /// <summary>
    ///     ⚠ Without pre-reservation a map at 99 % could promise the same last slot to twenty players
    ///     in flight and refuse nineteen of them at the door — each after loading the map.
    /// </summary>
    [Fact]
    public void A_pending_arrival_is_capacity_that_is_already_spent() {
        var board = new TransferBoard();

        Assert.Equal(ReservationRefusal.None, board.Reserve(Ticket(Somebody()), 5, Noon, true, false));
        Assert.Equal(1, board.Pending);

        // The realm decides there is no room BECAUSE of the pending one, and says so.
        Assert.Equal(ReservationRefusal.Full, board.Reserve(Ticket(Somebody()), 5, Noon, false, false));
    }

    [Fact]
    public void A_draining_shard_takes_nobody_new() {
        var board = new TransferBoard();

        Assert.Equal(ReservationRefusal.Draining, board.Reserve(Ticket(Somebody()), 5, Noon, true, true));
        Assert.Empty(board.Arrivals);
    }

    [Fact]
    public void An_expired_ticket_reserves_nothing() {
        var board = new TransferBoard();

        var refusal = board.Reserve(Ticket(Somebody(), Noon.AddSeconds(-1)), 5, Noon, true, false);

        Assert.Equal(ReservationRefusal.BadTicket, refusal);
    }

    [Fact]
    public void Reserving_twice_for_one_player_is_refused() {
        var board = new TransferBoard();
        var who = Somebody();

        board.Reserve(Ticket(who), 5, Noon, true, false);

        Assert.Equal(ReservationRefusal.AlreadyHere, board.Reserve(Ticket(who), 5, Noon, true, false));
    }

    /// <summary>
    ///     A higher epoch is the same person re-placed after their first attempt died. Refusing it
    ///     would leave them held out by the corpse of their own earlier transfer.
    /// </summary>
    [Fact]
    public void A_higher_epoch_replaces_a_reservation_rather_than_colliding_with_it() {
        var board = new TransferBoard();
        var who = Somebody();

        board.Reserve(Ticket(who), 5, Noon, true, false);

        Assert.Equal(ReservationRefusal.None, board.Reserve(Ticket(who), 6, Noon, true, false));
        Assert.Equal(6, Assert.Single(board.Arrivals).Epoch);
        Assert.Equal(1, board.Pending);
    }

    [Fact]
    public void An_arrival_at_the_wrong_epoch_is_not_the_one_being_held_for() {
        var board = new TransferBoard();
        var who = Somebody();

        board.Reserve(Ticket(who), 5, Noon, true, false);

        Assert.False(board.Arrived(who, 4));
        Assert.False(board.Woke(who, 5));            // still Reserved, not Dormant
        Assert.True(board.Arrived(who, 5));
    }

    /// <summary>A held slot whose client never arrives is capacity nobody can use.</summary>
    [Fact]
    public void A_slot_nobody_comes_for_is_swept() {
        var board = new TransferBoard { ReservationLifetime = TimeSpan.FromSeconds(45) };
        var who = Somebody();

        board.Reserve(Ticket(who), 5, Noon, true, false);

        Assert.Empty(board.Sweep(Noon.AddSeconds(44)));
        Assert.Equal([who], board.Sweep(Noon.AddSeconds(45)));
        Assert.Equal(0, board.Pending);
    }

    [Fact]
    public void A_reservation_whose_ticket_expired_is_swept_even_inside_its_lifetime() {
        var board = new TransferBoard { ReservationLifetime = TimeSpan.FromMinutes(10) };
        var who = Somebody();

        board.Reserve(Ticket(who, Noon.AddSeconds(30)), 5, Noon, true, false);

        Assert.Equal([who], board.Sweep(Noon.AddSeconds(31)));
    }

    [Fact]
    public void A_live_arrival_is_never_swept() {
        var board = new TransferBoard { ReservationLifetime = TimeSpan.FromSeconds(1) };
        var who = Somebody();

        board.Reserve(Ticket(who, Noon.AddDays(1)), 5, Noon, true, false);
        board.Arrived(who, 5);
        board.Woke(who, 5);

        Assert.Empty(board.Sweep(Noon.AddHours(1)));
        Assert.Single(board.Arrivals);
    }

    [Fact]
    public void A_dormant_arrival_gets_the_longer_lifetime() {
        var board = new TransferBoard {
            ReservationLifetime = TimeSpan.FromSeconds(45),
            DormantLifetime = TimeSpan.FromMinutes(5)
        };
        var who = Somebody();

        board.Reserve(Ticket(who, Noon.AddDays(1)), 5, Noon, true, false);
        board.Arrived(who, 5);

        Assert.Empty(board.Sweep(Noon.AddMinutes(4)));
        Assert.Equal([who], board.Sweep(Noon.AddMinutes(5)));
    }

    [Fact]
    public void Releasing_gives_the_slot_back() {
        var board = new TransferBoard();
        var who = Somebody();

        board.Reserve(Ticket(who), 5, Noon, true, false);

        Assert.True(board.Release(who));
        Assert.False(board.Release(who));
        Assert.Equal(0, board.Pending);
    }

    /// <summary>Releases the cluster key.</summary>
    public void Dispose() {
        signer.Dispose();
        GC.SuppressFinalize(this);
    }

    static PlayerKey Somebody() => new(Guid.NewGuid(), Guid.NewGuid());

    TransferTicket Ticket(PlayerKey who, DateTimeOffset? expires = null) =>
        signer.Sign(
            new() {
                Player = who,
                Target = Here,
                Endpoint = new("realm.example", 30001),
                LeaseEpoch = 5,
                Expires = expires ?? Noon.AddMinutes(2)
            }
        );
}
