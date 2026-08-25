// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.AssetEditors.Shading;
using Vixen.Editor.NodeGraph;
using Vixen.Editor.ShaderGraph;
using Vixen.Ui;
using Vixen.Ui.Controls.Advanced;
using Xunit;

namespace Vixen.Editor.AssetEditors.Tests;

/// <summary>The shader graph as a document: the file, the undo stack, and the shader that comes out.</summary>
/// <remarks>
///     <para>
///         <b>What is checked here is the half that is this assembly's.</b> That a graph emits correct
///         Raven is <c>Vixen.Editor.ShaderGraph.Tests</c>' — it puts every emission through the real
///         compiler and holds the golden text — so what these assert is that a file round-trips, that
///         an authored name reaches the emitted source, and that a compile reports on <i>both</i>
///         halves: what the graph compiler said, and what Raven said about the text it produced.
///     </para>
///     <para>
///         ⚠ <b>The Raven check is the one that would have caught the failure this document exists to
///         make visible.</b> A graph can be perfectly well-formed and emit a shader that does not
///         type-check; a panel that only listed <c>Diagnostics</c> would call that a success.
///     </para>
/// </remarks>
public class ShaderGraphTests {
    [Fact]
    public void ANewGraphCompilesAndItsRavenTypeChecks() {
        using var fixture = new EditorFixture();

        var path = fixture.Write("Assets/Tinted.vxshadergraph", string.Empty);
        var document = new ShaderGraphDocument(fixture.Project, AssetId.Empty, path);

        var shader = document.Compile();

        Assert.NotNull(shader);
        Assert.Empty(document.Diagnostics);

        // The whole point of emitting source rather than IR: the same front end a hand-written
        // shader goes through has an opinion about this one, and it has none.
        Assert.Empty(document.SourceDiagnostics);

        Assert.Equal("Tinted", shader!.Name);
        Assert.Contains(shader.Properties, property => property is { Name: "tint", Type: "float4" });
    }

    [Fact]
    public void AGraphRoundTripsThroughItsFile() {
        using var fixture = new EditorFixture();

        var path = fixture.Write("Assets/Surface.vxshadergraph", string.Empty);
        var document = new ShaderGraphDocument(fixture.Project, AssetId.Empty, path);

        var nodes = document.Graph.Nodes.Count;
        var edges = document.Graph.Edges.Count;

        document.Save();

        var reopened = new ShaderGraphDocument(fixture.Project, AssetId.Empty, path);

        Assert.Empty(reopened.LoadDiagnostics);
        Assert.Equal(nodes, reopened.Graph.Nodes.Count);
        Assert.Equal(edges, reopened.Graph.Edges.Count);
        Assert.NotNull(reopened.Compile());
    }

