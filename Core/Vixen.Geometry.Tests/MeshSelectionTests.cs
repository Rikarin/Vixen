// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Geometry.Tests;

/// <summary>What is selected in a mesh, and what changing the element mode does to it.</summary>
public class MeshSelectionTests {
    [Fact]
    public void A_new_selection_is_empty_and_is_of_vertices() {
        var selection = new MeshSelection();

        Assert.True(selection.IsEmpty);
        Assert.Equal(MeshElementKind.Vertex, selection.Kind);
        Assert.Equal(-1, selection.Active);
    }

    [Fact]
    public void Adding_the_same_element_twice_selects_it_once() {
        var selection = new MeshSelection();

        Assert.True(selection.Add(3));
        Assert.False(selection.Add(3));
        Assert.Single(selection.Indices);
    }

    [Fact]
    public void Toggling_is_both_halves_of_one_idea() {
        var selection = new MeshSelection();

        // The same modifier that extends a selection is what takes something back out of it — see
        // `SceneViewport.Select`, which makes the same argument about entities.
        Assert.True(selection.Toggle(2));
        Assert.False(selection.Toggle(2));
        Assert.True(selection.IsEmpty);
    }

    [Fact]
    public void The_active_element_is_the_one_chosen_last() {
        var selection = new MeshSelection();

        selection.Add(5);
        selection.Add(1);
        selection.Add(9);

        // ⚠ Ordered, because "weld to last" and doc 24's D4 "active element" snap base both mean the
        // most recent one — and a hash set's order is the hash's.
        Assert.Equal(9, selection.Active);
        Assert.Equal([5, 1, 9], selection.Indices);

        selection.Remove(9);

        Assert.Equal(1, selection.Active);
    }

    [Fact]
    public void The_version_moves_only_when_something_changed() {
        var selection = new MeshSelection();
        var was = selection.Version;

        selection.Add(0);

        Assert.NotEqual(was, selection.Version);

        was = selection.Version;
        selection.Add(0);
        selection.Remove(7);
        selection.Clear();
        selection.Clear();

        // One change: the clear. The two no-ops are what a frame loop calls without meaning anything
        // by it, and a version that moved for them would rebuild the highlight every frame.
        Assert.Equal(was + 1, selection.Version);
    }

    // ── Converting between the three kinds ──────────────────────────────────────────────────────

    [Fact]
    public void A_face_becomes_its_four_corners_and_its_four_edges() {
        var mesh = MeshShapes.Box();
        var selection = new MeshSelection();

        selection.Convert(mesh, MeshElementKind.Face);
        selection.Set(0);

        Assert.Equal(4, selection.Converted(mesh, MeshElementKind.Vertex).Count);
        Assert.Equal(4, selection.Converted(mesh, MeshElementKind.Edge).Count);
    }

    [Fact]
    public void A_face_converted_to_vertices_and_back_is_the_face_it_started_as() {
        var mesh = MeshShapes.Box();
        var selection = new MeshSelection();

        selection.Convert(mesh, MeshElementKind.Face);
        selection.Set(2);

        selection.Convert(mesh, MeshElementKind.Vertex);
        selection.Convert(mesh, MeshElementKind.Face);

        // ⚠ The round trip that stops switching modes twice from growing a selection. Coarse to fine
        // takes everything; fine to coarse takes only what is fully covered, so exactly the face whose
        // four corners are all there comes back.
        Assert.Equal([2], selection.Indices);
    }

    [Fact]
    public void Half_a_faces_corners_do_not_select_the_face() {
        var mesh = MeshShapes.Box();
        var selection = new MeshSelection();
        var corners = mesh.CornersOf(0).ToArray();

        selection.Set([corners[0], corners[1]]);

        Assert.Empty(selection.Converted(mesh, MeshElementKind.Face));
    }

    [Fact]
    public void An_edge_selection_covers_the_positions_a_gizmo_would_drag() {
        var mesh = MeshShapes.Box();
        var selection = new MeshSelection();

        selection.Convert(mesh, MeshElementKind.Edge);
        selection.Set([0, 1]);

        List<int> positions = [];

        selection.Positions(mesh, positions);

        // Two edges that share a corner: three positions, not four. What a gizmo drags is a set.
        Assert.Equal(3, positions.Count);
        Assert.Equal(positions.Count, positions.Distinct().Count());
    }

    [Fact]
    public void The_centre_of_a_face_is_the_average_of_its_corners() {
        var mesh = MeshShapes.Box(2f);
        var selection = new MeshSelection();

        selection.Convert(mesh, MeshElementKind.Face);
        selection.Set(0);

        var centre = Assert.IsType<Vector3>(selection.Centre(mesh));

        // The +X side of a two-unit box, so its centre is one unit out along X and nothing else.
        Assert.True(Vector3.NearEqual(centre, Vector3.UnitX, 1e-5f), centre.ToString());
    }

