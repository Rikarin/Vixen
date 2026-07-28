// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Ui.Rendering;

/// <summary>One contour of a flattened path: a run of points, and whether it joins back up.</summary>
/// <param name="First">Where its points start.</param>
/// <param name="Count">How many points it has.</param>
/// <param name="Closed">Whether the last point joins back to the first.</param>
/// <remarks>
///     ⚠ <b><see cref="Closed" /> survives flattening, and it has to.</b> A fill closes every contour
///     regardless — an open one leaks along whatever the scanline crosses next — so for a fill the
///     flag is redundant. A stroke is the opposite: an open contour gets a cap at each end and a
///     closed one gets a join at the seam, and the two look nothing alike. Flattening a path into
///     bare point runs would throw away the only thing that decides which.
/// </remarks>
public readonly record struct Contour(int First, int Count, bool Closed);

/// <summary>Turns a path's curves into point runs, at a tolerance the caller chooses.</summary>
/// <remarks>
///     <para>
///         The counterpart of <c>OutlineFlattener</c> for paths rather than glyph outlines, and
///         deliberately not a shared implementation: an outline is flattened into <i>edges</i> that
///         each remember which segment they came from, because a distance field has to find the
///         corners the outline actually has. A path is flattened into <i>contours</i>, because a
///         tessellator needs to know which points are neighbours and a stroke needs to know where a
///         contour ends. The subdivision arithmetic is four lines; the output shapes are the whole
///         difference.
///     </para>
///     <para>
///         ⚠ <b>This is where <see cref="PathBuilder" />'s decision to keep curves as curves is
///         spent.</b> A path built once and drawn at two zoom levels is flattened twice, at two
///         tolerances, and is right at both — which is exactly what flattening in the builder would
///         have made impossible.
///     </para>
/// </remarks>
public static class PathFlattener {
    /// <summary>The most pieces one curve is ever cut into.</summary>
    /// <remarks>
    ///     A bound rather than a target, for the same reason the outline flattener has one: a
    ///     pathological control polygon asks for thousands of segments to describe a shape that is
    ///     forty pixels across.
    /// </remarks>
    const int MaxSubdivisions = 64;

    /// <summary>How close two points have to be before the second is dropped.</summary>
    /// <remarks>
    ///     A repeated point is not merely wasteful. It is a zero-length edge, which has no direction,
    ///     and a stroke asks every edge for its normal — so one duplicate point puts a NaN into the
    ///     geometry and takes the whole contour with it.
    /// </remarks>
    const float Coincident = 1e-6f;

    /// <summary>Flattens a range of a path's segments.</summary>
    /// <param name="segments">The buffer the path lives in.</param>
    /// <param name="offset">Where this path starts in it.</param>
    /// <param name="length">How many segments it has.</param>
    /// <param name="tolerance">
    ///     How far a chord may sit from the curve it replaces, in the path's own units. Under half a
    ///     device pixel is invisible.
    /// </param>
    /// <param name="points">Where the points go. Not cleared — a caller flattens several paths into one buffer.</param>
    /// <param name="contours">Where the contours go. Not cleared, for the same reason.</param>
    public static void Flatten(
        IReadOnlyList<PathSegment> segments,
        int offset,
        int length,
        float tolerance,
        List<Vector2> points,
        List<Contour> contours
    ) {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(contours);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tolerance);

        if (offset < 0 || length <= 0 || offset + length > segments.Count) {
            return;
        }

        var first = -1;
        var closed = false;

        for (var i = offset; i < offset + length; i++) {
            var segment = segments[i];

            switch (segment.Verb) {
                case PathVerb.Move:
                    End(points, contours, ref first, ref closed);
                    first = points.Count;
                    points.Add(segment.P2);
                    break;

                case PathVerb.Line:
                    Start(points, ref first);
                    Add(points, segment.P2);
                    break;

                case PathVerb.Quadratic:
                    Start(points, ref first);
                    Quadratic(points, points[^1], segment.P0, segment.P2, tolerance);
                    break;

                case PathVerb.Cubic:
                    Start(points, ref first);
                    Cubic(points, points[^1], segment.P0, segment.P1, segment.P2, tolerance);
                    break;

                case PathVerb.Close:
                    // ⚠ The closing point is *not* appended. `Close` carries where it closes to and
                    // that is the contour's own first point, so appending it would put a duplicate at
                    // the seam — which a stroke reads as a zero-length final edge and a fill reads as
                    // a degenerate triangle. The flag says the seam is there; the point is already in
                    // the buffer.
                    closed = true;
                    End(points, contours, ref first, ref closed);
                    break;

                default:
                    break;
            }
        }