    /// <summary>A property's name is authored, survives a save, and is one undo entry.</summary>
    /// <remarks>
    ///     ⚠ <b>The defect this covers is the one that made the node library unusable in a real
    ///     project.</b> The name used to be a C# field on the node — which nothing writes and nothing
    ///     saves — so every texture in every graph was <c>albedo</c> and two colour properties were
    ///     one binding. It lives in the graph's texts now, which is what makes renaming possible at
    ///     all.
    /// </remarks>
    [Fact]
    public void RenamingAPropertyReachesTheEmittedShader() {
        using var fixture = new EditorFixture();

        var path = fixture.Write("Assets/Metal.vxshadergraph", string.Empty);
        var document = new ShaderGraphDocument(fixture.Project, AssetId.Empty, path);

        var tint = document.Graph.Nodes.First(node => node.Type == "Input/Colour Property");

        document.Stack.Execute(
            new SetPortTextCommand(document.Graph, tint.Id, ShaderProperties.Key, "baseColour", document)
        );

        var shader = document.Compile();

        Assert.NotNull(shader);
        Assert.Contains("var baseColour: float4", shader!.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("var tint:", shader.Source, StringComparison.Ordinal);
        Assert.Empty(document.SourceDiagnostics);

        // One entry, so a typed name is one press of Ctrl+Z rather than one per keystroke.
        document.Stack.Undo();

        Assert.Contains("var tint: float4", document.Compile()!.Source, StringComparison.Ordinal);

        document.Stack.Redo();
        document.Save();

        var reopened = new ShaderGraphDocument(fixture.Project, AssetId.Empty, path);

        Assert.Contains("var baseColour: float4", reopened.Compile()!.Source, StringComparison.Ordinal);
    }

    /// <summary>Two texture nodes under two names are two textures, which is what renaming is for.</summary>
    [Fact]
    public void TwoTexturesUnderTwoNamesAreTwoBindings() {
        using var fixture = new EditorFixture();

        var document = new ShaderGraphDocument(
            fixture.Project,
            AssetId.Empty,
            fixture.Write("Assets/Layered.vxshadergraph", string.Empty)
        );

        var first = document.Graph.Add("Texture/Sample 2D", new(80f, 320f));
        var second = document.Graph.Add("Texture/Sample 2D", new(80f, 480f));

        second.SetText(ShaderProperties.Key, "detail");

        var master = document.Graph.Nodes.First(node => node.Type == "Master/Unlit");

        document.Graph.Connect(new(first.Id, "RGBA"), new(master.Id, "Colour"));

        var shader = document.Compile();

        Assert.NotNull(shader);
        Assert.Empty(document.SourceDiagnostics);

        // A texture and its sampler are one property, so two textures are four declarations.
        Assert.Contains(shader!.Properties, property => property.Name == "albedo");
        Assert.Contains(shader.Properties, property => property.Name == "albedoSampler");
        Assert.Contains(shader.Properties, property => property.Name == "detail");
        Assert.Contains(shader.Properties, property => property.Name == "detailSampler");
    }

    /// <summary>A graph with nothing to write says so against the graph rather than emitting.</summary>
    [Fact]
    public void AGraphWithNoMasterIsReportedRatherThanEmitted() {
        using var fixture = new EditorFixture();

        var document = new ShaderGraphDocument(
            fixture.Project,
            AssetId.Empty,
            fixture.Write("Assets/Headless.vxshadergraph", string.Empty)
        );

        var master = document.Graph.Nodes.First(node => node.Type == "Master/Unlit");

        document.Graph.Remove(master.Id, out _);

        Assert.Null(document.Compile());
        Assert.Contains(document.Diagnostics, diagnostic => diagnostic.Id == "SG0003");

        // Nothing was emitted, so there is nothing for Raven to have an opinion about — and a list of
        // complaints about the *previous* shader would be worse than an empty one.
        Assert.Empty(document.SourceDiagnostics);
    }

    /// <summary>A sub-graph whose property node is called something Raven cannot spell.</summary>
    /// <remarks>
    ///     ⚠ <b>Nothing about this graph is malformed.</b> A name with a space in it is a thing an
    ///     author can type into the panel's rename box, no graph-level rule refuses it, and the shader
    ///     it emits does not parse — which is the failure doc 11 says a panel showing only
    ///     <c>Diagnostics</c> would report as a success.
    /// </remarks>
    internal static SubGraphLibrary Tint(
        string property,
        NodeTypeRegistry? registry = null,
        string path = "Sub-graphs/Tint"
    ) {
        var graph = new NodeGraphModel { Name = "Tint" };

        graph.Interface.Add(new("Colour", NodeGraph.PortDirection.Output, PortKind.Float4));

        var colour = graph.Add("Input/Colour Property");
        var exit = graph.Add(SubGraphs.OutputType);

        colour.SetText(ShaderProperties.Key, property);
        graph.Connect(new(colour.Id, "Colour"), new(exit.Id, "Colour"));

        var library = new SubGraphLibrary();

        library.Add(path, graph, registry);

        return library;
    }

    /// <summary>Raven's complaint about a generated line names the node that wrote it.</summary>
    [Fact]
    public void ARavensComplaintNamesTheNodeThatWroteTheLine() {
        using var fixture = new EditorFixture();

        var document = new ShaderGraphDocument(
            fixture.Project,
            AssetId.Empty,
            fixture.Write("Assets/Spaced.vxshadergraph", string.Empty)
        );

        var tint = document.Graph.Nodes.First(node => node.Type == "Input/Colour Property");

        tint.SetText(ShaderProperties.Key, "my tint");
        document.Compile();

        Assert.NotEmpty(document.SourceDiagnostics);

        // One per complaint and in the same order, so a panel showing both halves of one does not
        // have to join two lists by matching sentences.
        Assert.Equal(document.SourceDiagnostics.Count, document.SourceNodeDiagnostics.Count);
        Assert.Contains(document.SourceNodeDiagnostics, diagnostic => diagnostic.Node == tint.Id);

        // And the preamble belongs to nobody. Blaming the nearest node for a line the compiler wrote
        // would send an author to a node that is fine.
        Assert.All(
            document.SourceNodeDiagnostics,
            diagnostic => Assert.False(diagnostic.Span.IsNone)
        );
    }

    /// <summary>And when the line came out of a sub-graph, it names a node the author can select.</summary>
    /// <remarks>
    ///     <b>This is the pair of gaps doc 11 recorded, closing together.</b> The node that wrote the
    ///     line is a copy the flattener made, with an identity that is in no file and on no canvas;
    ///     what the diagnostic names is the sub-graph node in the graph the author has open.
    /// </remarks>
    [Fact]
    public void ARavensComplaintInsideASubGraphNamesTheSubGraphNode() {
        using var fixture = new EditorFixture();

        var document = new ShaderGraphDocument(
            fixture.Project,
            AssetId.Empty,
            fixture.Write("Assets/Nested.vxshadergraph", string.Empty)
        ) {
            SubGraphSource = Tint("my tint")
        };

        var master = document.Graph.Nodes.First(node => node.Type == "Master/Unlit");
        var sub = document.Graph.Add("Sub-graphs/Tint", new(240f, 320f));

        document.Graph.Connect(new(sub.Id, "Colour"), new(master.Id, "Colour"));

        var shader = document.Compile();

        // The graph compiler has nothing to say: the graph is well-formed and the sub-graph inlined.
        Assert.NotNull(shader);
        Assert.Empty(document.Diagnostics);
        Assert.NotEmpty(document.SourceDiagnostics);

        var blamed = document.SourceNodeDiagnostics.Where(diagnostic => diagnostic.Node.IsValid).ToArray();

        Assert.NotEmpty(blamed);
        Assert.Contains(blamed, diagnostic => diagnostic.Node == sub.Id);

        // ⚠ The assertion that matters. Every node a complaint names is one the open document has, so
        // every one of them can be selected, framed and badged.
        Assert.All(blamed, diagnostic => Assert.True(document.Graph.TryGet(diagnostic.Node, out _)));
    }

    /// <summary>A graph-level complaint about an inlined node is re-addressed the same way.</summary>
    [Fact]
    public void AGraphComplaintInsideASubGraphNamesTheSubGraphNode() {
        using var fixture = new EditorFixture();

        var document = new ShaderGraphDocument(
            fixture.Project,
            AssetId.Empty,
            fixture.Write("Assets/Twinned.vxshadergraph", string.Empty)
        );

        // A sub-graph saved against a node library this process has not loaded, which is the
        // commonest thing to find inside somebody else's sub-graph.
        var inner = new NodeGraphModel { Name = "Stale" };

        inner.Interface.Add(new("Colour", NodeGraph.PortDirection.Output, PortKind.Float4));

        var missing = inner.Add("Plugin/Missing");
        var exit = inner.Add(SubGraphs.OutputType);

        inner.Connect(new(missing.Id, "Out"), new(exit.Id, "Colour"));

        var library = new SubGraphLibrary();

        library.Add("Sub-graphs/Stale", inner, document.Registry);
        document.SubGraphSource = library;

        var sub = document.Graph.Add("Sub-graphs/Stale", new(240f, 320f));

        Assert.Null(document.Compile());

        var unknown = Assert.Single(document.Diagnostics, diagnostic => diagnostic.Id == "NG0001");

        // ⚠ Not the identity the flattener gave the copy. That one is in no file and on no canvas, so
        // a panel could print this sentence and an author would have nothing to click.
        Assert.Equal(sub.Id, unknown.Node);
        Assert.Contains("Sub-graphs/Stale", unknown.Message, StringComparison.Ordinal);
        Assert.Contains($"It is {missing.Id}", unknown.Message, StringComparison.Ordinal);
    }

    /// <summary>A file this build cannot read opens empty and says why, rather than throwing.</summary>
    [Fact]
    public void ABrokenFileOpensWithADiagnostic() {
        using var fixture = new EditorFixture();

        var document = new ShaderGraphDocument(
            fixture.Project,
            AssetId.Empty,
            fixture.Write("Assets/Broken.vxshadergraph", "nodes: [ this is not a graph")
        );

        Assert.NotEmpty(document.LoadDiagnostics);
        Assert.Empty(document.Graph.Nodes);
    }
}

/// <summary>The shader graph panel: what it builds, and what it says after a compile.</summary>
public class ShaderGraphViewTests {
    static ShaderGraphView Open(ViewHarness harness, string name, out ShaderGraphDocument document) {
        document = new(
            harness.Project.Project,
            AssetId.New(),
            harness.Project.Write("Assets/" + name, string.Empty)
        );

        var view = harness.Ui.Document.Root.Add<ShaderGraphView>();

        view.Show(document);
        harness.Ui.Frame();

        return view;
    }

