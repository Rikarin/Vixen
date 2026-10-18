// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Layout;

/// <summary>
///     The table layout algorithm: CSS 2.1 §17 in the separated-borders model, over the same store the
///     other four run on.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The fifth algorithm, and the shape the five audits of <c>Rikarin/Vixen#258</c> said it
///         had: a cell needs a width and an offset from its COLUMN before anything inside it can be
///         laid out, and its height from its ROW, which is the tallest of the cells beside it.</b> So
///         the table decides every column, then measures every cell at its column's width to decide
///         every row, then lays each row out at its row's height — three passes, in that order,
///         because each consumes the one before.
///     </para>
///     <para>
///         ⚠ <b>A row is laid out through <c>CalculateLayoutInternal</c> like any other child, so it
///         gets a rectangle, a cache entry and its <c>HasNewLayout</c> — and it reads the columns off
///         the table that is laying it out, which is an input its cache key does not carry.</b> The
///         key is the row's own size, and a row two whose width and height did not move would answer
///         from the cache while a change to row one's first cell moved every column under it. The row
///         is therefore marked dirty before each visit, which spends one layout per row per
///         generation and keeps the cache for the second visit in the same generation, where the
///         columns are a function of the key.
///     </para>
///     <para>
///         ⚠ <b>No anonymous boxes</b> — see <see cref="Display.Table" /> for what a stray child does
///         instead. And no <c>border-collapse: collapse</c>, no captions, no column or row-group
///         boxes, no spans, and a cell's content sits at the top of its row
///         (<c>vertical-align: top</c>, where CSS's initial value is <c>baseline</c> — the two agree
///         for a row of single-line cells in one font).
///     </para>
/// </remarks>
public sealed partial class LayoutTree {
    // ⚠ Pooled and used as a stack, because the store's steady-state allocation gate is zero bytes a
    // frame and a table nested in a cell pushes its columns above its ancestor's while the ancestor
    // is still laying out the row that holds it.
    float[] tableColumns = [];
    int tableColumnsTop;

    // The table whose row is being laid out, and where that table's columns are. Saved and restored
    // around every row visit, because laying out the row's cells can lay out a nested table.
    int activeTable = -1;
    int activeColumnsAt;
    int activeColumnCount;
    float activeSpacing;

    /// <summary>Whether a node is a row or a cell of a table, whose margins CSS 2.1 §17 does not apply.</summary>
    bool IsTablePart(int index) {
        var parent = links[index].Parent;
        if (parent < 0) {
            return false;
        }

        return styles[index].Display switch {
            Display.TableRow => styles[parent].Display == Display.Table,
            Display.TableCell => styles[parent].Display == Display.TableRow
                && links[parent].Parent >= 0
                && styles[links[parent].Parent].Display == Display.Table,
            _ => false
        };
    }

    /// <summary>The in-flow children a row lines up in its columns.</summary>
    /// <remarks>A child of a table that is not a row is its own one-cell row, so it counts as one.</remarks>
    int TableCellCount(int row) {
        if (styles[row].Display != Display.TableRow) {
            return 1;
        }

        var count = 0;
        foreach (var child in DocumentChildIds(row)) {
            if (IsInFlow(child)) {
                count++;
            }
        }

        return count;
    }

    /// <summary>The <paramref name="column" />-th in-flow cell of a row, or the stray child standing in for one.</summary>
    int TableCellAt(int row, int column) {
        if (styles[row].Display != Display.TableRow) {
            return column == 0 ? row : -1;
        }

        var at = 0;
        foreach (var child in DocumentChildIds(row)) {
            if (!IsInFlow(child)) {
                continue;
            }

            if (at == column) {
                return child;
            }

            at++;
        }

        return -1;
    }

    /// <summary>One axis of <c>border-spacing</c>, in points.</summary>
    float TableSpacing(int index, Dimension dimension) {
        var length = styles[index].BorderSpacing[(int)dimension];
        var value = length.Unit == LayoutUnit.Point ? length.Value : 0f;

        return float.IsNaN(value) ? 0f : MathF.Max(0f, value);
    }

    int PushTableColumns(int count) {
        if (tableColumns.Length < tableColumnsTop + count) {
            Array.Resize(ref tableColumns, int.Max(16, int.Max(tableColumns.Length * 2, tableColumnsTop + count)));
        }

        var at = tableColumnsTop;
        tableColumnsTop += count;
        tableColumns.AsSpan(at, count).Clear();

        return at;
    }

