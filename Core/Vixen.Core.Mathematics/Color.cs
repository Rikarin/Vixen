// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Vixen.Core.Mathematics;

/// <summary>
///     A colour as four bytes, RGBA: the storage and interchange form. Vertex colours, UI palettes,
///     hex codes, texture texels.
/// </summary>
/// <remarks>
///     <para>
///         <b>This type does not declare a colour space</b>, because the bytes on their own do not
///         have one — the same <c>#808080</c> is a mid grey to a designer and 0.216 linear to a
///         renderer. Every conversion here says which it means: <see cref="ToColor4" /> only divides
///         by 255, while <see cref="ToLinear" /> also decodes sRGB. Guessing is what produces washed
///         out or crushed output that nobody can trace.
///     </para>
///     <para>
///         Colours typed by a human, and every hex code, are sRGB. Use <see cref="ToLinear" /> on
///         them, and reach for <see cref="ToColor4" /> only for data that was already linear.
///     </para>
/// </remarks>
[DataContract]
[StructLayout(LayoutKind.Sequential)]
public readonly struct Color : IEquatable<Color>, IFormattable {
    /// <summary>The red component.</summary>
    public readonly byte R;

    /// <summary>The green component.</summary>
    public readonly byte G;

    /// <summary>The blue component.</summary>
    public readonly byte B;

    /// <summary>The alpha component.</summary>
    public readonly byte A;

    /// <summary>Opaque black.</summary>
    public static Color Black => new(0, 0, 0, 255);

    /// <summary>Opaque white.</summary>
    public static Color White => new(255, 255, 255, 255);

    /// <summary>Fully transparent black — all four bytes zero.</summary>
    public static Color Transparent => default;

    /// <summary>Builds a colour from its components.</summary>
    /// <param name="r">The red component.</param>
    /// <param name="g">The green component.</param>
    /// <param name="b">The blue component.</param>
    /// <param name="a">The alpha component.</param>
    public Color(byte r, byte g, byte b, byte a) {
        R = r;
        G = g;
        B = b;
        A = a;
    }

    /// <summary>Builds an opaque colour.</summary>
    /// <param name="r">The red component.</param>
    /// <param name="g">The green component.</param>
    /// <param name="b">The blue component.</param>
    public Color(byte r, byte g, byte b)
        : this(r, g, b, 255) { }

    /// <summary>The components as a span. Valid as long as the colour it came from.</summary>
    /// <returns>A span of four bytes.</returns>
    [UnscopedRef]
    public ReadOnlySpan<byte> AsSpan() => MemoryMarshal.CreateReadOnlySpan(in R, 4);

    /// <summary>The four bytes packed with red in the least significant byte.</summary>
    /// <returns>The packed value.</returns>
    /// <remarks>
    ///     The order a little-endian machine sees an <c>R8G8B8A8</c> texel as, which is why it is
    ///     this way round rather than the <c>0xAARRGGBB</c> that reads more naturally in a literal.
    /// </remarks>
    public uint ToRgba() => R | ((uint)G << 8) | ((uint)B << 16) | ((uint)A << 24);

    /// <summary>The four bytes packed as <c>0xAARRGGBB</c>, the order a hex literal reads in.</summary>
    /// <returns>The packed value.</returns>
    public uint ToArgb() => B | ((uint)G << 8) | ((uint)R << 16) | ((uint)A << 24);

    /// <summary>Unpacks a colour written red in the least significant byte.</summary>
    /// <param name="packed">The packed value.</param>
    /// <returns>The colour.</returns>
    public static Color FromRgba(uint packed) =>
        new((byte)packed, (byte)(packed >> 8), (byte)(packed >> 16), (byte)(packed >> 24));

    /// <summary>Unpacks a colour written <c>0xAARRGGBB</c>.</summary>
    /// <param name="packed">The packed value.</param>
    /// <returns>The colour.</returns>
    public static Color FromArgb(uint packed) =>
        new((byte)(packed >> 16), (byte)(packed >> 8), (byte)packed, (byte)(packed >> 24));

    /// <summary>
    ///     Parses <c>#RGB</c>, <c>#RGBA</c>, <c>#RRGGBB</c> or <c>#RRGGBBAA</c>, with or without the
    ///     leading hash. Alpha defaults to opaque.
    /// </summary>
    /// <param name="text">The hex code.</param>
    /// <param name="color">The parsed colour, or <see cref="Transparent" /> on failure.</param>
    /// <returns><see langword="true" /> if the text was a hex colour.</returns>
    /// <remarks>A hex code is sRGB. Follow this with <see cref="ToLinear" /> before rendering it.</remarks>
    public static bool TryParseHex(ReadOnlySpan<char> text, out Color color) {
        color = Transparent;

        var digits = text.StartsWith("#") ? text[1..] : text;
        if (digits.Length is not (3 or 4 or 6 or 8)) {
            return false;
        }

        Span<byte> channels = [0, 0, 0, 255];
        var compact = digits.Length <= 4;
        var perChannel = compact ? 1 : 2;

        for (var i = 0; i < digits.Length / perChannel; i++) {
            var slice = digits.Slice(i * perChannel, perChannel);
            if (!byte.TryParse(slice, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value)) {
                return false;
            }

            // #abc means #aabbcc: each digit is doubled, so f maps to 255 rather than to 15.
            channels[i] = compact ? (byte)((value * 16) + value) : value;
        }

        color = new(channels[0], channels[1], channels[2], channels[3]);
        return true;
    }

    /// <summary>Renders the colour as <c>#RRGGBBAA</c>.</summary>
    /// <param name="includeAlpha">Whether to append the alpha pair.</param>
    /// <returns>The hex code, with a leading hash.</returns>
    public string ToHex(bool includeAlpha = true) =>
        includeAlpha
            ? string.Create(CultureInfo.InvariantCulture, $"#{R:X2}{G:X2}{B:X2}{A:X2}")
            : string.Create(CultureInfo.InvariantCulture, $"#{R:X2}{G:X2}{B:X2}");

    /// <summary>
    ///     Divides each byte by 255. A numeric widening only — <b>no colour-space conversion</b>.
    ///     Right for data that was already linear, wrong for anything a person picked.
    /// </summary>
    /// <returns>The colour as floats in <c>[0, 1]</c>.</returns>
    public Color4 ToColor4() => new(R / 255f, G / 255f, B / 255f, A / 255f);

    /// <summary>
    ///     Treats the bytes as sRGB and decodes them to linear — the conversion a hex code, a colour
    ///     picker value or a palette entry needs before it reaches a shader.
    /// </summary>
    /// <returns>The linear colour.</returns>
    public Color4 ToLinear() => Color4.FromSrgb(ToColor4());

    /// <summary>Quantises a colour to bytes without any colour-space conversion.</summary>
    /// <param name="color">The colour, clamped to <c>[0, 1]</c>.</param>
    /// <returns>The quantised colour.</returns>
    public static Color FromColor4(Color4 color) {
        var saturated = Color4.Saturate(color);
        return new(Quantize(saturated.R), Quantize(saturated.G), Quantize(saturated.B), Quantize(saturated.A));
    }

    /// <summary>Encodes a linear colour as sRGB and quantises it to bytes.</summary>
    /// <param name="linear">The linear colour.</param>
    /// <returns>The encoded colour.</returns>
    public static Color FromLinear(Color4 linear) => FromColor4(linear.ToSrgb());

    /// <summary>Component-wise equality.</summary>
    /// <param name="left">The first colour.</param>
    /// <param name="right">The second colour.</param>
    /// <returns><see langword="true" /> if every component is equal.</returns>
    public static bool operator ==(Color left, Color right) =>
        left.R == right.R && left.G == right.G && left.B == right.B && left.A == right.A;

    /// <summary>The negation of <see cref="op_Equality" />.</summary>
    /// <param name="left">The first colour.</param>
    /// <param name="right">The second colour.</param>
    /// <returns><see langword="true" /> if any component differs.</returns>
    public static bool operator !=(Color left, Color right) => !(left == right);

    /// <summary>Splits the colour into its components.</summary>
    /// <param name="r">The red component.</param>
    /// <param name="g">The green component.</param>
    /// <param name="b">The blue component.</param>
    /// <param name="a">The alpha component.</param>
    public void Deconstruct(out byte r, out byte g, out byte b, out byte a) {
        r = R;
        g = G;
        b = B;
        a = A;
    }

    /// <inheritdoc />
    public bool Equals(Color other) => this == other;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Color other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => (int)ToRgba();

    /// <inheritdoc />
    public override string ToString() => ToHex();

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) {
        formatProvider ??= CultureInfo.InvariantCulture;
        return string.IsNullOrEmpty(format)
            ? ToHex()
            : $"({R.ToString(format, formatProvider)}, {G.ToString(format, formatProvider)}, {B.ToString(format, formatProvider)}, {A.ToString(format, formatProvider)})";
    }

    // Rounds rather than truncates, so 1.0 lands on 255 and the midpoint of each bucket maps back
    // to the value it came from.
    static byte Quantize(float value) => (byte)MathF.Round(value * 255f);
}
