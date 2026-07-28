// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Mixing;
using Vixen.Core.Mathematics;

namespace Vixen.Audio.Events;

/// <summary>How often, how far and how wide a scatterer throws its sound.</summary>
public sealed record AudioScattererSettings {
    /// <summary>The shortest gap between two spawns, in seconds.</summary>
    public float MinimumInterval { get; init; } = 1f;

    /// <summary>The longest gap between two spawns, in seconds.</summary>
    /// <remarks>
    ///     <b>A range and not a rate.</b> A fixed interval is a metronome, and a metronome is the one
    ///     thing an ambience must never sound like — the ear finds a period of a second or two within
    ///     about four repetitions and then cannot stop hearing it.
    /// </remarks>
    public float MaximumInterval { get; init; } = 4f;

    /// <summary>How close to the origin a spawn may land.</summary>
    /// <remarks>
    ///     Rarely zero. A bird that lands on the listener's head is the failure mode of every scatterer
    ///     ever written, and the fix is a hole in the middle rather than a rule about direction.
    /// </remarks>
    public float MinimumDistance { get; init; } = 5f;

    /// <summary>How far from the origin a spawn may land.</summary>
    public float MaximumDistance { get; init; } = 30f;

    /// <summary>How much of the vertical a spawn may use, from 0 for a flat ring to 1 for a whole sphere.</summary>
    /// <remarks>
    ///     Low by default, because most ambience comes from around rather than above: birds are in
    ///     trees and drips are on a ceiling, but neither is directly overhead, and a full sphere puts
    ///     a third of everything somewhere a listener will find odd.
    /// </remarks>
    public float VerticalSpread { get; init; } = 0.25f;

    /// <summary>Whether the origin follows the listener rather than staying where it was put.</summary>
    /// <remarks>
    ///     <b>On is the level-wide ambience</b> — birds everywhere, always around you, and no emitter
    ///     to run out of. Off is a place that makes noise: a rookery, a dripping cave, a fire. The
    ///     first cannot be walked away from and the second must be.
    /// </remarks>
    public bool FollowListener { get; init; } = true;

    /// <summary>Where its random sequence starts.</summary>
    public uint Seed { get; init; }
}

/// <summary>Throws a sound around at intervals, which is most of what an ambience is.</summary>
/// <remarks>
///     <para>
///         <b>The cheapest thing that makes a place feel inhabited.</b> A looping ambience bed says
///         "forest"; birds at irregular intervals from irregular directions say "you are in a forest".
///         The second is a timer, two random numbers and a call to <c>Play</c>, and it is worth more
///         than almost anything else of the same size.
///     </para>
///     <para>
///         <b>It is a caller of <see cref="AudioEvent" /> and not a part of one.</b> Everything that
///         makes the individual spawn good — which of the five bird calls, its pitch variation, how
///         many may overlap, how far each carries — is the event's job and already done. What is left
///         is when and where, which is this and is about forty lines.
///     </para>
///     <para>
///         <b>Nothing is allocated by a tick</b>, and a tick that does not spawn is a subtraction and
///         a comparison. A level with fifty scatterers costs fifty of those a frame.
///     </para>
/// </remarks>
public sealed class AudioScatterer {
    readonly AudioEngine engine;
    readonly AudioEvent sound;
    Xorshift32 random;
    float countdown;

    /// <summary>Where it scatters around, when it is not following the listener.</summary>
    public Vector3 Origin { get; set; }

    /// <summary>How often, how far and how wide.</summary>
    public AudioScattererSettings Settings { get; }

    /// <summary>Whether it is throwing anything.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>How many it has thrown since it started.</summary>
    public int SpawnCount { get; private set; }

    /// <summary>How long until the next one, in seconds.</summary>
    public float NextSpawnSeconds => countdown;

    /// <summary>A scatterer that throws an event about.</summary>
    /// <param name="engine">The engine, for where the listener is.</param>
    /// <param name="sound">What to throw. Its own instance limit is what stops a slow frame flooding the mix.</param>
    /// <param name="settings">How often, how far and how wide.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public AudioScatterer(AudioEngine engine, AudioEvent sound, AudioScattererSettings settings) {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(sound);
        ArgumentNullException.ThrowIfNull(settings);

