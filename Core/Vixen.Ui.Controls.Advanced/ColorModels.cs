// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Mathematics;

namespace Vixen.Ui.Controls.Advanced;

/// <summary>Which set of axes a <see cref="ColorPicker" /> is offering.</summary>
public enum ColorModel : byte {
    /// <summary>Hue, saturation and value — the wheel everybody has used.</summary>
    Hsv,

    /// <summary>Oklab in polar form: perceptual lightness, chroma and hue.</summary>
    /// <remarks>
    ///     Worth having beside HSV rather than instead of it. HSV's "value" is not brightness — a
    ///     fully saturated yellow and a fully saturated blue have the same V and are nothing like as
    ///     bright — so picking a set of colours that read as equally strong is guesswork in it and
    ///     arithmetic in OkLCh. HSV stays because it is what every reference image, every art tool
    ///     and every artist's muscle memory is in.
    /// </remarks>
    OkLch
}

/// <summary>A colour as hue, saturation and value.</summary>
/// <param name="H">Hue in degrees, 0 to 360.</param>
/// <param name="S">Saturation, 0 to 1.</param>
/// <param name="V">Value, 0 to 1.</param>
/// <remarks>
///     ⚠ <b>Kept alongside the RGB rather than derived from it on demand.</b> Grey has no hue, so a
///     picker that recomputed HSV from the colour would lose which hue the user was on the moment
///     they dragged the saturation to zero — and the field would jump back to red when they dragged
///     it out again. Every picker that has this bug has it for that reason.
/// </remarks>
public readonly record struct Hsv(float H, float S, float V) {
    /// <summary>Converts an sRGB colour.</summary>
    /// <param name="color">The colour. Components above one are divided out and reported separately.</param>
    /// <returns>The same colour as hue, saturation and value.</returns>
    public static Hsv FromRgb(Color4 color) {
        var max = MathF.Max(color.R, MathF.Max(color.G, color.B));
        var min = MathF.Min(color.R, MathF.Min(color.G, color.B));
        var chroma = max - min;

        var hue = 0f;

        if (chroma > 0f) {
            if (max.Equals(color.R)) {
                hue = ((color.G - color.B) / chroma % 6f) * 60f;
            } else if (max.Equals(color.G)) {
                hue = (((color.B - color.R) / chroma) + 2f) * 60f;
            } else {
                hue = (((color.R - color.G) / chroma) + 4f) * 60f;
            }
        }

        if (hue < 0f) {
            hue += 360f;
        }

        return new Hsv(hue, max <= 0f ? 0f : chroma / max, max);
    }

    /// <summary>Converts back.</summary>
    /// <param name="alpha">The alpha to carry through.</param>
    /// <returns>The colour.</returns>
    public Color4 ToRgb(float alpha = 1f) {
        var hue = ((H % 360f) + 360f) % 360f / 60f;
        var chroma = V * S;
        var second = chroma * (1f - MathF.Abs((hue % 2f) - 1f));
        var match = V - chroma;

        var (r, g, b) = (int) hue switch {
            0 => (chroma, second, 0f),
            1 => (second, chroma, 0f),
            2 => (0f, chroma, second),
            3 => (0f, second, chroma),
            4 => (second, 0f, chroma),
            _ => (chroma, 0f, second)
        };

        return new Color4(r + match, g + match, b + match, alpha);
    }
}

