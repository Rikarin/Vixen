// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.NodeGraph;
using Vixen.Input;
using Vixen.Ui;
using Xunit;
using PortDirection = Vixen.Editor.NodeGraph.PortDirection;

namespace Tests;

/// <summary>The model on a canvas: the projection, and every gesture arriving as a command.</summary>
public class ViewTests : IDisposable {
    readonly ViewFixture fixture = new();
    readonly NodeTypeRegistry registry = new();
    readonly NodeGraphModel graph = new();

    public ViewTests() {
        Vixen.Editor.NodeGraph.Tests.NodeTypes.Register(registry);
        fixture.Show(graph, registry);
    }

    public void Dispose() {
        fixture.Dispose();
        GC.SuppressFinalize(this);
    }

    NodeGraphView View => fixture.View;

    [Fact]
    public void A_node_is_drawn_with_the_ports_its_type_declares() {
        var node = graph.Add("Test/Combine", new(60f, 60f));
        fixture.Update();

        var item = fixture.Item(node.Id);

        Assert.Equal("Combine", item.Node!.Title);
        Assert.Equal(["A", "B"], item.Node.Inputs.Select(port => port.Name));
        Assert.Equal(["Out"], item.Node.Outputs.Select(port => port.Name));
    }

    [Fact]
    public void A_node_whose_type_is_missing_still_shows_the_ports_its_edges_name() {
        var known = graph.Add("Test/Colour");
        var missing = graph.Add("Plugin/Gone");

        graph.Connect(new(known.Id, "Out"), new(missing.Id, "Tint"));
        fixture.Update();

        var item = fixture.Item(missing.Id);

        // ⚠ The file opens and can be saved again unchanged, which is the difference between "this
        // node is missing" and "this file has been quietly destroyed". The title is the whole path so
        // it says which plugin is missing.
        Assert.Equal("Plugin/Gone", item.Node!.Title);
        Assert.Equal(["Tint"], item.Node.Inputs.Select(port => port.Name));
        Assert.Single(View.Canvas.Graph.Wires);
    }

    [Fact]
    public void A_sub_graphs_boundary_node_is_drawn_from_the_open_graphs_interface() {
        graph.Interface.Add(new("Colour", PortDirection.Input, PortKind.Float4));

        var entry = graph.Add(SubGraphs.InputType);
        fixture.Update();

        var item = fixture.Item(entry.Id);

        // Turned round: what the container feeds in is something to read from inside.
        Assert.Equal(["Colour"], item.Node!.Outputs.Select(port => port.Name));
        Assert.Empty(item.Node.Inputs);
    }

    [Fact]
    public void Dragging_a_wire_between_two_ports_records_a_connection() {
        var source = graph.Add("Test/Colour", new(40f, 40f));
        var sink = graph.Add("Test/Combine", new(320f, 40f));

        fixture.Update();

        var from = fixture.Port(source.Id, "Out", PortDirection.Output);
        var to = fixture.Port(sink.Id, "A", PortDirection.Input);
        var target = to.Bounds;

        fixture.DragFrom(from, target.X + (target.Width * 0.5f), target.Y + (target.Height * 0.5f));

        Assert.Equal(new PortRef(source.Id, "Out"), graph.Source(new(sink.Id, "A")));
        Assert.Equal(1, fixture.Stack.Depth.Value);

        fixture.Stack.Undo();
        Assert.Empty(graph.Edges);
    }

    [Fact]
    public void A_wire_between_two_ports_that_cannot_carry_each_other_is_refused_and_the_picture_put_back() {
        var texture = graph.Add("Test/Texture", new(40f, 40f));
        var named = graph.Add("Test/Named", new(320f, 40f));

        fixture.Update();

        ConnectionRefusal? refusal = null;
        View.ConnectionRefused += (_, reason) => refusal = reason;

        var from = fixture.Port(texture.Id, "Out", PortDirection.Output);
        var to = fixture.Port(named.Id, "Base Colour", PortDirection.Input);
        var target = to.Bounds;

        fixture.DragFrom(from, target.X + (target.Width * 0.5f), target.Y + (target.Height * 0.5f));

        Assert.Empty(graph.Edges);
        Assert.Equal(0, fixture.Stack.Depth.Value);
        Assert.NotNull(refusal);

        // ⚠ And the canvas's own optimistic wire is gone. The canvas draws the connection before this
        // class has recorded anything, which is what makes the gesture feel live; putting the picture
        // back is the other half of that bargain.
        Assert.Empty(View.Canvas.Graph.Wires);
    }

