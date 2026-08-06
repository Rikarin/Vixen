// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Geometry.Tests;

/// <summary>The kernel of doc 24's D2: two graphs, an edge table that reports, and face groups.</summary>
public class EditMeshTests {
    /// <summary>The assertion doc 24's testing table calls the highest-value item.</summary>
    /// <remarks>
    ///     ⚠ <b>Called after every operation, not instead of the operation's own assertion.</b> A mesh
    ///     operation that corrupts the edge table produces geometry that looks correct and fails three
    ///     operations later, in a mesh a designer has spent an hour on, with no way to attribute it.
    ///     One helper, run everywhere, turns that into a failing test in the commit that caused it.
    /// </remarks>
    static void AssertSolid(EditMesh mesh) {
        var report = mesh.Validate();

        Assert.True(report.IsSolid, report.Describe() ?? "solid");

        // And the tables agree with each other, which is what nothing above would notice.
        Assert.Equal(mesh.CornerCount, Total(mesh));
        Assert.All(mesh.Faces, face => Assert.True(face.Count >= 3));

        foreach (var corner in mesh.Corners) {
            Assert.InRange(corner, 0, mesh.PositionCount - 1);
        }

        for (var edge = 0; edge < mesh.Edges.Count; edge++) {
            Assert.NotEmpty(mesh.FacesOf(edge).ToArray());
            Assert.True(mesh.Edges[edge].A < mesh.Edges[edge].B, "an edge should be stored low-to-high");
        }
    }

    static int Total(EditMesh mesh) {
        var count = 0;

        foreach (var face in mesh.Faces) {
            count += face.Count;
        }

        return count;
    }

