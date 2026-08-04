// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Live.Persistence;
using Vixen.Live.Transfer;
using Xunit;

namespace Vixen.Live.Realms.Tests;

/// <summary>
///     Doc 27 § Testing's end-to-end leg: three realms in one process, players walking a loop between
///     them, asserting no duplicate spawn, no lost entity and no state divergence.
/// </summary>
/// <remarks>
///     ⚠ <b>The assertion that matters is not "the transfers worked".</b> It is that no player is
///     ever believed in by two realms at once and that the total in the world never moves — because a
///     fleet where transfers merely <em>usually</em> work is a fleet that duplicates items rarely,
///     which is worse than one that fails loudly.
/// </remarks>
public class TransferOracleTests {
    [Fact]
    public void A_player_walks_from_one_realm_to_another_and_arrives_once() {
        using var fleet = new TransferFleet();

        var queensdale = fleet.AddRealm("maps/queensdale");
        var divinity = fleet.AddRealm("maps/divinity");
        var bruna = fleet.Admit(queensdale, 100);

        Assert.True(fleet.Send(bruna, divinity));

        fleet.Pump(12);

        Assert.Equal(divinity, bruna.Where);
        Assert.Equal(1, bruna.Arrivals);
        Assert.Contains(bruna.Key, divinity.Residents);
        Assert.DoesNotContain(bruna.Key, queensdale.Residents);
        Assert.Equal(100, fleet.Holding(bruna));
        Assert.Equal(0, fleet.TotalInWorld(TransferFleet.Gold));
    }

    /// <summary>
    ///     ⚠ The overlap's whole purpose: they are simulated on the source for every step until the
    ///     handoff is acknowledged, and on exactly one realm at every instant in between.
    /// </summary>
    [Fact]
    public void A_player_is_never_resident_on_two_realms_at_once() {
        using var fleet = new TransferFleet();

        var a = fleet.AddRealm("maps/a");
        var b = fleet.AddRealm("maps/b");
        var bruna = fleet.Admit(a, 100);

        fleet.Send(bruna, b);

        for (var step = 0; step < 20; step++) {
            fleet.Pump();

            var homes = fleet.Realms.Count(realm => realm.Residents.Contains(bruna.Key));

            Assert.Equal(1, homes);
        }
    }

    /// <summary>
    ///     ⚠ The lease epoch is the boundary, and it only ever rises. A realm that kept writing after
    ///     losing it would be refused by the fence rather than believed.
    /// </summary>
    [Fact]
    public void Every_arrival_raises_the_fence_and_the_old_realm_can_no_longer_write() {
        using var fleet = new TransferFleet();

        var a = fleet.AddRealm("maps/a");
        var b = fleet.AddRealm("maps/b");
        var bruna = fleet.Admit(a, 100);

        fleet.Send(bruna, b);
        fleet.Pump(12);

        Assert.Equal(2, bruna.Epoch);

        // The realm they left, still flushing under the epoch it used to hold.
        Assert.Equal(LedgerVerdict.Superseded, fleet.SpendAt(bruna, 1, 10));
        Assert.Equal(100, fleet.Holding(bruna));
    }

    [Fact]
    public void A_realm_that_is_full_refuses_the_reservation_and_the_player_stays_put() {
        using var fleet = new TransferFleet();

        var a = fleet.AddRealm("maps/a");
        var b = fleet.AddRealm("maps/b");
        var bruna = fleet.Admit(a, 100);

        // Fill the target to its hard cap with residents who are not going anywhere.
        for (var index = 0; index < 120; index++) {
            b.Residents.Add(new(Guid.NewGuid(), Guid.NewGuid()));
        }

        Assert.False(fleet.Send(bruna, b));

        fleet.Pump(6);

        Assert.Equal(a, bruna.Where);
        Assert.Contains(bruna.Key, a.Residents);
        Assert.Equal(0, fleet.TotalInWorld(TransferFleet.Gold));
    }

