// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Ui.Rendering;

/// <summary>One corner of a tessellated triangle.</summary>
/// <param name="Position">Where it is, in the path's own units.</param>
/// <param name="Coverage">
///     How much of the pixel the shape covers there: one inside, falling to zero across the fringe.
/// </param>
/// <remarks>
///     ⚠ <b>The coverage is what antialiases a path</b>, and it has to be interpolated rather than
///     computed. A box and a glyph both carry a distance the shader evaluates per pixel; a triangle
///     carries no distance to its own boundary, so the only thing it can hand a fragment is a number
///     that was already 1 at one end of an edge and 0 at the other. That is the whole of the
///     technique and the whole of its limitation: it antialiases the <i>outline</i> and nothing else.
/// </remarks>
public readonly record struct PathVertex(Vector2 Position, float Coverage);

/// <summary>Turns flattened contours into triangles.</summary>
/// <remarks>
///     <para>
///         Emits <b>loose triangles</b> — every three vertices is one — rather than vertices and
///         indices. That is not a shortcut: the fill decomposition below produces trapezoids that
///         share no vertex with their neighbours, so an index buffer over it would be the identity
///         permutation and cost a second array to say so. The caller turns the vertices into
///         whatever its shader reads, which is also where the colour comes from.
///     </para>
///     <para>
///         ⚠ <b>The edge is antialiased by a fringe, not by the shader.</b> A rounded box gets a
///         perfect edge because the shader evaluates a distance field, and a glyph gets one for the
///         same reason; a triangle has no such function, so the outline is drawn twice — once as the
///         interior, and once as a half-pixel strip whose coverage runs from one on the inside to
///         zero on the outside. Multisampling the pass is the other answer and remains the
///         compositor's to choose; this one costs a strip of geometry and works under any pass.
///     </para>
/// </remarks>
public static class PathTessellator {
    /// <summary>Two points closer than this are the same point.</summary>
    const float Epsilon = 1e-6f;

    /// <summary>The most pieces one arc of a round join or cap is cut into.</summary>
    const int MaxArcSteps = 64;

    // ---------------------------------------------------------------- Fill

