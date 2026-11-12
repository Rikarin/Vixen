// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.NodeGraph;
using Vixen.TextureGraph;
using Xunit;

namespace Tests;

/// <summary>
///     A bypassed node and a reroute dot compile to exactly what the graph without them does, so the
///     compiler honours the flag rather than each front end hiding the node.
/// </summary>
public class TextureGraphBypassTests {
    const int Side = 64;

    static TextureGraphCompiler Compiler() {
        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        return new(registry) { BaseWidth = Side, BaseHeight = Side, Seed = 7 };
    }

    static string Kernels(TexturePlan plan) => string.Join(",", plan.Ops.Select(static op => op.Kernel));

    /// <summary>Noise, then Levels, then an Output — with the Levels switched off or not.</summary>
    static (NodeGraphModel Graph, GraphNode Levels) Chain(bool bypassed) {
        NodeGraphModel graph = new();
        var noise = graph.Add("Source/Noise");
        var levels = graph.Add("Colour/Levels");
        var output = graph.Add("Output/Output");

        output.SetText("Usage", "baseColor");
        graph.Connect(new(noise.Id, "Out"), new(levels.Id, "Input"));
        graph.Connect(new(levels.Id, "Out"), new(output.Id, "Input"));
        levels.Bypassed = bypassed;

        return (graph, levels);
    }

    [Fact]
    public void A_bypassed_node_compiles_to_the_graph_without_it() {
        var (on, _) = Chain(false);
        var (off, _) = Chain(true);
        NodeGraphModel without = new();
        var noise = without.Add("Source/Noise");
        var output = without.Add("Output/Output");
        output.SetText("Usage", "baseColor");
        without.Connect(new(noise.Id, "Out"), new(output.Id, "Input"));

        var full = Compiler().Compile(on);
        var bypassed = Compiler().Compile(off);
        var reference = Compiler().Compile(without);

        Assert.NotNull(full.Value);
        Assert.NotNull(bypassed.Value);
        Assert.NotNull(reference.Value);
        Assert.Contains(full.Value.Ops, static op => op.Kernel.Contains("Levels", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(Kernels(reference.Value), Kernels(bypassed.Value));
        Assert.NotEqual(Kernels(full.Value), Kernels(bypassed.Value));
    }

    [Fact]
    public void A_reroute_dot_compiles_to_a_straight_wire() {
        var (straight, _) = Chain(false);
        NodeGraphModel bent = new();
        var noise = bent.Add("Source/Noise");
        var levels = bent.Add("Colour/Levels");
        var output = bent.Add("Output/Output");
        var dot = bent.Add(Reroutes.Type);
        output.SetText("Usage", "baseColor");
        bent.Connect(new(noise.Id, "Out"), new(dot.Id, Reroutes.In));
        bent.Connect(new(dot.Id, Reroutes.Out), new(levels.Id, "Input"));
        bent.Connect(new(levels.Id, "Out"), new(output.Id, "Input"));

        var compiled = Compiler().Compile(bent);

        Assert.DoesNotContain(compiled.Diagnostics, static diagnostic => diagnostic.Severity == NodeSeverity.Error);
        Assert.Equal(Kernels(Compiler().Compile(straight).Value!), Kernels(compiled.Value!));
    }

    [Fact]
    public void Switching_a_node_off_is_structural_and_is_never_a_refresh() {
        var compiler = Compiler();
        var (graph, levels) = Chain(false);

        Assert.NotNull(compiler.Compile(graph).Value);

        levels.Bypassed = true;

        Assert.Null(compiler.Refresh(graph));
    }

    [Fact]
    public void A_value_on_a_node_beside_a_bypassed_one_still_refreshes() {
        var compiler = Compiler();
        var (graph, levels) = Chain(true);
        var noise = graph.Nodes.First(static node => node.Type == "Source/Noise");

        Assert.NotNull(compiler.Compile(graph).Value);

        noise.SetValue("Scale", 3f);

        Assert.NotNull(compiler.Refresh(graph));
        Assert.True(levels.Bypassed);
    }
}
