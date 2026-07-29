// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Ui.Controls;

/// <summary>A wrapping grid of which only what is on screen exists as elements.</summary>
/// <remarks>
///     <para>
///         <b><see cref="VirtualizingPanel" />'s bargain in two dimensions.</b> The items are the
///         caller's and the tiles are a pool; this control knows how many items there are and how big
///         a tile is, and nothing about what an item <i>is</i>. Forty thousand of them is forty
///         thousand of the caller's own objects and about sixty elements.
///     </para>
///     <para>
///         ⚠ <b>Its own control rather than a mode on the panel, and the reason is one number.</b> A
///         list's row occupies the full width, so item <c>n</c> is at <c>n × height</c> and nothing
///         else is needed. A grid's item is at a position that depends on <see cref="Columns" />,
///         which is a function of the <i>measured</i> width — so the same resize that changes the
///         viewport also changes which item is where, and the content height with it. Folding that
///         into a control whose entire premise is "row <c>n</c> is at <c>n × height</c>" would make
///         every line of it conditional.
///     </para>
///     <para>
///         ⚠ <b>Tiles are a fixed size, from <c>--tile-width</c> and <c>--tile-height</c>.</b> The
///         same reason a row has a fixed height: knowing where item 40 000 is without having measured
///         the 39 999 before it is what makes this arithmetic instead of a walk. A grid of tiles that
///         sized themselves to their captions would also be one whose columns moved as you typed in
///         a search box, which is a grid you cannot aim at.
///     </para>
///     <para>
///         ⚠ <b>Nothing has to call <see cref="Realise" />.</b> It runs on
///         <see cref="UiDocument.LayoutFinished" />, which is the only place that knows how wide the
///         viewport ended up — and for a grid that matters more than for a list, because the width is
///         what decides the layout rather than merely how much of it is visible.
///     </para>
/// </remarks>
public sealed partial class VirtualizingGrid : Control {
    readonly List<UiElement> tiles = [];

    Action<UiDocument>? settle;
    int tileWidthId;
    int tileHeightId;
    int first;
    int columns = 1;

    /// <summary>How many lines of tiles are realised above and below the viewport.</summary>
    /// <inheritdoc cref="VirtualizingPanel.Overscan" select="remarks" />
    public const int Overscan = VirtualizingPanel.Overscan;

    /// <inheritdoc />
    protected override string TagName => "virtualizing-grid";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The scroller the tiles live in.</summary>
    public ScrollView Scroller { get; private set; } = null!;

    /// <summary>The tiles that exist as elements, in pool order rather than in item order.</summary>
    /// <inheritdoc cref="VirtualizingPanel.Rows" select="remarks" />
    public IReadOnlyList<UiElement> Tiles => tiles;

    /// <summary>Which item the first tile of the pool is showing.</summary>
    public int FirstItem => first;

    /// <summary>How many tiles fit across, as of the last realise.</summary>
    /// <remarks>
    ///     At least one, however narrow the panel gets. A grid that computed zero columns would
    ///     divide by it, and a single column that overflows is a readable answer to a panel dragged
    ///     narrower than one tile.
    /// </remarks>
    public int Columns => columns;

    /// <summary>How wide a tile is, from <c>--tile-width</c>.</summary>
    public float TileWidth => Document.LengthOf(Style, tileWidthId) ?? 80f;

    /// <summary>How tall a tile is, from <c>--tile-height</c>.</summary>
    public float TileHeight => Document.LengthOf(Style, tileHeightId) ?? 80f;

    /// <summary>How many items there are.</summary>
    /// <inheritdoc cref="VirtualizingPanel.Count" select="remarks" />
    [UiProperty(Changed = nameof(OnCountChanged))]
    public partial int Count { get; set; }

    /// <summary>Makes a tile element. Called only when the pool has to grow.</summary>
    /// <inheritdoc cref="VirtualizingPanel.CreateRow" select="remarks" />
    public Func<VirtualizingGrid, UiElement>? CreateTile { get; set; }

    /// <summary>Puts an item's data on a tile.</summary>
    /// <inheritdoc cref="VirtualizingPanel.BindRow" select="remarks" />
    public Action<UiElement, int>? BindTile { get; set; }

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        tileWidthId = Document.Styles.Properties.Intern("--tile-width");
        tileHeightId = Document.Styles.Properties.Intern("--tile-height");

