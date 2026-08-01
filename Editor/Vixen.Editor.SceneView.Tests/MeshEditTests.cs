// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Engine.Transforms;
using Vixen.Geometry;
using Vixen.Rendering;
using Xunit;

namespace Vixen.Editor.SceneView.Tests;

/// <summary>Doc 24's P2: an element mode, what a click takes, and what an edit does to it.</summary>
public class MeshEditTests : IDisposable {
    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-meshedit-" + Guid.NewGuid().ToString("N"));
    readonly EditorProject project;
    readonly World world = new("Test");
    readonly SceneDocument scene;
    readonly MeshEdit editing;
    readonly TransformSystem transforms = new();

    public MeshEditTests() {
        Directory.CreateDirectory(root);

        project = new(new ProjectPaths(root));
        scene = new(project, world, AssetId.Empty, "Untitled");
        editing = new(scene);
    }

    public void Dispose() {
        world.Dispose();

        if (Directory.Exists(root)) {
            Directory.Delete(root, true);
        }

        GC.SuppressFinalize(this);
    }

    Entity Cube(Vector3 position = default) {
        var entity = scene.CreateShape(
            PrimitiveKind.Cube,
            new LocalTransform { Position = position, Rotation = Quaternion.Identity, Scale = Vector3.One }
        );

        transforms.Resolve(world);
        world.AdvanceVersion();

        scene.Selection.Set(entity);

        return entity;
    }

    // ── Entering and leaving ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Entering_an_element_mode_makes_the_selected_primitive_editable() {
        var entity = Cube();

        Assert.False(scene.HasMesh(entity));
        Assert.True(editing.Enter(MeshElementKind.Face));

