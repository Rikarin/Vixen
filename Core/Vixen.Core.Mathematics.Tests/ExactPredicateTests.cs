// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using CsCheck;
using Xunit;
using BigInteger = System.Numerics.BigInteger;

namespace Vixen.Core.Mathematics.Tests;

/// <summary>
///     The orientation and in-sphere predicates, against a reference that cannot be wrong.
/// </summary>
/// <remarks>
///     <para>
///         Every generated point here has coordinates that are integers over 256, so multiplying
///         by 256 makes them exact integers and the reference determinant below is plain integer
///         arithmetic in <see cref="BigInteger" /> — no rounding anywhere, and no shared code with
///         the thing under test. That is the only kind of reference worth having for a predicate:
///         comparing a filtered evaluation against another floating-point evaluation compares two
///         guesses.
///     </para>
///     <para>
///         The coarse lattice is also what makes the tests interesting rather than merely broad.
///         Random doubles are in general position with probability one, so they exercise the
///         filter and never the exact path; points on a lattice are coplanar and cospherical all
///         the time, which is both where the arithmetic is hard and where the light probes people
///         author actually sit.
///     </para>
/// </remarks>
public class ExactPredicateTests {
    static readonly Gen<Vector3> Lattice =
        Gen.Select(
            Gen.Int[-8, 8],
            Gen.Int[-8, 8],
            Gen.Int[-8, 8],
            static (x, y, z) => new Vector3(x / 4f, y / 4f, z / 4f)
        );

    // --- Orientation --------------------------------------------------------

    /// <summary>
    ///     The sign convention, stated once so that everything downstream can rely on it.
    /// </summary>
    /// <remarks>
    ///     <c>d</c> on the side the triangle's normal points at is <em>negative</em>. That reads
    ///     backwards until you write the determinant out: the value is
    ///     <c>det[a − d; b − d; c − d]</c>, which subtracts <c>d</c> rather than measuring from it.
    ///     Every face table in <see cref="DelaunayTetrahedralization" /> is wound to this.
    /// </remarks>
    [Fact]
    public void The_orientation_sign_is_negative_on_the_normals_side() {
        var a = new Vector3(0f, 0f, 0f);
        var b = new Vector3(1f, 0f, 0f);
        var c = new Vector3(0f, 1f, 0f);

        // (b - a) x (c - a) is +Z here.
        Assert.Equal(-1, ExactPredicates.Orient3D(a, b, c, new(0f, 0f, 1f)));
        Assert.Equal(1, ExactPredicates.Orient3D(a, b, c, new(0f, 0f, -1f)));
        Assert.Equal(0, ExactPredicates.Orient3D(a, b, c, new(3f, -7f, 0f)));
    }

    /// <summary>Swapping two arguments flips the sign; an even permutation does not.</summary>
    /// <remarks>
    ///     The property the whole mesh rests on. A tetrahedron's four faces are tabulated as even
    ///     permutations of its vertices precisely so that one orientation test answers for all of
    ///     them, and if antisymmetry ever failed, that tabulation would be four independent
    ///     conventions instead of one.
    /// </remarks>
    [Fact]
    public void Orientation_is_antisymmetric() {
        Gen.Select(Lattice, Lattice, Lattice, Lattice)
            .Sample(
                static points => {
                    var (a, b, c, d) = points;
                    var reference = ExactPredicates.Orient3D(a, b, c, d);

                    Assert.Equal(-reference, ExactPredicates.Orient3D(b, a, c, d));
                    Assert.Equal(-reference, ExactPredicates.Orient3D(a, c, b, d));
                    Assert.Equal(-reference, ExactPredicates.Orient3D(a, b, d, c));

                    // Two swaps: back where it started.
                    Assert.Equal(reference, ExactPredicates.Orient3D(b, a, d, c));
                    Assert.Equal(reference, ExactPredicates.Orient3D(c, d, a, b));
                }
            );
    }

