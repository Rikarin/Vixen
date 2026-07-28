// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Devices;
using Vixen.Audio.Ecs;
using Vixen.Audio.Mixing;
using Vixen.Audio.Spatial;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Transforms;
using Xunit;

namespace Vixen.Audio.Tests;

public sealed class AudioSystemTests : IDisposable {
    readonly World world = new("Audio");
    readonly AudioEngine engine;
    readonly NullAudioDevice device;
    readonly AudioSystem system;

    public AudioSystemTests() {
        (engine, device) = AudioTestData.Engine(channels: 2);
        system = new AudioSystem(engine);
    }

    public void Dispose() {
        system.Dispose();
        engine.Dispose();
        world.Dispose();
    }

    Entity Emitter(AudioClip clip, AudioSource source) {
        var entity = world.Create();
        world.Add(entity, source);
        world.Add(entity, new AudioClipRef { Clip = clip });
        return entity;
    }

    Entity PositionedEmitter(AudioClip clip, AudioSource source, Vector3 position) {
        var entity = Emitter(clip, source);
        world.Add(entity, AudioSpatial.Default);
        world.Add(entity, new WorldTransform { Value = Matrix4x4.FromTranslation(position) });
        return entity;
    }

    Entity Listener(Vector3 position) {
        var entity = world.Create();
        world.Add(entity, AudioListenerComponent.Default);
        world.Add(entity, new WorldTransform { Value = Matrix4x4.FromTranslation(position) });
        return entity;
    }

    [Fact]
    public void ASourceMarkedPlayingStarts() {
        var entity = Emitter(AudioTestData.Constant(4_800, 1f), AudioSource.Playing);

        system.Synchronize(world, 1f / 60f);

        var source = world.Read<AudioSource>(entity);

        Assert.True(source.Voice.IsValid);
        Assert.True(engine.IsPlaying(source.Voice));
        Assert.True(AudioTestData.Peak(AudioTestData.Render(device, 16)) > 0f);
    }

    [Fact]
    public void ASourceWithNoClipIsNotAnError() {
        var entity = world.Create();
        world.Add(entity, AudioSource.Playing);
        world.Add(entity, new AudioClipRef());

        system.Synchronize(world, 1f / 60f);

        Assert.False(world.Read<AudioSource>(entity).Voice.IsValid);
    }

    [Fact]
    public void ASourceIsNotRestartedEveryFrame() {
        var entity = Emitter(AudioTestData.Constant(4_800, 1f), AudioSource.Playing);

        system.Synchronize(world, 1f / 60f);
        var first = world.Read<AudioSource>(entity).Voice;

        AudioTestData.Render(device, 16);
        system.Synchronize(world, 1f / 60f);

        Assert.Equal(first, world.Read<AudioSource>(entity).Voice);
        Assert.Equal(1, engine.Statistics.ActiveVoices);
    }

    /// <summary>
    ///     Wanted, not actual: writing the state back is what makes "is the alarm still going" a
    ///     component read rather than a handle somebody had to keep across frames.
    /// </summary>
    [Fact]
    public void ASourceThatRunsOutReportsItselfStopped() {
        var entity = Emitter(AudioTestData.Constant(16, 1f), AudioSource.Playing);

        system.Synchronize(world, 1f / 60f);
        AudioTestData.Render(device, 128);
        system.Synchronize(world, 1f / 60f);

        var source = world.Read<AudioSource>(entity);

        Assert.Equal(AudioPlayback.Stopped, source.Playback);
        Assert.False(source.Voice.IsValid);
    }

    [Fact]
    public void SettingPlaybackToStoppedStopsTheSound() {
        var entity = Emitter(AudioTestData.Constant(48_000, 1f), AudioSource.Playing);

        system.Synchronize(world, 1f / 60f);
        AudioTestData.Render(device, 16);

        world.Get<AudioSource>(entity).Playback = AudioPlayback.Stopped;
        system.Synchronize(world, 1f / 60f);
        AudioTestData.Render(device, 128);
        system.Synchronize(world, 1f / 60f);

        Assert.False(world.Read<AudioSource>(entity).Voice.IsValid);
        Assert.Equal(0, engine.Statistics.ActiveVoices);
    }

    [Fact]
    public void PausingAndResumingGoThroughTheComponent() {
        var entity = Emitter(AudioTestData.Constant(48_000, 1f), AudioSource.Playing);

        system.Synchronize(world, 1f / 60f);
        var handle = world.Read<AudioSource>(entity).Voice;

        world.Get<AudioSource>(entity).Playback = AudioPlayback.Paused;
        system.Synchronize(world, 1f / 60f);
        Assert.Equal(VoiceState.Paused, engine.StateOf(handle));

        world.Get<AudioSource>(entity).Playback = AudioPlayback.Playing;
        system.Synchronize(world, 1f / 60f);
        Assert.Equal(VoiceState.Playing, engine.StateOf(handle));
    }

    [Fact]
    public void AGainChangeReachesTheVoice() {
        var entity = Emitter(AudioTestData.Constant(48_000, 1f), AudioSource.Playing);

        system.Synchronize(world, 1f / 60f);
        var loud = AudioTestData.Peak(AudioTestData.Render(device, 64));

        world.Get<AudioSource>(entity).Gain = 0.25f;
        system.Synchronize(world, 1f / 60f);
        AudioTestData.Render(device, 64);
        var quiet = AudioTestData.Peak(AudioTestData.Render(device, 64));

        Assert.Equal(loud * 0.25f, quiet, 0.001f);
    }

