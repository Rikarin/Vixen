// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;

namespace Vixen.Water;

/// <summary>How many waves a sea state is summed from.</summary>
/// <remarks>
///     <para>
///         <b>Quantised, and it is the permutation axis</b> —
///         [35 § D7](../../docs/plan/35-water.md#d7-waves-are-a-spectrum-summed-as-gerstner-and-the-fft-is-deferred-with-arithmetic).
///         A sea state gaining one wave must not compile a shader, so the count a document types
///         rounds up to one of three and the vertex stage has three variants rather than thirty.
///         [31 § D6](../../docs/plan/31-terrain-grass-and-trees.md)'s trick, unchanged.
///     </para>
///     <para>
///         ⚠ <b>It is also what keeps the two evaluators the same shape.</b> A dynamic loop on the
///         CPU and an unrolled one on the GPU is how two implementations of one function start to
///         differ in the last bits — see
///         [§ D2](../../docs/plan/35-water.md#d2-one-evaluator-two-hosts-and-the-seam-is-a-test).
///     </para>
/// </remarks>
public enum WaterWaveCount {
    /// <summary>Eight. A lake, a river, and anything a low-end target draws.</summary>
    Eight = 8,

    /// <summary>Sixteen. The default, and Unreal's.</summary>
    Sixteen = 16,

    /// <summary>Thirty-two. Open sea, where the long swell and the chop have to coexist.</summary>
    ThirtyTwo = 32
}

/// <summary>One Gerstner wave, as the evaluator consumes it.</summary>
/// <param name="Direction">Which way it travels, as a unit vector on the ground plane.</param>
/// <param name="Wavelength">Crest to crest, in metres.</param>
/// <param name="Amplitude">Half the crest-to-trough height, in metres.</param>
/// <param name="Steepness">
///     How far the horizontal displacement goes, 0…1 of the theoretical maximum. At 1 the crest is a
///     cusp and beyond it the surface folds through itself.
/// </param>
/// <param name="Phase">Where in its cycle it is at time zero, in radians.</param>
/// <remarks>
///     <para>
///         The runtime form. What an author edits is <see cref="WaterWaveSpectrum" />, which
///         <em>generates</em> these — that split is Unreal's simple-versus-spectrum generator pair
///         collapsed into one thing, because a simple generator is a spectrum with a flat
///         distribution and shipping both is shipping two mental models.
///     </para>
///     <para>
///         <b>Frequency and speed are derived rather than stored.</b> A deep-water gravity wave's
///         speed follows from its wavelength — <c>ω² = gk</c> — and storing a third number that can
///         disagree with the two it came from is the mistake
///         [§ D3](../../docs/plan/35-water.md#d3-the-water-info-texture-is-the-interchange-and-it-is-a-zone-render)
///         refuses for depth.
///     </para>
/// </remarks>
public readonly record struct GerstnerWave(
    Vector2 Direction,
    float Wavelength,
    float Amplitude,
    float Steepness,
    float Phase
) {
    /// <summary>Standard gravity, in metres per second squared.</summary>
    /// <remarks>
    ///     ⚠ <b>Not the physics world's gravity, and deliberately not configurable.</b> A sea state
    ///     is authored by eye against how water looks on Earth; a project that halves its gravity for
    ///     a moon level wants the ground to fall differently, not the ocean to become syrup — and a
    ///     wave model whose speed depends on a scene setting is one the seam test cannot pin, because
    ///     the server and the client would have to agree about a number neither of them sends.
    /// </remarks>
    public const float Gravity = 9.80665f;

    /// <summary>The angular wavenumber, <c>2π ⁄ λ</c>.</summary>
    public float WaveNumber => Wavelength > 0f ? WaterMath.Tau / Wavelength : 0f;

    /// <summary>The angular frequency, <c>√(g k)</c> — the deep-water dispersion relation.</summary>
    public float AngularFrequency => MathF.Sqrt(Gravity * WaveNumber);
}

