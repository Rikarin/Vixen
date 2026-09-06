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

    /// <summary>What an author typed on the sub-graph node is what its expressions fold against.</summary>
    /// <remarks>
    ///     ⚠ <b>Doc 48 § D9's knob, which for three batches was a field that accepted a number and
    ///     changed nothing — <a href="https://github.com/Rikarin/Vixen/issues/742">#742</a>.</b> The
    ///     override is stored on the sub-graph node and <c>Flatten</c> deletes that node, so the
    ///     value used to reach nothing and the expression folded against the published graph's
    ///     declared default. 4 × 2 is the author's number; 6 is the default's, and is what this
    ///     asserted before the fix.
    /// </remarks>
    [Fact]
    public void A_knob_turned_on_the_sub_graph_node_reaches_the_expression_inside_it() {
        var (compiler, graph, used) = Containing(
            Published("amount * 2f"),
            [new("amount", Default: 3f, Minimum: 0f, Maximum: 10f)]
        );

        used.SetText("amount", "4");

        var compilation = compiler.Compile(graph);

        Assert.Empty(compilation.Diagnostics);

        var blur = compilation.Value.Ops.First(op => string.Equals(op.Kernel, "Blur", StringComparison.Ordinal));

        Assert.Equal(8f, blur.Find("radius")!.Value.Value);
    }

    /// <summary>Two nodes of one published type are two sets of knobs, not one.</summary>
    /// <remarks>
    ///     ⚠ <b>The failure the fix for #742 could most easily have had.</b> Expressions are folded
    ///     in batches — one Raven compilation per group rather than one per field — and the obvious
    ///     key for a group is the published graph's path, which is exactly the key that cannot tell
    ///     these two apart. Grouping that way makes both blurs take whichever value the walk reached
    ///     first, and every assertion about a single instance still passes.
    /// </remarks>
    [Fact]
    public void Two_instances_of_one_published_graph_keep_their_own_knobs() {
        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        TextureGraphLibrary library = new();

        library.Publish(
            "Library/Grunge",
            Published("amount * 2f"),
            [new("amount", Default: 3f, Minimum: 0f, Maximum: 10f)],
            registry
        );

        NodeGraphModel graph = new();
        var first = graph.Add("Library/Grunge");
        var second = graph.Add("Library/Grunge");
        var blend = graph.Add("Colour/Blend");
        var output = graph.Add("Output/Output");

        first.SetText("amount", "1");
        second.SetText("amount", "5");

        graph.Connect(new(first.Id, "Out"), new(blend.Id, "Background"));
        graph.Connect(new(second.Id, "Out"), new(blend.Id, "Foreground"));
        graph.Connect(new(blend.Id, "Out"), new(output.Id, "Input"));

        var compilation = new TextureGraphCompiler(registry) {
            BaseWidth = 128,
            BaseHeight = 128,
            SubGraphSource = library
        }.Compile(graph);

        Assert.Empty(compilation.Diagnostics);

        // Two separable blurs each emit two ops, and the pair that share a radius are one instance.
        var radii = compilation.Value.Ops
            .Where(op => string.Equals(op.Kernel, "Blur", StringComparison.Ordinal))
            .Select(op => op.Find("radius")!.Value.Value)
            .Distinct()
            .Order()
            .ToArray();

        Assert.Equal([2f, 10f], radii);
    }

    /// <summary>An override that will not parse keeps the default and names the node carrying it.</summary>
    /// <remarks>
    ///     ⚠ <b>The half that makes the knob honest.</b> Reading an unparseable override as zero is
    ///     the failure <c>TextureGraphParameters.Read</c> exists to prevent — zero is a
    ///     valid-looking radius — and reporting it against no node at all would be a complaint about
    ///     a graph rather than about the node the author typed into.
    /// </remarks>
    [Fact]
    public void A_knob_given_something_that_is_not_a_number_says_so_against_the_node() {
        var (compiler, graph, used) = Containing(
            Published("amount * 2f"),
            [new("amount", Default: 3f, Minimum: 0f, Maximum: 10f)]
        );

        used.SetText("amount", "quite a lot");

        var compilation = compiler.Compile(graph);
        var diagnostic = Assert.Single(compilation.Diagnostics, one => one.Id == "TG0015");

        Assert.Equal(used.Id, diagnostic.Node);
        Assert.Contains("Library/Grunge", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("keeps its default of 3", diagnostic.Message, StringComparison.Ordinal);

        // And the picture is the default's rather than zero's.
        var blur = compilation.Value.Ops.First(op => string.Equals(op.Kernel, "Blur", StringComparison.Ordinal));

        Assert.Equal(6f, blur.Find("radius")!.Value.Value);
    }

    /// <summary>A knob set inside a published graph, on a graph <em>it</em> contains, travels too.</summary>
    /// <remarks>
    ///     ⚠ <b>The case that decides whether the overrides are keyed on the right node.</b>
    ///     <c>NodeOrigin.Source</c> is deliberately the <em>outermost</em> sub-graph node, because
    ///     that is the only node a canvas has to select; the settings that apply two levels in are
    ///     the ones written on the inner node, inside the published file. Keying the overrides on
    ///     <c>Source</c> would hand the inner graph the outer node's table — which for a shipped
    ///     compound is a stranger's numbers.
    /// </remarks>
    [Fact]
    public void A_knob_set_inside_a_published_graph_reaches_the_graph_that_one_contains() {
        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        TextureGraphLibrary library = new();

        library.Publish(
            "Library/Grunge",
            Published("amount * 2f"),
            [new("amount", Default: 3f, Minimum: 0f, Maximum: 10f)],
            registry
        );

        // A second published graph that contains the first, and turns its knob to 5.
        NodeGraphModel outer = new() { Name = "Wear" };

        outer.Interface.Add(new("Out", PortDirection.Output, PortKind.Image));

        var inner = outer.Add("Library/Grunge");
        var exit = outer.Add(SubGraphs.OutputType);

        inner.SetText("amount", "5");
        outer.Connect(new(inner.Id, "Out"), new(exit.Id, "Out"));

        library.Publish("Library/Wear", outer, [], registry);

        NodeGraphModel graph = new();
        var used = graph.Add("Library/Wear");
        var output = graph.Add("Output/Output");

        graph.Connect(new(used.Id, "Out"), new(output.Id, "Input"));

        var compilation = new TextureGraphCompiler(registry) {
            BaseWidth = 128,
            BaseHeight = 128,
            SubGraphSource = library
        }.Compile(graph);

        Assert.Empty(compilation.Diagnostics);

        var blur = compilation.Value.Ops.First(op => string.Equals(op.Kernel, "Blur", StringComparison.Ordinal));

        Assert.Equal(10f, blur.Find("radius")!.Value.Value);
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

    /// <summary>A graph lifted out of another one still folds the expressions it took with it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The gesture that had no test, and it was broken —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/802">#802</a>.</b> "Extract to
    ///         sub-graph" copied a node's <c>Texts</c>, which is where an expression lives, and left
    ///         the <c>Parameters</c> those texts are written against behind. So the published graph
    ///         declared no <c>amount</c>, <c>Bind</c> folded <c>amount * 2f</c> against an empty
    ///         list, and the author got <c>TG0013</c> about a graph the editor had built for them a
    ///         moment earlier.
    ///     </para>
    ///     <para>
    ///         <b>Every step here is the editor's own.</b> <c>SubGraphs.Extract</c> is what the
    ///         canvas calls, an empty <c>exposed</c> list is what <c>TextureCompoundLibrary</c>
    ///         passes, and the radius is read off the op — so a fix that carried the declarations
    ///         somewhere they are not read would leave this red.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_graph_extracted_out_of_another_keeps_the_knobs_its_expressions_read() {
        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        NodeGraphModel authored = new() { Name = "Material" };

        authored.Parameters.Add(new("amount", "3", Kind: SettingKind.Float, Minimum: 0f, Maximum: 10f));

        var noise = authored.Add("Source/Noise");
        var blur = authored.Add("Filters/Blur");
        var output = authored.Add("Output/Output");

        authored.Connect(new(noise.Id, "Out"), new(blur.Id, "Input"));
        authored.Connect(new(blur.Id, "Out"), new(output.Id, "Input"));
        blur.SetValue("Radius", 2f);
        blur.SetText(TextureGraphExpressions.KeyOf("Radius"), "amount * 2f");

        var extraction = SubGraphs.Extract(authored, [blur.Id], "Grunge", registry);

        // The empty list is `TextureCompoundLibrary`'s call: publish what the graph itself declares.
        TextureGraphLibrary library = new();

        library.Publish("Library/Grunge", extraction.Graph, [], registry);

        NodeGraphModel container = new();
        var source = container.Add("Source/Noise");
        var used = container.Add("Library/Grunge");
        var sink = container.Add("Output/Output");

        container.Connect(new(source.Id, "Out"), new(used.Id, extraction.Inputs.Values.Single()));
        container.Connect(new(used.Id, extraction.Outputs.Values.Single()), new(sink.Id, "Input"));

        var compilation = new TextureGraphCompiler(registry) {
            BaseWidth = 128,
            BaseHeight = 128,
            SubGraphSource = library
        }.Compile(container);

        Assert.Empty(compilation.Diagnostics);

        // 3 × 2, folded against the declaration that crossed — and not 2, which is the value the port
        // still carries and what a graph whose expression did not fold would have baked.
        var inlined = compilation.Value.Ops.First(
            op => string.Equals(op.Kernel, "Blur", StringComparison.Ordinal)
        );

        Assert.Equal(6f, inlined.Find("radius")!.Value.Value);
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
