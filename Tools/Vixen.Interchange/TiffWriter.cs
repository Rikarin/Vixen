// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.IO.Compression;
using Vixen.Core.Imaging;

namespace Vixen.Interchange;

/// <summary>Baseline TIFF, written: grey or colour, with or without alpha, at 8 or 16 bits or in 32-bit float.</summary>
/// <remarks>
///     <para>
///         <b>Little-endian, one image, chunky pixels, Deflate strips.</b> Deflate is compression 8,
///         which libtiff, macOS ImageIO and every painting program read, and it is the one
///         <c>System.IO.Compression</c> already ships. Integer samples go through the horizontal
///         predictor first, which is what makes a smooth texture compress; float samples do not,
///         since the predictor for them reorders bytes and fewer readers have it.
///     </para>
///     <para>
///         ⚠ <b>Alpha is marked unassociated</b> (ExtraSamples = 2): the colour is stored as it is,
///         not multiplied by alpha. A reader told otherwise divides it out again and brightens
///         every half-transparent texel.
///     </para>
/// </remarks>
public static class TiffWriter {
    const int StripBytes = 1 << 16;

    /// <summary>Encodes eight-bit samples.</summary>
    /// <param name="width">Its width.</param>
    /// <param name="height">Its height.</param>
    /// <param name="channels">One for grey, two for grey and alpha, three for RGB, four for RGBA.</param>
    /// <param name="samples">Interleaved, top row first.</param>
    /// <returns>The file.</returns>
    /// <exception cref="ArgumentException">The shape is not one this writes.</exception>
    public static byte[] Encode8(int width, int height, int channels, ReadOnlySpan<byte> samples) {
        Check(width, height, channels, samples.Length);
        return Write(width, height, channels, 8, 1, [..samples]);
    }

