// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.NodeGraph;
using Vixen.TextureGraph;
using Xunit;

namespace Tests;

/// <summary>
///     A graph's colour parameter: four lanes named once and substituted into every vector port whose expression
///     is its name alone, on an ordinary node and on a sub-graph node's own port.
/// </summary>
/// <remarks>
///     ⚠ <b>Every assertion is on all four lanes of the op</b>, because the failure this replaces was a colour that
///     reached lane 0 only and left the rest as typed — a red that looked like a tint.
/// </remarks>
public class TextureGraphColorParameterTests {
    const string Rust = "0.8,0.4,0.1,0.5";

    static TextureGraphParameter Tint(string color = Rust) =>
        new("tint", TextureGraphParameterKind.Color, Choice: color);

    static (TextureGraphCompiler Compiler, NodeGraphModel Graph, GraphNode Uniform) Tinted(string expression) {
        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        NodeGraphModel graph = new();
        var uniform = graph.Add("Source/Uniform");
        var output = graph.Add("Output/Output");

        graph.Connect(new(uniform.Id, "Out"), new(output.Id, "Input"));
        uniform.SetText(TextureGraphExpressions.KeyOf("Colour"), expression);

        TextureGraphCompiler compiler = new(registry) { BaseWidth = 64, BaseHeight = 64, Seed = 3 };

        compiler.Parameters.Add(Tint());

        return (compiler, graph, uniform);
    }

    static float[] LanesOf(TexturePlan plan) {
        var uniform = plan.Ops.First(static op => string.Equals(op.Kernel, "Uniform", StringComparison.Ordinal));

        return new[] { "red", "green", "blue", "alpha" }.Select(lane => uniform.Find(lane)!.Value.Value).ToArray();
    }

    [Theory]
    [InlineData("0.8,0.4,0.1,0.5", new[] { 0.8f, 0.4f, 0.1f, 0.5f })]
    [InlineData(" 2 , 0 , 0 ", new[] { 2f, 0f, 0f, 1f })]
    public void A_color_reads_three_or_four_numbers(string text, float[] expected) {
        Assert.True(SettingColors.TryRead(text, out var lanes));
        Assert.Equal(expected, lanes.ToArray());
        Assert.True(SettingColors.TryRead(SettingColors.Text(lanes.AsSpan()), out var again));
        Assert.Equal(expected, again.ToArray());
    }

    [Theory]
    [InlineData("")]
    [InlineData("1,1")]
    [InlineData("1,1,1,1,1")]
    [InlineData("red,0,0")]
    [InlineData("NaN,0,0")]
    public void Anything_else_is_not_a_color(string text) => Assert.False(SettingColors.TryRead(text, out _));

    [Fact]
    public void A_color_parameter_fills_every_lane_of_a_color_port() {
        var (compiler, graph, _) = Tinted("tint");

        var compilation = compiler.Compile(graph);

        Assert.Empty(compilation.Diagnostics);
        Assert.Equal([0.8f, 0.4f, 0.1f, 0.5f], LanesOf(compilation.Value));
        // Substituted, so nothing went to Raven.
        Assert.Equal(0, compiler.ExpressionCompilations);
    }

    [Fact]
    public void A_host_override_of_the_color_reaches_the_port_and_a_refresh_follows_it() {
        var (compiler, graph, _) = Tinted("tint");

        compiler.Arguments = new Dictionary<string, string>(StringComparer.Ordinal) { ["tint"] = "0,1,0" };

        var compilation = compiler.Compile(graph);

        Assert.Equal([0f, 1f, 0f, 1f], LanesOf(compilation.Value));

        compiler.Arguments = new Dictionary<string, string>(StringComparer.Ordinal) { ["tint"] = "0,0,1,1" };

        var refreshed = compiler.Refresh(graph);

        Assert.NotNull(refreshed);
        Assert.Equal(1, compiler.Refreshes);
        Assert.Equal([0f, 0f, 1f, 1f], LanesOf(refreshed));
    }

