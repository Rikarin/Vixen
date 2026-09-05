// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Input;
using Vixen.Ui.Styling;

namespace Vixen.Ui.Controls.Advanced;

/// <summary>One column of a <see cref="DataGrid" />.</summary>
/// <remarks>
///     <para>
///         <b>Accessors are delegates, not property names.</b> A grid that took <c>"Health"</c> and
///         reflected on it would not work trimmed or on iOS, which is the same argument
///         <c>PropertyGrid</c> makes for <c>Vixen.Core.Reflection</c> — and here the caller already
///         has the lambda, so there is nothing to generate.
///     </para>
///     <para>
///         ⚠ <b>A column with no <see cref="Commit" /> is read-only</b>, and the grid says so by
///         refusing to open an editor rather than by opening one that throws the value away.
///     </para>
/// </remarks>
public sealed class DataColumn {
    /// <summary>Creates a column.</summary>
    /// <param name="header">What is written at the top of it.</param>
    /// <param name="value">What to show for a row.</param>
    public DataColumn(string header, Func<object, object?>? value = null) {
        Header = header;
        Value = value;
    }

    /// <summary>What is written at the top.</summary>
    public string Header { get; set; }

    /// <summary>How wide it is, in pixels.</summary>
    public float Width { get; set; } = 120f;

    /// <summary>How narrow a drag may make it.</summary>
    public float MinimumWidth { get; set; } = 32f;

    /// <summary>Whether the header may be dragged to resize it.</summary>
    public bool Resizable { get; set; } = true;

    /// <summary>Whether clicking the header sorts by it.</summary>
    public bool Sortable { get; set; } = true;

    /// <summary>What to show for a row.</summary>
    public Func<object, object?>? Value { get; set; }

    /// <summary>What to do with an edited cell, or <c>null</c> if the column is read-only.</summary>
    public Action<object, string>? Commit { get; set; }

    /// <summary>How to fill a cell, if a string is not enough.</summary>
    /// <remarks>
    ///     The escape hatch for a progress bar in a column, a swatch, a pair of buttons. It is given
    ///     the cell and the item and may add whatever it likes; the default label is hidden for it,
    ///     so a template does not have to remove anything the grid put there.
    /// </remarks>
    public Action<DataCell, object>? Template { get; set; }

    /// <summary>How two values of this column compare, when the default will not do.</summary>
    public IComparer<object?>? Comparer { get; set; }

    /// <summary>The value for a row, as text.</summary>
    /// <param name="item">The row's item.</param>
    /// <returns>The text.</returns>
    public string TextOf(object item) =>
        Value?.Invoke(item) is { } value
            ? value as string ?? Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty
            : string.Empty;
}

/// <summary>One cell.</summary>
public sealed partial class DataCell : Control {
    /// <inheritdoc />
    protected override string TagName => "data-cell";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <inheritdoc />
    protected override AccessibleRole NativeRole => AccessibleRole.GridCell;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>The cell's own text, and it is the name rather than the value.</b> A grid cell is
    ///     read as its contents — <c>aria-valuenow</c> is for a control that holds a number between
    ///     bounds, which a cell is not. While the cell is being edited the editor inside it is the
    ///     text box, with its own value; the cell around it still says what it shows.
    /// </remarks>
    protected override string? NativeAccessibleName => Editor is not null ? Editor.Value : Label?.Text;

    /// <summary>Which column it is in, or <c>null</c> if it is parked.</summary>
    public DataColumn? Column { get; internal set; }

    /// <summary>Which item it is showing.</summary>
    public object? Item { get; internal set; }

    /// <summary>The default text, hidden when a template is in charge.</summary>
    public UiElement Label { get; private set; } = null!;

    /// <summary>The field shown while the cell is being edited.</summary>
    public TextBox? Editor { get; internal set; }

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();
        Label = Part("data-text");
    }
}

/// <summary>One header, with its sort arrow and the grip that resizes it.</summary>
public sealed partial class DataHeaderCell : Control {
    /// <inheritdoc />
    protected override string TagName => "data-header-cell";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <inheritdoc />
    /// <remarks>
    ///     ARIA <c>columnheader</c>, which is what tells a screen reader that this cell names the
    ///     column under it rather than being one more cell in it.
    /// </remarks>
    protected override AccessibleRole NativeRole => AccessibleRole.ColumnHeader;

    /// <inheritdoc />
    protected override string? NativeAccessibleName => Column?.Header ?? Label?.Text;

    /// <summary>Which column, or <c>null</c> if parked.</summary>
    public DataColumn? Column { get; internal set; }

    /// <summary>The title.</summary>
    public UiElement Label { get; private set; } = null!;

