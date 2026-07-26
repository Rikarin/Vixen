// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Vixen.Core.Mathematics;

/// <summary>
///     The six planes of a view volume, all facing inward, extracted from a view-projection matrix.
///     The first thing every visibility query asks.
/// </summary>
/// <remarks>
///     <para>
///         Extraction is Gribb–Hartmann: each plane is a sum or difference of two *columns* of the
///         matrix, which for our row-vector convention is what a clip-space inequality turns into.
///         Because every plane faces inward, "inside" is <c>DotCoordinate(p) >= 0</c> for all six.
///     </para>
///     <para>
///         <b>Reverse-Z is not cosmetic here.</b> Depth runs near → 1 and far → 0, so the plane that
///         a forward-Z projection would call *near* is this one's <b>far</b> plane and vice versa.
///         Getting that backwards produces a frustum that culls everything close to the camera,
///         which looks like a bug in the renderer rather than in the maths.
///     </para>
/// </remarks>
[DataContract]
[StructLayout(LayoutKind.Sequential)]
public readonly struct BoundingFrustum : IEquatable<BoundingFrustum> {
    /// <summary>How many planes bound a frustum.</summary>
    public const int PlaneCount = 6;

    /// <summary>How many corners a frustum has.</summary>
    public const int CornerCount = 8;

    /// <summary>The near plane, facing away from the camera.</summary>
    public readonly Plane Near;

    /// <summary>The far plane, facing back toward the camera.</summary>
    public readonly Plane Far;

    /// <summary>The left plane, facing right.</summary>
    public readonly Plane Left;

    /// <summary>The right plane, facing left.</summary>
    public readonly Plane Right;

    /// <summary>The top plane, facing down.</summary>
    public readonly Plane Top;

    /// <summary>The bottom plane, facing up.</summary>
    public readonly Plane Bottom;

    /// <summary>Extracts the six planes from a combined view-projection transform.</summary>
    /// <param name="viewProjection">The view matrix multiplied by the projection matrix.</param>
    public BoundingFrustum(in Matrix4x4 viewProjection) {
        // The columns. A clip coordinate is the dot product of the homogeneous point with one of
        // these, which is what makes the plane inequalities below fall out as column arithmetic.
        var x = new Vector4(viewProjection.M11, viewProjection.M21, viewProjection.M31, viewProjection.M41);
        var y = new Vector4(viewProjection.M12, viewProjection.M22, viewProjection.M32, viewProjection.M42);
        var z = new Vector4(viewProjection.M13, viewProjection.M23, viewProjection.M33, viewProjection.M43);
        var w = new Vector4(viewProjection.M14, viewProjection.M24, viewProjection.M34, viewProjection.M44);

        Left = PlaneFrom(w + x);   // clip.x >= -clip.w
        Right = PlaneFrom(w - x);  // clip.x <=  clip.w
        Bottom = PlaneFrom(w + y); // clip.y >= -clip.w
        Top = PlaneFrom(w - y);    // clip.y <=  clip.w

        // Reverse-Z: depth 1 is the near plane, so `clip.z <= clip.w` bounds the *near* side and
        // `clip.z >= 0` bounds the far side. A forward-Z projection would have these swapped.
        Near = PlaneFrom(w - z);
        Far = PlaneFrom(z);

        static Plane PlaneFrom(Vector4 coefficients) => Plane.Normalize(new(coefficients.Xyz, coefficients.W));
    }

    /// <summary>
    ///     The six planes as a span, in the order near, far, left, right, top, bottom. Valid as long
    ///     as the frustum it came from.
    /// </summary>
    /// <returns>A span of <see cref="PlaneCount" /> planes.</returns>
    [UnscopedRef]
    public ReadOnlySpan<Plane> AsSpan() => MemoryMarshal.CreateReadOnlySpan(in Near, PlaneCount);

    /// <summary>Whether a point is inside the frustum.</summary>
    /// <param name="point">The point.</param>
    /// <returns><see langword="true" /> if it is on the inner side of all six planes.</returns>
    public bool Contains(Vector3 point) {
        foreach (var plane in AsSpan()) {
            if (plane.DotCoordinate(point) < 0f) {
                return false;
            }
        }

        return true;
    }

    /// <summary>How a box sits relative to the frustum.</summary>
    /// <param name="box">The box to test.</param>
    /// <returns>The containment relationship.</returns>
    /// <remarks>
    ///     Conservative: a box that is outside the frustum but straddles the extension of two planes
    ///     is reported as <see cref="ContainmentType.Intersects" /> rather than
    ///     <see cref="ContainmentType.Disjoint" />. That costs an occasional draw call and never
    ///     costs a missing object, which is the right way round for a cull.
    /// </remarks>
    public ContainmentType Contains(BoundingBox box) {
        var straddles = false;

        foreach (var plane in AsSpan()) {
            switch (box.Intersects(plane)) {
                case PlaneIntersectionType.Back:
                    return ContainmentType.Disjoint;
                case PlaneIntersectionType.Intersecting:
                    straddles = true;
                    break;
                case PlaneIntersectionType.Front:
                default:
                    break;
            }
        }

        return straddles ? ContainmentType.Intersects : ContainmentType.Contains;
    }

    /// <summary>How a sphere sits relative to the frustum.</summary>
    /// <param name="sphere">The sphere to test.</param>
    /// <returns>The containment relationship.</returns>
    /// <remarks>Conservative in the same way <see cref="Contains(BoundingBox)" /> is.</remarks>
    public ContainmentType Contains(BoundingSphere sphere) {
        var straddles = false;

        foreach (var plane in AsSpan()) {
            switch (sphere.Intersects(plane)) {
                case PlaneIntersectionType.Back:
                    return ContainmentType.Disjoint;
                case PlaneIntersectionType.Intersecting:
                    straddles = true;
                    break;
                case PlaneIntersectionType.Front:
                default:
                    break;
            }
        }

        return straddles ? ContainmentType.Intersects : ContainmentType.Contains;
    }

    /// <summary>Whether a box is at least partly visible.</summary>
    /// <param name="box">The box to test.</param>
    /// <returns><see langword="false" /> only if the box is certainly outside.</returns>
    public bool Intersects(BoundingBox box) => Contains(box) != ContainmentType.Disjoint;

    /// <summary>Whether a sphere is at least partly visible.</summary>
    /// <param name="sphere">The sphere to test.</param>
    /// <returns><see langword="false" /> only if the sphere is certainly outside.</returns>
    public bool Intersects(BoundingSphere sphere) => Contains(sphere) != ContainmentType.Disjoint;

    /// <summary>
    ///     The eight corners, near face first: bottom-left, bottom-right, top-right, top-left, then
    ///     the same four on the far face. Shadow cascade fitting and frustum debug drawing both want
    ///     these.
    /// </summary>
    /// <param name="destination">A span of at least <see cref="CornerCount" /> vectors.</param>
    /// <exception cref="ArgumentException"><paramref name="destination" /> is too short.</exception>
    public void GetCorners(Span<Vector3> destination) {
        if (destination.Length < CornerCount) {
            throw new ArgumentException($"A frustum has {CornerCount} corners.", nameof(destination));
        }

        destination[0] = Intersection(Near, Bottom, Left);
        destination[1] = Intersection(Near, Bottom, Right);
        destination[2] = Intersection(Near, Top, Right);
        destination[3] = Intersection(Near, Top, Left);
        destination[4] = Intersection(Far, Bottom, Left);
        destination[5] = Intersection(Far, Bottom, Right);
        destination[6] = Intersection(Far, Top, Right);
        destination[7] = Intersection(Far, Top, Left);
    }

    /// <summary>The single point where three planes meet.</summary>
    /// <param name="a">The first plane.</param>
    /// <param name="b">The second plane.</param>
    /// <param name="c">The third plane.</param>
    /// <returns>The point, or <see cref="Vector3.Zero" /> if two of the planes are parallel.</returns>
    public static Vector3 Intersection(Plane a, Plane b, Plane c) {
        var bCrossC = Vector3.Cross(b.Normal, c.Normal);
        var determinant = Vector3.Dot(a.Normal, bCrossC);

        if (MathF.Abs(determinant) < MathUtil.ZeroTolerance) {
            return Vector3.Zero;
        }

        var numerator = (-a.D * bCrossC)
            - (b.D * Vector3.Cross(c.Normal, a.Normal))
            - (c.D * Vector3.Cross(a.Normal, b.Normal));

        return numerator / determinant;
    }

    /// <summary>Whether two frusta agree to within a tolerance, plane by plane.</summary>
    /// <param name="left">The first frustum.</param>
    /// <param name="right">The second frustum.</param>
    /// <param name="tolerance">The relative tolerance.</param>
    /// <returns><see langword="true" /> if every plane is within tolerance.</returns>
    public static bool NearEqual(
        in BoundingFrustum left,
        in BoundingFrustum right,
        float tolerance = MathUtil.ZeroTolerance
    ) {
        var a = left.AsSpan();
        var b = right.AsSpan();

        for (var i = 0; i < PlaneCount; i++) {
            if (!Plane.NearEqual(a[i], b[i], tolerance)) {
                return false;
            }
        }

        return true;
    }

    /// <summary>Exact equality, IEEE semantics. See <see cref="NearEqual" />.</summary>
    /// <param name="left">The first frustum.</param>
    /// <param name="right">The second frustum.</param>
    /// <returns><see langword="true" /> if every plane is equal.</returns>
    public static bool operator ==(BoundingFrustum left, BoundingFrustum right) => left.Equals(right);

    /// <summary>The negation of <see cref="op_Equality" />.</summary>
    /// <param name="left">The first frustum.</param>
    /// <param name="right">The second frustum.</param>
    /// <returns><see langword="true" /> if any plane differs.</returns>
    public static bool operator !=(BoundingFrustum left, BoundingFrustum right) => !(left == right);

    /// <inheritdoc />
    public bool Equals(BoundingFrustum other) =>
        Near == other.Near && Far == other.Far && Left == other.Left
        && Right == other.Right && Top == other.Top && Bottom == other.Bottom;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is BoundingFrustum other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Near, Far, Left, Right, Top, Bottom);

    /// <inheritdoc />
    public override string ToString() =>
        $"{{Near:{Near} Far:{Far} Left:{Left} Right:{Right} Top:{Top} Bottom:{Bottom}}}";
}
