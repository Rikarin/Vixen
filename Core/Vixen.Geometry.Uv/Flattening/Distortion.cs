// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Uv.Flattening;

/// <summary>The four measures of § D6, plus the one that is not a measure.</summary>
/// <remarks>
///     <para>
///         docs/plan/42 § D6: <b>reporting one number hides the failure that matters</b>. A conformal
///         map can have perfect angles and a fortyfold area ratio between two ends of one chart, which
///         reads as a texture that is sharp on the shoulder and mush on the hand — and an angle-only
///         metric calls that excellent. So angles and areas are separate numbers, the stretch is
///         reported as both an average and a worst case, and the flip count stands apart from all four.
///     </para>
///     <para>
///         ⚠ <b>Every one of the four is normalized so that one is perfect and larger is worse</b>,
///         which is what makes <c>UvSettings.DistortionThreshold</c> — <i>"one is a perfectly isometric
///         map"</i> — a single number that any of them can be compared against.
///     </para>
///     <para>
///         ⚠ <b>All four are invariant to the chart's scale, and that is a property to test rather
///         than to assume.</b> Sander's stretch is a ratio of surface length to parameter length, so it
///         carries units until it is divided by the map's average — and a measure that moved when a
///         model arrived in millimetres would make every threshold in this library a statement about
///         how big the model was.
///     </para>
/// </remarks>
static class Distortion {
    /// <summary>Measures one chart's mapping, and its contribution to a whole-mesh average.</summary>
    /// <param name="Distortion">The four measures and the flip count, for this chart alone.</param>
    /// <param name="SurfaceArea">The chart's area in world units squared, which is the weight it carries.</param>
    /// <param name="ParameterArea">Its area in the parameter plane, unsigned.</param>
    /// <param name="MeasurableArea">
    ///     How much of <paramref name="SurfaceArea" /> had a finite stretch. Less than all of it exactly
    ///     when a triangle collapsed in the parameterization, which is already a flip.
    /// </param>
    /// <param name="AngularSum">Area-weighted angular distortion, before the division.</param>
    /// <param name="AreaSum">Area-weighted area distortion, before the division.</param>
    /// <param name="StretchSum">Area-weighted squared L² stretch, unnormalized.</param>
    /// <param name="WorstStretch">The largest single-triangle stretch, unnormalized.</param>
    internal readonly record struct Measured(
        UvDistortion Distortion,
        double SurfaceArea,
        double ParameterArea,
        double MeasurableArea,
        double AngularSum,
        double AreaSum,
        double StretchSum,
        double WorstStretch
    );

    /// <summary>Measures a flattened chart.</summary>
    /// <param name="chart">The chart.</param>
    /// <param name="frames">Each triangle laid flat in its own plane.</param>
    /// <param name="coordinates">Two per vertex, as the ladder left them.</param>
    /// <param name="rounded">
    ///     The same coordinates already narrowed to <see langword="float" />, one per vertex. ⚠ The
    ///     flip test runs on these and not on the <see langword="double" />s — see the remarks.
    /// </param>
    /// <returns>The measures, and the sums a whole-mesh average is made from.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The flip test runs on the coordinates that ship.</b> The solve is in
    ///         <see langword="double" /> and <see cref="UvIsland" /> holds <see langword="float" />, so
    ///         a triangle can be positively oriented in the solve and exactly degenerate after the
    ///         narrowing. That is a fold in the asset, whatever the solve believed, and testing the
    ///         wrong array is a bug that only appears on the sliver that provoked it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><see cref="ExactPredicates.Orient2D" /> and not a <see langword="float" /> cross
    ///         product.</b> Three points that are exactly collinear can give a naive cross product of
    ///         <c>16</c> in one argument order and <c>−67108864</c> in another — the error is in the
    ///         coordinate <i>subtractions</i>, so no epsilon scaled to the inputs rescues it. The case
    ///         that matters here is a triangle that is exactly degenerate in the parameterization, and
    ///         that is precisely the case the naive test gets wrong.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A triangle with no area in three dimensions is not counted as flipped.</b> It has
    ///         no orientation to reverse, no frame to compare against, and no texels of its own; the
    ///         warning it deserves comes from the flattener, which knows it dropped it, rather than
    ///         from a correctness count that must stay at zero.
    ///     </para>
    /// </remarks>
    public static Measured Measure(
        ChartMesh chart,
        TriangleFrame[] frames,
        double[] coordinates,
        Vector2[] rounded
    ) {
        var surface = 0d;
        var parameter = 0d;
        var angular = 0d;
        var area = 0d;
        var stretch = 0d;
        var worst = 0d;
        var measurable = 0d;
        var flipped = 0;

        // Two passes: the area ratio each triangle is judged against is the chart's own, which is not
        // known until every triangle has been visited.
        var ratio = new double[chart.TriangleCount];

        for (var triangle = 0; triangle < chart.TriangleCount; triangle++) {
            var frame = frames[triangle];

            if (frame.IsDegenerate) {
                continue;
            }

            var a = chart.Triangles[triangle * 3];
            var b = chart.Triangles[(triangle * 3) + 1];
            var c = chart.Triangles[(triangle * 3) + 2];

            var ux = coordinates[(b * 2) + 0] - coordinates[(a * 2) + 0];
            var uy = coordinates[(b * 2) + 1] - coordinates[(a * 2) + 1];
            var vx = coordinates[(c * 2) + 0] - coordinates[(a * 2) + 0];
            var vy = coordinates[(c * 2) + 1] - coordinates[(a * 2) + 1];

            var doubledParameter = Math.Abs((ux * vy) - (uy * vx));

            surface += 0.5d * frame.DoubleArea;
            parameter += 0.5d * doubledParameter;
            ratio[triangle] = doubledParameter / frame.DoubleArea;

            if (ExactPredicates.Orient2D(rounded[a], rounded[b], rounded[c]) <= 0) {
                flipped++;
            }
        }

        var average = surface > 0d ? parameter / surface : 0d;

        for (var triangle = 0; triangle < chart.TriangleCount; triangle++) {
            var frame = frames[triangle];

            if (frame.IsDegenerate) {
                continue;
            }

            var weight = 0.5d * frame.DoubleArea;
            var relative = average > 0d ? ratio[triangle] / average : 0d;

            area += weight * (relative > 0d ? Math.Max(relative, 1d / relative) : 0d);

            var (larger, smaller) = Singular(chart, frames, coordinates, triangle);

            // ⚠ A triangle that collapsed to a segment in the parameterization has an infinite stretch
            // and is already counted as flipped. Left out of the averages rather than allowed to make
            // every one of them an infinity, which would report one bad triangle as a chart with no
            // measurable quality at all.
            if (!(smaller > 0d)) {
                continue;
            }

            // Sander's singular values are of the map from the *parameter* plane to the surface, which
            // is the inverse of the one fitted above — so they are the reciprocals, and they swap.
            var largest = 1d / smaller;
            var smallest = 1d / larger;

            angular += weight * (larger / smaller);
            stretch += weight * 0.5d * ((largest * largest) + (smallest * smallest));
            worst = Math.Max(worst, largest);
            measurable += weight;
        }

        // The normalization that makes stretch dimensionless: the map's own average scale. Without it
        // the same chart at two model scales reports two different numbers.
        var normal = average > 0d ? Math.Sqrt(average) : 0d;

        var measured = new UvDistortion(
            (float)(measurable > 0d ? angular / measurable : 0d),
            (float)(surface > 0d ? area / surface : 0d),
            (float)(measurable > 0d ? Math.Sqrt(stretch / measurable) * normal : 0d),
            (float)(worst * normal),
            flipped
        );

        return new(measured, surface, parameter, measurable, angular, area, stretch, worst);
    }

