// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Input;
using Xunit;

namespace Vixen.Ui.Controls.Advanced.Tests;

/// <summary>The nodes on a graph, reached without a pointer — the fifth of #420's six.</summary>
/// <remarks>
///     <para>
///         <b>Keyboard first, role second, in one change.</b> Every test here asserts a keystroke
///         <i>moved the keyboard</i> beside asserting the role, because #420's own comments record
///         twice that the coverage sweep cannot catch that ordering being broken: its rule is
///         "roleless and focusable is an offender", which says nothing about a widget role with no
///         keyboard behind it. This file is what catches it for <see cref="NodeItem" />.
///     </para>
///     <para>
///         ⚠ <b>The keyboard reaches a node without the focus ever entering the pool</b>, and that
///         is this control's answer to the hazard #420 names for it. A <see cref="NodeItem" /> is
///         rebound to a different node on every pan, so a roving tab stop over the items would be a
///         stop on whichever node scrolled into that slot. The canvas keeps the focus it already
///         had, the arrows move <see cref="NodeCanvas.Cursor" />, and the item showing that node is
///         the canvas's <see cref="AccessibleRelation.ActiveDescendant" /> — which
///         <see cref="The_active_descendant_follows_the_node_and_not_the_slot" /> holds across
///         exactly the rebinding that would break a tab stop.
///     </para>
///     <para>
///         ⚠ <b>The arrows mean <i>next node</i> and never <i>move the node</i></b>, which is doc
///         46 § A2's row for this control and is asserted directly: a canvas whose arrows moved the
///         selection would hand a keyboard user an edit where a pointer user gets navigation, and
///         no key would then reach the node beside it.
///     </para>
/// </remarks>
[Collection(SharedCatalogue.Name)]
public class NodeCanvasKeyboardTests {
    static NodeCanvas Canvas(AdvancedFixture fixture, NodeGraph graph) {
        var canvas = fixture.Add<NodeCanvas>();

        canvas.Graph = graph;
        fixture.Update();

        canvas.Refresh();
        fixture.Update();

        Assert.True(fixture.Document.Focus(canvas));

        return canvas;
    }

    /// <summary>Four nodes on a cross, so that every arrow has one obvious answer and three wrong ones.</summary>
    static (NodeGraph Graph, GraphNode Middle, GraphNode Left, GraphNode Right, GraphNode Above, GraphNode Below)
        Cross() {
        var graph = new NodeGraph();

        var middle = graph.AddNode("middle", new Vector2(300f, 250f));
        var left = graph.AddNode("left", new Vector2(60f, 250f));
        var right = graph.AddNode("right", new Vector2(540f, 250f));
        var above = graph.AddNode("above", new Vector2(300f, 40f));
        var below = graph.AddNode("below", new Vector2(300f, 460f));

        return (graph, middle, left, right, above, below);
    }

    [Fact]
    public void A_node_is_a_named_group_that_says_whether_it_is_selected() {
        using var fixture = new AdvancedFixture();

        var (graph, middle, _, _, _, _) = Cross();
        var canvas = Canvas(fixture, graph);

        var item = canvas.ItemOf(middle);
        Assert.NotNull(item);

        // ⚠ `Group` rather than `Option`: the element above it is `application`, and an `option`
        // outside a `listbox` describes a membership nothing in this tree offers.
        Assert.Equal(AccessibleRole.Group, item.Role);
        Assert.Equal("middle", item.AccessibleName);
        Assert.Equal(AccessibleStates.None, item.AccessibleState & AccessibleStates.Selected);

        canvas.Select(middle);
        fixture.Update();

        Assert.Equal(AccessibleStates.Selected, item.AccessibleState & AccessibleStates.Selected);
    }

    [Fact]
    public void The_first_arrow_press_selects_and_moves_nothing() {
        using var fixture = new AdvancedFixture();

        var (graph, middle, _, _, _, _) = Cross();
        var canvas = Canvas(fixture, graph);

        var places = graph.Nodes.Select(static node => node.Position).ToArray();

        Assert.Null(canvas.Cursor);
        Assert.Empty(canvas.Selection);

        fixture.Type(InputKey.Right);

        // The node nearest the middle of the view, which is the one the user is looking at — not a
        // node the arrow's direction picked out of a corner of the graph.
        Assert.Same(middle, canvas.Cursor);
        Assert.Same(middle, Assert.Single(canvas.Selection));

        // ⚠ And the graph is where it was. An arrow that moved a node would be an edit offered to a
        // keyboard user in place of the navigation a pointer user gets.
        Assert.Equal(places, graph.Nodes.Select(static node => node.Position).ToArray());
    }

