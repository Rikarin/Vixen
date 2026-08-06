// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Geometry.Uv.Solving;
using Xunit;

namespace Vixen.Geometry.Uv.Tests;

/// <summary>Every input where zero means something, and what the answer is when it does.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>This engine has the general form of this bug written down: a frame that draws and
///         looks wrong, because a struct field was left zero and its zero means disabled.</b> A solver
///         has four of them and they all arrive as ordinary geometry — a chart with one vertex nothing
///         references, a mesh whose conditioning pass dropped the only triangle touching an unknown, a
///         local–global step whose right-hand side came out exactly zero because nothing moved. None
///         of them is an error. Each therefore needs an answer written down, and this is where.
///     </para>
///     <para>
///         The recurring one is <b>finite</b>. A NaN in a solve does not throw; it comes back as a
///         coordinate, gets packed, gets baked, and is diagnosed four stages later.
///     </para>
/// </remarks>
public class DegenerateSystemTests {
    [Fact]
    public void A_system_with_no_rows_solves_to_nothing() {
        var report = new ConjugateGradient(new SparseMatrixBuilder(0, 0).Build()).Solve([], [], 32);

        Assert.Equal(0, report.Iterations);
        Assert.Equal(0d, report.Residual);
    }

    /// <summary>A right-hand side of zeros from a zero start is already solved, and the guard says so.</summary>
    [Fact]
    public void A_zero_right_hand_side_from_zero_returns_zero_without_dividing_by_it() {
        var matrix = PoissonGrid.Matrix(5);
        var solution = new double[matrix.RowCount];

        var report = new ConjugateGradient(matrix).Solve(new double[matrix.RowCount], solution, 32);

        Assert.Equal(0, report.Iterations);
        Assert.True(report.StoppedEarly);
        Assert.All(solution, value => Assert.Equal(0d, value));
    }

    /// <summary>The same right-hand side from a warm start that is wrong: it must come back to zero.</summary>
    [Fact]
    public void A_zero_right_hand_side_from_a_wrong_start_converges_to_zero() {
        var matrix = PoissonGrid.Matrix(6);
        var solution = Enumerable.Range(0, matrix.RowCount).Select(index => 1d + (index % 5)).ToArray();

        new ConjugateGradient(matrix).Solve(new double[matrix.RowCount], solution, matrix.RowCount * 2);

        Assert.All(solution, value => Assert.Equal(0d, value, 10));
    }

    /// <summary>
    ///     ⚠ The empty row, with the case that has a clean answer: nothing constrains the unknown and
    ///     nothing asks anything of it, so it must come out exactly where the warm start left it — not
    ///     near it, and certainly not NaN.
    /// </summary>
    [Theory]
    [InlineData((int)PreconditionerKind.Jacobi)]
    [InlineData((int)PreconditionerKind.IncompleteCholesky)]
    [InlineData((int)PreconditionerKind.None)]
    public void An_unconstrained_unknown_is_left_exactly_where_it_started(int ordinal) {
        var kind = (PreconditionerKind)ordinal;
        var builder = new SparseMatrixBuilder(3, 3);
        builder.Add(0, 0, 2d);
        builder.Add(2, 2, 4d);

        var solution = new double[] { 0d, 7.25d, 0d };
        var report = new ConjugateGradient(builder.Build(), kind).Solve([2d, 0d, 8d], solution, 16);

        Assert.Equal(1d, solution[0], 12);
        Assert.Equal(7.25d, solution[1]);
        Assert.Equal(2d, solution[2], 12);
        Assert.False(double.IsNaN(report.Residual));
    }

    /// <summary>And the case that has no clean answer stays finite, which is all that can be promised.</summary>
    [Theory]
    [InlineData((int)PreconditionerKind.Jacobi)]
    [InlineData((int)PreconditionerKind.IncompleteCholesky)]
    [InlineData((int)PreconditionerKind.None)]
    public void An_unsatisfiable_row_stays_finite(int ordinal) {
        var kind = (PreconditionerKind)ordinal;
        var builder = new SparseMatrixBuilder(3, 3);
        builder.Add(0, 0, 2d);
        builder.Add(2, 2, 4d);

        var solution = new double[3];
        var report = new ConjugateGradient(builder.Build(), kind).Solve([2d, 1d, 8d], solution, 16);

        Assert.All(solution, value => Assert.True(double.IsFinite(value), $"{value} is not finite."));
        Assert.True(double.IsFinite(report.Residual));
    }

    /// <summary>A matrix of nothing at all: every direction is mapped to nothing, and the guard catches it.</summary>
    [Fact]
    public void A_matrix_of_zeros_stops_rather_than_producing_a_nan() {
        var builder = new SparseMatrixBuilder(4, 4);

        for (var row = 0; row < 4; row++) {
            builder.Add(row, row, 0d);
        }

        var solution = new double[4];
        var report = new ConjugateGradient(builder.Build()).Solve([1d, 2d, 3d, 4d], solution, 32);

        Assert.True(report.StoppedEarly);
        Assert.All(solution, value => Assert.True(double.IsFinite(value), $"{value} is not finite."));
    }

    /// <summary>
    ///     ⚠ Every comparison in the solver is relative, so the answer at millimetre scale is the
    ///     answer at kilometre scale. A Laplacian carries the mesh's units squared, so this is the
    ///     lesson <c>EditMesh.DefaultWeldTolerance</c> and docs/plan/24 § P1 record, doubled.
    /// </summary>
    [Theory]
    [InlineData(1e-3d)]
    [InlineData(1d)]
    [InlineData(1e+3d)]
    public void The_solution_is_the_same_at_any_scale(double scale) {
        const int Extent = 10;

        var matrix = PoissonGrid.Matrix(Extent, scale);
        var expected = PoissonGrid.Eigenvector(Extent, 2, 1);
        var eigenvalue = PoissonGrid.Eigenvalue(Extent, 2, 1) * scale;
        var right = expected.Select(value => eigenvalue * value).ToArray();

        var solution = new double[matrix.RowCount];
        var report = new ConjugateGradient(matrix).Solve(right, solution, matrix.RowCount);

        Assert.False(report.PreconditionerFellBack);

        for (var index = 0; index < expected.Length; index++) {
            Assert.Equal(expected[index], solution[index], 10);
        }
    }

    /// <summary>And scaling only the right-hand side scales only the answer, at either extreme.</summary>
    [Theory]
    [InlineData(1e-3d)]
    [InlineData(1e+3d)]
    public void Scaling_the_right_hand_side_scales_the_answer(double scale) {
        var matrix = PoissonGrid.Matrix(8);
        var right = Enumerable.Range(0, matrix.RowCount).Select(index => Math.Cos(index * 0.37d)).ToArray();

        var plain = new double[matrix.RowCount];
        new ConjugateGradient(matrix).Solve(right, plain, matrix.RowCount);

        var scaled = new double[matrix.RowCount];
        new ConjugateGradient(matrix).Solve(right.Select(value => value * scale).ToArray(), scaled, matrix.RowCount);

        for (var index = 0; index < plain.Length; index++) {
            Assert.Equal(plain[index] * scale, scaled[index], Math.Abs(plain[index] * scale * 1e-8d) + 1e-12d);
        }
    }
}
