// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;

namespace Vixen.Core.Mathematics;

/// <summary>
///     A half-line: an origin and a direction. Picking, line-of-sight queries, and every raycast the
///     physics and navigation layers make.
/// </summary>
/// <remarks>
///     The direction is expected to be unit length — every <c>distance</c> this type reports is
///     measured in units of it, so a direction of length 2 halves every answer.
/// </remarks>
[DataContract]
[StructLayout(LayoutKind.Sequential)]
public readonly struct Ray : IEquatable<Ray>, IFormattable {
    /// <summary>Where the ray starts.</summary>
    public readonly Vector3 Origin;

    /// <summary>Which way it points. Expected to be unit length.</summary>
    public readonly Vector3 Direction;

    /// <summary>Builds a ray.</summary>
    /// <param name="origin">Where the ray starts.</param>
    /// <param name="direction">Which way it points. Normalised internally.</param>
    public Ray(Vector3 origin, Vector3 direction) {
        Origin = origin;
        Direction = Vector3.Normalize(direction);
    }

    /// <summary>The point <paramref name="distance" /> along the ray.</summary>
    /// <param name="distance">How far along.</param>
    /// <returns>The point.</returns>
    public Vector3 GetPoint(float distance) => Origin + (Direction * distance);

    /// <summary>Where the ray meets a plane.</summary>
    /// <param name="plane">The plane.</param>
    /// <param name="distance">The distance to the hit, or 0.</param>
    /// <returns><see langword="false" /> if the ray is parallel to the plane or points away from it.</returns>
    public bool Intersects(Plane plane, out float distance) {
        var alignment = plane.DotNormal(Direction);
        if (MathF.Abs(alignment) < MathUtil.ZeroTolerance) {
            distance = 0f;
            return false;
        }

        var hit = -plane.DotCoordinate(Origin) / alignment;
        distance = MathF.Max(hit, 0f);
        return hit >= 0f;
    }

    /// <summary>Where the ray enters a sphere.</summary>
    /// <param name="sphere">The sphere.</param>
    /// <param name="distance">The distance to the near hit, or 0 if the origin is inside.</param>
    /// <returns><see langword="false" /> if the ray misses.</returns>
    public bool Intersects(BoundingSphere sphere, out float distance) {
        distance = 0f;

        var toCentre = sphere.Center - Origin;
        var distanceSquared = toCentre.LengthSquared();
        var radiusSquared = sphere.Radius * sphere.Radius;

        // Starting inside counts as a hit at zero, which is what a picking query wants.
        if (distanceSquared <= radiusSquared) {
            return true;
        }

        var projection = Vector3.Dot(toCentre, Direction);
        if (projection < 0f) {
            return false;
        }

        var perpendicularSquared = distanceSquared - (projection * projection);
        if (perpendicularSquared > radiusSquared) {
            return false;
        }

        distance = projection - MathF.Sqrt(radiusSquared - perpendicularSquared);
        return true;
    }

    /// <summary>Where the ray enters a box.</summary>
    /// <param name="box">The box.</param>
    /// <param name="distance">The distance to the near hit, or 0 if the origin is inside.</param>
    /// <returns><see langword="false" /> if the ray misses.</returns>
    /// <remarks>
    ///     The slab method, and deliberately without a guard against a zero direction component:
    ///     the division yields ±infinity, the comparisons that follow treat it correctly, and
    ///     branching to avoid it costs more than it saves. A component that is exactly zero *and* an
    ///     origin exactly on the slab gives NaN, which the final comparison rejects — the same
    ///     answer a branch would have produced.
    /// </remarks>
    public bool Intersects(BoundingBox box, out float distance) {
        var inverse = new Vector3(1f / Direction.X, 1f / Direction.Y, 1f / Direction.Z);

        var first = (box.Minimum - Origin) * inverse;
        var second = (box.Maximum - Origin) * inverse;

        var near = Vector3.Min(first, second);
        var far = Vector3.Max(first, second);

        var entry = MathF.Max(MathF.Max(near.X, near.Y), near.Z);
        var exit = MathF.Min(MathF.Min(far.X, far.Y), far.Z);

        if (exit < 0f || entry > exit) {
            distance = 0f;
            return false;
        }

        distance = MathF.Max(entry, 0f);
        return true;
    }

    /// <summary>Where the ray meets a triangle.</summary>
    /// <param name="a">The first vertex.</param>
    /// <param name="b">The second vertex.</param>
    /// <param name="c">The third vertex.</param>
    /// <param name="distance">The distance to the hit, or 0.</param>
    /// <returns><see langword="false" /> if the ray misses.</returns>
    /// <remarks>
    ///     Möller–Trumbore: no precomputed plane, so it costs nothing to store per triangle, which
    ///     is what matters when the caller is walking a mesh. Hits from either side.
    /// </remarks>
    public bool Intersects(Vector3 a, Vector3 b, Vector3 c, out float distance) {
        distance = 0f;

        var edge1 = b - a;
        var edge2 = c - a;
        var perpendicular = Vector3.Cross(Direction, edge2);
        var determinant = Vector3.Dot(edge1, perpendicular);

        // Parallel to the triangle's plane.
        if (MathF.Abs(determinant) < MathUtil.ZeroTolerance) {
            return false;
        }

        var inverse = 1f / determinant;
        var toVertex = Origin - a;

        var u = Vector3.Dot(toVertex, perpendicular) * inverse;
        if (u is < 0f or > 1f) {
            return false;
        }

        var across = Vector3.Cross(toVertex, edge1);
        var v = Vector3.Dot(Direction, across) * inverse;
        if (v < 0f || u + v > 1f) {
            return false;
        }

        var hit = Vector3.Dot(edge2, across) * inverse;
        if (hit < 0f) {
            return false;
        }

        distance = hit;
        return true;
    }

    /// <summary>The ray after a transform.</summary>
    /// <param name="ray">The ray.</param>
    /// <param name="matrix">The transform.</param>
    /// <returns>The transformed ray, its direction renormalised.</returns>
    public static Ray Transform(Ray ray, in Matrix4x4 matrix) =>
        new(
            Matrix4x4.TransformPosition(ray.Origin, matrix),
            Matrix4x4.TransformDirection(ray.Direction, matrix)
        );

    /// <summary>Exact equality, IEEE semantics.</summary>
    /// <param name="left">The first ray.</param>
    /// <param name="right">The second ray.</param>
    /// <returns><see langword="true" /> if origin and direction are equal.</returns>
    public static bool operator ==(Ray left, Ray right) =>
        left.Origin == right.Origin && left.Direction == right.Direction;

    /// <summary>The negation of <see cref="op_Equality" />.</summary>
    /// <param name="left">The first ray.</param>
    /// <param name="right">The second ray.</param>
    /// <returns><see langword="true" /> if either differs.</returns>
    public static bool operator !=(Ray left, Ray right) => !(left == right);

    /// <inheritdoc />
    public bool Equals(Ray other) => this == other;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Ray other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Origin, Direction);

    /// <inheritdoc />
    public override string ToString() => ToString(null, null);

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) =>
        $"{{Origin:{Origin.ToString(format, formatProvider)} Direction:{Direction.ToString(format, formatProvider)}}}";
}
