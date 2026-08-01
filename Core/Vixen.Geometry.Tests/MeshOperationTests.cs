// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Geometry.Tests;

/// <summary>Doc 24's P3 geometry table: what each verb does to a mesh, and what it leaves true.</summary>
/// <remarks>
///     ⚠ <b>Every test ends with the invariant helper, which is doc 24's testing table's highest-value
///     item.</b> An operation that corrupts the edge table produces geometry that looks correct and
///     fails three operations later, in a mesh a designer has spent an hour on, with no way to
///     attribute it. Asserting the whole structure after each verb turns that into a failing test in
///     the commit that caused it.
/// </remarks>
public class MeshOperationTests {
    /// <summary>The whole structure, not only what the operation under test claimed to do.</summary>
    static void AssertSound(EditMesh mesh) {
        Assert.All(mesh.Faces, face => Assert.True(face.Count >= 3, "a face wants three corners"));

        foreach (var corner in mesh.Corners) {
            Assert.InRange(corner, 0, mesh.PositionCount - 1);
        }

        for (var edge = 0; edge < mesh.Edges.Count; edge++) {
            Assert.NotEmpty(mesh.FacesOf(edge).ToArray());
            Assert.True(mesh.Edges[edge].A < mesh.Edges[edge].B, "an edge is stored low-to-high");
        }

        var total = 0;

        foreach (var face in mesh.Faces) {
            total += face.Count;
        }

        Assert.Equal(mesh.CornerCount, total);
    }

    /// <summary>Sound, and a closed consistent solid on top of it.</summary>
    static void AssertSolid(EditMesh mesh) {
        AssertSound(mesh);

        var report = mesh.Validate();

        // ⚠ Orphans allowed, because every verb here leaves positions behind on purpose — see
        // `MeshOperations.Compact` for why compacting inside a gesture is the wrong moment.
        Assert.True(report.IsClosed, report.Describe() ?? "closed");
        Assert.True(report.IsConsistent, report.Describe() ?? "consistent");
        Assert.Empty(report.Degenerate);
    }

    static float Volume(EditMesh mesh) {
        var triangles = mesh.Triangulate();
        var total = 0f;

        for (var index = 0; index + 2 < triangles.Length; index += 3) {
            var a = mesh.Positions[triangles[index]];
            var b = mesh.Positions[triangles[index + 1]];
            var c = mesh.Positions[triangles[index + 2]];

            total += Vector3.Dot(a, Vector3.Cross(b, c)) / 6f;
        }

        return total;
    }

    static int Top(EditMesh mesh) {
        var top = 0;

        for (var face = 1; face < mesh.FaceCount; face++) {
            if (mesh.Normal(face).Y > mesh.Normal(top).Y) {
                top = face;
            }
        }

        return top;
    }

    // ── Extrude ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Extruding_a_box_face_makes_a_taller_box() {
        var mesh = MeshShapes.Box(2f);
        var top = Top(mesh);

        var made = MeshOperations.Extrude(mesh, [top], 3f);

        Assert.Single(made);

        // Six sides became ten: the five that were not extruded, the one that moved, and four walls.
        Assert.Equal(10, mesh.FaceCount);
        Assert.Equal(8f + (2f * 2f * 3f), Volume(mesh), 3);

