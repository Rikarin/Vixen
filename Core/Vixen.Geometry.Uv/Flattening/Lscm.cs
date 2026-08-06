// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Geometry.Uv.Solving;

namespace Vixen.Geometry.Uv.Flattening;

/// <summary>The ladder's first rung: one sparse least-squares solve, conformal, and no promises.</summary>
/// <remarks>
///     <para>
///         docs/plan/42 § D5. Least-squares conformal maps ask each triangle's map to satisfy the
///         Cauchy–Riemann equations and minimize the squared failure over the whole chart. Written as
///         a complex condition per triangle it is <c>Σⱼ Wⱼ uⱼ = 0</c> with <c>W</c> the opposite edge
///         in the triangle's own plane, which is two real rows per triangle and two real columns per
///         vertex — rectangular, overdetermined, and exactly the shape
///         <see cref="LeastSquaresSolver" /> was written for.
///     </para>
///     <para>
///         ⚠ <b>Conformal is not injective, and the difference is the whole reason there are three
///         rungs.</b> A conformal map preserves angles and says nothing about area, so it can fold a
///         chart over itself and can compress one end of a chart forty-fold against the other while
///         reporting perfect angular distortion — § D6's warning about single-number metrics, from the
///         inside. This rung is kept because it is one solve and because it is the initialization
///         <see cref="Arap" /> needs, not because its output is shippable.
///     </para>
///     <para>
///         ⚠ <b>Rows are divided by the square root of twice the area, which is what makes the energy
///         scale-free.</b> Without it a chart's large triangles dominate its small ones by their area,
///         so the same shape at two tessellations gives two different maps.
///     </para>
/// </remarks>
static class Lscm {
    /// <summary>How many times the warm-start budget this one cold solve is allowed.</summary>
    /// <remarks>
    ///     ⚠ <b>Every other solve in the ladder is warm and <c>UvSettings.SolverIterations</c> is sized
    ///     for those.</b> This one starts from nothing, so it has to build the whole Krylov space
    ///     rather than the correction to one, and the default sixty-four leaves a large chart visibly
    ///     unconverged — which then arrives as a fold that the repair pass is asked to fix and cannot.
    ///     A generous budget is safe here for the reason <see cref="ConjugateGradient.Exhausted" />
    ///     gives: the floor stops the iteration when it stops descending, so the cost of over-asking is
    ///     time and never accuracy. It is a multiple rather than a second setting because two budgets a
    ///     caller can set independently is two ways to get the same asset.
    /// </remarks>
    public const int ColdBudget = 16;

