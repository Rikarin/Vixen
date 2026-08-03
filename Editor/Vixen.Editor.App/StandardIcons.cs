// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.Ui;
using Vixen.Engine.Cameras;
using Vixen.Rendering.Ecs;
using Vixen.Ui.Controls;

namespace Vixen.Editor.App;

/// <summary>The pictures the editor ships, as contributions rather than as a switch.</summary>
/// <remarks>
///     <para>
///         <b>Doc 36 § D6, and what replaced <c>AssetThumbnails.For</c>.</b> That was a
///         <c>switch</c> over importer names in the application, which is F3's shape one organ along:
///         a plugin's asset type could not appear in it, so a contributed asset was visibly
///         second-class in the panel that shows it. These go into <c>EditorRegistry</c> at start-up
///         and every surface reads them from there, so a built-in picture and a plugin's are the same
///         thing to everything downstream.
///     </para>
///     <para>
///         ⚠ <b>A glyph per kind, not a picture of the asset, and the gap is worth restating.</b> A
///         real thumbnail means decoding the source image and uploading it as a GPU texture:
///         <c>Image.Texture</c> takes a number handed out by <c>UiRenderer.RegisterImage</c>, which
///         needs a device — and the application deliberately has none, the host does. What is here is
///         the fallback every browser has anyway, and <c>ThumbnailCache</c> is what puts a picture over
///         it when there is one.
///     </para>
///     <para>
///         <b>The colour is doing as much work as the shape.</b> A grid of forty identical grey glyphs
///         is a grid nobody can scan; one where textures are one colour and scenes are another can be
///         read at a glance, which is most of what a grid view is for.
///     </para>
///     <para>
///         ⚠ <b>The type icons are <c>IconPaint.Foreground</c> and the asset icons are literals, and
///         that is not an inconsistency.</b> An outliner row is a line of text with a glyph at the
///         front and reads as one thing; a grid tile is a picture with a caption under it. The first
///         wants the row's colour — including when the row is selected, where a literal would stop
///         contrasting with the highlight — and the second wants to be sortable by eye.
///     </para>
/// </remarks>
static class StandardIcons {
    /// <summary>What a folder shows. Not a registration: nothing keys it.</summary>
    public static IconArt Folder { get; } = IconArt.Of(EditorIcons.Open, new Color4(0.85f, 0.72f, 0.38f, 1f));

    /// <summary>What something nothing claims shows.</summary>
    public static IconArt Unknown { get; } = IconArt.Of(EditorIcons.New, new Color4(0.55f, 0.58f, 0.64f, 1f));

    /// <summary>The four pictures more than one key shares.</summary>
    /// <remarks>
    ///     ⚠ <b>Above the lists that name them, because a static initializer runs in source order.</b>
    ///     Below them each of these is null at the moment the list is built, and what that looks like
    ///     is a Project panel drawing nothing for half its file kinds.
    /// </remarks>
    static IconArt Texture { get; } = IconArt.Of(EditorIcons.Grid, new Color4(0.44f, 0.72f, 0.94f, 1f));

    static IconArt Scene { get; } = IconArt.Of(EditorIcons.World, new Color4(0.55f, 0.80f, 0.52f, 1f));

    static IconArt Model { get; } = IconArt.Of(EditorIcons.Scale, new Color4(0.83f, 0.62f, 0.94f, 1f));

    static IconArt Material { get; } = IconArt.Of(EditorIcons.Settings, new Color4(0.96f, 0.66f, 0.44f, 1f));

    static IconArt Audio { get; } = IconArt.Of(EditorIcons.Play, new Color4(0.94f, 0.53f, 0.65f, 1f));

    /// <summary>The picture for each importer the editor ships.</summary>
    /// <remarks>
    ///     ⚠ <b>Keyed on the importer tag rather than on the extension.</b> The tag is what the sidecar
    ///     records and what the browser's type filter offers, so the two cannot disagree about what a
    ///     file is — and a <c>.png</c> some other importer claimed gets that importer's picture rather
    ///     than being mistaken for a texture.
    /// </remarks>
    public static IReadOnlyList<AssetIcon> Assets { get; } = [
        new("TextureImporter", Texture),
        new("SceneImporter", Scene),
        new("ModelImporter", Model),
        new("MaterialImporter", Material),
        new("AudioImporter", Audio),
        new("VideoImporter", IconArt.Of(EditorIcons.Play, new Color4(0.68f, 0.62f, 0.94f, 1f))),
        new("NativeFormatImporter", IconArt.Of(EditorIcons.Save, new Color4(0.62f, 0.70f, 0.78f, 1f))),

        // ⚠ And the same pictures by extension, because a file has a name before it has a tag. A
        // project is indexed in the background: for the seconds between a scene appearing on disk and
        // the database claiming it — and for the whole life of a file no importer ever claims — the
        // tag is empty, and a Project panel that showed a generic page until an import finished would
        // be a panel that looks broken exactly while something is happening. Same instances, so the
        // two keys cannot drift into two pictures for one kind of file.
        new(".vxscene", Scene),
        new(".vxprefab", IconArt.Of(EditorIcons.Cube, new Color4(0.45f, 0.78f, 0.74f, 1f))),
        new(".vxmat", Material),
        new(".vxmesh", Model),
        new(".png", Texture),
        new(".jpg", Texture),
        new(".tga", Texture),
        new(".ktx2", Texture),
        new(".wav", Audio),
        new(".ogg", Audio)
    ];

    /// <summary>The picture for the components an outliner row is mostly telling apart.</summary>
    /// <remarks>
    ///     ⚠ <b>What an entity carries rather than what it is called.</b> An outliner of forty
    ///     identical rows is one you read rather than scan, and a name is the one thing on the row that
    ///     is already text. A camera, a light and a piece of geometry are the three things a scene is
    ///     mostly made of; everything else falls back to the plain entity glyph, and a plugin's
    ///     component takes this same list's place by registering one of its own.
    /// </remarks>
    public static IReadOnlyList<TypeIcon> Types { get; } = [
        new(typeof(Light), IconArt.Of(EditorIcons.Light)),
        new(typeof(Camera), IconArt.Of(EditorIcons.Camera)),
        new(typeof(PrimitiveShape), IconArt.Of(EditorIcons.Cube))
    ];
}