    /// <summary>Fills contours, honouring the fill rule.</summary>
    /// <param name="points">The flattened points.</param>
    /// <param name="contours">Which runs of them are contours.</param>
    /// <param name="rule">How to decide what is inside.</param>
    /// <param name="triangles">Where the triangles go, three points each. Not cleared.</param>
    /// <remarks>
    ///     <para>
    ///         <b>A trapezoid decomposition, not an ear clip.</b> Ear clipping is the usual answer and
    ///         it is wrong for the input this actually gets: it needs one simple polygon, so holes
    ///         need bridging edges and self-intersection needs resolving first — and a five-pointed
    ///         star drawn as five lines is self-intersecting, which is precisely the shape where
    ///         <see cref="PathFillRule.NonZero" /> and <see cref="PathFillRule.EvenOdd" /> disagree
    ///         and therefore the shape a fill rule exists for.
    ///     </para>
    ///     <para>
    ///         Sweeping instead makes the fill rule the whole of the algorithm rather than a
    ///         correction applied afterwards, and it is the same winding walk the glyph rasteriser
    ///         does one scanline at a time. The bands are cut at every vertex <i>and every crossing</i>,
    ///         so no edge begins, ends or crosses another inside one — which is what makes each span
    ///         an exact trapezoid rather than an approximation of one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The cost is quadratic in the edge count</b>, because finding the crossings compares
    ///         every pair. For what a user interface fills — icons, chart series, a few hundred edges
    ///         after flattening — that is microseconds and it buys correctness on arbitrary input. It
    ///         is the wrong shape for a path with thousands of edges, and the fix is the standard one:
    ///         a Bentley–Ottmann sweep with an active-edge list, which finds the same crossings in
    ///         <c>O((n + k) log n)</c>. Written down rather than built, because building it now would
    ///         be optimising a path nothing in the framework takes.
    ///     </para>
    /// </remarks>
    public static void Fill(
        IReadOnlyList<Vector2> points,
        IReadOnlyList<Contour> contours,
        PathFillRule rule,
        List<PathVertex> triangles
    ) {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(contours);
        ArgumentNullException.ThrowIfNull(triangles);

        var edges = new List<FillEdge>();

        foreach (var contour in contours) {
            if (contour.Count < 3) {
                continue;
            }

            // ⚠ Every contour is closed for filling, whatever its flag says. An open one has no
            // inside — the winding of a point depends on crossing a boundary that is not there — and
            // a scanline would run out of the shape and fill to whatever it meets next.
            for (var i = 0; i < contour.Count; i++) {
                var from = points[contour.First + i];
                var to = points[contour.First + ((i + 1) % contour.Count)];
                AddEdge(edges, from, to);
            }
        }

        if (edges.Count == 0) {
            return;
        }

        var bands = Bands(edges);
        var crossings = new List<Crossing>();

        for (var band = 0; band + 1 < bands.Count; band++) {
            var top = bands[band];
            var bottom = bands[band + 1];

            if (bottom - top <= Epsilon) {
                continue;
            }

            // ⚠ Sampled at the middle of the band, not at its top. At the top an edge that ends there
            // and one that starts there are both "at" the scanline, and whichever way the half-open
            // test falls, one band inherits the other's crossings. The middle is inside exactly the
            // edges that span the whole band, which is the set the trapezoids are built from.
            var middle = (top + bottom) * 0.5f;

            crossings.Clear();

            foreach (var edge in edges) {
                if (edge.Top <= middle && middle < edge.Bottom) {
                    crossings.Add(new Crossing(edge.At(middle), edge.At(top), edge.At(bottom), edge.Winding));
                }
            }

            if (crossings.Count < 2) {
                continue;
            }

            crossings.Sort(static (a, b) => a.Middle.CompareTo(b.Middle));

            var winding = 0;

            for (var i = 0; i + 1 < crossings.Count; i++) {
                winding += crossings[i].Winding;

                if (!Inside(winding, rule)) {
                    continue;
                }

                Trapezoid(triangles, crossings[i], crossings[i + 1], top, bottom);
            }
        }
    }