        this.engine = engine;
        this.sound = sound;
        Settings = settings;
        random = new(settings.Seed);
    }

    /// <summary>Starts it, with the first spawn a random interval away.</summary>
    /// <remarks>
    ///     <b>Not immediately.</b> Ten scatterers started on the same frame — which is what loading a
    ///     level is — would otherwise all fire on it, and a forest that opens with a chord of every
    ///     bird at once is worse than one that opens quietly.
    /// </remarks>
    public void Start() {
        IsRunning = true;
        countdown = NextInterval();
    }

    /// <summary>Stops it. Anything already playing is left to finish.</summary>
    public void Stop() => IsRunning = false;

    /// <summary>Stops it and stops everything it started.</summary>
    public void StopAll() {
        IsRunning = false;
        sound.StopAll();
    }

    /// <summary>Ticks the timer and spawns if it has run out.</summary>
    /// <param name="deltaSeconds">How much game time has passed.</param>
    /// <returns>The handle of what was spawned, or <see cref="VoiceHandle.None" /> if nothing was.</returns>
    /// <remarks>
    ///     <b>At most one spawn a call, deliberately.</b> A frame that took a second — a level load, a
    ///     breakpoint — would otherwise release everything that "should" have happened during it, all
    ///     at the same instant. Losing them is right: they were meant to be spread over a second that
    ///     the player did not experience.
    /// </remarks>
    public VoiceHandle Update(float deltaSeconds) {
        if (!IsRunning) {
            return VoiceHandle.None;
        }

        countdown -= deltaSeconds;

        if (countdown > 0f) {
            return VoiceHandle.None;
        }

        countdown = NextInterval();
        var handle = sound.Play(Scatter());

        if (handle.IsValid) {
            SpawnCount++;
        }

        return handle;
    }

    /// <summary>Throws one now, wherever the next one would have gone.</summary>
    /// <returns>The handle, or <see cref="VoiceHandle.None" /> if the event refused it.</returns>
    /// <remarks>For a gust of wind or a thunderclap — something gameplay decides the timing of.</remarks>
    public VoiceHandle SpawnNow() {
        var handle = sound.Play(Scatter());

        if (handle.IsValid) {
            SpawnCount++;
        }

        return handle;
    }

    /// <summary>Picks a point to throw the next one at.</summary>
    /// <returns>The position.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>Uniform in the shell, not uniform in the radius.</b> Drawing the distance evenly
    ///         between the minimum and the maximum puts far too many spawns near the middle, because
    ///         the area at a radius grows with the radius — so the cube root of a uniform draw is what
    ///         actually scatters things evenly through the volume, and the difference is audible as
    ///         "everything is happening right next to me".
    ///     </para>
    ///     <para>
    ///         The vertical is scaled rather than drawn separately, so the horizontal direction stays
    ///         uniform however flat the ring is asked to be.
    ///     </para>
    /// </remarks>
    public Vector3 Scatter() {
        var minimum = MathF.Max(Settings.MinimumDistance, 0f);
        var maximum = MathF.Max(Settings.MaximumDistance, minimum);

        var low = minimum * minimum * minimum;
        var high = maximum * maximum * maximum;
        var distance = MathF.Cbrt(low + ((high - low) * random.NextUnit()));

        var angle = random.NextUnit() * 2f * MathF.PI;
        var height = random.NextBipolar() * Math.Clamp(Settings.VerticalSpread, 0f, 1f);
        var flat = MathF.Sqrt(MathF.Max(1f - (height * height), 0f));

        var direction = new Vector3(
            MathF.Cos(angle) * flat,
            height,
            MathF.Sin(angle) * flat
        );

        var origin = Settings.FollowListener ? engine.Listener.Position : Origin;
        return origin + (direction * distance);
    }

    float NextInterval() {
        var shortest = MathF.Max(Settings.MinimumInterval, 0f);
        var longest = MathF.Max(Settings.MaximumInterval, shortest);
        return shortest + ((longest - shortest) * random.NextUnit());
    }
}
