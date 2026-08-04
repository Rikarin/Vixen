// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Live.Orchestration.Tests;

/// <summary>Three traffic shapes, and the thing that must not happen in any of them.</summary>
/// <remarks>
///     Doc 27 § Testing: "simulated arrival/departure traces (flash crowd, slow bleed, sawtooth)
///     asserting the shard count does not oscillate and converges within N windows". Oscillation is
///     measured as <see cref="FleetSimulation.Turns" /> — how many times the shard count changed
///     direction — because a peak or a final count would show a fleet that ended up right after
///     spending twenty minutes thrashing.
/// </remarks>
public sealed class MapFleetTests {
    [Fact]
    public void AFlashCrowdGrowsTheFleetAndThenStops() {
        var fleet = new FleetSimulation();

        // Two hundred people zoning in over twenty seconds, onto a map sized for a hundred a shard.
        fleet.Run(seconds: 20, arriving: 10);
        fleet.Run(seconds: 100);

        Assert.Equal(200, fleet.Population);

        // ⚠ Nobody was turned away while capacity was on its way. This is what the arrival rate being
        // measured over the span arrivals landed in buys: on a rate diluted by the nominal window, a
        // burst reads as a trickle for its first minute and twenty of these two hundred were refused.
        Assert.Equal(0, fleet.Refused);

        // Enough shards to hold them, and not many more — doc 27's named failure here is twenty
        // shards for two hundred people, because every observation during the burst sees a fleet with
        // no headroom while the shards it already asked for are still loading.
        Assert.InRange(fleet.ShardCount, 2, 4);

        // It grew, and then it stopped. One direction, no turns.
        Assert.Equal(0, fleet.Turns);
    }

    [Fact]
    public void AFleetGrowsAtOneShardPerCooldownAndSaysSoRatherThanRacing() {
        // ⚠ A named limit rather than a hidden one. The debounce that stops two hundred arrivals
        // producing twenty shards also caps how fast capacity can be added: one shard per cooldown,
        // which at the defaults is a hundred players every twenty seconds. Demand above that is
        // refused — and refusing is the right failure, because the alternative is a fleet that reacts
        // to its own unfinished work.
        var fleet = new FleetSimulation();

        fleet.Run(seconds: 40, arriving: 10);
        fleet.Run(seconds: 60);

        Assert.True(fleet.Refused > 0, "twice the growth rate was absorbed, so the limit is not where this says.");
        Assert.True(
            fleet.Spawns <= 5,
            $"{fleet.Spawns} shards for forty seconds of arrivals — the debounce is not debouncing."
        );

        // And everybody who was let in is still on the map.
        Assert.Equal(fleet.Placed, fleet.Population);
    }

    [Fact]
    public void ASlowBleedGivesTheShardsBack() {
        var fleet = new FleetSimulation();

        // Fill the map, then let it empty over ten minutes.
        fleet.Run(seconds: 40, arriving: 10);
        fleet.Run(seconds: 60);

        var peak = fleet.ShardCount;

        Assert.True(peak >= 3, $"the fleet only reached {peak} shards, so there is nothing to give back.");

        fleet.Run(seconds: 600, leaving: 1);

        // Converged, all the way back to the floor: one shard, and a map nobody is on.
        Assert.Equal(0, fleet.Population);
        Assert.Equal(1, fleet.ShardCount);
    }

    [Fact]
    public void ASlowBleedLosesNobodyOnTheWayDown() {
        var fleet = new FleetSimulation();

        fleet.Run(seconds: 40, arriving: 10);
        fleet.Run(seconds: 60);
        fleet.Run(seconds: 600, leaving: 1);

        // Draining moves players; it never disconnects them (doc 27 § Drain). So the population is
        // exactly what was let in minus what walked out, however many shards were retired underneath.
        Assert.Equal(fleet.Placed - fleet.Left, fleet.Population);
        Assert.True(fleet.Drains > 0, "no shard was retired, so nothing was being asserted about draining.");
    }

    [Fact]
    public void ASawtoothSettlesOnThePeakRatherThanChasingTheTrough() {
        var fleet = new FleetSimulation();

        // A map that fills and empties every three minutes for half an hour — a world boss on a
        // timer, which is the shape that finds a fleet arguing with itself.
        for (var cycle = 0; cycle < 10; cycle++) {
            fleet.Run(seconds: 60, arriving: 5);
            fleet.Run(seconds: 120, leaving: 3);
        }

        // ⚠ Converging on the peak is the CORRECT answer, and it is worth being explicit because
        // "returns to one shard" is the intuitive one and is wrong. The dwell exists so that a
        // two-minute trough does not retire capacity a two-minute peak is about to need; a fleet that
        // collapsed every trough would spend the next peak refusing people while shards load.
        var settled = fleet.Counts.TakeLast(300).ToList();

        Assert.True(
            settled.Max() - settled.Min() <= 1,
            $"the last five minutes ranged over {settled.Min()}–{settled.Max()} shards."
        );

        // And it got there without thrashing: a spawn or a drain every few minutes, not every few
        // seconds. Thirty minutes of this traffic is 1 800 observations.
        Assert.True(
            fleet.Spawns + fleet.Drains < 30,
            $"{fleet.Spawns} spawns and {fleet.Drains} drains over half an hour."
        );
    }

