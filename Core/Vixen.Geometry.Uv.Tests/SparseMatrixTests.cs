// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Geometry.Uv.Solving;
using Xunit;

namespace Vixen.Geometry.Uv.Tests;

/// <summary>The storage, which nothing above it can be right about if this is wrong.</summary>
/// <remarks>
///     docs/plan/42 § B1. The interesting facts here are all about the assembly rather than the
///     arithmetic: a Laplacian is built by visiting faces, so the same entry arrives once per incident
///     triangle and in whatever order the faces are stored.
/// </remarks>
public class SparseMatrixTests {
    [Fact]
    public void Entries_are_sorted_by_column_whatever_order_they_arrived_in() {
        var builder = new SparseMatrixBuilder(2, 4);
        builder.Add(0, 3, 1d);
        builder.Add(1, 0, 2d);
        builder.Add(0, 1, 3d);
        builder.Add(0, 0, 4d);
        builder.Add(1, 3, 5d);

        var matrix = builder.Build();

        Assert.Equal([0, 3, 5], matrix.RowStart);
        Assert.Equal([0, 1, 3, 0, 3], matrix.ColumnIndex);
        Assert.Equal([4d, 3d, 1d, 2d, 5d], matrix.Value);
    }

    /// <summary>The face loop's normal case: one entry per incident triangle, summed.</summary>
    [Fact]
    public void Duplicates_accumulate() {
        var builder = new SparseMatrixBuilder(1, 2);
        builder.Add(0, 1, 0.5d);
        builder.Add(0, 0, 1d);
        builder.Add(0, 1, 0.25d);
        builder.Add(0, 1, 0.125d);

        var matrix = builder.Build();

        Assert.Equal(2, matrix.NonZeroCount);
        Assert.Equal(1d, matrix[0, 0]);
        Assert.Equal(0.875d, matrix[0, 1]);
    }

    /// <summary>
    ///     ⚠ Floating-point addition is not associative, so "duplicates are summed" is not a complete
    ///     statement until the order is nailed down. Three values chosen so that the two orders give
    ///     genuinely different doubles.
    /// </summary>
    [Fact]
    public void Duplicates_accumulate_in_the_order_they_were_added() {
        const double Tiny = 1e-16d;

        var forward = new SparseMatrixBuilder(1, 1);
        forward.Add(0, 0, 1d);
        forward.Add(0, 0, Tiny);
        forward.Add(0, 0, Tiny);

        var reverse = new SparseMatrixBuilder(1, 1);
        reverse.Add(0, 0, Tiny);
        reverse.Add(0, 0, Tiny);
        reverse.Add(0, 0, 1d);

        // (1 + tiny) + tiny rounds each addition away and lands on one; tiny + tiny is exact and
        // carries the pair over half an ulp, so it lands one representable step above. An unstable
        // sort would make which of the two you get a property of the sort's pivot choices.
        Assert.Equal(1d, forward.Build()[0, 0]);
        Assert.Equal(Math.BitIncrement(1d), reverse.Build()[0, 0]);
    }

    /// <summary>An unknown nothing touched. It has a defined answer here and a NaN two files up.</summary>
    [Fact]
    public void An_empty_row_stores_nothing_and_multiplies_to_zero() {
        var builder = new SparseMatrixBuilder(3, 3);
        builder.Add(0, 0, 2d);
        builder.Add(2, 2, 3d);

        var matrix = builder.Build();

        Assert.Equal(matrix.RowStart[1], matrix.RowStart[2]);
        Assert.Equal(0d, matrix.Diagonal(1));
        Assert.Equal(0d, matrix.RowMagnitude(1));

        var destination = new double[3];
        matrix.Multiply([1d, 1d, 1d], destination);

        Assert.Equal([2d, 0d, 3d], destination);
    }

    /// <summary>An explicit zero is a structural entry, because the pattern is what IC(0) is defined against.</summary>
    [Fact]
    public void An_explicit_zero_survives_as_a_stored_entry() {
        var builder = new SparseMatrixBuilder(1, 2);
        builder.Add(0, 0, 0d);
        builder.Add(0, 1, 1d);

        var matrix = builder.Build();

        Assert.Equal(2, matrix.NonZeroCount);
        Assert.Equal([0, 1], matrix.ColumnIndex);
    }

    [Fact]
    public void A_matrix_with_no_rows_builds() {
        var matrix = new SparseMatrixBuilder(0, 0).Build();

        Assert.Equal(0, matrix.RowCount);
        Assert.Equal(0, matrix.NonZeroCount);
        Assert.True(matrix.IsSquare);
    }

    [Fact]
    public void The_transpose_swaps_the_shape_and_keeps_the_ordering() {
        var builder = new SparseMatrixBuilder(2, 3);
        builder.Add(0, 0, 1d);
        builder.Add(0, 2, 2d);
        builder.Add(1, 1, 3d);

        var transpose = builder.Build().Transpose();

        Assert.Equal(3, transpose.RowCount);
        Assert.Equal(2, transpose.ColumnCount);
        Assert.Equal([0, 1, 2, 3], transpose.RowStart);
        Assert.Equal([0, 1, 0], transpose.ColumnIndex);
        Assert.Equal([1d, 3d, 2d], transpose.Value);
    }

    [Fact]
    public void Multiplying_by_the_transpose_is_the_other_product() {
        var builder = new SparseMatrixBuilder(3, 2);
        builder.Add(0, 0, 1d);
        builder.Add(0, 1, 1d);
        builder.Add(1, 0, 1d);
        builder.Add(1, 1, 2d);
        builder.Add(2, 0, 1d);
        builder.Add(2, 1, 3d);

        var matrix = builder.Build();
        var destination = new double[2];
        matrix.Transpose().Multiply([1d, 2d, 2d], destination);

        Assert.Equal([5d, 11d], destination);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(2, 0)]
    [InlineData(0, 3)]
    public void An_entry_outside_the_shape_throws(int row, int column) {
        var builder = new SparseMatrixBuilder(2, 3);

        Assert.Throws<ArgumentOutOfRangeException>(() => builder.Add(row, column, 1d));
    }

    [Fact]
    public void A_vector_of_the_wrong_length_throws() {
        var matrix = PoissonGrid.Matrix(3);

        Assert.Throws<ArgumentException>(() => matrix.Multiply(new double[8], new double[9]));
        Assert.Throws<ArgumentException>(() => matrix.Multiply(new double[9], new double[8]));
    }
}
