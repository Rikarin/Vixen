// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Graphics.Tests;

public class PixelFormatTests {
    /// <summary>
    ///     A format the table forgets reports a block size of zero, which silently makes every size
    ///     calculation involving it come out as nothing. Better to find the gap here than as a
    ///     texture that uploads no bytes.
    /// </summary>
    [Fact]
    public void EveryFormatHasASize() {
        foreach (var format in Enum.GetValues<PixelFormat>()) {
            if (format == PixelFormat.Undefined) {
                continue;
            }

            Assert.True(format.BlockSize() > 0, $"{format} has no block size.");
        }
    }

    [Theory]
    [InlineData(PixelFormat.R8UNorm, 1)]
    [InlineData(PixelFormat.Rg8UNorm, 2)]
    [InlineData(PixelFormat.Rgba8UNorm, 4)]
    [InlineData(PixelFormat.Rgba16Float, 8)]
    [InlineData(PixelFormat.Rgba32Float, 16)]
    [InlineData(PixelFormat.Depth32Float, 4)]
    public void UncompressedFormatsAreOneTexelPerBlock(PixelFormat format, int size) {
        Assert.Equal(size, format.BlockSize());
        Assert.Equal((1, 1), format.BlockExtent());
        Assert.False(format.IsCompressed());
    }

    /// <summary>
    ///     BC1 is half a byte per pixel and BC7 is one — as 4×4 blocks of 8 and 16 bytes. Getting
    ///     this wrong is the bug where a compressed texture uploads as a quarter of an image.
    /// </summary>
    [Theory]
    [InlineData(PixelFormat.Bc1RgbaUNorm, 8, 4, 4)]
    [InlineData(PixelFormat.Bc7RgbaUNorm, 16, 4, 4)]
    [InlineData(PixelFormat.Astc4X4UNorm, 16, 4, 4)]
    [InlineData(PixelFormat.Astc8X8UNorm, 16, 8, 8)]
    public void CompressedFormatsAreBlocks(PixelFormat format, int size, int width, int height) {
        Assert.Equal(size, format.BlockSize());
        Assert.Equal((width, height), format.BlockExtent());
        Assert.True(format.IsCompressed());
    }

    /// <summary>
    ///     The step everyone forgets: a 5×5 BC7 mip is two blocks by two blocks, not 25 pixels'
    ///     worth. Treating it as the latter truncates the bottom-right of every texture whose size
    ///     is not a multiple of four.
    /// </summary>
    [Fact]
    public void ACompressedLevelRoundsUpToWholeBlocks() {
        Assert.Equal(16, PixelFormat.Bc7RgbaUNorm.LevelSize(4, 4));
        Assert.Equal(64, PixelFormat.Bc7RgbaUNorm.LevelSize(5, 5));
        Assert.Equal(16, PixelFormat.Bc7RgbaUNorm.LevelSize(1, 1));
    }