    /// <summary>Lays a table out.</summary>
    void CalculateTableLayoutImpl(
        int index,
        float availableWidth,
        float availableHeight,
        Direction direction,
        SizingMode widthSizingMode,
        SizingMode heightSizingMode,
        float ownerWidth,
        float ownerHeight,
        bool performLayout,
        int currentDepth,
        float marginAxisRow,
        float marginAxisColumn
    ) {
        var mark = tableColumnsTop;

        try {
            LayOutTable(
                index,
                availableWidth,
                availableHeight,
                direction,
                widthSizingMode,
                heightSizingMode,
                ownerWidth,
                ownerHeight,
                performLayout,
                currentDepth,
                marginAxisRow,
                marginAxisColumn
            );
        } finally {
            tableColumnsTop = mark;
        }
    }

    void LayOutTable(
        int index,
        float availableWidth,
        float availableHeight,
        Direction direction,
        SizingMode widthSizingMode,
        SizingMode heightSizingMode,
        float ownerWidth,
        float ownerHeight,
        bool performLayout,
        int currentDepth,
        float marginAxisRow,
        float marginAxisColumn
    ) {
        var insetLeft = results[index].Padding[(int)Edge.Left] + results[index].Border[(int)Edge.Left]
            + ScrollbarGutterAt(index, Edge.Left, direction);
        var insetRight = results[index].Padding[(int)Edge.Right] + results[index].Border[(int)Edge.Right]
            + ScrollbarGutterAt(index, Edge.Right, direction);
        var insetTop = results[index].Padding[(int)Edge.Top] + results[index].Border[(int)Edge.Top]
            + ScrollbarGutterAt(index, Edge.Top, direction);
        var insetBottom = results[index].Padding[(int)Edge.Bottom] + results[index].Border[(int)Edge.Bottom]
            + ScrollbarGutterAt(index, Edge.Bottom, direction);
        var insetRow = insetLeft + insetRight;
        var insetColumn = insetTop + insetBottom;

        var spacingX = TableSpacing(index, Dimension.Width);
        var spacingY = TableSpacing(index, Dimension.Height);

        // ── The grid of slots: one row per in-flow child, as many columns as the widest row ────────
        var rowCount = 0;
        var columnCount = 0;
        var firstRow = -1;

        foreach (var child in DocumentChildIds(index)) {
            if (styles[child].Display == Display.None) {
                if (performLayout) {
                    ZeroOutLayoutRecursively(child);
                }

                continue;
            }

            if (!IsInFlow(child)) {
                continue;
            }

            if (firstRow < 0) {
                firstRow = child;
            }

            rowCount++;
            columnCount = int.Max(columnCount, TableCellCount(child));
        }

        // CSS 2.1 §17.6.1: the spacing sits between the columns AND between the outer columns and the
        // table's padding edge, so n columns have n + 1 of it. A table with no columns has none.
        var horizontalSpacing = columnCount > 0 ? (columnCount + 1) * spacingX : 0f;
        var verticalSpacing = rowCount > 0 ? (rowCount + 1) * spacingY : 0f;

        // ── What the table knows about its own width ─────────────────────────────────────────────
        var statedWidth = ResolvedBoundedDimension(index, Dimension.Width, direction, ownerWidth, ownerHeight);
        var offeredWidth = widthSizingMode == SizingMode.StretchFit || widthSizingMode == SizingMode.FitContent
            ? availableWidth - marginAxisRow
            : float.NaN;

        // ⚠ Every read and write below goes through `tableColumns[at + i]` and never through a span
        // held across a layout call: measuring a cell can lay out a table nested in it, and that
        // table's columns can grow the array this one's live in.
        var columnsAt = PushTableColumns(columnCount * 2);

        // ⚠ §17.5.2.1 needs a width to share out, so an `auto`-width table is laid out automatically
        // whatever its `table-layout` says — CSS Tables 3 and every engine agree.
        var isFixed = styles[index].TableLayout == TableLayout.Fixed && !float.IsNaN(statedWidth);

        float outerWidth;
        if (isFixed) {
            // A stated width is the table's border box, and under StretchFit the parent has already
            // resolved the same declaration into the offer.
            var target = widthSizingMode == SizingMode.StretchFit && !float.IsNaN(offeredWidth)
                ? BoundAxis(index, FlexDirection.Row, direction, offeredWidth, ownerWidth, ownerWidth)
                : statedWidth;

            var used = FixedTableColumns(firstRow, columnsAt, columnCount, MathF.Max(0f, target - insetRow - horizontalSpacing), direction);
            outerWidth = MathF.Max(target, used + horizontalSpacing + insetRow);
        } else {
            var room = !float.IsNaN(statedWidth)
                ? statedWidth - insetRow - horizontalSpacing
                : float.IsNaN(offeredWidth)
                    ? float.NaN
                    : BoundAxis(index, FlexDirection.Row, direction, offeredWidth, ownerWidth, ownerWidth) - insetRow - horizontalSpacing;

            var used = AutoTableColumns(index, columnsAt, columnCount, room, !float.IsNaN(statedWidth), direction, currentDepth);
            outerWidth = MathF.Max(float.IsNaN(statedWidth) ? 0f : statedWidth, used + horizontalSpacing + insetRow);
        }

        outerWidth = MathF.Max(outerWidth, StyleResolution.PaddingAndBorderForAxis(in styles[index], FlexDirection.Row, direction, ownerWidth));

        var innerWidth = MathF.Max(0f, outerWidth - insetRow);
        var rowWidth = 0f;
        for (var column = 0; column < columnCount; column++) {
            rowWidth += tableColumns[columnsAt + column];
        }

        rowWidth += columnCount > 1 ? (columnCount - 1) * spacingX : 0f;

        // ── Rows: the tallest cell, or the row's own height ─────────────────────────────────────
        var rowsAt = PushTableColumns(rowCount);

        var contentHeight = verticalSpacing;
        var rowAt = 0;
        foreach (var row in DocumentChildIds(index)) {
            if (!IsInFlow(row)) {
                continue;
            }

            var height = 0f;
            if (styles[row].Display == Display.TableRow) {
                var stated = ResolvedDimension(row, Dimension.Height, float.NaN, innerWidth, direction);
                if (!float.IsNaN(stated)) {
                    height = stated;
                }
            }

            var cells = TableCellCount(row);
            for (var column = 0; column < cells; column++) {
                var cell = TableCellAt(row, column);
                var width = tableColumns[columnsAt + column];
                var marginRow = TablePartMargin(cell, FlexDirection.Row, width);
                var marginColumn = TablePartMargin(cell, FlexDirection.Column, width);

                // ⚠ The cell's own `height` is resolved HERE, because this store's convention is that
                // the caller resolves a child's stated size: a childless box answers a max-content
                // request with its padding and border and nothing else, so an empty `height: 20px`
                // cell would measure zero and its row would collapse. `MeasureGridItem` resolves it
                // for the same reason. A percentage has no table height to resolve against yet.
                var cellHeight = ResolvedDimension(cell, Dimension.Height, float.NaN, width, direction);
                if (!float.IsNaN(cellHeight)) {
                    cellHeight = BoundAxisWithinMinAndMax(cell, direction, FlexDirection.Column, cellHeight, float.NaN, width);
                }

                CalculateLayoutInternal(
                    cell,
                    width,
                    float.IsNaN(cellHeight) ? float.NaN : cellHeight + marginColumn,
                    direction,
                    SizingMode.StretchFit,
                    float.IsNaN(cellHeight) ? SizingMode.MaxContent : SizingMode.StretchFit,
                    MathF.Max(0f, width - marginRow),
                    float.NaN,
                    performLayout: false,
                    currentDepth
                );

                height = MathF.Max(height, results[cell].MeasuredDimensions[(int)Dimension.Height] + marginColumn);
            }

            tableColumns[rowsAt + rowAt++] = height;
            contentHeight += height;
        }

        // ── The table's height: a stated one is a minimum, and what it adds goes to the rows ──────
        var statedHeight = heightSizingMode == SizingMode.StretchFit
            ? BoundAxis(index, FlexDirection.Column, direction, availableHeight - marginAxisColumn, ownerHeight, ownerWidth)
            : ResolvedBoundedDimension(index, Dimension.Height, direction, ownerWidth, ownerHeight);

        var outerHeight = MathF.Max(
            contentHeight + insetColumn,
            StyleResolution.PaddingAndBorderForAxis(in styles[index], FlexDirection.Column, direction, ownerWidth)
        );

        if (!float.IsNaN(statedHeight) && statedHeight > outerHeight) {
            // CSS 2.1 §17.5.3 leaves the distribution undefined; engines share the extra in proportion
            // to each row's height, and evenly when every row is empty.
            var extra = statedHeight - outerHeight;
            var sum = contentHeight - verticalSpacing;
            for (var at = 0; at < rowCount; at++) {
                tableColumns[rowsAt + at] += sum > 0f ? extra * tableColumns[rowsAt + at] / sum : extra / rowCount;
            }

            outerHeight = statedHeight;
        }

        SetMeasuredDimension(index, Dimension.Width, outerWidth, outerWidth);
        SetMeasuredDimension(index, Dimension.Height, outerHeight, outerHeight);

        if (!performLayout) {
            return;
        }

        // ── Placement ────────────────────────────────────────────────────────────────────────────
        // ⚠ RTL mirrors the columns and nothing else: CSS 2.1 §17.5 puts column one at the right edge
        // of a table whose `direction` is `rtl`, and the rows still run top to bottom.
        var innerHeight = MathF.Max(0f, outerHeight - insetColumn);
        var y = insetTop + spacingY;
        rowAt = 0;

        foreach (var row in DocumentChildIds(index)) {
            if (!IsInFlow(row)) {
                continue;
            }

            var height = tableColumns[rowsAt + rowAt++];
            var rowLeft = insetLeft + spacingX;

            if (styles[row].Display == Display.TableRow) {
                var savedTable = activeTable;
                var savedAt = activeColumnsAt;
                var savedCount = activeColumnCount;
                var savedSpacing = activeSpacing;

                activeTable = index;
                activeColumnsAt = columnsAt;
                activeColumnCount = columnCount;
                activeSpacing = spacingX;

                // See the type's remarks: the columns are an input to this row the cache key lacks.
                flags[row] |= LayoutNodeState.Dirty;

                try {
                    CalculateLayoutInternal(
                        row,
                        rowWidth,
                        height,
                        direction,
                        SizingMode.StretchFit,
                        SizingMode.StretchFit,
                        innerWidth,
                        innerHeight,
                        performLayout: true,
                        currentDepth
                    );
                } finally {
                    activeTable = savedTable;
                    activeColumnsAt = savedAt;
                    activeColumnCount = savedCount;
                    activeSpacing = savedSpacing;
                }

                results[row].Position[(int)Edge.Left] = rowLeft;
                results[row].Position[(int)Edge.Top] = y;
            } else {
                // A stray child is its own one-cell row, in the first column.
                var width = columnCount > 0 ? tableColumns[columnsAt] : 0f;
                var left = direction == Direction.Ltr ? rowLeft : rowLeft + rowWidth - width;
                PlaceTableCell(row, width, height, left, y, direction, currentDepth);
            }

            y += height + spacingY;
        }

        if (EstablishesAbsoluteContainingBlock(index) || currentDepth == 1) {
            LayoutAbsoluteDescendants(
                index,
                index,
                widthSizingMode,
                direction,
                currentDepth,
                0f,
                0f,
                innerWidth,
                innerHeight
            );
        }
    }

