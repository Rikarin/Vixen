// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.NodeGraph;
using Xunit;

namespace Tests;

/// <summary>Laying a graph out: columns, no backwards wires, and the same answer twice.</summary>
public class LayoutTests {
    static NodeTypeRegistry Library() {
        var registry = new NodeTypeRegistry();
        Vixen.Editor.NodeGraph.Tests.NodeTypes.Register(registry);

        return registry;
    }

    [Fact]
    public void A_chain_comes_out_as_a_row_of_columns() {
        var graph = new NodeGraphModel();

        var first = graph.Add("Test/Colour");
        var second = graph.Add("Test/Combine");
        var third = graph.Add("Test/Combine");

        graph.Connect(new(first.Id, "Out"), new(second.Id, "A"));
        graph.Connect(new(second.Id, "Out"), new(third.Id, "A"));

        var options = NodeLayoutOptions.Default;
        var placed = NodeGraphLayout.Arrange(graph, Library(), options);

        Assert.True(placed[first.Id].X < placed[second.Id].X);
        Assert.True(placed[second.Id].X < placed[third.Id].X);

        // A chain is a line — of centres, not of tops. Every column holds one node and every column
        // is centred on the tallest, so a one-port node and a three-port one share a middle and not
        // an edge. Comparing tops here asserts that every node is the same height, which is the
        // assumption the layout exists to avoid.
        Assert.Equal(
            placed[first.Id].Y + (options.HeightOf(1) * 0.5f),
            placed[third.Id].Y + (options.HeightOf(2) * 0.5f),
            3
        );
    }

    [Fact]
    public void No_wire_ever_runs_backwards() {
        var graph = new NodeGraphModel();

        var a = graph.Add("Test/Colour");
        var b = graph.Add("Test/Vector");
        var c = graph.Add("Test/Combine");
        var d = graph.Add("Test/Combine");

        graph.Connect(new(a.Id, "Out"), new(c.Id, "A"));
        graph.Connect(new(b.Id, "Out"), new(c.Id, "B"));
        graph.Connect(new(c.Id, "Out"), new(d.Id, "A"));
        graph.Connect(new(a.Id, "Out"), new(d.Id, "B"));

        var placed = NodeGraphLayout.Arrange(graph, Library());

        foreach (var edge in graph.Edges) {
            Assert.True(placed[edge.From.Node].X < placed[edge.To.Node].X, $"{edge.From} runs backwards");
        }
    }

    [Fact]
    public void The_longest_path_decides_the_column_so_a_node_sits_beside_its_peers() {
        var graph = new NodeGraphModel();

        var source = graph.Add("Test/Colour");
        var middle = graph.Add("Test/Combine");
        var sink = graph.Add("Test/Combine");

        // Two routes to the sink: one hop and two. The shortest-path layering would put the source
        // one column left of the sink; the longest puts the sink two columns along, which is where
        // an author would have drawn it.
        graph.Connect(new(source.Id, "Out"), new(middle.Id, "A"));
        graph.Connect(new(middle.Id, "Out"), new(sink.Id, "A"));
        graph.Connect(new(source.Id, "Out"), new(sink.Id, "B"));

        var options = NodeLayoutOptions.Default;
        var placed = NodeGraphLayout.Arrange(graph, Library(), options);
        var stride = options.NodeWidth + options.ColumnGap;

        Assert.Equal(placed[source.Id].X + (2f * stride), placed[sink.Id].X, 3);
    }

    [Fact]
    public void Two_nodes_in_one_column_do_not_overlap() {
        var graph = new NodeGraphModel();

        var first = graph.Add("Test/Combine");
        var second = graph.Add("Test/Combine");
        var sink = graph.Add("Test/Combine");

        graph.Connect(new(first.Id, "Out"), new(sink.Id, "A"));
        graph.Connect(new(second.Id, "Out"), new(sink.Id, "B"));

        var options = NodeLayoutOptions.Default;
        var placed = NodeGraphLayout.Arrange(graph, Library(), options);

        Assert.Equal(placed[first.Id].X, placed[second.Id].X, 3);

        var gap = Math.Abs(placed[first.Id].Y - placed[second.Id].Y);

        // Two ports on the taller side of a Combine, so the box is that tall and the gap is at least
        // that plus the row gap. A layout that assumed one height per node overlaps here.
        Assert.True(gap >= options.HeightOf(2) + options.RowGap, $"they are {gap} apart");
    }

