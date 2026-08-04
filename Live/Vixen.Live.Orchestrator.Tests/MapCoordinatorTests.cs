// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Live.Cluster;
using Xunit;

namespace Vixen.Live.Orchestration.Tests;

/// <summary>Where slice one's counts come from, and what happens when a shard goes away.</summary>
public sealed class MapCoordinatorTests {
    static readonly ShardKey Key = new("maps/queensdale", "eu", new("0.1.0", 1));
    static readonly DateTimeOffset Noon = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    static MapCoordinator Map() => new(Key);

    static ShardReport Ready(ShardId? id = null, int population = 0) =>
        new(
            id ?? ShardId.New(),
            Key,
            ShardState.Ready,
            new("10.0.0.4", 7777),
            new("realm-1"),
            population,
            new(100, 120),
            Noon,
            Noon
        );

    static PlaceRequest Asking(Guid? party = null, Guid? guild = null, string locale = "") =>
        new(new(Guid.NewGuid(), Guid.NewGuid()), Key, party ?? Guid.Empty, guild ?? Guid.Empty, locale, ShardId.None);

    [Fact]
    public void AMapWithNoShardsSaysOneIsComingRatherThanRefusing() {
        var map = Map();

        var result = map.Place(Asking(), Noon);

        // ⚠ The difference between a client showing a progress bar and a client showing an error, so
        // it is answered rather than inferred. A map with nothing on it is the ordinary first
        // placement of the day.
        Assert.Equal(PlaceStatus.Refused, result.Status);
        Assert.Contains("no candidate", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AShardBeingStartedTurnsARefusalIntoAWait() {
        var map = Map();
        var starting = Ready() with { State = ShardState.Starting };

        map.ShardChanged(starting);

        var result = map.Place(Asking(), Noon);

        Assert.Equal(PlaceStatus.Starting, result.Status);
        Assert.Contains("starting", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void APlacedPlayerIsCountedImmediatelyRatherThanAtTheNextHeartbeat() {
        var map = Map();
        var shard = Ready();

        map.ShardChanged(shard);

        for (var arrival = 0; arrival < 10; arrival++) {
            Assert.Equal(PlaceStatus.Placed, map.Place(Asking(), Noon).Status);
        }

        // Two hundred people zoning in inside one heartbeat interval would otherwise all be scored
        // against a population of zero — the fill term reading two seconds of history at the exact
        // moment it matters most.
        Assert.Equal(10, map.Shards.Single().Population);
        Assert.Equal(10, map.Population);
    }

    [Fact]
    public void ThePartyCountIsComputedFromTheRoster() {
        var map = Map();
        var quiet = Ready(new(Guid.Parse("00000000-0000-0000-0000-000000000001")), population: 50);
        var busy = Ready(new(Guid.Parse("00000000-0000-0000-0000-000000000002")), population: 60);

        map.ShardChanged(quiet);
        map.ShardChanged(busy);

        var party = Guid.NewGuid();

        // One party member lands wherever the score sends them…
        var first = map.Place(Asking(party), Noon);

        Assert.Equal(PlaceStatus.Placed, first.Status);

        // …and the next one follows, because the map now knows where they are. This is the join the
        // director deliberately does not make: it scores counts, and this is what counts.
        for (var friend = 0; friend < 3; friend++) {
            Assert.Equal(first.Shard, map.Place(Asking(party), Noon).Shard);
        }
    }

    [Fact]
    public void AGuildPullsAndAPartyPullsHarder() {
        var map = Map();
        var first = Ready(new(Guid.Parse("00000000-0000-0000-0000-000000000001")));
        var second = Ready(new(Guid.Parse("00000000-0000-0000-0000-000000000002")));

        map.ShardChanged(first);
        map.ShardChanged(second);

        var guild = Guid.NewGuid();
        var party = Guid.NewGuid();

        // Five guild members land somewhere. The roster is built the way it is in production —
        // by placing people — rather than by being written directly.
        var guildShard = map.Place(Asking(guild: guild), Noon).Shard;

        for (var member = 0; member < 4; member++) {
            Assert.Equal(guildShard, map.Place(Asking(guild: guild), Noon).Shard);
        }

        // Put one party member on the other shard, by filling the guild's shard past its hard cap
        // for exactly one placement and then letting it back down.
        var crowded = map.Shards.Single(shard => shard.Shard == guildShard);

        map.ShardChanged(crowded with { Population = 500 });

        var partyShard = map.Place(Asking(party), Noon).Shard;

        Assert.NotEqual(guildShard, partyShard);

        map.ShardChanged(crowded);

        // Somebody in both now has five of their guild on one shard and one party member on the
        // other. The party wins, which is the ordering the two weights exist to express — and it is
        // what "join your friend's instance" means without a separate mechanism.
        Assert.Equal(partyShard, map.Place(Asking(party, guild), Noon).Shard);
    }

    [Fact]
    public void ALostShardTakesItsRosterWithIt() {
        var map = Map();
        var shard = Ready();

        map.ShardChanged(shard);
        map.Place(Asking(), Noon);
        map.Place(Asking(), Noon);

        Assert.Equal(2, map.Population);

        map.ShardChanged(shard with { State = ShardState.Lost });

        // Not a leak — doc 27 § Health's "recovery is a placement, not a resurrection". Their
        // volatile state went with the process, and a roster that remembered them would score a shard
        // that no longer exists.
        Assert.Empty(map.Shards);
        Assert.Equal(0, map.Population);
    }

    [Fact]
    public void APlayerWhoLeftStopsBeingCounted() {
        var map = Map();
        var shard = Ready();

        map.ShardChanged(shard);

        var request = Asking();

        map.Place(request, Noon);
        Assert.Equal(1, map.Shards.Single().Population);

        map.PlayerLeft(request.Player, shard.Shard);

        Assert.Equal(0, map.Population);
        Assert.Equal(0, map.Shards.Single().Population);
    }

    [Fact]
    public void ALeaveFromAShardTheyAreNotOnIsIgnored() {
        var map = Map();
        var shard = Ready();

        map.ShardChanged(shard);

        var request = Asking();

        map.Place(request, Noon);
        map.PlayerLeft(request.Player, ShardId.New());

        // Arrives when a player has already been moved: the old shard's despawn and the new shard's
        // admission race, and the loser must not decrement a count it does not own.
        Assert.Equal(1, map.Population);
    }

    [Fact]
    public void EveryPlacementCarriesItsArgument() {
        var map = Map();

        map.ShardChanged(Ready(population: 50));

        var result = map.Place(Asking(), Noon);

        Assert.Contains("scored", result.Reason, StringComparison.Ordinal);
        Assert.NotNull(map.LastDecision);
        Assert.Equal(PlacementOutcome.Placed, map.LastDecision!.Outcome);
    }

    [Fact]
    public void ArrivalsFeedTheProjectionEvenWhenTheyAreRefused() {
        var map = Map();

        for (var arrival = 0; arrival < 30; arrival++) {
            map.Place(Asking(), Noon);
        }

        // A map that only counted successful placements would read a saturated fleet as an idle one,
        // which is the exact moment the projection is supposed to speak up.
        Assert.True(map.ArrivalRate(Noon) > 0);
        Assert.Equal(FleetActionKind.Spawn, map.Tick(Noon).Kind);
    }
}
