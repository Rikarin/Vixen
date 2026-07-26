// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Runtime.InteropServices;

namespace Vixen.Core.Mathematics;

/// <summary>
///     A two-component integer vector: pixel and texel coordinates, texture and window sizes, grid
///     cells — the places where a <see cref="Vector2" /> would invite a rounding decision that has
///     no right answer.
/// </summary>
[DataContract]
[StructLayout(LayoutKind.Sequential)]
public readonly struct Int2 : IEquatable<Int2>, IFormattable {
    /// <summary>The X component.</summary>
    public readonly int X;

    /// <summary>The Y component.</summary>
    public readonly int Y;

    /// <summary>All components zero.</summary>
    public static Int2 Zero => default;

    /// <summary>All components one.</summary>
    public static Int2 One => new(1, 1);

    /// <summary>The X axis.</summary>
    public static Int2 UnitX => new(1, 0);

    /// <summary>The Y axis.</summary>
    public static Int2 UnitY => new(0, 1);

    /// <summary>Builds a vector from its components.</summary>
    /// <param name="x">The X component.</param>
    /// <param name="y">The Y component.</param>
    public Int2(int x, int y) {
        X = x;
        Y = y;
    }

    /// <summary>Builds a vector with every component set to <paramref name="value" />.</summary>
    /// <param name="value">The value for every component.</param>
    public Int2(int value) {
        X = value;
        Y = value;
    }

    /// <summary>The component at <paramref name="index" />.</summary>
    /// <param name="index">0 for X, 1 for Y.</param>
    /// <returns>The component.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index" /> is not 0 or 1.</exception>
    public int this[int index] =>
        index switch {
            0 => X,
            1 => Y,
            _ => throw new ArgumentOutOfRangeException(nameof(index), index, "An Int2 has two components.")
        };

    /// <summary>The product of the components — the area of a size, the texel count of an extent.</summary>
    public long Area => (long)X * Y;

    /// <summary>The component-wise minimum.</summary>
    /// <param name="left">The first vector.</param>
    /// <param name="right">The second vector.</param>
    /// <returns>The component-wise minimum.</returns>
    public static Int2 Min(Int2 left, Int2 right) => new(Math.Min(left.X, right.X), Math.Min(left.Y, right.Y));

    /// <summary>The component-wise maximum.</summary>
    /// <param name="left">The first vector.</param>
    /// <param name="right">The second vector.</param>
    /// <returns>The component-wise maximum.</returns>
    public static Int2 Max(Int2 left, Int2 right) => new(Math.Max(left.X, right.X), Math.Max(left.Y, right.Y));

    /// <summary>Constrains each component to the matching interval.</summary>
    /// <param name="value">The vector to constrain.</param>
    /// <param name="min">The lower bounds.</param>
    /// <param name="max">The upper bounds.</param>
    /// <returns>The constrained vector.</returns>
    public static Int2 Clamp(Int2 value, Int2 min, Int2 max) => Min(Max(value, min), max);

    /// <summary>Adds two vectors.</summary>
    /// <param name="left">The first vector.</param>
    /// <param name="right">The second vector.</param>
    /// <returns>The sum.</returns>
    public static Int2 operator +(Int2 left, Int2 right) => new(left.X + right.X, left.Y + right.Y);

    /// <summary>Subtracts one vector from another.</summary>
    /// <param name="left">The vector to subtract from.</param>
    /// <param name="right">The vector to subtract.</param>
    /// <returns>The difference.</returns>
    public static Int2 operator -(Int2 left, Int2 right) => new(left.X - right.X, left.Y - right.Y);

    /// <summary>Negates a vector.</summary>
    /// <param name="value">The vector.</param>
    /// <returns>The negated vector.</returns>
    public static Int2 operator -(Int2 value) => new(-value.X, -value.Y);

    /// <summary>Multiplies component by component.</summary>
    /// <param name="left">The first vector.</param>
    /// <param name="right">The second vector.</param>
    /// <returns>The component-wise product.</returns>
    public static Int2 operator *(Int2 left, Int2 right) => new(left.X * right.X, left.Y * right.Y);

    /// <summary>Scales a vector.</summary>
    /// <param name="value">The vector.</param>
    /// <param name="scale">The scale factor.</param>
    /// <returns>The scaled vector.</returns>
    public static Int2 operator *(Int2 value, int scale) => new(value.X * scale, value.Y * scale);

    /// <summary>Scales a vector.</summary>
    /// <param name="scale">The scale factor.</param>
    /// <param name="value">The vector.</param>
    /// <returns>The scaled vector.</returns>
    public static Int2 operator *(int scale, Int2 value) => value * scale;

    /// <summary>Divides component by component, truncating toward zero.</summary>
    /// <param name="left">The dividend.</param>
    /// <param name="right">The divisor.</param>
    /// <returns>The component-wise quotient.</returns>
    public static Int2 operator /(Int2 left, Int2 right) => new(left.X / right.X, left.Y / right.Y);

    /// <summary>Divides by a scalar, truncating toward zero.</summary>
    /// <param name="value">The dividend.</param>
    /// <param name="divisor">The divisor.</param>
    /// <returns>The quotient.</returns>
    public static Int2 operator /(Int2 value, int divisor) => new(value.X / divisor, value.Y / divisor);

    /// <summary>Component-wise equality.</summary>
    /// <param name="left">The first vector.</param>
    /// <param name="right">The second vector.</param>
    /// <returns><see langword="true" /> if every component is equal.</returns>
    public static bool operator ==(Int2 left, Int2 right) => left.X == right.X && left.Y == right.Y;

    /// <summary>The negation of <see cref="op_Equality" />.</summary>
    /// <param name="left">The first vector.</param>
    /// <param name="right">The second vector.</param>
    /// <returns><see langword="true" /> if any component differs.</returns>
    public static bool operator !=(Int2 left, Int2 right) => !(left == right);

    /// <summary>Widens to a float vector, which is always exact for these magnitudes.</summary>
    /// <param name="value">The vector to convert.</param>
    /// <returns>The equivalent <see cref="Vector2" />.</returns>
    public static implicit operator Vector2(Int2 value) => new(value.X, value.Y);

    /// <summary>Splits the vector into its components.</summary>
    /// <param name="x">The X component.</param>
    /// <param name="y">The Y component.</param>
    public void Deconstruct(out int x, out int y) {
        x = X;
        y = Y;
    }

    /// <inheritdoc />
    public bool Equals(Int2 other) => this == other;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Int2 other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(X, Y);

    /// <inheritdoc />
    public override string ToString() => ToString(null, null);

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) {
        formatProvider ??= CultureInfo.InvariantCulture;
        return $"({X.ToString(format, formatProvider)}, {Y.ToString(format, formatProvider)})";
    }
}