    /// <summary>
    ///     A margin of a table's child: none for a real row or cell, CSS 2.1 §17's own rule, and the
    ///     declared one for a stray child, which keeps it inside the slot it stands in.
    /// </summary>
    float TablePartMargin(int child, FlexDirection axis, float widthSize) =>
        IsTablePart(child) ? 0f : StyleResolution.MarginForAxis(in styles[child], axis, widthSize);

    /// <summary>Lays one cell out at its slot's size and puts it at its slot's origin.</summary>
    void PlaceTableCell(int cell, float width, float height, float left, float top, Direction direction, int currentDepth) {
        var marginRow = TablePartMargin(cell, FlexDirection.Row, width);
        var marginColumn = TablePartMargin(cell, FlexDirection.Column, width);

        CalculateLayoutInternal(
            cell,
            width,
            height,
            direction,
            SizingMode.StretchFit,
            SizingMode.StretchFit,
            MathF.Max(0f, width - marginRow),
            MathF.Max(0f, height - marginColumn),
            performLayout: true,
            currentDepth
        );

        var marginLeft = IsTablePart(cell) ? 0f : results[cell].Margin[(int)Edge.Left];
        var marginTop = IsTablePart(cell) ? 0f : results[cell].Margin[(int)Edge.Top];

        results[cell].Position[(int)Edge.Left] = left + marginLeft;
        results[cell].Position[(int)Edge.Top] = top + marginTop;
    }

