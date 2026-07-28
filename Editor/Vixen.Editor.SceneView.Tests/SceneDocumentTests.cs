// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Engine.Transforms;
using Xunit;

namespace Vixen.Editor.SceneView.Tests;

/// <summary>The scene as a document: names, hierarchy, undo, and surviving play mode.</summary>
public class SceneDocumentTests : IDisposable {
    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-scene-" + Guid.NewGuid().ToString("N"));
    readonly EditorProject project;
    readonly World world = new("Test");
    readonly SceneDocument scene;

    public SceneDocumentTests() {
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
    public void An_entity_with_no_name_reads_as_something_a_person_can_tell_apart() {
        var entity = scene.Add("Crate", LocalTransform.Identity);
        Assert.Equal("Crate", scene.NameOf(entity));

        // One that was never named still gets text rather than an empty row.
        var unnamed = scene.Scenes.CreateTransform(scene.Scene, LocalTransform.Identity);
        Assert.False(string.IsNullOrWhiteSpace(scene.NameOf(unnamed)));
    }

    [Fact]
    public void Renaming_is_one_undo_step_that_goes_back_to_the_old_name() {
        var entity = scene.Add("Crate", LocalTransform.Identity);
        scene.Stack.Clear();

        Assert.True(scene.Rename(entity, "Barrel"));
        Assert.Equal("Barrel", scene.NameOf(entity));
        Assert.Equal(1, scene.Stack.Depth.Value);

        scene.Stack.Undo();
        Assert.Equal("Crate", scene.NameOf(entity));
    }

    [Fact]
    public void Two_renames_are_two_undo_steps() {
        var entity = scene.Add("A", LocalTransform.Identity);
        scene.Stack.Clear();

        scene.Rename(entity, "B");
        scene.Rename(entity, "C");

        // A name is not a slider: two names typed are two decisions.
        Assert.Equal(2, scene.Stack.Depth.Value);
    }

    [Fact]
    public void Renaming_to_the_same_name_records_nothing() {
        var entity = scene.Add("Crate", LocalTransform.Identity);
        scene.Stack.Clear();

        Assert.False(scene.Rename(entity, "Crate"));
        Assert.Equal(0, scene.Stack.Depth.Value);
    }

    [Fact]
    public void The_roots_are_the_entities_with_no_parent() {
        var parent = scene.Add("Parent", LocalTransform.Identity);
        scene.Add("Child", LocalTransform.Identity, parent);
        scene.Add("Other", LocalTransform.Identity);

        Assert.Equal(2, scene.Roots.Count);
        Assert.Contains(parent, scene.Roots);
        Assert.Equal(3, scene.Entities.Count());
    }

    [Fact]
    public void Reparenting_keeps_where_the_entity_is_in_the_world() {
        var parent = scene.Add("Parent", LocalTransform.At(new Vector3(5f, 0f, 0f)));
        var child = scene.Add("Child", LocalTransform.At(new Vector3(1f, 0f, 0f)));

        // The world transforms are what the last pass produced, which for a fresh entity is what
        // Hierarchy.CreateTransform wrote — enough for the parent-space arithmetic to be exercised.
        Assert.True(scene.Reparent(child, parent));
        Assert.Equal(parent, Hierarchy.ParentOf(world, child));

        Assert.False(scene.Reparent(child, parent));
    }

    [Fact]
    public void A_cycle_is_refused_rather_than_walked_for_ever() {
        var parent = scene.Add("Parent", LocalTransform.Identity);
        var child = scene.Add("Child", LocalTransform.Identity, parent);

        // Making the parent a child of its own child is a loop the transform pass would never leave.
        Assert.False(scene.Reparent(parent, child));
        Assert.Equal(parent, Hierarchy.ParentOf(world, child));
    }

    [Fact]
    public void A_structural_change_says_so_and_a_rename_does_not() {
        var structure = 0;
        var renames = 0;

        scene.StructureChanged += _ => structure++;
        scene.Renamed += (_, _) => renames++;

        var entity = scene.Add("Crate", LocalTransform.Identity);
        Assert.Equal(1, structure);

        scene.Rename(entity, "Barrel");

        // A rename changes a row's contents, not the tree. A hierarchy that rebuilt on it would lose
        // its expansion state every time somebody typed a letter.
        Assert.Equal(1, structure);
        Assert.Equal(1, renames);
    }

    [Fact]
    public void Names_move_across_a_play_mode_restore() {
        var entity = scene.Add("Crate", LocalTransform.Identity);

        using var play = new PlayModeController(world);
        play.Play();

        var selection = play.Stop([entity]);
        var translated = Assert.Single(selection);

        // Before the remap the name map is keyed by handles that no longer name anything.
        scene.Remap(Table(entity, translated));

        Assert.Equal("Crate", scene.NameOf(translated));
        Assert.Equal(0, scene.PruneNames());
    }

    [Fact]
    public void A_name_whose_entity_did_not_survive_is_dropped_rather_than_carried_over() {
        var entity = scene.Add("Crate", LocalTransform.Identity);

        // An empty table is what a restore hands back for an entity play mode created and destroyed.
        scene.Remap(new Dictionary<Entity, Entity>());

        // Nothing translated, so nothing is named — rather than "Crate" landing on whatever entity
        // happens to take that slot next.
        Assert.NotEqual("Crate", scene.NameOf(entity));
    }

    [Fact]
    public void Saving_a_scene_nothing_can_write_fails_rather_than_reporting_success() {
        scene.Add("Crate", LocalTransform.Identity);

        // EditorDocument.Save marks the document clean afterwards, so a SaveCore that quietly wrote
        // nothing would leave it claiming to match a file that does not exist.
        Assert.Throws<InvalidOperationException>(scene.Save);
    }

    [Fact]
    public void A_scene_with_a_writer_saves_through_it_and_comes_out_clean() {
        var writer = new StubWriter();
        scene.Writer = writer;

        var entity = scene.Add("Crate", LocalTransform.Identity);
        scene.Rename(entity, "Barrel");

        Assert.True(scene.IsDirty.Value);

        scene.Save();

        Assert.Equal(1, writer.Writes);
        Assert.False(scene.IsDirty.Value);
    }

    static Dictionary<Entity, Entity> Table(Entity from, Entity to) => new() { [from] = to };

    sealed class StubWriter : ISceneWriter {
        public int Writes { get; private set; }

        public void Write(SceneDocument document) => Writes++;
    }
}
