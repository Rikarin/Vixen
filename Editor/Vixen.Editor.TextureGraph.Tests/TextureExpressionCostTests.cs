// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.NodeGraph;
using Vixen.Editor.TextureGraph;
using Xunit;

namespace Tests;

/// <summary>
///     What an expression costs, counted — <a href="https://github.com/Rikarin/Vixen/issues/940">#940</a>.
/// </summary>
/// <remarks>
///     <para>
///         <b><c>TextureGraphCompiler.Bind</c> states a bound, <c>TextureGraphExpressions</c>
///         restates it, and until now nothing counted the thing being costed.</b> The sentence used
///         to say "Raven is asked once per graph"; re-keying <c>Collect</c> on the expansion made
///         that false and it stood for a whole batch (#931). ⚠ <b>The correction was asserted on
///         exactly the evidence the wrong one had: none.</b> A bound nothing measures drifts the next
///         time the grouping key changes, which is precisely what happened.
///     </para>
///     <para>
///         ⚠ <b>The third test is the one that makes the other two mean anything.</b> "Forty fields
///         cost one" and "ten instances cost ten" are both satisfied by a counter stuck at the number
///         of expansions, or by a constant that happens to fit — a graph with no expressions
///         compiling <em>zero</em> sources is what separates a count of work from a description of
///         the graph's shape.
///     </para>
///     <para>
///         ⚠ <b>And every graph here is compiled for real, so a compiler that stopped folding fails
///         rather than passes — which was false for the batch this suite was written in</b>
///         (<a href="https://github.com/Rikarin/Vixen/issues/974">#974</a>). The count is incremented
///         <em>before</em> <c>Fold</c> runs, so it says nothing whatever about the fold's result:
///         discarding every folded value left all four tests green while the counter went on
///         counting the compilations it had made. A count of one over a compilation that produced
///         nothing is a number about a compiler that had given up.
///     </para>
///     <para>
///         <b>So each test reads the radius back off the plan beside the count.</b>
///         <c>Filters/Blur</c>'s own <c>Radius</c> is 8, the node value inside the compound is 2 and
///         the parameter every expression here names is 0.5 — three numbers that are never each
///         other, so a fold whose result never reached <c>TextureEmitter.Number</c> emits the wrong
///         one. That is an assertion about what the fold <em>produced</em> rather than that it was
///         attempted, and the diagnostics the older remark claimed were checked are now checked.
///     </para>
/// </remarks>
public class TextureExpressionCostTests {
    /// <summary>How many expression fields the crowded fixtures carry, all told.</summary>
    const int Fields = 40;

    /// <summary>Forty expression fields in one scope are one compilation, not forty.</summary>
    /// <remarks>
    ///     ⚠ <b>One field on each of forty nodes rather than forty on one, because
    ///     <c>Filters/Blur</c> has exactly one scalar port.</b> What is being counted is scopes, and
    ///     every one of these nodes is in the author's own graph — expansion 0 — so the shape of the
    ///     claim is unchanged.
    /// </remarks>
    [Fact]
    public void Forty_expression_fields_in_one_scope_are_one_compilation() {
        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        NodeGraphModel graph = new();
        var output = graph.Add("Output/Output");
        var last = graph.Add("Source/Noise");

        for (var field = 0; field < Fields; field++) {
            var blur = graph.Add("Filters/Blur");

            graph.Connect(new(last.Id, "Out"), new(blur.Id, "Input"));
            blur.SetText(TextureGraphExpressions.KeyOf("Radius"), "amount");
            last = blur;
        }

        graph.Connect(new(last.Id, "Out"), new(output.Id, "Input"));

        var compiler = Compiler(registry);
        var compilation = compiler.Compile(graph);

        Assert.NotNull(compilation.Artefact);
        Assert.Empty(compilation.Diagnostics);
        Assert.Equal(1, compiler.ExpressionCompilations);

        // ⚠ The fold's *effect* and not its occurrence — #974. Every one of these blurs asked for
        // `amount`, which is 0.5; the node's own `Radius` is 8, so a compiler that folded and threw
        // the answer away writes 8 here and this is what tells the two apart. Two ops per blur,
        // because the box is separable.
        var radii = Radii(compilation.Artefact);

        Assert.Equal(Fields * 2, radii.Count);
        Assert.All(radii, radius => Assert.Equal(0.5f, radius));
    }

