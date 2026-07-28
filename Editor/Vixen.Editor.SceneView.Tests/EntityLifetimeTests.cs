// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Engine.Transforms;
using Xunit;

namespace Vixen.Editor.SceneView.Tests;

/// <summary>Deleting entities and getting them back, with everything that made them what they were.</summary>
/// <remarks>
///     The handle is the part the ECS gives back. These are about the other four: the components, the
///     name, the stable id, and where the entity sat among its siblings. An undo that returns an
///     entity to the wrong place in the tree has not undone anything a user would recognise.
/// </remarks>
public sealed class EntityLifetimeTests : IDisposable {
    readonly World world = new("Test");
    readonly SceneDocument document;

    public EntityLifetimeTests() {
        var project = new EditorProject(new ProjectPaths(Path.Combine(Path.GetTempPath(), "vixen-lifetime")));
        document = new SceneDocument(project, world, AssetId.Empty, "Test");
    }

    public void Dispose() => world.Dispose();

    static List<Entity> Children(World world, Entity entity) {
        List<Entity> children = [];

        foreach (var child in Hierarchy.ChildrenOf(world, entity)) {
            children.Add(child);
        }

        return children;
    }

    /// <summary>A parent with children, and the order the hierarchy actually holds them in.</summary>
    /// <remarks>
    ///     ⚠ <b>Read back rather than assumed.</b> <c>Add</c> links, and linking prepends — so the
    ///     creation order is the reverse of the sibling order. A fixture that assumed otherwise would
    ///     make every ordering assertion below test the fixture's mistake rather than the restore.
    /// </remarks>
    (Entity Parent, Entity[] Children) Family(int count) {
        var parent = document.Add("Parent", LocalTransform.Identity);

        for (var index = 0; index < count; index++) {
            document.Add(
                "Child " + index.ToString(null as IFormatProvider),
                LocalTransform.At(new Vector3(index, 0f, 0f)),
                parent
            );
        }

        return (parent, [.. Children(world, parent)]);
    }

    [Fact]
    public void Deleting_takes_the_entity_and_undoing_gives_it_back() {
        var entity = document.Add("Crate", LocalTransform.At(new Vector3(1f, 2f, 3f)));

        Assert.True(document.Delete([entity]));
        Assert.False(world.IsAlive(entity));

        Assert.True(document.Stack.Undo());

        // The same handle, which is the whole reason `World.TryRecreate` exists.
        Assert.True(world.IsAlive(entity));
        Assert.Equal("Crate", document.NameOf(entity));
    }

    [Fact]
    public void The_components_come_back_with_their_values() {
        var entity = document.Add("Crate", LocalTransform.At(new Vector3(4f, 5f, 6f)));

        document.Delete([entity]);
        document.Stack.Undo();

        // A restore that gave back a live handle with zeroed components would pass every "is it
        // alive" assertion and lose the entity's position, which is the thing a user notices.
        Assert.Equal(new Vector3(4f, 5f, 6f), world.Read<LocalTransform>(entity).Position);
    }

    [Fact]
    public void The_whole_subtree_goes_and_the_whole_subtree_returns() {
        var (parent, children) = Family(3);

        document.Delete([parent]);

        Assert.False(world.IsAlive(parent));
        Assert.All(children, child => Assert.False(world.IsAlive(child)));

        document.Stack.Undo();

        Assert.True(world.IsAlive(parent));
        Assert.All(children, child => Assert.True(world.IsAlive(child)));
        Assert.Equal(children, Children(world, parent));
    }

    [Fact]
    public void A_child_comes_back_between_the_siblings_it_was_between() {
        var (parent, children) = Family(5);

        document.Delete([children[2]]);
        Assert.Equal([children[0], children[1], children[3], children[4]], Children(world, parent));

        document.Stack.Undo();

        // ⚠ The assertion the whole `SetParentAfter` primitive exists for. Linking prepends, so an
        // undo written without it puts the third child back first — an undo that did not undo.
        Assert.Equal(children, Children(world, parent));
    }

    [Fact]
    public void A_first_child_comes_back_first() {
        var (parent, children) = Family(4);

        document.Delete([children[0]]);
        document.Stack.Undo();

        Assert.Equal(children, Children(world, parent));
    }

    [Fact]
    public void A_last_child_comes_back_last() {
        var (parent, children) = Family(4);

        document.Delete([children[3]]);
        document.Stack.Undo();

        Assert.Equal(children, Children(world, parent));
    }

