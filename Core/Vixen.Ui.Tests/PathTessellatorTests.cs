// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui.Rendering;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>
///     Curves in, triangles out — checked against what the shape means rather than against a
///     transcript of what the tessellator did.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Two oracles carry almost all of this, and neither knows how the tessellator works.</b>
///         A fill is right when a point is covered by a triangle exactly when the winding rule says it
///         is inside, which is the definition of a fill and not a restatement of the algorithm. A
///         stroke with round joins and round caps is right when a point is covered exactly when it is
///         within half a width of the path — the Minkowski sum of the polyline with a disc, which is
///         what "a line of that thickness" means and is available in closed form.
///     </para>
///     <para>
///         Both sample away from the boundary. A point within a fraction of a pixel of the edge is one
///         where the flattened polygon, the true curve and the tessellation are all allowed to
///         disagree, and asserting there would be asserting the rounding.
///     </para>
/// </remarks>
public class PathTessellatorTests {
    const float Tolerance = 0.05f;

    // ------------------------------------------------------------ Flattening

    [Fact]
    public void A_closed_contour_says_so_and_an_open_one_does_not() {
        var closed = Flatten(new PathBuilder().AddRectangle(new Rectangle(0, 0, 10, 10)));
        var open = Flatten(new PathBuilder().MoveTo(new Vector2(0, 0)).LineTo(new Vector2(10, 0)));

        Assert.True(Assert.Single(closed.Contours).Closed);
        Assert.False(Assert.Single(open.Contours).Closed);
    }

    /// <summary>
    ///     ⚠ A contour that walks back to where it started before closing must not keep that point
    ///     twice. The duplicate is a zero-length edge at the seam, which has no direction — so a
    ///     stroke asks it for a normal, gets a NaN, and takes the whole contour with it.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Written against <c>AddRectangle</c> first, where it asserted nothing.</b> That helper
    ///     never draws back to its start — <c>Close</c> carries the closing point rather than
    ///     emitting one — so the duplicate the flattener removes was never there to remove, and
    ///     deleting the removal broke no test. The shape that needs it is the one an imported SVG
    ///     produces: an explicit line home, and then a close.
    /// </remarks>
    [Fact]
    public void Closing_after_walking_home_does_not_leave_a_duplicate_point() {
        var start = new Vector2(0, 0);

        var path = Flatten(
            new PathBuilder()
                .MoveTo(start)
                .LineTo(new Vector2(10, 0))
                .LineTo(new Vector2(10, 10))
                .LineTo(start)
                .Close()
        );

        var contour = Assert.Single(path.Contours);

        Assert.True(contour.Closed);
        Assert.Equal(3, contour.Count);
        Assert.NotEqual(path.Points[contour.First], path.Points[contour.First + contour.Count - 1]);
    }

    [Fact]
    public void Closing_without_walking_home_keeps_every_corner() {
        var path = Flatten(new PathBuilder().AddRectangle(new Rectangle(0, 0, 10, 10)));
        var contour = Assert.Single(path.Contours);

        Assert.Equal(4, contour.Count);
    }

    /// <summary>
    ///     ⚠ The point of keeping curves as curves: the same path flattens differently at two
    ///     tolerances, so a shape drawn at two zoom levels is right at both.
    /// </summary>
    [Fact]
    public void A_tighter_tolerance_subdivides_a_curve_more_finely() {
        var path = new PathBuilder().AddEllipse(new Rectangle(0, 0, 100, 100));

        var coarse = Flatten(path, tolerance: 4f);
        var fine = Flatten(path, tolerance: 0.01f);

        Assert.True(fine.Points.Count > coarse.Points.Count * 3);
    }

    [Fact]
    public void Several_contours_of_one_path_are_kept_apart() {
        var path = new PathBuilder()
            .AddRectangle(new Rectangle(0, 0, 40, 40))
            .AddRectangle(new Rectangle(10, 10, 20, 20));

        Assert.Equal(2, Flatten(path).Contours.Count);
    }

    // ------------------------------------------------------------------ Fill

    [Fact]
    public void A_square_fills_its_own_area() {
        var triangles = Fill(new PathBuilder().AddRectangle(new Rectangle(10, 20, 60, 40)));

        Assert.Equal(60 * 40, Area(triangles), 1);
    }

