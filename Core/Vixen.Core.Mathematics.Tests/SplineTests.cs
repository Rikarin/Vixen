// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Core.Mathematics.Tests;

/// <summary>
///     The spline — [docs/plan/31 § B5] and [§ T8], which is also [docs/plan/26]'s owed dolly track.
/// </summary>
public sealed class SplineTests {
    /// <summary>A straight run of four points a metre apart along X, with matched tangents.</summary>
    static Spline Straight() =>
        new(Spline.SmoothTangents([new(0, 0, 0), new(1, 0, 0), new(2, 0, 0), new(3, 0, 0)]));

    /// <summary>A square, closed, so the curve loops.</summary>
    static Spline Square(bool closed = true) =>
        new(Spline.SmoothTangents([new(0, 0, 0), new(4, 0, 0), new(4, 0, 4), new(0, 0, 4)], closed), closed);

    [Fact]
    public void ASplineNeedsTwoPoints() {
        Assert.Throws<ArgumentException>(() => new Spline([SplinePoint.At(Vector3.Zero)]));
        Assert.Throws<ArgumentException>(() => Spline.SmoothTangents([Vector3.Zero]));
    }

    [Fact]
    public void ItPassesThroughEveryControlPoint() {
        // The property that makes Hermite the right family for an editor: the control points are on
        // the curve, so dragging one drags the road.
        var spline = Square(closed: false);

        for (var index = 0; index < spline.Points.Length; index++) {
            Assert.Equal(spline.Points[index].Position, spline.Evaluate(index));
        }
    }

    [Fact]
    public void SegmentCountAndParameterRangeFollowFromWhetherItIsClosed() {
        Assert.Equal(3, Square(closed: false).SegmentCount);
        Assert.Equal(4, Square().SegmentCount);
        Assert.Equal(4f, Square().MaxParameter);
    }

    [Fact]
    public void APointWithNoTangentsGivesAStraightSegment() {
        var spline = new Spline([SplinePoint.At(new(0, 0, 0)), SplinePoint.At(new(10, 0, 0))]);

        // The path is the chord even though the speed along it is not constant — the Hermite basis
        // functions for the two positions sum to one, which is what makes it a point of the segment.
        for (var step = 0; step <= 10; step++) {
            var point = spline.Evaluate(step / 10f);

            Assert.Equal(0f, point.Y, 5);
            Assert.Equal(0f, point.Z, 5);
            Assert.InRange(point.X, 0f, 10f);
        }
    }

    [Fact]
    public void AnOpenSplineClampsAndAClosedOneWraps() {
        var open = Straight();

        Assert.Equal(open.Evaluate(0f), open.Evaluate(-5f));
        Assert.Equal(open.Evaluate(open.MaxParameter), open.Evaluate(99f));

        var closed = Square();

        AssertClose(closed.Evaluate(0.5f), closed.Evaluate(4.5f));
        AssertClose(closed.Evaluate(0.5f), closed.Evaluate(-3.5f));
    }

    [Fact]
    public void AClosedSplineJoinsItsEndToItsStart() {
        var closed = Square();
        AssertClose(closed.Evaluate(0f), closed.Evaluate(closed.MaxParameter));
    }

    [Fact]
    public void ANaNParameterReadsAsTheStartRatherThanPoisoningTheResult() {
        var spline = Straight();
        var point = spline.Evaluate(float.NaN);

        Assert.True(float.IsFinite(point.X));
        Assert.Equal(spline.Evaluate(0f), point);
    }

    // --- Tangents and frames ------------------------------------------------

    [Fact]
    public void TheTangentIsTheDirectionOfTravel() {
        var spline = Straight();

        for (var step = 1; step < 10; step++) {
            var tangent = spline.Tangent(step / 10f * spline.MaxParameter);

            Assert.Equal(1f, tangent.X, 4);
            Assert.Equal(1f, tangent.Length(), 4);
        }
    }

    /// <summary>
    ///     The analytic derivative agrees with a numeric one, which is what says it is the derivative
    ///     of <em>this</em> curve rather than a plausible-looking formula.
    /// </summary>
    [Fact]
    public void TheAnalyticTangentMatchesANumericalOne() {
        var spline = Square(closed: false);
        const float h = 1e-3f;

        for (var step = 1; step < 30; step++) {
            var t = step / 30f * spline.MaxParameter;
            var numeric = Vector3.Normalize(spline.Evaluate(t + h) - spline.Evaluate(t - h));

            AssertClose(numeric, spline.Tangent(t), 0.002f);
        }
    }

