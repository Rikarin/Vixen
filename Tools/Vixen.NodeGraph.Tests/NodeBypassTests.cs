// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.NodeGraph;
using Xunit;

namespace Tests;

/// <summary>
///     Bypassed nodes and reroute dots, which <see cref="NodeBypass.Apply" /> takes out of the graph a
///     compiler walks, and the delete-and-relink wiring that shares its trace.
/// </summary>
public class NodeBypassTests {
    static readonly NodeTypeDefinition Source = new(
        "Test/Source",
        [new("Out", PortDirection.Output, PortKind.Float4)],
        static () => new SubGraphNode()
    );

    /// <summary>A number first and a colour second, so "the first input" and "the first of the kind" differ.</summary>
    static readonly NodeTypeDefinition Filter = new(
        "Test/Filter",
        [
            new("Amount", PortDirection.Input, PortKind.Float),
            new("In", PortDirection.Input, PortKind.Float4),
            new("Out", PortDirection.Output, PortKind.Float4)
        ],
        static () => new SubGraphNode()
    );

    static readonly NodeTypeDefinition Sink = new(
        "Test/Sink",
        [new("In", PortDirection.Input, PortKind.Float4)],
        static () => new SubGraphNode()
    );

    static readonly NodeTypeDefinition Maker = new(
        "Test/Maker",
        [new("Amount", PortDirection.Input, PortKind.Float), new("Out", PortDirection.Output, PortKind.Float4)],
        static () => new SubGraphNode()
    );

    static NodeTypeDefinition? TypeOf(GraphNode node) =>
        node.Type switch {
            "Test/Source" => Source,
            "Test/Filter" => Filter,
            "Test/Sink" => Sink,
            "Test/Maker" => Maker,
            _ => null
        };

    static (NodeGraphModel Graph, GraphNode Source, GraphNode Filter, GraphNode Sink) Chain() {
        NodeGraphModel graph = new();
        var source = graph.Add("Test/Source");
        var filter = graph.Add("Test/Filter");
        var sink = graph.Add("Test/Sink");
        graph.Connect(new(source.Id, "Out"), new(filter.Id, "In"));
        graph.Connect(new(filter.Id, "Out"), new(sink.Id, "In"));

        return (graph, source, filter, sink);
    }

    [Fact]
    public void A_graph_with_nothing_switched_off_comes_back_as_itself() {
        var (graph, _, _, _) = Chain();

        Assert.Same(graph, NodeBypass.Apply(graph, TypeOf));
    }

    [Fact]
    public void A_bypassed_node_is_taken_out_and_its_input_wired_to_what_it_fed() {
        var (graph, source, filter, sink) = Chain();
        filter.Bypassed = true;

        var walked = NodeBypass.Apply(graph, TypeOf);

        Assert.False(walked.TryGet(filter.Id, out _));
        Assert.Equal(new PortRef(source.Id, "Out"), walked.Source(new(sink.Id, "In")));

        // The author's graph keeps the node, its flag and both wires.
        Assert.True(graph.TryGet(filter.Id, out var kept) && kept.Bypassed);
        Assert.Equal(2, graph.Edges.Count);
    }

    [Fact]
    public void It_passes_the_first_input_of_the_outputs_kind_not_the_first_input() {
        Assert.Equal("In", NodeBypass.Through(Filter, "Out"));
        Assert.Null(NodeBypass.Through(Maker, "Out"));
    }

    [Fact]
    public void A_bypassed_node_with_nothing_to_pass_leaves_what_it_fed_unwired() {
        NodeGraphModel graph = new();
        var maker = graph.Add("Test/Maker");
        var sink = graph.Add("Test/Sink");
        graph.Connect(new(maker.Id, "Out"), new(sink.Id, "In"));
        maker.Bypassed = true;

        var walked = NodeBypass.Apply(graph, TypeOf);

        Assert.Null(walked.Source(new(sink.Id, "In")));
        Assert.Empty(walked.Edges);
    }

    [Fact]
    public void A_chain_of_reroutes_and_bypassed_nodes_traces_back_to_the_real_source() {
        NodeGraphModel graph = new();
        var source = graph.Add("Test/Source");
        var dot = graph.Add(Reroutes.Type);
        var filter = graph.Add("Test/Filter");
        var second = graph.Add(Reroutes.Type);
        var sink = graph.Add("Test/Sink");
        graph.Connect(new(source.Id, "Out"), new(dot.Id, Reroutes.In));
        graph.Connect(new(dot.Id, Reroutes.Out), new(filter.Id, "In"));
        graph.Connect(new(filter.Id, "Out"), new(second.Id, Reroutes.In));
        graph.Connect(new(second.Id, Reroutes.Out), new(sink.Id, "In"));
        filter.Bypassed = true;

        var walked = NodeBypass.Apply(graph, TypeOf);

        Assert.Equal(2, walked.Nodes.Count);
        Assert.Equal(new PortRef(source.Id, "Out"), walked.Source(new(sink.Id, "In")));
    }

    [Fact]
    public void A_reroute_alone_is_a_straight_wire() {
        NodeGraphModel graph = new();
        var source = graph.Add("Test/Source");
        var dot = graph.Add(Reroutes.Type);
        var filter = graph.Add("Test/Filter");
        graph.Connect(new(source.Id, "Out"), new(dot.Id, Reroutes.In));
        graph.Connect(new(dot.Id, Reroutes.Out), new(filter.Id, "In"));
        filter.SetValue("Amount", 0.5f);

        var walked = NodeBypass.Apply(graph, TypeOf);

        Assert.Equal(new PortRef(source.Id, "Out"), walked.Source(new(filter.Id, "In")));
        Assert.True(walked.TryGet(filter.Id, out var copy));
        Assert.Equal([0.5f], copy.Values["Amount"]);
    }

    [Fact]
    public void A_reroute_carries_what_feeds_it_or_failing_that_what_it_feeds() {
        NodeGraphModel graph = new();
        var source = graph.Add("Test/Source");
        var fed = graph.Add(Reroutes.Type);
        var unfed = graph.Add(Reroutes.Type);
        var filter = graph.Add("Test/Filter");
        var lonely = graph.Add(Reroutes.Type);
        graph.Connect(new(source.Id, "Out"), new(fed.Id, Reroutes.In));
        graph.Connect(new(unfed.Id, Reroutes.Out), new(filter.Id, "Amount"));

        Assert.Equal(PortKind.Float4, Reroutes.KindOf(graph, fed.Id, TypeOf));
        Assert.Equal(PortKind.Float, Reroutes.KindOf(graph, unfed.Id, TypeOf));
        Assert.Equal(PortKind.None, Reroutes.KindOf(graph, lonely.Id, TypeOf));
    }

    [Fact]
    public void Removing_a_node_with_relink_bridges_its_pass_through_input_to_everything_it_fed() {
        var (graph, source, filter, sink) = Chain();
        var other = graph.Add("Test/Sink");
        graph.Connect(new(filter.Id, "Out"), new(other.Id, "In"));

        var bridges = NodeBypass.Bridges(graph, new HashSet<NodeId> { filter.Id }, TypeOf);

        Assert.Equal(
            [new(new(source.Id, "Out"), new(sink.Id, "In")), new(new(source.Id, "Out"), new(other.Id, "In"))],
            bridges
        );
    }

    [Fact]
    public void Removing_both_ends_of_a_wire_bridges_nothing() {
        var (graph, source, filter, _) = Chain();

        Assert.Empty(NodeBypass.Bridges(graph, new HashSet<NodeId> { source.Id, filter.Id }, TypeOf));
    }
}
