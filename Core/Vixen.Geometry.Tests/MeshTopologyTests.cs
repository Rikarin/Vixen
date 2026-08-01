// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Geometry.Tests;

/// <summary>Doc 24's P2 selection table, asked of shapes rather than of clicks.</summary>
public class MeshTopologyTests {
    // ── The tables the walks rest on ────────────────────────────────────────────────────────────

    [Fact]
    public void A_cubes_corner_has_the_three_edges_and_three_faces_that_meet_there() {
        var mesh = MeshShapes.Box();

        for (var position = 0; position < mesh.PositionCount; position++) {
            Assert.Equal(3, mesh.EdgesAt(position).Length);
            Assert.Equal(3, mesh.FacesAt(position).Length);
        }
    }

    [Fact]
    public void An_edge_between_two_positions_is_the_same_edge_read_either_way_round() {
        var mesh = MeshShapes.Box();

        for (var edge = 0; edge < mesh.Edges.Count; edge++) {
            var (a, b) = mesh.Edges[edge];

            Assert.Equal(edge, mesh.EdgeBetween(a, b));
            Assert.Equal(edge, mesh.EdgeBetween(b, a));
        }
    }

    [Fact]
    public void Two_positions_no_face_joins_have_no_edge_between_them() {
        var mesh = MeshShapes.Box();

        // Opposite corners of a cube: three faces away, and joined by nothing.
        Assert.Equal(-1, mesh.EdgeBetween(0, 7));
    }

    [Fact]
    public void Moving_a_position_does_not_disturb_the_incidence_tables() {
        var mesh = MeshShapes.Tube();
        var was = mesh.EdgesAt(4).ToArray();

        mesh.MovePosition(4, new Vector3(9f, 9f, 9f));

        // ⚠ The same guarantee the edge table itself makes, and for the same reason: a drag is a
        // change of where and not of what is joined to what. Rebuilding these per frame of a drag is
        // the whole cost of the drag.
        Assert.Equal(was, mesh.EdgesAt(4).ToArray());
    }

    // ── Loops ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_loop_round_a_tube_closes_and_is_one_edge_per_side() {
        var mesh = MeshShapes.Tube(8, 3);
        var around = Around(mesh, band: 1);

        List<int> loop = [];

        MeshTopology.EdgeLoop(mesh, around, loop);

        Assert.Equal(8, loop.Count);
        Assert.Contains(around, loop);