    [Fact]
    public void A_circle_fills_the_area_a_circle_has() {
        var triangles = Fill(new PathBuilder().AddEllipse(new Rectangle(0, 0, 100, 100)));

        // Slightly under πr², because a flattened circle is the inscribed polygon.
        Assert.Equal(MathF.PI * 50 * 50, Area(triangles), 5f);
    }

    /// <summary>
    ///     The fill oracle: covered by a triangle exactly where the winding rule says inside.
    /// </summary>
    [Theory]
    [InlineData(PathFillRule.NonZero)]
    [InlineData(PathFillRule.EvenOdd)]
    public void A_ring_agrees_with_the_winding_rule_everywhere(PathFillRule rule) {
        // Two circles, the inner one wound the same way as the outer. Under either rule this is a
        // shape with a hole under EvenOdd and a solid disc under NonZero, which is the whole reason
        // the rule is part of the batch key.
        var path = new PathBuilder()
            .AddEllipse(new Rectangle(0, 0, 120, 120))
            .AddEllipse(new Rectangle(30, 30, 60, 60));

        AssertFillAgrees(path, rule);
    }

    /// <summary>
    ///     ⚠ The shape ear clipping cannot take: a five-pointed star drawn as five lines crosses
    ///     itself, and the two fill rules disagree about its middle. This is what a sweep buys.
    /// </summary>
    [Theory]
    [InlineData(PathFillRule.NonZero)]
    [InlineData(PathFillRule.EvenOdd)]
    public void A_self_crossing_star_agrees_with_the_winding_rule_everywhere(PathFillRule rule) =>
        AssertFillAgrees(Star(), rule);

    [Fact]
    public void The_two_fill_rules_disagree_about_the_middle_of_a_star() {
        var nonZero = Area(Fill(Star()));
        var evenOdd = Area(Fill(Star(), PathFillRule.EvenOdd));

        // The pentagon in the middle is wound twice, so NonZero fills it and EvenOdd does not.
        Assert.True(nonZero > evenOdd * 1.2f, $"non-zero {nonZero}, even-odd {evenOdd}");
    }

    [Fact]
    public void An_open_contour_is_closed_for_filling() {
        // Three of a square's four sides. A fill has to close it, or the scanline runs out of the
        // shape and fills to whatever it meets next.
        var open = new PathBuilder()
            .MoveTo(new Vector2(0, 0))
            .LineTo(new Vector2(40, 0))
            .LineTo(new Vector2(40, 40))
            .LineTo(new Vector2(0, 40));

        Assert.Equal(40 * 40, Area(Fill(open)), 1);
    }

    [Fact]
    public void A_contour_of_two_points_fills_nothing() {
        var line = new PathBuilder().MoveTo(new Vector2(0, 0)).LineTo(new Vector2(40, 40));

        Assert.Empty(Fill(line));
    }

    // ---------------------------------------------------------------- Fringe

    /// <summary>
    ///     The fringe lies outside the shape and reaches exactly as far as it was asked to.
    /// </summary>
    [Fact]
    public void A_squares_fringe_is_a_band_outside_it() {
        const float Width = 2f;

        var fringe = Fringe(new PathBuilder().AddRectangle(new Rectangle(20, 20, 60, 60)), width: Width);

        // Just outside the right edge: in the band. Further out: past it. Inside: not the fringe's,
        // because the fringe starts *on* the outline and goes out.
        Assert.True(Covered(fringe, new Vector2(81, 50)), "the band is missing just outside the edge");
        Assert.False(Covered(fringe, new Vector2(84, 50)), "the band reached further than it was told to");
        Assert.False(Covered(fringe, new Vector2(70, 50)), "the band was drawn inside the shape");
    }