    [Fact]
    public void The_arrows_step_to_the_node_that_way_and_come_back() {
        using var fixture = new AdvancedFixture();

        var (graph, middle, left, right, above, below) = Cross();
        var canvas = Canvas(fixture, graph);

        canvas.Select(middle);

        fixture.Type(InputKey.Right);
        Assert.Same(right, canvas.Cursor);

        // ⚠ Left after Right comes back. A plain nearest-neighbour search satisfies "Right lands on
        // something to the right" and fails this, which is what makes spatial navigation feel
        // broken: two keys that do not undo each other.
        fixture.Type(InputKey.Left);
        Assert.Same(middle, canvas.Cursor);

        fixture.Type(InputKey.Up);
        Assert.Same(above, canvas.Cursor);

        fixture.Type(InputKey.Down);
        Assert.Same(middle, canvas.Cursor);

        fixture.Type(InputKey.Down);
        Assert.Same(below, canvas.Cursor);

        fixture.Type(InputKey.Up);
        fixture.Type(InputKey.Left);
        Assert.Same(left, canvas.Cursor);

        // The end of the row: there is nothing further left, so the keyboard stays where it is
        // rather than wrapping round to the far side of a graph the user cannot see.
        fixture.Type(InputKey.Left);
        Assert.Same(left, canvas.Cursor);

        // And every step replaced the selection, because none of them held Shift.
        Assert.Same(left, Assert.Single(canvas.Selection));
    }

    [Fact]
    public void Shift_and_an_arrow_gathers_the_nodes_it_steps_through() {
        using var fixture = new AdvancedFixture();

        var (graph, middle, _, right, _, _) = Cross();
        var canvas = Canvas(fixture, graph);

        canvas.Select(middle);
        fixture.Type(InputKey.Right, ModifierKeys.Shift);

        Assert.Same(right, canvas.Cursor);
        Assert.Equal(2, canvas.Selection.Count);
        Assert.Contains(middle, canvas.Selection);
        Assert.Contains(right, canvas.Selection);
    }

    [Fact]
    public void Enter_activates_the_node_the_keyboard_is_on() {
        using var fixture = new AdvancedFixture();

        var (graph, middle, _, right, _, _) = Cross();
        var canvas = Canvas(fixture, graph);

        var activated = new List<GraphNode>();
        canvas.Activated += (_, node) => activated.Add(node);

        // Nothing is chosen yet, so Enter has nothing to open — and must not invent one.
        fixture.Type(InputKey.Enter);
        Assert.Empty(activated);

        canvas.Select(middle);
        fixture.Type(InputKey.Right);
        fixture.Type(InputKey.Enter);

        Assert.Same(right, Assert.Single(activated));

        // Space says the same thing, which is what a screen reader tells a user about an item.
        fixture.Type(InputKey.Space);
        Assert.Equal(2, activated.Count);
    }

    [Fact]
    public void Escape_lets_go_of_the_selection() {
        using var fixture = new AdvancedFixture();

        var (graph, middle, _, _, _, _) = Cross();
        var canvas = Canvas(fixture, graph);

        canvas.Select(middle);
        Assert.NotEmpty(canvas.Selection);

        fixture.Type(InputKey.Escape);

        Assert.Empty(canvas.Selection);
        Assert.Null(canvas.Cursor);
    }

    [Fact]
    public void A_step_to_a_node_off_screen_brings_it_into_view() {
        using var fixture = new AdvancedFixture();
        var graph = new NodeGraph();

        var first = graph.AddNode("first", new Vector2(0f, 0f));
        var far = graph.AddNode("far", new Vector2(3_000f, 0f));

        var canvas = Canvas(fixture, graph);

        canvas.Select(first);
        fixture.Update();

        Assert.Null(canvas.ItemOf(far));

        fixture.Type(InputKey.Right);
        fixture.Update();

        Assert.Same(far, canvas.Cursor);

        // ⚠ The half that makes the rest of the keyboard true: a node stepped to while off screen
        // has no element at all, so nothing could be announced and the selection would be invisible.
        var item = canvas.ItemOf(far);
        Assert.NotNull(item);
        Assert.Same(item, canvas.AccessibleRelationTarget(AccessibleRelation.ActiveDescendant));

        // Panned the least that puts it on screen rather than centred: the node it stepped from is
        // now off the other edge, and the view did not jump past what it was asked for.
        Assert.True(canvas.View.X <= far.Position.X, $"the view starts at {canvas.View.X}, past the node");
    }

