// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.NodeGraph;
using Xunit;

namespace Tests;

/// <summary>Sub-graphs: the interface, the boundary nodes, inlining and extraction.</summary>
public class SubGraphTests {
    static NodeTypeRegistry Library() {
        var registry = new NodeTypeRegistry();
        Vixen.Editor.NodeGraph.Tests.NodeTypes.Register(registry);

        return registry;
    }

    /// <summary>A graph that adds a constant to whatever is fed in, as a sub-graph would express it.</summary>
    static NodeGraphModel Tint() {
        var graph = new NodeGraphModel { Name = "Tint" };

        graph.Interface.Add(new("Colour", PortDirection.Input, PortKind.Dynamic, [0.5f]));
        graph.Interface.Add(new("Result", PortDirection.Output, PortKind.Dynamic));

        var entry = graph.Add(SubGraphs.InputType);
        var combine = graph.Add("Test/Combine");
        var exit = graph.Add(SubGraphs.OutputType);

        graph.Connect(new(entry.Id, "Colour"), new(combine.Id, "A"));
        graph.Connect(new(combine.Id, "Out"), new(exit.Id, "Result"));

        return graph;
    }

    [Fact]
    public void The_entry_node_shows_the_interfaces_inputs_as_outputs() {
        var boundary = SubGraphs.Boundary(Tint(), SubGraphs.InputType);
        var port = Assert.Single(boundary.Ports);

        // ⚠ Turned round. Inside the graph, a value the container feeds in is something to read.
        Assert.Equal("Colour", port.Name);
        Assert.Equal(PortDirection.Output, port.Direction);
    }

    [Fact]
    public void The_exit_node_shows_the_interfaces_outputs_as_inputs() {
        var boundary = SubGraphs.Boundary(Tint(), SubGraphs.OutputType);
        var port = Assert.Single(boundary.Ports);

        Assert.Equal("Result", port.Name);
        Assert.Equal(PortDirection.Input, port.Direction);
    }

    [Fact]
    public void The_node_type_a_container_stores_has_the_interfaces_ports_the_right_way_round() {
        var definition = SubGraphs.Definition(Tint(), "Sub-graphs/Tint");

        Assert.Equal(2, definition.Ports.Length);

        // Inputs first, as the generator orders a compiled node's, so a sub-graph is drawn the same
        // way as everything beside it.
        Assert.Equal(PortDirection.Input, definition.Ports[0].Direction);
        Assert.Equal("Colour", definition.Ports[0].Name);
        Assert.Equal("Result", definition.Ports[1].Name);
    }

    [Fact]
    public void Inlining_replaces_the_node_with_the_graphs_contents_and_keeps_the_wiring() {
        var library = new SubGraphLibrary();
        library.Add("Sub-graphs/Tint", Tint());

        var host = new NodeGraphModel();
        var colour = host.Add("Test/Colour");
        var tint = host.Add("Sub-graphs/Tint");
        var sink = host.Add("Test/Combine");

        host.Connect(new(colour.Id, "Out"), new(tint.Id, "Colour"));
        host.Connect(new(tint.Id, "Result"), new(sink.Id, "A"));

        var flat = SubGraphs.Flatten(host, library, out var diagnostics);

        Assert.Empty(diagnostics);

        // Three nodes: the colour, the sink, and the one Combine that was inside the sub-graph. No
        // boundary nodes and no sub-graph node survive.
        Assert.Equal(3, flat.Nodes.Count);
        Assert.DoesNotContain(flat.Nodes, node => node.Type.StartsWith("Sub-graph", StringComparison.Ordinal));

        var inlined = Assert.Single(flat.Nodes, node => node.Id != colour.Id && node.Id != sink.Id);

        // The wire that arrived at the sub-graph now arrives at what was behind its entry node, and
        // the one that left now leaves what was in front of its exit node.
        Assert.Equal(new PortRef(colour.Id, "Out"), flat.Source(new(inlined.Id, "A")));
        Assert.Equal(new PortRef(inlined.Id, "Out"), flat.Source(new(sink.Id, "A")));
    }