    /// <summary>Feathers a fill's outline, so its edge is not whatever the rasteriser gives it.</summary>
    /// <param name="points">The flattened points.</param>
    /// <param name="contours">Which runs of them are contours.</param>
    /// <param name="rule">How to decide what is inside — the same one <see cref="Fill" /> was given.</param>
    /// <param name="width">
    ///     How far the fringe reaches outwards. Half a device pixel is the usual choice.
    /// </param>
    /// <param name="triangles">Where the triangles go. Not cleared.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Which way is out is asked of the fill rule, not derived from the winding.</b> The
    ///         cheap version takes a contour's signed area and calls that its orientation, and it is
    ///         wrong for exactly the shapes that need a fill rule: under
    ///         <see cref="PathFillRule.EvenOdd" /> a hole is a hole however it is wound, so an inner
    ///         contour wound the same way as its outer one would have its fringe drawn <i>into</i> the
    ///         shape — a bright band around every counter in an icon set.
    ///     </para>
    ///     <para>
    ///         So each edge is probed on both sides and kept only if exactly one of them is inside.
    ///         That is another quadratic pass, which is the order the sweep above already runs at, and
    ///         it is right for both rules, for holes, and for a contour that crosses itself — where an
    ///         edge can be interior to the shape and correctly contributes no fringe at all.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The fringe overlaps the fill rather than replacing its outermost sliver</b>, so a
    ///         pixel on the boundary is covered twice: once at full coverage by the interior and once
    ///         at a partial one by the strip. Under a normal source-over blend that is slightly too
    ///         opaque at the seam and invisible; under an additive one it would be a bright outline.
    ///         Insetting the fill to make room is the alternative and it is worse — it needs the
    ///         interior recomputed against an offset outline, which is the offset-curve problem this
    ///         whole design avoids.
    ///     </para>
    /// </remarks>
    public static void FillFringe(
        IReadOnlyList<Vector2> points,
        IReadOnlyList<Contour> contours,
        PathFillRule rule,
        float width,
        List<PathVertex> triangles
    ) {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(contours);
        ArgumentNullException.ThrowIfNull(triangles);

        if (width <= 0) {
            return;
        }

        var edges = new List<FillEdge>();

        foreach (var contour in contours) {
            if (contour.Count < 3) {
                continue;
            }

            for (var i = 0; i < contour.Count; i++) {
                AddEdge(edges, points[contour.First + i], points[contour.First + ((i + 1) % contour.Count)]);
            }
        }

        if (edges.Count == 0) {
            return;
        }

        var outline = new List<(Vector2 From, Vector2 To)>();

        foreach (var contour in contours) {
            if (contour.Count < 3) {
                continue;
            }

            for (var i = 0; i < contour.Count; i++) {
                outline.Add((points[contour.First + i], points[contour.First + ((i + 1) % contour.Count)]));
            }
        }

        var splits = new List<float>();
        var index = 0;

        foreach (var contour in contours) {
            if (contour.Count < 3) {
                continue;
            }

            var entering = Vector2.Zero;
            var leaving = Vector2.Zero;
            var hasEntering = false;
            var hasLeaving = false;
            var seam = false;

            for (var i = 0; i < contour.Count; i++, index++) {
                var from = outline[index].From;
                var to = outline[index].To;

                Split(outline, index, splits);

                var startsOut = Vector2.Zero;
                var endsOut = Vector2.Zero;
                var startsBoundary = false;

                for (var piece = 0; piece + 1 < splits.Count; piece++) {
                    var head = Lerp(from, to, splits[piece]);
                    var tail = Lerp(from, to, splits[piece + 1]);

                    if (!Outward(edges, rule, head, tail, out var outward)) {
                        continue;
                    }

                    Feather(triangles, head, tail, outward, width);

                    if (piece == 0) {
                        startsOut = outward;
                        startsBoundary = true;
                    }

                    endsOut = outward;
                    hasLeaving = true;
                }

                // ⚠ The wedge two fringe quads leave at a convex corner. Sub-pixel, and left open it
                // is a notch out of every corner of every icon — which at a half-pixel fringe is
                // exactly the artefact the fringe was added to remove.
                if (hasEntering && startsBoundary) {
                    Corner(triangles, from, entering, startsOut, width);
                }

                if (i == 0 && startsBoundary) {
                    leaving = startsOut;
                    seam = true;
                }

                entering = endsOut;
                hasEntering = hasLeaving;
                hasLeaving = false;
            }

            // The seam, where the contour's last edge meets its first.
            if (seam && hasEntering) {
                Corner(triangles, outline[index - contour.Count].From, entering, leaving, width);
            }
        }
    }

    /// <summary>Where along an edge it has to be cut, because something crosses it there.</summary>
    /// <remarks>
    ///     ⚠ <b>An edge is not boundary or interior as a whole, and a self-crossing contour is made of
    ///     edges that are both.</b> A pentagram drawn as five lines has every chord passing through
    ///     the pentagon in the middle: probe such a chord once, at its midpoint, and the answer is
    ///     "interior" — so the whole chord gets no fringe and the star has no antialiased edge at all.
    ///     Cutting each edge where anything crosses it makes every piece wholly one or the other,
    ///     which is the same thing the sweep does to the bands and for the same reason.
    /// </remarks>
    static void Split(List<(Vector2 From, Vector2 To)> outline, int index, List<float> splits) {
        splits.Clear();
        splits.Add(0f);
        splits.Add(1f);

        var (from, to) = outline[index];

        for (var other = 0; other < outline.Count; other++) {
            if (other == index) {
                continue;
            }

            if (Meets(from, to, outline[other].From, outline[other].To, out var t)) {
                splits.Add(t);
            }
        }

        splits.Sort();
    }

