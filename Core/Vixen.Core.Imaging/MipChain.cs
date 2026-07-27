// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics;

namespace Vixen.Core.Imaging;

/// <summary>Mip level extents, and filling a chain in.</summary>
/// <remarks>
///     Only uncompressed eight-bit-per-channel formats can be reduced here, and that is not a gap:
///     a mip chain is generated <b>before</b> compression, from the source pixels, because reducing
///     already-compressed blocks means decoding, filtering and re-encoding, and each round of that
///     loses more than the filter ever gains.
/// </remarks>
public static class MipChain {
    /// <summary>What one level's extent is.</summary>
    /// <param name="width">The largest level's width.</param>
    /// <param name="height">The largest level's height.</param>
    /// <param name="depth">The largest level's depth.</param>
    /// <param name="level">Which level.</param>
    /// <returns>Its extent, never smaller than one in any dimension.</returns>
    public static (int Width, int Height, int Depth) ExtentOf(int width, int height, int depth, int level) {
        ArgumentOutOfRangeException.ThrowIfNegative(level);

        return (
            Math.Max(1, width >> level),
            Math.Max(1, height >> level),
            Math.Max(1, depth >> level)
        );
    }

    /// <summary>Fills every level below the largest with a plain box filter, in place.</summary>
    /// <param name="texture">The texture, whose level zero is already filled.</param>
    /// <exception cref="NotSupportedException">Its format is compressed, or is not eight bits per channel.</exception>
    /// <remarks>
    ///     Averages the stored values, which is right only for a linear, opaque, non-directional
    ///     texture. Everything else wants <see cref="Generate(TextureData, MipOptions)" /> and a
    ///     <see cref="MipOptions" /> that says what the texture holds.
    /// </remarks>
    public static void Generate(TextureData texture) => Generate(texture, MipOptions.Linear);

    /// <summary>Fills every level below the largest by averaging, in place.</summary>
    /// <param name="texture">The texture, whose level zero is already filled.</param>
    /// <param name="options">What the texture holds, which is what decides how to average it.</param>
    /// <exception cref="NotSupportedException">Its format is compressed, or is not eight bits per channel.</exception>
    /// <exception cref="ArgumentException">The options contradict each other or the format.</exception>
    /// <remarks>
    ///     <para>
    ///         A box filter: each destination texel is the mean of the up-to-four source texels under
    ///         it. What "mean" means is <see cref="MipOptions" />'s business — averaging colour in
    ///         linear light, letting alpha weight the colour, and treating a normal as a direction
    ///         are all the same loop with a different definition of the sum.
    ///     </para>
    ///     <para>
    ///         <b>The result is rounded, not truncated.</b> Truncating loses half a level on average
    ///         at every step, and a chain is ten steps deep: the smallest mips of a texture come out
    ///         measurably darker than its largest, which reads as distant surfaces being dimmer than
    ///         near ones for no reason anybody can point at.
    ///     </para>
    /// </remarks>
    public static void Generate(TextureData texture, MipOptions options) {
        ArgumentNullException.ThrowIfNull(texture);

        var channels = ChannelsOf(texture.Format);

        if (options.Srgb && options.RenormaliseNormals) {
            throw new ArgumentException(
                "A texture cannot be both colour and a normal map. sRGB averaging applies a transfer "
                + "function to values that are a direction, and renormalising applies a length to values "
                + "that are a colour; asking for both means one of the two settings came from the wrong place.",
                nameof(options)
            );
        }

        if (options.AlphaWeighted && channels < 4) {
            throw new ArgumentException(
                $"{texture.Format} has no alpha channel to weight by.",
                nameof(options)
            );
        }

        if (options.RenormaliseNormals && channels < 2) {
            throw new ArgumentException(
                $"{texture.Format} has one channel, which cannot hold a direction.",
                nameof(options)
            );
        }

        Span<float> sum = stackalloc float[4];
        Span<int> sourceTexels = stackalloc int[4];

        for (var level = 1; level < texture.LevelCount; level++) {
            var source = texture.Levels[level - 1];
            var destination = texture.Levels[level];
            var from = texture.Level(level - 1);
            var to = texture.LevelSpan(level);

            for (var y = 0; y < destination.Height; y++) {
                for (var x = 0; x < destination.Width; x++) {
                    var taken = 0;

                    for (var dy = 0; dy < 2; dy++) {
                        var sourceY = (y * 2) + dy;

                        // A dimension that has already reached one has nothing at its second row or
                        // column, and reading it would run off the end into the next row of texels.
                        if (sourceY >= source.Height) {
                            continue;
                        }

                        for (var dx = 0; dx < 2; dx++) {
                            var sourceX = (x * 2) + dx;

                            if (sourceX >= source.Width) {
                                continue;
                            }

                            sourceTexels[taken++] = (((sourceY * source.Width) + sourceX) * channels);
                        }
                    }

                    var into = ((y * destination.Width) + x) * channels;

                    if (options.RenormaliseNormals) {
                        AverageDirection(from, sourceTexels[..taken], channels, sum);
                    } else {
                        AverageColour(from, sourceTexels[..taken], channels, options, sum);
                    }

                    for (var channel = 0; channel < channels; channel++) {
                        to[into + channel] = (byte)Math.Clamp((int)MathF.Round(sum[channel]), 0, 255);
                    }
                }
            }
        }
    }

