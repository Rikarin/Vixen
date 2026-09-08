// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Editor.Texturing.Painting;

/// <summary>
///     A curved stroke an artist has placed but not yet laid down: the points, and the positions a
///     stroke has to be moved through to follow the curve between them.
/// </summary>
/// <remarks>
///     <para>
///         <b>⚠ The front end is what <a href="https://github.com/Rikarin/Vixen/issues/1084">#1084</a>
///         said was missing, and the sampler is the easy half.</b> <c>BrushStroke.MoveTo</c> walks the
///         segment between two positions laying evenly spaced stamps, so a straight line has needed
///         no new arithmetic since the shift-click landed — and feeding it a curve's two endpoints
///         paints the curve's <em>chord</em>. What was missing was somewhere to author the curve at
///         all, which is why that issue says building the sampler first would have been a finished
///         thing nothing calls.
///     </para>
///     <para>
///         ⚠ <b>So the authoring model is the one that needs no new gesture: the points are clicked
///         and the curve passes through them.</b> The alternative on the table was a pen tool with
///         tangent handles — click to place, drag to shape — which is what a vector editor has and
///         is three gestures rather than one, on a pane whose only existing gesture is a press. A
///         curve <em>through</em> its points is also the honest reading of what #1084 calls "the
///         polyline the shift-click line already half-implies": the same clicks, with the corners
///         taken off.
///     </para>
///     <para>
///         ⚠ <b>Centripetal Catmull-Rom rather than uniform, and the difference is visible on
///         exactly the paths an artist draws.</b> A uniform parameterisation loops and cusps where
///         two clicked points are close together and the next is far away — which is what happens
///         when somebody places a tight corner and then a long sweep. The centripetal
///         parameterisation is the one Yuksel proved cannot self-intersect within a segment, and it
///         costs a square root per point.
///     </para>
///     <para>
///         ⚠ <b>The tolerance is a distance in texels and every deviation this measures is one
///         too.</b> A tolerance expressed as a fraction of the parameter, or an absolute epsilon on
///         a coordinate, would be a curve that is smooth on a small path and visibly faceted on a
///         large one — this repository's recurring shape. <see cref="Sample" /> is asserted at three
///         path sizes for that reason.
///     </para>
/// </remarks>
sealed class PaintPath {
    /// <summary>How many times a segment may be halved before the tolerance is given up on.</summary>
    /// <remarks>
    ///     ⚠ <b>A bound on the work and not on the accuracy, and the two are not the same claim.</b>
    ///     Halving a segment quarters its deviation, so ten levels cover a curve whose extent is a
    ///     million times the tolerance — past any atlas. It exists so that a path with two coincident
    ///     points, whose midpoint never converges, cannot spin.
    /// </remarks>
    public const int MaximumDepth = 10;

    /// <summary>How close the sampled polyline is asked to stay to the curve, in texels.</summary>
    /// <remarks>
    ///     Half a texel: the stamp is a footprint of texels, so a curve the polyline never leaves by
    ///     more than half of one is a stroke an artist cannot tell from the curve they drew.
    /// </remarks>
    public const float Tolerance = 0.5f;

    readonly List<Vector2> points = [];

    /// <summary>The points an artist has placed, in texels of the atlas.</summary>
    public IReadOnlyList<Vector2> Points => points;

    /// <summary>Whether there is enough here to stroke: two points make a curve, one makes a dot.</summary>
    public bool IsStrokeable => points.Count >= 2;

    /// <summary>Places a point at the end.</summary>
    /// <param name="at">Where, in texels.</param>
    public void Add(Vector2 at) => points.Add(at);

    /// <summary>Takes the last point back off.</summary>
    /// <returns>Whether there was one.</returns>
    public bool Undo() {
        if (points.Count == 0) {
            return false;
        }

        points.RemoveAt(points.Count - 1);

        return true;
    }

    /// <summary>Forgets the whole path.</summary>
    public void Clear() => points.Clear();

    /// <summary>The positions a stroke has to be moved through to follow the curve.</summary>
    /// <param name="into">Cleared, then filled with the positions in order, the first point first.</param>
    /// <param name="tolerance">
    ///     How far the polyline may sit from the curve, in texels. Not positive is
    ///     <see cref="Tolerance" />.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="into" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>Positions and not stamps.</b> Spacing, jitter and the leftover distance carried
    ///     between segments all belong to <c>BrushStroke</c>, and a sampler that decided where the
    ///     stamps went would be a second opinion about the thing a stroke exists to answer — so a
    ///     path laid at a fine tolerance and the same path laid at a coarse one differ in how many
    ///     times the stroke is told where the pointer is, and not in where a single stamp lands.
    /// </remarks>
    public void Sample(List<Vector2> into, float tolerance = Tolerance) {
        ArgumentNullException.ThrowIfNull(into);

        into.Clear();

        if (points.Count == 0) {
            return;
        }

        var limit = tolerance > 0f ? tolerance : Tolerance;

        into.Add(points[0]);

        for (var segment = 0; segment + 1 < points.Count; segment++) {
            // ⚠ The ends are doubled rather than extrapolated. A phantom control point placed by
            // reflecting the second through the first makes the curve leave the first point in a
            // direction nobody clicked, and an artist's first click is the one they placed most
            // deliberately.
            var before = points[Math.Max(segment - 1, 0)];
            var start = points[segment];
            var end = points[segment + 1];
            var after = points[Math.Min(segment + 2, points.Count - 1)];

            Split(into, before, start, end, after, 0f, 1f, start, end, limit, 0);
            into.Add(end);
        }
    }

