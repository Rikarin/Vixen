// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Effects;
using Vixen.Audio.Mixing;
using Vixen.Audio.Spatial;
using Vixen.Core.Mathematics;

namespace Vixen.Audio.Events;

/// <summary>Where a play of an event is, and any trim on top of what the event says.</summary>
/// <remarks>
///     Everything here is the caller's business and none of it is the sound's: a position changes
///     every frame, a rolloff does not. The event owns the second kind, so this is small.
/// </remarks>
public readonly record struct AudioEventPlayback() {
    /// <summary>Where in the world. Read only when the event is spatial.</summary>
    public Vector3 Position { get; init; }

    /// <summary>How fast it is moving, for doppler.</summary>
    public Vector3 Velocity { get; init; }

    /// <summary>Which way it faces, for a cone. Zero keeps the event's own direction.</summary>
    public Vector3 ConeDirection { get; init; }

    /// <summary>A linear gain multiplied into the event's, for a trim the event should not remember.</summary>
    public float Gain { get; init; } = 1f;

    /// <summary>A pitch ratio multiplied into the event's.</summary>
    public float Pitch { get; init; } = 1f;

    /// <summary>Whether it starts paused, so the caller can position it before a block is heard.</summary>
    public bool StartPaused { get; init; }
}

/// <summary>A sound as a designer describes it, rather than a file as a programmer plays it.</summary>
/// <remarks>
///     <para>
///         <b>This is the layer that separates an audio engine from audio middleware.</b> Gameplay
///         says <c>footsteps.Play(feet)</c>. Which of the five takes, at what pitch, how loud, on
///         which bus, how far it carries, how many may sound at once and which one gives way when
///         they do — all of that is the event's, and every one of those decisions is a thing a sound
///         designer wants to change and a programmer should not have to.
///     </para>
///     <para>
///         <b>It plays through the ordinary front door.</b> Every path here ends in
///         <c>AudioEngine.Play</c> with a <see cref="PlaybackSettings" /> it computed; there is no
///         second play path into the mixer and nothing here the mixer knows about. That is deliberate
///         — an event is an authoring idea, and the mixer stays a thing that renders voices.
///     </para>
///     <para>
///         <b>Nothing is allocated by a play.</b> The variant list, the weights, the shuffle bag and
///         the instance table are all sized once, at construction; a play is some arithmetic, an index
///         and a call. <c>docs/plan/00</c> forbids garbage in the frame loop, and a footstep is the
///         frame loop.
///     </para>
///     <para>
///         <b>Game thread only.</b> An event holds its selection state and its live instances without
///         a lock, and the audio thread never sees one. Two threads playing the same event is a bug
///         in the caller, and the shape of an engine that wants that is one event per thread.
///     </para>
/// </remarks>
public sealed class AudioEvent {
    readonly AudioEngine engine;
    readonly AudioClip[] clips;
    readonly float[] variantGain;
    readonly float[] variantPitch;
    readonly VoiceHandle[] instances;
    readonly float gain;
    readonly float gainVarianceDb;
    readonly float pitchVarianceSemitones;
    readonly bool loop;
    readonly int priority;
    Xorshift32 random;
    int live;

    /// <summary>What it is called.</summary>
    public string Name { get; }

    /// <summary>Which bus its plays route into.</summary>
    public int Bus { get; }

    /// <summary>Which variant plays next, and the state behind that.</summary>
    /// <remarks>Exposed because "did it actually stop repeating" is a question worth being able to ask.</remarks>
    public VariantSelector Variants { get; }

    /// <summary>How many sounds it can choose between.</summary>
    public int VariantCount => clips.Length;

    /// <summary>How many copies may sound at once. Zero is no limit.</summary>
    public int MaxInstances { get; }

    /// <summary>What gives way when <see cref="MaxInstances" /> is reached.</summary>
    public EventStealMode Steal { get; }

    /// <summary>Whether its plays are things in the world.</summary>
    public bool IsSpatial { get; }

    /// <summary>How its plays attenuate. The position is the caller's.</summary>
    public SpatialSettings Spatial { get; }

    /// <summary>The gain the last play landed on, before the caller's own trim.</summary>
    /// <remarks>
    ///     The event's level, the variant's correction and that play's roll of the dice, multiplied
    ///     together. Reported because a play is the only place they exist: anything that wants to
    ///     change a sound's gain later — the ECS integration does, every frame — has to scale what
    ///     the event decided rather than replace it, or the variation lasts exactly one block.
    /// </remarks>
    public float LastGain { get; private set; } = 1f;

