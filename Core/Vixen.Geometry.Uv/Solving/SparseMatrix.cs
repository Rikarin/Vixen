// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Threading;

namespace Vixen.Geometry.Uv.Solving;

/// <summary>A matrix held as its nonzeros, one compacted row after another.</summary>
/// <remarks>
///     <para>
///         docs/plan/42 § B1: nothing sparse existed in this repository, and every rung of
///         § D5's flattening ladder is a sparse system underneath — LSCM's normal equations, ARAP's
///         cotangent Laplacian. This is the storage they all sit on.
///     </para>
///     <para>
///         <b>Symmetric positive-definite is the case that matters and it is not the case that is
///         stored.</b> Both halves of a symmetric matrix are kept, and the shape is allowed to be
///         rectangular, because LSCM's own matrix is <c>2f × 2v</c> and only <i>its</i> normal
///         equations are square. A storage format that assumed symmetry would have to be replaced by
///         the first least-squares problem rather than reused by it, and
///         <see cref="LeastSquaresSolver" /> is exactly that problem.
///     </para>
///     <para>
///         ⚠ <b>Nonzeros are <c>double</c> where the rest of this assembly is <c>float</c>, and the
///         conversion happens at the edges.</b> A cotangent Laplacian over a mesh with a sliver in it
///         has a condition number that eats most of a <c>float</c>'s mantissa before the first
///         iteration; the coordinates that come out are still <c>float</c>. This costs bandwidth on a
///         matrix that is a few nonzeros per row and buys the difference between a flattener that
///         works on clean input and one that works.
///     </para>
///     <para>
///         ⚠ <b>Every row's entries are ascending by column and no column appears twice.</b>
///         <see cref="SparseMatrixBuilder" /> guarantees it, and both the indexer's binary search and
///         the incomplete-Cholesky factorization in <see cref="Preconditioner" /> are wrong without
///         it. Do not construct one by hand.
///     </para>
/// </remarks>
sealed class SparseMatrix {
    /// <summary>Below this many rows a multiply runs on the calling thread whatever it was handed.</summary>
    /// <remarks>
    ///     A dispatch through the scheduler and back costs more than a few thousand fused multiplies.
    ///     ⚠ It is a <i>timing</i> threshold and never a numerical one: crossing it does not change a
    ///     single bit of the answer, which is what <c>SparseSolverDeterminismTests</c> asserts.
    /// </remarks>
    public const int ParallelThreshold = 4096;

    /// <summary>How many rows the matrix has.</summary>
    public int RowCount { get; }

    /// <summary>How many columns the matrix has.</summary>
    public int ColumnCount { get; }

    /// <summary>How many nonzeros are stored. Explicit zeros the caller added are counted.</summary>
    public int NonZeroCount => Value.Length;

    /// <summary>Where each row's entries begin, with a trailing entry for the end. Length is <see cref="RowCount" /> + 1.</summary>
    public int[] RowStart { get; }

    /// <summary>The column of each stored entry, ascending within a row.</summary>
    public int[] ColumnIndex { get; }

    /// <summary>The value of each stored entry.</summary>
    public double[] Value { get; }

    /// <summary>Whether the matrix is square, which is what <see cref="ConjugateGradient" /> requires.</summary>
    public bool IsSquare => RowCount == ColumnCount;

    /// <summary>Wraps arrays that already satisfy the ascending-column invariant.</summary>
    /// <param name="rowCount">How many rows.</param>
    /// <param name="columnCount">How many columns.</param>
    /// <param name="rowStart">Where each row begins, plus a trailing end.</param>
    /// <param name="columnIndex">The column of each entry.</param>
    /// <param name="value">The value of each entry.</param>
    public SparseMatrix(int rowCount, int columnCount, int[] rowStart, int[] columnIndex, double[] value) {
        RowCount = rowCount;
        ColumnCount = columnCount;
        RowStart = rowStart;
        ColumnIndex = columnIndex;
        Value = value;
    }

