// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Graphics;
using Xunit;

namespace Vixen.Interchange.Tests;

/// <summary>
///     A TIFF read as any other picture is: through <see cref="ImageDecoders.BuiltIn" />, at eight bits when the file
///     holds eight and in float when it holds more.
/// </summary>
public sealed class TiffDecoderTests {
    static IImageDecoder Decoder(string extension) =>
        ImageDecoders.For(ImageDecoders.BuiltIn, extension) ?? throw new Xunit.Sdk.XunitException("no decoder");

    [Theory]
    [InlineData(".tif")]
    [InlineData(".TIFF")]
    public void TheBuiltInSetReadsBothExtensions(string extension) => Assert.IsType<TiffDecoder>(Decoder(extension));

    [Fact]
    public void EightBitColourStaysEightBitsWithItsAlpha() {
        byte[] samples = [10, 20, 30, 40, 250, 240, 230, 220];

        var texture = Decoder(".tif").Decode(new MemoryStream(TiffWriter.Encode8(2, 1, 4, samples)), ".tif");

        Assert.Equal((PixelFormat.Rgba8UNorm, 2, 1), (texture.Format, texture.Width, texture.Height));
        Assert.Equal(samples, texture.PixelSpan().ToArray());
    }

    [Fact]
    public void EightBitGreySpreadsToColourAndIsOpaque() {
        var texture = Decoder(".tif").Decode(new MemoryStream(TiffWriter.Encode8(2, 1, 1, [7, 200])), ".tif");

        Assert.Equal(new byte[] { 7, 7, 7, 255, 200, 200, 200, 255 }, texture.PixelSpan().ToArray());
    }

    /// <summary>Sixteen bits keep their range: two neighbouring levels a byte would merge stay apart.</summary>
    [Fact]
    public void SixteenBitsBecomeFloatAndKeepWhatAByteWouldLose() {
        ushort[] samples = [32768, 32769];

        var texture = Decoder(".tiff").Decode(new MemoryStream(TiffWriter.Encode16(2, 1, 1, samples)), ".tiff");

        Assert.Equal(PixelFormat.Rgba32Float, texture.Format);
        var floats = MemoryMarshal.Cast<byte, float>(texture.PixelSpan()).ToArray();
        Assert.Equal(32768 / 65535f, floats[0]);
        Assert.Equal(32769 / 65535f, floats[4]);
        Assert.Equal(1f, floats[3]);
    }

    [Fact]
    public void TheDepthIsReadFromTheHeader() {
        Assert.Equal(8, TiffReader.BitsPerSampleOf(TiffWriter.Encode8(1, 1, 1, [0])));
        Assert.Equal(16, TiffReader.BitsPerSampleOf(TiffWriter.Encode16(1, 1, 1, [0])));
        Assert.Equal(32, TiffReader.BitsPerSampleOf(TiffWriter.EncodeFloat(new(1, 1, 1, [0f]))));
    }
}
