// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Dsp;
using Xunit;

namespace Vixen.Audio.Tests;

/// <summary>
///     Checked against the complex transform rather than against hand-worked numbers, because the
///     failure mode of a real-input FFT is not a crash — it is a spectrum that is subtly wrong, and
///     the only thing that reliably catches that is the transform it is supposed to be replacing.
/// </summary>
public sealed class RealFftTests {
    static float[] Noise(int count, int seed) {
        var random = new Random(seed);
        var samples = new float[count];

        for (var i = 0; i < count; i++) {
            samples[i] = (float)((random.NextDouble() * 2.0) - 1.0);
        }

        return samples;
    }

    static float[] Tone(int count, float cyclesPerWindow, float amplitude = 1f, float phase = 0f) {
        var samples = new float[count];

        for (var i = 0; i < count; i++) {
            samples[i] = amplitude * MathF.Sin((2f * MathF.PI * cyclesPerWindow * i / count) + phase);
        }

        return samples;
    }

    /// <summary>The complex transform's answer, for comparison.</summary>
    static (float[] Real, float[] Imaginary) Reference(ReadOnlySpan<float> samples) {
        var real = samples.ToArray();
        var imaginary = new float[samples.Length];
        new Fft(samples.Length).Forward(real, imaginary);
        return (real, imaginary);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(64)]
    [InlineData(256)]
    [InlineData(1_024)]
    public void EveryBinAgreesWithTheComplexTransform(int size) {
        var samples = Noise(size, seed: size);
        var (expectedReal, expectedImaginary) = Reference(samples);

        var fft = new RealFft(size);
        var real = new float[fft.Bins];
        var imaginary = new float[fft.Bins];
        fft.Forward(samples, real, imaginary);

        Assert.Equal((size / 2) + 1, fft.Bins);

        for (var k = 0; k < fft.Bins; k++) {
            Assert.Equal(expectedReal[k], real[k], 1e-3f);
            Assert.Equal(expectedImaginary[k], imaginary[k], 1e-3f);
        }
    }

    /// <summary>Both ends are real for a real input, and getting either wrong is the classic mistake.</summary>
    [Theory]
    [InlineData(16)]
    [InlineData(128)]
    public void DcAndNyquistAreRealAndAreWhereTheyShouldBe(int size) {
        var samples = Noise(size, seed: 7);
        var fft = new RealFft(size);
        var real = new float[fft.Bins];
        var imaginary = new float[fft.Bins];

        fft.Forward(samples, real, imaginary);

        var sum = 0f;
        var alternating = 0f;

        for (var i = 0; i < size; i++) {
            sum += samples[i];
            alternating += i % 2 == 0 ? samples[i] : -samples[i];
        }

        Assert.Equal(sum, real[0], 1e-3f);
        Assert.Equal(0f, imaginary[0]);

        Assert.Equal(alternating, real[size / 2], 1e-3f);
        Assert.Equal(0f, imaginary[size / 2]);
    }

    [Theory]
    [InlineData(32)]
    [InlineData(256)]
    [InlineData(1_024)]
    public void ForwardThenInverseGivesBackWhatWentIn(int size) {
        var samples = Noise(size, seed: size + 1);
        var fft = new RealFft(size);
        var real = new float[fft.Bins];
        var imaginary = new float[fft.Bins];
        var restored = new float[size];

        fft.Forward(samples, real, imaginary);
        fft.Inverse(real, imaginary, restored);

        for (var i = 0; i < size; i++) {
            Assert.Equal(samples[i], restored[i], 1e-3f);
        }
    }

    /// <summary>A tone sitting exactly on a bin should be in that bin and nowhere else.</summary>
    [Fact]
    public void AToneLandsInItsOwnBin() {
        const int size = 512;
        const int bin = 40;

        var fft = new RealFft(size);
        var real = new float[fft.Bins];
        var imaginary = new float[fft.Bins];
        var magnitudes = new float[fft.Bins];

        fft.Forward(Tone(size, bin, 0.5f), real, imaginary);
        fft.Magnitudes(real, imaginary, magnitudes);

        // Half the window's worth of amplitude, which is what a real transform puts in one bin when
        // the other half of the energy is in the mirror it does not report.
        Assert.Equal(0.5f * size / 2f, magnitudes[bin], size * 0.01f);

        for (var k = 0; k < fft.Bins; k++) {
            if (Math.Abs(k - bin) > 1) {
                Assert.True(magnitudes[k] < magnitudes[bin] * 0.01f, $"bin {k} held {magnitudes[k]:F3}");
            }
        }
    }

    /// <summary>Phase is where a packing mistake hides: a magnitude-only test would pass regardless.</summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(0.7f)]
    [InlineData(1.9f)]
    [InlineData(-2.4f)]
    public void PhaseSurvivesTheUntangling(float phase) {
        const int size = 256;
        const int bin = 17;

        var samples = Tone(size, bin, 0.8f, phase);
        var (expectedReal, expectedImaginary) = Reference(samples);

        var fft = new RealFft(size);
        var real = new float[fft.Bins];
        var imaginary = new float[fft.Bins];
        fft.Forward(samples, real, imaginary);

        var expected = MathF.Atan2(expectedImaginary[bin], expectedReal[bin]);
        var actual = MathF.Atan2(imaginary[bin], real[bin]);

        Assert.Equal(expected, actual, 1e-2f);
    }

    [Fact]
    public void ItIsLinear() {
        const int size = 128;

        var a = Noise(size, seed: 1);
        var b = Noise(size, seed: 2);
        var sum = new float[size];

        for (var i = 0; i < size; i++) {
            sum[i] = a[i] + b[i];
        }

        var fft = new RealFft(size);

        var (realA, imaginaryA) = Transform(fft, a);
        var (realB, imaginaryB) = Transform(fft, b);
        var (realSum, imaginarySum) = Transform(fft, sum);

        for (var k = 0; k < fft.Bins; k++) {
            Assert.Equal(realA[k] + realB[k], realSum[k], 1e-3f);
            Assert.Equal(imaginaryA[k] + imaginaryB[k], imaginarySum[k], 1e-3f);
        }
    }

    static (float[] Real, float[] Imaginary) Transform(RealFft fft, ReadOnlySpan<float> samples) {
        var real = new float[fft.Bins];
        var imaginary = new float[fft.Bins];
        fft.Forward(samples, real, imaginary);
        return (real, imaginary);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(12)]
    [InlineData(0)]
    public void ASizeItCannotDoIsRefused(int size) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new RealFft(size));

    [Fact]
    public void ASpanOfTheWrongLengthIsRefused() {
        var fft = new RealFft(32);

        Assert.Throws<ArgumentException>(() => fft.Forward(new float[16], new float[17], new float[17]));
        Assert.Throws<ArgumentException>(() => fft.Forward(new float[32], new float[4], new float[4]));
        Assert.Throws<ArgumentException>(() => fft.Inverse(new float[17], new float[17], new float[8]));
    }
}
