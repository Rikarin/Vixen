// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Yaml;
using Vixen.Editor.Core;

namespace Vixen.Editor.Texturing.Layers;

/// <summary>Where a layer's painted pixels live, and what they are called.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A second file, and doc 48 Part 5 says why in one line: a stack is a file people merge
///         and a paint layer is not.</b> A <c>.vxlayers</c> is YAML with layer names, blend modes and
///         anchors in it — three people can edit one and a merge tool can resolve it. A 4K RGBA
///         buffer inside that file would make every stroke a whole-file conflict and every diff
///         useless, and it would put a texture in a text format.
///     </para>
///     <para>
///         ⚠ <b>Pixels and not strokes, which is the decision this whole shape exists to allow.</b>
///         Doc 48 § D10: storing strokes re-renders at any resolution and diffs beautifully, and it
///         makes every brush, every falloff and every blend mode a <em>format compatibility
///         surface</em> — change the falloff curve and every existing project repaints differently.
///         Both references store pixels; the stroke list is the session's undo and is discarded on
///         save. Nothing here writes a <c>.vxpaint</c>: the brush is M9
///         (<a href="https://github.com/Rikarin/Vixen/issues/574">#574</a>). What M7 owes is a name
///         that will not have to change when it arrives.
///     </para>
/// </remarks>
static class LayerPaint {
    /// <summary>What a painted layer's pixels are written as.</summary>
    public const string Extension = ".vxpaint";

    /// <summary>
    ///     What a paint layer's file is called, relative to the stack: the stack's name, the texture
    ///     set, the layer's id, and — for a mask — the word <c>mask</c>.
    /// </summary>
    /// <param name="stack">The stack's file name without its extension.</param>
    /// <param name="set">The texture set's name.</param>
    /// <param name="layer">The layer's <see cref="LayerAsset.Id" />.</param>
    /// <param name="mask">Whether this is the layer's mask rather than its content.</param>
    /// <returns>The file name.</returns>
    /// <exception cref="ArgumentException">The stack name or the layer id is empty.</exception>
    /// <remarks>
    ///     ⚠ <b>Derived from the identity rather than stored, and only as a <em>default</em>.</b>
    ///     <see cref="LayerAsset.Paint" /> holds whatever path the file actually names, because a
    ///     stack that renames its set would otherwise orphan every painted layer in it. This is what
    ///     a new paint layer is called; it is not how an existing one is found.
    /// </remarks>
    public static string NameFor(string stack, string set, string layer, bool mask = false) {
        ArgumentException.ThrowIfNullOrEmpty(stack);
        ArgumentException.ThrowIfNullOrEmpty(layer);

        var slot = set.Length > 0 ? $".{set}" : "";

        return $"{stack}{slot}.{layer}{(mask ? ".mask" : "")}{Extension}";
    }
}

/// <summary>A layer stack, open for editing.</summary>
/// <remarks>
///     <para>
///         <b>A <c>.vxlayers</c>, per doc 48 Part 5: layers, masks, anchors, parameters — and no
///         pixels.</b> What the stack <em>produces</em> is the same folder of maps and the same
///         <c>.vxmat</c> a graph produces, because it compiles to the same <c>TexturePlan</c>; § D4
///         is why that is not a compromise.
///     </para>
///     <para>
///         ⚠ <b>Nothing in the editor opens one yet.</b> <c>TexturingModule</c> registers a document,
///         an editor factory and a Create ▸ entry for <c>.vxtexgraph</c> and none of the three for
///         <c>.vxlayers</c>; this type's callers are its tests and
///         <see cref="LayerStackCompiler" />. That is filed as
///         <a href="https://github.com/Rikarin/Vixen/issues/791">#791</a> rather than left implicit,
///         because a finished thing nothing calls is this repository's commonest defect.
///     </para>
/// </remarks>
sealed class LayerStackDocument : EditorDocument {
    /// <summary>Where this document's compounds are read from, or null when it has no project path.</summary>
    readonly string? compounds;

    /// <summary>Whether a compound has been written since the library was built.</summary>
    bool stale;

    /// <summary>What a layer stack is written as.</summary>
    public const string Extension = ".vxlayers";

    /// <summary>What an unopened <c>.vxlayers</c> is: a zero-byte file.</summary>
    /// <remarks>
    ///     <c>TextureGraphDocument.NewContents</c>' reason, unchanged: a kind whose editor opens an
    ///     empty file as a sensible new document wants the empty file, so the starter stack lives in
    ///     the constructor rather than in a menu registration and again in the reader.
    /// </remarks>
    public const string NewContents = "";

    /// <summary>Where the file is, absolute.</summary>
    public string AssetPath { get; }

    /// <summary>The stack.</summary>
    public LayerStackAsset Document { get; set; }

    /// <summary>What reading the file had to say.</summary>
    /// <remarks>
    ///     Reported rather than thrown, for <c>TextureGraphDocument</c>'s reason: a stack this build
    ///     cannot read has to open, or the panel that could show the problem is unreachable.
    /// </remarks>
    public IReadOnlyList<string> LoadDiagnostics { get; } = [];

