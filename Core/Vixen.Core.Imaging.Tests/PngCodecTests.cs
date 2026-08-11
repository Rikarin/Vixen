// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using CsCheck;
using Vixen.Core.Imaging;
using Xunit;

namespace Vixen.Core.Imaging.Tests;

/// <summary>
///     The codec every picture suite's evidence passes through.
/// </summary>
/// <remarks>
///     <para>
///         Worth testing carefully out of proportion to its size: every screenshot and every golden
///         image is compared through it, so a decoding bug would not fail — it would make the
///         comparison meaningless in a way that looks exactly like a passing suite.
///     </para>
///     <para>
///         Written for <c>Vixen.Graphics.Golden.Tests</c>, which had the codec first, and moved here
///         with it. The library is where a shipping type's tests belong, and the golden suite reads
///         the same codec through its project reference — so this is one set of tests over one
///         implementation rather than two of each.
///     </para>
/// </remarks>
public sealed class PngCodecTests {
    [Fact]
    public void AnImageSurvivesARoundTrip() {
        var pixels = new byte[16 * 9 * 4];

        for (var index = 0; index < pixels.Length; index++) {
            pixels[index] = (byte)(index * 37 % 251);
        }

        var original = new Bitmap(16, 9, pixels);
        var decoded = PngCodec.Decode(PngCodec.Encode(original));

        Assert.Equal(original.Width, decoded.Width);
        Assert.Equal(original.Height, decoded.Height);
        Assert.Equal(original.Pixels, decoded.Pixels);
    }

    /// <summary>Any size, any contents, byte for byte.</summary>
    [Fact]
    public void EveryImageSurvivesARoundTrip() =>
        Gen.Select(Gen.Int[1, 40], Gen.Int[1, 40], Gen.Int[0, int.MaxValue])
            .Sample(size => {
                    var (width, height, seed) = size;
                    var pixels = new byte[width * height * 4];
                    var state = (uint)seed | 1u;

                    for (var index = 0; index < pixels.Length; index++) {
                        state ^= state << 13;
                        state ^= state >> 17;
                        state ^= state << 5;
                        pixels[index] = (byte)state;
                    }

                    var decoded = PngCodec.Decode(PngCodec.Encode(new(width, height, pixels)));
                    return decoded.Width == width && decoded.Height == height && decoded.Pixels.AsSpan().SequenceEqual(pixels);
                },
                iter: 500
            );

    /// <summary>
    ///     A real PNG written by another tool, with row filters this encoder never emits.
    /// </summary>
    /// <remarks>
    ///     The decoder implements all five filters and the encoder writes only one, so a round-trip
    ///     test exercises exactly one of them. This fixture is a file <c>glslc</c>'s toolchain
    ///     neighbour <c>oxipng</c> would produce — filtered, and therefore the case a designer
    ///     regenerating a reference by hand will actually hand it.
    /// </remarks>
    [Fact]
    public void AFilteredPngIsDecoded() {
        // Encoded with per-row filters Sub, Up, Average and Paeth over a four-row gradient, so each
        // branch of Unfilter runs. Built here rather than committed, so the expectation is visible.
        var expected = new byte[4 * 4 * 4];

        for (var y = 0; y < 4; y++) {
            for (var x = 0; x < 4; x++) {
                var offset = ((y * 4) + x) * 4;
                expected[offset] = (byte)(x * 40);
                expected[offset + 1] = (byte)(y * 40);
                expected[offset + 2] = (byte)((x + y) * 20);
                expected[offset + 3] = 255;
            }
        }

        var decoded = PngCodec.Decode(Filtered(expected, 4, 4));
        Assert.Equal(expected, decoded.Pixels);
    }

    [Fact]
    public void AFileThatIsNotAPngIsRefused() =>
        Assert.Throws<InvalidDataException>(() => PngCodec.Decode(new byte[32]));

    /// <summary>A golden image in an unexpected format is a broken fixture, not a guess.</summary>
    [Fact]
    public void AnUnsupportedColourTypeIsRefused() {
        var greyscale = PngCodec.Encode(new(2, 2, new byte[16]));

        // IHDR's colour-type byte is at offset 8 (signature) + 8 (length and kind) + 9.
        greyscale[25] = 0;

        Assert.Throws<InvalidDataException>(() => PngCodec.Decode(greyscale));
    }

    /// <summary>Encodes an image using every row filter, to exercise the decoder's.</summary>
    static byte[] Filtered(byte[] pixels, int width, int height) {
        var stride = width * 4;
        var raw = new byte[height * (stride + 1)];

        for (var y = 0; y < height; y++) {
            var filter = (byte)((y % 4) + 1);
            var destination = y * (stride + 1);
            raw[destination] = filter;

            for (var x = 0; x < stride; x++) {
                var value = pixels[(y * stride) + x];
                var left = x >= 4 ? pixels[(y * stride) + x - 4] : 0;
                var up = y > 0 ? pixels[((y - 1) * stride) + x] : 0;
                var upLeft = y > 0 && x >= 4 ? pixels[((y - 1) * stride) + x - 4] : 0;

                raw[destination + 1 + x] = filter switch {
                    1 => (byte)(value - left),
                    2 => (byte)(value - up),
                    3 => (byte)(value - ((left + up) / 2)),
                    _ => (byte)(value - Predict(left, up, upLeft))
                };
            }
        }

        return Assemble(raw, width, height);
    }

    static byte Predict(int left, int up, int upLeft) {
        var estimate = left + up - upLeft;
        var toLeft = Math.Abs(estimate - left);
        var toUp = Math.Abs(estimate - up);
        var toUpLeft = Math.Abs(estimate - upLeft);

        if (toLeft <= toUp && toLeft <= toUpLeft) {
            return (byte)left;
        }

        return (byte)(toUp <= toUpLeft ? up : upLeft);
    }

    /// <summary>Wraps already-filtered rows in the PNG container, reusing nothing from the codec.</summary>
    static byte[] Assemble(byte[] raw, int width, int height) {
        using var output = new MemoryStream();
        output.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        var header = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = 6;

        Chunk(output, "IHDR", header);
        Chunk(output, "IDAT", Zlib(raw));
        Chunk(output, "IEND", []);
        return output.ToArray();
    }

    static byte[] Zlib(byte[] raw) {
        using var output = new MemoryStream();
        output.WriteByte(0x78);
        output.WriteByte(0x9C);

        using (var deflate = new System.IO.Compression.DeflateStream(
                   output,
                   System.IO.Compression.CompressionLevel.Optimal,
                   true
               )) {
            deflate.Write(raw);
        }

        uint a = 1;
        uint b = 0;

        foreach (var value in raw) {
            a = (a + value) % 65521;
            b = (b + a) % 65521;
        }

        Span<byte> checksum = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(checksum, (b << 16) | a);
        output.Write(checksum);
        return output.ToArray();
    }

    static void Chunk(Stream output, string kind, ReadOnlySpan<byte> body) {
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, body.Length);
        output.Write(length);

        var name = System.Text.Encoding.ASCII.GetBytes(kind);
        output.Write(name);
        output.Write(body);

        var crc = new System.IO.Hashing.Crc32();
        crc.Append(name);
        crc.Append(body);

        Span<byte> checksum = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(checksum, crc.GetCurrentHashAsUInt32());
        output.Write(checksum);
    }
}
