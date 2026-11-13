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
}
