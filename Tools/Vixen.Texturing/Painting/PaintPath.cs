// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Texturing.Painting;

/// <summary>
///     A curved stroke an artist has placed but not yet laid down: the points, and the positions a
///     stroke has to be moved through to follow the curve between them.
/// </summary>
/// <remarks>
///     <para>
///         <b>
///             ⚠ The front end is what <a href="https://github.com/Rikarin/Vixen/issues/1084">#1084</a>
///             said was missing, and the sampler is the easy half.
///         </b>
///         <c>BrushStroke.MoveTo</c> walks the
///         segment between two positions laying evenly spaced stamps, so a straight line has needed
///         no new arithmetic since the shift-click landed — and feeding it a curve's two endpoints
///         paints the curve's <em>chord</em>. What was missing was somewhere to author the curve at
///         all, which is why that issue says building the sampler first would have been a finished
///         thing nothing calls.
///     </para>
///     <para>
///         ⚠
///         <b>
///             So the authoring model is the one that needs no new gesture: the points are clicked
///             and the curve passes through them.
///         </b>
///         The alternative on the table was a pen tool with
///         tangent handles — click to place, drag to shape — which is what a vector editor has and
///         is three gestures rather than one, on a pane whose only existing gesture is a press. A
///         curve <em>through</em> its points is also the honest reading of what #1084 calls "the
///         polyline the shift-click line already half-implies": the same clicks, with the corners
///         taken off.
///     </para>
///     <para>
///         ⚠
///         <b>
///             Centripetal Catmull-Rom rather than uniform, and the difference is visible on
///             exactly the paths an artist draws.
///         </b>
///         A uniform parameterisation loops and cusps where
///         two clicked points are close together and the next is far away — which is what happens
///         when somebody places a tight corner and then a long sweep. The centripetal
///         parameterisation is the one Yuksel proved cannot self-intersect within a segment, and it
///         costs a square root per point.
///     </para>
///     <para>
///         ⚠
///         <b>
///             The tolerance is a distance in texels and every deviation this measures is one
///             too.
///         </b>
///         A tolerance expressed as a fraction of the parameter, or an absolute epsilon on
///         a coordinate, would be a curve that is smooth on a small path and visibly faceted on a
///         large one — this repository's recurring shape. <see cref="Sample(List{Vector2}, float)" />
///         is asserted at three path sizes for that reason.
///     </para>
/// </remarks>
public sealed class PaintPath {
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

    /// <summary>How hard each point was pressed, one to a point.</summary>
    readonly List<float> pressures = [];

    /// <summary>One segment's own polyline, for the hit tests. Reused rather than allocated.</summary>
    /// <remarks>
    ///     A hit test runs on every pointer move in the path mode, and the curve it walks is the
    ///     same one <see cref="Sample(List{Vector2}, float)" /> builds — so allocating a list per move
    ///     would be the defect <c>PaintUvView.dirtied</c> exists to avoid, on the hover path rather
    ///     than the drag one.
    /// </remarks>
    readonly List<Vector2> piece = [];

    /// <summary>The points at nought depth, and one segment's curve in that space: reused, for <see cref="piece" />'s reason.</summary>
    readonly List<Vector3> raised = [];

    readonly List<Vector3> lifted = [];

    /// <summary>The points an artist has placed, in texels of the atlas.</summary>
    public IReadOnlyList<Vector2> Points => points;

    /// <summary>Whether there is enough here to stroke: two points make a curve, one makes a dot.</summary>
    public bool IsStrokeable => points.Count >= 2;

    /// <summary>Places a point at the end.</summary>
    /// <param name="at">Where, in texels.</param>
    /// <param name="pressure">How hard, 0…1.</param>
    public void Add(Vector2 at, float pressure = 1f) {
        points.Add(at);
        pressures.Add(pressure);
    }

