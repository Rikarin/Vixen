// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Core.Yaml;
using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Editor.Core.Scenes;
using Vixen.Engine.Cameras;
using Vixen.Engine.Scenes;
using Vixen.Engine.Transforms;
using Xunit;

namespace Vixen.Editor.SceneView.Tests;

/// <summary>The scene file: what it says, and what survives a round trip.</summary>
public class SceneSerializerTests : IDisposable {
    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-scenefile-" + Guid.NewGuid().ToString("N"));
    readonly EditorProject project;
    readonly World world = new("Test");
    readonly SceneDocument scene;

    public SceneSerializerTests() {
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

    /// <summary>An entity's children as a list, since the hierarchy hands back a walk and not one.</summary>
    static List<Entity> Children(World world, Entity entity) {
        List<Entity> children = [];

        foreach (var child in Hierarchy.ChildrenOf(world, entity)) {
            children.Add(child);
        }

        return children;
    }

    /// <summary>A second document over a second world, for reading a file back into.</summary>
    (World World, SceneDocument Scene) Fresh() {
        var other = new World("Reloaded");

        return (other, new SceneDocument(project, other, AssetId.Empty, "Untitled"));
    }

    void Populate() {
        var parent = scene.Add("Scene Root", LocalTransform.At(new Vector3(1f, 2f, 3f)));

        scene.Add("Light", LocalTransform.At(new Vector3(0f, 4f, 0f)), parent);

        var ground = scene.Add("Ground", LocalTransform.Identity, parent);
        scene.Add("Crate", LocalTransform.At(new Vector3(-1f, 0.5f, 2f)), ground);
        scene.Add("Barrel", LocalTransform.Identity);
    }

    [Fact]
    public void An_entity_id_round_trips_through_its_text() {
        var id = EntityId.New();

        Assert.Equal(EntityId.TextLength, id.ToString().Length);
        Assert.True(EntityId.TryParse(id.ToString(), out var parsed));
        Assert.Equal(id, parsed);

        Assert.False(EntityId.TryParse("not an id", out _));
        Assert.True(EntityId.None.IsNone);
    }

    [Fact]
    public void An_id_is_minted_once_and_kept() {
        var entity = scene.Add("Crate", LocalTransform.Identity);
        var first = scene.IdOf(entity);

        Assert.Equal(first, scene.IdOf(entity));
        Assert.True(scene.TryGetEntity(first, out var found));
        Assert.Equal(entity, found);
    }

    [Fact]
    public void The_hierarchy_survives_a_round_trip() {
        Populate();

        var yaml = SceneSerializer.ToYaml(scene);
        var (other, reloaded) = Fresh();

        using (other) {
            Assert.Equal(5, SceneSerializer.Load(reloaded, SceneSerializer.FromYaml(yaml)));

            var roots = reloaded.Roots;
            Assert.Equal(2, roots.Count);

            var sceneRoot = roots.Single(entity => reloaded.NameOf(entity) == "Scene Root");
            var children = Children(other, sceneRoot);

            Assert.Equal(2, children.Count);
            Assert.Contains(children, child => reloaded.NameOf(child) == "Ground");

            var ground = children.Single(child => reloaded.NameOf(child) == "Ground");
            Assert.Equal("Crate", reloaded.NameOf(Children(other, ground).Single()));
        }
    }

    [Fact]
    public void The_transforms_survive_a_round_trip() {
        var entity = scene.Add(
            "Crate",
            new LocalTransform {
                Position = new(1.5f, -2f, 0.25f),
                Rotation = Quaternion.FromAxisAngle(Vector3.UnitY, 0.7f),
                Scale = new(2f, 3f, 4f)
            }
        );

        var expected = world.Read<LocalTransform>(entity);
        var (other, reloaded) = Fresh();

        using (other) {
            SceneSerializer.Load(reloaded, SceneSerializer.FromYaml(SceneSerializer.ToYaml(scene)));

            var restored = other.Read<LocalTransform>(reloaded.Roots.Single());

            Assert.True(Vector3.NearEqual(expected.Position, restored.Position, 1e-5f));
            Assert.True(Quaternion.NearEqual(expected.Rotation, restored.Rotation, 1e-5f));
            Assert.True(Vector3.NearEqual(expected.Scale, restored.Scale, 1e-5f));
        }
    }

    [Fact]
    public void An_entity_keeps_the_id_the_file_gave_it() {
        var entity = scene.Add("Crate", LocalTransform.Identity);
        var id = scene.IdOf(entity);

        var (other, reloaded) = Fresh();

        using (other) {
            SceneSerializer.Load(reloaded, SceneSerializer.FromYaml(SceneSerializer.ToYaml(scene)));

            // The handle is new and the identity is not — which is the whole point of the id
            // existing, and what a reference between entities will be expressed in.
            Assert.True(reloaded.TryGetEntity(id, out var restored));
            Assert.Equal("Crate", reloaded.NameOf(restored));
        }
    }

    [Fact]
    public void Saving_a_reloaded_scene_writes_the_same_bytes() {
        Populate();

        var first = SceneSerializer.ToYaml(scene);
        var (other, reloaded) = Fresh();

        using (other) {
            SceneSerializer.Load(reloaded, SceneSerializer.FromYaml(first));

            // The ids came from the file rather than being minted afresh, so an open-and-save leaves
            // the working tree untouched. A format that failed this makes every scene a merge
            // conflict with itself.
            Assert.Equal(first, SceneSerializer.ToYaml(reloaded));
        }
    }

    [Fact]
    public void A_file_from_a_newer_editor_is_refused_rather_than_half_read() {
        var yaml = SceneSerializer.ToYaml(scene).Replace("version: 1", "version: 99", StringComparison.Ordinal);

        // Binding what it recognises and dropping the rest on the next save is the failure a version
        // field exists to prevent.
        Assert.Throws<NotSupportedException>(() => SceneSerializer.FromYaml(yaml));
    }

    [Fact]
    public void A_rotation_a_file_did_not_write_is_the_identity_rather_than_a_collapse() {
        var yaml = """
                   version: 1
                   name: Hand written
                   roots:
                     - id: 0123456789abcdef0123456789abcdef
                       name: Crate
                       position: 1 2 3
                   """;

        var (other, reloaded) = Fresh();

        using (other) {
            SceneSerializer.Load(reloaded, SceneSerializer.FromYaml(yaml));

            var local = other.Read<LocalTransform>(reloaded.Roots.Single());

            // A zeroed quaternion is a degenerate matrix and a zeroed scale is an entity that is
            // present, selectable and invisible. Neither is what a missing field means.
            Assert.Equal(Quaternion.Identity, local.Rotation);
            Assert.Equal(Vector3.One, local.Scale);
            Assert.Equal(new Vector3(1f, 2f, 3f), local.Position);
        }
    }

    [Fact]
    public void Text_that_is_not_a_scene_says_so_rather_than_binding_an_empty_scene() =>
        Assert.ThrowsAny<Exception>(() => SceneSerializer.FromYaml("\tnot: [valid"));

    [Fact]
    public void A_loaded_scene_opens_clean_with_an_empty_history() {
        Populate();

        var (other, reloaded) = Fresh();

        using (other) {
            SceneSerializer.Load(reloaded, SceneSerializer.FromYaml(SceneSerializer.ToYaml(scene)));

            Assert.Equal(0, reloaded.Stack.Depth.Value);
            Assert.False(reloaded.IsDirty.Value);
        }
    }

    [Fact]
    public void Saving_to_a_path_creates_the_directory_and_leaves_no_temporary_behind() {
        Populate();

        var path = Path.Combine(root, "Assets", "Scenes", "Level1" + SceneSerializer.Extension);
        SceneSerializer.Save(scene, path);

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));

