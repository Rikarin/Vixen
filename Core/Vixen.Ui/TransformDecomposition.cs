// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Ui;

/// <summary>A 4×4 transform taken apart into the components CSS interpolates, and put back together.</summary>
/// <param name="Translate">The translation, in points.</param>
/// <param name="Scale">The scale along each axis, negative on all three where the matrix mirrored.</param>
/// <param name="Skew">The xy, xz and yz shears, as factors rather than angles.</param>
/// <param name="Perspective">The perspective column, whose last component is one for any affine.</param>
/// <param name="Rotation">The rotation, as a unit quaternion <c>(x, y, z, w)</c>.</param>
/// <remarks>
///     <para>
///         <b>CSS Transforms 2 § 12's "Interpolation of Matrices", which is where a transition lands
///         when two transform lists cannot be interpolated function by function</b> —
///         <c>translateX(100px)</c> against <c>rotate(90deg)</c>, a <c>matrix()</c> against anything,
///         a <c>rotateX</c> against a <c>rotateY</c> that both turn. Each end is decomposed, the parts
///         are interpolated — linearly, the rotation along the great circle — and the result is
///         recomposed. #174.
///     </para>
///     <para>
///         ⚠ <b>Written from the algebra rather than transliterated from the specification's
///         pseudocode, and the difference is a transposed rotation.</b> The pseudocode's
///         recomposition builds its rotation matrix with <c>2(xy − zw)</c> in the cell its
///         decomposition reads <c>2(xy + zw)</c> from, so a literal port decomposes a quarter turn and
///         recomposes the opposite one. Here both halves are derived from one statement — in this
///         engine's row-vector convention (ADR-003), a matrix is <c>M = N · P</c>, where <c>N</c> is
///         the affine <c>[S·K·R, 0; t, 1]</c> and <c>P</c> the identity with the perspective vector as
///         its fourth column — and the gate is the round trip:
///         <c>Vixen.Ui.Tests.TransformDecompositionTests</c> recomposes a decomposition of random
///         matrices and asks for the same cells, which an inconsistent pair cannot pass. (A
///         transposition made consistently in both halves would pass it and would also be harmless:
///         conjugation commutes with slerp.)
///     </para>
///     <para>
///         ⚠ <b>Doubles throughout, and floats only at the edges.</b> The Gram–Schmidt pass
///         subtracts nearly equal numbers whenever a shear is small, and the quaternion comes out of
///         square roots of differences of the diagonal — both are where single precision loses the
///         third digit on an ordinary rotation.
///     </para>
/// </remarks>
readonly record struct TransformDecomposition(
    (double X, double Y, double Z) Translate,
    (double X, double Y, double Z) Scale,
    (double XY, double XZ, double YZ) Skew,
    (double X, double Y, double Z, double W) Perspective,
    (double X, double Y, double Z, double W) Rotation
) {
    /// <summary>Takes a matrix apart, or answers false for one that cannot be.</summary>
    /// <param name="matrix">The matrix, row-vector.</param>
    /// <param name="parts">Its components.</param>
    /// <returns>
    ///     False where <c>M44</c> is zero or the upper 3×3 is singular — a <c>scale(0)</c>, a
    ///     <c>scaleZ(0)</c> — which CSS answers with a discrete flip at the midpoint; see
    ///     <see cref="Interpolate" />.
    /// </returns>
    public static bool TryDecompose(in Matrix4x4 matrix, out TransformDecomposition parts) {
        parts = default;

        Span<double> m = [
            matrix.M11, matrix.M12, matrix.M13, matrix.M14,
            matrix.M21, matrix.M22, matrix.M23, matrix.M24,
            matrix.M31, matrix.M32, matrix.M33, matrix.M34,
            matrix.M41, matrix.M42, matrix.M43, matrix.M44
        ];

        if (m[15] == 0d) {
            return false;
        }

        var normal = m[15];

        for (var index = 0; index < 16; index++) {
            m[index] /= normal;
        }

        // `N`: the matrix with its perspective column set to the identity's. It is the affine the
        // perspective is solved against, and its determinant is the upper 3×3's — the singularity
        // test for free.
        Span<double> n = stackalloc double[16];
        m.CopyTo(n);
        n[3] = 0d;
        n[7] = 0d;
        n[11] = 0d;
        n[15] = 1d;

        Span<double> inverse = stackalloc double[16];

        if (!Invert(n, inverse)) {
            return false;
        }

        // `M = N · P` makes M's fourth column `N · p`, so `p = N⁻¹ · (that column)`.
        (double X, double Y, double Z, double W) perspective = (0d, 0d, 0d, 1d);

        if (m[3] != 0d || m[7] != 0d || m[11] != 0d) {
            Span<double> column = [m[3], m[7], m[11], m[15]];
            Span<double> solved = stackalloc double[4];

            for (var row = 0; row < 4; row++) {
                solved[row] = (inverse[(row * 4) + 0] * column[0])
                    + (inverse[(row * 4) + 1] * column[1])
                    + (inverse[(row * 4) + 2] * column[2])
                    + (inverse[(row * 4) + 3] * column[3]);
            }

            perspective = (solved[0], solved[1], solved[2], solved[3]);
        }

        var translate = (m[12], m[13], m[14]);

        // The upper 3×3 is `S · K · R`, row by row: row i is `sᵢ (rᵢ + Σⱼ₍ⱼ₍ᵢ₎ Kᵢⱼ rⱼ)`. Gram–Schmidt
        // on the rows recovers each factor in the order it was folded in.
        var r0 = (X: m[0], Y: m[1], Z: m[2]);
        var r1 = (X: m[4], Y: m[5], Z: m[6]);
        var r2 = (X: m[8], Y: m[9], Z: m[10]);

        var sx = Length(r0);
        r0 = Divide(r0, sx);

        var xy = Dot(r0, r1);
        r1 = Combine(r1, r0, -xy);

        var sy = Length(r1);
        r1 = Divide(r1, sy);
        xy /= sy;

        var xz = Dot(r0, r2);
        r2 = Combine(r2, r0, -xz);

        var yz = Dot(r1, r2);
        r2 = Combine(r2, r1, -yz);

        var sz = Length(r2);
        r2 = Divide(r2, sz);
        xz /= sz;
        yz /= sz;

        // ⚠ <b>A mirror is carried by the scale and not by the rotation</b>, because a quaternion
        // cannot hold one. All three scales flip together, which is what makes `scaleX(-1)` against
        // `rotate(180deg)` decompose to the same rotation and different scales rather than failing.
        if (Dot(r0, Cross(r1, r2)) < 0d) {
            sx = -sx;
            sy = -sy;
            sz = -sz;
            r0 = Divide(r0, -1d);
            r1 = Divide(r1, -1d);
            r2 = Divide(r2, -1d);
        }

        parts = new TransformDecomposition(
            translate,
            (sx, sy, sz),
            (xy, xz, yz),
            perspective,
            Quaternion(r0, r1, r2)
        );

        return true;
    }

    /// <summary>Puts the components back together as a row-vector matrix.</summary>
    /// <returns><c>N · P</c>, with <c>N = [S·K·R, 0; t, 1]</c>.</returns>
    public Matrix4x4 Recompose() {
        var (x, y, z, w) = Rotation;

        // The rotation's COLUMN-vector matrix `C`, which is the one every reference writes down; this
        // engine's row-vector rotation is its transpose, so row i of R is column i of C.
        var c00 = 1d - (2d * ((y * y) + (z * z)));
        var c01 = 2d * ((x * y) - (z * w));
        var c02 = 2d * ((x * z) + (y * w));
        var c10 = 2d * ((x * y) + (z * w));
        var c11 = 1d - (2d * ((x * x) + (z * z)));
        var c12 = 2d * ((y * z) - (x * w));
        var c20 = 2d * ((x * z) - (y * w));
        var c21 = 2d * ((y * z) + (x * w));
        var c22 = 1d - (2d * ((x * x) + (y * y)));

        var r0 = (X: c00, Y: c10, Z: c20);
        var r1 = (X: c01, Y: c11, Z: c21);
        var r2 = (X: c02, Y: c12, Z: c22);

        var u0 = Scaled(r0, Scale.X);
        var u1 = Scaled(Combine(r1, r0, Skew.XY), Scale.Y);
        var u2 = Scaled(Combine(Combine(r2, r0, Skew.XZ), r1, Skew.YZ), Scale.Z);

        var (px, py, pz, pw) = Perspective;
        var (tx, ty, tz) = Translate;

        return new Matrix4x4(
            (float) u0.X, (float) u0.Y, (float) u0.Z, (float) Dot(u0, (px, py, pz)),
            (float) u1.X, (float) u1.Y, (float) u1.Z, (float) Dot(u1, (px, py, pz)),
            (float) u2.X, (float) u2.Y, (float) u2.Z, (float) Dot(u2, (px, py, pz)),
            (float) tx, (float) ty, (float) tz, (float) (Dot((tx, ty, tz), (px, py, pz)) + pw)
        );
    }

    /// <summary>The matrix <paramref name="progress" /> of the way from one to the other.</summary>
    /// <param name="from">Where it starts.</param>
    /// <param name="to">Where it ends.</param>
    /// <param name="progress">How far, which a spring or a back-easing may take outside zero to one.</param>
    /// <returns>The interpolated matrix.</returns>
    /// <remarks>
    ///     ⚠ <b>A matrix that cannot be decomposed flips at the midpoint rather than refusing</b>,
    ///     which is CSS's rule and not a convenience: <c>scale(0)</c> is the commonest way anybody
    ///     writes "collapsed", and a transition out of it that refused would leave the element
    ///     untransformed — visible at full size — for the whole of the first half.
    /// </remarks>
    public static Matrix4x4 Interpolate(in Matrix4x4 from, in Matrix4x4 to, float progress) {
        if (!TryDecompose(from, out var a) || !TryDecompose(to, out var b)) {
            return progress < 0.5f ? from : to;
        }

        return Lerp(a, b, progress).Recompose();
    }

    /// <summary>The components <paramref name="progress" /> of the way along, the rotation by slerp.</summary>
    /// <remarks>
    ///     ⚠ <b>No shortest-path flip, which is Transforms 2's slerp as written.</b> Both quaternions
    ///     come out of <see cref="TryDecompose" /> with a non-negative <c>w</c>, so the two ends of a
    ///     turn are always within half a revolution of each other on the great circle the
    ///     specification walks; negating one when their dot product is negative would take the
    ///     other way round a turn of more than 180°, which is a different picture from a browser's.
    /// </remarks>
    public static TransformDecomposition Lerp(in TransformDecomposition from, in TransformDecomposition to, float progress) {
        double t = progress;

        static double L(double a, double b, double t) => a + ((b - a) * t);

        var (ax, ay, az, aw) = from.Rotation;
        var (bx, by, bz, bw) = to.Rotation;

        var product = Math.Clamp((ax * bx) + (ay * by) + (az * bz) + (aw * bw), -1d, 1d);

        (double X, double Y, double Z, double W) rotation;

        // ⚠ Both poles, not only the one the specification names: a dot of −1 is the SAME rotation
        // written as its antipode, and the formula below divides by zero on it.
        if (Math.Abs(product) >= 1d - 1e-12) {
            rotation = from.Rotation;
        } else {
            var theta = Math.Acos(product);
            var w = Math.Sin(t * theta) / Math.Sqrt(1d - (product * product));
            var keep = Math.Cos(t * theta) - (product * w);

            rotation = ((ax * keep) + (bx * w), (ay * keep) + (by * w), (az * keep) + (bz * w), (aw * keep) + (bw * w));
        }

        return new TransformDecomposition(
            (L(from.Translate.X, to.Translate.X, t), L(from.Translate.Y, to.Translate.Y, t), L(from.Translate.Z, to.Translate.Z, t)),
            (L(from.Scale.X, to.Scale.X, t), L(from.Scale.Y, to.Scale.Y, t), L(from.Scale.Z, to.Scale.Z, t)),
            (L(from.Skew.XY, to.Skew.XY, t), L(from.Skew.XZ, to.Skew.XZ, t), L(from.Skew.YZ, to.Skew.YZ, t)),
            (
                L(from.Perspective.X, to.Perspective.X, t),
                L(from.Perspective.Y, to.Perspective.Y, t),
                L(from.Perspective.Z, to.Perspective.Z, t),
                L(from.Perspective.W, to.Perspective.W, t)
            ),
            rotation
        );
    }

    /// <summary>The unit quaternion of a row-vector rotation, given its three rows.</summary>
    /// <remarks>
    ///     The standard extraction from the column-vector matrix <c>C = Rᵀ</c>, whose cell <c>Cᵢⱼ</c>
    ///     is <c>R</c>'s <c>Rⱼᵢ</c>. Each component's magnitude comes from the diagonal and its sign
    ///     from the antisymmetric part — Transforms 2's own choice, which keeps <c>w</c> non-negative.
    /// </remarks>
    static (double X, double Y, double Z, double W) Quaternion(
        (double X, double Y, double Z) r0,
        (double X, double Y, double Z) r1,
        (double X, double Y, double Z) r2
    ) {
        // C = Rᵀ: C00 = r0.X, C11 = r1.Y, C22 = r2.Z; C21 = R12 = r1.Z; C12 = R21 = r2.Y; and so on.
        var x = 0.5d * Math.Sqrt(Math.Max(1d + r0.X - r1.Y - r2.Z, 0d));
        var y = 0.5d * Math.Sqrt(Math.Max(1d - r0.X + r1.Y - r2.Z, 0d));
        var z = 0.5d * Math.Sqrt(Math.Max(1d - r0.X - r1.Y + r2.Z, 0d));
        var w = 0.5d * Math.Sqrt(Math.Max(1d + r0.X + r1.Y + r2.Z, 0d));

        if (r1.Z - r2.Y < 0d) {
            x = -x;
        }

        if (r2.X - r0.Z < 0d) {
            y = -y;
        }

        if (r0.Y - r1.X < 0d) {
            z = -z;
        }

        return (x, y, z, w);
    }

    static double Length((double X, double Y, double Z) v) => Math.Sqrt(Dot(v, v));

    static double Dot((double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
        (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);

    static (double X, double Y, double Z) Cross((double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
        ((a.Y * b.Z) - (a.Z * b.Y), (a.Z * b.X) - (a.X * b.Z), (a.X * b.Y) - (a.Y * b.X));

    static (double X, double Y, double Z) Divide((double X, double Y, double Z) v, double by) =>
        (v.X / by, v.Y / by, v.Z / by);

    static (double X, double Y, double Z) Scaled((double X, double Y, double Z) v, double by) =>
        (v.X * by, v.Y * by, v.Z * by);

    /// <summary><c>a + b · k</c>.</summary>
    static (double X, double Y, double Z) Combine((double X, double Y, double Z) a, (double X, double Y, double Z) b, double k) =>
        (a.X + (b.X * k), a.Y + (b.Y * k), a.Z + (b.Z * k));

    /// <summary>Inverts a row-major 4×4 by Gauss–Jordan with partial pivoting.</summary>
    /// <returns>False for a matrix whose pivot vanishes, which is the singular case.</returns>
    static bool Invert(ReadOnlySpan<double> matrix, Span<double> inverse) {
        Span<double> work = stackalloc double[16];
        matrix.CopyTo(work);

        for (var index = 0; index < 16; index++) {
            inverse[index] = index % 5 == 0 ? 1d : 0d;
        }

        for (var column = 0; column < 4; column++) {
            var pivot = column;

            for (var row = column + 1; row < 4; row++) {
                if (Math.Abs(work[(row * 4) + column]) > Math.Abs(work[(pivot * 4) + column])) {
                    pivot = row;
                }
            }

            if (Math.Abs(work[(pivot * 4) + column]) < 1e-12) {
                return false;
            }

            if (pivot != column) {
                for (var k = 0; k < 4; k++) {
                    (work[(pivot * 4) + k], work[(column * 4) + k]) = (work[(column * 4) + k], work[(pivot * 4) + k]);
                    (inverse[(pivot * 4) + k], inverse[(column * 4) + k]) = (inverse[(column * 4) + k], inverse[(pivot * 4) + k]);
                }
            }

            var scale = work[(column * 4) + column];

            for (var k = 0; k < 4; k++) {
                work[(column * 4) + k] /= scale;
                inverse[(column * 4) + k] /= scale;
            }

            for (var row = 0; row < 4; row++) {
                if (row == column) {
                    continue;
                }

                var factor = work[(row * 4) + column];

                if (factor == 0d) {
                    continue;
                }

                for (var k = 0; k < 4; k++) {
                    work[(row * 4) + k] -= factor * work[(column * 4) + k];
                    inverse[(row * 4) + k] -= factor * inverse[(column * 4) + k];
                }
            }
        }

        return true;
    }
}
