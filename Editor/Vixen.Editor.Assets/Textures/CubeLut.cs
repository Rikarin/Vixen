// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Imaging;
using Vixen.Graphics;

namespace Vixen.Editor.Assets.Textures;

/// <summary>Reads the Iridas/Adobe <c>.cube</c> grading table into a 3D texture.</summary>
/// <remarks>
///     <para>
///         <b>What a colourist hands over.</b> Resolve, Baselight, Nuke and Photoshop all export this
///         and nothing else is as widely written, so it is the format that makes "grade it properly
///         and give me the file" a workflow the engine supports rather than a request for numbers.
///     </para>
///     <para>
///         <b>Why 3D and not three curves.</b> A curve maps each channel from itself, so it cannot
///         rotate a hue or desaturate only the greens. A table indexed by the colour <em>as a
///         coordinate</em> expresses any mapping at all, and the hardware's trilinear filter is
///         exactly the interpolation between entries — so an arbitrarily complicated grade costs one
///         texture fetch. That is the whole reason the format won.
///     </para>
///     <para>
///         ⚠ <b>It is display-referred, and the tonemapper samples it after the curve for that
///         reason.</b> The input is a texture coordinate, so it has to be bounded — and a grade
///         authored on values that have already been through a curve behaves the way the colourist saw
///         it. What it therefore cannot do is anything to values above white, which is why the
///         scene-referred grade in <see cref="ColorGrading" /> exists beside it rather than instead
///         of it.
///     </para>
/// </remarks>
public static class CubeLut {
    /// <summary>The largest edge this reads, which is far past what anybody authors.</summary>
    /// <remarks>
    ///     A guard rather than a limit: 64³ is a quarter of a million entries and already more than
    ///     the format is used at. Without it a corrupt <c>LUT_3D_SIZE</c> is an allocation the parser
    ///     makes on the file's word.
    /// </remarks>
    public const int MaximumSize = 64;

