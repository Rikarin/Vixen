// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.NodeGraph;
using Vixen.Editor.ShaderGraph;
using Vixen.Raven;
using Vixen.Raven.Syntax;
using Xunit;

namespace Tests;

/// <summary>
///     The other direction of doc 07's map: which node wrote which line of the generated shader, and
///     therefore which node an author should be sent to when Raven objects to one.
/// </summary>
/// <remarks>
///     <para>
///         <b>Every test here goes through the real front end.</b> A test that asserted a line number
///         against a string it built itself would pass on a mapping that is off by the height of the
///         header, which is the one mistake this feature can make that is worse than not having it —
///         doc 11's own words are that a diagnostic naming a line of a file the author never wrote is
///         no answer at all.
///     </para>
///     <para>
///         ⚠ <b>The graph in the sub-graph tests below is well-formed.</b> The error is a property
///         name with a space in it, which is a thing an author can type into the panel's rename box
///         and which no graph-level rule refuses: the node graph is fine and the shader does not
///         parse. That is exactly the failure doc 11 says a panel showing only the graph compiler's
///         diagnostics would report as success.
///     </para>
/// </remarks>
public class ShaderGraphSpanTests {
    static NodeTypeRegistry Library() {
        var registry = new NodeTypeRegistry();

        Vixen.Editor.ShaderGraph.NodeTypes.Register(registry);

        return registry;
    }

    /// <summary>What Raven says about a piece of generated text, as lines counted from zero.</summary>
    static IReadOnlyList<(int Line, string Message)> Check(string source) {
        var tree = SyntaxTree.ParseText(source, path: "Graph.rvn");
        var compilation = Compilation.Create("ShaderGraph", tree);

        List<(int, string)> found = [];

        foreach (var diagnostic in compilation.GetDiagnostics()) {
            found.Add((
                diagnostic.Location.IsNone ? 0 : diagnostic.Location.GetLineSpan().Start.Line,
                $"{diagnostic.Id}: {diagnostic.GetMessage()}"
            ));
        }

        return found;
    }

    /// <summary>A sub-graph whose property node is named something Raven cannot spell.</summary>
    /// <param name="property">What the property inside it is called.</param>
    static NodeGraphModel Tint(string property) {
        var graph = new NodeGraphModel { Name = "Tint" };

        graph.Interface.Add(new("Colour", PortDirection.Output, PortKind.Float4));

        var colour = graph.Add("Input/Colour Property");
        var exit = graph.Add(SubGraphs.OutputType);

        colour.SetText(ShaderProperties.Key, property);
        graph.Connect(new(colour.Id, "Colour"), new(exit.Id, "Colour"));

        return graph;
    }

    /// <summary>A graph that drops that sub-graph in front of a master, and nothing else.</summary>
    static (NodeGraphModel Graph, NodeId SubGraph) Host(string property, out SubGraphLibrary library) {
        library = new();
        library.Add("Sub-graphs/Tint", Tint(property));

        var graph = new NodeGraphModel { Name = "Painted" };
        var tint = graph.Add("Sub-graphs/Tint");
        var master = graph.Add("Master/Unlit");

        graph.Connect(new(tint.Id, "Colour"), new(master.Id, "Colour"));

        return (graph, tint.Id);
    }

