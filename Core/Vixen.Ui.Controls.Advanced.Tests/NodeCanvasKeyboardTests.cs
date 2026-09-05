// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Input;
using Xunit;

namespace Vixen.Ui.Controls.Advanced.Tests;

/// <summary>The node canvas walked without a pointer — the fifth and last of #420's six.</summary>
/// <remarks>
///     <para>
///         <b>Keyboard first, role second, in one change.</b> A node announced as an <c>option</c>
///         before anything could reach it would convert "this control is not available to me" into
///         "this control is available and does nothing" — so every test here asserts a keystroke
///         reached a node beside asserting the role. ⚠ #420's own comment records twice that the
///         #422 coverage sweep cannot catch that ordering being broken: its rule is "roleless and
///         focusable is an offender", which says nothing about a role with no keyboard. These tests
///         are what catches it.
///     </para>
///     <para>
///         ⚠ <b>The cursor is not the selection and the difference is the whole design.</b> The
///         arrows walk the graph and Enter chooses, so a keyboard user can pass over six nodes on
///         the way to the seventh without editing anything — which is what a mouse user does by
///         moving the pointer. A canvas that selected on every arrow would put six entries on an
///         editor's undo stack for one journey.
///     </para>
///     <para>
///         ⚠ <b>And the parking hazard is the reason none of this is a tab stop.</b> A
///         <c>NodeItem</c> is pooled and rebound as the canvas pans, so a focus resting on one would
///         land on a different node between one press and the next. The focus stays on the canvas
///         and <c>aria-activedescendant</c> says which node it is on — which means the relation has
///         to be re-pointed after every realise, and
///         <see cref="The_active_descendant_follows_the_pool_across_a_pan" /> is the test that a
///         version which pointed it only on the keypress would fail.
///     </para>
/// </remarks>
[Collection(SharedCatalogue.Name)]
public class NodeCanvasKeyboardTests {
    /// <summary>
    ///     Four nodes: one at the origin, one to its right, one below it, and ⚠ a decoy that is
    ///     nearer along the way right but a long way off it.
    /// </summary>
    /// <remarks>
    ///     The decoy is what distinguishes "next" from "nearest". Scored on distance along the
    ///     direction alone it wins, and Right from the origin would reach a node nobody would
    ///     describe as being to the right of it.
    /// </remarks>
    static (NodeGraph Graph, GraphNode Origin, GraphNode Right, GraphNode Down, GraphNode Decoy) Cross() {
        var graph = new NodeGraph();

        return (
            graph,
            graph.AddNode("origin", new Vector2(0f, 0f)),
            graph.AddNode("right", new Vector2(300f, 0f)),
            graph.AddNode("down", new Vector2(0f, 200f)),
            graph.AddNode("decoy", new Vector2(250f, 400f))
        );
    }

    static NodeCanvas Canvas(AdvancedFixture fixture, NodeGraph graph) {
        var canvas = fixture.Add<NodeCanvas>();

        canvas.Graph = graph;
        fixture.Update();

        canvas.Refresh();
        fixture.Update();

        Assert.True(fixture.Document.Focus(canvas));

        return canvas;
    }

    [Fact]
    public void A_bound_node_is_an_option_in_the_listbox_the_surface_carries() {
        using var fixture = new AdvancedFixture();

        var (graph, origin, _, _, _) = Cross();
        var canvas = Canvas(fixture, graph);

        Assert.Equal(AccessibleRole.ListBox, canvas.Surface.Role);
        Assert.Equal(
            AccessibleStates.MultiSelectable,
            canvas.Surface.AccessibleState & AccessibleStates.MultiSelectable
        );

        var item = canvas.ItemOf(origin)!;

        Assert.Equal(AccessibleRole.Option, item.Role);

        // Named by the node's title rather than by its content: an option is named from what is
        // inside it, and what is inside this one is two columns of ports.
        Assert.Equal("origin", item.AccessibleName);
        Assert.Equal(AccessibleStates.None, item.AccessibleState & AccessibleStates.Selected);

        canvas.Select(origin);
        fixture.Update();

        Assert.Equal(AccessibleStates.Selected, item.AccessibleState & AccessibleStates.Selected);
    }

    [Fact]
    public void A_parked_item_leaves_the_tree_rather_than_announcing_an_empty_option() {
        using var fixture = new AdvancedFixture();

        var graph = new NodeGraph();
        var node = graph.AddNode("only", new Vector2(0f, 0f));

        var canvas = Canvas(fixture, graph);
        var item = canvas.ItemOf(node)!;

        Assert.Equal(AccessibleRole.Option, item.Role);

        // Far enough that the node is outside the overscan, which parks the one element there is.
        canvas.Pan = new Vector2(4_000f, 0f);
        fixture.Update();

        Assert.Null(canvas.ItemOf(node));
        Assert.Equal(AccessibleRole.None, item.Role);
        Assert.False(item.IsInAccessibilityTree);

        canvas.Pan = Vector2.Zero;
        fixture.Update();

        // And it comes back, because `ClearRole` hands the native role back rather than assigning
        // one over it.
        Assert.Equal(AccessibleRole.Option, canvas.ItemOf(node)!.Role);
    }

