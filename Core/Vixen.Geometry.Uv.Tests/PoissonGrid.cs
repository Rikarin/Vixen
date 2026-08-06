// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Geometry.Uv.Solving;

namespace Vixen.Geometry.Uv.Tests;

/// <summary>The five-point Laplacian on a square grid, which is the one sparse system with an exact answer.</summary>
/// <remarks>
///     <para>
///         Every rung of docs/plan/42 § D5's ladder assembles a Laplacian over a mesh, and a mesh
///         Laplacian has no closed form to check against. A regular grid does: the eigenvectors of the
///         five-point stencil with Dirichlet boundaries are
///         <c>sin(jπph)·sin(kπqh)</c> with eigenvalue <c>4 − 2cos(jπh) − 2cos(kπh)</c>, exactly, for
///         every <c>j</c> and <c>k</c>. So <c>b = λv</c> has the answer <c>v</c> and the test is
///         against arithmetic rather than against another implementation.
///     </para>
///     <para>
///         It is also the right <i>shape</i>: interior rows have five entries and boundary rows have
///         three or four, so the empty-neighbour arithmetic that a mesh boundary produces is exercised
///         rather than assumed away.
///     </para>
/// </remarks>
static class PoissonGrid {
    /// <summary>Assembles the stencil over an <paramref name="extent" /> squared interior grid.</summary>
    /// <param name="extent">How many interior points along one side.</param>
    /// <param name="scale">What to multiply every entry by, for the scale-invariance tests.</param>
    /// <returns>The matrix, <c>extent²</c> squared.</returns>
    public static SparseMatrix Matrix(int extent, double scale = 1d) {
        var size = extent * extent;
        var builder = new SparseMatrixBuilder(size, size);

        for (var y = 0; y < extent; y++) {
            for (var x = 0; x < extent; x++) {
                var row = (y * extent) + x;
                builder.Add(row, row, 4d * scale);

                if (x > 0) {
                    builder.Add(row, row - 1, -scale);
                }

                if (x < extent - 1) {
                    builder.Add(row, row + 1, -scale);
                }

                if (y > 0) {
                    builder.Add(row, row - extent, -scale);
                }

                if (y < extent - 1) {
                    builder.Add(row, row + extent, -scale);
                }
            }
        }

        return builder.Build();
    }

    /// <summary>The <c>(j, k)</c> eigenvector, sampled on the grid.</summary>
    /// <param name="extent">How many interior points along one side.</param>
    /// <param name="first">The first mode number, one or more.</param>
    /// <param name="second">The second mode number, one or more.</param>
    /// <returns>The eigenvector.</returns>
    public static double[] Eigenvector(int extent, int first, int second) {
        var spacing = 1d / (extent + 1);
        var vector = new double[extent * extent];

        for (var y = 0; y < extent; y++) {
            for (var x = 0; x < extent; x++) {
                vector[(y * extent) + x] =
                    Math.Sin(first * Math.PI * (x + 1) * spacing) * Math.Sin(second * Math.PI * (y + 1) * spacing);
            }
        }

        return vector;
    }

    /// <summary>The eigenvalue that goes with <see cref="Eigenvector" />.</summary>
    /// <param name="extent">How many interior points along one side.</param>
    /// <param name="first">The first mode number.</param>
    /// <param name="second">The second mode number.</param>
    /// <returns>The eigenvalue.</returns>
    public static double Eigenvalue(int extent, int first, int second) {
        var spacing = 1d / (extent + 1);

        return 4d - (2d * Math.Cos(first * Math.PI * spacing)) - (2d * Math.Cos(second * Math.PI * spacing));
    }
}
