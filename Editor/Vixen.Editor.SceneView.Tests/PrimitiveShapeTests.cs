// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Editor.Core.Scenes;
using Vixen.Engine.Transforms;
using Vixen.Rendering;
using Vixen.Rendering.Ecs;
using Xunit;

namespace Vixen.Editor.SceneView.Tests;

/// <summary>Entities that are drawn as a shape: making them, undoing that, and drawing them.</summary>
public class PrimitiveShapeTests : IDisposable {
    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-shapes-" + Guid.NewGuid().ToString("N"));
    readonly EditorProject project;
    readonly World world = new("Test");
    readonly SceneDocument scene;

    public PrimitiveShapeTests() {
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
        Assert.True(PrimitiveShapes.TryGet(world, cube, out var kind));
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
        Assert.True(PrimitiveShapes.TryGet(world, sphere, out var kind));
        Assert.Equal(PrimitiveKind.Sphere, kind);
    }

    [Fact]
    public void Deleting_one_and_undoing_it_keeps_the_shape() {
        var torus = scene.CreateShape(PrimitiveKind.Torus, LocalTransform.Identity);

        Assert.True(scene.Delete([torus]));
        Assert.True(scene.Stack.Undo());

        Assert.True(PrimitiveShapes.TryGet(world, torus, out var kind));
        Assert.Equal(PrimitiveKind.Torus, kind);
    }

    [Fact]
    public void An_entity_with_no_shape_has_none() {
        Assert.False(PrimitiveShapes.TryGet(world, scene.Add("Empty", LocalTransform.Identity), out _));
    }

    [Fact]
    public void Attaching_twice_changes_the_shape_rather_than_adding_a_second_one() {
        var entity = scene.Add("Thing", LocalTransform.Identity);

        PrimitiveShapes.Attach(world, entity, PrimitiveKind.Cube);
        PrimitiveShapes.Attach(world, entity, PrimitiveKind.Cone);

        Assert.True(PrimitiveShapes.TryGet(world, entity, out var kind));
        Assert.Equal(PrimitiveKind.Cone, kind);
    }

    [Fact]
    public void A_name_the_editor_does_not_know_is_not_a_shape() {
        Assert.False(PrimitiveShapes.TryParse(null, out _));
        Assert.False(PrimitiveShapes.TryParse("   ", out _));
        Assert.False(PrimitiveShapes.TryParse("Dodecahedron", out _));

        // Case-insensitive, because a hand-edited file is the case this has to survive.
        Assert.True(PrimitiveShapes.TryParse("cylinder", out var kind));
        Assert.Equal(PrimitiveKind.Cylinder, kind);
    }

    [Fact]
    public void Every_shape_in_the_menu_round_trips_through_its_name() {
        foreach (var kind in PrimitiveShapes.All) {
            Assert.True(PrimitiveShapes.TryParse(PrimitiveShapes.NameOf(kind), out var parsed));
            Assert.Equal(kind, parsed);
        }
    }

    [Fact]
    public void The_menu_offers_every_kind_exactly_once() {
        // A shape missing from the list is one nobody can spawn; one listed twice is two menu lines
        // that do the same thing and two commands registered under the same id.
        Assert.Equal(Enum.GetValues<PrimitiveKind>().Length, PrimitiveShapes.All.Count);
        Assert.Equal(PrimitiveShapes.All.Count, PrimitiveShapes.All.Distinct().Count());
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

        Assert.True(PrimitiveShapes.TryGet(other, entity, out var kind));
        Assert.Equal(PrimitiveKind.Capsule, kind);
    }

    [Fact]
    public void An_entity_with_no_shape_gains_none_on_the_way_back() {
        scene.Add("Empty", LocalTransform.Identity);

        using var other = new World("Reloaded");
        var reloaded = new SceneDocument(project, other, AssetId.Empty, "Untitled");

        SceneSerializer.Load(reloaded, SceneSerializer.FromYaml(SceneSerializer.ToYaml(scene)));

        Assert.False(PrimitiveShapes.TryGet(other, Assert.Single(reloaded.Roots), out _));
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
        Assert.False(PrimitiveShapes.TryGet(other, Assert.Single(reloaded.Roots), out _));
    }

    [Fact]
    public void Only_shaped_entities_are_drawn() {
        scene.Add("Empty", LocalTransform.Identity);
        scene.CreateShape(PrimitiveKind.Cube, LocalTransform.Identity);

        var meshes = new SceneMeshes();

        Assert.Equal(1, meshes.Build(scene));
        Assert.Equal(1, meshes.Instances.Length);
        Assert.Equal(PrimitiveKind.Cube, Assert.Single(meshes.Batches).Kind);
    }

    [Fact]
    public void A_scene_with_nothing_in_it_draws_nothing() {
        var meshes = new SceneMeshes();

        Assert.Equal(0, meshes.Build(scene));
        Assert.True(meshes.Instances.IsEmpty);
        Assert.Empty(meshes.Batches);
    }

