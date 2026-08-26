// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core.Imaging.BlockCompression;

/// <summary>A 4×4 block of HDR colour in sixteen bytes, written in BC6H's mode 11.</summary>
/// <remarks>
///     <para>
///         BC6H is the only compressed format that holds high dynamic range, which makes it the one
///         a prefiltered environment map ships in — six faces of half-float at 8 bytes a texel is
///         four times what the same cube costs here.
///     </para>
///     <para>
///         <b>Unsigned only, and mode 11 only.</b> BC6H has fourteen modes; ten of them partition the
///         block in two and nine store the second endpoint as a delta from the first. Mode 11 is the
///         plain one: one subset, one line through RGB, ten-bit endpoints stored outright. It is the
///         mode a full encoder picks for a smooth block, which an environment map is almost entirely
///         made of, and it is a real ceiling on a block containing a light source edge. The signed
///         variant is not written at all — nothing in the engine stores negative radiance.
///     </para>
///     <para>
///         <b>Endpoints are fitted in half-float bit space, not in linear light.</b> A half's bit
///         pattern is monotonic in its value and its steps are proportional rather than absolute,
///         which is the behaviour an HDR error metric wants: being ten units wrong at a radiance of
///         ten thousand does not matter, and being ten units wrong at a radiance of one does.
///     </para>
///     <para>
///         <b>Infinity is not representable, and that is the format being careful rather than this
///         encoder being careful.</b> The largest endpoint, 1023, widens to 0xFFFF and finishes as
///         the half bit pattern 0x7BFF — 65 504, the largest <i>finite</i> half. Since finishing
///         only ever scales down, no pair of endpoints and no index between them can produce 0x7C00.
///         An infinity or a NaN arriving in the source is clamped on the way in, where it is still
///         obvious what happened.
///     </para>
///     <para>
///         <b>Checked against an independent BC6H decoder, and it agreed on every block.</b>
///         <c>BcnReferenceDecoderTests</c> puts four thousand mode-11 blocks and the encoder's own
///         output past bcdec and gets identical halves — the only format here that was right the
///         first time along with BC7, because BC6H's specification is bit-exact about its rounding
///         and BC1's is not. ⚠ Mode 11 is all that was checked, since it is all this reads; the
///         other thirteen are unverified in both directions.
///     </para>
/// </remarks>
static class Bc6HBlock {
    /// <summary>How many bytes one block is.</summary>
    public const int ByteLength = 16;

    /// <summary>How many texels one block covers.</summary>
    public const int Texels = 16;

    /// <summary>The one mode written here: one subset, no delta, ten-bit endpoints.</summary>
    public const int Mode = 11;

    /// <summary>What the five mode bits hold for it.</summary>
    public const uint ModeBits = 0b00011;

    /// <summary>The largest ten-bit endpoint. Its value is half's largest finite number.</summary>
    public const int LargestEndpoint = 1023;

    /// <summary>Reads a block.</summary>
    /// <param name="block">Its sixteen bytes.</param>
    /// <param name="rgb">Forty-eight half-float bit patterns to fill: sixteen texels of RGB, row-major.</param>
    /// <exception cref="NotSupportedException">The block is in one of the thirteen modes this does not write.</exception>
    public static void Decode(ReadOnlySpan<byte> block, Span<ushort> rgb) {
        var reader = new BlockBitReader(block);
        var mode = reader.Read(2);

        if (mode >= 2) {
            mode |= reader.Read(3) << 2;
        }

        if (mode != ModeBits) {
            throw new NotSupportedException(
                $"This BC6H block is in mode bits {mode:b5}, and only mode {Mode} — bits {ModeBits:b5} — is "
                + "decoded here. Nothing in the engine decodes BC6H at run time; this exists to check what "
                + "Vixen's own encoder wrote."
            );
        }

        Span<int> first = stackalloc int[3];
        Span<int> second = stackalloc int[3];

        for (var channel = 0; channel < 3; channel++) {
            first[channel] = (int)reader.Read(10);
        }

        for (var channel = 0; channel < 3; channel++) {
            second[channel] = (int)reader.Read(10);
        }

        Span<ushort> palette = stackalloc ushort[16 * 3];
        Palette(first, second, palette);

        for (var texel = 0; texel < Texels; texel++) {
            // As in BC7, the anchor's top index bit is not stored.
            var index = (int)reader.Read(texel == 0 ? 3 : 4);
            palette.Slice(index * 3, 3).CopyTo(rgb[(texel * 3)..]);
        }
    }