    /// <summary>How far along the first segment the two cross, if they cross away from its ends.</summary>
    static bool Meets(Vector2 from, Vector2 to, Vector2 otherFrom, Vector2 otherTo, out float t) {
        t = 0f;

        var here = to - from;
        var there = otherTo - otherFrom;
        var denominator = (here.X * there.Y) - (here.Y * there.X);

        if (MathF.Abs(denominator) <= Epsilon) {
            return false;
        }

        var offset = otherFrom - from;
        t = ((offset.X * there.Y) - (offset.Y * there.X)) / denominator;
        var u = ((offset.X * here.Y) - (offset.Y * here.X)) / denominator;

        // Strictly inside this edge, and anywhere along the other one: a crossing at a shared vertex
        // is already a piece boundary by being one.
        return t > Epsilon && t < 1f - Epsilon && u >= 0f && u <= 1f;
    }

    static Vector2 Lerp(Vector2 from, Vector2 to, float t) => from + ((to - from) * t);

    /// <summary>Which side of an edge is outside the shape, if either is.</summary>
    /// <remarks>
    ///     Probed a hair off the midpoint rather than evaluated exactly on it: a point on the boundary
    ///     is where a winding count is least meaningful, which is the one place the answer must not
    ///     come from.
    /// </remarks>
    static bool Outward(
        List<FillEdge> edges,
        PathFillRule rule,
        Vector2 from,
        Vector2 to,
        out Vector2 outward
    ) {
        const float Probe = 1e-3f;

        outward = Vector2.Zero;
        var direction = Direction(from, to);

        if (direction == Vector2.Zero) {
            return false;
        }

        var normal = Perpendicular(direction);
        var middle = (from + to) * 0.5f;
        var ahead = Contains(edges, rule, middle + (normal * Probe));
        var behind = Contains(edges, rule, middle - (normal * Probe));

        if (ahead == behind) {
            // Inside on both sides, or outside on both: this edge is not a boundary here. Two
            // contours lying on top of each other, or a contour crossing itself, produce exactly this.
            return false;
        }

        outward = ahead ? -normal : normal;
        return true;
    }

    /// <summary>Whether a point is inside, by the same rule the fill used.</summary>
    static bool Contains(List<FillEdge> edges, PathFillRule rule, Vector2 point) {
        var winding = 0;

        foreach (var edge in edges) {
            if (edge.Top <= point.Y && point.Y < edge.Bottom && edge.At(point.Y) > point.X) {
                winding += edge.Winding;
            }
        }

        return Inside(winding, rule);
    }

    /// <summary>One edge's strip: full coverage on the edge, none at the far side.</summary>
    static void Feather(List<PathVertex> triangles, Vector2 from, Vector2 to, Vector2 outward, float width) {
        var offset = outward * width;
        var outerFrom = from + offset;
        var outerTo = to + offset;

        triangles.Add(new PathVertex(from, 1f));
        triangles.Add(new PathVertex(to, 1f));
        triangles.Add(new PathVertex(outerTo, 0f));
        triangles.Add(new PathVertex(from, 1f));
        triangles.Add(new PathVertex(outerTo, 0f));
        triangles.Add(new PathVertex(outerFrom, 0f));
    }

    /// <summary>
    ///     One edge's strip, with the outward direction taken from the piece's own centre — which is
    ///     what makes this right for every convex piece a stroke is built from without any of them
    ///     having to work out which side is out.
    /// </summary>
    static void FeatherFrom(List<PathVertex> triangles, Vector2 from, Vector2 to, Vector2 centre, float width) {
        var outward = Direction(centre, (from + to) * 0.5f);

        if (outward != Vector2.Zero) {
            Feather(triangles, from, to, outward, width);
        }
    }

    /// <summary>The wedge between two edges' strips, at the corner they share.</summary>
    static void Corner(List<PathVertex> triangles, Vector2 vertex, Vector2 first, Vector2 second, float width) {
        if (Vector2.DistanceSquared(first, second) <= Epsilon) {
            return;
        }

        triangles.Add(new PathVertex(vertex, 1f));
        triangles.Add(new PathVertex(vertex + (first * width), 0f));
        triangles.Add(new PathVertex(vertex + (second * width), 0f));
    }

    /// <summary>Whether a winding number counts as inside under a rule.</summary>
    static bool Inside(int winding, PathFillRule rule) =>
        rule == PathFillRule.EvenOdd ? (winding & 1) != 0 : winding != 0;

