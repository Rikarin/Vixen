// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using BigInteger = System.Numerics.BigInteger;

namespace Vixen.Core.Mathematics;

/// <summary>
///     The two geometric predicates a Delaunay tetrahedralisation is made of — which side of a
///     plane a point is on, and whether a point is inside a circumsphere — answered exactly.
/// </summary>
/// <remarks>
///     <para>
///         These return a <em>sign</em>, not a magnitude, and the sign is the exact one. That is
///         the whole point. A predicate evaluated in floating point returns a number that is
///         nearly right, and "nearly right" has no meaning for a question whose answer is one of
///         three values: an in-sphere test that says <c>-1e-19</c> where the truth is <c>0</c> has
///         not made a small error, it has given the wrong answer. Incremental construction then
///         builds a cavity that is not star-shaped, and the mesh that comes out is not a mesh.
///     </para>
///     <para>
///         <strong>Filtered, not slow.</strong> Every predicate first evaluates in
///         <see langword="double" /> alongside a bound on its own rounding error — the permanent
///         of the same expression, which is what you get by replacing every term with its absolute
///         value. If the result is further from zero than the bound, the sign is certain and the
///         answer is already in hand; that is the case for essentially every call. Only when the
///         two overlap does the exact path run, and it runs in
///         <see cref="System.Numerics.BigInteger" /> over the inputs rescaled to integers, where
///         there is no rounding at all.
///     </para>
///     <para>
///         The error bounds below are deliberately several times larger than the tightest
///         derivable ones. A bound that is too large costs a rare exact evaluation; a bound that
///         is too small is a wrong answer that nothing reports. Only one of those is worth
///         economising on.
///     </para>
///     <para>
///         Inputs must be finite. A NaN or an infinity makes both the filter and the rescaling
///         meaningless, and callers that read positions from content are expected to reject them
///         before they get here — <see cref="DelaunayTetrahedralization" /> does.
///     </para>
/// </remarks>
public static class ExactPredicates {
    // 2^-53: the unit roundoff of binary64, which is half of double.Epsilon's namesake
    // (double.Epsilon is the smallest subnormal, not the machine epsilon — a naming accident
    // worth stepping around explicitly).
    const double Roundoff = 1.1102230246251565e-16;

    // Generous by a factor of about four over what the operation counts require. See the remarks.
    const double OrientBound = 32.0 * Roundoff;
    const double SphereBound = 128.0 * Roundoff;

    /// <summary>
    ///     Which side of the plane through <paramref name="a" />, <paramref name="b" /> and
    ///     <paramref name="c" /> the point <paramref name="d" /> lies on.
    /// </summary>
    /// <returns>
    ///     <c>+1</c> when the tetrahedron <c>(a, b, c, d)</c> is positively oriented, <c>-1</c>
    ///     when it is negatively oriented, and <c>0</c> when the four points are coplanar.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         "Positively oriented" here means <c>d</c> lies on the side of the triangle that
    ///         <c>(b - a) × (c - a)</c> points <em>away</em> from. That is the determinant's own
    ///         convention rather than a choice — the value is
    ///         <c>det[a - d; b - d; c - d]</c>, which is six times the signed volume — and it is
    ///         the one the in-sphere test below expects, so the two agree without a sign to
    ///         remember.
    ///     </para>
    ///     <para>
    ///         The result is fully antisymmetric: swapping any two arguments flips the sign, and
    ///         any even permutation leaves it alone. Both facts are relied on, in the face tables
    ///         of a tetrahedral mesh and in the perturbation below.
    ///     </para>
    /// </remarks>
    public static int Orient3D(Vector3 a, Vector3 b, Vector3 c, Vector3 d) {
        double adx = a.X - (double)d.X, ady = a.Y - (double)d.Y, adz = a.Z - (double)d.Z;
        double bdx = b.X - (double)d.X, bdy = b.Y - (double)d.Y, bdz = b.Z - (double)d.Z;
        double cdx = c.X - (double)d.X, cdy = c.Y - (double)d.Y, cdz = c.Z - (double)d.Z;

        var bc = (bdy * cdz) - (bdz * cdy);
        var ca = (cdy * adz) - (cdz * ady);
        var ab = (ady * bdz) - (adz * bdy);

        var determinant = (adx * bc) + (bdx * ca) + (cdx * ab);

        var permanent = ((Math.Abs(bdy * cdz) + Math.Abs(bdz * cdy)) * Math.Abs(adx))
            + ((Math.Abs(cdy * adz) + Math.Abs(cdz * ady)) * Math.Abs(bdx))
            + ((Math.Abs(ady * bdz) + Math.Abs(adz * bdy)) * Math.Abs(cdx));

        var bound = OrientBound * permanent;

        if (determinant > bound || determinant < -bound) {
            return determinant > 0d ? 1 : -1;
        }

        return ExactOrient3D(a, b, c, d);
    }

