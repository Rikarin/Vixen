// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Layout;

/// <summary>
///     CSS Grid §8: where each item goes, and how big the implicit grid has to be to hold them all.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Placement runs before any track has a size, and it must.</b> §12's whole input is
///         "which items are in which tracks", and an item's contribution is attributed to the tracks
///         it spans — so the grid's <i>shape</i> is settled with no arithmetic in it at all, purely
///         from line numbers and spans. That separation is the reason grid is two algorithms rather
///         than one, and it is why this file has no floats in it.
///     </para>
///     <para>
///         ⚠ <b>The implicit grid can start before the explicit one.</b> An item at
///         <c>grid-column: -1 / span 3</c> in a two-column template, or one placed at line −5 of a
///         grid that has three lines, creates tracks on the <i>start</i> side. Nothing downstream
///         wants negative indices, so the whole coordinate space is resolved first in explicit-
///         relative terms and then shifted so that the leftmost line is zero. The shift is
///         <see cref="GridPlacementResult.ColumnOffset" />, and forgetting to apply it to one of the
///         four fields is the way an item ends up one track out only when something else is placed
///         negatively.
///     </para>
/// </remarks>
public sealed partial class LayoutTree {
    /// <summary>An item's resolved position on one axis, before the implicit shift.</summary>
    /// <param name="Start">The first track, or <see cref="int.MinValue" /> when auto-placement decides.</param>
    /// <param name="Span">How many tracks. Always at least one.</param>
    readonly record struct AxisPlacement(int Start, int Span) {
        public bool IsDefinite => Start != int.MinValue;

        public static AxisPlacement Auto(int span) => new(int.MinValue, span);
    }

    /// <summary>What §8 settled about a whole container.</summary>
    /// <param name="ItemsAt">Where the item array starts in the scratch.</param>
    /// <param name="ItemCount">How many in-flow items there are.</param>
    /// <param name="Columns">How many column tracks the final grid has.</param>
    /// <param name="Rows">How many row tracks the final grid has.</param>
    /// <param name="ColumnOffset">How many implicit columns were prepended before the explicit grid.</param>
    /// <param name="RowOffset">How many implicit rows were prepended before the explicit grid.</param>
    readonly record struct GridPlacementResult(
        int ItemsAt,
        int ItemCount,
        int Columns,
        int Rows,
        int ColumnOffset,
        int RowOffset
    );

    /// <summary>Resolves one axis of one item, per CSS Grid §8.3's line-resolution rules.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The two-span case is not an error, it is a documented deletion.</b> §8.3: "If the
    ///         placement contains two spans, remove the one contributed by the end grid-placement
    ///         property." So <c>grid-row: span 2 / span 3</c> is an auto-placed item spanning
    ///         <i>two</i> rows, not five and not three.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Equal lines are not a zero-track area.</b> A grid area must be at least one track,
    ///         so <c>grid-column: 2 / 2</c> resolves to the single track between lines 2 and 3, and a
    ///         reversed pair is swapped rather than rejected. An implementation that allowed a span of
    ///         zero would divide by it in §12.5.1.
    ///     </para>
    /// </remarks>
    /// <param name="start">The start property.</param>
    /// <param name="end">The end property.</param>
    /// <param name="explicitCount">How many tracks the template declared on this axis.</param>
    /// <returns>The resolved start and span.</returns>
    static AxisPlacement ResolveAxisPlacement(GridPlacement start, GridPlacement end, int explicitCount) {
        // §8.3: two spans is one span. Done first so every branch below sees at most one.
        if (start.Kind == GridPlacementKind.Span && end.Kind == GridPlacementKind.Span) {
            end = GridPlacement.Auto;
        }

        // A span against nothing is a span from wherever auto-placement puts it.
        if (start.Kind == GridPlacementKind.Span && end.IsAuto) {
            return AxisPlacement.Auto(start.Value);
        }

        if (start.IsAuto && end.Kind == GridPlacementKind.Span) {
            return AxisPlacement.Auto(end.Value);
        }

        if (start.IsAuto && end.IsAuto) {
            return AxisPlacement.Auto(1);
        }

        // Only the end is definite: the item ends there and is one track long, unless its own start
        // said how many tracks to count back.
        if (start.IsAuto || start.Kind == GridPlacementKind.Span) {
            var endLine = ResolveLine(end.Value, explicitCount);
            var span = start.Kind == GridPlacementKind.Span ? start.Value : 1;

            return new AxisPlacement(endLine - span, span);
        }

        var startLine = ResolveLine(start.Value, explicitCount);

        if (end.IsAuto) {
            return new AxisPlacement(startLine, 1);
        }

        if (end.Kind == GridPlacementKind.Span) {
            return new AxisPlacement(startLine, end.Value);
        }

        var other = ResolveLine(end.Value, explicitCount);

        if (other < startLine) {
            (startLine, other) = (other, startLine);
        }

        return new AxisPlacement(startLine, int.Max(1, other - startLine));
    }

