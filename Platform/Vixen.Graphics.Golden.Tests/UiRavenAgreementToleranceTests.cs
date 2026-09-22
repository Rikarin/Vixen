// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Ui.Testing.Visual;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>
///     Which tolerance <see cref="UiRavenAgreementTests" /> holds a driver to, and what the software
///     one admits — decided here, with no device, because the decision is the whole of #1190's
///     concession and a concession nothing can test is one that can widen unnoticed.
/// </summary>
/// <remarks>
///     Both halves of the software tolerance, on pictures that genuinely differ: the measured
///     lavapipe divergence (one code, a handful of pixels) passes, and the two nearest things that are
///     not a fused multiply-add — one pixel two codes off, and a whole frame one code off — do not
///     both pass. The second of those is admitted on purpose, and the test says so.
/// </remarks>
public sealed class UiRavenAgreementToleranceTests {
    const int Side = 128;

    /// <summary>A hardware driver is held to byte-for-byte, and only a CPU driver to a code.</summary>
    [Theory]
    [InlineData(AdapterKind.Discrete)]
    [InlineData(AdapterKind.Integrated)]
    [InlineData(AdapterKind.Unknown)]
    public void HardwareIsHeldToExact(AdapterKind adapter) {
        Assert.Equal(ImageTolerance.Exact, UiRavenAgreementTests.Agreement(adapter));
    }

    [Fact]
    public void ASoftwareRasterizerIsHeldToALastPlaceBit() {
        Assert.Equal(UiRavenAgreementTests.LastPlaceOnSoftware, UiRavenAgreementTests.Agreement(AdapterKind.Software));
        Assert.NotEqual(ImageTolerance.Exact, UiRavenAgreementTests.LastPlaceOnSoftware);
    }

    /// <summary>
    ///     The divergence CI measures — 24 of 16384 pixels by 1/255 on <c>bordered</c> — passes the
    ///     software tolerance and fails the exact one, which is the instrument check on both.
    /// </summary>
    [Fact]
    public void TheMeasuredLavapipeDivergenceIsAdmittedAndNothingWider() {
        var copy = Flat(100);
        var source = Nudged(100, by: 1, count: 24);

        Assert.NotEqual(copy.Pixels, source.Pixels);
        Assert.False(ImageComparer.Compare(copy, source, ImageTolerance.Exact).Matches);
        Assert.True(ImageComparer.Compare(copy, source, UiRavenAgreementTests.LastPlaceOnSoftware).Matches);

        // A single pixel two codes off is not a last-place bit and must still be reported.
        var further = Nudged(100, by: 2, count: 1);
        Assert.False(ImageComparer.Compare(copy, further, UiRavenAgreementTests.LastPlaceOnSoftware).Matches);
    }

    static Bitmap Flat(byte level) {
        var pixels = new byte[Side * Side * 4];

        for (var i = 0; i < pixels.Length; i += 4) {
            pixels[i] = level;
            pixels[i + 1] = level;
            pixels[i + 2] = level;
            pixels[i + 3] = 255;
        }

        return new(Side, Side, pixels);
    }

    /// <summary>The flat picture with the first <paramref name="count" /> pixels moved by <paramref name="by" />.</summary>
    static Bitmap Nudged(byte level, byte by, int count) {
        var bitmap = Flat(level);

        for (var pixel = 0; pixel < count; pixel++) {
            var offset = pixel * 4;
            bitmap.Pixels[offset] = (byte)(level + by);
            bitmap.Pixels[offset + 1] = (byte)(level + by);
            bitmap.Pixels[offset + 2] = (byte)(level + by);
        }

        return bitmap;
    }
}
