// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Geometry.Uv.Solving;

namespace Vixen.Geometry.Uv.Flattening;

/// <summary>The ladder's second rung: as-rigid-as-possible, local–global, warm-started throughout.</summary>
/// <remarks>
///     <para>
///         docs/plan/42 § D5. The local step fits the closest rotation to each triangle's current
///         Jacobian; the global step solves for the coordinates that best agree with all of those
///         rotations at once, which is a cotangent Laplacian. Each half step decreases the same energy,
///         so the loop needs no line search and no tolerance — <c>UvSettings.FlattenIterations</c> is a
///         count for the same reason <c>UvSettings.SolverIterations</c> is.
///     </para>
///     <para>
///         <b>Why this rung exists at all.</b> <see cref="Lscm" /> is conformal, and a conformal energy
///         is blind to scale: it is perfectly happy to shrink one end of a chart to a point as long as
///         the angles survive. ARAP penalizes any departure from a rotation, which is stretch
///         <i>and</i> compression, and the rotation it fits is a proper one — so a triangle that came
///         back flipped is asked to rotate onto a correctly-wound triangle rather than onto its own
///         mirror image. <b>That is the mechanism that un-folds a chart</b>, and it is why the third
///         rung is this same code over a smaller free set rather than different code.
///     </para>
///     <para>
///         ⚠ <b>The matrix is constant across the whole loop and only the right-hand side moves.</b>
///         That is the case <see cref="ConjugateGradient" />'s warm start was written for, and getting
///         it wrong is silent: allocating a fresh zeroed solution per iteration still converges, to the
///         same place, several times slower — which is what <c>WarmStartTests</c> measures. The matrix,
///         its incomplete Cholesky and both solution arrays are therefore built once, above the loop.
///     </para>
///     <para>
///         ⚠ <b>Both coordinates share one matrix and one factorization.</b> The Laplacian does not
///         know which axis it is solving for, so the abscissa and the ordinate are two right-hand sides
///         over one <see cref="ConjugateGradient" /> — which is also why that type keeps its working
///         vectors as fields and is explicitly not thread-safe.
///     </para>
///     <para>
///         ⚠ <b>The energy is ARAP's and not the symmetric Dirichlet, and the difference is the local
///         step.</b> § D5 names both. The global step is identical either way; a symmetric-Dirichlet
///         local step replaces the closest rotation with a per-triangle target that also penalizes
///         inversion by an infinite barrier, which needs a line search to stay inside it — and a line
///         search is a floating-point comparison deciding how many steps to take, which is § B6's
///         excluded class. ARAP's local step is closed-form, and the barrier's job here is done by the
///         flip count refusing the chart instead.
///     </para>
/// </remarks>
static class Arap {
    /// <summary>Runs the local–global loop over a chosen set of free vertices.</summary>
    /// <param name="chart">The chart.</param>
    /// <param name="frames">Each triangle laid flat in its own plane.</param>
    /// <param name="weights">The cotangent weights, already floored.</param>
    /// <param name="movable">
    ///     Which vertices the solve may move. Everything else is held exactly where
    ///     <paramref name="coordinates" /> has it.
    /// </param>
    /// <param name="coordinates">Two per vertex: the initialization in, the answer out.</param>
    /// <param name="iterations">How many local–global rounds to run.</param>
    /// <param name="solverIterations">The budget for each of the two linear solves in a round.</param>
    /// <returns>The last global solve's report, for the caller's own report.</returns>
    /// <remarks>
    ///     ⚠ <b>At least one vertex has to be immovable and the caller owns that.</b> A Laplacian's
    ///     null space is the constants — translate the whole chart and the energy does not change — so
    ///     a system with every vertex free is singular, conjugate gradient wanders along the null
    ///     direction, and the chart drifts to wherever the rounding took it. Anchoring one vertex
    ///     removes exactly that freedom and nothing else: the rotations in the right-hand side already
    ///     fix the orientation, and ARAP fixes the scale itself, which is what conformal energies do
    ///     not.
    /// </remarks>
    public static SolveReport Solve(
        ChartMesh chart,
        TriangleFrame[] frames,
        CotangentWeights weights,
        bool[] movable,
        double[] coordinates,
        int iterations,
        int solverIterations
    ) {
        var row = new int[chart.VertexCount];
        var unknowns = 0;

        for (var vertex = 0; vertex < chart.VertexCount; vertex++) {
            row[vertex] = movable[vertex] ? unknowns++ : -1;
        }

        if (unknowns == 0 || iterations <= 0) {
            return default;
        }

        var sides = Sides(chart, frames, weights);
        var builder = new SparseMatrixBuilder(unknowns, unknowns);

        foreach (var side in sides) {
            var left = row[side.First];
            var right = row[side.Second];

            if (left >= 0) {
                builder.Add(left, left, side.Weight);
            }

            if (right >= 0) {
                builder.Add(right, right, side.Weight);
            }

            if (left >= 0 && right >= 0) {
                builder.Add(left, right, -side.Weight);
                builder.Add(right, left, -side.Weight);
            }
        }

        var solver = new ConjugateGradient(builder.Build());
        var abscissa = new double[unknowns];
        var ordinate = new double[unknowns];

        for (var vertex = 0; vertex < chart.VertexCount; vertex++) {
            if (row[vertex] >= 0) {
                abscissa[row[vertex]] = coordinates[(vertex * 2) + 0];
                ordinate[row[vertex]] = coordinates[(vertex * 2) + 1];
            }
        }

        var rightAbscissa = new double[unknowns];
        var rightOrdinate = new double[unknowns];
        var rotations = new double[chart.TriangleCount * 2];
        var report = default(SolveReport);

        for (var round = 0; round < iterations; round++) {
            Fit(chart, frames, coordinates, rotations);

            Array.Clear(rightAbscissa);
            Array.Clear(rightOrdinate);

            foreach (var side in sides) {
                var cos = rotations[(side.Triangle * 2) + 0];
                var sin = rotations[(side.Triangle * 2) + 1];

                // R·(xᵢ − xⱼ) in the triangle's own plane: the edge vector the fitted rotation would
                // like this pair to have, which is the whole of ARAP's right-hand side.
                var turnedX = (cos * side.X) - (sin * side.Y);
                var turnedY = (sin * side.X) + (cos * side.Y);

                var left = row[side.First];
                var right = row[side.Second];

                if (left >= 0) {
                    rightAbscissa[left] += side.Weight * turnedX;
                    rightOrdinate[left] += side.Weight * turnedY;

                    // A held vertex is not an unknown, so its share of this edge's stiffness moves out
                    // of the matrix and into the free end's right-hand side.
                    if (right < 0) {
                        rightAbscissa[left] += side.Weight * coordinates[(side.Second * 2) + 0];
                        rightOrdinate[left] += side.Weight * coordinates[(side.Second * 2) + 1];
                    }
                }

                if (right >= 0) {
                    rightAbscissa[right] -= side.Weight * turnedX;
                    rightOrdinate[right] -= side.Weight * turnedY;

                    if (left < 0) {
                        rightAbscissa[right] += side.Weight * coordinates[(side.First * 2) + 0];
                        rightOrdinate[right] += side.Weight * coordinates[(side.First * 2) + 1];
                    }
                }
            }

            report = solver.Solve(rightAbscissa, abscissa, solverIterations);
            solver.Solve(rightOrdinate, ordinate, solverIterations);

            for (var vertex = 0; vertex < chart.VertexCount; vertex++) {
                if (row[vertex] >= 0) {
                    coordinates[(vertex * 2) + 0] = abscissa[row[vertex]];
                    coordinates[(vertex * 2) + 1] = ordinate[row[vertex]];
                }
            }
        }

        return report;
    }

