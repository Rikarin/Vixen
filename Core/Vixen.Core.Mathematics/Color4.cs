// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Vixen.Core.Mathematics;

/// <summary>
///     A colour with alpha, as four floats. **Linear**, unbounded above — an HDR light or an
///     emissive material is a colour with components far past 1, and clamping here would throw that
///     away before tonemapping ever sees it.
/// </summary>
/// <remarks>
///     A separate type from <see cref="Vector4" /> on purpose. They have the same layout and convert
///     freely, but a signature taking a <c>Color4</c> says what it wants, and colour has operations
///     — premultiplication, luminance, sRGB encoding — that are nonsense on a position.
/// </remarks>
[DataContract]
[StructLayout(LayoutKind.Sequential)]
public readonly struct Color4 : IEquatable<Color4>, IFormattable, ISpanFormattable {
    /// <summary>Number of components.</summary>
    public const int ComponentCount = 4;

    /// <summary>The red component.</summary>
    public readonly float R;

    /// <summary>The green component.</summary>
    public readonly float G;

    /// <summary>The blue component.</summary>
    public readonly float B;

    /// <summary>The alpha component: 0 fully transparent, 1 fully opaque.</summary>
    public readonly float A;

    /// <summary>Opaque black.</summary>
    public static Color4 Black => new(0f, 0f, 0f, 1f);

    /// <summary>Opaque white.</summary>
    public static Color4 White => new(1f, 1f, 1f, 1f);

    /// <summary>Fully transparent, and black rather than white so premultiplied blending behaves.</summary>
    public static Color4 Transparent => default;

    /// <summary>Opaque red.</summary>
    public static Color4 Red => new(1f, 0f, 0f, 1f);

    /// <summary>Opaque green.</summary>
    public static Color4 Green => new(0f, 1f, 0f, 1f);

    /// <summary>Opaque blue.</summary>
    public static Color4 Blue => new(0f, 0f, 1f, 1f);

    /// <summary>Builds a colour from its components.</summary>
    /// <param name="r">The red component.</param>
    /// <param name="g">The green component.</param>
    /// <param name="b">The blue component.</param>
    /// <param name="a">The alpha component.</param>
    public Color4(float r, float g, float b, float a) {
        R = r;
        G = g;
        B = b;
        A = a;
    }

    /// <summary>Builds an opaque colour.</summary>
    /// <param name="r">The red component.</param>
    /// <param name="g">The green component.</param>
    /// <param name="b">The blue component.</param>
    public Color4(float r, float g, float b)
        : this(r, g, b, 1f) { }

    /// <summary>Builds a colour from an RGB triple and an alpha.</summary>
    /// <param name="rgb">The colour.</param>
    /// <param name="a">The alpha component.</param>
    public Color4(Color3 rgb, float a)
        : this(rgb.R, rgb.G, rgb.B, a) { }

    /// <summary>A grey with the given intensity, fully opaque.</summary>
    /// <param name="intensity">The value for red, green and blue.</param>
    public Color4(float intensity)
        : this(intensity, intensity, intensity, 1f) { }

    /// <summary>The colour without its alpha.</summary>
    public Color3 Rgb => new(R, G, B);

    /// <summary>The component at <paramref name="index" />.</summary>
    /// <param name="index">0 for red, through 3 for alpha.</param>
    /// <returns>The component.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index" /> is not 0 to 3.</exception>
    public float this[int index] =>
        index switch {
            0 => R,
            1 => G,
            2 => B,
            3 => A,
            _ => throw new ArgumentOutOfRangeException(nameof(index), index, "A Color4 has four components.")
        };

    /// <summary>The components as a span. Valid as long as the colour it came from.</summary>
    /// <returns>A span of <see cref="ComponentCount" /> floats.</returns>
    [UnscopedRef]
    public ReadOnlySpan<float> AsSpan() => MemoryMarshal.CreateReadOnlySpan(in R, ComponentCount);

    /// <summary>The relative luminance, which is only meaningful because this is linear.</summary>
    /// <returns>The luminance.</returns>
    public float Luminance() => ColorSpace.Luminance(new(R, G, B));

    /// <summary>Decodes an sRGB-encoded colour into this linear space.</summary>
    /// <param name="srgb">The encoded colour. Alpha is copied through, never encoded.</param>
    /// <returns>The linear colour.</returns>
    /// <remarks>
    ///     Alpha is a coverage fraction, not a light level, so it is linear already. Passing it
    ///     through the transfer function is a classic and very visible mistake.
    /// </remarks>
    public static Color4 FromSrgb(Color4 srgb) =>
        new(
            ColorSpace.SrgbToLinear(srgb.R),
            ColorSpace.SrgbToLinear(srgb.G),
            ColorSpace.SrgbToLinear(srgb.B),
            srgb.A
        );

    /// <summary>Encodes this linear colour as sRGB.</summary>
    /// <returns>The encoded colour, with alpha unchanged.</returns>
    public Color4 ToSrgb() =>
        new(ColorSpace.LinearToSrgb(R), ColorSpace.LinearToSrgb(G), ColorSpace.LinearToSrgb(B), A);

    /// <summary>
    ///     Multiplies the colour by its own alpha, the form alpha blending actually wants.
    /// </summary>
    /// <returns>The premultiplied colour.</returns>
    public Color4 Premultiplied() => new(R * A, G * A, B * A, A);

    /// <summary>Constrains every component to <c>[0, 1]</c>.</summary>
    /// <param name="color">The colour.</param>
    /// <returns>The saturated colour.</returns>
    /// <remarks>
    ///     Not automatic. A value above 1 is a bright light, and losing it before tonemapping is
    ///     how a render ends up flat.
    /// </remarks>
    public static Color4 Saturate(Color4 color) =>
        new(
            MathUtil.Saturate(color.R),
            MathUtil.Saturate(color.G),
            MathUtil.Saturate(color.B),
            MathUtil.Saturate(color.A)
        );

    /// <summary>Interpolates between two colours, component by component.</summary>
    /// <param name="from">The colour at <paramref name="amount" /> = 0.</param>
    /// <param name="to">The colour at <paramref name="amount" /> = 1.</param>
    /// <param name="amount">The interpolant. Not clamped.</param>
    /// <returns>The interpolated colour.</returns>
    /// <remarks>
    ///     Correct precisely because these values are linear: the same interpolation on sRGB numbers
    ///     produces the dark, muddy midpoints that make gradients look wrong.
    /// </remarks>
    public static Color4 Lerp(Color4 from, Color4 to, float amount) =>
        new(
            MathUtil.Lerp(from.R, to.R, amount),
            MathUtil.Lerp(from.G, to.G, amount),
            MathUtil.Lerp(from.B, to.B, amount),
            MathUtil.Lerp(from.A, to.A, amount)
        );

    /// <summary>Whether two colours agree to within a tolerance.</summary>
    /// <param name="left">The first colour.</param>
    /// <param name="right">The second colour.</param>
    /// <param name="tolerance">The relative tolerance.</param>
    /// <returns><see langword="true" /> if every component is within tolerance.</returns>
    public static bool NearEqual(Color4 left, Color4 right, float tolerance = MathUtil.ZeroTolerance) =>
        MathUtil.NearEqual(left.R, right.R, tolerance)
        && MathUtil.NearEqual(left.G, right.G, tolerance)
        && MathUtil.NearEqual(left.B, right.B, tolerance)
        && MathUtil.NearEqual(left.A, right.A, tolerance);

    /// <summary>Adds two colours — combining two lights.</summary>
    /// <param name="left">The first colour.</param>
    /// <param name="right">The second colour.</param>
    /// <returns>The sum.</returns>
    public static Color4 operator +(Color4 left, Color4 right) =>
        new(left.R + right.R, left.G + right.G, left.B + right.B, left.A + right.A);

    /// <summary>Subtracts one colour from another.</summary>
    /// <param name="left">The colour to subtract from.</param>
    /// <param name="right">The colour to subtract.</param>
    /// <returns>The difference.</returns>
    public static Color4 operator -(Color4 left, Color4 right) =>
        new(left.R - right.R, left.G - right.G, left.B - right.B, left.A - right.A);

    /// <summary>Multiplies component by component — tinting, or a light meeting an albedo.</summary>
    /// <param name="left">The first colour.</param>
    /// <param name="right">The second colour.</param>
    /// <returns>The product.</returns>
    public static Color4 operator *(Color4 left, Color4 right) =>
        new(left.R * right.R, left.G * right.G, left.B * right.B, left.A * right.A);

    /// <summary>Scales a colour — changing an intensity.</summary>
    /// <param name="color">The colour.</param>
    /// <param name="scale">The scale factor.</param>
    /// <returns>The scaled colour.</returns>
    public static Color4 operator *(Color4 color, float scale) =>
        new(color.R * scale, color.G * scale, color.B * scale, color.A * scale);

    /// <summary>Scales a colour.</summary>
    /// <param name="scale">The scale factor.</param>
    /// <param name="color">The colour.</param>
    /// <returns>The scaled colour.</returns>
    public static Color4 operator *(float scale, Color4 color) => color * scale;

    /// <summary>Exact component-wise equality, IEEE semantics. See <see cref="NearEqual" />.</summary>
    /// <param name="left">The first colour.</param>
    /// <param name="right">The second colour.</param>
    /// <returns><see langword="true" /> if every component is equal.</returns>
    public static bool operator ==(Color4 left, Color4 right) =>
        left.R == right.R && left.G == right.G && left.B == right.B && left.A == right.A;

    /// <summary>The negation of <see cref="op_Equality" />.</summary>
    /// <param name="left">The first colour.</param>
    /// <param name="right">The second colour.</param>
    /// <returns><see langword="true" /> if any component differs.</returns>
    public static bool operator !=(Color4 left, Color4 right) => !(left == right);

    /// <summary>Reinterprets the colour as a vector, for the shader constants that take one.</summary>
    /// <param name="color">The colour.</param>
    /// <returns>The same four floats.</returns>
    public static explicit operator Vector4(Color4 color) => new(color.R, color.G, color.B, color.A);

    /// <summary>Reinterprets a vector as a colour.</summary>
    /// <param name="vector">The vector.</param>
    /// <returns>The same four floats.</returns>
    public static explicit operator Color4(Vector4 vector) => new(vector.X, vector.Y, vector.Z, vector.W);

    /// <summary>Splits the colour into its components.</summary>
    /// <param name="r">The red component.</param>
    /// <param name="g">The green component.</param>
    /// <param name="b">The blue component.</param>
    /// <param name="a">The alpha component.</param>
    public void Deconstruct(out float r, out float g, out float b, out float a) {
        r = R;
        g = G;
        b = B;
        a = A;
    }

    /// <inheritdoc />
    public bool Equals(Color4 other) => this == other;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Color4 other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(R, G, B, A);

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