    /// <summary>A line number as a zero-based track index, counting from either end.</summary>
    /// <remarks>
    ///     ⚠ Line 1 is the start edge of the <i>explicit</i> grid and line −1 is its end edge, so −1
    ///     is index <paramref name="explicitCount" /> and not index −1. There is no line 0;
    ///     <see cref="GridPlacement.Line" /> has already turned one into <c>auto</c>.
    /// </remarks>
    static int ResolveLine(int line, int explicitCount) =>
        line > 0 ? line - 1 : explicitCount + 1 + line;

    /// <summary>
    ///     Places every in-flow child into the grid, growing the implicit grid to fit.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The four passes are ordered, and the order is the specification's.</b> §8.5 places
    ///     fully-positioned items first so that the auto-placement cursor knows what it is avoiding,
    ///     then the items locked to one axis, then works out how wide the implicit grid ended up, and
    ///     only then runs the cursor over what is left. Running the cursor earlier makes a definitely
    ///     positioned item collide with an auto-placed one that had no way to know it was coming, and
    ///     the symptom is two items in one cell rather than an exception.
    /// </remarks>
    GridPlacementResult PlaceGridItems(int index, int explicitColumns, int explicitRows) {
        var itemsAt = Scratch.AllocateItems(links[index].ChildCount);
        var count = 0;

        var flow = styles[index].GridAutoFlow;
        var rowFlow = flow is GridAutoFlow.Row or GridAutoFlow.RowDense;
        var dense = flow is GridAutoFlow.RowDense or GridAutoFlow.ColumnDense;

        // Explicit-relative coordinates, which may go negative before the shift at the end.
        var minColumn = 0;
        var minRow = 0;
        var maxColumn = explicitColumns;
        var maxRow = explicitRows;

        foreach (var child in ChildIds(index)) {
            if (styles[child].Display == Display.None || styles[child].PositionType == PositionType.Absolute) {
                continue;
            }

            ref var item = ref Scratch.Item(itemsAt + count);
            item.Node = child;

            // ⚠ A named area is turned into a line here rather than in `ResolveAxisPlacement`,
            // because a name is resolved against the CONTAINER and that method is static and knows
            // only the two properties. See `ResolveNamedPlacement`.
            var column = ResolveAxisPlacement(
                ResolveNamedPlacement(index, child, Edge.Left, styles[child].GridColumnStart),
                ResolveNamedPlacement(index, child, Edge.Right, styles[child].GridColumnEnd),
                explicitColumns
            );

            var row = ResolveAxisPlacement(
                ResolveNamedPlacement(index, child, Edge.Top, styles[child].GridRowStart),
                ResolveNamedPlacement(index, child, Edge.Bottom, styles[child].GridRowEnd),
                explicitRows
            );

            // ⚠ Clamped here rather than trusted, because the corpus writes `grid-column-start: -19553`
            // and `span 20000` and CSS lets an implementation cap the grid. Without this the extents
            // below become the size of the arrays §12 allocates. See LayoutLimits.MaximumGridTracks.
            column = ClampPlacement(column);
            row = ClampPlacement(row);

            item.ColumnStart = column.Start;
            item.ColumnSpan = column.Span;
            item.RowStart = row.Start;
            item.RowSpan = row.Span;

            if (column.IsDefinite) {
                minColumn = int.Min(minColumn, column.Start);
                maxColumn = int.Max(maxColumn, column.Start + column.Span);
            }

            if (row.IsDefinite) {
                minRow = int.Min(minRow, row.Start);
                maxRow = int.Max(maxRow, row.Start + row.Span);
            }

            count++;
        }

        // ── The shift ───────────────────────────────────────────────────────────────────────────
        // Everything below works in final coordinates, where track 0 is the first track that exists.
        var columnOffset = -minColumn;
        var rowOffset = -minRow;

        for (var at = 0; at < count; at++) {
            ref var item = ref Scratch.Item(itemsAt + at);

            if (item.ColumnStart != int.MinValue) {
                item.ColumnStart += columnOffset;
            }

            if (item.RowStart != int.MinValue) {
                item.RowStart += rowOffset;
            }
        }

        var columns = maxColumn + columnOffset;
        var rows = maxRow + rowOffset;

        // ── §8.5 step 3: the minor axis is fixed before the cursor runs ─────────────────────────
        // ⚠ <b>The implicit grid grows on the minor axis exactly once, and it happens here.</b>
        // "If the largest span among all the items without a definite position is larger than the
        // size of the implicit grid, add tracks to accommodate it." Doing it lazily inside the
        // cursor instead makes the wrap point move as items are placed, so the same document lays
        // out differently depending on which item happened to be widest — and the wide item lands on
        // its own row rather than beside the narrow ones.
        var widestMinorSpan = 1;

        for (var at = 0; at < count; at++) {
            ref var item = ref Scratch.Item(itemsAt + at);
            var minorDefinite = rowFlow ? item.ColumnStart != int.MinValue : item.RowStart != int.MinValue;

            if (!minorDefinite) {
                widestMinorSpan = int.Max(widestMinorSpan, rowFlow ? item.ColumnSpan : item.RowSpan);
            }
        }

        if (rowFlow) {
            columns = int.Max(columns, widestMinorSpan);
        } else {
            rows = int.Max(rows, widestMinorSpan);
        }

        // ── §8.5 steps 2 and 4: the cursor ──────────────────────────────────────────────────────
        // The two axes are symmetric: `grid-auto-flow: column` is this algorithm with the roles
        // swapped, so it is written once against a "major"/"minor" pair rather than twice.
        //
        // ⚠ <b>Two passes, and the order is load-bearing.</b> §8.5 settles every item that is locked
        // to a major-axis line before it settles any item that is free on both — otherwise a
        // fully-auto item can take the cell that a later item's `grid-row: 3` had already claimed,
        // and the collision is silent. `IsFree` reads every *placed* item rather than only the ones
        // earlier in document order for the same reason.
        var cursorMajor = 0;
        var cursorMinor = 0;
        var minorLimit = rowFlow ? columns : rows;

        for (var pass = 0; pass < 2; pass++) {
            // ⚠ <b>§8.5 step 4 opens by RESETTING the cursor, and the two passes therefore do not
            // share one.</b> "Reset the auto-placement cursor to the start-most row and column line
            // in the implicit grid" is the first sentence of the step, and it is there because step
            // 2 has just walked every major-locked item — including ones far down the grid — and the
            // fully-auto items are not meant to start from wherever that walk finished.
            // `grid_placement_auto_negative` is three items in a 2x2 grid: one at `grid-row: 2`,
            // which pass 0 places and leaves the cursor on row 2, and one fully auto that belongs in
            // the free cell of row 1 and was landing beside it in row 2 instead.
            //
            // ⚠ Both of the fixtures this closes were filed under grid's track cap and neither is
            // about it. `grid_overlarge_auto_flow_column_large_negative_row_start` has a
            // `grid-row-start: -19553` in it, so it read as an over-large grid — but the number the
            // fixture actually disagreed about was which column the one auto-placed item took, and
            // that is this sentence. A large number in a fixture is not evidence about which rule it
            // is testing.
            cursorMajor = 0;
            cursorMinor = 0;

            for (var at = 0; at < count; at++) {
                ref var item = ref Scratch.Item(itemsAt + at);

                var majorStart = rowFlow ? item.RowStart : item.ColumnStart;
                var minorStart = rowFlow ? item.ColumnStart : item.RowStart;
                var majorSpan = rowFlow ? item.RowSpan : item.ColumnSpan;
                var minorSpan = rowFlow ? item.ColumnSpan : item.RowSpan;

                if (majorStart != int.MinValue && minorStart != int.MinValue) {
                    continue;
                }

                var lockedToMajor = majorStart != int.MinValue;

                // Pass 0 takes the items locked to a major line; pass 1 takes everything else.
                if (pass == 0 != lockedToMajor) {
                    continue;
                }

                int major;
                int minor;

                if (lockedToMajor) {
                    // ⚠ The sparse cursor only carries its minor position forward while it is still
                    // on the same major line; a locked item on a later line restarts from the first.
                    var from = dense || majorStart != cursorMajor ? 0 : cursorMinor;

                    major = majorStart;
                    minor = FindFreeMinor(itemsAt, count, at, rowFlow, majorStart, majorSpan, minorSpan, from);
                } else if (minorStart != int.MinValue) {
                    major = FindFreeMajor(itemsAt, count, at, rowFlow, minorStart, minorSpan, majorSpan, dense ? 0 : cursorMajor);
                    minor = minorStart;
                } else {
                    (major, minor) = FindFreeCell(
                        itemsAt,
                        count,
                        at,
                        rowFlow,
                        majorSpan,
                        minorSpan,
                        dense ? 0 : cursorMajor,
                        dense ? 0 : cursorMinor,
                        minorLimit
                    );
                }

                Assign(ref item, rowFlow, major, minor);

                if (!dense) {
                    cursorMajor = major;
                    cursorMinor = minor + minorSpan;
                }

                columns = int.Max(columns, item.ColumnEnd);
                rows = int.Max(rows, item.RowEnd);
            }
        }

        // ── The grid is clamped, so the items have to be clamped to the same grid ───────────────
        // ⚠ <b>Clamping the extents without clamping the items is an out-of-bounds read, not a
        // wrong number.</b> The corpus writes `grid-column-start: 32767` and `span 20000`, so the
        // extents genuinely exceed what this store will allocate; capping the track count alone
        // leaves items pointing past the last track. Chrome's own answer to these fixtures is a
        // clamped grid, so pulling the item into it is also the closer behaviour — and the failure
        // mode without it is an exception in the middle of a layout pass.
        columns = int.Clamp(columns, 0, LayoutLimits.MaximumGridTracks);
        rows = int.Clamp(rows, 0, LayoutLimits.MaximumGridTracks);

        if (count > 0) {
            columns = int.Max(columns, 1);
            rows = int.Max(rows, 1);
        }

        for (var at = 0; at < count; at++) {
            ref var item = ref Scratch.Item(itemsAt + at);

            item.ColumnStart = int.Clamp(item.ColumnStart, 0, int.Max(0, columns - 1));
            item.ColumnSpan = int.Clamp(item.ColumnSpan, 1, int.Max(1, columns - item.ColumnStart));
            item.RowStart = int.Clamp(item.RowStart, 0, int.Max(0, rows - 1));
            item.RowSpan = int.Clamp(item.RowSpan, 1, int.Max(1, rows - item.RowStart));
        }

        return new GridPlacementResult(itemsAt, count, columns, rows, columnOffset, rowOffset);

        static void Assign(ref GridItem item, bool rowFlow, int major, int minor) {
            if (rowFlow) {
                (item.RowStart, item.ColumnStart) = (major, minor);
            } else {
                (item.ColumnStart, item.RowStart) = (major, minor);
            }
        }
    }

