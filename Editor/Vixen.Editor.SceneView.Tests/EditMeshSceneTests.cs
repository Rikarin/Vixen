// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Editor.Core.Scenes;
using Vixen.Engine.Transforms;
using Vixen.Geometry;
using Vixen.Rendering;
using Xunit;

namespace Vixen.Editor.SceneView.Tests;

/// <summary>Doc 24's P1: a cube in a scene is an <c>EditMesh</c>, and it survives being saved.</summary>
public class EditMeshSceneTests : IDisposable {
    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-editmesh-" + Guid.NewGuid().ToString("N"));
    readonly EditorProject project;
    readonly World world = new("Test");
    readonly SceneDocument scene;

    public EditMeshSceneTests() {
        Directory.CreateDirectory(root);

        project = new(new ProjectPaths(root));
        scene = new(project, world, AssetId.Empty, "Untitled");
    }

    public void Dispose() {
        world.Dispose();

        if (Directory.Exists(root)) {
            Directory.Delete(root, true);
        }

        GC.SuppressFinalize(this);
    }

    Entity Cube() {
        var entity = scene.CreateShape(PrimitiveKind.Cube, LocalTransform.Identity);

        scene.SetMesh(entity, EditMeshes.From(PrimitiveKind.Cube));

        return entity;
    }

    // ── The adapter ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_primitive_becomes_a_mesh_whose_positions_are_the_ones_you_can_drag() {
        var mesh = EditMeshes.From(PrimitiveKind.Cube);

