// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Geometry.Uv.Solving;
using Xunit;

namespace Vixen.Geometry.Uv.Tests;

/// <summary>The two approximate inverses, and the two ways they are asked to do something undefined.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Both failures here are silent by default and neither throws.</b> A Jacobi division by
///         a zero diagonal makes an infinity, the next dot product makes a NaN out of it, and every
///         iteration after that is a NaN that arrives at the caller as coordinates rather than as an
///         exception. An incomplete Cholesky that breaks down takes the square root of a negative
///         pivot and does the same thing one step earlier. This engine has the general version of that
///         trap written down already — a field left zero whose zero means "off", and a frame that
///         draws and looks wrong.
///     </para>
/// </remarks>
public class PreconditionerTests {
    /// <summary>
    ///     Kershaw's matrix: symmetric positive-definite, and the standard counterexample to the claim
    ///     that IC(0) exists for such a matrix. The fourth pivot comes out at −5.
    /// </summary>
    static readonly double[] Kershaw = [
        3d, -2d, 0d, 2d,
        -2d, 3d, -2d, 0d,
        0d, -2d, 3d, -2d,
        2d, 0d, -2d, 3d
    ];

    /// <summary>The guard exists because the matrix that trips it is a real one, and here it is.</summary>
    [Fact]
    public void Incomplete_cholesky_breaks_down_on_a_matrix_it_should_and_says_so() {
        // ⚠ The first half of this fact is the important half. Without it the test proves that the
        // factorization refused a matrix, not that it refused one a correct factorization would have
        // been asked to accept.
        Assert.True(DenseReference.IsPositiveDefinite(Kershaw, 4), "Kershaw's matrix is supposed to be SPD.");

        var preconditioner = Preconditioner.Create(
            DenseReference.ToSparse(Kershaw, 4),
            PreconditionerKind.IncompleteCholesky
        );

        Assert.True(preconditioner.FellBack);
        Assert.Equal(PreconditionerKind.Jacobi, preconditioner.Kind);
    }

    /// <summary>And the fallback is reported all the way out, rather than being something you go and look for.</summary>
    [Fact]
    public void The_breakdown_reaches_the_report_and_the_solve_still_lands() {
        var matrix = DenseReference.ToSparse(Kershaw, 4);
        double[] right = [1d, 2d, 3d, 4d];
        var expected = DenseReference.Solve(Kershaw, right, 4);

        var solution = new double[4];
        var report = new ConjugateGradient(matrix).Solve(right, solution, 64);

        Assert.True(report.PreconditionerFellBack);
        Assert.Equal(PreconditionerKind.Jacobi, report.Preconditioner);

        for (var index = 0; index < 4; index++) {
            Assert.Equal(expected[index], solution[index], 10);
        }
    }

    /// <summary>
    ///     ⚠ The breakdown test is relative, so the same matrix in millimetres breaks down the same
    ///     way. An absolute pivot floor would call every pivot dead at one scale and every pivot
    ///     healthy at the other, and the fallback would then be a property of the import units.
    /// </summary>
    [Theory]
    [InlineData(1e-3d)]
    [InlineData(1d)]
    [InlineData(1e+3d)]
    [InlineData(1e+6d)]
    public void The_breakdown_is_decided_the_same_way_at_any_scale(double scale) {
        var scaled = Kershaw.Select(value => value * scale).ToArray();

        var preconditioner = Preconditioner.Create(
            DenseReference.ToSparse(scaled, 4),
            PreconditionerKind.IncompleteCholesky
        );

        Assert.True(preconditioner.FellBack);
    }

    /// <summary>A matrix IC(0) does exist for stays IC(0) at any scale, which is the other half of the claim.</summary>
    [Theory]
    [InlineData(1e-3d)]
    [InlineData(1d)]
    [InlineData(1e+3d)]
    public void A_healthy_factorization_stays_healthy_at_any_scale(double scale) {
        var preconditioner = Preconditioner.Create(
            PoissonGrid.Matrix(8, scale),
            PreconditionerKind.IncompleteCholesky
        );

        Assert.False(preconditioner.FellBack);
        Assert.Equal(PreconditionerKind.IncompleteCholesky, preconditioner.Kind);
    }

    /// <summary>⚠ The divide-by-zero. An empty row's diagonal is zero, and one over it is an infinity.</summary>
    [Fact]
    public void A_zero_diagonal_is_left_unscaled_rather_than_divided_by() {
        var builder = new SparseMatrixBuilder(3, 3);
        builder.Add(0, 0, 4d);
        builder.Add(2, 2, 0.5d);

        var preconditioner = Preconditioner.Create(builder.Build(), PreconditionerKind.Jacobi);
        var destination = new double[3];
        preconditioner.Apply([8d, 7d, 3d], destination);

        Assert.Equal(2d, destination[0]);

        // The identity for the row nothing constrains, which is defined, finite, and the only answer
        // that does not poison the iterations after it.
        Assert.Equal(7d, destination[1]);
        Assert.Equal(6d, destination[2]);
    }