    /// <summary>Keeps one axis of one item inside the grid the store is willing to allocate.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the FIRST of two saturating clamps and the one that binds.</b> The other is
    ///     at the end of <see cref="PlaceGridItems" />, where an item is pulled inside the final
    ///     extent; that one is the one <c>GridKnownGaps.txt</c> names, and it is not reached with
    ///     anything to fix. Two items whose authored lines are BOTH past
    ///     <see cref="LayoutLimits.MaximumGridTracks" /> are saturated onto the same start here,
    ///     before any extent exists, and merge into one cell — measured as a max-content grid of two
    ///     50-point items coming out 50 wide with both at x=0 for lines 70 000 and 80 000, and 100
    ///     wide for lines 65 534 and 65 536. Collapsing the empty runs has to replace both clamps to
    ///     change anything; replacing either alone leaves the merge where it was.
    /// </remarks>
    static AxisPlacement ClampPlacement(AxisPlacement placement) {
        var span = int.Clamp(placement.Span, 1, LayoutLimits.MaximumGridTracks);

        if (!placement.IsDefinite) {
            return AxisPlacement.Auto(span);
        }

        var start = int.Clamp(placement.Start, -LayoutLimits.MaximumGridTracks, LayoutLimits.MaximumGridTracks);

        return new AxisPlacement(start, int.Min(span, LayoutLimits.MaximumGridTracks - int.Max(0, start)));
    }

