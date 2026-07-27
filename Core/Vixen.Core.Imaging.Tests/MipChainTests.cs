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

    /// <summary>
    ///     The mean, <b>rounded</b>. Truncating loses half a level at every step and a chain is ten
    ///     steps deep, so a texture's smallest mips come out measurably darker than its largest —
    ///     which looks like distant surfaces being dimmer than near ones for no reason anyone can
    ///     point at. 138.75 is 139.
    /// </summary>
    [Fact]
    public void EachLevelIsTheRoundedMeanOfTheFourTexelsAboveIt() {
        var texture = new TextureData(PixelFormat.R8UNorm, 2, 2);
        new byte[] { 0, 100, 200, 255 }.CopyTo(texture.LevelSpan(0));

        MipChain.Generate(texture);

        Assert.Equal(139, texture.Level(1)[0]);
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

        Assert.Equal([50, 228], texture.Level(1).ToArray());
        Assert.Equal([139], texture.Level(2).ToArray());
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

    /// <summary>
    ///     The same fact, through the filter rather than through the table: a checkerboard of black
    ///     and white reduces to 188 when the caller says the texture is colour and to 128 when it
    ///     does not. Both answers are in the suite because both are correct for some texture, and
    ///     which one you get is the caller's statement about what the bytes mean.
    /// </summary>
    [Fact]
    public void AColourTextureIsFilteredInLinearLightAndAnythingElseIsNot() {
        Assert.Equal(188, Checkerboard(MipOptions.Colour));
        Assert.Equal(128, Checkerboard(MipOptions.Linear));

        static byte Checkerboard(MipOptions options) {
            var texture = new TextureData(PixelFormat.Rgba8UNorm, 2, 2);
            var pixels = texture.LevelSpan(0);

            for (var texel = 0; texel < 4; texel++) {
                var white = texel % 2 == 0;
                pixels[texel * 4] = white ? (byte)255 : (byte)0;
                pixels[(texel * 4) + 1] = white ? (byte)255 : (byte)0;
                pixels[(texel * 4) + 2] = white ? (byte)255 : (byte)0;
                pixels[(texel * 4) + 3] = 255;
            }

            MipChain.Generate(texture, options);
            return texture.Level(1)[0];
        }
    }

    /// <summary>
    ///     <para>
    ///         Alpha weighting, and the artefact it exists to prevent. A cut-out leaf texture is
    ///         painted green where it is opaque and whatever-was-left where it is not — very often
    ///         black, because that is what an empty canvas is. Averaging the colour without regard to
    ///         alpha drags the leaf's edge towards that black, and every mip level makes the fringe
    ///         wider, which is why distant foliage in a lot of games has a dark halo.
    ///     </para>
    ///     <para>
    ///         Alpha itself is averaged plainly in both cases: it is what decides the weights and
    ///         cannot be weighted by itself.
    ///     </para>
    /// </summary>
    [Fact]
    public void AlphaWeightingKeepsTransparentTexelsOutOfTheColour() {
        var weighted = Leaf(MipOptions.CutoutColour with { Srgb = false });
        var unweighted = Leaf(MipOptions.Linear);

        // One opaque green texel and three transparent black ones.
        Assert.Equal(200, weighted[1]);
        Assert.Equal(50, unweighted[1]);

        // And alpha is a quarter of the way up in both.
        Assert.Equal(64, weighted[3]);
        Assert.Equal(64, unweighted[3]);

        static byte[] Leaf(MipOptions options) {
            var texture = new TextureData(PixelFormat.Rgba8UNorm, 2, 2);
            var pixels = texture.LevelSpan(0);
            pixels[1] = 200;
            pixels[3] = 255;

            MipChain.Generate(texture, options);
            return texture.Level(1).ToArray();
        }
    }

    /// <summary>
    ///     Every texel transparent means there are no weights to divide by. Falling back to the plain
    ///     mean keeps whatever colour was painted under the transparency, which is what a dilation
    ///     pass goes looking for; dividing by zero would produce a NaN and then a black texel.
    /// </summary>
    [Fact]
    public void AWhollyTransparentFootprintFallsBackToThePlainMean() {
        var texture = new TextureData(PixelFormat.Rgba8UNorm, 2, 2);
        var pixels = texture.LevelSpan(0);

        for (var texel = 0; texel < 4; texel++) {
            pixels[texel * 4] = (byte)(texel * 20);
        }

        MipChain.Generate(texture, new() { AlphaWeighted = true });

        Assert.Equal(30, texture.Level(1)[0]);
    }

    /// <summary>
    ///     The average of four unit vectors is not a unit vector, and a normal map whose mips are
    ///     short lights as though the surface were flatter than it is — which is the correct
    ///     appearance for a surface whose detail has been averaged away, but only if the shortening
    ///     is deliberate. Renormalising makes it deliberate: the direction survives and the flatness
    ///     comes from the roughness map instead.
    /// </summary>
    [Fact]
    public void NormalsAreAveragedAsDirectionsAndComeBackUnitLength() {
        var texture = new TextureData(PixelFormat.Rgba8UNorm, 2, 2);
        var pixels = texture.LevelSpan(0);

        // Four normals at forty-five degrees, each leaning towards a different edge. Byte 218 is
        // +0.707, byte 36 is -0.707, and byte 128 is zero; every one of them is unit length, which
        // is the point — a normal map that was not unit length to begin with would prove nothing.
        ReadOnlySpan<(byte X, byte Y)> leaning = [(218, 128), (36, 128), (128, 218), (128, 36)];

        for (var texel = 0; texel < 4; texel++) {
            pixels[texel * 4] = leaning[texel].X;
            pixels[(texel * 4) + 1] = leaning[texel].Y;
            pixels[(texel * 4) + 2] = 218;
            pixels[(texel * 4) + 3] = 255;
        }

        MipChain.Generate(texture, MipOptions.NormalMap);

        var reduced = texture.Level(1);
        var x = (reduced[0] / 255f * 2f) - 1f;
        var y = (reduced[1] / 255f * 2f) - 1f;
        var z = (reduced[2] / 255f * 2f) - 1f;

        // Within a fiftieth: what is stored is a byte per channel, so a unit vector comes back as
        // a vector of length one give or take a step of the encoding.
        Assert.Equal(1f, MathF.Sqrt((x * x) + (y * y) + (z * z)), tolerance: 0.02f);

        // These four cancel in x and y, so what is left is straight up.
        Assert.True(z > 0.99f, $"the averaged normal points at {x}, {y}, {z}");
    }

    /// <summary>
    ///     And the unweighted filter does not: the same four unit normals average to a vector nearly
    ///     thirty per cent short of one, which is the artefact the option exists to remove.
    /// </summary>
    [Fact]
    public void TheUnweightedFilterLeavesNormalsShortOfUnitLength() {
        var texture = new TextureData(PixelFormat.Rgba8UNorm, 2, 2);
        var pixels = texture.LevelSpan(0);
        ReadOnlySpan<(byte X, byte Y)> leaning = [(218, 128), (36, 128), (128, 218), (128, 36)];

        for (var texel = 0; texel < 4; texel++) {
            pixels[texel * 4] = leaning[texel].X;
            pixels[(texel * 4) + 1] = leaning[texel].Y;
            pixels[(texel * 4) + 2] = 218;
            pixels[(texel * 4) + 3] = 255;
        }

        MipChain.Generate(texture, MipOptions.Linear);

        var reduced = texture.Level(1);
        var x = (reduced[0] / 255f * 2f) - 1f;
        var y = (reduced[1] / 255f * 2f) - 1f;
        var z = (reduced[2] / 255f * 2f) - 1f;

        // Four unit vectors averaging to one of length 0.71: nearly thirty per cent short, and a
        // shader that normalises this back gets the direction right and the shading history wrong.
        var length = MathF.Sqrt((x * x) + (y * y) + (z * z));
        Assert.True(length < 0.75f, $"the unweighted mean came out {length} long");
    }

    /// <summary>
    ///     A two-channel normal map carries x and y and leaves z to the shader, so z has to be
    ///     reconstructed before averaging and dropped afterwards. Averaging x and y on their own and
    ///     renormalising the pair is a different answer, and a wrong one: it throws away how far each
    ///     source normal was leaning towards the viewer, which is most of what distinguishes them.
    /// </summary>
    [Fact]
    public void ATwoChannelNormalMapReconstructsItsThirdChannelBeforeAveraging() {
        var texture = new TextureData(PixelFormat.Rg8UNorm, 2, 1);
        var pixels = texture.LevelSpan(0);

        // One normal leaning well over in x, one pointing straight at the viewer.
        pixels[0] = 230;
        pixels[1] = 128;
        pixels[2] = 128;
        pixels[3] = 128;

        MipChain.Generate(texture, MipOptions.NormalMap);

        // Worked out by hand: the two reconstruct to (0.804, 0.004, 0.595) and (0.004, 0.004,
        // 1.000), which sum and normalise to an x of 0.452 — byte 185. Averaging the stored x and y
        // on their own and leaving z to the shader would give 179 instead, because it has no way to
        // know that the first normal was leaning much further over than the second.
        Assert.Equal([185, 128], texture.Level(1).ToArray());
    }

    /// <summary>
    ///     A texture is colour or it is a direction, and a caller asking for both has taken one of
    ///     the two settings from the wrong place.
    /// </summary>
    [Fact]
    public void AskingForBothColourAndNormalsIsRefused() {
        var failure = Assert.Throws<ArgumentException>(
            () => MipChain.Generate(
                new(PixelFormat.Rgba8UNorm, 2, 2),
                new() { Srgb = true, RenormaliseNormals = true }
            )
        );

        Assert.Contains("cannot be both", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AlphaWeightingAFormatWithNoAlphaIsRefused() {
        var failure = Assert.Throws<ArgumentException>(
            () => MipChain.Generate(new(PixelFormat.R8UNorm, 2, 2), new() { AlphaWeighted = true })
        );

        Assert.Contains("no alpha channel", failure.Message, StringComparison.Ordinal);
    }
}
