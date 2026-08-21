// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Ecs;
using Vixen.Core.Mathematics;
using Vixen.Editor.Ui;
using Vixen.Engine.Cameras;
using Vixen.Engine.Transforms;
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
    /// <remarks>
    ///     ⚠ <b>The blue folder every desktop has, rather than the amber outline of an open file.</b>
    ///     A folder is the single most repeated picture in the Project panel and it was the editor's
    ///     "Open" glyph tinted — a shape that means <i>the verb</i> open, used for the noun. See
    ///     <c>MaterialIcons.Folder</c> for how the gradient is faked and why it is faked.
    /// </remarks>
    public static IconArt Folder => MaterialIcons.Folder;

    /// <summary>What something nothing claims shows.</summary>
    public static IconArt Unknown { get; } = MaterialIcons.Page(new Color4(0.55f, 0.58f, 0.64f, 1f));

    /// <summary>The pictures more than one key shares.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Above the lists that name them, because a static initializer runs in source
    ///         order.</b> Below them each of these is null at the moment the list is built, and what
    ///         that looks like is a Project panel drawing nothing for half its file kinds.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A sheet of paper with a mark on it, rather than a bare glyph.</b> A grid tile is a
    ///         picture with a caption under it, and a floating cog at tile size reads as an icon that
    ///         failed to load rather than as a material. The page says "file" and the mark says which
    ///         kind, which is the arrangement every file manager arrived at independently.
    ///     </para>
    /// </remarks>
    static IconArt Texture { get; } = MaterialIcons.Page(new Color4(0.30f, 0.62f, 0.92f, 1f), MaterialIcons.Marks.Texture);

    static IconArt Scene { get; } = MaterialIcons.Page(new Color4(0.36f, 0.72f, 0.42f, 1f), MaterialIcons.Marks.Scene);

    static IconArt Model { get; } = MaterialIcons.Page(new Color4(0.67f, 0.44f, 0.86f, 1f), MaterialIcons.Marks.Model);

    static IconArt Material { get; } = MaterialIcons.Page(new Color4(0.93f, 0.55f, 0.28f, 1f), MaterialIcons.Marks.Material);

    static IconArt Audio { get; } = MaterialIcons.Page(new Color4(0.90f, 0.35f, 0.52f, 1f), MaterialIcons.Marks.Audio);

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
        new("VideoImporter", MaterialIcons.Page(new Color4(0.55f, 0.48f, 0.90f, 1f), MaterialIcons.Marks.Video)),
        new("NativeFormatImporter", MaterialIcons.Page(new Color4(0.45f, 0.55f, 0.66f, 1f), MaterialIcons.Marks.Native)),

        // ⚠ And the same pictures by extension, because a file has a name before it has a tag. A
        // project is indexed in the background: for the seconds between a scene appearing on disk and
        // the database claiming it — and for the whole life of a file no importer ever claims — the
        // tag is empty, and a Project panel that showed a generic page until an import finished would
        // be a panel that looks broken exactly while something is happening. Same instances, so the
        // two keys cannot drift into two pictures for one kind of file.
        new(".vxscene", Scene),
        new(".vxprefab", MaterialIcons.Page(new Color4(0.24f, 0.68f, 0.64f, 1f), MaterialIcons.Marks.Model)),
        new(".vxmat", Material),
        new(".vxmesh", Model),
        new(".png", Texture),
        new(".jpg", Texture),
        new(".tga", Texture),
        new(".ktx2", Texture),
        new(".dds", Texture),
        new(".wav", Audio),
        new(".ogg", Audio)
    ];

    /// <summary>The picture for the components an outliner row is mostly telling apart.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>What an entity carries rather than what it is called.</b> An outliner of forty
    ///         identical rows is one you read rather than scan, and a name is the one thing on the row
    ///         that is already text. A camera, a light and a piece of geometry are the three things a
    ///         scene is mostly made of; everything else falls back to the plain entity glyph, and a
    ///         plugin's component takes this same list's place by registering one of its own.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The entries below <c>Order 0</c> are for the <i>inspector</i> and are ordered so
    ///         that they cannot change an outliner row.</b> An icon serves both surfaces —
    ///         <c>EditorArt.Of</c> is one lookup — but the two ask different questions of it. A
    ///         foldout asks "what is this component", which every component has an answer to; a row
    ///         asks "what is this entity mostly", and there <c>LocalTransform</c> is the worst
    ///         possible answer because every entity has one. A negative order puts it last among the
    ///         things a row could have chosen, which is where the plain entity glyph already was —
    ///         and it is the same picture, so the row is unchanged either way.
    ///     </para>
    /// </remarks>
    public static IReadOnlyList<TypeIcon> Types { get; } = [
        // ⚠ The outliner's three, kept as the editor's own line glyphs in the row's own colour. A
        // row is a line of text with a picture at the front of it and reads as one thing — see this
        // type's remarks — so these follow `color`, including when the row is selected and the
        // background under them has gone dark. Everything below is for the panels where a picture is
        // a picture.
        new(typeof(Light), IconArt.Of(EditorIcons.Light), Order: 10),
        new(typeof(Camera), IconArt.Of(EditorIcons.Camera), Order: 10),
        new(typeof(PrimitiveShape), IconArt.Of(EditorIcons.Cube), Order: 10),

        new(typeof(LocalTransform), IconArt.Of(EditorIcons.Entity), Order: -100),

        // And a picture for every component the editor ships — see `MaterialIcons`. Below the three
        // above on purpose: an entity carrying a light draws the light glyph in the outliner and its
        // component foldout draws the coloured one, which is each surface getting the picture that
        // suits it.
        .. MaterialIcons.Components
    ];
}
