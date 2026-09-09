// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Geometry.Tests;

/// <summary>
///     doc 24 § P3's knife — "the one row of the table left undone" — as the kernel primitive that
///     section names.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The oracles here are counts and volumes rather than pictures.</b> A cut face is two
///         faces and nothing else moved, so the face count goes up by exactly one per cut and the
///         solid's volume does not change at all — the second being what catches a half assembled with
///         the wrong winding, which a face count cannot see.
///     </para>
///     <para>
///         ⚠ <b>Every test ends in the invariant helpers, for <c>MeshOperationTests</c>' reason.</b> A
///         cut leaves T-junctions by construction, and a T-junction is geometry that is <i>correct</i>
///         — the vertex is exactly on the line — so nothing but a structural check can see one. It is
///         the failure this verb was most likely to ship with.
///     </para>
/// </remarks>
public class MeshKnifeTests {
    /// <summary>A cut between two edge midpoints makes two faces out of one.</summary>
    /// <remarks>
    ///     ⚠ <b>The face count is n + 1 and it is exact.</b> "More faces than before" is true of a
    ///     cut that split the wrong face, of one that split two, and of one that made a sliver — and
    ///     the whole point of a knife is that it does precisely what was asked.
    /// </remarks>
    [Fact]
    public void A_cut_between_two_midpoints_makes_two_faces_of_one() {
        var mesh = MeshShapes.Create(ShapeKind.Box);
        var before = mesh.FaceCount;
        var volume = Volume(mesh);
        var (from, to) = Across(mesh, 0);

        var made = MeshOperations.Knife(mesh, [new(0, from, to)]);

        Assert.Equal(2, made.Count);
        Assert.Equal(before + 1, mesh.FaceCount);

        // Nothing moved: a cut adds corners and removes no space.
        Assert.Equal(volume, Volume(mesh), 4);

        AssertSolid(mesh);
    }

    /// <summary>The two halves between them are the face that was there, corner for corner.</summary>
    /// <remarks>
    ///     ⚠ <b>A count says two faces exist; this says they are the right two.</b> A chord drawn to
    ///     the wrong corner also produces two faces of a quad, and so does one that walked the loop
    ///     backwards — the halves would just be different, and one of them inside out.
    /// </remarks>
    [Fact]
    public void The_two_halves_share_the_chord_and_nothing_else() {
        var mesh = MeshShapes.Create(ShapeKind.Box);
        var corners = mesh.CornersOf(0).ToArray();
        var (from, to) = Across(mesh, 0);

        var made = MeshOperations.Knife(mesh, [new(0, from, to)]);

        Assert.Equal(2, made.Count);

        var first = mesh.CornersOf(made[0]).ToArray();
        var second = mesh.CornersOf(made[1]).ToArray();

        // A quad cut between two midpoints is two quads: two original corners and the two new ones.
        Assert.Equal(4, first.Length);
        Assert.Equal(4, second.Length);

        // Exactly the two cut positions are in both halves, and every original corner is in one.
        Assert.Equal(2, first.Intersect(second).Count());

        foreach (var corner in corners) {
            Assert.True(
                first.Contains(corner) ^ second.Contains(corner),
                "an original corner belongs to exactly one half"
            );
        }
    }

    /// <summary>
    ///     A stroke across several faces is one call, because each cut renumbers the table the next
    ///     would index into.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>§ P3 names this as the thing to get right.</b> Cutting face 0, then face 3, is cutting
    ///     whatever face 3 has <i>become</i> — which after a rebuild is a different face, usually one
    ///     the stroke never crossed. The signature takes a list for that reason and this is the
    ///     assertion that it is honoured.
    /// </remarks>
    [Fact]
    public void A_stroke_across_several_faces_cuts_each_of_them() {
        var mesh = MeshShapes.Create(ShapeKind.Box);
        var before = mesh.FaceCount;
        var volume = Volume(mesh);

        var cuts = new List<KnifeCut>();

        for (var face = 0; face < before; face++) {
            var (from, to) = Across(mesh, face);

            cuts.Add(new(face, from, to));
        }

        var made = MeshOperations.Knife(mesh, cuts);

        Assert.Equal(before * 2, made.Count);
        Assert.Equal(before * 2, mesh.FaceCount);
        Assert.Equal(volume, Volume(mesh), 4);

        AssertSolid(mesh);
    }

