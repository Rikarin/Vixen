// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Geometry.Uv.Solving;
using Xunit;

namespace Vixen.Geometry.Uv.Tests;

/// <summary>The warm start, which is the entire argument for not writing a sparse Cholesky.</summary>
/// <remarks>
///     <para>
///         docs/plan/42 § D5 chose conjugate gradient over a factorization on one claim: in a
///         local–global iteration the system matrix is constant and only the right-hand side moves, so
///         <b>consecutive solves are close and a warm start converges in very few steps</b>. That is a
///         measurable claim and it is measured here — the smallest budget that reaches a given
///         accuracy from the previous iterate, against the smallest budget that reaches it from zero.
///     </para>
///     <para>
///         ⚠ <b>Sabotage-tested.</b> An <c>Array.Clear(solution)</c> was added at the top of
///         <see cref="ConjugateGradient.Solve" />, which is exactly what a solver that took a
///         <i>destination</i> rather than an <i>iterate</i> would do, and
///         all three facts below failed — <see cref="A_warm_start_needs_strictly_fewer_iterations" />
///         with the two budgets equal at 74. Without that check they would pass on a solver that
///         ignored the warm start entirely, because the cold run still converges and the comparison is
///         still between two numbers.
///     </para>
/// </remarks>
public class WarmStartTests {
    /// <summary>Big enough that the iteration counts are far apart, small enough to search linearly.</summary>
    const int Extent = 24;

    /// <summary>
    ///     Jacobi rather than incomplete Cholesky, on purpose: the stronger preconditioner converges
    ///     so fast that both budgets are single digits and the comparison stops being about anything.
    /// </summary>
    const PreconditionerKind Kind = PreconditionerKind.Jacobi;

    [Fact]
    public void A_warm_start_needs_strictly_fewer_iterations() {
        var matrix = PoissonGrid.Matrix(Extent);
        var solver = new ConjugateGradient(matrix, Kind);

        var first = RightHandSide(matrix.RowCount, 0d);
        var converged = new double[matrix.RowCount];
        solver.Solve(first, converged, matrix.RowCount * 2);

        // The next step of a local–global loop: the same matrix, a right-hand side that moved a
        // little because the local step rotated some triangles.
        var second = RightHandSide(matrix.RowCount, 0.02d);
        var target = Norm(second) * 1e-9d;

        var warm = SmallestBudget(solver, second, target, converged);
        var cold = SmallestBudget(solver, second, target, new double[matrix.RowCount]);

        Assert.True(cold > 0, "The cold start converged instantly, so there is nothing to be better than.");

        Assert.True(
            warm < cold,
            $"A warm start took {warm} iterations and a cold start took {cold}. docs/plan/42 § D5's "
            + "argument for conjugate gradient over a factorization is that this gap exists."
        );
    }

    /// <summary>And the gap grows as the two solves get closer, which is the mechanism and not a coincidence.</summary>
    /// <remarks>
    ///     ⚠ <b>A single "warm beats cold" comparison would also pass on a solver that was merely
    ///     lucky with one right-hand side.</b> The claim docs/plan/42 § D5 makes is causal — the saving
    ///     comes from consecutive solves being close — so the test drives the distance between them
    ///     and asserts the cost follows it down. Measured on this fixture: 67, 59, 48, 34 iterations
    ///     against a cold start that stays at 73 whatever happens.
    /// </remarks>
    [Fact]
    public void The_closer_the_previous_solve_the_cheaper_the_next_one() {
        var matrix = PoissonGrid.Matrix(Extent);
        var solver = new ConjugateGradient(matrix, Kind);

        var first = RightHandSide(matrix.RowCount, 0d);
        var converged = new double[matrix.RowCount];
        solver.Solve(first, converged, matrix.RowCount * 2);

        var warm = new List<int>();
        var cold = new List<int>();

        foreach (var drift in new[] { 0.1d, 0.02d, 0.001d, 0.0001d }) {
            var second = RightHandSide(matrix.RowCount, drift);
            var target = Norm(second) * 1e-9d;

            warm.Add(SmallestBudget(solver, second, target, converged));
            cold.Add(SmallestBudget(solver, second, target, new double[matrix.RowCount]));
        }

        for (var index = 1; index < warm.Count; index++) {
            Assert.True(
                warm[index] < warm[index - 1],
                $"The budgets went {string.Join(", ", warm)}, which is not monotone in how far the system moved."
            );
        }

        Assert.True(
            warm[^1] * 2 < cold.Min(),
            $"The closest warm start took {warm[^1]} against a cold start's {cold.Min()}, which is not a saving."
        );
    }