/// <summary>A sea state, as an author edits it.</summary>
/// <remarks>
///     <para>
///         <b>A spectrum and not a list</b>
///         ([35 § D7](../../docs/plan/35-water.md#d7-waves-are-a-spectrum-summed-as-gerstner-and-the-fft-is-deferred-with-arithmetic)).
///         Wind, a spread, a wavelength range and a seed are the things somebody describing a sea has
///         words for; sixteen amplitudes and phases are not.
///     </para>
///     <para>
///         ⚠ <b>Deterministic from <see cref="Seed" /> on every host</b>, which is a stated exit
///         criterion rather than a nicety: a client and a server that disagree about the sea disagree
///         about where a boat is. Every draw goes through <see cref="WaterMath.Hash" />, which is
///         integer arithmetic and therefore the same everywhere there is a 32-bit word.
///     </para>
///     <para>
///         <b>Why Gerstner and not an FFT.</b> A sum of sixteen waves is a <em>closed-form function
///         of position and time</em>, so a server can answer "where was the surface six ticks ago"
///         without having simulated the intervening frames — which is what makes a rolled-back
///         buoyancy force answerable at all. An FFT needs a per-frame dispatch chain and a CPU path
///         that either reads back a texture or runs a second transform, and neither can answer that
///         question. When the FFT lands it is a second model behind the same evaluator, and the seam
///         test is what makes adding it safe.
///     </para>
/// </remarks>
[DataContract("WaterWaveSpectrum")]
public readonly record struct WaterWaveSpectrum {
    /// <summary>Which way the wind blows, in radians about the vertical axis.</summary>
    public float WindDirection { get; init; }

    /// <summary>How hard, in metres per second. Drives the amplitude and the wavelength range.</summary>
    public float WindSpeed { get; init; }

    /// <summary>
    ///     How far off the wind a wave may travel, in radians. Zero is a perfectly ordered swell.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A spread of zero is what a bath looks like, not what the sea does.</b> Every wave
    ///     travelling in exactly one direction makes a surface with no crossing crests, which reads
    ///     as corrugated iron from any height. Half a radian is a reasonable open sea.
    /// </remarks>
    public float DirectionalSpread { get; init; }

    /// <summary>The shortest wave in the sum, in metres.</summary>
    public float MinimumWavelength { get; init; }

    /// <summary>The longest, in metres.</summary>
    public float MaximumWavelength { get; init; }

    /// <summary>
    ///     How amplitude falls off toward the short end, as an exponent on the normalised wavelength.
    /// </summary>
    /// <remarks>
    ///     One is linear in the wavelength, which is roughly what a fully developed sea does. Above
    ///     one the long swell dominates and the chop nearly disappears; below one the surface is all
    ///     chop, which is what a squall looks like.
    /// </remarks>
    public float WavelengthFalloff { get; init; }

    /// <summary>A multiplier on every amplitude the spectrum derives.</summary>
    public float AmplitudeScale { get; init; }

    /// <summary>How far the horizontal displacement goes, 0…1.</summary>
    /// <remarks>
    ///     ⚠ <b>Above one the surface folds through itself</b> and the normal turns inside out, which
    ///     reads as a black stripe along every crest. <see cref="Generate" /> clamps rather than
    ///     refusing, because a sea state pushed too far should look wrong rather than fail to load.
    /// </remarks>
    public float Steepness { get; init; }

    /// <summary>Which sea, of all the seas with these numbers.</summary>
    public uint Seed { get; init; }

    /// <summary>How many waves it is summed from.</summary>
    public WaterWaveCount Count { get; init; }

    /// <summary>An open sea in a fresh breeze: sixteen waves, half a metre of swell.</summary>
    public static WaterWaveSpectrum Default =>
        new() {
            WindDirection = 0f,
            WindSpeed = 8f,
            DirectionalSpread = 0.6f,
            MinimumWavelength = 4f,
            MaximumWavelength = 60f,
            WavelengthFalloff = 1f,
            AmplitudeScale = 1f,
            Steepness = 0.5f,
            Seed = 1u,
            Count = WaterWaveCount.Sixteen
        };

    /// <summary>A lake: eight short waves, almost no swell, and no wind direction worth having.</summary>
    public static WaterWaveSpectrum Calm =>
        Default with {
            WindSpeed = 2f,
            DirectionalSpread = 1.2f,
            MinimumWavelength = 1.5f,
            MaximumWavelength = 8f,
            AmplitudeScale = 0.25f,
            Steepness = 0.25f,
            Count = WaterWaveCount.Eight
        };

    /// <summary>Why this spectrum cannot be summed, or <see langword="null" /> if it can.</summary>
    public string? Validate() {
        if (!(MinimumWavelength > 0f)) {
            return "A spectrum's minimum wavelength has to be above zero; a wave of no length has no "
                + "frequency and its dispersion relation divides by nothing.";
        }

        if (MaximumWavelength < MinimumWavelength) {
            return $"A spectrum from {MinimumWavelength} m to {MaximumWavelength} m runs backwards.";
        }

        if (Count is not (WaterWaveCount.Eight or WaterWaveCount.Sixteen or WaterWaveCount.ThirtyTwo)) {
            return $"{(int)Count} is not one of the three quantised wave counts. The count is a shader "
                + "permutation, so it is 8, 16 or 32 and a document that wants 20 gets 32.";
        }

        return null;
    }

    /// <summary>Sums the spectrum into the waves the evaluator reads.</summary>
    /// <param name="into">Where they go. Its length says how many are wanted.</param>
    /// <returns>How many were written.</returns>
    /// <exception cref="ArgumentException">The spectrum is not one that can be summed.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>Allocation-free, over a span the caller owns.</b> A sea state is regenerated
    ///         whenever an author drags a slider, and a generator that allocated would allocate once
    ///         per frame of that drag.
    ///     </para>
    ///     <para>
    ///         <b>Wave <c>i</c> depends only on <c>i</c>.</b> The wavelengths are laid out
    ///         geometrically from the minimum to the maximum so that the sum covers the range evenly
    ///         in octaves rather than in metres — a linear spread puts fourteen of sixteen waves in
    ///         the long half, and the short chop that makes water read as water disappears.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The amplitude is derived from the wavelength and the wind, not drawn.</b> A random
    ///         amplitude per wave produces a sea whose energy is in the wrong place — it is the one
    ///         quantity an observer can name ("that swell is a metre") and a spectrum exists to make
    ///         it a consequence of the wind rather than of a slider per wave.
    ///     </para>
    /// </remarks>
    public int Generate(Span<GerstnerWave> into) {
        if (Validate() is { } why) {
            throw new ArgumentException(why, nameof(into));
        }

        var wanted = Math.Min(into.Length, (int)Count);
        var steepness = WaterMath.Saturate(Steepness);

        // The steepness an author states is shared out, because it is a property of the *surface* and
        // not of each wave: sixteen waves each displacing horizontally by the full amount is a
        // surface that folds even though every one of them is inside its own limit. Unreal divides
        // the same way and for the same reason.
        var perWave = wanted > 0 ? steepness / wanted : 0f;

        for (var index = 0; index < wanted; index++) {
            var hash = WaterMath.Hash(Seed, index);

            // Geometric across the range, jittered inside its own octave so that two waves never
            // share a wavelength exactly — which would make them one wave of twice the amplitude and
            // leave a hole in the spectrum.
            var t = wanted > 1 ? index / (float)(wanted - 1) : 0f;
            var jitter = (WaterMath.Unit(hash, 1) - 0.5f) / MathF.Max(wanted - 1, 1);
            var span = MathF.Log(MaximumWavelength / MinimumWavelength);
            var wavelength = MinimumWavelength * MathF.Exp(span * WaterMath.Saturate(t + jitter));

            // Fetch-limited energy: longer waves carry more of it, and the wind sets the scale. The
            // 1/500 is a unit-carrying constant chosen so that a fresh breeze over a sixty-metre
            // swell gives about half a metre of amplitude, which is what one looks like.
            var normalised = wavelength / MaximumWavelength;
            var amplitude = AmplitudeScale
                * WindSpeed
                * WindSpeed
                * MathF.Pow(normalised, MathF.Max(WavelengthFalloff, 0f))
                * (1f / 500f);

            // Directions spread about the wind, symmetric and deterministic.
            var offset = (WaterMath.Unit(hash, 2) - 0.5f) * 2f * DirectionalSpread;
            var angle = WindDirection + offset;

            WaterMath.SinCos(angle, out var sin, out var cos);

            into[index] = new(
                new(cos, sin),
                wavelength,
                amplitude,
                perWave,
                WaterMath.Unit(hash, 3) * WaterMath.Tau
            );
        }

        return wanted;
    }

    /// <summary>The tallest the surface can be above its rest height, in metres.</summary>
    /// <remarks>
    ///     <para>
    ///         The sum of every amplitude — the case where every crest lines up, which is rare and is
    ///         not impossible. It is what the node error metric, the far-mesh cut and the collision
    ///         bounds are all sized from, and it is what the zone panel shows an author who has just
    ///         raised the wind.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Not a measured maximum.</b> Sampling the sum over a grid and taking the largest
    ///         would give a smaller, prettier number that is wrong once a frame — and the frame it is
    ///         wrong on is the one where the bounding box does not contain the surface, which is a
    ///         wave culled away in front of the camera.
    ///     </para>
    /// </remarks>
    /// <param name="waves">The generated waves.</param>
    /// <returns>The bound, in metres.</returns>
    public static float MaximumAmplitude(ReadOnlySpan<GerstnerWave> waves) {
        var total = 0f;

        foreach (var wave in waves) {
            total += MathF.Abs(wave.Amplitude);
        }

        return total;
    }
}
