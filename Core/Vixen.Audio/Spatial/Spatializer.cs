// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Audio.Spatial;

/// <summary>What placing a sound in the world worked out to.</summary>
/// <param name="Distance">How far the source is from the listener.</param>
/// <param name="Attenuation">The distance gain alone, before the cone and the listener's own gain.</param>
/// <param name="ConeGain">The directional gain alone.</param>
/// <param name="DopplerRatio">
///     What to multiply the playback rate by. Above one is approaching, below one is receding.
/// </param>
/// <param name="LowPassHz">
///     Where distance has put the air-absorption low-pass, in hertz, or <c>0</c> for no filtering at
///     all — which is both the default and the common case, and is a bypass rather than a filter set
///     wide open.
/// </param>
/// <param name="Azimuth">
///     Which way round the listener the sound is, in degrees: 0 straight ahead, +90 to the right,
///     ±180 behind.
/// </param>
/// <param name="Elevation">
///     How far above or below the listener the sound is, in degrees: +90 overhead, −90 underfoot.
/// </param>
/// <param name="SourceSpeed">How fast the source is moving, in units a second, regardless of direction.</param>
/// <remarks>
///     <para>
///         The parts are returned separately rather than pre-multiplied because the audio debug
///         overlay <c>docs/plan/13</c> asks for shows exactly this: a source that is inaudible is
///         either too far away or pointing the wrong way, and a single combined number cannot say
///         which.
///     </para>
///     <para>
///         <b>The last three are here for the built-in parameters</b>, which are read from the game
///         thread while the audio thread writes them. That is the same documented race as
///         <c>Voice.Audibility</c>: every term is a float, so the worst case is a curve evaluated
///         against last block's geometry, which at sixty frames a second is exactly as good.
///     </para>
/// </remarks>
public readonly record struct SpatialResult(
    float Distance,
    float Attenuation,
    float ConeGain,
    float DopplerRatio,
    float LowPassHz = 0f,
    float Azimuth = 0f,
    float Elevation = 0f,
    float SourceSpeed = 0f
);

/// <summary>Turns a listener and a source into per-speaker gains and a pitch ratio.</summary>
/// <remarks>
///     <para>
///         <b>Panning and not HRTF.</b> A head-related transfer function is a pair of convolutions
///         per voice with a filter set that has to be shipped, and it is only correct on headphones —
///         over speakers it is worse than panning. This is amplitude panning, which is what OpenAL's
///         software mixer, WebAudio's <c>equalpower</c> mode and every console mixer do by default.
///         An HRTF panner is a later addition behind the same call, and the interesting part of the
///         work — distance, cones, doppler, the listener basis — does not change when it arrives.
///     </para>
///     <para>
///         <b>Constant power, not constant amplitude.</b> A sound panned to the centre is at 0.707
///         in both speakers rather than 0.5, because two speakers at 0.707 carry the same
///         <em>power</em> as one at 1. Linear panning makes anything crossing the centre audibly dip.
///     </para>
///     <para>
///         Pure function, no state, no allocation, called once per voice per block from the audio
///         thread — which is also what makes every one of these behaviours a unit test over numbers.
///     </para>
/// </remarks>
public static class Spatializer {
    const float Centre = 0.70710678f;

