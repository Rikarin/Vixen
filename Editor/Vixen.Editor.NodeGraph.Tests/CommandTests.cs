// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Editor.Core;
using Vixen.Editor.NodeGraph;
using Xunit;

namespace Tests;

/// <summary>Every graph edit as an undoable command, and what redo has to reproduce exactly.</summary>
public class CommandTests : IDisposable {
    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-graph-" + Guid.NewGuid().ToString("N"));
    readonly EditorProject project;
    readonly ScratchDocument document;
    readonly NodeGraphModel graph = new();

    public CommandTests() {
        Directory.CreateDirectory(root);

        project = new(new ProjectPaths(root));
        document = new(project);
    }

    public void Dispose() {
        if (Directory.Exists(root)) {
            Directory.Delete(root, true);
        }

        GC.SuppressFinalize(this);
    }

    CommandStack Stack => document.Stack;

    [Fact]
    public void An_added_node_comes_back_under_the_identity_it_had() {
        var command = new AddNodeCommand(graph, "Test/Colour", new(40f, 40f), document);

        Stack.Execute(command);

        var id = command.Node.Id;

        Assert.True(Stack.Undo());
        Assert.False(graph.TryGet(id, out _));

        Assert.True(Stack.Redo());
        Assert.True(graph.TryGet(id, out var back));

        // ⚠ The same identity, which is what lets a connection made after this survive an undo and a
        // redo of both — the edge that names it is restored by name rather than rewritten.
        Assert.Equal("Test/Colour", back.Type);
    }

    [Fact]
    public void Deleting_a_node_takes_its_wires_and_puts_them_back() {
        var source = graph.Add("Test/Colour");
        var middle = graph.Add("Test/Combine");
        var sink = graph.Add("Test/Combine");

        graph.Connect(new(source.Id, "Out"), new(middle.Id, "A"));
        graph.Connect(new(middle.Id, "Out"), new(sink.Id, "A"));

        Stack.Execute(new RemoveNodesCommand(graph, [middle.Id], document));

        Assert.Empty(graph.Edges);

        Assert.True(Stack.Undo());
        Assert.Equal(2, graph.Edges.Count);
        Assert.Equal(new PortRef(source.Id, "Out"), graph.Source(new(middle.Id, "A")));
        Assert.Equal(new PortRef(middle.Id, "Out"), graph.Source(new(sink.Id, "A")));
    }

    [Fact]
    public void Deleting_a_grouped_node_puts_it_back_where_it_was_in_the_group() {
        var first = graph.Add("Test/Colour");
        var second = graph.Add("Test/Colour");
        var third = graph.Add("Test/Colour");

        var group = new GraphGroup { Title = "Three" };
        group.Nodes.AddRange([first.Id, second.Id, third.Id]);
        graph.Groups.Add(group);

        Stack.Execute(new RemoveNodesCommand(graph, [second.Id], document));

        Assert.Equal([first.Id, third.Id], group.Nodes);

        Stack.Undo();

        // In the middle, not on the end. A group box whose shape changed because a node came back in
        // the wrong place is a change nobody made.
        Assert.Equal([first.Id, second.Id, third.Id], group.Nodes);
    }

    [Fact]
    public void Deleting_a_selection_is_one_entry_in_the_history() {
        var first = graph.Add("Test/Colour");
        var second = graph.Add("Test/Colour");

        Stack.Execute(new RemoveNodesCommand(graph, [first.Id, second.Id], document));

        Assert.Equal(1, Stack.Depth.Value);
        Assert.Equal("Delete 2 Nodes", Stack.UndoName.Value);
    }

    [Fact]
    public void Undoing_a_connection_puts_back_the_wire_it_displaced() {
        var first = graph.Add("Test/Colour");
        var second = graph.Add("Test/Vector");
        var sink = graph.Add("Test/Combine");

        graph.Connect(new(first.Id, "Out"), new(sink.Id, "A"));

        Stack.Execute(new ConnectCommand(graph, new(second.Id, "Out"), new(sink.Id, "A"), document));

        Assert.Equal(new PortRef(second.Id, "Out"), graph.Source(new(sink.Id, "A")));

        Stack.Undo();

        // ⚠ Not empty. Dragging onto an occupied input replaces what was there, and an undo that only
        // removed the new wire is not the state the author pressed undo to get back to.
        Assert.Equal(new PortRef(first.Id, "Out"), graph.Source(new(sink.Id, "A")));
    }