    /// <summary>The predicate agrees with exact integer arithmetic, on every generated case.</summary>
    [Fact]
    public void Orientation_matches_exact_integer_arithmetic() {
        Gen.Select(Lattice, Lattice, Lattice, Lattice)
            .Sample(
                static points => {
                    var (a, b, c, d) = points;

                    Assert.Equal(ReferenceOrient3D(a, b, c, d), ExactPredicates.Orient3D(a, b, c, d));
                }
            );
    }

    /// <summary>
    ///     Coplanarity is reported as coplanarity, on a plane chosen so that nothing cancels for
    ///     free.
    /// </summary>
    /// <remarks>
    ///     The points lie on <c>z = x + y</c> at coordinates that need every mantissa bit they
    ///     have. This is the answer that has to be exactly zero rather than nearly zero: a cavity
    ///     boundary that is coplanar with the point being inserted is what produces a
    ///     zero-volume cell, and a "nearly coplanar" verdict is how it gets built.
    /// </remarks>
    [Fact]
    public void Coplanar_points_are_reported_coplanar() {
        Gen.Select(Gen.Int[-64, 64], Gen.Int[-64, 64], Gen.Int[-64, 64], Gen.Int[-64, 64])
            .Sample(
                static offsets => {
                    var (x0, y0, x1, y1) = offsets;

                    var a = OnPlane(x0 / 128f, y0 / 128f);
                    var b = OnPlane(x1 / 128f, y1 / 128f);
                    var c = OnPlane((x0 + x1) / 128f, (y0 - y1) / 128f);
                    var d = OnPlane((x0 - x1) / 128f, (y0 + y1) / 128f);

                    Assert.Equal(0, ExactPredicates.Orient3D(a, b, c, d));

                    static Vector3 OnPlane(float x, float y) => new(x, y, x + y);
                }
            );
    }

    // --- In-sphere ----------------------------------------------------------

    /// <summary>Inside is positive, outside is negative, on the sphere is zero.</summary>
    [Fact]
    public void The_in_sphere_sign_says_inside_outside_and_on() {
        // A positively oriented tetrahedron on three axes; its circumsphere is centred at
        // (0.5, 0.5, 0.5) with radius squared 0.75.
        var a = new Vector3(0f, 0f, 0f);
        var b = new Vector3(0f, 1f, 0f);
        var c = new Vector3(1f, 0f, 0f);
        var d = new Vector3(0f, 0f, 1f);

        Assert.Equal(1, ExactPredicates.Orient3D(a, b, c, d));

        Assert.Equal(1, ExactPredicates.InSphere(a, b, c, d, new(0.25f, 0.25f, 0.25f)));
        Assert.Equal(-1, ExactPredicates.InSphere(a, b, c, d, new(5f, 5f, 5f)));
        Assert.Equal(0, ExactPredicates.InSphere(a, b, c, d, new(1f, 1f, 1f)));
    }

    /// <summary>
    ///     The eight corners of a cube are cospherical, and the predicate says so rather than
    ///     picking a side.
    /// </summary>
    /// <remarks>
    ///     This is the input that defeated the first attempt at this construction. A grid of light
    ///     probes is exactly this, repeated: no tolerance decides it, because there is nothing to
    ///     decide — the answer is zero, and what a mesh builder needs is a rule for what to do
    ///     with zero.
    /// </remarks>
    [Fact]
    public void A_cubes_corners_are_cospherical() {
        var corners = CubeCorners(1f);

        Assert.Equal(1, ExactPredicates.Orient3D(corners[0], corners[2], corners[1], corners[4]));

        foreach (var corner in (int[])[3, 5, 6, 7]) {
            Assert.Equal(
                0,
                ExactPredicates.InSphere(corners[0], corners[2], corners[1], corners[4], corners[corner])
            );
        }
    }

