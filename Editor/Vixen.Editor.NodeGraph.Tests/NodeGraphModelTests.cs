// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Core.Yaml;
using Vixen.Editor.NodeGraph;
using Xunit;

namespace Tests;

/// <summary>
///     The document model: what a graph is, before anything compiles one — doc 11 § node graphs.
/// </summary>
public class NodeGraphModelTests {
    [Fact]
    public void An_input_takes_one_edge_and_a_second_replaces_the_first() {
        var graph = new NodeGraphModel();
        var first = graph.Add("Test/Constant");
        var second = graph.Add("Test/Constant");
        var sink = graph.Add("Test/Combine");

        Assert.Null(graph.Connect(new(first.Id, "Out"), new(sink.Id, "A")));

        var replaced = graph.Connect(new(second.Id, "Out"), new(sink.Id, "A"));

        Assert.NotNull(replaced);
        Assert.Equal(first.Id, replaced.Value.From.Node);
        Assert.Equal(second.Id, graph.Source(new(sink.Id, "A"))!.Value.Node);
        Assert.Single(graph.Edges);
    }

    /// <summary>An output feeds as many inputs as an author likes.</summary>
    [Fact]
    public void An_output_takes_any_number_of_edges() {
        var graph = new NodeGraphModel();
        var source = graph.Add("Test/Constant");
        var one = graph.Add("Test/Combine");
        var two = graph.Add("Test/Combine");

        graph.Connect(new(source.Id, "Out"), new(one.Id, "A"));
        graph.Connect(new(source.Id, "Out"), new(two.Id, "A"));

        Assert.Equal(2, graph.Edges.Count);
    }

    /// <summary>
    ///     A cycle is refused where it is made, which is what lets everything downstream assume there
    ///     is not one.
    /// </summary>
    [Fact]
    public void A_cycle_is_refused_as_it_is_made() {
        var graph = new NodeGraphModel();
        var a = graph.Add("Test/Combine");
        var b = graph.Add("Test/Combine");
        var c = graph.Add("Test/Combine");

        graph.Connect(new(a.Id, "Out"), new(b.Id, "A"));
        graph.Connect(new(b.Id, "Out"), new(c.Id, "A"));

        var refused = Assert.Throws<ArgumentException>(() => graph.Connect(new(c.Id, "Out"), new(a.Id, "A")));

        Assert.Contains("cycle", refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, graph.Edges.Count);
    }

    [Fact]
    public void A_node_cannot_be_connected_to_itself() {
        var graph = new NodeGraphModel();
        var node = graph.Add("Test/Combine");

        Assert.Throws<ArgumentException>(() => graph.Connect(new(node.Id, "Out"), new(node.Id, "A")));
    }

    /// <summary>Every node comes after everything feeding it.</summary>
    [Fact]
    public void The_order_puts_a_node_after_everything_that_feeds_it() {
        var graph = new NodeGraphModel();

        // Built back to front on purpose, so insertion order is not the answer.
        var last = graph.Add("Test/Combine");
        var middle = graph.Add("Test/Combine");
        var first = graph.Add("Test/Constant");

        graph.Connect(new(first.Id, "Out"), new(middle.Id, "A"));
        graph.Connect(new(middle.Id, "Out"), new(last.Id, "A"));

        Assert.Equal([first.Id, middle.Id, last.Id], graph.Ordered().Select(node => node.Id));
    }

    /// <summary>Removing a node takes its edges with it, and hands them back for an undo.</summary>
    [Fact]
    public void Removing_a_node_detaches_its_edges_and_reports_them() {
        var graph = new NodeGraphModel();
        var source = graph.Add("Test/Constant");
        var middle = graph.Add("Test/Combine");
        var sink = graph.Add("Test/Combine");

        graph.Connect(new(source.Id, "Out"), new(middle.Id, "A"));
        graph.Connect(new(middle.Id, "Out"), new(sink.Id, "A"));

        var removed = graph.Remove(middle.Id, out var detached);

        Assert.NotNull(removed);
        Assert.Empty(graph.Edges);
        Assert.Equal(2, detached.Length);
    }