    /// <summary>
    ///     ⚠ The shape that decides whether "outward" was derived or asked. Under even-odd, an inner
    ///     contour wound the <i>same way</i> as its outer one is still a hole — so the fringe around
    ///     it has to point into the hole. Taking the direction from the contour's own winding gets
    ///     this exactly backwards and draws a bright band around every counter in an icon set.
    /// </summary>
    [Fact]
    public void The_fringe_around_a_hole_points_into_the_hole() {
        const float Width = 2f;

        // Both wound the same way, so nothing about the inner contour says "hole" except the rule.
        var ring = new PathBuilder()
            .AddEllipse(new Rectangle(0, 0, 120, 120))
            .AddEllipse(new Rectangle(35, 35, 50, 50));

        var fringe = Fringe(ring, PathFillRule.EvenOdd, Width);
        var centre = new Vector2(60, 60);

        // The hole's edge is 25 out from the centre. One unit inside it is in the band; one unit
        // outside it is in the ring's material and must not be.
        Assert.True(Covered(fringe, centre + new Vector2(24f, 0)), "the hole has no fringe");
        Assert.False(Covered(fringe, centre + new Vector2(27f, 0)), "the fringe was drawn into the ring");
        Assert.False(Covered(fringe, centre + new Vector2(20f, 0)), "the fringe reached too far into the hole");
    }

    /// <summary>
    ///     ⚠ A self-crossing contour has edges that are boundary along part of their length and
    ///     interior along the rest, so each one is cut where anything crosses it before being asked
    ///     which it is. Probed once at the midpoint, a pentagram's chords all read "interior" — the
    ///     midpoint is inside the pentagon — and the star comes out with no antialiased edge at all.
    /// </summary>
    [Fact]
    public void A_self_crossing_star_is_feathered_on_its_outer_edges_and_not_inside() {
        var fringe = Fringe(Star(), width: 2f);

        Assert.NotEmpty(fringe);

        // The middle of the pentagram is deep inside the shape under non-zero, so nothing there.
        Assert.False(Covered(fringe, new Vector2(70, 70)), "the fringe was drawn through the middle");

        // A point just outside the tip at the top is in the band. The star's first point is at
        // (70, 10), so a couple of units above it is outside the shape and inside the fringe.
        Assert.True(
            fringe.Any(vertex => Vector2.Distance(vertex.Position, new Vector2(70, 10)) < 3f),
            "the tip has no fringe"
        );
    }

    [Fact]
    public void The_fringe_runs_from_full_coverage_to_none() {
        var fringe = Fringe(new PathBuilder().AddRectangle(new Rectangle(0, 0, 40, 40)), width: 1f);

        Assert.Contains(fringe, vertex => vertex.Coverage == 1f);
        Assert.Contains(fringe, vertex => vertex.Coverage == 0f);
        Assert.All(fringe, vertex => Assert.InRange(vertex.Coverage, 0f, 1f));
    }

    [Fact]
    public void The_interior_is_fully_covered() =>
        Assert.All(Fill(new PathBuilder().AddEllipse(new Rectangle(0, 0, 40, 40))),
            vertex => Assert.Equal(1f, vertex.Coverage));

    [Fact]
    public void A_zero_width_fringe_draws_nothing() =>
        Assert.Empty(Fringe(new PathBuilder().AddRectangle(new Rectangle(0, 0, 40, 40)), width: 0f));

    /// <summary>
    ///     ⚠ The corner wedges. Without them every convex corner of every icon has a notch out of it
    ///     the size of the fringe, which is the artefact the fringe exists to remove.
    /// </summary>
    [Fact]
    public void A_corner_is_closed_rather_than_left_as_a_notch() {
        const float Width = 4f;

        var fringe = Fringe(new PathBuilder().AddRectangle(new Rectangle(20, 20, 60, 60)), width: Width);

        // Diagonally out from the top-right corner, where two strips would otherwise leave a wedge.
        Assert.True(Covered(fringe, new Vector2(81.5f, 18.5f)), "the corner was left open");
    }

    // ---------------------------------------------------------------- Stroke

    /// <summary>
    ///     The stroke oracle. With round joins and round caps the stroked region <i>is</i> the set of
    ///     points within half a width of the path, so every sample can be checked in closed form.
    /// </summary>
    [Fact]
    public void A_round_stroke_covers_exactly_what_is_within_half_a_width() {
        var path = new PathBuilder()
            .MoveTo(new Vector2(30, 30))
            .LineTo(new Vector2(90, 40))
            .LineTo(new Vector2(60, 100))
            .LineTo(new Vector2(110, 120));

        AssertStrokeIsTheDiscSum(path, width: 16, closed: false);
    }

