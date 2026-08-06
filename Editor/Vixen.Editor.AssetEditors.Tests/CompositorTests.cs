// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.AssetEditors.Compositor;
using Vixen.Editor.NodeGraph;
using Vixen.Rendering.Compositor;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Editor.AssetEditors.Tests;

/// <summary>What a compositor graph compiles to, and what it refuses.</summary>
public class CompositorCompilerTests {
    static readonly NodeTypeRegistry Registry = CompositorGraphCompiler.CreateRegistry();

    /// <summary>Every node kind the frame model has is in the library.</summary>
    [Fact]
    public void TheLibraryCoversTheModel() {
        foreach (var path in new[] {
                     "Frame/Frame", "Frame/Sequence", "Frame/Render Pass", "Draw/Single Stage",
                     "Draw/Full Screen", "Draw/Compute", "Shadows/Shadow Map", "Shadows/Punctual Shadows",
                     "Culling/Hi-Z", "Culling/GPU Culling", "Buffers/Upload", "Buffers/Readback",
                     "Declare/Resource", "Declare/Buffer", "Declare/Stage"
                 }) {
            Assert.True(Registry.TryGet(path, out _), $"'{path}' is not registered.");
        }
    }

    /// <summary>⚠ A graph with no frame node has nothing to say what it renders.</summary>
    [Fact]
    public void NoFrameIsRefused() {
        var graph = new NodeGraphModel();
        graph.Add("Draw/Single Stage");

        var result = new CompositorGraphCompiler(Registry).Compile(graph);

        Assert.Null(result.Artefact);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "CO0003");
    }

    /// <summary>⚠ And a graph with two has two answers.</summary>
    [Fact]
    public void TwoFramesAreRefused() {
        var graph = new NodeGraphModel();
        graph.Add("Frame/Frame");
        graph.Add("Frame/Frame");

        var result = new CompositorGraphCompiler(Registry).Compile(graph);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "CO0002");
    }

    /// <summary>The chain's order is the frame's order.</summary>
    [Fact]
    public void TheChainIsTheOrder() {
        var graph = new NodeGraphModel();

        var frame = graph.Add("Frame/Frame");
        var first = graph.Add("Draw/Single Stage");
        var second = graph.Add("Draw/Single Stage");

        first.SetText("Name", "Opaque");
        second.SetText("Name", "Transparent");

        graph.Connect(new(frame.Id, "Body"), new(first.Id, "In"));
        graph.Connect(new(first.Id, "Out"), new(second.Id, "In"));

        var compositor = Compile(graph);
        var sequence = Assert.IsType<SequenceAsset>(compositor.Game);

        Assert.Equal(2, sequence.Children.Length);
        Assert.Equal("Opaque", sequence.Children[0].Name);
        Assert.Equal("Transparent", sequence.Children[1].Name);
    }

    /// <summary>A chain of one is the node, not a sequence wrapped round it.</summary>
    [Fact]
    public void AChainOfOneIsTheNode() {
        var graph = new NodeGraphModel();

        var frame = graph.Add("Frame/Frame");
        var only = graph.Add("Draw/Single Stage");

        graph.Connect(new(frame.Id, "Body"), new(only.Id, "In"));

        Assert.IsType<SingleStageAsset>(Compile(graph).Game);
    }

    /// <summary>A pass's body is a chain of its own, and it becomes the pass's children.</summary>
    [Fact]
    public void APassBodyBecomesItsChildren() {
        var graph = new NodeGraphModel();

        var frame = graph.Add("Frame/Frame");
        var pass = graph.Add("Frame/Render Pass");
        var stage = graph.Add("Draw/Single Stage");

        pass.SetText("Name", "GBuffer");
        pass.SetText("ColourTargets", "albedo, normal");
        pass.SetText("DepthTarget", "depth");

        graph.Connect(new(frame.Id, "Body"), new(pass.Id, "In"));
        graph.Connect(new(pass.Id, "Body"), new(stage.Id, "In"));

        var compiled = Assert.IsType<RenderPassAsset>(Compile(graph).Game);

        Assert.Equal("GBuffer", compiled.Name);
        Assert.Equal(["albedo", "normal"], compiled.ColourTargets);
        Assert.Equal("depth", compiled.DepthTarget);
        Assert.Single(compiled.Children);
    }

    /// <summary>A declaration node contributes to the frame without being on the chain.</summary>
    [Fact]
    public void DeclarationsAreCollectedWhereverTheySit() {
        var graph = new NodeGraphModel();

        graph.Add("Frame/Frame");

        var resource = graph.Add("Declare/Resource");
        resource.SetText("Name", "bloom");
        resource.SetValue("Scale", 0.5f);

        var stage = graph.Add("Declare/Stage");
        stage.SetText("Name", "Opaque");

        var buffer = graph.Add("Declare/Buffer");
        buffer.SetText("Name", "clusters");
        buffer.SetValue("Size", 4096f);

        var compositor = Compile(graph);

        Assert.Equal("bloom", Assert.Single(compositor.Resources).Name);
        Assert.Equal(0.5f, compositor.Resources[0].Scale);
        Assert.Equal("Opaque", Assert.Single(compositor.Stages).Name);
        Assert.Equal(4096, Assert.Single(compositor.Buffers).Size);
    }

    /// <summary>⚠ A node with flow ports that is on no chain is reported rather than dropped.</summary>
    [Fact]
    public void AnUnreachedNodeIsReported() {
        var graph = new NodeGraphModel();

        graph.Add("Frame/Frame");
        graph.Add("Draw/Full Screen");

        var result = new CompositorGraphCompiler(Registry).Compile(graph);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "CO0004");
    }

    /// <summary>⚠ And two nodes wired to one flow output cannot both be next.</summary>
    [Fact]
    public void TwoNodesCannotBothBeNext() {
        var graph = new NodeGraphModel();

        var frame = graph.Add("Frame/Frame");
        var first = graph.Add("Draw/Single Stage");
        var second = graph.Add("Draw/Single Stage");

        graph.Connect(new(frame.Id, "Body"), new(first.Id, "In"));
        graph.Connect(new(frame.Id, "Body"), new(second.Id, "In"));

        var result = new CompositorGraphCompiler(Registry).Compile(graph);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "CO0006");
    }

    /// <summary>An enum setting is resolved by name, so inserting a member does not move it.</summary>
    [Fact]
    public void AChoiceIsResolvedByName() {
        var graph = new NodeGraphModel();

        var frame = graph.Add("Frame/Frame");
        var screen = graph.Add("Draw/Full Screen");

        screen.SetText("Blend", "Additive");
        screen.SetText("Shader", "Bloom");
        graph.Connect(new(frame.Id, "Body"), new(screen.Id, "In"));

        var compiled = Assert.IsType<FullScreenAsset>(Compile(graph).Game);

        Assert.Equal(BlendPreset.Additive, compiled.Blend);
        Assert.Equal("Bloom", compiled.Shader);
    }

    /// <summary>An unset flag is on, so a node an author has never touched still runs.</summary>
    [Fact]
    public void AnUnsetFlagIsOn() {
        var graph = new NodeGraphModel();

        var frame = graph.Add("Frame/Frame");
        var stage = graph.Add("Draw/Single Stage");

        graph.Connect(new(frame.Id, "Body"), new(stage.Id, "In"));

        Assert.True(Compile(graph).Game!.Enabled);
    }

    static GraphicsCompositorAsset Compile(NodeGraphModel graph) {
        var result = new CompositorGraphCompiler(Registry).Compile(graph);

        Assert.Empty(result.Diagnostics);
        return result.Artefact!;
    }
}