        AssertSolid(mesh);
    }

    [Fact]
    public void An_extrude_of_zero_still_builds_the_walls() {
        var mesh = MeshShapes.Box();

        MeshOperations.Extrude(mesh, [Top(mesh)], 0f);

        // ⚠ Exactly what a drag's first frame is. A verb that did nothing until the pointer moved
        // would have its walls appear a frame late and an undo entry that does not match what the
        // designer saw. The walls have no area yet, which is why this asserts soundness and not
        // solidity — the degenerate faces are real and they go the moment the drag moves.
        Assert.Equal(10, mesh.FaceCount);
        AssertSound(mesh);
    }

    [Fact]
    public void Extruding_a_region_gives_one_box_and_extruding_individually_gives_several() {
        var region = MeshShapes.Grid(2, 1);
        var apart = MeshShapes.Grid(2, 1);

        MeshOperations.Extrude(region, [0, 1], 1f);
        MeshOperations.Extrude(apart, [0, 1], 1f, individually: true);

        // ⚠ The whole difference is what counts as the rim. As a region the edge between the two
        // quads is interior and gets no wall; individually every edge is a rim, so the two boxes have
        // a wall each where they meet.
        Assert.True(
            apart.FaceCount > region.FaceCount,
            $"individually should make more faces: {apart.FaceCount} against {region.FaceCount}"
        );

        AssertSound(region);
        AssertSound(apart);
    }

    [Fact]
    public void Extruding_along_an_axis_moves_the_face_that_way_rather_than_along_its_normal() {
        var mesh = MeshShapes.Box(2f);
        var top = Top(mesh);

        var made = MeshOperations.ExtrudeAlong(mesh, [top], new Vector3(4f, 0f, 0f));

        Assert.Single(made);

        var moved = mesh.Faces[made[0]];
        var centre = Vector3.Zero;

        foreach (var corner in mesh.CornersOf(made[0])) {
            centre += mesh.Positions[corner];
        }

        centre /= moved.Count;

        Assert.Equal(4f, centre.X, 3);
        Assert.Equal(1f, centre.Y, 3);

        AssertSound(mesh);
    }

    [Fact]
    public void Extruding_nothing_does_nothing() {
        var mesh = MeshShapes.Box();
        var was = mesh.FaceCount;

        Assert.Empty(MeshOperations.Extrude(mesh, [], 1f));
        Assert.Empty(MeshOperations.Extrude(mesh, [99], 1f));
        Assert.Equal(was, mesh.FaceCount);
    }

    // ── Inset ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Insetting_a_face_leaves_a_ring_round_a_smaller_one() {
        var mesh = MeshShapes.Box(2f);
        var top = Top(mesh);

        var made = MeshOperations.Inset(mesh, [top], 0.25f);

        Assert.Single(made);
        Assert.Equal(10, mesh.FaceCount);

        // The inner face is smaller and in the same plane, which is what an inset means.
        Assert.True(mesh.Area(made[0]) < 4f, "the inner face should be smaller");
        Assert.Equal(1f, mesh.Normal(made[0]).Y, 3);

        AssertSolid(mesh);
    }

    [Fact]
    public void An_inset_past_the_middle_collapses_rather_than_turning_inside_out() {
        var mesh = MeshShapes.Box(2f);

        var made = MeshOperations.Inset(mesh, [Top(mesh)], 50f);

        // ⚠ A designer dragging an inset too far is asking for a point, not for a bow tie — and a bow
        // tie is geometry no triangulation is right for. The inner face has no area left, which is a
        // reported degeneracy rather than a self-intersection.
        Assert.DoesNotContain(made, face => mesh.Area(face) > 1e-4f);
        AssertSound(mesh);
    }

    // ── Bevel ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Bevelling_one_edge_of_a_box_cuts_the_corner_off_it() {
        var mesh = MeshShapes.Box(2f);
        var was = mesh.FaceCount;

        var made = MeshOperations.Bevel(mesh, [0], 0.3f, 1, out var unresolved);

        Assert.Single(made);
        Assert.Equal(was + 1, mesh.FaceCount);
        Assert.Equal(0, unresolved);

        AssertSound(mesh);
    }

    [Fact]
    public void A_bevel_with_segments_makes_a_face_per_segment() {
        var mesh = MeshShapes.Box(2f);

        var made = MeshOperations.Bevel(mesh, [0], 0.3f, 3, out _);

        Assert.Equal(3, made.Count);
        AssertSound(mesh);
    }

    [Fact]
    public void A_bevel_reports_the_corners_it_could_not_resolve_rather_than_producing_them() {
        var mesh = MeshShapes.Box(2f);

        // Two edges meeting at a corner, which is the case doc 24 calls a miniature research problem.
        var first = 0;
        var second = -1;

        for (var edge = 1; edge < mesh.Edges.Count; edge++) {
            if (mesh.Edges[edge].Touches(mesh.Edges[first].A) || mesh.Edges[edge].Touches(mesh.Edges[first].B)) {
                second = edge;
                break;
            }
        }

        MeshOperations.Bevel(mesh, [first, second], 0.2f, 1, out var unresolved);

        // ⚠ The honest first version bevels edges independently and says where it could not resolve a
        // corner, rather than producing a self-intersecting one silently.
        Assert.True(unresolved > 0, "a shared corner should be reported");
        AssertSound(mesh);
    }

    [Fact]
    public void A_non_manifold_edge_cannot_be_bevelled_and_is_counted() {
        var mesh = MeshShapes.Grid(1, 1);

        MeshOperations.Bevel(mesh, [0], 0.2f, 1, out var unresolved);

        // A grid's every edge is a boundary edge: "cut the corner between these two faces" has no
        // meaning where there is only one.
        Assert.Equal(1, unresolved);
    }

    // ── Loop cut and subdivide ──────────────────────────────────────────────────────────────────

    [Fact]
    public void A_loop_cut_through_a_tube_doubles_its_bands() {
        var mesh = MeshShapes.Tube(8, 2);
        var was = mesh.FaceCount;

        // An edge running along the tube, whose ring crosses all eight sides.
        var along = mesh.EdgeBetween(0, 8);

        var made = MeshOperations.LoopCut(mesh, along);

        Assert.Equal(was + 8, mesh.FaceCount);

        // The eight quads the ring ran through became sixteen; the other band is untouched, which is
        // what `made` naming sixteen rather than twenty-four says.
        Assert.Equal(16, made.Count);

        AssertSound(mesh);
    }

    [Fact]
    public void A_loop_cut_of_three_puts_three_loops_in() {
        var mesh = MeshShapes.Tube(8, 1);
        var was = mesh.FaceCount;

        MeshOperations.LoopCut(mesh, mesh.EdgeBetween(0, 8), cuts: 3);

        Assert.Equal(was * 4, mesh.FaceCount);
        AssertSound(mesh);
    }

    [Fact]
    public void A_loop_cut_slides_where_it_is_told() {
        var mesh = MeshShapes.Tube(8, 1);

        MeshOperations.LoopCut(mesh, mesh.EdgeBetween(0, 8), cuts: 1, slide: 0.25f);

        // The tube runs from −0.5 to 0.5 in Y, so a quarter along is −0.25.
        var found = false;

        foreach (var position in mesh.Positions) {
            found |= MathF.Abs(position.Y + 0.25f) < 1e-4f;
        }

        Assert.True(found, "the cut should sit a quarter of the way along");
        AssertSound(mesh);
    }

    [Fact]
    public void Subdividing_a_quad_makes_four() {
        var mesh = MeshShapes.Box();

        var made = MeshOperations.Subdivide(mesh, [0]);

        Assert.Equal(4, made.Count);
        Assert.Equal(9, mesh.FaceCount);
        Assert.All(made, face => Assert.Equal(4, mesh.Faces[face].Count));

        AssertSolid(mesh);
    }

    [Fact]
    public void Subdividing_the_whole_box_twice_is_sixteen_faces_a_side() {
        var mesh = MeshShapes.Box();

        MeshOperations.Subdivide(mesh, [.. Enumerable.Range(0, mesh.FaceCount)], count: 2);

        Assert.Equal(6 * 16, mesh.FaceCount);
        AssertSolid(mesh);
    }

    // ── Bridge and fill ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Bridging_two_faces_makes_a_tube_and_removes_them() {
        var mesh = MeshShapes.Box(2f);

        var top = Top(mesh);
        var bottom = 0;

        for (var face = 1; face < mesh.FaceCount; face++) {
            if (mesh.Normal(face).Y < mesh.Normal(bottom).Y) {
                bottom = face;
            }
        }

        var made = MeshOperations.Bridge(mesh, top, bottom);

        Assert.Equal(4, made.Count);

        // Six faces less two, plus four walls: the box became a tube through its own middle.
        Assert.Equal(8, mesh.FaceCount);
        AssertSound(mesh);
    }

    [Fact]
    public void Bridging_faces_of_different_shapes_declines_rather_than_guessing() {
        var mesh = MeshShapes.Box();

        mesh.AddPosition(new Vector3(3f, 0f, 0f));
        mesh.AddPosition(new Vector3(4f, 0f, 0f));
        mesh.AddPosition(new Vector3(3.5f, 1f, 0f));

        var triangle = mesh.AddFace([mesh.PositionCount - 3, mesh.PositionCount - 2, mesh.PositionCount - 1]);
        var was = mesh.FaceCount;

        Assert.Empty(MeshOperations.Bridge(mesh, 0, triangle));
        Assert.Equal(was, mesh.FaceCount);
    }

    [Fact]
    public void Filling_a_hole_closes_the_mesh_again() {
        var mesh = MeshShapes.Box();

        MeshOperations.Delete(mesh, [0]);

        Assert.False(mesh.Validate().IsClosed);

        var rim = -1;

        for (var edge = 0; edge < mesh.Edges.Count; edge++) {
            if (mesh.FacesOf(edge).Length == 1) {
                rim = edge;
                break;
            }
        }

        Assert.True(MeshOperations.FillHole(mesh, rim) >= 0);
        AssertSolid(mesh);
    }

    [Fact]
    public void A_fill_wound_the_wrong_way_would_be_reported_and_is_not() {
        var mesh = MeshShapes.Box();

        MeshOperations.Delete(mesh, [2]);

        var rim = -1;

        for (var edge = 0; edge < mesh.Edges.Count; edge++) {
            if (mesh.FacesOf(edge).Length == 1) {
                rim = edge;
                break;
            }
        }

        MeshOperations.FillHole(mesh, rim);

        // ⚠ The single face on each boundary edge already walks the rim one way, so a cap wound the
        // same way faces into the mesh — a hole that looks filled from outside and is inside out from
        // within, which `Reversed` is exactly the diagnosis for.
        Assert.Empty(mesh.Validate().Reversed);
    }

    // ── Flip, weld, dissolve, delete ────────────────────────────────────────────────────────────

    [Fact]
    public void Flipping_every_face_turns_a_box_inside_out_and_flipping_again_puts_it_back() {
        var mesh = MeshShapes.Box();
        var was = Volume(mesh);

        Assert.Equal(6, MeshOperations.Flip(mesh));
        Assert.Equal(-was, Volume(mesh), 4);

        MeshOperations.Flip(mesh);
        Assert.Equal(was, Volume(mesh), 4);

        AssertSolid(mesh);
    }

    [Fact]
    public void Flipping_one_face_of_a_solid_is_reported_as_the_inconsistency_it_is() {
        var mesh = MeshShapes.Box();

        MeshOperations.Flip(mesh, [0]);

        Assert.NotEmpty(mesh.Validate().Reversed);
        AssertSound(mesh);
    }

    [Fact]
    public void Welding_two_corners_of_a_grid_pulls_them_together_and_drops_what_collapses() {
        var mesh = MeshShapes.Grid(2, 2);
        var was = mesh.FaceCount;

        Assert.Equal(1, MeshOperations.Weld(mesh, [0, 1]));

        // The quad those two corners were both on becomes a triangle rather than a quad with a
        // zero-length edge in it.
        Assert.Equal(was, mesh.FaceCount);
        Assert.Equal(3, mesh.Faces[0].Count);

        AssertSound(mesh);
    }

    [Fact]
    public void Welding_to_a_point_puts_the_merged_position_where_it_is_told() {
        var mesh = MeshShapes.Grid(2, 2);

        MeshOperations.Weld(mesh, [0, 1], new Vector3(7f, 8f, 9f));

        Assert.Equal(new Vector3(7f, 8f, 9f), mesh.Positions[0]);
    }

    [Fact]
    public void Merging_by_distance_closes_a_seam_and_leaves_a_mesh_that_has_none() {
        var mesh = MeshShapes.Grid(2, 2);

        Assert.Equal(0, MeshOperations.MergeByDistance(mesh, 0.1f));

        // A grid's positions are a unit apart, so a tolerance of one and a half merges neighbours.
        Assert.True(MeshOperations.MergeByDistance(mesh, 1.5f) > 0);
        AssertSound(mesh);
    }

    [Fact]
    public void Dissolving_a_diagonal_turns_two_triangles_back_into_a_quad() {
        var mesh = new EditMesh();

        mesh.AddPosition(Vector3.Zero);
        mesh.AddPosition(Vector3.UnitX);
        mesh.AddPosition(Vector3.UnitX + Vector3.UnitZ);
        mesh.AddPosition(Vector3.UnitZ);

        mesh.AddFace([0, 1, 2]);
        mesh.AddFace([0, 2, 3]);

        var diagonal = mesh.EdgeBetween(0, 2);

        Assert.Equal(1, MeshOperations.Dissolve(mesh, [diagonal]));

        // ⚠ This is how a block-out made of triangles becomes one made of quads, which is what makes
        // loops and rings work on it afterwards.
        Assert.Equal(1, mesh.FaceCount);
        Assert.Equal(4, mesh.Faces[0].Count);
        Assert.Equal(-1, mesh.EdgeBetween(0, 2));

        AssertSound(mesh);
    }

    [Fact]
    public void A_boundary_edge_cannot_be_dissolved_and_is_skipped_rather_than_refused() {
        var mesh = MeshShapes.Grid(1, 1);

        Assert.Equal(0, MeshOperations.Dissolve(mesh, [0, 1, 2, 3]));
        Assert.Equal(1, mesh.FaceCount);
    }

    [Fact]
    public void Deleting_a_face_leaves_a_hole_and_leaves_its_positions() {
        var mesh = MeshShapes.Box();
        var positions = mesh.PositionCount;

        Assert.Equal(1, MeshOperations.Delete(mesh, [0]));
        Assert.Equal(5, mesh.FaceCount);

        // ⚠ The positions stay. A selection, an undo entry and a drag in flight all hold position
        // indices, and renumbering them under a running gesture is the defect D3 exists to prevent.
        Assert.Equal(positions, mesh.PositionCount);
        AssertSound(mesh);
    }

    [Fact]
    public void Compacting_removes_the_orphans_and_says_where_everything_went() {
        var mesh = MeshShapes.Box();

        MeshOperations.Delete(mesh, [0]);
        MeshOperations.Delete(mesh, [.. Enumerable.Range(0, mesh.FaceCount - 1)]);

        var map = MeshOperations.Compact(mesh);

        Assert.Equal(4, mesh.PositionCount);
        Assert.Equal(8, map.Length);
        Assert.Equal(4, map.Count(at => at >= 0));

        AssertSound(mesh);
    }

    // ── Whole meshes ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Detaching_faces_moves_them_into_a_mesh_of_their_own() {
        var mesh = MeshShapes.Box();

        var taken = MeshOperations.Detach(mesh, [0, 1]);

        Assert.NotNull(taken);
        Assert.Equal(2, taken.FaceCount);
        Assert.Equal(4, mesh.FaceCount);

        AssertSound(taken);
        AssertSound(mesh);
    }

    [Fact]
    public void Detaching_a_copy_leaves_the_original_alone() {
        var mesh = MeshShapes.Box();

        var taken = MeshOperations.Detach(mesh, [0], keep: true);

        Assert.NotNull(taken);
        Assert.Equal(6, mesh.FaceCount);
    }

    [Fact]
    public void Merging_two_meshes_keeps_their_groups_apart() {
        var mesh = MeshShapes.Box();
        var other = MeshShapes.Box();

        var groups = new HashSet<int>();

        foreach (var face in mesh.Faces) {
            groups.Add(face.Group);
        }

        MeshOperations.Append(mesh, other, Matrix4x4.FromTranslation(new Vector3(4f, 0f, 0f)));

        var after = new HashSet<int>();

        foreach (var face in mesh.Faces) {
            after.Add(face.Group);
        }

        // ⚠ A merge that collapsed the groups would make every select-by-group afterwards take the
        // whole room — which is precisely what "merge objects before baking" would ruin.
        Assert.Equal(groups.Count * 2, after.Count);
        Assert.Equal(12, mesh.FaceCount);

        AssertSolid(mesh);
    }
}
