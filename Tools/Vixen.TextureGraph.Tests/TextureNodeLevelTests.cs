// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.NodeGraph;
using Vixen.TextureGraph;
using Xunit;

namespace Tests;

/// <summary>
///     A node, or a compound instance, computed at a level of its own (Patina #96, ADR 0028): a size relative to the
///     graph's, so the graph's own size still moves every node together.
/// </summary>
public class TextureNodeLevelTests {
    const int Side = 128;

    static NodeTypeRegistry Registry() {
        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        return registry;
    }

    static TextureGraphCompiler Compiler(NodeTypeRegistry registry, int side = Side) =>
        new(registry) { BaseWidth = side, BaseHeight = side, Seed = 3, PreviewEveryNode = true };

    /// <summary>Noise into Blur into Invert into an Output.</summary>
    static (NodeGraphModel Graph, GraphNode Noise, GraphNode Blur, GraphNode Invert) Chain() {
        NodeGraphModel graph = new();
        var noise = graph.Add("Source/Noise");
        var blur = graph.Add("Filters/Blur");
        var invert = graph.Add("Colour/Invert");
        var output = graph.Add("Output/Output");

        output.SetText("Usage", "height");
        graph.Connect(new(noise.Id, "Out"), new(blur.Id, "Input"));
        graph.Connect(new(blur.Id, "Out"), new(invert.Id, "Input"));
        graph.Connect(new(invert.Id, "Out"), new(output.Id, "Input"));

        return (graph, noise, blur, invert);
    }

    static int SideOf(TextureGraphCompiler compiler, TexturePlan plan, NodeId node) =>
        plan.SizeOf(Assert.Single(compiler.NodeImages.Where(written => written.Node == node)).Image).X;

    /// <summary>A node at level one writes half the graph's size, and what it feeds follows it.</summary>
    [Fact]
    public void A_node_at_a_level_of_its_own_is_computed_at_it_and_what_it_feeds_follows() {
        var (graph, noise, blur, invert) = Chain();
        blur.Level = 1;
        var compiler = Compiler(Registry());
        var compilation = compiler.Compile(graph);
        var plan = compilation.Value;

        Assert.NotNull(plan);
        Assert.Equal(Side, SideOf(compiler, plan, noise.Id));
        Assert.Equal(Side / 2, SideOf(compiler, plan, blur.Id));
        Assert.Equal(Side / 2, SideOf(compiler, plan, invert.Id));
        Assert.Equal("TG0022", Assert.Single(compilation.Diagnostics).Id);
    }

    /// <summary>A node at level zero is at the graph's size even downstream of a coarser one; null follows it.</summary>
    /// <remarks>
    ///     Relative to the graph and not to what arrives, which is ADR 0028's "relative to its graph": the coarser
    ///     image is resampled up where the two meet, and the Output needs no resample, so it says nothing.
    /// </remarks>
    [Fact]
    public void Level_zero_is_the_graphs_size_where_no_level_follows_the_inputs() {
        var (graph, _, blur, invert) = Chain();
        blur.Level = 2;
        invert.Level = 0;
        var compiler = Compiler(Registry());
        var compilation = compiler.Compile(graph);

        Assert.Empty(compilation.Diagnostics);
        Assert.Equal(Side / 4, SideOf(compiler, compilation.Value!, blur.Id));
        Assert.Equal(Side, SideOf(compiler, compilation.Value!, invert.Id));
    }

    /// <summary>A finer level doubles, and a node with no image input at all takes it as well.</summary>
    [Fact]
    public void A_source_at_a_finer_level_is_computed_at_twice_the_graphs_size() {
        var (graph, noise, blur, _) = Chain();
        noise.Level = -1;
        var compiler = Compiler(Registry());
        var plan = compiler.Compile(graph).Value!;

        Assert.Equal(Side * 2, SideOf(compiler, plan, noise.Id));
        Assert.Equal(Side * 2, SideOf(compiler, plan, blur.Id));
    }

    /// <summary>The graph's size still moves every node together: a level is a ratio, never a texel count.</summary>
    [Fact]
    public void A_level_is_the_same_ratio_at_every_graph_size() {
        var (graph, _, blur, _) = Chain();
        blur.Level = 1;

        foreach (var side in new[] { 64, 256, 480 }) {
            var compiler = Compiler(Registry(), side);
            var plan = compiler.Compile(graph).Value!;

            Assert.Equal(side / 2, SideOf(compiler, plan, blur.Id));
        }
    }

    /// <summary>A level past what a node may be is clamped, and the node is told.</summary>
    [Fact]
    public void A_level_out_of_range_is_clamped_with_a_warning_on_the_node() {
        var (graph, _, blur, _) = Chain();
        blur.Level = 9;
        var compiler = Compiler(Registry());
        var compilation = compiler.Compile(graph);
        var warning = Assert.Single(compilation.Diagnostics, static one => one.Id == "TG0026");

        Assert.Equal(NodeSeverity.Warning, warning.Severity);
        Assert.Equal(blur.Id, warning.Node);
        Assert.Equal(
            Side >> TextureGraphCompiler.CoarsestNodeLevel,
            SideOf(compiler, compilation.Value!, blur.Id)
        );
    }

    /// <summary>A published graph of noise, blurred, with the blur at a level the test chooses.</summary>
    static NodeGraphModel Published(int? blurLevel) {
        NodeGraphModel published = new() { Name = "Grunge" };

        published.Interface.Add(new("Out", PortDirection.Output, PortKind.Image));

        var noise = published.Add("Source/Noise");
        var blur = published.Add("Filters/Blur");
        var exit = published.Add(SubGraphs.OutputType);

        blur.Level = blurLevel;
        published.Connect(new(noise.Id, "Out"), new(blur.Id, "Input"));
        published.Connect(new(blur.Id, "Out"), new(exit.Id, "Out"));

        return published;
    }

    /// <summary>The sides of the noise and blur a compound instance inlined, and the host's levels after it.</summary>
    static (int Noise, int Blur, int Levels) Inlined(int? instanceLevel, int? blurLevel) {
        var registry = Registry();
        TextureGraphLibrary library = new();

        library.Publish("Library/Grunge", Published(blurLevel), [], registry);

        NodeGraphModel graph = new();
        var used = graph.Add("Library/Grunge");
        var levels = graph.Add("Colour/Levels");
        var output = graph.Add("Output/Output");

        used.Level = instanceLevel;
        output.SetText("Usage", "height");
        graph.Connect(new(used.Id, "Out"), new(levels.Id, "Input"));
        graph.Connect(new(levels.Id, "Out"), new(output.Id, "Input"));

        var compiler = Compiler(registry);

        compiler.SubGraphSource = library;

        var plan = compiler.Compile(graph).Value!;
        var noise = plan.Ops.First(static op => op.Kernel == "Noise");
        var blur = plan.Ops.Last(static op => op.Kernel == "Blur");

        return (plan.SizeOf(noise.Output).X, plan.SizeOf(blur.Output).X, SideOf(compiler, plan, levels.Id));
    }

    /// <summary>An instance's level applies to every node inside it, on top of what each says.</summary>
    [Fact]
    public void A_compound_instance_at_a_level_computes_every_node_inside_at_it() {
        Assert.Equal((Side, Side, Side), Inlined(null, null));
        Assert.Equal((Side / 2, Side / 2, Side / 2), Inlined(1, null));
        Assert.Equal((Side / 2, Side / 4, Side / 4), Inlined(1, 1));
        Assert.Equal((Side * 2, Side, Side), Inlined(-1, 1));
    }
}
