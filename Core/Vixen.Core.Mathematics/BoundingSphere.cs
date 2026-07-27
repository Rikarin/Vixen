// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;

namespace Vixen.Core.Mathematics;

/// <summary>
///     A bounding sphere. Four floats against a box's six, rotation-invariant, and a single
///     subtraction to test — which is why it is the first cull and the box the second.
/// </summary>
[DataContract]
[StructLayout(LayoutKind.Sequential)]
public readonly struct BoundingSphere : IEquatable<BoundingSphere>, IFormattable {
    /// <summary>The centre.</summary>
    public readonly Vector3 Center;

    /// <summary>The radius.</summary>
    public readonly float Radius;

    /// <summary>The sphere that contains nothing.</summary>
    public static BoundingSphere Empty => new(Vector3.Zero, -1f);

    /// <summary>Whether the sphere contains nothing.</summary>
    public bool IsEmpty => Radius < 0f;

    /// <summary>Builds a sphere.</summary>
    /// <param name="center">The centre.</param>
    /// <param name="radius">The radius.</param>
    public BoundingSphere(Vector3 center, float radius) {
        Center = center;
        Radius = radius;
    }

    /// <summary>
    ///     A sphere containing every point, centred on the midpoint of their bounding box.
    /// </summary>
    /// <param name="points">The points.</param>
    /// <returns>The bounding sphere.</returns>
    /// <remarks>
    ///     Not the *minimal* sphere — that is Welzl's algorithm, which is randomised, recursive, and
    ///     worth having only where the tighter bound pays for itself. This is the bound every engine
    ///     computes at import time, and it is typically within a few percent.
    /// </remarks>
    public static BoundingSphere FromPoints(ReadOnlySpan<Vector3> points) {
        if (points.IsEmpty) {
            return Empty;
        }

        var center = BoundingBox.FromPoints(points).Center;
        var radiusSquared = 0f;

        foreach (var point in points) {
            radiusSquared = MathF.Max(radiusSquared, Vector3.DistanceSquared(center, point));
        }

        // One ulp of headroom. `Sqrt` rounds to nearest, so squaring the result can land a hair
        // below the value it came from, and the outermost point — the one that set the radius —
        // then falls outside the sphere built to contain it. A cull would drop it.
        return new(center, MathF.BitIncrement(MathF.Sqrt(radiusSquared)));
    }

    /// <summary>The sphere circumscribing a box.</summary>
    /// <param name="box">The box.</param>
    /// <returns>The bounding sphere.</returns>
    public static BoundingSphere FromBox(BoundingBox box) =>
        box.IsEmpty ? Empty : new(box.Center, box.Extent.Length());

    /// <summary>The smallest sphere containing both.</summary>
    /// <param name="left">The first sphere.</param>
    /// <param name="right">The second sphere.</param>
    /// <returns>The union bound.</returns>
    public static BoundingSphere Merge(BoundingSphere left, BoundingSphere right) {
        if (left.IsEmpty) {
            return right;
        }

        if (right.IsEmpty) {
            return left;
        }

        var offset = right.Center - left.Center;
        var distance = offset.Length();

        // One already swallows the other, so the answer is the larger one rather than something
        // needlessly bigger than both.
        if (distance + right.Radius <= left.Radius) {
            return left;
        }

        if (distance + left.Radius <= right.Radius) {
            return right;
        }

        var radius = (distance + left.Radius + right.Radius) * 0.5f;
        var direction = distance < MathUtil.ZeroTolerance ? Vector3.Zero : offset / distance;
        return new(left.Center + (direction * (radius - left.Radius)), radius);
    }

    /// <summary>Whether a point is inside or on the surface.</summary>
    /// <param name="point">The point.</param>
    /// <returns><see langword="true" /> if the point is within the sphere.</returns>
    public bool Contains(Vector3 point) => Vector3.DistanceSquared(Center, point) <= Radius * Radius;

    /// <summary>How another sphere sits relative to this one.</summary>
    /// <param name="other">The sphere to test.</param>
    /// <returns>The containment relationship.</returns>
    public ContainmentType Contains(BoundingSphere other) {
        var distance = Vector3.Distance(Center, other.Center);

        return distance > Radius + other.Radius
            ? ContainmentType.Disjoint
            : distance + other.Radius <= Radius
                ? ContainmentType.Contains
                : ContainmentType.Intersects;
    }

    /// <summary>How a box sits relative to this sphere.</summary>
    /// <param name="box">The box to test.</param>
    /// <returns>The containment relationship.</returns>
    public ContainmentType Contains(BoundingBox box) {
        if (!Intersects(box)) {
            return ContainmentType.Disjoint;
        }

        Span<Vector3> corners = stackalloc Vector3[BoundingBox.CornerCount];
        box.GetCorners(corners);

        foreach (var corner in corners) {
            if (!Contains(corner)) {
                return ContainmentType.Intersects;
            }
        }

        return ContainmentType.Contains;
    }

    /// <summary>Whether two spheres overlap.</summary>
    /// <param name="other">The sphere to test.</param>
    /// <returns><see langword="true" /> unless they are disjoint.</returns>
    public bool Intersects(BoundingSphere other) {
        var reach = Radius + other.Radius;
        return Vector3.DistanceSquared(Center, other.Center) <= reach * reach;
    }

    /// <summary>Whether a box overlaps this sphere.</summary>
    /// <param name="box">The box to test.</param>
    /// <returns><see langword="true" /> unless they are disjoint.</returns>
    public bool Intersects(BoundingBox box) => box.Intersects(this);

    /// <summary>Which side of a plane the sphere is on.</summary>
    /// <param name="plane">The plane.</param>
    /// <returns>The side, or <see cref="PlaneIntersectionType.Intersecting" /> if it straddles.</returns>
    /// <remarks>
    ///     Conservative at the boundary in the same way, and for the same reason, as
    ///     <see cref="BoundingBox.Intersects(Plane)" /> — which is where the margin is derived.
    /// </remarks>
    public PlaneIntersectionType Intersects(Plane plane) {
        var distance = plane.DotCoordinate(Center);

        // Same shape as the box test, one term shorter: the radius is stored rather than computed
        // from two corners, so only the cancelling dot product contributes an error the result
        // cannot absorb. Sharing the scale keeps a sphere and its bounding box from disagreeing
        // about which of them is tangent.
        var margin = MathUtil.RoundingSlack
            * (Radius + Vector3.Dot(Vector3.Abs(Center), Vector3.Abs(plane.Normal)) + MathF.Abs(plane.D));

        return distance > Radius + margin
            ? PlaneIntersectionType.Front
            : distance < -Radius - margin
                ? PlaneIntersectionType.Back
                : PlaneIntersectionType.Intersecting;
    }

    /// <summary>
    ///     The sphere after a transform. The radius grows by the largest of the three axis scales,
    ///     because a sphere under a non-uniform scale is an ellipsoid and this is its bound.
    /// </summary>
    /// <param name="sphere">The sphere.</param>
    /// <param name="matrix">The transform.</param>
    /// <returns>The transformed bound.</returns>
    public static BoundingSphere Transform(BoundingSphere sphere, in Matrix4x4 matrix) {
        if (sphere.IsEmpty) {
            return sphere;
        }

        var center = Matrix4x4.TransformPosition(sphere.Center, matrix);
        var scale = MathF.Sqrt(
            MathF.Max(
                new Vector3(matrix.M11, matrix.M12, matrix.M13).LengthSquared(),
                MathF.Max(
                    new Vector3(matrix.M21, matrix.M22, matrix.M23).LengthSquared(),
                    new Vector3(matrix.M31, matrix.M32, matrix.M33).LengthSquared()
                )
            )
        );

        return new(center, sphere.Radius * scale);
    }

    /// <summary>Whether two spheres agree to within a tolerance.</summary>
    /// <param name="left">The first sphere.</param>
    /// <param name="right">The second sphere.</param>
    /// <param name="tolerance">The relative tolerance.</param>
    /// <returns><see langword="true" /> if centre and radius are within tolerance.</returns>
    public static bool NearEqual(
        BoundingSphere left,
        BoundingSphere right,
        float tolerance = MathUtil.ZeroTolerance
    ) =>
        Vector3.NearEqual(left.Center, right.Center, tolerance)
        && MathUtil.NearEqual(left.Radius, right.Radius, tolerance);

    /// <summary>Exact equality, IEEE semantics. See <see cref="NearEqual" />.</summary>
    /// <param name="left">The first sphere.</param>
    /// <param name="right">The second sphere.</param>
    /// <returns><see langword="true" /> if centre and radius are equal.</returns>
    public static bool operator ==(BoundingSphere left, BoundingSphere right) =>
        left.Center == right.Center && left.Radius == right.Radius;

    /// <summary>The negation of <see cref="op_Equality" />.</summary>
    /// <param name="left">The first sphere.</param>
    /// <param name="right">The second sphere.</param>
    /// <returns><see langword="true" /> if either differs.</returns>
    public static bool operator !=(BoundingSphere left, BoundingSphere right) => !(left == right);

    /// <inheritdoc />
    public bool Equals(BoundingSphere other) => this == other;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is BoundingSphere other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Center, Radius);

    /// <inheritdoc />
    public override string ToString() => ToString(null, null);

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) =>
        $"{{Center:{Center.ToString(format, formatProvider)} Radius:{Radius.ToString(format, formatProvider ?? VectorFormat.DefaultProvider)}}}";
}