    /// <summary>The first free minor-axis position in a fixed major-axis band.</summary>
    /// <remarks>
    ///     ⚠ The search has no upper bound, and that is §8.5's behaviour rather than an oversight: an
    ///     item locked to a row is never moved to another row, so the implicit grid grows along the
    ///     minor axis until the item fits. The loop terminates because each step past the last placed
    ///     item is free.
    /// </remarks>
    int FindFreeMinor(int itemsAt, int count, int self, bool rowFlow, int major, int majorSpan, int minorSpan, int from) {
        var at = int.Max(0, from);

        while (!IsFree(itemsAt, count, self, rowFlow, major, majorSpan, at, minorSpan)) {
            at++;
        }

        return at;
    }

    /// <summary>The first free major-axis position in a fixed minor-axis band.</summary>
    int FindFreeMajor(int itemsAt, int count, int self, bool rowFlow, int minor, int minorSpan, int majorSpan, int from) {
        var at = int.Max(0, from);

        while (!IsFree(itemsAt, count, self, rowFlow, at, majorSpan, minor, minorSpan)) {
            at++;
        }

        return at;
    }

    /// <summary>The first free cell at or after a cursor, scanning the minor axis inside the major.</summary>
    (int Major, int Minor) FindFreeCell(
        int itemsAt,
        int count,
        int self,
        bool rowFlow,
        int majorSpan,
        int minorSpan,
        int fromMajor,
        int fromMinor,
        int minorLimit
    ) {
        var major = int.Max(0, fromMajor);
        var minor = int.Max(0, fromMinor);

        while (true) {
            // ⚠ An item that is wider than the whole grid does not wrap forever. §8.5 says an item
            // whose span exceeds the minor-axis size starts at the first line, and the implicit grid
            // grows to hold it — without this the loop below never finds a fitting position.
            if (minorSpan >= minorLimit) {
                return (FindFreeMajor(itemsAt, count, self, rowFlow, 0, minorSpan, majorSpan, major), 0);
            }

            if (minor + minorSpan > minorLimit) {
                major++;
                minor = 0;
                continue;
            }

            if (IsFree(itemsAt, count, self, rowFlow, major, majorSpan, minor, minorSpan)) {
                return (major, minor);
            }

            minor++;
        }
    }

