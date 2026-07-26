// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Vixen.Core.Mathematics;

/// <summary>
///     A two-component vector: texture coordinates, screen positions, sizes, and the layout
///     engine's currency.
/// </summary>
/// <remarks>UV origin is top-left, so V increases downward. See <c>Conventions.md</c>.</remarks>
[DataContract]
[StructLayout(LayoutKind.Sequential)]
public readonly struct Vector2 : IEquatable<Vector2>, IFormattable, ISpanFormattable {
    /// <summary>Number of components.</summary>
    public const int ComponentCount = 2;

    /// <summary>The X component.</summary>
    public readonly float X;

    /// <summary>The Y component.</summary>
    public readonly float Y;

    /// <summary>All components zero.</summary>
    public static Vector2 Zero => default;

    /// <summary>All components one.</summary>
    public static Vector2 One => new(1f, 1f);

    /// <summary>The X axis.</summary>
    public static Vector2 UnitX => new(1f, 0f);

    /// <summary>The Y axis.</summary>
    public static Vector2 UnitY => new(0f, 1f);

    /// <summary>Builds a vector from its components.</summary>
    /// <param name="x">The X component.</param>
    /// <param name="y">The Y component.</param>
    public Vector2(float x, float y) {
        X = x;
        Y = y;
    }

    /// <summary>Builds a vector with every component set to <paramref name="value" />.</summary>
    /// <param name="value">The value for every component.</param>
    public Vector2(float value) {
        X = value;
        Y = value;
    }

    /// <summary>The component at <paramref name="index" />.</summary>
    /// <param name="index">0 for X, 1 for Y.</param>
    /// <returns>The component.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index" /> is not 0 or 1.</exception>
    public float this[int index] =>
        index switch {
            0 => X,
            1 => Y,
            _ => throw new ArgumentOutOfRangeException(nameof(index), index, "A Vector2 has two components.")
        };

    /// <summary>The components as a span. Valid as long as the vector it came from.</summary>
    /// <returns>A span of <see cref="ComponentCount" /> floats.</returns>
    [UnscopedRef]
    public ReadOnlySpan<float> AsSpan() => MemoryMarshal.CreateReadOnlySpan(in X, ComponentCount);

    /// <summary>Copies the components into a caller-owned span.</summary>
    /// <param name="destination">A span of at least <see cref="ComponentCount" /> floats.</param>
    public void CopyTo(Span<float> destination) {
        destination[1] = Y;
        destination[0] = X;
    }

    /// <summary>The length of the vector.</summary>
    /// <returns>The length.</returns>
    public float Length() => MathF.Sqrt((X * X) + (Y * Y));

    /// <summary>The squared length, which avoids a square root where only comparison matters.</summary>
    /// <returns>The squared length.</returns>
    public float LengthSquared() => (X * X) + (Y * Y);

    /// <summary>Whether every component is zero.</summary>
    public bool IsZero => X == 0f && Y == 0f;

    /// <summary>Whether any component is NaN.</summary>
    public bool IsNaN => float.IsNaN(X) || float.IsNaN(Y);

    /// <summary>The vector scaled to unit length; <see cref="Zero" /> if it is degenerate.</summary>
    /// <param name="value">The vector to normalise.</param>
    /// <returns>The unit-length vector.</returns>
    public static Vector2 Normalize(Vector2 value) {
        var lengthSquared = value.LengthSquared();
        if (lengthSquared < MathUtil.ZeroTolerance * MathUtil.ZeroTolerance) {
            return Zero;
        }

        var inverse = 1f / MathF.Sqrt(lengthSquared);
        return new(value.X * inverse, value.Y * inverse);
    }

    /// <summary>The dot product.</summary>
    /// <param name="left">The first vector.</param>
    /// <param name="right">The second vector.</param>
    /// <returns>The scalar product.</returns>
    public static float Dot(Vector2 left, Vector2 right) => (left.X * right.X) + (left.Y * right.Y);

    /// <summary>
    ///     The Z component of the 3D cross product — the signed area of the parallelogram, positive
    ///     when <paramref name="right" /> is counter-clockwise from <paramref name="left" />.
    /// </summary>
    /// <param name="left">The first vector.</param>
    /// <param name="right">The second vector.</param>
    /// <returns>The signed area.</returns>
    public static float Cross(Vector2 left, Vector2 right) => (left.X * right.Y) - (left.Y * right.X);

    /// <summary>The distance between two points.</summary>
    /// <param name="left">The first point.</param>
    /// <param name="right">The second point.</param>
    /// <returns>The distance.</returns>
    public static float Distance(Vector2 left, Vector2 right) => (left - right).Length();

    /// <summary>The squared distance between two points.</summary>
    /// <param name="left">The first point.</param>
    /// <param name="right">The second point.</param>
    /// <returns>The squared distance.</returns>
    public static float DistanceSquared(Vector2 left, Vector2 right) => (left - right).LengthSquared();

    /// <summary>Linearly interpolates between two vectors.</summary>
    /// <param name="from">The vector at <paramref name="amount" /> = 0.</param>
    /// <param name="to">The vector at <paramref name="amount" /> = 1.</param>
    /// <param name="amount">The interpolant. Not clamped.</param>
    /// <returns>The interpolated vector.</returns>
    public static Vector2 Lerp(Vector2 from, Vector2 to, float amount) => from + ((to - from) * amount);

    /// <summary>The component-wise minimum.</summary>
    /// <param name="left">The first vector.</param>
    /// <param name="right">The second vector.</param>
    /// <returns>The component-wise minimum.</returns>
    public static Vector2 Min(Vector2 left, Vector2 right) =>
        new(MathF.Min(left.X, right.X), MathF.Min(left.Y, right.Y));

    /// <summary>The component-wise maximum.</summary>
    /// <param name="left">The first vector.</param>
    /// <param name="right">The second vector.</param>
    /// <returns>The component-wise maximum.</returns>
    public static Vector2 Max(Vector2 left, Vector2 right) =>
        new(MathF.Max(left.X, right.X), MathF.Max(left.Y, right.Y));

    /// <summary>Constrains each component to the matching interval.</summary>
    /// <param name="value">The vector to constrain.</param>
    /// <param name="min">The lower bounds.</param>
    /// <param name="max">The upper bounds.</param>
    /// <returns>The constrained vector.</returns>
    public static Vector2 Clamp(Vector2 value, Vector2 min, Vector2 max) => Min(Max(value, min), max);

    /// <summary>The component-wise absolute value.</summary>
    /// <param name="value">The vector.</param>
    /// <returns>The component-wise absolute value.</returns>
    public static Vector2 Abs(Vector2 value) => new(MathF.Abs(value.X), MathF.Abs(value.Y));

    /// <summary>Whether two vectors agree to within a tolerance, component by component.</summary>
    /// <param name="left">The first vector.</param>
    /// <param name="right">The second vector.</param>
    /// <param name="tolerance">The relative tolerance.</param>
    /// <returns><see langword="true" /> if every component is within tolerance.</returns>
    public static bool NearEqual(Vector2 left, Vector2 right, float tolerance = MathUtil.ZeroTolerance) =>
        MathUtil.NearEqual(left.X, right.X, tolerance) && MathUtil.NearEqual(left.Y, right.Y, tolerance);

    /// <summary>Adds two vectors.</summary>
    /// <param name="left">The first vector.</param>
    /// <param name="right">The second vector.</param>
    /// <returns>The sum.</returns>
    public static Vector2 operator +(Vector2 left, Vector2 right) => new(left.X + right.X, left.Y + right.Y);

    /// <summary>Subtracts one vector from another.</summary>
    /// <param name="left">The vector to subtract from.</param>
    /// <param name="right">The vector to subtract.</param>
    /// <returns>The difference.</returns>
    public static Vector2 operator -(Vector2 left, Vector2 right) => new(left.X - right.X, left.Y - right.Y);

    /// <summary>Negates a vector.</summary>
    /// <param name="value">The vector.</param>
    /// <returns>The negated vector.</returns>
    public static Vector2 operator -(Vector2 value) => new(-value.X, -value.Y);

    /// <summary>Multiplies component by component.</summary>
    /// <param name="left">The first vector.</param>
    /// <param name="right">The second vector.</param>
    /// <returns>The component-wise product.</returns>
    public static Vector2 operator *(Vector2 left, Vector2 right) => new(left.X * right.X, left.Y * right.Y);

    /// <summary>Scales a vector.</summary>
    /// <param name="value">The vector.</param>
    /// <param name="scale">The scale factor.</param>
    /// <returns>The scaled vector.</returns>
    public static Vector2 operator *(Vector2 value, float scale) => new(value.X * scale, value.Y * scale);

    /// <summary>Scales a vector.</summary>
    /// <param name="scale">The scale factor.</param>
    /// <param name="value">The vector.</param>
    /// <returns>The scaled vector.</returns>
    public static Vector2 operator *(float scale, Vector2 value) => value * scale;

    /// <summary>Divides component by component.</summary>
    /// <param name="left">The dividend.</param>
    /// <param name="right">The divisor.</param>
    /// <returns>The component-wise quotient.</returns>
    public static Vector2 operator /(Vector2 left, Vector2 right) => new(left.X / right.X, left.Y / right.Y);

    /// <summary>Divides a vector by a scalar.</summary>
    /// <param name="value">The dividend.</param>
    /// <param name="divisor">The divisor.</param>
    /// <returns>The quotient.</returns>
    public static Vector2 operator /(Vector2 value, float divisor) {
        var inverse = 1f / divisor;
        return new(value.X * inverse, value.Y * inverse);
    }

    /// <summary>Exact component-wise equality, IEEE semantics. See <see cref="NearEqual" />.</summary>
    /// <param name="left">The first vector.</param>
    /// <param name="right">The second vector.</param>
    /// <returns><see langword="true" /> if every component is equal.</returns>
    public static bool operator ==(Vector2 left, Vector2 right) => left.X == right.X && left.Y == right.Y;

    /// <summary>The negation of <see cref="op_Equality" />.</summary>
    /// <param name="left">The first vector.</param>
    /// <param name="right">The second vector.</param>
    /// <returns><see langword="true" /> if any component differs.</returns>
    public static bool operator !=(Vector2 left, Vector2 right) => !(left == right);

    /// <summary>Converts to the BCL vector, which has the same layout.</summary>
    /// <param name="value">The vector to convert.</param>
    /// <returns>The equivalent <see cref="System.Numerics.Vector2" />.</returns>
    public static implicit operator System.Numerics.Vector2(Vector2 value) => new(value.X, value.Y);

    /// <summary>Converts from the BCL vector, which has the same layout.</summary>
    /// <param name="value">The vector to convert.</param>
    /// <returns>The equivalent <see cref="Vector2" />.</returns>
    public static implicit operator Vector2(System.Numerics.Vector2 value) => new(value.X, value.Y);

    /// <summary>Splits the vector into its components.</summary>
    /// <param name="x">The X component.</param>
    /// <param name="y">The Y component.</param>
    public void Deconstruct(out float x, out float y) {
        x = X;
        y = Y;
    }

    /// <inheritdoc />
    public bool Equals(Vector2 other) => this == other;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Vector2 other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(X, Y);

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
