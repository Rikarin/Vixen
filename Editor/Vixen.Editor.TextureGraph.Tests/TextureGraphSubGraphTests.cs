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

    /// <summary>A published graph whose radius is an interface port with a declared default of 8.</summary>
    /// <remarks>
    ///     ⚠ <b>An interface <em>port</em> and not an exposed parameter, which is the whole point.</b>
    ///     A parameter is read out of the sub-graph node's settings by
    ///     <c>TextureGraphParameters.Read</c>, and an expression written against one has folded since
    ///     <a href="https://github.com/Rikarin/Vixen/issues/742">#742</a>. A port is decided by
    ///     <c>SubGraphs.Flatten</c> out of <c>node.Values</c>, one layer below anything that can call
    ///     Raven — <a href="https://github.com/Rikarin/Vixen/issues/1058">#1058</a>. The two look
    ///     identical in a node inspector and are not the same mechanism.
    /// </remarks>
    static NodeGraphModel PublishedWithAPortForItsRadius() {
        NodeGraphModel graph = new() { Name = "Highpass" };

        graph.Interface.Add(new("Out", PortDirection.Output, PortKind.Image));
        graph.Interface.Add(new("Radius", PortDirection.Input, PortKind.Float, [8f], ""));

        // ⚠ Declared and left unwired inside, so that an expression written on it has a port to name
        // and no number to become. A refusal that could only be provoked by inventing a port name
        // would not distinguish "this port takes no number" from "this graph has no such port".
        graph.Interface.Add(new("Source", PortDirection.Input, PortKind.Image));

        var entry = graph.Add(SubGraphs.InputType);
        var noise = graph.Add("Source/Noise");
        var blur = graph.Add("Filters/Blur");
        var exit = graph.Add(SubGraphs.OutputType);

        graph.Connect(new(noise.Id, "Out"), new(blur.Id, "Input"));
        graph.Connect(new(entry.Id, "Radius"), new(blur.Id, "Radius"));
        graph.Connect(new(blur.Id, "Out"), new(exit.Id, "Out"));

        return graph;
    }

    /// <summary>The same graph with a second scalar port, for counting compilations per node.</summary>
    static NodeGraphModel PublishedWithTwoPorts() {
        var graph = PublishedWithAPortForItsRadius();

        graph.Interface.Add(new("Second", PortDirection.Input, PortKind.Float, [1f], ""));

        return graph;
    }

    /// <summary>⚠ An expression on a sub-graph node's own port folds, against the graph it is written in.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>This exact graph compiled clean and baked a blur of 8 before
    ///         <a href="https://github.com/Rikarin/Vixen/issues/1058">#1058</a>, and was refused
    ///         outright after it.</b> Neither was the answer: inlining decided an unfed interface
    ///         input's value from <c>node.Values</c> alone, so a field that accepted Raven had nowhere
    ///         to send it — <a href="https://github.com/Rikarin/Vixen/issues/1074">#1074</a> is the
    ///         seam that gives it somewhere, and the refusal is gone because the capability replaced
    ///         it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The expression reads a parameter of the <em>containing</em> graph, and that is
    ///         what makes this a test of the scope rather than of arithmetic.</b> A literal
    ///         <c>32f</c> would fold identically under a resolver handed the wrong parameter list,
    ///         under one handed none, and under a flattener that had learned to parse numbers — so
    ///         the assertion would be satisfied by three implementations that are all wrong. Half of
    ///         <c>Amount</c> can only be 32 if the value the containing graph's own knob carries
    ///         reached the fold.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_expression_on_a_sub_graph_nodes_port_folds_against_the_containing_graphs_parameters() {
        var (compiler, graph, used) = Containing(PublishedWithAPortForItsRadius());

        compiler.Parameters.Add(new("Amount", TextureGraphParameterKind.Scalar, 64f, 0f, 256f));
        used.SetText(TextureGraphExpressions.KeyOf("Radius"), "Amount * 0.5f");

        var compilation = compiler.Compile(graph);

        Assert.Empty(compilation.Diagnostics);

        var blur = compilation.Value.Ops.First(op => string.Equals(op.Kernel, "Blur", StringComparison.Ordinal));

        Assert.Equal(32f, blur.Find("radius")!.Value.Value);
    }

    /// <summary>And the containing graph's <em>override</em> of that parameter reaches it too.</summary>
    /// <remarks>
    ///     ⚠ <b>The half that separates "the declarations reached the fold" from "the values did".</b>
    ///     <see cref="An_expression_on_a_sub_graph_nodes_port_folds_against_the_containing_graphs_parameters" />
    ///     is satisfied by a resolver that folds against every parameter's <em>default</em>, which is
    ///     what <c>TextureGraphParameters.Read</c> answers when it is handed no overrides at all — so
    ///     that test alone cannot see a resolver that never read <c>Arguments</c>.
    /// </remarks>
    [Fact]
    public void A_host_override_of_that_parameter_reaches_the_fold() {
        var (compiler, graph, used) = Containing(PublishedWithAPortForItsRadius());

        compiler.Parameters.Add(new("Amount", TextureGraphParameterKind.Scalar, 64f, 0f, 256f));
        compiler.Arguments = new Dictionary<string, string>(StringComparer.Ordinal) { ["Amount"] = "20" };
        used.SetText(TextureGraphExpressions.KeyOf("Radius"), "Amount * 0.5f");

        var compilation = compiler.Compile(graph);

        Assert.Empty(compilation.Diagnostics);

        var blur = compilation.Value.Ops.First(op => string.Equals(op.Kernel, "Blur", StringComparison.Ordinal));

        Assert.Equal(10f, blur.Find("radius")!.Value.Value);
    }

    /// <summary>⚠ A wire beats the expression, silently, exactly as it beats a number on the port.</summary>
    /// <remarks>
    ///     <b>The rule this replaces <c>TG0003</c> with.</b> Every port in this system takes its wire
    ///     over anything written on it, and a diagnostic here would be a rule sub-graph nodes alone
    ///     had. It is also the cost claim one layer down: the flattener never asks a resolver about a
    ///     connected input, which is why <c>ExpressionCompilations</c> stays at zero here — a
    ///     resolver that folded first and filtered afterwards would pass every other assertion in
    ///     this file and fail this one.
    /// </remarks>
    [Fact]
    public void A_wire_into_the_port_wins_over_the_expression_written_on_it() {
        var (compiler, graph, used) = Containing(PublishedWithAPortForItsRadius());

        compiler.Parameters.Add(new("Amount", TextureGraphParameterKind.Scalar, 64f, 0f, 256f));
        used.SetText(TextureGraphExpressions.KeyOf("Radius"), "Amount * 0.5f");

        // ⚠ The same graph twice, and the first half is what stops this being a test of a compiler
        // that folded nothing at all. Unwired the expression costs one compilation; wired it costs
        // none, and the difference is the wire.
        compiler.Compile(graph);

        Assert.Equal(1, compiler.ExpressionCompilations);

        var constant = graph.Add("Source/Uniform");

        graph.Connect(new(constant.Id, "Out"), new(used.Id, "Radius"));
        compiler.Compile(graph);

        Assert.Equal(0, compiler.ExpressionCompilations);
    }

    /// <summary>One Raven compilation per sub-graph node carrying an expression, not one per port.</summary>
    /// <remarks>
    ///     ⚠ <b>Two nodes and one field each is two; one node and two fields is one.</b> The pair is
    ///     what makes this a claim about the batching rather than a count of something — a resolver
    ///     asked once per port would answer 2 to both, and one asked once per graph would answer 1 to
    ///     both. <c>Bind</c>'s own bound is measured by <c>TextureExpressionCostTests</c>; this is the
    ///     one it does not cover, because these expressions are on nodes that no longer exist by the
    ///     time <c>Collect</c> runs.
    /// </remarks>
    [Fact]
    public void Each_sub_graph_node_carrying_an_expression_costs_one_compilation() {
        var (compiler, graph, used) = Containing(PublishedWithTwoPorts());

        compiler.Parameters.Add(new("Amount", TextureGraphParameterKind.Scalar, 64f, 0f, 256f));

        used.SetText(TextureGraphExpressions.KeyOf("Radius"), "Amount * 0.5f");
        used.SetText(TextureGraphExpressions.KeyOf("Second"), "Amount * 0.25f");

        compiler.Compile(graph);

        Assert.Equal(1, compiler.ExpressionCompilations);

        var second = graph.Add("Library/Grunge");
        var output = graph.Add("Output/Output");

        graph.Connect(new(second.Id, "Out"), new(output.Id, "Input"));
        second.SetText(TextureGraphExpressions.KeyOf("Radius"), "Amount * 0.5f");

        compiler.Compile(graph);

        Assert.Equal(2, compiler.ExpressionCompilations);
    }

    /// <summary>
    ///     ⚠ A sub-graph node <em>inside</em> a compound folds against that compound's parameters,
    ///     with that instance's overrides — which is the join nothing but this walk can make.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The reason <a href="https://github.com/Rikarin/Vixen/issues/1074">#1074</a> is a
    ///         seam and not a branch.</b> <c>Library/Outer</c> declares <c>Strength</c>, contains a
    ///         <c>Library/Inner</c> node, and writes <c>Strength * 4f</c> on that node's
    ///         <c>Radius</c>. The author's own graph declares no parameters at all, so a resolver
    ///         that folded against the graph being compiled would report an undefined name; one that
    ///         folded against the published graph's declared <em>defaults</em> would answer 16; only
    ///         one handed <c>Library/Outer</c>'s parameter list <em>and</em> the settings of the node
    ///         standing for this instance of it answers 40.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Three wrong implementations are each excluded by a different number here</b>, and
    ///         that is deliberate: the version of this test that set <c>Strength</c> to its default
    ///         would be satisfied by a resolver that never read a setting, which is exactly the
    ///         defect <a href="https://github.com/Rikarin/Vixen/issues/742">#742</a> was.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_sub_graph_node_inside_a_compound_folds_against_that_compounds_scope() {
        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        TextureGraphLibrary library = new();

        library.Publish("Library/Inner", PublishedWithAPortForItsRadius(), [], registry);

        NodeGraphModel outer = new() { Name = "Outer" };

        outer.Interface.Add(new("Out", PortDirection.Output, PortKind.Image));

        var inner = outer.Add("Library/Inner");
        var exit = outer.Add(SubGraphs.OutputType);

        outer.Connect(new(inner.Id, "Out"), new(exit.Id, "Out"));
        inner.SetText(TextureGraphExpressions.KeyOf("Radius"), "Strength * 4f");

        library.Publish(
            "Library/Outer",
            outer,
            [new("Strength", TextureGraphParameterKind.Scalar, 4f, 0f, 64f)],
            registry
        );

        NodeGraphModel graph = new();
        var used = graph.Add("Library/Outer");
        var output = graph.Add("Output/Output");

        graph.Connect(new(used.Id, "Out"), new(output.Id, "Input"));
        used.SetText("Strength", "10");

        TextureGraphCompiler compiler = new(registry) {
            BaseWidth = 128,
            BaseHeight = 128,
            Seed = 9,
            SubGraphSource = library
        };

        var compilation = compiler.Compile(graph);

        Assert.Empty(compilation.Diagnostics);

        var blur = compilation.Value.Ops.First(op => string.Equals(op.Kernel, "Blur", StringComparison.Ordinal));

        Assert.Equal(40f, blur.Find("radius")!.Value.Value);
    }

    /// <summary>An expression naming a port the published graph has not got is still refused.</summary>
    /// <remarks>
    ///     ⚠ <b>What survives of <c>TG0003</c>, reported as <c>TG0016</c> because it is the same
    ///     complaint <c>Collect</c> makes about an atomic node's port.</b> A field whose value nothing
    ///     can read should not compile, and two ids for one meaning is what
    ///     <c>TextureDiagnosticIdTests</c> exists to prevent.
    /// </remarks>
    [Fact]
    public void An_expression_naming_a_port_the_published_graph_has_not_got_is_refused() {
        var (compiler, graph, used) = Containing(PublishedWithAPortForItsRadius());

        used.SetText(TextureGraphExpressions.KeyOf("Diameter"), "32f");

        var compilation = compiler.Compile(graph);

        var refusal = Assert.Single(
            compilation.Diagnostics,
            diagnostic => string.Equals(diagnostic.Id, "TG0016", StringComparison.Ordinal)
        );

        Assert.Equal(used.Id, refusal.Node);
        Assert.Equal("Diameter", refusal.Port);
        Assert.Equal(NodeSeverity.Error, refusal.Severity);
        Assert.False(compilation.Succeeded);
    }

    /// <summary>And one on a port that carries an image, which no number could stand for.</summary>
    [Fact]
    public void An_expression_on_a_published_graphs_image_port_is_refused() {
        var (compiler, graph, used) = Containing(PublishedWithAPortForItsRadius());

        used.SetText(TextureGraphExpressions.KeyOf("Source"), "32f");

        var compilation = compiler.Compile(graph);

        var refusal = Assert.Single(
            compilation.Diagnostics,
            diagnostic => string.Equals(diagnostic.Id, "TG0016", StringComparison.Ordinal)
        );

        Assert.Equal("Source", refusal.Port);
        Assert.Contains("Image", refusal.Message, StringComparison.Ordinal);
        Assert.False(compilation.Succeeded);
    }

    /// <summary>
    ///     The same graph, with a number on the port, bakes it — so the refusal is about the
    ///     expression and not about the port.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>32 and not 8, which is what says the port itself works.</b> The sub-graph node's
    ///     <c>Values</c> reach the inlined <c>Blur</c> through <c>SubGraphs.Flatten</c>'s
    ///     interface-input constant; it is only the <em>expression</em> key beside them that nothing
    ///     reads. Asserting the declared default here instead would pass over a flattener that had
    ///     stopped carrying the port at all.
    /// </remarks>
    [Fact]
    public void The_same_graph_bakes_when_the_port_carries_a_number() {
        var (compiler, graph, used) = Containing(PublishedWithAPortForItsRadius());

        used.SetValue("Radius", 32f);

        var compilation = compiler.Compile(graph);

        Assert.Empty(compilation.Diagnostics);

        var blur = compilation.Value.Ops.First(op => string.Equals(op.Kernel, "Blur", StringComparison.Ordinal));

        Assert.Equal(32f, blur.Find("radius")!.Value.Value);
    }

    /// <summary>An empty expression field is not one, so clearing the box is not a refusal.</summary>
    /// <remarks>
    ///     ⚠ <b>The failure mode a refusal invites, and <c>Collect</c> already had to answer it.</b>
    ///     A panel that wrote the empty string back on every edit would turn every cleared field into
    ///     a complaint asking the author to do the thing they have just done — which is a refusal
    ///     nobody can act on, and the reason this is an assertion rather than a remark.
    /// </remarks>
    [Fact]
    public void An_empty_expression_field_on_a_sub_graph_node_is_not_refused() {
        var (compiler, graph, used) = Containing(PublishedWithAPortForItsRadius());

        used.SetText(TextureGraphExpressions.KeyOf("Radius"), "   ");

        Assert.Empty(compiler.Compile(graph).Diagnostics);
    }
}
