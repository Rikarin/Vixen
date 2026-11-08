// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;

namespace Vixen.Interchange;

/// <summary>Baseline JPEG, written: grey or colour, eight bits, no subsampling.</summary>
/// <remarks>
///     <para>
///         <b>No chroma subsampling, on purpose.</b> A photograph hides half-resolution colour; a
///         texture does not, because a base colour's chart edges and an id map's borders are exactly
///         the sharp colour transitions that 4:2:0 smears. The file is somewhat larger and every
///         reader takes it.
///     </para>
///     <para>
///         <b>The Huffman tables are made for the picture</b>, from a first pass that counts what the
///         second writes, as libjpeg does when asked to optimise. That makes the file smaller than
///         the tables in the standard's annex would, and it means the code never carries a
///         transcription of those tables to get wrong. ⚠ A pseudo-symbol counted once keeps every
///         real symbol off the all-ones code, which JPEG reserves.
///     </para>
///     <para>Quality scales the annex's quantisation tables the way libjpeg does, so 90 here is 90 there.</para>
/// </remarks>
public static class JpegWriter {
    const int Block = 64;

    static ReadOnlySpan<byte> ZigZag => [
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21,
        28, 35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61,
        54, 47, 55, 62, 63
    ];

    static ReadOnlySpan<byte> LumaTable => [
        16, 11, 10, 16, 24, 40, 51, 61, 12, 12, 14, 19, 26, 58, 60, 55, 14, 13, 16, 24, 40, 57, 69, 56, 14, 17, 22, 29,
        51, 87, 80, 62, 18, 22, 37, 56, 68, 109, 103, 77, 24, 35, 55, 64, 81, 104, 113, 92, 49, 64, 78, 87, 103, 121,
        120, 101, 72, 92, 95, 98, 112, 100, 103, 99
    ];

    static ReadOnlySpan<byte> ChromaTable => [
        17, 18, 24, 47, 99, 99, 99, 99, 18, 21, 26, 66, 99, 99, 99, 99, 24, 26, 56, 99, 99, 99, 99, 99, 47, 66, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99
    ];

    static readonly float[] Cosines = MakeCosines();

    /// <summary>Encodes a picture.</summary>
    /// <param name="width">Its width, at most 65535.</param>
    /// <param name="height">Its height, at most 65535.</param>
    /// <param name="channels">One for grey, three for RGB. JPEG has no alpha.</param>
    /// <param name="samples">Interleaved, top row first.</param>
    /// <param name="quality">1 to 100, as libjpeg reads it.</param>
    /// <returns>The file.</returns>
    /// <exception cref="ArgumentException">The shape is not one JPEG holds.</exception>
    public static byte[] Encode(int width, int height, int channels, ReadOnlySpan<byte> samples, int quality = 90) {
        if (channels is not (1 or 3)) {
            throw new ArgumentException($"A JPEG holds one or three channels, not {channels}.", nameof(channels));
        }

        if (width is < 1 or > ushort.MaxValue || height is < 1 or > ushort.MaxValue) {
            throw new ArgumentException($"A JPEG cannot be {width}×{height}.", nameof(width));
        }

        if (samples.Length != width * height * channels) {
            throw new ArgumentException("The sample count does not match the shape.", nameof(samples));
        }

        quality = Math.Clamp(quality, 1, 100);
        var tables = new[] { Scaled(LumaTable, quality), Scaled(ChromaTable, quality) };
        var planes = Planes(width, height, channels, samples);
        var (across, down) = ((width + 7) / 8, (height + 7) / 8);

        // Quantised coefficients, zig-zag order, block by block and component by component within a block.
        var coefficients = new short[across * down * channels * Block];

        Parallel.For(0, down, row => {
            Span<float> block = stackalloc float[Block];

            for (var column = 0; column < across; column++) {
                for (var component = 0; component < channels; component++) {
                    Load(planes[component], width, height, column * 8, row * 8, block);
                    var into = coefficients.AsSpan((((row * across) + column) * channels + component) * Block, Block);
                    Transform(block, tables[component == 0 ? 0 : 1], into);
                }
            }
        });

        // First pass: what each table has to say. Index 256 is the reserved pseudo-symbol.
        var dc = new[] { new long[257], new long[257] };
        var ac = new[] { new long[257], new long[257] };
        Walk(coefficients, channels, (table, symbol, _, _, isDc) => (isDc ? dc : ac)[table][symbol]++);

        var codes = new (int[] Lengths, int[] Codes)[4];

        for (var table = 0; table < (channels == 1 ? 1 : 2); table++) {
            dc[table][256] = 1;
            ac[table][256] = 1;
            codes[table * 2] = Code(dc[table]);
            codes[(table * 2) + 1] = Code(ac[table]);
        }

        using var output = new MemoryStream();
        Headers(output, width, height, channels, tables, codes);

        // Second pass: the scan.
        var bits = new BitWriter(output);
        Walk(coefficients, channels, (table, symbol, extra, extraBits, isDc) => {
            var (lengths, values) = codes[(table * 2) + (isDc ? 0 : 1)];
            bits.Write(values[symbol], lengths[symbol]);
            bits.Write(extra, extraBits);
        });
        bits.Flush();

        output.Write([0xFF, 0xD9]);
        return output.ToArray();
    }

