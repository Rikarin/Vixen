// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Core.Imaging.Tests;

public sealed class ImageResizeTests {
    static FloatImage Ramp(int width, int height) =>
        new(width, height, 1, [..Enumerable.Range(0, width * height).Select(static at => (float)at)]);

    /// <summary>Halving with a box is the mean of each two-by-two: the closed form a mip level has.</summary>
    [Fact]
    public void ABoxHalvingIsTheMeanOfEachTwoByTwo() {
        var image = Ramp(4, 4);

        var half = ImageResize.Resize(image, 2, 2);

        Assert.Equal(
            [(0f + 1 + 4 + 5) / 4, (2f + 3 + 6 + 7) / 4, (8f + 9 + 12 + 13) / 4, (10f + 11 + 14 + 15) / 4],
            half.Samples
        );
    }

    /// <summary>
    ///     A box keeps the picture's mean at any ratio, three to two included, where each target
    ///     texel straddles a source texel. Bilinear does not, which is the aliasing it is known for.
    /// </summary>
    [Theory]
    [InlineData(3, 2)]
    [InlineData(16, 3)]
    [InlineData(5, 7)]
    public void ABoxKeepsTheMean(int from, int to) {
        var image = Ramp(from, from);

        var resized = ImageResize.Resize(image, to, to);

        Assert.Equal(image.Samples.Average(), resized.Samples.Average(), 3);
    }

    [Fact]
    public void NearestMakesNoNewValues() {
        var image = new FloatImage(2, 2, 2, [0f, 1f, 2f, 3f, 4f, 5f, 6f, 7f]);

        var grown = ImageResize.Resize(image, 5, 3, ResizeFilter.Nearest);

        Assert.All(grown.Samples, value => Assert.Contains(value, image.Samples));
        Assert.Equal([0f, 1f], grown.Samples[..2]);
        Assert.Equal([6f, 7f], grown.Samples[^2..]);
    }

    [Fact]
    public void BilinearBlendsNeighbours() {
        var image = new FloatImage(2, 1, 1, [0f, 1f]);

        var grown = ImageResize.Resize(image, 4, 1, ResizeFilter.Bilinear);

        Assert.Equal([0f, 0.25f, 0.75f, 1f], grown.Samples);
    }

    [Fact]
    public void TheSameSizeIsACopy() {
        var image = Ramp(3, 3);

        var same = ImageResize.Resize(image, 3, 3);

        Assert.Equal(image.Samples, same.Samples);
        Assert.NotSame(image.Samples, same.Samples);
    }
}