    /// <summary>An explicit zero on the diagonal is the same case wearing a different hat.</summary>
    [Fact]
    public void An_explicitly_zero_diagonal_is_left_unscaled_too() {
        var builder = new SparseMatrixBuilder(2, 2);
        builder.Add(0, 0, 0d);
        builder.Add(0, 1, 1d);
        builder.Add(1, 0, 1d);
        builder.Add(1, 1, 2d);

        var destination = new double[2];
        Preconditioner.Create(builder.Build(), PreconditionerKind.Jacobi).Apply([5d, 6d], destination);

        Assert.Equal(5d, destination[0]);
        Assert.Equal(3d, destination[1]);
    }

    /// <summary>A row with no diagonal entry at all has no pivot to test, which is a breakdown.</summary>
    [Fact]
    public void Incomplete_cholesky_breaks_down_on_a_row_with_no_diagonal() {
        var builder = new SparseMatrixBuilder(2, 2);
        builder.Add(0, 0, 1d);
        builder.Add(1, 0, 0d);

        var preconditioner = Preconditioner.Create(builder.Build(), PreconditionerKind.IncompleteCholesky);

        Assert.True(preconditioner.FellBack);
    }

    /// <summary>
    ///     On a tridiagonal matrix a Cholesky produces no fill at all, so the <i>incomplete</i> one is
    ///     the complete one and the factor is exact — which makes this the sharp test of the
    ///     factorization rather than an approximate one.
    /// </summary>
    [Fact]
    public void On_a_tridiagonal_matrix_the_incomplete_factorization_is_the_exact_inverse() {
        const int Size = 20;

        var builder = new SparseMatrixBuilder(Size, Size);

        for (var row = 0; row < Size; row++) {
            builder.Add(row, row, 2d);

            if (row > 0) {
                builder.Add(row, row - 1, -1d);
            }

            if (row < Size - 1) {
                builder.Add(row, row + 1, -1d);
            }
        }

        var matrix = builder.Build();
        var preconditioner = Preconditioner.Create(matrix, PreconditionerKind.IncompleteCholesky);

        Assert.False(preconditioner.FellBack);

        var expected = Enumerable.Range(0, Size).Select(index => Math.Sin(index * 0.3d)).ToArray();
        var product = new double[Size];
        matrix.Multiply(expected, product);

        var recovered = new double[Size];
        preconditioner.Apply(product, recovered);

        for (var index = 0; index < Size; index++) {
            Assert.Equal(expected[index], recovered[index], 10);
        }
    }

    /// <summary>And on a matrix where it is genuinely incomplete, it still earns its cost.</summary>
    /// <remarks>
    ///     The claim a preconditioner makes is about iteration count and nothing else, so that is what
    ///     is measured: how many iterations each needs to bring the residual to a given place.
    /// </remarks>
    [Fact]
    public void Incomplete_cholesky_converges_in_fewer_iterations_than_jacobi() {
        var matrix = PoissonGrid.Matrix(20);
        var right = Enumerable.Range(0, matrix.RowCount).Select(index => Math.Sin(index * 0.618d)).ToArray();
        var target = Math.Sqrt(right.Sum(value => value * value)) * 1e-9d;

        var cholesky = Budget(matrix, PreconditionerKind.IncompleteCholesky, right, target);
        var jacobi = Budget(matrix, PreconditionerKind.Jacobi, right, target);
        var none = Budget(matrix, PreconditionerKind.None, right, target);

        Assert.True(cholesky < jacobi, $"Incomplete Cholesky took {cholesky} and Jacobi took {jacobi}.");
        Assert.True(jacobi <= none, $"Jacobi took {jacobi} and no preconditioner took {none}.");
    }

    static int Budget(SparseMatrix matrix, PreconditionerKind kind, double[] right, double target) {
        var solver = new ConjugateGradient(matrix, kind);

        for (var budget = 0; budget <= matrix.RowCount; budget++) {
            if (solver.Solve(right, new double[matrix.RowCount], budget).Residual <= target) {
                return budget;
            }
        }

        return int.MaxValue;
    }

    /// <summary>An IC(0) that is applied in place must give the same answer as one that is not.</summary>
    [Fact]
    public void Applying_in_place_is_the_same_as_applying_out_of_place() {
        var preconditioner = Preconditioner.Create(PoissonGrid.Matrix(6), PreconditionerKind.IncompleteCholesky);
        var input = Enumerable.Range(0, 36).Select(index => Math.Sin(index)).ToArray();

        var outOfPlace = new double[36];
        preconditioner.Apply(input, outOfPlace);

        var inPlace = (double[])input.Clone();
        preconditioner.Apply(inPlace, inPlace);

        Assert.Equal(outOfPlace, inPlace);
    }
}