        Assert.Equal(8, mesh.PositionCount);
        Assert.Equal(12, mesh.FaceCount);
        Assert.True(mesh.Validate().IsSolid, mesh.Validate().Describe() ?? "solid");
    }

    [Fact]
    public void Every_primitive_survives_the_trip_into_the_kernel() {
        foreach (var kind in Enum.GetValues<PrimitiveKind>()) {
            var mesh = EditMeshes.From(kind, 16, 8);
            var report = mesh.Validate();

            // ⚠ Not `IsSolid` for all of them. A plane and a quad are single-sided surfaces with a
            // rim, which is a fact about the primitive rather than a failure of the conversion — and a
            // test asserting otherwise would be one that has to be weakened the first time somebody
            // adds an open shape.
            Assert.True(report.IsManifold, $"{kind}: {report.Describe()}");
            Assert.True(report.IsConsistent, $"{kind}: {report.Describe()}");
            Assert.Empty(report.Degenerate);
            Assert.Equal(0, report.Orphans);
        }
    }

    [Fact]
    public void The_trip_out_is_one_vertex_per_corner_because_a_normal_belongs_to_one() {
        var mesh = EditMeshes.From(PrimitiveKind.Cube);
        var data = mesh.ToMeshData("Cube");

        // ⚠ Not eight. A cube drawn from eight shared vertices is a cube lit as a very lumpy sphere:
        // a corner has three normals and the drawing structure is where they live.
        Assert.Equal(mesh.CornerCount, data.VertexCount);
        Assert.Equal(mesh.FaceCount, data.TriangleCount);

        Assert.Equal(new Vector3(-0.5f), data.Bounds.Minimum);
        Assert.Equal(new Vector3(0.5f), data.Bounds.Maximum);

        // Six distinct normals for six sides, which is what flat shading means and what a block-out
        // wants. A converted sphere comes out faceted; smoothing groups are doc 24's P5.
        var facing = new HashSet<Vector3>(data.Normals);

        Assert.Equal(6, facing.Count);
    }

    // ── Storage ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void An_entity_carries_a_mesh_and_can_be_given_none() {
        var entity = Cube();

        Assert.True(scene.HasMesh(entity));
        Assert.NotNull(scene.MeshOf(entity));
        Assert.Single(scene.Meshes);

        scene.SetMesh(entity, null);

        Assert.False(scene.HasMesh(entity));
        Assert.Null(scene.MeshOf(entity));
    }

    [Fact]
    public void Reading_a_mesh_gives_the_mesh_rather_than_a_copy() {
        var entity = Cube();

        scene.MeshOf(entity)!.MovePosition(0, new Vector3(9f, 9f, 9f));

        // ⚠ Editing is what this is for, and a copy per read would make every drag allocate a mesh.
        // What takes copies is the undo command, once per edit — doc 24's D3.
        Assert.Equal(new Vector3(9f, 9f, 9f), scene.MeshOf(entity)!.Positions[0]);
    }

    // ── The scene format ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_mesh_saves_reloads_and_re_saves_to_identical_bytes() {
        Cube();

        var first = SceneSerializer.ToYaml(scene);

        // ⚠ Doc 24's P1 says this is where the phase can go wrong quietly: "a vertex list written at
        // whatever `float.ToString` gives makes every scene a merge conflict with itself". The format
        // already answered it for a transform, and a mesh made of `Vector3`s inherits the answer —
        // but the test has to arrive with the writer rather than after it.
        Assert.Contains("mesh:", first, StringComparison.Ordinal);

        using var reopened = new World("Reopened");
        var second = new SceneDocument(project, reopened, AssetId.Empty, "Untitled");

        SceneSerializer.Load(second, SceneSerializer.FromYaml(first));

        Assert.Equal(first, SceneSerializer.ToYaml(second));
    }

    [Fact]
    public void A_reloaded_mesh_is_the_same_mesh() {
        var entity = Cube();

        scene.MeshOf(entity)!.MovePosition(0, new Vector3(0.25f, 1.5f, -3.125f));
        scene.MeshOf(entity)!.SetGroup(0, 4);

        var yaml = SceneSerializer.ToYaml(scene);

        using var reopened = new World("Reopened");
        var second = new SceneDocument(project, reopened, AssetId.Empty, "Untitled");

        SceneSerializer.Load(second, SceneSerializer.FromYaml(yaml));

        var mesh = second.Meshes.Values.Single();
        var original = scene.MeshOf(entity)!;

        Assert.Equal(original.PositionCount, mesh.PositionCount);
        Assert.Equal(original.FaceCount, mesh.FaceCount);
        Assert.Equal(original.CornerCount, mesh.CornerCount);

        // Exactly, not nearly. The positions went through the registered `Vector3` converter, which
        // writes at round-trip precision.
        Assert.Equal(new Vector3(0.25f, 1.5f, -3.125f), mesh.Positions[0]);
        Assert.Equal(4, mesh.Faces[0].Group);

        Assert.Equal(original.Edges.Count, mesh.Edges.Count);
    }

    [Fact]
    public void A_scene_with_no_mesh_in_it_carries_none() {
        scene.CreateShape(PrimitiveKind.Sphere, LocalTransform.Identity);

        var data = SceneSerializer.ToFile(scene);

        Assert.Null(data.Roots[0].Mesh);
    }

    [Fact]
    public void A_mesh_record_that_does_not_add_up_loses_the_face_and_not_the_scene() {
        var data = new SceneMeshData {
            Positions = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
            Corners = [0, 1, 2],
            Faces = [3, 3],
            Groups = [0, 0]
        };

        // ⚠ The second face runs off the end of the corner list, which is what a bad merge leaves
        // behind. An editor that refused to open the scene would lose the ninety-nine entities that
        // were fine; the face that could not be read is dropped, which is visible.
        var mesh = EditMeshes.FromSceneData(data);

        Assert.NotNull(mesh);
        Assert.Equal(1, mesh!.FaceCount);
    }

    // ── The command ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Moving_one_vertex_is_undoable() {
        var entity = Cube();
        var mesh = scene.MeshOf(entity)!;

        var was = mesh.Positions[0];
        var now = was + new Vector3(1f, 0f, 0f);

        mesh.MovePosition(0, now);

        scene.Stack.Execute(EditMeshCommand.Moved(scene, entity, [0], [was]));

        Assert.Equal(now, scene.MeshOf(entity)!.Positions[0]);

        scene.Stack.Undo();
        Assert.Equal(was, scene.MeshOf(entity)!.Positions[0]);

        scene.Stack.Redo();
        Assert.Equal(now, scene.MeshOf(entity)!.Positions[0]);
    }

    [Fact]
    public void A_drag_over_many_frames_is_one_entry_in_the_history() {
        var entity = Cube();
        var mesh = scene.MeshOf(entity)!;

        var was = mesh.Positions[0];

        for (var frame = 1; frame <= 5; frame++) {
            var before = mesh.Positions[0];

            mesh.MovePosition(0, was + new Vector3(frame, 0f, 0f));
            scene.Stack.Execute(EditMeshCommand.Moved(scene, entity, [0], [before]));
        }

        // One for the mesh, and the entity's own Create Entity underneath it — `CreateShape` is an
        // undoable command too, which is what makes this fixture the one the editor actually has.
        Assert.Equal(2, scene.Stack.History.Count);

        // ⚠ And it undoes to where the *first* of them started rather than to one frame ago, which is
        // the half of merging that is easy to get wrong.
        scene.Stack.Undo();
        Assert.Equal(was, scene.MeshOf(entity)!.Positions[0]);
    }

    [Fact]
    public void A_topology_change_records_the_whole_mesh_and_does_not_merge() {
        var entity = Cube();
        var was = new EditMesh(scene.MeshOf(entity)!);

        // Standing in for an extrude: the structure changes, so there is no inverse to record.
        var edited = new EditMesh(was);

        var a = edited.AddPosition(new Vector3(2f, 0f, 0f));
        var b = edited.AddPosition(new Vector3(3f, 0f, 0f));
        var c = edited.AddPosition(new Vector3(2f, 1f, 0f));

        edited.AddFace([a, b, c]);
        scene.SetMesh(entity, edited);

        scene.Stack.Execute(EditMeshCommand.Rebuilt(scene, entity, was, "Extrude"));
        scene.Stack.Execute(EditMeshCommand.Rebuilt(scene, entity, edited, "Extrude"));

        // ⚠ Two entries, not one — three with the Create Entity the shape came with. Merging two
        // topology changes would throw away the middle state of a sequence whose steps are not
        // individually reversible.
        Assert.Equal(3, scene.Stack.History.Count);

        scene.Stack.Undo();
        scene.Stack.Undo();

        Assert.Equal(12, scene.MeshOf(entity)!.FaceCount);

        scene.Stack.Redo();
        Assert.Equal(13, scene.MeshOf(entity)!.FaceCount);
    }

    [Fact]
    public void An_undone_topology_change_is_not_disturbed_by_the_edit_that_followed_it() {
        var entity = Cube();
        var was = scene.MeshOf(entity)!;

        var command = EditMeshCommand.Rebuilt(scene, entity, was, "Edit Mesh");

        scene.Stack.Execute(command);

        // The live mesh keeps being edited afterwards. A command holding *that object* rather than a
        // copy would record a "before" that changes under it — an undo that puts things back where
        // they already are, which is the mistake the randomised do/undo/redo suite exists to catch.
        scene.MeshOf(entity)!.MovePosition(0, new Vector3(9f, 9f, 9f));

        scene.Stack.Undo();

        Assert.NotEqual(new Vector3(9f, 9f, 9f), scene.MeshOf(entity)!.Positions[0]);
    }

    [Fact]
    public void Adding_and_removing_a_mesh_are_both_undoable() {
        var entity = scene.CreateShape(PrimitiveKind.Cube, LocalTransform.Identity);

        scene.SetMesh(entity, EditMeshes.From(PrimitiveKind.Cube));
        scene.Stack.Execute(EditMeshCommand.Rebuilt(scene, entity, null, "Make Editable"));

        Assert.True(scene.HasMesh(entity));

        scene.Stack.Undo();
        Assert.False(scene.HasMesh(entity));

        scene.Stack.Redo();
        Assert.True(scene.HasMesh(entity));
    }
}
