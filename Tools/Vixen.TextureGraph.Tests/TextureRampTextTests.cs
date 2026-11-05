// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.NodeGraph;
using Vixen.TextureGraph;
using Xunit;

namespace Tests;

/// <summary>A ramp or a curve written in a setting, which a compilation reads so that no host has to.</summary>
public class TextureRampTextTests {
    const int Red = 0;
    const int Alpha = 3;

    static NodeTypeRegistry Registry() {
        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        return registry;
    }

    static (TextureGraphCompiler Compiler, NodeGraphModel Graph, GraphNode Node) Gradient(string ramp) {
        NodeGraphModel graph = new();
        var node = graph.Add("Source/Gradient");

        node.SetText("Ramp", ramp);

        var output = graph.Add("Output/Output");

        output.SetText("Usage", "baseColor");
        graph.Connect(new(node.Id, "Out"), new(output.Id, "Input"));

        return (new(Registry()) { BaseWidth = 16, BaseHeight = 16 }, graph, node);
    }

    [Fact]
    public void A_ramp_mixes_its_stops_in_the_values_they_were_written_in() {
        var table = TextureRamp.FromText("ramp:0 000000ff,1 ff0000ff")!;

        Assert.Equal(TextureRamp.Entries * 4, table.Length);
        Assert.Equal(0, table[Red]);
        Assert.Equal(255, table[((TextureRamp.Entries - 1) * 4) + Red]);
        Assert.Equal(128, table[(128 * 4) + Red]);
        Assert.Equal(255, table[(128 * 4) + Alpha]);
    }

    [Fact]
    public void A_channel_left_out_of_a_curve_is_straight_through_the_same_evaluator() {
        var straight = TextureRamp.Straight();
        var inverted = new Vixen.Core.Curves.CurveSample[] {
            new(0f, 1f, 0f, 0f, Vixen.Core.Curves.TangentMode.Auto),
            new(1f, 0f, 0f, 0f, Vixen.Core.Curves.TangentMode.Auto)
        };

        Assert.Equal(
            TextureRamp.FromCurves(inverted, straight, straight, straight),
            TextureRamp.FromText("curve:r=0 1,1 0")
        );
    }

    [Theory]
    [InlineData("Textures/ramp.png")]
    [InlineData("meshmap:curvature")]
    public void Anything_else_is_a_hosts_to_resolve(string reference) =>
        Assert.Null(TextureRamp.FromText(reference));

    [Theory]
    [InlineData("ramp:")]
    [InlineData("ramp:0 00")]
    [InlineData("ramp:x 000000ff")]
    [InlineData("ramp:0 gg0000ff")]
    [InlineData("curve:r=")]
    [InlineData("curve:0 0")]
    [InlineData("curve:r=0 nan")]
    public void Written_wrongly_says_so(string reference) =>
        Assert.Throws<InvalidDataException>(() => TextureRamp.FromText(reference));

    [Fact]
    public void A_compilation_carries_the_table_so_a_host_is_owed_nothing() {
        var (compiler, graph, _) = Gradient("ramp:0 ff0000ff,1 00ff00ff");

        var compilation = compiler.Compile(graph);

        Assert.True(compilation.Succeeded);
        var external = Assert.Single(compiler.Externals);
        Assert.Empty(external.Asset);
        Assert.Equal(TextureRamp.FromText("ramp:0 ff0000ff,1 00ff00ff"), external.Texels.ToArray());
    }

    [Fact]
    public void A_ramp_written_wrongly_names_its_node_and_a_path_still_crosses_as_a_reference() {
        var (compiler, graph, node) = Gradient("ramp:nonsense");

        var refused = compiler.Compile(graph);

        Assert.False(refused.Succeeded);
        Assert.Contains(refused.Diagnostics, diagnostic => diagnostic.Node == node.Id && diagnostic.Port == "Ramp");

        var (next, other, _) = Gradient("Textures/ramp.png");

        Assert.True(next.Compile(other).Succeeded);
        Assert.Equal("Textures/ramp.png", Assert.Single(next.Externals).Asset);
    }

    /// <summary>
    ///     ⚠ A refresh reuses the last compilation's uploads, so answering this one would leave the old table on the
    ///     device and a picture that does not follow the edit.
    /// </summary>
    [Fact]
    public void An_edited_ramp_is_a_compile_and_not_a_refresh() {
        var (compiler, graph, node) = Gradient("ramp:0 ff0000ff,1 00ff00ff");

        Assert.True(compiler.Compile(graph).Succeeded);
        Assert.NotNull(compiler.Refresh(graph));

        node.SetText("Ramp", "ramp:0 0000ffff,1 00ff00ff");

        Assert.Null(compiler.Refresh(graph));
    }

    [Fact]
    public void The_key_a_host_writes_is_the_key_a_compile_reads() {
        Assert.Equal("=Radius", TextureGraphExpressions.KeyOf("Radius"));
        Assert.True(TextureGraphExpressions.IsExpression("=Radius", out var port));
        Assert.Equal("Radius", port);
        Assert.False(TextureGraphExpressions.IsExpression("Usage", out _));
    }
}
