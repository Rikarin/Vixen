// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Gameplay;
using Xunit;

namespace Vixen.Live.Matchmaking.Tests;

public class EloTests {
    readonly EloRatingModel elo = new();

    [Fact]
    public void AnEvenMatchExpectsAHalf() {
        Assert.Equal(0.5d, EloRatingModel.Expected(1500d, 1500d), 12);
        Assert.Equal(1d, elo.Quality([[new(1500d)], [new(1500d)]]), 12);
    }

    [Fact]
    public void TwoHundredPointsIsAboutSeventySixPerCent() {
        // 1/(1 + 10^(-200/400)), which anybody can check on a calculator — the transparency doc 28
        // says is Elo's whole reason for still being here.
        Assert.Equal(0.7597469d, EloRatingModel.Expected(1200d, 1000d), 6);
    }

    [Fact]
    public void ThePublishedFideWorkedExampleComesOut() {
        // The example from the Elo literature: a player rated 1613 plays five games against 1609,
        // 1477, 1388, 1586 and 1720, is expected to score 2.867, actually scores 2.5, and ends on
        // 1601 with K = 32.
        double[] opponents = [1609d, 1477d, 1388d, 1586d, 1720d];
        var expected = 0d;

        foreach (var opponent in opponents) {
            expected += EloRatingModel.Expected(1613d, opponent);
        }

        Assert.Equal(2.867d, expected, 3);
        Assert.Equal(1601d, elo.AfterPeriod(1613d, opponents, 2.5d), 0);
    }

    [Fact]
    public void AWinMovesBothSidesByTheSameAmount() {
        var after = elo.Update([[new(1200d)], [new(1000d)]], [0, 1]);

        Assert.Equal(1207.688d, after[0][0].Mean, 3);
        Assert.Equal(992.312d, after[1][0].Mean, 3);
        // Elo is zero-sum: whatever one side gained the other lost, so the total is unchanged.
        Assert.Equal(2200d, after[0][0].Mean + after[1][0].Mean, 6);
    }

    [Fact]
    public void ADrawBetweenUnevenSidesFavoursTheUnderdog() {
        var after = elo.Update([[new(1200d)], [new(1000d)]], [0, 0]);

        Assert.True(after[0][0].Mean < 1200d);
        Assert.True(after[1][0].Mean > 1000d);
    }

    [Fact]
    public void ATeamRatesAsTheMeanOfItsPlayersRatherThanTheSum() {
        // ⚠ A sum makes a five-player team five times as strong as one player and matches it against
        // nobody; the mean is what the four-hundred-point scale is calibrated for.
        var five = elo.Quality([[new(1500d), new(1500d), new(1500d), new(1500d), new(1500d)], [new(1500d)]]);

        Assert.Equal(1d, five, 12);
    }

    [Fact]
    public void AFreeForAllIsRefusedRatherThanApproximated() =>
        Assert.Throws<ArgumentException>(() => elo.Update([[new(1500d)], [new(1500d)], [new(1500d)]], [0, 1, 2]));

    [Fact]
    public void QualityFallsAwayAsOneSideBecomesAForegoneConclusion() {
        var even = elo.Quality([[new(1500d)], [new(1500d)]]);
        var uneven = elo.Quality([[new(1500d)], [new(1900d)]]);
        var hopeless = elo.Quality([[new(1500d)], [new(3000d)]]);

        Assert.True(even > uneven);
        Assert.True(uneven > hopeless);
        Assert.InRange(hopeless, 0d, 0.05d);
    }
}

public class BayesianTests {
    readonly BayesianRatingModel model = new();

    [Fact]
    public void ThePublishedTrueSkillOneOnOneFiguresComeOut() {
        // The canonical worked example: two default players, one wins, and the ratings become
        // 29.396 ± 7.171 and 20.604 ± 7.171. Hitting three decimal places is what the Cody erfc and
        // the Acklam quantile are in Gaussian for.
        var after = model.Update([[model.Starting], [model.Starting]], [0, 1]);

        Assert.Equal(29.396d, after[0][0].Mean, 3);
        Assert.Equal(7.171d, after[0][0].Deviation, 3);
        Assert.Equal(20.604d, after[1][0].Mean, 3);
        Assert.Equal(7.171d, after[1][0].Deviation, 3);
    }