    [Fact]
    public void A_drag_is_one_undo_step_and_two_drags_are_two() {
        var node = graph.Add("Test/Colour", new(0f, 0f));

        Stack.Execute(new MoveNodesCommand(graph, new Dictionary<NodeId, Vector2> { [node.Id] = new(10f, 0f) }, document));
        Stack.Execute(new MoveNodesCommand(graph, new Dictionary<NodeId, Vector2> { [node.Id] = new(20f, 0f) }, document));

        Assert.Equal(1, Stack.Depth.Value);

        Stack.Seal();
        Stack.Execute(new MoveNodesCommand(graph, new Dictionary<NodeId, Vector2> { [node.Id] = new(30f, 0f) }, document));

        Assert.Equal(2, Stack.Depth.Value);

        Stack.Undo();
        Assert.Equal(new Vector2(20f, 0f), node.Position);

        // One more undo goes back to before the first drag, not to one mouse-move ago.
        Stack.Undo();
        Assert.Equal(Vector2.Zero, node.Position);
    }

    [Fact]
    public void Moves_of_different_nodes_do_not_merge() {
        var first = graph.Add("Test/Colour");
        var second = graph.Add("Test/Colour");

        Stack.Execute(new MoveNodesCommand(graph, new Dictionary<NodeId, Vector2> { [first.Id] = new(10f, 0f) }, document));
        Stack.Execute(new MoveNodesCommand(graph, new Dictionary<NodeId, Vector2> { [second.Id] = new(10f, 0f) }, document));

        Assert.Equal(2, Stack.Depth.Value);
    }

    [Fact]
    public void An_auto_layout_does_not_merge_into_the_drag_before_it() {
        var node = graph.Add("Test/Colour");

        Stack.Execute(new MoveNodesCommand(graph, new Dictionary<NodeId, Vector2> { [node.Id] = new(10f, 0f) }, document));
        Stack.Execute(LayoutCommand.For(graph, null, default, document));

        Assert.Equal(2, Stack.Depth.Value);
        Assert.Equal("Auto-Layout", Stack.UndoName.Value);
    }

    [Fact]
    public void A_port_that_never_had_a_value_is_left_without_one_by_undo() {
        var node = graph.Add("Test/Combine");

        Stack.Execute(new SetPortValueCommand(graph, node.Id, "A", [0.25f], document));

        Assert.Equal([0.25f], node.Values["A"]);

        Stack.Undo();

        // ⚠ Absent, not zero. A port with no inline value takes its type's default, and writing one
        // back would silently pin it to a number the node type is free to change.
        Assert.False(node.Values.ContainsKey("A"));
    }

    [Fact]
    public void Scrubbing_a_value_undoes_to_what_it_started_as() {
        var node = graph.Add("Test/Combine");
        node.SetValue("A", 1f);

        Stack.Execute(new SetPortValueCommand(graph, node.Id, "A", [2f], document));
        Stack.Execute(new SetPortValueCommand(graph, node.Id, "A", [3f], document));
        Stack.Execute(new SetPortValueCommand(graph, node.Id, "A", [4f], document));

        Assert.Equal(1, Stack.Depth.Value);
        Assert.Equal([4f], node.Values["A"]);

        Stack.Undo();
        Assert.Equal([1f], node.Values["A"]);
    }

    [Fact]
    public void A_group_comes_back_where_it_was_in_the_list() {
        var node = graph.Add("Test/Colour");

        var first = new GraphGroup { Title = "First" };
        var last = new GraphGroup { Title = "Last" };

        graph.Groups.AddRange([first, last]);

        Stack.Execute(new RemoveGroupCommand(graph, first, document));

        Assert.Equal([last], graph.Groups);

        Stack.Undo();
        Assert.Equal([first, last], graph.Groups);
        Assert.NotNull(node);
    }

    [Fact]
    public void Grouping_and_ungrouping_round_trip() {
        var first = graph.Add("Test/Colour");
        var second = graph.Add("Test/Colour");

        var command = new AddGroupCommand(graph, "Pair", [first.Id, second.Id], document);
        Stack.Execute(command);

        Assert.Single(graph.Groups);

        Stack.Undo();
        Assert.Empty(graph.Groups);

        Stack.Redo();

        // The same group object, so anything that kept a reference to it — a rename command on the
        // stack above this one — still names the group that is in the graph.
        Assert.Same(command.Group, Assert.Single(graph.Groups));
    }

    [Fact]
    public void A_note_is_added_edited_and_removed_reversibly() {
        var added = new AddCommentCommand(graph, "why", new(10f, 10f), document);
        Stack.Execute(added);

        Stack.Execute(new SetCommentCommand(graph, added.Comment, "because", document));
        Assert.Equal("because", added.Comment.Text);

        Stack.Undo();
        Assert.Equal("why", added.Comment.Text);

        Stack.Undo();
        Assert.Empty(graph.Comments);
    }

