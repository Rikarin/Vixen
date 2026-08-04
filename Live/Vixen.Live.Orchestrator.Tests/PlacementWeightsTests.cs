// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Live.Orchestration.Tests;

/// <summary>What a game authors when it disagrees with Guild Wars 2.</summary>
public sealed class PlacementWeightsTests {
    [Fact]
    public void TheDefaultsAreDocumentTwentySevens() {
        var weights = PlacementWeights.Default;

        Assert.Equal(10_000, weights.Party);
        Assert.Equal(400, weights.GuildMember);
        Assert.Equal(200, weights.Friend);
        Assert.Equal(300, weights.Locale);
        Assert.Equal(250, weights.HealthyFill);
        Assert.Equal(40, weights.HealthyFrom);
        Assert.Equal(80, weights.HealthyTo);
        Assert.Equal(40, weights.Overfull);
        Assert.Equal(-100, weights.Aged);
        Assert.Equal(-5_000, weights.AntiFlap);
    }

    [Fact]
    public void WeightsSurviveTheRoundTrip() {
        var weights = PlacementWeights.Default with {
            Party = 1,
            Locale = 50_000,
            MaxAge = TimeSpan.FromHours(2),
            GuildCap = 12
        };

        Assert.Equal(weights, PlacementWeights.Parse(weights.ToYaml()));
    }

    [Fact]
    public void ADocumentThatNamesOneTermLeavesTheRestAlone() {
        // What a game actually writes: a `.vxplacement` saying the one thing it disagrees with,
        // rather than a copy of every default that then goes stale.
        var weights = PlacementWeights.Parse("locale: 50000\n");

        Assert.Equal(50_000, weights.Locale);
        Assert.Equal(PlacementWeights.Default.Party, weights.Party);
        Assert.Equal(PlacementWeights.Default.AntiFlap, weights.AntiFlap);
    }

    [Fact]
    public void AGameThatTurnsATermOffTurnsItOff() {
        var weights = PlacementWeights.Parse("party: 0\nfriend: 0\nguildMember: 0\n");
        var director = new PlacementDirector(weights);

        var request = new PlacementRequest {
            Player = new(Guid.NewGuid(), Guid.NewGuid()),
            Key = new("maps/queensdale", "eu", new("0.1.0", 1)),
            Party = Guid.NewGuid()
        };

        var withParty = new ShardCandidate {
            Shard = ShardId.New(),
            Key = request.Key,
            State = ShardState.Ready,
            Population = 10,
            Capacity = new(100, 120),
            PartyMembers = 4
        };

        var busy = withParty with { Shard = ShardId.New(), PartyMembers = 0, Population = 60 };

        // A battleground that wants fill to decide and nothing else says so, and placement obeys —
        // which is the whole reason the weights are a `.vxplacement` and not constants.
        Assert.Equal(busy.Shard, director.Place(request, [withParty, busy]).Shard);
    }

    [Fact]
    public void TheExtensionIsTheOneTheDocumentNames() =>
        Assert.Equal(".vxplacement", PlacementWeights.Extension);
}