    /// <summary>Reads one entry, or zero where nothing is stored.</summary>
    /// <param name="row">The row.</param>
    /// <param name="column">The column.</param>
    /// <returns>The entry, or zero.</returns>
    /// <remarks>A binary search over the row. Fine for a factorization, wrong for an inner loop.</remarks>
    public double this[int row, int column] {
        get {
            var start = RowStart[row];
            var found = Array.BinarySearch(ColumnIndex, start, RowStart[row + 1] - start, column);

            return found < 0 ? 0d : Value[found];
        }
    }

    /// <summary>The entry on the diagonal, or zero where the row does not have one.</summary>
    /// <param name="row">The row, which is also the column.</param>
    /// <returns>The diagonal entry, or zero.</returns>
    /// <remarks>
    ///     ⚠ <b>Zero is a real answer here and it is the answer that hurts.</b> An empty row, or a row
    ///     whose diagonal was never assembled, reads as zero — and a Jacobi preconditioner that
    ///     divides by it produces an infinity, then a NaN on the next dot product, and every iteration
    ///     after that is poison that no assertion downstream will attribute to this. See
    ///     <see cref="Preconditioner" />.
    /// </remarks>
    public double Diagonal(int row) => row < ColumnCount ? this[row, row] : 0d;

    /// <summary>The largest magnitude in a row, which is what a comparison in that row is measured against.</summary>
    /// <param name="row">The row.</param>
    /// <returns>The largest <c>|value|</c> in the row, or zero for an empty one.</returns>
    /// <remarks>
    ///     ⚠ <b>This exists so that no epsilon in this namespace is absolute.</b>
    ///     <c>Vixen.Geometry</c>'s <c>EditMesh.DefaultWeldTolerance</c> and docs/plan/24 § P1 both
    ///     record the same lesson from the other direction: a fixed epsilon is a claim about how big
    ///     the model is, and a model arriving in millimetres rather than metres falsifies it silently.
    ///     A cotangent Laplacian inherits the mesh's units squared, so the claim is worse here, not
    ///     better.
    /// </remarks>
    public double RowMagnitude(int row) {
        var magnitude = 0d;

        for (var index = RowStart[row]; index < RowStart[row + 1]; index++) {
            var absolute = Math.Abs(Value[index]);

            if (absolute > magnitude) {
                magnitude = absolute;
            }
        }

        return magnitude;
    }

    /// <summary>Multiplies by a vector, into a destination the caller owns.</summary>
    /// <param name="source">The vector, <see cref="ColumnCount" /> long.</param>
    /// <param name="destination">Where the product goes, <see cref="RowCount" /> long. Overwritten.</param>
    /// <param name="scheduler">Threads to spread the rows across, or <see langword="null" /> for none.</param>
    /// <param name="batchSize">How many rows one work item covers, or zero to choose one.</param>
    /// <exception cref="ArgumentException">A vector is the wrong length.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>Each row sums in ascending column order and writes only its own element</b>, so the
    ///         result does not depend on how the rows were divided up or on how many threads saw them.
    ///         That is the whole reason this is the <i>only</i> part of a solve that is parallel:
    ///         doc 41 § D14 rules out a <c>float</c> reduction in a nondeterministic order, and a dot
    ///         product split across threads is precisely one. The dot products in
    ///         <see cref="ConjugateGradient" /> therefore stay on one thread, in index order, and are
    ///         not an oversight.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><paramref name="batchSize" /> is a second determinism axis and it is easy to test
    ///         only the first.</b> A run at sixteen workers and a run at one differ in <i>two</i>
    ///         things if the batch size was derived from the worker count, so a test that varies only
    ///         the worker count has not separated them.
    ///     </para>
    /// </remarks>
    public void Multiply(double[] source, double[] destination, JobScheduler? scheduler = null, int batchSize = 0) {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        if (source.Length != ColumnCount) {
            throw new ArgumentException($"The source is {source.Length} long for {ColumnCount} columns.", nameof(source));
        }

        if (destination.Length != RowCount) {
            throw new ArgumentException(
                $"The destination is {destination.Length} long for {RowCount} rows.",
                nameof(destination)
            );
        }

        if (scheduler is null || RowCount < ParallelThreshold) {
            MultiplyRows(this, source, destination, 0, RowCount);
            return;
        }

        var batch = batchSize > 0
            ? batchSize
            : Math.Max(256, (RowCount / Math.Max(1, (scheduler.WorkerCount + 1) * 4)) + 1);

        var batches = (RowCount + batch - 1) / batch;

        scheduler.ScheduleParallel(
                new MultiplyJob { Matrix = this, Source = source, Destination = destination, Batch = batch },
                batches,
                1
            )
            .Complete();
    }

