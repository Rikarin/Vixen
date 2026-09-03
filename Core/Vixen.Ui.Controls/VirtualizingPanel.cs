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
///         ⚠ <b>Rows are positioned absolutely, and by default at a fixed height.</b> Virtualisation
///         needs to know where row 40 000 is without having measured the 39 999 above it, and a fixed
///         height makes that arithmetic instead of a walk.
///     </para>
///     <para>
///         ⚠ <b>Variable heights are the running-sum index this used to say was a different
///         control.</b> They are not: <c>TreeView</c> delegates its pool here, so a second control
///         would have had to duplicate the pool, the parking, the <c>LayoutFinished</c> subscription,
///         <see cref="RowOf" /> and <see cref="ScrollIntoView(int)" /> to gain one array — and every
///         caller wanting one row taller than the rest would have had to choose a control rather than
///         set a property. What is true, and is the reason for the caution, is that the uniform path
///         must stay the arithmetic it was: it does, byte for byte, and this control is uniform until
///         something calls <see cref="SetRowHeight" /> or turns <see cref="MeasureRows" /> on. See
///         <see cref="OffsetOf" /> for what the index costs.
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

    // The running-sum index, and it is two arrays rather than one because both questions are asked
    // every frame: `own` answers "how much taller is item i than the estimate", which a harvest
    // writes and reads back, and `sums` is a Fenwick tree over it, which answers a prefix in
    // O(log n) and is what makes "where is row 40 000" arithmetic again.
    //
    // ⚠ Null until something asks for a variable height, and that is the whole compatibility story:
    // a panel nobody has called `SetRowHeight` on and whose `MeasureRows` is off allocates nothing
    // and runs the same six lines it always did.
    float[]? own;
    float[]? sums;

    // What `own` is measured against and how long it is. A change of either invalidates the index —
    // see `Reindex`.
    float estimate;
    int indexed;

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

    /// <summary>Whether a row's height is whatever the row turned out to be.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Off is a uniform list and is the default.</b> Turning it on stops this control
    ///         writing a <c>height</c> on to each row, so a row is as tall as its content — and the
    ///         realise that runs on the next <c>LayoutFinished</c> reads the height back and puts it
    ///         in the index. A row's height is therefore learned once, on the pass after it first
    ///         appears, and the list is right on the one after that.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Which means an item nobody has scrolled to is the estimate, and the estimate is
    ///         <c>--row-height</c>.</b> The scroll bar over a hundred thousand unmeasured rows is
    ///         therefore a guess that gets better as the list is used, which is what every variable
    ///         virtualiser does and is the reason the estimate is worth setting to something near the
    ///         truth. A caller that already knows each height — a log of records whose line count it
    ///         counted when it appended them — should call <see cref="SetRowHeight" /> instead and
    ///         leave this off: an exact index has no correction to make and no scroll bar that moves
    ///         under the thumb.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Scrolling is anchored across a correction</b>, and it has to be. Learning that
    ///         the forty rows above the viewport are each two pixels taller than the estimate moves
    ///         everything below them down by eighty; without compensating the scroll offset, reading
    ///         a long list would drag the text out from under the reader. See
    ///         <see cref="Realise" />.
    ///     </para>
    /// </remarks>
    public bool MeasureRows { get; set; }

    /// <summary>Says how tall one item is, rather than letting the row say.</summary>
    /// <param name="item">The item's index.</param>
    /// <param name="height">How tall it is. Negative is refused; zero is a row of no height.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="height" /> is negative.</exception>
    /// <remarks>
    ///     ⚠ <b>It is remembered for the item and not for the row</b>, which is the distinction the
    ///     pool makes everywhere else: rows are reused and items are not. An item outside
    ///     <see cref="Count" /> is remembered too, because a caller that sizes its data before it
    ///     counts it is doing the sensible thing in the wrong order rather than making a mistake.
    /// </remarks>
    public void SetRowHeight(int item, float height) {
        ArgumentOutOfRangeException.ThrowIfNegative(height);

        if (item < 0) {
            return;
        }

        Reindex(item + 1);

        var previous = own![item];

        if (!float.IsNaN(previous) && previous.Equals(height)) {
            return;
        }

        own[item] = height;
        Add(item, height - estimate - (float.IsNaN(previous) ? 0f : previous - estimate));
    }

    /// <summary>Forgets every height that was measured or set, so the list is uniform again.</summary>
    /// <remarks>
    ///     What a caller whose data changed wholesale wants. Heights are per item, so a list that
    ///     replaced its items keeps the old list's sizes against the new one's indices until this is
    ///     called — which looks like a list that has gone subtly wrong rather than like stale data.
    /// </remarks>
    public void ClearRowHeights() {
        own = null;
        sums = null;
        indexed = 0;
    }

    /// <summary>How tall one item is: what was set or measured for it, or <see cref="RowHeight" />.</summary>
    /// <param name="item">The item's index.</param>
    public float HeightOf(int item) {
        if (own is null) {
            return RowHeight;
        }

        Reindex(Count);

        return item < 0 || item >= indexed || float.IsNaN(own[item]) ? estimate : own[item];
    }

    /// <summary>Where an item's top edge is, in the scroller's content.</summary>
    /// <param name="item">The item's index. Clamped to the list.</param>
    /// <remarks>
    ///     <para>
    ///         <b>O(1) while the list is uniform and O(log n) once it is not</b>, which is the whole
    ///         reason for the index. The alternative — adding up the heights above the item — is what
    ///         makes a variable-height list quadratic to scroll, and it is quadratic in the number of
    ///         <i>items</i> rather than in the number of rows, so it does not show up until somebody
    ///         opens a file with a hundred thousand lines in it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What the index costs is two floats an item, and only after the first one is
    ///         set.</b> A hundred thousand rows is 800 KB, which is worth saying out loud because it
    ///         is charged for items nobody has looked at. It buys the property that matters: an
    ///         offset, a total and "which item is at this offset" are all logarithmic, so the frame
    ///         cost does not depend on how far down the list somebody has scrolled.
    ///     </para>
    /// </remarks>
    public float OffsetOf(int item) {
        var index = Math.Clamp(item, 0, Math.Max(0, Count));

        if (own is null) {
            return index * RowHeight;
        }

        Reindex(Count);

        return (index * estimate) + Prefix(index);
    }

    /// <summary>How tall the whole list is, realised or not.</summary>
    public float TotalHeight {
        get {
            if (own is null) {
                return Count * RowHeight;
            }

            Reindex(Count);

            return (Count * estimate) + Prefix(Count);
        }
    }

    /// <summary>Which item covers an offset in the content, or the last one past the end.</summary>
    /// <param name="offset">How far down the content.</param>
    /// <remarks>
    ///     ⚠ <b>A binary search over <see cref="OffsetOf" /> rather than a descent of the tree</b>,
    ///     so it is O(log²n) rather than O(log n). It runs twice a frame at most, and a Fenwick
    ///     descent would have to special-case the estimate term that is not in the tree — which is
    ///     more code to be wrong in than the frame can measure.
    /// </remarks>
    public int ItemAt(float offset) {
        if (Count <= 0) {
            return 0;
        }

        if (own is null) {
            var height = RowHeight;
            return height <= 0f ? 0 : Math.Clamp((int) MathF.Floor(offset / height), 0, Count - 1);
        }

        var low = 0;
        var high = Count - 1;

        while (low < high) {
            var middle = (low + high + 1) / 2;

            if (OffsetOf(middle) <= offset) {
                low = middle;
            } else {
                high = middle - 1;
            }
        }

        return low;
    }

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
        var top = OffsetOf(index);
        var height = HeightOf(index);
        var viewport = Scroller.Height;

        if (top < Scroller.ScrollTop) {
            Scroller.ScrollTop = top;
        } else if (top + height > Scroller.ScrollTop + viewport) {
            Scroller.ScrollTop = top + height - viewport;
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

        if (own is not null || MeasureRows) {
            RealiseVariable();
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

    /// <summary>Makes the index exist, long enough, and summed against the current estimate.</summary>
    /// <remarks>
    ///     ⚠ <b>What is stored is each item's <i>absolute</i> height, and the tree holds the
    ///     differences.</b> The obvious arrangement — store the differences and nothing else — cannot
    ///     survive a change of <c>--row-height</c>: an item 50 pixels tall is <c>+30</c> against an
    ///     estimate of 20 and <c>+20</c> against one of 30, and a stored <c>0</c> is indistinguishable
    ///     from an item nobody has measured. ⚠ <b>And that is not a hypothetical:</b> the estimate is
    ///     read out of the cascade, so a caller that sets heights before the first style pass sets
    ///     them against the fallback and the first layout changes it — which discarded every height on
    ///     the frame it was set. <see cref="float.NaN" /> is "not measured", and re-summing the tree
    ///     against a new estimate is one linear pass.
    /// </remarks>
    void Reindex(int length) {
        var height = RowHeight;
        var wanted = Math.Max(length, Math.Max(Count, 1));

        if (own is not null && indexed >= wanted && height.Equals(estimate)) {
            return;
        }

        if (own is null || indexed < wanted) {
            var previous = own;

            own = new float[wanted];
            own.AsSpan().Fill(float.NaN);

            if (previous is not null) {
                Array.Copy(previous, own, Math.Min(previous.Length, own.Length));
            }

            indexed = wanted;
        }

        estimate = height;

        // A Fenwick tree built in one pass rather than by n updates: every node adds itself into its
        // parent once, which is O(n) rather than O(n log n).
        sums = new float[indexed + 1];

        for (var i = 0; i < indexed; i++) {
            var k = i + 1;

            sums[k] += float.IsNaN(own[i]) ? 0f : own[i] - estimate;

            var parent = k + (k & -k);

            if (parent <= indexed) {
                sums[parent] += sums[k];
            }
        }
    }

    /// <summary>Adds to one item's delta.</summary>
    void Add(int item, float delta) {
        for (var k = item + 1; k <= indexed; k += k & -k) {
            sums![k] += delta;
        }
    }

    /// <summary>The deltas of items <c>[0, count)</c>, added up.</summary>
    float Prefix(int count) {
        var total = 0f;

        for (var k = Math.Min(count, indexed); k > 0; k -= k & -k) {
            total += sums![k];
        }

        return total;
    }

    /// <summary>Realises a list whose rows are not all the same height.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A separate path rather than a generalisation of the uniform one</b>, because the
    ///         uniform one is what every existing caller runs and its arithmetic is pinned by tests
    ///         about which item lands where. A single path parameterised on the index would have been
    ///         the same code for both and would have made every one of those tests a test of this.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The harvest is first and the scroll is anchored across it.</b> Reading that the
    ///         rows above the viewport are taller than the estimate moves everything below them down;
    ///         compensating <c>ScrollTop</c> by exactly how far the first visible item moved is what
    ///         stops the text sliding out from under whoever is reading it. Without it a long list
    ///         corrects itself by scrolling, which looks like the list fighting the wheel.
    ///     </para>
    /// </remarks>
    void RealiseVariable() {
        Reindex(Count);

        var viewport = Scroller.Height;
        var top = Scroller.ScrollTop;
        var anchor = ItemAt(top);
        var before = OffsetOf(anchor);

        if (MeasureRows) {
            for (var i = 0; i < rows.Count; i++) {
                var row = rows[i];
                var item = first + i;

                if (item < 0 || item >= Count || row.HasClass("parked")) {
                    continue;
                }

                // ⚠ Zero is "not measured yet" rather than "a row of no height". A row that has been
                // created but not laid out reports zero, and believing it would collapse the list on
                // the frame it first appears and then have to undo it — which is a visible jump. A
                // caller that genuinely wants a row of no height says so with `SetRowHeight`.
                if (row.Height > 0f) {
                    SetRowHeight(item, row.Height);
                }
            }

            var moved = OffsetOf(anchor) - before;

            if (moved != 0f) {
                top += moved;
                Scroller.ScrollTop = top;
            }
        }

        Scroller.Content.SetStyle(
            "height",
            TotalHeight.ToString("0.##", CultureInfo.InvariantCulture) + "px"
        );

        first = Math.Max(0, ItemAt(top) - Overscan);

        // How many rows reach the bottom of the viewport from `first`, plus the overscan below it.
        // A walk rather than a division, and it is a walk over the rows on screen rather than over
        // the list — which is the same order the uniform path's `ceiling` is.
        var capacity = 0;
        var used = OffsetOf(first) - top;

        while (first + capacity < Count && (capacity < Overscan || used < viewport)) {
            used += HeightOf(first + capacity);
            capacity++;
        }

        capacity = Math.Min(Count - first, capacity + Overscan + 1);

        while (rows.Count < capacity) {
            rows.Add(CreateRow!(this));
        }

        for (var i = 0; i < rows.Count; i++) {
            var row = rows[i];
            var item = first + i;

            if (i >= capacity || item >= Count) {
                row.AddClass("parked");
                continue;
            }

            row.RemoveClass("parked");
            row.SetStyle("top", OffsetOf(item).ToString("0.##", CultureInfo.InvariantCulture) + "px");

            // ⚠ No `height` under `MeasureRows`, and that is the mechanism rather than an omission:
            // a row this control has sized is a row whose measurement is this control's own answer
            // read back, which converges on the estimate and learns nothing.
            if (!MeasureRows) {
                row.SetStyle("height", HeightOf(item).ToString("0.##", CultureInfo.InvariantCulture) + "px");
            }

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