/// <summary>What a compositor document does to its file.</summary>
public class CompositorDocumentTests {
    /// <summary>A new file opens with a frame on it, because a graph with none compiles to nothing.</summary>
    [Fact]
    public void ANewFileHasAFrame() {
        using var fixture = new EditorFixture();
        var path = fixture.Write("Assets/Forward.vxcomp", string.Empty);

        var document = new CompositorDocument(fixture.Project, AssetId.New(), path);

        Assert.Single(document.Graph.Nodes);
        Assert.NotNull(document.Compile());
    }

    /// <summary>The graph round-trips through the file, identities and all.</summary>
    [Fact]
    public void TheGraphRoundTrips() {
        using var fixture = new EditorFixture();
        var path = fixture.Write("Assets/Forward.vxcomp", string.Empty);

        var document = new CompositorDocument(fixture.Project, AssetId.New(), path);
        var frame = document.Graph.Nodes.First();
        var stage = document.Graph.Add("Draw/Single Stage");

        stage.SetText("Name", "Opaque");
        stage.SetValue("Enabled", 1f);
        document.Graph.Connect(new(frame.Id, "Body"), new(stage.Id, "In"));
        document.Save();

        var reopened = new CompositorDocument(fixture.Project, AssetId.New(), path);

        Assert.Equal(2, reopened.Graph.Nodes.Count);
        Assert.Equal("Opaque", reopened.Graph.Nodes.Single(node => node.Id == stage.Id).TextOf("Name"));
        Assert.Empty(reopened.LoadDiagnostics);
    }

