// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Input;
using Xunit;

namespace Vixen.Ui.Controls.Advanced.Tests;

/// <summary>The graph model on its own: connection rules, cycles, and what a deletion takes with it.</summary>
public class NodeGraphTests {
    [Fact]
    public void A_wire_may_be_dragged_from_either_end() {
        var graph = new NodeGraph();

        var source = graph.AddNode("source");
        var sink = graph.AddNode("sink");

        var output = source.AddOutput("value");
        var input = sink.AddInput("value");

        // Backwards, which is how half of every user's drags arrive: they grabbed the input first.
        var wire = graph.Connect(input, output);

        Assert.NotNull(wire);
        Assert.Same(output, wire.From);
        Assert.Same(input, wire.To);
    }

    [Fact]
    public void An_input_takes_one_wire_and_a_second_replaces_it() {
        var graph = new NodeGraph();

        var first = graph.AddNode("a").AddOutput("out");
        var second = graph.AddNode("b").AddOutput("out");
        var input = graph.AddNode("sink").AddInput("in");

        graph.Connect(first, input);
        graph.Connect(second, input);

        var wire = Assert.Single(graph.Wires);
        Assert.Same(second, wire.From);
    }

    [Fact]
    public void An_output_feeds_as_many_inputs_as_it_likes() {
        var graph = new NodeGraph();
        var output = graph.AddNode("a").AddOutput("out");

        graph.Connect(output, graph.AddNode("b").AddInput("in"));
        graph.Connect(output, graph.AddNode("c").AddInput("in"));

        Assert.Equal(2, graph.Wires.Count);
    }

    [Fact]
    public void A_wire_that_would_close_a_loop_is_refused() {
        var graph = new NodeGraph();

        var a = graph.AddNode("a");
        var b = graph.AddNode("b");
        var c = graph.AddNode("c");

        graph.Connect(a.AddOutput("out"), b.AddInput("in"));
        graph.Connect(b.AddOutput("out"), c.AddInput("in"));

        // ⚠ Not a graph with a mistake in it — a walk that does not terminate. Every consumer of one
        // of these evaluates it by walking back from the outputs.
        Assert.Null(graph.Connect(c.AddOutput("out"), a.AddInput("in")));
        Assert.Equal(2, graph.Wires.Count);
    }

    [Fact]
    public void A_node_cannot_be_wired_to_itself() {
        var graph = new NodeGraph();
        var node = graph.AddNode("a");

        Assert.Null(graph.Connect(node.AddOutput("out"), node.AddInput("in")));
    }

    [Fact]
    public void Two_outputs_do_not_connect() {
        var graph = new NodeGraph();

        Assert.Null(graph.Connect(graph.AddNode("a").AddOutput("out"), graph.AddNode("b").AddOutput("out")));
    }

    [Fact]
    public void Removing_a_node_takes_its_wires_with_it() {
        var graph = new NodeGraph();

        var a = graph.AddNode("a");
        var b = graph.AddNode("b");
        var c = graph.AddNode("c");

        graph.Connect(a.AddOutput("out"), b.AddInput("in"));
        graph.Connect(b.AddOutput("out"), c.AddInput("in"));

        Assert.True(graph.Remove(b));

        // Both of them: a wire whose node is gone would still be saved, and the graph would reload
        // with a connection to something that does not exist.
        Assert.Empty(graph.Wires);
    }

    [Fact]
    public void A_node_belongs_to_one_group_at_a_time() {
        var graph = new NodeGraph();
        var node = graph.AddNode("a");

        var first = graph.AddGroup("first", node);
        var second = graph.AddGroup("second");

        second.Add(node);

        Assert.Empty(first.Nodes);
        Assert.Same(second, node.Group);
    }
}

/// <summary>Pan, zoom, culling, selection and the four things a drag on a canvas can mean.</summary>
public class NodeCanvasTests {
    static NodeCanvas Canvas(AdvancedFixture fixture, NodeGraph graph) {
        var canvas = fixture.Add<NodeCanvas>();

        canvas.Graph = graph;
        fixture.Update();

        canvas.Refresh();
        fixture.Update();

        return canvas;
    }

