// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Ecs;
using Vixen.Editor.Testing;
using Vixen.Engine.Transforms;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Issue 1217: <c>entity.group</c> and <c>entity.clear-parent</c> are each one undo step.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The depth is the assertion, not the shape of the scene.</b> Both gestures were a
///         loop of single-entity <c>Reparent</c> calls, so Ctrl+G over five entities pushed six
///         entries and Ctrl+Z put the fifth entity back and left a group holding the other four.
///         Asserting "the group is gone after one undo" would pass on a stack with five entries
///         still to pop; asserting the depth moved by exactly one is what says it was one gesture.
///     </para>
///     <para>
///         The instrument check is the pair: each test measures the depth immediately before the
///         command and immediately after, on the same stack, so a fixture that recorded nothing at
///         all would fail the round trip below rather than pass by moving zero.
///     </para>
/// </remarks>
public class GroupGestureUndoTests {
    /// <summary>Ctrl+G over three entities is one entry, and one Ctrl+Z takes the whole group back.</summary>
    [Fact]
    public void Grouping_a_selection_is_one_undo_step() {
        using var fixture = EditorSession.Start();
        var scene = fixture.Scene;

        var members = Three(fixture);
        var before = scene.Stack.Depth.Value;

        scene.Selection.Set(members);
        fixture.Run("entity.group").Settle();

        var group = Assert.Single(scene.Selection);

        Assert.Equal("Group", scene.NameOf(group));

        foreach (var member in members) {
            Assert.Equal(group, Hierarchy.ParentOf(scene.World, member));
        }

        // A create and three reparents ran; one entry is what a gesture is worth.
        Assert.Equal(before + 1, scene.Stack.Depth.Value);

        Assert.True(scene.Stack.Undo());

        // Nothing half-undone: no group left over and every member back at the root it came from.
        foreach (var member in members) {
            Assert.True(scene.World.IsAlive(member));
            Assert.True(Hierarchy.ParentOf(scene.World, member).IsNull);
        }

        Assert.False(scene.World.IsAlive(group));
        Assert.Equal(before, scene.Stack.Depth.Value);
    }

    /// <summary>Clearing the parent of a whole selection is one entry, not one per entity.</summary>
    [Fact]
    public void Clearing_the_parent_of_a_selection_is_one_undo_step() {
        using var fixture = EditorSession.Start();
        var scene = fixture.Scene;

        var parent = scene.Add("Parent", LocalTransform.Identity);
        var members = Three(fixture);

        scene.Reparent(members, parent);
        Assert.All(members, member => Assert.Equal(parent, Hierarchy.ParentOf(scene.World, member)));

        var before = scene.Stack.Depth.Value;

        scene.Selection.Set(members);
        fixture.Run("entity.clear-parent").Settle();

        Assert.All(members, member => Assert.True(Hierarchy.ParentOf(scene.World, member).IsNull));
        Assert.Equal(before + 1, scene.Stack.Depth.Value);

        Assert.True(scene.Stack.Undo());

        Assert.All(members, member => Assert.Equal(parent, Hierarchy.ParentOf(scene.World, member)));
        Assert.Equal(before, scene.Stack.Depth.Value);
    }

    /// <summary>Three siblings at the root, which is what three separately created entities are.</summary>
    static List<Entity> Three(EditorSession fixture) => [
        fixture.Scene.Add("One", LocalTransform.Identity),
        fixture.Scene.Add("Two", LocalTransform.Identity),
        fixture.Scene.Add("Three", LocalTransform.Identity)
    ];
}
