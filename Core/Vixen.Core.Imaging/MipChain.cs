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

    /// <summary>
    ///     Fills every level below the largest by averaging, in place.
    /// </summary>
    /// <param name="texture">The texture, whose level zero is already filled.</param>
    /// <exception cref="NotSupportedException">Its format is compressed, or is not eight bits per channel.</exception>
    /// <remarks>
    ///     <para>
    ///         A box filter — each destination texel is the mean of the up-to-four source texels
    ///         under it — and <b>the averaging is done on the stored values, not in linear light</b>.
    ///         That is wrong for an sRGB texture and it is the classic mip-generation bug: averaging
    ///         two sRGB-encoded values gives a result darker than averaging the light they stand for.
    ///     </para>
    ///     <para>
    ///         It is left wrong here on purpose, because the fix belongs one layer up. The importer
    ///         knows a texture's colour space — it is a setting in the <c>.meta</c> file — and it
    ///         converts to linear before generating and back afterwards. A filter that guessed from
    ///         the format would get it wrong for the normal maps and masks that are stored in an sRGB
    ///         format and are not colour. <see cref="Srgb" /> is here for that caller to use.
    ///     </para>
    /// </remarks>
    public static void Generate(TextureData texture) {
        ArgumentNullException.ThrowIfNull(texture);

        var channels = ChannelsOf(texture.Format);

        for (var level = 1; level < texture.LevelCount; level++) {
            var source = texture.Levels[level - 1];
            var destination = texture.Levels[level];
            var from = texture.Level(level - 1);
            var to = texture.LevelSpan(level);

            for (var y = 0; y < destination.Height; y++) {
                for (var x = 0; x < destination.Width; x++) {
                    for (var channel = 0; channel < channels; channel++) {
                        var total = 0;
                        var taken = 0;

                        for (var dy = 0; dy < 2; dy++) {
                            var sourceY = (y * 2) + dy;

                            if (sourceY >= source.Height) {
                                continue;
                            }

                            for (var dx = 0; dx < 2; dx++) {
                                var sourceX = (x * 2) + dx;

                                if (sourceX >= source.Width) {
                                    continue;
                                }

                                total += from[(((sourceY * source.Width) + sourceX) * channels) + channel];
                                taken++;
                            }
                        }

                        to[(((y * destination.Width) + x) * channels) + channel] = (byte)(total / taken);
                    }
                }
            }
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
