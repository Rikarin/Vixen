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
///         ⚠ <b>And every graph here is compiled for real, so a compiler that stopped folding would
///         fail rather than pass.</b> Each test asserts the plan came out and the diagnostics are
///         empty beside the count: a count of one over a compilation that produced nothing would be
///         a number about a compiler that had given up.
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
        Assert.Equal(1, compiler.ExpressionCompilations);
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
        Assert.Equal(0, compiler.ExpressionCompilations);
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

        compiler.Compile(expressive);

        Assert.Equal(1, compiler.ExpressionCompilations);

        NodeGraphModel bare = new();
        var flat = bare.Add("Source/Noise");
        var out2 = bare.Add("Output/Output");

        bare.Connect(new(flat.Id, "Out"), new(out2.Id, "Input"));
        compiler.Compile(bare);

        Assert.Equal(0, compiler.ExpressionCompilations);
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