    /// <summary>The lines a node wrote are the lines its own variable is assigned on.</summary>
    [Fact]
    public void A_span_covers_the_lines_the_node_actually_wrote() {
        var graph = new NodeGraphModel { Name = "Tinted" };
        var uv = graph.Add("Input/UV");
        var master = graph.Add("Master/Unlit");

        graph.Connect(new(uv.Id, "UV"), new(master.Id, "Colour"));

        var result = new ShaderGraphCompiler(Library()).Compile(graph);

        Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));

        var lines = result.Value.Source.ReplaceLineEndings("\n").Split('\n');
        var span = Assert.Single(result.Value.Spans, candidate => candidate.Node == uv.Id);

        Assert.Equal(1, span.Span.Lines);
        Assert.Contains($"n{uv.Id.Value}_UV", lines[span.Span.Line], StringComparison.Ordinal);

        // And the compiler's own text belongs to nobody, which is what stops a complaint about the
        // header being blamed on whichever node happens to be nearest.
        Assert.False(result.Value.NodeAt(0, out _));
    }

    /// <summary>A node inlined out of a sub-graph writes lines the sub-graph node owns.</summary>
    [Fact]
    public void A_span_from_inside_a_sub_graph_names_the_node_the_author_can_select() {
        var (graph, subGraph) = Host("tint", out var library);

        var result = new ShaderGraphCompiler(Library()) { SubGraphSource = library }.Compile(graph);

        Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));

        var lines = result.Value.Source.ReplaceLineEndings("\n").Split('\n');
        var owned = result.Value.Spans.Where(candidate => candidate.Node == subGraph).ToArray();

        // Two: where the property node inside asked for its uniform, and where it read it.
        Assert.Equal(2, owned.Length);
        Assert.Contains(owned, candidate => lines[candidate.Span.Line].Contains("var tint:", StringComparison.Ordinal));

        var span = Assert.Single(owned, candidate => lines[candidate.Span.Line].Contains("val ", StringComparison.Ordinal));

        // The selectable node is the author's; the emitted one is the synthetic copy, and it is the
        // identity in the variable name on that very line.
        Assert.NotEqual(subGraph, span.Emitted);
        Assert.Contains($"n{span.Emitted.Value}_Colour", lines[span.Span.Line], StringComparison.Ordinal);

        // ⚠ And it is in the graph the author has open. A node identity that is in no document is the
        // whole defect: nothing can select it, frame it or put a badge on it.
        Assert.True(graph.TryGet(span.Node, out _));
    }

    /// <summary>
    ///     The instrument itself: a deliberate error inside a sub-graph, and the node it names.
    /// </summary>
    [Fact]
    public void Ravens_complaint_about_a_sub_graphs_line_names_the_sub_graph_node() {
        var (graph, subGraph) = Host("my tint", out var library);

        var result = new ShaderGraphCompiler(Library()) { SubGraphSource = library }.Compile(graph);

        // The graph is well-formed. Nothing here has anything to say about it.
        Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));

        var source = result.Value;
        var complaints = Check(source.Source);

        Assert.NotEmpty(complaints);

        List<NodeDiagnostic> blamed = [];

        foreach (var (line, message) in complaints) {
            if (source.NodeAt(line, out var span)) {
                blamed.Add(new("SG0100", message, span.Node, "", NodeSeverity.Error, span.Span));
            }
        }

        Assert.NotEmpty(blamed);

        var report = string.Join("\n", complaints.Select(entry => $"line {entry.Line + 1}: {entry.Message}"))
            + "\n\n"
            + string.Join("\n", source.Spans.Select(span => $"{span.Node} (emitted {span.Emitted}) {span.Span}"))
            + "\n\n"
            + source.Source;

        // ⚠ The *first*, and not every one. Raven keeps going after a syntax error, so a line the
        // parser could not finish leaves the next one holding half an expression — here the master's
        // `float4(…)` is handed two arguments instead of four. Blaming the master for its own line is
        // what the map is for and is not wrong; asserting that a cascade never happens would be a test
        // about Raven's recovery rather than about this mapping.
        Assert.True(subGraph == blamed[0].Node, report);

        // Both of the lines the author's mistake is actually on: where the property is declared, and
        // where it is read. Neither is in the sub-graph's file and neither is in theirs, and both name
        // a node that is in the graph they have open.
        Assert.Equal(
            2,
            blamed.Where(diagnostic => diagnostic.Node == subGraph).Select(diagnostic => diagnostic.Span).Distinct().Count()
        );

        foreach (var diagnostic in blamed) {
            Assert.True(graph.TryGet(diagnostic.Node, out _), report);
            Assert.False(diagnostic.Span.IsNone);
        }
    }
}
