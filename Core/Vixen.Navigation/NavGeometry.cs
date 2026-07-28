// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Navigation;

/// <summary>
///     The plane geometry every navmesh question reduces to, done in XZ with the height carried
///     along.
/// </summary>
/// <remarks>
///     <para>
///         A navmesh is a two-and-a-half-dimensional structure: the polygons are planar in XZ and
///         only their vertices know about Y. Containment, intersection and distance are therefore all
///         two-dimensional, and height is recovered afterwards by interpolating across the polygon.
///         Doing it in three dimensions would be slower and would answer a different question — a
///         point standing on a bridge is inside the polygon under it too.
///     </para>
///     <para>
///         The methods are internal because they are a shape this assembly reasons in, not an API
///         anybody outside it should be reaching for; <c>Vixen.Core.Mathematics</c> is where a general
///         geometry helper belongs.
///     </para>
/// </remarks>
internal static class NavGeometry {
    /// <summary>The z-component of the cross product of two XZ vectors.</summary>
    /// <param name="left">The first vector.</param>
    /// <param name="right">The second.</param>
    /// <returns>Positive when the second turns clockwise from the first, seen from +Y.</returns>
    public static float Cross2D(Vector3 left, Vector3 right) => (left.X * right.Z) - (left.Z * right.X);

    /// <summary>Which side of a directed line a point falls on.</summary>
    /// <param name="from">The line's start.</param>
    /// <param name="to">The line's end.</param>
    /// <param name="point">The point.</param>
    /// <returns>Twice the signed area of the triangle.</returns>
    public static float Side2D(Vector3 from, Vector3 to, Vector3 point) => Cross2D(to - from, point - from);

    /// <summary>The squared distance between two points, ignoring height.</summary>
    /// <param name="left">One point.</param>
    /// <param name="right">The other.</param>
    /// <returns>The squared distance in XZ.</returns>
    public static float DistanceSquared2D(Vector3 left, Vector3 right) {
        var x = left.X - right.X;
        var z = left.Z - right.Z;

        return (x * x) + (z * z);
    }

    /// <summary>The distance between two points, ignoring height.</summary>
    /// <param name="left">One point.</param>
    /// <param name="right">The other.</param>
    /// <returns>The distance in XZ.</returns>
    public static float Distance2D(Vector3 left, Vector3 right) => MathF.Sqrt(DistanceSquared2D(left, right));

    /// <summary>Twice the signed area of a polygon in XZ. Positive when it is wound counter-clockwise.</summary>
    /// <param name="poly">The polygon's vertices, in order.</param>
    /// <returns>Twice the signed area.</returns>
    /// <remarks>
    ///     The winding is a contract rather than an observation: the bake produces counter-clockwise
    ///     polygons, the funnel's left and right depend on it, and <see cref="ClipSegment2D" /> is
    ///     only a half-plane clip because of it. This is what a test asserts that with.
    /// </remarks>
    public static float SignedArea2D(ReadOnlySpan<Vector3> poly) {
        var total = 0f;

        for (int index = 0, previous = poly.Length - 1; index < poly.Length; previous = index++) {
            total += (poly[previous].X * poly[index].Z) - (poly[index].X * poly[previous].Z);
        }

        return total;
    }

    /// <summary>The outward normal of one edge of a counter-clockwise polygon, normalised in XZ.</summary>
    /// <param name="from">The edge's start.</param>
    /// <param name="to">The edge's end.</param>
    /// <returns>The normal, with a zero Y.</returns>
    public static Vector3 OutwardNormal2D(Vector3 from, Vector3 to) {
        var edge = to - from;
        var normal = new Vector3(edge.Z, 0f, -edge.X);
        var length = normal.Length();

        return length > 1e-9f ? normal / length : Vector3.Zero;
    }