    /// <summary>The two triangles of one span of one band.</summary>
    static void Trapezoid(List<PathVertex> triangles, Crossing left, Crossing right, float top, float bottom) {
        var lt = left.Top;
        var rt = right.Top;
        var lb = left.Bottom;
        var rb = right.Bottom;

        // A span that is a point at both ends covers nothing. It happens wherever two contours touch.
        if (rt - lt <= Epsilon && rb - lb <= Epsilon) {
            return;
        }

        var a = new Vector2(lt, top);
        var b = new Vector2(rt, top);
        var c = new Vector2(rb, bottom);
        var d = new Vector2(lb, bottom);

        Triangle(triangles, a, b, c);
        Triangle(triangles, a, c, d);
    }

    /// <summary>Every y a band can begin or end at: the vertices, and where edges cross.</summary>
    static List<float> Bands(List<FillEdge> edges) {
        var values = new List<float>(edges.Count * 2);

        foreach (var edge in edges) {
            values.Add(edge.Top);
            values.Add(edge.Bottom);
        }

        for (var i = 0; i < edges.Count; i++) {
            for (var j = i + 1; j < edges.Count; j++) {
                if (Crosses(edges[i], edges[j], out var y)) {
                    values.Add(y);
                }
            }
        }

        values.Sort();

        var unique = new List<float>(values.Count);

        foreach (var value in values) {
            if (unique.Count == 0 || value - unique[^1] > Epsilon) {
                unique.Add(value);
            }
        }

        return unique;
    }

    /// <summary>Where two edges cross, if they cross strictly between their shared ends.</summary>
    /// <remarks>
    ///     Strictly, because a crossing at a shared endpoint is already a band boundary — both edges
    ///     put that y in the list by existing. Admitting it again would produce a zero-height band,
    ///     which the sweep skips anyway, at the cost of a comparison per pair.
    /// </remarks>
    static bool Crosses(FillEdge a, FillEdge b, out float y) {
        y = 0;

        var top = MathF.Max(a.Top, b.Top);
        var bottom = MathF.Min(a.Bottom, b.Bottom);

        if (bottom - top <= Epsilon) {
            return false;
        }

        var slope = a.Slope - b.Slope;

        if (MathF.Abs(slope) <= Epsilon) {
            return false;
        }

        y = ((b.X - (b.Slope * b.Top)) - (a.X - (a.Slope * a.Top))) / slope;
        return y > top + Epsilon && y < bottom - Epsilon;
    }

    /// <summary>Adds one edge, dropping the horizontal ones.</summary>
    /// <remarks>
    ///     ⚠ A horizontal edge is not skipped for cheapness. It spans no scanline, so it has no
    ///     crossing to contribute; keeping it would divide by a zero height to find one.
    /// </remarks>
    static void AddEdge(List<FillEdge> edges, Vector2 from, Vector2 to) {
        if (MathF.Abs(to.Y - from.Y) <= Epsilon) {
            return;
        }

        var down = to.Y > from.Y;
        var top = down ? from : to;
        var bottom = down ? to : from;

        edges.Add(
            new FillEdge(
                top.Y,
                bottom.Y,
                top.X,
                (bottom.X - top.X) / (bottom.Y - top.Y),
                down ? 1 : -1
            )
        );
    }

    /// <summary>One non-horizontal edge, as the sweep wants it.</summary>
    /// <param name="Top">The smaller y.</param>
    /// <param name="Bottom">The larger y.</param>
    /// <param name="X">Its x at <paramref name="Top" />.</param>
    /// <param name="Slope">How far x moves per unit of y.</param>
    /// <param name="Winding">+1 if the contour ran downwards through it, -1 if upwards.</param>
    readonly record struct FillEdge(float Top, float Bottom, float X, float Slope, int Winding) {
        /// <summary>Where this edge is at a given y.</summary>
        public float At(float y) => X + (Slope * (y - Top));
    }

    /// <summary>Where one edge cuts one band, at the sample line and at both boundaries.</summary>
    readonly record struct Crossing(float Middle, float Top, float Bottom, int Winding);

    // -------------------------------------------------------------- Stroke

