// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.Ui;
using Vixen.Ui;

namespace Vixen.Editor.App;

/// <summary>What a tile shows for an asset: a glyph and a colour.</summary>
/// <param name="Glyph">The shape drawn in the middle of the tile.</param>
/// <param name="Tint">What colour it is drawn in.</param>
public readonly record struct Thumbnail(PathBuilder Glyph, Color4 Tint);

/// <summary>Which picture stands for which kind of asset.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A glyph per kind, not a picture of the asset, and the gap is worth stating
///         precisely.</b> A real thumbnail means decoding the source image and uploading it as a GPU
///         texture: <c>Image.Texture</c> takes a number handed out by <c>UiRenderer.RegisterImage</c>,
///         which needs a device — and the application deliberately has none, the host does. Nothing
///         in <c>Vixen.Core.Imaging</c> decodes a PNG either; it handles KTX2, mips and cubemaps,
///         which are what the <i>import</i> produces rather than what a browser is pointed at.
///     </para>
///     <para>
///         So the honest thing to ship is what every browser falls back to anyway for the assets it
///         cannot preview, and to be clear that it is the fallback. A picture per asset needs a
///         decode-and-upload path with a cache and an eviction rule, which is the same machinery
///         E5's asset previews need and belongs with them rather than ahead of them.
///     </para>
///     <para>
///         <b>The colour is doing as much work as the shape.</b> A grid of forty identical grey
///         glyphs is a grid nobody can scan; a grid where textures are one colour and scenes are
///         another can be read at a glance, which is most of what a grid view is <i>for</i>.
///     </para>
/// </remarks>
static class AssetThumbnails {
    /// <summary>What a folder shows.</summary>
    public static Thumbnail Folder { get; } = new(EditorIcons.Open, new Color4(0.85f, 0.72f, 0.38f, 1f));

    /// <summary>What something the database has no importer for shows.</summary>
    public static Thumbnail Unknown { get; } = new(EditorIcons.New, new Color4(0.55f, 0.58f, 0.64f, 1f));

    /// <summary>The picture for an importer tag.</summary>
    /// <param name="importer">What the sidecar names, or empty.</param>
    /// <returns>Its thumbnail.</returns>
    /// <remarks>
    ///     ⚠ <b>Matched on the tag rather than the extension.</b> The tag is what the sidecar records
    ///     and what the browser's type filter offers, so the two cannot disagree about what a file
    ///     is — and an importer added by a plugin gets the fallback rather than being mistaken for
    ///     something else.
    /// </remarks>
    public static Thumbnail For(string? importer) =>
        importer switch {
            "TextureImporter" => new(EditorIcons.Grid, new Color4(0.44f, 0.72f, 0.94f, 1f)),
            "SceneImporter" => new(EditorIcons.World, new Color4(0.55f, 0.80f, 0.52f, 1f)),
            "ModelImporter" => new(EditorIcons.Scale, new Color4(0.83f, 0.62f, 0.94f, 1f)),
            "MaterialImporter" => new(EditorIcons.Settings, new Color4(0.96f, 0.66f, 0.44f, 1f)),
            "AudioImporter" => new(EditorIcons.Play, new Color4(0.94f, 0.53f, 0.65f, 1f)),
            "VideoImporter" => new(EditorIcons.Play, new Color4(0.68f, 0.62f, 0.94f, 1f)),
            "NativeFormatImporter" => new(EditorIcons.Save, new Color4(0.62f, 0.70f, 0.78f, 1f)),
            _ => Unknown
        };
}
