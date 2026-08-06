// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Threading;

namespace Vixen.Geometry.Uv.Solving;

/// <summary>A preconditioned conjugate gradient over a fixed number of iterations, warm-started.</summary>
/// <remarks>
///     <para>
///         docs/plan/42 § D5 chose this over a sparse Cholesky and the argument is worth keeping next
///         to the code. In a local–global iteration the system matrix is constant and only the
///         right-hand side moves, so a factorization computed once and back-substituted per iteration
///         is asymptotically better — and a supernodal sparse Cholesky with a fill-reducing ordering
///         is a large, subtle piece of numerical software that this engine would then own. Conjugate
///         gradient <b>started from the previous iterate</b> converges in very few steps precisely
///         because consecutive solves are close, which trades the asymptotic win for a great deal
///         less to maintain. If profiling later says otherwise, the change is behind this one type.
///     </para>
///     <para>
///         <b>The shape of the API is that argument.</b> The matrix and the preconditioner are
///         constructed once; <see cref="Solve" /> takes the previous answer <i>in</i> the array it
///         writes the next one to. A caller who allocates a fresh zeroed array per solve has thrown
///         away the reason this is a conjugate gradient at all, and will need several times the
///         budget to reach the same place — which is what <c>WarmStartTests</c> measures.
///     </para>
///     <para>
///         ⚠ <b>There is no tolerance and there is no way to ask for one.</b> § B6 and § D5: a
///         residual test is a floating-point comparison whose outcome can differ across platforms, and
///         byte-identical output across platforms is a gate rather than an ambition. The budget is a
///         count. <see cref="SolveReport.Residual" /> reports where it got to and nothing reads it.
///     </para>
///     <para>
///         ⚠ <b>Only the sparse multiply is parallel, and the dot products are deliberately not.</b>
///         doc 41 § D14 rules out a floating-point reduction in a nondeterministic order; a dot
///         product split across four threads and combined pairwise is exactly one, and it is the
///         standard way this is written elsewhere. Here every reduction sums in index order on the
///         calling thread. The multiply parallelizes because each row writes only its own element.
///     </para>
///     <para>
///         ⚠ <b>Not thread-safe.</b> The working vectors are fields so that a local–global loop does
///         not allocate four arrays per iteration, which means one instance is one solve at a time.
///     </para>
/// </remarks>
sealed class ConjugateGradient {
    /// <summary>How far the residual energy may fall below a cold start's before the iteration is noise.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is not the tolerance § D5 forbids, and the difference is worth being exact
    ///         about because the two look identical.</b> A convergence tolerance is a quality target: a
    ///         caller sets it, it sits somewhere the iteration is still making real progress, and
    ///         stopping one step either side of it gives measurably different coordinates — which is
    ///         why a platform that rounds one comparison the other way produces a different asset.
    ///         This is a floor at the limit of the arithmetic. <c>rho</c> is a squared residual, so
    ///         <c>1e-30</c> of a cold start's is a relative residual of <c>1e-15</c>, which is where a
    ///         <c>double</c> stops carrying information about it. Past that point the iterates differ
    ///         in the last bit and nothing else, so which side of the floor a platform lands on cannot
    ///         change the answer.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And without it a fixed budget is worse than a tolerance rather than better.</b>
    ///         Measured on a 3×2 least-squares system: converged at iteration 4, stagnated to
    ///         iteration 40, and then diverged — <c>1e+10</c> by iteration 80, because
    ///         <c>beta = next / rho</c> with both operands at the underflow floor is not a number that
    ///         means anything. A budget of 64, which is <c>UvSettings.SolverIterations</c>'s default,
    ///         lands squarely inside that. The floor is what makes the budget safe to set generously,
    ///         which is what a fixed budget has to be.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The anchor is a <i>cold</i> start's energy and not this solve's first iterate.</b>
    ///         Anchoring to the warm start would make the floor smaller exactly when the warm start was
    ///         good, so a local–global loop whose right-hand side stopped moving would grind through
    ///         its whole budget on noise — which is the case the anchor exists for.
    ///     </para>
    /// </remarks>
    internal const double Exhausted = 1e-30;

    readonly double[] residual;
    readonly double[] applied;
    readonly double[] direction;
    readonly double[] product;

    /// <summary>Rows with nothing stored in them at all. Usually empty; see <see cref="Solve" />.</summary>
    readonly int[] unconstrained;

    /// <summary>The system, held because it is the same one across a local–global loop.</summary>
    public SparseMatrix Matrix { get; }

    /// <summary>The approximate inverse, built once.</summary>
    public Preconditioner Preconditioner { get; }

    /// <summary>Builds a solver for one matrix, factorizing the preconditioner now.</summary>
    /// <param name="matrix">The system matrix. Square, and symmetric positive-definite in the case that matters.</param>
    /// <param name="kind">Which preconditioner to build.</param>
    /// <exception cref="ArgumentException">The matrix is not square.</exception>
    public ConjugateGradient(SparseMatrix matrix, PreconditionerKind kind = PreconditionerKind.IncompleteCholesky) {
        ArgumentNullException.ThrowIfNull(matrix);

        if (!matrix.IsSquare) {
            throw new ArgumentException(
                $"A conjugate gradient wants a square matrix and this one is {matrix.RowCount} by {matrix.ColumnCount}. "
                + $"For a least-squares system use {nameof(LeastSquaresSolver)}.",
                nameof(matrix)
            );
        }

        Matrix = matrix;
        Preconditioner = Preconditioner.Create(matrix, kind);

        residual = new double[matrix.RowCount];
        applied = new double[matrix.RowCount];
        direction = new double[matrix.RowCount];
        product = new double[matrix.RowCount];

        var empty = new List<int>();

        for (var row = 0; row < matrix.RowCount; row++) {
            if (matrix.RowStart[row] == matrix.RowStart[row + 1]) {
                empty.Add(row);
            }
        }

        unconstrained = [.. empty];
    }

