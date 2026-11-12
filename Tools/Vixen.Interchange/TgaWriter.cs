// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;

namespace Vixen.Interchange;

/// <summary>Truevision TGA, written uncompressed: grey, RGB or RGBA at eight bits.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Row zero is written first and the header says so</b>, with bit 5 of the image
///         descriptor set. A TGA that leaves the bit clear is read bottom-up by every reader that
///         honours it, which is how a TGA comes out upside down in one tool and not another —
///         <see cref="StbImageDecoder" /> honours it, and its tests say why that matters.
///     </para>
///     <para>
///         Uncompressed, because run-length TGA is the part of the format readers disagree about
///         most, and a texture that compresses well is better written as a PNG anyway. The
///         version 2 footer is written so a reader can tell the file is not version 1.
///     </para>
/// </remarks>
public static class TgaWriter {
    const byte TopLeft = 0x20;

    /// <summary>Encodes a picture.</summary>
    /// <param name="width">Its width, at most 65535.</param>
    /// <param name="height">Its height, at most 65535.</param>
    /// <param name="channels">One for grey, three for RGB, four for RGBA.</param>
    /// <param name="samples">Interleaved, top row first, in R, G, B, A order.</param>
    /// <returns>The file.</returns>
    /// <exception cref="ArgumentException">The shape is not one a TGA holds.</exception>
    public static byte[] Encode(int width, int height, int channels, ReadOnlySpan<byte> samples) {
        if (channels is not (1 or 3 or 4)) {
            throw new ArgumentException($"A TGA holds one, three or four channels, not {channels}.", nameof(channels));
        }

        if (width is < 1 or > ushort.MaxValue || height is < 1 or > ushort.MaxValue) {
            throw new ArgumentException($"A TGA cannot be {width}×{height}.", nameof(width));
        }

        if (samples.Length != width * height * channels) {
            throw new ArgumentException("The sample count does not match the shape.", nameof(samples));
        }

        const int header = 18;
        var footer = "\0\0\0\0\0\0\0\0TRUEVISION-XFILE.\0"u8;
        var file = new byte[header + samples.Length + footer.Length];
        file[2] = (byte)(channels == 1 ? 3 : 2);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(12), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(14), (ushort)height);
        file[16] = (byte)(channels * 8);
        file[17] = (byte)(TopLeft | (channels == 4 ? 8 : 0));

        var pixels = file.AsSpan(header, samples.Length);

        if (channels == 1) {
            samples.CopyTo(pixels);
        } else {
            // Blue, green, red, then alpha: the order the format stores a pixel in.
            for (var at = 0; at < samples.Length; at += channels) {
                pixels[at] = samples[at + 2];
                pixels[at + 1] = samples[at + 1];
                pixels[at + 2] = samples[at];

                if (channels == 4) {
                    pixels[at + 3] = samples[at + 3];
                }
            }
        }

        footer.CopyTo(file.AsSpan(header + samples.Length));
        return file;
    }
}