    [Fact]
    public void ADegenerateSegmentGivesAUsableTangentRatherThanZero() {
        var coincident = new Spline([SplinePoint.At(Vector3.Zero), SplinePoint.At(Vector3.Zero)]);
        var tangent = coincident.Tangent(0.5f);

        Assert.Equal(1f, tangent.Length(), 4);

        // A straight segment authored with no tangents has a zero derivative at its own ends, and
        // the chord is the right answer there — not the zero vector Normalize would hand back.
        var straight = new Spline([SplinePoint.At(Vector3.Zero), SplinePoint.At(new(0, 0, 5))]);

        Assert.Equal(1f, straight.Tangent(0f).Length(), 4);
        Assert.Equal(1f, straight.Tangent(0f).Z, 4);
    }

    [Fact]
    public void AFrameIsOrthonormalAndRightHanded() {
        var spline = Square(closed: false);

        for (var step = 0; step <= 20; step++) {
            var frame = spline.FrameAt(step / 20f * spline.MaxParameter, Vector3.UnitY);

            Assert.Equal(1f, frame.Tangent.Length(), 4);
            Assert.Equal(1f, frame.Normal.Length(), 4);
            Assert.Equal(1f, frame.Binormal.Length(), 4);

            Assert.Equal(0f, Vector3.Dot(frame.Tangent, frame.Normal), 4);
            Assert.Equal(0f, Vector3.Dot(frame.Tangent, frame.Binormal), 4);
            Assert.Equal(0f, Vector3.Dot(frame.Normal, frame.Binormal), 4);

            AssertClose(Vector3.Cross(frame.Binormal, frame.Tangent), frame.Normal, 1e-3f);
        }
    }

    [Fact]
    public void AVerticalCurveStillGetsAFrameRatherThanANaN() {
        // Heading straight along the world up, where the usual cross product is zero and any side is
        // as good as any other. Picking one deterministically beats normalising nearly-zero.
        var spline = new Spline([
            SplinePoint.Smooth(Vector3.Zero, new(0, 1, 0)),
            SplinePoint.Smooth(new(0, 10, 0), new(0, 1, 0))
        ]);

        var frame = spline.FrameAt(0.5f, Vector3.UnitY);

        Assert.Equal(1f, frame.Normal.Length(), 4);
        Assert.Equal(1f, frame.Binormal.Length(), 4);
        Assert.Equal(0f, Vector3.Dot(frame.Tangent, frame.Normal), 4);
    }

    [Fact]
    public void RollTwistsTheFrameAboutTheTangentAndInterpolatesBetweenPoints() {
        var spline = new Spline([
            SplinePoint.Smooth(Vector3.Zero, new(1, 0, 0)),
            SplinePoint.Smooth(new(10, 0, 0), new(1, 0, 0), MathF.PI / 2f)
        ]);

        var start = spline.FrameAt(0f, Vector3.UnitY);
        var end = spline.FrameAt(1f, Vector3.UnitY);

        AssertClose(Vector3.UnitY, start.Normal, 1e-3f);

        // A quarter turn puts the up vector where the side vector was.
        AssertClose(start.Binormal, end.Normal, 1e-3f);

        // Halfway is an eighth of a turn, so the normal still has a positive Y and a positive
        // component along the start binormal.
        var middle = spline.FrameAt(0.5f, Vector3.UnitY);
        Assert.True(Vector3.Dot(middle.Normal, Vector3.UnitY) > 0.6f);
        Assert.True(Vector3.Dot(middle.Normal, start.Binormal) > 0.6f);
    }

    // --- Arc length ---------------------------------------------------------

    [Fact]
    public void AStraightLinesLengthIsItsLength() {
        var spline = new Spline([
            SplinePoint.Smooth(Vector3.Zero, new(4, 0, 0)),
            SplinePoint.Smooth(new(12, 0, 0), new(4, 0, 0))
        ]);

        Assert.Equal(12f, spline.Length, 3);
    }

    [Fact]
    public void ADistanceParameterisationMovesAtConstantSpeed() {
        // The property Evaluate does not have, and the reason both exist. A camera stepping equal
        // distances must cover equal ground; stepping equal parameters it speeds up and slows down.
        var spline = Square(closed: false);
        var step = spline.Length / 40f;
        var previous = spline.EvaluateAtDistance(0f);

        for (var index = 1; index <= 40; index++) {
            var point = spline.EvaluateAtDistance(index * step);
            var travelled = Vector3.Distance(previous, point);

            Assert.InRange(travelled, step * 0.9f, step * 1.1f);
            previous = point;
        }
    }

    [Fact]
    public void ParameterAtDistanceIsMonotonicAndClamped() {
        var spline = Square(closed: false);

        Assert.Equal(0f, spline.ParameterAtDistance(-5f));
        Assert.Equal(spline.MaxParameter, spline.ParameterAtDistance(spline.Length + 5f));

        var previous = -1f;

        for (var step = 0; step <= 50; step++) {
            var parameter = spline.ParameterAtDistance(step / 50f * spline.Length);

            Assert.True(parameter >= previous, $"went backwards at step {step}.");
            previous = parameter;
        }
    }

