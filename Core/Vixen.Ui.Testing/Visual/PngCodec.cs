// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;
using System.Text;

namespace Vixen.Ui.Testing.Visual;

/// <summary>PNG, encoded and decoded, in about two hundred lines and with no imaging dependency.</summary>
/// <remarks>
///     <para>
///         A reference picture nobody can open is a reference picture nobody will look at, and
///         looking at it is the entire workflow when one fails. So baselines are PNGs rather than a
///         raw blob: viewable in any file browser, diffable by eye, and small in git.
///     </para>
///     <para>
///         Written by hand rather than taken from a package. ImageSharp is in the dependency register
///         but ADR-015 confines it to editor and tooling assemblies for licence reasons, and this
///         assembly ships to game developers. PNG's baseline — one colour type, one bit depth, one
///         interlace mode — is a couple of hundred lines, and the compression is
///         <c>System.IO.Compression</c>'s.
///     </para>
///     <para>
///         <b>Written for <c>Vixen.Graphics.Golden.Tests</c> and moved here</b>, which is why a UI
///         testing library owns the tree's PNG codec. That suite needed one first; this one needed
///         the same thing for its screenshots, and two hand-rolled PNG encoders is one too many.
///     </para>
///     <para>
///         ⚠ <b>The direction of the reference is the whole reason it could be consolidated at all.</b>
///         A shipping library can be referenced by a test project and a test project can be
///         referenced by nothing, so the copy that ships is the only one that can be the shared one —
///         and it is also the copy under a documentation and API-compatibility obligation, which is
///         the right one to make load bearing.
///     </para>
/// </remarks>
public static class PngCodec {
    static ReadOnlySpan<byte> Signature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Encodes a picture.</summary>
    /// <param name="image">What to encode.</param>
    /// <returns>The file's bytes.</returns>
    public static byte[] Encode(in Bitmap image) {
        var raw = new byte[(image.Height * image.Width * 4) + image.Height];

        for (var y = 0; y < image.Height; y++) {
            // Filter type 0 (None) on every row. Filtering would compress better and would also mean
            // the encoder had to choose one per row, which is a heuristic with no right answer and an
            // obvious way to make two runs produce different bytes for the same picture.
            var destination = y * ((image.Width * 4) + 1);
            raw[destination] = 0;
            Array.Copy(image.Pixels, y * image.Width * 4, raw, destination + 1, image.Width * 4);
        }

        using var output = new MemoryStream();
        output.Write(Signature);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, image.Width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), image.Height);
        header[8] = 8;
        header[9] = 6;

        WriteChunk(output, "IHDR", header);
        WriteChunk(output, "IDAT", Deflate(raw));
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    /// <summary>Decodes a picture.</summary>
    /// <param name="data">The file's bytes.</param>
    /// <returns>The picture.</returns>
    /// <exception cref="InvalidDataException">It is not a baseline 8-bit RGBA PNG.</exception>
    public static Bitmap Decode(ReadOnlySpan<byte> data) {
        if (data.Length < 8 || !data[..8].SequenceEqual(Signature)) {
            throw new InvalidDataException("Not a PNG: the signature does not match.");
        }

        var offset = 8;
        var width = 0;
        var height = 0;
        using var compressed = new MemoryStream();

        while (offset + 8 <= data.Length) {
            var length = BinaryPrimitives.ReadInt32BigEndian(data[offset..]);
            var kind = Encoding.ASCII.GetString(data.Slice(offset + 4, 4));
            var body = data.Slice(offset + 8, length);
            offset += 12 + length;

            switch (kind) {
                case "IHDR":
                    width = BinaryPrimitives.ReadInt32BigEndian(body);
                    height = BinaryPrimitives.ReadInt32BigEndian(body[4..]);

                    if (body[8] != 8 || body[9] != 6) {
                        throw new InvalidDataException(
                            $"Only 8-bit RGBA PNGs are read; this one is bit depth {body[8]}, colour "
                            + $"type {body[9]}. A baseline in an unexpected format is a broken "
                            + "fixture, not something to guess about."
                        );
                    }

                    if (body[12] != 0) {
                        throw new InvalidDataException("Interlaced PNGs are not read.");
                    }

                    break;

                case "IDAT":
                    compressed.Write(body);
                    break;

                case "IEND":
                    offset = data.Length;
                    break;
            }
        }

        if (width <= 0 || height <= 0) {
            throw new InvalidDataException("The PNG has no IHDR, or names an empty image.");
        }

        var raw = Inflate(compressed.ToArray(), (height * width * 4) + height);
        return new(width, height, Unfilter(raw, width, height));
    }

    /// <summary>Reads a picture from disk.</summary>
    /// <param name="path">Where it is.</param>
    /// <returns>The picture.</returns>
    public static Bitmap Load(string path) => Decode(File.ReadAllBytes(path));

    /// <summary>Writes a picture to disk, creating the directory if it is not there.</summary>
    /// <param name="path">Where to put it.</param>
    /// <param name="image">What to write.</param>
    public static void Save(string path, in Bitmap image) {
        ArgumentNullException.ThrowIfNull(path);

        if (Path.GetDirectoryName(path) is { Length: > 0 } directory) {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(path, Encode(image));
    }

    /// <summary>Undoes the per-row filters, which is the only interesting part of decoding.</summary>
    /// <remarks>
    ///     Five filter types, each predicting a byte from its left, upper and upper-left neighbours.
    ///     They are implemented in full even though this encoder only ever writes type 0: a reference
    ///     regenerated by a designer's tool will use all five, and a decoder that handled only what
    ///     its own encoder produced would reject exactly the files a human is most likely to hand it.
    /// </remarks>
    static byte[] Unfilter(byte[] raw, int width, int height) {
        var stride = width * 4;
        var pixels = new byte[height * stride];

        for (var y = 0; y < height; y++) {
            var filter = raw[y * (stride + 1)];
            var source = (y * (stride + 1)) + 1;
            var destination = y * stride;

            for (var x = 0; x < stride; x++) {
                var value = raw[source + x];
                var left = x >= 4 ? pixels[destination + x - 4] : 0;
                var up = y > 0 ? pixels[destination - stride + x] : 0;
                var upLeft = y > 0 && x >= 4 ? pixels[destination - stride + x - 4] : 0;

                pixels[destination + x] = filter switch {
                    0 => value,
                    1 => (byte)(value + left),
                    2 => (byte)(value + up),
                    3 => (byte)(value + ((left + up) / 2)),
                    4 => (byte)(value + Paeth(left, up, upLeft)),
                    _ => throw new InvalidDataException($"Unknown PNG row filter {filter}.")
                };
            }
        }

        return pixels;
    }

    /// <summary>The Paeth predictor: whichever neighbour the gradient points at.</summary>
    static byte Paeth(int left, int up, int upLeft) {
        var estimate = left + up - upLeft;
        var toLeft = Math.Abs(estimate - left);
        var toUp = Math.Abs(estimate - up);
        var toUpLeft = Math.Abs(estimate - upLeft);

        if (toLeft <= toUp && toLeft <= toUpLeft) {
            return (byte)left;
        }

        return (byte)(toUp <= toUpLeft ? up : upLeft);
    }

    /// <summary>Wraps deflate in the zlib framing PNG's IDAT requires.</summary>
    static byte[] Deflate(byte[] raw) {
        using var output = new MemoryStream();

        // zlib header: deflate, 32K window, default level, no dictionary. The two bytes have to be a
        // multiple of 31 read big-endian, which 0x78 0x9C is.
        output.WriteByte(0x78);
        output.WriteByte(0x9C);

        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, true)) {
            deflate.Write(raw);
        }

        Span<byte> checksum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, Adler32(raw));
        output.Write(checksum);
        return output.ToArray();
    }

    static byte[] Inflate(byte[] compressed, int expected) {
        // Skip the two-byte zlib header; DeflateStream reads the raw stream beneath it.
        using var input = new MemoryStream(compressed, 2, compressed.Length - 2);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        var raw = new byte[expected];
        deflate.ReadExactly(raw);
        return raw;
    }

    static uint Adler32(ReadOnlySpan<byte> data) {
        uint a = 1;
        uint b = 0;

        foreach (var value in data) {
            a = (a + value) % 65521;
            b = (b + a) % 65521;
        }

        return (b << 16) | a;
    }

    static void WriteChunk(Stream output, string kind, ReadOnlySpan<byte> body) {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, body.Length);
        output.Write(length);

        var name = Encoding.ASCII.GetBytes(kind);
        output.Write(name);
        output.Write(body);

        var crc = new Crc32();
        crc.Append(name);
        crc.Append(body);

        Span<byte> checksum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, crc.GetCurrentHashAsUInt32());
        output.Write(checksum);
    }
}