    [Fact]
    public void HysteresisIsWhatStopsTheSawtoothThrashing() {
        // The same traffic against a fleet whose merge fires on any dip, which is what the dwell and
        // the asymmetric thresholds are protecting against. Kept as a test rather than a comment
        // because it is the failure the defaults exist for, and it should be visible to whoever
        // proposes tuning them.
        var damped = new FleetSimulation();

        var twitchy = new FleetSimulation(
            FleetPolicy.Default with { MergeDwell = TimeSpan.Zero, MergeBelow = 0.9 }
        );

        foreach (var run in new[] { damped, twitchy }) {
            for (var cycle = 0; cycle < 10; cycle++) {
                run.Run(seconds: 60, arriving: 5);
                run.Run(seconds: 120, leaving: 3);
            }
        }

        Assert.True(
            twitchy.Turns > damped.Turns * 2,
            $"a fleet with no hysteresis turned {twitchy.Turns} times against {damped.Turns} — "
            + "the dwell and the thresholds are doing nothing."
        );

        Assert.True(twitchy.Refused > damped.Refused, "and it refused no more people than the damped one.");
    }

    [Fact]
    public void AMapWithNothingOnItStaysAtOneShard() {
        var fleet = new FleetSimulation();

        fleet.Run(seconds: 900);

        // Nothing to merge — one shard is the floor, and a fleet that drained it would be a map
        // nobody can enter without waiting for a process to start.
        Assert.Equal(1, fleet.ShardCount);
        Assert.Equal(0, fleet.Drains);
        Assert.Equal(0, fleet.Turns);
    }

    // ── The rules underneath, one at a time ─────────────────────────────────────────────────────

    static ShardCandidate Shard(int population, ShardState state = ShardState.Ready, ShardId? id = null) =>
        new() {
            Shard = id ?? ShardId.New(),
            Key = new("maps/queensdale", "eu", new("0.1.0", 1)),
            State = state,
            Population = population,
            Capacity = new(100, 120)
        };

