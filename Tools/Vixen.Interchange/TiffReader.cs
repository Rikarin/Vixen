// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.IO.Compression;
using Vixen.Core.Imaging;

namespace Vixen.Interchange;

/// <summary>
///     Baseline TIFF, read: the first image of a file, grey or colour, with or without alpha, at 8 or 16 bits or in
///     32-bit float, chunky, in strips, uncompressed or Deflate, with or without the horizontal predictor, either byte
///     order. It reads what <see cref="TiffWriter" /> writes and what most painting programs save; tiles, planar pixels,
///     LZW, JPEG-in-TIFF, palettes and CMYK are refused with what they are.
/// </summary>
/// <remarks>
///     ⚠ Alpha is returned as stored. A file that marks it associated (ExtraSamples = 1) holds colour multiplied by
///     alpha, and this does not divide it out; it says nothing either, since an engine's texture wants the numbers.
/// </remarks>
public static class TiffReader {
    const ushort Width = 256;
    const ushort Height = 257;
    const ushort BitsPerSample = 258;
    const ushort Compression = 259;
    const ushort Photometric = 262;
    const ushort StripOffsets = 273;
    const ushort SamplesPerPixel = 277;
    const ushort RowsPerStrip = 278;
    const ushort StripByteCounts = 279;
    const ushort PlanarConfiguration = 284;
    const ushort Predictor = 317;
    const ushort TileWidth = 322;
    const ushort SampleFormat = 339;

    /// <summary>Decodes a TIFF to numbers: integers as 0 to 1, floats as they are, top row first.</summary>
    /// <param name="data">The file.</param>
    /// <returns>The picture, with as many channels as the file has samples per pixel.</returns>
    /// <exception cref="InvalidDataException">It is not a TIFF, or not one this reads; the message says which.</exception>
    public static FloatImage Decode(ReadOnlySpan<byte> data) {
        if (data.Length < 8) {
            throw new InvalidDataException("A TIFF is at least eight bytes long.");
        }

        var little = (data[0], data[1]) switch {
            ((byte)'I', (byte)'I') => true,
            ((byte)'M', (byte)'M') => false,
            _ => throw new InvalidDataException("This is not a TIFF: it starts with neither II nor MM.")
        };
        var file = new Reader(data.ToArray(), little);
        if (file.U16(2) != 42) {
            throw new InvalidDataException("This is not a classic TIFF; BigTIFF is not read.");
        }

        var tags = file.Directory((int)file.U32(4));
        var width = (int)One(tags, Width);
        var height = (int)One(tags, Height);
        var channels = (int)(tags.GetValueOrDefault(SamplesPerPixel)?[0] ?? 1);
        var bits = (int)(tags.GetValueOrDefault(BitsPerSample)?[0] ?? 1);
        var format = tags.GetValueOrDefault(SampleFormat)?[0] ?? 1;
        Refuse(tags, channels, bits, format, width, height);

        var compression = tags.GetValueOrDefault(Compression)?[0] ?? 1;
        var predictor = tags.GetValueOrDefault(Predictor)?[0] ?? 1;
        var offsets = tags[StripOffsets];
        var counts = tags.GetValueOrDefault(StripByteCounts)
            ?? throw new InvalidDataException("The TIFF does not say how long its strips are.");
        var rowsPerStrip = (int)Math.Min(tags.GetValueOrDefault(RowsPerStrip)?[0] ?? uint.MaxValue, (uint)height);
        var bytesPerSample = bits / 8;
        var row = width * channels * bytesPerSample;
        var pixels = new byte[row * height];

        for (var strip = 0; strip < offsets.Length; strip++) {
            var first = strip * rowsPerStrip;
            if (first >= height) {
                break;
            }

            var length = Math.Min(rowsPerStrip, height - first) * row;
            var stored = file.Slice(offsets[strip], counts[strip]);
            var into = pixels.AsSpan(first * row, length);
            if (compression == 1) {
                stored[..Math.Min(stored.Length, length)].CopyTo(into);
            } else {
                Inflate(stored, into);
            }
        }

        if (!little && bytesPerSample > 1) {
            Swap(pixels, bytesPerSample);
        }

        if (predictor == 2) {
            Unpredict(pixels, width, height, channels, bytesPerSample);
        }

        return new(width, height, channels, Values(pixels, bits, format));
    }