    /// <summary>Works out how a source should sound from where the listener is.</summary>
    /// <param name="listener">Where the ears are.</param>
    /// <param name="source">Where the sound is and how it behaves.</param>
    /// <param name="outputChannels">How many speakers to spread it across.</param>
    /// <param name="gains">
    ///     Filled with one gain per output channel, listener gain included. Must be at least
    ///     <paramref name="outputChannels" /> long.
    /// </param>
    /// <returns>The parts that went into it.</returns>
    /// <remarks>
    ///     Beyond two channels the sound is placed in the first two and the rest are silent. A
    ///     surround panner is owed; silence in the surrounds is wrong in a way somebody will notice
    ///     and complain about, where a quiet, wrong-sounding smear across five speakers is wrong in a
    ///     way they will not be able to describe.
    /// </remarks>
    public static SpatialResult Evaluate(
        in AudioListener listener,
        in SpatialSettings source,
        int outputChannels,
        Span<float> gains
    ) {
        gains[..outputChannels].Clear();

        var toSource = source.Position - listener.Position;
        var distance = toSource.Length();
        var attenuation = Attenuate(source, distance);
        var cone = ConeGain(source, toSource, distance);
        var doppler = Doppler(listener, source, toSource, distance);
        var gain = attenuation * cone * listener.Gain;

        var cutoff = Absorption(source, distance);

        var (azimuth, elevation) = Bearing(listener, toSource, distance);
        var speed = source.Velocity.Length();

        if (outputChannels <= 1) {
            gains[0] = gain;
            return new SpatialResult(distance, attenuation, cone, doppler, cutoff, azimuth, elevation, speed);
        }

        // Inside the reference distance the direction stops meaning anything — the listener is
        // effectively inside the sound — so the pan is dissolved rather than allowed to swing
        // through 180° as they walk past it.
        var proximity = source.MinDistance > 0f ? Math.Clamp(distance / source.MinDistance, 0f, 1f) : 1f;
        var spread = Math.Clamp(Math.Max(source.Spread, 1f - proximity), 0f, 1f);
        var pan = distance > MathUtil.ZeroTolerance ? Pan(listener, toSource / distance) : 0f;

        var angle = (pan + 1f) * (MathF.PI * 0.25f);
        var left = MathUtil.Lerp(MathF.Cos(angle), Centre, spread);
        var right = MathUtil.Lerp(MathF.Sin(angle), Centre, spread);

        gains[0] = left * gain;
        gains[1] = right * gain;

        return new SpatialResult(distance, attenuation, cone, doppler, cutoff, azimuth, elevation, speed);
    }

    /// <summary>Where a sound is, as two angles in the listener's own frame.</summary>
    /// <param name="listener">Where the ears are.</param>
    /// <param name="toSource">From the listener to the sound, unnormalised.</param>
    /// <param name="distance">Its length, already computed.</param>
    /// <returns>The azimuth and elevation, in degrees.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>Degrees, and signed.</b> A parameter curve is drawn by a human against an axis with
    ///         numbers on it, and −180..180 is the axis anybody would draw — 0 in the middle for
    ///         straight ahead, the edges for behind. Radians would be correct and unreadable.
    ///     </para>
    ///     <para>
    ///         Azimuth is taken in the horizontal plane of the listener's own basis, so a listener
    ///         lying down still has a left and a right. Elevation is out of that plane, which is why
    ///         the two are computed together rather than as two dot products.
    ///     </para>
    /// </remarks>
    static (float Azimuth, float Elevation) Bearing(in AudioListener listener, Vector3 toSource, float distance) {
        if (distance <= MathUtil.ZeroTolerance) {
            // Inside the listener's own head. There is no direction, and any answer would swing
            // wildly as they moved.
            return (0f, 0f);
        }

        var forward = SafeNormalize(listener.Forward, Vector3.Forward);
        var up = SafeNormalize(listener.Up, Vector3.Up);
        var right = SafeNormalize(Vector3.Cross(forward, up), Vector3.Right);
        var direction = toSource / distance;

        var ahead = Vector3.Dot(direction, forward);
        var side = Vector3.Dot(direction, right);
        var above = Math.Clamp(Vector3.Dot(direction, up), -1f, 1f);

        return (
            MathF.Atan2(side, ahead) * (180f / MathF.PI),
            MathF.Asin(above) * (180f / MathF.PI)
        );
    }