    /// <summary>An even permutation of the first four is the same tetrahedron and the same answer.</summary>
    [Fact]
    public void The_in_sphere_test_only_depends_on_the_oriented_tetrahedron() {
        Gen.Select(Lattice, Lattice, Lattice, Lattice, Lattice)
            .Sample(
                static points => {
                    var (a, b, c, d, e) = points;

                    if (ExactPredicates.Orient3D(a, b, c, d) <= 0) {
                        return;
                    }

                    var reference = ExactPredicates.InSphere(a, b, c, d, e);

                    Assert.Equal(reference, ExactPredicates.InSphere(b, a, d, c, e));
                    Assert.Equal(reference, ExactPredicates.InSphere(c, d, a, b, e));
                    Assert.Equal(reference, ExactPredicates.InSphere(d, c, b, a, e));

                    // An odd permutation is the mirrored tetrahedron, so the answer inverts.
                    Assert.Equal(-reference, ExactPredicates.InSphere(b, a, c, d, e));
                }
            );
    }

    /// <summary>The predicate agrees with exact integer arithmetic, on every generated case.</summary>
    [Fact]
    public void The_in_sphere_test_matches_exact_integer_arithmetic() {
        Gen.Select(Lattice, Lattice, Lattice, Lattice, Lattice)
            .Sample(
                static points => {
                    var (a, b, c, d, e) = points;

                    Assert.Equal(
                        ReferenceInSphere(a, b, c, d, e),
                        ExactPredicates.InSphere(a, b, c, d, e)
                    );
                }
            );
    }

    // --- Simulation of simplicity -------------------------------------------

    /// <summary>The perturbed test never returns zero, and never disagrees with a decided one.</summary>
    /// <remarks>
    ///     Both halves matter. Never zero is what makes it usable as a decision; agreeing wherever
    ///     the unperturbed test already had an answer is what makes it a tie-break rather than a
    ///     different predicate.
    /// </remarks>
    [Fact]
    public void The_perturbed_test_decides_without_changing_decided_answers() {
        Gen.Select(Lattice, Lattice, Lattice, Lattice, Lattice)
            .Sample(
                static points => {
                    var (a, b, c, d, e) = points;

                    if (ExactPredicates.Orient3D(a, b, c, d) <= 0) {
                        return;
                    }

                    var exact = ExactPredicates.InSphere(a, b, c, d, e);
                    var perturbed = ExactPredicates.InSphere(a, b, c, d, e, 0, 1, 2, 3, 4);

                    Assert.NotEqual(0, perturbed);

                    if (exact != 0) {
                        Assert.Equal(exact, perturbed);
                    }
                }
            );
    }

    /// <summary>
    ///     The tie-break depends on the indices, not on the order the points were handed over.
    /// </summary>
    /// <remarks>
    ///     The consistency the mesh builder needs. The same five points reached from two different
    ///     cells must produce the same verdict, or the cavity is not a cavity — so the index
    ///     travels with the point rather than with the argument position.
    /// </remarks>
    [Fact]
    public void The_tie_break_travels_with_the_index_rather_than_the_argument() {
        Gen.Select(Lattice, Lattice, Lattice, Lattice, Lattice)
            .Sample(
                static points => {
                    var (a, b, c, d, e) = points;

                    if (ExactPredicates.Orient3D(a, b, c, d) <= 0) {
                        return;
                    }

                    var reference = ExactPredicates.InSphere(a, b, c, d, e, 7, 2, 9, 4, 1);

                    // Even permutations of the tetrahedron, with each index following its point.
                    Assert.Equal(reference, ExactPredicates.InSphere(b, a, d, c, e, 2, 7, 4, 9, 1));
                    Assert.Equal(reference, ExactPredicates.InSphere(c, d, a, b, e, 9, 4, 7, 2, 1));
                    Assert.Equal(reference, ExactPredicates.InSphere(d, c, b, a, e, 4, 9, 2, 7, 1));
                }
            );
    }

    /// <summary>
    ///     A cube's eight corners get a decision, and the decision is a consistent one.
    /// </summary>
    /// <remarks>
    ///     Consistent meaning: whichever way the tie falls, the four corners on the far side of
    ///     the sphere fall the same way, because the perturbation is a property of the point set
    ///     rather than of the question.
    /// </remarks>
    [Fact]
    public void A_cospherical_grid_gets_a_stable_decision() {
        var corners = CubeCorners(1f);

        var first = ExactPredicates.InSphere(
            corners[0],
            corners[2],
            corners[1],
            corners[4],
            corners[7],
            0,
            2,
            1,
            4,
            7
        );

        Assert.NotEqual(0, first);

        // Asking again, with the tetrahedron given by an even permutation, is the same question.
        var again = ExactPredicates.InSphere(
            corners[2],
            corners[0],
            corners[4],
            corners[1],
            corners[7],
            2,
            0,
            4,
            1,
            7
        );

        Assert.Equal(first, again);
    }

