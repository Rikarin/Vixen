// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging.BlockCompression;
using Vixen.Graphics;
using Xunit;

namespace Vixen.Core.Imaging.Tests;

/// <summary>Compressing a whole texture rather than a block: the walk, the edges and the refusals.</summary>
public sealed class BlockCompressorTests {
    [Fact]
    public void ACompressedTextureIsTheSizeTheFormatSaysItShouldBe() {
        var source = new TextureData(PixelFormat.Rgba8UNorm, 16, 16);

        var encoded = BlockCompressor.Encode(source, PixelFormat.Bc7RgbaUNorm);

        Assert.Equal(PixelFormat.Bc7RgbaUNorm, encoded.Format);
        Assert.Equal(source.LevelCount, encoded.LevelCount);

        // 16×16 at a byte per pixel, then 8×8, then three levels that are each a single block —
        // 4×4, 2×2 and 1×1 all round up to one, which is why a mip tail costs more than it looks.
        Assert.Equal(256 + 64 + 16 + 16 + 16, encoded.ByteLength);
    }

    /// <summary>
    ///     A texture whose width is not a multiple of four still has whole blocks in the file, and
    ///     the texels past the edge are filled by repeating the last real one. What matters is that
    ///     the padding stays out of the picture: every texel that exists comes back, and the ones
    ///     that do not are never written anywhere.
    /// </summary>
    [Fact]
    public void ATextureThatIsNotAWholeNumberOfBlocksKeepsEveryTexelItHas() {
        var source = new TextureData(PixelFormat.Rgba8UNorm, 5, 3, levelCount: 1);
        var pixels = source.PixelSpan();

        for (var texel = 0; texel < 5 * 3; texel++) {
            pixels[texel * 4] = (byte)(texel * 16);
            pixels[(texel * 4) + 1] = 0;
            pixels[(texel * 4) + 2] = 255;
            pixels[(texel * 4) + 3] = 255;
        }

        var encoded = BlockCompressor.Encode(source, PixelFormat.Bc7RgbaUNorm);

        // Two blocks across, one down: 5 rounds up to 8 and 3 rounds up to 4.
        Assert.Equal(2 * 16, encoded.ByteLength);

        var decoded = BlockCompressor.Decode(encoded);

        Assert.Equal(5, decoded.Width);
        Assert.Equal(3, decoded.Height);

        var back = decoded.Pixels;

        for (var texel = 0; texel < 5 * 3; texel++) {
            Assert.True(
                Math.Abs(back[texel * 4] - (texel * 16)) <= 10,
                $"texel {texel} came back as {back[texel * 4]} rather than {texel * 16}"
            );

            // Blue is constant, and off by at most the one low bit the two endpoints share.
            Assert.True(Math.Abs(back[(texel * 4) + 2] - 255) <= 1);
        }
    }

    /// <summary>
    ///     <para>
    ///         What the edge padding is <i>for</i>. A 5×4 texture's second block column holds four
    ///         real texels and twelve that are past the right edge, and those twelve have to be
    ///         something. Repeating the last real texel makes the block flat, so all four of BC1's
    ///         colours land inside the range the real texels occupy. Filling with black instead
    ///         stretches the endpoint line from black to the brightest real texel and spends three
    ///         quarters of its four levels on colours no texel has.
    ///     </para>
    ///     <para>
    ///         BC1 rather than BC7 because four levels is where the difference is visible, and the
    ///         bound is four rather than something comfortable for the same reason: repeating the
    ///         edge gets this column back to within one, and filling with black gets it to within
    ///         ten. A looser bound would have admitted both, which is what the first version of this
    ///         test did.
    ///     </para>
    /// </summary>
    [Fact]
    public void ThePaddingPastTheEdgeRepeatsTheEdgeRatherThanReachingForBlack() {
        var source = new TextureData(PixelFormat.Rgba8UNorm, 5, 4, levelCount: 1);
        var pixels = source.PixelSpan();

        for (var y = 0; y < 4; y++) {
            for (var x = 0; x < 5; x++) {
                var texel = (y * 5) + x;
                pixels[texel * 4] = x == 4 ? (byte)(180 + (y * 20)) : (byte)0;
                pixels[(texel * 4) + 3] = 255;
            }
        }

        var back = BlockCompressor.Decode(BlockCompressor.Encode(source, PixelFormat.Bc1RgbaUNorm)).Pixels;

        for (var y = 0; y < 4; y++) {
            var texel = (y * 5) + 4;
            var wanted = 180 + (y * 20);

            Assert.True(
                Math.Abs(back[texel * 4] - wanted) <= 4,
                $"the edge column's row {y} came back as {back[texel * 4]} rather than {wanted}"
            );
        }
    }

    [Fact]
    public void EveryLevelOfAMipChainIsCompressed() {
        var source = new TextureData(PixelFormat.Rgba8UNorm, 8, 8);
        source.PixelSpan().Fill(0x40);

        var decoded = BlockCompressor.Decode(BlockCompressor.Encode(source, PixelFormat.Bc7RgbaUNorm));

        for (var level = 0; level < source.LevelCount; level++) {
            foreach (var value in decoded.Level(level)) {
                Assert.Equal(0x40, value);
            }
        }
    }

