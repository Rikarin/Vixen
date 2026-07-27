// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;

namespace Vixen.Core.Imaging.BlockCompression;

/// <summary>The colour half of a 4×4 block in eight bytes: two 565 endpoints and sixteen two-bit indices.</summary>
/// <remarks>
///     <para>
///         BC1 on its own, and the colour half of BC3. The two endpoints are compared <i>as sixteen-bit
///         numbers</i> to choose the mode, exactly as <see cref="Bc4Block" /> compares its two bytes:
///         <b>colour0 greater than colour1</b> gives four opaque colours, two of them interpolated in
///         thirds; <b>colour0 less than or equal to colour1</b> gives three opaque colours and one
///         index that means transparent black. That is BC1's entire alpha channel — one bit, and it
///         costs a quarter of the colour resolution to use.
///     </para>
///     <para>
///         In BC3 the comparison is ignored and four opaque colours are always assumed, because BC3
///         has a real alpha channel in its other eight bytes. A BC3 encoder that emitted a
///         three-colour block would have its third index silently read as an interpolated colour, so
///         <see cref="Encode" /> is told which contract it is writing under rather than guessing.
///     </para>
/// </remarks>
static class Bc1Block {
    /// <summary>How many bytes one block is.</summary>
    public const int ByteLength = 8;

    /// <summary>How many texels one block covers.</summary>
    public const int Texels = 16;

    /// <summary>Below this, a texel is transparent; at or above it, opaque. BC1 has no third answer.</summary>
    public const byte AlphaCutoff = 128;

    /// <summary>How much of the second endpoint each index is worth, four-colour mode.</summary>
    static readonly float[] FourColourWeights = [0f, 1f, 1f / 3f, 2f / 3f];

    /// <summary>The same, three-colour mode. Index three is transparent and carries no colour.</summary>
    static readonly float[] ThreeColourWeights = [0f, 1f, 0.5f, 0f];

    /// <summary>Builds the four RGBA colours a pair of endpoints stands for.</summary>
    /// <param name="colour0">The first endpoint, as a 565 word.</param>
    /// <param name="colour1">The second endpoint, as a 565 word.</param>
    /// <param name="opaque">Whether the four-colour mode is forced, as it is inside BC3.</param>
    /// <param name="palette">Sixteen bytes to fill: four colours of RGBA.</param>
    public static void Palette(ushort colour0, ushort colour1, bool opaque, Span<byte> palette) {
        Unpack565(colour0, palette);
        Unpack565(colour1, palette[4..]);

        if (opaque || colour0 > colour1) {
            for (var channel = 0; channel < 3; channel++) {
                palette[8 + channel] = (byte)(((2 * palette[channel]) + palette[4 + channel]) / 3);
                palette[12 + channel] = (byte)((palette[channel] + (2 * palette[4 + channel])) / 3);
            }

            palette[11] = 255;
            palette[15] = 255;
            return;
        }

        for (var channel = 0; channel < 3; channel++) {
            palette[8 + channel] = (byte)((palette[channel] + palette[4 + channel]) / 2);
            palette[12 + channel] = 0;
        }

        palette[11] = 255;
        palette[15] = 0;
    }

    /// <summary>Reads a block.</summary>
    /// <param name="block">Its eight bytes.</param>
    /// <param name="opaque">Whether the four-colour mode is forced, as it is inside BC3.</param>
    /// <param name="rgba">Sixty-four bytes to fill: sixteen texels of RGBA, row-major.</param>
    public static void Decode(ReadOnlySpan<byte> block, bool opaque, Span<byte> rgba) {
        Span<byte> palette = stackalloc byte[16];
        Palette(
            BinaryPrimitives.ReadUInt16LittleEndian(block),
            BinaryPrimitives.ReadUInt16LittleEndian(block[2..]),
            opaque,
            palette
        );

        var indices = BinaryPrimitives.ReadUInt32LittleEndian(block[4..]);

        for (var texel = 0; texel < Texels; texel++) {
            var entry = (int)((indices >> (texel * 2)) & 3) * 4;
            palette.Slice(entry, 4).CopyTo(rgba[(texel * 4)..]);
        }
    }

