// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Core;
using Vixen.Ui;
using Vixen.Ui.Controls;

namespace Vixen.Editor.App;

/// <summary>One realised tile of an <see cref="AssetGrid" />: the element, and what it is showing.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A view over a pool slot rather than the element itself, since #1406.</b> A tile used
///         to be a control of this name with four typed parts, created by the grid's
///         <c>CreateTile</c> in C#. The tile template is <c>@rows</c> markup now, and a pool makes
///         every slot by tag name — so the slot is a plain <c>&lt;asset-tile&gt;</c> element, and this
///         names its parts for a caller that wants them, which is what the grid hands out.
///     </para>
///     <para>
///         ⚠ <b>Made on each ask and not kept.</b> The slot is rebound to another item as the grid
///         scrolls, so a view held across a scroll names whatever the slot shows now while its
///         <see cref="Node" /> still says what it showed then. Ask the grid again.
///     </para>
/// </remarks>
/// <param name="element">The slot.</param>
/// <param name="node">Which asset it shows, or <see langword="null" /> for a slot showing nothing.</param>
public sealed class AssetTile(UiElement element, AssetTreeNode? node) {
    /// <summary>The slot's element, which is what is laid out, hit and styled.</summary>
    public UiElement Element { get; } = element;

    /// <summary>Which asset it shows.</summary>
    public AssetTreeNode? Node { get; } = node;

    /// <summary>The glyph, shown while there is no picture and for everything that has none.</summary>
    /// <remarks>
    ///     ⚠ <b>Both exist and one is hidden, rather than one being swapped for the other.</b> A
    ///     tile is rebound as the grid scrolls, so building an element per bind would allocate one
    ///     per scrolled row for the life of the panel — the pool exists precisely to stop that.
    /// </remarks>
    public Icon Glyph => Part<Icon>();

    /// <summary>The picture, when the asset has one.</summary>
    public Image Picture => Part<Image>();

    /// <summary>The name under it.</summary>
    public UiElement Caption => Part<AssetCaption>();

    /// <summary>The source-control mark in the corner, hidden when there is nothing to say.</summary>
    /// <remarks>
    ///     ⚠ <b>Out of flow, and the tile's own comments say why it has to be.</b> A tile is 84 px
    ///     of padding, glyph, gap and caption with nothing spare — the arithmetic is written out
    ///     beside <c>asset-caption</c> in the sheet — so a badge in the column would take its height
    ///     from the picture or the name. It is positioned against the tile, which is a containing
    ///     block because the grid already positions it absolutely.
    /// </remarks>
    public UiElement Status => Part<AssetStatus>();

    T Part<T>() where T : UiElement =>
        Element.Children.OfType<T>().FirstOrDefault()
        ?? throw new InvalidOperationException($"the tile has no {typeof(T).Name}; the template in AssetGrid.vxml changed shape");
}

/// <summary>A tile's caption, a type of its own so that markup can set its text as the C# tile did.</summary>
/// <remarks>
///     ⚠ <b>An element rather than an interpolation</b>, which would put a <c>&lt;text&gt;</c> child
///     under a plain <c>&lt;asset-caption&gt;</c>: the tile's layout and <c>AssetGridDumpTests</c>
///     both want the caption's own text, which is <c>ConsoleView</c>'s reason for its columns.
/// </remarks>
internal sealed class AssetCaption : UiElement {
    /// <inheritdoc />
    protected override string TagName => "asset-caption";
}

/// <inheritdoc cref="AssetCaption" />
internal sealed class AssetStatus : UiElement {
    /// <inheritdoc />
    protected override string TagName => "asset-status";
}

/// <summary>A folder's contents as a wrapping grid of tiles.</summary>
/// <remarks>
///     <para>
///         <b>The other half of a content browser, and it is not a prettier list.</b> A tree answers
///         "where is this" and a grid answers "what is in here" — which is the question somebody
///         asks who is looking for the crate texture and does not remember what it is called. That is
///         why a grid is a <i>folder</i> view: it shows one directory's contents and you walk into
///         the next, rather than showing a flattened project.
///     </para>
///     <para>
///         ⚠ <b>The thumbnails are type glyphs until a picture arrives, and
///         <see cref="StandardIcons" /> says why.</b> The colour is doing most of the work either
///         way — a grid of forty identical grey glyphs cannot be scanned, and scanning is what a grid
///         is for.
///     </para>
///     <para>
///         ⚠ <b>Virtualised, so a folder's size is not a number this panel has an opinion about.</b>
///         The tiles are <see cref="VirtualizingGrid" />'s pool — about sixty of them for a folder of
///         any size — which is what makes an asset dump of forty thousand files scroll rather than
///         lock the editor up. It used to draw the first four hundred and say how many it had not.
///     </para>
///     <para>
///         ⚠ <b>The tile template is markup, in <c>AssetGrid.vxml</c>, and has been since #1406</b> —
///         the last production virtualised list whose rows were made and bound by hand in C#.
///     </para>
/// </remarks>
sealed partial class AssetGrid;