    /// <summary>An undo puts the node back under the identity it had, so the edges still name it.</summary>
    [Fact]
    public void A_removed_node_goes_back_with_its_identity() {
        var graph = new NodeGraphModel();
        var source = graph.Add("Test/Constant");
        var sink = graph.Add("Test/Combine");

        graph.Connect(new(source.Id, "Out"), new(sink.Id, "A"));

        var removed = graph.Remove(source.Id, out var detached)!;

        graph.Restore(removed);

        foreach (var edge in detached) {
            graph.Connect(edge.From, edge.To);
        }

        Assert.Equal(source.Id, graph.Source(new(sink.Id, "A"))!.Value.Node);
    }

    /// <summary>An identity is never reused, which is what makes the undo above safe.</summary>
    [Fact]
    public void An_identity_is_never_handed_out_twice() {
        var graph = new NodeGraphModel();
        var first = graph.Add("Test/Constant");

        graph.Remove(first.Id, out _);

        var second = graph.Add("Test/Constant");

        Assert.NotEqual(first.Id, second.Id);
    }

    /// <summary>
    ///     A graph goes out and comes back the same, identities included.
    /// </summary>
    /// <remarks>
    ///     Through the YAML serializer rather than through the shape conversion alone, because that is
    ///     what a file actually is. Identities are asserted because renumbering on load would make the
    ///     diff of a graph nobody changed the whole graph.
    /// </remarks>
    [Fact]
    public void A_graph_survives_a_round_trip_through_a_file() {
        var graph = new NodeGraphModel { Name = "Round" };
        var a = graph.Add("Test/Constant", new(10f, 20f));
        var b = graph.Add("Test/Combine", new(120f, 20f));

        b.SetValue("B", 0.5f);
        graph.Connect(new(a.Id, "Out"), new(b.Id, "A"));

        graph.Groups.Add(new() { Title = "Everything", Nodes = { a.Id, b.Id } });
        graph.Comments.Add(new() { Text = "why this is here", Position = new(0f, 200f), Size = new(180f, 90f) });

        var yaml = YamlSerializer.ToYaml(NodeGraphDocument.Save(graph));
        var reloaded = NodeGraphDocument.Load(YamlSerializer.Parse<NodeGraphAsset>(yaml), out var diagnostics);

        Assert.Empty(diagnostics);
        Assert.Equal("Round", reloaded.Name);
        Assert.Equal(graph.Nodes.Select(node => node.Id), reloaded.Nodes.Select(node => node.Id));
        Assert.Equal(graph.Nodes.Select(node => node.Type), reloaded.Nodes.Select(node => node.Type));
        Assert.Equal(graph.Edges, reloaded.Edges);

        var restored = Assert.Single(reloaded.Nodes, node => node.Id == b.Id);

        Assert.Equal([0.5f], restored.Values["B"]);

        var group = Assert.Single(reloaded.Groups);

        Assert.Equal([a.Id, b.Id], group.Nodes);

        var comment = Assert.Single(reloaded.Comments);

        Assert.Equal("why this is here", comment.Text);
        Assert.Equal(new Vector2(180f, 90f), comment.Size);
    }

    /// <summary>A file that has been merged badly opens, and says what it lost.</summary>
    /// <remarks>
    ///     The alternative — refuse the file — is an editor that cannot be used to repair the thing
    ///     it is refusing.
    /// </remarks>
    [Fact]
    public void A_file_with_a_dangling_edge_opens_and_reports_it() {
        var asset = new NodeGraphAsset {
            Name = "Broken",
            Nodes = [new() { Id = 1, Type = "Test/Constant" }],
            Edges = [new() { FromNode = 1, FromPort = "Out", ToNode = 99, ToPort = "A" }]
        };

        var graph = NodeGraphDocument.Load(asset, out var diagnostics);

        Assert.Single(graph.Nodes);
        Assert.Empty(graph.Edges);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "NG0101");
    }

    /// <summary>A file from a later version is refused, because its bytes cannot be guessed at.</summary>
    [Fact]
    public void A_file_from_the_future_is_refused_rather_than_half_read() {
        var asset = new NodeGraphAsset { Version = NodeGraphDocument.Version + 1 };

        Assert.Throws<NotSupportedException>(() => NodeGraphDocument.Load(asset, out _));
    }
}