    /// <summary>Writes a block.</summary>
    /// <param name="rgba">Sixty-four bytes: sixteen texels of RGBA, row-major.</param>
    /// <param name="allowAlpha">
    ///     Whether the three-colour mode may be used for cut-out alpha. False inside BC3, where the
    ///     comparison that selects it is not read.
    /// </param>
    /// <param name="block">Eight bytes to fill.</param>
    public static void Encode(ReadOnlySpan<byte> rgba, bool allowAlpha, Span<byte> block) {
        var cutOut = false;

        if (allowAlpha) {
            for (var texel = 0; texel < Texels; texel++) {
                cutOut |= rgba[(texel * 4) + 3] < AlphaCutoff;
            }
        }

        if (cutOut && !AnyOpaque(rgba)) {
            // Nothing to fit an axis through. Two equal endpoints put the block in the three-colour
            // mode, where index three is transparent black; every texel takes it.
            block.Clear();
            block[4] = 0xFF;
            block[5] = 0xFF;
            block[6] = 0xFF;
            block[7] = 0xFF;
            return;
        }

        var (first, second) = PrincipalAxisEndpoints(rgba, cutOut);
        var best = Pack(first, second, rgba, cutOut, block);

        // One least-squares pass. The axis fit places the endpoints at the extremes of the block's
        // colours, which is right only if the colours are spread evenly along it; re-solving them
        // from the indices the fit produced moves them to where the error is actually least.
        Span<byte> candidate = stackalloc byte[ByteLength];

        for (var pass = 0; pass < 2; pass++) {
            if (!Refine(block, rgba, cutOut, out var refinedFirst, out var refinedSecond)) {
                break;
            }

            var error = Pack(refinedFirst, refinedSecond, rgba, cutOut, candidate);

            if (error >= best) {
                break;
            }

            best = error;
            candidate.CopyTo(block);
        }
    }

    /// <summary>Turns a 565 word into an RGBA colour, replicating the high bits into the low ones.</summary>
    /// <param name="colour">The word.</param>
    /// <param name="rgba">Four bytes to fill.</param>
    public static void Unpack565(ushort colour, Span<byte> rgba) {
        var red = (colour >> 11) & 0x1F;
        var green = (colour >> 5) & 0x3F;
        var blue = colour & 0x1F;

        // Replication, not a shift: five ones must come back as 255, and 0xF8 would darken every
        // white texel in the engine by three levels.
        rgba[0] = (byte)((red << 3) | (red >> 2));
        rgba[1] = (byte)((green << 2) | (green >> 4));
        rgba[2] = (byte)((blue << 3) | (blue >> 2));
        rgba[3] = 255;
    }

    /// <summary>Turns an RGB colour into the nearest 565 word.</summary>
    /// <param name="red">Red, 0 to 255.</param>
    /// <param name="green">Green, 0 to 255.</param>
    /// <param name="blue">Blue, 0 to 255.</param>
    /// <returns>The word.</returns>
    public static ushort Pack565(int red, int green, int blue) {
        var quantisedRed = Quantise(red, 31);
        var quantisedGreen = Quantise(green, 63);
        var quantisedBlue = Quantise(blue, 31);

        return (ushort)((quantisedRed << 11) | (quantisedGreen << 5) | quantisedBlue);
    }

    static int Quantise(int value, int levels) =>
        Math.Clamp(((Math.Clamp(value, 0, 255) * levels) + 127) / 255, 0, levels);