    [Fact]
    public void A_round_stroke_of_a_closed_contour_covers_exactly_the_same_set() {
        var path = new PathBuilder()
            .MoveTo(new Vector2(40, 40))
            .LineTo(new Vector2(120, 50))
            .LineTo(new Vector2(90, 120))
            .Close();

        AssertStrokeIsTheDiscSum(path, width: 14, closed: true);
    }

    /// <summary>
    ///     ⚠ A closed contour joins at the seam rather than being capped, which is what
    ///     <c>Contour.Closed</c> is carried through flattening for. If the caps were applied anyway,
    ///     changing the cap would change the geometry.
    /// </summary>
    [Fact]
    public void A_closed_contour_is_not_capped() {
        var path = new PathBuilder().AddRectangle(new Rectangle(10, 10, 50, 30));

        var butt = Stroke(path, width: 6, cap: LineCap.Butt);
        var square = Stroke(path, width: 6, cap: LineCap.Square);

        Assert.Equal(butt, square);
    }

    [Fact]
    public void An_open_contour_is_capped_and_the_cap_is_the_one_asked_for() {
        var path = new PathBuilder().MoveTo(new Vector2(20, 50)).LineTo(new Vector2(80, 50));

        var butt = Area(Stroke(path, width: 10, cap: LineCap.Butt));
        var square = Area(Stroke(path, width: 10, cap: LineCap.Square));
        var round = Area(Stroke(path, width: 10, cap: LineCap.Round));

        Assert.Equal(60 * 10, butt, 1);

        // A square cap adds half a width at each end.
        Assert.Equal(butt + (10 * 10), square, 1);

        // ⚠ A round cap adds half a disc at each end and lands slightly *under* it, because the arc
        // is the inscribed polygon rather than the circle. Asserted as a bound rather than an
        // approximate equality, so the direction of the error is part of the claim: an arc that came
        // out larger than the disc it approximates would be an arc drawn the wrong way round.
        Assert.InRange(round, butt + (MathF.PI * 25) - 1.5f, butt + (MathF.PI * 25));
    }

    /// <summary>
    ///     ⚠ Without a limit, a miter on a nearly-doubled-back turn runs off to infinity. The spike is
    ///     the classic stroke artefact and it is one missing comparison away.
    /// </summary>
    [Fact]
    public void A_sharp_turn_falls_back_to_a_bevel_rather_than_growing_a_spike() {
        // A turn of about 173°, whose miter would reach roughly 16 times the half width.
        var path = new PathBuilder()
            .MoveTo(new Vector2(0, 0))
            .LineTo(new Vector2(100, 0))
            .LineTo(new Vector2(0, 12));

        var limited = Stroke(path, width: 8, join: LineJoin.Miter);
        var reach = limited.Max(vertex => vertex.Position.X) - 100;

        Assert.True(reach < 4 * 4, $"the miter reached {reach} past the corner");

        // ...and it is the limit doing it, not the geometry: raised past what this corner needs, the
        // same corner grows the spike.
        var unlimited = Stroke(path, width: 8, join: LineJoin.Miter, miterLimit: 64);
        Assert.True(unlimited.Max(vertex => vertex.Position.X) - 100 > 40);
    }

    [Fact]
    public void A_gentle_turn_keeps_its_miter() {
        var path = new PathBuilder()
            .MoveTo(new Vector2(0, 0))
            .LineTo(new Vector2(100, 0))
            .LineTo(new Vector2(200, 100));

        var bevel = Area(Stroke(path, width: 10, join: LineJoin.Bevel));
        var miter = Area(Stroke(path, width: 10, join: LineJoin.Miter));

        Assert.True(miter > bevel, $"miter {miter} should cover more than bevel {bevel}");
    }

    [Fact]
    public void A_straight_joint_adds_no_join_geometry() {
        var bent = new PathBuilder()
            .MoveTo(new Vector2(0, 0))
            .LineTo(new Vector2(50, 0))
            .LineTo(new Vector2(100, 0));

        var straight = new PathBuilder().MoveTo(new Vector2(0, 0)).LineTo(new Vector2(100, 0));

        Assert.Equal(Area(Stroke(straight, 10)), Area(Stroke(bent, 10)), 0.01f);
    }

