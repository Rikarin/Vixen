// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;
using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Platform.Linux.Tests;

/// <summary>
///     The clipboard's image format, tested on a machine with no clipboard — and no Linux.
/// </summary>
public class PngImageTests {
    [Fact]
    public void ARoundTripPreservesEveryChannel() {
        var pixels = new byte[] {
            255, 0, 0, 255, 0, 255, 0, 128, 0, 0, 255, 64,
            10, 20, 30, 40, 200, 100, 50, 255, 0, 0, 0, 0
        };

        var encoded = PngImage.Encode(new(pixels, new(3, 2)));

        Assert.NotNull(encoded);
        Assert.True(PngImage.TryDecode(encoded, out var decoded));
        Assert.Equal(new Int2(3, 2), decoded.Size);
        Assert.Equal(pixels, decoded.Pixels.ToArray());
    }

    [Fact]
    public void TheEncoderWritesTheSignatureAndAnEndChunk() {
        var encoded = PngImage.Encode(new(new byte[4], new(1, 1)));

        Assert.NotNull(encoded);
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A], encoded[..8]);
        Assert.Equal("IEND"u8.ToArray(), encoded[^8..^4]);
    }

    /// <summary>
    ///     Every filter a real encoder emits, decoded against a picture whose bytes are known. A
    ///     Paeth predictor whose tie-breaking is wrong produces an image that is subtly and
    ///     progressively wrong towards the bottom right, which is the kind of thing that survives a
    ///     round-trip test against one's own encoder.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void EveryFilterTypeDecodesToTheSamePicture(byte filter) {
        var pixels = new byte[4 * 4 * 4];

        for (var index = 0; index < pixels.Length; index++) {
            pixels[index] = (byte)(index * 7 % 251);
        }

        var encoded = Encode(pixels, 4, 4, filter);

        Assert.True(PngImage.TryDecode(encoded, out var decoded));
        Assert.Equal(pixels, decoded.Pixels.ToArray());
    }

    /// <summary>A greyscale screenshot is still a picture, and has an alpha channel of its own.</summary>
    [Fact]
    public void GreyscaleBecomesOpaqueRgba() {
        var encoded = Encode([16, 240], 2, 1, 0, colourType: 0);

        Assert.True(PngImage.TryDecode(encoded, out var image));
        Assert.Equal([16, 16, 16, 255, 240, 240, 240, 255], image.Pixels.ToArray());
    }

    [Fact]
    public void TruecolourWithoutAlphaBecomesOpaqueRgba() {
        var encoded = Encode([1, 2, 3, 4, 5, 6], 2, 1, 0, colourType: 2);

        Assert.True(PngImage.TryDecode(encoded, out var image));
        Assert.Equal([1, 2, 3, 255, 4, 5, 6, 255], image.Pixels.ToArray());
    }

    [Fact]
    public void SomethingThatIsNotAPngIsRefused() {
        Assert.False(PngImage.TryDecode([], out _));
        Assert.False(PngImage.TryDecode("GIF89a"u8, out _));
        Assert.False(PngImage.TryDecode(new byte[64], out _));
    }

    [Fact]
    public void SixteenBitSamplesAreRefusedRatherThanMisread() {
        var encoded = Encode(new byte[16], 2, 1, 0, colourType: 6, bitDepth: 16);

        Assert.False(PngImage.TryDecode(encoded, out _));
    }

    [Fact]
    public void AnInterlacedImageIsRefusedRatherThanMisread() {
        var encoded = Encode(new byte[16], 2, 2, 0, interlace: 1);

        Assert.False(PngImage.TryDecode(encoded, out _));
    }

    [Fact]
    public void TruncatedPixelDataIsRefused() {
        var encoded = Encode(new byte[16], 2, 2, 0);
        Assert.False(PngImage.TryDecode(encoded.AsSpan(0, encoded.Length - 20), out _));
    }

    [Fact]
    public void AnImageSmallerThanItsSizeIsNotEncoded() =>
        Assert.Null(PngImage.Encode(new(new byte[4], new(4, 4))));

    /// <summary>
    ///     A PNG built here rather than by <see cref="PngImage.Encode" />, so that the decoder is
    ///     tested against something other than its own output.
    /// </summary>
    static byte[] Encode(
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        byte filter,
        byte colourType = 6,
        byte bitDepth = 8,
        byte interlace = 0
    ) {
        var channels = colourType switch { 0 => 1, 2 => 3, 4 => 2, _ => 4 };
        var stride = width * channels * (bitDepth / 8);
        var raw = new byte[(stride + 1) * height];
        var previous = new byte[stride];

        for (var y = 0; y < height; y++) {
            var row = pixels.Slice(y * stride, stride);
            raw[y * (stride + 1)] = filter;

            for (var index = 0; index < stride; index++) {
                var left = index >= channels ? row[index - channels] : (byte)0;
                var above = previous[index];
                var upperLeft = index >= channels ? previous[index - channels] : (byte)0;

                raw[y * (stride + 1) + 1 + index] = filter switch {
                    1 => (byte)(row[index] - left),
                    2 => (byte)(row[index] - above),
                    3 => (byte)(row[index] - (left + above) / 2),
                    4 => (byte)(row[index] - Paeth(left, above, upperLeft)),
                    _ => row[index]
                };
            }

            row.CopyTo(previous);
        }

        using var output = new MemoryStream();
        output.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = bitDepth;
        header[9] = colourType;
        header[12] = interlace;

        Chunk(output, "IHDR"u8, header);

        using var deflated = new MemoryStream();

        using (var deflate = new ZLibStream(deflated, CompressionLevel.Fastest, leaveOpen: true)) {
            deflate.Write(raw);
        }

        Chunk(output, "IDAT"u8, deflated.ToArray());
        Chunk(output, "IEND"u8, []);

        return output.ToArray();
    }

    static void Chunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> payload) {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, payload.Length);
        output.Write(length);
        output.Write(type);
        output.Write(payload);

        var crc = new Crc32();
        crc.Append(type);
        crc.Append(payload);

        Span<byte> checksum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, crc.GetCurrentHashAsUInt32());
        output.Write(checksum);
    }

    static byte Paeth(byte left, byte above, byte upperLeft) {
        var estimate = left + above - upperLeft;
        var fromLeft = Math.Abs(estimate - left);
        var fromAbove = Math.Abs(estimate - above);
        var fromUpperLeft = Math.Abs(estimate - upperLeft);

        if (fromLeft <= fromAbove && fromLeft <= fromUpperLeft) {
            return left;
        }

        return fromAbove <= fromUpperLeft ? above : upperLeft;
    }
}
