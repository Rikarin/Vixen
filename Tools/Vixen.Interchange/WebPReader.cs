// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using Vixen.Core.Imaging;

namespace Vixen.Interchange;

/// <summary>
///     Lossless WebP (VP8L), read: the whole of the format's lossless bitstream as RFC 9649 defines it, every transform
///     (predictor, colour, subtract green, colour indexing), the colour cache, LZ77 copies through the distance map and
///     prefix codes per region. It reads what <see cref="WebPWriter" /> writes and what libwebp writes with
///     <c>-lossless</c>, in a plain or an extended file. Lossy (VP8) and animated files are refused with what they are.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>A prefix code with one symbol costs no bits</b>, whether it was written as a simple code or a normal
///         one. Reading a bit for it shifts every symbol after it, as writing one does.
///     </para>
///     <para>
///         ⚠ <b>Every texel enters the colour cache</b>, a literal, a copied one and one read from the cache alike,
///         or the cache a later texel reads from is not the one the encoder had.
///     </para>
/// </remarks>
public static class WebPReader {
    const int Literals = 256;
    const int LengthCodes = 24;
    const int DistanceCodes = 40;
    const int MaxSide = 16384;

    static ReadOnlySpan<byte> CodeLengthOrder => [17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];

    /// <summary>The format's 120 short distances, each a step across and a step down, in RFC 9649's order.</summary>
    static ReadOnlySpan<sbyte> DistanceMap => [
        0, 1, 1, 0, 1, 1, -1, 1, 0, 2, 2, 0, 1, 2, -1, 2, 2, 1, -2, 1, 2, 2, -2, 2, 0, 3, 3, 0, 1, 3, -1, 3,
        3, 1, -3, 1, 2, 3, -2, 3, 3, 2, -3, 2, 0, 4, 4, 0, 1, 4, -1, 4, 4, 1, -4, 1, 3, 3, -3, 3, 2, 4, -2, 4,
        4, 2, -4, 2, 0, 5, 3, 4, -3, 4, 4, 3, -4, 3, 5, 0, 1, 5, -1, 5, 5, 1, -5, 1, 2, 5, -2, 5, 5, 2, -5, 2,
        4, 4, -4, 4, 3, 5, -3, 5, 5, 3, -5, 3, 0, 6, 6, 0, 1, 6, -1, 6, 6, 1, -6, 1, 2, 6, -2, 6, 6, 2, -6, 2,
        4, 5, -4, 5, 5, 4, -5, 4, 3, 6, -3, 6, 6, 3, -6, 3, 0, 7, 7, 0, 1, 7, -1, 7, 5, 5, -5, 5, 7, 1, -7, 1,
        4, 6, -4, 6, 6, 4, -6, 4, 2, 7, -2, 7, 7, 2, -7, 2, 3, 7, -3, 7, 7, 3, -7, 3, 5, 6, -5, 6, 6, 5, -6, 5,
        8, 0, 4, 7, -4, 7, 7, 4, -7, 4, 8, 1, 8, 2, 6, 6, -6, 6, 8, 3, 5, 7, -5, 7, 7, 5, -7, 5, 8, 4, 6, 7,
        -6, 7, 7, 6, -7, 6, 8, 5, 7, 7, -7, 7, 8, 6, 8, 7
    ];

    /// <summary>Decodes a WebP to numbers from 0 to 1, top row first.</summary>
    /// <param name="data">The file.</param>
    /// <returns>The picture: four channels when the file says it uses alpha, otherwise three.</returns>
    /// <exception cref="InvalidDataException">It is not a WebP, or not one this reads; the message says which.</exception>
    public static FloatImage Decode(ReadOnlySpan<byte> data) {
        var (width, height, alpha, argb) = DecodeArgb(data);
        var channels = alpha ? 4 : 3;
        var samples = new float[argb.Length * channels];
        for (var at = 0; at < argb.Length; at++) {
            var texel = argb[at];
            samples[at * channels] = ((texel >> 16) & 0xFF) / 255f;
            samples[at * channels + 1] = ((texel >> 8) & 0xFF) / 255f;
            samples[at * channels + 2] = (texel & 0xFF) / 255f;
            if (alpha) {
                samples[at * channels + 3] = (texel >> 24) / 255f;
            }
        }

        return new(width, height, channels, samples);
    }

