// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.AssetEditors.Shading;
using Vixen.Editor.NodeGraph;
using Vixen.Editor.ShaderGraph;
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
}
