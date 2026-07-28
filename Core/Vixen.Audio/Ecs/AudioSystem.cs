// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Mixing;
using Vixen.Audio.Spatial;
using Vixen.Core.Mathematics;
using Vixen.Core.Threading;
using Vixen.Ecs;
using Vixen.Ecs.Systems;
using Vixen.Engine.Transforms;

namespace Vixen.Audio.Ecs;

/// <summary>Makes the mixer agree with the world, once a frame.</summary>
/// <remarks>
///     <para>
///         <b>Declarative, not imperative.</b> Game code writes <c>source.Playback =
///         AudioPlayback.Playing</c> and this reconciles: it starts what should be playing, stops
///         what should not, pushes the gain and the position of everything that is, and writes
///         <see cref="AudioPlayback.Stopped" /> back when a sound runs out. So a sound survives a
///         save and a reload — it is a component value, not a handle somebody has to have kept — and
///         "is the alarm still going" is a component read.
///     </para>
///     <para>
///         <b>In <see cref="SystemPhase.PostRender" />, deliberately.</b> <c>WorldTransform</c> is
///         resolved in <see cref="SystemPhase.PreRender" />, so this is the first phase in which a
///         source's position is this frame's rather than last frame's — and audio has nothing to say
///         to the renderer, so doing it after submission overlaps it with the GPU instead of
///         competing with culling.
///     </para>
///     <para>
///         <b>It does its work inline and returns the dependency it was given.</b> The whole pass is
///         a walk over the handful of entities that make noise, and the expensive part of audio — the
///         mixing — is on the device's thread and not in the frame at all.
///     </para>
/// </remarks>
/// <param name="engine">The engine to drive.</param>
[UpdateInGroup(SystemPhase.PostRender)]
public sealed class AudioSystem(AudioEngine engine) : SystemBase, IDeclaredAccess {
    readonly QueryDescription listeners = new QueryDescription()
        .WithAll<AudioListenerComponent, WorldTransform>();

    readonly QueryDescription positioned = new QueryDescription()
        .WithAll<AudioSource, AudioClipRef, AudioSpatial, WorldTransform>();

    readonly QueryDescription ambient = new QueryDescription()
        .WithAll<AudioSource, AudioClipRef>()
        .WithNone<AudioSpatial>();

    /// <summary>How many entities carried <see cref="AudioListenerComponent" /> in the last pass.</summary>
    /// <remarks>
    ///     More than one is a mistake in the scene: there is one set of speakers, the first listener
    ///     found is the one used, and which one that is depends on chunk order — so it will sound
    ///     right until an unrelated change reorders the archetypes.
    /// </remarks>
    public int ListenerCount { get; private set; }