    /// <summary>Builds the transpose, with the same ascending-column invariant.</summary>
    /// <returns>A new matrix, <see cref="ColumnCount" /> by <see cref="RowCount" />.</returns>
    /// <remarks>
    ///     ⚠ <b><see cref="LeastSquaresSolver" /> materializes this rather than scattering into a
    ///     shared accumulator, and the reason is determinism rather than speed.</b> Computing
    ///     <c>Aᵀr</c> by walking <c>A</c>'s rows and adding into <c>result[column]</c> visits one
    ///     output element from many rows in an order that is a property of the sparsity pattern; doing
    ///     it as a multiply by a stored transpose sums each output in ascending index order, and can
    ///     then be spread across threads by the same rule as everything else.
    /// </remarks>
    public SparseMatrix Transpose() {
        var starts = new int[ColumnCount + 1];

        foreach (var column in ColumnIndex) {
            starts[column + 1]++;
        }

        for (var column = 0; column < ColumnCount; column++) {
            starts[column + 1] += starts[column];
        }

        var cursor = new int[ColumnCount];
        starts.AsSpan(0, ColumnCount).CopyTo(cursor);

        var columns = new int[NonZeroCount];
        var values = new double[NonZeroCount];

        // Rows ascend and entries within a row ascend, so each transposed row is filled in ascending
        // order without a sort.
        for (var row = 0; row < RowCount; row++) {
            for (var index = RowStart[row]; index < RowStart[row + 1]; index++) {
                var slot = cursor[ColumnIndex[index]]++;
                columns[slot] = row;
                values[slot] = Value[index];
            }
        }

        return new(ColumnCount, RowCount, starts, columns, values);
    }

    static void MultiplyRows(SparseMatrix matrix, double[] source, double[] destination, int first, int end) {
        var rowStart = matrix.RowStart;
        var columnIndex = matrix.ColumnIndex;
        var value = matrix.Value;

        for (var row = first; row < end; row++) {
            var sum = 0d;

            for (var index = rowStart[row]; index < rowStart[row + 1]; index++) {
                sum += value[index] * source[columnIndex[index]];
            }

            destination[row] = sum;
        }
    }

    /// <summary>One run of rows, multiplied.</summary>
    /// <remarks>
    ///     The index is a <em>batch</em> of rows rather than a row, following <c>VfxSimulation</c>'s
    ///     sweep: a row of a mesh Laplacian is seven multiplies, which is less work than deciding to
    ///     do it.
    /// </remarks>
    readonly struct MultiplyJob : IJobParallelFor {
        public required SparseMatrix Matrix { get; init; }
        public required double[] Source { get; init; }
        public required double[] Destination { get; init; }
        public required int Batch { get; init; }

        public void Execute(int index) {
            var first = index * Batch;
            var end = Math.Min(first + Batch, Matrix.RowCount);

            if (end > first) {
                MultiplyRows(Matrix, Source, Destination, first, end);
            }
        }
    }
}
