// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Core;
using Vixen.Ui;
using Vixen.Ui.Controls;

namespace Vixen.Editor.App;

/// <summary>One asset as a tile: a glyph, a name, and what it stands for.</summary>
public sealed partial class AssetTile : Control {
    /// <inheritdoc />
    protected override string TagName => "asset-tile";

    /// <inheritdoc />
    protected override bool AcceptsFocus => true;

    /// <summary>Which asset it shows.</summary>
    public AssetTreeNode? Node { get; internal set; }

    /// <summary>The glyph, shown while there is no picture and for everything that has none.</summary>
    public Icon Glyph { get; private set; } = null!;

    /// <summary>The picture, when the asset has one.</summary>
    /// <remarks>
    ///     ⚠ <b>Both exist and one is hidden, rather than one being swapped for the other.</b> A
    ///     tile is rebound as the grid scrolls, so building an element per bind would allocate one
    ///     per scrolled row for the life of the panel — the pool exists precisely to stop that.
    /// </remarks>
    public Image Picture { get; private set; } = null!;

    /// <summary>The name under it.</summary>
    public UiElement Caption { get; private set; } = null!;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Glyph = Part<Icon>();

        Picture = Part<Image>();
        Picture.AddClass("hidden");

        Caption = Part("asset-caption");
    }
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
///         ⚠ <b>The thumbnails are type glyphs, not pictures of the assets, and
///         <see cref="AssetThumbnails" /> says why.</b> A picture needs a decode and a GPU upload,
///         which needs a device the application deliberately does not have. The colour is doing most
///         of the work either way — a grid of forty identical grey glyphs cannot be scanned, and
///         scanning is what a grid is for.
///     </para>
///     <para>
///         ⚠ <b>Virtualised, so a folder's size is not a number this panel has an opinion about.</b>
///         The tiles are <see cref="VirtualizingGrid" />'s pool — about sixty of them for a folder of
///         any size — which is what makes an asset dump of forty thousand files scroll rather than
///         lock the editor up. It used to draw the first four hundred and say how many it had not.
///     </para>
/// </remarks>
sealed partial class AssetGrid : Control {
    readonly List<AssetTreeNode> items = [];

    UiElement path = null!;
    VirtualizingGrid body = null!;

    /// <inheritdoc />
    protected override string TagName => "asset-grid";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The folder being shown, or <see langword="null" /> before the first fill.</summary>
    public AssetTreeNode? Folder { get; private set; }

    /// <summary>What the grid is showing, in order — the items, not the pooled elements.</summary>
    /// <remarks>
    ///     ⚠ <b>The items rather than the tiles, and the difference is the point of virtualising.</b>
    ///     A caller asking "what is in this folder" wants all of it; a caller asking "which element
    ///     shows the third one" is asking about a pool that slides, and <see cref="TileOf" /> is that
    ///     question.
    /// </remarks>
    public IReadOnlyList<AssetTreeNode> Items => items;

    /// <summary>The tiles that exist as elements, in pool order.</summary>
    public IReadOnlyList<AssetTile> Tiles => [.. body.Tiles.OfType<AssetTile>().Where(tile => !tile.HasClass("parked"))];

    /// <summary>The element showing an item, if it is realised.</summary>
    /// <param name="item">Its index in <see cref="Items" />.</param>
    /// <returns>The tile, or <see langword="null" /> when it is scrolled away.</returns>
    public AssetTile? TileOf(int item) => body.TileOf(item) as AssetTile;

    /// <summary>Scrolls until an item is on screen, so that it can be clicked.</summary>
    /// <param name="item">Its index in <see cref="Items" />.</param>
    public void ScrollIntoView(int item) {
        body.ScrollIntoView(item);
        body.Realise();
    }

    /// <summary>Raised when a tile is chosen — a click.</summary>
    public event Action<AssetTreeNode>? Selected;

    /// <summary>Raised when a tile is opened — a double-click, or Enter.</summary>
    /// <remarks>
    ///     ⚠ <b>A folder is not reported.</b> Opening one means walking into it, which is this
    ///     control's own business; a browser that raised "activated" for a folder would make the
    ///     application decide whether a double-click navigates or opens an editor, which is a
    ///     question about the grid.
    /// </remarks>
    public event Action<AssetTreeNode>? Activated;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        path = Part("asset-path");

        body = Part<VirtualizingGrid>();
        body.AddClass("asset-tiles");
        body.CreateTile = static grid => grid.Scroller.Content.Add<AssetTile>();
        body.BindTile = (tile, item) => Bind((AssetTile) tile, item);

