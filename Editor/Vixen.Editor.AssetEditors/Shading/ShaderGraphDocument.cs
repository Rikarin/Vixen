// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Yaml;
using Vixen.Editor.AssetEditors.Code;
using Vixen.Editor.Core;
using Vixen.Editor.NodeGraph;
using Vixen.Editor.ShaderGraph;
using Vixen.Raven;
using Vixen.Raven.Syntax;
using Vixen.Ui.Controls.Advanced;

namespace Vixen.Editor.AssetEditors.Shading;

/// <summary>A shader, open for editing as a graph, with the Raven it emits beside it.</summary>
/// <remarks>
///     <para>
///         <b>Doc 20's B5 has had "shader graph ✅" against <c>.ShaderGraph</c> for a while, and what
///         that ticked was the node library, the typing and the emission.</b> None of it was
///         reachable: there was no document, no factory and no registration, so nothing in the editor
///         could turn a double-click into a graph. This is that half — the same shape
///         <see cref="Vfx.VfxDocument" /> and <c>CompositorDocument</c> already have, and for the
///         same reason it is in this assembly rather than in <c>Vixen.Editor.ShaderGraph</c>: that
///         one knows nothing about a project or a panel, which is what lets its tests compile a graph
///         with no editor in the way.
///     </para>
///     <para>
///         ⚠ <b>The file holds the graph, not the shader.</b> A <c>.vxshadergraph</c> is a
///         <c>NodeGraphAsset</c> — nodes, edges, positions, the numbers and names an author typed —
///         and <see cref="Compile" /> turns it into Raven. Saving the emitted source instead would
///         throw away the layout and make the document unopenable; saving both would be two files
///         that can disagree, with the generated one the tempting thing to hand-edit.
///     </para>
///     <para>
///         ⚠ <b>Compiling runs the graph compiler <i>and</i> Raven's front end.</b> The graph
///         compiler's complaints name a node and a port; Raven's name a line of text the author never
///         wrote. Both are worth having and neither substitutes for the other — a graph that is
///         well-formed can still emit a shader that does not type-check, which is exactly the failure
///         a panel showing only <see cref="Diagnostics" /> would report as success. Mapping the
///         second kind back to the node that emitted the line needs the emitter to record spans and
///         is <see cref="SourceDiagnostics" />'s standing debt, written down in doc 11.
///     </para>
///     <para>
///         ⚠ <b>Nothing imports a <c>.vxshadergraph</c> yet</b>, for the reason
///         <c>CompositorDocument</c> gives about a <c>.vxcomp</c>: <c>NativeFormatImporter</c> carries
///         a document forward unchanged, and what a build needs from a graph is the compiled shader.
///         That is an importer that runs <see cref="Compile" /> and hands the source to Raven, which
///         is doc 08's seam and not a panel's.
///     </para>
///     <para>
///         ⚠ <b>A file this build cannot read opens empty and says why.</b>
///         <c>NodeGraphDocument.Load</c> repairs what it can and refuses a graph from a later version
///         outright; both arrive in <see cref="LoadDiagnostics" /> rather than as an exception, so the
///         panel that could show the problem is reachable.
///     </para>
/// </remarks>
public sealed class ShaderGraphDocument : EditorDocument, INodePreviewSource {
    /// <summary>What a shader graph is written as.</summary>
    public const string Extension = ".vxshadergraph";

    /// <summary>Where the file is, absolute.</summary>
    public string AssetPath { get; }

    /// <summary>The graph.</summary>
    public NodeGraphModel Graph { get; }

    /// <summary>The node types this document is edited against.</summary>
    public NodeTypeRegistry Registry { get; }

    /// <summary>What reading the file had to say — repairs, and a refusal.</summary>
    public IReadOnlyList<NodeDiagnostic> LoadDiagnostics { get; } = [];

    /// <summary>What the last <see cref="Compile" /> had to say about the graph.</summary>
    public IReadOnlyList<NodeDiagnostic> Diagnostics { get; private set; } = [];

    /// <summary>What renders a node's preview thumbnail, when this editor has a device to do it with.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Set by the host after the device exists, which is after this object does</b> — the
    ///         same seam and the same reason as <c>ThumbnailCache.Surface</c>: the window has to be up
    ///         before a Vulkan surface can be made from it, so a document that demanded a renderer up
    ///         front would be one the editor could not open. Null is the ordinary state headless and
    ///         in every test, and the canvas draws flat swatches.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The document is the canvas's preview source, not the renderer</b>, which is what
    ///         lets this be set at any time. <c>NodeGraphView.PreviewSource</c> is read once per draw
    ///         and assigned once in <c>ShaderGraphView.Show</c>; if that assignment were the renderer
    ///         itself, then a graph opened by the session restore — which happens before the first
    ///         frame, and therefore before there is a device — would show swatches for ever.
    ///     </para>
    /// </remarks>
    public INodePreviewSource? PreviewSource { get; set; }

    /// <summary>What Raven had to say about the source the last <see cref="Compile" /> emitted.</summary>
    /// <remarks>
    ///     Empty when the graph did not compile, because there is no text to have an opinion about —
    ///     and a list of complaints about the <i>previous</i> shader would be the worst of both.
    /// </remarks>
    public IReadOnlyList<CodeDiagnostic> SourceDiagnostics { get; private set; } = [];