    /// <summary>Lays a row of a table out: each cell at its column's width and the row's height.</summary>
    /// <remarks>
    ///     Reached only while the table is laying this row out — see <see cref="LayOutTable" /> —
    ///     because the columns are the table's and not the row's. Anywhere else a row is a block.
    /// </remarks>
    void LayOutTableRow(
        int index,
        float availableWidth,
        float availableHeight,
        Direction direction,
        bool performLayout,
        int currentDepth,
        float marginAxisRow,
        float marginAxisColumn
    ) {
        var columnsAt = activeColumnsAt;
        var columnCount = activeColumnCount;
        var spacing = activeSpacing;

        var rowWidth = MathF.Max(0f, availableWidth - marginAxisRow);
        var rowHeight = MathF.Max(0f, availableHeight - marginAxisColumn);

        SetMeasuredDimension(index, Dimension.Width, rowWidth, rowWidth);
        SetMeasuredDimension(index, Dimension.Height, rowHeight, rowHeight);

        if (!performLayout) {
            return;
        }

        var offset = 0f;
        var column = 0;

        foreach (var cell in DocumentChildIds(index)) {
            if (styles[cell].Display == Display.None) {
                ZeroOutLayoutRecursively(cell);
                continue;
            }

            if (!IsInFlow(cell)) {
                continue;
            }

            // ⚠ Read from the array every time rather than through a span taken above: laying a cell
            // out can lay a nested table out, and that table's columns can grow the array.
            var width = column < columnCount ? tableColumns[columnsAt + column] : 0f;
            var left = direction == Direction.Ltr ? offset : rowWidth - offset - width;

            PlaceTableCell(cell, width, rowHeight, left, 0f, direction, currentDepth);

            offset += width + spacing;
            column++;
        }

        if (EstablishesAbsoluteContainingBlock(index) || currentDepth == 1) {
            LayoutAbsoluteDescendants(
                index,
                index,
                SizingMode.StretchFit,
                direction,
                currentDepth,
                0f,
                0f,
                rowWidth,
                rowHeight
            );
        }
    }