    /// <summary>The pitch ratio the last play landed on, before the caller's own trim.</summary>
    public float LastPitch { get; private set; } = 1f;

    /// <summary>How many of its plays are still sounding.</summary>
    /// <remarks>Counts what is playing or paused; a sound already fading out has been let go of.</remarks>
    public int InstanceCount {
        get {
            Reap();
            return live;
        }
    }

    /// <summary>Builds an event an engine can play.</summary>
    /// <param name="engine">The engine its plays go to.</param>
    /// <param name="description">What it is.</param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public AudioEvent(AudioEngine engine, AudioEventDescription description) {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(description);

        this.engine = engine;
        Name = description.Name;
        Bus = description.Bus;
        MaxInstances = Math.Max(description.MaxInstances, 0);
        Steal = description.Steal;
        IsSpatial = description.IsSpatial;
        Spatial = description.Spatial;
        gain = Decibels.ToLinear(description.GainDb);
        gainVarianceDb = MathF.Abs(description.GainVarianceDb);
        pitchVarianceSemitones = MathF.Abs(description.PitchVarianceSemitones);
        loop = description.Loop;
        priority = description.Priority;

        // A variant with no clip is dropped rather than kept as a hole: the alternative is a silent
        // draw, which reads to a player as the sound failing to fire at random.
        var kept = 0;

        foreach (var variant in description.Variants) {
            if (variant.Clip is not null) {
                kept++;
            }
        }

        clips = new AudioClip[kept];
        variantGain = new float[kept];
        variantPitch = new float[kept];
        var weights = new float[kept];
        var at = 0;

        foreach (var variant in description.Variants) {
            if (variant.Clip is null) {
                continue;
            }

            clips[at] = variant.Clip;
            variantGain[at] = Decibels.ToLinear(variant.GainDb);
            variantPitch[at] = Semitones(variant.PitchSemitones);
            weights[at] = variant.Weight;
            at++;
        }

        Variants = new(weights, description.Selection, description.Seed);

        // The random sequence for gain and pitch is its own, and not the selector's. Sharing one
        // would make the pitch of a play depend on how many variants the event happens to have, so
        // adding a sixth footstep would change how the other five sound.
        random = new(description.Seed ^ 0x5BF03635);