    [Fact]
    public void The_authors_own_nodes_keep_their_identities_so_a_diagnostic_can_name_one() {
        var library = new SubGraphLibrary();
        library.Add("Sub-graphs/Tint", Tint());

        var host = new NodeGraphModel();
        var colour = host.Add("Test/Colour");
        var tint = host.Add("Sub-graphs/Tint");

        host.Connect(new(colour.Id, "Out"), new(tint.Id, "Colour"));

        var flat = SubGraphs.Flatten(host, library, out _);

        Assert.True(flat.TryGet(colour.Id, out var kept));
        Assert.Equal("Test/Colour", kept.Type);
    }

    [Fact]
    public void An_unconnected_sub_graph_input_leaves_its_value_on_whatever_it_fed() {
        var library = new SubGraphLibrary();
        library.Add("Sub-graphs/Tint", Tint());

        var host = new NodeGraphModel();
        var tint = host.Add("Sub-graphs/Tint");

        tint.SetValue("Colour", 0.25f, 0.5f, 0.75f);

        var flat = SubGraphs.Flatten(host, library, out _);
        var inlined = Assert.Single(flat.Nodes);

        // The entry node is gone, so there is nothing left to carry the value — it has to travel down
        // to the port that was reading it, or a sub-graph dropped in and not wired up would behave
        // differently from the graph it stands for.
        Assert.Equal([0.25f, 0.5f, 0.75f], inlined.Values["A"]);
    }

    [Fact]
    public void A_value_typed_at_the_top_travels_down_through_a_nested_sub_graph() {
        var library = new SubGraphLibrary();
        library.Add("Sub-graphs/Tint", Tint());

        // Twice's own input is wired straight through to the inner Tint's, and nothing feeds Twice.
        var outer = new NodeGraphModel { Name = "Twice" };
        outer.Interface.Add(new("In", PortDirection.Input, PortKind.Dynamic, [9f]));

        var entry = outer.Add(SubGraphs.InputType);
        var inner = outer.Add("Sub-graphs/Tint");

        outer.Connect(new(entry.Id, "In"), new(inner.Id, "Colour"));
        library.Add("Sub-graphs/Twice", outer);

        var host = new NodeGraphModel();
        var node = host.Add("Sub-graphs/Twice");

        node.SetValue("In", 0.75f);

        var flat = SubGraphs.Flatten(host, library, out _);
        var combine = Assert.Single(flat.Nodes);

        // ⚠ 0.75, not the interface's declared 9. The value has to be the one the outermost caller
        // typed, carried through two levels of entry node that neither of them fed.
        Assert.Equal([0.75f], combine.Values["A"]);
    }