    /// <summary>
    ///     Whether <paramref name="e" /> is inside the sphere through <paramref name="a" />,
    ///     <paramref name="b" />, <paramref name="c" /> and <paramref name="d" />.
    /// </summary>
    /// <returns>
    ///     <c>+1</c> inside, <c>-1</c> outside, <c>0</c> when all five points are cospherical.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         <strong>The first four points must be positively oriented</strong> — that is,
    ///         <see cref="Orient3D" /> over them must return <c>+1</c>. Hand it a negatively
    ///         oriented tetrahedron and the answer is exactly inverted, which is not a failure the
    ///         result can report. Any even permutation of the four is the same tetrahedron and
    ///         gives the same answer, so a caller that keeps its cells oriented never has to think
    ///         about it again.
    ///     </para>
    ///     <para>
    ///         Zero is a real answer and a common one: eight probes on the corners of a cube are
    ///         cospherical, and so is every axis-aligned grid anybody authors. A construction that
    ///         needs a decision there wants
    ///         <see cref="InSphere(Vector3, Vector3, Vector3, Vector3, Vector3, int, int, int, int, int)" />,
    ///         which breaks the tie the same way every time.
    ///     </para>
    /// </remarks>
    public static int InSphere(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 e) {
        double aex = a.X - (double)e.X, aey = a.Y - (double)e.Y, aez = a.Z - (double)e.Z;
        double bex = b.X - (double)e.X, bey = b.Y - (double)e.Y, bez = b.Z - (double)e.Z;
        double cex = c.X - (double)e.X, cey = c.Y - (double)e.Y, cez = c.Z - (double)e.Z;
        double dex = d.X - (double)e.X, dey = d.Y - (double)e.Y, dez = d.Z - (double)e.Z;

        // The six two-by-two minors of the x/y columns, each shared by two of the four cofactors
        // below. Sharing them is not only cheaper, it keeps the permanent honest: the same
        // products are the ones being bounded.
        var ab = (aex * bey) - (bex * aey);
        var ac = (aex * cey) - (cex * aey);
        var ad = (aex * dey) - (dex * aey);
        var bc = (bex * cey) - (cex * bey);
        var bd = (bex * dey) - (dex * bey);
        var cd = (cex * dey) - (dex * cey);

        var bcd = (bez * cd) - (cez * bd) + (dez * bc);
        var acd = (aez * cd) - (cez * ad) + (dez * ac);
        var abd = (aez * bd) - (bez * ad) + (dez * ab);
        var abc = (aez * bc) - (bez * ac) + (cez * ab);

        var aLift = (aex * aex) + (aey * aey) + (aez * aez);
        var bLift = (bex * bex) + (bey * bey) + (bez * bez);
        var cLift = (cex * cex) + (cey * cey) + (cez * cez);
        var dLift = (dex * dex) + (dey * dey) + (dez * dez);

        var determinant = (dLift * abc) - (cLift * abd) + (bLift * acd) - (aLift * bcd);

        var abPlus = Math.Abs(aex * bey) + Math.Abs(bex * aey);
        var acPlus = Math.Abs(aex * cey) + Math.Abs(cex * aey);
        var adPlus = Math.Abs(aex * dey) + Math.Abs(dex * aey);
        var bcPlus = Math.Abs(bex * cey) + Math.Abs(cex * bey);
        var bdPlus = Math.Abs(bex * dey) + Math.Abs(dex * bey);
        var cdPlus = Math.Abs(cex * dey) + Math.Abs(dex * cey);

        var permanent =
            (dLift * ((Math.Abs(aez) * bcPlus) + (Math.Abs(bez) * acPlus) + (Math.Abs(cez) * abPlus)))
            + (cLift * ((Math.Abs(aez) * bdPlus) + (Math.Abs(bez) * adPlus) + (Math.Abs(dez) * abPlus)))
            + (bLift * ((Math.Abs(aez) * cdPlus) + (Math.Abs(cez) * adPlus) + (Math.Abs(dez) * acPlus)))
            + (aLift * ((Math.Abs(bez) * cdPlus) + (Math.Abs(cez) * bdPlus) + (Math.Abs(dez) * bcPlus)));

        var bound = SphereBound * permanent;

        if (determinant > bound || determinant < -bound) {
            return determinant > 0d ? 1 : -1;
        }

        return ExactInSphere(a, b, c, d, e);
    }

