// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Geometry.Remeshing.Tests;

/// <summary>docs/plan/41 § D11 and its fourth exit criterion, asserted on bits rather than on epsilons.</summary>
/// <remarks>
///     <para>
///         <b>Every assertion here is bit equality, and that is the point of the phase.</b> The
///         criterion reads "output vertex <i>k</i> and its mirror are exact negations, and every vertex
///         on the plane has an exactly zero coordinate" — a test written with a tolerance would pass
///         against ZRemesher's mirrored flow, which is the behaviour § D11 exists to be better than.
///         <see cref="BitConverter.SingleToInt32Bits" /> is what makes the difference visible.
///     </para>
///     <para>
///         ⚠ <b>A sign-bit flip is exact negation for every float including zero, which is why the
///         correspondence is looked for that way rather than with <c>-value</c>.</b> The two agree for
///         every finite float; writing it as the bit operation is what states that the test would fail
///         on a reflection that rounded.
///     </para>
/// </remarks>
public class SymmetryTests {
    /// <summary>Exit criterion 4, in one assertion per half of it.</summary>
    [Theory]
    [InlineData("box")]
    [InlineData("sphere")]
    [InlineData("cylinder")]
    public void Every_vertex_is_on_the_plane_or_has_an_exact_mirror(string name) {
        var quads = Remesher.Remesh(
            RemesherTests.Fixture(name),
            new() { TargetQuads = 200, Symmetry = new Plane(Vector3.UnitX, 0f) },
            out var report
        );

        Assert.True(quads.FaceCount > 0, $"{name} produced nothing: {string.Join(" · ", report.Warnings)}");

        var bits = new HashSet<(int X, int Y, int Z)>();

        foreach (var position in quads.Positions) {
            bits.Add(Bits(position));
        }

        var seam = 0;

        foreach (var position in quads.Positions) {
            if (BitConverter.SingleToInt32Bits(position.X) == 0) {
                seam++;

                continue;
            }

            // Not "within a tolerance of the plane": a vertex the pass decided was on the plane was
            // stored as a literal 0f, so anything else is a vertex off it and must have a partner.
            Assert.NotEqual(0f, position.X);

            var (x, y, z) = Bits(position);

            Assert.True(
                bits.Contains((x ^ int.MinValue, y, z)),
                $"{name}: the vertex at {position} has no bit-exact mirror in the output."
            );
        }

        Assert.True(seam > 0, $"{name}: nothing landed on the plane, so the halves were never joined.");
    }

    /// <summary>The seam is one vertex referenced from both sides rather than two that agree.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the crack § D11 names.</b> A tolerance weld leaves two coincident vertices on
    ///     the plane; the mesh looks right, renders right, and splits open the first time it is
    ///     subdivided. Counting positions whose X is exactly zero and requiring each to be unique is
    ///     what says the halves share their seam instead of meeting at it.
    /// </remarks>
    [Fact]
    public void The_seam_is_shared_rather_than_welded() {
        var quads = Remesher.Remesh(
            RemesherTests.Fixture("box"),
            new() { TargetQuads = 200, Symmetry = new Plane(Vector3.UnitX, 0f) },
            out _
        );

        var seam = new List<Vector3>();

        foreach (var position in quads.Positions) {
            if (position.X == 0f) {
                seam.Add(position);
            }
        }

        Assert.NotEmpty(seam);
        Assert.Equal(seam.Count, seam.Distinct().Count());
    }

    /// <summary>The setting is honoured: turning it on changes the mesh that comes out.</summary>
    /// <remarks>
    ///     <b>The failure class this phase went looking for.</b> <c>Symmetry</c> was declared on
    ///     <see cref="RemeshSettings" /> from R1 and read only to produce a warning until now, which is
    ///     a setting that passes every test that does not compare two outputs.
    /// </remarks>
    [Fact]
    public void Turning_symmetry_on_changes_the_output() {
        var plain = Remesher.Remesh(RemesherTests.Fixture("cylinder"), new() { TargetQuads = 200 }, out _);

        var mirrored = Remesher.Remesh(
            RemesherTests.Fixture("cylinder"),
            new() { TargetQuads = 200, Symmetry = new Plane(Vector3.UnitX, 0f) },
            out var report
        );

        Assert.NotEqual(plain.PositionCount, mirrored.PositionCount);
        Assert.DoesNotContain(report.Warnings, warning => warning.Contains("not applied yet", StringComparison.Ordinal));
    }

    /// <summary>A plane that is not an axis through the origin says so instead of claiming exactness.</summary>
    [Fact]
    public void An_oblique_plane_is_reported_as_not_bit_exact() {
        Remesher.Remesh(
            RemesherTests.Fixture("box"),
            new() {
                TargetQuads = 200,
                Symmetry = Plane.Normalize(new(new Vector3(1f, 0.35f, 0f), 0f))
            },
            out var report
        );

        Assert.Contains(report.Warnings, warning => warning.Contains("bit-for-bit", StringComparison.Ordinal));
    }

    /// <summary>Still all quads, because § D8 does not get a symmetry exemption.</summary>
    [Fact]
    public void A_mirrored_result_is_still_all_quads() {
        var quads = Remesher.Remesh(
            RemesherTests.Fixture("sphere"),
            new() { TargetQuads = 300, Symmetry = new Plane(Vector3.UnitX, 0f) },
            out var report
        );

        Assert.Equal(0, report.NonQuadCount);
        Assert.Equal(report.QuadCount, quads.FaceCount);

        for (var face = 0; face < quads.FaceCount; face++) {
            Assert.Equal(4, quads.Faces[face].Count);
        }
    }

    static (int X, int Y, int Z) Bits(Vector3 position) =>
        (
            BitConverter.SingleToInt32Bits(position.X),
            BitConverter.SingleToInt32Bits(position.Y),
            BitConverter.SingleToInt32Bits(position.Z)
        );
}
