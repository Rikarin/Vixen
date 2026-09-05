// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Core;
using Vixen.Core.Yaml;
using Vixen.Editor.Core;
using Vixen.Editor.NodeGraph;
using Vixen.Editor.TextureGraph;

namespace Vixen.Editor.Texturing;

/// <summary>A texture graph, open for editing.</summary>
/// <remarks>
///     <para>
///         <b>A <c>NodeGraphAsset</c>, exactly as a <c>.vxshadergraph</c> is.</b> The file holds
///         nodes, edges, positions and the numbers an author typed — not the images and not the
///         plan. Doc 48 § D4 is why that is not a compromise: what a graph <em>produces</em> is a
///         folder of PNGs and a <c>.vxmat</c>, which the content build writes and the runtime reads,
///         so the authored graph and the baked output are two files with two lifetimes rather than
///         one file that is both.
///     </para>
///     <para>
///         ⚠ <b>A new graph is a colour wired into an output, not an empty canvas</b>, and the
///         reason is the compiler's: a graph with no <c>Output</c> node produces no images at all, so
///         a file that opened empty would report nothing to bake the first time anybody asked. One
///         <c>Source/Uniform</c> into one <c>Output/Output</c> is the smallest graph that both
///         compiles and shows the two moves every graph after it makes.
///     </para>
///     <para>
///         ⚠ <b>This used to say "nothing here compiles, because <c>TextureGraphCompiler</c> is
///         <c>internal</c>", and that claim is false.</b> It was true when it was written and
///         <a href="https://github.com/Rikarin/Vixen/issues/738">#738</a> closed by making the
///         compiler <c>public sealed</c>; the sentence outlived the fix, in this file and in the
///         panel's own status line, where an author reads it. <see cref="Compile" /> is the method
///         it said could not exist.
///     </para>
///     <para>
///         ⚠ <b>The registry carries published node types and the compilation carries the source
///         that resolves them, and the two come from one call.</b>
///         <c>TextureCompoundLibrary.Publish</c> had no caller outside its own tests —
///         <a href="https://github.com/Rikarin/Vixen/issues/799">#799</a>,
///         <a href="https://github.com/Rikarin/Vixen/issues/803">#803</a> — so doc 48 § 4.9's four
///         shipped compounds were in the assembly, loadable, compilable and absent from every menu.
///         A document takes both halves from <see cref="TextureNodeLibrary.Publish" /> precisely so
///         that they cannot come apart: a node in the search popup that the compiler cannot resolve
///         is a <c>TG0001</c> handed to the author who placed it.
///     </para>
///     <para>
///         ⚠ <b>The base resolution is not stored, because there is nowhere to store it.</b>
///         <c>NodeGraphModel</c> carries a name, a node list and an interface;
///         <c>TextureGraphCompiler.BaseWidth</c> is a property a host sets, and its own remarks call
///         that a gap — <a href="https://github.com/Rikarin/Vixen/issues/719">#719</a>. So
///         <see cref="BaseWidth" /> here is what the panel shows and what a bake would use, and it
///         does not survive a save. Inventing a sidecar to hold it would be a second file that
///         disagrees with the one #719 is going to add.
///     </para>
/// </remarks>
public sealed class TextureGraphDocument : EditorDocument {
    /// <summary>What a texture graph is written as.</summary>
    public const string Extension = ".vxtexgraph";

    /// <summary>What an unopened <c>.vxtexgraph</c> is: a zero-byte file.</summary>
    /// <remarks>
    ///     ⚠ <b>Empty rather than a starter document, which is the opposite of doc 31's four.</b>
    ///     <c>NewAssetKind</c>'s own remarks draw the line: a kind an <i>importer</i> reads needs
    ///     starter text, because an importer deserialises an empty file and reports it as incomplete;
    ///     a kind whose <i>editor</i> opens an empty one as a sensible new document wants the empty
    ///     file, because the starter graph then lives in one place — the constructor below — rather
    ///     than in a string in a menu registration and again in the reader.
    /// </remarks>
    public const string NewContents = "";

    /// <summary>Where the file is, absolute.</summary>
    public string AssetPath { get; }

    /// <summary>The graph.</summary>
    public NodeGraphModel Graph { get; }

    /// <summary>The node types this document is edited against.</summary>
    public NodeTypeRegistry Registry { get; }

    /// <summary>What resolves a published node type in <see cref="Registry" /> to its graph.</summary>
    /// <remarks>
    ///     Null when a caller supplied its own registry, which is the only way this document has a
    ///     registry it did not publish into. <see cref="Compile" /> hands it straight to the
    ///     compiler's <c>SubGraphSource</c>.
    /// </remarks>
    internal ISubGraphSource? SubGraphs { get; }

    /// <summary>Every compound file that could not be published, and why.</summary>
    /// <remarks>
    ///     ⚠ <b>A compound that will not read is a node type missing from the menu and nothing
    ///     else</b> — <c>TextureCompoundLibrary.Publish</c> reports and skips rather than throwing,
    ///     so that one bad file in <c>Assets/Compounds</c> does not cost an author the whole library.
    ///     That makes this the only place the loss is visible.
    /// </remarks>
    internal ImmutableArray<TextureCompoundProblem> CompoundProblems { get; } = [];