    static readonly DateTimeOffset Noon = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AMapWithNoShardAtAllSpawnsImmediately() {
        var fleet = new MapFleet(new("maps/queensdale", "eu", new("0.1.0", 1)));

        var action = fleet.Observe(Noon, []);

        Assert.Equal(FleetActionKind.Spawn, action.Kind);
        Assert.Contains("no shard", action.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AShardAlreadyStartingIsCapacityOnItsWay() {
        var fleet = new MapFleet(new("maps/queensdale", "eu", new("0.1.0", 1)));

        // Nothing ready, but something coming. Asking for a second one is the twenty-shards failure.
        Assert.Equal(FleetActionKind.None, fleet.Observe(Noon, [Shard(0, ShardState.Starting)]).Kind);
    }

    [Fact]
    public void TheCooldownIsWhatStopsABurstBecomingAFleet() {
        var key = new ShardKey("maps/queensdale", "eu", new("0.1.0", 1));
        var fleet = new MapFleet(key);

        fleet.Arrived(Noon, 200);

        var full = new[] { Shard(100) };

        Assert.Equal(FleetActionKind.Spawn, fleet.Observe(Noon, full).Kind);

        // A second ago is inside the cooldown; a minute later is not.
        Assert.Equal(FleetActionKind.None, fleet.Observe(Noon + TimeSpan.FromSeconds(1), full).Kind);
        Assert.Equal(FleetActionKind.Spawn, fleet.Observe(Noon + TimeSpan.FromSeconds(60), full).Kind);
    }

    [Fact]
    public void TheCeilingStopsTheSpawningRatherThanRaising() {
        var fleet = new MapFleet(new("maps/queensdale", "eu", new("0.1.0", 1)), FleetPolicy.Default with { MaxShards = 2 });

        fleet.Arrived(Noon, 500);

        Assert.Equal(FleetActionKind.None, fleet.Observe(Noon, [Shard(100), Shard(100)]).Kind);
    }

    [Fact]
    public void MergingWaitsForTheDwellAndThenTakesTheEmptiestShard() {
        var key = new ShardKey("maps/queensdale", "eu", new("0.1.0", 1));
        var fleet = new MapFleet(key);

        var emptiest = Shard(5, id: new(Guid.Parse("00000000-0000-0000-0000-000000000001")));
        var quiet = new[] { Shard(20), emptiest, Shard(10) };

        Assert.Equal(FleetActionKind.None, fleet.Observe(Noon, quiet).Kind);
        Assert.Equal(FleetActionKind.None, fleet.Observe(Noon + TimeSpan.FromSeconds(119), quiet).Kind);

        var action = fleet.Observe(Noon + TimeSpan.FromSeconds(121), quiet);

        Assert.Equal(FleetActionKind.Drain, action.Kind);
        Assert.Equal(emptiest.Shard, action.Shard);
    }

    [Fact]
    public void ABusyMinuteResetsTheDwell() {
        var key = new ShardKey("maps/queensdale", "eu", new("0.1.0", 1));
        var fleet = new MapFleet(key);

        var quiet = new[] { Shard(10), Shard(10) };
        var busy = new[] { Shard(10), Shard(90) };

        fleet.Observe(Noon, quiet);
        fleet.Observe(Noon + TimeSpan.FromSeconds(100), busy);

        // The dwell restarts, so the merge is not two minutes after the first quiet observation.
        Assert.Equal(FleetActionKind.None, fleet.Observe(Noon + TimeSpan.FromSeconds(130), quiet).Kind);
        Assert.Equal(FleetActionKind.Drain, fleet.Observe(Noon + TimeSpan.FromSeconds(260), quiet).Kind);
    }

    [Fact]
    public void OneQuietShardIsNotAMerge() {
        var fleet = new MapFleet(new("maps/queensdale", "eu", new("0.1.0", 1)));

        var mostlyBusy = new[] { Shard(5), Shard(80) };

        Assert.Equal(FleetActionKind.None, fleet.Observe(Noon, mostlyBusy).Kind);
        Assert.Equal(FleetActionKind.None, fleet.Observe(Noon + TimeSpan.FromMinutes(10), mostlyBusy).Kind);
    }

    [Fact]
    public void ASpawnResetsTheMergeDwellRatherThanLeavingItRunning() {
        var key = new ShardKey("maps/queensdale", "eu", new("0.1.0", 1));
        var fleet = new MapFleet(key);

        var quiet = new[] { Shard(10), Shard(10) };

        fleet.Observe(Noon, quiet);

        // A crowd arrives and the fleet asks for a shard. Draining one two minutes later because the
        // map was quiet beforehand is exactly the oscillation this class exists to prevent.
        fleet.Arrived(Noon + TimeSpan.FromSeconds(30), 400);

        Assert.Equal(FleetActionKind.Spawn, fleet.Observe(Noon + TimeSpan.FromSeconds(30), [Shard(100), Shard(100)]).Kind);
        Assert.Equal(FleetActionKind.None, fleet.Observe(Noon + TimeSpan.FromSeconds(125), quiet).Kind);
    }

    [Fact]
    public void TheRateIsMeasuredOverTheSpanArrivalsLandedIn() {
        var fleet = new MapFleet(new("maps/queensdale", "eu", new("0.1.0", 1)));

        fleet.Arrived(Noon, 60);

        // ⚠ Not sixty over the nominal window. Dividing by the window makes a burst read as a
        // trickle until the window fills, which is how a fleet ends up spawning after saturation
        // instead of before it — FleetSimulation's flash crowd refused twenty of two hundred on the
        // arithmetic this replaces.
        Assert.Equal(60 / FleetPolicy.Default.MinimumRateSpan.TotalSeconds, fleet.ArrivalRate(Noon), 6);

        // As the burst recedes the same arrivals read as a lower rate, because they are being spread
        // over the time that has passed since.
        Assert.Equal(60 / 20.0, fleet.ArrivalRate(Noon + TimeSpan.FromSeconds(20)), 6);
        Assert.Equal(0.0, fleet.ArrivalRate(Noon + TimeSpan.FromSeconds(31)), 6);
    }

    [Fact]
    public void OneMomentIsNotARate() {
        var fleet = new MapFleet(new("maps/queensdale", "eu", new("0.1.0", 1)));

        // A party of ten zoning in together is ten people, not ten a second. Without the floor under
        // the span they would extrapolate to three hundred over the lead time and spawn a shard.
        fleet.Arrived(Noon, 10);

        Assert.Equal(10 / FleetPolicy.Default.MinimumRateSpan.TotalSeconds, fleet.ArrivalRate(Noon), 6);
        Assert.Equal(FleetActionKind.None, fleet.Observe(Noon, [Shard(10)]).Kind);
    }
}