    /// <summary>Opening compiles, so the pane and the lists have something in them straight away.</summary>
    [Fact]
    public void OpeningCompilesAndShowsTheGeneratedRaven() {
        using var harness = new ViewHarness();

        var view = Open(harness, "Opened.vxshadergraph", out _);

        Assert.Contains("shader Opened", view.Generated.Source, StringComparison.Ordinal);

        // Read-only, because the next compile overwrites it — typing into it would throw work away.
        Assert.True(view.Generated.ReadOnly);

        // Hidden until asked for: the graph is what the author is looking at.
        Assert.True(view.Pane.HasClass("hidden"));

        view.ShowCode.IsChecked = true;
        harness.Ui.Frame();

        Assert.False(view.Pane.HasClass("hidden"));
    }

    /// <summary>The three columns are laid out, and showing the source narrows the canvas.</summary>
    /// <remarks>
    ///     ⚠ <b>A panel every part of which is drawn at zero by zero passes every other test here.</b>
    ///     The stylesheet is what makes these columns columns — an element nothing styles lays its
    ///     children out in a row — so the sizes are asserted rather than assumed.
    /// </remarks>
    [Fact]
    public void TheColumnsAreLaidOut() {
        using var harness = new ViewHarness();

        var view = Open(harness, "Laid.vxshadergraph", out _);
        var canvas = view.GraphView.Bounds.Width;

        Assert.True(canvas > 0f, "The canvas has no width.");
        Assert.True(view.Side.Bounds.Width > 0f, "The side column has no width.");
        Assert.True(view.Side.Bounds.Height > 0f, "The side column has no height.");
        Assert.Equal(0f, view.Pane.Bounds.Width);

        view.ShowCode.IsChecked = true;
        harness.Ui.Frame();

        Assert.True(view.Pane.Bounds.Width > 0f, "The source pane has no width.");
        Assert.True(view.GraphView.Bounds.Width < canvas, "Showing the source did not narrow the canvas.");
    }

