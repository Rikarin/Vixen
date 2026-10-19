// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Editor.Plugin;
using Vixen.Editor.SceneView;
using Vixen.Editor.Testing;
using Vixen.Engine.Transforms;
using Vixen.Rendering;
using Vixen.Rendering.Ecs;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Doc 50 § P2b's first slice (#1290): the entity verbs as a service, with and without an editor.</summary>
/// <remarks>
///     ⚠ <b>Two halves on purpose</b> (doc 50 § D2). The first tests reach the verbs with a
///     <see cref="SceneDocument" /> and nothing else — what was impossible while they were private on
///     the application. The last ones open an editor the way the host does and run the commands by
///     id, which is the proof the wiring holds: a module nobody activated would leave them green.
/// </remarks>
public sealed class SceneEditingTests : IDisposable {
    readonly World world = new("Test");
    readonly TransformSystem transforms = new();
    readonly SceneDocument document;
    readonly SceneEditing editing;

    public SceneEditingTests() {
        var project = new EditorProject(new ProjectPaths(Path.Combine(Path.GetTempPath(), "vixen-scene-editing")));

        document = new SceneDocument(project, world, AssetId.Empty, "Test");
        editing = new SceneEditing(() => document);
    }

    public void Dispose() => world.Dispose();

    void Resolve() {
        transforms.Resolve(world);
        world.AdvanceVersion();
    }

    [Fact]
    public void Group_is_one_undo_entry_that_undoes_the_create_and_every_reparent() {
        var members = new[] {
            document.Create("A", LocalTransform.Identity),
            document.Create("B", LocalTransform.Identity),
            document.Create("C", LocalTransform.Identity)
        };

        document.Selection.Set(members);
        var before = document.Stack.Depth.Value;

        var group = editing.Group();

        Assert.NotEqual(Entity.Null, group);
        Assert.Equal(before + 1, document.Stack.Depth.Value);
        Assert.All(members, member => Assert.Equal(group, Hierarchy.ParentOf(world, member)));
        Assert.Equal([group], document.Selection);

        document.Stack.Undo();

        Assert.False(world.IsAlive(group));
        Assert.All(members, member => Assert.Equal(Entity.Null, Hierarchy.ParentOf(world, member)));
    }

    [Fact]
    public void Snap_to_floor_with_nothing_under_it_lands_on_the_ground_plane_as_one_entry() {
        var crate = document.Create("Crate", LocalTransform.Identity with { Position = new Vector3(1f, 5f, 2f) });

        Resolve();
        document.Selection.Set([crate]);

        var before = document.Stack.Depth.Value;

        editing.SnapToFloor();

        Assert.Equal(before + 1, document.Stack.Depth.Value);
        Assert.Equal(new Vector3(1f, 0f, 2f), world.Read<LocalTransform>(crate).Position);

        document.Stack.Undo();

        Assert.Equal(new Vector3(1f, 5f, 2f), world.Read<LocalTransform>(crate).Position);
    }

    [Fact]
    public void A_parent_that_is_inside_what_is_moving_is_refused_with_a_reason() {
        var parent = document.Create("Parent", LocalTransform.Identity);
        var child = document.Create("Child", LocalTransform.Identity, parent);

        Assert.Equal("That is the entity being moved.", editing.WhyNotParent([parent], parent));
        Assert.Equal("It is inside Parent, which is what is moving.", editing.WhyNotParent([parent], child));
        Assert.Equal("Already the parent.", editing.WhyNotParent([child], parent));

        var changed = 0;
        editing.HierarchyChanged += _ => changed++;

        Assert.True(editing.SetParent([child], Entity.Null));
        Assert.Equal(1, changed);
    }

    // ── #1290's second slice: Part D's selection and transform verbs ─────────────────────────────

    [Fact]
    public void Isolate_hides_all_but_the_selection_its_ancestors_and_descendants_and_gives_back_what_was_hidden() {
        var rig = document.Create("Rig", LocalTransform.Identity);
        var arm = document.Create("Arm", LocalTransform.Identity, rig);
        var hand = document.Create("Hand", LocalTransform.Identity, arm);
        var lamp = document.Create("Lamp", LocalTransform.Identity);
        var crate = document.Create("Crate", LocalTransform.Identity);

        document.SetHidden(crate, true);
        document.Selection.Set([arm]);

        var marks = 0;
        editing.MarksChanged += _ => marks++;
        var depth = document.Stack.Depth.Value;

        editing.Isolate();

        Assert.True(editing.IsIsolating);
        Assert.False(document.IsHidden(rig));
        Assert.False(document.IsHidden(arm));
        Assert.False(document.IsHidden(hand));
        Assert.True(document.IsHiddenDirectly(lamp));

        editing.Isolate();

        Assert.False(editing.IsIsolating);
        Assert.False(document.IsHidden(lamp));

        // ⚠ What was hidden before comes back hidden, not "everything visible".
        Assert.True(document.IsHiddenDirectly(crate));
        Assert.Equal(2, marks);

        // Editor state, not an edit: no undo entry either way.
        Assert.Equal(depth, document.Stack.Depth.Value);
    }