    /// <summary>Whether a point is inside a polygon, in XZ.</summary>
    /// <param name="point">The point.</param>
    /// <param name="poly">The polygon's vertices, in order.</param>
    /// <returns><see langword="true" /> if it is inside.</returns>
    /// <remarks>
    ///     A crossing test rather than a winding one, so it is right for any simple polygon and does
    ///     not care which way the vertices run. Points exactly on an edge fall one way or the other
    ///     depending on which edge; that is inherent to the test and is why callers that must not lose
    ///     a point — the ones tracking which polygon an agent is on — fall back to the nearest
    ///     boundary point rather than trusting a single containment answer.
    /// </remarks>
    public static bool ContainsPoint2D(Vector3 point, ReadOnlySpan<Vector3> poly) {
        var inside = false;

        for (int index = 0, previous = poly.Length - 1; index < poly.Length; previous = index++) {
            var current = poly[index];
            var last = poly[previous];

            if (current.Z > point.Z != last.Z > point.Z &&
                point.X < ((last.X - current.X) * (point.Z - current.Z) / (last.Z - current.Z)) + current.X) {
                inside = !inside;
            }
        }

        return inside;
    }

    /// <summary>The closest point on a segment, in XZ, with the height interpolated along it.</summary>
    /// <param name="point">The point.</param>
    /// <param name="from">The segment's start.</param>
    /// <param name="to">The segment's end.</param>
    /// <param name="t">Where along the segment the answer is, clamped to 0..1.</param>
    /// <returns>The closest point.</returns>
    public static Vector3 ClosestPointOnSegment2D(Vector3 point, Vector3 from, Vector3 to, out float t) {
        var direction = to - from;
        var lengthSquared = (direction.X * direction.X) + (direction.Z * direction.Z);

        t = lengthSquared > 1e-12f
            ? Math.Clamp((((point.X - from.X) * direction.X) + ((point.Z - from.Z) * direction.Z)) / lengthSquared, 0f, 1f)
            : 0f;

        return from + (direction * t);
    }

    /// <summary>The closest point on a polygon's boundary, in XZ.</summary>
    /// <param name="point">The point.</param>
    /// <param name="poly">The polygon's vertices, in order.</param>
    /// <param name="edge">Which edge the answer lies on.</param>
    /// <returns>The closest point on the boundary.</returns>
    public static Vector3 ClosestPointOnBoundary2D(Vector3 point, ReadOnlySpan<Vector3> poly, out int edge) {
        var best = poly[0];
        var bestDistance = float.MaxValue;
        edge = 0;

        for (var index = 0; index < poly.Length; index++) {
            var candidate = ClosestPointOnSegment2D(point, poly[index], poly[(index + 1) % poly.Length], out _);
            var distance = DistanceSquared2D(point, candidate);

            if (distance < bestDistance) {
                bestDistance = distance;
                best = candidate;
                edge = index;
            }
        }

        return best;
    }

    /// <summary>The height of a polygon at a point inside it, from the fan of triangles about its first vertex.</summary>
    /// <param name="point">The point, whose Y is ignored.</param>
    /// <param name="poly">The polygon's vertices, in order.</param>
    /// <param name="height">The interpolated height.</param>
    /// <returns><see langword="false" /> if the point is outside every triangle of the fan.</returns>
    /// <remarks>
    ///     A convex polygon fans about any vertex, and the bake only ever produces convex polygons, so
    ///     the fan covers exactly the polygon. Interpolating rather than taking the polygon's plane
    ///     matters where a polygon is not planar — which it need not be, because the merge step joins
    ///     triangles whose vertices came from different voxel columns.
    /// </remarks>
    public static bool TryGetHeight(Vector3 point, ReadOnlySpan<Vector3> poly, out float height) {
        for (var index = 2; index < poly.Length; index++) {
            if (TryGetTriangleHeight(point, poly[0], poly[index - 1], poly[index], out height)) {
                return true;
            }
        }

        height = 0f;

        return false;
    }