    /// <summary>Moves a placed point.</summary>
    /// <param name="index">Which one.</param>
    /// <param name="at">Where it goes, in texels.</param>
    /// <returns>Whether there was a point there.</returns>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         The correction half of
    ///         <a href="https://github.com/Rikarin/Vixen/issues/1084">#1084</a>, and the reason it is
    ///         owed rather than optional: a path an artist cannot correct is a path they will not
    ///         use.
    ///     </b>
    ///     The curve passes <em>through</em> its points, so moving one is the whole editing
    ///     model — there are no tangent handles to shape, by that issue's own choice.
    /// </remarks>
    public bool Move(int index, Vector2 at) {
        if (index < 0 || index >= points.Count) {
            return false;
        }

        points[index] = at;

        return true;
    }

    /// <summary>Puts a point between two that are already placed.</summary>
    /// <param name="index">Where in the order it goes; the point already there moves along.</param>
    /// <param name="at">Where it is, in texels.</param>
    /// <returns>Whether the index named a place in the path.</returns>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         An index and not a position to search from, because the caller has already decided
    ///         which segment was clicked.
    ///     </b>
    ///     A path that doubles back — a hook, a spiral, anything an
    ///     artist draws around a form — has two segments near one texel, and a model that picked one
    ///     of them itself would insert into whichever it happened to test first.
    /// </remarks>
    public bool Insert(int index, Vector2 at) {
        if (index < 0 || index > points.Count) {
            return false;
        }

        // Halfway, since a point put on a curve was never pressed: nothing should change where it lands.
        var between = index > 0 && index < pressures.Count ? (pressures[index - 1] + pressures[index]) * 0.5f : 1f;

        points.Insert(index, at);
        pressures.Insert(index, between);

        return true;
    }

    /// <summary>Takes one placed point back off, wherever it is in the order.</summary>
    /// <param name="index">Which one.</param>
    /// <returns>Whether there was a point there.</returns>
    /// <remarks>
    ///     ⚠ <b>Beside <see cref="Undo" /> rather than instead of it.</b> Backspace taking the last
    ///     point back is the pen gesture every tool has and is what an artist reaches for while
    ///     still placing; this is what they reach for afterwards, having seen the curve. The two
    ///     are different verbs and the keys say so.
    /// </remarks>
    public bool RemoveAt(int index) {
        if (index < 0 || index >= points.Count) {
            return false;
        }

        points.RemoveAt(index);
        pressures.RemoveAt(index);

        return true;
    }

    /// <summary>Which placed point a position is nearest, if any is close enough.</summary>
    /// <param name="at">Where, in texels.</param>
    /// <param name="within">How far away still counts, in texels.</param>
    /// <returns>The point's index, or -1.</returns>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         The <em>last</em> of two equally near points wins, and that is what makes a doubled
    ///         click correctable.
    ///     </b>
    ///     Two points in the same texel are what an artist produces by
    ///     double-clicking, and the one they mean to drag away is the one they placed last — a
    ///     search that stopped at the first match would leave the second permanently under it.
    /// </remarks>
    public int Nearest(Vector2 at, float within) {
        var best = within;
        var found = -1;

        for (var index = 0; index < points.Count; index++) {
            var distance = (points[index] - at).Length();

            if (distance <= best) {
                best = distance;
                found = index;
            }
        }

        return found;
    }

