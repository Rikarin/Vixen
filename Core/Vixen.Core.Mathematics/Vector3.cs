// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Vixen.Core.Mathematics;

/// <summary>
///     A three-component vector: positions, directions, scales, and everything else the engine
///     measures in world space.
/// </summary>
/// <remarks>
///     Right-handed, Y-up, −Z forward. Fields rather than properties so <c>ref</c> returns and
///     <c>Unsafe.As</c> reinterpretation are legal and free. See <c>Conventions.md</c>.
/// </remarks>
[DataContract]
[StructLayout(LayoutKind.Sequential)]
public readonly struct Vector3 : IEquatable<Vector3>, IFormattable, ISpanFormattable {
    /// <summary>Number of components.</summary>
    public const int ComponentCount = 3;

    /// <summary>The X component.</summary>
    public readonly float X;

    /// <summary>The Y component.</summary>
    public readonly float Y;

    /// <summary>The Z component.</summary>
    public readonly float Z;

    /// <summary>All components zero.</summary>
    public static Vector3 Zero => default;

    /// <summary>All components one.</summary>
    public static Vector3 One => new(1f, 1f, 1f);

    /// <summary>The X axis.</summary>
    public static Vector3 UnitX => new(1f, 0f, 0f);

    /// <summary>The Y axis.</summary>
    public static Vector3 UnitY => new(0f, 1f, 0f);

    /// <summary>The Z axis.</summary>
    public static Vector3 UnitZ => new(0f, 0f, 1f);

    /// <summary>+Y.</summary>
    public static Vector3 Up => new(0f, 1f, 0f);

    /// <summary>−Y.</summary>
    public static Vector3 Down => new(0f, -1f, 0f);

    /// <summary>+X.</summary>
    public static Vector3 Right => new(1f, 0f, 0f);

    /// <summary>−X.</summary>
    public static Vector3 Left => new(-1f, 0f, 0f);

    /// <summary>−Z. Right-handed, so the direction a camera looks is negative Z.</summary>
    public static Vector3 Forward => new(0f, 0f, -1f);

    /// <summary>+Z.</summary>
    public static Vector3 Backward => new(0f, 0f, 1f);

    /// <summary>Builds a vector from its components.</summary>
    /// <param name="x">The X component.</param>
    /// <param name="y">The Y component.</param>
    /// <param name="z">The Z component.</param>
    public Vector3(float x, float y, float z) {
        X = x;
        Y = y;
        Z = z;
    }

    /// <summary>Builds a vector with every component set to <paramref name="value" />.</summary>
    /// <param name="value">The value for every component.</param>
    public Vector3(float value) {
        X = value;
        Y = value;
        Z = value;
    }

    /// <summary>Extends a <see cref="Vector2" /> with a Z component.</summary>
    /// <param name="xy">The X and Y components.</param>
    /// <param name="z">The Z component.</param>
    public Vector3(Vector2 xy, float z) {
        X = xy.X;
        Y = xy.Y;
        Z = z;
    }

    /// <summary>The X and Y components.</summary>
    public Vector2 Xy => new(X, Y);

    /// <summary>The component at <paramref name="index" />.</summary>
    /// <param name="index">0 for X, 1 for Y, 2 for Z.</param>
    /// <returns>The component.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index" /> is not 0, 1 or 2.</exception>
    public float this[int index] =>
        index switch {
            0 => X,
            1 => Y,
            2 => Z,
            _ => throw new ArgumentOutOfRangeException(nameof(index), index, "A Vector3 has three components.")
        };

    /// <summary>
    ///     The vector's components as a span, for bulk copies and interop. <c>[UnscopedRef]</c>
    ///     because the span aliases this instance: it is valid exactly as long as the vector it came
    ///     from, and the compiler enforces that rather than trusting the caller.
    /// </summary>
    /// <returns>A span of <see cref="ComponentCount" /> floats.</returns>
    [UnscopedRef]
    public ReadOnlySpan<float> AsSpan() => MemoryMarshal.CreateReadOnlySpan(in X, ComponentCount);

    /// <summary>Copies the components into a caller-owned span.</summary>
    /// <param name="destination">A span of at least <see cref="ComponentCount" /> floats.</param>
    public void CopyTo(Span<float> destination) {
        destination[2] = Z;
        destination[1] = Y;
        destination[0] = X;
    }

    /// <summary>The length of the vector.</summary>
    public float Length() => MathF.Sqrt((X * X) + (Y * Y) + (Z * Z));

    /// <summary>
    ///     The squared length. Prefer it wherever the answer is only compared — culling, nearest
    ///     searches, tolerance tests — because it skips a square root per element.
    /// </summary>
    /// <returns>The squared length.</returns>
    public float LengthSquared() => (X * X) + (Y * Y) + (Z * Z);

    /// <summary>Whether every component is zero.</summary>
    public bool IsZero => X == 0f && Y == 0f && Z == 0f;

    /// <summary>Whether any component is NaN.</summary>
    public bool IsNaN => float.IsNaN(X) || float.IsNaN(Y) || float.IsNaN(Z);

    /// <summary>
    ///     The vector scaled to unit length. A zero-length vector normalises to
    ///     <see cref="Zero" /> rather than to NaN, so a degenerate input propagates as something
    ///     visible instead of poisoning every later comparison.
    /// </summary>
    /// <param name="value">The vector to normalise.</param>
    /// <returns>The unit-length vector.</returns>
    public static Vector3 Normalize(Vector3 value) {
        var lengthSquared = value.LengthSquared();
        if (lengthSquared < MathUtil.ZeroTolerance * MathUtil.ZeroTolerance) {
            return Zero;
        }

        var inverse = 1f / MathF.Sqrt(lengthSquared);
        return new(value.X * inverse, value.Y * inverse, value.Z * inverse);
    }

    /// <summary>The dot product.</summary>
    /// <param name="left">The first vector.</param>
    /// <param name="right">The second vector.</param>
    /// <returns>The scalar product.</returns>
    public static float Dot(Vector3 left, Vector3 right) =>
        (left.X * right.X) + (left.Y * right.Y) + (left.Z * right.Z);

    /// <summary>
    ///     The cross product, right-handed: <c>Cross(UnitX, UnitY)</c> is <c>UnitZ</c>.
    /// </summary>
    /// <param name="left">The first vector.</param>
    /// <param name="right">The second vector.</param>
    /// <returns>A vector perpendicular to both.</returns>
    public static Vector3 Cross(Vector3 left, Vector3 right) =>
        new(
            (left.Y * right.Z) - (left.Z * right.Y),
            (left.Z * right.X) - (left.X * right.Z),
            (left.X * right.Y) - (left.Y * right.X)
        );

    /// <summary>The distance between two points.</summary>
    /// <param name="left">The first point.</param>
    /// <param name="right">The second point.</param>
    /// <returns>The distance.</returns>
    public static float Distance(Vector3 left, Vector3 right) => (left - right).Length();

    /// <summary>The squared distance between two points.</summary>
    /// <param name="left">The first point.</param>
    /// <param name="right">The second point.</param>
    /// <returns>The squared distance.</returns>
    public static float DistanceSquared(Vector3 left, Vector3 right) => (left - right).LengthSquared();

    /// <summary>Linearly interpolates between two vectors.</summary>
    /// <param name="from">The vector at <paramref name="amount" /> = 0.</param>
    /// <param name="to">The vector at <paramref name="amount" /> = 1.</param>
    /// <param name="amount">The interpolant. Not clamped.</param>
    /// <returns>The interpolated vector.</returns>
    public static Vector3 Lerp(Vector3 from, Vector3 to, float amount) => from + ((to - from) * amount);

    /// <summary>The component-wise minimum.</summary>
    /// <param name="left">The first vector.</param>
    /// <param name="right">The second vector.</param>
    /// <returns>The component-wise minimum.</returns>
    public static Vector3 Min(Vector3 left, Vector3 right) =>
        new(MathF.Min(left.X, right.X), MathF.Min(left.Y, right.Y), MathF.Min(left.Z, right.Z));

    /// <summary>The component-wise maximum.</summary>
    /// <param name="left">The first vector.</param>
    /// <param name="right">The second vector.</param>
    /// <returns>The component-wise maximum.</returns>
    public static Vector3 Max(Vector3 left, Vector3 right) =>
        new(MathF.Max(left.X, right.X), MathF.Max(left.Y, right.Y), MathF.Max(left.Z, right.Z));

    /// <summary>Constrains each component to the matching interval.</summary>
    /// <param name="value">The vector to constrain.</param>
    /// <param name="min">The lower bounds.</param>
    /// <param name="max">The upper bounds.</param>
    /// <returns>The constrained vector.</returns>
    public static Vector3 Clamp(Vector3 value, Vector3 min, Vector3 max) => Min(Max(value, min), max);

    /// <summary>The component-wise absolute value.</summary>
    /// <param name="value">The vector.</param>
    /// <returns>The component-wise absolute value.</returns>
    public static Vector3 Abs(Vector3 value) => new(MathF.Abs(value.X), MathF.Abs(value.Y), MathF.Abs(value.Z));

    /// <summary>Reflects a direction about a surface normal.</summary>
    /// <param name="direction">The incoming direction.</param>
    /// <param name="normal">The surface normal. Expected to be unit length.</param>
    /// <returns>The reflected direction.</returns>
    public static Vector3 Reflect(Vector3 direction, Vector3 normal) =>
        direction - (normal * (2f * Dot(direction, normal)));

    /// <summary>The component of <paramref name="value" /> along <paramref name="onto" />.</summary>
    /// <param name="value">The vector to project.</param>
    /// <param name="onto">The vector to project onto.</param>
    /// <returns>The projection, or <see cref="Zero" /> if <paramref name="onto" /> is degenerate.</returns>
    public static Vector3 Project(Vector3 value, Vector3 onto) {
        var lengthSquared = onto.LengthSquared();
        return lengthSquared < MathUtil.ZeroTolerance * MathUtil.ZeroTolerance
            ? Zero
            : onto * (Dot(value, onto) / lengthSquared);
    }

    /// <summary>Whether two vectors agree to within a tolerance, component by component.</summary>
    /// <param name="left">The first vector.</param>
    /// <param name="right">The second vector.</param>
    /// <param name="tolerance">The relative tolerance.</param>
    /// <returns><see langword="true" /> if every component is within tolerance.</returns>
    public static bool NearEqual(Vector3 left, Vector3 right, float tolerance = MathUtil.ZeroTolerance) =>
        MathUtil.NearEqual(left.X, right.X, tolerance)
        && MathUtil.NearEqual(left.Y, right.Y, tolerance)
        && MathUtil.NearEqual(left.Z, right.Z, tolerance);

    /// <summary>Adds two vectors.</summary>
    /// <param name="left">The first vector.</param>
    /// <param name="right">The second vector.</param>
    /// <returns>The sum.</returns>
    public static Vector3 operator +(Vector3 left, Vector3 right) =>
        new(left.X + right.X, left.Y + right.Y, left.Z + right.Z);

    /// <summary>Subtracts one vector from another.</summary>
    /// <param name="left">The vector to subtract from.</param>
    /// <param name="right">The vector to subtract.</param>
    /// <returns>The difference.</returns>
    public static Vector3 operator -(Vector3 left, Vector3 right) =>
        new(left.X - right.X, left.Y - right.Y, left.Z - right.Z);

    /// <summary>Negates a vector.</summary>
    /// <param name="value">The vector.</param>
    /// <returns>The negated vector.</returns>
    public static Vector3 operator -(Vector3 value) => new(-value.X, -value.Y, -value.Z);

    /// <summary>Multiplies component by component.</summary>
    /// <param name="left">The first vector.</param>
    /// <param name="right">The second vector.</param>
    /// <returns>The component-wise product.</returns>
    public static Vector3 operator *(Vector3 left, Vector3 right) =>
        new(left.X * right.X, left.Y * right.Y, left.Z * right.Z);

    /// <summary>Scales a vector.</summary>
    /// <param name="value">The vector.</param>
    /// <param name="scale">The scale factor.</param>
    /// <returns>The scaled vector.</returns>
    public static Vector3 operator *(Vector3 value, float scale) =>
        new(value.X * scale, value.Y * scale, value.Z * scale);

    /// <summary>Scales a vector.</summary>
    /// <param name="scale">The scale factor.</param>
    /// <param name="value">The vector.</param>
    /// <returns>The scaled vector.</returns>
    public static Vector3 operator *(float scale, Vector3 value) => value * scale;

    /// <summary>Divides component by component.</summary>
    /// <param name="left">The dividend.</param>
    /// <param name="right">The divisor.</param>
    /// <returns>The component-wise quotient.</returns>
    public static Vector3 operator /(Vector3 left, Vector3 right) =>
        new(left.X / right.X, left.Y / right.Y, left.Z / right.Z);

    /// <summary>Divides a vector by a scalar.</summary>
    /// <param name="value">The dividend.</param>
    /// <param name="divisor">The divisor.</param>
    /// <returns>The quotient.</returns>
    public static Vector3 operator /(Vector3 value, float divisor) {
        var inverse = 1f / divisor;
        return new(value.X * inverse, value.Y * inverse, value.Z * inverse);
    }

    /// <summary>
    ///     Exact component-wise equality, with IEEE semantics: two vectors holding NaN are not
    ///     equal. Approximate comparison is <see cref="NearEqual" />, never this.
    /// </summary>
    /// <param name="left">The first vector.</param>
    /// <param name="right">The second vector.</param>
    /// <returns><see langword="true" /> if every component is equal.</returns>
    public static bool operator ==(Vector3 left, Vector3 right) =>
        left.X == right.X && left.Y == right.Y && left.Z == right.Z;

    /// <summary>The negation of <see cref="op_Equality" />.</summary>
    /// <param name="left">The first vector.</param>
    /// <param name="right">The second vector.</param>
    /// <returns><see langword="true" /> if any component differs.</returns>
    public static bool operator !=(Vector3 left, Vector3 right) => !(left == right);

    /// <summary>Converts to the BCL vector, which has the same layout.</summary>
    /// <param name="value">The vector to convert.</param>
    /// <returns>The equivalent <see cref="System.Numerics.Vector3" />.</returns>
    public static implicit operator System.Numerics.Vector3(Vector3 value) => new(value.X, value.Y, value.Z);

    /// <summary>Converts from the BCL vector, which has the same layout.</summary>
    /// <param name="value">The vector to convert.</param>
    /// <returns>The equivalent <see cref="Vector3" />.</returns>
    public static implicit operator Vector3(System.Numerics.Vector3 value) => new(value.X, value.Y, value.Z);

    /// <summary>Splits the vector into its components.</summary>
    /// <param name="x">The X component.</param>
    /// <param name="y">The Y component.</param>
    /// <param name="z">The Z component.</param>
    public void Deconstruct(out float x, out float y, out float z) {
        x = X;
        y = Y;
        z = Z;
    }

    /// <inheritdoc />
    public bool Equals(Vector3 other) => this == other;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Vector3 other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(X, Y, Z);

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