    /// <summary>The arrow, hidden unless the grid is sorted by this column.</summary>
    public Icon Sort { get; private set; } = null!;

    /// <summary>The strip down the right edge that a drag resizes the column by.</summary>
    public UiElement Grip { get; private set; } = null!;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Label = Part("data-text");

        Sort = Part<Icon>();
        Sort.Geometry = ControlIcons.ChevronDown;

        Grip = Part("data-resizer");
    }
}

/// <summary>One realised row: an item's, or a group's header.</summary>
public sealed partial class DataRow : Control {
    readonly List<DataCell> cells = [];

    /// <inheritdoc />
    protected override string TagName => "data-row";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <inheritdoc />
    protected override AccessibleRole NativeRole => AccessibleRole.Row;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>A group header is named by the words it shows; an item row is not named at
    ///     all.</b> A <c>row</c> is a container role rather than a widget, so a bridge reads its
    ///     cells; naming it with the first cell's text would make a screen reader say that value
    ///     twice. A group header has no cells and its label is the only thing in it.
    /// </remarks>
    protected override string? NativeAccessibleName => GroupKey is null ? null : GroupLabel?.Text;

    /// <inheritdoc />
    /// <remarks>Selection is read from <see cref="ElementState.Checked" />, which the grid already sets for the cascade.</remarks>
    protected override AccessibleStates NativeAccessibleState =>
        (State & ElementState.Checked) != 0 ? AccessibleStates.Selected : AccessibleStates.None;

    /// <summary>Which row of the view it is, or -1 if parked.</summary>
    public int Index { get; internal set; } = -1;

    /// <summary>The item, or <c>null</c> for a group header.</summary>
    public object? Item { get; internal set; }

    /// <summary>The group this row heads, if it heads one.</summary>
    public object? GroupKey { get; internal set; }

    /// <summary>The cells, including the parked ones.</summary>
    public IReadOnlyList<DataCell> Cells => cells;

    /// <summary>The label a group header shows instead of cells.</summary>
    public UiElement GroupLabel { get; private set; } = null!;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        GroupLabel = Part("data-group-label");
        GroupLabel.AddClass("hidden");
    }

    internal DataCell CellAt(int slot) {
        while (cells.Count <= slot) {
            cells.Add(Add<DataCell>());
        }

        return cells[slot];
    }

    internal void ParkFrom(int slot) {
        for (var i = slot; i < cells.Count; i++) {
            cells[i].Column = null;
            cells[i].Item = null;
            cells[i].AddClass("parked");
        }
    }
}

/// <summary>One line of the view: an item, or the header of a group.</summary>
/// <param name="Item">The item's index in <see cref="DataGrid.Items" />, or -1 for a group header.</param>
/// <param name="GroupKey">What the group is keyed on, for a header.</param>
/// <param name="Count">How many items are in the group, for a header.</param>
readonly record struct GridRow(int Item, object? GroupKey, int Count);

/// <summary>A table, of which only what is on screen exists as elements.</summary>
/// <remarks>
///     <para>
///         <b>Virtualised in both directions</b>, which is what doc 09 asks of this control by name
///         and what makes it different from the tree. A hundred columns of a hundred thousand rows
///         is ten million cells; what exists is the twenty-odd columns and thirty-odd rows that are
///         on screen, and neither pool is rebuilt when the other scrolls.
///     </para>
///     <para>
///         ⚠ <b>Frozen columns are the leading <i>n</i>, not a flag on a column.</b> Freezing an
///         arbitrary subset raises a question with no good answer — what happens when a frozen
///         column is dragged to the middle — and every grid that offers it ends up reordering the
///         columns behind the user's back. <see cref="FrozenColumns" /> says how many stay put, and
///         <see cref="MoveColumn" /> is what puts a column among them.
///     </para>
///     <para>
///         ⚠ <b>A frozen cell is positioned against the scroll offset, not against the content.</b>
///         The rows live inside the scroller, so everything in them moves left as the view scrolls
///         right; a frozen cell adds the scroll offset back. That is one line and it is the entire
///         mechanism — there is no second scroller and no second element tree.
///     </para>
///     <para>
///         ⚠ <b>Sorting and grouping are a view over the items, never a reorder of them.</b> The
///         list belongs to the caller, who is very likely iterating it elsewhere, and a grid that
///         sorted in place would silently reorder a game's entity list because somebody clicked a
///         column heading.
///     </para>
/// </remarks>
public sealed partial class DataGrid : Control {
    readonly List<DataColumn> columns = [];
    readonly List<object> items = [];
    readonly List<GridRow> view = [];
    readonly List<float> offsets = [];
    readonly List<DataRow> rows = [];
    readonly List<DataHeaderCell> headers = [];
    readonly HashSet<int> selection = [];
    readonly HashSet<object> collapsed = [];