    [Fact]
    public void A_stroke_of_zero_width_draws_nothing() =>
        Assert.Empty(Stroke(new PathBuilder().MoveTo(Vector2.Zero).LineTo(new Vector2(10, 0)), 0));

    // --------------------------------------------------------------- Oracles

    /// <summary>Every sample point is covered exactly when the rule calls it inside.</summary>
    static void AssertFillAgrees(PathBuilder path, PathFillRule rule) {
        var flat = Flatten(path);
        var triangles = Fill(path, rule);
        var edges = Edges(flat);

        var (min, max) = Bounds(flat.Points);
        var checked_ = 0;

        for (var y = min.Y - 4; y <= max.Y + 4; y += 1.5f) {
            for (var x = min.X - 4; x <= max.X + 4; x += 1.5f) {
                var point = new Vector2(x, y);

                // Near an edge the flattened polygon and its tessellation are both allowed to round
                // either way, so nothing is asserted there.
                if (edges.Min(edge => DistanceToSegment(point, edge.From, edge.To)) < 0.35f) {
                    continue;
                }

                var inside = rule == PathFillRule.EvenOdd
                    ? (Winding(edges, point) & 1) != 0
                    : Winding(edges, point) != 0;

                Assert.Equal(inside, Covered(triangles, point));
                checked_++;
            }
        }

        Assert.True(checked_ > 1000, $"only {checked_} points were checked");
    }

    /// <summary>
    ///     The stroked region is the polyline grown by half a width in every direction, which is what
    ///     round joins and round caps mean.
    /// </summary>
    static void AssertStrokeIsTheDiscSum(PathBuilder path, float width, bool closed) {
        var flat = Flatten(path);
        var triangles = Stroke(path, width, LineJoin.Round, LineCap.Round);
        var half = width / 2;

        var segments = new List<(Vector2 From, Vector2 To)>();
        var contour = Assert.Single(flat.Contours);
        Assert.Equal(closed, contour.Closed);

        var last = closed ? contour.Count : contour.Count - 1;

        for (var i = 0; i < last; i++) {
            segments.Add((flat.Points[contour.First + i], flat.Points[contour.First + ((i + 1) % contour.Count)]));
        }

        var (min, max) = Bounds(flat.Points);
        var checked_ = 0;

        for (var y = min.Y - width; y <= max.Y + width; y += 1.5f) {
            for (var x = min.X - width; x <= max.X + width; x += 1.5f) {
                var point = new Vector2(x, y);
                var distance = segments.Min(segment => DistanceToSegment(point, segment.From, segment.To));

                // The arcs are inscribed polygons, so a band around the boundary is where the two are
                // allowed to differ — by at most the flattening tolerance.
                if (MathF.Abs(distance - half) < 0.4f) {
                    continue;
                }

                Assert.Equal(distance < half, Covered(triangles, point));
                checked_++;
            }
        }

        Assert.True(checked_ > 1000, $"only {checked_} points were checked");
    }

    // --------------------------------------------------------------- Helpers

    sealed record FlatPath(List<Vector2> Points, List<Contour> Contours);

    static FlatPath Flatten(PathBuilder path, float tolerance = Tolerance) {
        var points = new List<Vector2>();
        var contours = new List<Contour>();
        PathFlattener.Flatten(path.Segments, 0, path.Count, tolerance, points, contours);

        return new FlatPath(points, contours);
    }

    static List<PathVertex> Fill(PathBuilder path, PathFillRule rule = PathFillRule.NonZero) {
        var flat = Flatten(path);
        var triangles = new List<PathVertex>();
        PathTessellator.Fill(flat.Points, flat.Contours, rule, triangles);

        return triangles;
    }

    static List<PathVertex> Fringe(PathBuilder path, PathFillRule rule = PathFillRule.NonZero, float width = 0.5f) {
        var flat = Flatten(path);
        var triangles = new List<PathVertex>();
        PathTessellator.FillFringe(flat.Points, flat.Contours, rule, width, triangles);

        return triangles;
    }