    [Fact]
    public void AnUncompressedLevelIsWidthTimesHeightTimesTheTexelSize() {
        Assert.Equal(256L * 256 * 4, PixelFormat.Rgba8UNorm.LevelSize(256, 256));
        Assert.Equal(16L * 16 * 8 * 8, PixelFormat.Rgba16Float.LevelSize(16, 16, 8));
    }

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(2, 2, 2)]
    [InlineData(256, 256, 9)]
    [InlineData(1024, 1, 11)]
    [InlineData(640, 480, 10)]
    public void AMipChainRunsDownToOneTexel(int width, int height, int expected) =>
        Assert.Equal(expected, PixelFormats.MipLevelCount(width, height));

    /// <summary>
    ///     Colour textures must be sRGB and normal, roughness and mask textures must not — the whole
    ///     of colour correctness in one flag, and the pairing has to be exact in both directions or
    ///     a round trip through the content pipeline changes what a texture means.
    /// </summary>
    [Fact]
    public void SrgbAndLinearAreExactInverses() {
        foreach (var format in Enum.GetValues<PixelFormat>()) {
            var srgb = format.ToSrgb();
            var linear = format.ToLinear();

            Assert.Equal(format.IsSrgb(), format == srgb && format.IsSrgb());
            Assert.False(linear.IsSrgb());
            Assert.Equal(format.ToLinear(), srgb.ToLinear());
            Assert.Equal(format.ToSrgb(), linear.ToSrgb());
        }
    }

    [Fact]
    public void EverySrgbFormatHasALinearTwinAndTheOtherWayRound() {
        foreach (var format in Enum.GetValues<PixelFormat>()) {
            if (format.IsSrgb()) {
                Assert.NotEqual(format, format.ToLinear());
                Assert.Equal(format, format.ToLinear().ToSrgb());
            }
        }
    }

    /// <summary>
    ///     A depth format is not a colour format, and confusing them puts an attachment in the wrong
    ///     slot of a render pass.
    /// </summary>
    [Fact]
    public void DepthAndStencilAreRecognised() {
        Assert.True(PixelFormat.Depth32Float.HasDepth());
        Assert.False(PixelFormat.Depth32Float.HasStencil());
        Assert.True(PixelFormat.Depth24UNormStencil8.HasStencil());
        Assert.True(PixelFormat.Depth32FloatStencil8.IsDepthStencil());
        Assert.False(PixelFormat.Rgba8UNorm.IsDepthStencil());
    }

    [Fact]
    public void ASrgbFormatIsNeverADepthFormat() {
        foreach (var format in Enum.GetValues<PixelFormat>()) {
            Assert.False(format.IsSrgb() && format.IsDepthStencil());
        }
    }

    /// <summary>
    ///     What a layout has to declare about a texture it has not been given yet. Depth is the case
    ///     that matters — a shadow map read through a binding that says "float" is refused on WebGPU.
    /// </summary>
    [Theory]
    [InlineData(PixelFormat.Rgba8UNorm, DescriptorSampleType.Float)]
    [InlineData(PixelFormat.Rgba32Float, DescriptorSampleType.Float)]
    [InlineData(PixelFormat.Depth32Float, DescriptorSampleType.Depth)]
    [InlineData(PixelFormat.Depth24UNormStencil8, DescriptorSampleType.Depth)]
    [InlineData(PixelFormat.R32UInt, DescriptorSampleType.UInt)]
    [InlineData(PixelFormat.R8SInt, DescriptorSampleType.SInt)]
    public void AFormatKnowsHowItIsSampled(PixelFormat format, DescriptorSampleType expected) {
        Assert.Equal(expected, format.SampleTypeOf());
        Assert.True(expected.Accepts(format));
    }

    /// <summary>
    ///     Filterability is the device's answer, not the format's, so both float declarations accept
    ///     the same textures — and neither accepts a depth or integer one.
    /// </summary>
    [Fact]
    public void FloatAndUnfilterableFloatAcceptTheSameFormats() {
        foreach (var format in Enum.GetValues<PixelFormat>()) {
            Assert.Equal(
                DescriptorSampleType.Float.Accepts(format),
                DescriptorSampleType.UnfilterableFloat.Accepts(format)
            );
        }

        Assert.False(DescriptorSampleType.Float.Accepts(PixelFormat.Depth32Float));
        Assert.False(DescriptorSampleType.Float.Accepts(PixelFormat.R32UInt));
        Assert.False(DescriptorSampleType.Depth.Accepts(PixelFormat.Rgba8UNorm));
        Assert.False(DescriptorSampleType.UInt.Accepts(PixelFormat.R8SInt));
    }

    [Fact]
    public void AZeroOrNegativeExtentIsRejected() {
        Assert.Throws<ArgumentOutOfRangeException>(() => PixelFormat.Rgba8UNorm.LevelSize(0, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => PixelFormat.Rgba8UNorm.LevelSize(4, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => PixelFormats.MipLevelCount(0, 4));
    }
}
