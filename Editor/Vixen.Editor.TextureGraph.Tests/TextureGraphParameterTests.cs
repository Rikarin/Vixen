// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.NodeGraph;
using Vixen.Editor.TextureGraph;
using Xunit;

namespace Tests;

/// <summary>
///     Doc 48 § D9's exposed parameters, and § D6's Raven expressions over them — with no device and
///     no GPU anywhere near either.
/// </summary>
/// <remarks>
///     ⚠ <b>Ask what this file prints on the day Raven's folder stops folding.</b> Every value below
///     comes back through <c>FieldSymbol.ConstantValue</c>, and a folder that answered null for
///     everything would make <see cref="An_expression_is_folded_by_the_real_raven_compiler" /> fail
///     rather than pass over an unfolded expression:
///     <c>TextureGraphExpressions.Fold</c> reports <c>TG0014</c> and leaves the port's own number in
///     place, so the assertion is on the folded number and on the empty diagnostic list, and neither
///     survives.
/// </remarks>
public class TextureGraphParameterTests {
    static NodeTypeRegistry Registry() {
        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        return registry;
    }

    static TextureGraphCompiler Compiler() => new(Registry()) { BaseWidth = 256, BaseHeight = 256, Seed = 7 };

    /// <summary>A graph whose blur radius is an expression over one parameter.</summary>
    static (TextureGraphCompiler Compiler, NodeGraphModel Graph) Blurred(string expression, float amount = 0.5f) {
        NodeGraphModel graph = new();
        var noise = graph.Add("Source/Noise");
        var blur = graph.Add("Filters/Blur");
        var output = graph.Add("Output/Output");

        graph.Connect(new(noise.Id, "Out"), new(blur.Id, "Input"));
        graph.Connect(new(blur.Id, "Out"), new(output.Id, "Input"));
        blur.SetValue("Radius", 3f);
        blur.SetText(TextureGraphExpressions.KeyOf("Radius"), expression);

        var compiler = Compiler();

        compiler.Parameters.Add(new("amount", Default: amount, Minimum: 0f, Maximum: 4f, Group: "Wear"));

        return (compiler, graph);
    }

    static float RadiusOf(TexturePlan plan) {
        var blur = plan.Ops.First(op => string.Equals(op.Kernel, "Blur", StringComparison.Ordinal));

        return blur.Find("radius")!.Value.Value;
    }

    /// <summary>The knobs a published graph has become the settings of the node it is used as.</summary>
    [Fact]
    public void A_published_graph_is_a_node_whose_settings_are_its_parameters() {
        NodeGraphModel published = new() { Name = "Rust" };

        published.Interface.Add(new("Mask", PortDirection.Input, PortKind.Image));
        published.Interface.Add(new("Out", PortDirection.Output, PortKind.Image));

        List<TextureGraphParameter> parameters = [
            new("amount", Default: 0.5f, Minimum: 0f, Maximum: 1f, Group: "Wear", Summary: "How much rust."),
            new("octaves", TextureGraphParameterKind.Integer, 3f, 1f, 8f)
        ];

        var definition = TextureGraphParameters.Definition(published, parameters, "Library/Rust");

        // The interface is still the ports — doc 48 § D9's "its parameters are its ports and
        // settings" is both halves, and the settings half is the one this batch adds.
        Assert.Equal(["Mask", "Out"], definition.Ports.Select(port => port.Name).ToArray());
        Assert.Equal(["amount", "octaves"], definition.Settings.Select(setting => setting.Name).ToArray());

        // The default reaches a saved graph as text, in the parameter's own spelling: an integer
        // parameter is `3` rather than `3` formatted as a float.
        Assert.Equal("0.5", definition.Settings[0].Default);
        Assert.Equal("3", definition.Settings[1].Default);

        // ⚠ The group and the range ride in the summary because SettingDefinition has nowhere else
        // to put them — #730. When it does, this assertion is what changes.
        Assert.Equal("How much rust. · Wear · 0…1", definition.Settings[0].Summary);
    }

    /// <summary>A parameter list that disagrees with itself is refused, one message per fault.</summary>
    [Theory]
    [InlineData("has a", "not a name")]
    [InlineData("amount-2", "not a name")]
    [InlineData("", "not a name")]
    public void A_parameter_name_an_expression_could_not_spell_is_refused(string name, string expected) {
        var problem = Assert.Single(TextureGraphParameters.Check([new(name)]));

        Assert.Contains(expected, problem, StringComparison.Ordinal);
    }

    /// <summary>Two knobs under one name would be one knob.</summary>
    [Fact]
    public void Two_parameters_of_one_name_are_refused() {
        var problem = Assert.Single(TextureGraphParameters.Check([new("amount"), new("amount")]));

        Assert.Contains("Two parameters are called 'amount'", problem, StringComparison.Ordinal);
    }