    /// <summary>One triangle edge: which pair, how stiff, and what it looked like before it was mapped.</summary>
    /// <param name="Triangle">Which triangle contributed it.</param>
    /// <param name="First">The edge's first vertex, chart-local.</param>
    /// <param name="Second">Its second.</param>
    /// <param name="Weight">The cotangent of the opposite corner, floored.</param>
    /// <param name="X">The abscissa of <c>xFirst − xSecond</c> in the triangle's own plane.</param>
    /// <param name="Y">Its ordinate.</param>
    readonly record struct Side(int Triangle, int First, int Second, double Weight, double X, double Y);

    /// <summary>Every triangle's three edges, flattened once so the loop below never rebuilds them.</summary>
    /// <remarks>
    ///     ⚠ <b>Per triangle rather than per mesh edge, and the two are not the same sum.</b> An
    ///     interior edge is walked by both of its triangles and picks up both cotangents, which is what
    ///     the cotangent Laplacian is; a boundary edge picks up one. Iterating mesh edges instead and
    ///     halving would give the same matrix on a manifold interior and the wrong one on the boundary.
    /// </remarks>
    static Side[] Sides(ChartMesh chart, TriangleFrame[] frames, CotangentWeights weights) {
        var sides = new List<Side>(chart.TriangleCount * 3);

        for (var triangle = 0; triangle < chart.TriangleCount; triangle++) {
            var frame = frames[triangle];

            // ⚠ A triangle with no area has infinite cotangents and no frame to take a difference in.
            // Dropped rather than clamped: its vertices are still held by every other triangle that
            // uses them, and a chart made entirely of them is refused before it reaches here.
            if (frame.IsDegenerate) {
                continue;
            }

            for (var side = 0; side < 3; side++) {
                // The edge opposite corner `side`, which is the corner whose cotangent weights it.
                var first = chart.Triangles[(triangle * 3) + ((side + 1) % 3)];
                var second = chart.Triangles[(triangle * 3) + ((side + 2) % 3)];

                var (x, y) = side switch {
                    0 => (frame.X1 - frame.X2, -frame.Y2),
                    1 => (frame.X2, frame.Y2),
                    _ => (-frame.X1, 0d)
                };

                sides.Add(new(triangle, first, second, weights.Edge[(triangle * 3) + side], x, y));
            }
        }

        return [.. sides];
    }