        Scroller = Part<ScrollView>();
        Scroller.Content.AddClass("virtual-grid");

        settle = _ => Realise();
        Document.LayoutFinished += settle;
    }

    /// <inheritdoc />
    protected override void OnRemoved() {
        if (settle is not null) {
            Document.LayoutFinished -= settle;
            settle = null;
        }

        base.OnRemoved();
    }

    /// <summary>The element showing an item, or null if it is not realised.</summary>
    /// <param name="item">The item's index.</param>
    public UiElement? TileOf(int item) {
        var index = item - first;

        return index >= 0 && index < tiles.Count && !tiles[index].HasClass("parked") ? tiles[index] : null;
    }

    /// <summary>Scrolls until an item is inside the viewport.</summary>
    /// <param name="item">The item's index.</param>
    /// <inheritdoc cref="VirtualizingPanel.ScrollIntoView" select="remarks" />
    public void ScrollIntoView(int item) {
        var height = TileHeight;

        if (height <= 0f || Count <= 0) {
            return;
        }

        var line = Math.Clamp(item, 0, Count - 1) / Math.Max(columns, 1);
        var top = line * height;
        var viewport = Scroller.Height;

        if (top < Scroller.ScrollTop) {
            Scroller.ScrollTop = top;
        } else if (top + height > Scroller.ScrollTop + viewport) {
            Scroller.ScrollTop = top + height - viewport;
        }
    }

    /// <summary>Makes sure there is a tile for every visible one, and binds them.</summary>
    public void Realise() {
        var width = TileWidth;
        var height = TileHeight;

        if (width <= 0f || height <= 0f || CreateTile is null) {
            return;
        }

        // ⚠ The scroller's own width, not this control's. The difference is the vertical scrollbar,
        // and a grid that counted columns against the outer width fits one too many the moment the
        // content is tall enough to need one — which is every grid that needed virtualising.
        columns = Math.Max(1, (int) MathF.Floor(Math.Max(Scroller.Width, 0f) / width));

        var lines = (Count + columns - 1) / columns;

        Scroller.Content.SetStyle("height", Px(lines * height));

        var visible = (int) MathF.Ceiling(Math.Max(Scroller.Height, 0f) / height) + (Overscan * 2) + 1;
        var capacity = Math.Min(Count, visible * columns);

        // ⚠ Clamped in *lines* and not in items, which is the difference between this and a list.
        // Clamping to `Count - capacity` — what a list does — lands mid-line, and snapping that back
        // to a line boundary moves the window earlier than the end: the last few items then fall
        // past the pool and cannot be reached however far the grid is scrolled. Bounding the first
        // line by `lines - visible` instead makes `first + capacity` reach `Count` exactly.
        var firstLine = Math.Clamp(
            (int) MathF.Floor(Scroller.ScrollTop / height) - Overscan,
            0,
            Math.Max(0, lines - visible)
        );

        // ⚠ And it starts a line. A pool beginning mid-line would put item n at column n % columns
        // of a different line every time the offset crossed a tile, so the grid would shuffle
        // sideways as it scrolled.
        first = firstLine * columns;

        while (tiles.Count < capacity) {
            tiles.Add(CreateTile(this));
        }

        for (var index = 0; index < tiles.Count; index++) {
            var tile = tiles[index];
            var item = first + index;

            if (index >= capacity || item >= Count) {
                tile.AddClass("parked");
                continue;
            }

            tile.RemoveClass("parked");
            tile.SetStyle("left", Px(item % columns * width));
            tile.SetStyle("top", Px(item / columns * height));
            tile.SetStyle("width", Px(width));
            tile.SetStyle("height", Px(height));

            BindTile?.Invoke(tile, item);
        }
    }

    void OnCountChanged(int previous, int current) {
        _ = previous;
        _ = current;

        // The same reason the panel realises here: the scroller clamps against a content height that
        // has not been laid out yet, so a grid that shrank leaves the offset past its own end until
        // the next pass.
        Realise();
        Document.Invalidate();
    }

    static string Px(float value) => value.ToString("0.##", CultureInfo.InvariantCulture) + "px";
}
