// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.NodeGraph;
using Vixen.Editor.TextureGraph;
using Xunit;

namespace Tests;

/// <summary>
///     A graph declaring its own base resolution, seed and knobs — doc 48 § D8 and § D9, #719.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The seed is the half worth staring at.</b> A base resolution that reopened at the
///         host's default is visible — every length in the material is wrong by a power of two — and
///         a seed that reopened different is simply a different picture, of a material somebody
///         signed off, on another machine. Doc 48 § D5 says a procedural texture whose output changes
///         between runs is not a source asset; a seed the host chose is exactly that.
///     </para>
///     <para>
///         <b>The properties are still there and still win where the file is silent</b>, which is
///         what makes this additive rather than a migration: every graph built in code declares
///         nothing.
///     </para>
/// </remarks>
public class TextureGraphDeclarationTests {
    /// <summary>What the graph declares is what the plan is compiled at.</summary>
    [Fact]
    public void A_graphs_declared_resolution_and_seed_reach_the_plan() {
        var graph = Noise();

        TextureGraphSettings.Declare(graph, 512, 128, 90210);

        var compiler = Compiler();
        var compilation = compiler.Compile(graph);

        Assert.Empty(compilation.Diagnostics);
        Assert.Equal(512, compilation.Value.BaseWidth);
        Assert.Equal(128, compilation.Value.BaseHeight);
        Assert.Equal(90210u, compilation.Value.Seed);

        // The instrument: the compiler was built with entirely different numbers, so each equality
        // above is about the graph rather than about a default that happened to match.
        Assert.NotEqual(512, Compiler().BaseWidth);
        Assert.NotEqual(90210u, Compiler().Seed);
    }

    /// <summary>A graph that declares nothing is compiled at the host's numbers.</summary>
    /// <remarks>
    ///     Every graph written in code, and every <c>.vxtexgraph</c> saved before the field existed.
    /// </remarks>
    [Fact]
    public void A_graph_that_declares_nothing_keeps_the_hosts_numbers() {
        var compiler = Compiler();
        var plan = compiler.Compile(Noise()).Value;

        Assert.Equal(256, plan.BaseWidth);
        Assert.Equal(41823u, plan.Seed);
    }

    /// <summary>A declared number that is nonsense is a warning, and the host's number is used.</summary>
    /// <remarks>
    ///     ⚠ <b>Rather than zero, which is what a parse into an <c>out</c> variable gives.</b> A base
    ///     width of zero makes every image in the plan one texel: it validates, it evaluates, and the
    ///     material it produces is not one anybody would connect with a hand edit to a file.
    /// </remarks>
    [Theory]
    [InlineData(TextureGraphSettings.BaseWidth, "wide")]
    [InlineData(TextureGraphSettings.BaseWidth, "0")]
    [InlineData(TextureGraphSettings.BaseWidth, "-256")]
    [InlineData(TextureGraphSettings.Seed, "later")]
    public void A_declaration_that_is_not_a_number_is_a_warning_and_is_not_used(string key, string text) {
        var graph = Noise();

        graph.Settings[key] = text;

        var compilation = Compiler().Compile(graph);
        var caution = Assert.Single(compilation.Diagnostics, diagnostic => diagnostic.Id == "TG0019");

        Assert.Equal(NodeSeverity.Warning, caution.Severity);
        Assert.NotNull(compilation.Artefact);
        Assert.Equal(256, compilation.Value.BaseWidth);
        Assert.Equal(41823u, compilation.Value.Seed);
    }

    /// <summary>A graph's declared parameters are the ones its expressions are folded against.</summary>
    /// <remarks>
    ///     ⚠ <b>The assertion is a <em>number in the plan</em> and not the parameter list.</b> A
    ///     compiler that adopted the list and folded against its own would produce the same
    ///     <c>Parameters</c> collection and a different picture, which is the failure that reads as
    ///     "the file did not save my knob" and is really "the file saved it and nothing read it".
    /// </remarks>
    [Fact]
    public void A_graphs_declared_parameters_are_what_its_expressions_fold_against() {
        var graph = Noise();
        var blur = graph.Add("Filters/Blur");

        graph.Parameters.Add(new("amount", "0.25", "How much", SettingKind.Float, 0f, 1f, "Wear"));

        foreach (var node in graph.Nodes) {
            if (string.Equals(node.Type, "Output/Output", StringComparison.Ordinal)) {
                graph.Disconnect(new(node.Id, "Input"));
                graph.Connect(new(blur.Id, "Out"), new(node.Id, "Input"));
            } else if (string.Equals(node.Type, "Source/Noise", StringComparison.Ordinal)) {
                graph.Connect(new(node.Id, "Out"), new(blur.Id, "Input"));
            }
        }

        blur.SetText(TextureGraphExpressions.KeyOf("Radius"), "amount * 32f");

        var compiler = Compiler();
        var compilation = compiler.Compile(graph);

        Assert.Empty(compilation.Diagnostics);

        // 0.25 × 32, folded by Raven against the parameter the *graph* declared — the compiler was
        // handed none.
        var radius = Assert.Single(
            compilation.Value.Ops.Where(op => op.Kernel == "Blur").Select(op => op.Find("radius")!.Value).Distinct()
        );

        Assert.Equal(8f, radius.Value);

        // ⚠ And the compiler's own list is untouched by having compiled that graph, which is what
        // the *next* graph depends on: the declaration is per compilation, and a list adopted into
        // the host's field is a knob one document leaves behind for another.
        Assert.Empty(compiler.Parameters);

        // The range the graph declared crossed with the name, and the assertion is behavioural
        // rather than a field-by-field read of a list: an override outside 0…1 is refused and the
        // declared default stands, which is only true if the minimum, the maximum *and* the default
        // all arrived.
        var overridden = Compiler();

        overridden.Arguments = new Dictionary<string, string>(StringComparer.Ordinal) { ["amount"] = "5" };

        var refused = overridden.Compile(graph);

        Assert.Single(refused.Diagnostics, diagnostic => diagnostic.Id == "TG0015");
        Assert.Equal(
            8f,
            Assert.Single(
                refused.Value.Ops.Where(op => op.Kernel == "Blur").Select(op => op.Find("radius")!.Value).Distinct()
            ).Value
        );
    }

