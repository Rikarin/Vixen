// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using CsCheck;
using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Geometry.Tests;

/// <summary>Doc 24's Part 4: statements about every mesh rather than about the ones somebody typed.</summary>
/// <remarks>
///     <para>
///         <b>CsCheck, as <c>Vixen.Core.Mathematics</c> already does.</b> "Welding never merges two
///         positions a primitive meant to be apart" is a claim about every primitive at every size,
///         and a handful of hand-picked ones is a claim about those.
///     </para>
///     <para>
///         ⚠ <b>The invariant helper is what each of these ends with.</b> A property test that only
///         checked its own property would pass on a mesh whose edge table had quietly gone wrong,
///         which is the failure doc 24 says is impossible to attribute three operations later.
///     </para>
/// </remarks>
public class MeshPropertyTests {
    /// <summary>A box of a random size and position, as a triangle soup a renderer would produce.</summary>
    static (Vector3[] Positions, int[] Indices) Box(Vector3 centre, Vector3 extent) {
        List<Vector3> positions = [];
        List<int> indices = [];

        Face(Vector3.UnitX, -Vector3.UnitZ, Vector3.UnitY);
        Face(-Vector3.UnitX, Vector3.UnitZ, Vector3.UnitY);
        Face(Vector3.UnitY, Vector3.UnitX, -Vector3.UnitZ);
        Face(-Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ);
        Face(Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY);
        Face(-Vector3.UnitZ, -Vector3.UnitX, Vector3.UnitY);

        return ([.. positions], [.. indices]);

        void Face(Vector3 normal, Vector3 right, Vector3 up) {
            var origin = centre + (normal * Vector3.Dot(extent, Abs(normal)));
            var across = right * Vector3.Dot(extent, Abs(right));
            var along = up * Vector3.Dot(extent, Abs(up));

            var start = positions.Count;

            positions.Add(origin - across - along);
            positions.Add(origin + across - along);
            positions.Add(origin + across + along);
            positions.Add(origin - across + along);

            indices.AddRange([start, start + 1, start + 2, start, start + 2, start + 3]);
        }

        static Vector3 Abs(Vector3 value) => new(MathF.Abs(value.X), MathF.Abs(value.Y), MathF.Abs(value.Z));
    }

    static Gen<Vector3> Point(double low, double high) =>
        Gen.Select(Gen.Float[(float) low, (float) high], Gen.Float[(float) low, (float) high],
            Gen.Float[(float) low, (float) high], (x, y, z) => new Vector3(x, y, z));

    [Fact]
    public void A_box_at_any_size_and_anywhere_comes_out_solid() {
        Gen.Select(Point(-500d, 500d), Point(0.01d, 50d))
            .Sample(
                pair => {
                    var (centre, extent) = pair;
                    var (positions, indices) = Box(centre, extent);

                    var mesh = EditMesh.FromTriangles(positions, indices);
                    var report = mesh.Validate();

                    // ⚠ At five orders of magnitude of size, which is what the *relative* weld
                    // tolerance is for. An absolute one would leave a millimetre box welded into a
                    // point and a kilometre box with its seams open, and both would be found by a
                    // designer rather than here.
                    Assert.True(report.IsSolid, $"{centre} {extent}: {report.Describe()}");
                    Assert.Equal(8, mesh.PositionCount);
                    Assert.Equal(6, Groups(mesh));
                },
                iter: 200
            );
    }

    [Fact]
    public void Triangulating_and_rebuilding_gives_back_the_same_mesh() {
        Gen.Select(Point(-100d, 100d), Point(0.05d, 20d))
            .Sample(
                pair => {
                    var (centre, extent) = pair;
                    var (positions, indices) = Box(centre, extent);

                    var mesh = EditMesh.FromTriangles(positions, indices);

                    // ⚠ The round trip that matters for P1: what goes to a renderer and comes back has
                    // to be the same geometry, because "make this editable" and "draw this" are the
                    // two directions the same mesh travels every frame of every session.
                    var again = EditMesh.FromTriangles(mesh.Positions, mesh.Triangulate());

                    Assert.Equal(mesh.PositionCount, again.PositionCount);
                    Assert.Equal(mesh.FaceCount, again.FaceCount);
                    Assert.Equal(mesh.Edges.Count, again.Edges.Count);
                    Assert.True(again.Validate().IsSolid, again.Validate().Describe() ?? "solid");
                },
                iter: 200
            );
    }

    [Fact]
    public void Moving_every_position_by_an_offset_and_back_is_the_identity() {
        Gen.Select(Point(-100d, 100d), Point(0.05d, 20d), Point(-50d, 50d))
            .Sample(
                triple => {
                    var (centre, extent, offset) = triple;
                    var (positions, indices) = Box(centre, extent);

                    var mesh = EditMesh.FromTriangles(positions, indices);
                    var was = mesh.Positions.ToArray();

                    for (var index = 0; index < mesh.PositionCount; index++) {
                        mesh.MovePosition(index, mesh.Positions[index] + offset);
                    }

                    for (var index = 0; index < mesh.PositionCount; index++) {
                        mesh.MovePosition(index, mesh.Positions[index] - offset);
                    }

                    // Not exactly equal — adding and subtracting a float is not the identity — but
                    // within what the numbers can carry, and the *structure* is untouched, which is
                    // the half a position move must never disturb.
                    for (var index = 0; index < was.Length; index++) {
                        Assert.True(
                            Vector3.NearEqual(mesh.Positions[index], was[index], MathF.Max(extent.Length(), 1f) * 1e-4f),
                            $"{mesh.Positions[index]} against {was[index]}"
                        );
                    }

                    Assert.True(mesh.Validate().IsSolid, mesh.Validate().Describe() ?? "solid");
                },
                iter: 100
            );
    }

    [Fact]
    public void A_copy_is_equal_to_its_original_and_independent_of_it() {
        Gen.Select(Point(-100d, 100d), Point(0.05d, 20d))
            .Sample(
                pair => {
                    var (centre, extent) = pair;
                    var (positions, indices) = Box(centre, extent);

                    var mesh = EditMesh.FromTriangles(positions, indices);
                    var copy = new EditMesh(mesh);

                    Assert.Equal(mesh.Positions.ToArray(), copy.Positions.ToArray());
                    Assert.Equal(mesh.Corners.ToArray(), copy.Corners.ToArray());
                    Assert.Equal(mesh.Faces, copy.Faces);

                    mesh.MovePosition(0, mesh.Positions[0] + Vector3.One);

                    Assert.NotEqual(mesh.Positions[0], copy.Positions[0]);
                },
                iter: 100
            );
    }

    static int Groups(EditMesh mesh) {
        var seen = new HashSet<int>();

        foreach (var face in mesh.Faces) {
            seen.Add(face.Group);
        }

        return seen.Count;
    }
}