    /// <summary>The local step: the closest rotation to each triangle's current Jacobian.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The closest <i>rotation</i>, not the closest orthogonal matrix, and that is the
    ///         line that un-folds a chart.</b> The closest orthogonal matrix to a flipped triangle's
    ///         Jacobian is a reflection, and fitting one would tell the global step that the fold was
    ///         fine. Restricting to <c>SO(2)</c> tells it the opposite, every round, until it is not.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>No singular value decomposition, no <c>atan2</c>, and both omissions are
    ///         deliberate.</b> Maximizing <c>tr(RᵀJ)</c> over rotations gives
    ///         <c>(cos, sin) ∝ (J₀₀ + J₁₁, J₁₀ − J₀₁)</c> in closed form, so the whole local step is a
    ///         handful of multiplies and one square root. Reaching for a transcendental instead would
    ///         put the answer at the mercy of a library function with no correctly-rounded guarantee
    ///         and no promise of agreeing between two platforms — which is docs/plan/42 § B6 arriving
    ///         as a coordinate.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A Jacobian whose symmetric and antisymmetric parts both vanish fits the
    ///         identity.</b> Every rotation is equally far from it, so the maximization has no unique
    ///         answer — and the answer is chosen here rather than left to whatever <c>0/0</c> produces
    ///         and carried into the right-hand side as a NaN.
    ///     </para>
    /// </remarks>
    static void Fit(ChartMesh chart, TriangleFrame[] frames, double[] coordinates, double[] rotations) {
        for (var triangle = 0; triangle < chart.TriangleCount; triangle++) {
            var frame = frames[triangle];

            rotations[(triangle * 2) + 0] = 1d;
            rotations[(triangle * 2) + 1] = 0d;

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

            // J = U·X⁻¹ with X = [[X1, X2], [0, Y2]] and a positive determinant, so the determinant
            // scales both quantities below by the same positive number and cancels out of the ratio.
            // Only the two combinations the fit reads are formed.
            var symmetric = (ux * frame.Y2) - (uy * frame.X2) + (vy * frame.X1);
            var antisymmetric = (uy * frame.Y2) + (ux * frame.X2) - (vx * frame.X1);

            var magnitude = Math.Sqrt((symmetric * symmetric) + (antisymmetric * antisymmetric));

            if (magnitude > 0d) {
                rotations[(triangle * 2) + 0] = symmetric / magnitude;
                rotations[(triangle * 2) + 1] = antisymmetric / magnitude;
            }
        }
    }
}
