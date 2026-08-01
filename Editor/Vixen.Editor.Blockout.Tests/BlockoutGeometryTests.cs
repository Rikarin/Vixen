// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Editor.SceneView;
using Vixen.Engine.Transforms;
using Vixen.Geometry;
using Vixen.Rendering;
using Xunit;

namespace Vixen.Editor.Blockout.Tests;

/// <summary>Doc 24's P3 verbs, run against a scene: one undo entry each, and the result selected.</summary>
public class BlockoutGeometryTests : IDisposable {
    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-geometry-" + Guid.NewGuid().ToString("N"));
    readonly EditorProject project;
    readonly World world = new("Test");
    readonly SceneDocument scene;
    readonly MeshEdit editing;
    readonly TransformSystem transforms = new();

    public BlockoutGeometryTests() {
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

    Entity Cube(Vector3 position = default, float scale = 1f) {
        var entity = scene.CreateShape(
            PrimitiveKind.Cube,
            new LocalTransform { Position = position, Rotation = Quaternion.Identity, Scale = new(scale) }
        );

        transforms.Resolve(world);
        world.AdvanceVersion();

        scene.Selection.Set(entity);
        editing.Enter(MeshElementKind.Face);

        return entity;
    }

    static void AssertSound(EditMesh mesh) {
        foreach (var corner in mesh.Corners) {
            Assert.InRange(corner, 0, mesh.PositionCount - 1);
        }

        Assert.All(mesh.Faces, face => Assert.True(face.Count >= 3));

        for (var edge = 0; edge < mesh.Edges.Count; edge++) {
            Assert.NotEmpty(mesh.FacesOf(edge).ToArray());
        }
    }

    // ── Extrude, the one every other verb is judged against ─────────────────────────────────────

    [Fact]
    public void Extruding_a_face_grows_the_mesh_and_leaves_the_new_face_selected() {
        var entity = Cube();
        var mesh = scene.MeshOf(entity)!;
        var was = mesh.FaceCount;

        editing.Selection.Set(0);

        Assert.True(BlockoutGeometry.Extrude(editing, 2f));

        var after = scene.MeshOf(entity)!;

        Assert.True(after.FaceCount > was);

        // ⚠ Extruding a face and then moving it is one gesture in every modelling tool there is; a
        // verb that left the original selection would make the second half act on what the first
        // half left behind.
        Assert.Equal(MeshElementKind.Face, editing.Element);
        Assert.Single(editing.Selection.Indices);

        AssertSound(after);
    }

    [Fact]
    public void An_extrude_is_one_entry_in_the_history_and_undoes_whole() {
        var entity = Cube();
        var was = scene.MeshOf(entity)!.FaceCount;
        var entries = scene.Stack.History.Count;

        editing.Selection.Set(0);
        BlockoutGeometry.Extrude(editing, 2f);

        Assert.Equal(entries + 1, scene.Stack.History.Count);

        scene.Stack.Undo();

        // ⚠ D3: a topology change records the whole mesh, because a boolean has no inverse and an
        // undo implemented as an inverse operation is a second implementation of every verb.
        Assert.Equal(was, scene.MeshOf(entity)!.FaceCount);
    }

    [Fact]
    public void Extruding_with_nothing_selected_does_nothing() {
        Cube();

        // Two entries already: creating the entity, and making it editable. Neither is this verb's.
        var entries = scene.Stack.History.Count;

        Assert.False(BlockoutGeometry.Extrude(editing, 1f));
        Assert.Equal(entries, scene.Stack.History.Count);
    }

    [Fact]
    public void An_amount_in_the_scene_is_taken_into_the_meshs_own_space() {
        Cube(scale: 0.5f);

        // ⚠ An extrude of one metre on an entity scaled to a half is two units in the mesh. A verb
        // given the world number would move it half as far as the pointer said — invisible until
        // somebody extrudes a wall that had been scaled.
        var local = BlockoutGeometry.Local(editing, new Vector3(0f, 1f, 0f));

        Assert.Equal(2f, local.Y, 3);
    }

    // ── The rest of the table ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Insetting_leaves_a_smaller_face_inside_a_ring() {
        var entity = Cube();

        editing.Selection.Set(0);
        var area = scene.MeshOf(entity)!.Area(0);

        Assert.True(BlockoutGeometry.Inset(editing, 0.1f));

        var after = scene.MeshOf(entity)!;

        Assert.All(editing.Selection.Indices, face => Assert.True(after.Area(face) < area));
        AssertSound(after);
    }