/// <summary>A colour as perceptual lightness, chroma and hue.</summary>
/// <param name="L">Lightness, 0 at black and about 1 at white.</param>
/// <param name="C">How far from grey. Roughly 0 to 0.4 for anything inside sRGB.</param>
/// <param name="H">Hue in degrees.</param>
/// <remarks>
///     ⚠ <b>The conversion goes through linear RGB</b>, because Oklab's input is linear and feeding
///     it sRGB-encoded values produces something that is not Oklab and has none of the properties it
///     is being used for. <c>Vixen.Core.Mathematics</c> says the same thing about the same function;
///     it is repeated here because a colour picker is exactly where somebody reaches for the encoded
///     value, which is the one they can see.
/// </remarks>
public readonly record struct OkLch(float L, float C, float H) {
    /// <summary>Converts an sRGB colour.</summary>
    /// <param name="color">The colour.</param>
    /// <returns>The same colour in OkLCh.</returns>
    public static OkLch FromSrgb(Color4 color) {
        var lab = Oklab.FromLinear(
            new Vector3(
                ColorSpace.SrgbToLinear(color.R),
                ColorSpace.SrgbToLinear(color.G),
                ColorSpace.SrgbToLinear(color.B)
            )
        );

        var hue = MathF.Atan2(lab.B, lab.A) * 180f / MathF.PI;

        return new OkLch(lab.L, MathF.Sqrt((lab.A * lab.A) + (lab.B * lab.B)), hue < 0f ? hue + 360f : hue);
    }

    /// <summary>Converts back, clamping into the sRGB gamut.</summary>
    /// <param name="alpha">The alpha to carry through.</param>
    /// <returns>The colour.</returns>
    /// <remarks>
    ///     ⚠ <b>Clamped, and the clamp is a real loss.</b> Much of OkLCh's space is outside sRGB —
    ///     a lightness of 0.7 at a chroma of 0.3 is not a colour a monitor can make — and per-channel
    ///     clamping shifts the hue rather than reducing the chroma, which is the wrong answer done
    ///     cheaply. A gamut-mapping pass that walks the chroma down until the colour fits is the
    ///     right one and is owed. <see cref="IsInGamut" /> is how a picker can say so meanwhile.
    /// </remarks>
    public Color4 ToSrgb(float alpha = 1f) {
        var linear = Linear();

        return new Color4(
            ColorSpace.LinearToSrgb(Math.Clamp(linear.X, 0f, 1f)),
            ColorSpace.LinearToSrgb(Math.Clamp(linear.Y, 0f, 1f)),
            ColorSpace.LinearToSrgb(Math.Clamp(linear.Z, 0f, 1f)),
            alpha
        );
    }

    /// <summary>Whether a monitor can actually make this colour.</summary>
    public bool IsInGamut {
        get {
            var linear = Linear();

            return linear is { X: >= -0.001f and <= 1.001f, Y: >= -0.001f and <= 1.001f, Z: >= -0.001f and <= 1.001f };
        }
    }

    Vector3 Linear() {
        var radians = H * MathF.PI / 180f;
        return new Oklab(L, C * MathF.Cos(radians), C * MathF.Sin(radians)).ToLinear();
    }
}

/// <summary>Reading and writing the notation people paste.</summary>
static class Hex {
    /// <summary>Parses <c>#rgb</c>, <c>#rrggbb</c> or <c>#rrggbbaa</c>, with or without the hash.</summary>
    /// <param name="text">The text.</param>
    /// <param name="color">The colour.</param>
    /// <returns>Whether it was one of those.</returns>
    public static bool TryParse(string? text, out Color4 color) {
        color = default;

        var span = (text ?? string.Empty).AsSpan().Trim();

        if (span.Length > 0 && span[0] == '#') {
            span = span[1..];
        }

        if (span.Length == 3) {
            // ⚠ Each digit doubled rather than shifted: `#f00` is `#ff0000`, not `#f00000`. Getting
            // this wrong makes every short form 6% too dark, which nobody spots and everybody sees.
            Span<char> expanded = stackalloc char[6];

            for (var i = 0; i < 3; i++) {
                expanded[i * 2] = span[i];
                expanded[(i * 2) + 1] = span[i];
            }

            return TryParse(expanded, out color);
        }

        return TryParse(span, out color);
    }

    /// <summary>Writes <c>#rrggbb</c>, or <c>#rrggbbaa</c> when the colour is not opaque.</summary>
    /// <param name="color">The colour.</param>
    /// <returns>The text.</returns>
    public static string ToString(Color4 color) {
        var value = color.A >= 1f
            ? $"#{Byte(color.R):x2}{Byte(color.G):x2}{Byte(color.B):x2}"
            : $"#{Byte(color.R):x2}{Byte(color.G):x2}{Byte(color.B):x2}{Byte(color.A):x2}";

        return value;
    }

    static bool TryParse(ReadOnlySpan<char> span, out Color4 color) {
        color = default;

        if (span.Length is not (6 or 8)) {
            return false;
        }

        Span<float> parts = [0f, 0f, 0f, 1f];

        for (var i = 0; i * 2 < span.Length; i++) {
            if (!byte.TryParse(span.Slice(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var part)) {
                return false;
            }

            parts[i] = part / 255f;
        }

        color = new Color4(parts[0], parts[1], parts[2], parts[3]);
        return true;
    }

    static int Byte(float value) => (int) MathF.Round(Math.Clamp(value, 0f, 1f) * 255f);
}
