// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Live.Orchestration.Tests;

/// <summary>The megaserver's promises, asserted rather than described.</summary>
/// <remarks>
///     Doc 27 § Testing asks for property tests over the scoring function: a party is never split, a
///     shard above its hard cap is never chosen, and scoring is total and deterministic for a given
///     fleet. Those are the three at the bottom of this file; everything above them is one term at a
///     time.
/// </remarks>
public sealed class PlacementDirectorTests {
    static readonly RealmVersion Version = new("0.1.0", 0xC0FFEE);
    static readonly ShardKey Queensdale = new("maps/queensdale", "eu", Version);

    static PlacementRequest Asking(Guid? party = null, Guid? guild = null, string locale = "") =>
        new() {
            Player = new(Guid.NewGuid(), Guid.NewGuid()),
            Key = Queensdale,
            Party = party ?? Guid.Empty,
            Guild = guild ?? Guid.Empty,
            Locale = locale
        };

    static ShardCandidate Shard(
        int population = 50,
        ShardState state = ShardState.Ready,
        ShardKey? key = null,
        ShardId? id = null
    ) =>
        new() {
            Shard = id ?? ShardId.New(),
            Key = key ?? Queensdale,
            State = state,
            Endpoint = new("10.0.0.4", 7777),
            Population = population,
            Capacity = new(100, 120),
            Locale = "en-GB"
        };

    // ── The hard filters ────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("maps/divinity", "eu", "0.1.0", PlacementFilter.Map)]
    [InlineData("maps/queensdale", "na", "0.1.0", PlacementFilter.Region)]
    [InlineData("maps/queensdale", "eu", "0.2.0", PlacementFilter.Build)]
    public void ACandidateForSomethingElseIsNotScored(string map, string region, string build, PlacementFilter expected) {
        var candidate = Shard(key: new(map, region, new(build, Version.Content)));

        Assert.Equal(expected, PlacementDirector.Reject(Asking(), candidate));
    }

    [Fact]
    public void AStaleCatalogIsItsOwnRefusal() {
        // The one a client that has not fetched the content update hits, and ADR-022 turns it from a
        // hard rejection into a routing decision — so it has to be distinguishable from every other
        // reason a shard was skipped.
        var candidate = Shard(key: new("maps/queensdale", "eu", new("0.1.0", 0xBADF00D)));

        Assert.Equal(PlacementFilter.Content, PlacementDirector.Reject(Asking(), candidate));
    }

    [Theory]
    [InlineData(ShardState.Requested)]
    [InlineData(ShardState.Starting)]
    [InlineData(ShardState.Draining)]
    [InlineData(ShardState.Stopping)]
    [InlineData(ShardState.Stopped)]
    [InlineData(ShardState.Failed)]
    [InlineData(ShardState.Lost)]
    public void OnlyReadyIsAPlacementCandidate(ShardState state) =>
        Assert.Equal(PlacementFilter.NotReady, PlacementDirector.Reject(Asking(), Shard(state: state)));

    [Fact]
    public void AShardAtItsHardCapIsNotScored() {
        Assert.Equal(PlacementFilter.None, PlacementDirector.Reject(Asking(), Shard(population: 119)));
        Assert.Equal(PlacementFilter.Full, PlacementDirector.Reject(Asking(), Shard(population: 120)));
        Assert.Equal(PlacementFilter.Full, PlacementDirector.Reject(Asking(), Shard(population: 500)));
    }

    [Fact]
    public void AnAccessListIsAHardFilter() {
        var instance = Shard() with { Kind = ShardKind.Instance, Admits = false };

        Assert.Equal(PlacementFilter.Access, PlacementDirector.Reject(Asking(), instance));
    }

    // ── The score ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void APartyMemberOutweighsEverythingElsePutTogether() {
        var party = Guid.NewGuid();
        var guild = Guid.NewGuid();

        var withParty = Shard(id: new(Guid.Parse("00000000-0000-0000-0000-000000000001"))) with {
            PartyMembers = 1,
            Population = 95
        };

        // Everything the other shard could possibly have going for it: a full guild, five friends,
        // the right language, and a perfect fill.
        var withoutParty = Shard(id: new(Guid.Parse("00000000-0000-0000-0000-000000000002"))) with {
            GuildMembers = 50,
            Friends = 50,
            Locale = "en-GB",
            Population = 60
        };