    /// <summary>Encodes sixteen-bit samples.</summary>
    /// <param name="width">Its width.</param>
    /// <param name="height">Its height.</param>
    /// <param name="channels">One for grey, two for grey and alpha, three for RGB, four for RGBA.</param>
    /// <param name="samples">Interleaved, top row first.</param>
    /// <returns>The file.</returns>
    /// <exception cref="ArgumentException">The shape is not one this writes.</exception>
    public static byte[] Encode16(int width, int height, int channels, ReadOnlySpan<ushort> samples) {
        Check(width, height, channels, samples.Length);
        var bytes = new byte[samples.Length * 2];

        for (var at = 0; at < samples.Length; at++) {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at * 2), samples[at]);
        }

        return Write(width, height, channels, 16, 1, bytes);
    }

    /// <summary>Encodes 32-bit float samples.</summary>
    /// <param name="image">One to four channels, as for the integer forms.</param>
    /// <returns>The file.</returns>
    /// <exception cref="ArgumentException">The shape is not one this writes.</exception>
    public static byte[] EncodeFloat(FloatImage image) {
        ArgumentNullException.ThrowIfNull(image);
        Check(image.Width, image.Height, image.Channels, image.Samples.Length);
        var bytes = new byte[image.Samples.Length * 4];

        for (var at = 0; at < image.Samples.Length; at++) {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at * 4), image.Samples[at]);
        }

        return Write(image.Width, image.Height, image.Channels, 32, 3, bytes);
    }

    static void Check(int width, int height, int channels, int count) {
        if (channels is < 1 or > 4) {
            throw new ArgumentException($"A TIFF here holds one to four channels, not {channels}.", nameof(channels));
        }

        if (width < 1 || height < 1) {
            throw new ArgumentException($"A TIFF cannot be {width}×{height}.", nameof(width));
        }

        if (count != width * height * channels) {
            throw new ArgumentException("The sample count does not match the shape.", nameof(count));
        }
    }

    static byte[] Write(int width, int height, int channels, int bits, int format, byte[] pixels) {
        var bytesPerSample = bits / 8;
        var row = width * channels * bytesPerSample;
        var predict = format == 1;

        if (predict) {
            Predict(pixels, height, width, channels, bytesPerSample);
        }

        var rowsPerStrip = Math.Max(1, StripBytes / row);
        var strips = (height + rowsPerStrip - 1) / rowsPerStrip;
        var compressed = new byte[strips][];

        Parallel.For(
            0,
            strips,
            strip => {
                var first = strip * rowsPerStrip;
                var rows = Math.Min(rowsPerStrip, height - first);
                using var memory = new MemoryStream();

                using (var zlib = new ZLibStream(memory, CompressionLevel.Optimal, true)) {
                    zlib.Write(pixels, first * row, rows * row);
                }

                compressed[strip] = memory.ToArray();
            }
        );

        using var output = new MemoryStream();
        output.Write("II*\0"u8);
        output.Write(new byte[4]);

        var offsets = new uint[strips];
        var counts = new uint[strips];

        for (var strip = 0; strip < strips; strip++) {
            offsets[strip] = (uint)output.Position;
            counts[strip] = (uint)compressed[strip].Length;
            output.Write(compressed[strip]);
        }

        var alpha = channels is 2 or 4;
        List<(ushort Tag, ushort Type, uint[] Values)> entries = [
            (256, 4, [(uint)width]),
            (257, 4, [(uint)height]),
            (258, 3, [..Enumerable.Repeat((uint)bits, channels)]),
            (259, 3, [8]),
            (262, 3, [channels <= 2 ? 1u : 2u]),
            (273, 4, offsets),
            (277, 3, [(uint)channels]),
            (278, 4, [(uint)rowsPerStrip]),
            (279, 4, counts),
            (282, 5, [72, 1]),
            (283, 5, [72, 1]),
            (284, 3, [1]),
            (296, 3, [2])
        ];

        if (predict) {
            entries.Add((317, 3, [2]));
        }

        if (alpha) {
            entries.Add((338, 3, [2]));
        }

        entries.Add((339, 3, [..Enumerable.Repeat((uint)format, channels)]));

        // Values that do not fit in an entry's four bytes go before the directory, word-aligned.
        if (output.Position % 2 != 0) {
            output.WriteByte(0);
        }

        var outside = new Dictionary<int, uint>();

        for (var at = 0; at < entries.Count; at++) {
            var (_, type, values) = entries[at];
            var size = Size(type, values);

            if (size > 4) {
                outside[at] = (uint)output.Position;
                output.Write(Bytes(type, values));
            }
        }

        var directory = (uint)output.Position;
        Span<byte> buffer = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, (ushort)entries.Count);
        output.Write(buffer[..2]);

        for (var at = 0; at < entries.Count; at++) {
            var (tag, type, values) = entries[at];
            buffer.Clear();
            BinaryPrimitives.WriteUInt16LittleEndian(buffer, tag);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer[2..], type);
            BinaryPrimitives.WriteUInt32LittleEndian(
                buffer[4..],
                (uint)(type == 5 ? values.Length / 2 : values.Length)
            );

            if (outside.TryGetValue(at, out var where)) {
                BinaryPrimitives.WriteUInt32LittleEndian(buffer[8..], where);
            } else {
                Bytes(type, values).CopyTo(buffer[8..]);
            }

            output.Write(buffer);
        }

        output.Write(new byte[4]);
        var file = output.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), directory);
        return file;
    }

    static int Size(ushort type, uint[] values) => values.Length * (type == 3 ? 2 : 4);

    static byte[] Bytes(ushort type, uint[] values) {
        var bytes = new byte[Size(type, values)];

        for (var at = 0; at < values.Length; at++) {
            if (type == 3) {
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at * 2), (ushort)values[at]);
            } else {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at * 4), values[at]);
            }
        }

        return bytes;
    }

    /// <summary>Predictor 2: each sample less the same channel of the texel to its left, wrapping.</summary>
    static void Predict(byte[] pixels, int height, int width, int channels, int bytesPerSample) {
        var row = width * channels * bytesPerSample;

        Parallel.For(
            0,
            height,
            y => {
                var start = y * row;

                // Right to left, so every subtraction reads the value before it was changed.
                for (var x = width - 1; x > 0; x--) {
                    for (var lane = 0; lane < channels; lane++) {
                        var at = start + (x * channels + lane) * bytesPerSample;
                        var left = at - channels * bytesPerSample;

                        if (bytesPerSample == 1) {
                            pixels[at] = (byte)(pixels[at] - pixels[left]);
                        } else {
                            var value = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(pixels.AsSpan(at))
                                - BinaryPrimitives.ReadUInt16LittleEndian(pixels.AsSpan(left)));
                            BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(at), value);
                        }
                    }
                }
            }
        );
    }
}