    [Fact]
    public void A_sub_graph_that_contains_itself_is_refused_rather_than_inlined_for_ever() {
        var recursive = new NodeGraphModel { Name = "Loop" };
        recursive.Interface.Add(new("In", PortDirection.Input, PortKind.Dynamic));

        recursive.Add(SubGraphs.InputType);
        recursive.Add("Sub-graphs/Loop");

        var library = new SubGraphLibrary();
        library.Add("Sub-graphs/Loop", recursive);

        var host = new NodeGraphModel();
        host.Add("Sub-graphs/Loop");

        SubGraphs.Flatten(host, library, out var diagnostics);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "NG0111");
    }

    [Fact]
    public void Nested_sub_graphs_are_inlined_all_the_way_down() {
        var library = new SubGraphLibrary();
        library.Add("Sub-graphs/Tint", Tint());

        var outer = new NodeGraphModel { Name = "Twice" };
        outer.Interface.Add(new("In", PortDirection.Input, PortKind.Dynamic));
        outer.Interface.Add(new("Out", PortDirection.Output, PortKind.Dynamic));

        var entry = outer.Add(SubGraphs.InputType);
        var first = outer.Add("Sub-graphs/Tint");
        var second = outer.Add("Sub-graphs/Tint");
        var exit = outer.Add(SubGraphs.OutputType);

        outer.Connect(new(entry.Id, "In"), new(first.Id, "Colour"));
        outer.Connect(new(first.Id, "Result"), new(second.Id, "Colour"));
        outer.Connect(new(second.Id, "Result"), new(exit.Id, "Out"));

        library.Add("Sub-graphs/Twice", outer);

        var host = new NodeGraphModel();
        host.Add("Sub-graphs/Twice");

        var flat = SubGraphs.Flatten(host, library, out var diagnostics);

        Assert.Empty(diagnostics);
        Assert.Equal(2, flat.Nodes.Count);
        Assert.All(flat.Nodes, node => Assert.Equal("Test/Combine", node.Type));

        // The chain survives two levels of inlining: the second copy still reads the first's output.
        Assert.Single(flat.Edges);
    }

    [Fact]
    public void Extraction_makes_one_interface_port_per_crossing_rather_than_per_edge() {
        var graph = new NodeGraphModel();

        var source = graph.Add("Test/Colour");
        var left = graph.Add("Test/Combine");
        var right = graph.Add("Test/Combine");

        graph.Connect(new(source.Id, "Out"), new(left.Id, "A"));
        graph.Connect(new(source.Id, "Out"), new(right.Id, "A"));

        var extraction = SubGraphs.Extract(graph, [left.Id, right.Id], "Pair", Library());

        // ⚠ One input, not two. An external output feeding two selected nodes is one thing the
        // sub-graph reads, which is what the author drew.
        var input = Assert.Single(extraction.Graph.Interface, port => port.Direction == PortDirection.Input);

        Assert.Equal(2, extraction.Incoming.Length);
        Assert.Equal(input.Name, Assert.Single(extraction.Inputs.Values));
    }

    [Fact]
    public void Extraction_carries_the_kind_of_the_port_that_crossed() {
        var graph = new NodeGraphModel();

        var texture = graph.Add("Test/Texture");
        var inside = graph.Add("Test/Combine");

        // A texture arriving at a dynamic port is a type error, but the *extraction* still has to
        // describe what crossed: guessing Dynamic there would turn a refusable wire into a legal one.
        graph.Connect(new(texture.Id, "Out"), new(inside.Id, "A"));

        var extraction = SubGraphs.Extract(graph, [inside.Id], "Inner", Library());
        var port = Assert.Single(extraction.Graph.Interface, entry => entry.Direction == PortDirection.Input);

        Assert.Equal(PortKind.Texture, port.Kind);
    }

    /// <summary>Extraction and inlining both carry the names a node was given.</summary>
    /// <remarks>
    ///     ⚠ <b>Neither did.</b> Both copies took <c>Values</c> and left <c>Texts</c> behind, so
    ///     lifting a compositor pass into a sub-graph and dropping it back produced the same node with
    ///     every setting blanked — a silent change to what the graph renders, with the file's diff
    ///     showing only the surgery it was asked for.
    /// </remarks>
    [Fact]
    public void Extraction_and_inlining_carry_the_names_a_node_was_given() {
        var graph = new NodeGraphModel();
        var named = graph.Add("Test/Named Thing");

        named.SetText("Label", "glow");

        var extraction = SubGraphs.Extract(graph, [named.Id], "Inner", Library());

        Assert.Equal("glow", extraction.Graph.Nodes.Single(node => node.Id == named.Id).TextOf("Label"));

        var library = new SubGraphLibrary();
        library.Add("Sub-graphs/Inner", extraction.Graph);

        var host = new NodeGraphModel();

        host.Add("Sub-graphs/Inner");

        var flat = SubGraphs.Flatten(host, library, out var diagnostics);

        Assert.Empty(diagnostics);
        Assert.Equal("glow", Assert.Single(flat.Nodes).TextOf("Label"));
    }

    [Fact]
    public void An_extracted_graph_inlines_back_to_what_it_came_from() {
        var graph = new NodeGraphModel();

        var colour = graph.Add("Test/Colour");
        var middle = graph.Add("Test/Combine");
        var sink = graph.Add("Test/Combine");

        graph.Connect(new(colour.Id, "Out"), new(middle.Id, "A"));
        graph.Connect(new(middle.Id, "Out"), new(sink.Id, "A"));

        var extraction = SubGraphs.Extract(graph, [middle.Id], "Middle", Library());

        var library = new SubGraphLibrary();
        library.Add("Sub-graphs/Middle", extraction.Graph);

        // Apply the extraction by hand — the command is what does it in the editor — and check that
        // inlining the result reproduces the graph it was lifted out of.
        var replaced = new NodeGraphModel();
        var keptColour = replaced.Add(colour.Id, "Test/Colour");
        var keptSink = replaced.Add(sink.Id, "Test/Combine");
        var standIn = replaced.Add("Sub-graphs/Middle");

        replaced.Connect(new(keptColour.Id, "Out"), new(standIn.Id, extraction.Inputs.Values.Single()));
        replaced.Connect(new(standIn.Id, extraction.Outputs.Values.Single()), new(keptSink.Id, "A"));

        var flat = SubGraphs.Flatten(replaced, library, out var diagnostics);

        Assert.Empty(diagnostics);
        Assert.Equal(3, flat.Nodes.Count);

        var inlined = Assert.Single(flat.Nodes, node => node.Id != keptColour.Id && node.Id != keptSink.Id);

        Assert.Equal(new PortRef(keptColour.Id, "Out"), flat.Source(new(inlined.Id, "A")));
        Assert.Equal(new PortRef(inlined.Id, "Out"), flat.Source(new(keptSink.Id, "A")));
    }

    /// <summary>An extraction carries the knobs the texts it copies are written against.</summary>
    /// <remarks>
    ///     ⚠ <b>It did not, and the loss was silent both ways —
    ///     <a href="https://github.com/Rikarin/Vixen/issues/802">#802</a>.</b> The node's text
    ///     crossed and the declaration behind it did not, so a published sub-graph carried
    ///     <c>amount * 32f</c> and declared no <c>amount</c>; nothing refused it, and the containing
    ///     graph reported an undefined name about a graph that had compiled a moment earlier. This is
    ///     the framework half — <c>TextureGraphSubGraphTests</c> is the one that shows the expression
    ///     folding afterwards, which is what the parameter is <em>for</em>.
    /// </remarks>
    [Fact]
    public void Extraction_carries_the_parameters_a_copied_expression_is_written_against() {
        var graph = new NodeGraphModel();

        graph.Parameters.Add(new("amount", "0.5", Kind: SettingKind.Float, Minimum: 0f, Maximum: 1f));

        var named = graph.Add("Test/Named Thing");

        named.SetText("=Label", "amount * 32f");

        var extraction = SubGraphs.Extract(graph, [named.Id], "Inner", Library());
        var carried = Assert.Single(extraction.Graph.Parameters);

        Assert.Equal("amount", carried.Name);
        Assert.Equal("0.5", carried.Default);
        Assert.Equal(1f, carried.Maximum);
    }

    /// <summary>And the graph's own settings do not cross, which is the other half of #802.</summary>
    /// <remarks>
    ///     A sub-graph's base resolution and seed are ignored after a flatten — the containing
    ///     graph's are what a compiler reads — so copying them would put two numbers nothing reads
    ///     into an inspector that draws them as though something did.
    /// </remarks>
    [Fact]
    public void Extraction_does_not_carry_the_graphs_own_settings() {
        var graph = new NodeGraphModel();

        graph.Settings["BaseWidth"] = "512";

        var node = graph.Add("Test/Colour");

        Assert.Empty(SubGraphs.Extract(graph, [node.Id], "Inner", Library()).Graph.Settings);
    }

    [Fact]
    public void The_interface_survives_a_round_trip_through_the_file_shape() {
        var saved = NodeGraphDocument.Save(Tint());
        var loaded = NodeGraphDocument.Load(saved, out var diagnostics);

        Assert.Empty(diagnostics);
        Assert.Equal(2, loaded.Interface.Count);
        Assert.Equal("Colour", loaded.Interface[0].Name);
        Assert.Equal([0.5f], loaded.Interface[0].Default);
        Assert.Equal(PortDirection.Output, loaded.Interface[1].Direction);
    }

    [Fact]
    public void A_file_declaring_one_port_twice_loses_the_second_and_says_so() {
        var asset = new NodeGraphAsset {
            Interface = [
                new() { Name = "Colour", Direction = PortDirection.Input, Kind = PortKind.Float },
                new() { Name = "Colour", Direction = PortDirection.Input, Kind = PortKind.Float4 }
            ]
        };

        var loaded = NodeGraphDocument.Load(asset, out var diagnostics);

        Assert.Single(loaded.Interface);
        Assert.Equal(PortKind.Float, loaded.Interface[0].Kind);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "NG0103");
    }

    [Fact]
    public void A_compiler_with_a_source_inlines_before_it_walks_anything() {
        var registry = Library();
        var library = new SubGraphLibrary();

        library.Add("Sub-graphs/Tint", Tint(), registry);

        var host = new NodeGraphModel();
        var colour = host.Add("Test/Colour");
        var tint = host.Add("Sub-graphs/Tint");

        host.Connect(new(colour.Id, "Out"), new(tint.Id, "Colour"));

        var compiler = new ListingCompiler(registry) { SubGraphSource = library };
        var compiled = compiler.Compile(host);

        Assert.True(compiled.Succeeded);

        // The sub-graph node is nowhere in the walk: what the compiler saw is a colour and a combine.
        Assert.Equal(["Test/Colour", "Test/Combine"], compiled.Value);
    }

    [Fact]
    public void Without_a_source_a_sub_graph_node_is_simply_a_node_type_nobody_registered() {
        var host = new NodeGraphModel();
        host.Add("Sub-graphs/Tint");

        var compiled = new ListingCompiler(Library()).Compile(host);

        Assert.False(compiled.Succeeded);
        Assert.Contains(compiled.Diagnostics, diagnostic => diagnostic.Id == "NG0001");
    }

    /// <summary>The settings a sub-graph node carried survive the node itself.</summary>
    /// <remarks>
    ///     ⚠ <b>Inlining deletes the one thing the author typed into.</b> A sub-graph node's
    ///     <c>Texts</c> are where a containing graph stores what it set the published graph's knobs
    ///     to, and <c>Flatten</c> replaces that node with the graph's contents — so before
    ///     <a href="https://github.com/Rikarin/Vixen/issues/742">#742</a> the values reached nothing
    ///     at all and no compiler could have read them however it tried. The flattener records the
    ///     table rather than interpreting it, because which keys are knobs is the containing
    ///     language's question and not this assembly's.
    /// </remarks>
    [Fact]
    public void An_expansion_carries_what_the_sub_graph_node_was_set_to() {
        var library = new SubGraphLibrary();
        library.Add("Sub-graphs/Tint", Tint());

        var host = new NodeGraphModel();
        var colour = host.Add("Test/Colour");
        var first = host.Add("Sub-graphs/Tint");
        var second = host.Add("Sub-graphs/Tint");

        host.Connect(new(colour.Id, "Out"), new(first.Id, "Colour"));
        host.Connect(new(colour.Id, "Out"), new(second.Id, "Colour"));
        first.SetText("amount", "1");
        second.SetText("amount", "5");

        SubGraphs.Flatten(host, library, out _, out var inlining);

        // Two expansions of one path, and each answers with its own node's table — the whole reason
        // the key is an expansion rather than the type.
        Assert.Equal(2, inlining.Expansions.Count);

        var inlined = inlining.Origins.Keys.OrderBy(node => node.Value).ToArray();

        Assert.Equal(2, inlined.Length);

        var values = new List<string>();

        foreach (var node in inlined) {
            Assert.True(inlining.TryGetExpansion(node, out var expansion));
            Assert.Equal("Sub-graphs/Tint", expansion.Type);
            values.Add(expansion.Settings["amount"]);
        }

        Assert.Equal(["1", "5"], values.Order(StringComparer.Ordinal).ToArray());

        // ⚠ Copied rather than held: a compilation is an answer about the graph as it was, and the
        // table it read is one the author goes on editing.
        first.SetText("amount", "9");

        Assert.True(inlining.TryGetExpansion(inlined[0], out var again));
        Assert.NotEqual("9", again.Settings["amount"]);
    }

    /// <summary>A node the author wrote themselves came out of no expansion.</summary>
    [Fact]
    public void A_node_the_author_wrote_has_no_expansion() {
        var library = new SubGraphLibrary();
        library.Add("Sub-graphs/Tint", Tint());

        var host = new NodeGraphModel();
        var colour = host.Add("Test/Colour");

        host.Add("Sub-graphs/Tint");

        SubGraphs.Flatten(host, library, out _, out var inlining);

        Assert.False(inlining.TryGetExpansion(colour.Id, out _));
    }
}

/// <summary>A compiler that produces the list of node types it walked, in order.</summary>
/// <remarks>
///     The smallest thing that can answer "what did the compiler actually see", which is the only
///     question the inlining tests have. Every real compiler is a bigger version of this.
/// </remarks>
sealed class ListingCompiler(NodeTypeRegistry registry) : NodeGraphCompiler<List<string>>(registry) {
    readonly List<string> visited = [];

    protected override void Begin(NodeGraphModel graph) => visited.Clear();

    protected override void Visit(GraphNode node, NodeTypeDefinition definition, Node instance, NodeBinding binding) =>
        visited.Add(node.Type);

    protected override List<string>? Finish(NodeGraphModel graph) => [.. visited];

    protected override string Constant(ReadOnlySpan<float> value, PortKind kind) => "0";

    protected override string Convert(string expression, PortKind from, PortKind target) => expression;
}
