// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Dsp;
using Vixen.Audio.Effects;
using Xunit;

namespace Vixen.Audio.Tests;

/// <summary>
///     Oversampled distortion, tested by measuring the thing it exists to remove. An aliasing test
///     that does not look at where the folded tones actually land proves nothing, which is a mistake
///     this repository has already made once with the resampler.
/// </summary>
public sealed class OversamplingTests {
    const DistortionCurve Curve = DistortionCurve.SoftClip;
    const float Drive = 18f;

    const int Rate = 48_000;
    const int Size = 8_192;

    /// <summary>Runs a tone through a distortion and returns the spectrum of what came out.</summary>
    static float[] Spectrum(float toneHz, int oversampling, float driveDb = 24f, DistortionCurve curve = DistortionCurve.HardClip) {
        var effect = new DistortionEffect {
            Curve = curve,
            DriveDb = driveDb,
            OutputDb = 0f,
            Mix = 1f,
            Oversampling = oversampling
        };

        effect.Prepare(new AudioFormat(Rate, 1), Size);

        var buffer = new float[Size];

        for (var i = 0; i < Size; i++) {
            buffer[i] = 0.5f * MathF.Sin(2f * MathF.PI * toneHz * i / Rate);
        }

        effect.Process(buffer, Size, 1);

        // A window, so a tone that does not complete a whole number of cycles does not smear across
        // the spectrum and hide what is being measured.
        for (var i = 0; i < Size; i++) {
            var t = 2f * MathF.PI * i / (Size - 1);
            buffer[i] *= 0.5f - (0.5f * MathF.Cos(t));
        }

        var fft = new RealFft(Size);
        var real = new float[fft.Bins];
        var imaginary = new float[fft.Bins];
        var magnitudes = new float[fft.Bins];

        fft.Forward(buffer, real, imaginary);
        fft.Magnitudes(real, imaginary, magnitudes);
        return magnitudes;
    }

    /// <summary>The spectrum of a block, windowed.</summary>
    static float[] Spectrum(float[] block) {
        var windowed = (float[])block.Clone();

        for (var i = 0; i < Size; i++) {
            windowed[i] *= 0.5f - (0.5f * MathF.Cos(2f * MathF.PI * i / (Size - 1)));
        }

        var fft = new RealFft(Size);
        var real = new float[fft.Bins];
        var imaginary = new float[fft.Bins];
        var magnitudes = new float[fft.Bins];

        fft.Forward(windowed, real, imaginary);
        fft.Magnitudes(real, imaginary, magnitudes);
        return magnitudes;
    }

    static int BinOf(float hertz) => (int)MathF.Round(hertz * Size / Rate);

    static float Near(ReadOnlySpan<float> spectrum, float hertz) {
        var bin = BinOf(hertz);
        var loudest = 0f;

        for (var k = Math.Max(bin - 2, 0); k <= Math.Min(bin + 2, spectrum.Length - 1); k++) {
            loudest = MathF.Max(loudest, spectrum[k]);
        }

        return loudest;
    }

    /// <summary>
    ///     The claim, measured with a nonlinearity whose output bandwidth is known exactly. Squaring
    ///     a sine produces DC and one harmonic at twice the frequency and nothing else — so a 20 kHz
    ///     tone squared puts everything at 40 kHz, which at a 48 kHz rate folds to 8 kHz. There is no
    ///     ambiguity about what is being counted, which a curve with an unbounded harmonic series
    ///     cannot offer.
    /// </summary>
    [Fact]
    public void OversamplingRemovesTheFoldedTone() {
        var input = new float[Size];

        for (var i = 0; i < Size; i++) {
            input[i] = 0.7f * MathF.Sin(2f * MathF.PI * 20_000f * i / Rate);
        }

        var plain = new float[Size];

        for (var i = 0; i < Size; i++) {
            plain[i] = input[i] * input[i];
        }

        var sampler = new Oversampler(1, 4);
        var points = new float[4];
        var oversampled = new float[Size];

        for (var i = 0; i < Size; i++) {
            sampler.Expand(0, input[i], points);

            for (var p = 0; p < points.Length; p++) {
                points[p] *= points[p];
            }

            oversampled[i] = sampler.Collapse(0, points);
        }

        var without = Near(Spectrum(plain), 8_000f);
        var with = Near(Spectrum(oversampled), 8_000f);

        Assert.True(without > 1f, $"there was nothing to remove: the fold measured {without:F3}");

        // Two orders of magnitude. Anything less would mean the interpolator is leaking images that
        // the shaping then turns into difference tones — which is what a polyphase filter walking its
        // history the wrong way round does, and it gets worse rather than better with more taps.
        Assert.True(with < without / 100f, $"the fold was {without:F3} without and {with:F3} with");
    }