    /// <summary>The default miter limit, which is CSS's and SVG's.</summary>
    public const float DefaultMiterLimit = 4f;

    /// <summary>Strokes contours.</summary>
    /// <param name="points">The flattened points.</param>
    /// <param name="contours">Which runs of them are contours.</param>
    /// <param name="width">How wide the line is. Centred on the path, half either side.</param>
    /// <param name="join">How corners are turned.</param>
    /// <param name="cap">How the ends of an open contour are finished.</param>
    /// <param name="tolerance">How far a round join or cap may sit from the true arc.</param>
    /// <param name="miterLimit">
    ///     How many times the half width a miter may reach before it becomes a bevel.
    /// </param>
    /// <param name="triangles">Where the triangles go, three points each. Not cleared.</param>
    /// <param name="fringe">
    ///     How far the antialiasing strip reaches past the stroke's outline. Zero draws none.
    /// </param>
    /// <remarks>
    ///     <para>
    ///         Each segment becomes a quad offset half a width either side, and the gap that leaves on
    ///         the outside of every turn becomes the join. Written that way round rather than as one
    ///         offset outline, because an offset outline of a self-intersecting path is a hard
    ///         problem and a stroke does not need it solved: overlapping quads paint the same colour
    ///         twice, which is invisible, where a mis-resolved outline is a hole.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The fringe is emitted per piece, so it overlaps wherever the pieces do</b> — on
    ///         the inside of every turn, where the two segment quads already lie on top of each other.
    ///         That is invisible for an opaque stroke and only for an opaque one: a ramp from one to
    ///         zero in the same colour over a pixel already painted that colour leaves it exactly as
    ///         it was. At a partial alpha it is a faint bright line down the inside of each corner.
    ///         The alternative is resolving the union of the pieces into a single outline, which is
    ///         the offset-curve problem the paragraph above declines to solve, so this is the same
    ///         trade taken one step further and it is worth knowing where it stops being free.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Only the outside of a turn is filled.</b> The inside is already covered twice over
    ///         by the two segment quads, and adding geometry there would do nothing except at a
    ///         partial alpha, where it would show as a bright notch on every corner.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A closed contour joins at the seam and is not capped.</b> That is the whole reason
    ///         <see cref="Contour.Closed" /> is carried through flattening — a rectangle stroked as an
    ///         open contour has a notch in one corner and two butt ends meeting in it.
    ///     </para>
    /// </remarks>
    public static void Stroke(
        IReadOnlyList<Vector2> points,
        IReadOnlyList<Contour> contours,
        float width,
        LineJoin join,
        LineCap cap,
        float tolerance,
        List<PathVertex> triangles,
        float miterLimit = DefaultMiterLimit,
        float fringe = 0f
    ) {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(contours);
        ArgumentNullException.ThrowIfNull(triangles);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tolerance);

        if (width <= 0) {
            return;
        }

        var half = width * 0.5f;