    [Fact]
    public void ADrawBetweenTwoDefaultPlayersMovesNeitherMeanAndNarrowsBoth() {
        var after = model.Update([[model.Starting], [model.Starting]], [0, 0]);

        Assert.Equal(25d, after[0][0].Mean, 6);
        Assert.Equal(25d, after[1][0].Mean, 6);
        Assert.True(after[0][0].Deviation < model.Starting.Deviation);
    }

    [Fact]
    public void ADrawBetweenUnevenPlayersFavoursTheUnderdog() {
        var after = model.Update([[new(35d, 3d)], [new(15d, 3d)]], [0, 0]);

        Assert.True(after[0][0].Mean < 35d, "the favourite did not lose anything by drawing");
        Assert.True(after[1][0].Mean > 15d, "the underdog did not gain anything by drawing");
    }

    [Fact]
    public void UncertaintyFallsWithEveryGameAndNeverToNothing() {
        // ⚠ Tau adds a little back each time. Without it a rating converges to a variance of nearly
        // nothing and then cannot move again, so somebody who improves is stuck at what they were.
        var rating = model.Starting;

        for (var game = 0; game < 200; game++) {
            rating = model.Update([[rating], [model.Starting]], [0, 1])[0][0];
        }

        Assert.True(rating.Deviation < 3d, $"deviation stalled at {rating.Deviation}");
        Assert.True(rating.Deviation > 0.01d, $"deviation collapsed to {rating.Deviation}");
    }

    [Fact]
    public void ANewcomerMovesFurtherThanAVeteranForTheSameResult() {
        var newcomer = model.Update([[new(25d, 8.333d)], [new(25d, 8.333d)]], [0, 1])[0][0];
        var veteran = model.Update([[new(25d, 1d)], [new(25d, 8.333d)]], [0, 1])[0][0];

        Assert.True(newcomer.Mean - 25d > veteran.Mean - 25d);
    }

    [Fact]
    public void ARatingIsUsedConservativelyUntilItIsCertain() {
        // Three deviations below the mean, so a newcomer is not matched against experts on a guess.
        Assert.Equal(0d, model.Starting.Conservative, 6);
        Assert.Equal(22d, new Rating(25d, 1d).Conservative, 6);
    }

    [Fact]
    public void QualityIsOneForTwoIdenticalCertainPlayers() {
        var certain = new Rating(25d, 0.0001d);

        Assert.Equal(1d, model.Quality([[certain], [certain]]), 3);
        Assert.True(model.Quality([[new(40d, 1d)], [new(10d, 1d)]]) < 0.05d);
    }

    [Fact]
    public void ItHandlesTeamsOfDifferentSizes() {
        var after = model.Update([[model.Starting, model.Starting], [model.Starting]], [0, 1]);

        Assert.Equal(2, after[0].Length);
        Assert.Single(after[1]);

        // Two default players beating one is barely evidence, so they gain little; the lone player
        // losing to two is barely evidence either, so they lose little.
        Assert.True(after[0][0].Mean - 25d < 4d);
        Assert.True(25d - after[1][0].Mean < 4d);
    }

    [Fact]
    public void AFreeForAllIsRefusedRatherThanApproximated() =>
        Assert.Throws<ArgumentException>(
            () => model.Update([[model.Starting], [model.Starting], [model.Starting]], [0, 1, 2])
        );
}

/// <summary>Pairs the two oldest compatible tickets into a one-a-side match.</summary>
sealed class Duels(Matchmaker? queue = null, IRatingModel? model = null) : IMatchFunction {
    readonly IRatingModel model = model ?? new EloRatingModel();

    public Matchmaker? Queue { get; set; } = queue;

    public string Name => "duels";

    public IReadOnlyList<MatchProposal> Propose(IReadOnlyList<MatchTicket> pool, float now) {
        var proposals = new List<MatchProposal>();

        for (var left = 0; left < pool.Count; left++) {
            for (var right = left + 1; right < pool.Count; right++) {
                if (Queue is not null && !Queue.Compatible(pool[left], pool[right], now)) {
                    continue;
                }

                proposals.Add(
                    new(
                        [[pool[left]], [pool[right]]],
                        model.Quality([[pool[left].Rating], [pool[right].Rating]])
                    )
                );
            }
        }

        return proposals;
    }
}