    /// <summary>A unit cube as a triangle soup, split per corner exactly as a renderer's would be.</summary>
    /// <remarks>
    ///     Twenty-four positions for eight corners, because a drawing vertex carries a normal and a
    ///     texture coordinate and a cube's corner has three of each. That split is the thing the kernel
    ///     has to undo.
    /// </remarks>
    static (Vector3[] Positions, int[] Indices) CubeSoup(float size = 1f) {
        var half = size * 0.5f;

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
            var origin = normal * half;
            var start = positions.Count;

            positions.Add(origin - (right * half) - (up * half));
            positions.Add(origin + (right * half) - (up * half));
            positions.Add(origin + (right * half) + (up * half));
            positions.Add(origin - (right * half) + (up * half));

            indices.AddRange([start, start + 1, start + 2, start, start + 2, start + 3]);
        }
    }

    static EditMesh Cube(float size = 1f) {
        var (positions, indices) = CubeSoup(size);

        return EditMesh.FromTriangles(positions, indices);
    }

    // ── The two graphs ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_cubes_twenty_four_drawing_vertices_are_eight_things_you_can_drag() {
        var (positions, _) = CubeSoup();
        var mesh = Cube();

        // ⚠ D2's whole point. A corner is one *position* and three *corners*; snapping, welding, edge
        // loops and "drag this corner" run on the first, and normals, texture coordinates and
        // materials run on the second. One vertex list either splits smooth shading every time it
        // extrudes or welds texture coordinates every time it merges.
        Assert.Equal(24, positions.Length);
        Assert.Equal(8, mesh.PositionCount);

        Assert.Equal(12, mesh.FaceCount);
        Assert.Equal(36, mesh.CornerCount);

        AssertSolid(mesh);
    }

    [Fact]
    public void A_cube_has_the_eighteen_edges_a_triangulated_cube_has() {
        var mesh = Cube();

        // Twelve edges of the cube plus one diagonal across each of its six sides, because a face here
        // is still a triangle. The groups are what say the two halves are one wall.
        Assert.Equal(18, mesh.Edges.Count);
        Assert.All(Enumerable.Range(0, mesh.Edges.Count), edge => Assert.Equal(2, mesh.FacesOf(edge).Length));
    }

    [Fact]
    public void A_cubes_six_sides_are_six_groups_however_many_triangles_they_are_made_of() {
        var mesh = Cube();
        var groups = new HashSet<int>();

        foreach (var face in mesh.Faces) {
            groups.Add(face.Group);
        }

        // ⚠ Unreal's PolyGroups. A boolean returns triangles, and a face that was one wall before the
        // cut has to still be one wall afterwards or the next extrude acts on a sliver.
        Assert.Equal(6, groups.Count);
        Assert.Equal([0, 1, 2, 3, 4, 5], groups.Order());
    }

    [Fact]
    public void Two_walls_facing_the_same_way_and_touching_nothing_are_two_groups() {
        var mesh = new EditMesh();

        Quad(mesh, Vector3.Zero);
        Quad(mesh, new Vector3(0f, 0f, 5f));

        mesh.Regroup();

        // Connected *and* coplanar. Two parallel walls facing the same way are two groups because no
        // edge joins them, and the two triangles of a cube's side are one because an edge does.
        Assert.Equal(2, mesh.FaceCount);
        Assert.NotEqual(mesh.Faces[0].Group, mesh.Faces[1].Group);

        static void Quad(EditMesh mesh, Vector3 at) {
            var a = mesh.AddPosition(at);
            var b = mesh.AddPosition(at + Vector3.UnitX);
            var c = mesh.AddPosition(at + Vector3.UnitX + Vector3.UnitY);
            var d = mesh.AddPosition(at + Vector3.UnitY);

            mesh.AddFace([a, b, c, d]);
        }
    }

    // ── The edge table ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_T_junction_is_reported_and_not_refused() {
        var mesh = new EditMesh();

        var a = mesh.AddPosition(Vector3.Zero);
        var b = mesh.AddPosition(Vector3.UnitX);

        var up = mesh.AddPosition(Vector3.UnitY);
        var down = mesh.AddPosition(-Vector3.UnitY);
        var out3 = mesh.AddPosition(Vector3.UnitZ);

        mesh.AddFace([a, b, up]);
        mesh.AddFace([a, b, down]);
        mesh.AddFace([a, b, out3]);

        var report = mesh.Validate();

        // ⚠ D2: a half-edge structure cannot represent this, and blockout geometry is non-manifold
        // constantly — a wall meeting a floor in a T, a boolean result, an imported mesh with a stray
        // internal face. A kernel that refuses those refuses the ordinary case.
        Assert.False(report.IsManifold);
        Assert.Single(report.NonManifold);
        Assert.Equal(3, mesh.FacesOf(report.NonManifold[0]).Length);

        Assert.Contains("non-manifold", report.Describe()!, StringComparison.Ordinal);
    }

    [Fact]
    public void An_open_surface_reports_its_rim() {
        var mesh = new EditMesh();

        var a = mesh.AddPosition(Vector3.Zero);
        var b = mesh.AddPosition(Vector3.UnitX);
        var c = mesh.AddPosition(Vector3.UnitY);

        mesh.AddFace([a, b, c]);

        var report = mesh.Validate();

        // A block-out under construction is boundary edges all the way down, so this is a fact rather
        // than a fault — and `IsSolid` is what an operation claiming to preserve a closed surface
        // asserts.
        Assert.True(report.IsManifold);
        Assert.False(report.IsClosed);
        Assert.Equal(3, report.Boundary.Count);
    }

    [Fact]
    public void A_face_wound_the_wrong_way_round_is_reported() {
        var mesh = new EditMesh();

        var a = mesh.AddPosition(Vector3.Zero);
        var b = mesh.AddPosition(Vector3.UnitX);
        var c = mesh.AddPosition(Vector3.UnitY);
        var d = mesh.AddPosition(Vector3.UnitX + Vector3.UnitY);

        mesh.AddFace([a, b, c]);

        // ⚠ The same winding along the shared edge rather than the opposite one, which is two faces
        // facing opposite ways. Reported rather than repaired: which of the two is wrong is a question
        // about what the designer meant, and "flip normals" is a verb they run.
        mesh.AddFace([b, c, d]);

        var report = mesh.Validate();

        Assert.False(report.IsConsistent);
        Assert.Single(report.Reversed);
    }

    [Fact]
    public void A_position_no_face_uses_is_counted() {
        var mesh = Cube();

        mesh.AddPosition(new Vector3(9f, 9f, 9f));

        Assert.Equal(1, mesh.Validate().Orphans);
    }

    [Fact]
    public void Moving_a_position_does_not_disturb_the_edge_table() {
        var mesh = Cube();
        var before = mesh.Edges.ToArray();

        mesh.MovePosition(0, new Vector3(5f, 5f, 5f));

        // ⚠ Dragging a corner changes where the geometry is, not what is joined to what — and a table
        // rebuilt per frame of a drag is the whole cost of the drag.
        Assert.Equal(before, mesh.Edges);
        Assert.Equal(new Vector3(5f, 5f, 5f), mesh.Positions[0]);
    }

    // ── Construction and output ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Triangulating_a_triangulated_mesh_gives_back_what_went_in() {
        var (_, indices) = CubeSoup();
        var mesh = Cube();

        var triangles = mesh.Triangulate();

        Assert.Equal(indices.Length, triangles.Length);

        // Not the same numbers — the positions were welded — but the same triangles as places in
        // space, which is the thing a renderer draws.
        for (var index = 0; index + 2 < triangles.Length; index += 3) {
            var a = mesh.Positions[triangles[index]];
            var b = mesh.Positions[triangles[index + 1]];
            var c = mesh.Positions[triangles[index + 2]];

            Assert.NotEqual(a, b);
            Assert.NotEqual(b, c);
        }
    }

    [Fact]
    public void A_quad_triangulates_into_two_triangles_that_cover_it() {
        var mesh = new EditMesh();

        var a = mesh.AddPosition(Vector3.Zero);
        var b = mesh.AddPosition(Vector3.UnitX);
        var c = mesh.AddPosition(Vector3.UnitX + Vector3.UnitY);
        var d = mesh.AddPosition(Vector3.UnitY);

        mesh.AddFace([a, b, c, d]);

        var triangles = mesh.Triangulate();

        // ⚠ Two triangles and every corner used — not a particular diagonal. Doc 24's P3 replaced the
        // fan with ear clipping, which picks whichever corner is an ear first; asserting the diagonal
        // would be asserting the order of the search rather than the shape of the answer.
        Assert.Equal(6, triangles.Length);
        Assert.Equal([a, b, c, d], triangles.Distinct().Order());
        Assert.Equal(1f, Area(mesh, triangles), 5);
    }

    [Fact]
    public void A_concave_face_triangulates_without_a_triangle_outside_it() {
        var mesh = new EditMesh();

        // An L, whose reflex corner is the one a fan from the first corner cuts straight across.
        mesh.AddPosition(Vector3.Zero);
        mesh.AddPosition(new Vector3(2f, 0f, 0f));
        mesh.AddPosition(new Vector3(2f, 1f, 0f));
        mesh.AddPosition(new Vector3(1f, 1f, 0f));
        mesh.AddPosition(new Vector3(1f, 2f, 0f));
        mesh.AddPosition(new Vector3(0f, 2f, 0f));

        mesh.AddFace([0, 1, 2, 3, 4, 5]);

        var triangles = mesh.Triangulate();

        // The L's area is three, and a fan from corner zero would have covered four — the missing
        // one being the square outside the notch, drawn over geometry that is not there.
        Assert.Equal(12, triangles.Length);
        Assert.Equal(3f, Area(mesh, triangles), 4);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void A_concave_face_triangulates_the_same_whichever_axis_it_faces_along(int axis) {
        // ⚠ Both ways along each axis, and the negative one is the case worth having. Flattening a
        // face drops the axis its normal is most nearly parallel to, and the two coordinates left
        // over are anticlockwise only for a normal pointing the positive way — the other three faces
        // of a box need them swapped back. Get that wrong and every corner reads as reflex, ear
        // clipping finds nothing and falls back to the fan, and the fan of an L covers a square that
        // is not there.
        foreach (var sign in new[] { 1f, -1f }) {
            var mesh = new EditMesh();

            // The same L as above, turned to face along the axis under test.
            foreach (var corner in new[] {
                         Vector2.Zero, new(2f, 0f), new(2f, 1f), new(1f, 1f), new(1f, 2f), new Vector2(0f, 2f)
                     }) {
                var placed = axis switch {
                    0 => new Vector3(0f, corner.X, corner.Y),
                    1 => new Vector3(corner.Y, 0f, corner.X),
                    _ => new Vector3(corner.X, corner.Y, 0f)
                };

                mesh.AddPosition(sign < 0f ? -placed : placed);
            }

            mesh.AddFace(sign < 0f ? [5, 4, 3, 2, 1, 0] : [0, 1, 2, 3, 4, 5]);

            var triangles = mesh.Triangulate();

            Assert.Equal(12, triangles.Length);
            Assert.Equal(3f, Area(mesh, triangles), 4);
        }
    }

    /// <summary>Every shape, so that a kind added without a generator escapes this too.</summary>
    public static TheoryData<ShapeKind> Kinds {
        get {
            var data = new TheoryData<ShapeKind>();

            foreach (var kind in Enum.GetValues<ShapeKind>()) {
                data.Add(kind);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Triangulating_a_shape_gives_the_same_indices_whatever_units_it_was_authored_in(ShapeKind kind) {
        var mesh = MeshShapes.Create(kind);
        var reference = mesh.Triangulate();

        // A thousandth and a thousandfold: metres against millimetres, and metres against kilometres.
        // Neither factor is a power of two, so every coordinate is genuinely re-rounded rather than
        // having its exponent shifted — which is the whole point, and is what a unit conversion in an
        // importer does.
        foreach (var scale in new[] { 1e-3f, 1f, 1e+3f }) {
            var scaled = new EditMesh(mesh);

            for (var index = 0; index < scaled.PositionCount; index++) {
                scaled.MovePosition(index, scaled.Positions[index] * scale);
            }

            // ⚠ The indices themselves, not the triangles as places in space or their total area.
            // Doc 41's § D14 makes byte-identical remesher output a gate and doc 08 caches compiled
            // assets on a content hash, so a triangulation that depends on the units a model was
            // authored in re-pages every meshlet of an asset somebody converted — and doc 22's
            // crack-freedom is an equality over shared boundary vertices, which a different diagonal
            // across a shared quad breaks outright.
            Assert.Equal(reference, scaled.Triangulate());
        }
    }

    /// <summary>The total area of a triangle list, which is what a triangulation must preserve.</summary>
    static float Area(EditMesh mesh, int[] triangles) {
        var total = 0f;

        for (var index = 0; index + 2 < triangles.Length; index += 3) {
            var a = mesh.Positions[triangles[index]];
            var b = mesh.Positions[triangles[index + 1]];
            var c = mesh.Positions[triangles[index + 2]];

            total += Vector3.Cross(b - a, c - a).Length() * 0.5f;
        }

        return total;
    }

    [Fact]
    public void A_seam_welds_and_exact_equality_would_not_have() {
        Vector3[] positions = [
            Vector3.Zero,
            Vector3.UnitX,
            Vector3.UnitY,

            // The same point arrived at by different arithmetic, which is what `cos 0` against
            // `cos 2π` gives on the seam of every curved primitive.
            new Vector3(1e-8f, 0f, 0f),
            Vector3.UnitX,
            new Vector3(1f, 1f, 0f)
        ];

        var mesh = EditMesh.FromTriangles(positions, [0, 1, 2, 3, 4, 5]);

        Assert.Equal(4, mesh.PositionCount);
        Assert.Equal(2, mesh.FaceCount);
    }

    [Fact]
    public void A_triangle_that_collapsed_during_the_weld_is_dropped() {
        Vector3[] positions = [Vector3.Zero, new(1e-9f, 0f, 0f), Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ];

        // The first triangle's two ends weld onto one another, so it has no area, no normal and no
        // edge. A face table containing one is a table every operation has to test for.
        var mesh = EditMesh.FromTriangles(positions, [0, 1, 2, 0, 3, 4]);

        Assert.Equal(1, mesh.FaceCount);
    }

    [Fact]
    public void The_bounds_are_round_the_positions_and_an_empty_mesh_has_none() {
        Assert.Equal(default, new EditMesh().Bounds);

        var bounds = Cube(2f).Bounds;

        Assert.Equal(new Vector3(-1f), bounds.Minimum);
        Assert.Equal(new Vector3(1f), bounds.Maximum);
    }

    [Fact]
    public void A_normal_is_Newells_and_survives_a_nearly_collinear_corner() {
        var mesh = new EditMesh();

        var a = mesh.AddPosition(Vector3.Zero);
        var b = mesh.AddPosition(new Vector3(1f, 0f, 0f));

        // Almost on the line from a to b, which is what makes the cross product of the first three
        // corners noise — and what Newell's area-weighted sum over the whole loop is immune to.
        var c = mesh.AddPosition(new Vector3(2f, 1e-6f, 0f));
        var d = mesh.AddPosition(new Vector3(2f, 1f, 0f));
        var e = mesh.AddPosition(new Vector3(0f, 1f, 0f));

        mesh.AddFace([a, b, c, d, e]);

        Assert.True(Vector3.NearEqual(mesh.Normal(0), Vector3.UnitZ, 1e-4f), $"normal is {mesh.Normal(0)}");
    }

    // ── Layers, cloning and the refusals ────────────────────────────────────────────────────────

    [Fact]
    public void A_corner_layer_is_sized_to_the_corners_and_empty_means_absent() {
        var mesh = Cube();

        Assert.True(mesh.Normals.IsEmpty);

        mesh.SetNormals(new Vector3[mesh.CornerCount]);
        Assert.Equal(mesh.CornerCount, mesh.Normals.Length);

        // ⚠ Empty and zeroed are different things: the first is a mesh whose normals have not been
        // computed and the second is one whose normals are wrong.
        Assert.Throws<ArgumentException>(() => mesh.SetTexCoords(new Vector2[3]));
    }

    [Fact]
    public void A_layer_grows_with_the_faces_added_after_it() {
        var mesh = Cube();

        mesh.SetNormals(new Vector3[mesh.CornerCount]);

        var a = mesh.AddPosition(new Vector3(4f, 0f, 0f));
        var b = mesh.AddPosition(new Vector3(5f, 0f, 0f));
        var c = mesh.AddPosition(new Vector3(4f, 1f, 0f));

        mesh.AddFace([a, b, c]);

        // A layer that stayed short by three is one that throws on the next read, in whatever operation
        // happens to walk it — which is nowhere near the face that was added.
        Assert.Equal(mesh.CornerCount, mesh.Normals.Length);
    }

    [Fact]
    public void A_copy_shares_nothing_with_what_it_was_copied_from() {
        var mesh = Cube();
        var copy = new EditMesh(mesh);

        mesh.MovePosition(0, new Vector3(9f, 9f, 9f));
        mesh.SetGroup(0, 42);

        // ⚠ This is what an undo entry holds for a topology change, and a shallow copy is the mistake
        // `Vixen.Editor.Core`'s randomised do/undo/redo suite exists to catch: a command that stored a
        // reference where it needed a copy undoes to whatever the mesh looks like now.
        Assert.NotEqual(mesh.Positions[0], copy.Positions[0]);
        Assert.NotEqual(mesh.Faces[0].Group, copy.Faces[0].Group);

        AssertSolid(copy);
    }

    [Fact]
    public void A_face_of_two_corners_and_a_corner_naming_nothing_are_refused_at_the_door() {
        var mesh = new EditMesh();

        mesh.AddPosition(Vector3.Zero);
        mesh.AddPosition(Vector3.UnitX);

        // ⚠ Refused rather than reported, unlike everything in `MeshReport`. A designer can
        // legitimately make a non-manifold edge or an inside-out face; neither of these is something
        // anybody meant, and both fail somewhere further along in an operation that did not create
        // them.
        Assert.Throws<ArgumentException>(() => mesh.AddFace([0, 1]));
        Assert.Throws<ArgumentException>(() => mesh.AddFace([0, 1, 7]));
    }

    [Fact]
    public void Every_primitive_shaped_soup_comes_out_solid() {
        foreach (var size in new[] { 0.5f, 1f, 4f }) {
            var mesh = Cube(size);

            AssertSolid(mesh);
            Assert.True(mesh.Validate().IsSolid, mesh.Validate().Describe() ?? "solid");
        }
    }
}