    /// <summary>
    ///     A cut on one face leaves the neighbour whole, and the stitch is what puts the new corner
    ///     into it.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is the T-junction, and it is the failure a knife ships with.</b> The point is
    ///     exactly on the neighbour's edge, so nothing about the geometry is wrong: the two faces
    ///     simply stop sharing an edge, and the surface draws with a crack that opens and closes as
    ///     the camera moves. <c>Validate</c> reporting a boundary edge on a closed box is what sees it.
    /// </remarks>
    [Fact]
    public void A_cut_leaves_no_T_junction_behind_it() {
        var mesh = MeshShapes.Create(ShapeKind.Box);
        var (from, to) = Across(mesh, 0);

        MeshOperations.Knife(mesh, [new(0, from, to)]);

        var report = mesh.Validate();

        Assert.Empty(report.Boundary);
        Assert.True(report.IsClosed, report.Describe() ?? "closed");

        // And the neighbours grew the corner rather than the box growing a hole: the two faces the
        // cut's midpoints sit on are pentagons now.
        Assert.Contains(mesh.Faces, face => face.Count == 5);
    }

    /// <summary>A cut between two corners of a quad is its diagonal, and adds no position.</summary>
    /// <remarks>
    ///     ⚠ <b>The half that is easy to get wrong by always inserting.</b> A cut landed on a corner
    ///     that inserted a position there anyway makes a zero-length edge, which is geometry every
    ///     later verb has to survive and no assertion about face counts can see.
    /// </remarks>
    [Fact]
    public void A_cut_between_two_corners_adds_no_position() {
        var mesh = MeshShapes.Create(ShapeKind.Box);
        var corners = mesh.CornersOf(0).ToArray();

        Assert.Equal(4, corners.Length);

        var before = mesh.PositionCount;

        var made = MeshOperations.Knife(
            mesh,
            [new(0, mesh.Positions[corners[0]], mesh.Positions[corners[2]])]
        );

        Assert.Equal(2, made.Count);
        Assert.Equal(before, mesh.PositionCount);
        Assert.Equal(3, mesh.CornersOf(made[0]).Length);
        Assert.Equal(3, mesh.CornersOf(made[1]).Length);

        AssertSolid(mesh);
    }

    /// <summary>A chord that separates nothing is refused, and the face is left as it was.</summary>
    /// <remarks>
    ///     ⚠ <b>Three shapes of nothing, and each would make a different broken mesh.</b> Two points
    ///     on one edge is a face with two corners; two adjacent corners is the same; a point off the
    ///     boundary altogether is a chord with one end nowhere. A verb that produced any of them would
    ///     leave a table every later operation walks wrongly.
    /// </remarks>
    [Fact]
    public void A_chord_that_separates_nothing_is_refused() {
        var mesh = MeshShapes.Create(ShapeKind.Box);
        var corners = mesh.CornersOf(0).ToArray();
        var a = mesh.Positions[corners[0]];
        var b = mesh.Positions[corners[1]];
        var before = mesh.FaceCount;

        // Two points on one edge.
        Assert.Empty(MeshOperations.Knife(mesh, [new(0, Vector3.Lerp(a, b, 0.25f), Vector3.Lerp(a, b, 0.75f))]));

        // Two adjacent corners.
        Assert.Empty(MeshOperations.Knife(mesh, [new(0, a, b)]));

        // One corner, twice.
        Assert.Empty(MeshOperations.Knife(mesh, [new(0, a, a)]));

        // A point that is not on the boundary at all: the face's own middle.
        var middle = Middle(mesh, 0);

        Assert.Empty(MeshOperations.Knife(mesh, [new(0, a, middle)]));

        Assert.Equal(before, mesh.FaceCount);
        AssertSolid(mesh);
    }

