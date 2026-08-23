// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Layout;

/// <summary>
///     The grid layout algorithm: CSS Grid §7 through §12, over the same store the other two run on.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The store's third algorithm, and the one that needed something on the <i>input</i>
///         side.</b> Block's whole cost was three outputs — a collapsible margin at each end and a
///         flag — because a block child has to report what escaped past its edges. Grid reports
///         nothing unusual: it is a barrier to margin collapsing exactly as a flex container is, so
///         its honest answer to all three is "my own margin, and no", which
///         <c>CalculateLayoutImpl</c> already wrote before dispatching here. What grid needed instead
///         is a style field that is not a fixed number of bytes, and that is
///         <see cref="TrackArena" />.
///     </para>
///     <para>
///         ⚠ <b>Two axes, in order, and the order is not symmetric.</b> Columns are sized first
///         because an item's height depends on its width and not the other way round. Everything
///         between the two passes — the container's own inline size, the inline alignment of the
///         tracks, each item's resolved width — is an input to the row pass.
///     </para>
///     <para>
///         Named areas (<c>grid-template-areas</c>) are <b>not implemented</b>, and the reason is
///         written down rather than left to be discovered: B0's corpus contains no fixture that sets
///         the property — Taffy's own XML harness leaves it at its default — so it is the one part of
///         grid with no oracle at all. Implementing it against expectations of our own devising would
///         have put untested code behind a green suite. See the README.
///     </para>
/// </remarks>
public sealed partial class LayoutTree {
    // ⚠ Pooled rather than allocated per pass, because the store's steady-state allocation gate is
    // zero bytes a frame and a grid that laid out last frame must not allocate this frame. Created
    // on first use, so a tree with no grid in it pays nothing.
    GridScratch? gridScratch;

    GridScratch Scratch => gridScratch ??= new GridScratch();

    /// <summary>The three numbers §12 wants from one item on one axis.</summary>
    /// <param name="Minimum">
    ///     CSS Sizing's <i>minimum contribution</i>: the smallest outer size the item can have.
    /// </param>
    /// <param name="MinContent">Its min-content contribution.</param>
    /// <param name="MaxContent">Its max-content contribution.</param>
    readonly record struct GridContribution(float Minimum, float MinContent, float MaxContent);