        // Every edge in it runs between two positions of the same circle, which is what "round" means.
        foreach (var edge in loop) {
            Assert.Equal(Band(mesh, mesh.Edges[edge].A), Band(mesh, mesh.Edges[edge].B));
        }
    }

    [Fact]
    public void A_loop_along_a_tube_runs_the_whole_length_and_stops_at_the_open_ends() {
        var mesh = MeshShapes.Tube(8, 3);

        // An edge from the first circle to the second, at side zero.
        var along = mesh.EdgeBetween(0, 8);

        List<int> loop = [];

        MeshTopology.EdgeLoop(mesh, along, loop);

        // ⚠ Three bands, so three edges — and it ends at the rim rather than turning the corner,
        // because the rim's positions have three edges and a loop's continuation is defined only
        // where four meet. Stopping short is visible; guessing is not.
        Assert.Equal(3, loop.Count);
    }

    [Fact]
    public void A_loop_on_a_box_is_the_edge_it_started_from_and_nothing_else() {
        var mesh = MeshShapes.Box();

        List<int> loop = [];

        MeshTopology.EdgeLoop(mesh, 0, loop);

        // Every corner of a box has three edges, so there is nowhere for a loop to continue to.
        Assert.Single(loop);
        Assert.Equal(0, loop[0]);
    }

    [Fact]
    public void A_loop_asked_about_an_edge_that_is_not_there_answers_with_nothing() {
        var mesh = MeshShapes.Box();

        List<int> loop = [1, 2, 3];

        MeshTopology.EdgeLoop(mesh, mesh.Edges.Count, loop);

        Assert.Empty(loop);
    }

    // ── Rings ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_ring_crosses_the_quads_a_loop_runs_along() {
        var mesh = MeshShapes.Tube(8, 3);

        // An edge along the tube: its ring is the eight edges at the same height, one per side.
        var along = mesh.EdgeBetween(0, 8);

        List<int> ring = [];

        MeshTopology.EdgeRing(mesh, along, ring);

        Assert.Equal(8, ring.Count);

        foreach (var edge in ring) {
            Assert.NotEqual(Band(mesh, mesh.Edges[edge].A), Band(mesh, mesh.Edges[edge].B));
        }
    }

    [Fact]
    public void A_ring_and_a_loop_through_one_edge_share_only_that_edge() {
        var mesh = MeshShapes.Tube(8, 3);
        var along = mesh.EdgeBetween(0, 8);

        List<int> ring = [];
        List<int> loop = [];

        MeshTopology.EdgeRing(mesh, along, ring);
        MeshTopology.EdgeLoop(mesh, along, loop);

        // Rails and rungs: one runs along the strip of quads and the other across it, which is the
        // whole difference between what a loop cut is inserted along and what a bridge joins.
        Assert.Equal([along], ring.Intersect(loop));
    }

    [Fact]
    public void A_ring_through_a_grid_ends_at_the_rim() {
        var mesh = MeshShapes.Grid(3, 3);
        var across = mesh.EdgeBetween(0, 4);

        List<int> ring = [];

        MeshTopology.EdgeRing(mesh, across, ring);

        // Three quads across, so four rungs — a rung at each end as well as between them — and then a
        // boundary edge with nothing on its far side, which is where the walk stops.
        Assert.Equal(4, ring.Count);

        // Both ends of it are on the rim, which is what "ends at the rim" means for a walk that runs
        // in both directions from the edge it was given.
        Assert.Equal(2, ring.Count(edge => mesh.FacesOf(edge).Length == 1));
    }

    [Fact]
    public void A_ring_through_a_triangle_is_the_edge_alone() {
        var mesh = new EditMesh();

        mesh.AddPosition(Vector3.Zero);
        mesh.AddPosition(Vector3.UnitX);
        mesh.AddPosition(Vector3.UnitZ);
        mesh.AddFace([0, 1, 2]);

        List<int> ring = [];

        MeshTopology.EdgeRing(mesh, 0, ring);

        // ⚠ Declining rather than guessing. In a triangle no edge is opposite another, so a ring
        // through one has nowhere to go — and a walk that picked something would wander through a
        // mesh in a way nobody could describe afterwards.
        Assert.Single(ring);
    }

    // ── Regions ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Every_coplanar_face_of_a_grid_is_the_whole_grid() {
        var mesh = MeshShapes.Grid(3, 3);

        List<int> region = [];

        MeshTopology.Coplanar(mesh, 0, region);

        Assert.Equal(9, region.Count);
    }

    [Fact]
    public void A_coplanar_selection_stops_at_the_first_corner() {
        var mesh = MeshShapes.Box();

        List<int> region = [];

        MeshTopology.Coplanar(mesh, 0, region);

        // A box's neighbours are all at right angles, so "this wall" is the one face.
        Assert.Single(region);
    }

    [Fact]
    public void A_coplanar_selection_is_measured_against_the_face_that_was_clicked() {
        var mesh = MeshShapes.Tube(64, 1);

        List<int> region = [];

        MeshTopology.Coplanar(mesh, 0, region);

        // ⚠ The case the rule exists for. Sixty-four sides means neighbours a few degrees apart, so
        // comparing each face with the one it was reached from would walk the whole cylinder one
        // small step at a time and select a tube as a plane. Against the seed, it takes the handful
        // that really are flat with what was clicked.
        Assert.InRange(region.Count, 1, 8);
    }

    [Fact]
    public void A_group_is_every_face_filed_under_it_whether_or_not_they_touch() {
        var mesh = MeshShapes.Tube(8, 3);

        List<int> group = [];

        MeshTopology.Group(mesh, 1, group);

        Assert.Equal(8, group.Count);
        Assert.All(group, face => Assert.Equal(1, mesh.Faces[face].Group));
    }

    [Fact]
    public void A_shell_is_everything_joined_to_what_was_clicked_and_nothing_else() {
        var mesh = MeshShapes.Box();
        var apart = MeshShapes.Grid(1, 1);

        // Two shapes in one mesh, sharing nothing.
        var offset = mesh.PositionCount;

        foreach (var position in apart.Positions) {
            mesh.AddPosition(position + new Vector3(10f, 0f, 0f));
        }

        for (var face = 0; face < apart.FaceCount; face++) {
            var loop = apart.CornersOf(face).ToArray();

            for (var corner = 0; corner < loop.Length; corner++) {
                loop[corner] += offset;
            }

            mesh.AddFace(loop, 9);
        }

        List<int> shell = [];

        MeshTopology.Shell(mesh, 0, shell);

        Assert.Equal(6, shell.Count);
    }

    // ── Boundaries ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_grids_rim_is_one_closed_loop_of_positions() {
        var mesh = MeshShapes.Grid(3, 3);
        var rim = Boundary(mesh);

        List<int> loop = [];

        Assert.True(MeshTopology.BoundaryLoop(mesh, rim, loop));

        // Twelve positions round a three-by-three grid's rim, each once and in order.
        Assert.Equal(12, loop.Count);
        Assert.Equal(12, loop.Distinct().Count());

        for (var index = 0; index < loop.Count; index++) {
            Assert.True(mesh.EdgeBetween(loop[index], loop[(index + 1) % loop.Count]) >= 0);
        }
    }

    [Fact]
    public void A_closed_box_has_no_boundary_to_walk() {
        var mesh = MeshShapes.Box();

        List<int> loop = [];

        Assert.False(MeshTopology.BoundaryLoop(mesh, 0, loop));
        Assert.Empty(loop);
    }

    [Fact]
    public void A_tubes_two_rims_are_two_loops_that_share_nothing() {
        var mesh = MeshShapes.Tube(8, 2);

        List<int> first = [];
        List<int> second = [];

        Assert.True(MeshTopology.BoundaryLoop(mesh, Boundary(mesh), first));
        Assert.True(MeshTopology.BoundaryLoop(mesh, Boundary(mesh, skip: first), second));

        Assert.Equal(8, first.Count);
        Assert.Equal(8, second.Count);
        Assert.Empty(first.Intersect(second));
    }

    /// <summary>The first boundary edge, optionally one that misses a rim already walked.</summary>
    static int Boundary(EditMesh mesh, IReadOnlyCollection<int>? skip = null) {
        for (var edge = 0; edge < mesh.Edges.Count; edge++) {
            if (mesh.FacesOf(edge).Length == 1 && skip?.Contains(mesh.Edges[edge].A) != true) {
                return edge;
            }
        }

        return -1;
    }

    /// <summary>An edge running round one of a tube's circles.</summary>
    static int Around(EditMesh mesh, int band, int sides = 8) =>
        mesh.EdgeBetween((band * sides) + 0, (band * sides) + 1);

    /// <summary>Which of a tube's circles a position is on.</summary>
    static int Band(EditMesh mesh, int position, int sides = 8) => position / sides;
}