    [Fact]
    public void An_empty_selection_has_no_centre_rather_than_the_origin() {
        var mesh = MeshShapes.Box();

        Assert.Null(new MeshSelection().Centre(mesh));
    }

    // ── Growing and shrinking ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Growing_a_face_takes_the_faces_across_its_edges() {
        var mesh = MeshShapes.Box();
        var selection = new MeshSelection();

        selection.Convert(mesh, MeshElementKind.Face);
        selection.Set(0);
        selection.Grow(mesh);

        // A box's side has four neighbours and one face opposite it that it does not touch.
        Assert.Equal(5, selection.Count);
    }

    [Fact]
    public void Growing_does_not_leap_a_shared_corner() {
        var mesh = MeshShapes.Grid(3, 3);
        var selection = new MeshSelection();

        selection.Convert(mesh, MeshElementKind.Face);
        selection.Set(0);
        selection.Grow(mesh);

        // ⚠ The corner quad of a grid has two edge-neighbours and one more that meets it at a single
        // point. Taking that one is what makes a coplanar selection leak across a diagonal join.
        Assert.Equal(3, selection.Count);
    }

    [Fact]
    public void Shrinking_gives_back_everything_on_the_rim() {
        var mesh = MeshShapes.Grid(3, 3);
        var selection = new MeshSelection();

        selection.Convert(mesh, MeshElementKind.Face);
        selection.All(mesh);
        selection.Shrink(mesh);

        // Nine quads, and only the middle one has a neighbour across all four of its edges.
        Assert.Equal([4], selection.Indices);
    }

    [Fact]
    public void Growing_a_closed_mesh_and_shrinking_it_settles_rather_than_oscillating() {
        var mesh = MeshShapes.Box();
        var selection = new MeshSelection();

        selection.Convert(mesh, MeshElementKind.Face);
        selection.All(mesh);
        selection.Grow(mesh);

        Assert.Equal(6, selection.Count);

        selection.Shrink(mesh);

        // ⚠ Shrink is not grow's inverse and cannot be: growing a whole closed mesh changes nothing,
        // so an inverse would have to know that it had. Every face of a box has a neighbour across
        // every edge, so nothing is on the rim and nothing is given back.
        Assert.Equal(6, selection.Count);
    }

    [Fact]
    public void Growing_vertices_walks_one_edge_out() {
        var mesh = MeshShapes.Box();
        var selection = new MeshSelection();

        selection.Set(0);
        selection.Grow(mesh);

        // A cube's corner and the three corners an edge away from it.
        Assert.Equal(4, selection.Count);
    }

    // ── All, none, invert, and surviving an edit ────────────────────────────────────────────────

    [Fact]
    public void Inverting_twice_is_where_it_started() {
        var mesh = MeshShapes.Box();
        var selection = new MeshSelection();

        selection.Convert(mesh, MeshElementKind.Face);
        selection.Set([1, 3]);

        selection.Invert(mesh);

        Assert.Equal(4, selection.Count);

        selection.Invert(mesh);

        Assert.Equal([1, 3], selection.Indices.Order());
    }

    [Fact]
    public void Selecting_everything_is_every_element_of_the_mode_you_are_in() {
        var mesh = MeshShapes.Box();
        var selection = new MeshSelection();

        selection.All(mesh);

        Assert.Equal(mesh.PositionCount, selection.Count);

        selection.Convert(mesh, MeshElementKind.Edge);
        selection.All(mesh);

        Assert.Equal(mesh.Edges.Count, selection.Count);
    }

    [Fact]
    public void A_position_move_leaves_every_index_meaning_what_it_did() {
        var mesh = MeshShapes.Box();
        var selection = new MeshSelection();

        selection.Convert(mesh, MeshElementKind.Face);
        selection.Set([0, 2]);

        mesh.MovePosition(0, new Vector3(4f, 4f, 4f));

        // ⚠ Doc 24's P2 exit turns on this: a drag and its undo change where things are and not what
        // is joined to what, so the selection is still the two faces the designer chose.
        Assert.False(selection.Validate(mesh));
        Assert.Equal([0, 2], selection.Indices);
    }

    [Fact]
    public void A_selection_that_outlives_its_mesh_is_trimmed_rather_than_left_dangling() {
        var mesh = MeshShapes.Box();
        var selection = new MeshSelection();

        selection.Convert(mesh, MeshElementKind.Face);
        selection.Set([0, 5]);

        var smaller = MeshShapes.Grid(1, 1);

        Assert.True(selection.Validate(smaller));
        Assert.Equal([0], selection.Indices);
    }
}
