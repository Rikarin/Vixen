// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.NodeGraph;
using Vixen.TextureGraph;
using Xunit;

namespace Tests;

/// <summary>
///     <c>TG0025</c>: a number or a setting under a name the node's type does not declare is said, as an error in a
///     bake and a warning in a draft.
/// </summary>
/// <remarks>
///     ⚠ <b>Silent before Patina's wave 6.</b> The walk binds what a type declares and nothing else, so a
///     <c>Source/Gradient</c> handed a <c>Basis</c> compiled cleanly and drew its default sweep. Each case below goes
///     green with the check in <c>TextureGraphCompiler.Unclaimed</c> taken out, so each asserts the diagnostic is
///     there rather than only that the plan is.
/// </remarks>
public class TextureUnknownKeyTests {
    static NodeTypeRegistry Registry() {
        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        return registry;
    }

    /// <summary>A gradient into an output, which compiles with nothing to say.</summary>
    static (NodeGraphModel Graph, GraphNode Gradient) Swept() {
        NodeGraphModel graph = new();
        var gradient = graph.Add("Source/Gradient");
        var output = graph.Add("Output/Output");

        output.SetText("Usage", "baseColor");
        graph.Connect(new(gradient.Id, "Out"), new(output.Id, "Input"));

        return (graph, gradient);
    }

    /// <summary>The issue's own case: a gradient given a noise's setting refuses a bake and names the key.</summary>
    [Fact]
    public void A_setting_the_type_has_not_got_refuses_a_bake() {
        var (graph, gradient) = Swept();
        TextureGraphCompiler compiler = new(Registry()) { BaseWidth = 64, BaseHeight = 64 };

        Assert.Empty(compiler.Compile(graph).Diagnostics);

        gradient.SetText("Basis", "Value");

        var compilation = compiler.Compile(graph);
        var diagnostic = Assert.Single(compilation.Diagnostics);

        Assert.Equal("TG0025", diagnostic.Id);
        Assert.Equal(NodeSeverity.Error, diagnostic.Severity);
        Assert.Equal(gradient.Id, diagnostic.Node);
        Assert.Equal("Basis", diagnostic.Port);
        Assert.Contains("'Source/Gradient' has no setting called that", diagnostic.Message, StringComparison.Ordinal);
        Assert.Null(compilation.Artefact);
    }

    /// <summary>In a draft the same key is a warning, and the picture is still drawn.</summary>
    [Fact]
    public void A_draft_warns_and_still_compiles() {
        var (graph, gradient) = Swept();
        TextureGraphCompiler compiler = new(Registry()) { BaseWidth = 64, BaseHeight = 64, Drafting = true };

        gradient.SetText("Basis", "Value");

        var compilation = compiler.Compile(graph);
        var diagnostic = Assert.Single(compilation.Diagnostics);

        Assert.Equal("TG0025", diagnostic.Id);
        Assert.Equal(NodeSeverity.Warning, diagnostic.Severity);
        Assert.NotNull(compilation.Artefact);
    }

    /// <summary>A number under a name that is no input — or that is an output — is said too.</summary>
    [Fact]
    public void A_number_on_no_input_is_said() {
        var (graph, gradient) = Swept();
        TextureGraphCompiler compiler = new(Registry()) { BaseWidth = 64, BaseHeight = 64 };

        gradient.SetValue("Octaves", 4f);
        gradient.SetValue("Out", 1f);

        var found = compiler.Compile(graph)
            .Diagnostics.Where(static one => one.Id == "TG0025")
            .Select(static one => one.Port);

        Assert.Equal(["Octaves", "Out"], found.Order(StringComparer.Ordinal));
    }

    /// <summary>An expression's key names a port and is <c>TG0016</c>'s question, so it is not asked twice.</summary>
    [Fact]
    public void An_expression_key_is_left_to_its_own_diagnostic() {
        var (graph, gradient) = Swept();
        TextureGraphCompiler compiler = new(Registry()) { BaseWidth = 64, BaseHeight = 64 };

        gradient.SetText(TextureGraphExpressions.KeyOf("Nothing"), "1f");

        var compilation = compiler.Compile(graph);

        Assert.DoesNotContain(compilation.Diagnostics, static one => one.Id == "TG0025");
        Assert.Contains(compilation.Diagnostics, static one => one.Id == "TG0016");
    }

    /// <summary>
    ///     A published graph's node is checked against its interface and its knobs, before inlining throws it away.
    /// </summary>
    [Fact]
    public void A_compounds_node_is_checked_against_its_knobs() {
        var registry = Registry();
        var library = TextureCompoundLibrary.Publish(registry, null, out _);
        NodeGraphModel graph = new();
        var dust = graph.Add("Generators/Dust");
        var output = graph.Add("Output/Output");

        output.SetText("Usage", "baseColor");
        graph.Connect(new(dust.Id, "Out"), new(output.Id, "Input"));
        dust.SetText("facing", "0.7");

        TextureGraphCompiler compiler = new(registry) { BaseWidth = 64, BaseHeight = 64, SubGraphSource = library };

        Assert.Empty(compiler.Compile(graph).Diagnostics);

        dust.SetText("facng", "0.7");
        dust.SetValue("Amount", 1f);

        var found = compiler.Compile(graph).Diagnostics.Where(static one => one.Id == "TG0025").ToArray();

        Assert.Equal(["Amount", "facng"], found.Select(static one => one.Port).Order(StringComparer.Ordinal));
        Assert.All(found, one => Assert.Equal(dust.Id, one.Node));
    }
}