    /// <summary>What reading the file had to say — repairs, and a refusal.</summary>
    /// <remarks>
    ///     Reported rather than thrown, for <c>ShaderGraphDocument</c>'s reason: a graph this build
    ///     cannot read has to open, or the panel that could show the problem is unreachable.
    /// </remarks>
    public IReadOnlyList<NodeDiagnostic> LoadDiagnostics { get; } = [];

    /// <summary>The width the graph is authored at, in texels.</summary>
    /// <remarks>See the type's remarks: held, shown, and not saved, because #719 owns the file half.</remarks>
    public int BaseWidth { get; set; } = 1024;

    /// <summary>The height the graph is authored at.</summary>
    public int BaseHeight { get; set; } = 1024;

    /// <summary>Opens a texture graph.</summary>
    /// <param name="project">The project it belongs to.</param>
    /// <param name="asset">Its identity.</param>
    /// <param name="path">Where the file is, absolute.</param>
    /// <param name="registry">The node types, or <see langword="null" /> for this build's.</param>
    public TextureGraphDocument(
        EditorProject project,
        AssetId asset,
        string path,
        NodeTypeRegistry? registry = null
    ) : base(project, asset, Path.GetFileName(path)) {
        ArgumentException.ThrowIfNullOrEmpty(path);

        AssetPath = path;

        if (registry is null) {
            // ⚠ The project's assets folder, so that `Assets/Compounds` is published beside the four
            // this build ships. A caller that brought its own registry brings its own sub-graph
            // source too — or none, which is what a graph of atomic nodes needs.
            var library = TextureNodeLibrary.Publish(project.Paths.Assets);

            Registry = library.Registry;
            SubGraphs = library.SubGraphs;
            CompoundProblems = library.Problems;
        } else {
            Registry = registry;
        }

        var text = File.Exists(path) ? File.ReadAllText(path) : string.Empty;

        if (text.Trim().Length == 0) {
            Graph = Starter(Path.GetFileNameWithoutExtension(path));

            return;
        }

        try {
            var stored = YamlSerializer.Parse<NodeGraphAsset>(text);

            Graph = NodeGraphDocument.Load(stored, out var diagnostics);
            LoadDiagnostics = diagnostics;
        } catch (Exception exception) when (exception is YamlBindingException
            or YamlParseException or NotSupportedException) {
            Graph = new() { Name = Path.GetFileNameWithoutExtension(path) };
            LoadDiagnostics = [new("TX0000", exception.Message, NodeId.None)];
        }
    }

    /// <summary>The smallest graph that produces a map.</summary>
    /// <param name="name">What to call it.</param>
    /// <returns>The graph.</returns>
    static NodeGraphModel Starter(string name) {
        var graph = new NodeGraphModel { Name = name };

        var colour = graph.Add("Source/Uniform", new(80f, 80f));
        var output = graph.Add("Output/Output", new(400f, 80f));

        graph.Connect(new(colour.Id, "Out"), new(output.Id, "Input"));

        return graph;
    }

    /// <summary>Compiles the graph to a plan, at the resolution the document is showing.</summary>
    /// <returns>The plan and the diagnostics, exactly as the compiler produced them.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The compiler is built per call and that is not an oversight.</b>
    ///         <c>TextureGraphCompiler</c> holds the last compilation's parameter values and node
    ///         images; a compiler kept across edits would answer a question about the graph as it was
    ///         two keystrokes ago. <c>ShaderGraphDocument.Compile</c> makes the same choice.
    ///     </para>
    ///     <para>
    ///         <b>Nothing is thrown and nothing is swallowed.</b> A graph with no <c>Output</c> node,
    ///         an unwired port, a published node this build cannot resolve — each is a diagnostic on
    ///         the compilation with a node id a panel can select, which is the whole reason the
    ///         compiler reports rather than throws.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><see cref="SubGraphs" /> is handed over even when it is null</b>, because the
    ///         compiler's own answer for a missing source is the <c>TG0001</c> that names the node —
    ///         "nothing inlined it" — and substituting an empty library here would turn that into a
    ///         node that silently produced no image.
    ///     </para>
    /// </remarks>
    internal NodeGraphCompilation<TexturePlan> Compile() =>
        new TextureGraphCompiler(Registry) {
            BaseWidth = BaseWidth,
            BaseHeight = BaseHeight,
            SubGraphSource = SubGraphs
        }.Compile(Graph);

    /// <summary>The graph as it would be written.</summary>
    /// <returns>The YAML.</returns>
    internal string ToYaml() => YamlSerializer.ToYaml(NodeGraphDocument.Save(Graph));

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Through a temporary and then moved, and LF whatever the platform.</b>
    ///         <c>AssetEditors.AssetFile</c> is these same six lines one assembly over and this does
    ///         not call it: reaching for it would put <c>Vixen.Editor.AssetEditors</c> — Assimp, and a
    ///         model importer for two dozen authoring formats — into the build of a plugin that wants
    ///         a text file written, which is the cost <c>PluginServices</c>'s own remarks refuse in
    ///         the same words. A save interrupted halfway must not leave a truncated graph where the
    ///         work was.
    ///     </para>
    /// </remarks>
    protected override void SaveCore() {
        var text = ToYaml().Replace("\r\n", "\n", StringComparison.Ordinal);

        if (text.Length > 0 && !text.EndsWith('\n')) {
            text += "\n";
        }

        var temporary = AssetPath + ".tmp";

        File.WriteAllText(temporary, text);
        File.Move(temporary, AssetPath, overwrite: true);
    }
}
