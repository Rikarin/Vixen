// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ecs;
using Vixen.Engine.Scenes;
using Xunit;

namespace Vixen.Live.Realms.Tests;

/// <summary>Is the map up, and what is the tick costing.</summary>
public sealed class MapAndHeartbeatTests {
    static ShardKey Key(string map = "maps/queensdale") => new(map, "eu", new("0.1.0", 1));

    [Fact]
    public void TheMapIsFoundByTheNameTheWireAlreadyUses() {
        using var world = new World("realm");
        var scenes = new SceneManager(world);
        var map = new MapLifetime(Key());

        Assert.Equal("queensdale", map.SceneName);
        Assert.False(map.Resolve(scenes));
        Assert.Equal(MapState.Loading, map.State);

        // The host's startup scene, opened before OnInitialise. NetworkSceneId is the hash of this
        // name, which is why matching on it is the wire's own identity rather than a workaround.
        var loaded = scenes.Create("queensdale");

        Assert.True(map.Resolve(scenes));
        Assert.Equal(MapState.Ready, map.State);
        Assert.Equal(loaded, map.Scene);
        Assert.True(map.IsReady);
    }

    [Fact]
    public void AnotherScenesPresenceIsNotTheMap() {
        using var world = new World("realm");
        var scenes = new SceneManager(world);
        var map = new MapLifetime(Key());

        scenes.Create("divinitys-reach");

        // A realm whose map never appears never becomes ready, which is the correct failure: it is
        // started, it is never placed on, and it admits nobody into an empty world.
        Assert.False(map.Resolve(scenes));
        Assert.Equal(MapState.Loading, map.State);
    }

    [Fact]
    public void AHeadWithNoWorldNeverResolves() {
        var map = new MapLifetime(Key());

        Assert.False(map.Resolve(null));
        Assert.False(map.IsReady);
    }

    [Fact]
    public void QuiescingKeepsTheMapReadyBecauseTheShardIsStillSimulating() {
        var map = new MapLifetime(Key());

        map.Ready(new(1));
        map.Quiesce();

        // Doc 27 § Drain: a drained shard moves its players out, it does not disconnect them. So
        // quiescing changes admission and nothing about the simulation.
        Assert.Equal(MapState.Quiescing, map.State);
        Assert.True(map.IsReady);

        map.Quiesce();
        Assert.Equal(MapState.Quiescing, map.State);
    }

    [Fact]
    public void UnloadingTakesTheScenesEntitiesWithIt() {
        using var world = new World("realm");
        var scenes = new SceneManager(world);
        var map = new MapLifetime(Key());
        var scene = scenes.Create("queensdale");

        scenes.CreateEntity(scene);
        scenes.CreateEntity(scene);

        Assert.True(map.Resolve(scenes));
        Assert.Equal(2, map.Unload(scenes));
        Assert.Equal(MapState.Unloaded, map.State);
        Assert.False(map.Scene.IsValid);
        Assert.False(map.IsReady);
    }

    [Fact]
    public void UnloadingAMapThatWasNeverUpIsHarmless() {
        var map = new MapLifetime(Key());

        Assert.Equal(0, map.Unload(null));
        Assert.Equal(MapState.Unloaded, map.State);
    }

    [Fact]
    public void TheHeartbeatKeepsTheRemainderRatherThanDrifting() {
        var heartbeat = new RealmHeartbeat(TimeSpan.FromMilliseconds(100));

        // Thirty-three milliseconds never lands on a hundred. Discarding the overshoot each time is
        // how three "missed" heartbeats become a shard declared Lost while it is simulating happily.
        var due = 0;

        for (var tick = 0; tick < 300; tick++) {
            if (heartbeat.IsDue(TimeSpan.FromMilliseconds(33))) {
                due++;
            }
        }

        Assert.Equal(99, due);
        Assert.Equal(99, heartbeat.SampleCount);
    }

    [Fact]
    public void TheTailIsTheNumberThatIsReportedAndTheMeanIsNot() {
        var heartbeat = new RealmHeartbeat(windowSize: 100);

        for (var tick = 0; tick < 98; tick++) {
            heartbeat.Observe(TimeSpan.FromMilliseconds(4));
        }

        heartbeat.Observe(TimeSpan.FromMilliseconds(40));
        heartbeat.Observe(TimeSpan.FromMilliseconds(40));

        // A shard whose average tick is under 5 ms and whose p99 is 40 ms is one where every player
        // sees a hitch twice a second. The mean never says so, which is why placement watches the
        // other number.
        Assert.Equal(40, heartbeat.TickP99());
        Assert.Equal(4.72, heartbeat.TickMean(), 2);
    }

    [Fact]
    public void TheWindowForgetsOldTicks() {
        var heartbeat = new RealmHeartbeat(windowSize: 8);

        heartbeat.Observe(TimeSpan.FromMilliseconds(500));

        for (var tick = 0; tick < 8; tick++) {
            heartbeat.Observe(TimeSpan.FromMilliseconds(2));
        }

        Assert.Equal(2, heartbeat.TickP99());
    }

    [Fact]
    public void AnUnobservedHeartbeatReportsNothingRatherThanZeroDressedUp() {
        var heartbeat = new RealmHeartbeat();

        Assert.Equal(0, heartbeat.TickP99());
        Assert.Equal(0, heartbeat.TickMean());
        Assert.Equal(RealmHeartbeat.DefaultInterval, heartbeat.Interval);
        Assert.Equal(256, heartbeat.WindowSize);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AWindowThatCouldNotHoldATickIsRefused(int windowSize) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new RealmHeartbeat(windowSize: windowSize));

    [Fact]
    public void AnIntervalThatNeverElapsesIsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new RealmHeartbeat(TimeSpan.Zero));
}
