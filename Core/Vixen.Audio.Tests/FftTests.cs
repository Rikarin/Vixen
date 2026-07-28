// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Dsp;
using Xunit;

namespace Vixen.Audio.Tests;

/// <summary>
///     Two effects are built on this and neither can be debugged by ear, so it is checked against
///     transforms whose answers are known on paper.
/// </summary>
public sealed class FftTests {
    [Fact]
    public void ADeltaTransformsToAFlatSpectrum() {
        var fft = new Fft(16);
        var real = new float[16];
        var imaginary = new float[16];
        real[0] = 1f;

        fft.Forward(real, imaginary);

        // Every bin of the transform of a unit impulse at zero is exactly 1 + 0i.
        for (var i = 0; i < 16; i++) {
            Assert.Equal(1f, real[i], 1e-5f);
            Assert.Equal(0f, imaginary[i], 1e-5f);
        }
    }

    [Fact]
    public void AConstantTransformsToASingleBinAtZero() {
        var fft = new Fft(16);
        var real = new float[16];
        var imaginary = new float[16];
        Array.Fill(real, 1f);

        fft.Forward(real, imaginary);

        Assert.Equal(16f, real[0], 1e-4f);

        for (var i = 1; i < 16; i++) {
            Assert.Equal(0f, MathF.Sqrt((real[i] * real[i]) + (imaginary[i] * imaginary[i])), 1e-4f);
        }
    }

    /// <summary>
    ///     A sine at exactly bin <c>k</c> puts all of its energy in bins <c>k</c> and <c>N − k</c> —
    ///     the mirror is what makes half a real signal's transform redundant.
    /// </summary>
    [Fact]
    public void ASineLandsInItsOwnBinAndItsMirror() {
        const int size = 64;
        const int bin = 5;

        var fft = new Fft(size);
        var real = new float[size];
        var imaginary = new float[size];

        for (var i = 0; i < size; i++) {
            real[i] = MathF.Sin(2f * MathF.PI * bin * i / size);
        }

        fft.Forward(real, imaginary);

        for (var i = 0; i < size; i++) {
            var magnitude = MathF.Sqrt((real[i] * real[i]) + (imaginary[i] * imaginary[i]));

            if (i == bin || i == size - bin) {
                Assert.Equal(size / 2f, magnitude, 1e-3f);
                continue;
            }

            Assert.Equal(0f, magnitude, 1e-3f);
        }
    }

    [Fact]
    public void AForwardFollowedByAnInverseGivesBackWhatWentIn() {
        const int size = 256;
        var fft = new Fft(size);
        var real = new float[size];
        var imaginary = new float[size];
        var expected = new float[size];

        for (var i = 0; i < size; i++) {
            // Something with no symmetry to hide a mistake behind.
            expected[i] = real[i] = MathF.Sin(i * 0.37f) + (0.3f * MathF.Cos(i * 1.11f));
        }

        fft.Forward(real, imaginary);
        fft.Inverse(real, imaginary);

        for (var i = 0; i < size; i++) {
            Assert.Equal(expected[i], real[i], 1e-4f);
            Assert.Equal(0f, imaginary[i], 1e-4f);
        }
    }

    /// <summary>
    ///     What the convolution reverb is built on: multiplying two spectra is convolving the two
    ///     signals. If this does not hold, the reverb is noise.
    /// </summary>
    [Fact]
    public void MultiplyingSpectraConvolvesTheSignals() {
        const int size = 16;
        var fft = new Fft(size);

        var a = new float[size];
        var b = new float[size];
        a[0] = 1f;
        a[1] = 0.5f;
        b[0] = 1f;
        b[2] = 0.25f;

        var expected = new float[size];

        for (var i = 0; i < size; i++) {
            for (var j = 0; j + i < size; j++) {
                expected[i + j] += a[i] * b[j];
            }
        }

        var ar = (float[])a.Clone();
        var ai = new float[size];
        var br = (float[])b.Clone();
        var bi = new float[size];

        fft.Forward(ar, ai);
        fft.Forward(br, bi);

        for (var i = 0; i < size; i++) {
            var re = (ar[i] * br[i]) - (ai[i] * bi[i]);
            var im = (ar[i] * bi[i]) + (ai[i] * br[i]);
            ar[i] = re;
            ai[i] = im;
        }

        fft.Inverse(ar, ai);

        for (var i = 0; i < size; i++) {
            Assert.Equal(expected[i], ar[i], 1e-4f);
        }
    }

    [Fact]
    public void ASizeThatIsNotAPowerOfTwoIsRefused() {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Fft(12));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Fft(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Fft(0));
    }

    [Fact]
    public void NextSizeRoundsUpToAPowerOfTwo() {
        Assert.Equal(2, Fft.NextSize(1));
        Assert.Equal(512, Fft.NextSize(512));
        Assert.Equal(1_024, Fft.NextSize(513));
    }
}
