// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;

namespace Vixen.Interchange;

/// <summary>Lossless WebP (VP8L), written: RGB or RGBA at eight bits, every texel exactly as given.</summary>
/// <remarks>
///     <para>
///         <b>Lossless only.</b> Lossy WebP is a VP8 video key frame, a codec of its own, and its
///         artefacts are worse for a normal or a mask than JPEG's. A texture that wants lossy
///         compression wants a GPU format.
///     </para>
///     <para>
///         <b>A small subset of the encoder's tools, all of them in the format's baseline.</b> The
///         subtract-green transform, then each texel either as four literals or as a copy of a run
///         from the texel to its left or the one above. No predictor transform, no colour cache and
///         one set of prefix codes for the whole picture: the file is larger than <c>cwebp</c>'s
///         and decodes the same in every reader, which is what was checked, against libwebp's own
///         <c>dwebp</c>.
///     </para>
///     <para>
///         ⚠ <b>A prefix code with one symbol costs no bits</b>, in the format's decoder and so in
///         this encoder. Writing a bit for it shifts every symbol after it.
///     </para>
/// </remarks>
public static class WebPWriter {
    const int Literals = 256;
    const int LengthCodes = 24;
    const int DistanceCodes = 40;
    const int MaxLength = 4096;
    const int MinRun = 3;

    static ReadOnlySpan<byte> CodeLengthOrder => [17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];

