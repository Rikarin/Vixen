// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Vixen.Core;
using Vixen.Core.Yaml;
using Vixen.Editor.Core;
using Vixen.Editor.NodeGraph;
using Vixen.Editor.TextureGraph;

namespace Vixen.Editor.Texturing;

/// <summary>A texture graph, compiled — everything a pane about to draw it needs.</summary>
/// <param name="Plan">The plan, or <see langword="null" /> when the graph did not compile.</param>
/// <param name="Diagnostics">What the compiler had to say, about nodes.</param>
/// <param name="Outputs">Which image is which map, by usage.</param>
/// <param name="Externals">The imported images this plan needs supplied, per bitmap node.</param>
/// <remarks>
///     ⚠ <b><c>NodeGraphCompilation&lt;TexturePlan&gt;</c> alone is not enough to draw with, which is
///     why this exists</b> — <a href="https://github.com/Rikarin/Vixen/issues/792">#792</a>. It
///     carries the plan and the diagnostics and drops <c>TextureGraphCompiler.Outputs</c> and
///     <c>.Externals</c> on the floor, so a caller had the ops and no way to know which image is the
///     base colour or which bitmap wants a file. <see cref="LayerStackCompilation" /> is this same
///     shape one type over, and for the same reason.
/// </remarks>
sealed record TextureGraphCompilation(
    TexturePlan? Plan,
    ImmutableArray<NodeDiagnostic> Diagnostics,
    ImmutableArray<TextureGraphOutput> Outputs,
    ImmutableArray<TextureGraphExternal> Externals
);

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
///         panel's own status line, where an author still reads it —
///         <a href="https://github.com/Rikarin/Vixen/issues/816">#816</a>. <see cref="Compile" /> is
///         the method it said could not exist.
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

    /// <summary>Where this document's compounds are read from, or null when it did not publish.</summary>
    readonly string? compounds;

    /// <summary>Whether a compound has been saved since the library was built.</summary>
    bool stale;

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
    /// <remarks>
    ///     ⚠ <b>Replaced rather than mutated when a compound is saved</b> — see
    ///     <see cref="Republish" /> — so a caller that cached this holds an old menu. The panel reads
    ///     it on every show for that reason.
    /// </remarks>
    public NodeTypeRegistry Registry { get; private set; }

    /// <summary>What resolves a published node type in <see cref="Registry" /> to its graph.</summary>
    /// <remarks>
    ///     Null when a caller supplied its own registry, which is the only way this document has a
    ///     registry it did not publish into. <see cref="Compile" /> hands it straight to the
    ///     compiler's <c>SubGraphSource</c>.
    /// </remarks>
    internal ISubGraphSource? SubGraphs { get; private set; }

    /// <summary>Every compound file that could not be published, and why.</summary>
    /// <remarks>
    ///     ⚠ <b>A compound that will not read is a node type missing from the menu and nothing
    ///     else</b> — <c>TextureCompoundLibrary.Publish</c> reports and skips rather than throwing,
    ///     so that one bad file in <c>Assets/Compounds</c> does not cost an author the whole library.
    ///     That makes this the only place the loss is visible.
    /// </remarks>
    internal ImmutableArray<TextureCompoundProblem> CompoundProblems { get; private set; } = [];

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
            compounds = TextureNodeLibrary.FolderOf(project.Paths.Assets);

            Adopt(TextureNodeLibrary.Publish(project.Paths.Assets));

            // ⚠ Before the write rather than after it, which is why marking is all this does: the
            // project raises this so a file watcher can be told to ignore a path it is about to see
            // change, so the bytes are not on disk yet. Reading them here would republish the file
            // as it was.
            project.DocumentSaving += Saving;
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

    /// <summary>Rebuilds the node library if a compound has been saved since it was built.</summary>
    /// <returns><see langword="true" /> if it was rebuilt, so a caller can re-read what it cached.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The other half of <a href="https://github.com/Rikarin/Vixen/issues/803">#803</a>:
    ///         a document published once, in its constructor.</b> An author who edited a compound and
    ///         came back to the graph containing it saw the version that was on disk when this
    ///         document opened — the old ports, the old defaults, the old contents inlined into every
    ///         bake — and nothing said so. Reopening the graph was the only cure.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A flag set by a save and read here, rather than a check that walks the folder.</b>
    ///         The obvious mistake is republishing whenever anybody asks whether the library is
    ///         stale: this is asked from <see cref="Compile" /> and from the panel's every show, and
    ///         a directory walk plus a <c>stat</c> per compound on every keystroke is the same trap
    ///         as republishing on every keystroke wearing a hat. A save is the rare, deliberate act
    ///         that can change a compound, so a save is what sets the flag.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And a compound changed <em>outside</em> the editor sets it too, which for a batch
    ///         it did not</b> — <a href="https://github.com/Rikarin/Vixen/issues/922">#922</a>. A
    ///         <c>git checkout</c>, a text editor and a tool that writes <c>.vxtexgraph</c> files
    ///         raise no <c>DocumentSaving</c>, so a containing graph went on inlining the version
    ///         that was on disk when it opened. <see cref="OnProjectFileChanged" /> is the other
    ///         setter: <c>ExternalEdits</c> tells every open document what moved, and this one
    ///         answers for its own folder.
    ///     </para>
    /// </remarks>
    public bool Republish() {
        if (!stale) {
            return false;
        }

        stale = false;

        Adopt(TextureNodeLibrary.Publish(Project.Paths.Assets));

        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         <b>The half <see cref="Republish" /> could not have on its own —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/922">#922</a>.</b>
    ///         <c>EditorProject.DocumentSaving</c> hears a save made <em>in the editor</em>; a
    ///         <c>git checkout</c>, a text editor and a tool that writes <c>.vxtexgraph</c> files
    ///         raise nothing at all. This is where a change from outside arrives, and it is a
    ///         notification rather than a reload: what a compound changing means to a graph
    ///         containing it is that the library is stale, not that this file is.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A flag and no work, because this runs on the frame, once per drained change per
    ///         open document.</b> Reading the folder here would make somebody else's Ctrl+S cost the
    ///         editor a frame — <see cref="Republish" />'s own "a directory walk per keystroke is the
    ///         same trap wearing a hat", moved one caller along.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Null is "the watcher lost events", and it marks stale.</b> An overflow says
    ///         nothing about which file moved, so the honest answer is the conservative one: the cost
    ///         of being wrong is one republish, and the cost of assuming the best is a bake made from
    ///         a compound nobody can see is old.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Its own file changing is not this.</b> That is a reload, decided by
    ///         <c>ExternalEdits</c>'s policy against unsaved edits; republishing the library for it
    ///         would rebuild every node type because the graph on the canvas moved.
    ///     </para>
    /// </remarks>
    protected override void OnProjectFileChanged(string? path) {
        base.OnProjectFileChanged(path);

        if (compounds is null) {
            return;
        }

        if (path is null) {
            stale = true;

            return;
        }

        var absolute = Path.GetFullPath(Project.Paths.Absolute(path));
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(compounds));

        stale = stale
            || absolute.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    protected override void OnClosed() {
        base.OnClosed();

        // Only ever attached when this document published its own library, and unsubscribing one
        // that was never attached is free — so this is unconditional rather than mirrored.
        Project.DocumentSaving -= Saving;
    }

    /// <summary>Takes a freshly published library, replacing whatever this document had.</summary>
    [MemberNotNull(nameof(Registry))]
    void Adopt(TextureLibrary library) {
        Registry = library.Registry;
        SubGraphs = library.SubGraphs;
        CompoundProblems = library.Problems;
    }

    /// <summary>Notices that a graph in this project's compound folder is about to be written.</summary>
    /// <remarks>
    ///     ⚠ <b>Not every save, and not this document's own.</b> A save of the graph being edited
    ///     changes no compound, and replacing <see cref="Registry" /> on it would reproject the
    ///     canvas for nothing. What matters is a <c>.vxtexgraph</c> under
    ///     <c>Assets/Compounds</c> — which is the only thing <c>TextureNodeLibrary.Publish</c> reads
    ///     off disk.
    /// </remarks>
    void Saving(EditorDocument document) {
        if (compounds is null
            || ReferenceEquals(document, this)
            || document is not TextureGraphDocument graph) {
            return;
        }

        var saved = Path.GetFullPath(graph.AssetPath);
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(compounds));

        stale = stale
            || saved.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
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
    /// <returns>The plan, the diagnostics, the outputs and the externals, as the compiler made them.</returns>
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
    internal TextureGraphCompilation Compile() {
        // ⚠ Here rather than only in the panel, because this is where a stale library costs
        // something an author cannot see: the compilation inlines whatever the compound was when
        // this document opened, and the bake made from it is that graph, silently.
        Republish();

        TextureGraphCompiler compiler = new(Registry) {
            BaseWidth = BaseWidth,
            BaseHeight = BaseHeight,
            SubGraphSource = SubGraphs
        };

        var compilation = compiler.Compile(Graph);

        // ⚠ Read off the compiler *after* the compile and carried out, because they are only set by
        // it. A caller handed the bare `NodeGraphCompilation` has the ops and no way to know which
        // image is which map or which bitmap wants a file — which is why nothing could draw this.
        return new(compilation.Artefact, compilation.Diagnostics, compiler.Outputs, compiler.Externals);
    }

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