    [Fact]
    public void The_same_graph_lays_out_the_same_way_twice() {
        var graph = new NodeGraphModel();

        var a = graph.Add("Test/Colour");
        var b = graph.Add("Test/Vector");
        var c = graph.Add("Test/Combine");
        var d = graph.Add("Test/Combine");
        var e = graph.Add("Test/Combine");

        graph.Connect(new(a.Id, "Out"), new(c.Id, "A"));
        graph.Connect(new(b.Id, "Out"), new(c.Id, "B"));
        graph.Connect(new(c.Id, "Out"), new(d.Id, "A"));
        graph.Connect(new(a.Id, "Out"), new(e.Id, "A"));
        graph.Connect(new(d.Id, "Out"), new(e.Id, "B"));

        var first = NodeGraphLayout.Arrange(graph, Library());
        var second = NodeGraphLayout.Arrange(graph, Library());

        // A fixed number of sweeps rather than "until it stops improving", so the answer is a
        // function of the graph. A golden test of a laid-out graph needs that.
        Assert.Equal(first, second);
    }

    [Fact]
    public void An_empty_graph_lays_out_to_nothing() => Assert.Empty(NodeGraphLayout.Arrange(new(), Library()));

    [Fact]
    public void Unconnected_nodes_are_stacked_in_one_column() {
        var graph = new NodeGraphModel();

        var first = graph.Add("Test/Colour");
        var second = graph.Add("Test/Colour");
        var third = graph.Add("Test/Colour");

        var placed = NodeGraphLayout.Arrange(graph, Library());

        Assert.Equal(placed[first.Id].X, placed[second.Id].X, 3);
        Assert.Equal(placed[second.Id].X, placed[third.Id].X, 3);
        Assert.Equal(3, placed.Values.Select(position => position.Y).Distinct().Count());
    }
}

/// <summary>Ranking the node library against what was typed, and against a dragged wire.</summary>
public class SearchTests {
    static NodeTypeRegistry Library() {
        var registry = new NodeTypeRegistry();
        Vixen.Editor.NodeGraph.Tests.NodeTypes.Register(registry);

        return registry;
    }

    [Fact]
    public void An_exact_title_wins() {
        var results = NodeSearch.Rank(Library(), "Colour");

        Assert.Equal("Test/Colour", results[0].Type.Path);
        Assert.Equal(NodeSearch.Exact, results[0].Score);
    }

    [Fact]
    public void A_prefix_beats_a_substring() {
        var results = NodeSearch.Rank(Library(), "Co");
        var paths = results.Select(result => result.Type.Path).ToArray();

        // Colour and Combine both start with it; nothing else should be above them.
        Assert.Equal(["Test/Colour", "Test/Combine"], paths[..2]);
    }

    [Fact]
    public void A_query_that_matches_nothing_at_all_returns_nothing() =>
        Assert.Empty(NodeSearch.Rank(Library(), "zzzz"));

    [Fact]
    public void An_empty_query_offers_the_whole_library_in_path_order() {
        var registry = Library();
        var results = NodeSearch.Rank(registry, "");

        Assert.Equal(registry.Count, results.Length);
        Assert.Equal(results.Select(result => result.Type.Path).Order(StringComparer.Ordinal), results.Select(result => result.Type.Path));
    }

    [Fact]
    public void Ties_break_the_same_way_every_time() {
        var first = NodeSearch.Rank(Library(), "test");
        var second = NodeSearch.Rank(Library(), "test");

        // A create menu whose item under the cursor moves between keystrokes that changed nothing is
        // one nobody can use.
        Assert.Equal(first.Select(result => result.Type.Path), second.Select(result => result.Type.Path));
    }

    [Fact]
    public void A_wire_from_a_texture_output_offers_only_what_takes_a_texture() {
        var results = NodeSearch.Rank(Library(), "", new PortFilter(PortKind.Texture, PortDirection.Input));

        // Nothing in the fixture library has a texture input, and a dynamic port is not one: there is
        // no width a texture and a float agree on.
        Assert.Empty(results);
    }

    [Fact]
    public void A_wire_from_a_float_output_offers_the_dynamic_inputs_and_says_which() {
        var results = NodeSearch.Rank(Library(), "", new PortFilter(PortKind.Float, PortDirection.Input));
        var combine = Assert.Single(results, result => result.Type.Path == "Test/Combine");

        // The first port in declaration order, which is the topmost one drawn — what dropping a wire
        // on a node you have just created means.
        Assert.Equal("A", combine.Port);
    }

    [Fact]
    public void A_wire_dragged_off_an_input_looks_for_an_output() {
        var results = NodeSearch.Rank(Library(), "", new PortFilter(PortKind.Float3, PortDirection.Output));
        var paths = results.Select(result => result.Type.Path).ToArray();

        Assert.Contains("Test/Vector", paths);
        Assert.Contains("Test/Constant", paths);
        Assert.DoesNotContain("Test/Texture", paths);
    }

    [Fact]
    public void The_limit_takes_the_best_rather_than_the_first_found() {
        var results = NodeSearch.Rank(Library(), "Co", limit: 1);

        Assert.Single(results);
        Assert.Equal("Test/Colour", results[0].Type.Path);
    }
}
