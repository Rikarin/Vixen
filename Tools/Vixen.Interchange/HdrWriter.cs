// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Core.Imaging;

namespace Vixen.Interchange;

/// <summary>Radiance RGBE (<c>.hdr</c>), written with the run-length scanlines every reader expects.</summary>
/// <remarks>
///     <para>
///         <b>Three colours sharing one exponent</b>, so a texel keeps about eight bits of precision
///         relative to its brightest channel and none below that. That suits radiance — an emissive
///         or an environment — and does not suit data such as a height, which belongs in EXR.
///     </para>
///     <para>
///         A negative value is written as zero, since the format has no sign. A grey picture is
///         written as three equal channels, and an alpha channel is dropped, because there is no
///         fourth one to put it in.
///     </para>
/// </remarks>
public static class HdrWriter {
    /// <summary>Encodes a picture.</summary>
    /// <param name="image">One, three or four channels; a fourth is not written.</param>
    /// <returns>The file.</returns>
    /// <exception cref="ArgumentException">The channel count is not one this writes.</exception>
    public static byte[] Encode(FloatImage image) {
        ArgumentNullException.ThrowIfNull(image);

        if (image.Channels is not (1 or 3 or 4)) {
            throw new ArgumentException(
                $"An HDR file is written from one, three or four channels, not {image.Channels}.",
                nameof(image)
            );
        }

        using var output = new MemoryStream();
        output.Write(
            Encoding.ASCII.GetBytes($"#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y {image.Height} +X {image.Width}\n")
        );

        var width = image.Width;
        var line = new byte[width * 4];
        var component = new byte[width];

        for (var y = 0; y < image.Height; y++) {
            for (var x = 0; x < width; x++) {
                var at = (y * width + x) * image.Channels;
                var red = image.Samples[at];
                var (green, blue) = image.Channels == 1 ? (red, red) : (image.Samples[at + 1], image.Samples[at + 2]);
                Rgbe(red, green, blue, line.AsSpan(x * 4, 4));
            }

            // The run-length form covers widths from 8 to 32767; anything else is written flat.
            if (width is < 8 or > 0x7FFF) {
                output.Write(line);
                continue;
            }

            output.Write([2, 2, (byte)(width >> 8), (byte)width]);

            for (var lane = 0; lane < 4; lane++) {
                for (var x = 0; x < width; x++) {
                    component[x] = line[x * 4 + lane];
                }

                Runs(output, component);
            }
        }

        return output.ToArray();
    }

    static void Rgbe(float red, float green, float blue, Span<byte> into) {
        (red, green, blue) = (Math.Max(red, 0f), Math.Max(green, 0f), Math.Max(blue, 0f));
        var top = Math.Max(red, Math.Max(green, blue));

        if (!(top >= 1e-32f) || float.IsNaN(top)) {
            into.Clear();
            return;
        }

        top = Math.Min(top, float.MaxValue);
        var exponent = MathF.ILogB(top) + 1;
        var scale = MathF.ScaleB(256f, -exponent);
        into[0] = (byte)Math.Min(255f, red * scale);
        into[1] = (byte)Math.Min(255f, green * scale);
        into[2] = (byte)Math.Min(255f, blue * scale);
        into[3] = (byte)(exponent + 128);
    }

    /// <summary>One component of a scanline: runs of four or more as a count over 128 and a byte, the rest literally.</summary>
    static void Runs(Stream output, ReadOnlySpan<byte> values) {
        var at = 0;

        while (at < values.Length) {
            var run = 1;

            while (at + run < values.Length && run < 127 && values[at + run] == values[at]) {
                run++;
            }

            if (run >= 4) {
                output.Write([(byte)(128 + run), values[at]]);
                at += run;
                continue;
            }

            // Literals up to the next run of four, at most 128 of them.
            var start = at;

            while (at < values.Length && at - start < 128) {
                var ahead = 1;

                while (at + ahead < values.Length && ahead < 4 && values[at + ahead] == values[at]) {
                    ahead++;
                }

                if (ahead >= 4) {
                    break;
                }

                at++;
            }

            output.WriteByte((byte)(at - start));
            output.Write(values[start..at]);
        }
    }
}