    /// <summary>Flattens a chart conformally.</summary>
    /// <param name="chart">The chart, which must already have been found to be a disk.</param>
    /// <param name="frames">Each triangle laid flat in its own plane.</param>
    /// <param name="settings">Where the solver budget comes from.</param>
    /// <param name="coordinates">
    ///     Two per vertex, written. Whatever is in it on the way in is the warm start, so a zeroed
    ///     array is a cold solve.
    /// </param>
    /// <returns>What the solve did.</returns>
    public static SolveReport Solve(
        ChartMesh chart,
        TriangleFrame[] frames,
        UvSettings settings,
        double[] coordinates
    ) {
        var (first, second, distance) = Pins.Choose(chart);

        // The gauge. The first pin goes to the origin and contributes nothing to the right-hand side;
        // the second goes onto the abscissa at the world distance between them, so the chart comes out
        // at roughly its own scale and ARAP's first local step sees rotations rather than a scaling.
        var pinned = new double[chart.VertexCount * 2];
        pinned[(second * 2) + 0] = distance > 0d ? distance : 1d;

        var free = new int[chart.VertexCount];
        var unknowns = 0;

        for (var vertex = 0; vertex < chart.VertexCount; vertex++) {
            free[vertex] = vertex == first || vertex == second ? -1 : unknowns++;
        }

        var builder = new SparseMatrixBuilder(chart.TriangleCount * 2, unknowns * 2);
        var right = new double[chart.TriangleCount * 2];

        for (var triangle = 0; triangle < chart.TriangleCount; triangle++) {
            var frame = frames[triangle];

            // ⚠ A triangle with no area contributes two empty rows rather than an infinity. Its
            // vertices are still constrained by every other triangle that uses them, and a chart whose
            // *only* triangles are degenerate leaves empty columns — which `LeastSquaresSolver`
            // already defines as "leave the unknown where the warm start put it".
            if (frame.IsDegenerate) {
                continue;
            }

            var weight = 1d / Math.Sqrt(frame.DoubleArea);

            // The opposite edge of each corner, as a complex number, in the triangle's own plane.
            Span<double> realPart = [frame.X2 - frame.X1, -frame.X2, frame.X1];
            Span<double> imaginaryPart = [frame.Y2, -frame.Y2, 0d];

            var real = triangle * 2;
            var imaginary = real + 1;
            var accumulated = (Real: 0d, Imaginary: 0d);

            for (var corner = 0; corner < 3; corner++) {
                var vertex = chart.Triangles[(triangle * 3) + corner];
                var wx = realPart[corner] * weight;
                var wy = imaginaryPart[corner] * weight;
                var column = free[vertex];

                if (column < 0) {
                    var a = pinned[(vertex * 2) + 0];
                    var b = pinned[(vertex * 2) + 1];

                    accumulated.Real += (wx * a) - (wy * b);
                    accumulated.Imaginary += (wy * a) + (wx * b);

                    continue;
                }

                // (wx + i·wy)(a + i·b) = (wx·a − wy·b) + i(wy·a + wx·b).
                builder.Add(real, (column * 2) + 0, wx);
                builder.Add(real, (column * 2) + 1, -wy);
                builder.Add(imaginary, (column * 2) + 0, wy);
                builder.Add(imaginary, (column * 2) + 1, wx);
            }

            right[real] = -accumulated.Real;
            right[imaginary] = -accumulated.Imaginary;
        }

        var solution = new double[unknowns * 2];

        for (var vertex = 0; vertex < chart.VertexCount; vertex++) {
            if (free[vertex] >= 0) {
                solution[(free[vertex] * 2) + 0] = coordinates[(vertex * 2) + 0];
                solution[(free[vertex] * 2) + 1] = coordinates[(vertex * 2) + 1];
            }
        }

        var solver = new LeastSquaresSolver(builder.Build());
        var report = solver.Solve(right, solution, settings.SolverIterations * ColdBudget);

        for (var vertex = 0; vertex < chart.VertexCount; vertex++) {
            var column = free[vertex];

            coordinates[(vertex * 2) + 0] = column < 0 ? pinned[(vertex * 2) + 0] : solution[(column * 2) + 0];
            coordinates[(vertex * 2) + 1] = column < 0 ? pinned[(vertex * 2) + 1] : solution[(column * 2) + 1];
        }

        Unmirror(chart, frames, coordinates);

        return report;
    }

    /// <summary>Reflects the chart when the solve settled on the anti-conformal branch.</summary>
    /// <remarks>
    ///     ⚠ <b>A mirrored chart is every triangle flipped, which the flip count would report as a
    ///     total failure and which is in fact one sign.</b> The conformal energy as written penalizes
    ///     anti-holomorphic maps, so this is rare — but the pinned pair fixes the gauge and does not
    ///     fix the branch, and a chart that came back mirrored would otherwise be handed to the repair
    ///     pass, which would work on the whole chart and fail. Reflecting about the abscissa is exact:
    ///     one sign bit per coordinate, no arithmetic.
    /// </remarks>
    static void Unmirror(ChartMesh chart, TriangleFrame[] frames, double[] coordinates) {
        var signed = 0d;

        for (var triangle = 0; triangle < chart.TriangleCount; triangle++) {
            if (frames[triangle].IsDegenerate) {
                continue;
            }

            var a = chart.Triangles[triangle * 3];
            var b = chart.Triangles[(triangle * 3) + 1];
            var c = chart.Triangles[(triangle * 3) + 2];

            var abx = coordinates[(b * 2) + 0] - coordinates[(a * 2) + 0];
            var aby = coordinates[(b * 2) + 1] - coordinates[(a * 2) + 1];
            var acx = coordinates[(c * 2) + 0] - coordinates[(a * 2) + 0];
            var acy = coordinates[(c * 2) + 1] - coordinates[(a * 2) + 1];

            signed += (abx * acy) - (aby * acx);
        }

        if (signed >= 0d) {
            return;
        }

        for (var vertex = 0; vertex < chart.VertexCount; vertex++) {
            coordinates[(vertex * 2) + 1] = -coordinates[(vertex * 2) + 1];
        }
    }
}