    /// <summary>Decodes a WebP to eight-bit red, green, blue and alpha, top row first.</summary>
    /// <param name="data">The file.</param>
    /// <returns>Its width, its height, whether it says it uses alpha, and four bytes a texel.</returns>
    /// <exception cref="InvalidDataException">It is not a WebP, or not one this reads; the message says which.</exception>
    public static (int Width, int Height, bool Alpha, byte[] Rgba) DecodeRgba8(ReadOnlySpan<byte> data) {
        var (width, height, alpha, argb) = DecodeArgb(data);
        var rgba = new byte[argb.Length * 4];
        for (var at = 0; at < argb.Length; at++) {
            var texel = argb[at];
            rgba[at * 4] = (byte)(texel >> 16);
            rgba[at * 4 + 1] = (byte)(texel >> 8);
            rgba[at * 4 + 2] = (byte)texel;
            rgba[at * 4 + 3] = (byte)(texel >> 24);
        }

        return (width, height, alpha, rgba);
    }

    static (int Width, int Height, bool Alpha, uint[] Argb) DecodeArgb(ReadOnlySpan<byte> data) {
        var stream = Lossless(data);
        var bits = new BitReader(stream);
        if (bits.Read(8) != 0x2F) {
            throw new InvalidDataException("The WebP's lossless stream does not start with its signature.");
        }

        var width = (int)bits.Read(14) + 1;
        var height = (int)bits.Read(14) + 1;
        var alpha = bits.Read(1) == 1;
        if (bits.Read(3) != 0) {
            throw new InvalidDataException("The WebP's lossless stream is of a version this does not read.");
        }

        var decoder = new Decoder(bits);
        var argb = decoder.Picture(width, height);
        return (width, height, alpha, argb);
    }