    /// <inheritdoc />
    /// <remarks>
    ///     Declared here rather than with attributes, because naming a component type in a generic
    ///     call is what assigns it an id — an attribute can only look one up, and on the first frame
    ///     there is nothing to look up.
    /// </remarks>
    public SystemAccess Access { get; } = SystemAccess.Declare()
        .Read<WorldTransform>()
        .Read<AudioClipRef>()
        .Write<AudioSource>()
        .Write<AudioSpatial>()
        .Write<AudioListenerComponent>()
        .Build();

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        Synchronize(context.World, context.Time.DeltaSeconds);
        return dependency;
    }

    /// <summary>Reconciles every source and the listener with the mixer.</summary>
    /// <param name="world">The world.</param>
    /// <param name="deltaSeconds">How long the frame was, for the velocity derivation.</param>
    /// <remarks>Public so a test, a tool or an editor can drive audio without standing up a runner.</remarks>
    public void Synchronize(World world, float deltaSeconds) {
        ArgumentNullException.ThrowIfNull(world);

        UpdateListener(world, deltaSeconds);
        UpdatePositioned(world, deltaSeconds);
        UpdateAmbient(world);

        // The frame's delta and not a wall clock: a fade that kept running under a pause menu, or
        // ignored slow motion, is a bug somebody spends an afternoon on.
        engine.Update(deltaSeconds);
    }

    void UpdateListener(World world, float deltaSeconds) {
        var found = 0;

        foreach (var chunk in world.Chunks(listeners)) {
            var components = chunk.Values<AudioListenerComponent>();
            var transforms = chunk.ReadValues<WorldTransform>();

            for (var i = 0; i < chunk.Count; i++) {
                found++;

                if (found > 1) {
                    continue;
                }

                ref var component = ref components[i];
                var matrix = transforms[i].Value;
                var position = matrix.Translation;

                engine.SetListener(new AudioListener {
                    Position = position,
                    Forward = matrix.Forward,
                    Up = matrix.Up,
                    Velocity = Track(ref component.Velocity,
                        ref component.PreviousPosition,
                        ref component.HasPreviousPosition,
                        component.AutoVelocity,
                        position,
                        deltaSeconds),
                    Gain = component.Gain
                });
            }
        }

        ListenerCount = found;
    }

    void UpdatePositioned(World world, float deltaSeconds) {
        foreach (var chunk in world.Chunks(positioned)) {
            var sources = chunk.Values<AudioSource>();
            var spatials = chunk.Values<AudioSpatial>();
            var transforms = chunk.ReadValues<WorldTransform>();
            var entities = chunk.Entities;

            for (var i = 0; i < chunk.Count; i++) {
                ref var source = ref sources[i];
                ref var spatial = ref spatials[i];
                var matrix = transforms[i].Value;
                var position = matrix.Translation;

                Track(ref spatial.Velocity,
                    ref spatial.PreviousPosition,
                    ref spatial.HasPreviousPosition,
                    spatial.AutoVelocity,
                    position,
                    deltaSeconds);

                var settings = spatial.ToSettings(position, matrix.Forward);
                Reconcile(ref source, world.Read<AudioClipRef>(entities[i]).Clip, spatial: true, settings);
            }
        }
    }

    void UpdateAmbient(World world) {
        foreach (var chunk in world.Chunks(ambient)) {
            var sources = chunk.Values<AudioSource>();
            var entities = chunk.Entities;

            for (var i = 0; i < chunk.Count; i++) {
                Reconcile(
                    ref sources[i],
                    world.Read<AudioClipRef>(entities[i]).Clip,
                    spatial: false,
                    default
                );
            }
        }
    }

    void Reconcile(ref AudioSource source, AudioClip? clip, bool spatial, in SpatialSettings settings) {
        var state = source.Voice.IsValid ? engine.StateOf(source.Voice) : VoiceState.Free;
        var alive = state is VoiceState.Playing or VoiceState.Paused or VoiceState.Stopping;

        if (source.Voice.IsValid && !alive) {
            // It ran out on its own. Reporting that as a component value is what lets gameplay wait
            // on a sound without holding a handle across frames.
            source.Voice = VoiceHandle.None;

            if (source.Playback is AudioPlayback.Playing) {
                source.Playback = AudioPlayback.Stopped;
            }
        }

        switch (source.Playback) {
            case AudioPlayback.Playing when !alive:
                if (clip is not null) {
                    source.Voice = engine.Play(clip, new PlaybackSettings {
                        Bus = source.Bus,
                        Gain = source.Gain,
                        Pitch = source.Pitch,
                        Pan = source.Pan,
                        Loop = source.Loop,
                        Priority = source.Priority,
                        IsSpatial = spatial,
                        Spatial = settings
                    });
                }

                break;

            case AudioPlayback.Playing:
                if (state is VoiceState.Paused) {
                    engine.Resume(source.Voice);
                }

                Push(source, spatial, settings);
                break;

            case AudioPlayback.Paused when alive:
                if (state is VoiceState.Playing) {
                    engine.Pause(source.Voice);
                }

                Push(source, spatial, settings);
                break;

            case AudioPlayback.Stopped when alive:
                engine.Stop(source.Voice);
                source.Voice = VoiceHandle.None;
                break;

            default:
                break;
        }
    }

    void Push(in AudioSource source, bool spatial, in SpatialSettings settings) {
        engine.SetGain(source.Voice, source.Gain);
        engine.SetPitch(source.Voice, source.Pitch);

        if (spatial) {
            engine.SetSpatial(source.Voice, settings);
        } else {
            engine.SetPan(source.Voice, source.Pan);
        }
    }

    static Vector3 Track(
        ref Vector3 velocity,
        ref Vector3 previous,
        ref bool hasPrevious,
        bool automatic,
        Vector3 position,
        float deltaSeconds
    ) {
        if (!automatic) {
            return velocity;
        }

        // The first frame has nothing to subtract from, and subtracting the origin would give a
        // source spawned two hundred units out a velocity of two hundred units a frame — a doppler
        // shift that turns the first block of every sound into a chirp.
        velocity = hasPrevious && deltaSeconds > 0f ? (position - previous) / deltaSeconds : Vector3.Zero;
        previous = position;
        hasPrevious = true;
        return velocity;
    }
}