    /// <summary>What the shader declares is listed, which is what a material has to fill in.</summary>
    [Fact]
    public void TheDeclaredPropertiesAreListed() {
        using var harness = new ViewHarness();

        var view = Open(harness, "Listed.vxshadergraph", out _);

        Assert.Contains(view.Properties.Children, row => row.Children[^1].Text == "tint");
    }

    /// <summary>Selecting a property node offers its name, and typing one is an undoable edit.</summary>
    [Fact]
    public void APropertyNodeIsRenamedFromThePanel() {
        using var harness = new ViewHarness();

        var view = Open(harness, "Renamed.vxshadergraph", out var document);

        // Nothing selected: no row, because there is no name to type into.
        Assert.Null(view.PropertyName);

        var tint = document.Graph.Nodes.First(node => node.Type == "Input/Colour Property");

        view.GraphView.Select([tint.Id]);
        harness.Ui.Frame();

        Assert.NotNull(view.PropertyName);

        view.PropertyName!.Value = "baseColour";
        harness.Ui.Frame();

        Assert.Equal("baseColour", tint.TextOf(ShaderProperties.Key));

        view.Compile();
        harness.Ui.Frame();

        Assert.Contains("var baseColour: float4", view.Generated.Source, StringComparison.Ordinal);
    }