    int rowHeightId;
    int headerHeightId;
    int first;
    int firstColumn;
    int lastColumn;
    int anchor = -1;

    DataColumn? resizing;
    DataColumn? reordering;
    float grabbed;
    bool moved;

    /// <summary>How many rows are realised above and below the viewport.</summary>
    public const int Overscan = 2;

    /// <inheritdoc />
    protected override string TagName => "data-grid";

    /// <inheritdoc />
    protected override bool AcceptsFocus => true;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b><c>treegrid</c> once it is grouped and <c>grid</c> otherwise, which is a real
    ///     difference rather than a nicety.</b> A grouped grid has expandable rows, and
    ///     <c>treegrid</c> is the role whose keyboard model a screen reader announces the Left and
    ///     Right arrows for. Reporting <c>grid</c> while the rows collapse would leave a
    ///     screen-reader user with no way to know the groups can be opened.
    /// </remarks>
    protected override AccessibleRole NativeRole =>
        GroupColumn is null ? AccessibleRole.Grid : AccessibleRole.TreeGrid;

    /// <inheritdoc />
    /// <remarks>On <c>TreeView</c>'s terms: it changes what Ctrl and Shift mean.</remarks>
    protected override AccessibleStates NativeAccessibleState =>
        MultiSelect ? AccessibleStates.MultiSelectable : AccessibleStates.None;

    /// <summary>The strip of headings.</summary>
    /// <remarks>
    ///     ⚠ <b>Inside the scroller, and it used to be a sibling of it.</b> A header outside the
    ///     scrollport is a second pane: it cannot scroll vertically because it is not in the port at
    ///     all, and it has to be moved horizontally by <i>subtracting</i> the scroll offset where
    ///     everything in the port adds it. Two arithmetics for one visual result is what
    ///     <c>Rikarin/Vixen#787</c> is about, and the answer is CSS's: it is the scroller's own first
    ///     child, <c>position: sticky; top: 0</c>, so the one offset every cell in the grid adds is
    ///     the only one there is.
    /// </remarks>
    public UiElement Header { get; private set; } = null!;

    /// <summary>The scroller the rows live in.</summary>
    public ScrollView Scroller { get; private set; } = null!;

    /// <summary>The columns, left to right.</summary>
    public IReadOnlyList<DataColumn> Columns => columns;

    /// <summary>What is being shown.</summary>
    public IReadOnlyList<object> Items => items;

    /// <summary>The rows that exist as elements, including the parked ones.</summary>
    public IReadOnlyList<DataRow> Rows => rows;

    /// <summary>The header cells that exist.</summary>
    public IReadOnlyList<DataHeaderCell> Headers => headers;

    /// <summary>How many lines the view has, group headers included.</summary>
    public int RowCount => view.Count;

    /// <summary>Which items are selected, by their index in <see cref="Items" />.</summary>
    public IReadOnlyCollection<int> Selection => selection;

    /// <summary>How tall a row is, from <c>--row-height</c>.</summary>
    public float RowHeight => Document.LengthOf(Style, rowHeightId) ?? 24f;

    /// <summary>How tall the heading strip is, from <c>--header-height</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>A declared length rather than <c>Header.Height</c>, and the difference is an ordering
    ///     one.</b> <see cref="Refresh" /> sets the content pane's height before the pass that would
    ///     measure the header, so reading the measured value there gives zero on the first frame and
    ///     the right answer on every one after it — a grid whose rows are all one header out of place
    ///     until something else invalidates it. The row offsets and the pane's height are decided from
    ///     the same declaration the stylesheet lays the header out with.
    /// </remarks>
    public float HeaderHeight => Document.LengthOf(Style, headerHeightId) ?? 26f;

    /// <summary>How many columns on the left do not scroll.</summary>
    [UiProperty(Changed = nameof(OnLayoutChanged))]
    public partial int FrozenColumns { get; set; }

    /// <summary>Whether more than one row may be selected.</summary>
    [UiProperty(Default = true)]
    public partial bool MultiSelect { get; set; }

    /// <summary>Which column the view is sorted by, or <c>null</c>.</summary>
    public DataColumn? SortColumn { get; private set; }

    /// <summary>Which way.</summary>
    public bool SortDescending { get; private set; }

    /// <summary>Which column the view is grouped by, or <c>null</c>.</summary>
    public DataColumn? GroupColumn { get; private set; }

    /// <summary>Raised when the selection changes.</summary>
    public event Action<DataGrid>? SelectionChanged;

    /// <summary>Raised after an inline edit is committed.</summary>
    public event Action<DataGrid, object, DataColumn>? CellEdited;

