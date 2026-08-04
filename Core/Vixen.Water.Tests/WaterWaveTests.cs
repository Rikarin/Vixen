// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using CsCheck;
using Vixen.Water;
using Xunit;

namespace Vixen.Water.Tests;

/// <summary>
///     A sea state, summed from a spectrum — [docs/plan/35 § D7].
/// </summary>
/// <remarks>
///     The load-bearing test here is <see cref="TheSameSeedIsTheSameSeaEverywhere" />. A client and a
///     server that disagree about the sea disagree about where a boat is, and the disagreement is a
///     few centimetres at first — which is exactly the size of thing that gets attributed to network
///     jitter and worked around with a fudge factor.
/// </remarks>
public sealed class WaterWaveTests {
    static GerstnerWave[] Generate(in WaterWaveSpectrum spectrum) {
        var waves = new GerstnerWave[(int)spectrum.Count];
        var written = spectrum.Generate(waves);

        Assert.Equal(waves.Length, written);

        return waves;
    }

    /// <summary>
    ///     ⚠ The same seed is the same sea, bit for bit, wherever it is summed.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         W1's stated exit criterion: a spectrum is deterministic from its seed on three
    ///         operating systems and two architectures. One process cannot check three hosts, so what
    ///         it checks instead is the only thing that makes that claim testable at all — the exact
    ///         bits, written down. The bit-exact CI legs run this on the other two.
    ///     </para>
    ///     <para>
    ///         Every draw goes through <c>WaterMath.Hash</c>, which is integer arithmetic, and every
    ///         trigonometric call goes through <c>WaterMath.SinCos</c>, which is a stated polynomial.
    ///         Neither has a platform-dependent path. <c>System.Random</c> would have: the framework
    ///         declines to promise a seeded sequence is stable even across its own versions.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheSameSeedIsTheSameSeaEverywhere() {
        var waves = Generate(WaterWaveSpectrum.Default);

        Assert.Equal(0xDDBC3901u, Fingerprint(waves));

