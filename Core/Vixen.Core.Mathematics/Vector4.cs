// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Vixen.Core.Mathematics;

/// <summary>
///     A four-component vector: homogeneous positions, shader constants, tangents with a handedness
///     sign, and anything that has to match a GPU <c>float4</c> byte for byte.
/// </summary>
/// <remarks>
///     Sixteen bytes, sequentially laid out, so it reinterprets to and from
///     <see cref="Vector128{T}" /> with no work at all — which is what the matrix code relies on.
/// </remarks>
[DataContract]
[StructLayout(LayoutKind.Sequential)]
public readonly struct Vector4 : IEquatable<Vector4>, IFormattable, ISpanFormattable {
    /// <summary>Number of components.</summary>
    public const int ComponentCount = 4;

    /// <summary>The X component.</summary>
    public readonly float X;

    /// <summary>The Y component.</summary>
    public readonly float Y;

    /// <summary>The Z component.</summary>
    public readonly float Z;

    /// <summary>The W component.</summary>
    public readonly float W;

    /// <summary>All components zero.</summary>
    public static Vector4 Zero => default;

    /// <summary>All components one.</summary>
    public static Vector4 One => new(1f, 1f, 1f, 1f);

    /// <summary>The X axis.</summary>
    public static Vector4 UnitX => new(1f, 0f, 0f, 0f);

    /// <summary>The Y axis.</summary>
    public static Vector4 UnitY => new(0f, 1f, 0f, 0f);

    /// <summary>The Z axis.</summary>
    public static Vector4 UnitZ => new(0f, 0f, 1f, 0f);

    /// <summary>The W axis.</summary>
    public static Vector4 UnitW => new(0f, 0f, 0f, 1f);

    /// <summary>Builds a vector from its components.</summary>
    /// <param name="x">The X component.</param>
    /// <param name="y">The Y component.</param>
    /// <param name="z">The Z component.</param>
    /// <param name="w">The W component.</param>
    public Vector4(float x, float y, float z, float w) {
        X = x;
        Y = y;
        Z = z;
        W = w;
    }

    /// <summary>Builds a vector with every component set to <paramref name="value" />.</summary>
    /// <param name="value">The value for every component.</param>
    public Vector4(float value) {
        X = value;
        Y = value;
        Z = value;
        W = value;
    }

    /// <summary>Extends a <see cref="Vector3" /> with a W component.</summary>
    /// <param name="xyz">The X, Y and Z components.</param>
    /// <param name="w">The W component. Use 1 for a position and 0 for a direction.</param>
    public Vector4(Vector3 xyz, float w) {
        X = xyz.X;
        Y = xyz.Y;
        Z = xyz.Z;
        W = w;
    }

    /// <summary>The X, Y and Z components.</summary>
    public Vector3 Xyz => new(X, Y, Z);

    /// <summary>The X and Y components.</summary>
    public Vector2 Xy => new(X, Y);

    /// <summary>The component at <paramref name="index" />.</summary>
    /// <param name="index">0 for X, through 3 for W.</param>
    /// <returns>The component.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index" /> is not 0 to 3.</exception>
    public float this[int index] =>
        index switch {
            0 => X,
            1 => Y,
            2 => Z,
            3 => W,
            _ => throw new ArgumentOutOfRangeException(nameof(index), index, "A Vector4 has four components.")
        };

    /// <summary>The components as a span. Valid as long as the vector it came from.</summary>
    /// <returns>A span of <see cref="ComponentCount" /> floats.</returns>
    [UnscopedRef]
    public ReadOnlySpan<float> AsSpan() => MemoryMarshal.CreateReadOnlySpan(in X, ComponentCount);

    /// <summary>Copies the components into a caller-owned span.</summary>
    /// <param name="destination">A span of at least <see cref="ComponentCount" /> floats.</param>
    public void CopyTo(Span<float> destination) {
        destination[3] = W;
        destination[2] = Z;
        destination[1] = Y;
        destination[0] = X;
    }

    /// <summary>Reinterprets the vector as a SIMD register. Free — the layouts are identical.</summary>
    /// <returns>The same sixteen bytes as a <see cref="Vector128{T}" />.</returns>
    public Vector128<float> AsVector128() => Unsafe.BitCast<Vector4, Vector128<float>>(this);

    /// <summary>Reinterprets a SIMD register as a vector. Free — the layouts are identical.</summary>
    /// <param name="value">The register.</param>
    /// <returns>The same sixteen bytes as a <see cref="Vector4" />.</returns>
    public static Vector4 FromVector128(Vector128<float> value) => Unsafe.BitCast<Vector128<float>, Vector4>(value);

    /// <summary>The length of the vector.</summary>
    /// <returns>The length.</returns>
    public float Length() => MathF.Sqrt(LengthSquared());

    /// <summary>The squared length, which avoids a square root where only comparison matters.</summary>
    /// <returns>The squared length.</returns>
    public float LengthSquared() => (X * X) + (Y * Y) + (Z * Z) + (W * W);

    /// <summary>Whether every component is zero.</summary>
    public bool IsZero => X == 0f && Y == 0f && Z == 0f && W == 0f;

    /// <summary>Whether any component is NaN.</summary>
    public bool IsNaN => float.IsNaN(X) || float.IsNaN(Y) || float.IsNaN(Z) || float.IsNaN(W);

    /// <summary>The vector scaled to unit length; <see cref="Zero" /> if it is degenerate.</summary>
    /// <param name="value">The vector to normalise.</param>
    /// <returns>The unit-length vector.</returns>
    public static Vector4 Normalize(Vector4 value) {
        var lengthSquared = value.LengthSquared();
        return lengthSquared < MathUtil.ZeroTolerance * MathUtil.ZeroTolerance
            ? Zero
            : value * (1f / MathF.Sqrt(lengthSquared));
    }

    /// <summary>The dot product.</summary>
    /// <param name="left">The first vector.</param>
    /// <param name="right">The second vector.</param>
    /// <returns>The scalar product.</returns>
    public static float Dot(Vector4 left, Vector4 right) =>
        (left.X * right.X) + (left.Y * right.Y) + (left.Z * right.Z) + (left.W * right.W);

    /// <summary>The distance between two points.</summary>
    /// <param name="left">The first point.</param>
    /// <param name="right">The second point.</param>
    /// <returns>The distance.</returns>
    public static float Distance(Vector4 left, Vector4 right) => (left - right).Length();

    /// <summary>Linearly interpolates between two vectors.</summary>
    /// <param name="from">The vector at <paramref name="amount" /> = 0.</param>
    /// <param name="to">The vector at <paramref name="amount" /> = 1.</param>
    /// <param name="amount">The interpolant. Not clamped.</param>
    /// <returns>The interpolated vector.</returns>
    public static Vector4 Lerp(Vector4 from, Vector4 to, float amount) => from + ((to - from) * amount);

    /// <summary>The component-wise minimum.</summary>
    /// <param name="left">The first vector.</param>
    /// <param name="right">The second vector.</param>
    /// <returns>The component-wise minimum.</returns>
    public static Vector4 Min(Vector4 left, Vector4 right) =>
        FromVector128(Vector128.Min(left.AsVector128(), right.AsVector128()));

    /// <summary>The component-wise maximum.</summary>
    /// <param name="left">The first vector.</param>
    /// <param name="right">The second vector.</param>
    /// <returns>The component-wise maximum.</returns>
    public static Vector4 Max(Vector4 left, Vector4 right) =>
        FromVector128(Vector128.Max(left.AsVector128(), right.AsVector128()));

    /// <summary>Constrains each component to the matching interval.</summary>
    /// <param name="value">The vector to constrain.</param>
    /// <param name="min">The lower bounds.</param>
    /// <param name="max">The upper bounds.</param>
    /// <returns>The constrained vector.</returns>
    public static Vector4 Clamp(Vector4 value, Vector4 min, Vector4 max) => Min(Max(value, min), max);

    /// <summary>The component-wise absolute value.</summary>
    /// <param name="value">The vector.</param>
    /// <returns>The component-wise absolute value.</returns>
    public static Vector4 Abs(Vector4 value) => FromVector128(Vector128.Abs(value.AsVector128()));

    /// <summary>Whether two vectors agree to within a tolerance, component by component.</summary>
    /// <param name="left">The first vector.</param>
    /// <param name="right">The second vector.</param>
    /// <param name="tolerance">The relative tolerance.</param>
    /// <returns><see langword="true" /> if every component is within tolerance.</returns>
    public static bool NearEqual(Vector4 left, Vector4 right, float tolerance = MathUtil.ZeroTolerance) =>
        MathUtil.NearEqual(left.X, right.X, tolerance)
        && MathUtil.NearEqual(left.Y, right.Y, tolerance)
        && MathUtil.NearEqual(left.Z, right.Z, tolerance)
        && MathUtil.NearEqual(left.W, right.W, tolerance);

    /// <summary>Adds two vectors.</summary>
    /// <param name="left">The first vector.</param>
    /// <param name="right">The second vector.</param>
    /// <returns>The sum.</returns>
    public static Vector4 operator +(Vector4 left, Vector4 right) =>
        FromVector128(left.AsVector128() + right.AsVector128());

    /// <summary>Subtracts one vector from another.</summary>
    /// <param name="left">The vector to subtract from.</param>
    /// <param name="right">The vector to subtract.</param>
    /// <returns>The difference.</returns>
    public static Vector4 operator -(Vector4 left, Vector4 right) =>
        FromVector128(left.AsVector128() - right.AsVector128());

    /// <summary>Negates a vector.</summary>
    /// <param name="value">The vector.</param>
    /// <returns>The negated vector.</returns>
    public static Vector4 operator -(Vector4 value) => FromVector128(-value.AsVector128());

    /// <summary>Multiplies component by component.</summary>
    /// <param name="left">The first vector.</param>
    /// <param name="right">The second vector.</param>
    /// <returns>The component-wise product.</returns>
    public static Vector4 operator *(Vector4 left, Vector4 right) =>
        FromVector128(left.AsVector128() * right.AsVector128());

    /// <summary>Scales a vector.</summary>
    /// <param name="value">The vector.</param>
    /// <param name="scale">The scale factor.</param>
    /// <returns>The scaled vector.</returns>
    public static Vector4 operator *(Vector4 value, float scale) => FromVector128(value.AsVector128() * scale);

    /// <summary>Scales a vector.</summary>
    /// <param name="scale">The scale factor.</param>
    /// <param name="value">The vector.</param>
    /// <returns>The scaled vector.</returns>
    public static Vector4 operator *(float scale, Vector4 value) => value * scale;

    /// <summary>Divides component by component.</summary>
    /// <param name="left">The dividend.</param>
    /// <param name="right">The divisor.</param>
    /// <returns>The component-wise quotient.</returns>
    public static Vector4 operator /(Vector4 left, Vector4 right) =>
        FromVector128(left.AsVector128() / right.AsVector128());

    /// <summary>Divides a vector by a scalar.</summary>
    /// <param name="value">The dividend.</param>
    /// <param name="divisor">The divisor.</param>
    /// <returns>The quotient.</returns>
    public static Vector4 operator /(Vector4 value, float divisor) => FromVector128(value.AsVector128() / divisor);

    /// <summary>Exact component-wise equality, IEEE semantics. See <see cref="NearEqual" />.</summary>
    /// <param name="left">The first vector.</param>
    /// <param name="right">The second vector.</param>
    /// <returns><see langword="true" /> if every component is equal.</returns>
    public static bool operator ==(Vector4 left, Vector4 right) =>
        left.X == right.X && left.Y == right.Y && left.Z == right.Z && left.W == right.W;

    /// <summary>The negation of <see cref="op_Equality" />.</summary>
    /// <param name="left">The first vector.</param>
    /// <param name="right">The second vector.</param>
    /// <returns><see langword="true" /> if any component differs.</returns>
    public static bool operator !=(Vector4 left, Vector4 right) => !(left == right);

    /// <summary>Converts to the BCL vector, which has the same layout.</summary>
    /// <param name="value">The vector to convert.</param>
    /// <returns>The equivalent <see cref="System.Numerics.Vector4" />.</returns>
    public static implicit operator System.Numerics.Vector4(Vector4 value) =>
        new(value.X, value.Y, value.Z, value.W);

    /// <summary>Converts from the BCL vector, which has the same layout.</summary>
    /// <param name="value">The vector to convert.</param>
    /// <returns>The equivalent <see cref="Vector4" />.</returns>
    public static implicit operator Vector4(System.Numerics.Vector4 value) =>
        new(value.X, value.Y, value.Z, value.W);

    /// <summary>Splits the vector into its components.</summary>
    /// <param name="x">The X component.</param>
    /// <param name="y">The Y component.</param>
    /// <param name="z">The Z component.</param>
    /// <param name="w">The W component.</param>
    public void Deconstruct(out float x, out float y, out float z, out float w) {
        x = X;
        y = Y;
        z = Z;
        w = W;
    }

    /// <inheritdoc />
    public bool Equals(Vector4 other) => this == other;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Vector4 other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(X, Y, Z, W);

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
