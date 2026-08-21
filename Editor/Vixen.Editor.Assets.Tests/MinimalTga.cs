// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Assets.Tests;

/// <summary>Writes a TGA, from the specification, with its origin bit either way up.</summary>
/// <remarks>
///     <para>
///         <b>The whole point of this fixture is the one byte at offset 17.</b> TGA is the only
///         common authoring format that stores which end of the file the top of the image is at:
///         bit 5 of the image descriptor is set when row zero is the top and clear when row zero is
///         the bottom, and both are legal files that every paint program emits depending on which
///         one you ask. A decoder that ignores the bit reads half the world's TGAs upside down, and
///         a flipped albedo and a flipped normal map both render <i>plausibly</i> — which is why
///         this is written from the format rather than from the library that reads it, and why the
///         suite asserts both orientations decode to the same picture.
///     </para>
///     <para>
///         Only what a test needs: image type 2 (uncompressed true-colour), 32 bits a pixel, no
///         colour map, no image id, no run-length encoding. Pixels are stored BGRA, which is TGA's
///         order and not the caller's.
///     </para>
/// </remarks>
static class MinimalTga {
    /// <summary>Writes an uncompressed 32-bit image.</summary>
    /// <param name="width">Its width.</param>
    /// <param name="height">Its height.</param>
    /// <param name="rgba">Its pixels, four bytes each, row-major and <b>top row first</b>.</param>
    /// <param name="topDown">Whether to store the top row first and say so, or store it last.</param>
    /// <returns>The file's bytes.</returns>
    public static byte[] Write(int width, int height, ReadOnlySpan<byte> rgba, bool topDown) {
        var file = new byte[18 + (width * height * 4)];

        file[0] = 0;                        // No image id.
        file[1] = 0;                        // No colour map.
        file[2] = 2;                        // Uncompressed true-colour.

        // Bytes 3..7 are the colour map specification, and stay zero.
        file[8] = 0;                        // x origin, low byte.
        file[9] = 0;
        file[10] = 0;                       // y origin, low byte.
        file[11] = 0;
        file[12] = (byte)(width & 0xFF);
        file[13] = (byte)(width >> 8);
        file[14] = (byte)(height & 0xFF);
        file[15] = (byte)(height >> 8);
        file[16] = 32;                      // Bits per pixel.

        // The low four bits are how many of those are alpha; bit 5 is the origin. Bit 4, which
        // would mean right-to-left, is deliberately not exercised: no writer in the world sets it.
        file[17] = (byte)(8 | (topDown ? 0x20 : 0x00));

        for (var y = 0; y < height; y++) {
            var source = y * width * 4;
            var row = topDown ? y : height - 1 - y;
            var into = 18 + (row * width * 4);

            for (var x = 0; x < width; x++) {
                file[into + (x * 4)] = rgba[source + (x * 4) + 2];          // B
                file[into + (x * 4) + 1] = rgba[source + (x * 4) + 1];      // G
                file[into + (x * 4) + 2] = rgba[source + (x * 4)];          // R
                file[into + (x * 4) + 3] = rgba[source + (x * 4) + 3];      // A
            }
        }

        return file;
    }
}
