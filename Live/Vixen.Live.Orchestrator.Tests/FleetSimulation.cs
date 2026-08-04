// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Live.Orchestration.Tests;

/// <summary>A map's fleet, a second at a time, with nothing real in it.</summary>
/// <remarks>
///     <para>
///         Doc 27 § Testing wants "simulated arrival/departure traces (flash crowd, slow bleed,
///         sawtooth) asserting the shard count does not oscillate and converges within N windows".
///         This is the thing that runs them: half an hour of traffic in a few milliseconds, because
///         the clock is a parameter and there are no processes.
///     </para>
///     <para>
///         ⚠ <b>It models the two delays that make hysteresis necessary and nothing else.</b> A
///         spawned shard takes <see cref="Startup" /> to become placeable — which is why a fleet that
///         only spawned at saturation would spend those seconds refusing people — and a drained shard
///         does not vanish until its players have been moved. Everything else about a realm is
///         irrelevant to whether the shard count settles.
///     </para>
/// </remarks>
sealed class FleetSimulation {
    static readonly ShardKey Key = new("maps/queensdale", "eu", new("0.1.0", 0xC0FFEE));

    readonly List<Simulated> shards = [];
    readonly PlacementDirector director = new();
    readonly MapFleet fleet;
    readonly Random random;
    readonly ShardCapacity capacity;