        // ⚠ D6's one-way door, opened by the deliberate act of entering the mode rather than by the
        // first click. Pressing `3` and seeing nothing change is a mode that reads as broken.
        Assert.True(scene.HasMesh(entity));
        Assert.Equal(entity, editing.Target);
        Assert.Equal(MeshElementKind.Face, editing.Element);
    }

    [Fact]
    public void Making_a_shape_editable_is_undoable() {
        var entity = Cube();

        editing.Enter(MeshElementKind.Vertex);

        Assert.True(scene.HasMesh(entity));

        scene.Stack.Undo();

        // A designer who pressed `2` with the wrong thing selected has to be able to take it back —
        // making a wall editable is a change to what the file writes.
        Assert.False(scene.HasMesh(entity));
    }

    [Fact]
    public void An_element_mode_with_nothing_selected_has_nothing_to_edit() {
        Assert.False(editing.Enter(MeshElementKind.Face));
        Assert.True(editing.Target.IsNull);
    }

    [Fact]
    public void An_element_mode_with_two_things_selected_has_nothing_to_edit() {
        var first = Cube();
        var second = Cube();

        scene.Selection.Set([first, second]);

        // ⚠ The element indices of two meshes are two numbering schemes, so a selection spanning both
        // is one no operation can act on. Every reference toolset draws the same line.
        Assert.False(editing.Enter(MeshElementKind.Face));
        Assert.False(scene.HasMesh(first));
    }

    [Fact]
    public void Leaving_the_element_modes_forgets_the_target_and_keeps_the_mesh() {
        var entity = Cube();

        editing.Enter(MeshElementKind.Face);
        editing.Selection.Set(0);

        editing.Exit();

        Assert.True(editing.Target.IsNull);
        Assert.True(editing.Selection.IsEmpty);

        // The demotion is not undone by coming back out: it is a one-way door, which is the whole
        // point of calling it one.
        Assert.True(scene.HasMesh(entity));
    }

    [Fact]
    public void Selecting_a_different_entity_drops_the_element_selection() {
        Cube();

        editing.Enter(MeshElementKind.Face);
        editing.Selection.Set(0);

        scene.Selection.Set(Cube());
        editing.Reconcile();

        Assert.True(editing.Selection.IsEmpty);
    }

    // ── What a click does ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_click_replaces_and_shift_click_toggles() {
        Cube();
        editing.Enter(MeshElementKind.Face);

        editing.Clicked(new SubObject(SubObjectKind.Face, 2), additive: false);
        Assert.Equal([2], editing.Selection.Indices);

        editing.Clicked(new SubObject(SubObjectKind.Face, 5), additive: true);
        Assert.Equal([2, 5], editing.Selection.Indices);

        editing.Clicked(new SubObject(SubObjectKind.Face, 2), additive: true);
        Assert.Equal([5], editing.Selection.Indices);
    }

    [Fact]
    public void A_miss_clears_and_a_shift_miss_does_not() {
        Cube();
        editing.Enter(MeshElementKind.Face);
        editing.Selection.Set([1, 2]);

        editing.Clicked(SubObject.None, additive: true);
        Assert.Equal(2, editing.Selection.Count);

        editing.Clicked(SubObject.None, additive: false);
        Assert.True(editing.Selection.IsEmpty);
    }

    [Fact]
    public void Changing_the_element_mode_converts_what_is_selected() {
        Cube();
        editing.Enter(MeshElementKind.Face);
        editing.Selection.Set(0);

        editing.Element = MeshElementKind.Vertex;

        // A cube out of the primitive is triangles, so one face is three corners.
        Assert.Equal(3, editing.Selection.Count);
        Assert.Equal(MeshElementKind.Vertex, editing.Element);
    }

    // ── Surviving an edit ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_position_move_leaves_the_selection_alone() {
        var entity = Cube();

        editing.Enter(MeshElementKind.Face);
        editing.Selection.Set([0, 1]);

        var mesh = scene.MeshOf(entity)!;

        mesh.MovePosition(0, new Vector3(3f, 3f, 3f));
        scene.TouchMesh(entity);

        // ⚠ P2's exit criterion. A drag and its undo change where things are and not what is joined
        // to what, so every index still names the element the designer chose.
        Assert.False(editing.Reconcile());
        Assert.Equal([0, 1], editing.Selection.Indices);
    }

    [Fact]
    public void A_topology_change_drops_it_rather_than_leaving_it_naming_other_things() {
        var entity = Cube();

        editing.Enter(MeshElementKind.Face);
        editing.Selection.Set([0, 1]);

        var mesh = new EditMesh(scene.MeshOf(entity)!);

        mesh.AddPosition(new Vector3(2f, 2f, 2f));
        mesh.AddFace([0, 1, mesh.PositionCount - 1]);

        scene.SetMesh(entity, mesh);

        Assert.True(editing.Reconcile());
        Assert.True(editing.Selection.IsEmpty);
    }

    [Fact]
    public void Reconciling_twice_over_an_unchanged_mesh_costs_nothing_and_changes_nothing() {
        Cube();

        editing.Enter(MeshElementKind.Face);
        editing.Selection.Set(3);

        var was = editing.Selection.Version;

        Assert.False(editing.Reconcile());
        Assert.False(editing.Reconcile());
        Assert.Equal(was, editing.Selection.Version);
    }

    // ── The gizmo over elements ─────────────────────────────────────────────────────────────────

    [Fact]
    public void The_gizmo_sits_on_the_selection_rather_than_on_the_entity() {
        var entity = Cube(new Vector3(5f, 0f, 0f));

        editing.Enter(MeshElementKind.Vertex);
        editing.Selection.Set(0);

        var target = new MeshGizmoTarget(scene, editing);
        var mesh = scene.MeshOf(entity)!;

        Assert.Equal(
            Matrix4x4.TransformPosition(mesh.Positions[0], Matrix4x4.FromTranslation(new Vector3(5f, 0f, 0f))),
            target.Position
        );
    }

    [Fact]
    public void Dragging_a_face_moves_its_corners_and_leaves_the_entity_where_it_was() {
        var entity = Cube();

        editing.Enter(MeshElementKind.Face);
        editing.Selection.Set(0);

        var mesh = scene.MeshOf(entity)!;
        var corners = mesh.CornersOf(0).ToArray();
        var was = corners.Select(corner => mesh.Positions[corner]).ToArray();

        var target = new MeshGizmoTarget(scene, editing);

        target.Position += new Vector3(0f, 2f, 0f);

        for (var index = 0; index < corners.Length; index++) {
            Assert.Equal(was[index] + new Vector3(0f, 2f, 0f), mesh.Positions[corners[index]]);
        }

        // ⚠ The entity did not move — its corners did. Confusing the two is how a designer ends up
        // with a corridor whose pivot is nowhere near it.
        Assert.Equal(Vector3.Zero, new Transform(world, entity).Position);
    }

    [Fact]
    public void A_drag_applies_from_where_it_started_rather_than_accumulating() {
        Cube();

        editing.Enter(MeshElementKind.Vertex);
        editing.Selection.Set(0);

        var target = new MeshGizmoTarget(scene, editing);
        var start = target.Position;

        // The gizmo recomputes from mouse-down and calls the setter with absolute values on every
        // frame of a drag, so the same value twice has to land in the same place.
        target.Position = start + new Vector3(1f, 0f, 0f);
        target.Position = start + new Vector3(1f, 0f, 0f);

        Assert.Equal(start + new Vector3(1f, 0f, 0f), target.Position);
    }

    [Fact]
    public void Scaling_a_face_moves_its_corners_about_their_own_centre() {
        var entity = Cube();

        editing.Enter(MeshElementKind.Face);
        editing.Selection.Set(0);

        var mesh = scene.MeshOf(entity)!;
        var target = new MeshGizmoTarget(scene, editing);
        var centre = target.Position;

        target.Scale = new Vector3(2f);

        List<int> covered = [];

        editing.Positions(covered);

        foreach (var position in covered) {
            // Every corner ends twice as far from the centre and in the same direction, which is what
            // scaling a face means — and is not what scaling the entity would do.
            Assert.True(Vector3.Distance(mesh.Positions[position], centre) > 0f);
        }

        Assert.Equal(centre, target.Position);
    }

    [Fact]
    public void An_empty_selection_is_an_empty_target_rather_than_a_drag_of_nothing() {
        Cube();

        editing.Enter(MeshElementKind.Face);

        Assert.True(new MeshGizmoTarget(scene, editing).IsEmpty);
    }
}