    /// <summary>Writes a block.</summary>
    /// <param name="rgb">Forty-eight half-float bit patterns: sixteen texels of RGB, row-major.</param>
    /// <param name="block">Sixteen bytes to fill.</param>
    public static void Encode(ReadOnlySpan<ushort> rgb, Span<byte> block) {
        Span<float> low = stackalloc float[3];
        Span<float> high = stackalloc float[3];
        PrincipalAxisEndpoints(rgb, low, high);

        Span<int> first = stackalloc int[3];
        Span<int> second = stackalloc int[3];
        Span<byte> indices = stackalloc byte[Texels];
        Span<int> bestFirst = stackalloc int[3];
        Span<int> bestSecond = stackalloc int[3];
        Span<byte> bestIndices = stackalloc byte[Texels];

        Quantise(low, first);
        Quantise(high, second);
        var best = Assign(rgb, first, second, indices);
        first.CopyTo(bestFirst);
        second.CopyTo(bestSecond);
        indices.CopyTo(bestIndices);

        for (var pass = 0; pass < 2; pass++) {
            if (!Solve(rgb, bestIndices, low, high)) {
                break;
            }

            Quantise(low, first);
            Quantise(high, second);
            var error = Assign(rgb, first, second, indices);

            if (error >= best) {
                break;
            }

            best = error;
            first.CopyTo(bestFirst);
            second.CopyTo(bestSecond);
            indices.CopyTo(bestIndices);
        }

        if (bestIndices[0] > 7) {
            for (var channel = 0; channel < 3; channel++) {
                (bestFirst[channel], bestSecond[channel]) = (bestSecond[channel], bestFirst[channel]);
            }

            for (var texel = 0; texel < Texels; texel++) {
                bestIndices[texel] = (byte)(15 - bestIndices[texel]);
            }
        }

        var writer = new BlockBitWriter(block);
        writer.Write(ModeBits, 5);

        for (var channel = 0; channel < 3; channel++) {
            writer.Write((uint)bestFirst[channel], 10);
        }

        for (var channel = 0; channel < 3; channel++) {
            writer.Write((uint)bestSecond[channel], 10);
        }

        for (var texel = 0; texel < Texels; texel++) {
            writer.Write(bestIndices[texel], texel == 0 ? 3 : 4);
        }
    }

    /// <summary>Builds the sixteen colours a pair of ten-bit endpoints stands for.</summary>
    /// <param name="first">The first endpoint's three channels.</param>
    /// <param name="second">The second endpoint's three channels.</param>
    /// <param name="palette">Forty-eight half-float bit patterns to fill.</param>
    public static void Palette(ReadOnlySpan<int> first, ReadOnlySpan<int> second, Span<ushort> palette) {
        Span<int> a = stackalloc int[3];
        Span<int> b = stackalloc int[3];

        for (var channel = 0; channel < 3; channel++) {
            a[channel] = Unquantise(first[channel]);
            b[channel] = Unquantise(second[channel]);
        }

        for (var entry = 0; entry < 16; entry++) {
            var weight = Bc7Block.Weights[entry];

            for (var channel = 0; channel < 3; channel++) {
                var interpolated = ((a[channel] * (64 - weight)) + (b[channel] * weight) + 32) >> 6;
                palette[(entry * 3) + channel] = (ushort)Finish(interpolated);
            }
        }
    }

    /// <summary>Widens a ten-bit endpoint to the sixteen-bit value the interpolation runs on.</summary>
    /// <param name="endpoint">The endpoint, 0 to 1023.</param>
    /// <returns>The widened value.</returns>
    public static int Unquantise(int endpoint) => endpoint switch {
        0 => 0,
        1023 => 0xFFFF,
        _ => ((endpoint << 15) + 0x4000) >> 9
    };

    /// <summary>Scales an interpolated value into a half-float bit pattern.</summary>
    /// <param name="interpolated">The interpolated sixteen-bit value.</param>
    /// <returns>The half's bits.</returns>
    public static int Finish(int interpolated) => (interpolated * 31) >> 6;

    /// <summary>The half-float bit pattern one endpoint on its own decodes to.</summary>
    /// <param name="endpoint">The endpoint, 0 to 1023.</param>
    /// <returns>The half's bits.</returns>
    public static int EndpointValue(int endpoint) => Finish(Unquantise(endpoint));