    /// <summary>Which segment of the drawn curve a position is nearest, if any is close enough.</summary>
    /// <param name="at">Where, in texels.</param>
    /// <param name="within">How far from the curve still counts, in texels.</param>
    /// <param name="on">The place on the curve it answers for, or <paramref name="at" /> for none.</param>
    /// <returns>The segment's index — the point it starts at — or -1.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             Against the sampled curve and not against the chord between two points, which is
    ///             the difference between a hit test and a near miss.
    ///         </b>
    ///         The curve bows away from its
    ///         chord by design — that is the whole reason a curved stroke is not two
    ///         <c>MoveTo</c> calls — so a test against chords would refuse a click made on the line
    ///         the artist can see, in exactly the places the curve is most curved.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             At <see cref="Tolerance" /> and not at the caller's, so the thing tested is the
    ///             thing drawn.
    ///         </b>
    ///         <c>PaintUvView</c> previews the path at this tolerance and lays the
    ///         stroke at it too; a hit test sampled coarser would answer against a polyline nobody
    ///         has ever seen.
    ///     </para>
    /// </remarks>
    public int Nearest(Vector2 at, float within, out Vector2 on) {
        on = at;

        var best = within;
        var found = -1;

        for (var segment = 0; segment + 1 < points.Count; segment++) {
            piece.Clear();
            piece.Add(points[segment]);
            Fill(piece, segment, Tolerance);
            piece.Add(points[segment + 1]);

            for (var step = 1; step < piece.Count; step++) {
                var distance = Closest(at, piece[step - 1], piece[step], out var closest);

                if (distance > best) {
                    continue;
                }

                best = distance;
                found = segment;
                on = closest;
            }
        }

        return found;
    }

    /// <summary>Takes the last point back off.</summary>
    /// <returns>Whether there was one.</returns>
    public bool Undo() {
        if (points.Count == 0) {
            return false;
        }

        points.RemoveAt(points.Count - 1);
        pressures.RemoveAt(pressures.Count - 1);

        return true;
    }

