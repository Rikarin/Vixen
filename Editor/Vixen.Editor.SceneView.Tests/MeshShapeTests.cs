// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Editor.Core.Scenes;
using Vixen.Engine.Transforms;
using Vixen.Rendering;
using Xunit;

namespace Vixen.Editor.SceneView.Tests;

/// <summary>Entities that are drawn as a shape: making them, undoing that, and drawing them.</summary>
public class MeshShapeTests : IDisposable {
    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-shapes-" + Guid.NewGuid().ToString("N"));
    readonly EditorProject project;
    readonly World world = new("Test");
    readonly SceneDocument scene;

    public MeshShapeTests() {
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

    [Fact]
    public void A_created_shape_is_named_after_itself_and_carries_the_kind() {
        var cube = scene.CreateShape(PrimitiveKind.Cube, LocalTransform.Identity);

        Assert.Equal("Cube", scene.NameOf(cube));
        Assert.True(MeshShapes.TryGet(world, cube, out var kind));
        Assert.Equal(PrimitiveKind.Cube, kind);
    }

    [Fact]
    public void Creating_one_is_undoable_and_the_shape_comes_back_with_it() {
        var sphere = scene.CreateShape(PrimitiveKind.Sphere, LocalTransform.Identity);

        Assert.True(scene.Stack.Undo());
        Assert.False(world.IsAlive(sphere));

        Assert.True(scene.Stack.Redo());

        // ⚠ The redo restores a snapshot rather than running the initialiser again, so this is the
        // assertion that says the snapshot carries components the create command added — a redo that
        // brought the entity back without its shape would be an invisible cube in the hierarchy.
        Assert.True(MeshShapes.TryGet(world, sphere, out var kind));
        Assert.Equal(PrimitiveKind.Sphere, kind);
    }

    [Fact]
    public void Deleting_one_and_undoing_it_keeps_the_shape() {
        var torus = scene.CreateShape(PrimitiveKind.Torus, LocalTransform.Identity);

        Assert.True(scene.Delete([torus]));
        Assert.True(scene.Stack.Undo());

        Assert.True(MeshShapes.TryGet(world, torus, out var kind));
        Assert.Equal(PrimitiveKind.Torus, kind);
    }

    [Fact]
    public void An_entity_with_no_shape_has_none() {
        Assert.False(MeshShapes.TryGet(world, scene.Add("Empty", LocalTransform.Identity), out _));
    }

    [Fact]
    public void Attaching_twice_changes_the_shape_rather_than_adding_a_second_one() {
        var entity = scene.Add("Thing", LocalTransform.Identity);

        MeshShapes.Attach(world, entity, PrimitiveKind.Cube);
        MeshShapes.Attach(world, entity, PrimitiveKind.Cone);

        Assert.True(MeshShapes.TryGet(world, entity, out var kind));
        Assert.Equal(PrimitiveKind.Cone, kind);
    }

    [Fact]
    public void A_name_the_editor_does_not_know_is_not_a_shape() {
        Assert.False(MeshShapes.TryParse(null, out _));
        Assert.False(MeshShapes.TryParse("   ", out _));
        Assert.False(MeshShapes.TryParse("Dodecahedron", out _));

        // Case-insensitive, because a hand-edited file is the case this has to survive.
        Assert.True(MeshShapes.TryParse("cylinder", out var kind));
        Assert.Equal(PrimitiveKind.Cylinder, kind);
    }

    [Fact]
    public void Every_shape_in_the_menu_round_trips_through_its_name() {
        foreach (var kind in MeshShapes.All) {
            Assert.True(MeshShapes.TryParse(MeshShapes.NameOf(kind), out var parsed));
            Assert.Equal(kind, parsed);
        }
    }

    [Fact]
    public void The_menu_offers_every_kind_exactly_once() {
        // A shape missing from the list is one nobody can spawn; one listed twice is two menu lines
        // that do the same thing and two commands registered under the same id.
        Assert.Equal(Enum.GetValues<PrimitiveKind>().Length, MeshShapes.All.Count);
        Assert.Equal(MeshShapes.All.Count, MeshShapes.All.Distinct().Count());
    }

    [Fact]
    public void A_shape_survives_a_file_round_trip() {
        scene.CreateShape(PrimitiveKind.Capsule, LocalTransform.At(new Vector3(1f, 2f, 3f)));

        var yaml = SceneSerializer.ToYaml(scene);
        Assert.Contains("Capsule", yaml, StringComparison.Ordinal);

        using var other = new World("Reloaded");
        var reloaded = new SceneDocument(project, other, AssetId.Empty, "Untitled");

        SceneSerializer.Load(reloaded, SceneSerializer.FromYaml(yaml));

        var entity = Assert.Single(reloaded.Roots);

        Assert.True(MeshShapes.TryGet(other, entity, out var kind));
        Assert.Equal(PrimitiveKind.Capsule, kind);
    }

    [Fact]
    public void An_entity_with_no_shape_gains_none_on_the_way_back() {
        scene.Add("Empty", LocalTransform.Identity);

        using var other = new World("Reloaded");
        var reloaded = new SceneDocument(project, other, AssetId.Empty, "Untitled");

        SceneSerializer.Load(reloaded, SceneSerializer.FromYaml(SceneSerializer.ToYaml(scene)));

        Assert.False(MeshShapes.TryGet(other, Assert.Single(reloaded.Roots), out _));
    }

    [Fact]
    public void A_shape_the_file_names_and_this_editor_does_not_leaves_the_entity_in_place() {
        var file = new SceneFile();
        file.Roots.Add(new SceneEntityData { Name = "From the future", Shape = "Dodecahedron" });

        using var other = new World("Reloaded");
        var reloaded = new SceneDocument(project, other, AssetId.Empty, "Untitled");

        // ⚠ Opened, minus the geometry. Refusing the whole file would make every shape added to a
        // newer editor a flag day for everyone who has not updated.
        Assert.Equal(1, SceneSerializer.Load(reloaded, file));
        Assert.False(MeshShapes.TryGet(other, Assert.Single(reloaded.Roots), out _));
    }

    [Fact]
    public void Only_shaped_entities_are_drawn() {
        scene.Add("Empty", LocalTransform.Identity);
        scene.CreateShape(PrimitiveKind.Cube, LocalTransform.Identity);

        var meshes = new SceneMeshes();

        Assert.Equal(1, meshes.Build(scene));
        Assert.Equal(MeshPrimitives.Cube().VertexCount, meshes.Vertices.Length);
    }

    [Fact]
    public void A_scene_with_nothing_in_it_draws_nothing() {
        var meshes = new SceneMeshes();

        Assert.Equal(0, meshes.Build(scene));
        Assert.True(meshes.Vertices.IsEmpty);
        Assert.True(meshes.Indices.IsEmpty);
    }

    [Fact]
    public void Two_shapes_go_into_one_buffer_with_their_indices_offset() {
        scene.CreateShape(PrimitiveKind.Cube, LocalTransform.Identity);
        scene.CreateShape(PrimitiveKind.Cube, LocalTransform.At(new Vector3(3f, 0f, 0f)));

        var meshes = new SceneMeshes();
        meshes.Build(scene);

        var cube = MeshPrimitives.Cube();

        Assert.Equal(cube.VertexCount * 2, meshes.Vertices.Length);
        Assert.Equal(cube.Indices.Length * 2, meshes.Indices.Length);

        // ⚠ The second shape's indices have to be pushed past the first shape's vertices. Without
        // the offset both cubes draw on top of each other at the first one's position, which looks
        // like the second one simply not having been created.
        var highest = 0u;

        foreach (var index in meshes.Indices) {
            highest = Math.Max(highest, index);
        }

        Assert.Equal((uint) (cube.VertexCount * 2) - 1, highest);
    }

    [Fact]
    public void A_shape_is_drawn_where_its_transform_says() {
        scene.CreateShape(PrimitiveKind.Cube, LocalTransform.At(new Vector3(10f, 0f, 0f)));

        var meshes = new SceneMeshes();
        meshes.Build(scene);

        foreach (var vertex in meshes.Vertices) {
            Assert.InRange(vertex.Position.X, 9.4f, 10.6f);
        }
    }

    [Fact]
    public void A_non_uniform_scale_leaves_the_normals_perpendicular_to_their_faces() {
        scene.CreateShape(
            PrimitiveKind.Cube,
            new LocalTransform {
                Position = Vector3.Zero,
                Rotation = Quaternion.Identity,
                Scale = new Vector3(4f, 1f, 1f)
            }
        );

        var meshes = new SceneMeshes();
        meshes.Build(scene);

        // A cube's normals are axis-aligned and stay so under an axis-aligned scale — but only if
        // they go through the inverse transpose. Through the matrix itself the ±X faces come out
        // four times as long and, once normalised, still axis-aligned; the test that catches it is
        // that every normal is *unit* and axis-aligned, which the matrix path breaks for the
        // diagonal of any rotated shape. Keeping the cube axis-aligned makes the assertion exact.
        foreach (var vertex in meshes.Vertices) {
            Assert.Equal(1f, vertex.Normal.Length(), 3);

            var axes = MathF.Abs(vertex.Normal.X) + MathF.Abs(vertex.Normal.Y) + MathF.Abs(vertex.Normal.Z);
            Assert.Equal(1f, axes, 3);
        }
    }

    [Fact]
    public void A_selected_shape_is_a_different_colour() {
        var cube = scene.CreateShape(PrimitiveKind.Cube, LocalTransform.Identity);
        var meshes = new SceneMeshes();

        meshes.Build(scene);
        var plain = meshes.Vertices[0].Colour;

        scene.Selection.Set(cube);
        meshes.Build(scene);

        // ⚠ Shown by colour rather than only by the gizmo sitting on it, for the reason SceneLines
        // gives: with several things selected the gizmo is at one place and the rest have nothing
        // saying they are about to move.
        Assert.NotEqual(plain, meshes.Vertices[0].Colour);
        Assert.Equal(meshes.SelectedColour, meshes.Vertices[0].Colour);
    }
}