        // And a different seed is a different sea, or the fingerprint above is testing nothing.
        Assert.NotEqual(
            Fingerprint(waves),
            Fingerprint(Generate(WaterWaveSpectrum.Default with { Seed = 2u }))
        );
    }

    /// <summary>Wave <c>i</c> depends only on <c>i</c>, so a count change moves nothing else.</summary>
    /// <remarks>
    ///     ⚠ What lets the count be a quantised shader permutation without a sea state visibly
    ///     changing when it crosses a boundary: a sixteen-wave sea and the first sixteen of a
    ///     thirty-two-wave one differ only in where they sit in the wavelength range, not in which
    ///     draws they took.
    /// </remarks>
    [Fact]
    public void AWavesIdentityDependsOnlyOnItsIndex() {
        var eight = Generate(WaterWaveSpectrum.Default with { Count = WaterWaveCount.Eight });
        var sixteen = Generate(WaterWaveSpectrum.Default with { Count = WaterWaveCount.Sixteen });

        for (var index = 0; index < eight.Length; index++) {
            // The wavelength is a function of the index *and* the count — the range is shared out —
            // but the direction and phase draws are the index's alone.
            Assert.Equal(eight[index].Phase, sixteen[index].Phase);
        }
    }

    /// <summary>The wavelengths cover the authored range, geometrically.</summary>
    /// <remarks>
    ///     ⚠ Geometric and not linear. A linear spread puts fourteen of sixteen waves in the long
    ///     half of the range, and the short chop that makes water read as water disappears — the
    ///     surface becomes a slow heave with nothing on top of it.
    /// </remarks>
    [Fact]
    public void TheWavelengthsCoverTheRangeInOctaves() {
        var spectrum = WaterWaveSpectrum.Default with { MinimumWavelength = 4f, MaximumWavelength = 64f };
        var waves = Generate(spectrum);

        foreach (var wave in waves) {
            Assert.InRange(wave.Wavelength, 3.9f, 64.1f);
        }

        // Four octaves across sixteen waves: at least a third of them are in the short half, which a
        // linear spread would not manage.
        var shortHalf = waves.Count(wave => wave.Wavelength < 16f);

        Assert.True(shortHalf >= 5, $"only {shortHalf} of {waves.Length} waves are shorter than 16 m");
    }

    /// <summary>The steepness an author states is shared out across the waves, not repeated.</summary>
    /// <remarks>
    ///     ⚠ Sixteen waves each displacing horizontally by the author's full steepness is a surface
    ///     that folds through itself even though every one of them is inside its own limit — and a
    ///     folded surface has an inside-out normal, which reads as a black stripe along every crest.
    /// </remarks>
    [Fact]
    public void TheSteepnessIsSharedOutAcrossTheSum() {
        var waves = Generate(WaterWaveSpectrum.Default with { Steepness = 0.8f });
        var total = waves.Sum(wave => wave.Steepness);

        Assert.Equal(0.8f, total, 1e-4f);
    }

    /// <summary>Directions spread about the wind, and a spread of zero is one direction.</summary>
    [Fact]
    public void DirectionsSpreadAboutTheWind() {
        var ordered = Generate(WaterWaveSpectrum.Default with { WindDirection = 0f, DirectionalSpread = 0f });

        foreach (var wave in ordered) {
            Assert.Equal(1f, wave.Direction.X, 1e-5f);
            Assert.Equal(0f, wave.Direction.Y, 1e-5f);
        }

        var spread = Generate(WaterWaveSpectrum.Default with { WindDirection = 0f, DirectionalSpread = 0.6f });

        Assert.True(spread.Any(wave => wave.Direction.Y > 0.05f), "no wave spread to one side");
        Assert.True(spread.Any(wave => wave.Direction.Y < -0.05f), "no wave spread to the other");
    }

    /// <summary>The dispersion relation is derived, so a wave's speed follows from its length.</summary>
    /// <remarks>
    ///     ⚠ Derived and not stored, for the reason depth is not stored in the info texture: a third
    ///     number that can disagree with the two it came from. A long swell overtakes a short chop,
    ///     which is what a real sea does and what makes crests cross rather than march in step.
    /// </remarks>
    [Fact]
    public void ALongerWaveTravelsFaster() {
        var slow = new GerstnerWave(new(1f, 0f), 4f, 0.1f, 0.1f, 0f);
        var fast = new GerstnerWave(new(1f, 0f), 64f, 0.1f, 0.1f, 0f);

        // Phase speed is ω/k = √(g/k), so the longer wave is faster.
        Assert.True(fast.AngularFrequency / fast.WaveNumber > slow.AngularFrequency / slow.WaveNumber);

        // And the relation itself, against its own definition.
        Assert.Equal(MathF.Sqrt(GerstnerWave.Gravity * fast.WaveNumber), fast.AngularFrequency, 1e-5f);
    }

    /// <summary>The maximum amplitude is a bound, not a measurement.</summary>
    /// <remarks>
    ///     ⚠ A measured maximum — sampling the sum over a grid and taking the largest — gives a
    ///     smaller, prettier number that is wrong about once a frame. The frame it is wrong on is the
    ///     one where the bounding box does not contain the surface, which is a wave culled away in
    ///     front of the camera.
    /// </remarks>
    [Fact]
    public void TheMaximumAmplitudeBoundsEveryCrest() {
        var waves = Generate(WaterWaveSpectrum.Default);
        var bound = WaterWaveSpectrum.MaximumAmplitude(waves);
        var evaluator = new WaterEvaluator(null, waves, WaterAttenuation.Default);

        for (var step = 0; step < 2_000; step++) {
            var position = new Core.Mathematics.Vector2(step * 0.37f, step * -0.61f);
            var height = evaluator.Lift(position, step * 0.017f, 1f);

            Assert.InRange(height, -bound, bound);
        }
    }

    /// <summary>A spectrum that cannot be summed says so rather than dividing by nothing.</summary>
    [Fact]
    public void AnImpossibleSpectrumIsRefusedByName() {
        Assert.NotNull((WaterWaveSpectrum.Default with { MinimumWavelength = 0f }).Validate());
        Assert.NotNull((WaterWaveSpectrum.Default with { MinimumWavelength = 90f }).Validate());
        Assert.NotNull((WaterWaveSpectrum.Default with { Count = (WaterWaveCount)20 }).Validate());
        Assert.Null(WaterWaveSpectrum.Default.Validate());
        Assert.Null(WaterWaveSpectrum.Calm.Validate());

        Assert.Throws<ArgumentException>(
            () => (WaterWaveSpectrum.Default with { MinimumWavelength = 0f }).Generate(new GerstnerWave[16])
        );
    }

    /// <summary>
    ///     Amplitude falls off monotonically as the ground rises, and is exactly zero at zero depth.
    /// </summary>
    /// <remarks>
    ///     [docs/plan/35 § Part 4]'s property test. ⚠ Exactly zero at zero and not merely small: a
    ///     millimetre of swell lapping over dry sand is what an asymptotic falloff looks like from a
    ///     character's eye height, and it is worse than no attenuation at all because it looks
    ///     deliberate.
    /// </remarks>
    [Fact]
    public void AttenuationIsMonotoneAndReachesZero() {
        var attenuation = WaterAttenuation.Default;

        Assert.Equal(0f, attenuation.At(0f));
        Assert.Equal(1f, attenuation.At(attenuation.Depth));
        Assert.Equal(1f, attenuation.At(attenuation.Depth * 10f));

        Gen.Float[0f, 20f]
            .Select(Gen.Float[0f, 20f])
            .Sample(
                pair => {
                    var (first, second) = pair;
                    var (shallow, deep) = first <= second ? (first, second) : (second, first);

                    Assert.True(
                        attenuation.At(shallow) <= attenuation.At(deep) + 1e-6f,
                        $"attenuation rose as the water got shallower: {shallow} m over {deep} m"
                    );
                },
                iter: 10_000
            );
    }

    /// <summary>Every wave's bits, folded into one number a CI leg on another host can compare.</summary>
    static uint Fingerprint(GerstnerWave[] waves) {
        var hash = 2166136261u;

        foreach (var value in MemoryMarshal.Cast<GerstnerWave, float>(waves)) {
            var bits = BitConverter.SingleToUInt32Bits(value);

            hash = (hash ^ bits) * 16777619u;
        }

        return hash;
    }
}