    /// <summary>Places a sound for several listeners at once.</summary>
    /// <param name="listeners">Everywhere the game is listening from.</param>
    /// <param name="source">Where the sound is, and how it behaves there.</param>
    /// <param name="outputChannels">How many speakers to spread it across.</param>
    /// <param name="gains">Where the speaker gains go. At least <paramref name="outputChannels" /> long.</param>
    /// <param name="scratch">Working room for one listener's answer. The same length.</param>
    /// <returns>What the listener who hears it best hears.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>The direction blends and the level does not.</b> Speaker gains are summed across
    ///         listeners in proportion to how well each hears the sound, and the sum is then scaled so
    ///         its total is the loudest listener's alone.
    ///     </para>
    ///     <para>
    ///         <b>Summing outright was rejected</b>: two players standing together beside a generator
    ///         would hear it twice as loud as one player standing there, and every sound in the level
    ///         would get louder as the party gathered.
    ///     </para>
    ///     <para>
    ///         <b>And so was taking the nearest listener outright.</b> It has the level right, but the
    ///         pan flips the instant the sound crosses the midpoint between two players — which is
    ///         audible, and worse than being slightly wrong on either side of it. Blending the
    ///         direction and normalising the level is right at both ends and unobjectionable between.
    ///     </para>
    ///     <para>
    ///         <b>What it does not fix.</b> Close to the midpoint between two distant listeners the
    ///         blend is dominated by which of them is nearer, so a sound crossing that line can appear
    ///         to move the wrong way — it becomes more of the near listener's sound, and they hear it
    ///         off to their side. That is inherent to representing two places with two speakers rather
    ///         than a flaw in the blend, and it is continuous, which the alternative was not.
    ///     </para>
    ///     <para>
    ///         Distance, doppler and the absorption cutoff come from the best listener rather than
    ///         being blended: they are properties of one path from the sound to one pair of ears, and
    ///         the average of two doppler shifts is a pitch neither listener would hear.
    ///     </para>
    /// </remarks>
    public static SpatialResult Evaluate(
        in AudioListenerSet listeners,
        in SpatialSettings source,
        int outputChannels,
        Span<float> gains,
        Span<float> scratch
    ) {
        if (listeners.Count <= 1) {
            var only = listeners.Count == 1 ? listeners.Get(0) : AudioListener.Default;
            var single = Evaluate(only, source, outputChannels, gains);
            var weight = listeners.Count == 1 ? listeners.WeightOf(0) : 1f;

            if (weight != 1f) {
                for (var channel = 0; channel < outputChannels; channel++) {
                    gains[channel] *= weight;
                }
            }

            return single;
        }

        gains[..outputChannels].Clear();

        var best = default(SpatialResult);
        var loudest = -1f;
        var total = 0f;

        for (var i = 0; i < listeners.Count; i++) {
            var listener = listeners.Get(i);
            var weight = listeners.WeightOf(i);
            var result = Evaluate(listener, source, outputChannels, scratch);
            var contribution = result.Attenuation * result.ConeGain * listener.Gain * weight;

            if (contribution > loudest) {
                loudest = contribution;
                best = result;
            }

            if (contribution <= 0f) {
                continue;
            }

            total += contribution;

            for (var channel = 0; channel < outputChannels; channel++) {
                gains[channel] += scratch[channel] * weight;
            }
        }

        if (total > 0f && loudest > 0f) {
            // The blended direction at the best listener's level. Without this the gains are a sum
            // and a sound equidistant from four players is four times too loud.
            var normalise = loudest / total;

            for (var channel = 0; channel < outputChannels; channel++) {
                gains[channel] *= normalise;
            }
        }

        return best;
    }

    /// <summary>Where distance has put the low-pass, in hertz, or zero for none.</summary>
    /// <remarks>
    ///     <para>
    ///         Interpolated logarithmically, from 20 kHz at the reference distance down to
    ///         <see cref="SpatialSettings.AirAbsorptionCutoff" /> at the maximum. Linearly would spend
    ///         almost the whole journey in the top octave, where nothing is, and then collapse through
    ///         everything audible in the last few metres — pitch is logarithmic and a filter sweep has
    ///         to be too, or it does not sound like moving away, it sounds like a switch.
    ///     </para>
    ///     <para>
    ///         Real air absorption also depends on humidity and temperature and is frequency-dependent
    ///         in a way one biquad cannot express. This is the cheap approximation every game uses; the
    ///         accurate model belongs in an offline tool, not in a voice.
    ///     </para>
    /// </remarks>
    static float Absorption(in SpatialSettings source, float distance) {
        var strength = Math.Clamp(source.AirAbsorption, 0f, 1f);

        if (strength <= 0f) {
            return 0f;
        }

        var min = Math.Max(source.MinDistance, MathUtil.ZeroTolerance);
        var max = Math.Max(source.MaxDistance, min + MathUtil.ZeroTolerance);
        var travelled = Math.Clamp((distance - min) / (max - min), 0f, 1f) * strength;

        if (travelled <= 0f) {
            return 0f;
        }

        var target = Math.Clamp(source.AirAbsorptionCutoff, 20f, 20_000f);
        return MathF.Exp(MathUtil.Lerp(MathF.Log(20_000f), MathF.Log(target), travelled));
    }