    [Fact]
    public void An_override_that_is_not_a_color_keeps_the_default_and_says_so() {
        var (compiler, graph, _) = Tinted("tint");

        compiler.Arguments = new Dictionary<string, string>(StringComparer.Ordinal) { ["tint"] = "orange" };

        var compilation = compiler.Compile(graph);

        Assert.Contains(compilation.Diagnostics, static diagnostic => diagnostic.Message.Contains("not a colour"));
        Assert.Equal([0.8f, 0.4f, 0.1f, 0.5f], LanesOf(compilation.Value));
    }

    [Fact]
    public void A_color_on_a_port_that_takes_one_number_is_refused() {
        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        NodeGraphModel graph = new();
        var noise = graph.Add("Source/Noise");
        var blur = graph.Add("Filters/Blur");
        var output = graph.Add("Output/Output");

        graph.Connect(new(noise.Id, "Out"), new(blur.Id, "Input"));
        graph.Connect(new(blur.Id, "Out"), new(output.Id, "Input"));
        blur.SetText(TextureGraphExpressions.KeyOf("Radius"), "tint");

        TextureGraphCompiler compiler = new(registry) { BaseWidth = 64, BaseHeight = 64, Seed = 3 };

        compiler.Parameters.Add(Tint());

        var refusal = Assert.Single(compiler.Compile(graph).Diagnostics);

        Assert.Equal("TG0012", refusal.Id);
        Assert.Equal(blur.Id, refusal.Node);
        Assert.Equal("Radius", refusal.Port);
    }

