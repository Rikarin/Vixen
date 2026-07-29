// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ecs;
using Vixen.Video.Ecs;
using Vixen.Video.Playback;
using Xunit;

namespace Vixen.Video.Tests;

/// <summary>The ECS bridge: the set of videos being advanced is a query, not a list.</summary>
public sealed class VideoSystemTests {
    [Fact]
    public void AnEntityWithASurfaceIsAdvanced() {
        using var world = new World();
        var system = new VideoSystem();

        var player = Player(4);
        var entity = world.Create();

        world.Add(entity, new VideoSurface { Player = player, PlayOnStart = true });
        world.Add(entity, default(VideoPlaybackInfo));

        system.Step(world, TimeSpan.Zero);

        Assert.Equal(VideoPlaybackState.Playing, world.Read<VideoPlaybackInfo>(entity).State);
        Assert.NotNull(player.CurrentFrame);
    }

    [Fact]
    public void PlayOnStartHappensOnceRatherThanEveryFrame() {
        using var world = new World();
        var system = new VideoSystem();

        var player = Player(8);
        var entity = world.Create();

        world.Add(entity, new VideoSurface { Player = player, PlayOnStart = true });

        system.Step(world, TimeSpan.Zero);
        system.Step(world, TimeSpan.FromMilliseconds(40));

        // Play() on an ended video seeks to the start, so a system that called it every frame would
        // hold the picture on frame zero for ever.
        Assert.True(world.Read<VideoSurface>(entity).Started);
        Assert.Equal(TimeSpan.FromMilliseconds(40), player.Position);
    }

    [Fact]
    public void TheInfoComponentIsOptional() {
        using var world = new World();
        var system = new VideoSystem();

        var entity = world.Create();

        world.Add(entity, new VideoSurface { Player = Player(2), PlayOnStart = true });

        // No VideoPlaybackInfo. A surface on its own must still advance.
        system.Step(world, TimeSpan.FromMilliseconds(40));
    }

    [Fact]
    public void ASurfaceWithNoPlayerIsSkipped() {
        using var world = new World();
        var system = new VideoSystem();

        var entity = world.Create();

        world.Add(entity, default(VideoSurface));

        system.Step(world, TimeSpan.FromMilliseconds(40));

        Assert.Null(world.Read<VideoSurface>(entity).Player);
    }

    [Fact]
    public void TheComponentCanOwnTheLoopFlag() {
        using var world = new World();
        var system = new VideoSystem();

        var player = Player(2);
        var entity = world.Create();

        world.Add(entity, new VideoSurface { Player = player, OverridesLoop = true, Loop = true });

        system.Step(world, TimeSpan.Zero);

        Assert.True(player.Loop);
    }

    [Fact]
    public void APlayerConfiguredInCodeIsNotOverwrittenByAComponentNobodyTouched() {
        using var world = new World();
        var system = new VideoSystem();

        var player = Player(2);

        player.Loop = true;

        var entity = world.Create();

        world.Add(entity, new VideoSurface { Player = player });

        system.Step(world, TimeSpan.Zero);

        Assert.True(player.Loop);
    }

    static VideoPlayer Player(int frames) =>
        new(
            new WebMVideoStreamDecoder(VideoTestContent.Video(16, 16, frames).Stream()),
            new VideoPlayerOptions { UseDecodeThread = false, QueueCapacity = 2 }
        );
}