    /// <summary>A graph that does not compile says so in the list rather than silently.</summary>
    [Fact]
    public void AGraphThatDoesNotCompileIsReported() {
        using var harness = new ViewHarness();

        var view = Open(harness, "Twins.vxshadergraph", out var document);

        document.Graph.Add("Master/Sprite", new(400f, 320f));

        view.Compile();
        harness.Ui.Frame();

        Assert.Contains(
            view.Diagnostics.Children,
            row => row.Children[0].Text == "graph"
                && (row.Children[^1].Text ?? "").Contains("SG0002", StringComparison.Ordinal)
        );

        Assert.Empty(view.Generated.Source);
    }

    /// <summary>
    ///     Tapping a complaint about a generated line selects the node that wrote it — and when the
    ///     line came out of a sub-graph, the node it selects is the sub-graph node the author has.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>This is what makes the span map worth recording.</b> A diagnostic that names a node
    ///         and a panel that only prints the sentence is a map nothing reads, which is the commonest
    ///         way a feature here is finished and useless.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A <c>TapEvent</c>, raised on the row.</b> An <c>AnalysisRow</c> is a bare element,
    ///         so it never raises a <c>ClickEvent</c>; a test that pressed the row the way it presses
    ///         the Compile button would pass against a handler that can never fire.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TappingADiagnosticSelectsTheNodeThatWroteTheLine() {
        using var harness = new ViewHarness();

        var view = Open(harness, "Blamed.vxshadergraph", out var document);

        document.SubGraphSource = ShaderGraphTests.Tint("my tint", document.Registry);

        var master = document.Graph.Nodes.First(node => node.Type == "Master/Unlit");
        var sub = document.Graph.Add("Sub-graphs/Tint", new(240f, 320f));

        document.Graph.Connect(new(sub.Id, "Colour"), new(master.Id, "Colour"));

        view.Compile();
        harness.Ui.Frame();

        var row = -1;

        for (var index = 0; index < view.Diagnostics.Children.Count; index++) {
            if (view.BlamedBy(index) == sub.Id) {
                row = index;

                break;
            }
        }

        Assert.True(row >= 0, string.Join("\n", view.Diagnostics.Children.Select(child => child.Children[^1].Text)));

        // The row says which node as well as which line, because a list of line numbers is a list
        // about a file the author never wrote.
        Assert.Contains("Tint", view.Diagnostics.Children[row].Children[^1].Text ?? "", StringComparison.Ordinal);

        view.Diagnostics.Children[row].Raise(new TapEvent { Count = 1 });
        harness.Ui.Frame();

        Assert.Equal([sub.Id], view.GraphView.Selection);
    }
}