    /// <summary>Two nodes side by side at the origin, one feeding the other.</summary>
    static (NodeGraph Graph, GraphNode Source, GraphNode Sink) Pair() {
        var graph = new NodeGraph();

        var source = graph.AddNode("source", new Vector2(0f, 0f));
        var sink = graph.AddNode("sink", new Vector2(240f, 0f));

        source.AddOutput("value");
        sink.AddInput("value");

        return (graph, source, sink);
    }

    [Fact]
    public void A_port_anchor_is_arithmetic_rather_than_a_laid_out_box() {
        using var fixture = new AdvancedFixture();

        var (graph, source, sink) = Pair();
        var canvas = Canvas(fixture, graph);

        var anchor = canvas.AnchorOf(source.Outputs[0]);

        Assert.Equal(source.Position.X + source.Width, anchor.X, 3);
        Assert.Equal(canvas.HeaderHeight + (canvas.PortPitch * 0.5f), anchor.Y, 3);

        // ⚠ The claim the arithmetic exists for: pan until the node has no elements at all and the
        // anchor is still the same graph point. An endpoint read from a port's box would be zero.
        canvas.Pan = new Vector2(100_000f, 100_000f);
        fixture.Update();

        Assert.Null(canvas.ItemOf(source));
        Assert.Equal(anchor, canvas.AnchorOf(source.Outputs[0]));
        Assert.NotEqual(anchor, canvas.AnchorOf(sink.Inputs[0]));
    }

    [Fact]
    public void A_huge_graph_realises_only_what_is_on_screen() {
        using var fixture = new AdvancedFixture();
        var graph = new NodeGraph();

        for (var i = 0; i < 10_000; i++) {
            graph.AddNode($"node{i}", new Vector2(i % 100 * 400f, i / 100 * 400f));
        }

        var canvas = Canvas(fixture, graph);

        Assert.Equal(10_000, graph.Nodes.Count);

        // Doc 09's claim about virtualisation, applied to a canvas rather than a list: an 800×600
        // viewport at a zoom of one sees a handful of a graph pitched at four hundred units.
        Assert.True(canvas.Items.Count < 30, $"realised {canvas.Items.Count} items");
        Assert.Equal("node0", canvas.Visible[0].Title);
    }

    [Fact]
    public void Panning_rebinds_the_items_rather_than_making_new_ones() {
        using var fixture = new AdvancedFixture();
        var graph = new NodeGraph();

        for (var i = 0; i < 400; i++) {
            graph.AddNode($"node{i}", new Vector2(i * 300f, 0f));
        }

        var canvas = Canvas(fixture, graph);

        var before = canvas.Items.Count;
        var element = canvas.Items[0];

        canvas.Pan = new Vector2(3_000f, 0f);
        fixture.Update();

        Assert.Equal(before, canvas.Items.Count);

        // The same element, showing a different node — which is what makes a pan cost property
        // writes rather than a tear-down of everything on screen.
        Assert.Same(element, canvas.Items[0]);
        Assert.Equal("node10", canvas.Items[0].Node?.Title);
    }

    [Fact]
    public void Zooming_keeps_the_graph_point_under_the_pointer_where_it_was() {
        using var fixture = new AdvancedFixture();

        var (graph, _, _) = Pair();
        var canvas = Canvas(fixture, graph);

        var before = canvas.ToGraph(200f, 150f);

        fixture.Document.Dispatch(new WheelEvent { X = 200f, Y = 150f, DeltaY = -400f, Timestamp = TimeSpan.Zero });
        fixture.Update();

        Assert.True(canvas.Zoom > 1f, $"zoom is {canvas.Zoom}");

        var after = canvas.ToGraph(200f, 150f);

        Assert.Equal(before.X, after.X, 2);
        Assert.Equal(before.Y, after.Y, 2);
    }

    [Fact]
    public void The_zoom_stops_at_its_limits() {
        using var fixture = new AdvancedFixture();
        var canvas = Canvas(fixture, new NodeGraph());

        canvas.Zoom = 1000f;
        Assert.Equal(canvas.MaximumZoom, canvas.Zoom);

        canvas.Zoom = 0f;
        Assert.Equal(canvas.MinimumZoom, canvas.Zoom);
    }

