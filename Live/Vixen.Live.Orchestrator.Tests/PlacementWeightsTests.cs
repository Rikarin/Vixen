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

    /// <summary>
    ///     ⚠ A key that names no weight is dropped, and a reader that wants to know can now be told.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Both halves, because the interesting one is the default.</b> A typo'd
    ///         <c>heathyFill:</c> scores exactly like a file that never mentioned fill, so a
    ///         <c>.vxplacement</c> of nothing but typos and no <c>.vxplacement</c> at all are the same
    ///         fleet — which is what makes the silence worth a callback. What it is not worth is a
    ///         refusal: this is operator config, and a file written for a newer engine has to boot on
    ///         an older shard mid-upgrade rather than take it down for a spelling.
    ///     </para>
    ///     <para>
    ///         The consequence is asserted alongside the key, so this cannot pass against a reader
    ///         that reports the key and then binds it anyway.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AKeyThatNamesNoWeightIsReportedWhenTheReaderAsksAndDroppedWhenItDoesNot() {
        List<string> unknown = [];

        var told = PlacementWeights.Parse("heathyFill: 9\n", unknown.Add);

        Assert.Equal("heathyFill", Assert.Single(unknown));
        Assert.Equal(PlacementWeights.Default.HealthyFill, told.HealthyFill);

        var quiet = PlacementWeights.Parse("heathyFill: 9\n");

        Assert.Equal(PlacementWeights.Default.HealthyFill, quiet.HealthyFill);
    }

    /// <summary>And a file that spells every weight right reports none, so the list means something.</summary>
    [Fact]
    public void AWellSpelledDocumentReportsNoUnknownKey() {
        List<string> unknown = [];

        var weights = PlacementWeights.Parse(PlacementWeights.Default.ToYaml(), unknown.Add);

        Assert.Empty(unknown);
        Assert.Equal(PlacementWeights.Default, weights);
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
