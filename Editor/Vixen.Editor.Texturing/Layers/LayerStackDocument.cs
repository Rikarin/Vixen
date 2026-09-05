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

    /// <summary>Opens a layer stack.</summary>
    /// <param name="project">The project it belongs to.</param>
    /// <param name="asset">Its identity.</param>
    /// <param name="path">Where the file is, absolute.</param>
    /// <exception cref="ArgumentException"><paramref name="path" /> is empty.</exception>
    public LayerStackDocument(EditorProject project, AssetId asset, string path)
        : base(project, asset, Path.GetFileName(path)) {
        ArgumentException.ThrowIfNullOrEmpty(path);

        AssetPath = path;

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
