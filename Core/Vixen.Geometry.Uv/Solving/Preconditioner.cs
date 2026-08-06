// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Geometry.Uv.Solving;

/// <summary>Which approximate inverse a conjugate gradient is asked to run with.</summary>
/// <remarks>
///     docs/plan/42 § D5 names two, and the reason for two is that they fail differently: Jacobi
///     always exists and barely helps, incomplete Cholesky helps a great deal and does not always
///     exist. <see cref="Preconditioner.Create" /> asks for the second and reports when it got the
///     first.
/// </remarks>
enum PreconditionerKind {
    /// <summary>None. The residual is passed through, which is plain conjugate gradient.</summary>
    None,

    /// <summary>The inverse of the diagonal. Costs one division per row and fixes scaling, nothing else.</summary>
    Jacobi,

    /// <summary>An incomplete Cholesky factor with no fill beyond the matrix's own pattern.</summary>
    IncompleteCholesky
}

/// <summary>An approximate inverse, applied once per conjugate-gradient iteration.</summary>
/// <remarks>
///     <para>
///         Built once from a matrix and applied many times, because docs/plan/42 § D5's local–global
///         loop holds the system matrix constant and moves only the right-hand side. That is the same
///         observation the textbook uses to argue for a sparse Cholesky, and here it pays for the
///         factorization of a <i>preconditioner</i> instead — which is a few hundred lines rather
///         than a supernodal solver with a fill-reducing ordering.
///     </para>
///     <para>
///         ⚠ <b>Every guard in here is relative to the row it is in, and none of them is a
///         tolerance on convergence.</b> The two are easy to confuse. A guard against dividing by a
///         zero pivot has to fire or the next iteration is NaN; a test on how small the residual has
///         got is a decision about when to stop, and § B6 forbids that one because its outcome can
///         differ across platforms. Nothing here stops anything.
///     </para>
///     <para>
///         ⚠ <b>Not thread-safe.</b> <see cref="Apply" /> writes the destination it is handed and
///         nothing else, but a single instance applied from two threads at once has no reason to be
///         correct in future. One solver, one preconditioner, one thread.
///     </para>
/// </remarks>
sealed class Preconditioner {
    /// <summary>How small a pivot may get, as a fraction of what it started as, before it counts as gone.</summary>
    /// <remarks>
    ///     ⚠ <b>Relative, and that is the whole point of the constant.</b> The same mesh imported in
    ///     millimetres rather than metres has a Laplacian scaled by a million, so an absolute
    ///     <c>1e-12</c> would call every pivot healthy at one scale and every pivot dead at the other.
    ///     <c>EditMesh.DefaultWeldTolerance</c> and docs/plan/24 § P1 record the same lesson.
    /// </remarks>
    const double RelativeEpsilon = 1e-12;

    readonly double[]? inverseDiagonal;
    readonly SparseMatrix? factor;
    readonly double[]? factorDiagonal;

    /// <summary>What this actually is, which is not always what was asked for.</summary>
    public PreconditionerKind Kind { get; }

    /// <summary>Whether an incomplete Cholesky was asked for, broke down, and became a Jacobi.</summary>
    /// <remarks>
    ///     ⚠ <b>IC(0) is not guaranteed to exist for an arbitrary symmetric positive-definite
    ///     matrix</b> — Kershaw's 4×4 is the standard counterexample and it is in the tests. A pivot
    ///     goes non-positive, the square root of it is NaN, and every iteration after that is NaN
    ///     without a single exception being thrown. So the breakdown is detected at construction, the
    ///     factor is thrown away, and this flag says so; <see cref="SolveReport" /> carries it out to
    ///     the caller rather than leaving it as something you would only find by looking.
    /// </remarks>
    public bool FellBack { get; }

    Preconditioner(PreconditionerKind kind, double[]? inverseDiagonal, SparseMatrix? factor, double[]? factorDiagonal, bool fellBack) {
        Kind = kind;
        FellBack = fellBack;
        this.inverseDiagonal = inverseDiagonal;
        this.factor = factor;
        this.factorDiagonal = factorDiagonal;
    }