    /// <summary>
    ///     The mean of up to four texels, in linear light if the caller says so and weighted by alpha
    ///     if the caller says so. Alpha itself is averaged plainly in both cases: it is neither a
    ///     colour nor weighted by itself.
    /// </summary>
    static void AverageColour(
        ReadOnlySpan<byte> from,
        ReadOnlySpan<int> texels,
        int channels,
        MipOptions options,
        Span<float> result
    ) {
        var weightTotal = 0f;
        result.Clear();

        foreach (var texel in texels) {
            var weight = options.AlphaWeighted ? from[texel + 3] : 1f;
            weightTotal += weight;

            for (var channel = 0; channel < channels && channel < 3; channel++) {
                var value = from[texel + channel];
                result[channel] += weight * (options.Srgb ? Srgb.ToLinearTable[value] : value);
            }

            if (channels == 4) {
                result[3] += from[texel + 3];
            }
        }

        if (weightTotal <= 0f) {
            // Every texel is fully transparent, so there is no colour to preserve and no weights to
            // divide by. An unweighted mean keeps whatever was painted under the transparency, which
            // is what a later dilation pass wants to find.
            foreach (var texel in texels) {
                for (var channel = 0; channel < channels && channel < 3; channel++) {
                    result[channel] += from[texel + channel];
                }
            }

            weightTotal = texels.Length;
        }

        for (var channel = 0; channel < channels && channel < 3; channel++) {
            result[channel] = options.Srgb
                ? Srgb.FromLinear(result[channel] / weightTotal)
                : result[channel] / weightTotal;
        }

        if (channels == 4) {
            result[3] /= texels.Length;
        }
    }

    /// <summary>
    ///     The mean of up to four directions. Two-channel normal maps carry only x and y, so z is
    ///     reconstructed before averaging and dropped afterwards — averaging x and y on their own
    ///     and renormalising the pair is a different and wrong answer, because it discards how far
    ///     each source normal was leaning towards the viewer.
    /// </summary>
    static void AverageDirection(ReadOnlySpan<byte> from, ReadOnlySpan<int> texels, int channels, Span<float> result) {
        result.Clear();

        foreach (var texel in texels) {
            var x = (from[texel] / 255f * 2f) - 1f;
            var y = (from[texel + 1] / 255f * 2f) - 1f;
            var z = channels >= 3
                ? (from[texel + 2] / 255f * 2f) - 1f
                : MathF.Sqrt(Math.Max(0f, 1f - (x * x) - (y * y)));

            result[0] += x;
            result[1] += y;
            result[2] += z;

            if (channels == 4) {
                result[3] += from[texel + 3];
            }
        }

        var length = MathF.Sqrt((result[0] * result[0]) + (result[1] * result[1]) + (result[2] * result[2]));

        // Four normals that cancel exactly leave nothing to point at. Straight up is the answer that
        // says "this surface has no detail left at this scale", which at that point is true.
        if (length < 1e-6f) {
            result[0] = 0f;
            result[1] = 0f;
            result[2] = 1f;
        } else {
            result[0] /= length;
            result[1] /= length;
            result[2] /= length;
        }

        for (var channel = 0; channel < 3 && channel < channels; channel++) {
            result[channel] = (result[channel] + 1f) * 0.5f * 255f;
        }

        if (channels == 4) {
            result[3] /= texels.Length;
        }
    }

    /// <summary>Turns an sRGB-encoded value into the light it stands for.</summary>
    /// <param name="encoded">The value, zero to one.</param>
    /// <returns>The linear value.</returns>
    public static float ToLinear(float encoded) =>
        encoded <= 0.04045f ? encoded / 12.92f : MathF.Pow((encoded + 0.055f) / 1.055f, 2.4f);

    /// <summary>Turns a linear value into its sRGB encoding.</summary>
    /// <param name="linear">The linear value, zero to one.</param>
    /// <returns>The encoded value.</returns>
    public static float ToSrgb(float linear) =>
        linear <= 0.0031308f ? linear * 12.92f : (1.055f * MathF.Pow(linear, 1f / 2.4f)) - 0.055f;

    /// <summary>The sRGB transfer function, as a byte-to-byte table.</summary>
    /// <remarks>
    ///     Two hundred and fifty-six entries, built once. An importer generating a mip chain for a
    ///     colour texture converts through these rather than calling <see cref="MathF.Pow" /> per
    ///     channel per texel, which for a 4K texture is forty million calls.
    /// </remarks>
    public static class Srgb {
        /// <summary>The linear value each encoded byte stands for.</summary>
        public static ReadOnlySpan<float> ToLinearTable => LinearValues;

        static readonly float[] LinearValues = BuildLinear();

        /// <summary>Encodes a linear value as a byte.</summary>
        /// <param name="linear">The linear value, zero to one.</param>
        /// <returns>The encoded byte.</returns>
        public static byte FromLinear(float linear) =>
            (byte)Math.Clamp((int)((ToSrgb(Math.Clamp(linear, 0f, 1f)) * 255f) + 0.5f), 0, 255);

        static float[] BuildLinear() {
            var table = new float[256];

            for (var index = 0; index < table.Length; index++) {
                table[index] = ToLinear(index / 255f);
            }

            return table;
        }
    }

    static int ChannelsOf(PixelFormat format) =>
        format switch {
            PixelFormat.R8UNorm => 1,
            PixelFormat.Rg8UNorm => 2,
            PixelFormat.Rgba8UNorm or PixelFormat.Rgba8UNormSrgb
                or PixelFormat.Bgra8UNorm or PixelFormat.Bgra8UNormSrgb => 4,
            _ => throw new NotSupportedException(
                $"{format} cannot be reduced here. A mip chain is generated from the source pixels before "
                + "compression, because reducing compressed blocks means decode, filter and re-encode, and each "
                + "round of that loses more than the filter gains."
            )
        };
}