    /// <summary>
    ///     <see cref="InSphere(Vector3, Vector3, Vector3, Vector3, Vector3)" />, with cospherical
    ///     ties broken by the points' indices rather than left at zero.
    /// </summary>
    /// <returns><c>+1</c> inside or <c>-1</c> outside. Never zero.</returns>
    /// <remarks>
    ///     <para>
    ///         This is simulation of simplicity, and it is what makes an incremental
    ///         tetrahedralisation survive the inputs people actually author. A grid of light
    ///         probes is cospherical eight points at a time; a strict test then finds no cell to
    ///         re-triangulate and a lenient one finds too many, and both produce a mesh that is
    ///         not Delaunay. The fix is not a tolerance — it is to answer the question for a point
    ///         set that has been nudged into general position, and to do the nudging in symbols so
    ///         that no coordinate actually moves.
    ///     </para>
    ///     <para>
    ///         Each point <c>i</c> is lifted onto the paraboloid and then raised by an
    ///         infinitesimal <c>δᵢ</c>, with <c>δ₀ ≫ δ₁ ≫ …</c>, so the <em>lowest</em> index
    ///         dominates. The in-sphere test is a determinant, and the determinant is linear in
    ///         each row, so the perturbed value is exactly
    ///         <c>S + Σ δᵢ·Cᵢ</c> — with no cross terms at all, because two rows perturbed in the
    ///         same column are two identical rows and contribute a zero determinant. The sign is
    ///         therefore the sign of the first non-zero <c>Cᵢ</c>, and every <c>Cᵢ</c> is an
    ///         orientation of the other four points. <c>C</c> for <paramref name="e" /> is the
    ///         orientation of the tetrahedron itself, which is non-zero by precondition, so the
    ///         scan always terminates with an answer.
    ///     </para>
    ///     <para>
    ///         Lowest index dominating is chosen for stability rather than correctness: appending
    ///         a probe gives it the highest index, which cannot change how any earlier tie was
    ///         broken.
    ///     </para>
    /// </remarks>
    /// <param name="a">The first vertex of a positively oriented tetrahedron.</param>
    /// <param name="b">The second vertex.</param>
    /// <param name="c">The third vertex.</param>
    /// <param name="d">The fourth vertex.</param>
    /// <param name="e">The point being tested.</param>
    /// <param name="indexA">A unique, non-negative index for <paramref name="a" />.</param>
    /// <param name="indexB">A unique, non-negative index for <paramref name="b" />.</param>
    /// <param name="indexC">A unique, non-negative index for <paramref name="c" />.</param>
    /// <param name="indexD">A unique, non-negative index for <paramref name="d" />.</param>
    /// <param name="indexE">A unique, non-negative index for <paramref name="e" />.</param>
    public static int InSphere(
        Vector3 a,
        Vector3 b,
        Vector3 c,
        Vector3 d,
        Vector3 e,
        int indexA,
        int indexB,
        int indexC,
        int indexD,
        int indexE
    ) {
        var side = InSphere(a, b, c, d, e);
        if (side != 0) {
            return side;
        }

        // The cofactor of row r's lifted entry, which is ± the orientation of the other four
        // points in their row order. The alternating signs are the determinant's, not a choice.
        Span<int> order = [indexA, indexB, indexC, indexD, indexE];

        for (var rank = 0; rank < 5; rank++) {
            var row = -1;
            for (var candidate = 0; candidate < 5; candidate++) {
                if (order[candidate] >= 0 && (row < 0 || order[candidate] < order[row])) {
                    row = candidate;
                }
            }

            // Cannot happen: row 4's cofactor is the tetrahedron's own orientation, which the
            // precondition says is non-zero, so the loop always returns before exhausting the
            // rows. Kept as a total function rather than an assertion.
            if (row < 0) {
                return -1;
            }

            order[row] = -1;

            var cofactor = row switch {
                0 => -Orient3D(b, c, d, e),
                1 => Orient3D(a, c, d, e),
                2 => -Orient3D(a, b, d, e),
                3 => Orient3D(a, b, c, e),
                _ => -Orient3D(a, b, c, d)
            };

            if (cofactor != 0) {
                return cofactor;
            }
        }

        return -1;
    }