        // ⚠ Pointer and tap, not `ClickEvent`. A bare `Control` never raises one — `RaiseClick` is
        // `ButtonBase`'s — so a tile is selected from the press and opened from the recogniser's tap
        // count, which is exactly how `TreeView` does the same two gestures.
        AddHandler<PointerEvent>(static (element, args) => ((AssetGrid) element).Pointed(args));
        AddHandler<TapEvent>(static (element, args) => ((AssetGrid) element).Tapped(args));
        AddHandler<DragEvent>(static (element, args) => ((AssetGrid) element).Dragged(args));
    }

    /// <summary>What each asset's importer tag is, for its glyph.</summary>
    public Func<AssetTreeNode, string?> Describe { get; set; } = static _ => null;

    /// <summary>The picture for an asset, if one has been made.</summary>
    /// <remarks>
    ///     Asked on every bind rather than pushed, because a thumbnail arrives whenever its decode
    ///     finishes — which is usually a few frames after the tile that wanted it was drawn.
    /// </remarks>
    public Func<AssetTreeNode, ulong> Picture { get; set; } = static _ => 0;

    /// <summary>Shows a folder's contents.</summary>
    /// <param name="folder">The folder, which the caller has already filtered.</param>
    public void Show(AssetTreeNode folder) {
        ArgumentNullException.ThrowIfNull(folder);

        Folder = folder;

        items.Clear();
        items.AddRange(folder.Children);

        Breadcrumbs(folder);

        // ⚠ Set before the realise rather than after. `Count` writes the content height and rebinds,
        // and a realise against the previous count would place this folder's tiles at the last
        // folder's positions for a frame.
        body.Count = items.Count;
        body.Realise();
    }

    /// <summary>Rebinds the realised tiles, for a picture that arrived after they were drawn.</summary>
    public void Refresh() => body.Realise();

    /// <summary>How big a tile is.</summary>
    /// <param name="Name">What the dropdown calls it, and what a preferences file holds.</param>
    /// <param name="Width">How wide, in pixels.</param>
    /// <param name="Height">How tall. Taller than it is wide, because the caption is under the glyph.</param>
    /// <param name="Glyph">How big the icon or the thumbnail inside it is.</param>
    public readonly record struct TileScale(string Name, float Width, float Height, float Glyph);

    /// <summary>The sizes on offer, smallest first.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Four steps rather than a slider, and each is a set of numbers that agree.</b> A
    ///         tile is a width, a height and a glyph size, and a free number would let somebody ask
    ///         for a 40-pixel tile holding a 40-pixel glyph and no room for a name. Four is what every
    ///         file manager offers and is enough — the question people actually ask is "more at once"
    ///         or "big enough to recognise", not "88 pixels".
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The height leaves two lines for the caption at every step.</b> That is what makes
    ///         the grid scannable at the small end: a tile whose name is clipped to one line is a
    ///         column of <c>T_Crate_…</c>, which is a grid of identical rows.
    ///     </para>
    /// </remarks>
    public static IReadOnlyList<TileScale> TileSizes { get; } = [
        new("Small", 64f, 68f, 28f),
        new("Medium", 82f, 84f, 40f),
        new("Large", 112f, 116f, 60f),
        new("Huge", 152f, 156f, 88f)
    ];

    /// <summary>What a grid shows when nothing has chosen.</summary>
    public const string DefaultTileSize = "Medium";

    /// <summary>Which of <see cref="TileSizes" /> the tiles are drawn at, by name.</summary>
    /// <remarks>
    ///     ⚠ <b>Written as custom properties on the grid rather than as a class.</b>
    ///     <c>VirtualizingGrid</c> reads <c>--tile-width</c> and <c>--tile-height</c> to work out how
    ///     many fit across and where item 40 000 is — see its remarks — so the size has to be a
    ///     number it can read without measuring an element. The glyph size goes the same way so that
    ///     the theme keeps deciding what a tile looks like.
    /// </remarks>
    public string TileSize {
        get;

        set {
            var scale = TileSizes.FirstOrDefault(
                candidate => string.Equals(candidate.Name, value, StringComparison.Ordinal)
            );

            // An unknown name — a preferences file from a version with different steps — falls back
            // rather than leaving the grid with no size at all.
            if (scale.Name is null) {
                scale = TileSizes.First(candidate => candidate.Name == DefaultTileSize);
            }

            field = scale.Name;

            body.SetStyle("--tile-width", Px(scale.Width));
            body.SetStyle("--tile-height", Px(scale.Height));
            body.SetStyle("--tile-glyph", Px(scale.Glyph));

            // ⚠ Realised rather than left to the next layout pass. `Realise` is what writes each
            // tile's own width and height, and the pass that would run it is the one that has just
            // been invalidated — so without this the grid keeps the old spacing until something else
            // happens to scroll it.
            body.Realise();
        }
    } = DefaultTileSize;

    static string Px(float value) =>
        value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "px";

    /// <summary>Marks the tiles for a set of assets as chosen and the rest as not.</summary>
    /// <param name="chosen">What is selected.</param>
    /// <remarks>
    ///     <para>
    ///         Pushed in rather than kept, for the reason the outliner's highlight is: the selection
    ///         is the project's and this is a view of it, so a grid holding its own would be a second
    ///         answer that drifts the moment anything else selects something.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Kept, because a pooled tile is rebound as it scrolls.</b> Marking the realised
    ///         tiles alone would lose the highlight the moment a selected item scrolled off and back,
    ///         so the set is held and applied again on every bind.
    ///     </para>
    /// </remarks>
    public void Mark(IReadOnlyCollection<AssetId> chosen) {
        ArgumentNullException.ThrowIfNull(chosen);

        marked.Clear();

        foreach (var asset in chosen) {
            marked.Add(asset);
        }

        foreach (var tile in body.Tiles.OfType<AssetTile>()) {
            Restate(tile);
        }
    }

    readonly HashSet<AssetId> marked = [];

    void Restate(AssetTile tile) {
        if (tile.Node is { IsIndexed: true } asset && marked.Contains(asset.Guid)) {
            tile.State |= Vixen.Ui.Styling.ElementState.Checked;
        } else {
            tile.State &= ~Vixen.Ui.Styling.ElementState.Checked;
        }
    }

    /// <summary>The trail of folders above this one, each of which can be gone back to.</summary>
    void Breadcrumbs(AssetTreeNode folder) {
        while (path.Children.Count > 0) {
            path.Children[^1].Remove();
        }

        List<AssetTreeNode> trail = [];

        // ⚠ Walked from the root down rather than by splitting the path string. A folder's name and
        // its place in the tree are the tree's answers, and a browser that recomputed them from text
        // would disagree with it about a folder with a slash in its name.
        for (var current = Folder; current is not null; current = Containing(current)) {
            trail.Insert(0, current);
        }

        foreach (var step in trail) {
            var crumb = path.Add<Button>();
            var target = step;

            crumb.Label = step.Name;
            crumb.Variant = ControlVariant.Subtle;
            crumb.Size = ControlSize.Small;
            crumb.AddClass("asset-crumb");
            crumb.Clicked += _ => Enter(target);
        }
    }

    /// <summary>Puts an item on a pooled tile.</summary>
    void Bind(AssetTile tile, int item) {
        if (item < 0 || item >= items.Count) {
            return;
        }

        var node = items[item];

        tile.Node = node;
        tile.Caption.Text = node.Name;

        var thumbnail = node.IsFolder ? AssetThumbnails.Folder : AssetThumbnails.For(Describe(node));

        tile.Glyph.Geometry = thumbnail.Glyph;
        tile.Glyph.SetStyle("color", Css(thumbnail.Tint));

        // ⚠ The glyph stays until there is a picture, and goes the moment there is one. A tile that
        // showed neither while a decode was in flight would flicker empty through every scroll.
        var picture = node.IsFolder ? 0 : Picture(node);

        tile.Picture.Texture = picture;

        if (picture == 0) {
            tile.Picture.AddClass("hidden");
            tile.Glyph.RemoveClass("hidden");
        } else {
            tile.Picture.RemoveClass("hidden");
            tile.Glyph.AddClass("hidden");
        }

        if (node.IsFolder) {
            tile.AddClass("folder");
        } else {
            tile.RemoveClass("folder");
        }

        Restate(tile);
    }

    /// <summary>Walks into a folder.</summary>
    public void Enter(AssetTreeNode folder) {
        ArgumentNullException.ThrowIfNull(folder);

        Navigated?.Invoke(folder);
    }

    /// <summary>Raised when a drag started on a tile is released outside the grid.</summary>
    /// <inheritdoc cref="Activated" select="remarks" />
    public event Action<float, float>? DroppedOutside;

    /// <summary>Raised while a drag started on a tile is somewhere outside the grid.</summary>
    /// <remarks>
    ///     ⚠ <b>The moves as well as the release, because a drop the user cannot aim is a drop they
    ///     get wrong.</b> An inspector row is twenty pixels tall and the field within it narrower
    ///     still; without something lighting up under the pointer, assigning to the right member is
    ///     guesswork that is only found out about afterwards. Reported outside the grid only — a drag
    ///     within it is the grid's own business.
    /// </remarks>
    public event Action<float, float>? DraggedOutside;

    /// <summary>Raised when a drag ends, however it ends and wherever it ended.</summary>
    /// <remarks>
    ///     ⚠ <b>Cancelled as well as completed, and that is the whole reason it is separate from the
    ///     two above.</b> A window losing focus mid-drag produces no release, so a host holding "a
    ///     gesture is in flight" off the pointer alone would hold it for the rest of the session —
    ///     and what that suspends is the inspector following the selection.
    /// </remarks>
    public event Action? DragEnded;

    /// <summary>Raised when the grid should show a different folder.</summary>
    /// <remarks>
    ///     Out rather than done here, because the folder the grid shows has to be the <i>filtered</i>
    ///     one — the search box and the type dropdown decide what is in it — and the filtering is the
    ///     browser's.
    /// </remarks>
    public event Action<AssetTreeNode>? Navigated;

    /// <summary>What contains a node, for the breadcrumbs.</summary>
    /// <remarks>
    ///     Supplied rather than worked out here: a folder's place is the <i>tree's</i> answer, and a
    ///     grid that recomputed it from the path string would disagree with the tree about a folder
    ///     with a slash in its name.
    /// </remarks>
    public Func<AssetTreeNode, AssetTreeNode?> Containing { get; set; } = static _ => null;

    void Pointed(PointerEvent args) {
        if (args.Action != PointerAction.Pressed || TileAt(args.X, args.Y) is not { Node: { } node }) {
            return;
        }

        Selected?.Invoke(node);
    }

    void Tapped(TapEvent args) {
        if (args.Count != 2 || TileAt(args.X, args.Y) is not { Node: { } node }) {
            return;
        }

        if (node.IsFolder) {
            Enter(node);
        } else {
            Activated?.Invoke(node);
        }

        // ⚠ The run ends here, and a grid needs this more than a list does. Walking into a folder
        // puts a *different* tile under a pointer that has not moved, so without this the next
        // double-click arrives as taps three and four and opens nothing.
        Document.Gestures.EndTapRun();

        args.Handled = true;
    }

    void Dragged(DragEvent args) {
        var bounds = Bounds;

        var inside = args.X >= bounds.X
            && args.X < bounds.X + bounds.Width
            && args.Y >= bounds.Y
            && args.Y < bounds.Y + bounds.Height;

        if (args.Stage is DragStage.Completed or DragStage.Cancelled) {
            DragEnded?.Invoke();
        }

        switch (args.Stage) {
            case DragStage.Completed when !inside:
                DroppedOutside?.Invoke(args.X, args.Y);
                break;

            // ⚠ Started as well as Moved. A drag that crosses the panel edge in one motion — which is
            // every drag that starts near it — has its first event outside already, and a hover that
            // only began on the second would flicker on for the first field the pointer crossed.
            case DragStage.Started or DragStage.Moved when !inside:
                DraggedOutside?.Invoke(args.X, args.Y);
                break;

            // ⚠ Coming back inside, and being cancelled, both have to reach the host — otherwise
            // whatever it lit up stays lit for the rest of the session. Both are reported as a move
            // to nowhere, which is what they are.
            case DragStage.Started or DragStage.Moved or DragStage.Cancelled:
                DraggedOutside?.Invoke(float.NaN, float.NaN);
                break;

            default:
                break;
        }
    }

    /// <summary>The tile under a point, if any.</summary>
    public AssetTile? TileAt(float x, float y) {
        foreach (var tile in body.Tiles.OfType<AssetTile>()) {
            if (tile.HasClass("parked")) {
                continue;
            }

            var bounds = tile.Bounds;

            if (x >= bounds.X && x < bounds.X + bounds.Width && y >= bounds.Y && y < bounds.Y + bounds.Height) {
                return tile;
            }
        }

        return null;
    }

    static string Css(Vixen.Core.Mathematics.Color4 colour) =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"rgba({(int) (colour.R * 255f)}, {(int) (colour.G * 255f)}, {(int) (colour.B * 255f)}, {colour.A:0.##})"
        );
}
