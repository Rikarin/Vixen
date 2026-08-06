// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Threading;

namespace Vixen.Geometry.Uv.Solving;

/// <summary>Solves <c>AᵀA x = Aᵀb</c> without ever forming <c>AᵀA</c>.</summary>
/// <remarks>
///     <para>
///         docs/plan/42 § D5's first rung. LSCM is a least-squares problem — two rows per triangle,
///         two columns per vertex, overdetermined and rectangular — and its normal equations are what
///         a conjugate gradient wants. Forming them is the obvious move and the wrong one twice over:
///         <c>AᵀA</c> is denser than <c>A</c> by roughly the square of the vertex valence, and its
///         condition number is the square of <c>A</c>'s, so a matrix a <c>double</c> handles
///         comfortably becomes one it does not.
///     </para>
///     <para>
///         So this is CGLS: the iteration is a conjugate gradient on <c>AᵀA</c> written entirely in
///         terms of one multiply by <c>A</c> and one by <c>Aᵀ</c>, and the residual it carries is
///         <c>b − Ax</c> in the original space rather than the normal-equation one. The transpose is
///         stored once, which is what keeps <c>Aᵀr</c> an ordinary sparse multiply — see
///         <see cref="SparseMatrix.Transpose" /> for why that matters to determinism and not just to
///         speed.
///     </para>
///     <para>
///         ⚠ <b>Incomplete Cholesky has nothing to factorize here</b>, because the matrix it would
///         factorize is the one this type exists not to build. Jacobi does apply: the diagonal of
///         <c>AᵀA</c> is the sum of squares down each column of <c>A</c>, which is one pass over the
///         nonzeros. A column of <c>A</c> that is entirely zero — an unknown no constraint mentions,
///         which in LSCM is a vertex no triangle uses — gives a zero there, and the guard for it is
///         the same relative one as everywhere else in this namespace.
///     </para>
///     <para>
///         ⚠ <b>Fixed budget, warm start, no tolerance</b>, for the reasons on
///         <see cref="ConjugateGradient" />. ⚠ <b>Not thread-safe</b>, for the same reason.
///     </para>
/// </remarks>
sealed class LeastSquaresSolver {
    /// <summary>How small a column norm may be, against the largest, before its unknown is left unscaled.</summary>
    const double RelativeEpsilon = 1e-12;

    readonly SparseMatrix transpose;
    readonly double[] inverseDiagonal;

    /// <summary>
    ///     <c>Σⱼ mⱼ‖aⱼ‖²</c> over the columns: what turns a residual's size into the largest gradient
    ///     that residual could produce. See <see cref="Solve" />'s floor.
    /// </summary>
    readonly double columnEnergy;

    readonly double[] residual;
    readonly double[] gradient;
    readonly double[] applied;
    readonly double[] direction;
    readonly double[] product;

    /// <summary>The rectangular system.</summary>
    public SparseMatrix Matrix { get; }

    /// <summary>Whether the normal equations' diagonal is used to scale each unknown.</summary>
    public bool Preconditioned { get; }

    /// <summary>Builds a solver for one rectangular system, transposing it now.</summary>
    /// <param name="matrix">The system. Any shape; <c>rows ≥ columns</c> is the case it is for.</param>
    /// <param name="preconditioned">Whether to scale by the normal equations' diagonal. Usually yes.</param>
    public LeastSquaresSolver(SparseMatrix matrix, bool preconditioned = true) {
        ArgumentNullException.ThrowIfNull(matrix);

        Matrix = matrix;
        Preconditioned = preconditioned;
        transpose = matrix.Transpose();

        residual = new double[matrix.RowCount];
        product = new double[matrix.RowCount];
        gradient = new double[matrix.ColumnCount];
        applied = new double[matrix.ColumnCount];
        direction = new double[matrix.ColumnCount];
        inverseDiagonal = ColumnScaling(transpose, preconditioned, out columnEnergy);
    }