        var (other, reloaded) = Fresh();

        using (other) {
            Assert.Equal(5, SceneSerializer.Load(reloaded, path));
        }
    }

    [Fact]
    public void Loading_a_path_with_no_file_is_an_empty_scene_rather_than_a_throw() =>
        Assert.Equal(0, SceneSerializer.Load(scene, Path.Combine(root, "nothing" + SceneSerializer.Extension)));

    [Fact]
    public void A_document_with_a_writer_saves_where_the_writer_says() {
        var path = Path.Combine(root, "Assets", "Level1" + SceneSerializer.Extension);

        scene.Writer = new SceneFileWriter(path);
        scene.Add("Crate", LocalTransform.Identity);
        scene.Save();

        Assert.True(File.Exists(path));
        Assert.False(scene.IsDirty.Value);
    }

    [Fact]
    public void A_component_survives_a_round_trip_through_the_file() {
        var entity = scene.Add("Main Camera", LocalTransform.At(new Vector3(0f, 2f, -10f)));
        world.Add(entity, Camera.Perspective with { FarPlane = 250f, Order = 3 });

        var yaml = SceneSerializer.ToYaml(scene);
        var (other, reloaded) = Fresh();

        using (other) {
            SceneSerializer.Load(reloaded, SceneSerializer.FromYaml(yaml));
            var camera = other.Read<Camera>(reloaded.Roots.Single());

            // The tag is how the file says which component this is — the same mechanism a .meta uses
            // for its importer, and the same name the compiled scene and the binary serializer use.
            Assert.Contains("!Camera", yaml, StringComparison.Ordinal);
            Assert.Equal(250f, camera.FarPlane);
            Assert.Equal(3, camera.Order);
            Assert.Equal(yaml, SceneSerializer.ToYaml(reloaded));
        }
    }

    [Fact]
    public void A_component_nothing_registered_is_refused_rather_than_dropped_on_the_next_save() {
        var yaml = """
                   version: 1
                   name: Untitled
                   roots:
                     - id: 4c2b1a0908070605040302010f0e0d0c
                       name: Crate
                       components:
                         - !SceneEntity
                           name: Nested
                   """;

        // A type the binder can find and the scene registry does not know. Loading it and saving
        // would silently delete the block from the file, which is the failure the version check
        // exists to prevent arriving through another door.
        Assert.Throws<SceneComponentException>(() => SceneSerializer.Load(scene, SceneSerializer.FromYaml(yaml)));
    }

    [Fact]
    public void The_file_is_something_a_person_can_read() {
        Populate();

        var yaml = SceneSerializer.ToYaml(scene);

        // Not a golden test — the point is that a scene is diffable and mergeable at all, which is
        // the whole reason the authoring format is YAML and the runtime's is not this.
        Assert.Contains("Scene Root", yaml, StringComparison.Ordinal);
        Assert.Contains("children:", yaml, StringComparison.Ordinal);
        Assert.Contains("version: 1", yaml, StringComparison.Ordinal);
    }
}
