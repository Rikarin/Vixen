// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;

namespace Vixen.Editor.TextureGraph;

/// <summary>Turns what came off a device into something <c>PngCodec</c> can write.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>An encoder, not a kernel, and doc 48 § D3's ban does not reach it.</b> The rule is
///         that no node has a CPU implementation, because a parity test between two transcriptions of
///         one operation proves only that somebody copied carefully. Nothing in a graph converts a
///         half-float to a byte; this is the last step out of the pipeline, and it has no twin to
///         disagree with.
///     </para>
///     <para>
///         <b>One channel becomes grey rather than red.</b> Half of what a texture graph produces is
///         a mask, and a mask written into the red channel of a PNG is a picture nobody can read at a
///         glance — which defeats the reason doc 48 § D4 writes files in the first place.
///     </para>
/// </remarks>
internal static class TexturePixels {
    /// <summary>Reads raw texels into an eight-bit RGBA picture.</summary>
    /// <param name="raw">The bytes, tightly packed, top row first.</param>
    /// <param name="width">Its width in texels.</param>
    /// <param name="height">Its height in texels.</param>
    /// <param name="format">What the bytes are.</param>
    /// <returns>The picture.</returns>
    public static Bitmap ToBitmap(ReadOnlySpan<byte> raw, int width, int height, TextureFormat format) {
        var pixels = new byte[width * height * 4];
        var stride = TextureFormats.BytesPerTexel(format);

        for (var texel = 0; texel < width * height; texel++) {
            var source = raw[(texel * stride)..];
            var destination = texel * 4;

            switch (format) {
                case TextureFormat.R8: {
                    Grey(pixels, destination, source[0]);

                    break;
                }

                case TextureFormat.Rg8: {
                    pixels[destination] = source[0];
                    pixels[destination + 1] = source[1];
                    pixels[destination + 2] = 0;
                    pixels[destination + 3] = 255;

                    break;
                }

                case TextureFormat.Rgba8: {
                    source[..4].CopyTo(pixels.AsSpan(destination));

                    break;
                }

                case TextureFormat.R16Float: {
                    Grey(pixels, destination, Byte(Half(source)));

                    break;
                }

                case TextureFormat.Rgba16Float: {
                    for (var channel = 0; channel < 4; channel++) {
                        pixels[destination + channel] = Byte(Half(source[(channel * 2)..]));
                    }

                    break;
                }

                default:
                    throw new ArgumentOutOfRangeException(nameof(format), format, "Not one of the five plan formats.");
            }
        }

        return new(width, height, pixels);
    }

    static void Grey(byte[] pixels, int at, byte value) {
        pixels[at] = value;
        pixels[at + 1] = value;
        pixels[at + 2] = value;
        pixels[at + 3] = 255;
    }

    static float Half(ReadOnlySpan<byte> source) => (float)BitConverter.ToHalf(source);

    /// <summary>A linear value in 0…1 as a byte, with the values outside that range clamped.</summary>
    /// <remarks>
    ///     ⚠ <b>No sRGB encode, and that is the same decision the shader-graph preview makes.</b> A
    ///     texture graph works in the numbers the material wants — a roughness, a height, a mask — and
    ///     gamma-encoding them on the way to a file would make every non-colour output wrong. A base
    ///     colour destined for an sRGB texture is encoded by the importer, from the <c>usage</c> the
    ///     bake writes into the <c>.meta</c>, which is doc 48 § M5.
    /// </remarks>
    static byte Byte(float value) => (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);
}