/// <summary>Fills two teams of a fixed size, never splitting a ticket.</summary>
sealed class Teams(int size) : IMatchFunction {
    public string Name => "teams";

    public IReadOnlyList<MatchProposal> Propose(IReadOnlyList<MatchTicket> pool, float now) {
        var left = new List<MatchTicket>();
        var right = new List<MatchTicket>();
        var leftSeats = 0;
        var rightSeats = 0;

        foreach (var ticket in pool) {
            if (leftSeats <= rightSeats && leftSeats + ticket.Size <= size) {
                left.Add(ticket);
                leftSeats += ticket.Size;
            } else if (rightSeats + ticket.Size <= size) {
                right.Add(ticket);
                rightSeats += ticket.Size;
            }
        }

        return leftSeats == size && rightSeats == size ? [new([left, right], 1d)] : [];
    }
}

public class MatchmakerTests {
    static MatchTicket Ticket(string id, double rating, float enqueued = 0f, int size = 1) =>
        new(id, [.. Enumerable.Range(1, size).Select(who => new PlayerId((ulong)who))], new(rating), enqueued);

    [Fact]
    public void ATicketIsAPartyAndAPartyIsNeverSplit() {
        // ⚠ doc 28 § Testing names this. It is a property of the types rather than a rule: nothing
        // downstream is ever handed one member of a party, so nothing can separate them.
        var queue = new Matchmaker(new Teams(4));
        var random = new GameplayRandom(0xD0E5ul);

        for (var run = 0; run < 200; run++) {
            var parties = new Dictionary<string, int>(StringComparer.Ordinal);

            for (var index = 0; index < 12; index++) {
                var size = random.NextInt(1, 4);
                var id = $"t{run}/{index}";

                parties[id] = size;
                queue.Enqueue(
                    new(
                        id,
                        [.. Enumerable.Range(0, size).Select(seat => new PlayerId((ulong)((index * 8) + seat + 1)))],
                        new(1500d),
                        index
                    )
                );
            }

            foreach (var proposal in queue.Cycle(100f)) {
                foreach (var team in proposal.Teams) {
                    foreach (var ticket in team) {
                        Assert.Equal(parties[ticket.Id], ticket.Size);
                    }
                }
            }

            foreach (var id in parties.Keys) {
                queue.Cancel(id);
            }
        }
    }

    [Fact]
    public void APoolFiltersOnTagsAndOnRating() {
        var tags = new GameplayTagTableBuilder().Add("Role.Tank").Add("Role.Healer").Build();
        var tank = new GameplayTagSet();

        tank.Add(tags.Resolve("Role.Tank"));

        var queue = new Matchmaker(
            new Duels(),
            new MatchPool(GameplayTagQuery.Resolve(tags, all: ["Role.Tank"]), MinimumRating: 1000d)
        );

        Assert.False(queue.Enqueue(Ticket("no-tag", 1500d)));
        Assert.False(queue.Enqueue(new("too-low", [new PlayerId(1)], new(500d), 0f, tank)));
        Assert.True(queue.Enqueue(new("yes", [new PlayerId(1)], new(1500d), 0f, tank)));
    }

    [Fact]
    public void APoolFiltersOnLatency() {
        var queue = new Matchmaker(new Duels(), new MatchPool(Region: 1, MaximumLatency: 80f));

        Assert.True(queue.Enqueue(new("near", [new PlayerId(1)], new(1500d), 0f, null, [200f, 40f])));
        Assert.False(queue.Enqueue(new("far", [new PlayerId(2)], new(1500d), 0f, null, [20f, 300f])));
        Assert.False(queue.Enqueue(new("unmeasured", [new PlayerId(3)], new(1500d), 0f)));
    }

    [Fact]
    public void TheSameTicketDoesNotGoInTwice() {
        var queue = new Matchmaker(new Duels());

        Assert.True(queue.Enqueue(Ticket("a", 1500d)));
        Assert.False(queue.Enqueue(Ticket("a", 1500d)));
        Assert.Equal(1, queue.Count);
        Assert.True(queue.Cancel("a"));
        Assert.False(queue.Cancel("a"));
    }

