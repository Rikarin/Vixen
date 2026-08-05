// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Live.Persistence;
using Vixen.Live.Transfer;
using Vixen.Net.Transport;
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
            b.Ghosts.Add(new(Guid.NewGuid(), Guid.NewGuid()));
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

        // ⚠ Settled first. The lease moves at Committing and the traveller's own epoch moves at
        // Arrive, so stopping the loop between the two leaves the fence one ahead of the player —
        // correctly, and briefly. Asserting into the middle of a transfer is asserting a race.
        Assert.True(fleet.Settle(), "the fleet never settled");

        // Nothing was lost, nothing was created, and people actually moved.
        Assert.Equal(expected, travellers.Sum(traveller => fleet.Holding(traveller)));
        Assert.True(fleet.Committed > 20, $"only {fleet.Committed} transfers committed — the loop is not exercising anything");

        foreach (var traveller in travellers) {
            Assert.Equal(traveller.Epoch, fleet.Fence(traveller));
        }
    }

    /// <summary>Asserts a traveller is being simulated by exactly one realm, and says what if not.</summary>
    /// <remarks>
    ///     ⚠ <b>The message is the point.</b> "Expected 1, actual 2" over a three-thousand-step run is
    ///     a bisect; naming each realm's admission and arrival state says which of the two mechanisms
    ///     disagreed, which is how the unbound-admission bug was found in one run instead of ten.
    /// </remarks>
    static void OneHome(TransferFleet fleet, TransferFleet.Traveller traveller, int step) {
        var homes = fleet.Realms.Count(realm => realm.Residents.Contains(traveller.Key));

        if (homes == 1) {
            return;
        }

        var detail = string.Join(
            " | ",
            fleet.Realms.Select(realm =>
                $"{realm.Map}: joined={realm.Joined(traveller.Key) is not null} "
                + $"arrival={realm.Transfers.Arriving.Arrivals.FirstOrDefault(entry => entry.Player == traveller.Key)?.State.ToString() ?? "none"}")
        );

        Assert.Fail(
            $"step {step}: {homes} realms simulate {traveller.Key}, in flight={traveller.InFlight is not null}, "
            + $"client={traveller.Client.State} :: {detail}"
        );
    }

    // ── Under a network that loses things ───────────────────────────────────────────────────────

    /// <summary>The three wires worth asserting against, and one that duplicates everything.</summary>
    /// <remarks>
    ///     <c>Awful</c> is the one that matters. A profile nobody would ship on is the profile a
    ///     player on a train has, and "it works on broadband" is not a claim about transfers.
    /// </remarks>
    public static TheoryData<string> Wires => ["Mobile", "Awful", "Duplicating"];

    static NetworkSimulationProfile Profile(string name) =>
        name switch {
            "Mobile" => NetworkSimulationProfile.Mobile,
            "Awful" => NetworkSimulationProfile.Awful,
            _ => NetworkSimulationProfile.Broadband with { DuplicateChance = 0.25 }
        };

    /// <summary>
    ///     ⚠ <b>The leg doc 27 § Testing names and this file used to say plainly that it did not
    ///     have.</b> Every traveller now holds a real session, and during the overlap a second one to
    ///     the target — so the wire carries a handshake that a loss profile can actually spoil, and
    ///     residency is what each realm's own <c>PlayerAdmission</c> says rather than what the harness
    ///     remembered to update.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The assertions are about the outcome and never about the number of steps.</b> A lossy
    ///     wire takes as many attempts as it takes; asserting "committed within N pumps" would be
    ///     asserting the loss rate, and it would go red on a profile change rather than on a bug.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Wires))]
    public void The_oracle_holds_over_a_wire_that_loses_things(string wire) {
        using var fleet = new TransferFleet {
            Wire = Profile(wire),
            Seed = 20260805,

            // ⚠ Longer than the clean-wire fleet's, because on Awful the second session's handshake
            // is hundreds of steps and the overlap is what it happens inside. Two seconds there would
            // abort every transfer before the network had finished being slow, and the run would
            // prove that a deadline works rather than that a transfer does.
            Deadlines = new() {
                Placing = TimeSpan.FromSeconds(2),
                Preparing = TimeSpan.FromSeconds(2),
                Overlapping = TimeSpan.FromSeconds(20),
                Committing = TimeSpan.FromSeconds(2),
                HandingOff = TimeSpan.FromSeconds(2)
            }
        };

        var a = fleet.AddRealm("maps/a");
        var b = fleet.AddRealm("maps/b");
        var travellers = Enumerable.Range(0, 3).Select(index => fleet.Admit(index % 2 == 0 ? a : b, 100 + index)).ToList();
        var expected = travellers.Sum(fleet.Holding);

        for (var step = 0; step < 3_000; step++) {
            foreach (var traveller in travellers) {
                fleet.Send(traveller, traveller.Where == a ? b : a);
            }

            fleet.Pump();

            foreach (var traveller in travellers) {
                OneHome(fleet, traveller, step);
            }

            Assert.Equal(0, fleet.TotalInWorld(TransferFleet.Gold));
        }

        Assert.True(fleet.Settle(), "the fleet never settled");
        Assert.Equal(expected, travellers.Sum(fleet.Holding));

        // Not a rate and not a deadline: only that the wire did not make the protocol impossible.
        Assert.True(fleet.Committed > 0, $"nothing committed over {wire} — the transfer never survives this wire");

        foreach (var traveller in travellers) {
            Assert.Equal(traveller.Epoch, fleet.Fence(traveller));
        }
    }

    // ── Bounded prediction resets ───────────────────────────────────────────────────────────────

    /// <summary>
    ///     ⚠ <b>Doc 27 § Intra-map seams states the price of a transfer and this is the assertion of
    ///     it:</b> <i>"the visible cost of a transfer is one interpolation delay of extra smoothing and
    ///     one prediction reset"</i>. One per commit, none per abort.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Every traveller runs a real <c>ClientPrediction</c> that steps on every pump</b>, so
    ///     the history being thrown away had something in it. A reset counter over a prediction loop
    ///     that never ran reads zero for the wrong reason, which is why this file previously declined
    ///     to assert one at all rather than assert it green.
    /// </remarks>
    [Fact]
    public void A_transfer_costs_exactly_one_prediction_reset_and_an_abort_costs_none() {
        using var fleet = new TransferFleet();

        var a = fleet.AddRealm("maps/a");
        var b = fleet.AddRealm("maps/b");
        var travellers = Enumerable.Range(0, 6).Select(index => fleet.Admit(index % 2 == 0 ? a : b, 50)).ToList();

        var random = new Random(20260805);

        for (var step = 0; step < 800; step++) {
            foreach (var traveller in travellers) {
                if (random.Next(3) == 0) {
                    fleet.Send(traveller, random.Next(2) == 0 ? a : b);
                }

                // ⚠ Per *step*, not per transfer, and a transfer now spans many steps: one in five
                // here kills essentially all of them. One in eighty leaves a mix, which is what makes
                // "an abort costs no reset" a claim about something that happened.
                if (traveller.InFlight is not null && random.Next(80) == 0) {
                    fleet.GiveUp(traveller);
                }
            }

            fleet.Pump();
        }

        Assert.True(fleet.Settle(), "the fleet never settled");
        Assert.True(fleet.Committed > 5, $"only {fleet.Committed} transfers committed");
        Assert.True(fleet.Aborted > 0, "no transfer was ever given up on");

        foreach (var traveller in travellers) {
            // The protocol's own counter — doc 27's PredictionResetCount — and the client's history.
            Assert.Equal(traveller.Arrivals, traveller.Client.PredictionResets);
            Assert.Equal(traveller.Arrivals, traveller.Prediction.Resets);

            // ⚠ The reset threw work away rather than being a counter nobody had fed.
            Assert.True(
                traveller.Prediction.Discarded >= traveller.Arrivals,
                $"{traveller.Arrivals} resets discarded {traveller.Prediction.Discarded} predicted ticks"
            );

            // ⚠ A seam is a clear and never a rollback: the state to replay from belongs to a
            // simulation that no longer owns this player.
            Assert.Equal(0, traveller.Prediction.Resimulated);
        }

        Assert.Equal(
            fleet.Committed,
            fleet.Realms.Sum(realm => realm.Transfers.Metrics.PredictionResets)
        );
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
                    fleet.GiveUp(traveller);
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
