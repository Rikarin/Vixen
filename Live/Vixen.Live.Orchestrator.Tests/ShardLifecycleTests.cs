// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Live.Cluster;
using Xunit;

namespace Vixen.Live.Orchestration.Tests;

/// <summary>The spine, and the transitions that are not edges of it.</summary>
public sealed class ShardLifecycleTests {
    static DateTimeOffset now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    static readonly ShardKey Key = new("maps/queensdale", "eu", new("0.1.0", 1));

    static ShardLifecycle Shard() => new(ShardId.New(), new(TimeSpan.FromSeconds(2), 3, () => now));

    static ShardHeartbeat Beat(int population = 0) => new(population, 2.0, 1.0, 0, now);

    [Fact]
    public void TheHappyPathIsFiveTransitions() {
        var shard = Shard();

        Assert.Equal(ShardState.Requested, shard.State);

        shard.Requested(Key, new(100, 120));
        shard.Starting(new("realm-1"), new("10.0.0.4", 7777));
        Assert.Equal(ShardState.Starting, shard.State);

        shard.Ready(new("10.0.0.4", 7777));
        Assert.Equal(ShardState.Ready, shard.State);

        shard.Drain();
        Assert.Equal(ShardState.Draining, shard.State);

        shard.Stopped();
        Assert.Equal(ShardState.Stopped, shard.State);
    }

    [Fact]
    public void TheRealmsWordAboutWhereItBoundWins() {
        var shard = Shard();

        shard.Requested(Key, new(100, 120));
        shard.Starting(new("realm-1"), new("10.0.0.4", 0));
        shard.Ready(new("10.0.0.4", 41234));

        // They agree in every ordinary case; where they do not, the realm is right, because it is the
        // one holding the socket.
        Assert.Equal(new RealmEndpoint("10.0.0.4", 41234), shard.Report().Endpoint);
    }

    [Fact]
    public void AShardTheClusterHasWrittenOffStaysWrittenOff() {
        var shard = Shard();

        shard.Requested(Key, new(100, 120));
        shard.Starting(new("realm-1"), new("10.0.0.4", 7777));
        shard.Ready(new("10.0.0.4", 7777));
        shard.Stopped();

        // A process that came back from the dead — a supervisor that restarted it, a partition that
        // healed after its players were placed elsewhere. Recovery is a placement, not a
        // resurrection.
        shard.Ready(new("10.0.0.4", 7777));

        Assert.Equal(ShardState.Stopped, shard.State);
        Assert.Equal(ShardState.Stopped, shard.Heartbeat(Beat(40)));
        Assert.Equal(0, shard.Report().Population);
    }

    [Fact]
    public void DrainingIsOneWay() {
        var shard = Shard();

        shard.Requested(Key, new(100, 120));
        shard.Ready(new("10.0.0.4", 7777));
        shard.Drain();

        // A shard that could be talked out of draining would make a rollout's completion a race
        // rather than an invariant.
        shard.Ready(new("10.0.0.4", 7777));

        Assert.Equal(ShardState.Draining, shard.State);
    }

    [Fact]
    public void TheHeartbeatIsHowARealmLearnsItShouldBeDraining() {
        var shard = Shard();

        shard.Requested(Key, new(100, 120));
        shard.Ready(new("10.0.0.4", 7777));

        Assert.Equal(ShardState.Ready, shard.Heartbeat(Beat(30)));

        shard.Drain();

        // ⚠ The reply to a heartbeat it was sending anyway, so nothing in the control plane ever
        // needs to call *into* a realm.
        Assert.Equal(ShardState.Draining, shard.Heartbeat(Beat(30)));
    }

    [Fact]
    public void ThreeMissedHeartbeatsIsLost() {
        var shard = Shard();

        shard.Requested(Key, new(100, 120));
        shard.Ready(new("10.0.0.4", 7777));
        shard.Heartbeat(Beat(30));

        now += TimeSpan.FromSeconds(5);
        Assert.False(shard.Expire());
        Assert.Equal(ShardState.Ready, shard.State);

        now += TimeSpan.FromSeconds(2);
        Assert.True(shard.Expire());
        Assert.Equal(ShardState.Lost, shard.State);
        Assert.Equal(0, shard.Report().Population);
    }

    [Fact]
    public void AShardThatNeverCameUpIsFailedRatherThanLost() {
        var shard = Shard();

        shard.Requested(Key, new(100, 120));
        shard.Starting(new("realm-1"), new("10.0.0.4", 7777));
        shard.Lost();

        // Telling them apart is what makes "the map is broken" distinguishable from "that machine
        // died" in a fleet view.
        Assert.Equal(ShardState.Failed, shard.State);
    }

    [Fact]
    public void AShardThatWasRunningIsLostRatherThanFailed() {
        var shard = Shard();

        shard.Requested(Key, new(100, 120));
        shard.Ready(new("10.0.0.4", 7777));
        shard.Lost();

        Assert.Equal(ShardState.Lost, shard.State);
    }

    [Fact]
    public void AStoppedShardDoesNotExpire() {
        var shard = Shard();

        shard.Requested(Key, new(100, 120));
        shard.Ready(new("10.0.0.4", 7777));
        shard.Stopped();

        now += TimeSpan.FromHours(1);

        Assert.False(shard.Expire());
        Assert.Equal(ShardState.Stopped, shard.State);
    }

    [Fact]
    public void TheReportCarriesWhatAFleetViewNeeds() {
        var shard = Shard();

        shard.Requested(Key, new(100, 120));
        shard.Starting(new("realm-7"), new("10.0.0.9", 30001));
        shard.Ready(new("10.0.0.9", 30001));
        shard.Heartbeat(Beat(42));

        var report = shard.Report();

        Assert.Equal(shard.Shard, report.Shard);
        Assert.Equal(Key, report.Key);
        Assert.Equal(ShardState.Ready, report.State);
        Assert.Equal(new RealmInstanceId("realm-7"), report.Instance);
        Assert.Equal(42, report.Population);
        Assert.Equal(new ShardCapacity(100, 120), report.Capacity);
    }
}