    /// <summary>What one graph declares does not reach the next graph the same compiler compiles.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The documented behaviour is "a graph that declares nothing keeps the host's
    ///         values", and before this it was true exactly once.</b> <c>Adopt</c> wrote the graph's
    ///         declarations into the compiler's own settable properties and replaced its parameter
    ///         list, so a compiler that had seen a graph declaring 512×512 and a seed carried both
    ///         into every graph it compiled afterwards.
    ///     </para>
    ///     <para>
    ///         <b>A compiler is reusable and the preview pane reuses one</b> —
    ///         <c>TextureGraphPreviews.Rebuild</c> asks its factory for one per rebuild and re-sets
    ///         the resolution but not the seed or the knobs — so this is the shape the trap has: a
    ///         second document previewing with the first one's seed, which is a different picture and
    ///         nothing anywhere says so.
    ///     </para>
    /// </remarks>
    [Fact]
    public void What_one_graph_declares_does_not_leak_into_the_next() {
        var compiler = Compiler();
        var declaring = Noise();

        TextureGraphSettings.Declare(declaring, 512, 512, 90210);
        declaring.Parameters.Add(new("amount", "0.25", "", SettingKind.Float, 0f, 1f));

        Assert.Equal(512, compiler.Compile(declaring).Value.BaseWidth);

        // The same compiler, a graph that declares nothing, and the host's numbers back.
        var plain = compiler.Compile(Noise()).Value;

        Assert.Equal(256, plain.BaseWidth);
        Assert.Equal(256, plain.BaseHeight);
        Assert.Equal(41823u, plain.Seed);
        Assert.Empty(compiler.Parameters);
        Assert.Empty(compiler.ParameterValues);
    }

    /// <summary>A parameter list is the same list after a trip through the framework's settings.</summary>
    /// <remarks>
    ///     The two halves of the round-trip #719 needs: a texture graph's parameters are written into
    ///     a model as <see cref="SettingDefinition" />s and read back out. ⚠ Every field, because a
    ///     conversion that dropped the range would leave a knob that no longer refuses a value
    ///     outside it — and <see cref="TextureGraphParameters.Read" /> is what enforces that.
    /// </remarks>
    [Fact]
    public void A_parameter_list_survives_the_trip_through_the_frameworks_settings() {
        List<TextureGraphParameter> parameters = [
            new("amount", TextureGraphParameterKind.Scalar, 0.25f, 0f, 1f, "Wear", "How much"),
            new("tiles", TextureGraphParameterKind.Integer, 4f, 1f, 16f, "Layout"),
            new("tiling", TextureGraphParameterKind.Boolean, 1f)
        ];

        Assert.Equal(parameters, TextureGraphParameters.Declared(TextureGraphParameters.Settings(parameters)));
    }

    /// <summary>A graph declaring its own numbers keeps them when it contains a sub-graph.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><a href="https://github.com/Rikarin/Vixen/issues/780">#780</a>, and it is #719's
    ///         own failure reached by another route on the day #719 closed.</b>
    ///         <c>NodeGraphCompiler.Compile</c> replaces the graph with
    ///         <see cref="SubGraphs.Flatten" />'s before <c>Begin</c> runs, and the flattener built a
    ///         fresh model carrying the three side tables that existed when it was written. So a
    ///         graph declaring 512×512 and a seed compiled at the host's 256 and 41823 the moment it
    ///         contained one published node — with no diagnostic, because there is nothing structural
    ///         about the wrong number.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Every other test in this file uses a flat graph</b>, which is exactly the one
    ///         input for which the flattener does not run.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_graph_containing_a_sub_graph_still_declares_its_own_resolution_and_seed() {
        NodeGraphModel published = new() { Name = "Grunge" };

