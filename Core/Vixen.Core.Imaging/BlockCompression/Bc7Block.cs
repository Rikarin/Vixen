// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core.Imaging.BlockCompression;

/// <summary>A 4×4 RGBA block in sixteen bytes, written in BC7's mode 6.</summary>
/// <remarks>
///     <para>
///         <b>Mode 6 only, and that is a quality ceiling rather than a shortcut with no cost.</b> BC7
///         has eight modes; the other seven divide the block into two or three subsets along one of
///         sixty-four partition patterns, which is what lets BC7 hold an edge between two materials
///         without smearing them together. Mode 6 is the single-subset one: one line through RGBA
///         space, seven-bit endpoints with a shared low bit, and four-bit indices. On smooth content
///         — which is most of a texture — it is the mode a full encoder picks anyway. On a block with
///         a hard edge through it, a partitioned mode would be visibly better and this will not find
///         it.
///     </para>
///     <para>
///         What it does produce is <i>valid</i> BC7 that any decoder reads, at the right size, so a
///         build ships and a device runs. Doc 03 calls for the native encoder (`ispc_texcomp`) for
///         production quality and doc 01 registers it; this is what the engine uses until that is
///         bound, and what it falls back to on a machine that has not restored it.
///     </para>
///     <para>
///         <b>Decoding is mode 6 only as well.</b> Nothing in the engine decodes BC7 at run time —
///         the runtime uploads blocks — so the decoder exists to check the encoder and to show a
///         preview of what this wrote. A block from another encoder will say so rather than be
///         misread.
///     </para>
/// </remarks>
static class Bc7Block {
    /// <summary>How many bytes one block is.</summary>
    public const int ByteLength = 16;

    /// <summary>How many texels one block covers.</summary>
    public const int Texels = 16;

    /// <summary>The one mode written here.</summary>
    public const int Mode = 6;

    /// <summary>How far along the endpoint line each four-bit index is, in sixty-fourths.</summary>
    /// <remarks>
    ///     Not evenly spaced, and not a rounding of an even spacing either — the table is in the
    ///     specification and is symmetric about its middle, which is what makes swapping the two
    ///     endpoints and inverting every index produce the identical block.
    /// </remarks>
    public static ReadOnlySpan<byte> Weights => [0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64];

    /// <summary>Reads a block.</summary>
    /// <param name="block">Its sixteen bytes.</param>
    /// <param name="rgba">Sixty-four bytes to fill: sixteen texels of RGBA, row-major.</param>
    /// <exception cref="NotSupportedException">The block is in one of the seven modes this does not write.</exception>
    public static void Decode(ReadOnlySpan<byte> block, Span<byte> rgba) {
        var reader = new BlockBitReader(block);
        var mode = 0;

        // The mode is unary: as many zeros as the mode number, then a one.
        while (mode < 8 && reader.Read(1) == 0) {
            mode++;
        }

        if (mode != Mode) {
            throw new NotSupportedException(
                $"This BC7 block is mode {(mode < 8 ? mode : 8)}, and only mode {Mode} is decoded here. Nothing "
                + "in the engine decodes BC7 at run time — the runtime uploads blocks — so this decoder exists "
                + "to check what Vixen's own encoder wrote."
            );
        }

        Span<uint> quantised = stackalloc uint[8];

        for (var field = 0; field < 8; field++) {
            quantised[field] = reader.Read(7);
        }

        var parity0 = reader.Read(1);
        var parity1 = reader.Read(1);

        Span<byte> first = stackalloc byte[4];
        Span<byte> second = stackalloc byte[4];

        for (var channel = 0; channel < 4; channel++) {
            first[channel] = (byte)((quantised[channel * 2] << 1) | parity0);
            second[channel] = (byte)((quantised[(channel * 2) + 1] << 1) | parity1);
        }

        Span<byte> palette = stackalloc byte[16 * 4];
        Palette(first, second, palette);

        for (var texel = 0; texel < Texels; texel++) {
            // Texel zero is the anchor: its top bit is not stored, because the encoder is required to
            // order the endpoints so that it is zero. That is the whole of BC7's index-inversion rule.
            var index = (int)reader.Read(texel == 0 ? 3 : 4);
            palette.Slice(index * 4, 4).CopyTo(rgba[(texel * 4)..]);
        }
    }