    /// <summary>The same fields spread over ten compound instances are ten, and not forty.</summary>
    /// <remarks>
    ///     <b>The half re-keying <c>Collect</c> on the expansion bought, and the half it cost.</b>
    ///     Two instances of one compound are two sets of numbers and have to be folded apart; the
    ///     price is one Raven compilation each. What the bound promises is that the price is per
    ///     <em>instance</em> and not per field, which is the question somebody putting an expression
    ///     on every knob of a compound is actually asking.
    /// </remarks>
    [Fact]
    public void Ten_instances_of_a_compound_are_ten_compilations_and_not_forty() {
        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        TextureGraphLibrary library = new();

        library.Publish("Library/Grunge", Published(expressions: 4), [], registry);

        NodeGraphModel graph = new();
        var output = graph.Add("Output/Output");
        var last = graph.Add("Source/Noise");

        // ⚠ Chained rather than left loose, because an unreachable node is not in `Ordered()` and its
        // expressions would never be collected — a fixture that counted nothing while asserting ten.
        for (var instance = 0; instance < 10; instance++) {
            var used = graph.Add("Library/Grunge");

            graph.Connect(new(last.Id, "Out"), new(used.Id, "In"));
            last = used;
        }

        graph.Connect(new(last.Id, "Out"), new(output.Id, "Input"));

        var compiler = Compiler(registry, library);
        var compilation = compiler.Compile(graph);

        Assert.NotNull(compilation.Artefact);

        // ⚠ Ten and not forty: four expression fields inside each instance are one source between
        // them. And ten rather than one, because the author's own graph holds no expression here —
        // "plus one for the author's own graph" is a term that is only there when there is one.
        Assert.Equal(10, compiler.ExpressionCompilations);

        // ⚠ And the fold reached the plan — #974. Inside the compound each blur carries an authored
        // `Radius` of 2 *and* an expression folding to 0.5, so an expression the compiler folded and
        // discarded is not the absence of a number here: it is the number 2, forty times over.
        var radii = Radii(compilation.Artefact);

        Assert.Equal(10 * 4 * 2, radii.Count);
        Assert.All(radii, radius => Assert.Equal(0.5f, radius));
    }

    /// <summary>⚠ And a graph with no expressions compiles none, which a constant cannot do.</summary>
    /// <remarks>
    ///     <b>The half that separates a counter from a number that happens to be right.</b> The graph
    ///     is the same shape as the one above with the expression fields left off, so anything that
    ///     answered "one per expansion" or "one per graph" is red here and green in both tests above.
    /// </remarks>
    [Fact]
    public void A_graph_with_no_expressions_compiles_none() {
        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        TextureGraphLibrary library = new();

        library.Publish("Library/Grunge", Published(expressions: 0), [], registry);

        NodeGraphModel graph = new();
        var noise = graph.Add("Source/Noise");
        var used = graph.Add("Library/Grunge");
        var output = graph.Add("Output/Output");

        graph.Connect(new(noise.Id, "Out"), new(used.Id, "In"));
        graph.Connect(new(used.Id, "Out"), new(output.Id, "Input"));

        var compiler = Compiler(registry, library);
        var compilation = compiler.Compile(graph);

        Assert.NotNull(compilation.Artefact);
        Assert.Empty(compilation.Diagnostics);
        Assert.Equal(0, compiler.ExpressionCompilations);

        // The other half of the same claim: with nothing to fold, the authored 2 is what arrives.
        // A "folder" that wrote 0.5 over every radius it saw would pass both tests above and fail
        // here, which is why this one reads the number too.
        Assert.All(Radii(compilation.Artefact), radius => Assert.Equal(2f, radius));
    }