    /// <summary>Merges per-chart measures into one figure for the whole mesh.</summary>
    /// <param name="charts">What every chart measured.</param>
    /// <returns>The four measures over the union, and the total flip count.</returns>
    /// <remarks>
    ///     ⚠ <b>Area-weighted rather than averaged over charts.</b> A mesh whose forty-first chart is
    ///     one triangle would otherwise let that triangle carry a fortieth of the headline number, and
    ///     a charter that fragments would look like a flattener that improved.
    /// </remarks>
    public static UvDistortion Combine(IReadOnlyList<Measured> charts) {
        var surface = 0d;
        var parameter = 0d;
        var measurable = 0d;
        var angular = 0d;
        var area = 0d;
        var stretch = 0d;
        var worst = 0d;
        var flipped = 0;

        foreach (var chart in charts) {
            surface += chart.SurfaceArea;
            parameter += chart.ParameterArea;
            measurable += chart.MeasurableArea;
            angular += chart.AngularSum;
            area += chart.AreaSum;
            stretch += chart.StretchSum;
            worst = Math.Max(worst, chart.WorstStretch);
            flipped += chart.Distortion.Flipped;
        }

        if (!(surface > 0d)) {
            return new(0f, 0f, 0f, 0f, flipped);
        }

        var normal = Math.Sqrt(parameter / surface);

        return new(
            (float)(measurable > 0d ? angular / measurable : 0d),
            (float)(area / surface),
            (float)(measurable > 0d ? Math.Sqrt(stretch / measurable) * normal : 0d),
            (float)(worst * normal),
            flipped
        );
    }

    /// <summary>The two singular values of one triangle's Jacobian, surface to parameter.</summary>
    /// <remarks>
    ///     The standard closed form for a 2×2: the matrix is the sum of a rotation-and-scale and a
    ///     reflection-and-scale, and their magnitudes are the half-sum and half-difference of the
    ///     singular values. ⚠ The smaller one is taken as a magnitude, so a flipped triangle reports
    ///     the stretch it has rather than a negative number that would cancel in an average.
    /// </remarks>
    static (double Larger, double Smaller) Singular(
        ChartMesh chart,
        TriangleFrame[] frames,
        double[] coordinates,
        int triangle
    ) {
        var frame = frames[triangle];
        var a = chart.Triangles[triangle * 3];
        var b = chart.Triangles[(triangle * 3) + 1];
        var c = chart.Triangles[(triangle * 3) + 2];

        var ux = coordinates[(b * 2) + 0] - coordinates[(a * 2) + 0];
        var uy = coordinates[(b * 2) + 1] - coordinates[(a * 2) + 1];
        var vx = coordinates[(c * 2) + 0] - coordinates[(a * 2) + 0];
        var vy = coordinates[(c * 2) + 1] - coordinates[(a * 2) + 1];

        var determinant = frame.DoubleArea;

        // J = U·X⁻¹ with X = [[X1, X2], [0, Y2]].
        var j00 = ux * frame.Y2 / determinant;
        var j01 = ((vx * frame.X1) - (ux * frame.X2)) / determinant;
        var j10 = uy * frame.Y2 / determinant;
        var j11 = ((vy * frame.X1) - (uy * frame.X2)) / determinant;

        var turn = Math.Sqrt(Square((j00 + j11) * 0.5d) + Square((j10 - j01) * 0.5d));
        var flip = Math.Sqrt(Square((j00 - j11) * 0.5d) + Square((j10 + j01) * 0.5d));

        return (turn + flip, Math.Abs(turn - flip));
    }

    static double Square(double value) => value * value;
}