    /// <summary>A default outside its own range is said rather than clamped.</summary>
    [Fact]
    public void A_default_outside_its_own_range_is_refused() {
        var problem = Assert.Single(TextureGraphParameters.Check([new("amount", Default: 4f, Maximum: 1f)]));

        Assert.Contains("outside its own range", problem, StringComparison.Ordinal);
    }

    /// <summary>An override that does not parse keeps the default and says so.</summary>
    [Fact]
    public void An_override_that_does_not_parse_keeps_the_default() {
        List<TextureGraphParameter> parameters = [new("amount", Default: 0.25f, Minimum: 0f, Maximum: 1f)];

        var values = TextureGraphParameters.Read(
            parameters,
            new Dictionary<string, string> { ["amount"] = "quite a lot" },
            out var problems
        );

        // ⚠ The default and not zero. A parameter parsed to zero is a plausible number for every
        // knob a texture graph has, which is the whole reason this is a refusal.
        Assert.Equal(0.25f, values["amount"]);
        Assert.Contains("keeps its default", Assert.Single(problems), StringComparison.Ordinal);
    }

    /// <summary>An override outside the declared range is refused by the range.</summary>
    [Fact]
    public void An_override_outside_the_range_keeps_the_default() {
        List<TextureGraphParameter> parameters = [new("amount", Default: 0.25f, Minimum: 0f, Maximum: 1f)];

        var values = TextureGraphParameters.Read(
            parameters,
            new Dictionary<string, string> { ["amount"] = "40" },
            out var problems
        );

        Assert.Equal(0.25f, values["amount"]);
        Assert.Contains("outside its range", Assert.Single(problems), StringComparison.Ordinal);
    }

    /// <summary>
    ///     Doc 48 § D6: a scalar port's value is a Raven expression over the parameters, and the real
    ///     Raven compiler is what folds it.
    /// </summary>
    [Fact]
    public void An_expression_is_folded_by_the_real_raven_compiler() {
        var (compiler, graph) = Blurred("amount * 8f + 1f", 0.5f);
        var compilation = compiler.Compile(graph);

        Assert.Empty(compilation.Diagnostics);
        Assert.Equal(5f, RadiusOf(compilation.Value));

        // And the port's own number is what an expression replaced: 3, which is what the graph would
        // have compiled to without one.
        Assert.Equal(0.5f, compiler.ParameterValues["amount"]);
    }

    /// <summary>The same graph, with the parameter overridden, is a different plan.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the assertion that cannot be satisfied by an expression nothing reads.</b>
    ///     <see cref="An_expression_is_folded_by_the_real_raven_compiler" /> alone would pass if the
    ///     radius happened to be the number typed on the port; two compilations of one graph that
    ///     differ only in the override cannot.
    /// </remarks>
    [Fact]
    public void An_override_changes_what_the_expression_folds_to() {
        var (first, graph) = Blurred("amount * 8f + 1f", 0.5f);
        var (second, _) = Blurred("amount * 8f + 1f", 0.5f);

        second.Arguments = new Dictionary<string, string> { ["amount"] = "0.25" };

        Assert.Equal(5f, RadiusOf(first.Compile(graph).Value));
        Assert.Equal(3f, RadiusOf(second.Compile(graph).Value));
    }

    /// <summary>An expression that will not compile names the node and the port, not a line.</summary>
    [Fact]
    public void An_expression_that_does_not_compile_names_its_node_and_port() {
        var (compiler, graph) = Blurred("amount * mystery");
        var compilation = compiler.Compile(graph);

        var diagnostic = Assert.Single(compilation.Diagnostics, one => one.Id == "TG0013");

        Assert.Equal("Radius", diagnostic.Port);
        Assert.NotEqual(NodeId.None, diagnostic.Node);
        Assert.False(diagnostic.Span.IsNone);
    }

    /// <summary>A complaint's node is the node whose expression is on that line, not the first one.</summary>
    /// <remarks>
    ///     ⚠ <b>Two expressions, one good and one bad, because one alone proves nothing about the
    ///     mapping.</b> A mapping that always answered "the first expression" would pass a test with
    ///     one expression in it, and this repository has shipped that test before.
    /// </remarks>
    [Fact]
    public void A_complaint_is_addressed_to_the_expression_that_caused_it() {
        NodeGraphModel graph = new();
        var noise = graph.Add("Source/Noise");
        var good = graph.Add("Filters/Blur");
        var bad = graph.Add("Colour/Levels");
        var output = graph.Add("Output/Output");

        graph.Connect(new(noise.Id, "Out"), new(good.Id, "Input"));
        graph.Connect(new(good.Id, "Out"), new(bad.Id, "Input"));
        graph.Connect(new(bad.Id, "Out"), new(output.Id, "Input"));

        good.SetText(TextureGraphExpressions.KeyOf("Radius"), "amount * 2f");
        bad.SetText(TextureGraphExpressions.KeyOf("Gamma"), "amount * nothing");

        var compiler = Compiler();

        compiler.Parameters.Add(new("amount", Default: 1f));

        var diagnostic = Assert.Single(compiler.Compile(graph).Diagnostics, one => one.Id == "TG0013");

        Assert.Equal(bad.Id, diagnostic.Node);
        Assert.Equal("Gamma", diagnostic.Port);
    }