    /// <summary>The height of a triangle's plane at a point inside it.</summary>
    /// <param name="point">The point, whose Y is ignored.</param>
    /// <param name="a">The first vertex.</param>
    /// <param name="b">The second.</param>
    /// <param name="c">The third.</param>
    /// <param name="height">The interpolated height.</param>
    /// <returns><see langword="false" /> if the point is outside the triangle.</returns>
    public static bool TryGetTriangleHeight(Vector3 point, Vector3 a, Vector3 b, Vector3 c, out float height) {
        var v0X = c.X - a.X;
        var v0Z = c.Z - a.Z;
        var v1X = b.X - a.X;
        var v1Z = b.Z - a.Z;
        var v2X = point.X - a.X;
        var v2Z = point.Z - a.Z;

        var denominator = (v0X * v1Z) - (v0Z * v1X);

        if (MathF.Abs(denominator) < 1e-9f) {
            height = 0f;

            return false;
        }

        var u = ((v1Z * v2X) - (v1X * v2Z)) / denominator;
        var v = ((v0X * v2Z) - (v0Z * v2X)) / denominator;

        // A small tolerance, because a point that a containment test called inside the polygon has to
        // be found by one of the fan's triangles even when it sits exactly on a shared edge.
        const float Epsilon = 1e-4f;

        if (u < -Epsilon || v < -Epsilon || u + v > 1 + Epsilon) {
            height = 0f;

            return false;
        }

        height = a.Y + ((c.Y - a.Y) * u) + ((b.Y - a.Y) * v);

        return true;
    }

    /// <summary>Clips a segment against a convex polygon, in XZ.</summary>
    /// <param name="from">The segment's start.</param>
    /// <param name="to">The segment's end.</param>
    /// <param name="poly">The polygon's vertices, in order.</param>
    /// <param name="enter">Where along the segment it enters the polygon.</param>
    /// <param name="exit">Where along the segment it leaves.</param>
    /// <param name="enterEdge">The edge it entered through, or -1 if it started inside.</param>
    /// <param name="exitEdge">The edge it left through, or -1 if it ended inside.</param>
    /// <returns><see langword="false" /> if the segment misses the polygon entirely.</returns>
    /// <remarks>
    ///     The half-plane clip that only works because the polygons are convex — which the bake
    ///     guarantees, and which is the property the whole query layer is built on. A raycast across
    ///     the mesh is this, once per polygon, following the exit edge into the next one.
    /// </remarks>
    public static bool ClipSegment2D(
        Vector3 from,
        Vector3 to,
        ReadOnlySpan<Vector3> poly,
        out float enter,
        out float exit,
        out int enterEdge,
        out int exitEdge
    ) {
        const float Epsilon = 1e-6f;

        enter = 0f;
        exit = 1f;
        enterEdge = -1;
        exitEdge = -1;

        var direction = to - from;

        for (int index = 0, previous = poly.Length - 1; index < poly.Length; previous = index++) {
            // The polygons are wound counter-clockwise in XZ, so a point is inside the polygon when
            // it is to the left of every edge: cross(edge, point - edge start) ≥ 0.
            var edge = poly[index] - poly[previous];
            var side = Cross2D(edge, from - poly[previous]);
            var rate = Cross2D(edge, direction);

            if (MathF.Abs(rate) < Epsilon) {
                // Parallel to this edge. Which side the segment is on does not change along it, so
                // being outside means missing the polygon whatever t is.
                if (side < 0) {
                    return false;
                }

                continue;
            }

            var t = -side / rate;

            if (rate > 0) {
                // Moving from outside to inside across this edge.
                if (t > enter) {
                    enter = t;
                    enterEdge = previous;

                    if (enter > exit) {
                        return false;
                    }
                }
            } else {
                if (t < exit) {
                    exit = t;
                    exitEdge = previous;

                    if (enter > exit) {
                        return false;
                    }
                }
            }
        }

        return true;
    }
}
