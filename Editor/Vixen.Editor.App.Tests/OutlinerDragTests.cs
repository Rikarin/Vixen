// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Editor.Testing;
using Vixen.Engine.Transforms;
using Vixen.Ui.Controls.Advanced;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Doc 20 § Part D — Hierarchy: reparent by drag, and reorder among siblings.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Half of this was already wired and the issue reporting it said otherwise.</b>
///         <c>hierarchy.AllowDrag</c> has been true, <c>TreeView</c> has distinguished a drop
///         <i>on</i> a row from one <i>between</i> two rows since it grew a drop indicator, the whole
///         selection travels when the dragged row is part of it, and a drop inside the dragged
///         subtree is refused. What was missing is the last call: <c>Dropped</c> read only
///         <c>node.Parent</c>, so the position the gesture carried was discarded and every drop
///         landed the entity at the head of its new parent's children.
///     </para>
///     <para>
///         Driven through <c>TreeView.MoveNode</c> rather than through synthetic pointer events,
///         because that is the method the drag calls and the one that raises <c>Moved</c> — a test
///         over a pixel path would be testing the gesture recogniser, which has its own suite.
///     </para>
/// </remarks>
public class OutlinerDragTests {
    [Fact]
    public void A_drop_between_two_rows_reorders_the_entities_rather_than_landing_first() {
        using var fixture = EditorSession.Start();

        var (parent, children) = Family(fixture, 4);

        fixture.Settle();

        var tree = fixture.Hierarchy;
        var rows = Nodes(tree, children);

        // Behind the third row, which is what a drop between the third and the fourth means.
        Assert.True(tree.MoveNode(rows[children[0]], rows[children[2]], DropPosition.After));
        fixture.Settle();

        Assert.Equal(
            [children[1], children[2], children[0], children[3]],
            Children(fixture, parent)
        );
    }

    [Fact]
    public void A_drop_onto_a_row_makes_a_child_of_it() {
        using var fixture = EditorSession.Start();

        var (_, children) = Family(fixture, 3);

        fixture.Settle();

        var tree = fixture.Hierarchy;
        var rows = Nodes(tree, children);

        Assert.True(tree.MoveNode(rows[children[0]], rows[children[2]], DropPosition.Into));
        fixture.Settle();

        Assert.Equal(children[2], Hierarchy.ParentOf(fixture.Scene.World, children[0]));
    }

    /// <summary>Dropping a parent into its own descendant is refused, and drawn as refused.</summary>
    /// <remarks>
    ///     ⚠ <b>The refusal is the tree's and it happens before the document is asked.</b>
    ///     <c>TreeView.MoveNode</c> answers false for a target inside the dragged subtree, and the
    ///     aiming pass hides the drop indicator over one — so the gesture never reaches
    ///     <c>ReparentCommand</c>'s own cycle filter. Both exist; this asserts the outer one, because
    ///     it is the one a person sees.
    /// </remarks>
    [Fact]
    public void A_row_cannot_be_dropped_inside_its_own_subtree() {
        using var fixture = EditorSession.Start();

        var outer = fixture.Scene.Add("Drag Outer", LocalTransform.Identity);
        var inner = fixture.Scene.Add("Drag Inner", LocalTransform.Identity);

        fixture.Scene.Reparent(inner, outer);
        fixture.Settle();

        var tree = fixture.Hierarchy;
        var rows = Nodes(tree, [outer, inner]);

        Assert.False(tree.MoveNode(rows[outer], rows[inner], DropPosition.Into));
        Assert.False(tree.MoveNode(rows[outer], rows[inner], DropPosition.After));

        Assert.Equal(outer, Hierarchy.ParentOf(fixture.Scene.World, inner));
        Assert.True(Hierarchy.ParentOf(fixture.Scene.World, outer).IsNull);
    }

    /// <summary>A multi-row drag is one undo step and keeps the rows in order.</summary>
    [Fact]
    public void The_whole_selection_travels_as_one_undoable_move() {
        using var fixture = EditorSession.Start();

        var (parent, children) = Family(fixture, 4);
        var host = fixture.Scene.Add("Drag Host", LocalTransform.Identity);

        fixture.Scene.Selection.Set([children[0], children[1]]);
        fixture.Settle();

        var tree = fixture.Hierarchy;
        var rows = Nodes(tree, [.. children, host]);
        var depth = fixture.Scene.Stack.Depth.Value;

        Assert.True(tree.MoveNode(rows[children[0]], rows[host], DropPosition.Into));
        fixture.Settle();

        Assert.Equal([children[0], children[1]], Children(fixture, host));
        Assert.Equal([children[2], children[3]], Children(fixture, parent));

        // ⚠ One step for two entities. Two would be the shape of every "undo did not undo what I
        // did" report — and it is why the document takes the whole drag rather than one call a row.
        Assert.Equal(depth + 1, fixture.Scene.Stack.Depth.Value);

        fixture.Run("edit.undo").Settle();

        Assert.Equal(children, Children(fixture, parent));
    }

    static List<Entity> Children(EditorSession fixture, Entity entity) {
        List<Entity> children = [];

        foreach (var child in Hierarchy.ChildrenOf(fixture.Scene.World, entity)) {
            children.Add(child);
        }

        return children;
    }

    static Dictionary<Entity, TreeNode> Nodes(TreeView tree, IEnumerable<Entity> wanted) {
        Dictionary<Entity, TreeNode> found = [];
        var looking = wanted.ToHashSet();

        foreach (var node in EditorSession.NodesOf(tree)) {
            if (node.Tag is Entity entity && looking.Contains(entity)) {
                found[entity] = node;
            }
        }

        Assert.Equal(looking.Count, found.Count);

        return found;
    }

    /// <summary>A parent with children, in the sibling order the hierarchy actually holds.</summary>
    static (Entity Parent, Entity[] Children) Family(EditorSession fixture, int count) {
        var parent = fixture.Scene.Add("Drag Parent", LocalTransform.Identity);

        for (var index = 0; index < count; index++) {
            fixture.Scene.Add(
                "Drag Child " + index.ToString(null as IFormatProvider),
                LocalTransform.At(new Vector3(index, 0f, 0f)),
                parent
            );
        }

        return (parent, [.. Children(fixture, parent)]);
    }
}