    /// <summary>Builds one, falling back to Jacobi where an incomplete Cholesky does not exist.</summary>
    /// <param name="matrix">The system matrix. Square, and symmetric positive-definite in the case that matters.</param>
    /// <param name="kind">Which one to build.</param>
    /// <returns>The preconditioner. Check <see cref="FellBack" />.</returns>
    /// <exception cref="ArgumentException">The matrix is not square.</exception>
    public static Preconditioner Create(SparseMatrix matrix, PreconditionerKind kind) {
        ArgumentNullException.ThrowIfNull(matrix);

        if (!matrix.IsSquare) {
            throw new ArgumentException(
                $"A preconditioner wants a square matrix and this one is {matrix.RowCount} by {matrix.ColumnCount}.",
                nameof(matrix)
            );
        }

        if (kind == PreconditionerKind.None) {
            return new(PreconditionerKind.None, null, null, null, false);
        }

        if (kind == PreconditionerKind.IncompleteCholesky) {
            var factorized = Factorize(matrix);

            if (factorized is not null) {
                return new(PreconditionerKind.IncompleteCholesky, null, factorized.Value.Factor, factorized.Value.Diagonal, false);
            }

            return new(PreconditionerKind.Jacobi, InvertDiagonal(matrix), null, null, true);
        }

        return new(PreconditionerKind.Jacobi, InvertDiagonal(matrix), null, null, false);
    }

    /// <summary>Applies the approximate inverse.</summary>
    /// <param name="residual">What to apply it to.</param>
    /// <param name="destination">Where the result goes. May be the same array as the residual.</param>
    public void Apply(double[] residual, double[] destination) {
        switch (Kind) {
            case PreconditionerKind.None:
                if (!ReferenceEquals(residual, destination)) {
                    residual.CopyTo(destination, 0);
                }

                return;

            case PreconditionerKind.Jacobi: {
                var inverse = inverseDiagonal!;

                for (var row = 0; row < inverse.Length; row++) {
                    destination[row] = residual[row] * inverse[row];
                }

                return;
            }

            default:
                Substitute(residual, destination);
                return;
        }
    }

    /// <summary>The reciprocal of each diagonal entry, or one where there is nothing to divide by.</summary>
    /// <remarks>
    ///     ⚠ <b>A zero on the diagonal is the failure this whole function exists for.</b> It is not
    ///     exotic: an unknown that no triangle touched, a chart with a stray vertex, a row the
    ///     assembler pinned and then forgot to give a value, and the matrix has an empty row. Dividing
    ///     by it gives an infinity, the first dot product turns it into a NaN, and NaN propagates
    ///     through every remaining iteration and out into coordinates that are silently garbage. The
    ///     answer here is one — that row is left unpreconditioned, which is the identity and is always
    ///     defined.
    /// </remarks>
    static double[] InvertDiagonal(SparseMatrix matrix) {
        var inverse = new double[matrix.RowCount];

        for (var row = 0; row < matrix.RowCount; row++) {
            var diagonal = matrix.Diagonal(row);
            var magnitude = matrix.RowMagnitude(row);

            inverse[row] = Math.Abs(diagonal) > RelativeEpsilon * magnitude ? 1d / diagonal : 1d;
        }

        return inverse;
    }

