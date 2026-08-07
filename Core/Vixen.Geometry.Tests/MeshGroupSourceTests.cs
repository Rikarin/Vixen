// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Geometry.Tests;

/// <summary>What a face group means, which is a different question from what its id is.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The defect these exist for reads a coplanarity guess as a material boundary.</b>
///         docs/plan/41 § D4 makes a group boundary a hard feature and docs/plan/42 § D3 makes it
///         partition the charts first and unconditionally, both on the argument that a group boundary is
///         somewhere the texture already changes. <see cref="EditMesh.Regroup" /> makes no such claim: on
///         a faceted surface almost no two adjacent triangles are within half a degree of coplanar, so
///         it produces one group per triangle. Measured on sixteen image-to-3D GLBs, that read as
///         material gave between 13 165 and 24 197 charts on meshes of 13 165 to 25 439 triangles.
///     </para>
///     <para>
///         <b>These are the kernel half.</b> That the two readings then diverge downstream is
///         <c>UvFacetedGroupTests</c> and <c>FacetedSurfaceTests</c>.
///     </para>
/// </remarks>
public class MeshGroupSourceTests {
    /// <summary>A faceted surface groups per triangle, and says the groups are a guess.</summary>
    /// <remarks>
    ///     ⚠ <b>The count is the defect and not a detail of the fixture.</b> A roughened dome of 512
    ///     triangles comes out with 389 groups because hardly any of its neighbours are coplanar, which
    ///     is the correct answer to the question <see cref="EditMesh.Regroup" /> is asking and the wrong
    ///     answer to the question the two consumers were asking it.
    /// </remarks>
    [Fact]
    public void A_faceted_surface_groups_per_triangle_and_says_the_groups_are_a_guess() {
        var mesh = Faceted(16, 16);

        Assert.Equal(MeshGroupSource.Coplanarity, mesh.GroupSource);
        Assert.True(mesh.FaceCount > 400, $"The fixture is meant to be big enough to matter: {mesh.FaceCount}.");

        // ⚠ Most of them, which is the shape of the defect: on the corpus this was found on it was
        // 24 197 groups over 25 439 triangles, and what matters is that the number is of the order of
        // the triangle count rather than of the order of the number of surfaces a person would name.
        Assert.True(
            Groups(mesh) * 3 >= mesh.FaceCount * 2,
            $"{Groups(mesh)} groups over {mesh.FaceCount} faces is not the per-triangle grouping this is about."
        );
    }

    /// <summary>A flat surface groups into one, and still says the groups are a guess.</summary>
    /// <remarks>
    ///     The reading does not depend on how many groups came out — a coplanar grid gives one group and
    ///     it is still not a statement about materials.
    /// </remarks>
    [Fact]
    public void A_flat_surface_groups_into_one_and_still_says_the_groups_are_a_guess() {
        var mesh = Flat(8, 8);

        Assert.Equal(MeshGroupSource.Coplanarity, mesh.GroupSource);
        Assert.Equal(1, Groups(mesh));
    }

    /// <summary>Putting a face in a group is the assignment the two consumers are looking for.</summary>
    [Fact]
    public void Putting_a_face_in_a_group_makes_the_groups_an_assignment() {
        var mesh = Flat(8, 8);

        mesh.SetGroup(0, 4);

        Assert.Equal(MeshGroupSource.Assigned, mesh.GroupSource);
    }

    /// <summary>Regrouping throws the assignment away and says so.</summary>
    /// <remarks>
    ///     ⚠ <b>The direction that has to be true, or the flag becomes a lie that outlives its
    ///     mesh.</b> An operation that regroups has recomputed the ids from coplanarity, so whatever
    ///     they meant before is gone whether or not the numbers happen to match.
    /// </remarks>
    [Fact]
    public void Regrouping_throws_the_assignment_away() {
        var mesh = Flat(8, 8);

        mesh.SetGroup(0, 4);
        mesh.Regroup();

        Assert.Equal(MeshGroupSource.Coplanarity, mesh.GroupSource);
    }

