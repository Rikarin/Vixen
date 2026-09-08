// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Engine.Transforms;
using Xunit;

namespace Vixen.Editor.SceneView.Tests;

/// <summary>Doc 20 § Part D's second ⛔: reordering among siblings, which had no way in.</summary>
/// <remarks>
///     <para>
///         <b><c>Hierarchy.SetParentAfter</c> has existed since the outliner did, and
///         <see cref="ReparentCommand" /> used it to <i>undo</i> a move.</b> What nothing could say
///         was where a move should <i>land</i>: <c>Do</c> placed every entity with no neighbour,
///         which links — and linking prepends. So a drop landed at the head of its new parent's
///         children whatever the pointer had aimed at, and a drop between two rows of one parent was
///         refused outright as "already under that parent".
///     </para>
///     <para>
///         ⚠ <b>The multi-entity assertions are the ones worth the file.</b> Placing several entities
///         behind one neighbour reverses them; each has to land behind the one before it, and the
///         redundancy check has to be over that chain rather than per entity — a first row that is
///         already where it is going is not a reason to leave the rest in front of it.
///     </para>
/// </remarks>
public sealed class SiblingOrderTests : IDisposable {
    readonly World world = new("Test");
    readonly SceneDocument document;

    public SiblingOrderTests() {
        var project = new EditorProject(new ProjectPaths(Path.Combine(Path.GetTempPath(), "vixen-sibling-order")));
        document = new SceneDocument(project, world, AssetId.Empty, "Test");
    }

    public void Dispose() => world.Dispose();

    [Fact]
    public void A_drop_between_two_rows_of_one_parent_reorders_them() {
        var (parent, children) = Family(4);

        // ⚠ Refused entirely before there was a destination position: the parent does not change, so
        // the command filtered the move out and `Reparent` answered false.
        Assert.True(document.Reparent([children[0]], parent, children[2]));

        Assert.Equal([children[1], children[2], children[0], children[3]], Children(parent));
    }

    [Fact]
    public void A_drop_at_the_head_of_a_list_lands_first() {
        var (parent, children) = Family(3);

        Assert.True(document.Reparent([children[2]], parent, Entity.Null));
        Assert.Equal([children[2], children[0], children[1]], Children(parent));
    }

    [Fact]
    public void A_move_that_changes_neither_the_parent_nor_the_neighbour_is_refused() {
        var (parent, children) = Family(3);

        // Already directly behind the first child, which is where it is being asked to go.
        Assert.False(document.Reparent([children[1]], parent, children[0]));
        Assert.Equal(children, Children(parent));
    }

    [Fact]
    public void Several_entities_dropped_together_keep_the_order_they_were_given() {
        var (parent, children) = Family(5);
        var (other, _) = Family(0);

        Assert.True(document.Reparent([children[0], children[1], children[2]], other, Entity.Null));

        // Behind one another rather than all behind the same neighbour, which reverses them.
        Assert.Equal([children[0], children[1], children[2]], Children(other));
        Assert.Equal([children[3], children[4]], Children(parent));
    }

    /// <summary>A chain whose first link is already in place still moves the rest.</summary>
    /// <remarks>
    ///     ⚠ <b>Filtering per entity passes every other test in this file and fails this one.</b>
    ///     Dropping A and B behind a neighbour when A is already there would skip A, and B would then
    ///     land behind the neighbour — in front of A rather than behind it.
    /// </remarks>
    [Fact]
    public void A_chain_whose_first_entity_is_already_in_place_still_moves_the_rest() {
        var (parent, children) = Family(4);

        // children[1] already sits directly behind children[0]; children[3] does not.
        Assert.True(document.Reparent([children[1], children[3]], parent, children[0]));

        Assert.Equal([children[0], children[1], children[3], children[2]], Children(parent));
    }

    [Fact]
    public void An_undo_of_a_reorder_puts_it_back_between_the_siblings_it_was_between() {
        var (parent, children) = Family(4);

        document.Reparent([children[2]], parent, Entity.Null);
        Assert.Equal([children[2], children[0], children[1], children[3]], Children(parent));

        document.Stack.Undo();
        Assert.Equal(children, Children(parent));
    }

    /// <summary>A neighbour that is itself being dragged is walked back from, not thrown on.</summary>
    /// <remarks>
    ///     ⚠ <c>Hierarchy.SetParentAfter</c> throws on a neighbour under a different parent, which is
    ///     right for the primitive and wrong for a drag — the row somebody aimed at may be one of the
    ///     rows they are dragging.
    /// </remarks>
    [Fact]
    public void A_destination_neighbour_that_is_itself_moving_degrades_rather_than_throwing() {
        var (parent, children) = Family(4);
        var (other, _) = Family(0);

        Assert.True(document.Reparent([children[0], children[1]], other, children[1]));
        Assert.Equal([children[0], children[1]], Children(other));
    }

    List<Entity> Children(Entity entity) {
        List<Entity> children = [];

        foreach (var child in Hierarchy.ChildrenOf(world, entity)) {
            children.Add(child);
        }

        return children;
    }

    /// <summary>A parent with children, and the order the hierarchy actually holds them in.</summary>
    /// <remarks>
    ///     ⚠ Read back rather than assumed: <c>Add</c> links, and linking prepends, so the creation
    ///     order is the reverse of the sibling order.
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

        return (parent, [.. Children(parent)]);
    }
}