    /// <summary>⚠ A file this build cannot read opens empty and says why, rather than throwing.</summary>
    [Fact]
    public void ABrokenFileOpensAndExplains() {
        using var fixture = new EditorFixture();
        var path = fixture.Write("Assets/Forward.vxcomp", "version: 99\n");

        var document = new CompositorDocument(fixture.Project, AssetId.New(), path);

        Assert.NotEmpty(document.LoadDiagnostics);
    }

    /// <summary>The compiled frame is available as YAML for a host that wants to hand one over.</summary>
    [Fact]
    public void TheFrameCanBeWrittenOut() {
        using var fixture = new EditorFixture();
        var path = fixture.Write("Assets/Forward.vxcomp", string.Empty);

        var document = new CompositorDocument(fixture.Project, AssetId.New(), path);
        var frame = document.Graph.Nodes.First();
        var stage = document.Graph.Add("Draw/Single Stage");

        stage.SetText("Name", "Opaque");
        document.Graph.Connect(new(frame.Id, "Body"), new(stage.Id, "In"));

        var yaml = document.FrameToYaml();

        Assert.NotNull(yaml);
        Assert.Contains("Opaque", yaml, StringComparison.Ordinal);
    }
}

/// <summary>
///     The settings panel, which is a <see cref="KeyValueList" /> since the five hand-rolled row
///     builders in the editor were folded into one control.
/// </summary>
/// <remarks>
///     ⚠ <b>Laid out, not merely built.</b> A row's two halves are the control's business now, so an
///     assertion that only counted rows would pass against a panel whose editors are all zero pixels
///     wide — which is exactly what a value slot holding text <i>and</i> an element would produce,
///     because the layout treats a node that measures its own text as a leaf.
/// </remarks>
public class CompositorSettingsTests {
    [Fact]
    public void SelectingANodeFillsTheRowsWithRealEditors() {
        using var harness = new ViewHarness();
        var view = Open(harness, out var document);

        var stage = document.Graph.Add("Draw/Single Stage");
        view.GraphView.Select([stage.Id]);
        harness.Ui.Frame();

        Assert.True(view.Fields.Count > 0, "the node has fields and the panel showed none");
        Assert.Equal(view.RowCount, view.Fields.Count);

        foreach (var row in view.Fields.Rows) {
            Assert.NotNull(row.KeyPart.Text);
            Assert.True(row.ValuePart.Bounds.Width > 0f, $"'{row.KeyPart.Text}' has no room for its editor");

            var editor = Assert.Single(row.ValuePart.Children);
            Assert.True(editor.Bounds.Width > 0f, $"'{row.KeyPart.Text}' has an editor of no width");
        }

        // Equal halves, out of the shared theme rather than out of anything this panel says.
        var first = view.Fields.Rows[0];
        Assert.Equal(first.KeyPart.Bounds.Width, first.ValuePart.Bounds.Width, 0.5f);
    }

    /// <summary>
    ///     ⚠ And selecting a different node rebuilds rather than reuses. A pooled row would still be
    ///     holding the previous type's editor and the handler that writes to the previous node's port.
    /// </summary>
    [Fact]
    public void SelectingAnotherNodeRebuildsTheRows() {
        using var harness = new ViewHarness();
        var view = Open(harness, out var document);

        var stage = document.Graph.Add("Draw/Single Stage");
        var pass = document.Graph.Add("Frame/Render Pass");

        view.GraphView.Select([stage.Id]);
        harness.Ui.Frame();

        var before = view.Fields.Rows.Select(row => row.KeyPart.Text).ToArray();

        view.GraphView.Select([pass.Id]);
        harness.Ui.Frame();

        Assert.Equal(view.Fields.Count, view.Fields.Rows.Count);
        Assert.NotEqual(before, view.Fields.Rows.Select(row => row.KeyPart.Text).ToArray());
    }

    /// <summary>
    ///     Nothing selected is a sentence, and the sentence is not a row — a bare element parented
    ///     among the rows would take a position in the stripe's alternation and shift every one of
    ///     them.
    /// </summary>
    [Fact]
    public void NothingSelectedIsASentenceBesideTheList() {
        using var harness = new ViewHarness();
        var view = Open(harness, out _);

        Assert.Equal(0, view.Fields.Count);
        Assert.NotNull(view.Caption.Text);
        Assert.Contains("Select a node", view.Caption.Text, StringComparison.Ordinal);
    }

    static CompositorView Open(ViewHarness harness, out CompositorDocument document) {
        var path = harness.Project.Write("Assets/Forward.vxcomp", string.Empty);
        document = new(harness.Project.Project, AssetId.New(), path);

        var view = harness.Ui.Document.Root.Add<CompositorView>();
        view.Show(document);

        harness.Ui.Frame();
        return view;
    }
}