    /// <summary>Factorizes with no fill beyond the matrix's own lower-triangular pattern.</summary>
    /// <returns>The factor and its diagonal, or <see langword="null" /> where a pivot did not survive.</returns>
    /// <remarks>
    ///     <para>
    ///         Left-looking, one row at a time. Row <c>i</c>'s entries are scattered into a dense
    ///         accumulator; each column <c>j</c> below the diagonal is finished in ascending order and
    ///         its contribution subtracted from every later column of the same row. The lookup of
    ///         <c>L[q][j]</c> is a binary search into a row that is already finished — <c>q ≤ i</c>
    ///         always — which is what makes the pass a single sweep rather than a fixed point.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The pivot test is <c>!(pivot &gt; epsilon × original)</c> rather than
    ///         <c>pivot &lt;= 0</c>, and the negation is deliberate</b>: written that way it catches a
    ///         NaN as well, which is what an earlier row's failed square root would have put there.
    ///     </para>
    /// </remarks>
    static (SparseMatrix Factor, double[] Diagonal)? Factorize(SparseMatrix matrix) {
        var size = matrix.RowCount;
        var starts = new int[size + 1];

        for (var row = 0; row < size; row++) {
            var kept = 0;

            for (var index = matrix.RowStart[row]; index < matrix.RowStart[row + 1]; index++) {
                if (matrix.ColumnIndex[index] <= row) {
                    kept++;
                }
            }

            starts[row + 1] = starts[row] + kept;
        }

        var columns = new int[starts[size]];
        var values = new double[starts[size]];
        var written = 0;

        for (var row = 0; row < size; row++) {
            for (var index = matrix.RowStart[row]; index < matrix.RowStart[row + 1]; index++) {
                if (matrix.ColumnIndex[index] <= row) {
                    columns[written] = matrix.ColumnIndex[index];
                    values[written] = matrix.Value[index];
                    written++;
                }
            }
        }

        var factor = new SparseMatrix(size, size, starts, columns, values);
        var diagonal = new double[size];
        var accumulator = new double[size];

        for (var row = 0; row < size; row++) {
            var first = starts[row];
            var end = starts[row + 1];

            for (var index = first; index < end; index++) {
                accumulator[columns[index]] = values[index];
            }

            // The diagonal entry is what the pivot is measured against, and a row without one has
            // nothing to measure: that is a breakdown, and the empty-row case lands here too.
            var original = end > first && columns[end - 1] == row ? values[end - 1] : 0d;

            for (var index = first; index < end && columns[index] < row; index++) {
                var column = columns[index];
                var scaled = accumulator[column] / diagonal[column];
                accumulator[column] = scaled;

                for (var later = index + 1; later < end; later++) {
                    var target = columns[later];

                    // ⚠ The last column of this row is the row itself, and its factor entry is the
                    // one just computed rather than anything in `values` — the write-back below has
                    // not happened yet, so a lookup would read the original matrix and the diagonal
                    // would come out too large. Every other target is an earlier row, finished.
                    if (target == row) {
                        accumulator[target] -= scaled * scaled;
                        continue;
                    }

                    var start = starts[target];
                    var found = Array.BinarySearch(columns, start, starts[target + 1] - start, column);

                    if (found >= 0) {
                        accumulator[target] -= scaled * values[found];
                    }
                }
            }

            var pivot = accumulator[row];

            if (!(pivot > RelativeEpsilon * original)) {
                return null;
            }

            var root = Math.Sqrt(pivot);
            diagonal[row] = root;
            accumulator[row] = root;

            for (var index = first; index < end; index++) {
                values[index] = accumulator[columns[index]];
                accumulator[columns[index]] = 0d;
            }
        }

        return (factor, diagonal);
    }

    /// <summary>Forward substitution through the factor, then backward through its transpose.</summary>
    /// <remarks>
    ///     The backward half walks the factor's rows in reverse and subtracts into earlier entries,
    ///     which is a transpose solve without storing a transpose. Both halves are serial and in index
    ///     order, which is what keeps the result the same on any thread count.
    /// </remarks>
    void Substitute(double[] residual, double[] destination) {
        var matrix = factor!;
        var diagonal = factorDiagonal!;

        if (!ReferenceEquals(residual, destination)) {
            residual.CopyTo(destination, 0);
        }

        for (var row = 0; row < matrix.RowCount; row++) {
            var sum = destination[row];

            for (var index = matrix.RowStart[row]; index < matrix.RowStart[row + 1] - 1; index++) {
                sum -= matrix.Value[index] * destination[matrix.ColumnIndex[index]];
            }

            destination[row] = sum / diagonal[row];
        }

        for (var row = matrix.RowCount - 1; row >= 0; row--) {
            var scaled = destination[row] / diagonal[row];
            destination[row] = scaled;

            for (var index = matrix.RowStart[row]; index < matrix.RowStart[row + 1] - 1; index++) {
                destination[matrix.ColumnIndex[index]] -= matrix.Value[index] * scaled;
            }
        }
    }
}
