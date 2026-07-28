// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Events;
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

    // Four queries and not two, because an event and a clip are alternatives: an entity carrying both
    // must be reconciled once, and WithNone is what makes "the event wins" an archetype fact rather
    // than a branch that runs twice and starts two sounds.
    readonly QueryDescription positioned = new QueryDescription()
        .WithAll<AudioSource, AudioClipRef, AudioSpatial, WorldTransform>()
        .WithNone<AudioEventRef>();

    readonly QueryDescription ambient = new QueryDescription()
        .WithAll<AudioSource, AudioClipRef>()
        .WithNone<AudioSpatial, AudioEventRef>();

    readonly QueryDescription positionedEvents = new QueryDescription()
        .WithAll<AudioSource, AudioEventRef, AudioSpatial, WorldTransform>();

    readonly QueryDescription ambientEvents = new QueryDescription()
        .WithAll<AudioSource, AudioEventRef>()
        .WithNone<AudioSpatial>();

    readonly QueryDescription zones = new QueryDescription()
        .WithAll<AudioReverbZoneRef, WorldTransform>();

    /// <summary>How many entities carried <see cref="AudioListenerComponent" /> in the last pass.</summary>
    /// <remarks>
    ///     <para>
    ///         Up to <see cref="AudioListenerSet.MaxListeners" /> of them are used, which is what makes
    ///         split-screen work. Past that they are counted and ignored, and which ones survive
    ///         depends on chunk order — so a scene with five is a scene that will sound right until an
    ///         unrelated change reorders the archetypes.
    ///     </para>
    ///     <para>
    ///         One is still the ordinary case, and a set of one behaves exactly as a single listener
    ///         always did.
    ///     </para>
    /// </remarks>
    public int ListenerCount { get; private set; }

    /// <summary>How many reverb zone entities were live in the last pass.</summary>
    public int ZoneCount { get; private set; }

    /// <inheritdoc />
    /// <remarks>
    ///     Declared here rather than with attributes, because naming a component type in a generic
    ///     call is what assigns it an id — an attribute can only look one up, and on the first frame
    ///     there is nothing to look up.
    /// </remarks>
    public SystemAccess Access { get; } = SystemAccess.Declare()
        .Read<WorldTransform>()
        .Read<AudioClipRef>()
        .Read<AudioEventRef>()
        .Read<AudioReverbZoneRef>()
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
        UpdatePositionedEvents(world, deltaSeconds);
        UpdateAmbientEvents(world);
        UpdateZones(world);

        // The frame's delta and not a wall clock: a fade that kept running under a pause menu, or
        // ignored slow motion, is a bug somebody spends an afternoon on.
        engine.Update(deltaSeconds);
    }

    /// <summary>Rebuilds the reverb zones from whatever is in the world this frame.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Rebuilt and not maintained.</b> A zone entity that has been destroyed, disabled or
    ///         had its component removed simply stops appearing here, and stops being a zone — no
    ///         teardown to forget and no handle to leak. The parameters it drove are still released,
    ///         because the set remembers every name it has ever seen even when the zone is gone.
    ///     </para>
    ///     <para>
    ///         Before <c>engine.Update</c>, which is where the winner is worked out and written to
    ///         the mixer's parameters — so a room entered this frame is heard this frame.
    ///     </para>
    /// </remarks>
    void UpdateZones(World world) {
        engine.ReverbZones.BeginSync();
        ZoneCount = 0;

        foreach (var chunk in world.Chunks(zones)) {
            var entities = chunk.Entities;
            var transforms = chunk.ReadValues<WorldTransform>();

            for (var i = 0; i < chunk.Count; i++) {
                // One at a time, because the zone is a reference and a managed component's values
                // live in the world's store rather than in the chunk — the same reason
                // AudioEventRef is read this way a few methods down.
                var placed = world.Read<AudioReverbZoneRef>(entities[i]);

                if (!placed.Enabled || placed.Zone is not { } zone) {
                    continue;
                }

                engine.ReverbZones.Sync(zone, transforms[i].Value.Translation);
                ZoneCount++;
            }
        }
    }

    void UpdateListener(World world, float deltaSeconds) {
        var found = 0;
        var set = default(AudioListenerSet);

        foreach (var chunk in world.Chunks(listeners)) {
            var components = chunk.Values<AudioListenerComponent>();
            var transforms = chunk.ReadValues<WorldTransform>();

            for (var i = 0; i < chunk.Count; i++) {
                found++;
                ref var component = ref components[i];
                var matrix = transforms[i].Value;
                var position = matrix.Translation;

                // Velocity is tracked for every listener, including the ones past the cap: it is the
                // component's own state, and skipping it would leave a listener that came back inside
                // the cap deriving its velocity from wherever it was when it dropped out.
                var velocity = Track(ref component.Velocity,
                    ref component.PreviousPosition,
                    ref component.HasPreviousPosition,
                    component.AutoVelocity,
                    position,
                    deltaSeconds);

                set.TryAdd(
                    new AudioListener {
                        Position = position,
                        Forward = matrix.Forward,
                        Up = matrix.Up,
                        Velocity = velocity,
                        Gain = component.Gain
                    },
                    component.Weight
                );
            }
        }

        ListenerCount = found;

        if (found > 0) {
            engine.SetListeners(set);
        }
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
                Reconcile(ref source, world.Read<AudioClipRef>(entities[i]).Clip, null, spatial: true, settings);
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
                    null,
                    spatial: false,
                    default
                );
            }
        }
    }

    void UpdatePositionedEvents(World world, float deltaSeconds) {
        foreach (var chunk in world.Chunks(positionedEvents)) {
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

                // The entity supplies where and how fast; the event supplies everything else about
                // how that place sounds. AudioSpatial's own rolloff and cone are not read here.
                var sound = world.Read<AudioEventRef>(entities[i]).Event;
                var settings = sound?.Place(position, spatial.Velocity, matrix.Forward) ?? default;
                Reconcile(ref source, null, sound, spatial: true, settings);
            }
        }
    }

    void UpdateAmbientEvents(World world) {
        foreach (var chunk in world.Chunks(ambientEvents)) {
            var sources = chunk.Values<AudioSource>();
            var entities = chunk.Entities;

            for (var i = 0; i < chunk.Count; i++) {
                Reconcile(
                    ref sources[i],
                    null,
                    world.Read<AudioEventRef>(entities[i]).Event,
                    spatial: false,
                    default
                );
            }
        }
    }

    void Reconcile(
        ref AudioSource source,
        AudioClip? clip,
        AudioEvent? sound,
        bool spatial,
        in SpatialSettings settings
    ) {
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
                if (sound is not null) {
                    source.Voice = sound.Play(new AudioEventPlayback {
                        Position = settings.Position,
                        Velocity = settings.Velocity,
                        ConeDirection = settings.ConeDirection,
                        Gain = source.Gain,
                        Pitch = source.Pitch
                    });

                    // What the event chose for this play, so the per-frame push can scale it rather
                    // than overwrite it. A refused play leaves them alone; nothing reads them.
                    if (source.Voice.IsValid) {
                        source.VoiceGainScale = sound.LastGain;
                        source.VoicePitchScale = sound.LastPitch;
                    }
                } else if (clip is not null) {
                    source.VoiceGainScale = 1f;
                    source.VoicePitchScale = 1f;

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
        // Scaled, not replaced. For a clip the scales are one and this is the gain that was asked
        // for; for an event they carry the variant and the randomisation, which the source knows
        // nothing about and must not flatten.
        engine.SetGain(source.Voice, source.Gain * source.VoiceGainScale);
        engine.SetPitch(source.Voice, source.Pitch * source.VoicePitchScale);

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