    [Fact]
    public void Bevelling_reports_the_corners_it_could_not_resolve() {
        var entity = Cube();

        editing.Element = MeshElementKind.Edge;
        editing.Selection.All(scene.MeshOf(entity)!);

        // Every edge of a cube at once, which is every corner unresolved — the case doc 24 calls a
        // miniature research problem and asks to be reported rather than silently produced.
        Assert.True(BlockoutGeometry.Bevel(editing, 0.05f, 1, out var unresolved));
        Assert.True(unresolved > 0);

        AssertSound(scene.MeshOf(entity)!);
    }

    [Fact]
    public void Subdividing_a_face_makes_more_of_them() {
        var entity = Cube();
        var was = scene.MeshOf(entity)!.FaceCount;

        editing.Selection.Set(0);

        Assert.True(BlockoutGeometry.Subdivide(editing));
        Assert.True(scene.MeshOf(entity)!.FaceCount > was);
    }

    [Fact]
    public void Deleting_a_face_leaves_a_hole_that_filling_closes() {
        var entity = Cube();

        editing.Selection.Set(0);
        Assert.True(BlockoutGeometry.Delete(editing));

        var opened = scene.MeshOf(entity)!;

        Assert.False(opened.Validate().IsClosed);

        editing.Element = MeshElementKind.Edge;
        editing.Selection.All(opened);

        Assert.True(BlockoutGeometry.FillHole(editing));
        Assert.True(scene.MeshOf(entity)!.Validate().IsClosed);
    }

    [Fact]
    public void Flipping_the_whole_mesh_is_reported_as_consistent_again() {
        var entity = Cube();

        editing.Selection.All(scene.MeshOf(entity)!);

        Assert.True(BlockoutGeometry.Flip(editing));
        Assert.Empty(scene.MeshOf(entity)!.Validate().Reversed);
    }

    [Fact]
    public void Welding_two_corners_merges_them() {
        var entity = Cube();
        var mesh = scene.MeshOf(entity)!;

        // Two corners of one face, so the face they are both on has to collapse to a triangle.
        var corners = mesh.CornersOf(0).ToArray();

        editing.Element = MeshElementKind.Vertex;
        editing.Selection.Set([corners[0], corners[1]]);

        Assert.True(BlockoutGeometry.Weld(editing));

        var after = scene.MeshOf(entity)!;

        Assert.True(after.PositionCount <= mesh.PositionCount);
        Assert.True(after.FaceCount < 12, $"a face should have collapsed: {after.FaceCount}");
        AssertSound(after);
    }

    [Fact]
    public void Dissolving_the_diagonals_turns_a_triangulated_cube_into_quads() {
        var entity = Cube();
        var mesh = scene.MeshOf(entity)!;

        Assert.Equal(12, mesh.FaceCount);

        editing.Element = MeshElementKind.Edge;
        editing.Selection.All(mesh);

        Assert.True(BlockoutGeometry.Dissolve(editing));

        // ⚠ Dissolve removes an element and keeps the surface — which is how a block-out made of
        // triangles becomes one made of quads, and is what makes loops and rings work on it.
        var after = scene.MeshOf(entity)!;

        Assert.True(after.FaceCount < 12);
        AssertSound(after);
    }

    // ── Whole meshes ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Detaching_faces_makes_an_entity_and_both_halves_undo_together() {
        var entity = Cube();
        var was = scene.MeshOf(entity)!.FaceCount;
        var entries = scene.Stack.History.Count;

        editing.Selection.Set([0, 1]);

        var taken = BlockoutGeometry.Detach(editing, "Wall");

