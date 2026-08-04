// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Live.Cluster;
using Xunit;

namespace Vixen.Live.Orchestration.Tests;

/// <summary>
///     Doc 27 § Testing: <i>"a rollout from version A to B with players in flight, asserting nobody
///     is disconnected and <c>VersionSpread</c> reaches zero"</i>.
/// </summary>
public class RolloutTests {
    static readonly DateTimeOffset Noon = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);
    static readonly RealmVersion Old = new("0.1.0", 0xC0FFEE);
    static readonly RealmVersion New = new("0.2.0", 0xDEADBEEF);

    [Fact]
    public void A_fleet_already_on_the_target_is_settled() {
        var rollout = new Rollout(New, Noon);
        var decision = rollout.Observe([Shard(New, 40), Shard(New, 12)], Noon);

        Assert.True(decision.Complete);
        Assert.Equal(0, decision.Spread);
        Assert.Empty(decision.Drain);
    }

    [Fact]
    public void An_empty_region_has_nothing_to_move() {
        var decision = new Rollout(New, Noon).Observe([], Noon);

        Assert.True(decision.Complete);
        Assert.Equal("there are no shards to move", decision.Explain);
    }

    /// <summary>
    ///     ⚠ Every step a rollout produces is a drain, and a drain moves players out at safe moments.
    ///     A rollout that could disconnect would be the one live-ops action able to undo doc 27's
    ///     promise that nothing is force-disconnected.
    /// </summary>
    [Fact]
    public void A_rollout_only_ever_asks_for_drains() {
        var rollout = new Rollout(New, Noon);
        var decision = rollout.Observe([Shard(Old, 40), Shard(Old, 12), Shard(New, 3)], Noon);

        Assert.Equal(RolloutState.Rolling, decision.State);
        Assert.Equal(2d / 3, decision.Spread, 6);
        Assert.Equal(2, decision.Drain.Length);
    }

    /// <summary>
    ///     ⚠ Draining every old shard at once asks every player in the region to transfer inside one
    ///     window — a thundering herd against new shards that have not finished starting. It presents
    ///     as a rollout that made the game unplayable rather than as a capacity mistake.
    /// </summary>
    [Fact]
    public void The_drain_width_is_what_stops_a_rollout_being_an_outage() {
        var rollout = new Rollout(New, Noon, new() { DrainWidth = 2 });

        var decision = rollout.Observe(
            [Shard(Old, 1), Shard(Old, 2), Shard(Old, 3), Shard(Old, 4), Shard(Old, 5)],
            Noon
        );

        Assert.Equal(2, decision.Drain.Length);
    }

    [Fact]
    public void Shards_already_draining_count_against_the_width() {
        var rollout = new Rollout(New, Noon, new() { DrainWidth = 2 });

        var decision = rollout.Observe(
            [Shard(Old, 10), Shard(Old, 20), Shard(Old, 30, ShardState.Draining)],
            Noon
        );

        Assert.Single(decision.Drain);
    }

    [Fact]
    public void A_width_that_is_full_asks_for_nothing_and_says_why() {
        var rollout = new Rollout(New, Noon, new() { DrainWidth = 1 });

        var decision = rollout.Observe(
            [Shard(Old, 10), Shard(Old, 20, ShardState.Draining)],
            Noon
        );

        Assert.Empty(decision.Drain);
        Assert.Contains("which is the width", decision.Explain, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Emptiest first: a shard with four people finishes its drain in a minute and gives its
    ///     capacity back, where the busiest would hold a slot in the width for an hour.
    /// </summary>
    [Fact]
    public void The_emptiest_old_shard_drains_first() {
        var rollout = new Rollout(New, Noon, new() { DrainWidth = 1 });

        var busy = Shard(Old, 90);
        var quiet = Shard(Old, 2);

        var decision = rollout.Observe([busy, quiet], Noon);

        Assert.Equal(quiet.Shard, Assert.Single(decision.Drain));
    }

    [Fact]
    public void A_shard_that_is_not_ready_yet_is_not_drained() {
        var rollout = new Rollout(New, Noon);

        var decision = rollout.Observe([Shard(Old, 5, ShardState.Starting)], Noon);

        Assert.Empty(decision.Drain);
        Assert.Equal(1, decision.Spread);
        Assert.Contains("not ready to drain yet", decision.Explain, StringComparison.Ordinal);
    }

    // ── The three bounds on fragmentation ───────────────────────────────────────────────────────

    /// <summary>Fine for an hour and corrosive for a day, which is why the default is a day.</summary>
    [Fact]
    public void Inside_the_grace_an_old_client_still_gets_somewhere_to_play() {
        var rollout = new Rollout(New, Noon, new() { Grace = TimeSpan.FromHours(24) });

        Assert.True(rollout.Admits(Old, Noon.AddHours(23)));
        Assert.True(rollout.Admits(New, Noon.AddDays(9)));
    }

    [Fact]
    public void Past_the_grace_no_more_old_version_shards_are_started() {
        var rollout = new Rollout(New, Noon, new() { Grace = TimeSpan.FromHours(24) });

        Assert.False(rollout.Admits(Old, Noon.AddHours(24)));

        var decision = rollout.Observe([Shard(Old, 5)], Noon.AddHours(24));

        Assert.Equal(RolloutState.Forcing, decision.State);
        Assert.Contains("grace", decision.Explain, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Exactly zero: a rollout that stopped at 2 % would leave shards on the old build for ever,
    ///     and "for ever" is how a fleet ends up running four versions.
    /// </summary>
    [Fact]
    public void The_rollout_is_only_complete_at_zero_spread() {
        var rollout = new Rollout(New, Noon);

        var almost = rollout.Observe([.. Enumerable.Range(0, 49).Select(_ => Shard(New, 1)).Append(Shard(Old, 1))], Noon);

        Assert.False(almost.Complete);
        Assert.Equal(0.02, almost.Spread, 6);
    }

    // ── Rolling back ────────────────────────────────────────────────────────────────────────────

    /// <summary>Nothing about the mechanism is directional.</summary>
    [Fact]
    public void A_rollback_is_the_same_call_with_the_old_pair() {
        var rollout = new Rollout(New, Noon);

        rollout.PointAt(Old, Noon.AddHours(1));

        Assert.Equal(Old, rollout.Target);

        var decision = rollout.Observe([Shard(New, 10), Shard(Old, 10)], Noon.AddHours(1));

        Assert.Equal(RolloutState.Rolling, decision.State);
        Assert.Equal(0.5, decision.Spread, 6);
        Assert.Single(decision.Drain);
    }

    /// <summary>
    ///     ⚠ Without restarting the grace, a rollback inherits the elapsed grace of the rollout it is
    ///     undoing — putting the fleet straight into Forcing against the version everybody is already
    ///     on, which turns a rollback into an outage.
    /// </summary>
    [Fact]
    public void A_rollback_restarts_the_grace() {
        var rollout = new Rollout(New, Noon, new() { Grace = TimeSpan.FromHours(24) });

        Assert.False(rollout.Admits(Old, Noon.AddHours(25)));

        rollout.PointAt(Old, Noon.AddHours(25));

        Assert.True(rollout.Admits(New, Noon.AddHours(25)));
        Assert.Equal(RolloutState.Rolling, rollout.Observe([Shard(New, 1)], Noon.AddHours(25)).State);
    }

    [Fact]
    public void Pointing_at_the_version_it_is_already_on_changes_nothing() {
        var rollout = new Rollout(New, Noon);

        rollout.PointAt(New, Noon.AddHours(9));

        Assert.Equal(Noon, rollout.Since);
    }

    /// <summary>The whole thing, driven until it converges — § Testing's actual ask.</summary>
    [Fact]
    public void A_rollout_converges_to_zero_spread_without_disconnecting_anybody() {
        var rollout = new Rollout(New, Noon, new() { DrainWidth = 2 });
        var fleet = Enumerable.Range(0, 10).Select(_ => Shard(Old, 40)).ToList();
        var now = Noon;

        for (var round = 0; round < 40 && !rollout.Observe(fleet, now).Complete; round++) {
            var decision = rollout.Observe(fleet, now);

            // A drained shard's players move to a new-version shard, which is a transfer rather than
            // a disconnection: the population is conserved across the step.
            foreach (var shard in decision.Drain) {
                var index = fleet.FindIndex(report => report.Shard == shard);
                var moved = fleet[index].Population;

                fleet[index] = fleet[index] with { State = ShardState.Stopped, Population = 0 };
                fleet.Add(Shard(New, moved));
            }

            now = now.AddMinutes(5);
        }

        var final = rollout.Observe(fleet, now);

        Assert.True(final.Complete);
        Assert.Equal(0, final.Spread);
        Assert.Equal(400, fleet.Where(shard => shard.Key.Version == New).Sum(shard => shard.Population));
    }

    static ShardReport Shard(RealmVersion version, int population, ShardState state = ShardState.Ready) =>
        new(
            ShardId.New(),
            new("maps/queensdale", "eu", version),
            state,
            new("realm.example", 30000),
            new("pod-1"),
            population,
            new(50, 60),
            Noon,
            Noon
        );
}