        // An unlimited event still tracks, up to what the pool could hold, because that is what makes
        // StopAll work — and a looping ambience that cannot be stopped as a group is the thing
        // somebody discovers at the end of the project.
        instances = new VoiceHandle[MaxInstances > 0 ? MaxInstances : engine.VoiceCapacity];
    }

    /// <summary>Plays it, in the room.</summary>
    /// <returns>A handle, or <see cref="VoiceHandle.None" /> if it was refused or the pool was full.</returns>
    public VoiceHandle Play() => Play(new AudioEventPlayback());

    /// <summary>Plays it at a place in the world.</summary>
    /// <param name="position">Where.</param>
    /// <returns>A handle, or <see cref="VoiceHandle.None" /> if it was refused or the pool was full.</returns>
    /// <remarks>The overload that most calls want: gameplay knows where a thing is and nothing else.</remarks>
    public VoiceHandle Play(Vector3 position) => Play(new AudioEventPlayback { Position = position });

    /// <summary>Plays it.</summary>
    /// <param name="attributes">Where it is, and any trim.</param>
    /// <returns>A handle, or <see cref="VoiceHandle.None" /> if it was refused or the pool was full.</returns>
    public VoiceHandle Play(in AudioEventPlayback attributes) {
        if (clips.Length == 0) {
            return VoiceHandle.None;
        }

        Reap();

        // Before the draw, so a refused request does not advance the shuffle bag — otherwise a busy
        // event quietly skips variants and the round-robin guarantee is not one.
        if (MaxInstances > 0 && live >= MaxInstances && !MakeRoom()) {
            return VoiceHandle.None;
        }

        var variant = Variants.Next();
        var level = gain * variantGain[variant];
        var rate = variantPitch[variant];

        if (gainVarianceDb > 0f) {
            level *= Decibels.ToLinear(random.NextBipolar() * gainVarianceDb);
        }

        if (pitchVarianceSemitones > 0f) {
            rate *= Semitones(random.NextBipolar() * pitchVarianceSemitones);
        }

        LastGain = level;
        LastPitch = rate;

        var handle = engine.Play(clips[variant], new PlaybackSettings {
            Bus = Bus,
            Gain = level * attributes.Gain,
            Pitch = rate * attributes.Pitch,
            Loop = loop,
            Priority = priority,
            IsSpatial = IsSpatial,
            StartPaused = attributes.StartPaused,
            Spatial = IsSpatial ? Locate(attributes) : default
        });

        if (handle.IsValid) {
            Record(handle);
        }

        return handle;
    }

    /// <summary>Stops every copy of it that is still sounding.</summary>
    public void StopAll() {
        for (var i = 0; i < live; i++) {
            engine.Stop(instances[i]);
        }

        live = 0;
    }

    /// <summary>Fades every copy of it out and stops each when it gets there.</summary>
    /// <param name="duration">How long to take.</param>
    /// <param name="curve">Which way.</param>
    /// <remarks>What a looping ambience wants when a level ends. <see cref="StopAll" /> takes one block.</remarks>
    public void FadeOutAll(TimeSpan duration, AudioFadeCurve curve = AudioFadeCurve.Decibel) {
        for (var i = 0; i < live; i++) {
            engine.FadeOutAndStop(instances[i], duration, curve);
        }

        // Let go of them here rather than when they land: they are on their way out and must not
        // count against the instance limit for the whole of a two-second fade.
        live = 0;
    }

    /// <summary>The event's own spatial settings, with a caller's position filled in.</summary>
    /// <param name="position">Where it is.</param>
    /// <param name="velocity">How fast it is moving.</param>
    /// <param name="coneDirection">Which way it faces. Zero keeps the event's.</param>
    /// <returns>The settings a play at that place would use.</returns>
    /// <remarks>
    ///     Public because anything moving a sound the event started has to keep the rest of the
    ///     settings — writing a bare position over them would replace the event's rolloff and cone
    ///     with whatever the caller happened to have, one frame after it started.
    /// </remarks>
    public SpatialSettings Place(Vector3 position, Vector3 velocity, Vector3 coneDirection) => Spatial with {
        Position = position,
        Velocity = velocity,
        ConeDirection = coneDirection == Vector3.Zero ? Spatial.ConeDirection : coneDirection
    };

    SpatialSettings Locate(in AudioEventPlayback attributes) =>
        Place(attributes.Position, attributes.Velocity, attributes.ConeDirection);

    /// <summary>Drops the instances that have ended on their own.</summary>
    /// <remarks>
    ///     Lazily, from the calls that care, rather than from a per-frame pass over every event in the
    ///     game. The table is at most a few entries and the walk happens on a play, which was about to
    ///     do considerably more work than this.
    /// </remarks>
    void Reap() {
        var kept = 0;

        for (var i = 0; i < live; i++) {
            if (engine.StateOf(instances[i]) is VoiceState.Playing or VoiceState.Paused) {
                instances[kept++] = instances[i];
            }
        }

        live = kept;
    }

    bool MakeRoom() {
        var victim = Steal switch {
            EventStealMode.Oldest => 0,
            EventStealMode.Newest => live - 1,
            EventStealMode.Quietest => Quietest(),
            _ => -1
        };

        if ((uint)victim >= (uint)live) {
            return false;
        }

        engine.Stop(instances[victim]);
        Array.Copy(instances, victim + 1, instances, victim, live - victim - 1);
        live--;
        return true;
    }

    int Quietest() {
        var best = 0;
        var quietest = float.MaxValue;

        for (var i = 0; i < live; i++) {
            var audibility = engine.AudibilityOf(instances[i]);

            if (audibility < quietest) {
                quietest = audibility;
                best = i;
            }
        }

        return best;
    }

    void Record(VoiceHandle handle) {
        // Only reachable on an unlimited event that has more copies going than the pool has slots,
        // which means the pool has been stealing from it. Forgetting the oldest keeps the newest
        // stoppable, which is the more useful half.
        if (live == instances.Length) {
            Array.Copy(instances, 1, instances, 0, instances.Length - 1);
            live--;
        }

        instances[live++] = handle;
    }

    static float Semitones(float value) => value == 0f ? 1f : MathF.Pow(2f, value / 12f);
}