    [Fact]
    public void Several_children_deleted_at_once_come_back_in_order() {
        var (parent, children) = Family(5);

        document.Delete([children[1], children[3]]);
        Assert.Equal([children[0], children[2], children[4]], Children(world, parent));

        document.Stack.Undo();

        Assert.Equal(children, Children(world, parent));
    }

    [Fact]
    public void The_stable_id_survives_a_delete_and_an_undo() {
        var entity = document.Add("Crate", LocalTransform.Identity);
        var id = document.IdOf(entity);

        document.Delete([entity]);
        document.Stack.Undo();

        // ⚠ What a saved file names it. A restored entity with a fresh id would be a new object as
        // far as every reference in the project is concerned, which is a delete that broke links an
        // undo claimed to repair.
        Assert.Equal(id, document.IdOf(entity));
        Assert.True(document.TryGetEntity(id, out var found));
        Assert.Equal(entity, found);
    }

    [Fact]
    public void An_entity_that_was_never_named_does_not_come_back_named() {
        var entity = Hierarchy.CreateTransform(world, LocalTransform.Identity);

        document.Scenes.Adopt(document.Scene, entity);
        Assert.False(document.TryGetName(entity, out _));

        document.Delete([entity]);
        document.Stack.Undo();

        // The generated "Entity 7" is a rendering of a handle and not a name. Assigning it on restore
        // would turn an unnamed entity into one whose name is a number that means nothing.
        Assert.False(document.TryGetName(entity, out _));
    }

    [Fact]
    public void Redo_deletes_it_again_and_a_second_undo_still_works() {
        var (parent, children) = Family(3);

        document.Delete([parent]);
        document.Stack.Undo();
        Assert.True(document.Stack.Redo());

        Assert.False(world.IsAlive(parent));

        document.Stack.Undo();

        Assert.True(world.IsAlive(parent));
        Assert.Equal(children, Children(world, parent));
    }

    [Fact]
    public void Creating_is_undoable_and_the_redo_gives_the_same_handle_back() {
        var entity = document.Create("Barrel", LocalTransform.At(new Vector3(9f, 0f, 0f)));

        Assert.True(world.IsAlive(entity));

        Assert.True(document.Stack.Undo());
        Assert.False(world.IsAlive(entity));

        Assert.True(document.Stack.Redo());

        // The same handle on redo, so anything that recorded it between the create and the undo —
        // another command on the stack, most obviously — still names the right entity.
        Assert.True(world.IsAlive(entity));
        Assert.Equal(new Vector3(9f, 0f, 0f), world.Read<LocalTransform>(entity).Position);
        Assert.Equal("Barrel", document.NameOf(entity));
    }

    [Fact]
    public void Deleting_a_parent_and_its_own_child_together_is_not_a_double_delete() {
        var (parent, children) = Family(2);

        // A selection can hold both; the parent's delete takes the child with it, so the child's own
        // entry finds nothing alive by the time it is reached.
        Assert.True(document.Delete([parent, children[0]]));

        document.Stack.Undo();

        Assert.True(world.IsAlive(parent));
        Assert.Equal(children, Children(world, parent));
    }

    [Fact]
    public void Deleting_nothing_alive_is_not_an_undo_entry() {
        var entity = document.Add("Crate", LocalTransform.Identity);

        world.Destroy(entity);

        Assert.False(document.Delete([entity]));
        Assert.False(document.Stack.CanUndo.Value);
    }

    [Fact]
    public void A_delete_marks_the_document_dirty_and_an_undo_takes_it_back() {
        var entity = document.Add("Crate", LocalTransform.Identity);

        document.Stack.MarkClean();
        document.Delete([entity]);

        Assert.True(document.IsDirty.Value);

        document.Stack.Undo();

        Assert.False(document.IsDirty.Value);
    }

    [Fact]
    public void An_undo_whose_slot_was_taken_refuses_rather_than_half_restoring() {
        var (parent, children) = Family(2);

        document.Delete([parent]);

        // Something else takes the slots the delete freed. In the editor this is another document or
        // a play-mode restore; here it is the shortest way to reach the state.
        List<Entity> thieves = [];

        for (var index = 0; index < 8; index++) {
            thieves.Add(world.Create());
        }

        Assert.Contains(thieves, thief => thief.Id == parent.Id || children.Any(child => child.Id == thief.Id));
        Assert.Throws<InvalidOperationException>(() => document.Stack.Undo());

        // Nothing half-restored: the world holds the thieves and nothing the command was keeping.
        Assert.False(world.IsAlive(parent));
    }
}