    [Fact]
    public void Select_by_name_ignores_case_and_leaves_the_selection_alone_when_it_finds_nothing() {
        var lamp = document.Create("Desk Lamp", LocalTransform.Identity);
        var crate = document.Create("Crate", LocalTransform.Identity);

        document.Selection.Set([crate]);

        Assert.Empty(editing.SelectByName("chair"));
        Assert.Equal([crate], document.Selection);

        Assert.Equal([lamp], editing.SelectByName("LAMP"));
        Assert.Equal([lamp], document.Selection);
    }

    [Fact]
    public void A_recalled_selection_set_comes_back_without_what_was_deleted() {
        var a = document.Create("A", LocalTransform.Identity);
        var b = document.Create("B", LocalTransform.Identity);

        document.Selection.Set([a, b]);
        Assert.True(editing.SaveSelectionSet("Pair"));

        document.Delete([b]);
        document.Selection.Clear();

        Assert.Equal([a], editing.RecallSelectionSet("Pair"));
        Assert.Equal([a], document.Selection);
        Assert.Equal(2, editing.SelectionSets["Pair"].Count);
    }

    [Fact]
    public void Paste_transform_writes_the_copied_pose_onto_the_selection_as_one_undoable_entry() {
        var from = document.Create("From", LocalTransform.Identity with { Position = new Vector3(1f, 2f, 3f) });
        var to = document.Create("To", LocalTransform.Identity);
        var also = document.Create("Also", LocalTransform.Identity with { Position = new Vector3(9f, 0f, 0f) });

        document.Selection.Set([from]);
        Assert.True(editing.CopyTransform());

        document.Selection.Set([to, also]);

        var depth = document.Stack.Depth.Value;

        editing.PasteTransform();

        Assert.Equal(depth + 1, document.Stack.Depth.Value);
        Assert.Equal(new Vector3(1f, 2f, 3f), world.Read<LocalTransform>(to).Position);
        Assert.Equal(new Vector3(1f, 2f, 3f), world.Read<LocalTransform>(also).Position);

        Assert.True(document.Stack.Undo());

        Assert.Equal(Vector3.Zero, world.Read<LocalTransform>(to).Position);
        Assert.Equal(new Vector3(9f, 0f, 0f), world.Read<LocalTransform>(also).Position);
    }

    [Fact]
    public void Align_and_distribute_move_along_one_axis_as_one_entry_each() {
        var a = document.Create("A", LocalTransform.Identity with { Position = new Vector3(0f, 1f, 0f) });
        var b = document.Create("B", LocalTransform.Identity with { Position = new Vector3(1f, 2f, 5f) });
        var c = document.Create("C", LocalTransform.Identity with { Position = new Vector3(10f, 3f, 0f) });

        Resolve();

        // Onto the primary — the last selected — on Y only.
        document.Selection.Set([a, b, c]);

        var depth = document.Stack.Depth.Value;

        editing.Align(1);

        Assert.Equal(depth + 1, document.Stack.Depth.Value);
        Assert.Equal(new Vector3(0f, 3f, 0f), world.Read<LocalTransform>(a).Position);
        Assert.Equal(new Vector3(1f, 3f, 5f), world.Read<LocalTransform>(b).Position);

        Resolve();

        // Between the ends on X, which do not move.
        editing.Distribute(0);
        Resolve();

        Assert.Equal(depth + 2, document.Stack.Depth.Value);
        Assert.Equal(0f, world.Read<LocalTransform>(a).Position.X);
        Assert.Equal(5f, world.Read<LocalTransform>(b).Position.X, 4);
        Assert.Equal(10f, world.Read<LocalTransform>(c).Position.X);

        Assert.True(document.Stack.Undo());
        Assert.True(document.Stack.Undo());

        Assert.Equal(new Vector3(1f, 2f, 5f), world.Read<LocalTransform>(b).Position);
        Assert.Throws<ArgumentOutOfRangeException>(() => editing.Align(3));
    }