    [Fact]
    public void The_active_descendant_follows_the_node_and_not_the_slot() {
        using var fixture = new AdvancedFixture();
        var graph = new NodeGraph();

        for (var i = 0; i < 400; i++) {
            graph.AddNode($"node{i}", new Vector2(i * 300f, 0f));
        }

        var canvas = Canvas(fixture, graph);
        var chosen = graph.Nodes[1];

        canvas.Select(chosen);
        fixture.Update();

        var item = canvas.ItemOf(chosen);
        Assert.NotNull(item);
        Assert.Same(item, canvas.AccessibleRelationTarget(AccessibleRelation.ActiveDescendant));

        // ⚠ The pan that rebinds the pool. `Items[1]` is a different node afterwards — that sharing
        // is what makes a pan cost property writes — so a relation set once when the keyboard moved
        // would now be pointing a screen reader at a node nobody chose.
        canvas.Pan = new Vector2(3_000f, 0f);
        fixture.Update();

        Assert.NotSame(chosen, item.Node);
        Assert.Null(canvas.ItemOf(chosen));
        Assert.Null(canvas.AccessibleRelationTarget(AccessibleRelation.ActiveDescendant));

        // And back: the cursor never moved, so the element showing it is the active descendant
        // again — a different element from the one it was before, which is the whole point.
        canvas.Pan = Vector2.Zero;
        fixture.Update();

        Assert.Same(chosen, canvas.Cursor);

        var again = canvas.ItemOf(chosen);
        Assert.NotNull(again);
        Assert.Same(again, canvas.AccessibleRelationTarget(AccessibleRelation.ActiveDescendant));
    }

    [Fact]
    public void A_deleted_node_takes_the_keyboard_with_it() {
        using var fixture = new AdvancedFixture();

        var (graph, middle, _, _, _, _) = Cross();
        var canvas = Canvas(fixture, graph);

        canvas.Select(middle);
        fixture.Type(InputKey.Delete);
        fixture.Update();

        // ⚠ Cleared rather than left pointing at a node the graph no longer holds: the next arrow
        // steps from the cursor's rectangle, and a stale one would step from a node nobody can see.
        Assert.Null(canvas.Cursor);
        Assert.Null(canvas.AccessibleRelationTarget(AccessibleRelation.ActiveDescendant));
        Assert.DoesNotContain(middle, graph.Nodes);

        // And the keyboard still works afterwards, from the middle of the view.
        fixture.Type(InputKey.Right);
        Assert.NotNull(canvas.Cursor);
    }

    [Fact]
    public void A_field_on_a_node_keeps_the_arrows_it_needs() {
        using var fixture = new AdvancedFixture();
        var graph = new NodeGraph();

        var node = graph.AddNode("node", new Vector2(60f, 60f));
        var port = node.AddInput("value");

        port.Editor = new PortEditor(PortEditorKind.Number);
        graph.AddNode("other", new Vector2(400f, 60f));

        var canvas = Canvas(fixture, graph);
        var item = canvas.ItemOf(node);

        Assert.NotNull(item);

        var box = item.Inputs[0].Fields.Boxes[0];
        Assert.True(fixture.Document.Focus(box));
        Assert.Same(box, fixture.Document.Focused);

        canvas.Select(node);
        var zoom = canvas.Zoom;

        fixture.Type(InputKey.Right);

        // An arrow inside a number box is the box's caret and not a step to the node beside it.
        // Without this the keyboard model added here would have made every inline editor on a
        // canvas unusable.
        Assert.Same(node, canvas.Cursor);

        // ⚠ And it is <c>TextField</c> marking the key handled that does it, not the canvas's
        // `Typing` guard: gating `Typing` to false leaves the assertion above green, because the
        // canvas's handler is an ordinary one whose `handledEventsToo` is false and never runs at
        // all. `Typing` is what stops the keys a field does *not* answer — F zooms to fit, and
        // typing the letter f into a value is how that would have been discovered in the editor.
        fixture.Type(InputKey.F);
        Assert.Equal(zoom, canvas.Zoom);
    }
}