    // --- The reference ------------------------------------------------------

    static Vector3[] CubeCorners(float size) => [
        new(0f, 0f, 0f),
        new(size, 0f, 0f),
        new(0f, size, 0f),
        new(size, size, 0f),
        new(0f, 0f, size),
        new(size, 0f, size),
        new(0f, size, size),
        new(size, size, size)
    ];

    /// <summary>256 times a coordinate, which for every value the generators make is an integer.</summary>
    static BigInteger Exact(float value) {
        var scaled = value * 256f;
        Assert.Equal(scaled, MathF.Round(scaled));

        return new((long)scaled);
    }

    static int ReferenceOrient3D(Vector3 a, Vector3 b, Vector3 c, Vector3 d) {
        BigInteger adx = Exact(a.X) - Exact(d.X), ady = Exact(a.Y) - Exact(d.Y), adz = Exact(a.Z) - Exact(d.Z);
        BigInteger bdx = Exact(b.X) - Exact(d.X), bdy = Exact(b.Y) - Exact(d.Y), bdz = Exact(b.Z) - Exact(d.Z);
        BigInteger cdx = Exact(c.X) - Exact(d.X), cdy = Exact(c.Y) - Exact(d.Y), cdz = Exact(c.Z) - Exact(d.Z);

        var determinant = (adx * ((bdy * cdz) - (bdz * cdy)))
            - (ady * ((bdx * cdz) - (bdz * cdx)))
            + (adz * ((bdx * cdy) - (bdy * cdx)));

        return determinant.Sign;
    }

    /// <summary>
    ///     The lifted four-by-four determinant, expanded along its last column by the book.
    /// </summary>
    /// <remarks>
    ///     Written the long way on purpose: the implementation shares two-by-two minors between
    ///     its four cofactors, and a reference that shared them too would agree with it about a
    ///     shared mistake.
    /// </remarks>
    static int ReferenceInSphere(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 e) {
        Span<BigInteger> rows = new BigInteger[16];

        Fill(rows[..4], a, e);
        Fill(rows[4..8], b, e);
        Fill(rows[8..12], c, e);
        Fill(rows[12..], d, e);

        var determinant = BigInteger.Zero;

        for (var column = 0; column < 4; column++) {
            var minor = Minor(rows, 0, column);
            var term = rows[column] * minor;
            determinant += (column & 1) == 0 ? term : -term;
        }

        return determinant.Sign;

        static void Fill(Span<BigInteger> row, Vector3 point, Vector3 origin) {
            row[0] = Exact(point.X) - Exact(origin.X);
            row[1] = Exact(point.Y) - Exact(origin.Y);
            row[2] = Exact(point.Z) - Exact(origin.Z);
            row[3] = (row[0] * row[0]) + (row[1] * row[1]) + (row[2] * row[2]);
        }
    }

    /// <summary>The three-by-three determinant left after striking one row and one column.</summary>
    static BigInteger Minor(ReadOnlySpan<BigInteger> rows, int skipRow, int skipColumn) {
        Span<BigInteger> values = new BigInteger[9];
        var next = 0;

        for (var row = 0; row < 4; row++) {
            if (row == skipRow) {
                continue;
            }

            for (var column = 0; column < 4; column++) {
                if (column == skipColumn) {
                    continue;
                }

                values[next++] = rows[(row * 4) + column];
            }
        }

        return (values[0] * ((values[4] * values[8]) - (values[5] * values[7])))
            - (values[1] * ((values[3] * values[8]) - (values[5] * values[6])))
            + (values[2] * ((values[3] * values[7]) - (values[4] * values[6])));
    }
}