    [Fact]
    public void EveryFaceOfACubeMapIsCompressed() {
        var source = new TextureData(PixelFormat.Rgba8UNorm, 4, 4, levelCount: 1, faceCount: 6);
        var pixels = source.PixelSpan();

        for (var face = 0; face < 6; face++) {
            pixels.Slice(face * 4 * 4 * 4, 4 * 4 * 4).Fill((byte)(face * 40));
        }

        var encoded = BlockCompressor.Encode(source, PixelFormat.Bc7RgbaUNorm);

        Assert.Equal(6 * 16, encoded.ByteLength);

        var back = BlockCompressor.Decode(encoded).Pixels;

        for (var face = 0; face < 6; face++) {
            Assert.Equal((byte)(face * 40), back[face * 4 * 4 * 4]);
        }
    }

    /// <summary>
    ///     The transfer function is applied by the hardware on the way out of the sampler, so a
    ///     source that is sRGB and a target that is not means the shader gets values that were never
    ///     converted — an image that is famously hard to diagnose by eye. Saying so at the point of
    ///     the mistake is cheaper than a colour bug report.
    /// </summary>
    [Theory]
    [InlineData(PixelFormat.Rgba8UNormSrgb, PixelFormat.Bc7RgbaUNorm)]
    [InlineData(PixelFormat.Rgba8UNorm, PixelFormat.Bc7RgbaUNormSrgb)]
    public void EncodingAcrossTheSrgbBoundaryIsRefused(PixelFormat source, PixelFormat target) {
        var failure = Assert.Throws<ArgumentException>(
            () => BlockCompressor.Encode(new(source, 4, 4, levelCount: 1), target)
        );

        Assert.Contains("sRGB", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSrgbnessOfTheSourceSurvivesTheRoundTrip() {
        var source = new TextureData(PixelFormat.Rgba8UNormSrgb, 4, 4, levelCount: 1);

        var encoded = BlockCompressor.Encode(source, PixelFormat.Bc7RgbaUNormSrgb);

        Assert.Equal(PixelFormat.Rgba8UNormSrgb, BlockCompressor.Decode(encoded).Format);
    }

    /// <summary>
    ///     ASTC and ETC2 have sizes, block extents and KTX2 numbers here, and no encoder. That is
    ///     deliberate — doc 03 says the encoder is native — so the refusal names what is missing
    ///     rather than saying the format is unknown.
    /// </summary>
    [Theory]
    [InlineData(PixelFormat.Astc4X4UNorm)]
    [InlineData(PixelFormat.Astc8X8UNorm)]
    [InlineData(PixelFormat.Etc2Rgba8UNorm)]
    public void TheFormatsThatNeedANativeEncoderSayThat(PixelFormat target) {
        Assert.False(BlockCompressor.CanEncode(target));

        var failure = Assert.Throws<NotSupportedException>(
            () => BlockCompressor.Encode(new(PixelFormat.Rgba8UNorm, 4, 4, levelCount: 1), target)
        );

        Assert.Contains("astcenc", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     BC6H is the one format here whose input is not bytes. Encoding it from eight-bit colour
    ///     would compile, run, and quietly crush every block into the first hundredth of the range
    ///     it can hold, so the two paths are kept apart at the type level and the mistake is named.
    /// </summary>
    [Fact]
    public void Bc6HCannotBeEncodedFromEightBitColour() {
        var failure = Assert.Throws<NotSupportedException>(
            () => BlockCompressor.Encode(new(PixelFormat.Rgba8UNorm, 4, 4, levelCount: 1), PixelFormat.Bc6HRgbUFloat)
        );

        Assert.Contains("Rgba16Float", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Bc6HDecodesToHalfFloatAndNotToBytes() {
        var source = new TextureData(PixelFormat.Rgba16Float, 4, 4, levelCount: 1);

        var decoded = BlockCompressor.Decode(BlockCompressor.Encode(source, PixelFormat.Bc6HRgbUFloat));

        Assert.Equal(PixelFormat.Rgba16Float, decoded.Format);
    }

    /// <summary>
    ///     A compressed texture is not decoded at run time, so the container has to carry it whole:
    ///     writing a BC7 texture to KTX2 and reading it back has to give the same bytes, or a build
    ///     ships blocks nothing can sample.
    /// </summary>
    [Fact]
    public void ACompressedTextureSurvivesTheContainer() {
        var source = new TextureData(PixelFormat.Rgba8UNorm, 8, 8);

        for (var texel = 0; texel < source.ByteLength / 4; texel++) {
            source.PixelSpan()[texel * 4] = (byte)texel;
            source.PixelSpan()[(texel * 4) + 3] = 255;
        }

        var encoded = BlockCompressor.Encode(source, PixelFormat.Bc7RgbaUNorm);
        var read = Ktx2.Read(Ktx2.Write(encoded));

        Assert.Equal(PixelFormat.Bc7RgbaUNorm, read.Format);
        Assert.Equal(encoded.Pixels.ToArray(), read.Pixels.ToArray());
    }
}