    DateTimeOffset now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    /// <summary>How long a shard takes to load its map and report ready.</summary>
    public TimeSpan Startup { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>How many shards have ever been spawned.</summary>
    public int Spawns { get; private set; }

    /// <summary>How many have ever been drained.</summary>
    public int Drains { get; private set; }

    /// <summary>How many arrivals found nowhere to go.</summary>
    public int Refused { get; private set; }

    /// <summary>How many arrivals were placed.</summary>
    public int Placed { get; private set; }

    /// <summary>How many players have left of their own accord.</summary>
    public int Left { get; private set; }

    /// <summary>The shard count at the end of every second, in order.</summary>
    public List<int> Counts { get; } = [];

    /// <summary>Shards that exist at all — starting, ready or draining.</summary>
    public int ShardCount => shards.Count;

    /// <summary>Shards a player could be placed on.</summary>
    public int ReadyCount => shards.Count(shard => shard.State == ShardState.Ready);

    /// <summary>Everybody on the map.</summary>
    public int Population => shards.Sum(shard => shard.Population);

    public FleetSimulation(FleetPolicy? policy = null, ShardCapacity? capacity = null, int seed = 20260804) {
        fleet = new(Key, policy);
        random = new(seed);
        this.capacity = capacity ?? new(100, 120);

        // Every map starts with one shard, as an orchestrator's first placement would have produced.
        Spawn();
        shards[0].State = ShardState.Ready;
    }

    /// <summary>Runs a second: arrivals, departures, the fleet's decision, and the consequences.</summary>
    /// <param name="arriving">How many players zone in.</param>
    /// <param name="leaving">How many log out.</param>
    public void Second(int arriving = 0, int leaving = 0) {
        now += TimeSpan.FromSeconds(1);

        foreach (var shard in shards) {
            if (shard.State == ShardState.Starting && now >= shard.ReadyAt) {
                shard.State = ShardState.Ready;
            }
        }

        Depart(leaving);
        Arrive(arriving);
        MoveDrainingPlayers();

        var action = fleet.Observe(now, Candidates());

        switch (action.Kind) {
            case FleetActionKind.Spawn:
                Spawn();

                break;

            case FleetActionKind.Drain when shards.SingleOrDefault(shard => shard.Shard == action.Shard) is { } going:
                going.State = ShardState.Draining;
                Drains++;

                break;

            default:
                break;
        }

        // A drained shard that has emptied stops, which is what makes the count come back down.
        shards.RemoveAll(shard => shard.State == ShardState.Draining && shard.Population == 0);
        Counts.Add(shards.Count);
    }

    /// <summary>Runs a while with the same traffic every second.</summary>
    /// <param name="seconds">How long.</param>
    /// <param name="arriving">Arrivals a second.</param>
    /// <param name="leaving">Departures a second.</param>
    public void Run(int seconds, int arriving = 0, int leaving = 0) {
        for (var second = 0; second < seconds; second++) {
            Second(arriving, leaving);
        }
    }

    /// <summary>The most shards that existed at once during the run.</summary>
    public int Peak => Counts.Count == 0 ? shards.Count : Counts.Max();

    /// <summary>How many times the shard count changed direction — the oscillation measure.</summary>
    /// <remarks>
    ///     A fleet that grows and then shrinks turns once. One that spawns and merges the same shard
    ///     over and over turns every few seconds, which is the failure hysteresis exists to prevent
    ///     and which a peak or a final count would not show.
    /// </remarks>
    public int Turns {
        get {
            var turns = 0;
            var direction = 0;

            for (var index = 1; index < Counts.Count; index++) {
                var step = Math.Sign(Counts[index] - Counts[index - 1]);

                if (step == 0) {
                    continue;
                }

                if (direction != 0 && step != direction) {
                    turns++;
                }

                direction = step;
            }

            return turns;
        }
    }

    void Arrive(int count) {
        for (var index = 0; index < count; index++) {
            fleet.Arrived(now);

            var decision = director.Place(new() { Player = new(Guid.NewGuid(), Guid.NewGuid()), Key = Key }, Candidates());

            if (decision.Outcome != PlacementOutcome.Placed) {
                Refused++;

                continue;
            }

            shards.Single(shard => shard.Shard == decision.Shard).Population++;
            Placed++;
        }
    }

    void Depart(int count) {
        for (var index = 0; index < count; index++) {
            var occupied = shards.Where(shard => shard.Population > 0).ToList();

            if (occupied.Count == 0) {
                return;
            }

            occupied[random.Next(occupied.Count)].Population--;
            Left++;
        }
    }

    /// <summary>
    ///     A drained shard moves its players out; it does not disconnect them (doc 27 § Drain).
    /// </summary>
    /// <remarks>
    ///     Ten a second, which is a stand-in for the readiness rules: a real drain moves people when
    ///     they are idle, and the only property this simulation needs from that is that it takes a
    ///     while and that nobody is dropped.
    /// </remarks>
    void MoveDrainingPlayers() {
        foreach (var shard in shards.Where(entry => entry.State == ShardState.Draining).ToList()) {
            for (var moved = 0; moved < 10 && shard.Population > 0; moved++) {
                var decision = director.Place(
                    new() {
                        Player = new(Guid.NewGuid(), Guid.NewGuid()),
                        Key = Key,
                        CameFrom = shard.Shard
                    },
                    Candidates()
                );

                if (decision.Outcome != PlacementOutcome.Placed) {
                    // Nowhere to put them, so they stay. A drain that could not finish is a live-ops
                    // alert rather than a disconnect, which is the whole of doc 27 § Drain.
                    return;
                }

                shard.Population--;
                shards.Single(entry => entry.Shard == decision.Shard).Population++;
            }
        }
    }

    void Spawn() {
        Spawns++;

        shards.Add(
            new() {
                Shard = ShardId.New(),
                State = ShardState.Starting,
                ReadyAt = now + Startup,
                StartedAt = now
            }
        );
    }

    IReadOnlyList<ShardCandidate> Candidates() =>
        [
            .. shards.Select(shard => new ShardCandidate {
                    Shard = shard.Shard,
                    Key = Key,
                    State = shard.State,
                    Endpoint = new("10.0.0.4", 7777),
                    Population = shard.Population,
                    Capacity = capacity,
                    Age = now - shard.StartedAt
                }
            )
        ];

    sealed class Simulated {
        public ShardId Shard { get; init; }

        public ShardState State { get; set; }

        public DateTimeOffset ReadyAt { get; init; }

        public DateTimeOffset StartedAt { get; init; }

        public int Population { get; set; }
    }
}