    /// <summary>Raised when a row is double-clicked.</summary>
    public event Action<DataGrid, object>? Activated;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        rowHeightId = Document.PropertyId("--row-height");
        headerHeightId = Document.PropertyId("--header-height");

        Scroller = Part<ScrollView>();

        // ⚠ The scroller's FIRST child, so that the flow position `position: sticky` floors against
        // is the top of the content pane. Every row after it is absolutely positioned and cannot
        // move it; the header is the one box in this control that is in flow at all.
        Header = Scroller.Content.Add("data-header");

        Scroller.Scrolled += _ => Realise();

        // Nothing else announces a resize, and a resize changes how many rows fit. Gated on the
        // size — see Control.WhenResized — because Refresh rebuilds the sorted, filtered view.
        WhenResized(Refresh);

        AddHandler<PointerEvent>(static (element, args) => ((DataGrid) element).Pointed(args));
        AddHandler<TapEvent>(static (element, args) => ((DataGrid) element).Tapped(args));
        AddHandler<KeyEvent>(static (element, args) => ((DataGrid) element).Keyed(args));
    }

    // ── Contents ─────────────────────────────────────────────────────────────

    /// <summary>Adds a column on the right.</summary>
    /// <param name="header">Its heading.</param>
    /// <param name="value">What to show for a row.</param>
    /// <returns>The column.</returns>
    public DataColumn AddColumn(string header, Func<object, object?>? value = null) {
        var column = new DataColumn(header, value);
        columns.Add(column);

        Refresh();
        return column;
    }

    /// <summary>Adds a column that was made elsewhere.</summary>
    /// <param name="column">The column.</param>
    /// <returns>It.</returns>
    public DataColumn AddColumn(DataColumn column) {
        ArgumentNullException.ThrowIfNull(column);

        columns.Add(column);
        Refresh();

        return column;
    }

    /// <summary>Where a column currently is, left to right.</summary>
    /// <param name="column">The column.</param>
    /// <returns>Its index, or -1 if it is not in this grid.</returns>
    public int IndexOf(DataColumn column) => columns.IndexOf(column);

    /// <summary>Moves a column to another position.</summary>
    /// <param name="from">Where it is.</param>
    /// <param name="to">Where it should go.</param>
    public void MoveColumn(int from, int to) {
        if (from < 0 || from >= columns.Count) {
            return;
        }

        to = Math.Clamp(to, 0, columns.Count - 1);

        if (from == to) {
            return;
        }

        var column = columns[from];

        columns.RemoveAt(from);
        columns.Insert(to, column);

        Refresh();
    }

    /// <summary>Shows a list of objects.</summary>
    /// <param name="source">The items. Copied, so the grid does not hold the caller's list.</param>
    public void SetItems(IEnumerable<object> source) {
        ArgumentNullException.ThrowIfNull(source);

        items.Clear();
        items.AddRange(source);

        selection.Clear();
        Refresh();
    }

    /// <summary>Sorts the view by a column, or turns sorting off.</summary>
    /// <param name="column">The column, or <c>null</c> for the original order.</param>
    /// <param name="descending">Which way.</param>
    public void SortBy(DataColumn? column, bool descending = false) {
        SortColumn = column;
        SortDescending = descending;

        Refresh();
    }

    /// <summary>Groups the view by a column, or turns grouping off.</summary>
    /// <param name="column">The column, or <c>null</c>.</param>
    public void GroupBy(DataColumn? column) {
        GroupColumn = column;
        collapsed.Clear();

        Refresh();
    }

    /// <summary>Collapses or expands a group.</summary>
    /// <param name="key">Its key.</param>
    public void ToggleGroup(object key) {
        ArgumentNullException.ThrowIfNull(key);

        if (!collapsed.Remove(key)) {
            collapsed.Add(key);
        }

        Refresh();
    }

    // ── Refreshing ───────────────────────────────────────────────────────────

    /// <summary>Rebuilds the view and realises the elements for it.</summary>
    public void Refresh() {
        Rebuild();

        offsets.Clear();

        var running = 0f;

        foreach (var column in columns) {
            offsets.Add(running);
            running += MathF.Max(column.MinimumWidth, column.Width);
        }

        offsets.Add(running);

        // The header is in the pane now, so the pane is that much taller and every row starts that
        // much further down. One term, in the two places that decide a vertical position.
        Scroller.Content.SetStyle("height", Inline.Px(HeaderHeight + (view.Count * RowHeight)));
        Scroller.Content.SetStyle("width", Inline.Px(running));

        // A pass before anything reads a size — see `TreeView.Refresh` for why a declaration is not
        // a measurement.
        Document.Update();

        Scroller.Refresh();
        Realise();
    }

    /// <summary>Works out which lines the view has, in order.</summary>
    void Rebuild() {
        view.Clear();

        var order = new List<int>(items.Count);

        for (var i = 0; i < items.Count; i++) {
            order.Add(i);
        }

        if (SortColumn is { } sort) {
            var comparer = sort.Comparer ?? DefaultComparer.Instance;

            // ⚠ Through LINQ because its ordering is stable and `List.Sort` is not. Two rows that
            // compare equal must keep the order they were in — otherwise clicking a heading twice
            // shuffles the ties, and "sort by name, then sort by level" stops being a two-key sort.
            order = SortDescending
                ? [.. order.OrderByDescending(index => sort.Value?.Invoke(items[index]), comparer)]
                : [.. order.OrderBy(index => sort.Value?.Invoke(items[index]), comparer)];
        }

        if (GroupColumn is not { } group) {
            foreach (var index in order) {
                view.Add(new GridRow(index, null, 0));
            }

            return;
        }

        // Grouped in the order the groups first appear, which after a sort is the sorted order —
        // so "sort by level, group by level" gives the groups in level order without a second rule.
        var seen = new List<object>();
        var buckets = new Dictionary<object, List<int>>();

        foreach (var index in order) {
            var key = group.Value?.Invoke(items[index]) ?? string.Empty;

            if (!buckets.TryGetValue(key, out var bucket)) {
                buckets[key] = bucket = [];
                seen.Add(key);
            }

            bucket.Add(index);
        }

        foreach (var key in seen) {
            var bucket = buckets[key];
            view.Add(new GridRow(-1, key, bucket.Count));

            if (collapsed.Contains(key)) {
                continue;
            }

            foreach (var index in bucket) {
                view.Add(new GridRow(index, key, 0));
            }
        }
    }

    void Realise() {
        var rowHeight = RowHeight;

        if (rowHeight <= 0f || columns.Count == 0) {
            return;
        }

        Window();

        var capacity = Math.Min(view.Count, (int) MathF.Ceiling(Scroller.Height / rowHeight) + (Overscan * 2) + 1);
        first = Math.Clamp(
            (int) MathF.Floor((Scroller.ScrollTop - HeaderHeight) / rowHeight) - Overscan,
            0,
            Math.Max(0, view.Count - capacity)
        );

        while (rows.Count < capacity) {
            rows.Add(Scroller.Content.Add<DataRow>());
        }

        for (var i = 0; i < rows.Count; i++) {
            var row = rows[i];
            var index = first + i;

            if (i >= capacity || index >= view.Count) {
                row.Index = -1;
                row.Item = null;
                row.AddClass("parked");

                continue;
            }

            row.RemoveClass("parked");
            Bind(row, index, rowHeight);
        }

        RealiseHeader();
    }

    /// <summary>Which columns are on screen, and how wide the frozen band is.</summary>
    /// <remarks>
    ///     ⚠ <b>The frozen columns are excluded from the search, not clamped out of it.</b> They are
    ///     always realised and always at the left, so a viewport scrolled past them must still start
    ///     the scrolling range after the band — otherwise the first scrolling column is drawn
    ///     underneath a frozen one and looks like it has gone missing.
    /// </remarks>
    void Window() {
        var frozen = Math.Clamp(FrozenColumns, 0, columns.Count);
        var left = Scroller.ScrollLeft + offsets[frozen];
        var right = Scroller.ScrollLeft + Scroller.Width;

        firstColumn = frozen;
        lastColumn = frozen - 1;

        for (var i = frozen; i < columns.Count; i++) {
            if (offsets[i + 1] <= left) {
                firstColumn = i + 1;
                continue;
            }

            if (offsets[i] >= right) {
                break;
            }

            lastColumn = i;
        }

        firstColumn = Math.Min(firstColumn, columns.Count);
    }

    void Bind(DataRow row, int index, float rowHeight) {
        var line = view[index];

        row.Index = index;
        row.GroupKey = line.GroupKey;
        row.Item = line.Item >= 0 ? items[line.Item] : null;

        row.SetStyle("top", Inline.Px(HeaderHeight + (index * rowHeight)));
        row.SetStyle("height", Inline.Px(rowHeight));

        if (line.Item < 0) {
            row.AddClass("data-group");
            row.GroupLabel.RemoveClass("hidden");
            row.GroupLabel.Text = $"{line.GroupKey} ({line.Count})";

            // ⚠ Pinned to the scroll offset like a frozen cell. A group header that scrolled away
            // horizontally leaves a table whose rows are unlabelled at exactly the moment there are
            // enough columns to need labelling.
            row.GroupLabel.SetStyle("left", Inline.Px(Scroller.ScrollLeft));
            row.ParkFrom(0);

            return;
        }

        row.RemoveClass("data-group");
        row.GroupLabel.AddClass("hidden");

        if (selection.Contains(line.Item)) {
            row.State |= ElementState.Checked;
        } else {
            row.State &= ~ElementState.Checked;
        }

        var slot = 0;
        var frozen = Math.Clamp(FrozenColumns, 0, columns.Count);

        for (var i = 0; i < frozen; i++) {
            Fill(row.CellAt(slot++), columns[i], row.Item!, offsets[i] + Scroller.ScrollLeft, true);
        }

        for (var i = firstColumn; i <= lastColumn; i++) {
            Fill(row.CellAt(slot++), columns[i], row.Item!, offsets[i], false);
        }

        row.ParkFrom(slot);

        // A row is as wide as the whole table rather than as wide as the cells it happens to have
        // realised, so its background and its selection highlight run the full width.
        row.SetStyle("width", Inline.Px(offsets[^1]));
    }

    static void Fill(DataCell cell, DataColumn column, object item, float left, bool frozen) {
        cell.RemoveClass("parked");

        cell.Column = column;
        cell.Item = item;

        cell.SetStyle("left", Inline.Px(left));
        cell.SetStyle("width", Inline.Px(MathF.Max(column.MinimumWidth, column.Width)));

        if (frozen) {
            cell.AddClass("frozen");
        } else {
            cell.RemoveClass("frozen");
        }

        if (column.Template is { } template) {
            cell.Label.AddClass("hidden");
            template(cell, item);

            return;
        }

        cell.Label.RemoveClass("hidden");
        cell.Label.Text = column.TextOf(item);
    }

    void RealiseHeader() {
        var frozen = Math.Clamp(FrozenColumns, 0, columns.Count);
        var needed = frozen + Math.Max(0, lastColumn - firstColumn + 1);

        while (headers.Count < needed) {
            headers.Add(Header.Add<DataHeaderCell>());
        }

        var slot = 0;

        // ⚠ The same two lines `Bind` uses for a row's cells, which is what moving the header into
        // the scroller bought. A frozen heading adds the scroll offset back because the strip it is
        // in has been moved left by exactly that much; a scrolling one takes the offset it was given.
        // Until #787 the second of these SUBTRACTED the offset, because the header was outside the
        // port and the rows were inside it.
        for (var i = 0; i < frozen; i++) {
            Fill(headers[slot++], columns[i], offsets[i] + Scroller.ScrollLeft, true);
        }

        for (var i = firstColumn; i <= lastColumn; i++) {
            Fill(headers[slot++], columns[i], offsets[i], false);
        }

        for (var i = slot; i < headers.Count; i++) {
            headers[i].Column = null;
            headers[i].AddClass("parked");
        }
    }

    void Fill(DataHeaderCell cell, DataColumn column, float left, bool frozen) {
        cell.RemoveClass("parked");

        cell.Column = column;
        cell.Label.Text = column.Header;

        cell.SetStyle("left", Inline.Px(left));
        cell.SetStyle("width", Inline.Px(MathF.Max(column.MinimumWidth, column.Width)));

        if (frozen) {
            cell.AddClass("frozen");
        } else {
            cell.RemoveClass("frozen");
        }

        if (ReferenceEquals(SortColumn, column)) {
            cell.RemoveClass("unsorted");
            cell.Sort.Geometry = SortDescending ? ControlIcons.ChevronUp : ControlIcons.ChevronDown;
        } else {
            cell.AddClass("unsorted");
        }
    }

    void OnLayoutChanged(int previous, int current) => Refresh();

    // ── Selection ────────────────────────────────────────────────────────────

    /// <summary>Selects a row, or adds it to or removes it from the selection.</summary>
    /// <param name="item">Its index in <see cref="Items" />, or -1 to select nothing.</param>
    /// <param name="modifiers">What was held: Control toggles, Shift extends.</param>
    public void Select(int item, ModifierKeys modifiers = ModifierKeys.None) {
        if (item < 0) {
            selection.Clear();
            anchor = -1;

            Restate();
            return;
        }

        if (MultiSelect && modifiers.HasFlag(ModifierKeys.Shift) && anchor >= 0) {
            // ⚠ Through the view rather than through the item indices. A sorted or grouped grid's
            // rows are not in item order, and Shift means "everything between these two on screen"
            // — a range over the item indices selects whatever happened to be numbered between them.
            var from = RowIndexOf(anchor);
            var to = RowIndexOf(item);

            if (from >= 0 && to >= 0) {
                selection.Clear();

                for (var i = Math.Min(from, to); i <= Math.Max(from, to); i++) {
                    if (view[i].Item >= 0) {
                        selection.Add(view[i].Item);
                    }
                }

                Restate();
                return;
            }
        }

        if (MultiSelect && modifiers.HasFlag(ModifierKeys.Control)) {
            if (!selection.Remove(item)) {
                selection.Add(item);
            }

            // The anchor follows a Ctrl-click, so "Ctrl-click here, Shift-click there" selects the
            // range between them — which is what every file manager does.
            anchor = item;

            Restate();
            return;
        }

        selection.Clear();
        selection.Add(item);

        anchor = item;
        Restate();
    }

    /// <summary>Which line of the view an item is on, or -1 if it is inside a collapsed group.</summary>
    /// <param name="item">The item's index.</param>
    /// <returns>The line.</returns>
    public int RowIndexOf(int item) {
        for (var i = 0; i < view.Count; i++) {
            if (view[i].Item == item) {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The item a line of the view shows, or -1 for a group header.</summary>
    /// <param name="row">The line.</param>
    /// <returns>The item's index.</returns>
    public int ItemAt(int row) => row >= 0 && row < view.Count ? view[row].Item : -1;

    void Restate() {
        foreach (var row in rows) {
            var item = ItemAt(row.Index);

            if (item >= 0 && selection.Contains(item)) {
                row.State |= ElementState.Checked;
            } else {
                row.State &= ~ElementState.Checked;
            }
        }

        SelectionChanged?.Invoke(this);
    }

    // ── Inline editing ───────────────────────────────────────────────────────

    /// <summary>Puts a field in a cell so its value can be typed.</summary>
    /// <param name="cell">The cell.</param>
    /// <returns>Whether the column accepts edits.</returns>
    public bool BeginEdit(DataCell cell) {
        ArgumentNullException.ThrowIfNull(cell);

        if (cell.Column is not { Commit: not null } column || cell.Item is not { } item || cell.Editor is not null) {
            return false;
        }

        var editor = cell.Add<TextBox>();
        editor.Value = column.TextOf(item);
        editor.AddClass("data-editor");

        cell.Editor = editor;
        cell.Label.AddClass("hidden");

        Document.Focus(editor);
        editor.SelectAll();

        editor.Submitted += _ => Commit(cell, true);

        editor.AddHandler<KeyEvent>(
            (_, args) => {
                if (args is { Action: KeyAction.Pressed, Key: InputKey.Escape }) {
                    Commit(cell, false);
                    args.Handled = true;
                }
            }
        );

        return true;
    }

    void Commit(DataCell cell, bool keep) {
        if (cell.Editor is not { } editor || cell.Column is not { } column || cell.Item is not { } item) {
            return;
        }

        var text = editor.Value ?? string.Empty;

        cell.Editor = null;
        cell.Label.RemoveClass("hidden");

        editor.Remove();
        Document.Focus(this);

        if (!keep) {
            return;
        }

        column.Commit?.Invoke(item, text);
        CellEdited?.Invoke(this, item, column);

        Refresh();
    }

    // ── Input ────────────────────────────────────────────────────────────────

    void Pointed(PointerEvent args) {
        switch (args.Action) {
            case PointerAction.Pressed when args.Button == PointerButton.Primary:
                Document.Focus(this);
                Pressed(args);

                break;

            case PointerAction.Moved when resizing is { } column:
                column.Width = MathF.Max(column.MinimumWidth, args.X - grabbed);
                Refresh();

                break;

            case PointerAction.Moved when reordering is not null:
                moved |= Reorder(args.X);
                break;

            case PointerAction.Released when resizing is not null || reordering is not null:
                // ⚠ A heading that was pressed and released without moving is a click, and a click
                // on a heading sorts. Sorting on the press instead would re-sort the table under a
                // pointer that was about to drag the column somewhere, which reorders the wrong one.
                if (!moved && reordering is { Sortable: true } sortable) {
                    SortBy(sortable, ReferenceEquals(SortColumn, sortable) && !SortDescending);
                }

                resizing = null;
                reordering = null;
                moved = false;

                Document.ReleasePointer();
                break;

            default:
                return;
        }

        args.Handled = true;
    }

    void Pressed(PointerEvent args) {
        if (Under<DataHeaderCell>(args.Source) is { Column: { } column } header) {
            // The grip takes the press and the heading does not, so a drag near the edge resizes and
            // a drag anywhere else reorders.
            if (column.Resizable && args.X >= header.Grip.AbsoluteLeft && header.Grip.Width > 0f) {
                resizing = column;
                grabbed = header.AbsoluteLeft;

                Document.CapturePointer(this);
                return;
            }

            reordering = column;
            moved = false;

            Document.CapturePointer(this);
            return;
        }

        if (Under<DataRow>(args.Source) is not { Index: >= 0 } row) {
            return;
        }

        if (row.Item is null && row.GroupKey is { } key) {
            ToggleGroup(key);
            return;
        }

        var item = ItemAt(row.Index);

        if (item >= 0) {
            Select(item, args.Modifiers);
        }
    }

    /// <summary>Moves the dragged column to wherever the pointer is.</summary>
    /// <returns>Whether it moved.</returns>
    bool Reorder(float x) {
        if (reordering is not { } column) {
            return false;
        }

        var from = columns.IndexOf(column);
        // ⚠ No scroll term. `Header.AbsoluteLeft` is inside the scrolled pane now, so it already
        // carries the offset that used to have to be added back here.
        var at = x - Header.AbsoluteLeft;

        for (var i = 0; i < columns.Count; i++) {
            if (at >= offsets[i] && at < offsets[i + 1] && i != from) {
                MoveColumn(from, i);
                return true;
            }
        }

        return false;
    }

    void Tapped(TapEvent args) {
        if (args.Count != 2) {
            return;
        }

        if (Under<DataCell>(args.Source) is { } cell && BeginEdit(cell)) {
            args.Handled = true;
            return;
        }

        if (Under<DataRow>(args.Source) is { Item: { } item }) {
            Activated?.Invoke(this, item);
            args.Handled = true;
        }
    }

    void Keyed(KeyEvent args) {
        if (args.Action != KeyAction.Pressed || view.Count == 0) {
            return;
        }

        var current = anchor >= 0 ? RowIndexOf(anchor) : -1;

        switch (args.Key) {
            case InputKey.Down:
                Step(current + 1, args.Modifiers);
                break;

            case InputKey.Up:
                Step(current <= 0 ? 0 : current - 1, args.Modifiers);
                break;

            case InputKey.Home:
                Step(0, args.Modifiers);
                break;

            case InputKey.End:
                Step(view.Count - 1, args.Modifiers);
                break;

            case InputKey.A when args.Modifiers.HasFlag(ModifierKeys.Control) && MultiSelect:
                selection.Clear();

                foreach (var line in view) {
                    if (line.Item >= 0) {
                        selection.Add(line.Item);
                    }
                }

                Restate();
                break;

            default:
                return;
        }

        args.Handled = true;
    }

    void Step(int row, ModifierKeys modifiers) {
        // Past a group header rather than onto one, because a header is not a row anybody selects
        // and stopping on it makes Down look as though it did nothing.
        while (row >= 0 && row < view.Count && view[row].Item < 0) {
            row++;
        }

        if (row < 0 || row >= view.Count) {
            return;
        }

        Select(view[row].Item, modifiers);

        // The minimum movement that works, for the reason `ScrollView.ScrollIntoView` gives:
        // centring makes a list jump under somebody arrowing down it one row at a time.
        // ⚠ The near edge is the bottom of the sticky header and not the top of the port, because
        // the header is inside the port and covers exactly that much of it. Scrolling a row to
        // `ScrollTop` would put it underneath the heading it belongs to.
        var top = HeaderHeight + (row * RowHeight);

        if (top - HeaderHeight < Scroller.ScrollTop) {
            Scroller.ScrollTop = top - HeaderHeight;
        } else if (top + RowHeight > Scroller.ScrollTop + Scroller.Height) {
            Scroller.ScrollTop = top + RowHeight - Scroller.Height;
        }

        Realise();
    }

    static T? Under<T>(UiElement? element) where T : UiElement {
        for (var walk = element; walk is not null; walk = walk.Parent) {
            if (walk is T found) {
                return found;
            }
        }

        return null;
    }

    /// <summary>Comparing two cell values without knowing what they are.</summary>
    /// <remarks>
    ///     ⚠ <b>Nulls sort first and mixed types fall back to text.</b> A column whose accessor
    ///     returns an <see cref="int" /> for some rows and a <see cref="string" /> for others is a
    ///     bug in the caller, and throwing from a comparison sort turns it into an exception at a
    ///     mouse click rather than a table in a strange order.
    /// </remarks>
    sealed class DefaultComparer : IComparer<object?> {
        public static DefaultComparer Instance { get; } = new();

        public int Compare(object? left, object? right) {
            if (left is null) {
                return right is null ? 0 : -1;
            }

            if (right is null) {
                return 1;
            }

            if (left is IComparable comparable && left.GetType() == right.GetType()) {
                return comparable.CompareTo(right);
            }

            return string.CompareOrdinal(
                Convert.ToString(left, CultureInfo.InvariantCulture),
                Convert.ToString(right, CultureInfo.InvariantCulture)
            );
        }
    }
}