    /// <summary>Writes a block.</summary>
    /// <param name="rgba">Sixty-four bytes: sixteen texels of RGBA, row-major.</param>
    /// <param name="block">Sixteen bytes to fill.</param>
    public static void Encode(ReadOnlySpan<byte> rgba, Span<byte> block) {
        Span<float> low = stackalloc float[4];
        Span<float> high = stackalloc float[4];
        PrincipalAxisEndpoints(rgba, low, high);

        Span<byte> first = stackalloc byte[4];
        Span<byte> second = stackalloc byte[4];
        Span<byte> indices = stackalloc byte[Texels];
        Span<byte> bestFirst = stackalloc byte[4];
        Span<byte> bestSecond = stackalloc byte[4];
        Span<byte> bestIndices = stackalloc byte[Texels];
        uint bestParity0 = 0;
        uint bestParity1 = 0;
        var best = long.MaxValue;

        // The two parity bits are the eighth bit of every channel of their endpoint, shared across
        // all four. Four combinations is few enough to try them all rather than reason about which
        // one the block wants.
        for (uint parity0 = 0; parity0 < 2; parity0++) {
            for (uint parity1 = 0; parity1 < 2; parity1++) {
                Quantise(low, parity0, first);
                Quantise(high, parity1, second);
                var error = Assign(rgba, first, second, indices);

                if (error >= best) {
                    continue;
                }

                best = error;
                bestParity0 = parity0;
                bestParity1 = parity1;
                first.CopyTo(bestFirst);
                second.CopyTo(bestSecond);
                indices.CopyTo(bestIndices);
            }
        }

        // Two least-squares passes, same reasoning as BC1: the axis fit puts the endpoints at the
        // extremes, and re-solving them from the indices moves them to where the error is least.
        for (var pass = 0; pass < 2; pass++) {
            if (!Solve(rgba, bestIndices, low, high)) {
                break;
            }

            var improved = false;

            for (uint parity0 = 0; parity0 < 2; parity0++) {
                for (uint parity1 = 0; parity1 < 2; parity1++) {
                    Quantise(low, parity0, first);
                    Quantise(high, parity1, second);
                    var error = Assign(rgba, first, second, indices);

                    if (error >= best) {
                        continue;
                    }

                    best = error;
                    bestParity0 = parity0;
                    bestParity1 = parity1;
                    first.CopyTo(bestFirst);
                    second.CopyTo(bestSecond);
                    indices.CopyTo(bestIndices);
                    improved = true;
                }
            }

            if (!improved) {
                break;
            }
        }

        // The anchor's top index bit is not stored, so it has to be zero. Swapping the endpoints and
        // inverting every index describes the identical palette — the weight table is symmetric —
        // so this costs nothing.
        if (bestIndices[0] > 7) {
            for (var channel = 0; channel < 4; channel++) {
                (bestFirst[channel], bestSecond[channel]) = (bestSecond[channel], bestFirst[channel]);
            }

            (bestParity0, bestParity1) = (bestParity1, bestParity0);

            for (var texel = 0; texel < Texels; texel++) {
                bestIndices[texel] = (byte)(15 - bestIndices[texel]);
            }
        }

        var writer = new BlockBitWriter(block);
        writer.Write(1u << Mode, Mode + 1);

        for (var channel = 0; channel < 4; channel++) {
            writer.Write((uint)(bestFirst[channel] >> 1), 7);
            writer.Write((uint)(bestSecond[channel] >> 1), 7);
        }

        writer.Write(bestParity0, 1);
        writer.Write(bestParity1, 1);

        for (var texel = 0; texel < Texels; texel++) {
            writer.Write(bestIndices[texel], texel == 0 ? 3 : 4);
        }
    }

    /// <summary>Builds the sixteen colours a pair of endpoints stands for.</summary>
    /// <param name="first">The first endpoint's four channels.</param>
    /// <param name="second">The second endpoint's four channels.</param>
    /// <param name="palette">Sixty-four bytes to fill.</param>
    public static void Palette(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second, Span<byte> palette) {
        for (var entry = 0; entry < 16; entry++) {
            var weight = Weights[entry];

            for (var channel = 0; channel < 4; channel++) {
                palette[(entry * 4) + channel] =
                    (byte)((((first[channel] * (64 - weight)) + (second[channel] * weight)) + 32) >> 6);
            }
        }
    }

