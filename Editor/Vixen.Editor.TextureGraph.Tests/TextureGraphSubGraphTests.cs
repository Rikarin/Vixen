// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.NodeGraph;
using Vixen.Editor.TextureGraph;
using Xunit;

namespace Tests;

/// <summary>Doc 48 § D9: a published graph is a node, inlined rather than called.</summary>
/// <remarks>
///     ⚠ <b>Ask what this file prints on the day inlining stops happening.</b> Every graph below
///     contains a node of a type only the library knows, so a compiler that did not inline would
///     report <c>NG0001</c> ("no node type is registered") and produce no plan —
///     <see cref="A_sub_graph_is_inlined_rather_than_called" /> asserts on the op list of a plan that
///     would not exist. It cannot pass over an inlining that did not run.
/// </remarks>
public class TextureGraphSubGraphTests {
    /// <summary>A published graph: noise, blurred, out through the boundary.</summary>
    static NodeGraphModel Published(string expression = "") {
        NodeGraphModel graph = new() { Name = "Grunge" };

        graph.Interface.Add(new("Out", PortDirection.Output, PortKind.Image));

        var noise = graph.Add("Source/Noise");
        var blur = graph.Add("Filters/Blur");
        var exit = graph.Add(SubGraphs.OutputType);

        graph.Connect(new(noise.Id, "Out"), new(blur.Id, "Input"));
        graph.Connect(new(blur.Id, "Out"), new(exit.Id, "Out"));
        blur.SetValue("Radius", 2f);

        if (expression.Length > 0) {
            blur.SetText(TextureGraphExpressions.KeyOf("Radius"), expression);
        }

        return graph;
    }

    static (TextureGraphCompiler Compiler, NodeGraphModel Graph, GraphNode Used) Containing(
        NodeGraphModel published,
        IReadOnlyList<TextureGraphParameter>? exposed = null
    ) {
        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        TextureGraphLibrary library = new();

        library.Publish("Library/Grunge", published, exposed ?? [], registry);

        NodeGraphModel graph = new();
        var used = graph.Add("Library/Grunge");
        var levels = graph.Add("Colour/Levels");
        var output = graph.Add("Output/Output");

        graph.Connect(new(used.Id, "Out"), new(levels.Id, "Input"));
        graph.Connect(new(levels.Id, "Out"), new(output.Id, "Input"));

        TextureGraphCompiler compiler = new(registry) {
            BaseWidth = 128,
            BaseHeight = 128,
            Seed = 9,
            SubGraphSource = library
        };

        return (compiler, graph, used);
    }

    /// <summary>The sub-graph's ops land in the containing graph's plan, in order, flat.</summary>
    [Fact]
    public void A_sub_graph_is_inlined_rather_than_called() {
        var (compiler, graph, _) = Containing(Published());
        var compilation = compiler.Compile(graph);

        Assert.Empty(compilation.Diagnostics);

        // Noise and the two halves of the separable blur are the sub-graph's; Levels is the
        // container's. One list, one plan, one pool — which is what "inlined rather than called"
        // means and why doc 48 § D9 chose it.
        Assert.Equal(
            ["Noise", "Blur", "Blur", "Levels"],
            compilation.Value.Ops.Select(op => op.Kernel).ToArray()
        );

        Assert.False(compiler.Inlining.IsEmpty);
    }

