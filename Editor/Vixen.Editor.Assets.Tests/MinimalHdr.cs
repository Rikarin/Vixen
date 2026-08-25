// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;

namespace Vixen.Editor.Assets.Tests;

/// <summary>Writes a Radiance <c>.hdr</c>, from the format, in shared-exponent RGBE.</summary>
/// <remarks>
///     <para>
///         <b>The point of this fixture is the exponent byte.</b> Radiance stores four bytes a texel
///         — a mantissa per channel and one exponent shared between them — so a texel's value is
///         <c>mantissa × 2^(exponent − 136)</c> and has no upper bound. That is the whole reason the
///         format exists and the whole reason an importer may not narrow it to a byte: the sun being
///         ten thousand times the sky is the content of the image, not a detail of it.
///     </para>
///     <para>
///         <b>Only the run-length form, and only eight texels wide or more.</b> Radiance has two
///         scanline encodings — flat, four bytes a texel in order, and the adaptive run-length form
///         whose scanline begins <c>02 02</c> with the width in the two bytes after it — and which
///         one a reader takes is decided by the width alone, because a flat scanline's first texel
///         could otherwise be mistaken for the marker. Under eight, readers take the flat path.
///     </para>
///     <para>
///         ⚠ <b>Which is why this refuses to write one.</b> StbImageSharp 2.30.15 decodes only the
///         first scanline of a flat file and leaves the rest of the image zero, so a fixture narrower
///         than eight would be a test that measured the reader's bug instead of the importer's
///         behaviour. Every real Radiance writer emits the run-length form; so does this.
///     </para>
///     <para>
///         The runs it emits are all literal — a run-length encoder that never finds a run is still
///         a run-length encoder, and what a decoder has to get right here is the framing rather than
///         the compression.
///     </para>
/// </remarks>
static class MinimalHdr {
    /// <summary>How far the stored exponent is offset: 128 for the bias, 8 for the mantissa's scale.</summary>
    public const int ExponentBias = 136;

    /// <summary>The largest literal run one count byte can introduce.</summary>
    const int MaxLiteralRun = 128;

    /// <summary>Writes an image.</summary>
    /// <param name="width">Its width.</param>
    /// <param name="height">Its height.</param>
    /// <param name="rgbe">Its texels, four bytes each — red, green, blue, exponent — row-major and <b>top row first</b>.</param>
    /// <returns>The file's bytes.</returns>
    public static byte[] Write(int width, int height, ReadOnlySpan<byte> rgbe) {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 8);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(width, 32768);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        var file = new MemoryStream();

        // The signature, the one format token every reader insists on, a blank line to end the
        // header, and the resolution string. `-Y H +X W` is rows top to bottom, columns left to
        // right, which is the only layout in practice and the only one readers accept.
        var header = new StringBuilder();
        header.Append("#?RADIANCE\n");
        header.Append("FORMAT=32-bit_rle_rgbe\n");
        header.Append('\n');
        header.Append(CultureInfo.InvariantCulture, $"-Y {height} +X {width}\n");

        file.Write(Encoding.ASCII.GetBytes(header.ToString()));

        for (var row = 0; row < height; row++) {
            var scanline = rgbe.Slice(row * width * 4, width * 4);

            file.WriteByte(2);
            file.WriteByte(2);
            file.WriteByte((byte)(width >> 8));
            file.WriteByte((byte)(width & 0xFF));

            // The run-length form is planar: all of the reds, then all of the greens, and so on.
            for (var channel = 0; channel < 4; channel++) {
                var written = 0;

                while (written < width) {
                    var run = Math.Min(MaxLiteralRun, width - written);

                    // A count of 128 or less introduces that many literal bytes; over 128 would
                    // introduce a repeat of the one byte after it.
                    file.WriteByte((byte)run);

                    for (var index = 0; index < run; index++) {
                        file.WriteByte(scanline[((written + index) * 4) + channel]);
                    }

                    written += run;
                }
            }
        }

        return file.ToArray();
    }

    /// <summary>What one channel of an RGBE texel decodes to, by the format's own arithmetic.</summary>
    /// <param name="mantissa">The channel's byte.</param>
    /// <param name="exponent">The texel's shared exponent byte.</param>
    /// <returns>The radiance, which has no upper bound.</returns>
    public static float ToFloat(byte mantissa, byte exponent) =>
        exponent == 0 ? 0f : mantissa * MathF.ScaleB(1f, exponent - ExponentBias);
}
