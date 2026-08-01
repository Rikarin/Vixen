// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Editor.Core.Scenes;
using Vixen.Editor.SceneView;
using Vixen.Engine.Transforms;
using Vixen.Geometry;
using Xunit;

namespace Vixen.Editor.Blockout.Tests;

/// <summary>Doc 24's P5, editor side: what a face is made of, how it is mapped and how it is shaded.</summary>
public class BlockoutSurfaceTests : IDisposable {
    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-surfaces-" + Guid.NewGuid().ToString("N"));
    readonly EditorProject project;
    readonly World world = new("Test");
    readonly SceneDocument scene;
    readonly MeshEdit editing;
    readonly TransformSystem transforms = new();

    public BlockoutSurfaceTests() {
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

    Entity Editable(ShapeKind kind = ShapeKind.Box) {
        var entity = BlockoutCreate.Shape(scene, kind);

        transforms.Resolve(world);
        world.AdvanceVersion();

        scene.Selection.Set(entity);
        editing.Enter(MeshElementKind.Face);

        return entity;
    }

    [Fact]
    public void Assigning_a_material_takes_the_whole_group_and_survives_a_parameter_change() {
        var entity = Editable();
        var brick = new AssetReference(AssetId.New());

        editing.Selection.Set(0);

        Assert.Equal(1, BlockoutSurfaces.Assign(editing, brick));

        var group = scene.MeshOf(entity)!.Faces[0].Group;

        Assert.Equal(brick, scene.MaterialsOf(entity)[group]);

        // ⚠ Assigning does not demote, which is the one surface verb that does not: the assignment
        // lives beside the mesh rather than inside it, so regenerating the geometry leaves it alone.
        Assert.True(scene.IsParametric(entity));

        BlockoutCreate.Resize(scene, entity, scene.ShapeOf(entity)!.Value with { Size = new(9f, 2f, 2f) });

        Assert.Equal(brick, scene.MaterialsOf(entity)[group]);

        scene.Stack.Undo();
        scene.Stack.Undo();

        Assert.Empty(scene.MaterialsOf(entity));
    }

    [Fact]
    public void An_entity_with_materials_on_two_groups_is_drawn_as_two_pieces() {
        var entity = Editable();

        scene.SetMaterial(entity, MeshShapes.GroupTop, new AssetReference(AssetId.New()));

        transforms.Resolve(world);
        world.AdvanceVersion();

        var meshes = new SceneMeshes();

        meshes.Build(scene);

        // ⚠ A material is per instance in the viewport's shader, so two materials on one mesh are two
        // draws over two pieces of geometry. Six groups on a box, so six batches — and one for a mesh
        // whose groups nobody has dressed, which is what keeps an undressed block-out one upload.
        Assert.Equal(6, meshes.Batches.Count);
        Assert.All(meshes.Batches, batch => Assert.True(batch.Shape.IsEdit));

        scene.SetMaterial(entity, MeshShapes.GroupTop, AssetReference.Null);
        meshes.Build(scene);

        Assert.Single(meshes.Batches);
    }

    [Fact]
    public void Every_piece_of_a_split_mesh_together_is_the_whole_mesh() {
        var entity = Editable();

        scene.SetMaterial(entity, MeshShapes.GroupTop, new AssetReference(AssetId.New()));

        transforms.Resolve(world);
        world.AdvanceVersion();

        var meshes = new SceneMeshes();

        meshes.Build(scene);

        var whole = scene.MeshOf(entity)!.ToMeshData().TriangleCount;
        var pieces = meshes.Batches.Sum(batch => meshes.Shape(batch.Shape)?.TriangleCount ?? 0);

        Assert.Equal(whole, pieces);
    }

    [Fact]
    public void A_world_projection_maps_the_selected_faces_and_demotes() {
        var entity = Editable();

        editing.Selection.Set(0);

        Assert.True(BlockoutSurfaces.Project(editing, UvProjection.World));

        // A mapping written into the mesh's corner layer would be lost the next time a parameter
        // rebuilt it, so this one is the door closing.
        Assert.True(scene.IsPlainMesh(entity));

        var mesh = scene.MeshOf(entity)!;
        var face = mesh.Faces[0];

        Assert.NotEqual(Vector2.Zero, mesh.TexCoords[face.Start + 1]);

        // And the faces nobody chose keep the nothing they had.
        Assert.Equal(Vector2.Zero, mesh.TexCoords[mesh.Faces[1].Start]);
    }

    [Fact]
    public void Projecting_with_nothing_selected_maps_the_whole_object() {
        Editable();

        editing.Selection.Clear();

        // ⚠ Empty means everything here and nothing in `BlockoutGeometry`, and the asymmetry is the
        // point: "project the UVs" has a sensible whole-object reading and "extrude" does not.
        Assert.True(BlockoutSurfaces.Project(editing, UvProjection.Box));

        var mesh = editing.Mesh!;

        Assert.All(
            mesh.Faces,
            face => Assert.NotEqual(Vector2.Zero, mesh.TexCoords[face.Start] - mesh.TexCoords[face.Start + 2])
        );
    }

    [Fact]
    public void Auto_smoothing_a_cylinder_makes_its_wall_smooth_and_survives_the_file() {
        var entity = Editable(ShapeKind.Cylinder);

        editing.Selection.Clear();

        Assert.True(BlockoutSurfaces.AutoSmooth(editing));

        var mesh = scene.MeshOf(entity)!;

        Assert.Contains(mesh.Faces, face => face.Group == MeshShapes.GroupSide && face.Smoothing != 0);
        Assert.All(
            mesh.Faces.Where(face => face.Group == MeshShapes.GroupTop),
            face => Assert.Equal(0, face.Smoothing)
        );

        // ⚠ And it reaches the file. A verb whose result vanished on save is one people would
        // reasonably describe as not working.
        transforms.Resolve(world);
        world.AdvanceVersion();

        var yaml = SceneSerializer.ToYaml(scene);

        using var second = new World("Reload");

        var reopened = new SceneDocument(project, second, AssetId.Empty, "Untitled");

        SceneSerializer.Load(reopened, SceneFile.FromYaml(yaml));

        Assert.Contains(reopened.MeshOf(reopened.Roots.Single())!.Faces, face => face.Smoothing != 0);
        Assert.Equal(yaml, SceneSerializer.ToYaml(reopened));
    }

    [Fact]
    public void A_smoothed_mesh_hands_the_renderer_averaged_normals() {
        var entity = Editable(ShapeKind.Cylinder);

        editing.Selection.Clear();

        var faceted = scene.MeshOf(entity)!.ToMeshData();

        Assert.True(BlockoutSurfaces.AutoSmooth(editing));

        var smooth = scene.MeshOf(entity)!.ToMeshData();

        // The wall's corners moved off their face normals and the caps' did not, which is the whole
        // of what a smoothing group buys and is the fix for the faceted converted sphere `EditMeshes`
        // has been warning about since P1.
        Assert.NotEqual(faceted.Normals[0], smooth.Normals[0]);
        Assert.All(smooth.Normals, normal => Assert.Equal(1f, normal.Length(), 3));
    }

    [Fact]
    public void Hardening_takes_a_face_back_out_of_its_group() {
        Editable(ShapeKind.Cylinder);

        editing.Selection.Clear();

        Assert.True(BlockoutSurfaces.AutoSmooth(editing));

        editing.Selection.All(editing.Mesh!);

        Assert.True(BlockoutSurfaces.Smooth(editing, smooth: false));
        Assert.All(editing.Mesh!.Faces, face => Assert.Equal(0, face.Smoothing));
    }

    [Fact]
    public void A_new_face_group_is_what_makes_part_of_a_wall_a_different_material() {
        var entity = Editable();

        editing.Selection.Set(0);

        var was = scene.MeshOf(entity)!.Faces[0].Group;

        Assert.True(BlockoutSurfaces.Regroup(editing));

        var now = scene.MeshOf(entity)!.Faces[0].Group;

        Assert.NotEqual(was, now);
        Assert.Equal(6, scene.MeshOf(entity)!.Faces.Select(face => face.Group).Distinct().Count());
    }

    [Fact]
    public void A_materialled_group_round_trips_through_the_scene_file() {
        Editable();

        var brick = new AssetReference(AssetId.New());

        editing.Selection.Set(0);
        BlockoutSurfaces.Assign(editing, brick);

        transforms.Resolve(world);
        world.AdvanceVersion();

        var yaml = SceneSerializer.ToYaml(scene);

        using var second = new World("Reload");

        var reopened = new SceneDocument(project, second, AssetId.Empty, "Untitled");

        SceneSerializer.Load(reopened, SceneFile.FromYaml(yaml));

        var made = reopened.Roots.Single();

        Assert.Single(reopened.MaterialsOf(made));
        Assert.Equal(brick, reopened.MaterialsOf(made).Values.Single());
        Assert.Equal(yaml, SceneSerializer.ToYaml(reopened));

    }
}