    /// <summary>
    ///     ⚠ <b>The assertion <c>docs/blockout-tools.md</c> § B1 is about.</b> A frame's cost used to be
    ///     every vertex of every entity, transformed on the processor, with a cache keyed by kind — so
    ///     a hundred cubes were one mesh and a hundred *edited* meshes were a hundred rebuilds a frame.
    ///     What a frame collects now is one instance per entity and one batch per shape, whatever the
    ///     shape's vertex count is, which is what makes a drag aimable at scene scale.
    /// </summary>
    [Fact]
    public void A_hundred_shapes_of_one_kind_are_one_batch_of_a_hundred_instances() {
        for (var index = 0; index < 100; index++) {
            scene.CreateShape(PrimitiveKind.Cube, LocalTransform.At(new Vector3(index * 2f, 0f, 0f)));
        }

        var meshes = new SceneMeshes();

        Assert.Equal(100, meshes.Build(scene));

        var batch = Assert.Single(meshes.Batches);

        Assert.Equal(0, batch.First);
        Assert.Equal(100, batch.Count);
        Assert.Equal(100, meshes.Instances.Length);
    }

    /// <summary>Two kinds are two batches, and each one's run is contiguous.</summary>
    /// <remarks>
    ///     ⚠ <b>A draw names a first instance and a count</b>, so an instance in the wrong run is an
    ///     entity drawn as the wrong shape rather than an entity drawn in the wrong place. Interleaving
    ///     the two kinds in the scene is what makes the grouping do work.
    /// </remarks>
    [Fact]
    public void Two_kinds_are_two_batches_and_each_run_is_contiguous() {
        scene.CreateShape(PrimitiveKind.Cube, LocalTransform.At(new Vector3(0f, 0f, 0f)));
        scene.CreateShape(PrimitiveKind.Sphere, LocalTransform.At(new Vector3(3f, 0f, 0f)));
        scene.CreateShape(PrimitiveKind.Cube, LocalTransform.At(new Vector3(6f, 0f, 0f)));

        var meshes = new SceneMeshes();
        meshes.Build(scene);

        Assert.Equal(2, meshes.Batches.Count);
        Assert.Equal(3, meshes.Instances.Length);

        var cubes = Assert.Single(meshes.Batches, batch => batch.Kind == PrimitiveKind.Cube);
        var spheres = Assert.Single(meshes.Batches, batch => batch.Kind == PrimitiveKind.Sphere);

        Assert.Equal(2, cubes.Count);
        Assert.Equal(1, spheres.Count);

        // The two runs cover the instances between them without overlapping.
        Assert.Equal(3, cubes.Count + spheres.Count);
        Assert.NotEqual(cubes.First, spheres.First);
    }

    [Fact]
    public void A_shape_is_drawn_where_its_transform_says() {
        scene.CreateShape(PrimitiveKind.Cube, LocalTransform.At(new Vector3(10f, 0f, 0f)));

        var meshes = new SceneMeshes();
        meshes.Build(scene);

        // The fourth row of a row-vector matrix is its translation, which is where the shader reads it
        // from — see `MeshInstanced.rvn` on why the rows cross the boundary rather than a mat4.
        var instance = meshes.Instances[0];

        Assert.Equal(new Vector3(10f, 0f, 0f), instance.Transform.Translation);
        Assert.Equal(new Vector3(10f, 0f, 0f), Matrix4x4.TransformPosition(Vector3.Zero, instance.Transform));
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

        // A cube's normals are axis-aligned and stay so under an axis-aligned scale — but only if they
        // go through the inverse transpose. Through the matrix itself the ±X faces come out four times
        // as long and, once normalised, still axis-aligned; the test that catches it is that every
        // normal is *unit* and axis-aligned, which the matrix path breaks for the diagonal of any
        // rotated shape. Keeping the cube axis-aligned makes the assertion exact.
        //
        // ⚠ The transform happens in the vertex stage now, so what is asserted is the matrix the stage
        // is given: one inverse per entity rather than one per vertex, and the reason it is stored
        // rather than derived is that a shader language has no inverse to ask with.
        var normals = meshes.Instances[0].Normals;

        foreach (var source in MeshPrimitives.Cube().Normals) {
            var normal = Vector3.Normalize(Matrix4x4.TransformDirection(source, normals));

            Assert.Equal(1f, normal.Length(), 3);
            Assert.Equal(1f, MathF.Abs(normal.X) + MathF.Abs(normal.Y) + MathF.Abs(normal.Z), 3);
        }
    }

    [Fact]
    public void A_selected_shape_is_a_different_colour() {
        var cube = scene.CreateShape(PrimitiveKind.Cube, LocalTransform.Identity);
        var meshes = new SceneMeshes();

        meshes.Build(scene);
        var plain = meshes.Instances[0].Colour;

        scene.Selection.Set(cube);
        meshes.Build(scene);

        // ⚠ Shown by colour rather than only by the gizmo sitting on it, for the reason SceneLines
        // gives: with several things selected the gizmo is at one place and the rest have nothing
        // saying they are about to move.
        Assert.NotEqual(plain, meshes.Instances[0].Colour);
        Assert.Equal(meshes.SelectedColour, meshes.Instances[0].Colour);
    }
}
