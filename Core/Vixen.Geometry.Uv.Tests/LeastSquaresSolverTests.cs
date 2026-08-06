// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using CsCheck;
using Vixen.Geometry.Uv.Solving;
using Xunit;

namespace Vixen.Geometry.Uv.Tests;

/// <summary>The rectangular path, which is the one LSCM takes.</summary>
/// <remarks>
///     docs/plan/42 § D5's first rung is "one sparse least-squares solve". The reference here forms
///     <c>AᵀA</c> densely on purpose — that is exactly what the solver must not do, so an oracle that
///     does it is testing the thing that matters.
/// </remarks>
public class LeastSquaresSolverTests {
    /// <summary>A rectangular system with more rows than columns and full column rank.</summary>
    static readonly Gen<(int Rows, int Columns, double[] Matrix, double[] Right)> Systems =
        from columns in Gen.Int[2, 8]
        from extra in Gen.Int[0, 8]
        from entries in Gen.Double[-2d, 2d].Array[(columns + extra) * columns]
        from right in Gen.Double[-4d, 4d].Array[columns + extra]
        select (columns + extra, columns, WithFullRank(entries, columns + extra, columns), right);

    /// <summary>Fitting a line through three points, done on paper first.</summary>
    [Fact]
    public void A_line_fit_matches_the_arithmetic() {
        var builder = new SparseMatrixBuilder(3, 2);

        for (var row = 0; row < 3; row++) {
            builder.Add(row, 0, 1d);
            builder.Add(row, 1, row + 1);
        }

        var solution = new double[2];

        // AᵀA is [[3, 6], [6, 14]] and Aᵀb is [5, 11]; the determinant is 6, so the intercept is
        // 2/3 and the slope is 1/2.
        var report = new LeastSquaresSolver(builder.Build()).Solve([1d, 2d, 2d], solution, 32);

        Assert.Equal(2d / 3d, solution[0], 10);
        Assert.Equal(0.5d, solution[1], 10);
        Assert.False(report.PreconditionerFellBack);
    }

    /// <summary>A square, full-rank system has an exact answer and least squares must find it.</summary>
    [Fact]
    public void A_consistent_system_is_solved_exactly() {
        var builder = new SparseMatrixBuilder(3, 3);
        builder.Add(0, 0, 2d);
        builder.Add(0, 1, 1d);
        builder.Add(1, 1, 3d);
        builder.Add(1, 2, -1d);
        builder.Add(2, 0, 1d);
        builder.Add(2, 2, 4d);

        var solution = new double[3];
        var report = new LeastSquaresSolver(builder.Build()).Solve([5d, 5d, 9d], solution, 64);

        // Square and full rank, so the least-squares answer is the exact one and the residual is
        // zero. Checking A x rather than x is the statement that does not depend on working the
        // inverse out by hand.
        Assert.True(report.Residual < 1e-8d, $"The residual came out {report.Residual}.");

        var product = new double[3];
        builder.Build().Multiply(solution, product);

        Assert.Equal(5d, product[0], 8);
        Assert.Equal(5d, product[1], 8);
        Assert.Equal(9d, product[2], 8);
    }

    /// <summary>
    ///     Against the dense normal equations, on the quantity least squares is actually about.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The residual and not the unknowns, and that is not a weaker claim.</b> A least-squares
    ///     minimum can be flat: two answers a hundredth apart can fit the data equally well, and on a
    ///     nearly rank-deficient draw the dense oracle and this solver pick different points on the
    ///     same floor. Comparing unknowns there fails on the oracle. Comparing <c>‖b − Ax‖</c> asks
    ///     the question the method exists to answer — and an implementation that got the unknowns
    ///     wrong in any way that mattered would fail it.
    /// </remarks>
    [Fact]
    public void It_agrees_with_the_dense_normal_equations() {
        Systems.Sample(system => {
                var (rows, columns, dense, right) = system;
                var expected = SolveNormalEquations(dense, right, rows, columns);

                var solution = new double[columns];
                var report = new LeastSquaresSolver(ToSparse(dense, rows, columns)).Solve(right, solution, columns * 40);

                Assert.All(solution, value => Assert.True(double.IsFinite(value), $"{value} is not finite."));
                AssertFitsAtLeastAsWell(report.Residual, expected, dense, right, rows, columns);
            },
            iter: 300
        );
    }

