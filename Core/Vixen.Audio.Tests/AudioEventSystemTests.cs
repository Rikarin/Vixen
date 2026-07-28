// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Devices;
using Vixen.Audio.Ecs;
using Vixen.Audio.Events;
using Vixen.Audio.Spatial;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Transforms;
using Xunit;

namespace Vixen.Audio.Tests;

/// <summary>An entity that plays an event rather than a clip.</summary>
public sealed class AudioEventSystemTests : IDisposable {
    readonly World world = new("AudioEvents");
    readonly AudioEngine engine;
    readonly NullAudioDevice device;
    readonly AudioSystem system;

    public AudioEventSystemTests() {
        (engine, device) = AudioTestData.Engine(channels: 2, voices: 16);
        system = new(engine);
    }

    public void Dispose() {
        system.Dispose();
        engine.Dispose();
        world.Dispose();
    }

    AudioEvent Sound(AudioEventDescription description) => new(engine, description);

    static AudioEventDescription OneClip(int frames = 48_000) => new() {
        Variants = [new(AudioTestData.Constant(frames, 1f))]
    };

    Entity Emitter(AudioEvent sound, AudioSource source) {
        var entity = world.Create();
        world.Add(entity, source);
        world.Add(entity, new AudioEventRef { Event = sound });
        return entity;
    }

    Entity Listener(Vector3 position) {
        var entity = world.Create();
        world.Add(entity, AudioListenerComponent.Default);
        world.Add(entity, new WorldTransform { Value = Matrix4x4.FromTranslation(position) });
        return entity;
    }

    [Fact]
    public void AnEntityCarryingAnEventPlaysThroughIt() {
        var sound = Sound(OneClip());
        var entity = Emitter(sound, AudioSource.Playing);

        system.Synchronize(world, 1f / 60f);

        Assert.True(world.Read<AudioSource>(entity).Voice.IsValid);
        Assert.Equal(1, sound.InstanceCount);
        Assert.True(AudioTestData.Peak(AudioTestData.Render(device, 16)) > 0f);
    }

    [Fact]
    public void AnEventRefWithNothingInItIsNotAnError() {
        var entity = world.Create();
        world.Add(entity, AudioSource.Playing);
        world.Add(entity, new AudioEventRef());

        system.Synchronize(world, 1f / 60f);

        Assert.False(world.Read<AudioSource>(entity).Voice.IsValid);
    }

    /// <summary>An entity with both is a question with one good answer, and the archetype gives it.</summary>
    [Fact]
    public void TheEventWinsOverAClipOnTheSameEntity() {
        var sound = Sound(OneClip() with { GainDb = -12f });
        var entity = Emitter(sound, AudioSource.Playing);
        world.Add(entity, new AudioClipRef { Clip = AudioTestData.Constant(48_000, 1f) });

        system.Synchronize(world, 1f / 60f);

        // One voice and not two, and its gain came from the event rather than from the clip path.
        Assert.Equal(1, sound.InstanceCount);
        Assert.Equal(0.2512f, world.Read<AudioSource>(entity).VoiceGainScale, 1e-3f);
    }

    /// <summary>
    ///     The reason <c>AudioSource</c> carries a scale at all. The system pushes gain every frame; if
    ///     it pushed the source's own value the event's level, the variant's correction and that play's
    ///     randomisation would all be gone one frame after the sound started.
    /// </summary>
    [Fact]
    public void ThePerFrameGainPushScalesWhatTheEventChoseInsteadOfReplacingIt() {
        var sound = Sound(OneClip() with { GainDb = -20f });
        var entity = Emitter(sound, AudioSource.Playing with { Gain = 0.5f });

        system.Synchronize(world, 1f / 60f);
        var started = engine.GainOf(world.Read<AudioSource>(entity).Voice);

        // The frame after, which is the one that used to flatten it.
        system.Synchronize(world, 1f / 60f);
        var pushed = engine.GainOf(world.Read<AudioSource>(entity).Voice);

        Assert.Equal(0.05f, started, 1e-3f);
        Assert.Equal(started, pushed, 1e-5f);
    }

    [Fact]
    public void ThePitchPushScalesTooSoAVariantKeepsItsTuning() {
        var sound = Sound(new AudioEventDescription {
            Variants = [new(AudioTestData.Constant(48_000, 1f)) { PitchSemitones = 12f }]
        });

        var entity = Emitter(sound, AudioSource.Playing with { Pitch = 0.5f });

        system.Synchronize(world, 1f / 60f);
        system.Synchronize(world, 1f / 60f);

        // An octave up from the variant and an octave down from the source is where it started.
        Assert.Equal(1f, engine.PitchOf(world.Read<AudioSource>(entity).Voice), 1e-4f);
    }

    /// <summary>Where it is comes from the transform; how far it carries comes from the event.</summary>
    [Fact]
    public void ASpatialEventTakesItsPositionFromTheEntityAndItsRolloffFromItself() {
        Listener(Vector3.Zero);

        var sound = Sound(OneClip() with {
            IsSpatial = true,
            Spatial = new() { MinDistance = 1f, MaxDistance = 50f, Attenuation = AttenuationModel.Linear }
        });

        var entity = Emitter(sound, AudioSource.Playing);
        world.Add(entity, AudioSpatial.Default with { MaxDistance = 4f, Attenuation = AttenuationModel.Exponential });
        world.Add(entity, new WorldTransform { Value = Matrix4x4.FromTranslation(new Vector3(0f, 0f, 25f)) });

        system.Synchronize(world, 1f / 60f);
        AudioTestData.Render(device, 64);

        // Half way along a linear rolloff from 1 to 50 is about half volume. The entity's own
        // AudioSpatial says 4 and exponential, and is not what was used — it supplied the position.
        var audibility = engine.AudibilityOf(world.Read<AudioSource>(entity).Voice);
        Assert.InRange(audibility, 0.4f, 0.6f);
    }

    [Fact]
    public void StoppingTheSourceReleasesTheEventsInstance() {
        var sound = Sound(OneClip());
        var entity = Emitter(sound, AudioSource.Playing);

        system.Synchronize(world, 1f / 60f);
        Assert.Equal(1, sound.InstanceCount);

        world.Get<AudioSource>(entity).Playback = AudioPlayback.Stopped;
        system.Synchronize(world, 1f / 60f);

        Assert.Equal(0, sound.InstanceCount);
    }

    /// <summary>The whole point of the limit, seen from where it will actually be hit.</summary>
    [Fact]
    public void TheInstanceLimitHoldsAcrossManyEntitiesPlayingOneEvent() {
        var sound = Sound(OneClip() with { MaxInstances = 3, Steal = EventStealMode.None });

        for (var i = 0; i < 8; i++) {
            Emitter(sound, AudioSource.Playing);
        }

        system.Synchronize(world, 1f / 60f);

        Assert.Equal(3, sound.InstanceCount);
    }
}
