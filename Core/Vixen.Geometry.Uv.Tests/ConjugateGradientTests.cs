// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using CsCheck;
using Vixen.Geometry.Uv.Solving;
using Xunit;

namespace Vixen.Geometry.Uv.Tests;

/// <summary>The solve, against arithmetic that was done by hand and against a method with other failures.</summary>
/// <remarks>
///     docs/plan/42 § D5. Three oracles, because each catches what the others cannot: two systems
///     small enough to solve on paper, a grid Laplacian whose eigenpairs are exact, and Gaussian
///     elimination over random systems from CsCheck.
/// </remarks>
public class ConjugateGradientTests {
    /// <summary>Symmetric, diagonally dominant, sparse, and a right-hand side — everything a solve needs.</summary>
    /// <remarks>
    ///     Dominance is what makes the generated matrix positive-definite without rejecting most of
    ///     what CsCheck produces, and the sparsity is cut by magnitude so that the pattern varies with
    ///     the sample rather than being fixed.
    /// </remarks>
    static readonly Gen<(int Size, double[] Matrix, double[] Right)> Systems =
        from size in Gen.Int[3, 14]
        from entries in Gen.Double[-1d, 1d].Array[size * size]
        from bump in Gen.Double[0.25d, 4d]
        from right in Gen.Double[-5d, 5d].Array[size]
        select (size, Dominant(entries, size, bump), right);

    [Fact]
    public void A_two_by_two_matches_the_arithmetic() {
        var builder = new SparseMatrixBuilder(2, 2);
        builder.Add(0, 0, 4d);
        builder.Add(0, 1, 1d);
        builder.Add(1, 0, 1d);
        builder.Add(1, 1, 3d);

        var solution = new double[2];
        var report = new ConjugateGradient(builder.Build()).Solve([1d, 2d], solution, 16);

        // Cramer: the determinant is 11, so x = 1/11 and y = 7/11.
        Assert.Equal(1d / 11d, solution[0], 12);
        Assert.Equal(7d / 11d, solution[1], 12);
        Assert.False(report.PreconditionerFellBack);
    }

    /// <summary>The one-dimensional Laplacian, whose answer is all ones for this right-hand side.</summary>
    [Fact]
    public void A_three_by_three_laplacian_matches_the_arithmetic() {
        var builder = new SparseMatrixBuilder(3, 3);
        builder.Add(0, 0, 2d);
        builder.Add(0, 1, -1d);
        builder.Add(1, 0, -1d);
        builder.Add(1, 1, 2d);
        builder.Add(1, 2, -1d);
        builder.Add(2, 1, -1d);
        builder.Add(2, 2, 2d);

        var solution = new double[3];
        new ConjugateGradient(builder.Build()).Solve([1d, 0d, 1d], solution, 24);

        Assert.Equal(1d, solution[0], 12);
        Assert.Equal(1d, solution[1], 12);
        Assert.Equal(1d, solution[2], 12);
    }

    /// <summary>
    ///     A grid whose exact eigenpairs are known, so the expected answer is a sine and not another
    ///     implementation's output.
    /// </summary>
    [Theory]
    [InlineData((int)PreconditionerKind.None)]
    [InlineData((int)PreconditionerKind.Jacobi)]
    [InlineData((int)PreconditionerKind.IncompleteCholesky)]
    public void The_poisson_grid_recovers_its_own_eigenvector(int ordinal) {
        const int Extent = 16;

        // ⚠ The parameter is an ordinal because `PreconditionerKind` is internal and a public test
        // method cannot name it. The cast in the attribute is a compile-time constant, so the three
        // legs still read as the three preconditioners.
        var kind = (PreconditionerKind)ordinal;

        var matrix = PoissonGrid.Matrix(Extent);
        var expected = PoissonGrid.Eigenvector(Extent, 1, 1);
        var eigenvalue = PoissonGrid.Eigenvalue(Extent, 1, 1);
        var right = expected.Select(value => eigenvalue * value).ToArray();

        var solution = new double[matrix.RowCount];
        var report = new ConjugateGradient(matrix, kind).Solve(right, solution, matrix.RowCount);

        Assert.Equal(kind, report.Preconditioner);

        for (var index = 0; index < expected.Length; index++) {
            Assert.Equal(expected[index], solution[index], 10);
        }
    }