    static float[] MakeCosines() {
        var table = new float[Block];

        for (var u = 0; u < 8; u++) {
            for (var x = 0; x < 8; x++) {
                var scale = u == 0 ? Math.Sqrt(0.5) : 1.0;
                table[(u * 8) + x] = (float)(scale / 2.0 * Math.Cos((2 * x + 1) * u * Math.PI / 16));
            }
        }

        return table;
    }

    static int[] Scaled(ReadOnlySpan<byte> table, int quality) {
        var scale = quality < 50 ? 5000 / quality : 200 - (quality * 2);
        var scaled = new int[Block];

        for (var at = 0; at < Block; at++) {
            scaled[at] = Math.Clamp(((table[at] * scale) + 50) / 100, 1, 255);
        }

        return scaled;
    }

    /// <summary>The picture as component planes, centred on zero: Y, Cb and Cr for colour, as JFIF defines them.</summary>
    static float[][] Planes(int width, int height, int channels, ReadOnlySpan<byte> samples) {
        var planes = new float[channels][];

        for (var component = 0; component < channels; component++) {
            planes[component] = new float[width * height];
        }

        for (var at = 0; at < width * height; at++) {
            if (channels == 1) {
                planes[0][at] = samples[at] - 128f;
                continue;
            }

            var (r, g, b) = (samples[at * 3], samples[(at * 3) + 1], samples[(at * 3) + 2]);
            planes[0][at] = (0.299f * r) + (0.587f * g) + (0.114f * b) - 128f;
            planes[1][at] = (-0.168736f * r) - (0.331264f * g) + (0.5f * b);
            planes[2][at] = (0.5f * r) - (0.418688f * g) - (0.081312f * b);
        }

        return planes;
    }

    /// <summary>An 8×8 block, the edge texels repeated where it hangs over the picture.</summary>
    static void Load(float[] plane, int width, int height, int left, int top, Span<float> block) {
        for (var y = 0; y < 8; y++) {
            var row = Math.Min(top + y, height - 1) * width;

            for (var x = 0; x < 8; x++) {
                block[(y * 8) + x] = plane[row + Math.Min(left + x, width - 1)];
            }
        }
    }

    /// <summary>The forward DCT as two matrix products, then quantisation into zig-zag order.</summary>
    static void Transform(Span<float> block, int[] table, Span<short> into) {
        Span<float> rows = stackalloc float[Block];

        for (var y = 0; y < 8; y++) {
            for (var u = 0; u < 8; u++) {
                var sum = 0f;

                for (var x = 0; x < 8; x++) {
                    sum += Cosines[(u * 8) + x] * block[(y * 8) + x];
                }

                rows[(y * 8) + u] = sum;
            }
        }

        for (var v = 0; v < 8; v++) {
            for (var u = 0; u < 8; u++) {
                var sum = 0f;

                for (var y = 0; y < 8; y++) {
                    sum += Cosines[(v * 8) + y] * rows[(y * 8) + u];
                }

                block[(v * 8) + u] = sum;
            }
        }

        for (var at = 0; at < Block; at++) {
            var natural = ZigZag[at];
            into[at] = (short)MathF.Round(block[natural] / table[natural]);
        }
    }

    delegate void Emit(int table, int symbol, int extra, int extraBits, bool isDc);