    [Fact]
    public void A_dragged_node_lands_on_the_grid() {
        using var fixture = new AdvancedFixture();

        var (graph, source, _) = Pair();
        var canvas = Canvas(fixture, graph);

        canvas.GridSize = 20f;

        var item = canvas.ItemOf(source);
        Assert.NotNull(item);

        var header = AdvancedFixture.Centre(item.Header);

        fixture.Press(header.X, header.Y);
        fixture.Move(header.X + 47f, header.Y + 33f);
        fixture.Release(header.X + 47f, header.Y + 33f);

        Assert.Equal(40f, source.Position.X);
        Assert.Equal(40f, source.Position.Y);
    }

    [Fact]
    public void A_drag_moves_everything_that_was_selected() {
        using var fixture = new AdvancedFixture();

        var (graph, source, sink) = Pair();
        var canvas = Canvas(fixture, graph);

        canvas.SnapToGrid = false;

        fixture.Click(canvas.ItemOf(source)!.Header);
        fixture.Click(canvas.ItemOf(sink)!.Header, ModifierKeys.Control);

        Assert.Equal(2, canvas.Selection.Count);

        var header = AdvancedFixture.Centre(canvas.ItemOf(source)!.Header);

        fixture.Press(header.X, header.Y);
        fixture.Move(header.X + 60f, header.Y);
        fixture.Release(header.X + 60f, header.Y);

        // ⚠ Both. A press on something already selected must not reduce the selection to it, or
        // dragging a group of five moves one of them.
        Assert.Equal(60f, source.Position.X, 2);
        Assert.Equal(300f, sink.Position.X, 2);
    }

    [Fact]
    public void A_marquee_selects_what_it_covers() {
        using var fixture = new AdvancedFixture();

        var (graph, source, sink) = Pair();
        var canvas = Canvas(fixture, graph);

        // From empty canvas well below the nodes, back up over both of them.
        fixture.DragPoint(500f, 300f, 4f, 4f);

        Assert.Equal(2, canvas.Selection.Count);
        Assert.Contains(source, canvas.Selection);
        Assert.Contains(sink, canvas.Selection);

        // And a band over nothing clears it, because the press on empty canvas deselected first.
        fixture.DragPoint(500f, 300f, 560f, 360f);
        Assert.Empty(canvas.Selection);
    }

    [Fact]
    public void Dragging_from_one_port_to_another_makes_a_wire() {
        using var fixture = new AdvancedFixture();

        var (graph, source, sink) = Pair();
        var canvas = Canvas(fixture, graph);

        var connected = 0;
        canvas.Connected += (_, _) => connected++;

        var from = canvas.ItemOf(source)!.ViewOf(source.Outputs[0]);
        var to = canvas.ItemOf(sink)!.ViewOf(sink.Inputs[0]);

        Assert.NotNull(from);
        Assert.NotNull(to);

        var target = AdvancedFixture.Centre(to);

        fixture.DragFrom(from, target.X, target.Y);

        var wire = Assert.Single(graph.Wires);

        Assert.Same(source.Outputs[0], wire.From);
        Assert.Same(sink.Inputs[0], wire.To);
        Assert.Equal(1, connected);
    }

    [Fact]
    public void A_wire_dropped_on_nothing_connects_nothing() {
        using var fixture = new AdvancedFixture();

        var (graph, source, _) = Pair();
        var canvas = Canvas(fixture, graph);

        var from = canvas.ItemOf(source)!.ViewOf(source.Outputs[0]);
        Assert.NotNull(from);

        fixture.DragFrom(from, 500f, 400f);

        Assert.Empty(graph.Wires);
        Assert.Null(canvas.PendingPort);
    }

    [Fact]
    public void Dragging_off_a_connected_input_picks_the_wire_up() {
        using var fixture = new AdvancedFixture();

        var (graph, source, sink) = Pair();
        graph.Connect(source.Outputs[0], sink.Inputs[0]);

        var canvas = Canvas(fixture, graph);
        var port = canvas.ItemOf(sink)!.ViewOf(sink.Inputs[0]);

        Assert.NotNull(port);

        var centre = AdvancedFixture.Centre(port);

        fixture.Press(centre.X, centre.Y);

        // ⚠ Off the input rather than a second wire onto it: the existing one is now in hand, held
        // by the output it came from, which is what makes rerouting one gesture instead of two.
        Assert.Empty(graph.Wires);
        Assert.Same(source.Outputs[0], canvas.PendingPort);

        fixture.Release(500f, 400f);
        Assert.Null(canvas.PendingPort);
    }