    [Fact]
    public void TheListenerFollowsItsTransform() {
        Listener(new Vector3(5f, 0f, 0f));
        system.Synchronize(world, 1f / 60f);

        Assert.Equal(new Vector3(5f, 0f, 0f), engine.Listener.Position);
        Assert.Equal(Vector3.Forward, engine.Listener.Forward);
        Assert.Equal(1, system.ListenerCount);
    }

    /// <summary>
    ///     There is one set of speakers. Two listeners is a mistake in the scene, and which one wins
    ///     would otherwise depend on chunk order — so it would sound right until an unrelated change
    ///     reordered the archetypes.
    /// </summary>
    [Fact]
    public void TwoListenersAreCountedSoTheMistakeIsVisible() {
        Listener(Vector3.Zero);
        Listener(new Vector3(100f, 0f, 0f));

        system.Synchronize(world, 1f / 60f);

        Assert.Equal(2, system.ListenerCount);
    }

    [Fact]
    public void APositionedSourceIsPannedByWhereItIs() {
        Listener(Vector3.Zero);
        PositionedEmitter(AudioTestData.Constant(48_000, 1f), AudioSource.Playing, new Vector3(10f, 0f, 0f));

        system.Synchronize(world, 1f / 60f);
        var rendered = AudioTestData.Render(device, 64);

        // Hard right: the listener's right is +X for a right-handed, Y-up, −Z-forward basis.
        Assert.Equal(0f, MathF.Abs(rendered[0]), 0.001f);
        Assert.True(MathF.Abs(rendered[1]) > 0f);
    }

    [Fact]
    public void MovingTheEntityMovesTheSound() {
        Listener(Vector3.Zero);
        var entity = PositionedEmitter(
            AudioTestData.Constant(48_000, 1f),
            AudioSource.Playing,
            new Vector3(10f, 0f, 0f)
        );

        system.Synchronize(world, 1f / 60f);
        AudioTestData.Render(device, 64);

        world.Get<WorldTransform>(entity).Value = Matrix4x4.FromTranslation(new Vector3(-10f, 0f, 0f));
        system.Synchronize(world, 1f / 60f);
        AudioTestData.Render(device, 64);
        var rendered = AudioTestData.Render(device, 64);

        Assert.True(MathF.Abs(rendered[0]) > 0f);
        Assert.Equal(0f, MathF.Abs(rendered[1]), 0.001f);
    }

    /// <summary>
    ///     Without a previous position the first frame would compute "however far it is from nowhere,
    ///     per frame" and doppler-shift the start of every sound into a chirp.
    /// </summary>
    [Fact]
    public void VelocityIsZeroOnTheFrameASourceAppears() {
        Listener(Vector3.Zero);
        var entity = PositionedEmitter(
            AudioTestData.Constant(48_000, 1f),
            AudioSource.Playing,
            new Vector3(0f, 0f, -200f)
        );

        system.Synchronize(world, 1f / 60f);

        Assert.Equal(Vector3.Zero, world.Read<AudioSpatial>(entity).Velocity);
    }

    [Fact]
    public void VelocityIsWorkedOutFromHowFarTheEntityMoved() {
        Listener(Vector3.Zero);
        var entity = PositionedEmitter(AudioTestData.Constant(48_000, 1f), AudioSource.Playing, Vector3.Zero);

        system.Synchronize(world, 0.5f);
        world.Get<WorldTransform>(entity).Value = Matrix4x4.FromTranslation(new Vector3(0f, 0f, -5f));
        system.Synchronize(world, 0.5f);

        // Five units in half a second is ten units a second.
        Assert.Equal(new Vector3(0f, 0f, -10f), world.Read<AudioSpatial>(entity).Velocity);
    }

    [Fact]
    public void AutoVelocityCanBeTurnedOffForASourceWithItsOwnPhysics() {
        Listener(Vector3.Zero);
        var entity = PositionedEmitter(AudioTestData.Constant(48_000, 1f), AudioSource.Playing, Vector3.Zero);

        world.Get<AudioSpatial>(entity).AutoVelocity = false;
        world.Get<AudioSpatial>(entity).Velocity = new Vector3(0f, 0f, 42f);

        system.Synchronize(world, 0.5f);
        world.Get<WorldTransform>(entity).Value = Matrix4x4.FromTranslation(new Vector3(0f, 0f, -5f));
        system.Synchronize(world, 0.5f);

        Assert.Equal(new Vector3(0f, 0f, 42f), world.Read<AudioSpatial>(entity).Velocity);
    }

    [Fact]
    public void AnAmbientSourceIsNotSpatialisedAtAll() {
        Listener(new Vector3(1_000f, 0f, 0f));
        Emitter(AudioTestData.Constant(48_000, 1f), AudioSource.Playing);

        system.Synchronize(world, 1f / 60f);
        var rendered = AudioTestData.Render(device, 64);

        // A thousand units away and still at full volume, because it is a sound in the room.
        Assert.Equal(0.7071f, MathF.Abs(rendered[0]), 0.001f);
    }
}
