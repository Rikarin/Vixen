// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using Vixen.Core.Imaging;
using Vixen.Graphics;

namespace Vixen.Interchange;

/// <summary>Reads a TIFF through <see cref="TiffReader" />, as an importer reads any other picture.</summary>
/// <remarks>
///     <para>
///         <b>Eight bits stay eight bits; anything deeper goes to float.</b> An 8-bit TIFF is what a painting
///         program saves for a colour map, and widening it to float would make every caller that keeps bytes refuse
///         it. A 16-bit or float TIFF carries the range a height or a normal was saved with, and narrowing it to a
///         byte at the front door would throw that away, as <see cref="StbImageDecoder" /> keeps Radiance HDR float.
///     </para>
///     <para>
///         Grey is spread to red, green and blue, a second lane is alpha, and a picture without alpha is opaque, as
///         <see cref="StbImageDecoder" /> answers for a PNG of the same shape.
///     </para>
/// </remarks>
public sealed class TiffDecoder : IImageDecoder {
    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; } = [".tif", ".tiff"];

    /// <inheritdoc />
    public TextureData Decode(Stream stream, string extension) {
        ArgumentNullException.ThrowIfNull(stream);

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
        var image = TiffReader.Decode(bytes);
        var eight = TiffReader.BitsPerSampleOf(bytes) == 8;
        var texture = new TextureData(
            eight ? PixelFormat.Rgba8UNorm : PixelFormat.Rgba32Float,
            image.Width,
            image.Height,
            1
        );
        var pixels = texture.PixelSpan();
        var texels = image.Width * image.Height;
        Span<float> rgba = stackalloc float[4];
        for (var at = 0; at < texels; at++) {
            Expand(image, at, rgba);
            for (var lane = 0; lane < 4; lane++) {
                if (eight) {
                    pixels[at * 4 + lane] = (byte)Math.Round(Math.Clamp(rgba[lane], 0f, 1f) * 255f);
                } else {
                    BinaryPrimitives.WriteSingleLittleEndian(pixels[((at * 4 + lane) * 4)..], rgba[lane]);
                }
            }
        }

        return texture;
    }

    /// <summary>One texel as red, green, blue and alpha, whatever lanes the file had.</summary>
    static void Expand(FloatImage image, int at, Span<float> rgba) {
        var lanes = image.Channels;
        var from = image.Samples.AsSpan(at * lanes, lanes);
        var grey = lanes <= 2;
        rgba[0] = from[0];
        rgba[1] = grey ? from[0] : from[1];
        rgba[2] = grey ? from[0] : from[2];
        rgba[3] = lanes is 2 or 4 ? from[lanes - 1] : 1f;
    }
}
