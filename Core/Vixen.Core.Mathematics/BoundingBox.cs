// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;

namespace Vixen.Core.Mathematics;

/// <summary>
///     An axis-aligned bounding box, stored as its two extreme corners. The workhorse of culling,
///     spatial queries and broad-phase collision.
/// </summary>
/// <remarks>
///     An <b>empty</b> box has <see cref="Minimum" /> above <see cref="Maximum" /> — see
///     <see cref="Empty" />. That is not a degenerate case to guard against, it is the identity for
///     <see cref="Merge(BoundingBox,BoundingBox)" />, which is what makes accumulating a bound over
///     a loop work without a special case for the first item.
/// </remarks>
[DataContract]
[StructLayout(LayoutKind.Sequential)]
public readonly struct BoundingBox : IEquatable<BoundingBox>, IFormattable {
    /// <summary>How many corners a box has.</summary>
    public const int CornerCount = 8;

    /// <summary>The corner with the smallest coordinates.</summary>
    public readonly Vector3 Minimum;

    /// <summary>The corner with the largest coordinates.</summary>
    public readonly Vector3 Maximum;

    /// <summary>
    ///     The box that contains nothing: inverted bounds, so merging anything into it yields
    ///     exactly that thing. Start every accumulation here.
    /// </summary>
    public static BoundingBox Empty =>
        new(new(float.PositiveInfinity), new(float.NegativeInfinity));

    /// <summary>Builds a box from its extreme corners.</summary>
    /// <param name="minimum">The corner with the smallest coordinates.</param>
    /// <param name="maximum">The corner with the largest coordinates.</param>
    public BoundingBox(Vector3 minimum, Vector3 maximum) {
        Minimum = minimum;
        Maximum = maximum;
    }

    /// <summary>The midpoint.</summary>
    public Vector3 Center => (Minimum + Maximum) * 0.5f;

    /// <summary>The full width, height and depth.</summary>
    public Vector3 Size => Maximum - Minimum;

    /// <summary>Half the size — the distance from the centre to a face along each axis.</summary>
    public Vector3 Extent => Size * 0.5f;

    /// <summary>Whether the box contains nothing.</summary>
    public bool IsEmpty => Minimum.X > Maximum.X || Minimum.Y > Maximum.Y || Minimum.Z > Maximum.Z;

    /// <summary>The tightest box containing every point.</summary>
    /// <param name="points">The points. May be empty, giving <see cref="Empty" />.</param>
    /// <returns>The bounding box.</returns>
    public static BoundingBox FromPoints(ReadOnlySpan<Vector3> points) {
        var minimum = new Vector3(float.PositiveInfinity);
        var maximum = new Vector3(float.NegativeInfinity);

        foreach (var point in points) {
            minimum = Vector3.Min(minimum, point);
            maximum = Vector3.Max(maximum, point);
        }

        return new(minimum, maximum);
    }

    /// <summary>The tightest box containing a sphere.</summary>
    /// <param name="sphere">The sphere.</param>
    /// <returns>The bounding box.</returns>
    public static BoundingBox FromSphere(BoundingSphere sphere) =>
        new(sphere.Center - new Vector3(sphere.Radius), sphere.Center + new Vector3(sphere.Radius));

    /// <summary>The tightest box containing both.</summary>
    /// <param name="left">The first box.</param>
    /// <param name="right">The second box.</param>
    /// <returns>The union bound.</returns>
    public static BoundingBox Merge(BoundingBox left, BoundingBox right) =>
        new(Vector3.Min(left.Minimum, right.Minimum), Vector3.Max(left.Maximum, right.Maximum));

    /// <summary>The tightest box containing this one and a point.</summary>
    /// <param name="box">The box.</param>
    /// <param name="point">The point.</param>
    /// <returns>The grown box.</returns>
    public static BoundingBox Merge(BoundingBox box, Vector3 point) =>
        new(Vector3.Min(box.Minimum, point), Vector3.Max(box.Maximum, point));

    /// <summary>The eight corners, in a fixed order: −X first, then −Y, then −Z varying fastest.</summary>
    /// <param name="destination">A span of at least <see cref="CornerCount" /> vectors.</param>
    /// <exception cref="ArgumentException"><paramref name="destination" /> is too short.</exception>
    public void GetCorners(Span<Vector3> destination) {
        if (destination.Length < CornerCount) {
            throw new ArgumentException($"A box has {CornerCount} corners.", nameof(destination));
        }

        destination[0] = new(Minimum.X, Minimum.Y, Minimum.Z);
        destination[1] = new(Minimum.X, Minimum.Y, Maximum.Z);
        destination[2] = new(Minimum.X, Maximum.Y, Minimum.Z);
        destination[3] = new(Minimum.X, Maximum.Y, Maximum.Z);
        destination[4] = new(Maximum.X, Minimum.Y, Minimum.Z);
        destination[5] = new(Maximum.X, Minimum.Y, Maximum.Z);
        destination[6] = new(Maximum.X, Maximum.Y, Minimum.Z);
        destination[7] = new(Maximum.X, Maximum.Y, Maximum.Z);
    }

    /// <summary>Whether a point is inside or on the boundary.</summary>
    /// <param name="point">The point.</param>
    /// <returns><see langword="true" /> if the point is within the box.</returns>
    public bool Contains(Vector3 point) =>
        point.X >= Minimum.X && point.X <= Maximum.X
        && point.Y >= Minimum.Y && point.Y <= Maximum.Y
        && point.Z >= Minimum.Z && point.Z <= Maximum.Z;

    /// <summary>How another box sits relative to this one.</summary>
    /// <param name="other">The box to test.</param>
    /// <returns>The containment relationship.</returns>
    public ContainmentType Contains(BoundingBox other) {
        if (Maximum.X < other.Minimum.X || Minimum.X > other.Maximum.X
            || Maximum.Y < other.Minimum.Y || Minimum.Y > other.Maximum.Y
            || Maximum.Z < other.Minimum.Z || Minimum.Z > other.Maximum.Z) {
            return ContainmentType.Disjoint;
        }

        return Minimum.X <= other.Minimum.X && other.Maximum.X <= Maximum.X
            && Minimum.Y <= other.Minimum.Y && other.Maximum.Y <= Maximum.Y
            && Minimum.Z <= other.Minimum.Z && other.Maximum.Z <= Maximum.Z
                ? ContainmentType.Contains
                : ContainmentType.Intersects;
    }

    /// <summary>How a sphere sits relative to this box.</summary>
    /// <param name="sphere">The sphere to test.</param>
    /// <returns>The containment relationship.</returns>
    public ContainmentType Contains(BoundingSphere sphere) {
        var closest = Vector3.Clamp(sphere.Center, Minimum, Maximum);
        if (Vector3.DistanceSquared(sphere.Center, closest) > sphere.Radius * sphere.Radius) {
            return ContainmentType.Disjoint;
        }

        var lower = sphere.Center - new Vector3(sphere.Radius);
        var upper = sphere.Center + new Vector3(sphere.Radius);

        return Minimum.X <= lower.X && Minimum.Y <= lower.Y && Minimum.Z <= lower.Z
            && upper.X <= Maximum.X && upper.Y <= Maximum.Y && upper.Z <= Maximum.Z
                ? ContainmentType.Contains
                : ContainmentType.Intersects;
    }

    /// <summary>Whether two boxes overlap at all.</summary>
    /// <param name="other">The box to test.</param>
    /// <returns><see langword="true" /> unless they are disjoint.</returns>
    public bool Intersects(BoundingBox other) => Contains(other) != ContainmentType.Disjoint;

    /// <summary>Whether a sphere overlaps this box.</summary>
    /// <param name="sphere">The sphere to test.</param>
    /// <returns><see langword="true" /> unless they are disjoint.</returns>
    public bool Intersects(BoundingSphere sphere) =>
        Vector3.DistanceSquared(sphere.Center, Vector3.Clamp(sphere.Center, Minimum, Maximum))
        <= sphere.Radius * sphere.Radius;

    /// <summary>Which side of a plane the box is on.</summary>
    /// <param name="plane">The plane.</param>
    /// <returns>The side, or <see cref="PlaneIntersectionType.Intersecting" /> if it straddles.</returns>
    public PlaneIntersectionType Intersects(Plane plane) {
        // Test only the two corners furthest along the normal in each direction; the other six
        // cannot change the answer, and finding them is three comparisons rather than eight dots.
        var center = Center;
        var extent = Extent;
        var reach = (extent.X * MathF.Abs(plane.Normal.X))
            + (extent.Y * MathF.Abs(plane.Normal.Y))
            + (extent.Z * MathF.Abs(plane.Normal.Z));

        var distance = plane.DotCoordinate(center);

        return distance > reach
            ? PlaneIntersectionType.Front
            : distance < -reach
                ? PlaneIntersectionType.Back
                : PlaneIntersectionType.Intersecting;
    }

    /// <summary>
    ///     The tightest axis-aligned box containing this one after a transform.
    /// </summary>
    /// <param name="box">The box.</param>
    /// <param name="matrix">The transform.</param>
    /// <returns>The transformed bound.</returns>
    /// <remarks>
    ///     Grows: a rotated box is no longer axis-aligned, so the result is the axis-aligned bound
    ///     of the rotated one. Re-fitting to the source geometry gives a tighter answer; this is the
    ///     cheap one, and it computes the new extent from the absolute values of the matrix rather
    ///     than transforming all eight corners.
    /// </remarks>
    public static BoundingBox Transform(BoundingBox box, in Matrix4x4 matrix) {
        if (box.IsEmpty) {
            return box;
        }

        var center = Matrix4x4.TransformPosition(box.Center, matrix);
        var extent = box.Extent;

        var transformed = new Vector3(
            (extent.X * MathF.Abs(matrix.M11)) + (extent.Y * MathF.Abs(matrix.M21))
            + (extent.Z * MathF.Abs(matrix.M31)),
            (extent.X * MathF.Abs(matrix.M12)) + (extent.Y * MathF.Abs(matrix.M22))
            + (extent.Z * MathF.Abs(matrix.M32)),
            (extent.X * MathF.Abs(matrix.M13)) + (extent.Y * MathF.Abs(matrix.M23))
            + (extent.Z * MathF.Abs(matrix.M33))
        );

        return new(center - transformed, center + transformed);
    }

    /// <summary>Whether two boxes agree to within a tolerance.</summary>
    /// <param name="left">The first box.</param>
    /// <param name="right">The second box.</param>
    /// <param name="tolerance">The relative tolerance.</param>
    /// <returns><see langword="true" /> if both corners are within tolerance.</returns>
    public static bool NearEqual(BoundingBox left, BoundingBox right, float tolerance = MathUtil.ZeroTolerance) =>
        Vector3.NearEqual(left.Minimum, right.Minimum, tolerance)
        && Vector3.NearEqual(left.Maximum, right.Maximum, tolerance);

    /// <summary>Exact equality, IEEE semantics. See <see cref="NearEqual" />.</summary>
    /// <param name="left">The first box.</param>
    /// <param name="right">The second box.</param>
    /// <returns><see langword="true" /> if both corners are equal.</returns>
    public static bool operator ==(BoundingBox left, BoundingBox right) =>
        left.Minimum == right.Minimum && left.Maximum == right.Maximum;

    /// <summary>The negation of <see cref="op_Equality" />.</summary>
    /// <param name="left">The first box.</param>
    /// <param name="right">The second box.</param>
    /// <returns><see langword="true" /> if either corner differs.</returns>
    public static bool operator !=(BoundingBox left, BoundingBox right) => !(left == right);

    /// <inheritdoc />
    public bool Equals(BoundingBox other) => this == other;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is BoundingBox other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Minimum, Maximum);

    /// <inheritdoc />
    public override string ToString() => ToString(null, null);

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) =>
        $"{{Min:{Minimum.ToString(format, formatProvider)} Max:{Maximum.ToString(format, formatProvider)}}}";
}