    /// <summary>CSS 2.1 §17.5.2.1: the fixed layout's columns, in border-box widths. Returns their sum.</summary>
    /// <remarks>
    ///     <para>
    ///         A first-row cell whose <c>width</c> is not <c>auto</c> decides its column, as its border
    ///         box; every other column shares what is left of <paramref name="room" /> equally. Cells
    ///         in later rows decide nothing, which is the whole point of the algorithm — the columns
    ///         are known once the first row is.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Two cases the sentence above does not cover, and the answer to each.</b> When every
    ///         column is decided and they sum to less than the room, the room is shared in proportion
    ///         to their widths, as every engine does; §17.5.2.1 says only that the table is "at least"
    ///         its width. When they sum to more, nothing shrinks and the table grows past its
    ///         <c>width</c>, which is §17.5.2.1's "the greater of".
    ///     </para>
    /// </remarks>
    float FixedTableColumns(int firstRow, int columnsAt, int columnCount, float room, Direction direction) {
        // A span is safe here and only here: nothing below lays anything out.
        var columns = tableColumns.AsSpan(columnsAt, columnCount);
        var decided = 0f;
        var undecided = 0;

        for (var column = 0; column < columns.Length; column++) {
            var cell = firstRow < 0 ? -1 : TableCellAt(firstRow, column);
            var width = float.NaN;

            if (cell >= 0) {
                width = ResolvedDimension(cell, Dimension.Width, room, room, direction);
                if (!float.IsNaN(width)) {
                    width = MathF.Max(width, StyleResolution.PaddingAndBorderForAxis(in styles[cell], FlexDirection.Row, direction, room));
                }
            }

            columns[column] = width;

            if (float.IsNaN(width)) {
                undecided++;
            } else {
                decided += width;
            }
        }

        if (undecided > 0) {
            var share = MathF.Max(0f, room - decided) / undecided;
            for (var column = 0; column < columns.Length; column++) {
                if (float.IsNaN(columns[column])) {
                    columns[column] = share;
                }
            }

            return decided + share * undecided;
        }

        if (decided < room && columns.Length > 0) {
            var scale = decided > 0f ? room / decided : 0f;
            for (var column = 0; column < columns.Length; column++) {
                columns[column] = decided > 0f ? columns[column] * scale : room / columns.Length;
            }

            return room;
        }

        return decided;
    }

