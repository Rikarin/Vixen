// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Geometry.Uv.Solving;

/// <summary>Collects <c>(row, column, value)</c> triplets and compacts them into a <see cref="SparseMatrix" />.</summary>
/// <remarks>
///     <para>
///         Assembling a Laplacian means visiting every triangle and adding a contribution per corner
///         pair, so the same <c>(row, column)</c> arrives once per incident triangle and the entries
///         arrive in whatever order the faces are stored in. Both are the normal case rather than a
///         misuse, and <see cref="Build" /> is what turns them into one sorted row.
///     </para>
///     <para>
///         ⚠ <b>Duplicates are summed in the order they were added, and that order is part of the
///         answer.</b> Floating-point addition is not associative, so <c>(a + b) + c</c> and
///         <c>a + (b + c)</c> are different matrices — near enough to look identical and far enough to
///         move the last bit of a coordinate, which is what docs/plan/42 § D12's byte-identical gate
///         is measured on. The sort below is therefore <i>stable</i>: it orders by column and leaves
///         everything that ties in the order the caller produced it.
///     </para>
/// </remarks>
sealed class SparseMatrixBuilder {
    readonly List<int> rows = [];
    readonly List<int> columns = [];
    readonly List<double> values = [];

    /// <summary>How many rows the matrix will have.</summary>
    public int RowCount { get; }

    /// <summary>How many columns the matrix will have.</summary>
    public int ColumnCount { get; }

    /// <summary>How many triplets have been added, before duplicates are merged.</summary>
    public int TripletCount => rows.Count;

    /// <summary>Starts a builder for a matrix of a known shape.</summary>
    /// <param name="rowCount">How many rows. Zero is allowed and builds an empty matrix.</param>
    /// <param name="columnCount">How many columns.</param>
    /// <exception cref="ArgumentOutOfRangeException">Either count is negative.</exception>
    public SparseMatrixBuilder(int rowCount, int columnCount) {
        ArgumentOutOfRangeException.ThrowIfNegative(rowCount);
        ArgumentOutOfRangeException.ThrowIfNegative(columnCount);

        RowCount = rowCount;
        ColumnCount = columnCount;
    }

    /// <summary>Records one contribution.</summary>
    /// <param name="row">The row, in <c>[0, RowCount)</c>.</param>
    /// <param name="column">The column, in <c>[0, ColumnCount)</c>.</param>
    /// <param name="value">What to add. Zero is stored rather than dropped — see the remarks.</param>
    /// <exception cref="ArgumentOutOfRangeException">The row or column is outside the shape.</exception>
    /// <remarks>
    ///     ⚠ <b>An explicitly added zero survives into the built matrix as a stored entry.</b>
    ///     Dropping it would make the sparsity pattern depend on the values, and the pattern is what
    ///     <see cref="Preconditioner" />'s incomplete Cholesky is defined against — an IC(0) whose
    ///     pattern changed because one cotangent happened to cancel is a factorization that differs
    ///     between two meshes that should have behaved the same.
    /// </remarks>
    public void Add(int row, int column, double value) {
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, RowCount);
        ArgumentOutOfRangeException.ThrowIfNegative(column);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(column, ColumnCount);

        rows.Add(row);
        columns.Add(column);
        values.Add(value);
    }

    /// <summary>Sorts, merges and compacts everything added so far.</summary>
    /// <returns>The matrix. The builder is left usable and unchanged.</returns>
    /// <remarks>
    ///     A counting sort into row segments, which is stable because the triplets are scattered in
    ///     the order they were added; then an insertion sort by column within each segment, which is
    ///     stable for the same reason and is the right sort for rows of seven entries; then one pass
    ///     that sums runs of equal columns.
    /// </remarks>
    public SparseMatrix Build() {
        var count = rows.Count;
        var starts = new int[RowCount + 1];

        for (var triplet = 0; triplet < count; triplet++) {
            starts[rows[triplet] + 1]++;
        }

        for (var row = 0; row < RowCount; row++) {
            starts[row + 1] += starts[row];
        }

        var cursor = new int[RowCount];
        starts.AsSpan(0, RowCount).CopyTo(cursor);

        var scatteredColumns = new int[count];
        var scatteredValues = new double[count];

        for (var triplet = 0; triplet < count; triplet++) {
            var slot = cursor[rows[triplet]]++;
            scatteredColumns[slot] = columns[triplet];
            scatteredValues[slot] = values[triplet];
        }

        for (var row = 0; row < RowCount; row++) {
            SortRow(scatteredColumns, scatteredValues, starts[row], starts[row + 1]);
        }

        var mergedStarts = new int[RowCount + 1];
        var mergedColumns = new int[count];
        var mergedValues = new double[count];
        var written = 0;

        for (var row = 0; row < RowCount; row++) {
            var index = starts[row];
            var end = starts[row + 1];

            while (index < end) {
                var column = scatteredColumns[index];
                var sum = scatteredValues[index];
                index++;

                while (index < end && scatteredColumns[index] == column) {
                    sum += scatteredValues[index];
                    index++;
                }

                mergedColumns[written] = column;
                mergedValues[written] = sum;
                written++;
            }

            mergedStarts[row + 1] = written;
        }

        Array.Resize(ref mergedColumns, written);
        Array.Resize(ref mergedValues, written);

        return new(RowCount, ColumnCount, mergedStarts, mergedColumns, mergedValues);
    }

    /// <summary>A stable insertion sort by column, over one row's scattered entries.</summary>
    static void SortRow(int[] columns, double[] values, int first, int end) {
        for (var index = first + 1; index < end; index++) {
            var column = columns[index];
            var value = values[index];
            var slot = index - 1;

            // Strictly greater, so entries that tie keep the order they were added in. An `>=` here
            // reverses duplicates and changes the sum in the last bits.
            while (slot >= first && columns[slot] > column) {
                columns[slot + 1] = columns[slot];
                values[slot + 1] = values[slot];
                slot--;
            }

            columns[slot + 1] = column;
            values[slot + 1] = value;
        }
    }
}