    static int ExactOrient3D(Vector3 a, Vector3 b, Vector3 c, Vector3 d) {
        var scaled = new BigInteger[12];
        ToCommonScale([a.X, a.Y, a.Z, b.X, b.Y, b.Z, c.X, c.Y, c.Z, d.X, d.Y, d.Z], scaled);

        BigInteger adx = scaled[0] - scaled[9], ady = scaled[1] - scaled[10], adz = scaled[2] - scaled[11];
        BigInteger bdx = scaled[3] - scaled[9], bdy = scaled[4] - scaled[10], bdz = scaled[5] - scaled[11];
        BigInteger cdx = scaled[6] - scaled[9], cdy = scaled[7] - scaled[10], cdz = scaled[8] - scaled[11];

        var determinant = (adx * ((bdy * cdz) - (bdz * cdy)))
            + (bdx * ((cdy * adz) - (cdz * ady)))
            + (cdx * ((ady * bdz) - (adz * bdy)));

        return determinant.Sign;
    }

    static int ExactInSphere(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 e) {
        var scaled = new BigInteger[15];

        ToCommonScale(
            [a.X, a.Y, a.Z, b.X, b.Y, b.Z, c.X, c.Y, c.Z, d.X, d.Y, d.Z, e.X, e.Y, e.Z],
            scaled
        );

        BigInteger aex = scaled[0] - scaled[12], aey = scaled[1] - scaled[13], aez = scaled[2] - scaled[14];
        BigInteger bex = scaled[3] - scaled[12], bey = scaled[4] - scaled[13], bez = scaled[5] - scaled[14];
        BigInteger cex = scaled[6] - scaled[12], cey = scaled[7] - scaled[13], cez = scaled[8] - scaled[14];
        BigInteger dex = scaled[9] - scaled[12], dey = scaled[10] - scaled[13], dez = scaled[11] - scaled[14];

        var ab = (aex * bey) - (bex * aey);
        var ac = (aex * cey) - (cex * aey);
        var ad = (aex * dey) - (dex * aey);
        var bc = (bex * cey) - (cex * bey);
        var bd = (bex * dey) - (dex * bey);
        var cd = (cex * dey) - (dex * cey);

        var bcd = (bez * cd) - (cez * bd) + (dez * bc);
        var acd = (aez * cd) - (cez * ad) + (dez * ac);
        var abd = (aez * bd) - (bez * ad) + (dez * ab);
        var abc = (aez * bc) - (bez * ac) + (cez * ab);

        var aLift = (aex * aex) + (aey * aey) + (aez * aez);
        var bLift = (bex * bex) + (bey * bey) + (bez * bez);
        var cLift = (cex * cex) + (cey * cey) + (cez * cez);
        var dLift = (dex * dex) + (dey * dey) + (dez * dez);

        var determinant = (dLift * abc) - (cLift * abd) + (bLift * acd) - (aLift * bcd);

        return determinant.Sign;
    }

    /// <summary>
    ///     Rewrites a handful of floating-point values as integers sharing one power-of-two scale.
    /// </summary>
    /// <remarks>
    ///     Every binary floating-point number is an integer times a power of two, so this loses
    ///     nothing: it factors out the smallest exponent present and hands back the integers that
    ///     remain. The determinants above are homogeneous, so the common factor scales the result
    ///     by a positive number and leaves its sign — the only thing anybody asked for — alone.
    /// </remarks>
    static void ToCommonScale(ReadOnlySpan<double> values, Span<BigInteger> result) {
        Span<long> mantissas = stackalloc long[values.Length];
        Span<int> exponents = stackalloc int[values.Length];

        var smallest = int.MaxValue;

        for (var i = 0; i < values.Length; i++) {
            (mantissas[i], exponents[i]) = Decompose(values[i]);

            if (mantissas[i] != 0 && exponents[i] < smallest) {
                smallest = exponents[i];
            }
        }

        if (smallest == int.MaxValue) {
            smallest = 0;
        }

        for (var i = 0; i < values.Length; i++) {
            result[i] = mantissas[i] == 0
                ? BigInteger.Zero
                : new BigInteger(mantissas[i]) << (exponents[i] - smallest);
        }
    }

    /// <summary>Splits a double into the exact <c>mantissa · 2^exponent</c> it already is.</summary>
    static (long Mantissa, int Exponent) Decompose(double value) {
        var bits = BitConverter.DoubleToInt64Bits(value);
        var negative = bits < 0;
        var exponent = (int)((bits >> 52) & 0x7FF);
        var mantissa = bits & 0x000F_FFFF_FFFF_FFFF;

        if (exponent == 0) {
            // Subnormal: no implicit leading one, and the exponent field's zero stands for the
            // same scale as a one.
            exponent = 1;
        } else {
            mantissa |= 0x0010_0000_0000_0000;
        }

        return (negative ? -mantissa : mantissa, exponent - 1075);
    }
}