    /// <summary>The node types this stack compiles against, and what inlines the compounds in them.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Held by the document, because the alternative is a directory walk per frame.</b>
    ///         <a href="https://github.com/Rikarin/Vixen/issues/924">#924</a> passed the project's
    ///         assets folder down <c>LayerStackCompiler.Compile</c> on every evaluation, and
    ///         <c>TextureCompoundLibrary.Publish</c> at the bottom of that is
    ///         <c>EnumerateFiles(AllDirectories)</c> plus a <c>File.ReadAllText</c> and a YAML parse
    ///         per project compound. <c>LayerStackPreview.Evaluate</c> runs from
    ///         <c>LayerStackView.Edited</c>, which an opacity slider raises once per frame of a drag
    ///         — so the fix for a correctness gap put the filesystem on the interactive path
    ///         (<a href="https://github.com/Rikarin/Vixen/issues/956">#956</a>).
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Replaced rather than mutated when a compound changes</b> — see
    ///         <see cref="Republish" /> — so a caller that cached it holds an old menu.
    ///     </para>
    /// </remarks>
    internal TextureLibrary Library { get; private set; }

    /// <summary>Opens a layer stack.</summary>
    /// <param name="project">The project it belongs to.</param>
    /// <param name="asset">Its identity.</param>
    /// <param name="path">Where the file is, absolute.</param>
    /// <exception cref="ArgumentException"><paramref name="path" /> is empty.</exception>
    public LayerStackDocument(EditorProject project, AssetId asset, string path)
        : base(project, asset, Path.GetFileName(path)) {
        ArgumentException.ThrowIfNullOrEmpty(path);

        AssetPath = path;

        // ⚠ Once, here, and then only when something says a compound moved. `TextureNodeLibrary`'s
        // own `FolderOf` is asked for the folder rather than a second `Path.Combine`, because a
        // second spelling of "which folder" is a second answer to "did that file change".
        compounds = TextureNodeLibrary.FolderOf(project.Paths.Assets);
        Library = TextureNodeLibrary.Publish(project.Paths.Assets);

        // ⚠ Before the write rather than after it, which is why marking is all this does: the
        // project raises this so a watcher can be told to ignore a path it is about to see change,
        // so the bytes are not on disk yet. `TextureGraphDocument`'s constructor says the same.
        project.DocumentSaving += Saving;

        var name = Path.GetFileNameWithoutExtension(path);
        var text = File.Exists(path) ? File.ReadAllText(path) : string.Empty;

        if (text.Trim().Length == 0) {
            Document = Starter(name);

            return;
        }

        try {
            Document = LayerStackYaml.Read(text);
        } catch (Exception exception) when (exception is YamlBindingException
            or YamlParseException or NotSupportedException) {
            Document = new() { Name = name };
            LoadDiagnostics = [exception.Message];
        }
    }

    /// <summary>Rebuilds the node library if a compound has been written since it was built.</summary>
    /// <returns><see langword="true" /> if it was rebuilt, so a caller can re-read what it cached.</returns>
    /// <remarks>
    ///     <para>
    ///         <b><c>TextureGraphDocument.Republish</c>'s shape, on the tool that needed it more.</b>
    ///         A graph panel compiles when the canvas changes; a layer stack's preview compiles on
    ///         every edit, and an opacity drag is an edit per frame.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A flag set by a write and read here, rather than a check that walks the
    ///         folder.</b> The obvious mistake is asking the disk whether the library is stale, which
    ///         is the directory walk this exists to remove wearing a hat. A save is the rare,
    ///         deliberate act that can change a compound, so a save is what sets the flag — and so is
    ///         a change from outside the editor, because a <c>git checkout</c> raises no
    ///         <c>DocumentSaving</c> at all.
    ///     </para>
    /// </remarks>
    internal bool Republish() {
        if (!stale) {
            return false;
        }

        stale = false;
        Library = TextureNodeLibrary.Publish(Project.Paths.Assets);

        return true;
    }


    /// <inheritdoc />
    protected override void OnClosed() {
        base.OnClosed();

        Project.DocumentSaving -= Saving;
    }

    /// <summary>Notices that a graph in this project's compound folder is about to be written.</summary>
    /// <remarks>
    ///     ⚠ <b>Not this document's own save.</b> A <c>.vxlayers</c> is not a compound, so saving the
    ///     stack being edited changes no node type — and republishing the library for it would
    ///     rebuild every one of them because a layer's opacity moved.
    /// </remarks>
    void Saving(EditorDocument document) {
        if (compounds is null || document is not TextureGraphDocument graph) {
            return;
        }

        stale = stale || Under(Path.GetFullPath(graph.AssetPath));
    }

