// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Geometry.Uv.Solving;

namespace Vixen.Geometry.Uv.Tests;

/// <summary>A dense oracle: Gaussian elimination, and a Cholesky that only reports whether it worked.</summary>
/// <remarks>
///     <para>
///         A conjugate gradient tested only against systems whose answer someone worked out by hand is
///         tested on the systems someone could work out by hand, which are the easy ones. This is the
///         other half — an <c>O(n³)</c> method with a completely different failure surface, run on the
///         same random systems, so that agreeing with it means something.
///     </para>
///     <para>
///         ⚠ <b><see cref="IsPositiveDefinite" /> is here to make one specific claim checkable</b>:
///         that Kershaw's matrix, on which the incomplete Cholesky in <see cref="Preconditioner" />
///         breaks down, really is symmetric positive-definite. Without it that test proves the solver
///         rejects a matrix, not that it rejects a matrix it should have accepted.
///     </para>
/// </remarks>
static class DenseReference {
    /// <summary>Solves by Gaussian elimination with partial pivoting.</summary>
    /// <param name="matrix">The system, row-major and <paramref name="size" /> squared.</param>
    /// <param name="right">The right-hand side.</param>
    /// <param name="size">How many unknowns.</param>
    /// <returns>The solution.</returns>
    public static double[] Solve(double[] matrix, double[] right, int size) {
        var work = (double[])matrix.Clone();
        var answer = (double[])right.Clone();

        for (var pivot = 0; pivot < size; pivot++) {
            var best = pivot;

            for (var row = pivot + 1; row < size; row++) {
                if (Math.Abs(work[(row * size) + pivot]) > Math.Abs(work[(best * size) + pivot])) {
                    best = row;
                }
            }

            if (best != pivot) {
                for (var column = 0; column < size; column++) {
                    (work[(pivot * size) + column], work[(best * size) + column]) =
                        (work[(best * size) + column], work[(pivot * size) + column]);
                }

                (answer[pivot], answer[best]) = (answer[best], answer[pivot]);
            }

            var diagonal = work[(pivot * size) + pivot];

            for (var row = pivot + 1; row < size; row++) {
                var factor = work[(row * size) + pivot] / diagonal;

                if (factor == 0d) {
                    continue;
                }

                for (var column = pivot; column < size; column++) {
                    work[(row * size) + column] -= factor * work[(pivot * size) + column];
                }

                answer[row] -= factor * answer[pivot];
            }
        }

        for (var row = size - 1; row >= 0; row--) {
            var sum = answer[row];

            for (var column = row + 1; column < size; column++) {
                sum -= work[(row * size) + column] * answer[column];
            }

            answer[row] = sum / work[(row * size) + row];
        }

        return answer;
    }

    /// <summary>Whether a dense Cholesky runs to the end, which is the definition of positive-definite.</summary>
    /// <param name="matrix">The system, row-major and <paramref name="size" /> squared.</param>
    /// <param name="size">How many unknowns.</param>
    /// <returns>Whether every pivot stayed positive.</returns>
    public static bool IsPositiveDefinite(double[] matrix, int size) {
        var factor = new double[size * size];

        for (var row = 0; row < size; row++) {
            for (var column = 0; column <= row; column++) {
                var sum = matrix[(row * size) + column];

                for (var index = 0; index < column; index++) {
                    sum -= factor[(row * size) + index] * factor[(column * size) + index];
                }

                if (column < row) {
                    factor[(row * size) + column] = sum / factor[(column * size) + column];
                    continue;
                }

                if (sum <= 0d) {
                    return false;
                }

                factor[(row * size) + row] = Math.Sqrt(sum);
            }
        }

        return true;
    }

    /// <summary>Turns a dense row-major matrix into a sparse one, dropping exact zeros.</summary>
    /// <param name="matrix">The system, row-major and <paramref name="size" /> squared.</param>
    /// <param name="size">How many unknowns.</param>
    /// <returns>The same matrix, sparse.</returns>
    public static SparseMatrix ToSparse(double[] matrix, int size) {
        var builder = new SparseMatrixBuilder(size, size);

        for (var row = 0; row < size; row++) {
            for (var column = 0; column < size; column++) {
                if (matrix[(row * size) + column] != 0d) {
                    builder.Add(row, column, matrix[(row * size) + column]);
                }
            }
        }

        return builder.Build();
    }
}
