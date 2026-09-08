// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.Texturing.Painting;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>
///     The curve a pen gesture places, and the positions a stroke follows it through.
/// </summary>
/// <remarks>
///     <para>
///         <b>Doc 48 § D13's "curve/path strokes", and
///         <a href="https://github.com/Rikarin/Vixen/issues/1084">#1084</a>'s account of why the
///         sampler was not the hard part.</b> <c>BrushStroke.MoveTo</c> interpolates straight, so the
///         claim that matters is that the positions this hands it are close enough together that
///         the chords between them are not visible — which is a distance in texels, measured against
///         a densely sampled curve rather than against a second implementation of the same one.
///     </para>
///     <para>
///         ⚠ <b>Asserted at three sizes, because a tolerance is exactly the kind of number that
///         stops holding at scale.</b> A sampler that subdivided a fixed number of times, or that
///         measured its error as a fraction of the segment, passes on a path the size of a test
///         fixture and paints a visible polygon on a path the size of an atlas. The three here span
///         four orders of magnitude, which is a stroke across four texels and a stroke across four
///         thousand.
///     </para>
/// </remarks>
public class PaintPathTests {
    /// <summary>The polyline stays inside the tolerance of the curve, at every size.</summary>
    /// <remarks>
    ///     ⚠ <b>What this cannot distinguish, said plainly: the oracle is the same evaluator at a
    ///     thousandth of the tolerance, so a <em>wrong curve</em> would satisfy it.</b> What it does
    ///     measure is the subdivision — how far the polyline a stroke is actually moved through may
    ///     sit from the curve the artist was shown — which is the claim that scales and therefore the
    ///     claim worth three sizes. The curve's own shape is pinned by the two assertions that need
    ///     no oracle at all: it passes through every point that was clicked, and a straight path
    ///     stays straight.
    /// </remarks>
    [Theory]
    [InlineData(4f)]
    [InlineData(120f)]
    [InlineData(4000f)]
    public void The_sampled_polyline_stays_within_the_tolerance_at_every_size(float size) {
        var path = Curve(size);

        List<Vector2> positions = [];

        path.Sample(positions, PaintPath.Tolerance);

        Assert.True(positions.Count >= path.Points.Count, "the sampler dropped one of the points.");
        Assert.Equal(path.Points[0], positions[0]);
        Assert.Equal(path.Points[^1], positions[^1]);

        // ⚠ It interpolates: the curve goes through every point that was clicked. A B-spline through
        // the same control points is smooth, plausible and misses all of them, and an artist placing
        // a stroke down the middle of a shape would find the paint beside it.
        var dense = Dense(path);

        Assert.All(
            path.Points,
            clicked => Assert.True(
                dense.Any(at => (at - clicked).Length() <= 1e-3f * size),
                $"the curve misses {clicked}, which somebody clicked."
            )
        );

        var worst = 0f;

        foreach (var point in dense) {
            var nearest = float.PositiveInfinity;

            for (var step = 1; step < positions.Count; step++) {
                nearest = MathF.Min(nearest, ToSegment(point, positions[step - 1], positions[step]));
            }

            worst = MathF.Max(worst, nearest);
        }

        Assert.True(
            worst <= PaintPath.Tolerance,
            $"a path {size} texels across strayed {worst:0.000} texels from its own curve, against a "
            + $"tolerance of {PaintPath.Tolerance}. A sampler whose error grows with the path is one "
            + "that was tested at one size."
        );

        // ⚠ The instrument. A sampler that simply emitted a great many points would satisfy the
        // bound above without following anything, and one that emitted the clicked points alone
        // would satisfy it on a straight path — so the count has to sit between the two, and the
        // straight case below is what says the subdivision is driven by curvature rather than by
        // the parameter.
        Assert.True(
            positions.Count < 4000,
            $"{positions.Count} positions for four points is a subdivision that is not converging."
        );
    }

    /// <summary>A path that is already straight is not subdivided at all.</summary>
    /// <remarks>
    ///     ⚠ <b>The half that says the tolerance is doing the work.</b> Three collinear points have
    ///     a curve that is their own chords, so a sampler driven by the deviation emits exactly them
    ///     — and one driven by the parameter, or by a fixed count, emits a hundred. It is also the
    ///     case that says a shift-click line and a two-point path paint the same stroke.
    /// </remarks>
    [Fact]
    public void A_straight_path_is_its_own_points_and_nothing_between_them() {
        PaintPath path = new();

        path.Add(new(10f, 10f));
        path.Add(new(60f, 10f));
        path.Add(new(110f, 10f));

        List<Vector2> positions = [];

        path.Sample(positions);

        Assert.Equal(3, positions.Count);
    }

    /// <summary>Two clicks in the same place do not make a curve of not-a-numbers.</summary>
    /// <remarks>
    ///     ⚠ <b>A double-click is how an artist produces one, so it is not a hypothetical.</b> The
    ///     centripetal parameterisation divides by the knot spacing at every level of the pyramid,
    ///     and a NaN out of it reaches a stamp centre — where it paints nothing anywhere, silently,
    ///     rather than throwing.
    /// </remarks>
    [Fact]
    public void Two_points_in_the_same_place_do_not_produce_a_curve_of_nans() {
        PaintPath path = new();

        path.Add(new(20f, 20f));
        path.Add(new(20f, 20f));
        path.Add(new(80f, 40f));

        List<Vector2> positions = [];

        path.Sample(positions);

        Assert.NotEmpty(positions);
        Assert.All(positions, at => Assert.True(float.IsFinite(at.X) && float.IsFinite(at.Y), $"{at}"));
    }

    /// <summary>A path is placed, taken back a point at a time, and forgotten.</summary>
    [Fact]
    public void A_path_is_placed_undone_and_cleared() {
        PaintPath path = new();

        Assert.False(path.IsStrokeable);
        Assert.False(path.Undo());

        path.Add(new(1f, 1f));

        Assert.False(path.IsStrokeable, "one point is a dot, and a dot is what the paint mode is for.");

        path.Add(new(9f, 9f));

        Assert.True(path.IsStrokeable);
        Assert.True(path.Undo());
        Assert.False(path.IsStrokeable);

        path.Add(new(9f, 9f));
        path.Clear();

        Assert.Empty(path.Points);

        // A cleared path samples to nothing rather than to its last state, which is what a stroke
        // laid after an Escape would otherwise repaint.
        List<Vector2> positions = [];

        path.Sample(positions);

        Assert.Empty(positions);
    }

    /// <summary>An S through four points, scaled to a size.</summary>
    static PaintPath Curve(float size) {
        PaintPath path = new();

        foreach (var point in new[] {
                     new Vector2(0f, 0f), new(0.35f, 0.6f), new(0.65f, -0.6f), new(1f, 0.1f)
                 }) {
            path.Add(point * size);
        }

        return path;
    }

    /// <summary>The curve itself, at far more points than the sampler would emit.</summary>
    static List<Vector2> Dense(PaintPath path) {
        List<Vector2> dense = [];

        path.Sample(dense, 1e-3f);

        return dense;
    }

    /// <summary>How far a point is from a segment.</summary>
    static float ToSegment(Vector2 point, Vector2 from, Vector2 to) {
        var span = to - from;
        var length = span.LengthSquared();

        if (!(length > 0f)) {
            return (point - from).Length();
        }

        var t = Math.Clamp(Vector2.Dot(point - from, span) / length, 0f, 1f);

        return (point - (from + (span * t))).Length();
    }
}
