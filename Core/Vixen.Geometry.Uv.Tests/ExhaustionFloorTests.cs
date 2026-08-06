// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Geometry.Uv.Solving;
using Xunit;

namespace Vixen.Geometry.Uv.Tests;

/// <summary>The property the exhaustion floor's whole determinism argument rests on, asserted rather than reasoned.</summary>
/// <remarks>
///     <para>
///         <b><see cref="ConjugateGradient.Exhausted" /> is defended on the grounds that it "is not
///         the tolerance docs/plan/42 § D5 forbids" — because it sits where a <c>double</c> has
///         stopped carrying information, so which side of it a platform lands on cannot change the
///         answer.</b> That argument is correct only while the floor fires <i>after</i> the iteration
///         has stopped making progress. A floor that fired mid-descent would put the stopping
///         iteration back under the control of a floating-point comparison, which is exactly the
///         failure § B6 exists to prevent, and it would do it invisibly — the coordinates would still
///         look like coordinates.
///     </para>
///     <para>
///         ⚠ <b>So the claim is a testable one and it was not being tested.</b> These facts run a
///         battery of well-conditioned systems at the budget a caller actually gets —
///         <c>UvSettings.SolverIterations</c>' default of 64 — and assert that wherever the guard
///         fires, the residual is already at the arithmetic's floor rather than merely small.
///     </para>
/// </remarks>
public class ExhaustionFloorTests {
    /// <summary>What <c>UvSettings.SolverIterations</c> hands the solver unless a caller says otherwise.</summary>
    const int DefaultBudget = 64;

    /// <summary>
    ///     ⚠ Where the guard fires, it fires on an answer that is already exact to fifteen digits —
    ///     never on one that is merely close. Below this ratio the remaining iterations can only add
    ///     last-place noise, which is the premise the determinism argument needs.
    /// </summary>
    const double Converged = 1e-12;

    // ⚠ The ordinal rather than the enum, because `PreconditionerKind` is internal and a public
    // xunit method may not name it. Ugly, and the alternative is making a solver detail public to
    // suit a test framework.
    [Theory]
    [InlineData((int)PreconditionerKind.None)]
    [InlineData((int)PreconditionerKind.Jacobi)]
    [InlineData((int)PreconditionerKind.IncompleteCholesky)]
    public void TheFloorNeverFiresWhileTheIterationIsStillDescending(int ordinal) {
        var kind = (PreconditionerKind)ordinal;
        var fired = 0;

        // Several sizes, several eigenvectors: a system whose answer is one low-frequency mode
        // converges in a handful of iterations and then sits at the floor for the remaining sixty,
        // which is precisely the case the guard was added for. A high-frequency mode on a larger
        // grid does not converge inside the budget at all, which is the case it must stay out of.
        foreach (var extent in (int[])[4, 8, 12]) {
            foreach (var mode in (int[])[1, 2, 3]) {
                var matrix = PoissonGrid.Matrix(extent);
                var answer = PoissonGrid.Eigenvector(extent, mode, mode);
                var right = new double[answer.Length];

                for (var index = 0; index < answer.Length; index++) {
                    right[index] = PoissonGrid.Eigenvalue(extent, mode, mode) * answer[index];
                }

                var initial = Math.Sqrt(right.Sum(value => value * value));
                var solution = new double[answer.Length];
                var report = new ConjugateGradient(matrix, kind).Solve(right, solution, DefaultBudget);

                if (!report.StoppedEarly) {
                    continue;
                }

                fired++;

                Assert.True(
                    report.Residual <= Converged * initial,
                    $"The floor fired at iteration {report.Iterations} of {DefaultBudget} on a {extent}×{extent} "
                    + $"grid with residual {report.Residual:E3} against an initial {initial:E3} — a ratio of "
                    + $"{report.Residual / initial:E3}, which is descent rather than exhaustion. "
                    + "ConjugateGradient.Exhausted's remarks claim this cannot happen, and the determinism "
                    + "gate depends on the claim."
                );
            }
        }

        // ⚠ A test that never observed the guard would pass for the wrong reason, and this is the
        // shape Vixen.Vfx.Tests/VfxParallelTests uses for the same hazard: assert the fixture is
        // non-trivial before believing what it says about it.
        Assert.True(fired > 0, $"The floor never fired under {kind}, so this proved nothing about it.");
    }