    /// <summary>A face index nothing owns is skipped rather than throwing.</summary>
    [Fact]
    public void A_cut_naming_no_face_does_nothing() {
        var mesh = MeshShapes.Create(ShapeKind.Box);
        var before = mesh.FaceCount;
        var (from, to) = Across(mesh, 0);

        Assert.Empty(MeshOperations.Knife(mesh, [new(mesh.FaceCount + 3, from, to)]));
        Assert.Equal(before, mesh.FaceCount);
    }

    /// <summary>The halves keep the face's group and its smoothing, and dropping the second is silent.</summary>
    /// <remarks>
    ///     ⚠ <b><c>MeshLoop</c>'s own remarks name this trap:</b> a verb that carried the group and
    ///     dropped the smoothing produces a mesh that is materialled correctly and faceted, which reads
    ///     as a shading bug in the renderer rather than as the cut that caused it.
    /// </remarks>
    [Fact]
    public void The_halves_keep_the_group_and_the_smoothing() {
        var mesh = MeshShapes.Create(ShapeKind.Box);

        mesh.SetGroup(0, 7);
        mesh.SetSmoothing(0, 3);

        var (from, to) = Across(mesh, 0);
        var made = MeshOperations.Knife(mesh, [new(0, from, to)]);

        Assert.Equal(2, made.Count);

        foreach (var face in made) {
            Assert.Equal(7, mesh.Faces[face].Group);
            Assert.Equal(3, mesh.Faces[face].Smoothing);
        }
    }

    // ============================================================ Harness

    /// <summary>The midpoints of a face's first edge and of the edge opposite it.</summary>
    static (Vector3 From, Vector3 To) Across(EditMesh mesh, int face) {
        var loop = mesh.CornersOf(face).ToArray();
        var opposite = loop.Length / 2;

        return (
            Vector3.Lerp(mesh.Positions[loop[0]], mesh.Positions[loop[1]], 0.5f),
            Vector3.Lerp(
                mesh.Positions[loop[opposite]],
                mesh.Positions[loop[(opposite + 1) % loop.Length]],
                0.5f
            )
        );
    }

    static Vector3 Middle(EditMesh mesh, int face) {
        var loop = mesh.CornersOf(face).ToArray();
        var total = Vector3.Zero;

        foreach (var corner in loop) {
            total += mesh.Positions[corner];
        }

        return total / loop.Length;
    }

    static void AssertSound(EditMesh mesh) {
        Assert.All(mesh.Faces, face => Assert.True(face.Count >= 3, "a face wants three corners"));

        foreach (var corner in mesh.Corners) {
            Assert.InRange(corner, 0, mesh.PositionCount - 1);
        }

        for (var edge = 0; edge < mesh.Edges.Count; edge++) {
            Assert.NotEmpty(mesh.FacesOf(edge).ToArray());
            Assert.True(mesh.Edges[edge].A < mesh.Edges[edge].B, "an edge is stored low-to-high");
        }
    }

    static void AssertSolid(EditMesh mesh) {
        AssertSound(mesh);

        var report = mesh.Validate();

        Assert.True(report.IsClosed, report.Describe() ?? "closed");
        Assert.True(report.IsConsistent, report.Describe() ?? "consistent");
        Assert.Empty(report.Degenerate);
    }

    /// <summary>Six times the signed volume, which is zero for a mesh whose winding disagrees.</summary>
    static float Volume(EditMesh mesh) {
        var triangles = mesh.Triangulate();
        var total = 0f;

        for (var index = 0; index + 2 < triangles.Length; index += 3) {
            var a = mesh.Positions[triangles[index]];
            var b = mesh.Positions[triangles[index + 1]];
            var c = mesh.Positions[triangles[index + 2]];

            total += Vector3.Dot(a, Vector3.Cross(b, c));
        }

        return total / 6f;
    }
}