    /// <summary>Parses a table into a 3D texture, ready for <c>Ktx2.Write</c>.</summary>
    /// <param name="text">The file's contents.</param>
    /// <returns>An <c>Rgba16Float</c> volume, <c>size</c> on every edge.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text" /> is null.</exception>
    /// <exception cref="FormatException">It is not a 3D table, or does not hold what it declared.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Sixteen-bit float and not eight-bit unsigned.</b> Eight bits is 256 steps per
    ///         channel over the whole range, and a grade's job is often to move the last few steps of
    ///         a gradient — the banding an 8-bit table adds is worst in exactly the skies and skin
    ///         tones somebody graded the shot for. It also lets a table hold values outside 0..1,
    ///         which several exporters produce and which an unsigned format would silently clip.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Red varies fastest.</b> The format says so, and it is the opposite of how a 3D
    ///         texture's memory is usually described — getting it backwards produces a table that is
    ///         its own transpose, which looks like a plausible grade of a different picture.
    ///     </para>
    /// </remarks>
    public static TextureData Parse(string text) {
        ArgumentNullException.ThrowIfNull(text);

        var size = 0;
        var domainMinimum = new float[] { 0f, 0f, 0f };
        var domainMaximum = new float[] { 1f, 1f, 1f };

        List<float> entries = [];

        foreach (var raw in text.Split('\n')) {
            var line = raw.AsSpan().Trim();

            // Blank lines, comments, and the title — which is a quoted string that would otherwise
            // parse as three failed floats.
            if (line.IsEmpty || line[0] == '#' || line.StartsWith("TITLE", StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            if (line.StartsWith("LUT_1D_SIZE", StringComparison.OrdinalIgnoreCase)) {
                throw new FormatException(
                    "This is a 1D .cube, and a 1D table is a curve per channel — it cannot express the hue and "
                    + "saturation mappings a 3D one can, and there is nothing here that samples one. Export a 3D "
                    + "LUT instead."
                );
            }

            if (line.StartsWith("LUT_3D_SIZE", StringComparison.OrdinalIgnoreCase)) {
                size = ParseSize(line);
                continue;
            }

            if (line.StartsWith("DOMAIN_MIN", StringComparison.OrdinalIgnoreCase)) {
                Triple(line[10..], domainMinimum, "DOMAIN_MIN");
                continue;
            }

            if (line.StartsWith("DOMAIN_MAX", StringComparison.OrdinalIgnoreCase)) {
                Triple(line[10..], domainMaximum, "DOMAIN_MAX");
                continue;
            }

            Row(line, entries);
        }

        if (size <= 0) {
            throw new FormatException("The file declares no LUT_3D_SIZE, so there is no way to know its shape.");
        }

        var expected = size * size * size * 3;

        if (entries.Count != expected) {
            throw new FormatException(
                $"LUT_3D_SIZE says {size}, which is {expected / 3} entries, and the file holds {entries.Count / 3}."
            );
        }

        return Build(size, entries, domainMinimum, domainMaximum);
    }

    /// <summary>Turns the parsed entries into half-float RGBA texels.</summary>
    static TextureData Build(int size, List<float> entries, float[] minimum, float[] maximum) {
        // Four channels rather than three: no graphics API samples a three-channel float texture
        // everywhere, and the alpha is what makes a texel a power of two so a row has no padding.
        var texture = new TextureData(PixelFormat.Rgba16Float, size, size, levelCount: 1, depth: size);
        var pixels = texture.PixelSpan();
        var at = 0;

        for (var i = 0; i < size * size * size; i++) {
            for (var channel = 0; channel < 3; channel++) {
                // The domain is what the *input* was normalised over, so it is undone on the way in.
                // Almost every file leaves it at 0..1 and this is a multiply by one.
                var span = maximum[channel] - minimum[channel];
                var value = entries[(i * 3) + channel];

                if (MathF.Abs(span - 1f) > 1e-6f || minimum[channel] != 0f) {
                    value = span > 1e-6f ? (value - minimum[channel]) / span : value;
                }

                Write(pixels, ref at, value);
            }

            Write(pixels, ref at, 1f);
        }

        return texture;
    }

    static void Write(Span<byte> pixels, ref int at, float value) {
        var bits = BitConverter.HalfToUInt16Bits((Half)value);

        pixels[at++] = (byte)(bits & 0xFF);
        pixels[at++] = (byte)(bits >> 8);
    }

    static int ParseSize(ReadOnlySpan<char> line) {
        var value = line[11..].Trim();

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size)) {
            throw new FormatException($"LUT_3D_SIZE is '{value}', which is not a whole number.");
        }

        if (size is < 2 or > MaximumSize) {
            throw new FormatException($"LUT_3D_SIZE is {size}; this reads 2 to {MaximumSize}.");
        }

        return size;
    }

    static void Triple(ReadOnlySpan<char> line, float[] into, string what) {
        var index = 0;

        foreach (var range in line.Split(' ')) {
            var token = line[range].Trim();

            if (token.IsEmpty) {
                continue;
            }

            if (index >= 3) {
                throw new FormatException($"{what} has more than three numbers on it.");
            }

            into[index++] = Number(token, what);
        }

        if (index != 3) {
            throw new FormatException($"{what} has {index} numbers on it and needs three.");
        }
    }

    static void Row(ReadOnlySpan<char> line, List<float> entries) {
        var found = 0;

        foreach (var range in line.Split(' ')) {
            var token = line[range].Trim();

            if (token.IsEmpty) {
                continue;
            }

            entries.Add(Number(token, "a table row"));
            found++;
        }

        if (found != 3) {
            throw new FormatException($"A table row has {found} numbers on it and every row needs three.");
        }
    }

    static float Number(ReadOnlySpan<char> token, string what) =>
        float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new FormatException($"'{token}' on {what} is not a number.");
}