    [Fact]
    public void A_secondary_drag_pans_wherever_it_started() {
        using var fixture = new AdvancedFixture();

        var (graph, source, _) = Pair();
        var canvas = Canvas(fixture, graph);

        // Over a node, which is the case a graph dense enough to need panning is always in.
        var header = AdvancedFixture.Centre(canvas.ItemOf(source)!.Header);

        fixture.Press(header.X, header.Y, PointerButton.Middle);
        fixture.Move(header.X - 100f, header.Y - 50f);
        fixture.Release(header.X - 100f, header.Y - 50f, PointerButton.Middle);

        Assert.Equal(100f, canvas.Pan.X, 2);
        Assert.Equal(50f, canvas.Pan.Y, 2);

        // And the node it started over did not move.
        Assert.Equal(Vector2.Zero, source.Position);
    }

    [Fact]
    public void Deleting_the_selection_removes_the_nodes_and_their_wires() {
        using var fixture = new AdvancedFixture();

        var (graph, source, sink) = Pair();
        graph.Connect(source.Outputs[0], sink.Inputs[0]);

        var canvas = Canvas(fixture, graph);

        fixture.Click(canvas.ItemOf(source)!.Header);
        Assert.Single(canvas.Selection);

        fixture.Type(InputKey.Delete);

        Assert.Single(graph.Nodes);
        Assert.Empty(graph.Wires);
        Assert.Empty(canvas.Selection);
    }

    [Fact]
    public void Zoom_to_fit_frames_the_whole_graph() {
        using var fixture = new AdvancedFixture();
        var graph = new NodeGraph();

        graph.AddNode("a", new Vector2(-2_000f, -1_000f));
        graph.AddNode("b", new Vector2(2_000f, 1_000f));

        var canvas = Canvas(fixture, graph);

        canvas.ZoomToFit();
        fixture.Update();

        Assert.Equal(2, canvas.Items.Count(static item => item.Node is not null));

        var view = canvas.View;

        foreach (var node in graph.Nodes) {
            Assert.True(view.Contains(canvas.RectOf(node)), $"{node.Title} is outside {view}");
        }
    }

    [Fact]
    public void Dragging_a_group_header_moves_everything_in_it() {
        using var fixture = new AdvancedFixture();

        var (graph, source, sink) = Pair();

        // Down the canvas, because a group's box reaches above the nodes in it by its padding and
        // its header — at the origin the bar this test drags would be off the top edge.
        source.Position = new Vector2(0f, 120f);
        sink.Position = new Vector2(240f, 120f);

        var group = graph.AddGroup("pair", source, sink);

        var canvas = Canvas(fixture, graph);
        canvas.SnapToGrid = false;

        var view = canvas.ViewOf(group);
        Assert.NotNull(view);

        var header = AdvancedFixture.Centre(view.Header);

        fixture.Press(header.X, header.Y);
        fixture.Move(header.X + 50f, header.Y + 25f);
        fixture.Release(header.X + 50f, header.Y + 25f);

        Assert.Equal(50f, source.Position.X, 2);
        Assert.Equal(145f, source.Position.Y, 2);
        Assert.Equal(290f, sink.Position.X, 2);
    }

    [Fact]
    public void A_click_that_moved_nothing_reduces_the_selection_to_one() {
        using var fixture = new AdvancedFixture();

        var (graph, source, sink) = Pair();
        var canvas = Canvas(fixture, graph);

        fixture.Click(canvas.ItemOf(source)!.Header);
        fixture.Click(canvas.ItemOf(sink)!.Header, ModifierKeys.Control);

        Assert.Equal(2, canvas.Selection.Count);

        // ⚠ The other half of the press-does-not-reduce rule. A press keeps the selection so a drag
        // can move all of it; a press and release that moved nothing meant "just this one", and
        // without this half a multiple selection could never be narrowed by clicking.
        fixture.Click(canvas.ItemOf(source)!.Header);

        Assert.Same(source, Assert.Single(canvas.Selection));
    }