    [Fact]
    public void Dropping_a_wire_on_nothing_opens_the_create_search_filtered_by_the_port() {
        var source = graph.Add("Test/Colour", new(40f, 40f));
        fixture.Update();

        var from = fixture.Port(source.Id, "Out", PortDirection.Output);

        fixture.DragFrom(from, 700f, 500f);

        Assert.True(View.Search.IsOpen);

        var filter = Assert.NotNull(View.Search.Filter);

        // Looking for an input, because the wire left an output. Named after the far end so no call
        // site has to remember to invert it.
        Assert.Equal(PortDirection.Input, filter.Direction);
        Assert.Equal(PortKind.Float4, filter.Kind);
        Assert.All(View.Search.Results, result => Assert.NotEmpty(result.Port));
    }

    [Fact]
    public void Choosing_a_type_from_a_dragged_wire_creates_the_node_and_connects_it_in_one_step() {
        var source = graph.Add("Test/Colour", new(40f, 40f));
        fixture.Update();

        fixture.DragFrom(fixture.Port(source.Id, "Out", PortDirection.Output), 700f, 500f);

        View.Search.Field.Value = "Combine";
        fixture.Update();

        Assert.True(View.Search.Accept());
        fixture.Update();

        Assert.Equal(2, graph.Nodes.Count);

        var created = Assert.Single(graph.Nodes, node => node.Id != source.Id);

        Assert.Equal("Test/Combine", created.Type);
        Assert.Equal(new PortRef(source.Id, "Out"), graph.Source(new(created.Id, "A")));

        // One entry, not two: adding the node and wiring it up is one gesture and one undo.
        Assert.Equal(1, fixture.Stack.Depth.Value);

        fixture.Stack.Undo();
        Assert.Single(graph.Nodes);
    }

    [Fact]
    public void Pulling_a_wire_off_an_input_and_dropping_it_on_nothing_disconnects_it() {
        var source = graph.Add("Test/Colour", new(40f, 40f));
        var sink = graph.Add("Test/Combine", new(320f, 40f));

        graph.Connect(new(source.Id, "Out"), new(sink.Id, "A"));
        fixture.Update();

        fixture.DragFrom(fixture.Port(sink.Id, "A", PortDirection.Input), 700f, 500f);

        Assert.Empty(graph.Edges);
        Assert.False(View.Search.IsOpen);

        fixture.Stack.Undo();
        Assert.Single(graph.Edges);
    }

    [Fact]
    public void Rerouting_a_wire_onto_another_input_is_one_undo_step() {
        var source = graph.Add("Test/Colour", new(40f, 40f));
        var sink = graph.Add("Test/Combine", new(320f, 40f));

        graph.Connect(new(source.Id, "Out"), new(sink.Id, "A"));
        fixture.Update();

        var to = fixture.Port(sink.Id, "B", PortDirection.Input).Bounds;

        fixture.DragFrom(
            fixture.Port(sink.Id, "A", PortDirection.Input),
            to.X + (to.Width * 0.5f),
            to.Y + (to.Height * 0.5f)
        );

        // ⚠ Moved, not copied. The canvas picks the wire up at the press and says nothing about it,
        // so a view that only recorded the new connection would leave the graph with both.
        var edge = Assert.Single(graph.Edges);
        Assert.Equal("B", edge.To.Port);

        Assert.Equal(1, fixture.Stack.Depth.Value);

        fixture.Stack.Undo();
        Assert.Equal("A", Assert.Single(graph.Edges).To.Port);
    }

    [Fact]
    public void Dragging_a_node_records_one_move_and_undoes_to_where_it_started() {
        var node = graph.Add("Test/Colour", new(64f, 64f));
        fixture.Update();

        var item = fixture.Item(node.Id);
        var bounds = item.Bounds;

        fixture.Press(bounds.X + 20f, bounds.Y + 6f);
        fixture.Move(bounds.X + 120f, bounds.Y + 86f);
        fixture.Move(bounds.X + 140f, bounds.Y + 96f);
        fixture.Release(bounds.X + 140f, bounds.Y + 96f);

        Assert.NotEqual(new Vector2(64f, 64f), node.Position);
        Assert.Equal(1, fixture.Stack.Depth.Value);

        fixture.Stack.Undo();
        Assert.Equal(new Vector2(64f, 64f), node.Position);
    }

