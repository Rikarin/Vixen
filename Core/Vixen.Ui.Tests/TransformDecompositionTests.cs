// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>The matrix decomposition a transition falls back to, held to a round trip and a closed form.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The round trip is the gate the specification's own pseudocode would fail.</b> Its
///         recomposition builds the rotation with <c>2(xy − zw)</c> where its decomposition reads
///         <c>2(xy + zw)</c>, so a literal port recomposes the transpose of every rotation it took
///         apart. A decomposition and a recomposition written from one statement survive
///         <c>Recompose(Decompose(M)) = M</c>; two written separately, even correctly each by some
///         convention, need not.
///     </para>
///     <para>
///         ⚠ <b>The round trip says nothing about the interpolation between the two ends</b> — a
///         slerp walking the wrong way round, or at <c>1 − t</c>, recomposes every end exactly. So
///         the middle is pinned by a closed form: half way from no turn to a quarter turn is an eighth
///         of a turn the same way round. (A transposition made consistently in BOTH halves is not a
///         defect either test needs to see: conjugation commutes with slerp, so it recomposes the
///         right rotation at every step.)
///     </para>
/// </remarks>
public class TransformDecompositionTests {
    /// <summary>
    ///     ⚠ <b>Two hundred arbitrary matrices come back cell for cell</b> — perspective column,
    ///     shear, mirror and all.
    /// </summary>
    /// <remarks>
    ///     Every cell drawn uniformly, rather than a product of friendly factors built by hand: a
    ///     matrix assembled from a rotation, a scale and a shear chosen in the same convention as the
    ///     decomposition is a matrix the decomposition was written to take apart, and it proves
    ///     nothing about the rest. Each is compared against itself divided by its <c>M44</c>, which is
    ///     the only normalisation the decomposition makes and the same homography.
    /// </remarks>
    [Fact]
    public void A_decomposition_recomposes_to_the_matrix_it_came_from() {
        var random = new Random(174);
        var checkedCount = 0;
        Span<float> cells = stackalloc float[16];

        for (var attempt = 0; attempt < 400 && checkedCount < 200; attempt++) {
            for (var index = 0; index < 16; index++) {
                cells[index] = (float) ((random.NextDouble() * 4d) - 2d);
            }

            // A perspective column small enough that the matrix is a plausible projection rather
            // than one whose w changes sign across the element, and an M44 well away from zero.
            cells[3] *= 0.01f;
            cells[7] *= 0.01f;
            cells[11] *= 0.01f;
            cells[15] = (float) (0.5d + random.NextDouble());

            var matrix = new Matrix4x4(
                cells[0], cells[1], cells[2], cells[3],
                cells[4], cells[5], cells[6], cells[7],
                cells[8], cells[9], cells[10], cells[11],
                cells[12], cells[13], cells[14], cells[15]
            );

            // Near-singular upper 3×3s are real but prove precision rather than algebra.
            var upper = (cells[0] * ((cells[5] * cells[10]) - (cells[6] * cells[9])))
                - (cells[1] * ((cells[4] * cells[10]) - (cells[6] * cells[8])))
                + (cells[2] * ((cells[4] * cells[9]) - (cells[5] * cells[8])));

            if (MathF.Abs(upper) < 0.25f) {
                continue;
            }

            Assert.True(TransformDecomposition.TryDecompose(matrix, out var parts), $"matrix {attempt} did not decompose");

            var back = parts.Recompose();
            var w = cells[15];

            AssertCells(Scaled(matrix, 1f / w), back, 2e-4f, $"matrix {attempt}");
            checkedCount++;
        }

        Assert.Equal(200, checkedCount);
    }

    /// <summary>Half way from no turn to a quarter turn is an eighth of a turn, the same way round.</summary>
    [Fact]
    public void Half_way_between_two_turns_is_the_turn_between_them() {
        var none = Rotation(0f);
        var quarter = Rotation(90f);

        AssertCells(Rotation(45f), TransformDecomposition.Interpolate(none, quarter, 0.5f), 1e-5f, "an eighth");
        AssertCells(Rotation(22.5f), TransformDecomposition.Interpolate(none, quarter, 0.25f), 1e-5f, "a sixteenth");
    }

    /// <summary>
    ///     ⚠ <b>A matrix that cannot be taken apart flips at the midpoint rather than refusing.</b>
    /// </summary>
    /// <remarks>
    ///     <c>scale(0)</c> has no rotation to recover — its upper 3×3 is singular — and CSS answers a
    ///     pair containing one with a discrete swap. Refusing instead would draw the element
    ///     untransformed for the whole of the first half, which is the full-size card a collapse was
    ///     written to hide.
    /// </remarks>
    [Fact]
    public void A_matrix_that_cannot_be_decomposed_flips_at_the_midpoint() {
        var collapsed = new Matrix4x4(
            0f, 0f, 0f, 0f,
            0f, 0f, 0f, 0f,
            0f, 0f, 1f, 0f,
            0f, 0f, 0f, 1f
        );

        Assert.False(TransformDecomposition.TryDecompose(collapsed, out _));

        AssertCells(collapsed, TransformDecomposition.Interpolate(collapsed, Matrix4x4.Identity, 0.4f), 0f, "before the midpoint");
        AssertCells(Matrix4x4.Identity, TransformDecomposition.Interpolate(collapsed, Matrix4x4.Identity, 0.6f), 0f, "after it");
    }

    /// <summary>The row-vector turn a <c>rotate()</c> is — the same arithmetic <c>TransformReader</c> uses.</summary>
    static Matrix4x4 Rotation(float degrees) {
        var radians = degrees * (MathF.PI / 180f);
        var (cos, sin) = (MathF.Cos(radians), MathF.Sin(radians));

        return new Matrix4x4(
            cos, sin, 0f, 0f,
            -sin, cos, 0f, 0f,
            0f, 0f, 1f, 0f,
            0f, 0f, 0f, 1f
        );
    }

    static Matrix4x4 Scaled(in Matrix4x4 m, float k) =>
        new(
            m.M11 * k, m.M12 * k, m.M13 * k, m.M14 * k,
            m.M21 * k, m.M22 * k, m.M23 * k, m.M24 * k,
            m.M31 * k, m.M32 * k, m.M33 * k, m.M34 * k,
            m.M41 * k, m.M42 * k, m.M43 * k, m.M44 * k
        );

    static void AssertCells(in Matrix4x4 expected, in Matrix4x4 actual, float tolerance, string what) {
        ReadOnlySpan<float> e = [
            expected.M11, expected.M12, expected.M13, expected.M14,
            expected.M21, expected.M22, expected.M23, expected.M24,
            expected.M31, expected.M32, expected.M33, expected.M34,
            expected.M41, expected.M42, expected.M43, expected.M44
        ];

        ReadOnlySpan<float> a = [
            actual.M11, actual.M12, actual.M13, actual.M14,
            actual.M21, actual.M22, actual.M23, actual.M24,
            actual.M31, actual.M32, actual.M33, actual.M34,
            actual.M41, actual.M42, actual.M43, actual.M44
        ];

        for (var index = 0; index < 16; index++) {
            var allowed = tolerance * MathF.Max(1f, MathF.Abs(e[index]));

            Assert.True(
                MathF.Abs(e[index] - a[index]) <= allowed,
                $"{what}: cell M{(index / 4) + 1}{(index % 4) + 1} is {a[index]}, expected {e[index]}"
            );
        }
    }
}
