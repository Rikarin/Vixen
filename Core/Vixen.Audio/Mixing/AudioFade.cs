// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Effects;
using Vixen.Core.Mathematics;

namespace Vixen.Audio.Mixing;

/// <summary>How a gain travels from one value to another.</summary>
public enum AudioFadeCurve {
    /// <summary>Straight down the amplitude.</summary>
    /// <remarks>
    ///     Famously disappointing as a fade-out: loudness is roughly logarithmic, so half the
    ///     amplitude is nowhere near half as loud, and a linear fade sounds like nothing happening
    ///     followed by the sound falling off a cliff at the end. It is the right curve for a
    ///     cross-fade between two takes of the same material, where the sum is what matters.
    /// </remarks>
    Linear,

    /// <summary>Straight down the decibels, which is what a hand on a fader does.</summary>
    /// <remarks>The default, because it is the one that sounds like a fade.</remarks>
    Decibel
}

/// <summary>Where a gain has got to, part way through a fade.</summary>
public static class AudioFade {
    /// <summary>How far below unity a decibel fade starts and ends. −80 dB is inaudible.</summary>
    /// <remarks>
    ///     A floor is needed because silence is −∞ dB and interpolating to it never arrives. −80
    ///     rather than the −120 <see cref="Decibels.FromLinear" /> floors at: the last forty decibels
    ///     of a fade are below anything a player can hear over their own room, and spending a third
    ///     of the fade's duration there makes it feel longer than it was asked to be.
    /// </remarks>
    public const float FloorDb = -80f;

    /// <summary>Interpolates a gain.</summary>
    /// <param name="from">Where it started.</param>
    /// <param name="to">Where it is going.</param>
    /// <param name="t">How far through, from 0 to 1.</param>
    /// <param name="curve">Which way.</param>
    /// <returns>The gain now.</returns>
    /// <remarks>
    ///     Lands exactly on <paramref name="to" /> at <c>t = 1</c> rather than on whatever the curve
    ///     evaluates to, so a fade to silence reaches silence instead of the floor.
    /// </remarks>
    public static float Evaluate(float from, float to, float t, AudioFadeCurve curve = AudioFadeCurve.Decibel) {
        if (t >= 1f) {
            return to;
        }

        if (t <= 0f) {
            return from;
        }

        if (curve is AudioFadeCurve.Linear) {
            return MathUtil.Lerp(from, to, t);
        }

        var start = MathF.Max(Decibels.FromLinear(from), FloorDb);
        var end = MathF.Max(Decibels.FromLinear(to), FloorDb);
        return Decibels.ToLinear(MathUtil.Lerp(start, end, t));
    }
}

/// <summary>One fade in progress.</summary>
/// <remarks>
///     <para>
///         <b>Stepped on the game thread, not in the mixer.</b> A fade is a gain changing over
///         hundreds of milliseconds, and the game thread already visits every voice once a frame in
///         <c>AudioEngine.Update</c>. Sixty steps a second, each of them smoothed across an audio
///         block by the ramp the voice already applies, is indistinguishable from a per-sample
///         envelope — and it keeps the audio thread free of anything that needs a clock.
///     </para>
///     <para>
///         Which is also why the duration is in seconds of <em>game</em> time: a fade under a paused
///         game stops, which is what a pause menu wants, and a fade under slow motion slows down,
///         which is what slow motion is for.
///     </para>
/// </remarks>
struct FadeState {
    public bool Active;
    public int Generation;
    public float From;
    public float To;
    public float Elapsed;
    public float Duration;
    public AudioFadeCurve Curve;
    public bool StopAtEnd;

    /// <summary>Advances the fade and says where the gain should now be.</summary>
    /// <param name="deltaSeconds">How much time passed.</param>
    /// <param name="gain">The gain now.</param>
    /// <returns>Whether the fade has finished.</returns>
    public bool Step(float deltaSeconds, out float gain) {
        Elapsed += deltaSeconds;
        var t = Duration <= 0f ? 1f : Math.Clamp(Elapsed / Duration, 0f, 1f);
        gain = AudioFade.Evaluate(From, To, t, Curve);
        return t >= 1f;
    }
}