        published.Interface.Add(new("Out", PortDirection.Output, PortKind.Image));

        var inner = published.Add("Source/Noise");
        var exit = published.Add(SubGraphs.OutputType);

        published.Connect(new(inner.Id, "Out"), new(exit.Id, "Out"));

        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        TextureGraphLibrary library = new();

        library.Publish("Library/Grunge", published, [], registry);

        NodeGraphModel graph = new();
        var used = graph.Add("Library/Grunge");
        var output = graph.Add("Output/Output");

        output.SetText("Usage", "baseColor");
        graph.Connect(new(used.Id, "Out"), new(output.Id, "Input"));
        TextureGraphSettings.Declare(graph, 512, 128, 90210);

        TextureGraphCompiler compiler = new(registry) {
            BaseWidth = 256,
            BaseHeight = 256,
            Seed = 41823,
            SubGraphSource = library
        };

        var compilation = compiler.Compile(graph);

        Assert.Empty(compilation.Diagnostics);
        Assert.Equal(512, compilation.Value.BaseWidth);
        Assert.Equal(128, compilation.Value.BaseHeight);
        Assert.Equal(90210u, compilation.Value.Seed);

        // The instrument: the sub-graph really was inlined, so the numbers above survived a flatten
        // rather than a compilation that never called one.
        Assert.Contains(compilation.Value.Ops, op => op.Kernel == "Noise");
    }

    /// <summary>Publishing a graph that declares its own knobs needs no second list of them.</summary>
    /// <remarks>
    ///     ⚠ <b>The third instance of #779's and #780's shape, and the one that would have been
    ///     found last.</b> <c>TextureGraphLibrary.Publish</c> takes the exposed parameters as an
    ///     argument, because when it was written a parameter list was a property of whoever
    ///     constructed a compiler. Since #719 the graph carries them — so a caller that has not been
    ///     updated passes an empty list, and a published graph's knobs reach neither the settings of
    ///     the node standing for it nor the expressions inside it, which then bind against nothing.
    /// </remarks>
    [Fact]
    public void A_published_graph_declaring_its_own_parameters_needs_no_second_list() {
        NodeGraphModel published = new() { Name = "Grunge" };

        published.Interface.Add(new("Out", PortDirection.Output, PortKind.Image));
        published.Parameters.Add(new("amount", "0.25", "How much", SettingKind.Float, 0f, 1f, "Wear"));

        var inner = published.Add("Source/Noise");
        var blur = published.Add("Filters/Blur");
        var exit = published.Add(SubGraphs.OutputType);

        published.Connect(new(inner.Id, "Out"), new(blur.Id, "Input"));
        published.Connect(new(blur.Id, "Out"), new(exit.Id, "Out"));
        blur.SetText(TextureGraphExpressions.KeyOf("Radius"), "amount * 32f");

        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        TextureGraphLibrary library = new();

        // No second list: what the graph declares is what it publishes.
        library.Publish("Library/Grunge", published, [], registry);

        var knob = Assert.Single(library.ParametersOf("Library/Grunge"));

        Assert.Equal("amount", knob.Name);
        Assert.Equal(1f, knob.Maximum);
        Assert.Equal("Wear", knob.Group);

        // And the node standing for it shows the knob, with the range that makes it a slider.
        var setting = Assert.Single(registry.Get("Library/Grunge").Settings);

        Assert.Equal("amount", setting.Name);
        Assert.True(setting.IsBounded);

        // ⚠ And the expression *inside* the published graph folds against it, which is the half that
        // is a picture rather than a panel: 0.25 × 32.
        NodeGraphModel host = new();
        var used = host.Add("Library/Grunge");
        var output = host.Add("Output/Output");

        output.SetText("Usage", "baseColor");
        host.Connect(new(used.Id, "Out"), new(output.Id, "Input"));

        TextureGraphCompiler compiler = new(registry) {
            BaseWidth = 256,
            BaseHeight = 256,
            Seed = 41823,
            SubGraphSource = library
        };

        var compilation = compiler.Compile(host);

        Assert.Empty(compilation.Diagnostics);
        Assert.Equal(
            8f,
            Assert.Single(
                compilation.Value.Ops
                    .Where(op => op.Kernel == "Blur")
                    .Select(op => op.Find("radius")!.Value)
                    .Distinct()
            ).Value
        );
    }

    /// <summary>A graph of one noise node, kept as a base colour.</summary>
    static NodeGraphModel Noise() {
        NodeGraphModel graph = new();
        var noise = graph.Add("Source/Noise");
        var output = graph.Add("Output/Output");

        output.SetText("Usage", "baseColor");
        graph.Connect(new(noise.Id, "Out"), new(output.Id, "Input"));

        return graph;
    }

    static TextureGraphCompiler Compiler() {
        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        return new(registry) { BaseWidth = 256, BaseHeight = 256, Seed = 41823 };
    }
}