    /// <summary>Runs the budget, starting from whatever <paramref name="solution" /> already holds.</summary>
    /// <param name="right">The right-hand side, <c>RowCount</c> long. Read only.</param>
    /// <param name="solution">The starting iterate in, the answer out. <c>ColumnCount</c> long.</param>
    /// <param name="iterations">The budget. Zero leaves the warm start alone.</param>
    /// <param name="scheduler">Threads for the two multiplies, or <see langword="null" /> for none.</param>
    /// <param name="batchSize">How many rows one work item covers, or zero to choose one.</param>
    /// <returns>What happened. <see cref="SolveReport.Residual" /> is <c>‖b − Ax‖</c>, not <c>‖Aᵀ(b − Ax)‖</c>.</returns>
    /// <exception cref="ArgumentException">A vector is the wrong length.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="iterations" /> is negative.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The exhaustion floor is anchored to <c>‖b‖</c> and not to <c>‖Aᵀb‖</c>, and
    ///         anchoring it to the gradient was a bug that survived a property suite for a while.</b>
    ///         <see cref="ConjugateGradient" /> iterates on the residual and measures its floor against
    ///         the residual a cold start would have carried, which is the same quantity in the same
    ///         units. CGLS iterates on the <i>gradient</i> <c>Aᵀr</c>, and the two are not
    ///         interchangeable: for a square system <c>r</c> goes to zero, but a least-squares
    ///         <c>r</c> converges to whatever part of <c>b</c> lies outside <c>A</c>'s column space
    ///         and stays there. What goes to zero is <c>Aᵀr</c>, and how far down it can go is set by
    ///         the rounding in forming it — about <c>ε‖aⱼ‖‖r‖</c> per column, so
    ///         <c>ε²‖r‖²·Σⱼ mⱼ‖aⱼ‖²</c> in <c>rho</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>So a floor of <c>1e-30·‖Aᵀb‖²</c> is unreachable exactly when <c>b</c> is nearly
    ///         orthogonal to the columns</b> — which is not an exotic system, it is the ordinary
    ///         inconsistent least-squares one, and the worse the fit the further out of reach the floor
    ///         goes. Measured on a well-conditioned 10×2 draw with <c>‖b‖ = 6.7</c> and
    ///         <c>‖Aᵀb‖ = 0.023</c>: the cancellation in <c>Aᵀb</c> put the floor at <c>5.5e-34</c>,
    ///         <c>rho</c> bottomed out at <c>7.9e-31</c> on iteration 2 with the answer already exact,
    ///         and the guard sat 1400× below a number that could not fall any further. The remaining
    ///         78 iterations of the budget amplified rounding noise to a residual of <c>1e+20</c>.
    ///         <c>‖b‖²·Σⱼ mⱼ‖aⱼ‖²</c> is the largest gradient energy a residual the size of a cold
    ///         start's could carry, so <c>1e-30</c> of it is where <c>Aᵀr</c> stops meaning anything
    ///         whatever angle <c>b</c> sits at. It is never below the old anchor — Cauchy–Schwarz — so
    ///         nothing that stopped before stops later.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It is still a floor and not the tolerance § D5 forbids</b>, for the reason on
    ///         <see cref="ConjugateGradient.Exhausted" />: the test it spells out is
    ///         <c>‖Aᵀr‖ₘ &gt; 1e-15·‖A‖ₘ‖b‖</c>, a relative gradient of <c>1e-15</c>, which is where a
    ///         <c>double</c> has stopped carrying one. <c>ExhaustionFloorTests</c> holds it to that.
    ///     </para>
    /// </remarks>
    public SolveReport Solve(
        double[] right,
        double[] solution,
        int iterations,
        JobScheduler? scheduler = null,
        int batchSize = 0
    ) {
        ArgumentNullException.ThrowIfNull(right);
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentOutOfRangeException.ThrowIfNegative(iterations);

        var rows = Matrix.RowCount;
        var columns = Matrix.ColumnCount;

        if (right.Length != rows) {
            throw new ArgumentException($"The right-hand side is {right.Length} long for {rows} rows.", nameof(right));
        }

        if (solution.Length != columns) {
            throw new ArgumentException(
                $"The solution is {solution.Length} long for {columns} columns.",
                nameof(solution)
            );
        }

        var kind = Preconditioned ? PreconditionerKind.Jacobi : PreconditionerKind.None;

        if (rows == 0 || columns == 0) {
            return new(0, kind, false, false, 0d);
        }

        // The exhaustion floor. See ConjugateGradient.Exhausted for why there is one at all — this is
        // the path it was measured diverging on — and the remarks on this method for why the anchor is
        // the residual a cold start would have carried and not the gradient it would have begun from.
        var floor = ConjugateGradient.Exhausted * columnEnergy * ConjugateGradient.Dot(right, right);

        Matrix.Multiply(solution, product, scheduler, batchSize);

        for (var row = 0; row < rows; row++) {
            residual[row] = right[row] - product[row];
        }

        transpose.Multiply(residual, gradient, scheduler, batchSize);
        Scale(gradient, applied);
        applied.CopyTo(direction, 0);

        var rho = ConjugateGradient.Dot(gradient, applied);
        var ran = 0;
        var stopped = false;

        for (; ran < iterations; ran++) {
            if (!(rho > floor)) {
                stopped = true;
                break;
            }

            Matrix.Multiply(direction, product, scheduler, batchSize);

            // ⚠ ‖Ap‖² and not pᵀ(AᵀA)p spelled out, which is the same number and one multiply fewer.
            // Zero means A maps the direction to nothing — a rank-deficient system, not a converged
            // one — and dividing by it is the NaN this guard exists for.
            var curvature = ConjugateGradient.Dot(product, product);

            if (!(curvature > 0d)) {
                stopped = true;
                break;
            }

            var step = rho / curvature;

            for (var column = 0; column < columns; column++) {
                solution[column] += step * direction[column];
            }

            for (var row = 0; row < rows; row++) {
                residual[row] -= step * product[row];
            }

            transpose.Multiply(residual, gradient, scheduler, batchSize);
            Scale(gradient, applied);

            var next = ConjugateGradient.Dot(gradient, applied);
            var beta = next / rho;
            rho = next;

            for (var column = 0; column < columns; column++) {
                direction[column] = applied[column] + (beta * direction[column]);
            }
        }

        return new(ran, kind, false, stopped, ConjugateGradient.Norm(residual));
    }

