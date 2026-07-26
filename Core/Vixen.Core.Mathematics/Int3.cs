// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Runtime.InteropServices;

namespace Vixen.Core.Mathematics;

/// <summary>
///     A three-component integer vector: volume texture extents, voxel and cluster coordinates,
///     compute dispatch sizes.
/// </summary>
[DataContract]
[StructLayout(LayoutKind.Sequential)]
public readonly struct Int3 : IEquatable<Int3>, IFormattable {
    /// <summary>The X component.</summary>
    public readonly int X;

    /// <summary>The Y component.</summary>
    public readonly int Y;

    /// <summary>The Z component.</summary>
    public readonly int Z;

    /// <summary>All components zero.</summary>
    public static Int3 Zero => default;

    /// <summary>All components one.</summary>
    public static Int3 One => new(1, 1, 1);

    /// <summary>The X axis.</summary>
    public static Int3 UnitX => new(1, 0, 0);

    /// <summary>The Y axis.</summary>
    public static Int3 UnitY => new(0, 1, 0);

    /// <summary>The Z axis.</summary>
    public static Int3 UnitZ => new(0, 0, 1);

    /// <summary>Builds a vector from its components.</summary>
    /// <param name="x">The X component.</param>
    /// <param name="y">The Y component.</param>
    /// <param name="z">The Z component.</param>
    public Int3(int x, int y, int z) {
        X = x;
        Y = y;
        Z = z;
    }

    /// <summary>Builds a vector with every component set to <paramref name="value" />.</summary>
    /// <param name="value">The value for every component.</param>
    public Int3(int value) {
        X = value;
        Y = value;
        Z = value;
    }

    /// <summary>The component at <paramref name="index" />.</summary>
    /// <param name="index">0 for X, 1 for Y, 2 for Z.</param>
    /// <returns>The component.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index" /> is not 0, 1 or 2.</exception>
    public int this[int index] =>
        index switch {
            0 => X,
            1 => Y,
            2 => Z,
            _ => throw new ArgumentOutOfRangeException(nameof(index), index, "An Int3 has three components.")
        };

    /// <summary>The X and Y components.</summary>
    public Int2 Xy => new(X, Y);

    /// <summary>The product of the components — the element count of a volume.</summary>
    public long Volume => (long)X * Y * Z;

    /// <summary>The component-wise minimum.</summary>
    /// <param name="left">The first vector.</param>
    /// <param name="right">The second vector.</param>
    /// <returns>The component-wise minimum.</returns>
    public static Int3 Min(Int3 left, Int3 right) =>
        new(Math.Min(left.X, right.X), Math.Min(left.Y, right.Y), Math.Min(left.Z, right.Z));

    /// <summary>The component-wise maximum.</summary>
    /// <param name="left">The first vector.</param>
    /// <param name="right">The second vector.</param>
    /// <returns>The component-wise maximum.</returns>
    public static Int3 Max(Int3 left, Int3 right) =>
        new(Math.Max(left.X, right.X), Math.Max(left.Y, right.Y), Math.Max(left.Z, right.Z));

    /// <summary>Constrains each component to the matching interval.</summary>
    /// <param name="value">The vector to constrain.</param>
    /// <param name="min">The lower bounds.</param>
    /// <param name="max">The upper bounds.</param>
    /// <returns>The constrained vector.</returns>
    public static Int3 Clamp(Int3 value, Int3 min, Int3 max) => Min(Max(value, min), max);

    /// <summary>Adds two vectors.</summary>
    /// <param name="left">The first vector.</param>
    /// <param name="right">The second vector.</param>
    /// <returns>The sum.</returns>
    public static Int3 operator +(Int3 left, Int3 right) =>
        new(left.X + right.X, left.Y + right.Y, left.Z + right.Z);

    /// <summary>Subtracts one vector from another.</summary>
    /// <param name="left">The vector to subtract from.</param>
    /// <param name="right">The vector to subtract.</param>
    /// <returns>The difference.</returns>
    public static Int3 operator -(Int3 left, Int3 right) =>
        new(left.X - right.X, left.Y - right.Y, left.Z - right.Z);

    /// <summary>Negates a vector.</summary>
    /// <param name="value">The vector.</param>
    /// <returns>The negated vector.</returns>
    public static Int3 operator -(Int3 value) => new(-value.X, -value.Y, -value.Z);

    /// <summary>Multiplies component by component.</summary>
    /// <param name="left">The first vector.</param>
    /// <param name="right">The second vector.</param>
    /// <returns>The component-wise product.</returns>
    public static Int3 operator *(Int3 left, Int3 right) =>
        new(left.X * right.X, left.Y * right.Y, left.Z * right.Z);

    /// <summary>Scales a vector.</summary>
    /// <param name="value">The vector.</param>
    /// <param name="scale">The scale factor.</param>
    /// <returns>The scaled vector.</returns>
    public static Int3 operator *(Int3 value, int scale) => new(value.X * scale, value.Y * scale, value.Z * scale);

    /// <summary>Scales a vector.</summary>
    /// <param name="scale">The scale factor.</param>
    /// <param name="value">The vector.</param>
    /// <returns>The scaled vector.</returns>
    public static Int3 operator *(int scale, Int3 value) => value * scale;

    /// <summary>Divides component by component, truncating toward zero.</summary>
    /// <param name="left">The dividend.</param>
    /// <param name="right">The divisor.</param>
    /// <returns>The component-wise quotient.</returns>
    public static Int3 operator /(Int3 left, Int3 right) =>
        new(left.X / right.X, left.Y / right.Y, left.Z / right.Z);

    /// <summary>Divides by a scalar, truncating toward zero.</summary>
    /// <param name="value">The dividend.</param>
    /// <param name="divisor">The divisor.</param>
    /// <returns>The quotient.</returns>
    public static Int3 operator /(Int3 value, int divisor) =>
        new(value.X / divisor, value.Y / divisor, value.Z / divisor);

    /// <summary>Component-wise equality.</summary>
    /// <param name="left">The first vector.</param>
    /// <param name="right">The second vector.</param>
    /// <returns><see langword="true" /> if every component is equal.</returns>
    public static bool operator ==(Int3 left, Int3 right) =>
        left.X == right.X && left.Y == right.Y && left.Z == right.Z;

    /// <summary>The negation of <see cref="op_Equality" />.</summary>
    /// <param name="left">The first vector.</param>
    /// <param name="right">The second vector.</param>
    /// <returns><see langword="true" /> if any component differs.</returns>
    public static bool operator !=(Int3 left, Int3 right) => !(left == right);

    /// <summary>Widens to a float vector, which is always exact for these magnitudes.</summary>
    /// <param name="value">The vector to convert.</param>
    /// <returns>The equivalent <see cref="Vector3" />.</returns>
    public static implicit operator Vector3(Int3 value) => new(value.X, value.Y, value.Z);

    /// <summary>Splits the vector into its components.</summary>
    /// <param name="x">The X component.</param>
    /// <param name="y">The Y component.</param>
    /// <param name="z">The Z component.</param>
    public void Deconstruct(out int x, out int y, out int z) {
        x = X;
        y = Y;
        z = Z;
    }

    /// <inheritdoc />
    public bool Equals(Int3 other) => this == other;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Int3 other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(X, Y, Z);

    /// <inheritdoc />
    public override string ToString() => ToString(null, null);

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) {
        formatProvider ??= CultureInfo.InvariantCulture;
        return
            $"({X.ToString(format, formatProvider)}, {Y.ToString(format, formatProvider)}, {Z.ToString(format, formatProvider)})";
    }
}