    /// <summary>Whether a rectangle collides with anything already placed.</summary>
    /// <remarks>
    ///     ⚠ <b>A linear scan over the placed items rather than an occupancy bitmap</b>, because the
    ///     bitmap's size is the product of the two axes and this store admits ten thousand tracks on
    ///     each. A grid whose template is <c>repeat(10000, 0px)</c> — which the corpus contains —
    ///     would want a hundred million cells to answer a question about the two items actually in
    ///     it. The scan is O(placed) per probe, which is the wrong complexity for a grid with
    ///     thousands of auto-placed items and the right one for every grid that fits on a screen;
    ///     see the README's note on what this costs.
    /// </remarks>
    bool IsFree(int itemsAt, int count, int self, bool rowFlow, int major, int majorSpan, int minor, int minorSpan) {
        var rowStart = rowFlow ? major : minor;
        var rowSpan = rowFlow ? majorSpan : minorSpan;
        var columnStart = rowFlow ? minor : major;
        var columnSpan = rowFlow ? minorSpan : majorSpan;

        // ⚠ Every placed item, not only the ones before this one in document order. An item that is
        // definitely positioned occupies its area from the moment §8.3 resolved it, whatever its
        // document position — which is the whole reason §8.5 does the definite ones in a pass of
        // their own.
        for (var at = 0; at < count; at++) {
            if (at == self) {
                continue;
            }

            ref var other = ref Scratch.Item(itemsAt + at);

            if (other.RowStart == int.MinValue || other.ColumnStart == int.MinValue) {
                continue;
            }

            if (rowStart < other.RowEnd
                && other.RowStart < rowStart + rowSpan
                && columnStart < other.ColumnEnd
                && other.ColumnStart < columnStart + columnSpan) {
                return false;
            }
        }

        return true;
    }
}
