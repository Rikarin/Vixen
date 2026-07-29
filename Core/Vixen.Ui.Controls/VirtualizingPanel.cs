// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Ui.Controls;

/// <summary>A scrolling list of which only what is on screen exists as elements.</summary>
/// <remarks>
///     <para>
///         <b>The items are the caller's and the rows are a pool.</b> This control knows how many
///         items there are and how tall a row is; it does not know what an item <i>is</i>. A hundred
///         thousand of them is a hundred thousand of the caller's own objects and about thirty
///         elements, rebound as the view scrolls — which is doc 09's claim about a data grid, made
///         once here instead of again in each control that wants it.
///     </para>
///     <para>
///         ⚠ <b>Rows are positioned absolutely, at a fixed height.</b> Virtualisation needs to know
///         where row 40 000 is without having measured the 39 999 above it, and a fixed height makes
///         that arithmetic instead of a walk. Variable heights need a running-sum index maintained as
///         things change size; that is a different control and is owed rather than approximated.
///     </para>
///     <para>
///         ⚠ <b>Nothing has to call <see cref="Realise" />.</b> It runs on
///         <see cref="UiDocument.LayoutFinished" />, which is the only place that knows how tall the
///         viewport ended up — so a panel resized without being scrolled realises against the size it
///         actually has. That is the gap every explicit <c>Refresh()</c> in this library existed for,
///         and the reason the callback was built.
///     </para>
/// </remarks>
public sealed partial class VirtualizingPanel : Control {
    readonly List<UiElement> rows = [];

    Action<UiDocument>? settle;
    int rowHeightId;
    int first;

    /// <summary>How many rows are realised above and below the viewport.</summary>
    /// <remarks>
    ///     ⚠ <b>Not zero.</b> A pool sized exactly to the viewport has to rebind on every pixel of
    ///     scroll, and the row entering at the bottom is created in the frame it is first drawn — so
    ///     the first frame of a flick shows a gap. Two rows of slack costs two elements.
    /// </remarks>
    public const int Overscan = 2;

    /// <inheritdoc />
    protected override string TagName => "virtualizing-panel";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The scroller the rows live in.</summary>
    public ScrollView Scroller { get; private set; } = null!;

    /// <summary>The rows that exist as elements, in pool order rather than in item order.</summary>
    /// <remarks>
    ///     ⚠ Pool order. Row <c>0</c> shows item <see cref="FirstItem" />, not item zero — the pool is
    ///     a window that slides, and a caller looking for the element showing a particular item asks
    ///     <see cref="RowOf" /> rather than indexing this.
    /// </remarks>
    public IReadOnlyList<UiElement> Rows => rows;

    /// <summary>Which item the first row of the pool is showing.</summary>
    public int FirstItem => first;

    /// <summary>How tall a row is, from <c>--row-height</c>.</summary>
    public float RowHeight => Document.LengthOf(Style, rowHeightId) ?? 22f;

    /// <summary>How many items there are.</summary>
    /// <remarks>
    ///     The only thing this control knows about the caller's data. Setting it re-measures the
    ///     scrollable height and re-binds, so a list that grew by one does not need a second call.
    /// </remarks>
    [UiProperty(Changed = nameof(OnCountChanged))]
    public partial int Count { get; set; }

    /// <summary>Makes a row element. Called only when the pool has to grow.</summary>
    /// <remarks>
    ///     ⚠ <b>The pool only ever grows.</b> A panel that was once tall keeps the rows it needed;
    ///     shrinking it would mean removing elements during a scroll, which is the allocation this
    ///     whole arrangement exists to avoid. Surplus rows are parked with a class the theme hides.
    /// </remarks>
    public Func<VirtualizingPanel, UiElement>? CreateRow { get; set; }

