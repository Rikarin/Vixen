// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Vixen.Core.Mathematics;

/// <summary>
///     A linear colour with no alpha: light colours, emissive intensities, tint parameters — the
///     places where an alpha channel would be twelve wasted bytes per instance and one more thing to
///     leave uninitialised.
/// </summary>
[DataContract]
[StructLayout(LayoutKind.Sequential)]
public readonly struct Color3 : IEquatable<Color3>, IFormattable, ISpanFormattable {
    /// <summary>Number of components.</summary>
    public const int ComponentCount = 3;

    /// <summary>The red component.</summary>
    public readonly float R;

    /// <summary>The green component.</summary>
    public readonly float G;

    /// <summary>The blue component.</summary>
    public readonly float B;

    /// <summary>Black.</summary>
    public static Color3 Black => default;

    /// <summary>White.</summary>
    public static Color3 White => new(1f, 1f, 1f);

    /// <summary>Builds a colour from its components.</summary>
    /// <param name="r">The red component.</param>
    /// <param name="g">The green component.</param>
    /// <param name="b">The blue component.</param>
    public Color3(float r, float g, float b) {
        R = r;
        G = g;
        B = b;
    }

    /// <summary>A grey with the given intensity.</summary>
    /// <param name="intensity">The value for every component.</param>
    public Color3(float intensity)
        : this(intensity, intensity, intensity) { }

    /// <summary>The component at <paramref name="index" />.</summary>
    /// <param name="index">0 for red, 1 for green, 2 for blue.</param>
    /// <returns>The component.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index" /> is not 0, 1 or 2.</exception>
    public float this[int index] =>
        index switch {
            0 => R,
            1 => G,
            2 => B,
            _ => throw new ArgumentOutOfRangeException(nameof(index), index, "A Color3 has three components.")
        };

    /// <summary>The components as a span. Valid as long as the colour it came from.</summary>
    /// <returns>A span of <see cref="ComponentCount" /> floats.</returns>
    [UnscopedRef]
    public ReadOnlySpan<float> AsSpan() => MemoryMarshal.CreateReadOnlySpan(in R, ComponentCount);

    /// <summary>The relative luminance.</summary>
    /// <returns>The luminance.</returns>
    public float Luminance() => ColorSpace.Luminance(new(R, G, B));

    /// <summary>Decodes an sRGB-encoded colour into linear space.</summary>
    /// <param name="srgb">The encoded colour.</param>
    /// <returns>The linear colour.</returns>
    public static Color3 FromSrgb(Color3 srgb) =>
        new(ColorSpace.SrgbToLinear(srgb.R), ColorSpace.SrgbToLinear(srgb.G), ColorSpace.SrgbToLinear(srgb.B));

    /// <summary>Encodes this linear colour as sRGB.</summary>
    /// <returns>The encoded colour.</returns>
    public Color3 ToSrgb() =>
        new(ColorSpace.LinearToSrgb(R), ColorSpace.LinearToSrgb(G), ColorSpace.LinearToSrgb(B));

    /// <summary>Interpolates between two colours.</summary>
    /// <param name="from">The colour at <paramref name="amount" /> = 0.</param>
    /// <param name="to">The colour at <paramref name="amount" /> = 1.</param>
    /// <param name="amount">The interpolant. Not clamped.</param>
    /// <returns>The interpolated colour.</returns>
    public static Color3 Lerp(Color3 from, Color3 to, float amount) =>
        new(
            MathUtil.Lerp(from.R, to.R, amount),
            MathUtil.Lerp(from.G, to.G, amount),
            MathUtil.Lerp(from.B, to.B, amount)
        );

    /// <summary>Whether two colours agree to within a tolerance.</summary>
    /// <param name="left">The first colour.</param>
    /// <param name="right">The second colour.</param>
    /// <param name="tolerance">The relative tolerance.</param>
    /// <returns><see langword="true" /> if every component is within tolerance.</returns>
    public static bool NearEqual(Color3 left, Color3 right, float tolerance = MathUtil.ZeroTolerance) =>
        MathUtil.NearEqual(left.R, right.R, tolerance)
        && MathUtil.NearEqual(left.G, right.G, tolerance)
        && MathUtil.NearEqual(left.B, right.B, tolerance);

    /// <summary>Adds two colours.</summary>
    /// <param name="left">The first colour.</param>
    /// <param name="right">The second colour.</param>
    /// <returns>The sum.</returns>
    public static Color3 operator +(Color3 left, Color3 right) =>
        new(left.R + right.R, left.G + right.G, left.B + right.B);

    /// <summary>Subtracts one colour from another.</summary>
    /// <param name="left">The colour to subtract from.</param>
    /// <param name="right">The colour to subtract.</param>
    /// <returns>The difference.</returns>
    public static Color3 operator -(Color3 left, Color3 right) =>
        new(left.R - right.R, left.G - right.G, left.B - right.B);

    /// <summary>Multiplies component by component.</summary>
    /// <param name="left">The first colour.</param>
    /// <param name="right">The second colour.</param>
    /// <returns>The product.</returns>
    public static Color3 operator *(Color3 left, Color3 right) =>
        new(left.R * right.R, left.G * right.G, left.B * right.B);

    /// <summary>Scales a colour.</summary>
    /// <param name="color">The colour.</param>
    /// <param name="scale">The scale factor.</param>
    /// <returns>The scaled colour.</returns>
    public static Color3 operator *(Color3 color, float scale) =>
        new(color.R * scale, color.G * scale, color.B * scale);

    /// <summary>Scales a colour.</summary>
    /// <param name="scale">The scale factor.</param>
    /// <param name="color">The colour.</param>
    /// <returns>The scaled colour.</returns>
    public static Color3 operator *(float scale, Color3 color) => color * scale;

    /// <summary>Exact component-wise equality, IEEE semantics. See <see cref="NearEqual" />.</summary>
    /// <param name="left">The first colour.</param>
    /// <param name="right">The second colour.</param>
    /// <returns><see langword="true" /> if every component is equal.</returns>
    public static bool operator ==(Color3 left, Color3 right) =>
        left.R == right.R && left.G == right.G && left.B == right.B;

    /// <summary>The negation of <see cref="op_Equality" />.</summary>
    /// <param name="left">The first colour.</param>
    /// <param name="right">The second colour.</param>
    /// <returns><see langword="true" /> if any component differs.</returns>
    public static bool operator !=(Color3 left, Color3 right) => !(left == right);

    /// <summary>Reinterprets the colour as a vector.</summary>
    /// <param name="color">The colour.</param>
    /// <returns>The same three floats.</returns>
    public static explicit operator Vector3(Color3 color) => new(color.R, color.G, color.B);

    /// <summary>Reinterprets a vector as a colour.</summary>
    /// <param name="vector">The vector.</param>
    /// <returns>The same three floats.</returns>
    public static explicit operator Color3(Vector3 vector) => new(vector.X, vector.Y, vector.Z);

    /// <summary>Splits the colour into its components.</summary>
    /// <param name="r">The red component.</param>
    /// <param name="g">The green component.</param>
    /// <param name="b">The blue component.</param>
    public void Deconstruct(out float r, out float g, out float b) {
        r = R;
        g = G;
        b = B;
    }

    /// <inheritdoc />
    public bool Equals(Color3 other) => this == other;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Color3 other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(R, G, B);

    /// <inheritdoc />
    public override string ToString() => ToString(null, null);

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) =>
        VectorFormat.ToString(format, formatProvider, AsSpan());

    /// <inheritdoc />
    public bool TryFormat(
        Span<char> destination,
        out int charsWritten,
        ReadOnlySpan<char> format = default,
        IFormatProvider? provider = null
    ) =>
        VectorFormat.TryFormat(destination, out charsWritten, format, provider, AsSpan());
}