    /// <summary>
    ///     ⚠ The same claim on the rectangular path, whose floor is anchored to a different quantity —
    ///     and which is where a floor that could never fire went unnoticed.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <see cref="LeastSquaresSolver" /> iterates on <c>Aᵀr</c> and not on <c>r</c>, so it
    ///         cannot measure its floor against a cold start's residual energy the way
    ///         <see cref="ConjugateGradient" /> does — see that method's remarks for the whole argument.
    ///         The anchor it uses instead is <c>‖b‖²·Σⱼ mⱼ‖aⱼ‖²</c>, which is <b>never smaller</b> than
    ///         the <c>‖Aᵀb‖²</c> it replaced and on a badly angled system is a million times larger.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>So this half of the fact matters more here than it did there.</b> Raising a floor is
    ///         exactly how one turns into the quality threshold § D5 forbids, and the only thing
    ///         standing between the two is that it still fires after descent has stopped rather than
    ///         during it.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheLeastSquaresFloorNeverFiresWhileTheIterationIsStillDescending(bool preconditioned) {
        var fired = 0;

        foreach (var extent in (int[])[4, 8, 12]) {
            foreach (var mode in (int[])[1, 2, 3]) {
                var matrix = PoissonGrid.Matrix(extent);
                var answer = PoissonGrid.Eigenvector(extent, mode, mode);
                var right = new double[answer.Length];

                for (var index = 0; index < answer.Length; index++) {
                    right[index] = PoissonGrid.Eigenvalue(extent, mode, mode) * answer[index];
                }

                var initial = Math.Sqrt(right.Sum(value => value * value));
                var solution = new double[answer.Length];

                var report = new LeastSquaresSolver(matrix, preconditioned)
                    .Solve(right, solution, DefaultBudget);

                if (!report.StoppedEarly) {
                    continue;
                }

                fired++;

                Assert.True(
                    report.Residual <= Converged * initial,
                    $"The floor fired at iteration {report.Iterations} of {DefaultBudget} on a {extent}×{extent} "
                    + $"grid with residual {report.Residual:E3} against an initial {initial:E3} — a ratio of "
                    + $"{report.Residual / initial:E3}, which is descent rather than exhaustion. "
                    + "LeastSquaresSolver.Solve's remarks claim this cannot happen, and the determinism "
                    + "gate depends on the claim."
                );
            }
        }

        Assert.True(fired > 0, $"The floor never fired with preconditioned={preconditioned}, so this proved nothing.");
    }

    /// <summary>
    ///     ⚠ And the other half: a budget the system cannot converge inside must spend all of it. If
    ///     the guard were a quality threshold in disguise it would cut this short, and the iteration
    ///     count would become a thing two platforms could disagree about.
    /// </summary>
    [Fact]
    public void AnUnconvergedSystemSpendsItsWholeBudget() {
        // ⚠ The right-hand side must not be an eigenvector, and the first version of this test was
        // wrong for exactly that reason. `b = λv` puts the answer inside the first Krylov subspace,
        // so conjugate gradient lands on it in one iteration however large the grid is — which made
        // a test meant to prove the budget is spent instead prove it is not. A right-hand side
        // spread across every mode is what needs the iterations.
        const int Extent = 20;
        const int Budget = 20;

        var matrix = PoissonGrid.Matrix(Extent);
        var right = new double[Extent * Extent];

        for (var index = 0; index < right.Length; index++) {
            right[index] = ((index * 37) % 101) - 50;
        }

        var report = new ConjugateGradient(matrix, PreconditionerKind.None)
            .Solve(right, new double[right.Length], Budget);

        Assert.False(
            report.StoppedEarly,
            $"The guard fired at iteration {report.Iterations} with residual {report.Residual:E3}."
        );

        Assert.Equal(Budget, report.Iterations);
    }

    /// <summary>
    ///     ⚠ And the rectangular path's half of it, on the raised floor. CGLS works against
    ///     <c>AᵀA</c>'s spectrum, so it needs <i>more</i> iterations than the square path on the same
    ///     grid, not fewer — a floor that cut this short would be a quality threshold.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnUnconvergedLeastSquaresSystemSpendsItsWholeBudget(bool preconditioned) {
        // ⚠ Not an eigenvector, for the reason spelled out on the square version above.
        const int Extent = 20;
        const int Budget = 20;

        var matrix = PoissonGrid.Matrix(Extent);
        var right = new double[Extent * Extent];

        for (var index = 0; index < right.Length; index++) {
            right[index] = ((index * 37) % 101) - 50;
        }

        var report = new LeastSquaresSolver(matrix, preconditioned)
            .Solve(right, new double[right.Length], Budget);

        Assert.False(
            report.StoppedEarly,
            $"The guard fired at iteration {report.Iterations} with residual {report.Residual:E3}."
        );

        Assert.Equal(Budget, report.Iterations);
    }
}
