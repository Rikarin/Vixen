// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Ecs;
using Vixen.Editor.Testing;
using Vixen.Engine.Transforms;
using Vixen.Rendering.Ecs;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Issue 1173's item (2): the gesture that turns a selection into a LOD chain.</summary>
/// <remarks>
///     <para>
///         <b>What was missing was authoring and not runtime.</b> <c>LodGroupComponent</c> and
///         <c>LodLevel</c> are both <c>[Component] [DataContract]</c>, so the inspector has always
///         shown them and a scene has always serialised them — and that was the whole of it. A
///         three-level chain was three meshes, one empty, three reparents, three level numbers typed
///         in by hand and a threshold list typed into a fourth field.
///     </para>
///     <para>
///         ⚠ <b>The undo assertion is the half that is easy to leave out and easy to get wrong.</b>
///         The gesture is a create, n reparents and n component writes — 2n+1 commands — so a
///         Ctrl+Z that took back one of them leaves a group with four levels in it and a mesh sitting
///         where it started. Asserting "the group is gone" would pass on a stack that still had six
///         entries left to pop; asserting the depth is what says it was one step.
///     </para>
/// </remarks>
public class LodGroupGestureTests {
    /// <summary>The claim: three selected meshes become a group with three numbered levels.</summary>
    [Fact]
    public void Grouping_three_entities_as_a_lod_group_numbers_them_and_writes_the_thresholds() {
        using var fixture = EditorSession.Start();
        var scene = fixture.Scene;

        var members = Three(fixture);

        scene.Selection.Set(members);
        Assert.True(fixture.CanRun("entity.group-lod"));

        fixture.Run("entity.group-lod").Settle();

        var group = Assert.Single(scene.Selection);

        Assert.Equal("LOD Group", scene.NameOf(group));
        Assert.True(scene.World.Has<LodGroupComponent>(group));

        // ⚠ Two thresholds for three levels, descending. `LodRenderFeature.Add` throws on a list
        // that is not, so a default that merely looked plausible would be a menu line whose output
        // takes the renderer down the first time the group is drawn.
        var thresholds = scene.World.Read<LodGroupComponent>(group).Thresholds;

        Assert.NotNull(thresholds);
        Assert.Equal(2, thresholds.Length);
        Assert.True(thresholds[0] > thresholds[1], $"{thresholds[0]} then {thresholds[1]} does not descend");
        Assert.True(thresholds[^1] > 0f);

        // Selection order is level order, first selected is LOD 0, and every one of them is under
        // the group rather than where it started.
        for (var level = 0; level < members.Count; level++) {
            Assert.Equal(group, Hierarchy.ParentOf(scene.World, members[level]));
            Assert.True(scene.World.Has<LodLevel>(members[level]));
            Assert.Equal(level, scene.World.Read<LodLevel>(members[level]).Level);
        }
    }

    /// <summary>
    ///     One gesture, one undo step. Seven commands ran; a stack that recorded them separately
    ///     would leave the scene half grouped after a single Ctrl+Z.
    /// </summary>
    [Fact]
    public void The_whole_gesture_is_one_undo_step() {
        using var fixture = EditorSession.Start();
        var scene = fixture.Scene;

        var members = Three(fixture);
        var before = scene.Stack.Depth.Value;

        scene.Selection.Set(members);
        fixture.Run("entity.group-lod").Settle();

        Assert.Equal(before + 1, scene.Stack.Depth.Value);

        Assert.True(scene.Stack.Undo());

        // Everything the gesture did, gone: no component on any member and no group above them.
        foreach (var member in members) {
            Assert.True(scene.World.IsAlive(member));
            Assert.False(scene.World.Has<LodLevel>(member));
        }

        Assert.DoesNotContain(scene.Entities, entity => scene.World.Has<LodGroupComponent>(entity));
        Assert.Equal(before, scene.Stack.Depth.Value);
    }

    /// <summary>
    ///     ⚠ A chain of one is a group whose only level is always the one drawn, which is what an
    ///     author gets by doing nothing — so the line greys itself out rather than making a group
    ///     that means nothing. This is also the instrument check on the assertion above: a command
    ///     whose enablement were simply <c>true</c> would make every one of these pass.
    /// </summary>
    [Fact]
    public void The_line_is_greyed_until_two_things_are_selected() {
        using var fixture = EditorSession.Start();
        var scene = fixture.Scene;

        var members = Three(fixture);

        scene.Selection.Clear();
        Assert.False(fixture.CanRun("entity.group-lod"));

        scene.Selection.Set([members[0]]);
        Assert.False(fixture.CanRun("entity.group-lod"));

        scene.Selection.Set([members[0], members[1]]);
        Assert.True(fixture.CanRun("entity.group-lod"));
    }

    /// <summary>Three siblings at the root, which is what three separately imported meshes are.</summary>
    static List<Entity> Three(EditorSession fixture) => [
        fixture.Scene.Add("High", LocalTransform.Identity),
        fixture.Scene.Add("Medium", LocalTransform.Identity),
        fixture.Scene.Add("Low", LocalTransform.Identity)
    ];
}