    /// <summary>
    ///     ⚠ A column of nothing is two unknowns per unreferenced vertex in LSCM. The diagonal of
    ///     <c>AᵀA</c> is zero there, and one over it is an infinity.
    /// </summary>
    [Fact]
    public void An_unreferenced_column_is_left_where_the_warm_start_put_it() {
        var builder = new SparseMatrixBuilder(3, 3);
        builder.Add(0, 0, 1d);
        builder.Add(1, 0, 1d);
        builder.Add(2, 2, 2d);

        var solution = new double[] { 0d, -4.5d, 0d };
        var report = new LeastSquaresSolver(builder.Build()).Solve([2d, 2d, 6d], solution, 32);

        Assert.Equal(2d, solution[0], 10);
        Assert.Equal(-4.5d, solution[1]);
        Assert.Equal(3d, solution[2], 10);
        Assert.True(double.IsFinite(report.Residual));
    }

    /// <summary>A matrix of nothing: the guard fires and no NaN reaches the caller.</summary>
    [Fact]
    public void A_matrix_of_zeros_stops_rather_than_producing_a_nan() {
        var builder = new SparseMatrixBuilder(3, 2);
        builder.Add(0, 0, 0d);
        builder.Add(1, 1, 0d);

        var solution = new double[2];
        var report = new LeastSquaresSolver(builder.Build()).Solve([1d, 2d, 3d], solution, 16);

        Assert.True(report.StoppedEarly);
        Assert.All(solution, value => Assert.True(double.IsFinite(value), $"{value} is not finite."));
    }

    /// <summary>The unpreconditioned leg, so a bug in the scaling cannot hide behind the scaling.</summary>
    [Fact]
    public void It_agrees_with_the_dense_normal_equations_unpreconditioned() {
        Systems.Sample(system => {
                var (rows, columns, dense, right) = system;
                var expected = SolveNormalEquations(dense, right, rows, columns);

                var solution = new double[columns];

                var report = new LeastSquaresSolver(ToSparse(dense, rows, columns), preconditioned: false).Solve(
                    right,
                    solution,
                    columns * 40
                );

                Assert.All(solution, value => Assert.True(double.IsFinite(value), $"{value} is not finite."));
                AssertFitsAtLeastAsWell(report.Residual, expected, dense, right, rows, columns);
            },
            iter: 300
        );
    }

    /// <summary>
    ///     ⚠ A budget many times what convergence needs, which is what caught the divergence
    ///     <see cref="ConjugateGradient.Exhausted" /> exists for.
    /// </summary>
    /// <remarks>
    ///     A budget is a count set once for every chart in a mesh, so it is set generously and most
    ///     solves finish long before it runs out. Before the exhaustion floor this system converged at
    ///     iteration 4, sat still until iteration 40, and then ran away to <c>1e+10</c> by iteration
    ///     80 — with no exception, no NaN, and coordinates that look like coordinates.
    /// </remarks>
    [Fact]
    public void A_budget_far_past_convergence_does_not_run_away() {
        var builder = new SparseMatrixBuilder(3, 2);
        double[] dense = [41d / 14d, 27d / 14d, -1d, 2d, 57d / 32d, 0d];

        for (var row = 0; row < 3; row++) {
            for (var column = 0; column < 2; column++) {
                if (dense[(row * 2) + column] != 0d) {
                    builder.Add(row, column, dense[(row * 2) + column]);
                }
            }
        }

        var matrix = builder.Build();
        double[] right = [2.109171695124708d, 34d / 33d, -2d];

        var near = new double[2];
        var nearReport = new LeastSquaresSolver(matrix, preconditioned: false).Solve(right, near, 8);

        var far = new double[2];
        var farReport = new LeastSquaresSolver(matrix, preconditioned: false).Solve(right, far, 4000);

        Assert.True(farReport.StoppedEarly, "The floor never fired, so this proves nothing.");
        Assert.Equal(near[0], far[0], 10);
        Assert.Equal(near[1], far[1], 10);
        Assert.Equal(nearReport.Residual, farReport.Residual, 10);
    }