        foreach (var contour in contours) {
            if (contour.Count < 2) {
                continue;
            }

            // ⚠ A "closed" contour of two points is a line drawn there and back. Stroked as closed it
            // would ask for a join at a 180° turn at both ends, which has no miter and no bevel; as
            // open it is the same picture with two caps, which is what it looks like.
            var closed = contour.Closed && contour.Count >= 3;
            var last = closed ? contour.Count : contour.Count - 1;

            for (var i = 0; i < last; i++) {
                var from = points[contour.First + i];
                var to = points[contour.First + ((i + 1) % contour.Count)];
                Segment(triangles, from, to, half, fringe);
            }

            var first = closed ? 0 : 1;

            for (var i = first; i < last; i++) {
                var previous = points[contour.First + ((i + contour.Count - 1) % contour.Count)];
                var vertex = points[contour.First + i];
                var next = points[contour.First + ((i + 1) % contour.Count)];
                Join(triangles, previous, vertex, next, half, join, tolerance, miterLimit, fringe);
            }

            if (closed) {
                continue;
            }

            var start = points[contour.First];
            var second = points[contour.First + 1];
            var end = points[contour.First + contour.Count - 1];
            var penultimate = points[contour.First + contour.Count - 2];

            Cap(triangles, start, Direction(second, start), half, cap, tolerance, fringe);
            Cap(triangles, end, Direction(penultimate, end), half, cap, tolerance, fringe);
        }
    }

    /// <summary>One segment's quad.</summary>
    static void Segment(List<PathVertex> triangles, Vector2 from, Vector2 to, float half, float fringe) {
        var direction = Direction(from, to);

        if (direction == Vector2.Zero) {
            return;
        }

        var unit = Perpendicular(direction);
        var normal = unit * half;

        Triangle(triangles, from + normal, to + normal, to - normal);
        Triangle(triangles, from + normal, to - normal, from - normal);

        if (fringe > 0) {
            Feather(triangles, from + normal, to + normal, unit, fringe);
            Feather(triangles, from - normal, to - normal, -unit, fringe);
        }
    }

    /// <summary>The wedge on the outside of one corner.</summary>
    static void Join(
        List<PathVertex> triangles,
        Vector2 previous,
        Vector2 vertex,
        Vector2 next,
        float half,
        LineJoin join,
        float tolerance,
        float miterLimit,
        float fringe
    ) {
        var incoming = Direction(previous, vertex);
        var outgoing = Direction(vertex, next);

        if (incoming == Vector2.Zero || outgoing == Vector2.Zero) {
            return;
        }

        var turn = (incoming.X * outgoing.Y) - (incoming.Y * outgoing.X);

        if (MathF.Abs(turn) <= Epsilon) {
            // Straight through, or doubled back. Straight needs nothing. A reversal has no outside to
            // fill — the two quads lie on top of each other — and a round join there would be a full
            // disc, which is what a round *cap* is for and is not what a join means.
            return;
        }

        // Which side the turn leaves uncovered. The two quads overlap on the inside of the corner, so
        // the wedge belongs to the other one.
        var side = turn > 0 ? -half : half;
        var from = vertex + (Perpendicular(incoming) * side);
        var to = vertex + (Perpendicular(outgoing) * side);

        switch (join) {
            case LineJoin.Bevel:
                Bevel(triangles, vertex, from, to, fringe);
                break;

            case LineJoin.Round: {
                // The wedge is the shorter way round by construction — a join turns by less than a
                // half turn, or it is the reversal handled above — so the signed angle between the
                // two offset points is the sweep.
                var start = from - vertex;
                var finish = to - vertex;

                var sweep = MathF.Atan2(
                    (start.X * finish.Y) - (start.Y * finish.X),
                    (start.X * finish.X) + (start.Y * finish.Y)
                );

                Arc(triangles, vertex, from, sweep, MathF.Abs(side), tolerance, fringe);
                break;
            }

            default: {
                var bisector = from + to - (vertex * 2);
                var length = bisector.Length();

                if (length <= Epsilon) {
                    Bevel(triangles, vertex, from, to, fringe);
                    break;
                }

                bisector *= 1f / length;

                // How far the meeting point is from the corner, over the half width. As the turn
                // approaches a reversal the two offset edges become parallel and this runs away, which
                // is the spike the limit exists to stop.
                var cosine = Vector2.Dot(bisector, Perpendicular(incoming) * MathF.Sign(side));
                var reach = MathF.Abs(cosine) <= Epsilon ? float.PositiveInfinity : 1f / MathF.Abs(cosine);

                if (reach > miterLimit) {
                    Bevel(triangles, vertex, from, to, fringe);
                    break;
                }

                var tip = vertex + (bisector * (MathF.Abs(side) * reach));
                Triangle(triangles, vertex, from, tip);
                Triangle(triangles, vertex, tip, to);

                if (fringe > 0) {
                    FeatherFrom(triangles, from, tip, vertex, fringe);
                    FeatherFrom(triangles, tip, to, vertex, fringe);
                }

                break;
            }
        }
    }

    /// <summary>What finishes one end of an open contour, given the way the stroke is leaving.</summary>
    static void Cap(
        List<PathVertex> triangles,
        Vector2 end,
        Vector2 direction,
        float half,
        LineCap cap,
        float tolerance,
        float fringe
    ) {
        if (direction == Vector2.Zero) {
            return;
        }

        var normal = Perpendicular(direction) * half;

        if (cap == LineCap.Butt) {
            // ⚠ Nothing to draw and still something to feather. A butt cap is a flat end, which is a
            // boundary like any other — left unfeathered it is the one hard edge on an otherwise
            // smooth line, and on a nearly-horizontal line it is the most visible one.
            if (fringe > 0) {
                Feather(triangles, end + normal, end - normal, -direction, fringe);
            }

            return;
        }

        if (cap == LineCap.Square) {
            var reach = direction * half;
            Triangle(triangles, end + normal, end + normal + reach, end - normal + reach);
            Triangle(triangles, end + normal, end - normal + reach, end - normal);

            if (fringe > 0) {
                var centre = end + (reach * 0.5f);
                FeatherFrom(triangles, end + normal, end + normal + reach, centre, fringe);
                FeatherFrom(triangles, end + normal + reach, end - normal + reach, centre, fringe);
                FeatherFrom(triangles, end - normal + reach, end - normal, centre, fringe);
            }

            return;
        }

        // ⚠ A half turn, and the sign is written down rather than derived. Asking for the arc between
        // the two offset points would work out the direction from their cross product — which for
        // opposite points is exactly zero, so the answer would come from which side of zero the
        // rounding fell on, and the wrong side draws the cap back over the line it caps. Starting at
        // −normal and sweeping +π passes through the direction the stroke is leaving in, always.
        Arc(triangles, end, end - normal, MathF.PI, half, tolerance, fringe);
    }

    /// <summary>The bevel triangle a join falls back to, with its outer edge feathered.</summary>
    static void Bevel(List<PathVertex> triangles, Vector2 vertex, Vector2 from, Vector2 to, float fringe) {
        Triangle(triangles, vertex, from, to);

        if (fringe > 0) {
            FeatherFrom(triangles, from, to, vertex, fringe);
        }
    }

    /// <summary>
    ///     A fan from a centre, sweeping a signed angle — positive anticlockwise in the coordinates as
    ///     written — from a point on its circle.
    /// </summary>
    static void Arc(
        List<PathVertex> triangles,
        Vector2 centre,
        Vector2 from,
        float sweep,
        float radius,
        float tolerance,
        float fringe
    ) {
        if (MathF.Abs(sweep) <= Epsilon) {
            return;
        }

        var start = from - centre;

        // ⚠ The step comes from the radius, not from a constant. A chord of a circle of radius r
        // subtending θ sits r(1 − cos(θ/2)) from the arc, so the angle a tolerance allows shrinks as
        // the stroke gets wider — a fixed count is faceted on a thick line and wasteful on a hairline.
        var ratio = Math.Clamp(1f - (tolerance / MathF.Max(radius, Epsilon)), -1f, 1f);
        var step = 2f * MathF.Acos(ratio);
        var count = Math.Clamp((int)MathF.Ceiling(MathF.Abs(sweep) / MathF.Max(step, 1e-3f)), 1, MaxArcSteps);

        var previous = from;

        for (var i = 1; i <= count; i++) {
            var angle = sweep * i / count;
            var cos = MathF.Cos(angle);
            var sin = MathF.Sin(angle);
            var point = centre + new Vector2((start.X * cos) - (start.Y * sin), (start.X * sin) + (start.Y * cos));

            Triangle(triangles, centre, previous, point);

            if (fringe > 0) {
                FeatherFrom(triangles, previous, point, centre, fringe);
            }

            previous = point;
        }
    }

    static void Triangle(List<PathVertex> triangles, Vector2 a, Vector2 b, Vector2 c) {
        triangles.Add(new PathVertex(a, 1f));
        triangles.Add(new PathVertex(b, 1f));
        triangles.Add(new PathVertex(c, 1f));
    }

    static Vector2 Direction(Vector2 from, Vector2 to) {
        var delta = to - from;
        var length = delta.Length();

        return length <= Epsilon ? Vector2.Zero : delta * (1f / length);
    }

    static Vector2 Perpendicular(Vector2 value) => new(-value.Y, value.X);
}
