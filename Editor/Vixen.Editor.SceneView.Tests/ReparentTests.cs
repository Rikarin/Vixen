// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Editor.SceneView;
using Vixen.Engine.Transforms;
using Xunit;

namespace Vixen.Editor.SceneView.Tests;

/// <summary>Dragging a row onto another one, and what Ctrl+Z has to put back.</summary>
/// <remarks>
///     ⚠ <b>Doc 20's B1 asks for drag-to-reparent "undoably", and the parenthesis is the whole
///     test.</b> Linking a child prepends — the right default for building a hierarchy, and the
///     wrong one for undo: a user who moves the third of five children and presses Ctrl+Z gets it
///     back at the head of the list, which is an undo that did not undo.
/// </remarks>
public class ReparentTests : IDisposable {
    readonly World world = new("Test");
    readonly EditorProject project;
    readonly SceneDocument scene;
    readonly string directory;

    public ReparentTests() {
        directory = Path.Combine(Path.GetTempPath(), "vixen-reparent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        project = new EditorProject(new ProjectPaths(directory));
        project.Open();

        scene = new SceneDocument(project, world, AssetId.Empty, "Test");
    }

    public void Dispose() {
        world.Dispose();

        try {
            Directory.Delete(directory, recursive: true);
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            // A temp directory that would not go is not a failed test.
        }

        GC.SuppressFinalize(this);
    }

    Entity Add(string name, Entity parent = default) => scene.Add(name, LocalTransform.Identity, parent);

    /// <summary>The children of an entity, in order.</summary>
    /// <remarks>
    ///     Materialised by hand rather than with LINQ: <c>Hierarchy.ChildrenOf</c> hands back a
    ///     struct sequence over the sibling list, which is what keeps a hierarchy walk
    ///     allocation-free and what makes it not an <c>IEnumerable</c>.
    /// </remarks>
    List<Entity> Children(Entity parent) {
        List<Entity> found = [];

        foreach (var child in Hierarchy.ChildrenOf(world, parent)) {
            found.Add(child);
        }

        return found;
    }

    /// <summary>The same, by name, which is what an order assertion reads best as.</summary>
    List<string> ChildrenOf(Entity parent) => [.. Children(parent).Select(scene.NameOf)];

    [Fact]
    public void Reparenting_moves_the_entity_and_is_one_undo_step() {
        var shelf = Add("Shelf");
        var floor = Add("Floor");
        var crate = Add("Crate", floor);

        Assert.True(scene.Reparent(crate, shelf));
        Assert.Equal(shelf, Hierarchy.ParentOf(world, crate));

        scene.Stack.Undo();

        Assert.Equal(floor, Hierarchy.ParentOf(world, crate));
    }

    [Fact]
    public void An_undo_puts_it_back_between_the_siblings_it_was_between() {
        var shelf = Add("Shelf");
        var floor = Add("Floor");

        // Children link at the head, so adding a…e leaves them in the order e d c b a.
        foreach (var name in new[] { "a", "b", "c", "d", "e" }) {
            Add(name, floor);
        }

        var before = ChildrenOf(floor);
        var third = Children(floor)[2];

        Assert.True(scene.Reparent(third, shelf));
        Assert.Equal(4, ChildrenOf(floor).Count);

        scene.Stack.Undo();

        // ⚠ The whole point. An undo that returned it to the head of the list would leave the order
        // c e d b a here, and the user who pressed Ctrl+Z would have to fix it by hand.
        Assert.Equal(before, ChildrenOf(floor));
    }

    [Fact]
    public void Several_entities_move_as_one_step_and_come_back_in_order() {
        var shelf = Add("Shelf");
        var floor = Add("Floor");

        foreach (var name in new[] { "a", "b", "c", "d" }) {
            Add(name, floor);
        }

        var before = ChildrenOf(floor);
        var moving = Children(floor).Take(2).ToList();

        Assert.True(scene.Reparent(moving, shelf));
        Assert.Equal(2, ChildrenOf(floor).Count);

        // One step, not two: a drag of five rows is one thing somebody did.
        scene.Stack.Undo();

        Assert.Equal(before, ChildrenOf(floor));
    }

    [Fact]
    public void A_cycle_is_refused_rather_than_walked_for_ever() {
        var floor = Add("Floor");
        var crate = Add("Crate", floor);

        Assert.False(scene.Reparent(floor, crate));
        Assert.Equal(Entity.Null, Hierarchy.ParentOf(world, floor));
    }

    [Fact]
    public void Moving_something_to_where_it_already_is_does_nothing_and_makes_no_undo_step() {
        var floor = Add("Floor");
        var crate = Add("Crate", floor);

        Assert.False(scene.Reparent(crate, floor));

        // A command that executed to nothing would be an undo step that appears to do nothing.
        Assert.False(scene.Stack.CanUndo.Value);
    }

    [Fact]
    public void A_child_carried_inside_a_moving_parent_is_not_moved_again() {
        var shelf = Add("Shelf");
        var floor = Add("Floor");
        var crate = Add("Crate", floor);
        var lid = Add("Lid", crate);

        // Dragging a parent and its child together: the child travels inside the parent, and a
        // second move for it would take it out of the subtree it just travelled in.
        Assert.True(scene.Reparent([crate, lid], shelf));

        Assert.Equal(shelf, Hierarchy.ParentOf(world, crate));
        Assert.Equal(crate, Hierarchy.ParentOf(world, lid));

        scene.Stack.Undo();

        Assert.Equal(floor, Hierarchy.ParentOf(world, crate));
        Assert.Equal(crate, Hierarchy.ParentOf(world, lid));
    }

    [Fact]
    public void An_entity_keeps_where_it_is_in_the_world() {
        var shelf = scene.Add("Shelf", LocalTransform.At(new Vector3(10f, 0f, 0f)));
        var crate = scene.Add("Crate", LocalTransform.At(new Vector3(1f, 2f, 3f)));

        var transforms = new TransformSystem();

        transforms.Resolve(world);
        world.AdvanceVersion();

        Assert.True(scene.Reparent(crate, shelf));

        transforms.Resolve(world);

        // Dragging a crate onto a shelf must not teleport it inside the shelf's local space. Every
        // editor behaves this way and the one that does not is reported on the first afternoon.
        var position = world.Read<WorldTransform>(crate).Value.Translation;

        Assert.Equal(1f, position.X, 3);
        Assert.Equal(2f, position.Y, 3);
        Assert.Equal(3f, position.Z, 3);
    }

    [Fact]
    public void Making_something_a_root_works_and_undoes() {
        var floor = Add("Floor");
        var crate = Add("Crate", floor);

        Assert.True(scene.Reparent(crate, Entity.Null));
        Assert.Equal(Entity.Null, Hierarchy.ParentOf(world, crate));
        Assert.Contains(crate, scene.Roots);

        scene.Stack.Undo();

        Assert.Equal(floor, Hierarchy.ParentOf(world, crate));
    }

    [Fact]
    public void The_step_is_named_for_how_many_it_moved() {
        var shelf = Add("Shelf");
        var floor = Add("Floor");
        var one = Add("One", floor);
        var two = Add("Two", floor);

        Assert.True(scene.Reparent(one, shelf));
        Assert.Equal("Reparent Entity", scene.Stack.UndoName.Value);

        // ⚠ Both are under `floor` and both move. An entity already where it is being sent is
        // filtered out — which is right, and which would have made this a one-entity command and
        // the assertion below a test of nothing.
        Assert.True(scene.Reparent([one, two], Entity.Null));
        Assert.Equal("Reparent Entities", scene.Stack.UndoName.Value);
    }
}