    static bool AnyOpaque(ReadOnlySpan<byte> rgba) {
        for (var texel = 0; texel < Texels; texel++) {
            if (rgba[(texel * 4) + 3] >= AlphaCutoff) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Fits a line through the block's opaque colours and takes the two texels furthest apart
    ///     along it. A bounding box would do for most blocks and be visibly wrong for the ones that
    ///     run diagonally through colour space, which is most skin, most foliage and every sunset.
    /// </summary>
    static ((int R, int G, int B) First, (int R, int G, int B) Second) PrincipalAxisEndpoints(
        ReadOnlySpan<byte> rgba,
        bool cutOut
    ) {
        Span<float> mean = stackalloc float[3];
        Span<float> covariance = stackalloc float[9];
        Span<float> centred = stackalloc float[3];
        Span<float> axis = stackalloc float[3];
        var counted = 0;

        for (var texel = 0; texel < Texels; texel++) {
            if (cutOut && rgba[(texel * 4) + 3] < AlphaCutoff) {
                continue;
            }

            for (var channel = 0; channel < 3; channel++) {
                mean[channel] += rgba[(texel * 4) + channel];
            }

            counted++;
        }

        for (var channel = 0; channel < 3; channel++) {
            mean[channel] /= counted;
        }

        for (var texel = 0; texel < Texels; texel++) {
            if (cutOut && rgba[(texel * 4) + 3] < AlphaCutoff) {
                continue;
            }

            for (var channel = 0; channel < 3; channel++) {
                centred[channel] = rgba[(texel * 4) + channel] - mean[channel];
            }

            for (var row = 0; row < 3; row++) {
                for (var column = 0; column < 3; column++) {
                    covariance[(row * 3) + column] += centred[row] * centred[column];
                }
            }
        }

        PrincipalAxis.Find(covariance, 3, axis);

        var lowest = float.MaxValue;
        var highest = float.MinValue;
        var lowestTexel = 0;
        var highestTexel = 0;

        for (var texel = 0; texel < Texels; texel++) {
            if (cutOut && rgba[(texel * 4) + 3] < AlphaCutoff) {
                continue;
            }

            var projection = (rgba[texel * 4] * axis[0])
                + (rgba[(texel * 4) + 1] * axis[1])
                + (rgba[(texel * 4) + 2] * axis[2]);

            if (projection < lowest) {
                lowest = projection;
                lowestTexel = texel;
            }

            if (projection > highest) {
                highest = projection;
                highestTexel = texel;
            }
        }

        return (Texel(rgba, lowestTexel), Texel(rgba, highestTexel));
    }

    static (int R, int G, int B) Texel(ReadOnlySpan<byte> rgba, int texel) =>
        (rgba[texel * 4], rgba[(texel * 4) + 1], rgba[(texel * 4) + 2]);

    /// <summary>
    ///     Re-solves the two endpoints from the indices already chosen, by least squares. Returns
    ///     false when every texel took the same index, which leaves the system singular.
    /// </summary>
    static bool Refine(
        ReadOnlySpan<byte> block,
        ReadOnlySpan<byte> rgba,
        bool cutOut,
        out (int R, int G, int B) first,
        out (int R, int G, int B) second
    ) {
        first = default;
        second = default;

        var indices = BinaryPrimitives.ReadUInt32LittleEndian(block[4..]);

        // Pack orders the endpoints so that cut-out alpha and the three-colour mode are the same
        // thing, which is why this does not need to re-read the comparison to know which it wrote.
        ReadOnlySpan<float> weights = cutOut ? ThreeColourWeights : FourColourWeights;

        float a = 0, b = 0, c = 0;
        Span<float> right0 = stackalloc float[3];
        Span<float> right1 = stackalloc float[3];

        for (var texel = 0; texel < Texels; texel++) {
            var index = (int)((indices >> (texel * 2)) & 3);

            if (cutOut && index == 3) {
                continue;
            }

            var weight = weights[index];
            var inverse = 1f - weight;

            a += inverse * inverse;
            b += inverse * weight;
            c += weight * weight;

            for (var channel = 0; channel < 3; channel++) {
                right0[channel] += inverse * rgba[(texel * 4) + channel];
                right1[channel] += weight * rgba[(texel * 4) + channel];
            }
        }

        var determinant = (a * c) - (b * b);

        if (MathF.Abs(determinant) < 1e-4f) {
            return false;
        }

        Span<int> solved0 = stackalloc int[3];
        Span<int> solved1 = stackalloc int[3];

        for (var channel = 0; channel < 3; channel++) {
            solved0[channel] = (int)MathF.Round(((c * right0[channel]) - (b * right1[channel])) / determinant);
            solved1[channel] = (int)MathF.Round(((a * right1[channel]) - (b * right0[channel])) / determinant);
        }

        first = (solved0[0], solved0[1], solved0[2]);
        second = (solved1[0], solved1[1], solved1[2]);
        return true;
    }

    /// <summary>
    ///     Quantises the endpoints, orders them so the wanted mode is selected, then picks each
    ///     texel's index from the palette <i>a decoder</i> would build — so the encoder cannot
    ///     disagree with the decoder about what it just wrote.
    /// </summary>
    static long Pack(
        (int R, int G, int B) first,
        (int R, int G, int B) second,
        ReadOnlySpan<byte> rgba,
        bool cutOut,
        Span<byte> block
    ) {
        var colour0 = Pack565(first.R, first.G, first.B);
        var colour1 = Pack565(second.R, second.G, second.B);

        // The comparison is the mode bit. Cut-out alpha needs the three-colour mode and everything
        // else wants the four-colour one, and both are had by ordering the same two words.
        if (cutOut ? colour0 > colour1 : colour0 < colour1) {
            (colour0, colour1) = (colour1, colour0);
        }

        BinaryPrimitives.WriteUInt16LittleEndian(block, colour0);
        BinaryPrimitives.WriteUInt16LittleEndian(block[2..], colour1);

        Span<byte> palette = stackalloc byte[16];
        Palette(colour0, colour1, !cutOut, palette);

        uint indices = 0;
        long error = 0;

        for (var texel = 0; texel < Texels; texel++) {
            var texelRgba = rgba.Slice(texel * 4, 4);

            if (cutOut && texelRgba[3] < AlphaCutoff) {
                indices |= 3u << (texel * 2);
                continue;
            }

            var best = 0;
            var bestDistance = long.MaxValue;
            var entries = cutOut ? 3 : 4;

            for (var entry = 0; entry < entries; entry++) {
                var distance = 0L;

                for (var channel = 0; channel < 3; channel++) {
                    var difference = (long)palette[(entry * 4) + channel] - texelRgba[channel];
                    distance += difference * difference;
                }

                if (distance < bestDistance) {
                    bestDistance = distance;
                    best = entry;
                }
            }

            indices |= (uint)best << (texel * 2);
            error += bestDistance;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(block[4..], indices);
        return error;
    }
}