        End(points, contours, ref first, ref closed);
    }

    /// <summary>Opens a contour for a verb that draws before anything moved.</summary>
    /// <remarks>
    ///     ⚠ At the origin, rather than at the verb's own first point. That is not a guess about what
    ///     the caller meant — <see cref="PathBuilder" /> starts its pen at <c>default</c>, so a
    ///     <c>LineTo</c> with no <c>MoveTo</c> before it really does draw from (0, 0), and flattening
    ///     it from anywhere else would draw a different path from the one the builder describes.
    /// </remarks>
    static void Start(List<Vector2> points, ref int first) {
        if (first < 0) {
            first = points.Count;
            points.Add(Vector2.Zero);
        }
    }

    /// <summary>Closes off the contour under construction, if it has enough points to be one.</summary>
    static void End(List<Vector2> points, List<Contour> contours, ref int first, ref bool closed) {
        if (first < 0) {
            return;
        }

        var count = points.Count - first;

        // ⚠ A closed contour may end on the point it started at, and then that point is in the buffer
        // twice — once from the `MoveTo` and once from a `LineTo` back to it. Dropped here rather
        // than left in, because the seam edge is implied by `Closed` and the duplicate would make it
        // a zero-length one.
        if (closed && count > 1 && Near(points[first], points[^1])) {
            points.RemoveAt(points.Count - 1);
            count--;
        }

        // Two points make a line, which a stroke can draw and a fill cannot. Kept either way: the
        // tessellator drops what it cannot use, and it is the one that knows which it is.
        if (count >= 2) {
            contours.Add(new Contour(first, count, closed));
        } else {
            points.RemoveRange(first, count);
        }

        first = -1;
        closed = false;
    }

    static void Add(List<Vector2> points, Vector2 point) {
        if (points.Count == 0 || !Near(points[^1], point)) {
            points.Add(point);
        }
    }

    static bool Near(Vector2 a, Vector2 b) => Vector2.DistanceSquared(a, b) <= Coincident * Coincident;

    static void Quadratic(List<Vector2> points, Vector2 from, Vector2 control, Vector2 to, float tolerance) {
        // How far the control point strays from the chord bounds the curve's own deviation.
        var steps = Steps(Vector2.Distance(from, control) + Vector2.Distance(control, to), tolerance);

        for (var i = 1; i <= steps; i++) {
            var t = (float)i / steps;
            var u = 1 - t;
            Add(points, (u * u * from) + (2 * u * t * control) + (t * t * to));
        }
    }

    static void Cubic(List<Vector2> points, Vector2 from, Vector2 first, Vector2 second, Vector2 to, float tolerance) {
        var polygon = Vector2.Distance(from, first) + Vector2.Distance(first, second) + Vector2.Distance(second, to);
        var steps = Steps(polygon, tolerance);

        for (var i = 1; i <= steps; i++) {
            var t = (float)i / steps;
            var u = 1 - t;

            Add(
                points,
                (u * u * u * from)
                + (3 * u * u * t * first)
                + (3 * u * t * t * second)
                + (t * t * t * to)
            );
        }
    }

    /// <summary>How many pieces a curve of that control-polygon length needs.</summary>
    /// <remarks>
    ///     The chord of a curve cut into <c>n</c> pieces deviates by roughly <c>L / n²</c>, so the
    ///     count grows with the square root of the length over the tolerance.
    /// </remarks>
    static int Steps(float polygon, float tolerance) =>
        Math.Clamp((int)Math.Ceiling(Math.Sqrt(polygon / tolerance)), 1, MaxSubdivisions);
}