    /// <summary>Runs the budget, starting from whatever <paramref name="solution" /> already holds.</summary>
    /// <param name="right">The right-hand side, <c>RowCount</c> long. Read only.</param>
    /// <param name="solution">
    ///     The starting iterate on the way in and the answer on the way out. ⚠ <b>Hand back the
    ///     previous solve's array</b> — see the remarks on the type.
    /// </param>
    /// <param name="iterations">The budget. Zero is legal and leaves the warm start alone.</param>
    /// <param name="scheduler">Threads for the multiply, or <see langword="null" /> for none.</param>
    /// <param name="batchSize">How many rows one work item covers, or zero to choose one.</param>
    /// <returns>What happened, for the report.</returns>
    /// <exception cref="ArgumentException">A vector is the wrong length.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="iterations" /> is negative.</exception>
    /// <remarks>
    ///     <para>
    ///         The answer does not depend on <paramref name="scheduler" /> or on
    ///         <paramref name="batchSize" />, by a bit. Both are timing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A right-hand side of all zeros with a zero start returns immediately and leaves
    ///         zeros</b>, rather than dividing <c>0/0</c> on the first step. That is the defined
    ///         answer, and it is the answer an empty chart produces.
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

        var size = Matrix.RowCount;

        if (right.Length != size) {
            throw new ArgumentException($"The right-hand side is {right.Length} long for {size} rows.", nameof(right));
        }

        if (solution.Length != size) {
            throw new ArgumentException($"The solution is {solution.Length} long for {size} rows.", nameof(solution));
        }

        if (size == 0) {
            return new(0, Preconditioner.Kind, Preconditioner.FellBack, false, 0d);
        }

        // What a cold start would have had to work through, which is what the floor is measured
        // against. `residual` and `applied` are scratch here; their real values come next.
        right.CopyTo(residual, 0);
        Mask(residual);
        Preconditioner.Apply(residual, applied);
        var floor = Exhausted * Dot(residual, applied);

        // r = b − Ax, which is where the warm start is spent: a good x makes this small and the
        // Krylov space it generates only has to cover what is left.
        Matrix.Multiply(solution, product, scheduler, batchSize);

        for (var row = 0; row < size; row++) {
            residual[row] = right[row] - product[row];
        }

        // ⚠ An unconstrained unknown's residual is `b` forever, because nothing the iteration can do
        // touches it. Left in, it keeps `rho` above the floor while contributing nothing to the
        // curvature — so the step length is divided by a quantity that has converged and the unknown
        // runs off to infinity. Masked out, the solve is over the part of the system that exists and
        // the unknown stays exactly where the warm start put it.
        Mask(residual);
        Preconditioner.Apply(residual, applied);
        applied.CopyTo(direction, 0);

        var rho = Dot(residual, applied);
        var ran = 0;
        var stopped = false;

        for (; ran < iterations; ran++) {
            // ⚠ Not a convergence test — see `Exhausted`. `!(rho > floor)` is also true for zero and
            // for NaN, and both of those mean the next division would put an infinity into the
            // solution and never take it out again.
            if (!(rho > floor)) {
                stopped = true;
                break;
            }

            Matrix.Multiply(direction, product, scheduler, batchSize);
            var curvature = Dot(direction, product);

            if (!(curvature > 0d)) {
                stopped = true;
                break;
            }

            var step = rho / curvature;

            for (var row = 0; row < size; row++) {
                solution[row] += step * direction[row];
                residual[row] -= step * product[row];
            }

            Mask(residual);
            Preconditioner.Apply(residual, applied);
            var next = Dot(residual, applied);
            var beta = next / rho;
            rho = next;

            for (var row = 0; row < size; row++) {
                direction[row] = applied[row] + (beta * direction[row]);
            }
        }

        return new(ran, Preconditioner.Kind, Preconditioner.FellBack, stopped, TrueNorm(residual, right));
    }

    /// <summary>Zeroes the residual of every row that has nothing in it.</summary>
    void Mask(double[] vector) {
        foreach (var row in unconstrained) {
            vector[row] = 0d;
        }
    }

    /// <summary>
    ///     The norm of <c>b − Ax</c> including the rows <see cref="Mask" /> took out, because a report
    ///     that hid an unsatisfiable row would be reporting on a different system than the caller
    ///     handed in.
    /// </summary>
    double TrueNorm(double[] masked, double[] right) {
        var sum = Dot(masked, masked);

        foreach (var row in unconstrained) {
            sum += right[row] * right[row];
        }

        return Math.Sqrt(sum);
    }

    /// <summary>A dot product, summed in index order on one thread. See the type's remarks.</summary>
    internal static double Dot(double[] left, double[] right) {
        var sum = 0d;

        for (var index = 0; index < left.Length; index++) {
            sum += left[index] * right[index];
        }

        return sum;
    }

    /// <summary>The Euclidean norm, summed in index order.</summary>
    internal static double Norm(double[] vector) => Math.Sqrt(Dot(vector, vector));
}