    static List<PathVertex> Stroke(
        PathBuilder path,
        float width,
        LineJoin join = LineJoin.Miter,
        LineCap cap = LineCap.Butt,
        float miterLimit = PathTessellator.DefaultMiterLimit
    ) {
        var flat = Flatten(path);
        var triangles = new List<PathVertex>();
        PathTessellator.Stroke(flat.Points, flat.Contours, width, join, cap, Tolerance, triangles, miterLimit);

        return triangles;
    }

    /// <summary>Every edge of every contour, each contour closed as a fill closes it.</summary>
    static List<(Vector2 From, Vector2 To)> Edges(FlatPath path) {
        var edges = new List<(Vector2, Vector2)>();

        foreach (var contour in path.Contours) {
            for (var i = 0; i < contour.Count; i++) {
                edges.Add((
                    path.Points[contour.First + i],
                    path.Points[contour.First + ((i + 1) % contour.Count)]
                ));
            }
        }

        return edges;
    }

    /// <summary>The winding number of a point, by casting a ray along +x.</summary>
    static int Winding(List<(Vector2 From, Vector2 To)> edges, Vector2 point) {
        var winding = 0;

        foreach (var (from, to) in edges) {
            if (from.Y <= point.Y == to.Y <= point.Y) {
                continue;
            }

            var t = (point.Y - from.Y) / (to.Y - from.Y);

            if (from.X + (t * (to.X - from.X)) > point.X) {
                winding += to.Y > from.Y ? 1 : -1;
            }
        }

        return winding;
    }

    /// <summary>Whether any emitted triangle contains the point.</summary>
    static bool Covered(List<PathVertex> triangles, Vector2 point) {
        for (var i = 0; i + 2 < triangles.Count; i += 3) {
            if (InTriangle(point, triangles[i].Position, triangles[i + 1].Position, triangles[i + 2].Position)) {
                return true;
            }
        }

        return false;
    }

    static bool InTriangle(Vector2 point, Vector2 a, Vector2 b, Vector2 c) {
        var first = Side(point, a, b);
        var second = Side(point, b, c);
        var third = Side(point, c, a);

        // Closed, so a point on a shared edge of two triangles belongs to both rather than neither.
        return (first >= 0 && second >= 0 && third >= 0) || (first <= 0 && second <= 0 && third <= 0);
    }

    static float Side(Vector2 point, Vector2 from, Vector2 to) =>
        ((to.X - from.X) * (point.Y - from.Y)) - ((to.Y - from.Y) * (point.X - from.X));

    static float DistanceToSegment(Vector2 point, Vector2 from, Vector2 to) {
        var along = to - from;
        var lengthSquared = along.LengthSquared();

        if (lengthSquared <= 1e-12f) {
            return Vector2.Distance(point, from);
        }

        var t = Math.Clamp(Vector2.Dot(point - from, along) / lengthSquared, 0f, 1f);
        return Vector2.Distance(point, from + (along * t));
    }

    /// <summary>The signed area of the triangles, which for a tiling is the area they cover.</summary>
    static float Area(List<PathVertex> triangles) {
        var total = 0f;

        for (var i = 0; i + 2 < triangles.Count; i += 3) {
            total += MathF.Abs(Side(triangles[i + 2].Position, triangles[i].Position, triangles[i + 1].Position)) * 0.5f;
        }

        return total;
    }

    static (Vector2 Min, Vector2 Max) Bounds(List<Vector2> points) {
        var min = new Vector2(float.MaxValue, float.MaxValue);
        var max = new Vector2(float.MinValue, float.MinValue);

        foreach (var point in points) {
            min = new Vector2(MathF.Min(min.X, point.X), MathF.Min(min.Y, point.Y));
            max = new Vector2(MathF.Max(max.X, point.X), MathF.Max(max.Y, point.Y));
        }

        return (min, max);
    }

    /// <summary>A five-pointed star as five lines, which crosses itself.</summary>
    static PathBuilder Star() {
        var path = new PathBuilder();

        for (var i = 0; i < 5; i++) {
            // Every second point, which is what turns a pentagon into a pentagram.
            var angle = (i * 4 * MathF.PI / 5) - (MathF.PI / 2);
            var point = new Vector2(70 + (60 * MathF.Cos(angle)), 70 + (60 * MathF.Sin(angle)));

            if (i == 0) {
                path.MoveTo(point);
            } else {
                path.LineTo(point);
            }
        }

        return path.Close();
    }
}