    [Fact]
    public void The_minimap_pans_the_canvas_to_what_was_clicked() {
        using var fixture = new AdvancedFixture();
        var graph = new NodeGraph();

        graph.AddNode("a", new Vector2(-1_500f, -1_500f));
        graph.AddNode("b", new Vector2(1_500f, 1_500f));

        var canvas = Canvas(fixture, graph);
        var minimap = canvas.Minimap;

        Assert.True(minimap.Scale > 0f);

        var target = minimap.ToScreen(new Vector2(1_500f, 1_500f));

        fixture.Press(target.X, target.Y);
        fixture.Release(target.X, target.Y);

        // Centred on what was pointed at rather than pinned to it, which is what a map click means.
        Assert.Equal(1_500f, canvas.View.Center.X, 0);
        Assert.Equal(1_500f, canvas.View.Center.Y, 0);
    }

    [Fact]
    public void A_node_that_gains_a_port_gets_taller_without_being_told() {
        using var fixture = new AdvancedFixture();

        var (graph, source, _) = Pair();
        var canvas = Canvas(fixture, graph);

        var before = canvas.HeightOf(source);

        source.AddOutput("second");
        graph.Touch();

        Assert.Equal(before + canvas.PortPitch, canvas.HeightOf(source), 3);
    }

    [Fact]
    public void Changing_the_graph_drops_the_selection_with_it() {
        using var fixture = new AdvancedFixture();

        var (graph, source, _) = Pair();
        var canvas = Canvas(fixture, graph);

        canvas.Select(source);
        Assert.Single(canvas.Selection);

        canvas.Graph = new NodeGraph();

        // ⚠ Otherwise Delete on the new graph edits nodes belonging to the old one.
        Assert.Empty(canvas.Selection);
    }

    // ── Inline values ────────────────────────────────────────────────────────

    [Fact]
    public void An_input_with_an_editor_is_drawn_with_a_box_per_lane() {
        using var fixture = new AdvancedFixture();

        var (graph, _, sink) = Pair();

        var editor = new PortEditor(PortEditorKind.Number, 3) { LaneNames = "XYZ" };
        editor.Set([0.1f, 0.2f, 0.3f]);

        sink.Inputs[0].Editor = editor;

        var canvas = Canvas(fixture, graph);
        var fields = canvas.ItemOf(sink)!.Inputs[0].Fields;

        Assert.Equal(3, fields.Boxes.Count);
        Assert.Equal([0.1d, 0.2d, 0.3d], fields.Boxes.Select(box => Math.Round(box.Number, 3)));
    }

    /// <remarks>
    ///     The canvas hides it rather than the caller removing it, because the canvas is the thing
    ///     that knows a wire was dropped — a frame before anything above it has recorded anything.
    /// </remarks>
    [Fact]
    public void A_connected_input_hides_its_editor() {
        using var fixture = new AdvancedFixture();

        var (graph, source, sink) = Pair();

        sink.Inputs[0].Editor = new(PortEditorKind.Number);

        var canvas = Canvas(fixture, graph);
        Assert.NotNull(canvas.ItemOf(sink)!.Inputs[0].Fields.Port);

        graph.Connect(source.Outputs[0], sink.Inputs[0]);
        fixture.Update();

        Assert.Null(canvas.ItemOf(sink)!.Inputs[0].Fields.Port);
    }

    [Fact]
    public void Typing_into_a_box_writes_the_editor_and_says_so() {
        using var fixture = new AdvancedFixture();

        var (graph, _, sink) = Pair();
        var editor = new PortEditor(PortEditorKind.Number, 2);

        sink.Inputs[0].Editor = editor;

        var canvas = Canvas(fixture, graph);

        GraphPort? reported = null;
        canvas.PortEdited += (_, port) => reported = port;

        canvas.ItemOf(sink)!.Inputs[0].Fields.Boxes[1].Number = 0.5d;

        Assert.Same(sink.Inputs[0], reported);
        Assert.Equal(0.5f, editor[1], 3);
    }

    /// <remarks>
    ///     ⚠ The pool is rebound as the canvas scrolls, and a bind that reported an edit would tell
    ///     the graph above it that every value on every node that came into view had just been typed.
    /// </remarks>
    [Fact]
    public void Binding_a_node_reports_no_edit() {
        using var fixture = new AdvancedFixture();

        var (graph, _, sink) = Pair();

        var editor = new PortEditor(PortEditorKind.Number);
        editor.Set([7f]);

        sink.Inputs[0].Editor = editor;

        var canvas = Canvas(fixture, graph);
        var edits = 0;

        canvas.PortEdited += (_, _) => edits++;

        canvas.Refresh();
        fixture.Update();

        Assert.Equal(0, edits);
    }
}