    /// <summary>The reciprocal of each column's sum of squares, or one where the column is empty.</summary>
    /// <param name="transpose">The transposed system, whose rows are the columns being scaled.</param>
    /// <param name="preconditioned">Whether to scale at all.</param>
    /// <param name="energy">
    ///     <c>Σⱼ mⱼ‖aⱼ‖²</c> with the scaling applied — <c>‖A‖²_F</c> unpreconditioned, and the count
    ///     of columns that carry anything under Jacobi. <see cref="Solve" />'s floor is this times a
    ///     residual energy, and summing it here is what keeps it out of the per-solve path.
    /// </param>
    /// <remarks>
    ///     ⚠ <b>The empty column is not a hypothetical.</b> LSCM has two unknowns per vertex and the
    ///     constraints come from triangles, so a vertex that no surviving triangle references — a
    ///     stray left by conditioning, docs/plan/41 § D3 — is two columns of nothing. One is the
    ///     identity for those unknowns, which leaves them where the warm start put them instead of
    ///     making the whole solve NaN. Such a column contributes nothing to <paramref name="energy" />
    ///     either, which is right: no residual reaches it, so it can carry no gradient.
    /// </remarks>
    static double[] ColumnScaling(SparseMatrix transpose, bool preconditioned, out double energy) {
        // ⚠ Both legs need the column norms now — the unpreconditioned one does not scale by them but
        // its floor is still measured in them — so they are gathered before the branch.
        var scaling = new double[transpose.RowCount];
        var largest = 0d;

        for (var column = 0; column < transpose.RowCount; column++) {
            var sum = 0d;

            for (var index = transpose.RowStart[column]; index < transpose.RowStart[column + 1]; index++) {
                var value = transpose.Value[index];
                sum += value * value;
            }

            scaling[column] = sum;

            if (sum > largest) {
                largest = sum;
            }
        }

        var total = 0d;

        for (var column = 0; column < scaling.Length; column++) {
            var norm = scaling[column];

            // Relative to the largest column, because the whole matrix carries the mesh's units and an
            // absolute floor here would be a claim about how big the model is.
            scaling[column] = preconditioned && norm > RelativeEpsilon * largest ? 1d / norm : 1d;
            total += norm * scaling[column];
        }

        energy = total;

        return scaling;
    }

    void Scale(double[] source, double[] destination) {
        for (var index = 0; index < source.Length; index++) {
            destination[index] = source[index] * inverseDiagonal[index];
        }
    }
}