    /// <summary>Every symbol the scan holds, in order, with the bits that follow it.</summary>
    static void Walk(short[] coefficients, int channels, Emit emit) {
        Span<int> previous = stackalloc int[channels];
        var blocks = coefficients.Length / Block;

        for (var at = 0; at < blocks; at++) {
            var component = at % channels;
            var table = component == 0 ? 0 : 1;
            var block = coefficients.AsSpan(at * Block, Block);
            var difference = block[0] - previous[component];
            previous[component] = block[0];
            var (size, extra) = Magnitude(difference);
            emit(table, size, extra, size, true);

            var zeros = 0;

            for (var index = 1; index < Block; index++) {
                if (block[index] == 0) {
                    zeros++;
                    continue;
                }

                while (zeros > 15) {
                    emit(table, 0xF0, 0, 0, false);
                    zeros -= 16;
                }

                (size, extra) = Magnitude(block[index]);
                emit(table, (zeros << 4) | size, extra, size, false);
                zeros = 0;
            }

            if (zeros > 0) {
                emit(table, 0x00, 0, 0, false);
            }
        }
    }

    static (int Size, int Bits) Magnitude(int value) {
        var size = value == 0 ? 0 : 32 - int.LeadingZeroCount(Math.Abs(value));
        return (size, value < 0 ? value + (1 << size) - 1 : value);
    }

    static (int[] Lengths, int[] Codes) Code(long[] counts) {
        var lengths = PrefixCodes.Lengths(counts, 16);
        return (lengths, PrefixCodes.Codes(lengths));
    }

    static void Headers(Stream output, int width, int height, int channels, int[][] tables, (int[] Lengths, int[] Codes)[] codes) {
        output.Write([0xFF, 0xD8]);
        Segment(output, 0xE0, [(byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0]);

        var count = channels == 1 ? 1 : 2;
        List<byte> quantisation = [];

        for (var table = 0; table < count; table++) {
            quantisation.Add((byte)table);

            foreach (var natural in ZigZag) {
                quantisation.Add((byte)tables[table][natural]);
            }
        }

        Segment(output, 0xDB, [.. quantisation]);

        List<byte> frame = [8, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, (byte)channels];

        for (var component = 0; component < channels; component++) {
            frame.AddRange([(byte)(component + 1), 0x11, (byte)(component == 0 ? 0 : 1)]);
        }

        Segment(output, 0xC0, [.. frame]);

        List<byte> huffman = [];

        for (var table = 0; table < count; table++) {
            Table(huffman, 0x00 | table, codes[table * 2].Lengths);
            Table(huffman, 0x10 | table, codes[(table * 2) + 1].Lengths);
        }

        Segment(output, 0xC4, [.. huffman]);

        List<byte> scan = [(byte)channels];

        for (var component = 0; component < channels; component++) {
            scan.AddRange([(byte)(component + 1), (byte)(component == 0 ? 0x00 : 0x11)]);
        }

        scan.AddRange([0, 63, 0]);
        Segment(output, 0xDA, [.. scan]);
    }

    /// <summary>A DHT table: counts per length, then the symbols in code order, the reserved one left out.</summary>
    static void Table(List<byte> into, int id, int[] lengths) {
        into.Add((byte)id);

        for (var length = 1; length <= 16; length++) {
            into.Add((byte)lengths.Take(256).Count(l => l == length));
        }

        for (var length = 1; length <= 16; length++) {
            for (var symbol = 0; symbol < 256; symbol++) {
                if (lengths[symbol] == length) {
                    into.Add((byte)symbol);
                }
            }
        }
    }

    static void Segment(Stream output, byte marker, ReadOnlySpan<byte> body) {
        Span<byte> header = [0xFF, marker, 0, 0];
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], (ushort)(body.Length + 2));
        output.Write(header);
        output.Write(body);
    }

    /// <summary>Most significant bit first, with a zero stuffed after every 0xFF the scan makes.</summary>
    sealed class BitWriter(Stream output) {
        int buffer;
        int count;

        public void Write(int value, int bits) {
            for (var bit = bits - 1; bit >= 0; bit--) {
                buffer = (buffer << 1) | ((value >> bit) & 1);
                count++;

                if (count == 8) {
                    Put();
                }
            }
        }

        /// <summary>Pads the last byte with ones, as the standard asks.</summary>
        public void Flush() {
            while (count != 0) {
                buffer = (buffer << 1) | 1;
                count++;

                if (count == 8) {
                    Put();
                }
            }
        }

        void Put() {
            output.WriteByte((byte)buffer);

            if (buffer == 0xFF) {
                output.WriteByte(0);
            }

            buffer = 0;
            count = 0;
        }
    }
}