    [Fact]
    public void The_arrows_walk_to_the_next_node_and_move_nothing() {
        using var fixture = new AdvancedFixture();

        var (graph, origin, right, down, decoy) = Cross();
        var canvas = Canvas(fixture, graph);

        var places = graph.Nodes.Select(static node => node.Position).ToArray();

        // From nothing, the nearest node to the middle of the view — never one off in the dark.
        fixture.Type(InputKey.Right);
        Assert.NotNull(canvas.Cursor);

        canvas.Cursor = origin;

        fixture.Type(InputKey.Right);

        // ⚠ `right` and not `decoy`, which is nearer along the way right and a long way off it. A
        // canvas that scored candidates on the distance along the direction alone would answer the
        // decoy, and a reader would have no way to predict where an arrow goes.
        Assert.Same(right, canvas.Cursor);

        canvas.Cursor = origin;
        fixture.Type(InputKey.Down);
        Assert.Same(down, canvas.Cursor);

        fixture.Type(InputKey.Up);
        Assert.Same(origin, canvas.Cursor);

        // Nothing above the origin: the cursor stays rather than wrapping round to the bottom of a
        // graph, which on a canvas is a jump to somewhere the reader was not.
        fixture.Type(InputKey.Up);
        Assert.Same(origin, canvas.Cursor);

        // And the whole point of "next item rather than move": not one node changed places, and
        // nothing was selected on the way.
        Assert.Equal(places, graph.Nodes.Select(static node => node.Position));
        Assert.Empty(canvas.Selection);
        Assert.NotSame(decoy, canvas.Cursor);
    }

    [Fact]
    public void Enter_selects_what_the_cursor_is_on_and_control_adds_to_it() {
        using var fixture = new AdvancedFixture();

        var (graph, origin, right, _, _) = Cross();
        var canvas = Canvas(fixture, graph);

        canvas.Cursor = origin;

        fixture.Type(InputKey.Enter);
        Assert.Same(origin, Assert.Single(canvas.Selection));

        fixture.Type(InputKey.Right);

        // Still one: walking over a node is not choosing it.
        Assert.Same(origin, Assert.Single(canvas.Selection));

        fixture.Type(InputKey.Space, ModifierKeys.Control);
        Assert.Equal(2, canvas.Selection.Count);
        Assert.Contains(right, canvas.Selection);

        // Control again takes it back out, which is what Control means to a click here.
        fixture.Type(InputKey.Space, ModifierKeys.Control);
        Assert.Same(origin, Assert.Single(canvas.Selection));
    }

    [Fact]
    public void A_cursor_put_on_a_culled_node_brings_it_into_view() {
        using var fixture = new AdvancedFixture();

        var graph = new NodeGraph();

        graph.AddNode("near", new Vector2(0f, 0f));
        var far = graph.AddNode("far", new Vector2(3_000f, 0f));

        var canvas = Canvas(fixture, graph);

        // The element does not exist yet, which is the state the cursor has to survive: a pool is
        // the size of the viewport and a graph is not.
        Assert.Null(canvas.ItemOf(far));

        canvas.Cursor = far;
        fixture.Update();

        Assert.NotNull(canvas.ItemOf(far));
        Assert.True(canvas.View.Contains(canvas.RectOf(far).Center));

        // Panned, never zoomed: an arrow press that rescaled the graph would move everything the
        // reader had a picture of.
        Assert.Equal(1f, canvas.Zoom, 4);
    }

    [Fact]
    public void The_active_descendant_follows_the_pool_across_a_pan() {
        using var fixture = new AdvancedFixture();

        var graph = new NodeGraph();

        var first = graph.AddNode("first", new Vector2(0f, 0f));
        graph.AddNode("second", new Vector2(300f, 0f));

        var canvas = Canvas(fixture, graph);

        canvas.Cursor = first;

        Assert.Same(
            canvas.ItemOf(first),
            canvas.AccessibleRelationTarget(AccessibleRelation.ActiveDescendant)
        );

        // Out of sight: there is no element to point at, and pointing at a parked one would name
        // whichever node it is given next.
        canvas.Pan = new Vector2(4_000f, 0f);
        fixture.Update();

        Assert.Null(canvas.ItemOf(first));
        Assert.Null(canvas.AccessibleRelationTarget(AccessibleRelation.ActiveDescendant));

        canvas.Pan = Vector2.Zero;
        fixture.Update();

        var target = canvas.AccessibleRelationTarget(AccessibleRelation.ActiveDescendant);

        Assert.Same(canvas.ItemOf(first), target);
        Assert.Same(first, Assert.IsType<NodeItem>(target).Node);

        // One relation, not a trail of every element the cursor has ever been shown by.
        Assert.Single(canvas.AccessibleRelationships);
    }

    [Fact]
    public void Deleting_the_node_under_the_cursor_takes_the_cursor_with_it() {
        using var fixture = new AdvancedFixture();

        var (graph, origin, _, _, _) = Cross();
        var canvas = Canvas(fixture, graph);

        canvas.Cursor = origin;
        fixture.Type(InputKey.Enter);

        fixture.Type(InputKey.Delete);

        Assert.Null(canvas.Cursor);
        Assert.Null(canvas.AccessibleRelationTarget(AccessibleRelation.ActiveDescendant));

        // And the arrows still work afterwards, from the middle of the view rather than from a node
        // that is no longer in the graph.
        fixture.Type(InputKey.Right);

        Assert.NotNull(canvas.Cursor);
        Assert.Contains(canvas.Cursor, graph.Nodes);
    }
}
