// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.NodeGraph;
using Vixen.TextureGraph;
using Xunit;

namespace Tests;

/// <summary>
///     A graph's two-number parameter (Patina #181): a position or a tiling, declared to an expression as a
///     <c>float2</c> so that <c>Offset.x</c> folds, and filling a vector port written with its name alone.
/// </summary>
/// <remarks>
///     ⚠ <b>The scalar assertions are on the folded radius and on an empty diagnostic list.</b> Before Raven folded a
///     swizzle of a constant, <c>Offset.x</c> bound and did not fold, so the port kept its own 3 with a "cannot fold"
///     beside it; both halves catch that.
/// </remarks>
public class TextureGraphPairParameterTests {
    static TextureGraphParameter Offset(string pair = "0.5,2", float minimum = 0f, float maximum = 4f) =>
        new("Offset", TextureGraphParameterKind.Float2, Minimum: minimum, Maximum: maximum, Choice: pair);

    static (TextureGraphCompiler Compiler, NodeGraphModel Graph, GraphNode Blur) Blurred(string expression) {
        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        NodeGraphModel graph = new();
        var noise = graph.Add("Source/Noise");
        var blur = graph.Add("Filters/Blur");
        var output = graph.Add("Output/Output");

        graph.Connect(new(noise.Id, "Out"), new(blur.Id, "Input"));
        graph.Connect(new(blur.Id, "Out"), new(output.Id, "Input"));
        blur.SetValue("Radius", 3f);
        blur.SetText(TextureGraphExpressions.KeyOf("Radius"), expression);

        TextureGraphCompiler compiler = new(registry) { BaseWidth = 64, BaseHeight = 64, Seed = 3 };

        compiler.Parameters.Add(Offset());

        return (compiler, graph, blur);
    }

    static float RadiusOf(TexturePlan plan) =>
        plan.Ops.First(static op => string.Equals(op.Kernel, "Blur", StringComparison.Ordinal))
            .Find("radius")!.Value.Value;

    [Theory]
    [InlineData("0.5,0.25", 0.5f, 0.25f)]
    [InlineData(" -1 , 8 ", -1f, 8f)]
    public void A_pair_reads_two_numbers_and_writes_them_back(string text, float x, float y) {
        Assert.True(SettingPairs.TryRead(text, out var lanes));
        Assert.Equal([x, y], lanes.ToArray());
        Assert.Equal(
            lanes.ToArray(),
            SettingPairs.TryRead(SettingPairs.Text(x, y), out var again) ? again.ToArray() : []
        );
    }

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("1,1,1")]
    [InlineData("x,0")]
    [InlineData("Infinity,0")]
    public void Anything_else_is_not_a_pair(string text) => Assert.False(SettingPairs.TryRead(text, out _));

    [Theory]
    [InlineData("Offset.x * 4f", 2f)]
    [InlineData("Offset.y", 2f)]
    [InlineData("Offset.x + Offset.y", 2.5f)]
    public void A_lane_of_a_pair_folds_in_an_expression(string expression, float radius) {
        var (compiler, graph, _) = Blurred(expression);

        var compilation = compiler.Compile(graph);

        Assert.Empty(compilation.Diagnostics);
        Assert.Equal(radius, RadiusOf(compilation.Value));
    }

    [Fact]
    public void A_host_override_moves_the_lanes_and_a_refresh_follows_it() {
        var (compiler, graph, _) = Blurred("Offset.y");

        compiler.Arguments = new Dictionary<string, string>(StringComparer.Ordinal) { ["Offset"] = "0,1" };
        Assert.Equal(1f, RadiusOf(compiler.Compile(graph).Value));

        compiler.Arguments = new Dictionary<string, string>(StringComparer.Ordinal) { ["Offset"] = "0,3.5" };
        var refreshed = compiler.Refresh(graph);

        Assert.NotNull(refreshed);
        Assert.Equal(3.5f, RadiusOf(refreshed));
    }

    [Theory]
    [InlineData("left")]
    [InlineData("1,9")]
    public void An_override_that_is_not_a_pair_in_range_keeps_the_default_and_says_so(string written) {
        var (compiler, graph, _) = Blurred("Offset.y");

        compiler.Arguments = new Dictionary<string, string>(StringComparer.Ordinal) { ["Offset"] = written };
        var compilation = compiler.Compile(graph);

        Assert.Contains(
            compilation.Diagnostics,
            static d => d.Message.Contains("not two numbers", StringComparison.Ordinal)
        );
        Assert.Equal(2f, RadiusOf(compilation.Value));
    }

    [Fact]
    public void A_pair_named_alone_on_a_port_that_takes_one_number_is_refused_naming_its_lanes() {
        var (compiler, graph, blur) = Blurred("Offset");

        var refusal = Assert.Single(compiler.Compile(graph).Diagnostics);

        Assert.Equal("TG0012", refusal.Id);
        Assert.Equal((blur.Id, "Radius"), (refusal.Node, refusal.Port));
        Assert.Contains("Offset.x", refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0.5,0.5,0.5")]
    [InlineData("0.5,9")]
    public void A_pair_default_that_does_not_read_or_leaves_its_range_is_refused(string pair) =>
        Assert.Single(TextureGraphParameters.Check([Offset(pair)]));

    [Fact]
    public void A_pair_is_a_Float2_setting_and_back() {
        var setting = Assert.Single(TextureGraphParameters.Settings([Offset()]));

        Assert.Equal((SettingKind.Float2, "0.5,2"), (setting.Kind, setting.Default));
        Assert.Equal(Offset(), Assert.Single(TextureGraphParameters.Declared([setting])) with { Group = string.Empty });
    }
}