    /// <summary>Refuses what this does not read, naming it.</summary>
    static void Refuse(Dictionary<ushort, uint[]> tags, int channels, int bits, uint format, int width, int height) {
        if (width < 1 || height < 1) {
            throw new InvalidDataException($"A TIFF cannot be {width}×{height}.");
        }

        if (tags.ContainsKey(TileWidth)) {
            throw new InvalidDataException("A tiled TIFF is not read; save it in strips.");
        }

        if ((tags.GetValueOrDefault(PlanarConfiguration)?[0] ?? 1) != 1) {
            throw new InvalidDataException("A TIFF with planes of its own per channel is not read; save it chunky.");
        }

        if (channels is < 1 or > 4) {
            throw new InvalidDataException($"A TIFF of {channels} samples per pixel is not read.");
        }

        var photometric = tags.GetValueOrDefault(Photometric)?[0] ?? 1;
        if (photometric is not (1 or 2)) {
            throw new InvalidDataException(
                $"A TIFF with photometric interpretation {photometric} (a palette, CMYK, YCbCr or white-is-zero) is not read."
            );
        }

        if ((bits, format) is not ((8, 1) or (16, 1) or (32, 3))) {
            throw new InvalidDataException(
                $"A TIFF of {bits}-bit {(format == 3 ? "float" : "integer")} samples is not read: 8 or 16-bit integers or 32-bit float are."
            );
        }

        var compression = tags.GetValueOrDefault(Compression)?[0] ?? 1;
        if (compression is not (1 or 8 or 32946)) {
            throw new InvalidDataException(
                $"A TIFF compressed with scheme {compression} is not read: none and Deflate are."
            );
        }

        if (!tags.ContainsKey(StripOffsets)) {
            throw new InvalidDataException("The TIFF does not say where its strips are.");
        }
    }

    static uint One(Dictionary<ushort, uint[]> tags, ushort tag) =>
        tags.TryGetValue(tag, out var values) && values.Length > 0
            ? values[0]
            : throw new InvalidDataException($"The TIFF has no tag {tag}.");

    static void Inflate(ReadOnlySpan<byte> stored, Span<byte> into) {
        using var zlib = new ZLibStream(new MemoryStream(stored.ToArray()), CompressionMode.Decompress);
        var read = 0;
        while (read < into.Length) {
            var got = zlib.Read(into[read..]);
            if (got == 0) {
                throw new InvalidDataException("A TIFF strip ends before its rows do.");
            }

            read += got;
        }
    }

    static void Swap(byte[] pixels, int size) {
        for (var at = 0; at + size <= pixels.Length; at += size) {
            pixels.AsSpan(at, size).Reverse();
        }
    }

    /// <summary>Predictor 2 undone: each sample plus the same channel of the texel to its left, wrapping.</summary>
    static void Unpredict(byte[] pixels, int width, int height, int channels, int bytesPerSample) {
        var row = width * channels * bytesPerSample;
        for (var y = 0; y < height; y++) {
            var start = y * row;
            for (var x = 1; x < width; x++) {
                for (var lane = 0; lane < channels; lane++) {
                    var at = start + (x * channels + lane) * bytesPerSample;
                    var left = at - channels * bytesPerSample;
                    if (bytesPerSample == 1) {
                        pixels[at] = (byte)(pixels[at] + pixels[left]);
                    } else if (bytesPerSample == 2) {
                        var value = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(pixels.AsSpan(at))
                            + BinaryPrimitives.ReadUInt16LittleEndian(pixels.AsSpan(left)));
                        BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(at), value);
                    } else {
                        throw new InvalidDataException("The horizontal predictor is not read on float samples.");
                    }
                }
            }
        }
    }

    static float[] Values(byte[] pixels, int bits, uint format) {
        var values = new float[pixels.Length / (bits / 8)];
        for (var at = 0; at < values.Length; at++) {
            values[at] = (bits, format) switch {
                (8, _) => pixels[at] / 255f,
                (16, _) => BinaryPrimitives.ReadUInt16LittleEndian(pixels.AsSpan(at * 2)) / 65535f,
                _ => BinaryPrimitives.ReadSingleLittleEndian(pixels.AsSpan(at * 4))
            };
        }

        return values;
    }

    /// <summary>A file's bytes in its own byte order.</summary>
    sealed class Reader(byte[] bytes, bool little) {
        public ushort U16(int at) {
            Check(at, 2);
            return little
                ? BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at))
                : BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(at));
        }

        public uint U32(int at) {
            Check(at, 4);
            return little
                ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at))
                : BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at));
        }

        public ReadOnlySpan<byte> Slice(uint at, uint length) {
            if (at > bytes.Length || length > bytes.Length - at) {
                throw new InvalidDataException("A TIFF strip runs past the end of the file.");
            }

            return bytes.AsSpan((int)at, (int)length);
        }

        /// <summary>The first directory's tags, each as its values: SHORT and LONG only, which is all this asks.</summary>
        public Dictionary<ushort, uint[]> Directory(int at) {
            var count = U16(at);
            Dictionary<ushort, uint[]> tags = [];
            for (var entry = 0; entry < count; entry++) {
                var place = at + 2 + entry * 12;
                var (tag, type, many) = (U16(place), U16(place + 2), U32(place + 4));
                var size = type switch {
                    3 => 2,
                    4 => 4,
                    _ => 0
                };
                if (size == 0 || many > bytes.Length / size) {
                    continue; // a type this never needs, such as a rational resolution
                }

                var start = many * size <= 4 ? place + 8 : (int)U32(place + 8);
                var values = new uint[many];
                for (var value = 0; value < many; value++) {
                    values[value] = size == 2 ? U16(start + value * 2) : U32(start + value * 4);
                }

                tags[tag] = values;
            }

            return tags;
        }

        void Check(int at, int size) {
            if (at < 0 || at > bytes.Length - size) {
                throw new InvalidDataException("The TIFF points past its own end.");
            }
        }
    }
}
