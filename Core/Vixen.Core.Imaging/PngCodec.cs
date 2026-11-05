// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;
using System.Text;

namespace Vixen.Core.Imaging;

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
///         <b>Written for <c>Vixen.Graphics.Golden.Tests</c>, then owned by
///         <c>Vixen.Ui.Testing</c>, and now here.</b> Each move was the same argument one step
///         further down: a picture is written by more things than the one that needed it first, and
///         the assembly a consumer cannot reference is the assembly that forces a second encoder.
///         A UI testing library was the wrong owner for the last of those reasons — a game head that
///         wants to write its own frame to a file cannot reference a test framework to do it, and
///         <c>Platform/</c> could not reference it at all.
///     </para>
///     <para>
///         ⚠ <b>The direction of the reference is the whole reason it can be shared.</b> Everything
///         that encodes a picture — the golden suites, the UI baselines, a
///         <c>--vixen-capture</c> run — is above this assembly, and none of them is above each
///         other. Sitting under all three is what stops the count of hand-written PNG encoders in
///         this repository from going back up.
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

    /// <summary>Encodes 16-bit samples, which is what a map needing more than 256 levels is written as.</summary>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    /// <param name="channels">One for grey, three for RGB, four for RGBA.</param>
    /// <param name="samples">Interleaved, top row first, width × height × channels of them.</param>
    /// <returns>The file.</returns>
    /// <exception cref="ArgumentException">The channels or the sample count are not ones a file can hold.</exception>
    /// <remarks>
    ///     ⚠ <b>Filter type 0 on every row, for the reason <see cref="Encode" /> gives</b>, and big-endian
    ///     samples because the format is, which is the mistake a 16-bit writer makes silently: a
    ///     byte-swapped image of a smooth gradient is noise that decodes without complaint.
    /// </remarks>
    public static byte[] Encode16(int width, int height, int channels, ReadOnlySpan<ushort> samples) {
        var colourType = channels switch {
            1 => 0,
            3 => 2,
            4 => 6,
            _ => throw new ArgumentException($"{channels} channels: one, three or four are written.", nameof(channels))
        };

        if (width <= 0 || height <= 0 || samples.Length != width * height * channels) {
            throw new ArgumentException("The samples do not fill the image.", nameof(samples));
        }

        var stride = width * channels * sizeof(ushort);
        var raw = new byte[(height * stride) + height];

        for (var y = 0; y < height; y++) {
            var row = samples.Slice(y * width * channels, width * channels);
            var at = (y * (stride + 1)) + 1;

            foreach (var sample in row) {
                BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(at), sample);
                at += sizeof(ushort);
            }
        }

        using var output = new MemoryStream();
        output.Write(Signature);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 16;
        header[9] = (byte)colourType;

        WriteChunk(output, "IHDR", header);
        WriteChunk(output, "IDAT", Deflate(raw));
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    /// <summary>Encodes eight-bit grey, RGB or RGBA samples, so a one-channel map is one channel in the file.</summary>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="channels">One for grey, three for RGB, four for RGBA.</param>
    /// <param name="samples">Interleaved samples, row-major, top row first.</param>
    /// <returns>The file's bytes.</returns>
    /// <exception cref="ArgumentException">The channel count is not one of those, or the samples do not fill the image.</exception>
    /// <remarks>
    ///     <see cref="Decode16" /> reads these back. <see cref="Decode" /> does not: it is the
    ///     eight-bit RGBA reader the golden-image suites use, and it refuses anything else.
    /// </remarks>
    public static byte[] Encode8(int width, int height, int channels, ReadOnlySpan<byte> samples) {
        var colourType = channels switch {
            1 => 0,
            3 => 2,
            4 => 6,
            _ => throw new ArgumentException($"{channels} channels: one, three or four are written.", nameof(channels))
        };

        if (width <= 0 || height <= 0 || samples.Length != width * height * channels) {
            throw new ArgumentException("The samples do not fill the image.", nameof(samples));
        }

        var stride = width * channels;
        var raw = new byte[(height * stride) + height];

        for (var y = 0; y < height; y++) {
            samples.Slice(y * stride, stride).CopyTo(raw.AsSpan((y * (stride + 1)) + 1));
        }

        using var output = new MemoryStream();
        output.Write(Signature);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = (byte)colourType;

        WriteChunk(output, "IHDR", header);
        WriteChunk(output, "IDAT", Deflate(raw));
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    /// <summary>Decodes what <see cref="Encode16" /> and <see cref="Encode" /> write, keeping its depth.</summary>
    /// <param name="data">The file.</param>
    /// <returns>The picture as 16-bit samples: an 8-bit file's levels scaled to fill the range.</returns>
    /// <exception cref="InvalidDataException">It is palette, interlaced, or of another depth.</exception>
    public static (int Width, int Height, int Channels, ushort[] Samples) Decode16(ReadOnlySpan<byte> data) {
        if (data.Length < 8 || !data[..8].SequenceEqual(Signature)) {
            throw new InvalidDataException("Not a PNG: the signature does not match.");
        }

        var offset = 8;
        var (width, height, depth, channels) = (0, 0, 0, 0);
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
                    depth = body[8];
                    channels = body[9] switch {
                        0 => 1,
                        2 => 3,
                        4 => 2,
                        6 => 4,
                        _ => throw new InvalidDataException("Palette PNGs are not read.")
                    };

                    if (depth is not (8 or 16) || body[12] != 0) {
                        throw new InvalidDataException($"A PNG of depth {depth}, or interlaced, is not read.");
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

        var bytes = depth / 8;
        var stride = width * channels * bytes;
        var pixels = Reverse(Inflate(compressed.ToArray(), (height * stride) + height), stride, height, channels * bytes);
        var samples = new ushort[width * height * channels];

        for (var index = 0; index < samples.Length; index++) {
            samples[index] = bytes == 2
                ? BinaryPrimitives.ReadUInt16BigEndian(pixels.AsSpan(index * 2))
                : (ushort)(pixels[index] * 257);
        }

        return (width, height, channels, samples);
    }

    /// <summary>Undoes the row filters of a picture of any pixel size.</summary>
    static byte[] Reverse(byte[] raw, int stride, int height, int pixel) {
        var pixels = new byte[height * stride];

        for (var y = 0; y < height; y++) {
            var filter = raw[y * (stride + 1)];
            var source = (y * (stride + 1)) + 1;
            var destination = y * stride;

            for (var x = 0; x < stride; x++) {
                var value = raw[source + x];
                var left = x >= pixel ? pixels[destination + x - pixel] : 0;
                var up = y > 0 ? pixels[destination - stride + x] : 0;
                var upLeft = y > 0 && x >= pixel ? pixels[destination - stride + x - pixel] : 0;

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
