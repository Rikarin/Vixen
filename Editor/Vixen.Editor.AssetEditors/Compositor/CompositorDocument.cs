// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Yaml;
using Vixen.Editor.Core;
using Vixen.Editor.NodeGraph;
using Vixen.Rendering.Compositor;

namespace Vixen.Editor.AssetEditors.Compositor;

/// <summary>A graphics compositor, open for editing as a graph.</summary>
/// <remarks>
///     <para>
///         <b>The file holds the graph, not the frame.</b> A <c>.vxcomp</c> is a
///         <c>NodeGraphAsset</c> — nodes, edges, positions, the values and names an author typed —
///         and <see cref="Compile" /> turns it into the <see cref="GraphicsCompositorAsset" /> a
///         renderer builds a frame from. Saving the compiled form instead would throw away the
///         layout and make the document unopenable; saving both would be two files that can
///         disagree.
///     </para>
///     <para>
///         ⚠ <b>Nothing imports a <c>.vxcomp</c> yet.</b> <c>NativeFormatImporter</c> carries a
///         document forward unchanged, which is right for a material and wrong for a graph: what a
///         build needs is the compiled frame, so a compositor wants an importer that runs
///         <see cref="Compile" /> — the same shape <c>SceneImporter</c> has. Until it exists, a
///         compositor is authored here and handed to a renderer by a host that compiles it itself.
///     </para>
///     <para>
///         ⚠ <b>A file this build cannot read opens empty and says why.</b>
///         <c>NodeGraphDocument.Load</c> repairs what it can — an edge naming a node that is not
///         there, a duplicated identity — and reports it; a graph from a later version is refused
///         outright, because there it cannot tell what the bytes mean. Both arrive in
///         <see cref="LoadDiagnostics" /> rather than as an exception, so the panel that could show
///         the problem is reachable.
///     </para>
/// </remarks>
public sealed class CompositorDocument : EditorDocument {
    /// <summary>What a compositor graph is written as.</summary>
    public const string Extension = ".vxcomp";

    /// <summary>Where the file is, absolute.</summary>
    public string AssetPath { get; }

    /// <summary>The graph.</summary>
    public NodeGraphModel Graph { get; }

    /// <summary>The node types this document is edited against.</summary>
    public NodeTypeRegistry Registry { get; }

    /// <summary>What reading the file had to say — repairs, and a refusal.</summary>
    public IReadOnlyList<NodeDiagnostic> LoadDiagnostics { get; } = [];

    /// <summary>What the last <see cref="Compile" /> had to say.</summary>
    public IReadOnlyList<NodeDiagnostic> Diagnostics { get; private set; } = [];

    /// <summary>The frame the last <see cref="Compile" /> produced, or <see langword="null" />.</summary>
    public GraphicsCompositorAsset? Frame { get; private set; }

    /// <summary>Raised after <see cref="Compile" /> has run.</summary>
    public event Action<CompositorDocument>? Compiled;

    /// <summary>Opens a compositor graph.</summary>
    /// <param name="project">The project it belongs to.</param>
    /// <param name="asset">Its identity.</param>
    /// <param name="path">Where the file is, absolute.</param>
    /// <param name="registry">The node types, or <see langword="null" /> for this assembly's.</param>
    public CompositorDocument(
        EditorProject project,
        AssetId asset,
        string path,
        NodeTypeRegistry? registry = null
    ) : base(project, asset, Path.GetFileName(path)) {
        ArgumentException.ThrowIfNullOrEmpty(path);

        AssetPath = path;
        Registry = registry ?? CompositorGraphCompiler.CreateRegistry();

        var text = AssetFile.Read(path);

        if (text.Trim().Length == 0) {
            // A new file is a frame with nothing on it rather than an empty canvas, because a graph
            // with no frame node compiles to nothing and the first thing anybody would do is add one.
            Graph = new() { Name = Path.GetFileNameWithoutExtension(path) };
            Graph.Add("Frame/Frame", new(80f, 80f));

            return;
        }

        try {
            var asset0 = YamlSerializer.Parse<NodeGraphAsset>(text);

            Graph = NodeGraphDocument.Load(asset0, out var diagnostics);
            LoadDiagnostics = diagnostics;
        } catch (Exception exception) when (exception is YamlBindingException
            or YamlParseException or NotSupportedException) {
            Graph = new() { Name = Path.GetFileNameWithoutExtension(path) };
            LoadDiagnostics = [new("CO0000", exception.Message, NodeId.None)];
        }
    }

    /// <summary>Compiles the graph into a frame.</summary>
    /// <returns>The frame, or <see langword="null" /> when something was wrong with the graph.</returns>
    /// <remarks>
    ///     ⚠ <b>Run when it is asked for, not on every edit.</b> Wiring a node produces a graph that
    ///     is briefly incomplete — a pass with no targets, a chain with a gap — and compiling on
    ///     every change would fill the panel with complaints about a state the author is halfway
    ///     through leaving.
    /// </remarks>
    public GraphicsCompositorAsset? Compile() {
        var result = new CompositorGraphCompiler(Registry).Compile(Graph);

        Frame = result.Artefact;
        Diagnostics = result.Diagnostics;

        Compiled?.Invoke(this);
        return Frame;
    }

    /// <summary>The graph as this document would write it, without writing it.</summary>
    /// <returns>The YAML.</returns>
    public string ToYaml() => YamlSerializer.ToYaml(NodeGraphDocument.Save(Graph));

    /// <summary>The frame as YAML, for a host that wants to hand a renderer a file.</summary>
    /// <returns>The YAML, or <see langword="null" /> when the graph does not compile.</returns>
    public string? FrameToYaml() => Compile() is { } frame ? YamlSerializer.ToYaml(frame) : null;

    /// <inheritdoc />
    protected override void SaveCore() => AssetFile.Write(AssetPath, ToYaml());
}