    /// <summary>Whether an absolute path is inside this project's compound folder.</summary>
    /// <param name="absolute">The path.</param>
    /// <returns>Whether it is.</returns>
    bool Under(string absolute) =>
        absolute.StartsWith(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(compounds!)) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase
        );

    /// <summary>The seven channels doc 48 § D11 names, and what each starts from.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Seven channels rather than § D11's five, because ORM is a <em>packing</em> and not
    ///         a channel.</b> The prose says "occlusion·roughness·metalness packed, which is what
    ///         <c>TexturedOrmFeature.cs:288</c> reads" — that is what the bake writes into one file,
    ///         and it is three maps until it does. Modelling the packed map as one channel would mean
    ///         a layer that wanted to write roughness alone had to write a colour whose other two
    ///         components mean occlusion and metalness, which is the opposite of what a per-channel
    ///         enable is for.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the defaults are the values whose <em>zero</em> is not "off".</b> Occlusion
    ///         starts at white, not black: a stack whose occlusion channel started at zero would bake
    ///         a fully occluded surface, which reads as a lighting bug three subsystems away.
    ///     </para>
    /// </remarks>
    public static List<ChannelAsset> DefaultChannels() => [
        new() { Usage = "baseColor", Default = [0.5f, 0.5f, 0.5f, 1f] },
        new() { Usage = "normal", Default = [0.5f, 0.5f, 1f, 1f] },
        new() { Usage = "roughness", Default = [0.5f, 0.5f, 0.5f, 1f] },
        new() { Usage = "metalness", Default = [0f, 0f, 0f, 1f] },
        new() { Usage = "occlusion", Default = [1f, 1f, 1f, 1f] },
        new() { Usage = "height", Default = [0.5f, 0.5f, 0.5f, 1f] },
        new() { Usage = "emissive", Default = [0f, 0f, 0f, 1f] }
    ];

    /// <summary>The smallest stack that bakes a material.</summary>
    /// <param name="name">What to call it.</param>
    /// <returns>The stack.</returns>
    /// <remarks>
    ///     ⚠ <b>One fill layer and not none</b>, for <c>TextureGraphDocument.Starter</c>'s reason: a
    ///     stack with no layers still compiles — every channel is its own default — but it shows
    ///     none of the two moves every stack after it makes, which are adding a layer and giving it
    ///     a blend mode.
    /// </remarks>
    public static LayerStackAsset Starter(string name) =>
        new() {
            Name = name,
            Sets = [
                new() {
                    Name = "Default",
                    Channels = DefaultChannels(),
                    Layers = [
                        new() {
                            Id = "base",
                            Name = "Base",
                            Kind = LayerKind.Fill,
                            Fill = LayerFillSource.Constant,
                            Blend = LayerBlendMode.Copy,
                            Values = { ["baseColor"] = [0.5f, 0.5f, 0.5f, 1f] }
                        }
                    ]
                }
            ]
        };

    /// <summary>Whether a model has appeared, moved or gone since the picker was last filled.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A flag set by a notification and cleared by whoever acts on it</b> —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/954">#954</a>. <c>LayerStackView</c>
    ///         refilled the mesh picker only when the document reference or the bound path changed,
    ///         and the module hands the same reference to every refresh — so importing a model while
    ///         a stack was open never added it to the list, which is exactly the failure the remark
    ///         above <c>Rebind</c> claimed the design prevented.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A flag rather than the refill itself, which is <c>OnProjectFileChanged</c>'s own
    ///         instruction.</b> That runs on the frame, once per drained change per open document,
    ///         and the refill walks every asset in the project — so doing it there would make an
    ///         external program's Ctrl+S cost a project walk per open stack, several times over for
    ///         a checkout that touched a hundred files.
    ///     </para>
    /// </remarks>
    public bool ModelsChanged { get; set; } = true;

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Two flags and no work, because this runs on the frame, once per drained change
    ///         per open document.</b> Reading the compound folder or walking the project's assets
    ///         here would make somebody else's Ctrl+S cost the editor a frame —
    ///         <see cref="Republish" />'s own trap, moved one caller along. Both answers are set
    ///         here and acted on by whoever needs them.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Null means "events were lost" and has to count as a change for both.</b>
    ///         <c>ExternalEdits</c> passes it when the watcher overflowed, at which point the only
    ///         honest answer is that anything may have happened. The cost of being wrong is one
    ///         republish and one picker refill; the cost of assuming the best is a picture baked
    ///         from a compound nobody can see is old, and a picker stale for the rest of the session
    ///         with nothing saying so.
    ///     </para>
    /// </remarks>
    protected override void OnProjectFileChanged(string? path) {
        base.OnProjectFileChanged(path);

        if (path is null || LayerStackMesh.Extensions.Contains(Path.GetExtension(path).ToLowerInvariant())) {
            ModelsChanged = true;
        }

        if (compounds is null) {
            return;
        }

        if (path is null) {
            stale = true;

            return;
        }

        stale = stale || Under(Path.GetFullPath(Project.Paths.Absolute(path)));
    }

    /// <summary>The stack as it would be written.</summary>
    /// <returns>The YAML.</returns>
    internal string ToYaml() => LayerStackYaml.Write(Document);

    /// <inheritdoc />
    /// <remarks>
    ///     Through a temporary and then moved, and LF whatever the platform —
    ///     <c>TextureGraphDocument.SaveCore</c>'s six lines and its reason.
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