    /// <summary>Forgets the whole path.</summary>
    public void Clear() {
        points.Clear();
        pressures.Clear();
    }

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
            Fill(into, segment, limit);
            into.Add(points[segment + 1]);
        }
    }

    /// <summary>The positions a stroke has to be moved through, and how hard at each.</summary>
    /// <param name="into">Cleared, then filled as <see cref="Sample(List{Vector2}, float)" /> fills it.</param>
    /// <param name="pressure">Cleared, then filled with one pressure to a position.</param>
    /// <param name="tolerance">
    ///     How far the polyline may sit from the curve, in texels. Not positive is
    ///     <see cref="Tolerance" />.
    /// </param>
    /// <exception cref="ArgumentNullException">A list is null.</exception>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         By distance along each segment and not by sample, for the reason the positions are
    ///         not stamps.
    ///     </b>
    ///     How many samples a stretch of curve gets is what the tolerance decided,
    ///     and a fade laid out per sample would run faster round the bends.
    /// </remarks>
    public void Sample(List<Vector2> into, List<float> pressure, float tolerance = Tolerance) {
        ArgumentNullException.ThrowIfNull(into);
        ArgumentNullException.ThrowIfNull(pressure);

        into.Clear();
        pressure.Clear();

        if (points.Count == 0) {
            return;
        }

        var limit = tolerance > 0f ? tolerance : Tolerance;

        into.Add(points[0]);
        pressure.Add(pressures[0]);

        for (var segment = 0; segment + 1 < points.Count; segment++) {
            var start = into.Count - 1;

            Fill(into, segment, limit);
            into.Add(points[segment + 1]);
            Fade(into, start, pressures[segment], pressures[segment + 1], pressure);
        }
    }

    /// <summary>Lays one segment's fade along the positions it was cut into, after its first.</summary>
    static void Fade(List<Vector2> positions, int start, float from, float to, List<float> pressure) {
        var length = 0f;

        for (var index = start + 1; index < positions.Count; index++) {
            length += (positions[index] - positions[index - 1]).Length();
        }

        var walked = 0f;

        for (var index = start + 1; index + 1 < positions.Count; index++) {
            walked += (positions[index] - positions[index - 1]).Length();
            pressure.Add(float.Lerp(from, to, length > 0f ? walked / length : 1f));
        }

        // The point itself and not the far end of a sum: an artist's click holds exactly what it was given.
        pressure.Add(to);
    }

    /// <summary>Puts one segment's interior points into a list, without either of its ends.</summary>
    /// <param name="into">Where they go, appended.</param>
    /// <param name="segment">Which segment: the one starting at that point.</param>
    /// <param name="tolerance">How far the polyline may sit from the curve, in texels.</param>
    /// <remarks>
    ///     ⚠ The ends are doubled rather than extrapolated. A phantom control point placed by
    ///     reflecting the second through the first makes the curve leave the first point in a
    ///     direction nobody clicked, and an artist's first click is the one they placed most
    ///     deliberately.
    /// </remarks>
    void Fill(List<Vector2> into, int segment, float tolerance) {
        raised.Clear();

        foreach (var point in points) {
            raised.Add(new(point.X, point.Y, 0f));
        }

        lifted.Clear();
        Fill(lifted, raised, segment, tolerance);

        foreach (var point in lifted) {
            into.Add(new(point.X, point.Y));
        }
    }

    /// <summary>One segment's interior points of a curve through any points, flat or not.</summary>
    /// <remarks>
    ///     ⚠ <b>One curve for the atlas and for the model</b> (Patina #130): a path on the atlas is this curve with every
    ///     point at nought depth, which changes no deviation, so a path laid on the surface bends exactly as one laid
    ///     on the map does.
    /// </remarks>
    static void Fill(List<Vector3> into, IReadOnlyList<Vector3> through, int segment, float tolerance) {
        var before = through[Math.Max(segment - 1, 0)];
        var start = through[segment];
        var end = through[segment + 1];
        var after = through[Math.Min(segment + 2, through.Count - 1)];

        Split(into, before, start, end, after, 0f, 1f, start, end, tolerance, 0);
    }

    /// <summary>A curve through points anywhere in space, as a polyline, and where along the points each sample is.</summary>
    /// <param name="through">The points the curve passes through, in order.</param>
    /// <param name="tolerance">How far the polyline may sit from the curve, in the points' own units. Above zero.</param>
    /// <param name="into">Cleared, then filled with the polyline, the first point first.</param>
    /// <param name="along">
    ///     Cleared, then filled with one number to a sample: the segment it is on, plus how far along that segment it is
    ///     by distance, 0 to 1. A caller lerps whatever each point carries — a normal, a pressure — by it.
    /// </param>
    /// <exception cref="ArgumentNullException">A list is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tolerance" /> is not above zero.</exception>
    /// <remarks>
    ///     By distance along each segment and not by sample, for <see cref="Sample(List{Vector2}, List{float}, float)" />'s
    ///     reason: how many samples a stretch gets is the tolerance's business, and a fade laid out per sample would run
    ///     faster round the bends.
    /// </remarks>
    public static void Sample(IReadOnlyList<Vector3> through, float tolerance, List<Vector3> into, List<float> along) {
        ArgumentNullException.ThrowIfNull(through);
        ArgumentNullException.ThrowIfNull(into);
        ArgumentNullException.ThrowIfNull(along);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tolerance);

        into.Clear();
        along.Clear();

        if (through.Count == 0) {
            return;
        }

        into.Add(through[0]);
        along.Add(0f);

        for (var segment = 0; segment + 1 < through.Count; segment++) {
            var start = into.Count - 1;

            Fill(into, through, segment, tolerance);
            into.Add(through[segment + 1]);

            var length = 0f;

            for (var index = start + 1; index < into.Count; index++) {
                length += (into[index] - into[index - 1]).Length();
            }

            var walked = 0f;

            for (var index = start + 1; index + 1 < into.Count; index++) {
                walked += (into[index] - into[index - 1]).Length();
                along.Add(segment + (length > 0f ? walked / length : 1f));
            }

            along.Add(segment + 1f);
        }
    }

    /// <summary>Halves a stretch of one segment until its chord is inside the tolerance.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             The deviation is the <em>perpendicular</em> distance to the chord and not the
    ///             distance to the chord's midpoint, and the two are not the same number here.
    ///         </b>
    ///         The
    ///         parameterisation is centripetal, so the curve at the middle of the parameter interval
    ///         is not over the middle of the chord — which means a perfectly straight path measured
    ///         the second way reports a deviation equal to how uneven the clicks were, and gets
    ///         subdivided thirteen times for a line. What a stroke cares about is how far the paint
    ///         is from the drawing, which is the distance to the segment.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             And three samples rather than one, because a midpoint test on a cubic has a
    ///             known false positive.
    ///         </b>
    ///         An S-shaped stretch crosses its own chord at the middle —
    ///         deviation exactly nought — while both halves bow away from it, so a single-sample
    ///         test declares the worst case flat. The quarter points are where those bows are.
    ///     </para>
    /// </remarks>
    static void Split(
        List<Vector3> into,
        Vector3 before,
        Vector3 start,
        Vector3 end,
        Vector3 after,
        float from,
        float to,
        Vector3 at,
        Vector3 until,
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
    static float Deviation(Vector3 point, Vector3 from, Vector3 until) => Closest(point, from, until, out _);

    /// <summary>How far a point is from a segment, and which place on it is nearest.</summary>
    /// <param name="point">The point.</param>
    /// <param name="from">Where the segment starts.</param>
    /// <param name="until">Where it ends.</param>
    /// <param name="on">The nearest place on the segment, ends included.</param>
    /// <returns>The distance, in texels.</returns>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         Clamped to the segment rather than to its infinite line, which is what makes it a
    ///         hit test as well as a deviation.
    ///     </b>
    ///     An unclamped projection answers a distance of nought
    ///     for a click a long way past the end of a stretch of curve, which would insert a point
    ///     into a segment the artist was nowhere near.
    /// </remarks>
    static float Closest(Vector2 point, Vector2 from, Vector2 until, out Vector2 on) {
        var distance = Closest(
            new(point.X, point.Y, 0f),
            new(from.X, from.Y, 0f),
            new(until.X, until.Y, 0f),
            out Vector3 raised
        );

        on = new(raised.X, raised.Y);

        return distance;
    }

    /// <summary>How far a point is from a segment in space, and which place on it is nearest.</summary>
    static float Closest(Vector3 point, Vector3 from, Vector3 until, out Vector3 on) {
        var span = until - from;
        var length = span.LengthSquared();

        if (!(length > 0f)) {
            on = from;

            return (point - from).Length();
        }

        var t = Math.Clamp(Vector3.Dot(point - from, span) / length, 0f, 1f);

        on = from + span * t;

        return (point - on).Length();
    }

    /// <summary>One point of the centripetal Catmull-Rom spline through four control points.</summary>
    /// <param name="before">The point before the segment.</param>
    /// <param name="start">Where the segment starts.</param>
    /// <param name="end">Where it ends.</param>
    /// <param name="after">The point after it.</param>
    /// <param name="t">How far along the segment, 0…1.</param>
    /// <returns>The point on the curve.</returns>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         Written as the Barry–Goldman pyramid rather than as a basis matrix, because the
    ///         knots are not uniform.
    ///     </b>
    ///     The matrix form only exists for the uniform parameterisation;
    ///     with centripetal knots the interpolation has to be done on the knots themselves, which is
    ///     what these three levels are. ⚠ <b>And a repeated knot is a division by zero</b> — two
    ///     clicks in the same place, which an artist produces by double-clicking — so each level
    ///     falls back to its left end where the interval has no width, rather than returning a NaN
    ///     that would propagate into a stamp centre and paint nothing anywhere.
    /// </remarks>
    static Vector3 Evaluate(Vector3 before, Vector3 start, Vector3 end, Vector3 after, float t) {
        var t0 = 0f;
        var t1 = t0 + Knot(before, start);
        var t2 = t1 + Knot(start, end);
        var t3 = t2 + Knot(end, after);
        var at = t1 + (t2 - t1) * t;

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
    static float Knot(Vector3 from, Vector3 to) => MathF.Max(MathF.Sqrt((to - from).Length()), 1e-4f);

    static Vector3 Mix(Vector3 from, Vector3 to, float at, float until, float t) {
        var width = until - at;

        return width <= 0f ? from : from + (to - from) * ((t - at) / width);
    }
}