    /// <summary>
    ///     A transfer nobody drives runs out its deadline and the player is still there — which is
    ///     the abort path a client that closed its laptop mid-zone takes.
    /// </summary>
    [Fact]
    public void A_client_that_never_arrives_leaves_the_player_where_they_were() {
        using var fleet = new TransferFleet();

        var a = fleet.AddRealm("maps/a");
        var b = fleet.AddRealm("maps/b");
        var bruna = fleet.Admit(a, 100);

        fleet.Send(bruna, b);

        // Take the client out of the loop entirely, then run past the overlap deadline.
        bruna.Client.Abandon();
        bruna.InFlight = null;

        // Past the two-second overlap deadline this fleet is configured with.
        fleet.Pump(200);

        Assert.False(a.Transfers.IsLeaving(bruna.Key));
        Assert.Equal(a, bruna.Where);
        Assert.Contains(bruna.Key, a.Residents);
        Assert.Equal(100, fleet.Holding(bruna));

        var histogram = a.Transfers.Metrics.AbortHistogram();

        Assert.Contains(histogram, entry => entry.Reason == TransferAbort.ClientNeverArrived);
    }

    /// <summary>
    ///     ⚠ <b>The duplication oracle.</b> Twelve travellers walking between four realms for
    ///     thousands of steps, and the two things that must be true after every single one of them:
    ///     nobody is in two places, and the world's total is unchanged.
    /// </summary>
    [Theory]
    [InlineData(20260804)]
    [InlineData(17)]
    [InlineData(0xF00D)]
    public void Nobody_is_ever_in_two_places_and_the_world_total_never_moves(int seed) {
        using var fleet = new TransferFleet();

        var maps = new[] { "maps/queensdale", "maps/divinity", "maps/harathi", "maps/lornar" }
            .Select(fleet.AddRealm)
            .ToList();

        var random = new Random(seed);
        var travellers = Enumerable.Range(0, 12)
            .Select(index => fleet.Admit(maps[index % maps.Count], 100 + index))
            .ToList();

        var expected = travellers.Sum(traveller => fleet.Holding(traveller));

        for (var step = 0; step < 1_500; step++) {
            // Somebody decides to walk through a portal.
            if (random.Next(4) == 0) {
                var who = travellers[random.Next(travellers.Count)];
                var target = maps[random.Next(maps.Count)];

                fleet.Send(who, target);
            }

            fleet.Pump();

            // ── The oracle, after every step ────────────────────────────────────────────────────
            foreach (var traveller in travellers) {
                var homes = fleet.Realms.Count(realm => realm.Residents.Contains(traveller.Key));

                Assert.Equal(1, homes);
            }

            Assert.Equal(0, fleet.TotalInWorld(TransferFleet.Gold));
        }

        // Nothing was lost, nothing was created, and people actually moved.
        Assert.Equal(expected, travellers.Sum(traveller => fleet.Holding(traveller)));
        Assert.True(fleet.Committed > 20, $"only {fleet.Committed} transfers committed — the loop is not exercising anything");

        foreach (var traveller in travellers) {
            Assert.Equal(traveller.Epoch, fleet.Fence(traveller));
        }
    }

    /// <summary>
    ///     The same run with every realm's clock and every deadline against it: transfers are started
    ///     faster than they can finish, so most of them abort. The oracle must hold anyway.
    /// </summary>
    [Fact]
    public void The_oracle_holds_when_most_transfers_are_aborting() {
        using var fleet = new TransferFleet();

        var a = fleet.AddRealm("maps/a");
        var b = fleet.AddRealm("maps/b");
        var travellers = Enumerable.Range(0, 8).Select(index => fleet.Admit(index % 2 == 0 ? a : b, 50)).ToList();

        var random = new Random(20260804);

        for (var step = 0; step < 800; step++) {
            foreach (var traveller in travellers) {
                if (random.Next(3) == 0) {
                    fleet.Send(traveller, random.Next(2) == 0 ? a : b);
                }

                // Half the time, the client simply gives up mid-flight.
                if (traveller.InFlight is not null && random.Next(5) == 0) {
                    traveller.Client.Abandon();
                    traveller.InFlight = null;
                    traveller.Where.Transfers.TryGet(traveller.Key, out var transfer);
                    transfer?.Stop(TransferAbort.PlayerGone, fleet.Now);
                    fleet.NoteAbort();
                }
            }

            fleet.Pump();

            foreach (var traveller in travellers) {
                Assert.Equal(1, fleet.Realms.Count(realm => realm.Residents.Contains(traveller.Key)));
            }

            Assert.Equal(0, fleet.TotalInWorld(TransferFleet.Gold));
        }

        Assert.Equal(400, travellers.Sum(traveller => fleet.Holding(traveller)));
    }
}