    /// <summary>Halves a stretch of one segment until its chord is inside the tolerance.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The deviation is the <em>perpendicular</em> distance to the chord and not the
    ///         distance to the chord's midpoint, and the two are not the same number here.</b> The
    ///         parameterisation is centripetal, so the curve at the middle of the parameter interval
    ///         is not over the middle of the chord — which means a perfectly straight path measured
    ///         the second way reports a deviation equal to how uneven the clicks were, and gets
    ///         subdivided thirteen times for a line. What a stroke cares about is how far the paint
    ///         is from the drawing, which is the distance to the segment.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And three samples rather than one, because a midpoint test on a cubic has a
    ///         known false positive.</b> An S-shaped stretch crosses its own chord at the middle —
    ///         deviation exactly nought — while both halves bow away from it, so a single-sample
    ///         test declares the worst case flat. The quarter points are where those bows are.
    ///     </para>
    /// </remarks>
    static void Split(
        List<Vector2> into,
        Vector2 before,
        Vector2 start,
        Vector2 end,
        Vector2 after,
        float from,
        float to,
        Vector2 at,
        Vector2 until,
        float tolerance,
        int depth
    ) {
        var middle = (from + to) * 0.5f;
        var curve = Evaluate(before, start, end, after, middle);

        if (depth >= MaximumDepth) {
            return;
        }

        var deviation = MathF.Max(
            Deviation(curve, at, until),
            MathF.Max(
                Deviation(Evaluate(before, start, end, after, (from + middle) * 0.5f), at, until),
                Deviation(Evaluate(before, start, end, after, (middle + to) * 0.5f), at, until)
            )
        );

        if (deviation <= tolerance) {
            return;
        }

        Split(into, before, start, end, after, from, middle, at, curve, tolerance, depth + 1);
        into.Add(curve);
        Split(into, before, start, end, after, middle, to, curve, until, tolerance, depth + 1);
    }

    /// <summary>How far a point of the curve is from the chord a stroke would walk instead.</summary>
    /// <param name="point">The point on the curve.</param>
    /// <param name="from">Where the chord starts.</param>
    /// <param name="until">Where it ends.</param>
    /// <returns>The distance, in texels.</returns>
    static float Deviation(Vector2 point, Vector2 from, Vector2 until) {
        var span = until - from;
        var length = span.LengthSquared();

        if (!(length > 0f)) {
            return (point - from).Length();
        }

        var t = Math.Clamp(Vector2.Dot(point - from, span) / length, 0f, 1f);

        return (point - (from + (span * t))).Length();
    }

    /// <summary>One point of the centripetal Catmull-Rom spline through four control points.</summary>
    /// <param name="before">The point before the segment.</param>
    /// <param name="start">Where the segment starts.</param>
    /// <param name="end">Where it ends.</param>
    /// <param name="after">The point after it.</param>
    /// <param name="t">How far along the segment, 0…1.</param>
    /// <returns>The point on the curve.</returns>
    /// <remarks>
    ///     ⚠ <b>Written as the Barry–Goldman pyramid rather than as a basis matrix, because the
    ///     knots are not uniform.</b> The matrix form only exists for the uniform parameterisation;
    ///     with centripetal knots the interpolation has to be done on the knots themselves, which is
    ///     what these three levels are. ⚠ <b>And a repeated knot is a division by zero</b> — two
    ///     clicks in the same place, which an artist produces by double-clicking — so each level
    ///     falls back to its left end where the interval has no width, rather than returning a NaN
    ///     that would propagate into a stamp centre and paint nothing anywhere.
    /// </remarks>
    static Vector2 Evaluate(Vector2 before, Vector2 start, Vector2 end, Vector2 after, float t) {
        var t0 = 0f;
        var t1 = t0 + Knot(before, start);
        var t2 = t1 + Knot(start, end);
        var t3 = t2 + Knot(end, after);
        var at = t1 + ((t2 - t1) * t);

        var a1 = Mix(before, start, t0, t1, at);
        var a2 = Mix(start, end, t1, t2, at);
        var a3 = Mix(end, after, t2, t3, at);
        var b1 = Mix(a1, a2, t0, t2, at);
        var b2 = Mix(a2, a3, t1, t3, at);

        return Mix(b1, b2, t1, t2, at);
    }

    /// <summary>The knot spacing between two control points: the centripetal exponent is a quarter.</summary>
    /// <remarks>
    ///     ⚠ <b>Floored well above zero rather than at it.</b> Two coincident clicks give a knot
    ///     interval of nought, and every level of the pyramid divides by one — so the floor is what
    ///     stops a double-click producing a curve of NaNs. It is far below a texel, so no path an
    ///     artist can see is moved by it.
    /// </remarks>
    static float Knot(Vector2 from, Vector2 to) =>
        MathF.Max(MathF.Sqrt((to - from).Length()), 1e-4f);

    static Vector2 Mix(Vector2 from, Vector2 to, float at, float until, float t) {
        var width = until - at;

        return width <= 0f ? from : from + ((to - from) * ((t - at) / width));
    }
}