    /// <summary>The extreme case: nothing moved, so there is nothing to do and the guard says so.</summary>
    [Fact]
    public void An_unchanged_system_costs_no_iterations_at_all() {
        var matrix = PoissonGrid.Matrix(8);
        var solver = new ConjugateGradient(matrix);
        var right = RightHandSide(matrix.RowCount, 0d);

        var solution = new double[matrix.RowCount];
        solver.Solve(right, solution, matrix.RowCount * 2);

        var before = (double[])solution.Clone();
        var again = solver.Solve(right, solution, 32);

        Assert.True(again.Iterations < 3, $"A converged system re-solved took {again.Iterations} iterations.");

        for (var index = 0; index < solution.Length; index++) {
            Assert.Equal(before[index], solution[index], 12);
        }
    }

    /// <summary>The least-squares path warm-starts by the same rule, and is measured the same way.</summary>
    [Fact]
    public void A_least_squares_warm_start_needs_strictly_fewer_iterations() {
        var matrix = PoissonGrid.Matrix(Extent);
        var solver = new LeastSquaresSolver(matrix);

        var first = RightHandSide(matrix.RowCount, 0d);
        var converged = new double[matrix.ColumnCount];
        solver.Solve(first, converged, matrix.ColumnCount);

        var second = RightHandSide(matrix.RowCount, 0.02d);
        var target = Norm(second) * 1e-7d;

        var warm = SmallestBudget(solver, second, target, converged);
        var cold = SmallestBudget(solver, second, target, new double[matrix.ColumnCount]);

        Assert.True(cold > 0, "The cold start converged instantly, so there is nothing to be better than.");
        Assert.True(warm < cold, $"A warm start took {warm} iterations and a cold start took {cold}.");
    }

    /// <summary>
    ///     The smallest budget that reaches <paramref name="target" />, by trying each in turn.
    /// </summary>
    /// <remarks>
    ///     ⚠ The threshold lives <b>here</b> and not in the solver, which is the whole point of
    ///     docs/plan/42 § D5's fixed budget: a test is allowed to ask "how many iterations did that
    ///     take", and a solver is not allowed to answer it at runtime, because that answer is a
    ///     floating-point comparison whose outcome can differ between platforms that agree on the
    ///     arithmetic leading up to it.
    /// </remarks>
    static int SmallestBudget(ConjugateGradient solver, double[] right, double target, double[] start) {
        for (var budget = 0; budget <= right.Length; budget++) {
            var solution = (double[])start.Clone();

            if (solver.Solve(right, solution, budget).Residual <= target) {
                return budget;
            }
        }

        return int.MaxValue;
    }

    static int SmallestBudget(LeastSquaresSolver solver, double[] right, double target, double[] start) {
        for (var budget = 0; budget <= right.Length; budget++) {
            var solution = (double[])start.Clone();

            if (solver.Solve(right, solution, budget).Residual <= target) {
                return budget;
            }
        }

        return int.MaxValue;
    }

    static double[] RightHandSide(int size, double drift) {
        var right = new double[size];

        for (var index = 0; index < size; index++) {
            right[index] = Math.Sin((index * 0.618d) + drift) + (0.25d * Math.Cos(index * 0.137d));
        }

        return right;
    }

    static double Norm(double[] vector) => Math.Sqrt(vector.Sum(value => value * value));
}