    /// <summary>The second mode too, because the first one is symmetric enough to pass by accident.</summary>
    [Fact]
    public void The_poisson_grid_recovers_a_higher_mode() {
        const int Extent = 12;

        var matrix = PoissonGrid.Matrix(Extent);
        var expected = PoissonGrid.Eigenvector(Extent, 3, 2);
        var right = expected.Select(value => PoissonGrid.Eigenvalue(Extent, 3, 2) * value).ToArray();

        var solution = new double[matrix.RowCount];
        new ConjugateGradient(matrix).Solve(right, solution, matrix.RowCount);

        for (var index = 0; index < expected.Length; index++) {
            Assert.Equal(expected[index], solution[index], 10);
        }
    }

    /// <summary>Against Gaussian elimination, over systems nobody chose.</summary>
    [Fact]
    public void It_agrees_with_gaussian_elimination_on_random_systems() {
        Systems.Sample(system => {
                var (size, dense, right) = system;
                var expected = DenseReference.Solve(dense, right, size);

                var solution = new double[size];
                var report = new ConjugateGradient(DenseReference.ToSparse(dense, size)).Solve(
                    right,
                    solution,
                    size * 8
                );

                Assert.False(double.IsNaN(report.Residual));

                for (var index = 0; index < size; index++) {
                    Assert.True(
                        Math.Abs(expected[index] - solution[index]) < 1e-8d,
                        $"Unknown {index} of {size} came out {solution[index]} against {expected[index]}."
                    );
                }
            },
            iter: 500
        );
    }

    /// <summary>The same, with no preconditioner at all, so a bug in one cannot hide behind the other.</summary>
    [Fact]
    public void It_agrees_with_gaussian_elimination_unpreconditioned() {
        Systems.Sample(system => {
                var (size, dense, right) = system;
                var expected = DenseReference.Solve(dense, right, size);

                var solution = new double[size];

                new ConjugateGradient(DenseReference.ToSparse(dense, size), PreconditionerKind.None).Solve(
                    right,
                    solution,
                    size * 8
                );

                for (var index = 0; index < size; index++) {
                    Assert.True(
                        Math.Abs(expected[index] - solution[index]) < 1e-8d,
                        $"Unknown {index} of {size} came out {solution[index]} against {expected[index]}."
                    );
                }
            },
            iter: 500
        );
    }

    [Fact]
    public void A_rectangular_matrix_is_refused() {
        var matrix = new SparseMatrixBuilder(3, 2).Build();

        var thrown = Assert.Throws<ArgumentException>(() => new ConjugateGradient(matrix));
        Assert.Contains(nameof(LeastSquaresSolver), thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_negative_budget_is_refused() {
        var solver = new ConjugateGradient(PoissonGrid.Matrix(3));

        Assert.Throws<ArgumentOutOfRangeException>(() => solver.Solve(new double[9], new double[9], -1));
    }

    /// <summary>A budget of zero is legal, does nothing, and reports where the warm start already was.</summary>
    [Fact]
    public void A_budget_of_zero_leaves_the_warm_start_alone() {
        var matrix = PoissonGrid.Matrix(4);
        var solution = Enumerable.Range(0, matrix.RowCount).Select(index => index * 0.25d).ToArray();
        var copy = (double[])solution.Clone();

        var report = new ConjugateGradient(matrix).Solve(new double[matrix.RowCount], solution, 0);

        Assert.Equal(0, report.Iterations);
        Assert.Equal(copy, solution);
        Assert.True(report.Residual > 0d);
    }

    /// <summary>Symmetric, diagonally dominant and sparse. Row-major.</summary>
    static double[] Dominant(double[] entries, int size, double bump) {
        var matrix = new double[size * size];

        for (var row = 0; row < size; row++) {
            for (var column = row + 1; column < size; column++) {
                var value = entries[(row * size) + column];

                // Cut by magnitude so the pattern varies with the sample. Half of a uniform draw on
                // [-1, 1] falls inside, which is about the density of a mesh Laplacian at this size.
                if (Math.Abs(value) < 0.5d) {
                    continue;
                }

                matrix[(row * size) + column] = value;
                matrix[(column * size) + row] = value;
            }
        }

        for (var row = 0; row < size; row++) {
            var sum = 0d;

            for (var column = 0; column < size; column++) {
                if (column != row) {
                    sum += Math.Abs(matrix[(row * size) + column]);
                }
            }

            matrix[(row * size) + row] = sum + bump;
        }

        return matrix;
    }
}