    /// <summary>The automatic layout's columns, in border-box widths. Returns their sum.</summary>
    /// <remarks>
    ///     <para>
    ///         Each column's range runs from the widest min-content of its cells to the widest
    ///         max-content, a cell with a stated width contributing that width (or its min-content, if
    ///         wider) to both ends. Then, against the room:
    ///     </para>
    ///     <list type="bullet">
    ///         <item>at or below the min-content sum, every column is its minimum;</item>
    ///         <item>between the two sums, each column gets its minimum plus the same fraction of its own range;</item>
    ///         <item>
    ///             above the max-content sum — only for a table with a stated width — the extra goes to
    ///             the columns in proportion to their maxima. An <c>auto</c>-width table stops at the
    ///             max-content sum, which is its shrink-to-fit.
    ///         </item>
    ///     </list>
    ///     <para>
    ///         ⚠ <b>Non-normative ground.</b> §17.5.2.2 is explicitly not a specification, and CSS
    ///         Tables 3's automatic layout distinguishes percentage and fixed columns this store has
    ///         no way to write yet. The three branches above agree with it where every column is
    ///         <c>auto</c> or a length.
    ///     </para>
    /// </remarks>
    float AutoTableColumns(
        int index,
        int columnsAt,
        int columnCount,
        float room,
        bool hasStatedWidth,
        Direction direction,
        int currentDepth
    ) {
        // The minima in the first half of the block and the maxima in the second, indexed rather than
        // spanned because measuring a cell can grow the array — see the remarks in `LayOutTable`.
        var maximaAt = columnsAt + columnCount;
        for (var column = 0; column < columnCount; column++) {
            tableColumns[columnsAt + column] = 0f;
            tableColumns[maximaAt + column] = 0f;
        }

        foreach (var row in DocumentChildIds(index)) {
            if (!IsInFlow(row)) {
                continue;
            }

            var cells = TableCellCount(row);
            for (var column = 0; column < cells; column++) {
                var cell = TableCellAt(row, column);
                var margin = TablePartMargin(cell, FlexDirection.Row, float.NaN);

                // CSS Sizing's two contributions, asked the way `LayoutTree.Intrinsic` asks them: the
                // max-content size with nothing on offer, the min-content size as a fit into zero.
                CalculateLayoutInternal(
                    cell,
                    float.NaN,
                    float.NaN,
                    direction,
                    SizingMode.MaxContent,
                    SizingMode.MaxContent,
                    float.NaN,
                    float.NaN,
                    performLayout: false,
                    currentDepth
                );

                var maxContent = results[cell].MeasuredDimensions[(int)Dimension.Width] + margin;

                CalculateLayoutInternal(
                    cell,
                    margin,
                    float.NaN,
                    direction,
                    SizingMode.FitContent,
                    SizingMode.MaxContent,
                    float.NaN,
                    float.NaN,
                    performLayout: false,
                    currentDepth
                );

                var minContent = results[cell].MeasuredDimensions[(int)Dimension.Width] + margin;

                var stated = ResolvedDimension(cell, Dimension.Width, float.NaN, float.NaN, direction);
                if (!float.IsNaN(stated)) {
                    minContent = MathF.Max(minContent, stated + margin);
                    maxContent = minContent;
                }

                tableColumns[columnsAt + column] = MathF.Max(tableColumns[columnsAt + column], minContent);
                tableColumns[maximaAt + column] = MathF.Max(tableColumns[maximaAt + column], MathF.Max(minContent, maxContent));
            }
        }

        var minima = tableColumns.AsSpan(columnsAt, columnCount);
        var maxima = tableColumns.AsSpan(maximaAt, columnCount);

        var minSum = 0f;
        var maxSum = 0f;
        for (var column = 0; column < minima.Length; column++) {
            minSum += minima[column];
            maxSum += maxima[column];
        }

        var target = float.IsNaN(room) ? maxSum : hasStatedWidth ? room : MathF.Min(room, maxSum);

        if (target <= minSum) {
            return minSum;
        }

        if (target <= maxSum) {
            var fraction = maxSum > minSum ? (target - minSum) / (maxSum - minSum) : 0f;
            for (var column = 0; column < minima.Length; column++) {
                minima[column] += (maxima[column] - minima[column]) * fraction;
            }

            return target;
        }

        for (var column = 0; column < minima.Length; column++) {
            minima[column] = maxSum > 0f
                ? maxima[column] + (target - maxSum) * maxima[column] / maxSum
                : target / minima.Length;
        }

        return minima.Length > 0 ? target : 0f;
    }
}