    /// <summary>Encodes a picture.</summary>
    /// <param name="width">Its width, at most 16384.</param>
    /// <param name="height">Its height, at most 16384.</param>
    /// <param name="channels">One for grey, three for RGB, four for RGBA.</param>
    /// <param name="samples">Interleaved, top row first.</param>
    /// <returns>The file.</returns>
    /// <exception cref="ArgumentException">The shape is not one a WebP holds.</exception>
    public static byte[] Encode(int width, int height, int channels, ReadOnlySpan<byte> samples) {
        if (channels is not (1 or 3 or 4)) {
            throw new ArgumentException(
                $"A WebP is written from one, three or four channels, not {channels}.",
                nameof(channels)
            );
        }

        if (width is < 1 or > 16384 || height is < 1 or > 16384) {
            throw new ArgumentException($"A WebP cannot be {width}×{height}.", nameof(width));
        }

        if (samples.Length != width * height * channels) {
            throw new ArgumentException("The sample count does not match the shape.", nameof(samples));
        }

        // ARGB, with green subtracted from red and blue.
        var argb = new uint[width * height];
        var alpha = false;

        for (var at = 0; at < argb.Length; at++) {
            var source = at * channels;
            uint red = samples[source];
            var (green, blue) = channels == 1 ? (red, red) : ((uint)samples[source + 1], (uint)samples[source + 2]);
            var opacity = channels == 4 ? samples[source + 3] : 255u;
            alpha |= opacity != 255;
            argb[at] = (opacity << 24) | (((red - green) & 0xFF) << 16) | (green << 8) | ((blue - green) & 0xFF);
        }

        var tokens = Tokens(argb, width);

        var counts = new[] {
            new long[Literals + LengthCodes], new long[Literals], new long[Literals], new long[Literals],
            new long[DistanceCodes]
        };

        foreach (var token in tokens) {
            if (token.Length == 0) {
                counts[0][(token.Pixel >> 8) & 0xFF]++;
                counts[1][(token.Pixel >> 16) & 0xFF]++;
                counts[2][token.Pixel & 0xFF]++;
                counts[3][token.Pixel >> 24]++;
            } else {
                counts[0][Literals + Prefix(token.Length).Code]++;
                counts[4][Prefix(token.Distance).Code]++;
            }
        }

        var writer = new BitWriter();
        writer.Write(0x2F, 8);
        writer.Write((uint)(width - 1), 14);
        writer.Write((uint)(height - 1), 14);
        writer.Write(alpha ? 1u : 0u, 1);
        writer.Write(0, 3);

        // One transform, subtract green, then no more.
        writer.Write(1, 1);
        writer.Write(2, 2);
        writer.Write(0, 1);

        // No colour cache, and one set of prefix codes for the whole picture.
        writer.Write(0, 1);
        writer.Write(0, 1);

        var codes = new Code[5];

        for (var group = 0; group < 5; group++) {
            codes[group] = WriteCode(writer, counts[group]);
        }

        foreach (var token in tokens) {
            if (token.Length == 0) {
                codes[0].Write(writer, (int)((token.Pixel >> 8) & 0xFF));
                codes[1].Write(writer, (int)((token.Pixel >> 16) & 0xFF));
                codes[2].Write(writer, (int)(token.Pixel & 0xFF));
                codes[3].Write(writer, (int)(token.Pixel >> 24));
                continue;
            }

            var length = Prefix(token.Length);
            codes[0].Write(writer, Literals + length.Code);
            writer.Write(length.Extra, length.ExtraBits);
            var distance = Prefix(token.Distance);
            codes[4].Write(writer, distance.Code);
            writer.Write(distance.Extra, distance.ExtraBits);
        }

        var data = writer.ToArray();
        var padded = data.Length + (data.Length & 1);
        var file = new byte[20 + padded];
        "RIFF"u8.CopyTo(file);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), (uint)(12 + padded));
        "WEBPVP8L"u8.CopyTo(file.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(16), (uint)data.Length);
        data.CopyTo(file.AsSpan(20));
        return file;
    }

    /// <summary>One literal texel, or a copy of <c>Length</c> texels from <c>Distance</c> as a distance code.</summary>
    readonly record struct Token(uint Pixel, int Length, int Distance);

    /// <summary>
    ///     Literals and runs. A run copies from the texel to the left (distance code 2) or the one
    ///     above (distance code 1), the two shortest codes the format's distance map has.
    /// </summary>
    static List<Token> Tokens(uint[] argb, int width) {
        List<Token> tokens = [];
        var at = 0;

        while (at < argb.Length) {
            var left = at >= 1 ? Run(argb, at, 1) : 0;
            var above = at >= width ? Run(argb, at, width) : 0;

            if (Math.Max(left, above) >= MinRun) {
                var (length, code) = above > left ? (above, 1) : (left, 2);
                tokens.Add(new(0, length, code));
                at += length;
            } else {
                tokens.Add(new(argb[at], 0, 0));
                at++;
            }
        }

        return tokens;
    }

    static int Run(uint[] argb, int at, int distance) {
        var length = 0;

        while (at + length < argb.Length && length < MaxLength && argb[at + length] == argb[at + length - distance]) {
            length++;
        }

        return length;
    }

    /// <summary>The format's prefix coding of a length or distance code of one or more.</summary>
    static (int Code, uint Extra, int ExtraBits) Prefix(int value) {
        var d = value - 1;

        if (d < 4) {
            return (d, 0, 0);
        }

        var highest = 31 - int.LeadingZeroCount(d);
        var second = (d >> (highest - 1)) & 1;
        var extraBits = highest - 1;
        return (2 * highest + second, (uint)(d & ((1 << extraBits) - 1)), extraBits);
    }

    /// <summary>Writes a prefix code for some counts, and returns it for the data.</summary>
    static Code WriteCode(BitWriter writer, long[] counts) {
        var used = counts.Select(static (count, symbol) => (count, symbol)).Where(static u => u.count > 0).ToList();

        // A simple code: one symbol below 256, which costs nothing per use. An unused alphabet is symbol zero.
        if (used.Count <= 1) {
            var symbol = used.Count == 0 ? 0 : used[0].symbol;
            writer.Write(1, 1);
            writer.Write(0, 1);
            writer.Write(1, 1);
            writer.Write((uint)symbol, 8);
            return new(new int[counts.Length], new int[counts.Length], true);
        }

        var lengths = PrefixCodes.Lengths(counts, 15);
        WriteLengths(writer, lengths);
        return new(lengths, PrefixCodes.Codes(lengths), false);
    }

    /// <summary>A normal code: its lengths, themselves prefix coded, with runs of zeros as codes 17 and 18.</summary>
    static void WriteLengths(BitWriter writer, int[] lengths) {
        List<(int Symbol, int Extra, int ExtraBits)> stream = [];

        for (var at = 0; at < lengths.Length;) {
            if (lengths[at] != 0) {
                stream.Add((lengths[at], 0, 0));
                at++;
                continue;
            }

            var zeros = 0;

            while (at + zeros < lengths.Length && lengths[at + zeros] == 0 && zeros < 138) {
                zeros++;
            }

            if (zeros >= 11) {
                stream.Add((18, zeros - 11, 7));
            } else if (zeros >= 3) {
                stream.Add((17, zeros - 3, 3));
            } else {
                for (var zero = 0; zero < zeros; zero++) {
                    stream.Add((0, 0, 0));
                }
            }

            at += zeros;
        }

        var counts = new long[19];

        foreach (var (symbol, _, _) in stream) {
            counts[symbol]++;
        }

        var lengthLengths = PrefixCodes.Lengths(counts, 7);
        var single = counts.Count(static count => count > 0) == 1;
        var codes = PrefixCodes.Codes(lengthLengths);
        var written = 19;

        while (written > 4 && lengthLengths[CodeLengthOrder[written - 1]] == 0) {
            written--;
        }

        writer.Write(0, 1);
        writer.Write((uint)(written - 4), 4);

        for (var at = 0; at < written; at++) {
            writer.Write((uint)lengthLengths[CodeLengthOrder[at]], 3);
        }

        // Every symbol is coded: no max_symbol.
        writer.Write(0, 1);

        foreach (var (symbol, extra, extraBits) in stream) {
            if (!single) {
                writer.Write((uint)PrefixCodes.Reverse(codes[symbol], lengthLengths[symbol]), lengthLengths[symbol]);
            }

            writer.Write((uint)extra, extraBits);
        }
    }

    /// <summary>A prefix code ready to write symbols with.</summary>
    sealed record Code(int[] Lengths, int[] Codes, bool Free) {
        public void Write(BitWriter writer, int symbol) {
            if (!Free) {
                writer.Write((uint)PrefixCodes.Reverse(Codes[symbol], Lengths[symbol]), Lengths[symbol]);
            }
        }
    }

    /// <summary>Least significant bit first, which is the order VP8L reads in.</summary>
    sealed class BitWriter {
        readonly List<byte> output = [];
        ulong buffer;
        int count;

        public void Write(uint value, int bits) {
            if (bits == 0) {
                return;
            }

            buffer |= (ulong)(value & (uint)((1UL << bits) - 1)) << count;
            count += bits;

            while (count >= 8) {
                output.Add((byte)buffer);
                buffer >>= 8;
                count -= 8;
            }
        }

        public byte[] ToArray() {
            if (count > 0) {
                output.Add((byte)buffer);
                buffer = 0;
                count = 0;
            }

            return [..output];
        }
    }
}