    /// <summary>An expression Raven binds and cannot fold is refused rather than taken as zero.</summary>
    [Fact]
    public void A_call_is_not_folded_and_is_refused_by_name() {
        var (compiler, graph) = Blurred("sin(amount)");
        var compilation = compiler.Compile(graph);

        var diagnostic = Assert.Single(compilation.Diagnostics, one => one.Id is "TG0013" or "TG0014");

        Assert.Equal("Radius", diagnostic.Port);
    }

    /// <summary>A newline in an expression is refused where it is typed.</summary>
    /// <remarks>
    ///     ⚠ <b>The trap doc 48 and <c>CLAUDE.md</c> both name: a newline ends a statement in
    ///     Raven.</b> Emitted as written, the second line would be a statement of its own in the
    ///     middle of a declaration list — and every expression after it would be attributed to the
    ///     wrong node, because the line numbers would have shifted by one.
    /// </remarks>
    [Fact]
    public void An_expression_over_two_lines_is_refused() {
        var (compiler, graph) = Blurred("amount\n * 2f");
        var compilation = compiler.Compile(graph);

        var diagnostic = Assert.Single(compilation.Diagnostics, one => one.Id == "TG0012");

        Assert.Contains("newline ends a statement", diagnostic.Message, StringComparison.Ordinal);
    }

    /// <summary>An expression on a port the node has not got names the port.</summary>
    [Fact]
    public void An_expression_for_a_port_that_is_gone_is_refused() {
        var (compiler, graph) = Blurred("amount");
        var blur = graph.Nodes.First(node => string.Equals(node.Type, "Filters/Blur", StringComparison.Ordinal));

        blur.SetText(TextureGraphExpressions.KeyOf("Sharpness"), "amount");

        var diagnostic = Assert.Single(compiler.Compile(graph).Diagnostics, one => one.Id == "TG0016");

        Assert.Equal("Sharpness", diagnostic.Port);
    }

    /// <summary>An expression on an image port is refused: an image is wired, not computed.</summary>
    [Fact]
    public void An_expression_on_an_image_port_is_refused() {
        var (compiler, graph) = Blurred("amount");
        var blur = graph.Nodes.First(node => string.Equals(node.Type, "Filters/Blur", StringComparison.Ordinal));

        blur.SetText(TextureGraphExpressions.KeyOf("Input"), "amount");

        var diagnostic = Assert.Single(compiler.Compile(graph).Diagnostics, one => one.Id == "TG0016");

        Assert.Equal("Input", diagnostic.Port);
        Assert.Contains("wired, not computed", diagnostic.Message, StringComparison.Ordinal);
    }

    /// <summary>A parameter list that does not hold together is reported against the graph.</summary>
    [Fact]
    public void A_broken_parameter_list_is_reported_against_no_node() {
        var (compiler, graph) = Blurred("amount");

        compiler.Parameters.Add(new("amount", Default: 2f));

        var diagnostic = Assert.Single(compiler.Compile(graph).Diagnostics, one => one.Id == "TG0011");

        Assert.Equal(NodeId.None, diagnostic.Node);
    }

    /// <summary>A cleared field is not an expression: the port keeps the number typed on it.</summary>
    /// <remarks>
    ///     ⚠ <b>The state a field is in for the whole time an author is deleting one.</b> An editor
    ///     that wrote the empty string back would otherwise turn every cleared box into a diagnostic
    ///     asking for exactly what had just been done.
    /// </remarks>
    [Fact]
    public void A_cleared_expression_leaves_the_ports_own_number() {
        var (compiler, graph) = Blurred("   ");
        var compilation = compiler.Compile(graph);

        Assert.Empty(compilation.Diagnostics);
        Assert.Equal(3f, RadiusOf(compilation.Value));
    }

    /// <summary>A graph with no expressions asks Raven nothing and compiles as it always did.</summary>
    [Fact]
    public void A_graph_with_no_expressions_is_unchanged() {
        NodeGraphModel graph = new();
        var noise = graph.Add("Source/Noise");
        var blur = graph.Add("Filters/Blur");
        var output = graph.Add("Output/Output");

        graph.Connect(new(noise.Id, "Out"), new(blur.Id, "Input"));
        graph.Connect(new(blur.Id, "Out"), new(output.Id, "Input"));
        blur.SetValue("Radius", 3f);

        var compilation = Compiler().Compile(graph);

        Assert.Empty(compilation.Diagnostics);
        Assert.Equal(3f, RadiusOf(compilation.Value));
    }
}