    /// <summary>A shape's six named groups are an assignment, because that is what they are.</summary>
    /// <remarks>
    ///     ⚠ <b>This is what keeps a cylinder's rim a crease.</b> <see cref="MeshShapes" /> numbers every
    ///     face it makes with the top, bottom and side groups the block-out tools select on, so a shape
    ///     that came out of it carries a real assignment and the remesh's § D4 feature source and the
    ///     unwrap's § D3 partition both still read it.
    /// </remarks>
    [Theory]
    [InlineData(ShapeKind.Box)]
    [InlineData(ShapeKind.Cylinder)]
    [InlineData(ShapeKind.Sphere)]
    [InlineData(ShapeKind.Stairs)]
    public void A_built_shape_carries_an_assignment(ShapeKind kind) {
        Assert.Equal(MeshGroupSource.Assigned, MeshShapes.Create(kind).GroupSource);
    }

    /// <summary>A boolean of two shapes keeps the assignment, and one of two guesses keeps the guess.</summary>
    [Fact]
    public void A_boolean_carries_the_wider_of_its_operands_readings() {
        var shapes = MeshBoolean.Apply(
            MeshShapes.Create(ShapeKind.Box),
            MeshShapes.Create(ShapeKind.Box),
            BooleanOperation.Union,
            Matrix4x4.FromTranslation(new(0.6f, 0.4f, 0.3f))
        );

        Assert.NotNull(shapes);
        Assert.Equal(MeshGroupSource.Assigned, shapes.GroupSource);
    }

    /// <summary>How many distinct groups a mesh's faces are in.</summary>
    static int Groups(EditMesh mesh) {
        var seen = new HashSet<int>();

        for (var face = 0; face < mesh.FaceCount; face++) {
            seen.Add(mesh.Faces[face].Group);
        }

        return seen.Count;
    }

    /// <summary>A dome as a triangle soup: curved and roughened, so no two neighbours are coplanar.</summary>
    /// <remarks>
    ///     ⚠ <b>The radial roughness is the fixture, not decoration.</b> A perfectly regular lat-long
    ///     sphere still pairs the two triangles of each quad into one group, which halves the count and
    ///     hides the shape of the defect; the surfaces this was found on came out of an image-to-3D
    ///     model and are faceted at the scale of a single triangle. The offset is a fixed hash of the
    ///     two grid indices rather than a random number, because a fixture that differs between runs is
    ///     a test that reports a different thing every time.
    /// </remarks>
    internal static EditMesh Faceted(int around, int up) {
        var positions = new List<Vector3>();
        var indices = new List<int>();

        for (var ring = 0; ring <= up; ring++) {
            var phi = MathF.PI * 0.5f * ring / up;

            for (var step = 0; step < around; step++) {
                var theta = MathF.Tau * step / around;
                var noise = ((ring * 73856093) ^ (step * 19349663) ^ ((ring + step) * 83492791)) & 0xFFFF;
                var rough = 1f + (0.02f * noise / 0xFFFF);

                positions.Add(
                    rough
                    * new Vector3(
                        MathF.Cos(theta) * MathF.Cos(phi),
                        MathF.Sin(phi),
                        MathF.Sin(theta) * MathF.Cos(phi)
                    )
                );
            }
        }

        for (var ring = 0; ring < up; ring++) {
            for (var step = 0; step < around; step++) {
                var next = (step + 1) % around;
                var low = ring * around;
                var high = (ring + 1) * around;

                indices.AddRange([low + step, high + step, high + next]);
                indices.AddRange([low + step, high + next, low + next]);
            }
        }

        return EditMesh.FromTriangles([.. positions], [.. indices]);
    }

    /// <summary>A flat grid as a triangle soup: every triangle coplanar with every other.</summary>
    internal static EditMesh Flat(int across, int along) {
        var positions = new List<Vector3>();
        var indices = new List<int>();

        for (var row = 0; row <= along; row++) {
            for (var column = 0; column <= across; column++) {
                positions.Add(new((float) column / across, 0f, (float) row / along));
            }
        }

        for (var row = 0; row < along; row++) {
            for (var column = 0; column < across; column++) {
                var low = (row * (across + 1)) + column;
                var high = low + across + 1;

                indices.AddRange([low, high, high + 1]);
                indices.AddRange([low, high + 1, low + 1]);
            }
        }

        return EditMesh.FromTriangles([.. positions], [.. indices]);
    }
}