    /// <summary>Where a direction sits from left to right, as seen by a listener.</summary>
    /// <param name="listener">Whose basis to use.</param>
    /// <param name="direction">A unit vector from the listener to the source.</param>
    /// <returns>−1 hard left, 0 straight ahead or straight behind, +1 hard right.</returns>
    /// <remarks>
    ///     The listener's right is <c>cross(forward, up)</c>, which is <c>+X</c> for the engine's
    ///     right-handed, Y-up, −Z-forward convention — see
    ///     <c>Core/Vixen.Core.Mathematics/Conventions.md</c>, which is the file this has to agree
    ///     with and the reason a sign flip here is a settled argument rather than an open one.
    ///     <para>
    ///         A source directly overhead pans to the centre, because the component of a unit vector
    ///         along the listener's right shrinks as it tilts away from the horizontal. That falls
    ///         out of the dot product rather than being a special case.
    ///     </para>
    /// </remarks>
    public static float Pan(in AudioListener listener, Vector3 direction) {
        var forward = SafeNormalize(listener.Forward, Vector3.Forward);
        var up = SafeNormalize(listener.Up, Vector3.Up);
        var right = SafeNormalize(Vector3.Cross(forward, up), Vector3.Right);

        return Math.Clamp(Vector3.Dot(direction, right), -1f, 1f);
    }

    static float Attenuate(in SpatialSettings source, float distance) {
        var min = Math.Max(source.MinDistance, MathUtil.ZeroTolerance);
        var max = Math.Max(source.MaxDistance, min);
        var rolloff = Math.Max(source.RolloffFactor, 0f);
        var clamped = Math.Clamp(distance, min, max);

        return source.Attenuation switch {
            AttenuationModel.None => 1f,
            AttenuationModel.Linear => Math.Clamp(1f - (rolloff * (clamped - min) / (max - min)), 0f, 1f),
            AttenuationModel.Exponential => MathF.Pow(clamped / min, -rolloff),
            _ => min / (min + (rolloff * (clamped - min)))
        };
    }

    static float ConeGain(in SpatialSettings source, Vector3 toSource, float distance) {
        var inner = Math.Clamp(source.ConeInnerAngle, 0f, 360f);
        var outer = Math.Clamp(source.ConeOuterAngle, inner, 360f);

        if (inner >= 360f || distance <= MathUtil.ZeroTolerance) {
            return 1f;
        }

        var direction = SafeNormalize(source.ConeDirection, Vector3.Forward);
        var toListener = -toSource / distance;
        var angle = MathUtil.RadiansToDegrees(MathF.Acos(Math.Clamp(Vector3.Dot(direction, toListener), -1f, 1f)));

        // The authored angles are the full width of the cone, so the angle from its axis is compared
        // against half of each — which is the convention OpenAL, FMOD and Wwise all use, and getting
        // it wrong makes every cone in a project twice as wide as it was drawn.
        var half = inner * 0.5f;
        var halfOuter = outer * 0.5f;

        if (angle <= half) {
            return 1f;
        }

        if (angle >= halfOuter) {
            return source.ConeOuterGain;
        }

        return MathUtil.Lerp(1f, source.ConeOuterGain, (angle - half) / (halfOuter - half));
    }

    static float Doppler(
        in AudioListener listener,
        in SpatialSettings source,
        Vector3 toSource,
        float distance
    ) {
        var factor = Math.Max(source.DopplerFactor, 0f);

        if (factor <= 0f || distance <= MathUtil.ZeroTolerance) {
            return 1f;
        }

        var speed = Math.Max(source.SpeedOfSound, MathUtil.ZeroTolerance);
        var toListener = -toSource / distance;

        // OpenAL's formula, and the same one the WebAudio specification writes out: both speeds are
        // measured along the source-to-listener line, positive meaning "the listener is receding"
        // and "the source is approaching" respectively.
        //
        // Both are clamped below the speed of sound. At it the denominator is zero and past it the
        // ratio changes sign, and what a supersonic source should sound like is not a question a
        // game mixer has to answer.
        var limit = speed / factor * 0.99f;
        var listenerSpeed = Math.Clamp(Vector3.Dot(listener.Velocity, toListener), -limit, limit);
        var sourceSpeed = Math.Clamp(Vector3.Dot(source.Velocity, toListener), -limit, limit);

        return (speed - (factor * listenerSpeed)) / (speed - (factor * sourceSpeed));
    }

    static Vector3 SafeNormalize(Vector3 value, Vector3 fallback) {
        var length = value.Length();
        return length > MathUtil.ZeroTolerance ? value / length : fallback;
    }
}