    [Fact]
    public void A_color_default_that_does_not_read_is_refused() {
        var problem = Assert.Single(TextureGraphParameters.Check([Tint("brown")]));

        Assert.Contains("not a colour", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_color_parameter_crosses_a_published_graph_as_a_color_setting() {
        var setting = Assert.Single(TextureGraphParameters.Settings([Tint()]));

        Assert.Equal(SettingKind.Color, setting.Kind);
        Assert.Equal(Rust, setting.Default);

        var declared = Assert.Single(TextureGraphParameters.Declared([setting]));

        Assert.Equal(Tint(), declared);
    }

    /// <summary>A published graph whose colour is an interface port feeding a Uniform inside.</summary>
    static (TextureGraphCompiler Compiler, NodeGraphModel Graph, GraphNode Used) Containing() {
        NodeGraphModel published = new() { Name = "Swatch" };

        published.Interface.Add(new("Out", PortDirection.Output, PortKind.Image));
        published.Interface.Add(new("Tint", PortDirection.Input, PortKind.Float4, [1f, 1f, 1f, 1f], string.Empty));

        var entry = published.Add(SubGraphs.InputType);
        var uniform = published.Add("Source/Uniform");
        var exit = published.Add(SubGraphs.OutputType);

        published.Connect(new(entry.Id, "Tint"), new(uniform.Id, "Colour"));
        published.Connect(new(uniform.Id, "Out"), new(exit.Id, "Out"));

        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        TextureGraphLibrary library = new();

        library.Publish("Library/Swatch", published, [], registry);

        NodeGraphModel graph = new();
        var used = graph.Add("Library/Swatch");
        var output = graph.Add("Output/Output");

        graph.Connect(new(used.Id, "Out"), new(output.Id, "Input"));

        TextureGraphCompiler compiler = new(registry) {
            BaseWidth = 64, BaseHeight = 64, Seed = 3, SubGraphSource = library
        };

        compiler.Parameters.Add(Tint());

        return (compiler, graph, used);
    }

    [Fact]
    public void A_color_parameter_fills_a_sub_graph_nodes_color_port() {
        var (compiler, graph, used) = Containing();

        used.SetText(TextureGraphExpressions.KeyOf("Tint"), "tint");

        var compilation = compiler.Compile(graph);

        Assert.Empty(compilation.Diagnostics);
        Assert.Equal([0.8f, 0.4f, 0.1f, 0.5f], LanesOf(compilation.Value));

        compiler.Arguments = new Dictionary<string, string>(StringComparer.Ordinal) { ["tint"] = "1,0,0,1" };

        var refreshed = compiler.Refresh(graph);

        Assert.NotNull(refreshed);
        Assert.Equal([1f, 0f, 0f, 1f], LanesOf(refreshed));
    }

    /// <summary>⚠ A number on a sub-graph's colour port would be read in every lane, so it is refused.</summary>
    [Fact]
    public void A_number_on_a_sub_graph_nodes_color_port_is_refused_rather_than_splatted() {
        var (compiler, graph, used) = Containing();

        used.SetText(TextureGraphExpressions.KeyOf("Tint"), "0.5f");

        var compilation = compiler.Compile(graph);
        var refusal = Assert.Single(compilation.Diagnostics);

        Assert.Equal("TG0012", refusal.Id);
        Assert.Equal("Tint", refusal.Port);
        Assert.Equal(used.Id, refusal.Node);
        Assert.False(compilation.Succeeded);
    }

    /// <summary>
    ///     Arithmetic over a colour is refused as a colour, not handed to Raven as a name it does not know (Patina
    ///     #272): the TG0013 that came back said nothing about colours.
    /// </summary>
    [Theory]
    [InlineData("tint * 0.5")]
    [InlineData("(tint)")]
    [InlineData("tint.r")]
    public void Arithmetic_over_a_color_is_refused_as_a_color(string expression) {
        var (compiler, graph, uniform) = Tinted(expression);

        var refusal = Assert.Single(compiler.Compile(graph).Diagnostics);

        Assert.Equal("TG0012", refusal.Id);
        Assert.Equal((uniform.Id, "Colour"), (refusal.Node, refusal.Port));
        Assert.Contains("computes with the colour 'tint'", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(0, compiler.ExpressionCompilations);
    }

    /// <summary>A name that only begins like a colour's is arithmetic as before, and Raven folds it.</summary>
    [Fact]
    public void A_number_knob_whose_name_begins_like_a_colours_is_still_arithmetic() {
        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        NodeGraphModel graph = new();
        var noise = graph.Add("Source/Noise");
        var blur = graph.Add("Filters/Blur");
        var output = graph.Add("Output/Output");

        graph.Connect(new(noise.Id, "Out"), new(blur.Id, "Input"));
        graph.Connect(new(blur.Id, "Out"), new(output.Id, "Input"));
        blur.SetText(TextureGraphExpressions.KeyOf("Radius"), "tinted * 2");

        TextureGraphCompiler compiler = new(registry) { BaseWidth = 64, BaseHeight = 64, Seed = 3 };

        compiler.Parameters.Add(Tint());
        compiler.Parameters.Add(new("tinted", TextureGraphParameterKind.Scalar, 2f, 0f, 8f));

        var compilation = compiler.Compile(graph);

        Assert.Empty(compilation.Diagnostics);
        Assert.Equal(1, compiler.ExpressionCompilations);
    }

    /// <summary>
    ///     Two published graphs, one inside the other: <c>Library/Outer</c> declares <c>tint</c> and hands it to the
    ///     <c>Library/Middle</c> inside it by reference, and <c>Library/Middle</c>'s colour <c>shade</c> fills a port
    ///     two ways — on a Uniform's own port, folded once the walk is over, and on a <c>Library/Swatch</c> node's
    ///     port, resolved during the walk.
    /// </summary>
    static (TextureGraphCompiler Compiler, NodeGraphModel Graph, GraphNode Outer) Nested(bool swatch, string passed) {
        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        TextureGraphLibrary library = new();

        NodeGraphModel inner = new() { Name = "Swatch" };

        inner.Interface.Add(new("Out", PortDirection.Output, PortKind.Image));
        inner.Interface.Add(new("Tint", PortDirection.Input, PortKind.Float4, [1f, 1f, 1f, 1f], string.Empty));

        var entry = inner.Add(SubGraphs.InputType);
        var painted = inner.Add("Source/Uniform");
        var leaving = inner.Add(SubGraphs.OutputType);

        inner.Connect(new(entry.Id, "Tint"), new(painted.Id, "Colour"));
        inner.Connect(new(painted.Id, "Out"), new(leaving.Id, "Out"));
        library.Publish("Library/Swatch", inner, [], registry);

        NodeGraphModel middle = new() { Name = "Middle" };

        middle.Interface.Add(new("Out", PortDirection.Output, PortKind.Image));

        var exit = middle.Add(SubGraphs.OutputType);
        var source = middle.Add(swatch ? "Library/Swatch" : "Source/Uniform");

        source.SetText(TextureGraphExpressions.KeyOf(swatch ? "Tint" : "Colour"), "shade");
        middle.Connect(new(source.Id, "Out"), new(exit.Id, "Out"));
        library.Publish(
            "Library/Middle",
            middle,
            [new("shade", TextureGraphParameterKind.Color, Choice: "1,1,1,1")],
            registry
        );

        NodeGraphModel outer = new() { Name = "Outer" };

        outer.Interface.Add(new("Out", PortDirection.Output, PortKind.Image));

        var held = outer.Add("Library/Middle");
        var away = outer.Add(SubGraphs.OutputType);

        held.SetText("shade", passed);
        outer.Connect(new(held.Id, "Out"), new(away.Id, "Out"));
        library.Publish("Library/Outer", outer, [Tint()], registry);

        NodeGraphModel graph = new();
        var placed = graph.Add("Library/Outer");
        var output = graph.Add("Output/Output");

        graph.Connect(new(placed.Id, "Out"), new(output.Id, "Input"));

        TextureGraphCompiler compiler = new(registry) {
            BaseWidth = 64, BaseHeight = 64, Seed = 3, SubGraphSource = library
        };

        return (compiler, graph, placed);
    }

    /// <summary>
    ///     A compound's colour knob can follow the knob of the compound around it, as a name knob does (Patina #272),
    ///     whether the colour is folded after the walk or resolved during it.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_nested_compounds_color_can_follow_the_one_around_it(bool swatch) {
        var (compiler, graph, outer) = Nested(swatch, TextureGraphParameters.ReferencePrefix + "tint");

        var declared = compiler.Compile(graph);

        Assert.Empty(declared.Diagnostics);
        Assert.Equal([0.8f, 0.4f, 0.1f, 0.5f], LanesOf(declared.Value));

        outer.SetText("tint", "0,0,1,1");

        var overridden = compiler.Compile(graph);

        Assert.Empty(overridden.Diagnostics);
        Assert.Equal([0f, 0f, 1f, 1f], LanesOf(overridden.Value));
    }

    /// <summary>A reference to a colour knob the graph around does not declare keeps the default and says so.</summary>
    [Fact]
    public void A_reference_to_no_color_knob_keeps_the_default_and_says_so() {
        var (compiler, graph, _) = Nested(false, TextureGraphParameters.ReferencePrefix + "nothing");

        var compilation = compiler.Compile(graph);

        Assert.Contains(
            compilation.Diagnostics,
            static diagnostic => diagnostic.Message.Contains("no colour knob called that", StringComparison.Ordinal)
        );
        Assert.Equal([1f, 1f, 1f, 1f], LanesOf(compilation.Value));
    }

    /// <summary>The host cannot pass a reference down: nothing contains the author's own graph.</summary>
    [Fact]
    public void A_reference_from_the_host_is_refused() {
        var colors = TextureGraphParameters.Colors(
            [Tint()],
            new Dictionary<string, string>(StringComparer.Ordinal) {
                ["tint"] = TextureGraphParameters.ReferencePrefix + "paint"
            },
            null,
            out var problems
        );

        Assert.Equal([0.8f, 0.4f, 0.1f, 0.5f], colors["tint"].ToArray());
        Assert.Contains("nothing contains it", Assert.Single(problems), StringComparison.Ordinal);
    }
}