    /// <summary>The harmonics that belong there have to survive, or this is just a low-pass.</summary>
    [Fact]
    public void TheRealHarmonicsAreStillThere() {
        const float tone = 1_000f;

        var spectrum = Spectrum(tone, oversampling: 4);
        var fundamental = Near(spectrum, tone);

        // Hard clipping a sine makes odd harmonics. The third and fifth are well below Nyquist and
        // are the sound of the effect.
        Assert.True(Near(spectrum, 3_000f) > fundamental * 0.05f, "the third harmonic went missing");
        Assert.True(Near(spectrum, 5_000f) > fundamental * 0.02f, "the fifth harmonic went missing");
    }

    [Fact]
    public void WithoutOversamplingItBehavesExactlyAsItAlwaysDid() {
        var effect = new DistortionEffect { DriveDb = 12f, OutputDb = 0f, Mix = 1f };
        effect.Prepare(new AudioFormat(Rate, 1), 64);

        var buffer = new float[64];
        var expected = new float[64];

        for (var i = 0; i < 64; i++) {
            buffer[i] = expected[i] = 0.3f * MathF.Sin(2f * MathF.PI * 440f * i / Rate);
        }

        effect.Process(buffer, 64, 1);

        var drive = Decibels.ToLinear(12f);

        for (var i = 0; i < 64; i++) {
            Assert.Equal(DistortionEffect.Shape(expected[i] * drive, DistortionCurve.SoftClip), buffer[i], 1e-5f);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(8)]
    public void AFactorItDoesNotHaveMeansOff(int factor) {
        var effect = new DistortionEffect { Oversampling = factor };
        Assert.Equal(1, effect.Oversampling);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void TheFactorsItDoesHaveStick(int factor) {
        var effect = new DistortionEffect { Oversampling = factor };
        Assert.Equal(factor, effect.Oversampling);
    }

    // ── The oversampler on its own ────────────────────────────────────────────────────────────

    /// <summary>
    ///     Up and straight back down with nothing in between has to be the identity, or every reading
    ///     downstream of it carries a gain error nobody put there.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void ARoundTripThroughNothingIsTheSignal(int factor) {
        var sampler = new Oversampler(1, factor);
        var points = new float[factor];

        var input = new float[512];
        var output = new float[512];

        for (var i = 0; i < input.Length; i++) {
            input[i] = 0.6f * MathF.Sin(2f * MathF.PI * 500f * i / Rate);
        }

        for (var i = 0; i < input.Length; i++) {
            sampler.Expand(0, input[i], points);
            output[i] = sampler.Collapse(0, points);
        }

        // The filters have a delay, so the comparison is against a shifted copy — taken from the
        // oversampler rather than guessed, because a hand-written constant goes stale the moment the
        // tap count moves and then reports a gain error that is really an alignment error.
        var delay = sampler.Latency;

        var correlation = 0f;
        var energy = 0f;

        for (var i = delay + 32; i < input.Length - 32; i++) {
            correlation += output[i] * input[i - delay];
            energy += input[i - delay] * input[i - delay];
        }

        // Both bounds. A lower bound on its own is passed by any gain above it — including the
        // factor-of-four one that a polyphase upsampler gets when it is given the zero-stuffing
        // correction it does not need.
        var gain = correlation / energy;
        Assert.True(gain is > 0.85f and < 1.15f, $"the round trip came out at a gain of {gain:F3}");
    }

    [Fact]
    public void AFactorTheOversamplerDoesNotHaveIsRefused() {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Oversampler(2, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Oversampler(0));
    }

    [Fact]
    public void ChannelsKeepTheirOwnFilters() {
        var sampler = new Oversampler(2, 4);
        var points = new float[4];

        // A step on the left and silence on the right. Shared state would let the step leak.
        for (var i = 0; i < 64; i++) {
            sampler.Expand(0, 1f, points);
            sampler.Collapse(0, points);

            sampler.Expand(1, 0f, points);
            var right = sampler.Collapse(1, points);

            Assert.Equal(0f, right, 1e-6f);
        }
    }
}
