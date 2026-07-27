// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core.Imaging.BlockCompression;

/// <summary>One channel of a 4×4 block in eight bytes: two endpoints and sixteen three-bit indices.</summary>
/// <remarks>
///     <para>
///         BC4 is the whole of BC4, half of BC5, and the alpha half of BC3, so it is written once
///         here and used three times.
///     </para>
///     <para>
///         The two endpoint bytes carry a mode as well as a value. <b>Red0 greater than red1</b>
///         means the six values between them are interpolated in sevenths — eight usable levels, all
///         inside the block's own range. <b>Red0 less than or equal to red1</b> means only four are
///         interpolated, in fifths, and the last two palette entries are exactly 0 and 255. The
///         second mode spends two of its eight levels on the extremes, which pays only when the block
///         actually contains them.
///     </para>
/// </remarks>
static class Bc4Block {
    /// <summary>How many bytes one block is.</summary>
    public const int ByteLength = 8;

    /// <summary>How many texels one block covers.</summary>
    public const int Texels = 16;

    /// <summary>Builds the eight values the two endpoints stand for.</summary>
    /// <param name="red0">The first endpoint.</param>
    /// <param name="red1">The second endpoint.</param>
    /// <param name="palette">Eight bytes to fill.</param>
    public static void Palette(byte red0, byte red1, Span<byte> palette) {
        palette[0] = red0;
        palette[1] = red1;

        if (red0 > red1) {
            for (var step = 1; step <= 6; step++) {
                palette[1 + step] = (byte)((((7 - step) * red0) + (step * red1)) / 7);
            }

            return;
        }

        for (var step = 1; step <= 4; step++) {
            palette[1 + step] = (byte)((((5 - step) * red0) + (step * red1)) / 5);
        }

        palette[6] = 0;
        palette[7] = 255;
    }

    /// <summary>Reads a block.</summary>
    /// <param name="block">Its eight bytes.</param>
    /// <param name="values">Sixteen bytes to fill, row-major.</param>
    public static void Decode(ReadOnlySpan<byte> block, Span<byte> values) {
        Span<byte> palette = stackalloc byte[8];
        Palette(block[0], block[1], palette);

        var indices = (ulong)block[2]
            | ((ulong)block[3] << 8)
            | ((ulong)block[4] << 16)
            | ((ulong)block[5] << 24)
            | ((ulong)block[6] << 32)
            | ((ulong)block[7] << 40);

        for (var texel = 0; texel < Texels; texel++) {
            values[texel] = palette[(int)((indices >> (texel * 3)) & 7)];
        }
    }

    /// <summary>Writes a block.</summary>
    /// <param name="values">Sixteen bytes, row-major.</param>
    /// <param name="block">Eight bytes to fill.</param>
    public static void Encode(ReadOnlySpan<byte> values, Span<byte> block) {
        byte lowest = 255;
        byte highest = 0;
        byte innerLowest = 255;
        byte innerHighest = 0;

        foreach (var value in values) {
            lowest = Math.Min(lowest, value);
            highest = Math.Max(highest, value);

            // The six-value mode gets 0 and 255 for free, so its endpoints are better spent on the
            // range strictly inside them.
            if (value > 0) {
                innerLowest = Math.Min(innerLowest, value);
            }

            if (value < 255) {
                innerHighest = Math.Max(innerHighest, value);
            }
        }

        if (innerLowest > innerHighest) {
            (innerLowest, innerHighest) = (lowest, highest);
        }

        // Eight-value mode wants red0 > red1, six-value mode wants red0 <= red1; the ordering of the
        // two bytes *is* the mode bit, so there is nothing else to write.
        if (SquaredError(highest, lowest, values) <= SquaredError(innerLowest, innerHighest, values)) {
            Write(highest, lowest, values, block);
            return;
        }

        Write(innerLowest, innerHighest, values, block);
    }

    static void Write(byte red0, byte red1, ReadOnlySpan<byte> values, Span<byte> block) {
        Span<byte> palette = stackalloc byte[8];
        Palette(red0, red1, palette);

        block[0] = red0;
        block[1] = red1;

        ulong indices = 0;

        for (var texel = 0; texel < Texels; texel++) {
            indices |= (ulong)Nearest(palette, values[texel]) << (texel * 3);
        }

        for (var byteIndex = 0; byteIndex < 6; byteIndex++) {
            block[2 + byteIndex] = (byte)(indices >> (byteIndex * 8));
        }
    }

    static int SquaredError(byte red0, byte red1, ReadOnlySpan<byte> values) {
        Span<byte> palette = stackalloc byte[8];
        Palette(red0, red1, palette);

        var total = 0;

        foreach (var value in values) {
            var difference = palette[Nearest(palette, value)] - value;
            total += difference * difference;
        }

        return total;
    }

    static int Nearest(ReadOnlySpan<byte> palette, byte value) {
        var best = 0;
        var bestDistance = int.MaxValue;

        for (var entry = 0; entry < palette.Length; entry++) {
            var distance = Math.Abs(palette[entry] - value);

            if (distance < bestDistance) {
                bestDistance = distance;
                best = entry;
            }
        }

        return best;
    }

    /// <summary>Reads the sixteen three-bit indices out of a block, for tests and tools.</summary>
    /// <param name="block">Its eight bytes.</param>
    /// <param name="indices">Sixteen bytes to fill.</param>
    public static void ReadIndices(ReadOnlySpan<byte> block, Span<byte> indices) {
        ulong packed = 0;

        for (var byteIndex = 0; byteIndex < 6; byteIndex++) {
            packed |= (ulong)block[2 + byteIndex] << (byteIndex * 8);
        }

        for (var texel = 0; texel < Texels; texel++) {
            indices[texel] = (byte)((packed >> (texel * 3)) & 7);
        }
    }
}