    /// <summary>Puts an item's data on a row. Called whenever a row starts showing a different item.</summary>
    /// <remarks>
    ///     ⚠ <b>Called for a row that is already showing that item, too.</b> Binding is not assumed to
    ///     be expensive and the alternative is remembering what each row was showing and trusting it —
    ///     which goes wrong the moment the caller's data changes underneath without the index doing.
    ///     A binder that is costly should compare and return early itself.
    /// </remarks>
    public Action<UiElement, int>? BindRow { get; set; }

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        rowHeightId = Document.Styles.Properties.Intern("--row-height");

        Scroller = Part<ScrollView>();
        Scroller.Content.AddClass("virtual-content");

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
    /// <remarks>
    ///     Null is the ordinary answer for anything scrolled well off screen, and a caller that wants
    ///     to reach a row — to focus it, to scroll it into view — asks
    ///     <see cref="ScrollIntoView(int)" /> first.
    /// </remarks>
    public UiElement? RowOf(int item) {
        var index = item - first;
        return index >= 0 && index < rows.Count && !rows[index].HasClass("parked") ? rows[index] : null;
    }

    /// <summary>Scrolls until an item is inside the viewport.</summary>
    /// <param name="item">The item's index.</param>
    /// <remarks>
    ///     ⚠ <b>Arithmetic rather than a search for the element.</b> The whole point of virtualisation
    ///     is that the thing being scrolled to usually does not exist yet — asking
    ///     <c>ScrollView.ScrollIntoView</c> for its element would be asking about an element that is
    ///     parked at the top of the list or absent entirely.
    /// </remarks>
    public void ScrollIntoView(int item) {
        var rowHeight = RowHeight;

        if (rowHeight <= 0f || Count <= 0) {
            return;
        }

        var index = Math.Clamp(item, 0, Count - 1);
        var top = index * rowHeight;
        var viewport = Scroller.Height;

        if (top < Scroller.ScrollTop) {
            Scroller.ScrollTop = top;
        } else if (top + rowHeight > Scroller.ScrollTop + viewport) {
            Scroller.ScrollTop = top + rowHeight - viewport;
        }
    }

    /// <summary>Makes sure there is a row for every visible line of the viewport, and binds them.</summary>
    /// <remarks>
    ///     Public and idempotent, for a caller that has just changed its data and wants to read a row
    ///     before the next pass. Nothing is obliged to call it.
    /// </remarks>
    public void Realise() {
        var rowHeight = RowHeight;

        if (rowHeight <= 0f || CreateRow is null) {
            return;
        }

        Scroller.Content.SetStyle(
            "height",
            (Count * rowHeight).ToString("0.##", CultureInfo.InvariantCulture) + "px"
        );

        var capacity = Math.Min(Count, (int) MathF.Ceiling(Scroller.Height / rowHeight) + (Overscan * 2) + 1);
        first = Math.Clamp((int) MathF.Floor(Scroller.ScrollTop / rowHeight) - Overscan, 0, Math.Max(0, Count - capacity));

        while (rows.Count < capacity) {
            rows.Add(CreateRow(this));
        }

        for (var i = 0; i < rows.Count; i++) {
            var row = rows[i];
            var item = first + i;

            if (i >= capacity || item >= Count) {
                row.AddClass("parked");
                continue;
            }

            row.RemoveClass("parked");
            row.SetStyle("top", (item * rowHeight).ToString("0.##", CultureInfo.InvariantCulture) + "px");
            row.SetStyle("height", rowHeight.ToString("0.##", CultureInfo.InvariantCulture) + "px");

            BindRow?.Invoke(row, item);
        }
    }

    void OnCountChanged(int previous, int current) {
        _ = previous;
        _ = current;

        // ⚠ The scroll offset is clamped by the *scroller*, against a content height that has not
        // been laid out yet — so a list that shrank leaves the offset past its own end until the next
        // pass. Realising here writes the new height; `LayoutFinished` then realises again against
        // the measured one, which is the pass that puts the rows in the right place.
        Realise();
        Document.Invalidate();
    }
}