    [Fact]
    public void Delete_goes_through_the_stack_rather_than_through_the_canvas() {
        var node = graph.Add("Test/Colour", new(64f, 64f));
        fixture.Update();

        fixture.Click(fixture.Item(node.Id));

        Assert.Equal([node.Id], View.Selection);

        fixture.Type(InputKey.Delete);

        Assert.Empty(graph.Nodes);

        // ⚠ The canvas would have removed the node from its own copy of the graph and told nobody,
        // which the next reprojection would silently undo. The view claims the key first.
        Assert.Equal(1, fixture.Stack.Depth.Value);

        fixture.Stack.Undo();
        Assert.Single(graph.Nodes);
    }

    [Fact]
    public void Copy_and_paste_round_trip_through_the_view() {
        var source = graph.Add("Test/Colour", new(40f, 40f));
        var sink = graph.Add("Test/Combine", new(320f, 40f));

        graph.Connect(new(source.Id, "Out"), new(sink.Id, "A"));
        fixture.Update();

        View.Select([source.Id, sink.Id]);

        Assert.True(View.CopySelection());
        Assert.True(View.Paste());

        Assert.Equal(4, graph.Nodes.Count);
        Assert.Equal(2, graph.Edges.Count);

        // What was pasted is what is selected, so the next drag means the new nodes rather than the
        // ones they were copied from.
        Assert.Equal(2, View.Selection.Count);
        Assert.DoesNotContain(source.Id, View.Selection);
    }

    [Fact]
    public void Grouping_a_selection_draws_a_box_the_canvas_can_see() {
        var first = graph.Add("Test/Colour", new(40f, 40f));
        var second = graph.Add("Test/Colour", new(40f, 200f));

        fixture.Update();
        View.Select([first.Id, second.Id]);

        Assert.True(View.GroupSelection("Pair"));

        var group = Assert.Single(graph.Groups);

        Assert.Equal("Pair", group.Title);
        Assert.Equal(2, Assert.Single(View.Canvas.Graph.Groups).Nodes.Count);

        Assert.True(View.UngroupSelection());
        Assert.Empty(graph.Groups);
    }

    [Fact]
    public void A_note_is_drawn_and_follows_the_canvas_when_it_pans() {
        View.AddComment("why this is here", new(100f, 100f));
        fixture.Update();

        var note = Assert.Single(View.Canvas.Surface.Children.OfType<NodeCommentView>());
        var before = note.Bounds.X;

        // Panned by the gesture rather than by writing the property, because that is the path that
        // has to keep up: the view repositions notes from a bubbling pointer handler, which runs
        // after the canvas has moved and still in time for this frame's layout.
        fixture.Press(500f, 400f, PointerButton.Middle);
        fixture.Move(420f, 400f);
        fixture.Release(420f, 400f, PointerButton.Middle);

        Assert.True(note.Bounds.X < before, $"the note did not move: {before} to {note.Bounds.X}");
    }

    [Fact]
    public void An_auto_layout_moves_everything_in_one_undo_step() {
        var first = graph.Add("Test/Colour", new(500f, 500f));
        var second = graph.Add("Test/Combine", new(0f, 0f));

        graph.Connect(new(first.Id, "Out"), new(second.Id, "A"));
        fixture.Update();

        Assert.True(View.AutoLayout());
        Assert.True(first.Position.X < second.Position.X);
        Assert.Equal(1, fixture.Stack.Depth.Value);

        fixture.Stack.Undo();
        Assert.Equal(new Vector2(500f, 500f), first.Position);
    }

    [Fact]
    public void Extracting_a_selection_leaves_one_node_wired_where_the_selection_was() {
        var colour = graph.Add("Test/Colour", new(0f, 0f));
        var middle = graph.Add("Test/Combine", new(240f, 0f));
        var sink = graph.Add("Test/Combine", new(480f, 0f));

        graph.Connect(new(colour.Id, "Out"), new(middle.Id, "A"));
        graph.Connect(new(middle.Id, "Out"), new(sink.Id, "A"));

        fixture.Update();
        View.Select([middle.Id]);

        var library = new SubGraphLibrary();
        var extracted = View.ExtractSelection("Middle", "Sub-graphs/Middle", library);

        Assert.NotNull(extracted);
        Assert.Equal(3, graph.Nodes.Count);
        Assert.False(graph.TryGet(middle.Id, out _));

        var standIn = Assert.Single(graph.Nodes, node => node.Type == "Sub-graphs/Middle");

        // The stand-in is drawn with the extracted graph's interface, which is only true because the
        // library registered the derived node type before the edit was recorded.
        Assert.Equal(2, fixture.Item(standIn.Id).Node!.Inputs.Count + fixture.Item(standIn.Id).Node!.Outputs.Count);
        Assert.Equal(2, graph.Edges.Count);

        fixture.Stack.Undo();
        Assert.True(graph.TryGet(middle.Id, out _));
    }