    /// <summary>Rounds an endpoint to what seven bits and a shared parity bit can hold.</summary>
    static void Quantise(ReadOnlySpan<float> endpoint, uint parity, Span<byte> quantised) {
        for (var channel = 0; channel < 4; channel++) {
            var wanted = Math.Clamp(endpoint[channel], 0f, 255f);
            var seven = (uint)Math.Clamp((int)MathF.Round((wanted - parity) / 2f), 0, 127);
            quantised[channel] = (byte)((seven << 1) | parity);
        }
    }

    static long Assign(ReadOnlySpan<byte> rgba, ReadOnlySpan<byte> first, ReadOnlySpan<byte> second, Span<byte> indices) {
        Span<byte> palette = stackalloc byte[16 * 4];
        Palette(first, second, palette);

        var total = 0L;

        for (var texel = 0; texel < Texels; texel++) {
            var best = 0;
            var bestDistance = long.MaxValue;

            for (var entry = 0; entry < 16; entry++) {
                var distance = 0L;

                for (var channel = 0; channel < 4; channel++) {
                    var difference = (long)palette[(entry * 4) + channel] - rgba[(texel * 4) + channel];
                    distance += difference * difference;
                }

                if (distance < bestDistance) {
                    bestDistance = distance;
                    best = entry;
                }
            }

            indices[texel] = (byte)best;
            total += bestDistance;
        }

        return total;
    }

    /// <summary>
    ///     Re-solves the endpoints from the indices by least squares. False when every texel took the
    ///     same index and there is nothing to solve.
    /// </summary>
    static bool Solve(ReadOnlySpan<byte> rgba, ReadOnlySpan<byte> indices, Span<float> low, Span<float> high) {
        float a = 0, b = 0, c = 0;
        Span<float> right0 = stackalloc float[4];
        Span<float> right1 = stackalloc float[4];

        for (var texel = 0; texel < Texels; texel++) {
            var weight = Weights[indices[texel]] / 64f;
            var inverse = 1f - weight;

            a += inverse * inverse;
            b += inverse * weight;
            c += weight * weight;

            for (var channel = 0; channel < 4; channel++) {
                right0[channel] += inverse * rgba[(texel * 4) + channel];
                right1[channel] += weight * rgba[(texel * 4) + channel];
            }
        }

        var determinant = (a * c) - (b * b);

        if (MathF.Abs(determinant) < 1e-4f) {
            return false;
        }

        for (var channel = 0; channel < 4; channel++) {
            low[channel] = ((c * right0[channel]) - (b * right1[channel])) / determinant;
            high[channel] = ((a * right1[channel]) - (b * right0[channel])) / determinant;
        }

        return true;
    }

    /// <summary>
    ///     The two texels furthest apart along the block's principal axis in RGBA — four dimensions
    ///     rather than BC1's three, because mode 6 fits alpha on the same line as colour.
    /// </summary>
    static void PrincipalAxisEndpoints(ReadOnlySpan<byte> rgba, Span<float> low, Span<float> high) {
        Span<float> mean = stackalloc float[4];
        Span<float> covariance = stackalloc float[16];
        Span<float> centred = stackalloc float[4];
        Span<float> axis = stackalloc float[4];

        for (var texel = 0; texel < Texels; texel++) {
            for (var channel = 0; channel < 4; channel++) {
                mean[channel] += rgba[(texel * 4) + channel];
            }
        }

        for (var channel = 0; channel < 4; channel++) {
            mean[channel] /= Texels;
        }

        for (var texel = 0; texel < Texels; texel++) {
            for (var channel = 0; channel < 4; channel++) {
                centred[channel] = rgba[(texel * 4) + channel] - mean[channel];
            }

            for (var row = 0; row < 4; row++) {
                for (var column = 0; column < 4; column++) {
                    covariance[(row * 4) + column] += centred[row] * centred[column];
                }
            }
        }

        PrincipalAxis.Find(covariance, 4, axis);

        var lowest = float.MaxValue;
        var highest = float.MinValue;
        var lowestTexel = 0;
        var highestTexel = 0;

        for (var texel = 0; texel < Texels; texel++) {
            var projection = 0f;

            for (var channel = 0; channel < 4; channel++) {
                projection += rgba[(texel * 4) + channel] * axis[channel];
            }

            if (projection < lowest) {
                lowest = projection;
                lowestTexel = texel;
            }

            if (projection > highest) {
                highest = projection;
                highestTexel = texel;
            }
        }

        for (var channel = 0; channel < 4; channel++) {
            low[channel] = rgba[(lowestTexel * 4) + channel];
            high[channel] = rgba[(highestTexel * 4) + channel];
        }
    }
}