        var decision = new PlacementDirector().Place(
            Asking(party, guild, "en-GB"),
            [withoutParty, withParty]
        );

        // "Join your friend's instance" falls out of placement rather than being a mechanism beside
        // it, and this is what that means.
        Assert.Equal(PlacementOutcome.Placed, decision.Outcome);
        Assert.Equal(withParty.Shard, decision.Shard);
    }

    [Fact]
    public void GuildAndFriendTermsAreCappedSoTheyCannotOutrankAParty() {
        var weights = PlacementWeights.Default;
        var ceiling = (weights.GuildMember * weights.GuildCap)
            + (weights.Friend * weights.FriendCap)
            + weights.Locale
            + weights.HealthyFill;

        Assert.True(
            ceiling < weights.Party,
            $"every other positive term together is {ceiling}, which is not less than a party's {weights.Party}."
        );
    }

    [Fact]
    public void PlacementPrefersFillingAShardThatIsAlreadyBusy() {
        // What makes consolidation possible: a map that is emptying converges on a few busy shards
        // rather than a lot of lonely ones, which is the input the merge rule then acts on.
        var quiet = Shard(population: 10);
        var healthy = Shard(population: 60);

        var decision = new PlacementDirector().Place(Asking(), [quiet, healthy]);

        Assert.Equal(healthy.Shard, decision.Shard);
    }

    [Fact]
    public void TheLastFifthOfAShardFallsAwaySteeply() {
        var director = new PlacementDirector();
        var request = Asking();

        var scores = new[] { 50, 80, 90, 100, 115 }
            .Select(population => director.Place(request, [Shard(population: population)]).Score)
            .ToArray();

        // Healthy at 50 and 80, then negative and getting worse — so the room above the soft cap is
        // reserved for the people who have a reason to be there.
        Assert.Equal(PlacementWeights.Default.HealthyFill, scores[0]);
        Assert.Equal(PlacementWeights.Default.HealthyFill, scores[1]);
        Assert.True(scores[2] < 0);
        Assert.True(scores[3] < scores[2]);
        Assert.True(scores[4] < scores[3]);
    }

    [Fact]
    public void APlayerIsNotSentBackToTheShardTheyWereJustMovedOff() {
        var left = Shard(population: 60);
        var arrived = Shard(population: 60);

        var decision = new PlacementDirector().Place(
            Asking() with { CameFrom = left.Shard },
            [left, arrived]
        );

        Assert.Equal(arrived.Shard, decision.Shard);
        Assert.Contains(
            decision.Verdicts.Single(verdict => verdict.Shard == left.Shard).Terms,
            term => term.Name == "antiflap"
        );
    }

    [Fact]
    public void AnAgedShardIsBiasedAgainstSoARolloutFinishes() {
        var old = Shard(population: 60) with { Age = TimeSpan.FromHours(9) };
        var fresh = Shard(population: 60);

        Assert.Equal(fresh.Shard, new PlacementDirector().Place(Asking(), [old, fresh]).Shard);
    }

    [Fact]
    public void TheGameSetsTheWeightsAndTheEngineDoesNot() {
        // A game that wants locale to dominate says so, and placement obeys.
        var weights = PlacementWeights.Default with { Locale = 50_000 };

        var wrongLanguage = Shard(population: 60) with { Locale = "de" };
        var rightLanguage = Shard(population: 10) with { Locale = "en-GB" };

        var decision = new PlacementDirector(weights).Place(
            Asking(locale: "en-GB"),
            [wrongLanguage, rightLanguage]
        );

        Assert.Equal(rightLanguage.Shard, decision.Shard);
    }

    // ── The explanation ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryCandidateGetsAVerdictWhetherItWasScoredOrNot() {
        var wrongMap = Shard(key: new("maps/divinity", "eu", Version));
        var full = Shard(population: 200);
        var good = Shard(population: 60);

        var decision = new PlacementDirector().Place(Asking(), [wrongMap, full, good]);

        Assert.Equal(3, decision.Verdicts.Count);
        Assert.Equal(PlacementFilter.Map, decision.Verdicts[0].Excluded);
        Assert.Equal(PlacementFilter.Full, decision.Verdicts[1].Excluded);
        Assert.True(decision.Verdicts[2].WasScored);

        // Doc 27 § Diagnostics: without this, placement complaints are unanswerable.
        var explanation = decision.Explain();

        Assert.Contains(good.Shard.ToString(), explanation, StringComparison.Ordinal);
        Assert.Contains("Full", explanation, StringComparison.Ordinal);
        Assert.Contains("fill", explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void NoCandidateIsAnAnswerRatherThanAFailure() {
        var decision = new PlacementDirector().Place(Asking(), [Shard(population: 200)]);

        Assert.Equal(PlacementOutcome.NoCandidate, decision.Outcome);
        Assert.False(decision.Shard.IsValid);
        Assert.Contains("no candidate", decision.Explain(), StringComparison.Ordinal);
    }

    [Fact]
    public void AMapWithNoShardsAtAllIsTheSameAnswer() =>
        Assert.Equal(PlacementOutcome.NoCandidate, new PlacementDirector().Place(Asking(), []).Outcome);

    // ── The three properties doc 27 § Testing asks for ──────────────────────────────────────────

    [Fact]
    public void APartyIsNeverSplit() {
        var director = new PlacementDirector();
        var random = new Random(20260804);

        for (var trial = 0; trial < 20_000; trial++) {
            var party = Guid.NewGuid();
            var shards = new List<ShardCandidate>();
            var holding = random.Next(0, 6);

            for (var index = 0; index < 6; index++) {
                shards.Add(
                    Shard(population: random.Next(0, 119)) with {
                        PartyMembers = index == holding ? random.Next(1, 5) : 0,
                        GuildMembers = random.Next(0, 40),
                        Friends = random.Next(0, 40),
                        Locale = random.Next(2) == 0 ? "en-GB" : "de",
                        Age = TimeSpan.FromHours(random.Next(0, 12))
                    }
                );
            }

            var decision = director.Place(Asking(party, Guid.NewGuid(), "en-GB"), shards);

            // The only way the party's shard loses is if it is filtered out, and the only filter that
            // can apply to it here is being full.
            var theirs = shards[holding];

            if (theirs.Capacity.Admits(theirs.Population)) {
                Assert.Equal(theirs.Shard, decision.Shard);
            }
        }
    }

    [Fact]
    public void AShardAboveItsHardCapIsNeverChosen() {
        var director = new PlacementDirector();
        var random = new Random(4242);

        for (var trial = 0; trial < 20_000; trial++) {
            var shards = Enumerable.Range(0, 5)
                .Select(_ => Shard(population: random.Next(0, 400)) with {
                    PartyMembers = random.Next(0, 3),
                    Friends = random.Next(0, 10)
                })
                .ToList();

            var decision = director.Place(Asking(Guid.NewGuid()), shards);

            if (decision.Outcome != PlacementOutcome.Placed) {
                continue;
            }

            var chosen = shards.Single(shard => shard.Shard == decision.Shard);

            Assert.True(chosen.Capacity.Admits(chosen.Population), $"{chosen} was chosen.");
        }
    }

    [Fact]
    public void ScoringIsTotalAndDeterministicForAGivenFleet() {
        var director = new PlacementDirector();
        var random = new Random(1234);

        for (var trial = 0; trial < 5_000; trial++) {
            var request = Asking(Guid.NewGuid(), Guid.NewGuid(), "en-GB");

            var shards = Enumerable.Range(0, 8)
                .Select(_ => Shard(population: random.Next(0, 130), state: (ShardState)random.Next(0, 8)) with {
                    PartyMembers = random.Next(0, 3),
                    GuildMembers = random.Next(0, 20),
                    Friends = random.Next(0, 20),
                    Locale = random.Next(3) switch { 0 => "en-GB", 1 => "de", _ => "" },
                    Age = TimeSpan.FromMinutes(random.Next(0, 900)),
                    Admits = random.Next(10) > 0
                })
                .ToList();

            var first = director.Place(request, shards);

            // Total: every candidate has a verdict, and it either scored or names the filter.
            Assert.Equal(shards.Count, first.Verdicts.Count);

            // Deterministic: the same fleet in a different order is the same answer. This is what
            // the tie-break on shard id is for — nothing else about the ordering may matter.
            var shuffled = shards.OrderBy(_ => random.Next()).ToList();
            var second = director.Place(request, shuffled);

            Assert.Equal(first.Outcome, second.Outcome);
            Assert.Equal(first.Shard, second.Shard);
            Assert.Equal(first.Score, second.Score);
        }
    }
}