    [Fact]
    public void Double_clicking_a_sub_graph_node_says_which_graph_to_open() {
        var inner = new NodeGraphModel { Name = "Middle" };
        inner.Interface.Add(new("In", PortDirection.Input, PortKind.Dynamic));

        var library = new SubGraphLibrary();
        library.Add("Sub-graphs/Middle", inner, registry);

        View.SubGraphSource = library;

        var node = graph.Add("Sub-graphs/Middle", new(64f, 64f));
        fixture.Update();

        NodeGraphModel? opened = null;
        View.SubGraphOpened += (_, _, child) => opened = child;

        var item = fixture.Item(node.Id);

        fixture.Click(item);
        fixture.Click(item);

        Assert.Same(inner, opened);
    }

    [Fact]
    public void A_view_with_no_stack_shows_the_graph_and_refuses_to_change_it() {
        var node = graph.Add("Test/Colour", new(64f, 64f));
        fixture.Update();

        View.Stack = null;
        View.Select([node.Id]);

        Assert.True(View.IsReadOnly);
        Assert.False(View.DeleteSelection());
        Assert.Null(View.Create("Test/Colour", default));
        Assert.Single(graph.Nodes);
    }

    [Fact]
    public void The_selection_survives_an_edit_that_did_not_touch_it() {
        var kept = graph.Add("Test/Colour", new(40f, 40f));
        var other = graph.Add("Test/Colour", new(40f, 200f));

        fixture.Update();
        View.Select([kept.Id]);

        View.Create("Test/Combine", new(400f, 40f));

        // The whole picture is rebuilt on every structural change, so the selection has to be
        // reapplied from identities rather than carried on the canvas's own node objects.
        Assert.Equal([kept.Id], View.Selection);
        Assert.NotNull(other);
    }

    [Fact]
    public void A_preview_is_only_offered_for_a_type_that_asked_for_one() {
        var combine = graph.Add("Test/Combine", new(64f, 64f));
        var colour = graph.Add("Test/Colour", new(64f, 240f));

        fixture.Update();

        Assert.True(View.Definition(combine.Type)!.Preview);
        Assert.False(View.Definition(colour.Type)!.Preview);

        var asked = new List<NodeId>();
        View.PreviewSource = new Recorder(asked);

        fixture.Update();

        // Only the node whose type declared Preview reaches the source at all, so a Time node costs
        // nothing to a graph that draws forty of them.
        Assert.Equal([combine.Id], asked);
    }

    [Fact]
    public void A_preview_with_a_render_target_is_drawn_as_an_image_and_one_without_as_a_swatch() {
        graph.Add("Test/Combine", new(64f, 64f));
        fixture.Update();

        View.PreviewSource = new Fixed(new NodePreview(Color4.White, Image: 77UL, FlipVertically: true));
        fixture.Update();

        var image = Assert.Single(Commands(DrawCommandKind.Image), command => command.Image == 77UL);

        // Flipped, because a scene renders with the engine's Y up and an interface draws with Y down.
        // The same question Viewport answers, and the same answer.
        Assert.True(image.Source.Height < 0f);

        View.PreviewSource = new Fixed(new NodePreview(new Color4(1f, 0f, 0f, 1f)));
        fixture.Update();

        Assert.DoesNotContain(Commands(DrawCommandKind.Image), command => command.Image == 77UL);
    }

    IEnumerable<DrawCommand> Commands(DrawCommandKind kind) {
        foreach (var command in fixture.Ui.Drawing.Commands) {
            if (command.Kind == kind) {
                yield return command;
            }
        }
    }

    sealed class Recorder(List<NodeId> asked) : INodePreviewSource {
        public bool TryGet(NodeGraphModel graph, GraphNode node, NodeTypeDefinition definition, out NodePreview preview) {
            asked.Add(node.Id);
            preview = new(new Color4(1f, 0f, 0f, 1f));

            return true;
        }
    }

    sealed class Fixed(NodePreview answer) : INodePreviewSource {
        public bool TryGet(NodeGraphModel graph, GraphNode node, NodeTypeDefinition definition, out NodePreview preview) {
            preview = answer;

            return true;
        }
    }
}