    /// <summary>⚠ Relative again: the same fit at either end of six orders of magnitude.</summary>
    [Theory]
    [InlineData(1e-3d)]
    [InlineData(1e+3d)]
    public void The_fit_is_the_same_at_any_scale(double scale) {
        var builder = new SparseMatrixBuilder(4, 2);
        var scaledBuilder = new SparseMatrixBuilder(4, 2);

        for (var row = 0; row < 4; row++) {
            builder.Add(row, 0, 1d);
            builder.Add(row, 1, row);
            scaledBuilder.Add(row, 0, scale);
            scaledBuilder.Add(row, 1, row * scale);
        }

        double[] right = [1d, 3d, 4d, 6d];

        var plain = new double[2];
        new LeastSquaresSolver(builder.Build()).Solve(right, plain, 32);

        var scaled = new double[2];
        new LeastSquaresSolver(scaledBuilder.Build()).Solve(right.Select(value => value * scale).ToArray(), scaled, 32);

        Assert.Equal(plain[0], scaled[0], 8);
        Assert.Equal(plain[1], scaled[1], 8);
    }

    /// <summary>Sparsifies, then makes the leading square block dominant so the column rank is full.</summary>
    /// <remarks>
    ///     Without the second half a drawn system is singular often enough to matter, and a singular
    ///     one would fail against a dense oracle that is itself dividing by nothing — which would be a
    ///     test failing on the oracle rather than on the solver.
    /// </remarks>
    static double[] WithFullRank(double[] entries, int rows, int columns) {
        var matrix = (double[])entries.Clone();

        for (var row = 0; row < rows; row++) {
            for (var column = 0; column < columns; column++) {
                if (Math.Abs(matrix[(row * columns) + column]) < 1d) {
                    matrix[(row * columns) + column] = 0d;
                }
            }
        }

        for (var row = 0; row < columns; row++) {
            var sum = 0d;

            for (var column = 0; column < columns; column++) {
                if (column != row) {
                    sum += Math.Abs(matrix[(row * columns) + column]);
                }
            }

            matrix[(row * columns) + row] = sum + 1d;
        }

        return matrix;
    }

    static SparseMatrix ToSparse(double[] dense, int rows, int columns) {
        var builder = new SparseMatrixBuilder(rows, columns);

        for (var row = 0; row < rows; row++) {
            for (var column = 0; column < columns; column++) {
                if (dense[(row * columns) + column] != 0d) {
                    builder.Add(row, column, dense[(row * columns) + column]);
                }
            }
        }

        return builder.Build();
    }

    /// <summary>Asserts the solve fits no worse than the dense oracle, allowing for a flat minimum.</summary>
    static void AssertFitsAtLeastAsWell(
        double achieved,
        double[] expected,
        double[] dense,
        double[] right,
        int rows,
        int columns
    ) {
        var reference = 0d;

        for (var row = 0; row < rows; row++) {
            var sum = right[row];

            for (var column = 0; column < columns; column++) {
                sum -= dense[(row * columns) + column] * expected[column];
            }

            reference += sum * sum;
        }

        reference = Math.Sqrt(reference);

        Assert.True(
            achieved <= (reference * (1d + 1e-6d)) + 1e-9d,
            $"The solve left a residual of {achieved} where Gaussian elimination on the dense normal "
            + $"equations left {reference}."
        );
    }

    /// <summary>The oracle: form <c>AᵀA</c> and <c>Aᵀb</c> densely, then eliminate.</summary>
    static double[] SolveNormalEquations(double[] dense, double[] right, int rows, int columns) {
        var normal = new double[columns * columns];
        var side = new double[columns];

        for (var first = 0; first < columns; first++) {
            for (var second = 0; second < columns; second++) {
                var sum = 0d;

                for (var row = 0; row < rows; row++) {
                    sum += dense[(row * columns) + first] * dense[(row * columns) + second];
                }

                normal[(first * columns) + second] = sum;
            }

            var product = 0d;

            for (var row = 0; row < rows; row++) {
                product += dense[(row * columns) + first] * right[row];
            }

            side[first] = product;
        }

        return DenseReference.Solve(normal, side, columns);
    }
}