    /// <summary>
    ///     The create verbs reached with a document and nothing else: under the first selected entity,
    ///     selected afterwards, one undo entry each, and the undo takes the entity away.
    /// </summary>
    [Fact]
    public void Each_create_verb_is_one_undo_entry_under_the_selection_and_undoes_whole() {
        var parent = document.Create("Parent", LocalTransform.Identity);

        Func<Entity>[] verbs = [
            editing.CreateEntity,
            () => editing.CreateShape(PrimitiveKind.Cube),
            () => editing.CreateLight(LightKind.Spot),
            editing.CreateCamera
        ];

        foreach (var verb in verbs) {
            document.Selection.Set([parent]);
            var before = document.Stack.Depth.Value;

            var created = verb();

            Assert.True(world.IsAlive(created));
            Assert.Equal(parent, Hierarchy.ParentOf(world, created));
            Assert.Equal([created], document.Selection);
            Assert.Equal(before + 1, document.Stack.Depth.Value);

            document.Stack.Undo();

            Assert.False(world.IsAlive(created));
        }

        Assert.True(world.Has<Light>(editing.CreateLight(LightKind.Point)));
    }

    /// <summary>
    ///     By effect, through the shell: the command is <c>SceneModule</c>'s, over the published
    ///     service, and pressing it changes the scene by exactly one undo entry.
    /// </summary>
    [Fact]
    public void The_group_command_reaches_the_service_from_an_editor_opened_the_way_the_host_opens_one() {
        using var editor = EditorSession.Start();

        Assert.Equal(PluginState.Active, editor.Plugins.Find(SceneModule.ModuleId)!.State);

        var service = editor.Plugins.Services.Require<ISceneEditing>();

        Assert.Same(editor.Scene, service.Active);

        var a = editor.Scene.Create("A", LocalTransform.Identity);
        var b = editor.Scene.Create("B", LocalTransform.Identity);

        editor.Scene.Selection.Set([a, b]);
        editor.Settle();

        var before = editor.Scene.Stack.Depth.Value;

        editor.Run("entity.group");

        Assert.Equal(before + 1, editor.Scene.Stack.Depth.Value);

        var group = Hierarchy.ParentOf(editor.Scene.World, a);

        Assert.NotEqual(Entity.Null, group);
        Assert.Equal(group, Hierarchy.ParentOf(editor.Scene.World, b));
    }

    /// <summary>The module is what registers the lines, so switching it off takes them out.</summary>
    [Fact]
    public void Switching_the_scene_module_off_takes_its_lines_with_it() {
        using var editor = EditorSession.Start();

        Assert.NotNull(editor.Shell.Commands["entity.group"]);

        editor.Plugins.Disable(SceneModule.ModuleId);

        Assert.Null(editor.Shell.Commands["entity.group"]);
        Assert.Null(editor.Shell.Commands["entity.snap-to-floor"]);

        // And Part D's verbs that ask nothing, which moved with #1290's second slice.
        Assert.Null(editor.Shell.Commands["edit.isolate"]);
        Assert.Null(editor.Shell.Commands["entity.reset-transform"]);
        Assert.Null(editor.Shell.Commands["entity.copy-transform"]);
        Assert.Null(editor.Shell.Commands["entity.paste-transform"]);

        // And the six that ask first, through the shell's chooser since #1290's third slice — plus
        // the Planned line declared beside them.
        Assert.Null(editor.Shell.Commands["entity.align"]);
        Assert.Null(editor.Shell.Commands["entity.distribute"]);
        Assert.Null(editor.Shell.Commands["edit.select-by-name"]);
        Assert.Null(editor.Shell.Commands["edit.select-by-type"]);
        Assert.Null(editor.Shell.Commands["edit.save-selection-set"]);
        Assert.Null(editor.Shell.Commands["edit.recall-selection-set"]);
        Assert.Null(editor.Shell.Commands["entity.relative-transform"]);

        // And the create verbs, Create Empty with its shortcut's command.
        Assert.Null(editor.Shell.Commands["scene.create-entity"]);
        Assert.Null(editor.Shell.Commands[SceneModule.ShapeCommandId(PrimitiveKind.Cube)]);
        Assert.Null(editor.Shell.Commands[SceneModule.LightCommandId(LightKind.Point)]);
        Assert.Null(editor.Shell.Commands["scene.create-camera"]);

        // The Edit menu's selection verbs are still the application's, over the same service.
        Assert.NotNull(editor.Shell.Commands["edit.duplicate"]);
    }
}