    /// <summary>The lossless stream inside the file, from a plain or an extended file's chunks.</summary>
    static ReadOnlySpan<byte> Lossless(ReadOnlySpan<byte> data) {
        if (data.Length < 12 || !data[..4].SequenceEqual("RIFF"u8) || !data.Slice(8, 4).SequenceEqual("WEBP"u8)) {
            throw new InvalidDataException("This is not a WebP: it does not start with RIFF and WEBP.");
        }

        var end = (int)Math.Min(data.Length, 8L + BinaryPrimitives.ReadUInt32LittleEndian(data[4..]));
        for (var at = 12; at + 8 <= end;) {
            var tag = data.Slice(at, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(data[(at + 4)..]);
            var body = at + 8;
            if (size > (uint)(end - body)) {
                throw new InvalidDataException("The WebP's chunk runs past the end of the file.");
            }

            if (tag.SequenceEqual("VP8L"u8)) {
                return data.Slice(body, (int)size);
            }

            if (tag.SequenceEqual("VP8 "u8)) {
                throw new InvalidDataException(
                    "The WebP is lossy (VP8), and only lossless WebP is read. Save it lossless, or as a PNG."
                );
            }

            if (tag.SequenceEqual("ANMF"u8)) {
                throw new InvalidDataException("The WebP is animated, and only a still picture is read.");
            }

            at = body + (int)size + (int)(size & 1);
        }

        throw new InvalidDataException("The WebP has no picture in it.");
    }

    /// <summary>Applies an image's transforms backwards and decodes its pixels.</summary>
    sealed class Decoder(BitReader bits) {
        public uint[] Picture(int width, int height) {
            List<Transform> transforms = [];
            var across = width;
            var seen = 0;
            while (bits.Read(1) == 1) {
                var type = (int)bits.Read(2);
                if ((seen & (1 << type)) != 0) {
                    throw new InvalidDataException("The WebP uses a transform twice.");
                }

                seen |= 1 << type;
                var transform = ReadTransform(type, across, height);
                transforms.Add(transform);
                across = transform.Packed;
            }

            var argb = Image(across, height, true);
            for (var at = transforms.Count - 1; at >= 0; at--) {
                argb = transforms[at].Undo(argb, height);
            }

            return argb;
        }

        Transform ReadTransform(int type, int width, int height) {
            switch (type) {
                case 0 or 1: {
                    var size = (int)bits.Read(3) + 2;
                    var blocks = Image(Blocks(width, size), Blocks(height, size), false);
                    return type == 0 ? new Predictor(width, size, blocks) : new ColourTransform(width, size, blocks);
                }
                case 2:
                    return new SubtractGreen(width);
                default: {
                    var count = (int)bits.Read(8) + 1;
                    var table = Image(count, 1, false);
                    for (var at = 1; at < count; at++) {
                        table[at] = AddLanes(table[at], table[at - 1]);
                    }

                    var packing = count <= 2 ? 3 : count <= 4 ? 2 : count <= 16 ? 1 : 0;
                    return new ColourIndexing(width, packing, table);
                }
            }
        }

        /// <summary>One entropy-coded image: the main one, or a transform's or a prefix map's.</summary>
        uint[] Image(int width, int height, bool main) {
            var cacheBits = 0;
            if (bits.Read(1) == 1) {
                cacheBits = (int)bits.Read(4);
                if (cacheBits is < 1 or > 11) {
                    throw new InvalidDataException("The WebP's colour cache is of a size the format does not allow.");
                }
            }

            var groupBits = 0;
            uint[]? groupMap = null;
            var groups = 1;
            if (main && bits.Read(1) == 1) {
                groupBits = (int)bits.Read(3) + 2;
                groupMap = Image(Blocks(width, groupBits), Blocks(height, groupBits), false);
                for (var at = 0; at < groupMap.Length; at++) {
                    groupMap[at] = (groupMap[at] >> 8) & 0xFFFF;
                    groups = Math.Max(groups, (int)groupMap[at] + 1);
                }
            }

            var codes = new PrefixCode[groups][];
            var cacheSize = cacheBits == 0 ? 0 : 1 << cacheBits;
            for (var group = 0; group < groups; group++) {
                codes[group] = [
                    PrefixCode.Read(bits, Literals + LengthCodes + cacheSize), PrefixCode.Read(bits, Literals),
                    PrefixCode.Read(bits, Literals), PrefixCode.Read(bits, Literals),
                    PrefixCode.Read(bits, DistanceCodes)
                ];
            }

            return Pixels(width, height, new(codes, groupMap, groupBits, cacheBits));
        }

        /// <summary>What decoding an image's pixels reads from.</summary>
        sealed record Entropy(PrefixCode[][] Codes, uint[]? GroupMap, int GroupBits, int CacheBits);

        uint[] Pixels(int width, int height, Entropy entropy) {
            var argb = new uint[width * height];
            var cache = entropy.CacheBits == 0 ? [] : new uint[1 << entropy.CacheBits];
            var groupsAcross = Blocks(width, Math.Max(entropy.GroupBits, 1));
            var cached = 0;
            for (var at = 0; at < argb.Length;) {
                var x = at % width;
                var y = at / width;
                var codes = entropy.GroupMap is null
                    ? entropy.Codes[0]
                    : entropy.Codes[entropy.GroupMap[(y >> entropy.GroupBits) * groupsAcross
                        + (x >> entropy.GroupBits)]];
                var green = codes[0].Next(bits);
                if (green < Literals) {
                    var red = codes[1].Next(bits);
                    var blue = codes[2].Next(bits);
                    var alpha = codes[3].Next(bits);
                    argb[at++] = ((uint)alpha << 24) | ((uint)red << 16) | ((uint)green << 8) | (uint)blue;
                } else if (green < Literals + LengthCodes) {
                    var length = Prefixed(green - Literals);
                    var distance = Distance(Prefixed(codes[4].Next(bits)), width);
                    if (distance > at || length > argb.Length - at) {
                        throw new InvalidDataException("The WebP copies texels from outside the picture.");
                    }

                    for (var step = 0; step < length; step++, at++) {
                        argb[at] = argb[at - distance];
                    }
                } else {
                    var index = green - Literals - LengthCodes;
                    if (index >= cache.Length) {
                        throw new InvalidDataException("The WebP reads its colour cache past its end.");
                    }

                    argb[at++] = cache[index];
                }

                for (; cached < at && cache.Length > 0; cached++) {
                    cache[(int)((0x1E35A7BDu * argb[cached]) >> (32 - entropy.CacheBits))] = argb[cached];
                }
            }

            return argb;
        }

        /// <summary>A length or a distance code from its prefix symbol and the extra bits after it.</summary>
        int Prefixed(int symbol) {
            if (symbol < 4) {
                return symbol + 1;
            }

            var extra = (symbol - 2) >> 1;
            var offset = (2 + (symbol & 1)) << extra;
            return offset + (int)bits.Read(extra) + 1;
        }

        /// <summary>A distance code as how many texels back: the first 120 through the map, the rest less 120.</summary>
        static int Distance(int code, int width) {
            if (code > 120) {
                return code - 120;
            }

            var across = DistanceMap[(code - 1) * 2];
            var down = DistanceMap[(code - 1) * 2 + 1];
            return Math.Max(1, across + down * width);
        }
    }

    static int Blocks(int side, int bits) => (side + (1 << bits) - 1) >> bits;

    static uint AddLanes(uint a, uint b) =>
        (((a & 0xFF00FF00u) + (b & 0xFF00FF00u)) & 0xFF00FF00u)
        | (((a & 0x00FF00FFu) + (b & 0x00FF00FFu)) & 0x00FF00FFu);

    /// <summary>A transform, undone: what it takes is its packed width, and what it gives is the width before it.</summary>
    abstract class Transform(int width) {
        /// <summary>The width before it.</summary>
        protected int Width { get; } = width;

        /// <summary>The width of what it leaves to the next transform: less than the width only for packed indices.</summary>
        public virtual int Packed => Width;

        public abstract uint[] Undo(uint[] argb, int height);
    }

    sealed class SubtractGreen(int width) : Transform(width) {
        public override uint[] Undo(uint[] argb, int height) {
            for (var at = 0; at < argb.Length; at++) {
                var green = (argb[at] >> 8) & 0xFF;
                argb[at] = AddLanes(argb[at], (green << 16) | green);
            }

            return argb;
        }
    }

    sealed class ColourIndexing(int width, int packing, uint[] table) : Transform(width) {
        public override int Packed => Blocks(Width, packing);

        public override uint[] Undo(uint[] argb, int height) {
            var output = new uint[Width * height];
            var per = 1 << packing;
            var bitsPer = 8 >> packing;
            var mask = (1u << bitsPer) - 1;
            for (var y = 0; y < height; y++) {
                for (var x = 0; x < Width; x++) {
                    var packed = (argb[y * Packed + (x >> packing)] >> 8) & 0xFF;
                    var index = (packed >> ((x & (per - 1)) * bitsPer)) & mask;
                    output[y * Width + x] = index < table.Length ? table[index] : 0u;
                }
            }

            return output;
        }
    }

    sealed class ColourTransform(int width, int size, uint[] blocks) : Transform(width) {
        public override uint[] Undo(uint[] argb, int height) {
            var across = Blocks(Width, size);
            for (var y = 0; y < height; y++) {
                for (var x = 0; x < Width; x++) {
                    var element = blocks[(y >> size) * across + (x >> size)];
                    var at = y * Width + x;
                    var texel = argb[at];
                    var green = (sbyte)(texel >> 8);
                    var red = (int)((texel >> 16) & 0xFF);
                    var blue = (int)(texel & 0xFF);
                    red += Delta((sbyte)element, green);
                    blue += Delta((sbyte)(element >> 8), green);
                    blue += Delta((sbyte)(element >> 16), (sbyte)red);
                    argb[at] = (texel & 0xFF00FF00u) | ((uint)(red & 0xFF) << 16) | (uint)(blue & 0xFF);
                }
            }

            return argb;
        }

        static int Delta(sbyte multiplier, sbyte colour) => (multiplier * colour) >> 5;
    }

    sealed class Predictor(int width, int size, uint[] blocks) : Transform(width) {
        public override uint[] Undo(uint[] argb, int height) {
            var across = Blocks(Width, size);
            for (var y = 0; y < height; y++) {
                for (var x = 0; x < Width; x++) {
                    var at = y * Width + x;
                    var predicted = y == 0 ? x == 0 ? 0xFF000000u : argb[at - 1]
                        : x == 0 ? argb[at - Width]
                        : Predict((int)((blocks[(y >> size) * across + (x >> size)] >> 8) & 0xF), argb, at, Width);
                    argb[at] = AddLanes(argb[at], predicted);
                }
            }

            return argb;
        }

        /// <summary>
        ///     A texel's prediction from its left, top, top-left and top-right neighbours. The top-right of the last
        ///     column is the first texel of the texel's own row, which is where the next index in memory is.
        /// </summary>
        static uint Predict(int mode, uint[] argb, int at, int width) {
            var left = argb[at - 1];
            var top = argb[at - width];
            var topLeft = argb[at - width - 1];
            var topRight = argb[at - width + 1];
            return mode switch {
                0 => 0xFF000000u,
                1 => left,
                2 => top,
                3 => topRight,
                4 => topLeft,
                5 => Average(Average(left, topRight), top),
                6 => Average(left, topLeft),
                7 => Average(left, top),
                8 => Average(topLeft, top),
                9 => Average(top, topRight),
                10 => Average(Average(left, topLeft), Average(top, topRight)),
                11 => Select(left, top, topLeft),
                12 => Lanes(left, top, topLeft, static (a, b, c) => a + b - c),
                13 => Lanes(Average(left, top), topLeft, 0, static (a, b, _) => a + (a - b) / 2),
                _ => 0xFF000000u
            };
        }

        static uint Average(uint a, uint b) => Lanes(a, b, 0, static (x, y, _) => (x + y) >> 1);

        static uint Select(uint left, uint top, uint topLeft) {
            int towardsLeft = 0, towardsTop = 0;
            for (var shift = 0; shift < 32; shift += 8) {
                var l = (int)((left >> shift) & 0xFF);
                var t = (int)((top >> shift) & 0xFF);
                var p = l + t - (int)((topLeft >> shift) & 0xFF);
                towardsLeft += Math.Abs(p - l);
                towardsTop += Math.Abs(p - t);
            }

            return towardsLeft < towardsTop ? left : top;
        }

        /// <summary>Each lane through <paramref name="lane" />, held to 0 to 255.</summary>
        static uint Lanes(uint a, uint b, uint c, Func<int, int, int, int> lane) {
            var result = 0u;
            for (var shift = 0; shift < 32; shift += 8) {
                var value = lane((int)((a >> shift) & 0xFF), (int)((b >> shift) & 0xFF), (int)((c >> shift) & 0xFF));
                result |= (uint)Math.Clamp(value, 0, 255) << shift;
            }

            return result;
        }
    }

    /// <summary>A canonical prefix code, read from the stream, with a table for codes of up to eight bits.</summary>
    sealed class PrefixCode {
        const int TableBits = 8;
        readonly int single = -1;
        readonly (short Symbol, byte Length)[] table = new (short, byte)[1 << TableBits];
        readonly int[] counts = new int[16];
        readonly int[] symbols = [];

        PrefixCode(int[] lengths) {
            var used = 0;
            var last = 0;
            for (var symbol = 0; symbol < lengths.Length; symbol++) {
                if (lengths[symbol] > 0) {
                    used++;
                    last = symbol;
                    counts[lengths[symbol]]++;
                }
            }

            if (used == 0) {
                throw new InvalidDataException("The WebP has a prefix code with no symbol.");
            }

            if (used == 1) {
                single = last;
                return;
            }

            // Kraft's sum must be exactly one: an incomplete or over-full code is a damaged file.
            var left = 1;
            for (var length = 1; length < 16; length++) {
                left = (left << 1) - counts[length];
                if (left < 0) {
                    throw new InvalidDataException("The WebP has a prefix code with too many short codes.");
                }
            }

            if (left != 0) {
                throw new InvalidDataException("The WebP has a prefix code that is not complete.");
            }

            var offsets = new int[16];
            for (var length = 1; length < 15; length++) {
                offsets[length + 1] = offsets[length] + counts[length];
            }

            symbols = new int[used];
            var codes = new int[lengths.Length];
            var next = new int[16];
            var code = 0;
            for (var length = 1; length < 16; length++) {
                code = (code + counts[length - 1]) << 1;
                next[length] = code;
            }

            for (var symbol = 0; symbol < lengths.Length; symbol++) {
                var length = lengths[symbol];
                if (length == 0) {
                    continue;
                }

                symbols[offsets[length]++] = symbol;
                codes[symbol] = next[length]++;
                if (length <= TableBits) {
                    var reversed = Reverse(codes[symbol], length);
                    for (var fill = reversed; fill < table.Length; fill += 1 << length) {
                        table[fill] = ((short)symbol, (byte)length);
                    }
                }
            }
        }

        public static PrefixCode Read(BitReader bits, int alphabet) {
            var lengths = new int[alphabet];
            if (bits.Read(1) == 1) {
                var count = (int)bits.Read(1) + 1;
                var first = (int)bits.Read(bits.Read(1) == 1 ? 8 : 1);
                Set(lengths, first);
                if (count == 2) {
                    Set(lengths, (int)bits.Read(8));
                }

                return new(lengths);
            }

            var lengthLengths = new int[19];
            var written = (int)bits.Read(4) + 4;
            for (var at = 0; at < written; at++) {
                lengthLengths[CodeLengthOrder[at]] = (int)bits.Read(3);
            }

            var lengthCode = new PrefixCode(lengthLengths);
            var limit = alphabet;
            if (bits.Read(1) == 1) {
                var width = 2 + 2 * (int)bits.Read(3);
                limit = 2 + (int)bits.Read(width);
                if (limit > alphabet) {
                    throw new InvalidDataException("The WebP's prefix code has more lengths than symbols.");
                }
            }

            var previous = 8;
            for (var symbol = 0; symbol < alphabet && limit-- > 0;) {
                var length = lengthCode.Next(bits);
                if (length < 16) {
                    lengths[symbol++] = length;
                    previous = length == 0 ? previous : length;
                    continue;
                }

                var (extra, offset, value) = length switch {
                    16 => (2, 3, previous),
                    17 => (3, 3, 0),
                    _ => (7, 11, 0)
                };
                var repeat = (int)bits.Read(extra) + offset;
                if (symbol + repeat > alphabet) {
                    throw new InvalidDataException("The WebP's prefix code repeats past its last symbol.");
                }

                lengths.AsSpan(symbol, repeat).Fill(value);
                symbol += repeat;
            }

            return new(lengths);
        }

        static void Set(int[] lengths, int symbol) {
            if (symbol >= lengths.Length) {
                throw new InvalidDataException("The WebP's prefix code names a symbol its alphabet does not have.");
            }

            lengths[symbol] = 1;
        }

        /// <summary>The next symbol: from the table when its code is short, and bit by bit when it is not.</summary>
        public int Next(BitReader bits) {
            if (single >= 0) {
                return single;
            }

            var entry = table[bits.Peek(TableBits)];
            if (entry.Length > 0) {
                bits.Skip(entry.Length);
                return entry.Symbol;
            }

            var code = 0;
            var first = 0;
            var index = 0;
            for (var length = 1; length < 16; length++) {
                code |= (int)bits.Read(1);
                var count = counts[length];
                if (code - first < count) {
                    return symbols[index + code - first];
                }

                index += count;
                first = (first + count) << 1;
                code <<= 1;
            }

            throw new InvalidDataException("The WebP has a code no prefix code holds.");
        }

        static int Reverse(int code, int length) {
            var reversed = 0;
            for (var bit = 0; bit < length; bit++) {
                reversed = (reversed << 1) | ((code >> bit) & 1);
            }

            return reversed;
        }
    }

    /// <summary>
    ///     Least significant bit first, which is the order VP8L writes in. A look ahead past the end sees zeros, and
    ///     taking a bit past it is a file cut short.
    /// </summary>
    sealed class BitReader(ReadOnlySpan<byte> data) {
        readonly byte[] bytes = data.ToArray();
        long position;

        public uint Read(int count) {
            var value = Peek(count);
            Skip(count);
            return value;
        }

        public uint Peek(int count) {
            if (count == 0) {
                return 0;
            }

            var at = (int)(position >> 3);
            ulong window = 0;
            for (var step = 0; step < 5 && at + step < bytes.Length; step++) {
                window |= (ulong)bytes[at + step] << (step * 8);
            }

            return (uint)(window >> (int)(position & 7)) & (uint)((1UL << count) - 1);
        }

        public void Skip(int count) {
            position += count;
            if (position > bytes.Length * 8L) {
                throw new InvalidDataException("The WebP ends before its picture does.");
            }
        }
    }
}
