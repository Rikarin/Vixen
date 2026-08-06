// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Threading;
using Vixen.Geometry.Uv.Solving;
using Xunit;

namespace Vixen.Geometry.Uv.Tests;

/// <summary>Byte-identical, at any worker count and any batch size.</summary>
/// <remarks>
///     <para>
///         docs/plan/42 § D12 and doc 41 § D14: the same input and settings produce byte-identical
///         output at any thread count on any platform, and it is a gate rather than an ambition
///         because the content hash depends on it. <b>Not close — the same bits</b>, which is what
///         <see cref="BitConverter.DoubleToInt64Bits" /> below is for; a comparison with a tolerance
///         would pass on a solver that had a nondeterministic reduction in it and was merely converging
///         to the same place.
///     </para>
///     <para>
///         ⚠ <b>Worker count and batch size are two axes and testing one is not testing the other.</b>
///         If the batch size were derived from the worker count, a run at sixteen and a run at one
///         would differ in both, and a solver whose answer depended only on the batch size would still
///         pass. Both are varied here, and independently.
///     </para>
///     <para>
///         ⚠ <b>Every scheduler is disposed.</b> <c>JobScheduler.MaxSchedulers</c> is a process-wide
///         cap of eight that a scheduler only gives back on <see cref="IDisposable.Dispose" />, and
///         xunit runs test classes in parallel — so a leaked one here fails a test somewhere else,
///         later, in a class that has nothing to do with this. All the scheduler use in this assembly
///         is in this one class for that reason: one class is one collection, and one collection does
///         not run against itself.
///     </para>
/// </remarks>
public class SolverDeterminismTests {
    /// <summary>Big enough that the multiply actually takes the parallel path.</summary>
    /// <remarks>
    ///     ⚠ <b>The guard below is the point of this constant.</b> A fixture under
    ///     <see cref="SparseMatrix.ParallelThreshold" /> runs on the calling thread at every worker
    ///     count, so the test would pass by never having tested anything — which is the shape
    ///     <c>VfxParallelTests</c> guards with its "only <c>n</c> particles survived, which proves
    ///     little" assertion.
    /// </remarks>
    const int Extent = 96;

    public static TheoryData<int, int> Configurations {
        get {
            var data = new TheoryData<int, int>();

            foreach (var workers in new[] { 1, 4, 16 }) {
                foreach (var batch in new[] { 0, 1, 37, 512 }) {
                    data.Add(workers, batch);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Configurations))]
    public void A_solve_is_the_same_bits_at_any_worker_count_and_batch_size(int workers, int batch) {
        var matrix = PoissonGrid.Matrix(Extent);

        Assert.True(
            matrix.RowCount >= SparseMatrix.ParallelThreshold,
            $"{matrix.RowCount} rows never reaches the parallel path, so this proves nothing."
        );

        var right = RightHandSide(matrix.RowCount);

        var serial = new double[matrix.RowCount];
        var serialReport = new ConjugateGradient(matrix).Solve(right, serial, 24);

        // Non-trivial: the solve has to have done real work, or identical output is identical
        // because nothing happened.
        Assert.Equal(24, serialReport.Iterations);
        Assert.True(serial.Distinct().Count() > matrix.RowCount / 2, "The answer is too uniform to compare.");

        using var scheduler = new JobScheduler(workers);
        Assert.Equal(workers, scheduler.WorkerCount);

        var parallel = new double[matrix.RowCount];
        var parallelReport = new ConjugateGradient(matrix).Solve(right, parallel, 24, scheduler, batch);

        Assert.Equal(serialReport, parallelReport);
        AssertSameBits(serial, parallel);
    }

    [Theory]
    [MemberData(nameof(Configurations))]
    public void A_least_squares_solve_is_the_same_bits_too(int workers, int batch) {
        var matrix = PoissonGrid.Matrix(Extent);
        var right = RightHandSide(matrix.RowCount);

        var serial = new double[matrix.ColumnCount];
        var serialReport = new LeastSquaresSolver(matrix).Solve(right, serial, 16);

        Assert.Equal(16, serialReport.Iterations);
        Assert.True(serial.Distinct().Count() > matrix.ColumnCount / 2, "The answer is too uniform to compare.");

        using var scheduler = new JobScheduler(workers);

        var parallel = new double[matrix.ColumnCount];
        var parallelReport = new LeastSquaresSolver(matrix).Solve(right, parallel, 16, scheduler, batch);

        Assert.Equal(serialReport, parallelReport);
        AssertSameBits(serial, parallel);
    }

    /// <summary>The multiply on its own, which is where the only parallelism is.</summary>
    [Theory]
    [MemberData(nameof(Configurations))]
    public void A_multiply_is_the_same_bits_at_any_worker_count_and_batch_size(int workers, int batch) {
        var matrix = PoissonGrid.Matrix(Extent);
        var source = RightHandSide(matrix.ColumnCount);

        var serial = new double[matrix.RowCount];
        matrix.Multiply(source, serial);

        using var scheduler = new JobScheduler(workers);

        var parallel = new double[matrix.RowCount];
        matrix.Multiply(source, parallel, scheduler, batch);

        AssertSameBits(serial, parallel);
    }

    /// <summary>Three worker counts against each other rather than each against the serial leg.</summary>
    [Fact]
    public void Every_worker_count_agrees_with_every_other() {
        var matrix = PoissonGrid.Matrix(Extent);
        var right = RightHandSide(matrix.RowCount);
        var answers = new List<double[]>();

        // ⚠ Sequential, and disposed each time round. Sixteen schedulers alive at once would be
        // twice the process-wide cap, and the exception it throws names the cap rather than this.
        foreach (var workers in new[] { 1, 2, 4, 8, 16 }) {
            using var scheduler = new JobScheduler(workers);
            var solution = new double[matrix.RowCount];
            new ConjugateGradient(matrix).Solve(right, solution, 20, scheduler);
            answers.Add(solution);
        }

        foreach (var answer in answers) {
            AssertSameBits(answers[0], answer);
        }
    }

    /// <summary>Something with structure in it, so an off-by-one in the batching shows up.</summary>
    static double[] RightHandSide(int size) {
        var right = new double[size];

        for (var index = 0; index < size; index++) {
            right[index] = Math.Sin(index * 0.618d) + (0.25d * Math.Cos(index * 0.137d));
        }

        return right;
    }

    static void AssertSameBits(double[] expected, double[] actual) {
        Assert.Equal(expected.Length, actual.Length);

        for (var index = 0; index < expected.Length; index++) {
            Assert.True(
                BitConverter.DoubleToInt64Bits(expected[index]) == BitConverter.DoubleToInt64Bits(actual[index]),
                $"Element {index} is {actual[index]:R} against {expected[index]:R}. "
                + "Close is not the contract; docs/plan/42 § D12 asks for the same bits."
            );
        }
    }
}