    /// <summary>⚠ And the count is the last compilation's, not the compiler's life.</summary>
    /// <remarks>
    ///     A compiler is reused across a panel's edits, so a running total would answer a question
    ///     nobody asked — and would make every assertion above depend on what the test did before it.
    /// </remarks>
    [Fact]
    public void The_count_is_reset_by_the_next_compilation() {
        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        NodeGraphModel expressive = new();
        var noise = expressive.Add("Source/Noise");
        var blur = expressive.Add("Filters/Blur");
        var out1 = expressive.Add("Output/Output");

        expressive.Connect(new(noise.Id, "Out"), new(blur.Id, "Input"));
        expressive.Connect(new(blur.Id, "Out"), new(out1.Id, "Input"));
        blur.SetText(TextureGraphExpressions.KeyOf("Radius"), "amount");

        var compiler = Compiler(registry);

        var first = compiler.Compile(expressive);

        Assert.NotNull(first.Artefact);
        Assert.Equal(1, compiler.ExpressionCompilations);
        Assert.All(Radii(first.Artefact), radius => Assert.Equal(0.5f, radius));

        NodeGraphModel bare = new();
        var flat = bare.Add("Source/Noise");
        var out2 = bare.Add("Output/Output");

        bare.Connect(new(flat.Id, "Out"), new(out2.Id, "Input"));
        compiler.Compile(bare);

        Assert.Equal(0, compiler.ExpressionCompilations);
    }

    /// <summary>The radius every <c>Blur</c> op in a plan carries, in emission order.</summary>
    /// <param name="plan">The compiled plan.</param>
    /// <returns>One number per dispatch, so two per node.</returns>
    /// <remarks>
    ///     ⚠ <b>Read off the op rather than through <c>TexturePlan.Resolve</c></b>, which scales a
    ///     <c>TexelsAtBase</c> parameter to the resolution of the image the op writes. Every fixture
    ///     here is at the base resolution and the scale is 1, so the two agree today — and a suite
    ///     about what an expression folded to should not go red the day somebody adds a rescale.
    /// </remarks>
    static List<float> Radii(TexturePlan plan) {
        List<float> radii = [];

        foreach (var op in plan.Ops) {
            if (op.Kernel == "Blur" && op.Find("radius") is { } radius) {
                radii.Add(radius.Value);
            }
        }

        return radii;
    }

    /// <summary>A compiler with one parameter every expression here is written over.</summary>
    static TextureGraphCompiler Compiler(NodeTypeRegistry registry, ISubGraphSource? subGraphs = null) {
        TextureGraphCompiler compiler = new(registry) {
            BaseWidth = 128,
            BaseHeight = 128,
            Seed = 9,
            SubGraphSource = subGraphs
        };

        compiler.Parameters.Add(new("amount", Default: 0.5f, Minimum: 0f, Maximum: 4f, Group: "Wear"));

        return compiler;
    }

    /// <summary>A published graph with a given number of expression fields inside it.</summary>
    /// <param name="expressions">How many of its blur's ports are written as expressions.</param>
    /// <returns>The graph.</returns>
    static NodeGraphModel Published(int expressions) {
        NodeGraphModel graph = new() { Name = "Grunge" };

        graph.Interface.Add(new("In", PortDirection.Input, PortKind.Image));
        graph.Interface.Add(new("Out", PortDirection.Output, PortKind.Image));

        var entry = graph.Add(SubGraphs.InputType);
        var exit = graph.Add(SubGraphs.OutputType);
        var last = entry;
        var port = "In";

        // One blur per expression field, for the same reason as above: the node has one scalar. A
        // compound holding four of them is four fields in one scope, which is what "the price is per
        // instance and not per field" is a claim about.
        for (var field = 0; field < Math.Max(expressions, 1); field++) {
            var blur = graph.Add("Filters/Blur");

            graph.Connect(new(last.Id, port), new(blur.Id, "Input"));
            blur.SetValue("Radius", 2f);

            if (field < expressions) {
                blur.SetText(TextureGraphExpressions.KeyOf("Radius"), "0.5f");
            }

            last = blur;
            port = "Out";
        }

        graph.Connect(new(last.Id, port), new(exit.Id, "Out"));

        return graph;
    }
}
