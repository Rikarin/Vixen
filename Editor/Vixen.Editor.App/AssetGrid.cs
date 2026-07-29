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

    /// <summary>The picture.</summary>
    public Icon Glyph { get; private set; } = null!;

    /// <summary>The name under it.</summary>
    public UiElement Caption { get; private set; } = null!;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Glyph = Part<Icon>();
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
///         ⚠ <b>Not virtualised, and capped instead.</b> A wrapping grid needs different arithmetic
///         from <c>VirtualizingPanel</c>'s row pooling — how many fit per line is a function of the
///         width — and building that control is not this panel's job. A folder with more than
///         <see cref="Limit" /> things in it shows the first of them and says how many it did not,
///         which is honest and keeps the panel responsive. A silent truncation would be the worse
///         half of both options.
///     </para>
/// </remarks>
sealed partial class AssetGrid : Control {
    /// <summary>How many tiles are drawn before the grid says it has stopped.</summary>
    /// <remarks>
    ///     Enough that no ordinary folder reaches it, and small enough that the pathological one — a
    ///     texture atlas dump, a decompiled asset bundle — does not lock the editor up.
    /// </remarks>
    public const int Limit = 400;

    readonly List<AssetTile> tiles = [];

    UiElement path = null!;
    UiElement body = null!;
    TextBlock note = null!;

    /// <inheritdoc />
    protected override string TagName => "asset-grid";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The folder being shown, or <see langword="null" /> before the first fill.</summary>
    public AssetTreeNode? Folder { get; private set; }

    /// <summary>The tiles, in order.</summary>
    public IReadOnlyList<AssetTile> Tiles => tiles;

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
        body = Part("asset-tiles");

        note = Part<TextBlock>();
        note.AddClass("asset-note");
        note.AddClass("hidden");

        // ⚠ Pointer and tap, not `ClickEvent`. A bare `Control` never raises one — `RaiseClick` is
        // `ButtonBase`'s — so a tile is selected from the press and opened from the recogniser's tap
        // count, which is exactly how `TreeView` does the same two gestures.
        AddHandler<PointerEvent>(static (element, args) => ((AssetGrid) element).Pointed(args));
        AddHandler<TapEvent>(static (element, args) => ((AssetGrid) element).Tapped(args));
    }

    /// <summary>Shows a folder's contents.</summary>
    /// <param name="folder">The folder, which the caller has already filtered.</param>
    /// <param name="describe">What each asset's importer tag is, for its glyph.</param>
    public void Show(AssetTreeNode folder, Func<AssetTreeNode, string?> describe) {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(describe);

        Folder = folder;

        Breadcrumbs(folder);
        Fill(folder, describe);
    }

    /// <summary>Marks the tiles for a set of assets as chosen and the rest as not.</summary>
    /// <param name="chosen">What is selected.</param>
    /// <remarks>
    ///     Pushed in rather than kept, for the reason the outliner's highlight is: the selection is
    ///     the project's and this is a view of it, so a grid holding its own would be a second answer
    ///     that drifts the moment anything else selects something.
    /// </remarks>
    public void Mark(IReadOnlyCollection<AssetId> chosen) {
        ArgumentNullException.ThrowIfNull(chosen);

        foreach (var tile in tiles) {
            if (tile.Node is { IsIndexed: true } asset && chosen.Contains(asset.Guid)) {
                tile.State |= Vixen.Ui.Styling.ElementState.Checked;
            } else {
                tile.State &= ~Vixen.Ui.Styling.ElementState.Checked;
            }
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

    void Fill(AssetTreeNode folder, Func<AssetTreeNode, string?> describe) {
        while (body.Children.Count > 0) {
            body.Children[^1].Remove();
        }

        tiles.Clear();

        var shown = 0;

        foreach (var child in folder.Children) {
            if (shown == Limit) {
                break;
            }

            var tile = body.Add<AssetTile>();

            tile.Node = child;
            tile.Caption.Text = child.Name;

            var thumbnail = child.IsFolder ? AssetThumbnails.Folder : AssetThumbnails.For(describe(child));

            tile.Glyph.Geometry = thumbnail.Glyph;
            tile.Glyph.SetStyle("color", Css(thumbnail.Tint));

            if (child.IsFolder) {
                tile.AddClass("folder");
            }

            tiles.Add(tile);
            shown++;
        }

        var hidden = folder.Children.Count - shown;

        if (hidden > 0) {
            note.RemoveClass("hidden");
            note.Text = $"…and {hidden} more. Narrow it with the search box.";
        } else {
            note.AddClass("hidden");
        }
    }

    /// <summary>Walks into a folder.</summary>
    public void Enter(AssetTreeNode folder) {
        ArgumentNullException.ThrowIfNull(folder);

        Navigated?.Invoke(folder);
    }

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

    /// <summary>The tile under a point, if any.</summary>
    public AssetTile? TileAt(float x, float y) {
        foreach (var tile in tiles) {
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