    [Fact]
    public void AMatchedTicketLeavesTheQueue() {
        var queue = new Matchmaker(new Duels());

        queue.Enqueue(Ticket("a", 1500d));
        queue.Enqueue(Ticket("b", 1500d));

        var made = new List<MatchProposal>();

        queue.Matched += proposal => made.Add(proposal);

        Assert.Single(queue.Cycle(1f));
        Assert.Single(made);
        Assert.Equal(0, queue.Count);
        Assert.Equal(2, made[0].Players);
    }

    [Fact]
    public void OverlappingProposalsAreResolvedHighestQualityFirst() {
        var queue = new Matchmaker(new Duels());

        queue.Enqueue(Ticket("even-a", 1500d));
        queue.Enqueue(Ticket("even-b", 1500d));
        queue.Enqueue(Ticket("far", 2500d));

        var accepted = Assert.Single(queue.Cycle(1f));
        var ids = accepted.Tickets.Select(ticket => ticket.Id).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(["even-a", "even-b"], ids);
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public void TheBandWidensWithTheLongerOfTheTwoWaits() {
        // ⚠ The wider band, not the narrower: somebody who has waited ten minutes must be matchable
        // with a newcomer, or they can only ever be paired with somebody equally starved.
        var queue = new Matchmaker(new Duels(), wideningPerSecond: 10d);
        var patient = Ticket("patient", 1000d);
        var fresh = Ticket("fresh", 1400d, enqueued: 100f);

        Assert.False(queue.Compatible(patient, fresh, 10f));
        Assert.True(queue.Compatible(patient, fresh, 60f));
    }

    [Fact]
    public void QueueTimesAreBoundedUnderASyntheticArrivalTrace() {
        // doc 28 § Testing's third named matchmaking test. Players arrive steadily across a wide
        // rating spread; with the band widening, nobody waits for ever. Without it, the ends of the
        // ladder never match anybody — which the second half of this asserts.
        var random = new GameplayRandom(0x9EEDul);
        var function = new Duels();
        var queue = new Matchmaker(function, wideningPerSecond: 40d);

        function.Queue = queue;

        var longest = 0f;

        for (var second = 0; second < 600; second++) {
            if (second % 2 == 0) {
                queue.Enqueue(Ticket($"t{second}", 800d + (random.NextInt(1400)), second));
            }

            foreach (var proposal in queue.Cycle(second)) {
                longest = MathF.Max(longest, proposal.OldestWait(second));
            }
        }

        Assert.True(longest < 120f, $"somebody waited {longest} seconds");
        Assert.True(queue.Count < 10, $"{queue.Count} tickets never matched");
    }

    [Fact]
    public void WithoutAWideningBandTheEndsOfTheLadderStarve() {
        var function = new Duels();
        var queue = new Matchmaker(function, wideningPerSecond: 0.0001d);

        function.Queue = queue;

        // One player at each extreme and nobody in the middle: with a band that barely grows, they
        // are still waiting after ten minutes.
        queue.Enqueue(Ticket("bottom", 100d));
        queue.Enqueue(Ticket("top", 3000d));

        for (var second = 0; second < 600; second++) {
            queue.Cycle(second);
        }

        Assert.Equal(2, queue.Count);
    }

    [Fact]
    public void AnEmptyQueueAndAFunctionWithNothingToSayBothCycleToNothing() {
        var queue = new Matchmaker(new Duels());

        Assert.Empty(queue.Cycle(0f));

        queue.Enqueue(Ticket("alone", 1500d));

        Assert.Empty(queue.Cycle(1f));
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public void TheFunctionSeesThePoolOldestFirst() {
        var seen = new List<string>();
        var queue = new Matchmaker(new Recorder(seen));

        queue.Enqueue(Ticket("late", 1500d, 90f));
        queue.Enqueue(Ticket("early", 1500d, 10f));
        queue.Enqueue(Ticket("middle", 1500d, 50f));
        queue.Cycle(100f);

        Assert.Equal(["early", "middle", "late"], seen);
    }

    sealed class Recorder(List<string> seen) : IMatchFunction {
        public string Name => "recorder";

        public IReadOnlyList<MatchProposal> Propose(IReadOnlyList<MatchTicket> pool, float now) {
            seen.AddRange(pool.Select(ticket => ticket.Id));

            return [];
        }
    }
}