        Assert.NotNull(taken);
        Assert.True(scene.HasMesh(taken.Value));
        Assert.Equal(was - 2, scene.MeshOf(entity)!.FaceCount);

        // ⚠ One entry, because it is one act. Undoing half of it is a scene with the same geometry
        // twice or with none of it.
        Assert.Equal(entries + 1, scene.Stack.History.Count);

        scene.Stack.Undo();

        Assert.Equal(was, scene.MeshOf(entity)!.FaceCount);
        Assert.False(world.IsAlive(taken.Value));
    }

    [Fact]
    public void Merging_another_mesh_in_brings_its_faces_and_takes_its_entity() {
        var first = Cube();
        var second = Cube(new Vector3(4f, 0f, 0f));

        scene.Selection.Set(first);
        editing.Enter(MeshElementKind.Face);

        var was = scene.MeshOf(first)!.FaceCount;

        Assert.Equal(1, BlockoutGeometry.Merge(editing, [second]));
        Assert.Equal(was * 2, scene.MeshOf(first)!.FaceCount);
        Assert.False(world.IsAlive(second));

        AssertSound(scene.MeshOf(first)!);
    }

    [Fact]
    public void Every_verb_declines_quietly_when_there_is_nothing_to_act_on() {
        // No target, no mesh, no selection — each of these is a key press somebody made.
        Assert.False(BlockoutGeometry.Extrude(editing, 1f));
        Assert.False(BlockoutGeometry.Inset(editing, 1f));
        Assert.False(BlockoutGeometry.Bevel(editing, 1f, 1, out _));
        Assert.False(BlockoutGeometry.LoopCut(editing));
        Assert.False(BlockoutGeometry.Subdivide(editing));
        Assert.False(BlockoutGeometry.Bridge(editing));
        Assert.False(BlockoutGeometry.FillHole(editing));
        Assert.False(BlockoutGeometry.Flip(editing));
        Assert.False(BlockoutGeometry.Weld(editing));
        Assert.False(BlockoutGeometry.Dissolve(editing));
        Assert.False(BlockoutGeometry.Delete(editing));
        Assert.Null(BlockoutGeometry.Detach(editing));
        Assert.Equal(0, BlockoutGeometry.Merge(editing, []));

        Assert.Empty(scene.Stack.History);
    }

    // ── The room doc 24's exit criterion asks for ───────────────────────────────────────────────

    [Fact]
    public void A_room_with_a_doorway_and_a_chamfered_edge_is_built_from_a_cube() {
        var entity = Cube(scale: 4f);
        var mesh = scene.MeshOf(entity)!;

        // Hollow it: turn the box inside out so its faces are walls seen from within.
        editing.Selection.All(mesh);
        Assert.True(BlockoutGeometry.Flip(editing));

        // A doorway: inset one wall and push the middle through it.
        editing.Selection.Set(0);
        Assert.True(BlockoutGeometry.Inset(editing, 0.2f));
        Assert.True(BlockoutGeometry.Extrude(editing, -0.5f));

        // A window: the same, on another wall.
        editing.Selection.Set(4);
        Assert.True(BlockoutGeometry.Inset(editing, 0.3f));
        Assert.True(BlockoutGeometry.Extrude(editing, -0.3f));

        // And a chamfer on an edge of it.
        editing.Element = MeshElementKind.Edge;
        editing.Selection.Set(0);
        Assert.True(BlockoutGeometry.Bevel(editing, 0.1f, 1, out _));

        var built = scene.MeshOf(entity)!;

        AssertSound(built);

        // ⚠ Every step round-trips: doc 24's P3 exit asks for the room to survive the file, and a
        // topology the writer cannot express is one a designer loses on save.
        var data = built.ToSceneData();
        var read = EditMeshes.FromSceneData(data);

        Assert.NotNull(read);
        Assert.Equal(built.FaceCount, read.FaceCount);
        Assert.Equal(built.PositionCount, read.PositionCount);

        // And every one of them is one entry, so the whole room undoes step by step.
        Assert.True(scene.Stack.History.Count >= 6);
    }
}
