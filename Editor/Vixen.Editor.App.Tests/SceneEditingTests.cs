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

        // The Edit menu's selection verbs are still the application's, over the same service.
        Assert.NotNull(editor.Shell.Commands["edit.duplicate"]);
    }
}
