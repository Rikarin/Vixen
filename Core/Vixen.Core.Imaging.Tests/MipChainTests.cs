// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics;
using Xunit;

namespace Vixen.Core.Imaging.Tests;

public sealed class MipChainTests {
    [Theory]
    [InlineData(256, 256, 9)]
    [InlineData(1, 1, 1)]
    [InlineData(8, 2, 4)]
    [InlineData(1024, 1, 11)]
    public void AChainRunsDownToOneByOne(int width, int height, int expected) =>
        Assert.Equal(expected, new TextureData(PixelFormat.Rgba8UNorm, width, height).LevelCount);

    /// <summary>
    ///     A non-square texture's smaller side reaches one first and stays there while the longer side
    ///     keeps halving. Clamping the wrong way round is how a 1024×1 texture ends up with one level.
    /// </summary>
    [Fact]
    public void ASideThatReachesOneStaysThereWhileTheOtherKeepsHalving() {
        var texture = new TextureData(PixelFormat.R8UNorm, 8, 2);

        Assert.Equal([(8, 2), (4, 1), (2, 1), (1, 1)], texture.Levels.Select(level => (level.Width, level.Height)));
    }

    [Fact]
    public void EachLevelIsTheMeanOfTheFourTexelsAboveIt() {
        var texture = new TextureData(PixelFormat.R8UNorm, 2, 2);
        new byte[] { 0, 100, 200, 255 }.CopyTo(texture.LevelSpan(0));

        MipChain.Generate(texture);

        Assert.Equal((0 + 100 + 200 + 255) / 4, texture.Level(1)[0]);
    }

    /// <summary>
    ///     A dimension that has already reached one is not read twice. A 1×4 texture reduces to 1×2,
    ///     and each destination texel's two-wide footprint has only one texel in it — reading the
    ///     second would run off the end of the row and into whatever follows.
    /// </summary>
    /// <remarks>
    ///     This test replaced one called "an odd-sized level averages only the texels that are
    ///     there", which was named for a property it never exercised: deleting the bounds check left
    ///     it green. An odd width never produces an out-of-range read — a five-wide level reduces to
    ///     two, and the last column is simply dropped — so the only case that needs the check is a
    ///     dimension already at one.
    /// </remarks>
    [Fact]
    public void ADimensionThatHasReachedOneIsNotReadTwice() {
        var texture = new TextureData(PixelFormat.R8UNorm, 1, 4);
        new byte[] { 0, 100, 200, 255 }.CopyTo(texture.LevelSpan(0));

        MipChain.Generate(texture);

        Assert.Equal([50, 227], texture.Level(1).ToArray());
        Assert.Equal([138], texture.Level(2).ToArray());
    }

    /// <summary>
    ///     And an odd-sized level drops its last row or column rather than weighting it. A box filter
    ///     does; saying so is better than implying it does something cleverer.
    /// </summary>
    [Fact]
    public void AnOddSizedLevelDropsItsLastColumn() {
        var texture = new TextureData(PixelFormat.R8UNorm, 5, 1);
        new byte[] { 0, 100, 200, 220, 255 }.CopyTo(texture.LevelSpan(0));

        MipChain.Generate(texture);

        Assert.Equal([50, 210], texture.Level(1).ToArray());
    }

    [Fact]
    public void EveryChannelIsReducedIndependently() {
        var texture = new TextureData(PixelFormat.Rgba8UNorm, 2, 1);
        new byte[] { 0, 10, 20, 30, 100, 110, 120, 130 }.CopyTo(texture.LevelSpan(0));

        MipChain.Generate(texture);

        Assert.Equal([50, 60, 70, 80], texture.Level(1).ToArray());
    }

    /// <summary>
    ///     Reducing compressed blocks means decode, filter and re-encode, and each round loses more
    ///     than the filter gains — so a chain is generated before compression, and asking for the
    ///     other order says so rather than producing quiet mush.
    /// </summary>
    [Fact]
    public void ACompressedFormatIsRefusedWithTheReason() {
        var failure = Assert.Throws<NotSupportedException>(
            () => MipChain.Generate(new TextureData(PixelFormat.Bc7RgbaUNorm, 8, 8))
        );

        Assert.Contains("before compression", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The transfer function is a round trip, which is what lets an importer convert to linear,
    ///     filter, and convert back without drifting.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(63)]
    [InlineData(128)]
    [InlineData(254)]
    [InlineData(255)]
    public void TheSrgbTableRoundTripsEveryByte(byte value) =>
        Assert.Equal(value, MipChain.Srgb.FromLinear(MipChain.Srgb.ToLinearTable[value]));

    /// <summary>
    ///     And the reason it exists: averaging sRGB-encoded values is darker than averaging the light
    ///     they stand for. Half black and half white is 188, not 128.
    /// </summary>
    [Fact]
    public void AveragingInLinearLightIsBrighterThanAveragingTheEncodedValues() {
        var encodedMean = (0 + 255) / 2;
        var linearMean = MipChain.Srgb.FromLinear((MipChain.Srgb.ToLinearTable[0] + MipChain.Srgb.ToLinearTable[255]) / 2f);

        Assert.Equal(127, encodedMean);
        Assert.Equal(188, linearMean);
    }
}