    /// <summary>Lays a grid container out.</summary>
    void CalculateGridLayoutImpl(
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
        var mark = Scratch.Mark;

        try {
            LayOutGrid(
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
            // ⚠ The watermark comes back even if a measure function threw, because the alternative is
            // a scratch stack that only ever grows and a second exception nobody can read.
            Scratch.Restore(mark);
        }
    }

    void LayOutGrid(
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
        var insetLeft = results[index].Padding[(int) Edge.Left] + results[index].Border[(int) Edge.Left];
        var insetRight = results[index].Padding[(int) Edge.Right] + results[index].Border[(int) Edge.Right];
        var insetTop = results[index].Padding[(int) Edge.Top] + results[index].Border[(int) Edge.Top];
        var insetBottom = results[index].Padding[(int) Edge.Bottom] + results[index].Border[(int) Edge.Bottom];
        var insetRow = insetLeft + insetRight;
        var insetColumn = insetTop + insetBottom;

        var columnGap = StyleResolution.GapForAxis(in styles[index], FlexDirection.Row, ownerWidth);
        var rowGap = StyleResolution.GapForAxis(in styles[index], FlexDirection.Column, ownerWidth);

        // ── What the container knows about its own size before it looks at anything ─────────────
        var definiteWidth = widthSizingMode == SizingMode.StretchFit
            ? BoundAxis(index, FlexDirection.Row, direction, availableWidth - marginAxisRow, ownerWidth, ownerWidth)
            : ResolvedBoundedDimension(index, Dimension.Width, direction, ownerWidth, ownerHeight);

        var definiteHeight = heightSizingMode == SizingMode.StretchFit
            ? BoundAxis(index, FlexDirection.Column, direction, availableHeight - marginAxisColumn, ownerHeight, ownerWidth)
            : ResolvedBoundedDimension(index, Dimension.Height, direction, ownerWidth, ownerHeight);

        var innerWidth = float.IsNaN(definiteWidth) ? float.NaN : MathF.Max(0f, definiteWidth - insetRow);
        var innerHeight = float.IsNaN(definiteHeight) ? float.NaN : MathF.Max(0f, definiteHeight - insetColumn);

        // ⚠ <b>A grid that already knows its own inline size resolves its percentage column gutter
        // against that, and not against its owner.</b> CSS Box Alignment §8 measures the gutter
        // against the content box of the element the gutter belongs to — so a `column-gap: 20%` on a
        // 100-point grid is 20 whatever the owner happens to be, and reading the owner's size makes
        // the same declaration mean different things in the same box. The owner is still the basis
        // while this grid's size is unknown, because at that point there is nothing better; the
        // second pass below revisits it once the max-content pass has produced a number.
        if (!float.IsNaN(innerWidth)) {
            columnGap = StyleResolution.GapForAxis(in styles[index], FlexDirection.Row, innerWidth);
        }

        // ── §7.2.3.2, then §8 ───────────────────────────────────────────────────────────────────
        var explicitColumns = ExplicitTrackCount(in styles[index].GridTemplateColumns, innerWidth, columnGap);
        var explicitRows = ExplicitTrackCount(in styles[index].GridTemplateRows, innerHeight, rowGap);

        var placement = PlaceGridItems(index, explicitColumns, explicitRows);

        var columnsAt = BuildGridTracks(
            in styles[index].GridTemplateColumns,
            in styles[index].GridAutoColumns,
            placement.Columns,
            placement.ColumnOffset,
            explicitColumns,
            innerWidth
        );

        CollapseAutoFitTracks(in styles[index].GridTemplateColumns, in placement, columnsAt, explicitColumns, inline: true);

        // ── The inline axis ─────────────────────────────────────────────────────────────────────
        var columnAxis = new GridAxis(
            Inline: true,
            columnsAt,
            placement.Columns,
            placement.ItemsAt,
            placement.ItemCount,
            innerWidth,
            columnGap,
            ConstraintFor(widthSizingMode, innerWidth),
            StretchesTracks(styles[index].JustifyContent)
        );

        SizeGridTracks(in columnAxis, direction, ownerWidth, ownerHeight, currentDepth);

        var contentWidth = UsedTrackSpace(in columnAxis);

        float outerWidth;

        // ⚠ The same width with the grid's own min and max left off, because CSS Flexbox §9.2's
        // flex base size is the measurement before that clamp — see
        // LayoutResult.UnclampedMeasuredDimensions. It follows every step below EXCEPT the clamp:
        // a fit-content ceiling and a definite width are how the size is decided rather than bounds
        // laid over the decision, so both apply to this figure too.
        float unboundedWidth;

        if (widthSizingMode == SizingMode.StretchFit) {
            outerWidth = definiteWidth;
            unboundedWidth = definiteWidth;
        } else {
            outerWidth = BoundAxis(index, FlexDirection.Row, direction, contentWidth + insetRow, ownerWidth, ownerWidth);
            unboundedWidth = MathF.Max(contentWidth + insetRow, insetRow);

            // ⚠ <b>A fit-content container that did not fit re-sizes its tracks, and one that did
            // must not.</b> CSS Sizing §5.1 makes fit-content the max-content size clamped to the
            // available space, so the first pass has to be a max-content one — but once the clamp
            // bites, the tracks were sized against a width the container does not have, and an
            // `1fr` column would keep the width it wanted rather than shrinking.
            var fitCeiling = widthSizingMode == SizingMode.FitContent && !float.IsNaN(availableWidth)
                ? MathF.Max(availableWidth - marginAxisRow, insetRow)
                : float.PositiveInfinity;

            var clamped = MathF.Min(outerWidth, fitCeiling);

            // ⚠ <b>A container sized under an intrinsic constraint re-sizes its tracks too, and for
            // a reason the clamp never reaches: the constraint itself is an input to §12.5.</b> Step
            // 3.3 opens "if the grid container is being sized under a max-content constraint", and
            // under that constraint an `auto` track absorbs a spanning item's max-content
            // contribution alongside the `max-content` ones — see <see cref="IsAffectedBy" />. That
            // is the right answer to the question the first pass asks, which is "how wide would this
            // grid like to be". It is the wrong answer to the question the boxes are drawn from,
            // which is "how wide is it" — and by the time the width falls out of the first pass, that
            // second question has a definite answer. `max-content auto` spanned by one item wants
            // 60 + 20 once the 80 is known, and reports 40 + 40 if the max-content pass is left
            // standing: the `auto` track keeps a share it was only ever lent while the width was
            // unknown. So the intrinsic pass measures, and a definite pass lays out.
            var measuredIntrinsically = columnAxis.Constraint != GridSizingConstraint.Definite;

            if (clamped < outerWidth - 1e-4f || !float.IsNaN(definiteWidth) || measuredIntrinsically) {
                outerWidth = float.IsNaN(definiteWidth) ? clamped : definiteWidth;
                unboundedWidth = float.IsNaN(definiteWidth) ? MathF.Min(unboundedWidth, fitCeiling) : definiteWidth;
                innerWidth = MathF.Max(0f, outerWidth - insetRow);

                ResetTracks(columnsAt, placement.Columns);

                // ⚠ <b>A percentage gutter is a fraction of THIS grid's content box, not of its
                // owner's.</b> CSS Box Alignment §8: `column-gap` percentages resolve against the
                // inline size of the content box of the element the gutter belongs to. That size is
                // the one number the first pass does not have — which is why the first pass resolves
                // the percentage to nothing and this one, holding the answer, resolves it for real.
                // `column-gap: 20%` on a grid that measures 100 wide is 20 points of gutter, and the
                // tracks it separates are re-sized around it: a `max-content` pair spanned by a
                // 60-point item is 20 + 20 with the gutter and 30 + 30 without.
                //
                // ⚠ The container does NOT then re-measure. Its width was settled by the max-content
                // pass, and a gutter that widens the tracks past it overflows rather than growing the
                // box — which is exactly what Chrome draws, tracks ending at 120 inside a 100-point
                // grid. Feeding the new content width back would make the percentage chase itself.
                columnAxis = columnAxis with {
                    AvailableSpace = innerWidth,
                    Gap = StyleResolution.GapForAxis(in styles[index], FlexDirection.Row, innerWidth),
                    Constraint = GridSizingConstraint.Definite
                };

                SizeGridTracks(in columnAxis, direction, ownerWidth, ownerHeight, currentDepth);
            }
        }

        innerWidth = MathF.Max(0f, outerWidth - insetRow);
        columnAxis = columnAxis with { AvailableSpace = innerWidth };

        // Each item's inline size, which the row pass measures against.
        for (var at = 0; at < placement.ItemCount; at++) {
            ref var item = ref Scratch.Item(placement.ItemsAt + at);
            item.ResolvedInlineSize = AreaSize(in columnAxis, item.ColumnStart, item.ColumnSpan);
        }

        // ── §11.8, and it has to happen here ────────────────────────────────────────────────────
        // The shim is an input to §12 rather than an output of it, so it is resolved after the
        // columns (an item's baseline depends on the inline size it was given) and before the rows.
        ResolveBaselineShims(index, placement.ItemsAt, placement.ItemCount, direction, ownerWidth, ownerHeight, currentDepth);

        // ── The block axis ──────────────────────────────────────────────────────────────────────
        var rowsAt = BuildGridTracks(
            in styles[index].GridTemplateRows,
            in styles[index].GridAutoRows,
            placement.Rows,
            placement.RowOffset,
            explicitRows,
            innerHeight
        );

        CollapseAutoFitTracks(in styles[index].GridTemplateRows, in placement, rowsAt, explicitRows, inline: false);

        var rowAxis = new GridAxis(
            Inline: false,
            rowsAt,
            placement.Rows,
            placement.ItemsAt,
            placement.ItemCount,
            innerHeight,
            rowGap,
            ConstraintFor(heightSizingMode, innerHeight),
            StretchesTracks(styles[index].AlignContent)
        );

        SizeGridTracks(in rowAxis, direction, ownerWidth, ownerHeight, currentDepth);

        var contentHeight = UsedTrackSpace(in rowAxis);

        var outerHeight = heightSizingMode == SizingMode.StretchFit
            ? definiteHeight
            : BoundAxis(index, FlexDirection.Column, direction, contentHeight + insetColumn, ownerHeight, ownerWidth);

        var unboundedHeight = heightSizingMode == SizingMode.StretchFit
            ? definiteHeight
            : MathF.Max(contentHeight + insetColumn, insetColumn);

        innerHeight = MathF.Max(0f, outerHeight - insetColumn);

        // ⚠ <b>The block axis takes the same definite second pass the inline axis takes, and for the
        // same reason — but the input that makes it necessary is different.</b> Above, the first pass
        // is intrinsic because the container's width is unknown; here it is intrinsic because the
        // container's height is, and `BoundAxis` has just resolved that height against the node's own
        // `min-height`. A grid with `min-height: 100px` and one empty `auto` row measures its content
        // at zero, grows its border box to 100 because of the minimum, and then has 56 points of
        // content box that the row was never told about. §12.8 stretches `auto` tracks into
        // "remaining positive, definite free space", and until this pass runs there is no definite
        // free space to stretch into — which is the second sentence of §12.8, "if the free space is
        // indefinite, but the grid container has a definite min-width/height, use that size", read as
        // a whole second pass rather than as a special case inside one step.
        //
        // ⚠ That reading is what makes `grid-template-rows: 1fr` come out at 56 as well. §12.7's
        // indefinite branch sizes an `fr` track from its own base size, which is zero; only a definite
        // available space gives the flexible track a fraction to be. One pass with a number in it
        // answers both, where a clause bolted onto §12.8 would have answered only the `auto` case.
        //
        // The container does NOT re-measure afterwards — its height was settled by the intrinsic pass
        // and by the minimum, exactly as the inline axis does not re-measure around its gutter.
        if (rowAxis.Constraint != GridSizingConstraint.Definite) {
            ResetTracks(rowsAt, placement.Rows);

            rowAxis = rowAxis with { AvailableSpace = innerHeight, Constraint = GridSizingConstraint.Definite };

            SizeGridTracks(in rowAxis, direction, ownerWidth, ownerHeight, currentDepth);
        }

        rowAxis = rowAxis with { AvailableSpace = innerHeight };

        SetMeasuredDimension(index, Dimension.Width, outerWidth, unboundedWidth);
        SetMeasuredDimension(index, Dimension.Height, outerHeight, unboundedHeight);

        if (!performLayout) {
            return;
        }

        // ── §10.3 and §11: where the tracks sit, then where the items sit inside them ───────────
        // ⚠ Both axes are positioned in *content-box* coordinates and the physical inset is added
        // when each box is placed. That is what lets the one RTL line in `PlaceGridItemBoxes` be a
        // mirror of a single number rather than an inset-aware rearrangement of the whole pass.
        PositionTracks(in columnAxis, styles[index].JustifyContent, styles[index].JustifyContentOverflow);
        PositionTracks(in rowAxis, JustifyOf(styles[index].AlignContent), styles[index].AlignContentOverflow);

        PlaceGridItemBoxes(index, in columnAxis, in rowAxis, direction, innerWidth, innerHeight, insetLeft, insetTop, currentDepth);

        RecordAbsoluteGridAreas(
            index,
            in placement,
            in columnAxis,
            in rowAxis,
            direction,
            explicitColumns,
            explicitRows,
            innerWidth,
            insetLeft,
            insetTop
        );

        if (styles[index].PositionType != PositionType.Static || currentDepth == 1) {
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

    /// <summary>CSS Grid §11.8: how far each baseline-aligned item drops to meet its row's baseline.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A baseline-sharing group is a row, and the shim is a contribution rather than an
    ///         offset.</b> §12.5 step 1 folds the distance from an item's own baseline to its group's
    ///         into the size it contributes to its track, so a 20-point item whose baseline is 10
    ///         points down, sharing a row with a 50-point item whose baseline is at its bottom edge,
    ///         asks its row for 40 + 20 rather than for 20. Sizing the rows first and shifting the
    ///         items afterwards gets the second number right and the first one wrong, which is a
    ///         row that is too short by exactly the shim.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The probe lays the item out for real, and it has to.</b> An item's baseline is
    ///         either its own bottom edge or a descendant's, and
    ///         <see cref="CalculateBaseline" />'s descent reads
    ///         <c>Position[Edge.Top]</c> — which a measurement pass never writes. So this is the one
    ///         place in grid sizing that asks for <c>performLayout: true</c>. It costs a layout of
    ///         each baseline-aligned item, which is why the whole pass is skipped unless one exists;
    ///         the result is thrown away and recomputed against the real area in
    ///         <see cref="PlaceGridItemBoxes" />, and the two calls differ in their arguments, so the
    ///         layout cache does not confuse them.
    ///     </para>
    ///     <para>
    ///         ⚠ Only items with a row span of exactly 1 join a group. §11.8 says so, and the reason
    ///         is the same circularity §6.6 avoids: an item spanning two rows has no single row to
    ///         push against, and letting it set a shared baseline would make a row's size depend on a
    ///         row that depends on it. A spanning item is aligned as though it said <c>start</c>.
    ///     </para>
    /// </remarks>
    void ResolveBaselineShims(
        int index,
        int itemsAt,
        int itemCount,
        Direction direction,
        float ownerWidth,
        float ownerHeight,
        int currentDepth
    ) {
        var participants = 0;

        for (var at = 0; at < itemCount; at++) {
            var itemAt = itemsAt + at;

            // ⚠ Never a `ref` held across the layout call below: the item store grows when a nested
            // grid asks for room, and the reference would point into the array that was replaced.
            // See the remarks on GridScratch.
            Scratch.Item(itemAt).BaselineShim = 0f;
            Scratch.Item(itemAt).OwnBaseline = float.NaN;

            var child = Scratch.Item(itemAt).Node;
            if (Scratch.Item(itemAt).RowSpan != 1 || GridItemAlign(index, child) != Align.Baseline) {
                continue;
            }

            var inlineSize = Scratch.Item(itemAt).ResolvedInlineSize;
            var marginRow = StyleResolution.MarginForAxis(in styles[child], FlexDirection.Row, ownerWidth);
            var marginColumn = StyleResolution.MarginForAxis(in styles[child], FlexDirection.Column, ownerWidth);

            // ⚠ <b>The stated height has to be resolved here for the same reason
            // <see cref="MeasureGridItem" /> resolves it</b>: a childless box answers a max-content
            // block request with its padding and border and nothing else, so an empty
            // <c>height: 20px</c> item measures zero tall — and a synthesised baseline <i>is</i> the
            // bottom edge, so every plain item in the group would report a baseline of zero and the
            // shims would come out inverted. That is not a hypothetical; it is what the first
            // version of this pass did.
            var statedHeight = StyleResolution.ProcessedDimension(in styles[child], Dimension.Height).Unit == LayoutUnit.Point
                ? ResolvedDimension(child, Dimension.Height, ownerHeight, ownerWidth, direction)
                : float.NaN;

            if (!float.IsNaN(statedHeight)) {
                statedHeight = BoundAxisWithinMinAndMax(child, direction, FlexDirection.Column, statedHeight, ownerHeight, ownerWidth);
            }

            CalculateLayoutInternal(
                child,
                inlineSize,
                float.IsNaN(statedHeight) ? float.NaN : statedHeight + marginColumn,
                direction,
                float.IsNaN(inlineSize) ? SizingMode.MaxContent : SizingMode.StretchFit,
                float.IsNaN(statedHeight) ? SizingMode.MaxContent : SizingMode.StretchFit,
                float.IsNaN(inlineSize) ? ownerWidth : MathF.Max(0f, inlineSize - marginRow),
                ownerHeight,
                performLayout: true,
                currentDepth
            );

            // The baseline of the item's *outer* box, because the shim shifts the outer box and the
            // row is sized in outer sizes.
            var marginTop = StyleResolution.InlineStartMargin(in styles[child], FlexDirection.Column, direction, ownerWidth);

            Scratch.Item(itemAt).OwnBaseline = marginTop.OrZero() + CalculateBaseline(child);
            participants++;
        }

        if (participants == 0) {
            return;
        }

        // Each group's deepest baseline is the one the others drop to. Quadratic over the items of
        // one container, which is what the alternative — a per-row array out of the scratch — would
        // cost to allocate for the handful of items a grid holds.
        for (var at = 0; at < itemCount; at++) {
            var own = Scratch.Item(itemsAt + at).OwnBaseline;
            if (float.IsNaN(own)) {
                continue;
            }

            var row = Scratch.Item(itemsAt + at).RowStart;
            var deepest = own;

            for (var other = 0; other < itemCount; other++) {
                var candidate = Scratch.Item(itemsAt + other).OwnBaseline;
                if (!float.IsNaN(candidate) && Scratch.Item(itemsAt + other).RowStart == row) {
                    deepest = MathF.Max(deepest, candidate);
                }
            }

            Scratch.Item(itemsAt + at).BaselineShim = deepest - own;
        }
    }

    /// <summary>The block-axis alignment one grid item ends up with.</summary>
    /// <remarks>
    ///     ⚠ Not <see cref="ResolveChildAlignment" />: that one degrades <c>baseline</c> to
    ///     <c>flex-start</c> whenever the container's <c>flex-direction</c> is a column, which is a
    ///     flex rule reached through a property a grid container does not use. This store's default
    ///     <c>FlexDirection</c> is <c>Column</c>, so borrowing it would turn baseline alignment off
    ///     for every grid that did not happen to say <c>flex-direction: row</c>.
    /// </remarks>
    Align GridItemAlign(int index, int child) =>
        styles[child].AlignSelf == Align.Auto ? styles[index].AlignItems : styles[child].AlignSelf;

    /// <summary>A node's own stated size on an axis, clamped, or NaN when it has none.</summary>
    float ResolvedBoundedDimension(int index, Dimension dimension, Direction direction, float ownerWidth, float ownerHeight) {
        var axis = dimension == Dimension.Width ? FlexDirection.Row : FlexDirection.Column;
        var reference = dimension == Dimension.Width ? ownerWidth : ownerHeight;
        var stated = ResolvedDimension(index, dimension, reference, ownerWidth, direction);

        return float.IsNaN(stated) ? float.NaN : BoundAxis(index, axis, direction, stated, reference, ownerWidth);
    }

    /// <summary>Which of §12's three constraints a sizing mode is.</summary>
    static GridSizingConstraint ConstraintFor(SizingMode mode, float innerSize) =>
        !float.IsNaN(innerSize) ? GridSizingConstraint.Definite
        : mode == SizingMode.StretchFit ? GridSizingConstraint.Definite
        : GridSizingConstraint.MaxContent;

    /// <summary>Whether a content-distribution value leaves the leftover space to the tracks.</summary>
    /// <remarks>
    ///     ⚠ CSS Box Alignment §6.2: only <c>normal</c> and <c>stretch</c> stretch. Every other
    ///     keyword takes the free space for itself and distributes it <i>between</i> the tracks,
    ///     which is why §12.8 must not run for them — a <c>space-between</c> grid whose auto tracks
    ///     had already eaten the free space would have nothing left to space out.
    /// </remarks>
    static bool StretchesTracks(Justify justify) => justify == Justify.FlexStart;

    /// <inheritdoc cref="StretchesTracks(Justify)" />
    static bool StretchesTracks(Align align) => align is Align.Stretch or Align.FlexStart;

    /// <summary>Reads a block-axis content distribution as the same six keywords the inline one uses.</summary>
    static Justify JustifyOf(Align align) => align switch {
        Align.Center => Justify.Center,
        Align.FlexEnd => Justify.FlexEnd,
        Align.SpaceBetween => Justify.SpaceBetween,
        Align.SpaceAround => Justify.SpaceAround,
        Align.SpaceEvenly => Justify.SpaceEvenly,
        _ => Justify.FlexStart
    };

    /// <summary>How many explicit tracks the template comes to once its repetition is counted.</summary>
    int ExplicitTrackCount(in GridTemplate template, float availableSpace, float gap) {
        if (!template.IsDefined) {
            return 0;
        }

        if (template.AutoRepeatKind == GridAutoRepeat.None) {
            return template.Count;
        }

        var stated = template.Count - template.AutoRepeatCount;
        var fixedSize = 0f;
        var stored = tracks.Slice(template.Offset, template.Count);

        for (var at = 0; at < template.Count; at++) {
            if (at >= template.AutoRepeatIndex && at < template.AutoRepeatIndex + template.AutoRepeatCount) {
                continue;
            }

            var track = stored[at];
            var size = track.Max.IsFixed(availableSpace) ? track.Max.Resolve(availableSpace) : track.Min.Resolve(availableSpace);
            fixedSize += float.IsNaN(size) ? 0f : MathF.Max(0f, size);
        }

        var repetitions = AutomaticRepetitions(in template, availableSpace, gap, stated, fixedSize);

        return int.Min(stated + (repetitions * template.AutoRepeatCount), LayoutLimits.MaximumGridTracks);
    }

    /// <summary>§7.2.3.2: <c>auto-fit</c> drops the generated tracks that no item landed in.</summary>
    /// <remarks>
    ///     ⚠ <b>Only the tracks the repetition generated, and only the empty ones.</b> An explicit
    ///     track the author wrote out by hand is never collapsed however empty it is, and neither is
    ///     an implicit one — so this walks the repetition's own range rather than every track. A
    ///     collapsed track is zero wide and shares a single line with its neighbour, which is why
    ///     <see cref="UsedTrackSpace" /> counts surviving tracks when it adds up the gutters.
    /// </remarks>
    void CollapseAutoFitTracks(in GridTemplate template, in GridPlacementResult placement, int tracksAt, int explicitCount, bool inline) {
        if (template.AutoRepeatKind != GridAutoRepeat.AutoFit || template.AutoRepeatCount <= 0) {
            return;
        }

        var leading = inline ? placement.ColumnOffset : placement.RowOffset;
        var from = leading + template.AutoRepeatIndex;
        var repeated = explicitCount - (template.Count - template.AutoRepeatCount);
        var to = from + repeated;

        for (var track = from; track < to; track++) {
            var occupied = false;

            for (var at = 0; at < placement.ItemCount && !occupied; at++) {
                ref var item = ref Scratch.Item(placement.ItemsAt + at);
                occupied = item.StartOn(inline) <= track && track < item.StartOn(inline) + item.SpanOn(inline);
            }

            if (!occupied) {
                Scratch.Track(tracksAt + track).IsCollapsed = true;
            }
        }
    }

    /// <summary>Puts every track back to its unsized state, for the one case that sizes twice.</summary>
    void ResetTracks(int tracksAt, int count) {
        for (var at = 0; at < count; at++) {
            ref var track = ref Scratch.Track(tracksAt + at);

            track.BaseSize = 0f;
            track.GrowthLimit = 0f;
            track.PlannedIncrease = 0f;
            track.ItemIncurredIncrease = 0f;
            track.IsMarked = false;
        }
    }

    /// <summary>The size of a grid area: the tracks it spans, plus the gutters between them.</summary>
    float AreaSize(in GridAxis axis, int start, int span) {
        var total = 0f;
        var alive = 0;

        for (var at = start; at < start + span && at < axis.TrackCount; at++) {
            ref var track = ref Scratch.Track(axis.TracksAt + at);

            if (track.IsCollapsed) {
                continue;
            }

            total += track.BaseSize;
            alive++;
        }

        return total + (axis.Gap * int.Max(0, alive - 1));
    }

    /// <summary>§10.3: distributes the container's leftover space between the tracks.</summary>
    /// <param name="axis">The axis whose tracks are being placed.</param>
    /// <param name="distribution">The content distribution the container asked for.</param>
    /// <param name="overflow">Whether it was written <c>safe</c>.</param>
    void PositionTracks(in GridAxis axis, Justify distribution, OverflowAlignment overflow) {
        var alive = 0;
        for (var at = 0; at < axis.TrackCount; at++) {
            if (!Scratch.Track(axis.TracksAt + at).IsCollapsed) {
                alive++;
            }
        }

        var free = float.IsNaN(axis.AvailableSpace) ? 0f : axis.AvailableSpace - UsedTrackSpace(in axis);

        // ⚠ <b>Negative free space is honoured, and only the distributed alignments step aside for
        // it.</b> CSS Box Alignment §4.4 gives every alignment an overflow behaviour, and the
        // default one is <c>unsafe</c>: `center` and `end` overflow in the direction they align,
        // rather than falling back to start. That is not a detail — `TaffyStyleMap` refuses
        // `safe X` outright and strips `unsafe X` precisely because unsafe is what every keyword in
        // this store already means, so clamping here contradicted the store's own vocabulary.
        //
        // What negative free space does change is §4.2's distributed alignments, each of which
        // names its own fallback when there is nothing left to distribute: `space-between` becomes
        // `start`, and `space-around` and `space-evenly` become `safe center`. ⚠ <b>The `safe` in
        // that fallback is the whole point of it</b> — a bare `center` would overflow by half, and
        // `safe` is exactly the keyword that says to fall back to start instead. So all three
        // arrive at start, and negative free space is spent on none of them. Mapping the last two
        // to a plain `Center` puts every track half the overflow to the left, which is what
        // `grid_justify_content_space_around_negative_space_gap` and its `space_evenly` twin catch.
        //
        // ⚠ <b>And `safe` is also the keyword an author can write on the property itself</b>, which
        // is the one way negative free space does reach `center` and `end`.
        // `grid_safe_justify_content_end_overflow` is three 40-point columns in a 100-point grid:
        // `unsafe end` starts them at −20 and `safe end` at 0.
        if (free < 0f) {
            distribution = SafeFallback(
                distribution switch {
                    Justify.SpaceBetween or Justify.SpaceAround or Justify.SpaceEvenly => Justify.FlexStart,
                    _ => distribution
                },
                overflow,
                free
            );
        }

        var (leading, between) = distribution switch {
            Justify.Center => (free / 2f, 0f),
            Justify.FlexEnd => (free, 0f),
            Justify.SpaceBetween when alive > 1 => (0f, free / (alive - 1)),
            Justify.SpaceAround when alive > 0 => (free / alive / 2f, free / alive),
            Justify.SpaceEvenly when alive > 0 => (free / (alive + 1), free / (alive + 1)),
            _ => (0f, 0f)
        };

        var cursor = leading;

        for (var at = 0; at < axis.TrackCount; at++) {
            ref var track = ref Scratch.Track(axis.TracksAt + at);

            track.Offset = cursor;

            if (track.IsCollapsed) {
                continue;
            }

            cursor += track.BaseSize + axis.Gap + between;
        }
    }

    /// <summary>Lays each item out inside the area it was placed in, and aligns it there.</summary>
    void PlaceGridItemBoxes(
        int index,
        in GridAxis columnAxis,
        in GridAxis rowAxis,
        Direction direction,
        float innerWidth,
        float innerHeight,
        float insetLeft,
        float insetTop,
        int currentDepth
    ) {
        var containerJustify = styles[index].JustifyItems;
        var containerAlign = styles[index].AlignItems;

        foreach (var child in ChildIds(index)) {
            if (styles[child].Display == Display.None) {
                ZeroOutLayoutRecursively(child);
            }
        }

        for (var at = 0; at < columnAxis.ItemCount; at++) {
            // ⚠ Read out, not held: `CalculateLayoutInternal` below may grow the item store, and a
            // `ref` taken here would point into the array it replaced. See GridScratch's remarks.
            var item = Scratch.Item(columnAxis.ItemsAt + at);
            var child = item.Node;

            var areaX = Scratch.Track(columnAxis.TracksAt + item.ColumnStart).Offset;
            var areaY = Scratch.Track(rowAxis.TracksAt + item.RowStart).Offset;
            var areaWidth = AreaSize(in columnAxis, item.ColumnStart, item.ColumnSpan);
            var areaHeight = AreaSize(in rowAxis, item.RowStart, item.RowSpan);

            // ⚠ <b>A grid item's percentage margins are a fraction of its <i>grid area</i>, not of
            // the grid container.</b> CSS Box Model §8: a percentage margin resolves against the
            // inline size of the containing block, and CSS Grid §9 makes an item's containing block
            // its grid area rather than the container's content box. All four of them, including
            // the vertical pair — `grid_margins_percent_center` is the family that tells the two
            // apart, and a two-column grid halves every percentage margin the moment this is right.
            var marginStart = StyleResolution.InlineStartMargin(in styles[child], FlexDirection.Row, direction, areaWidth);
            var marginEnd = StyleResolution.InlineEndMargin(in styles[child], FlexDirection.Row, direction, areaWidth);
            var marginTop = StyleResolution.InlineStartMargin(in styles[child], FlexDirection.Column, direction, areaWidth);
            var marginBottom = StyleResolution.InlineEndMargin(in styles[child], FlexDirection.Column, direction, areaWidth);

            var startIsAuto = StyleResolution.InlineStartMarginIsAuto(in styles[child], FlexDirection.Row, direction);
            var endIsAuto = StyleResolution.InlineEndMarginIsAuto(in styles[child], FlexDirection.Row, direction);
            var topIsAuto = StyleResolution.InlineStartMarginIsAuto(in styles[child], FlexDirection.Column, direction);
            var bottomIsAuto = StyleResolution.InlineEndMarginIsAuto(in styles[child], FlexDirection.Column, direction);

            var justify = Resolve(styles[child].JustifySelf, containerJustify);
            var align = Resolve(styles[child].AlignSelf, containerAlign);

            // The prefix travels with the position it modifies: an item that defers to the
            // container's `justify-items` inherits its `safe` too. See ResolveChildAlignmentOverflow.
            var justifyOverflow = styles[child].JustifySelf == Align.Auto
                ? styles[index].JustifyItemsOverflow
                : styles[child].JustifySelfOverflow;

            var alignOverflow = styles[child].AlignSelf == Align.Auto
                ? styles[index].AlignItemsOverflow
                : styles[child].AlignSelfOverflow;

            // ⚠ The size handed down is the *outer* one — the area, margins included — because
            // `CalculateLayoutInternal` subtracts the margins itself on the way in. Passing the
            // border-box size instead makes every margined item overflow its own cell by exactly
            // its margins, which reads as an off-by-a-margin in the alignment rather than here.
            var (width, widthMode) = ItemSizeOn(
                child,
                Dimension.Width,
                direction,
                areaWidth,
                marginStart.OrZero() + marginEnd.OrZero(),
                justify,
                startIsAuto || endIsAuto,
                areaWidth,
                areaHeight
            );

            var (height, heightMode) = ItemSizeOn(
                child,
                Dimension.Height,
                direction,
                areaHeight,
                marginTop.OrZero() + marginBottom.OrZero(),
                align,
                topIsAuto || bottomIsAuto,
                areaWidth,
                areaHeight
            );

            // ⚠ AN ITEM WITH AN ASPECT RATIO IS NOT STRETCHED DOWN THE BLOCK AXIS; the ratio sizes it.
            // CSS Box Alignment §6.2's `normal` fills the area only where the item has nothing else to
            // say, and CSS Sizing §4.1 gives a box with a preferred aspect ratio and a definite inline
            // size a definite block size — which is something else to say. `ItemSizeOn` cannot know
            // this on its own because the answer is the *other* axis's size, decided just above.
            // `grid_aspect_ratio_fill_child_height` is the one-line demonstration: `width: 50px;
            // aspect-ratio: 2` in a 100x100 grid is 50x25, and stretching gave it the area's 100.
            //
            // The inline axis is clamped first and the block axis derived from the clamped value, for
            // the same reason the absolute path does it in that order — a maximum the ratio carried in
            // from the other axis has to reach the derived size, not just the one it was written on.
            if (!float.IsNaN(styles[child].AspectRatio) && styles[child].AspectRatio > 0f) {
                var bounds = ResolveAspectBounds(child, direction, areaWidth, areaHeight);
                var marginRowSum = marginStart.OrZero() + marginEnd.OrZero();
                var borderBoxWidth = ClampBlockChildAxis(width - marginRowSum, bounds.MinWidth, bounds.MaxWidth);

                width = borderBoxWidth + marginRowSum;
                widthMode = SizingMode.StretchFit;

                // A stated height was never the ratio's to decide, and neither is one the item's own
                // `align-self` asked to be content-sized rather than stretched.
                if (float.IsNaN(ResolvedDimension(child, Dimension.Height, areaHeight, areaWidth, direction))
                    && heightMode == SizingMode.StretchFit) {
                    height = ClampBlockChildAxis(
                            HeightAcrossRatio(child, direction, borderBoxWidth, areaWidth),
                            bounds.MinHeight,
                            bounds.MaxHeight
                        )
                        + marginTop.OrZero()
                        + marginBottom.OrZero();
                }
            }

            // ⚠ The owner size is the grid <i>area</i>, which is what CSS Grid §9 makes the item's
            // containing block. Handing down the container's content box instead resolves every
            // percentage inside the item against the whole grid rather than against its own cell.
            CalculateLayoutInternal(
                child,
                width,
                height,
                direction,
                widthMode,
                heightMode,
                areaWidth,
                areaHeight,
                performLayout: true,
                currentDepth
            );

            var usedWidth = results[child].MeasuredDimensions[(int) Dimension.Width];
            var usedHeight = results[child].MeasuredDimensions[(int) Dimension.Height];

            var offsetX = AlignInArea(
                areaWidth,
                usedWidth,
                marginStart,
                marginEnd,
                startIsAuto,
                endIsAuto,
                justify,
                justifyOverflow,
                out var usedStart,
                out var usedEnd
            );

            var offsetY = AlignInArea(
                areaHeight,
                usedHeight,
                marginTop,
                marginBottom,
                topIsAuto,
                bottomIsAuto,
                align,
                alignOverflow,
                out var usedTop,
                out var usedBottom
            );

            // §11.8: a baseline-aligned item starts where its group's baseline puts it. `AlignInArea`
            // has already placed it at the area's start — the shim is the rest of the answer, and the
            // row was sized to hold it by `MeasureGridItem`.
            offsetY += item.BaselineShim;

            // ⚠ <b>The inline axis is mirrored for RTL here and nowhere else.</b> §12 has no opinion
            // about direction — it lays tracks out from the content box's start at offset zero — and
            // CSS Writing Modes makes that start the *right* edge in RTL. Mirroring the finished box
            // against the content box keeps one arithmetic path and puts the single
            // direction-dependent line where it can be read. `insetLeft` is physical: the padding
            // and border on the left stay on the left in both directions.
            var x = direction == Direction.Ltr
                ? insetLeft + areaX + offsetX
                : insetLeft + innerWidth - areaX - offsetX - usedWidth;

            var relativeX = direction == Direction.Ltr
                ? RelativePosition(child, FlexDirection.Row, direction, innerWidth)
                : -RelativePosition(child, FlexDirection.Row, direction, innerWidth);

            results[child].Position[(int) Edge.Left] = x + relativeX;
            results[child].Position[(int) Edge.Top] =
                insetTop + areaY + offsetY + RelativePosition(child, FlexDirection.Column, direction, innerHeight);

            results[child].Margin[(int) (direction == Direction.Ltr ? Edge.Left : Edge.Right)] = usedStart;
            results[child].Margin[(int) (direction == Direction.Ltr ? Edge.Right : Edge.Left)] = usedEnd;
            results[child].Margin[(int) Edge.Top] = usedTop;
            results[child].Margin[(int) Edge.Bottom] = usedBottom;
        }

        static Align Resolve(Align self, Align container) => self == Align.Auto ? container : self;
    }

    /// <summary>
    ///     §9.2: cuts each out-of-flow child's grid area out of the finished tracks, for the absolute
    ///     walk to use as that child's containing block.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>An <c>auto</c> line is the container's PADDING edge, not a one-track span, and
    ///         that single sentence is most of this method.</b> §9.2 resolves an out-of-flow child's
    ///         four placement properties against the implicit grid and puts the padding edge wherever
    ///         one of them says <c>auto</c> — so <c>grid-column-start: 1</c> alone gives an area that
    ///         runs from the first line all the way to the right padding edge, not a 40-point column.
    ///         Reading the placement with <see cref="ResolveAxisPlacement" /> instead, which is §8's
    ///         in-flow rule, gives a one-track area and the wrong width for every
    ///         <c>grid_absolute_{row,column}_{start,end}</c> fixture in the corpus.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A <c>span</c> is <c>auto</c> here too, and so is a line outside the grid.</b> An
    ///         out-of-flow child does not create implicit tracks — <see cref="PlaceGridItems" /> has
    ///         already skipped it — so <c>grid-row-start: 5</c> against a two-row grid names a line
    ///         that does not exist and falls back to the padding edge rather than growing the grid to
    ///         reach it. That is what <c>grid_absolute_gaps_out_of_range_lines_rtl</c> pins.
    ///     </para>
    ///     <para>
    ///         The rectangle is written physically, mirrored for RTL exactly as
    ///         <see cref="PlaceGridItemBoxes" /> mirrors an in-flow item, because
    ///         <see cref="LayoutResult.Position" /> is physical and the absolute walk has no track
    ///         list left to consult by the time it runs.
    ///     </para>
    /// </remarks>
    void RecordAbsoluteGridAreas(
        int index,
        in GridPlacementResult placement,
        in GridAxis columnAxis,
        in GridAxis rowAxis,
        Direction direction,
        int explicitColumns,
        int explicitRows,
        float innerWidth,
        float insetLeft,
        float insetTop
    ) {
        var borderLeft = results[index].Border[(int) Edge.Left];
        var borderTop = results[index].Border[(int) Edge.Top];
        var paddingBoxRight = results[index].MeasuredDimensions[(int) Dimension.Width] - results[index].Border[(int) Edge.Right];
        var paddingBoxBottom = results[index].MeasuredDimensions[(int) Dimension.Height] - results[index].Border[(int) Edge.Bottom];

        foreach (var child in ChildIds(index)) {
            if (styles[child].Display == Display.None || styles[child].PositionType != PositionType.Absolute) {
                continue;
            }

            var columnStart = AbsoluteLine(styles[child].GridColumnStart, explicitColumns, placement.ColumnOffset, columnAxis.TrackCount);
            var columnEnd = AbsoluteLine(styles[child].GridColumnEnd, explicitColumns, placement.ColumnOffset, columnAxis.TrackCount);
            var rowStart = AbsoluteLine(styles[child].GridRowStart, explicitRows, placement.RowOffset, rowAxis.TrackCount);
            var rowEnd = AbsoluteLine(styles[child].GridRowEnd, explicitRows, placement.RowOffset, rowAxis.TrackCount);

            // Two definite lines the wrong way round name the same area as the right way round.
            if (columnStart >= 0 && columnEnd >= 0 && columnEnd < columnStart) {
                (columnStart, columnEnd) = (columnEnd, columnStart);
            }

            if (rowStart >= 0 && rowEnd >= 0 && rowEnd < rowStart) {
                (rowStart, rowEnd) = (rowEnd, rowStart);
            }

            float left;
            float right;

            if (direction == Direction.Ltr) {
                left = columnStart < 0 ? borderLeft : insetLeft + GridLinePosition(in columnAxis, columnStart);
                right = columnEnd < 0 ? paddingBoxRight : insetLeft + GridLinePosition(in columnAxis, columnEnd);
            } else {
                right = columnStart < 0 ? paddingBoxRight : insetLeft + innerWidth - GridLinePosition(in columnAxis, columnStart);
                left = columnEnd < 0 ? borderLeft : insetLeft + innerWidth - GridLinePosition(in columnAxis, columnEnd);
            }

            var top = rowStart < 0 ? borderTop : insetTop + GridLinePosition(in rowAxis, rowStart);
            var bottom = rowEnd < 0 ? paddingBoxBottom : insetTop + GridLinePosition(in rowAxis, rowEnd);

            results[child].GridAreaLeft = MathF.Min(left, right);
            results[child].GridAreaTop = MathF.Min(top, bottom);
            results[child].GridAreaWidth = MathF.Abs(right - left);
            results[child].GridAreaHeight = MathF.Abs(bottom - top);
        }
    }

    /// <summary>One placement property as a track index, or −1 for "the padding edge".</summary>
    static int AbsoluteLine(GridPlacement placement, int explicitCount, int offset, int trackCount) {
        if (placement.Kind != GridPlacementKind.Line) {
            return -1;
        }

        var line = ResolveLine(placement.Value, explicitCount) + offset;

        return line < 0 || line > trackCount ? -1 : line;
    }

    /// <summary>Where a grid line sits, measured from the container's content box.</summary>
    float GridLinePosition(in GridAxis axis, int line) {
        if (axis.TrackCount == 0) {
            return 0f;
        }

        if (line >= axis.TrackCount) {
            ref var last = ref Scratch.Track(axis.TracksAt + axis.TrackCount - 1);
            return last.Offset + (last.IsCollapsed ? 0f : last.BaseSize);
        }

        return Scratch.Track(axis.TracksAt + int.Max(0, line)).Offset;
    }

    /// <summary>
    ///     What size to ask an item for, given the area it sits in and how it is aligned there.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Only a stretched item is given its area's size; every other alignment asks for the
    ///     item's own.</b> CSS Box Alignment §6.2 makes <c>stretch</c> the default precisely so that
    ///     a card with no width fills its cell — but <c>justify-self: center</c> on the same card
    ///     means "be as wide as you want, then centre", and handing it the area's width instead
    ///     produces a centred box that is already the full width and therefore is not centred at all.
    ///     An item with a definite size of its own is never stretched either, and neither is one with
    ///     an auto margin, which §10.2 gives precedence over alignment.
    /// </remarks>
    (float Size, SizingMode Mode) ItemSizeOn(
        int child,
        Dimension dimension,
        Direction direction,
        float areaSize,
        float marginSum,
        Align alignment,
        bool hasAutoMargin,
        float areaWidth,
        float areaHeight
    ) {
        var reference = dimension == Dimension.Width ? areaWidth : areaHeight;
        var stated = ResolvedDimension(child, dimension, reference, areaWidth, direction);

        if (!float.IsNaN(stated)) {
            return (stated + marginSum, SizingMode.StretchFit);
        }

        if (alignment is Align.Stretch or Align.Auto && !hasAutoMargin) {
            return (MathF.Max(0f, areaSize), SizingMode.StretchFit);
        }

        // Not stretched: the item is sized to its content, but never larger than its area.
        //
        // ⚠ <b>…and the area is narrowed by the item's own maximum FIRST, because the block axis is
        // measured at whatever inline size this returns.</b> Clamping afterwards gives the right
        // width and the wrong height: `grid_size_child_fixed_tracks`' last item is `max-width: 30px`
        // in a 40-point column, and measuring its text at 40 wraps it into two lines where 30 needs
        // four. Chrome answers 30x40; clamping the finished box answers 30x20, which reads as a
        // line-breaking bug and is a sequencing one. The used inline size is unchanged by moving the
        // clamp — fit-content is monotonic in the available space, so `min(fit-content(a), m)` and
        // `fit-content(min(a, m))` agree — which is why only the other axis moves.
        var axis = dimension == Dimension.Width ? FlexDirection.Row : FlexDirection.Column;
        var borderBox = MathF.Max(0f, areaSize - marginSum);
        var bounded = BoundAxisWithinMinAndMax(child, direction, axis, borderBox, reference, areaWidth);

        return (MathF.Max(0f, bounded + marginSum), SizingMode.FitContent);
    }

    /// <summary>
    ///     Where an item sits inside its area, once auto margins have had their say.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Auto margins beat alignment, and they beat it silently.</b> CSS Box Alignment §10.2:
    ///     if either margin on the axis is <c>auto</c>, the free space goes to the margins and the
    ///     alignment property is ignored entirely. So <c>margin-left: auto</c> with
    ///     <c>justify-self: start</c> pushes the item to the *end* — the declaration that looks like
    ///     it should win does not.
    /// </remarks>
    static float AlignInArea(
        float areaSize,
        float itemSize,
        float startMargin,
        float endMargin,
        bool startIsAuto,
        bool endIsAuto,
        Align alignment,
        OverflowAlignment overflow,
        out float usedStart,
        out float usedEnd
    ) {
        var free = areaSize - itemSize - startMargin.OrZero() - endMargin.OrZero();

        if (startIsAuto || endIsAuto) {
            var count = (startIsAuto ? 1 : 0) + (endIsAuto ? 1 : 0);
            var share = MathF.Max(0f, free) / count;

            usedStart = startIsAuto ? share : startMargin.OrZero();
            usedEnd = endIsAuto ? share : endMargin.OrZero();

            return usedStart;
        }

        usedStart = startMargin.OrZero();
        usedEnd = endMargin.OrZero();

        // ⚠ <b>An item bigger than its area overflows in the direction it aligns, and is not sent
        // back to the start.</b> CSS Box Alignment §4.4's `safe` keyword is the one that falls back
        // to start on overflow; the default is `unsafe`, which honours the alignment whatever the
        // relative sizes. So `align-self: end` on a 60-point item in a 40-point row puts it 20
        // points ABOVE the row, and `center` puts it 10 above — negative offsets that a clamp here
        // rounded to zero, which reads as an item that simply ignores its own alignment.
        //
        // ⚠ <b>And when the author DID write `safe`, this is the line that honours it.</b> Asked of
        // the keyword rather than of the sign, so an `unsafe` overflow still overflows:
        // `grid_safe_align_self_end_overflow` and `grid_unsafe_align_self_end_overflow` are the same
        // 150-point item in the same 100-point row and Chrome answers 0 and −50.
        return usedStart + SafeFallback(alignment, overflow, free) switch {
            Align.Center => free / 2f,
            Align.FlexEnd => free,
            _ => 0f
        };
    }

    /// <summary>The three numbers §12 asks of one item on one axis.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The two axes answer differently, and the block axis is the easy one.</b> On the
    ///         inline axis an item has a genuine min-content size and a genuine max-content size and
    ///         they differ — a paragraph's longest word against the whole paragraph on one line. On
    ///         the block axis, once the inline size is fixed, there is only one answer: the height
    ///         that content takes at that width. So the row pass measures once and reports the same
    ///         number three times, which is not a simplification but what the definitions come to.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The <i>minimum</i> contribution is not the min-content one.</b> CSS Sizing: an
    ///         item whose preferred size behaves as <c>auto</c> contributes its <i>used minimum
    ///         size</i>, so a box with <c>min-width: 20px</c> and a 50-point min-content size
    ///         contributes 20 to an <c>auto</c> track's floor and 50 to a <c>min-content</c> one.
    ///         Reading both as the min-content contribution makes every <c>auto</c> track as wide as
    ///         its content, which is right often enough to look correct and wrong wherever a stated
    ///         minimum is smaller.
    ///     </para>
    /// </remarks>
    GridContribution MeasureGridItem(
        in GridAxis axis,
        in GridItem item,
        Direction direction,
        float ownerWidth,
        float ownerHeight,
        int currentDepth
    ) {
        var child = item.Node;

        if (!axis.Inline) {
            // ⚠ <b>A percentage margin on the item is a fraction of its GRID AREA, and by the row
            // pass the area's inline size is known.</b> CSS Grid §9 makes the area the item's
            // containing block, which is the rule `PlaceGridItemBoxes` already states for the
            // margins it positions with — but the row pass resolved the same margins against the
            // grid's own owner width, which for a max-content container is NaN. So a `margin: 5%`
            // item contributed its border box alone and its row came out short by both margins.
            // ⚠ The inline pass deliberately does NOT do this: there the area's size is the thing
            // being computed, and CSS Sizing §5.2.1 makes a percentage against an unknown
            // containing block behave as `auto` while an intrinsic contribution is calculated.
            var marginReference = float.IsNaN(item.ResolvedInlineSize) ? ownerWidth : item.ResolvedInlineSize;
            var marginColumn = StyleResolution.MarginForAxis(in styles[child], FlexDirection.Column, marginReference);
            var marginRow = StyleResolution.MarginForAxis(in styles[child], FlexDirection.Row, marginReference);
            // ⚠ <b>An item with a stated width is measured at THAT width, not at its area's.</b>
            // `ItemSizeOn` has always resolved the preferred size before the item is laid out; this
            // pass handed the area's size down as a stretch-fit and let the callee discover the
            // declaration for itself — which it cannot, because a percentage width needs a
            // containing block and the callee is given the area as its owner. So a `width: 50%`
            // grid nested in a 200-point grid measured its own children against 200 and laid them
            // out against 100, and every percentage inside it came out doubled. That is the whole
            // of the `grid_percent_items_nested_*` family, and it was filed as a cyclic percentage
            // — a percentage resolved against the wrong box rather than against an unknown one, for
            // the third time in this file.
            //
            // ⚠ <b>The used width and the containing block are two different numbers and both are
            // needed.</b> `outerInline` is how much room the item gets; `item.ResolvedInlineSize` —
            // the grid area — is what the item's own percentages are a fraction of, which is what
            // `PlaceGridItemBoxes` passes as `ownerWidth` and what CSS Grid §9 requires. Collapsing
            // them either way is a fixture: feeding the used width down as the owner makes
            // `grid_percent_items_width_and_padding`'s `padding: 3%` a fraction of the item instead
            // of the area, and feeding the area down as the available size is the bug above.
            // `areaInline` is the third, and it is only the border box the ratio needs.
            var areaInline = MathF.Max(0f, item.ResolvedInlineSize - marginRow);

            var statedInline = float.IsNaN(item.ResolvedInlineSize)
                ? float.NaN
                : ResolvedDimension(child, Dimension.Width, item.ResolvedInlineSize, item.ResolvedInlineSize, direction);

            var borderBoxInline = float.IsNaN(statedInline) ? areaInline : MathF.Max(0f, statedInline);
            var outerInline = float.IsNaN(statedInline) ? item.ResolvedInlineSize : borderBoxInline + marginRow;

            // ⚠ <b>An item's own stated height has to be resolved here, because the callee will not
            // do it.</b> `MeasureNodeWithoutChildren` answers a max-content request with the node's
            // padding and border and nothing else — every other algorithm in this store resolves a
            // child's specified size <i>before</i> calling down (the flex path through
            // `ComputeFlexBasisForChild`, the block path through `ResolveBlockChildBox`), so the
            // childless branch never has to. Skipping it here reports a `height: 100px` box as zero
            // tall, and because a `0fr` track's whole size comes from this number the track collapses
            // and every stretched sibling in it collapses with it. The inline axis was accidentally
            // immune: its min-content contribution already reads the preferred size.
            var statedHeight = StyleResolution.ProcessedDimension(in styles[child], Dimension.Height).Unit == LayoutUnit.Point
                ? ResolvedDimension(child, Dimension.Height, ownerHeight, ownerWidth, direction)
                : float.NaN;

            // ⚠ …and a ratio is the other way the block axis can be definite without being stated.
            // CSS Sizing §4.1: a box with a preferred aspect ratio and a definite inline size has a
            // definite block size. The inline size here came from the column pass, so it is.
            if (float.IsNaN(statedHeight) && !float.IsNaN(styles[child].AspectRatio) && styles[child].AspectRatio > 0f && !float.IsNaN(borderBoxInline)) {
                statedHeight = HeightAcrossRatio(child, direction, borderBoxInline, item.ResolvedInlineSize);
            }

            var automaticMinimumIsZero = AutomaticMinimumIsZero(in axis, in item, child, Dimension.Height);

            float measured;
            if (!float.IsNaN(statedHeight)) {
                // ⚠ <b><see cref="BoundAxis" /> and not <see cref="BoundAxisWithinMinAndMax" />: the
                // pair differ by exactly the padding-and-border floor, and that floor is the whole
                // of `grid_padding_border_overrides_size`.</b> CSS Box Model: a `border-box` size
                // smaller than the box's own edges is raised to them, so `height: 12px` on a box with
                // 14 points of vertical padding and border is a 14-point box. The inline axis was
                // never short of this — its contributions come back through
                // `CalculateLayoutInternal`, which floors on the way out — so the item laid out 14
                // tall inside a row this method had sized at 12. One axis measured the box and the
                // other measured the declaration.
                measured = BoundAxis(child, FlexDirection.Column, direction, statedHeight, ownerHeight, ownerWidth);
            } else {
                CalculateLayoutInternal(
                    child,
                    outerInline,
                    float.NaN,
                    direction,
                    float.IsNaN(item.ResolvedInlineSize) ? SizingMode.MaxContent : SizingMode.StretchFit,
                    SizingMode.MaxContent,
                    float.IsNaN(item.ResolvedInlineSize) ? ownerWidth : item.ResolvedInlineSize,
                    ownerHeight,
                    performLayout: false,
                    currentDepth
                );

                measured = results[child].MeasuredDimensions[(int) Dimension.Height];
            }

            // §12.5 step 1: a baseline-aligned item contributes the shim that carries it down to its
            // group's baseline as well as its own outer size. See ResolveBaselineShims.
            var height = MathF.Max(0f, measured) + marginColumn + item.BaselineShim;

            // §6.6 applies to the block axis too: an item spanning several tracks, one of which is
            // flexible, has an automatic minimum of zero however tall its contents are. That
            // zeroing is of the CONTENT-BASED minimum, which is the only part of the answer §6.6
            // is about — a stated height is not automatic and survives it.
            var contentBasedBlockMinimum = automaticMinimumIsZero && float.IsNaN(statedHeight)
                ? 0f
                : MathF.Max(0f, measured);

            // §6.6: an item whose tracks all state a fixed maximum cannot floor them past it.
            var blockCeiling = FixedMaximumAcross(in axis, in item);

            if (!float.IsNaN(blockCeiling)) {
                contentBasedBlockMinimum = MathF.Min(contentBasedBlockMinimum, MathF.Max(0f, blockCeiling - marginColumn));
            }

            // ⚠ <b>A stated `min-height` is the minimum contribution, and reading the measured
            // height instead is the block axis's version of the bug this method's summary warns
            // about for the inline one.</b> CSS Sizing §5: where the preferred size behaves as
            // `auto`, an item contributes its USED MINIMUM SIZE, so a 100-point-tall box with
            // `min-height: 50px` floors its row at 50 and not at 100. The inline axis has always
            // gone through `MinimumContribution` for this; the block axis only asked whether a
            // minimum was DEFINED — using it to switch §6.6's zeroing off — and then contributed
            // the full content height anyway, which is the whole `_003_fixedmin_` family: every
            // one of its failures was a height and not one of them was a width.
            var blockMinimum = MinimumContribution(
                child,
                Dimension.Height,
                direction,
                contentBasedBlockMinimum,
                ownerWidth,
                ownerHeight
            ) + marginColumn + item.BaselineShim;

            return new GridContribution(blockMinimum, height, height);
        }

        var margin = StyleResolution.MarginForAxis(in styles[child], FlexDirection.Row, ownerWidth);

        var minContent = MinContentContribution(child, FlexDirection.Row, direction, ownerWidth, ownerHeight) + margin;

        // ⚠ <b>The owner size handed to the max-content probe is NaN, and passing the real one is a
        // bug that looks like a track-sizing bug.</b> CSS Sizing §5.2.1: while an intrinsic
        // contribution is being calculated, a percentage against a containing block whose size is
        // not yet known behaves as <c>auto</c> — and that is exactly the situation here, because the
        // containing block is the grid area and the grid area is what this measurement is being
        // taken in order to size. Threading the grid's own owner width through instead resolves
        // <c>width: 100%</c> against a box two levels out: `grid_percent_items_100_percent_2_col_auto`
        // puts two 100%-wide items in a 200-point grid and gets two 200-point columns.
        CalculateLayoutInternal(
            child,
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

        var maxContent = results[child].MeasuredDimensions[(int) Dimension.Width] + margin;

        var contentBasedInlineMinimum = AutomaticMinimumIsZero(in axis, in item, child, Dimension.Width)
            ? 0f
            : minContent - margin;

        // §6.6: an item whose tracks all state a fixed maximum cannot floor them past it.
        var inlineCeiling = FixedMaximumAcross(in axis, in item);

        if (!float.IsNaN(inlineCeiling)) {
            contentBasedInlineMinimum = MathF.Min(contentBasedInlineMinimum, MathF.Max(0f, inlineCeiling - margin));
        }

        return new GridContribution(
            MinimumContribution(
                child,
                Dimension.Width,
                direction,
                contentBasedInlineMinimum,
                ownerWidth,
                ownerHeight
            ) + margin,
            minContent,
            MathF.Max(maxContent, minContent)
        );
    }

    /// <summary>
    ///     Whether CSS Grid §6.6 replaces this item's content-based automatic minimum with zero.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A multi-track item over a flexible track has an automatic minimum of zero, and
    ///         nothing about its contents changes that.</b> §6.6 grants the content-based minimum
    ///         only when the item spans a track with an <c>auto</c> minimum <i>and</i>, if it spans
    ///         more than one, none of them is flexible. The reason is circularity: a flexible track's
    ///         size is a share of the space left over, and letting a spanning item floor it would
    ///         make the leftover depend on the share.
    ///     </para>
    ///     <para>
    ///         The corpus's <c>_003_automin_</c> families are exactly this rule. A 100-point child
    ///         inside an item spanning <c>0fr 0fr</c> in a 60-point grid makes the item <b>zero</b>
    ///         wide in Chrome, not 100 — and the same item spanning <c>0fr 1fr</c> is 54, the whole
    ///         content box, because §12.7 gave all of it to the <c>1fr</c> track rather than because
    ///         anything measured the child.
    ///     </para>
    ///     <para>
    ///         ⚠ A scroll container is excluded for the same reason it is excluded from Flexbox §4.5:
    ///         being allowed to be smaller than its contents is what the property means.
    ///     </para>
    /// </remarks>
    bool AutomaticMinimumIsZero(in GridAxis axis, in GridItem item, int child, Dimension dimension) {
        if (OverflowOn(child, dimension) != Overflow.Visible) {
            return true;
        }

        var start = item.StartOn(axis.Inline);
        var span = item.SpanOn(axis.Inline);

        var spansAutoMinimum = false;
        var spansFlexible = false;

        for (var at = start; at < start + span; at++) {
            ref var track = ref Scratch.Track(axis.TracksAt + at);

            spansAutoMinimum |= track.Size.Min.Kind == GridSizingKind.Auto;
            spansFlexible |= track.Size.IsFlexible;
        }

        return !spansAutoMinimum || (span > 1 && spansFlexible);
    }

    /// <summary>
    ///     CSS Grid §6.6's ceiling on a content-based automatic minimum: the space the item's own
    ///     tracks can ever offer it, or NaN when they are not all fixed.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>§6.6 has a second paragraph, and it is the one that stops a track from being
    ///         dragged past a ceiling it stated.</b> After granting a content-based minimum, the rule
    ///         adds that <i>if the item spans only tracks that have a fixed max track sizing
    ///         function</i>, its content size suggestion is further clamped to the stretch fit into
    ///         the area's maximum size — the sum of those maxima plus any intervening fixed gutters.
    ///         Without it a <c>minmax(auto, 10px)</c> column holding 20 points of text is 20 wide,
    ///         because the automatic minimum floors the base size and §7.2.2 then lifts the growth
    ///         limit to meet it. Chrome makes it 10, which is what the author asked for.
    ///     </para>
    ///     <para>
    ///         ⚠ This clamps the CONTENT-based suggestion only. A stated <c>min-width</c> is not an
    ///         automatic minimum and §6.6 never reaches it, which is why the callers apply this to the
    ///         value they hand <see cref="MinimumContribution" /> rather than to its answer.
    ///     </para>
    ///     <para>
    ///         <see cref="GridSizingFunction.Resolve" /> answering NaN <i>is</i> the "not a fixed max
    ///         track sizing function" test: a <c>fr</c>, an <c>auto</c>, a <c>max-content</c>, a
    ///         <c>fit-content()</c> and a percentage against an indefinite container all return NaN,
    ///         and each of them is a maximum the track's content can still move.
    ///     </para>
    /// </remarks>
    float FixedMaximumAcross(in GridAxis axis, in GridItem item) {
        var start = item.StartOn(axis.Inline);
        var span = item.SpanOn(axis.Inline);
        var total = axis.Gap * (span - 1);

        for (var at = start; at < start + span; at++) {
            var resolved = Scratch.Track(axis.TracksAt + at).Size.Max.Resolve(axis.AvailableSpace);

            if (float.IsNaN(resolved)) {
                return float.NaN;
            }

            total += resolved;
        }

        return total;
    }

    /// <summary>CSS Sizing's minimum contribution: the smallest outer size an item can have.</summary>
    float MinimumContribution(
        int child,
        Dimension dimension,
        Direction direction,
        float minContentSize,
        float ownerWidth,
        float ownerHeight
    ) {
        var axis = dimension == Dimension.Width ? FlexDirection.Row : FlexDirection.Column;
        var reference = dimension == Dimension.Width ? ownerWidth : ownerHeight;

        // A definite preferred size is the whole answer — the contents are never consulted.
        if (StyleResolution.ProcessedDimension(in styles[child], dimension).Unit == LayoutUnit.Point) {
            var preferred = ResolvedDimension(child, dimension, reference, ownerWidth, direction);

            if (!float.IsNaN(preferred)) {
                return MathF.Max(0f, BoundAxisWithinMinAndMax(child, direction, axis, preferred, reference, ownerWidth));
            }
        }

        // A stated minimum is used as though it were the preferred size; only `min-*: auto` falls
        // through to the content-based automatic minimum.
        var stated = StyleResolution.ResolvedMinDimension(in styles[child], dimension, reference, ownerWidth, direction);

        var contribution = float.IsNaN(stated) ? minContentSize : stated;

        return MathF.Max(0f, BoundAxisWithinMinAndMax(child, direction, axis, contribution, reference, ownerWidth));
    }
}