    /// <summary>
    ///     A quarter-circle-ish arc measures under the true length rather than over.
    /// </summary>
    /// <remarks>
    ///     Chords cut corners, so a sampled arc length is always an under-estimate — and under is the
    ///     right direction to be wrong in, because a camera told the track is shorter than it is stops
    ///     at the end rather than past it. This pins both the direction and the size of the error.
    /// </remarks>
    [Fact]
    public void MeasuredLengthIsAnUnderEstimateAndACloseOne() {
        // The standard cubic approximation to a quarter circle of radius 1. Its Bézier handles are
        // 4/3·(√2 − 1) long, and a Hermite tangent is three times a Bézier handle — m₀ = 3(P₁ − P₀).
        // Dropping that factor gives a curve that is visibly inside the circle and measures 8 % short.
        const float k = 4f * 0.4142136f;

        var spline = new Spline([
            new(new(1, 0, 0), Vector3.Zero, new(0, 0, k)),
            new(new(0, 0, 1), new(k, 0, 0), Vector3.Zero)
        ]);

        var exact = MathF.PI / 2f;

        Assert.True(spline.Length <= exact, $"{spline.Length} should not exceed {exact}.");
        Assert.InRange(spline.Length, exact - 0.01f, exact);
    }

    // --- Nearest point ------------------------------------------------------

    [Fact]
    public void DistanceToFindsThePerpendicularFoot() {
        var spline = new Spline([
            SplinePoint.Smooth(Vector3.Zero, new(4, 0, 0)),
            SplinePoint.Smooth(new(12, 0, 0), new(4, 0, 0))
        ]);

        var distance = spline.DistanceTo(new(6f, 0f, 5f), out var parameter);

        Assert.Equal(5f, distance, 2);
        AssertClose(new(6f, 0f, 0f), spline.Evaluate(parameter), 0.05f);
    }

    [Fact]
    public void DistanceToClampsToTheEndsRatherThanExtrapolating() {
        var spline = Straight();
        var distance = spline.DistanceTo(new(-10f, 0f, 0f), out var parameter);

        Assert.Equal(0f, parameter, 3);
        Assert.Equal(10f, distance, 2);
    }

    [Fact]
    public void APointOnTheCurveIsAtZeroDistanceFromIt() {
        var spline = Square(closed: false);

        for (var step = 1; step < 12; step++) {
            var t = step / 12f * spline.MaxParameter;
            var on = spline.Evaluate(t);

            Assert.Equal(0f, spline.DistanceTo(on, out var found), 2);

            // A tolerance rather than a decimal place: Assert.Equal(t, found, 1) rounds both sides,
            // and 0.25 against 0.2500001 straddles a banker's-rounding boundary in opposite
            // directions — a green test that fails on a value that is closer than the one it passed.
            Assert.True(MathF.Abs(t - found) < 0.02f, $"expected about {t} but found {found}.");
        }
    }

    // --- Auto tangents ------------------------------------------------------

    [Fact]
    public void SmoothTangentsMirrorSoTheCurveIsSmoothThroughEveryPoint() {
        var points = Spline.SmoothTangents([new(0, 0, 0), new(1, 0, 1), new(2, 0, 0)]);

        foreach (var point in points) {
            AssertClose(-point.TangentOut, point.TangentIn, 1e-5f);
        }
    }

    [Fact]
    public void AnOpenPathsEndsUseTheirOneChordRatherThanAPhantomPoint() {
        // A reflected phantom overshoots, and an overshooting road runs off the end of itself.
        var points = Spline.SmoothTangents([new(0, 0, 0), new(1, 0, 0), new(2, 0, 0)]);

        Assert.Equal(0.5f, points[0].TangentOut.X, 5);
        Assert.Equal(1f, points[1].TangentOut.X, 5);
        Assert.Equal(0.5f, points[2].TangentOut.X, 5);
    }

    [Fact]
    public void FullTensionMakesAPolyline() {
        var points = Spline.SmoothTangents([new(0, 0, 0), new(1, 0, 1), new(2, 0, 0)], tension: 1f);

        foreach (var point in points) {
            Assert.Equal(Vector3.Zero, point.TangentOut);
        }
    }

    [Fact]
    public void AClosedPathsEndsSeeEachOther() {
        var open = Spline.SmoothTangents([new(0, 0, 0), new(4, 0, 0), new(4, 0, 4), new(0, 0, 4)]);
        var closed = Spline.SmoothTangents([new(0, 0, 0), new(4, 0, 0), new(4, 0, 4), new(0, 0, 4)], closed: true);

        Assert.NotEqual(open[0].TangentOut, closed[0].TangentOut);
        Assert.NotEqual(open[^1].TangentOut, closed[^1].TangentOut);
    }

    static void AssertClose(Vector3 expected, Vector3 actual, float tolerance = 1e-4f) {
        Assert.True(
            Vector3.Distance(expected, actual) <= tolerance,
            $"expected {expected} but got {actual}, which is {Vector3.Distance(expected, actual)} away."
        );
    }
}
