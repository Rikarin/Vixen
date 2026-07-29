// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Platform.Windows.Tests;

/// <summary>
///     The clipboard's bitmap format, tested on a machine with no clipboard — and no Windows. This
///     is the half of clipboard images that can be wrong, which is why it is the half that is a
///     pure function.
/// </summary>
public class DibImageTests {
    [Fact]
    public void ARoundTripPreservesEveryChannel() {
        var pixels = new byte[] {
            255, 0, 0, 255, 0, 255, 0, 128,
            0, 0, 255, 64, 10, 20, 30, 40
        };

        var image = new ClipboardImage(pixels, new(2, 2));
        var dib = DibImage.Encode(image);

        Assert.NotNull(dib);
        Assert.True(DibImage.TryDecode(dib, out var decoded));
        Assert.Equal(new Int2(2, 2), decoded.Size);
        Assert.Equal(pixels, decoded.Pixels.ToArray());
    }

    /// <summary>
    ///     A bottom-up bitmap is the common case and the one an off-by-one in the row arithmetic
    ///     shows up in, so the encoder's output is checked to be bottom-up rather than only to
    ///     survive its own decoder.
    /// </summary>
    [Fact]
    public void TheEncoderWritesBottomUpBgra() {
        var pixels = new byte[] { 1, 2, 3, 255, 9, 8, 7, 255 };
        var dib = DibImage.Encode(new(pixels, new(1, 2)));

        Assert.NotNull(dib);
        Assert.Equal(124, BinaryPrimitives.ReadInt32LittleEndian(dib));
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(8)));

        // The last row of the image is the first row of the file, and its bytes are BGRA.
        Assert.Equal(7, dib[124]);
        Assert.Equal(8, dib[125]);
        Assert.Equal(9, dib[126]);
    }

    [Fact]
    public void ATopDownBitmapIsNotFlippedTwice() {
        var topDown = Header(2, -1, 32, bitfields: false);
        Write(topDown, 40, [10, 20, 30, 255, 40, 50, 60, 255]);

        Assert.True(DibImage.TryDecode(topDown, out var image));
        Assert.Equal([30, 20, 10, 255, 60, 50, 40, 255], image.Pixels.ToArray());
    }

    /// <summary>
    ///     The reading that makes both kinds of producer work: an application that filled in the
    ///     fourth byte and one that left it alone write the same bytes and mean opposite things, and
    ///     an all-zero alpha plane is only ever the second.
    /// </summary>
    [Fact]
    public void AnAllZeroAlphaPlaneIsReadAsOpaque() {
        var dib = Header(1, 1, 32, bitfields: false);
        Write(dib, 40, [1, 2, 3, 0]);

        Assert.True(DibImage.TryDecode(dib, out var image));
        Assert.Equal(255, image.Pixels.Span[3]);
    }

    [Fact]
    public void OneTransparentPixelAmongOpaqueOnesIsKept() {
        var dib = Header(2, 1, 32, bitfields: false);
        Write(dib, 40, [1, 2, 3, 0, 4, 5, 6, 255]);

        Assert.True(DibImage.TryDecode(dib, out var image));
        Assert.Equal(0, image.Pixels.Span[3]);
        Assert.Equal(255, image.Pixels.Span[7]);
    }

    /// <summary>Twenty-four bits per pixel has no alpha channel and is not transparent.</summary>
    [Fact]
    public void TwentyFourBitRowsArePaddedToFourBytes() {
        var dib = Header(1, 2, 24, bitfields: false);

        // One pixel is three bytes; the row is padded to four. Bottom-up, so the second row of the
        // file is the first row of the image.
        Write(dib, 40, [7, 8, 9, 0, 1, 2, 3, 0]);

        Assert.True(DibImage.TryDecode(dib, out var image));
        Assert.Equal([3, 2, 1, 255, 9, 8, 7, 255], image.Pixels.ToArray());
    }

    /// <summary>
    ///     Sixteen-bit channels have to be scaled rather than shifted: a five-bit maximum of 31
    ///     multiplied by 8 is 248, which is a white that is visibly not white.
    /// </summary>
    [Fact]
    public void FiveBitChannelsScaleToFullRange() {
        var dib = Header(1, 1, 16, bitfields: true);
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(40), 0xF800);
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(44), 0x07E0);
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(48), 0x001F);
        Write(dib, 52, [0xFF, 0xFF, 0, 0]);

        Assert.True(DibImage.TryDecode(dib, out var image));
        Assert.Equal([255, 255, 255, 255], image.Pixels.ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(39)]
    public void TooShortToBeAHeaderIsRefused(int length) =>
        Assert.False(DibImage.TryDecode(new byte[length], out _));

    [Fact]
    public void APalettisedBitmapIsRefusedRatherThanMisread() {
        var dib = Header(1, 1, 8, bitfields: false);
        Assert.False(DibImage.TryDecode(dib, out _));
    }

    [Fact]
    public void AHeaderThatPromisesMorePixelsThanItCarriesIsRefused() {
        // A megapixel of header and four pixels of bitmap, which is what a truncated clipboard read
        // looks like.
        var dib = Bare(1024, 1024, 32);
        Assert.False(DibImage.TryDecode(dib, out _));
    }

    /// <summary>An implausible size is refused before it is multiplied out into an allocation.</summary>
    [Fact]
    public void AnAbsurdSizeIsRefusedBeforeItIsAllocated() {
        Assert.False(DibImage.TryDecode(Bare(int.MaxValue, int.MaxValue, 32), out _));
        Assert.False(DibImage.TryDecode(Bare(65536, 1, 32), out _));

        // int.MinValue has no positive counterpart, so negating it to get a top-down height is the
        // one input where the arithmetic itself is the bug.
        Assert.False(DibImage.TryDecode(Bare(4, int.MinValue, 32), out _));
    }

    [Fact]
    public void AnImageSmallerThanItsSizeIsNotEncoded() =>
        Assert.Null(DibImage.Encode(new(new byte[4], new(4, 4))));

    /// <summary>A V3 <c>BITMAPINFOHEADER</c> with room for pixels and, when asked for, its masks.</summary>
    static byte[] Header(int width, int height, ushort bits, bool bitfields) {
        var stride = ((width * bits + 31) / 32) * 4;
        var masks = bitfields ? 12 : 0;
        var dib = new byte[40 + masks + stride * Math.Abs(height)];

        BinaryPrimitives.WriteInt32LittleEndian(dib, 40);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), width);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), height);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(14), bits);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(16), bitfields ? 3 : 0);

        return dib;
    }

    static void Write(byte[] dib, int offset, ReadOnlySpan<byte> pixels) => pixels.CopyTo(dib.AsSpan(offset));

    /// <summary>A header with nothing behind it, for the sizes that must not be believed.</summary>
    static byte[] Bare(int width, int height, ushort bits) {
        var dib = new byte[40];

        BinaryPrimitives.WriteInt32LittleEndian(dib, 40);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), width);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), height);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(14), bits);

        return dib;
    }
}