    /// <summary>The shader the last <see cref="Compile" /> emitted, or <see langword="null" />.</summary>
    public ShaderGraphSource? Source { get; private set; }

    /// <summary>Raised after <see cref="Compile" /> has run.</summary>
    public event Action<ShaderGraphDocument>? Compiled;

    /// <summary>Opens a shader graph.</summary>
    /// <param name="project">The project it belongs to.</param>
    /// <param name="asset">Its identity.</param>
    /// <param name="path">Where the file is, absolute.</param>
    /// <param name="registry">The node types, or <see langword="null" /> for this build's.</param>
    public ShaderGraphDocument(
        EditorProject project,
        AssetId asset,
        string path,
        NodeTypeRegistry? registry = null
    ) : base(project, asset, Path.GetFileName(path)) {
        ArgumentException.ThrowIfNullOrEmpty(path);

        AssetPath = path;
        Registry = registry ?? ShaderNodeLibrary.Create();

        var text = AssetFile.Read(path);

        if (text.Trim().Length == 0) {
            // ⚠ A new graph is a tinted surface rather than an empty canvas, and the reason is the
            // compiler's own third diagnostic: a graph with no master emits nothing at all, which is
            // what a new file that opened empty would report the first time anybody pressed Compile.
            // A colour property wired into a master is the smallest graph that both compiles and
            // demonstrates the two things every graph after it does — a node feeding the master, and
            // a name a material sets.
            Graph = new() { Name = Path.GetFileNameWithoutExtension(path) };

            var tint = Graph.Add("Input/Colour Property", new(80f, 80f));
            var master = Graph.Add("Master/Unlit", new(400f, 80f));

            Graph.Connect(new(tint.Id, "Colour"), new(master.Id, "Colour"));

            return;
        }

        try {
            var stored = YamlSerializer.Parse<NodeGraphAsset>(text);

            Graph = NodeGraphDocument.Load(stored, out var diagnostics);
            LoadDiagnostics = diagnostics;
        } catch (Exception exception) when (exception is YamlBindingException
            or YamlParseException or NotSupportedException) {
            Graph = new() { Name = Path.GetFileNameWithoutExtension(path) };
            LoadDiagnostics = [new("SG0000", exception.Message, NodeId.None)];
        }
    }

    /// <summary>Compiles the graph, and puts the emitted source through Raven.</summary>
    /// <returns>The shader, or <see langword="null" /> when the graph does not compile.</returns>
    /// <remarks>
    ///     ⚠ <b>Run when it is asked for, not on every edit.</b> Wiring a node produces a graph that
    ///     is briefly incomplete — a master with nothing feeding it, two masters while one is being
    ///     replaced — and compiling on every change would fill the panel with complaints about a
    ///     state the author is halfway through leaving.
    /// </remarks>
    public ShaderGraphSource? Compile() {
        var result = new ShaderGraphCompiler(Registry) {
            DefaultName = Path.GetFileNameWithoutExtension(AssetPath)
        }.Compile(Graph);

        Source = result.Artefact;
        Diagnostics = result.Diagnostics;
        SourceDiagnostics = Source is { } shader ? Check(shader.Source, AssetPath) : [];

        Compiled?.Invoke(this);
        return Source;
    }

    /// <summary>What Raven says about a shader graph's output.</summary>
    /// <param name="source">The emitted text.</param>
    /// <param name="path">What to call it in a message.</param>
    /// <returns>The complaints, in the editor's own form.</returns>
    /// <remarks>
    ///     <b>Lex, parse and bind — and stop there</b>, which is <c>ShaderDocument</c>'s rule and
    ///     holds for the same reason: those are the diagnostics that say something about the shader
    ///     rather than about a backend. What is different is that nobody can fix this text, so the
    ///     complaints are about the <i>graph</i> even though they name lines — see the class's own
    ///     remarks on what mapping them back would take.
    /// </remarks>
    internal static IReadOnlyList<CodeDiagnostic> Check(string source, string path) {
        var tree = SyntaxTree.ParseText(source, path: path);
        var compilation = Compilation.Create(Path.GetFileNameWithoutExtension(path), tree);

        List<CodeDiagnostic> found = [];

        foreach (var diagnostic in compilation.GetDiagnostics()) {
            found.Add(ShaderDocument.Translate(diagnostic));
        }

        return found;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Forwards, so that a canvas which took this document as its source before there was a
    ///     device starts showing pictures the moment there is one.
    /// </remarks>
    public bool TryGet(
        NodeGraphModel graph,
        NodeGraph.GraphNode node,
        NodeTypeDefinition definition,
        out NodePreview preview
    ) {
        if (PreviewSource is { } source) {
            return source.TryGet(graph, node, definition, out preview);
        }

        preview = default;

        return false;
    }

    /// <summary>The graph as this document would write it, without writing it.</summary>
    /// <returns>The YAML.</returns>
    public string ToYaml() => YamlSerializer.ToYaml(NodeGraphDocument.Save(Graph));

    /// <summary>The Raven the graph emits, for a host that wants the text.</summary>
    /// <returns>The source, or <see langword="null" /> when the graph does not compile.</returns>
    public string? ShaderSource() => (Source ?? Compile())?.Source;

    /// <inheritdoc />
    protected override void SaveCore() => AssetFile.Write(AssetPath, ToYaml());
}