    /// <summary>
    ///     ⚠ A complaint about a node inside a sub-graph names a node the author can select.
    /// </summary>
    /// <remarks>
    ///     <b>This is the half of doc 48 § D9 that had to be proved rather than assumed.</b> The walk
    ///     is over the flattened graph, whose inlined nodes have identities that are in no document
    ///     and on no canvas — so a diagnostic naming one is a diagnostic nothing can select, frame or
    ///     badge. What the author gets is the sub-graph node in their own graph, plus a sentence
    ///     saying which node inside which library entry it really was.
    /// </remarks>
    [Fact]
    public void A_complaint_from_inside_a_sub_graph_names_a_node_the_author_has() {
        var (compiler, graph, used) = Containing(Published("nothing * 2f"));
        var compilation = compiler.Compile(graph);

        var diagnostic = Assert.Single(compilation.Diagnostics, one => one.Id == "TG0013");

        // The node is the one on the author's canvas — the sub-graph node — and not the synthetic
        // identity the flattened graph gave the Blur inside it.
        Assert.Equal(used.Id, diagnostic.Node);
        Assert.True(graph.TryGet(diagnostic.Node, out _));

        // And the sentence that says where it really was, so "which of the eight blurs" has an
        // answer.
        Assert.Contains("inside 'Library/Grunge'", diagnostic.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     An expression inside a sub-graph binds against <em>that</em> graph's parameters.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The containing graph declares a parameter of the same name and a different value, and
    ///     that is the whole test.</b> A compiler that bound the flattened graph against one
    ///     parameter list would compile this perfectly and produce a radius of 20 — a plausible
    ///     picture, from a published graph silently reading a knob that happens to share its name
    ///     with one of its own.
    /// </remarks>
    [Fact]
    public void An_inlined_expression_binds_against_its_own_graphs_parameters() {
        var (compiler, graph, _) = Containing(
            Published("amount * 2f"),
            [new("amount", Default: 3f, Minimum: 0f, Maximum: 10f)]
        );

        compiler.Parameters.Add(new("amount", Default: 10f, Minimum: 0f, Maximum: 20f));

        var compilation = compiler.Compile(graph);

        Assert.Empty(compilation.Diagnostics);

        var blur = compilation.Value.Ops.First(op => string.Equals(op.Kernel, "Blur", StringComparison.Ordinal));

        Assert.Equal(6f, blur.Find("radius")!.Value.Value);
    }

    /// <summary>A published graph with knobs is a node with settings, and the library keeps them.</summary>
    [Fact]
    public void A_published_graph_registers_a_node_type_carrying_its_parameters() {
        NodeTypeRegistry registry = new();
        TextureGraphLibrary library = new();
        List<TextureGraphParameter> exposed = [new("amount", Default: 0.5f, Minimum: 0f, Maximum: 1f)];

        library.Publish("Library/Grunge", Published(), exposed, registry);

        Assert.True(registry.TryGet("Library/Grunge", out var definition));
        Assert.Equal(["amount"], definition.Settings.Select(setting => setting.Name).ToArray());
        Assert.Equal(["Out"], definition.Ports.Select(port => port.Name).ToArray());
        Assert.Equal(["amount"], library.ParametersOf("Library/Grunge").Select(one => one.Name).ToArray());
    }

    /// <summary>A parameter list that does not hold together is refused at publication.</summary>
    /// <remarks>
    ///     ⚠ <b>Once, where it is wrong, rather than once per graph that contains it.</b> A library
    ///     entry is contained by many graphs, and reporting its fault against each of their authors
    ///     names the wrong person every time.
    /// </remarks>
    [Fact]
    public void A_graph_whose_parameters_do_not_hold_together_is_not_published() {
        TextureGraphLibrary library = new();

        var failure = Assert.Throws<ArgumentException>(
            () => library.Publish("Library/Grunge", Published(), [new("amount"), new("amount")])
        );

        Assert.Contains("Two parameters are called 'amount'", failure.Message, StringComparison.Ordinal);
        Assert.Empty(library.Paths);
    }

    /// <summary>A sub-graph nothing inlined is said as that, rather than as "not a texture node".</summary>
    /// <remarks>
    ///     ⚠ <b>The failure a host that forgot the library gets, and it is worth its own sentence.</b>
    ///     The node type <em>is</em> registered — publishing put it there — so the framework's
    ///     <c>NG0001</c> never fires; what arrives at the walk is a <c>SubGraphNode</c>, which is not
    ///     a texture node, and the generic message for that says something true and useless about a
    ///     node that is exactly the right thing to have on the canvas.
    /// </remarks>
    [Fact]
    public void A_sub_graph_nobody_inlined_says_the_library_is_missing() {
        var (compiler, graph, used) = Containing(Published());

        compiler.SubGraphSource = null;

        var diagnostic = Assert.Single(compiler.Compile(graph).Diagnostics, one => one.Id == "TG0001");

        Assert.Equal(used.Id, diagnostic.Node);
        Assert.Contains("no library to resolve sub-graphs through", diagnostic.Message, StringComparison.Ordinal);
    }

    /// <summary>Which image each node wrote is named by a node the author has, not by a copy.</summary>
    [Fact]
    public void An_inlined_nodes_image_is_recorded_against_the_node_the_author_has() {
        var (compiler, graph, used) = Containing(Published());

        compiler.PreviewEveryNode = true;
        compiler.Compile(graph);

        Assert.NotEmpty(compiler.NodeImages);

        foreach (var written in compiler.NodeImages) {
            Assert.True(graph.TryGet(written.Node, out _), $"{written.Node} is in no document.");
        }

        // The sub-graph node stands for three images — noise, the blur's scratch is not a port, and
        // the blur's own output — so it appears more than once, which is right: it is one node that
        // produced several.
        Assert.Contains(compiler.NodeImages, written => written.Node == used.Id);
    }
}
