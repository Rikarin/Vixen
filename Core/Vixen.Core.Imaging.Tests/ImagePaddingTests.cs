// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Core.Imaging.Tests;

public sealed class ImagePaddingTests {
    const int Side = 17;
    const float Unset = -7f;

    /// <summary>A grey picture of <see cref="Unset" /> with islands at the given texels holding the given values.</summary>
    static (FloatImage Image, bool[] Coverage) Picture(params (int X, int Y, float Value)[] islands) {
        var samples = Enumerable.Repeat(Unset, Side * Side).ToArray();
        var coverage = new bool[Side * Side];

        foreach (var (x, y, value) in islands) {
            samples[y * Side + x] = value;
            coverage[y * Side + x] = true;
        }

        return (new(Side, Side, 1, samples), coverage);
    }

    static float At(FloatImage image, int x, int y) => image.Samples[y * image.Width + x];

    /// <summary>
    ///     Four-neighbour rounds reach exactly the texels within that many steps, which is a
    ///     diamond: a closed form, so a round that reaches too far or not far enough shows.
    /// </summary>
    [Fact]
    public void TheIslandsGrowByTheDistanceInFourNeighbourSteps() {
        var (image, coverage) = Picture((8, 8, 0.25f));

        var grown = ImagePadding.Pad(image, coverage, PaddingMode.Colour, 3, [0.9f]);

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                var steps = Math.Abs(x - 8) + Math.Abs(y - 8);
                Assert.Equal(steps <= 3 ? 0.25f : 0.9f, At(image, x, y));
            }
        }

        // 1 + 4 + 8 + 12 texels within three steps, less the island itself.
        Assert.Equal(24, grown);
    }

    [Fact]
    public void InfiniteFillsEveryTexelAndLeavesTheIslandsAlone() {
        var (image, coverage) = Picture((0, 0, 0.5f), (16, 16, 0.75f));

        ImagePadding.Pad(image, coverage, PaddingMode.Infinite, 0);

        Assert.DoesNotContain(Unset, image.Samples);
        Assert.Equal(0.5f, At(image, 0, 0));
        Assert.Equal(0.75f, At(image, 16, 16));
        Assert.Equal(0.5f, At(image, 3, 2));
        Assert.Equal(0.75f, At(image, 13, 14));
    }

    /// <summary>
    ///     ⚠ Each round commits after it finishes. Writing in place lets a texel filled early in a
    ///     scan seed one later in the same round, so one island's padding reaches further right
    ///     and down than the other's reaches left and up. Two islands placed as mirror images of
    ///     each other must meet in the middle, and the padding must be the mirror image too.
    ///     ⚠ The picture is an even width on purpose: the islands are then an odd number of steps
    ///     apart, so the two fronts reach neighbouring texels in the same round, which is the only
    ///     place a texel could read a value written in its own round.
    /// </summary>
    [Fact]
    public void PaddingHasNoDirection() {
        const int width = 16;
        const int height = 9;
        var samples = new float[width * height];
        var coverage = new bool[width * height];
        (samples[4 * width + 2], coverage[4 * width + 2]) = (0f, true);
        (samples[4 * width + 13], coverage[4 * width + 13]) = (1f, true);
        var image = new FloatImage(width, height, 1, samples);

        ImagePadding.Pad(image, coverage, PaddingMode.Infinite, 0);

        for (var y = 0; y < height; y++) {
            for (var x = 0; x < width; x++) {
                Assert.Equal(1f - At(image, x, y), At(image, width - 1 - x, y), 5);
            }
        }
    }

    [Fact]
    public void TransparentLeavesZeroInEveryLanePastTheDistance() {
        var samples = Enumerable.Repeat(Unset, Side * Side * 4).ToArray();
        var coverage = new bool[Side * Side];
        coverage[0] = true;
        samples[0] = samples[1] = samples[2] = samples[3] = 1f;
        var image = new FloatImage(Side, Side, 4, samples);

        ImagePadding.Pad(image, coverage, PaddingMode.Transparent, 1);

        Assert.Equal([1f, 1f, 1f, 1f], image.Samples[4..8]);
        Assert.Equal([0f, 0f, 0f, 0f], image.Samples[8..12]);
        Assert.DoesNotContain(Unset, image.Samples);
    }

    /// <summary>
    ///     Diffusion fills everything with a blend of the islands: nothing outside their range, and
    ///     nearer the island a texel is nearer to.
    /// </summary>
    [Fact]
    public void DiffusionBlendsTheIslandsWithNoEdgeOfItsOwn() {
        var (image, coverage) = Picture((0, 0, 0f), (16, 16, 1f));

        ImagePadding.Pad(image, coverage, PaddingMode.Diffusion, 1);

        Assert.All(image.Samples, static value => Assert.InRange(value, 0f, 1f));
        Assert.True(At(image, 3, 3) < At(image, 13, 13), "A texel by the dark island came out lighter.");
        Assert.True(At(image, 5, 5) is > 0f and < 1f, "Diffusion copied one island instead of blending.");
    }

    [Fact]
    public void NoIslandLeavesThePictureAsItWas() {
        var (image, coverage) = Picture();

        Assert.Equal(0, ImagePadding.Pad(image, coverage, PaddingMode.Colour, 4, [1f]));
        Assert.All(image.Samples, static value => Assert.Equal(Unset, value));
    }

    [Fact]
    public void ACoverageOfAnotherSizeIsRefused() {
        var (image, _) = Picture();

        Assert.Throws<ArgumentException>(() => ImagePadding.Pad(image, new bool[4], PaddingMode.Infinite, 0));
    }
}