    [Fact]
    public void A_paste_keeps_its_identities_across_undo_and_redo() {
        var source = graph.Add("Test/Colour");
        var sink = graph.Add("Test/Combine");

        graph.Connect(new(source.Id, "Out"), new(sink.Id, "A"));

        var fragment = NodeGraphClipboard.Copy(graph, [source.Id, sink.Id]);
        Assert.NotNull(fragment);

        var command = new PasteCommand(graph, fragment, new(50f, 50f), document);
        Stack.Execute(command);

        var identities = command.Pasted.Select(node => node.Id).ToArray();

        Assert.Equal(4, graph.Nodes.Count);
        Assert.Equal(2, graph.Edges.Count);

        Stack.Undo();
        Assert.Equal(2, graph.Nodes.Count);

        Stack.Redo();

        // ⚠ The same identities. A paste that renumbered on redo would leave an undone-and-redone
        // paste holding different nodes from the ones the author then selected and moved.
        Assert.Equal(identities, command.Pasted.Select(node => node.Id));
        Assert.Equal(2, graph.Edges.Count);
    }

    /// <summary>A copy carries the names as well as the numbers.</summary>
    /// <remarks>
    ///     ⚠ <b>It did not, and nothing said so.</b> <c>Copy</c> took <c>Values</c> and left
    ///     <c>Texts</c> behind, so a copied compositor pass came back with no name, no targets and no
    ///     depth attachment — a node that looks like the one that was copied and renders nothing. The
    ///     same omission was in the sub-graph surgery; see <c>SubGraphTests</c>.
    /// </remarks>
    [Fact]
    public void A_copy_carries_the_names_a_node_was_given() {
        var node = graph.Add("Test/Named Thing");

        node.SetText("Label", "glow");
        node.SetValue("Weight", 0.75f);

        var fragment = NodeGraphClipboard.Copy(graph, [node.Id]);

        Assert.NotNull(fragment);
        Assert.Equal("glow", fragment.Nodes[0].Texts["Label"]);

        Stack.Execute(new PasteCommand(graph, fragment, new(50f, 50f), document));

        var pasted = graph.Nodes.Single(entry => entry.Id != node.Id);

        Assert.Equal("glow", pasted.TextOf("Label"));
        Assert.Equal([0.75f], pasted.Values["Weight"]);
    }

    [Fact]
    public void A_copy_carries_only_the_wires_with_both_ends_in_the_selection() {
        var outside = graph.Add("Test/Colour");
        var inside = graph.Add("Test/Combine");
        var other = graph.Add("Test/Combine");

        graph.Connect(new(outside.Id, "Out"), new(inside.Id, "A"));
        graph.Connect(new(inside.Id, "Out"), new(other.Id, "A"));

        var fragment = NodeGraphClipboard.Copy(graph, [inside.Id, other.Id]);

        Assert.NotNull(fragment);
        Assert.Equal(2, fragment.Nodes.Length);
        Assert.Single(fragment.Edges);
    }

    [Fact]
    public void Extracting_a_sub_graph_is_reversible() {
        var colour = graph.Add("Test/Colour");
        var middle = graph.Add("Test/Combine");
        var sink = graph.Add("Test/Combine");

        graph.Connect(new(colour.Id, "Out"), new(middle.Id, "A"));
        graph.Connect(new(middle.Id, "Out"), new(sink.Id, "A"));

        var extraction = SubGraphs.Extract(graph, [middle.Id], "Middle");
        var command = new ExtractSubGraphCommand(graph, extraction, "Sub-graphs/Middle", new(100f, 0f), document);

        Stack.Execute(command);

        Assert.Equal(3, graph.Nodes.Count);
        Assert.False(graph.TryGet(middle.Id, out _));
        Assert.Equal(2, graph.Edges.Count);

        Stack.Undo();

        Assert.Equal(3, graph.Nodes.Count);
        Assert.True(graph.TryGet(middle.Id, out _));
        Assert.Equal(new PortRef(colour.Id, "Out"), graph.Source(new(middle.Id, "A")));
        Assert.Equal(new PortRef(middle.Id, "Out"), graph.Source(new(sink.Id, "A")));
    }

    [Fact]
    public void Editing_a_graph_marks_its_document_dirty() {
        Assert.False(document.IsDirty.Value);

        Stack.Execute(new AddNodeCommand(graph, "Test/Colour", default, document));

        Assert.True(document.IsDirty.Value);
    }

    [Fact]
    public void The_view_is_told_after_every_command() {
        var raised = 0;
        graph.Changed += _ => raised++;

        Stack.Execute(new AddNodeCommand(graph, "Test/Colour", default, document));

        Assert.True(raised > 0);

        raised = 0;
        Stack.Undo();

        // An undo is a change too: whatever is showing the graph has to catch up with it exactly as
        // it does with the edit.
        Assert.True(raised > 0);
    }
}