    /// <summary>
    ///     Rounds a value in half-float bit space to the nearest usable ten-bit endpoint. Bisection
    ///     rather than an inverted formula, because <see cref="EndpointValue" /> has two special
    ///     cases in it and the closed form of "the inverse except near both ends" is a place to hide
    ///     an off-by-one.
    /// </summary>
    /// <param name="value">The wanted half-float bit pattern.</param>
    /// <returns>The endpoint.</returns>
    public static int Quantise(float value) {
        var wanted = (int)MathF.Round(Math.Clamp(value, 0f, EndpointValue(LargestEndpoint)));
        var low = 0;
        var high = LargestEndpoint;

        while (low < high) {
            var middle = (low + high) / 2;

            if (EndpointValue(middle) < wanted) {
                low = middle + 1;
            } else {
                high = middle;
            }
        }

        if (low > 0 && wanted - EndpointValue(low - 1) < EndpointValue(low) - wanted) {
            low--;
        }

        return low;
    }

    static void Quantise(ReadOnlySpan<float> endpoint, Span<int> quantised) {
        for (var channel = 0; channel < 3; channel++) {
            quantised[channel] = Quantise(endpoint[channel]);
        }
    }

    static long Assign(ReadOnlySpan<ushort> rgb, ReadOnlySpan<int> first, ReadOnlySpan<int> second, Span<byte> indices) {
        Span<ushort> palette = stackalloc ushort[16 * 3];
        Palette(first, second, palette);

        var total = 0L;

        for (var texel = 0; texel < Texels; texel++) {
            var best = 0;
            var bestDistance = long.MaxValue;

            for (var entry = 0; entry < 16; entry++) {
                var distance = 0L;

                for (var channel = 0; channel < 3; channel++) {
                    var difference = (long)palette[(entry * 3) + channel] - rgb[(texel * 3) + channel];
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

    static bool Solve(ReadOnlySpan<ushort> rgb, ReadOnlySpan<byte> indices, Span<float> low, Span<float> high) {
        float a = 0, b = 0, c = 0;
        Span<float> right0 = stackalloc float[3];
        Span<float> right1 = stackalloc float[3];

        for (var texel = 0; texel < Texels; texel++) {
            var weight = Bc7Block.Weights[indices[texel]] / 64f;
            var inverse = 1f - weight;

            a += inverse * inverse;
            b += inverse * weight;
            c += weight * weight;

            for (var channel = 0; channel < 3; channel++) {
                right0[channel] += inverse * rgb[(texel * 3) + channel];
                right1[channel] += weight * rgb[(texel * 3) + channel];
            }
        }

        var determinant = (a * c) - (b * b);

        if (MathF.Abs(determinant) < 1e-4f) {
            return false;
        }

        for (var channel = 0; channel < 3; channel++) {
            low[channel] = ((c * right0[channel]) - (b * right1[channel])) / determinant;
            high[channel] = ((a * right1[channel]) - (b * right0[channel])) / determinant;
        }

        return true;
    }

    static void PrincipalAxisEndpoints(ReadOnlySpan<ushort> rgb, Span<float> low, Span<float> high) {
        Span<float> mean = stackalloc float[3];
        Span<float> covariance = stackalloc float[9];
        Span<float> centred = stackalloc float[3];
        Span<float> axis = stackalloc float[3];

        for (var texel = 0; texel < Texels; texel++) {
            for (var channel = 0; channel < 3; channel++) {
                mean[channel] += rgb[(texel * 3) + channel];
            }
        }

        for (var channel = 0; channel < 3; channel++) {
            mean[channel] /= Texels;
        }

        for (var texel = 0; texel < Texels; texel++) {
            for (var channel = 0; channel < 3; channel++) {
                centred[channel] = rgb[(texel * 3) + channel] - mean[channel];
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
            var projection = (rgb[texel * 3] * axis[0])
                + (rgb[(texel * 3) + 1] * axis[1])
                + (rgb[(texel * 3) + 2] * axis[2]);

            if (projection < lowest) {
                lowest = projection;
                lowestTexel = texel;
            }

            if (projection > highest) {
                highest = projection;
                highestTexel = texel;
            }
        }

        for (var channel = 0; channel < 3; channel++) {
            low[channel] = rgb[(lowestTexel * 3) + channel];
            high[channel] = rgb[(highestTexel * 3) + channel];
        }
    }
}
