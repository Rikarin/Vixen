// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;

namespace Vixen.Core.Mathematics;

/// <summary>
///     An infinite plane, stored as the coefficients of <c>dot(Normal, p) + D = 0</c>. The side the
///     normal points to is the front.
/// </summary>
/// <remarks>
///     <see cref="D" /> is the *negated* distance from the origin along the normal, which is the
///     form that makes <see cref="DotCoordinate" /> a single dot product and is why every clipping
///     and culling routine stores it this way.
/// </remarks>
[DataContract]
[StructLayout(LayoutKind.Sequential)]
public readonly struct Plane : IEquatable<Plane>, IFormattable {
    /// <summary>The plane's normal. Unit length for the distance methods to mean anything.</summary>
    public readonly Vector3 Normal;

    /// <summary>The negated distance from the origin along <see cref="Normal" />.</summary>
    public readonly float D;

    /// <summary>Builds a plane from its four coefficients.</summary>
    /// <param name="a">The normal's X component.</param>
    /// <param name="b">The normal's Y component.</param>
    /// <param name="c">The normal's Z component.</param>
    /// <param name="d">The negated distance from the origin.</param>
    public Plane(float a, float b, float c, float d) {
        Normal = new(a, b, c);
        D = d;
    }

    /// <summary>Builds a plane from a normal and a distance.</summary>
    /// <param name="normal">The normal.</param>
    /// <param name="d">The negated distance from the origin along the normal.</param>
    public Plane(Vector3 normal, float d) {
        Normal = normal;
        D = d;
    }

    /// <summary>The plane through <paramref name="point" /> facing <paramref name="normal" />.</summary>
    /// <param name="point">A point on the plane.</param>
    /// <param name="normal">The normal. Normalised internally.</param>
    /// <returns>The plane.</returns>
    public static Plane FromPointNormal(Vector3 point, Vector3 normal) {
        var unit = Vector3.Normalize(normal);
        return new(unit, -Vector3.Dot(unit, point));
    }

    /// <summary>
    ///     The plane through three points, wound counter-clockwise when seen from the front.
    /// </summary>
    /// <param name="a">The first point.</param>
    /// <param name="b">The second point.</param>
    /// <param name="c">The third point.</param>
    /// <returns>The plane, or a degenerate one if the points are collinear.</returns>
    public static Plane FromPoints(Vector3 a, Vector3 b, Vector3 c) =>
        FromPointNormal(a, Vector3.Cross(b - a, c - a));

    /// <summary>Rescales the plane so its normal is unit length.</summary>
    /// <param name="plane">The plane.</param>
    /// <returns>The normalised plane, describing the same set of points.</returns>
    public static Plane Normalize(Plane plane) {
        var length = plane.Normal.Length();
        if (length < MathUtil.ZeroTolerance) {
            return plane;
        }

        var inverse = 1f / length;
        return new(plane.Normal * inverse, plane.D * inverse);
    }

    /// <summary>
    ///     The signed distance from <paramref name="point" /> to the plane: positive in front,
    ///     negative behind, zero on it. Only a distance if the normal is unit length.
    /// </summary>
    /// <param name="point">The point.</param>
    /// <returns>The signed distance.</returns>
    public float DotCoordinate(Vector3 point) => Vector3.Dot(Normal, point) + D;

    /// <summary>The dot product of the plane's normal with a direction, ignoring <see cref="D" />.</summary>
    /// <param name="direction">The direction.</param>
    /// <returns>The scalar product.</returns>
    public float DotNormal(Vector3 direction) => Vector3.Dot(Normal, direction);

    /// <summary>Which side of the plane a point is on.</summary>
    /// <param name="point">The point.</param>
    /// <returns>The side, or <see cref="PlaneIntersectionType.Intersecting" /> if it is on the plane.</returns>
    public PlaneIntersectionType Classify(Vector3 point) {
        var distance = DotCoordinate(point);
        return distance > MathUtil.ZeroTolerance
            ? PlaneIntersectionType.Front
            : distance < -MathUtil.ZeroTolerance
                ? PlaneIntersectionType.Back
                : PlaneIntersectionType.Intersecting;
    }

    /// <summary>Moves a plane by a transform.</summary>
    /// <param name="plane">The plane.</param>
    /// <param name="matrix">The transform.</param>
    /// <returns>The transformed plane, renormalised.</returns>
    /// <remarks>
    ///     A plane is transformed by the inverse transpose, exactly as a normal is, and for the same
    ///     reason: it is defined by a direction perpendicular to a surface. Transforming the
    ///     coefficients by the matrix itself is wrong under any non-uniform scale.
    /// </remarks>
    public static Plane Transform(Plane plane, in Matrix4x4 matrix) {
        if (!Matrix4x4.Invert(matrix, out var inverse)) {
            return plane;
        }

        var transposed = Matrix4x4.Transpose(inverse);
        var coefficients = new Vector4(plane.Normal, plane.D) * transposed;
        return Normalize(new(coefficients.Xyz, coefficients.W));
    }

    /// <summary>Whether two planes agree to within a tolerance.</summary>
    /// <param name="left">The first plane.</param>
    /// <param name="right">The second plane.</param>
    /// <param name="tolerance">The relative tolerance.</param>
    /// <returns><see langword="true" /> if the coefficients are within tolerance.</returns>
    public static bool NearEqual(Plane left, Plane right, float tolerance = MathUtil.ZeroTolerance) =>
        Vector3.NearEqual(left.Normal, right.Normal, tolerance) && MathUtil.NearEqual(left.D, right.D, tolerance);

    /// <summary>Exact equality, IEEE semantics. See <see cref="NearEqual" />.</summary>
    /// <param name="left">The first plane.</param>
    /// <param name="right">The second plane.</param>
    /// <returns><see langword="true" /> if the coefficients are equal.</returns>
    public static bool operator ==(Plane left, Plane right) => left.Normal == right.Normal && left.D == right.D;

    /// <summary>The negation of <see cref="op_Equality" />.</summary>
    /// <param name="left">The first plane.</param>
    /// <param name="right">The second plane.</param>
    /// <returns><see langword="true" /> if any coefficient differs.</returns>
    public static bool operator !=(Plane left, Plane right) => !(left == right);

    /// <inheritdoc />
    public bool Equals(Plane other) => this == other;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Plane other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Normal, D);

    /// <inheritdoc />
    public override string ToString() => ToString(null, null);

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) =>
        $"{{Normal:{Normal.ToString(format, formatProvider)} D:{D.ToString(format, formatProvider ?? VectorFormat.DefaultProvider)}}}";
}
