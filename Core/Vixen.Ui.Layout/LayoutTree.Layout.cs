// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Layout;

/// <summary>
///     The flexbox algorithm: CSS Flexible Box Layout Module Level 1, as Yoga implements it, over
///     the store rather than over a node graph.
/// </summary>
/// <remarks>
///     <para>
///         This is a port and it is deliberately a close one. The structure, the step numbering and
///         the order of operations follow Yoga's <c>CalculateLayout.cpp</c>, because the value here
///         is not a better arrangement of the same logic — it is the several hundred browser-derived
///         fixtures in <c>Generated/</c> that judge whether the logic is right at all. Rearranging
///         it would forfeit the ability to compare against the reference when a fixture fails.
///     </para>
///     <para>
///         What is *not* ported, and why: <c>display: contents</c> (outside doc 09's stated scope);
///         Yoga's errata flags and experimental features, none of which a default configuration
///         turns on; the grid axes; and the separate min-content measure callback, whose fallback —
///         asking the ordinary measure function for its size under <c>AtMost 0</c> — is what text
///         measurers answer with the longest word anyway.
///     </para>
/// </remarks>
public sealed partial class LayoutTree {
    uint generation;

    /// <summary>Lays out <paramref name="node" /> and everything under it.</summary>
    /// <param name="node">The root of the subtree to lay out.</param>
    /// <param name="ownerWidth">The width available to it, or NaN.</param>
    /// <param name="ownerHeight">The height available to it, or NaN.</param>
    /// <param name="ownerDirection">The writing direction it inherits.</param>
    /// <remarks>
    ///     Only dirty subtrees are descended into, so a static panel costs one comparison per frame.
    ///     The generation counter is what makes that safe: it forces every dirty node to be visited
    ///     at least once per pass, and lets the measurement cache serve every visit after that.
    /// </remarks>
    public void CalculateLayout(LayoutNodeId node, float ownerWidth, float ownerHeight, Direction ownerDirection) {
        var index = Validate(node);

        // Before anything takes a child span: sorting can move the arena. See LayoutTree.Order.cs.
        FlushChildOrder();

        generation++;

        ref var style = ref styles[index];
        var direction = StyleResolution.ResolveDirection(in style, ownerDirection);

        var width = float.NaN;
        var widthMode = SizingMode.MaxContent;
        if (HasDefiniteLength(index, Dimension.Width, ownerWidth)) {
            width = ResolvedDimension(index, Dimension.Width, ownerWidth, ownerWidth, direction)
                + StyleResolution.MarginForAxis(in style, FlexDirection.Row, ownerWidth);
            widthMode = SizingMode.StretchFit;
        } else if (!float.IsNaN(StyleResolution.ResolvedMaxDimension(in style, Dimension.Width, ownerWidth, ownerWidth, direction))) {
            width = StyleResolution.ResolvedMaxDimension(in style, Dimension.Width, ownerWidth, ownerWidth, direction);
            widthMode = SizingMode.FitContent;
        } else {
            width = ownerWidth;
            widthMode = float.IsNaN(width) ? SizingMode.MaxContent : SizingMode.StretchFit;
        }

        var height = float.NaN;
        var heightMode = SizingMode.MaxContent;
        if (HasDefiniteLength(index, Dimension.Height, ownerHeight)) {
            height = ResolvedDimension(index, Dimension.Height, ownerHeight, ownerWidth, direction)
                + StyleResolution.MarginForAxis(in style, FlexDirection.Column, ownerWidth);
            heightMode = SizingMode.StretchFit;
        } else if (!float.IsNaN(StyleResolution.ResolvedMaxDimension(in style, Dimension.Height, ownerHeight, ownerWidth, direction))) {
            height = StyleResolution.ResolvedMaxDimension(in style, Dimension.Height, ownerHeight, ownerWidth, direction);
            heightMode = SizingMode.FitContent;
        } else {
            height = ownerHeight;
            heightMode = float.IsNaN(height) ? SizingMode.MaxContent : SizingMode.StretchFit;
        }

        if (CalculateLayoutInternal(
                index,
                width,
                height,
                ownerDirection,
                widthMode,
                heightMode,
                ownerWidth,
                ownerHeight,
                performLayout: true,
                0
            )) {
            SetPosition(index, results[index].Direction, ownerWidth, ownerHeight);
            RoundToPixelGrid(index, 0d, 0d);
        }
    }

    /// <summary>Decides whether a layout request is redundant, and answers from cache if it is.</summary>
    /// <returns>Whether anything was actually computed.</returns>
    bool CalculateLayoutInternal(
        int index,
        float availableWidth,
        float availableHeight,
        Direction ownerDirection,
        SizingMode widthSizingMode,
        SizingMode heightSizingMode,
        float ownerWidth,
        float ownerHeight,
        bool performLayout,
        int currentDepth
    ) {
        currentDepth++;
        if (currentDepth > LayoutLimits.MaximumLayoutDepth) {
            throw new InvalidOperationException(
                $"Layout re-entered node {index} more than {LayoutLimits.MaximumLayoutDepth} times. Either the tree "
                + "is deeper than that, or a measure function is answering the same question differently each time, "
                + "which makes the algorithm oscillate instead of settle."
            );
        }

        ref var layout = ref results[index];
        var dirty = (flags[index] & LayoutNodeState.Dirty) != 0;
        var needToVisit = (dirty && layout.GenerationCount != generation) || layout.LastOwnerDirection != ownerDirection;

        if (needToVisit) {
            layout.NextCachedMeasurementsIndex = 0;
            layout.CachedLayout = new CachedMeasurement {
                AvailableWidth = -1f,
                AvailableHeight = -1f,
                WidthMeasureMode = MeasureMode.Undefined,
                HeightMeasureMode = MeasureMode.Undefined,
                ComputedWidth = -1f,
                ComputedHeight = -1f
            };
        }

        var cached = -1;
        var cachedIsLayout = false;

        if ((flags[index] & LayoutNodeState.HasMeasureFunction) != 0) {
            var marginRow = StyleResolution.MarginForAxis(in styles[index], FlexDirection.Row, ownerWidth);
            var marginColumn = StyleResolution.MarginForAxis(in styles[index], FlexDirection.Column, ownerWidth);

            if (CanUseCachedMeasurement(
                    widthSizingMode,
                    availableWidth,
                    heightSizingMode,
                    availableHeight,
                    in layout.CachedLayout,
                    marginRow,
                    marginColumn
                )) {
                cachedIsLayout = true;
                cached = 0;
            } else {
                for (var i = 0; i < layout.NextCachedMeasurementsIndex; i++) {
                    if (CanUseCachedMeasurement(
                            widthSizingMode,
                            availableWidth,
                            heightSizingMode,
                            availableHeight,
                            in layout.CachedMeasurements[i],
                            marginRow,
                            marginColumn
                        )) {
                        cached = i;
                        break;
                    }
                }
            }
        } else if (performLayout) {
            if (Inexact(layout.CachedLayout.AvailableWidth, availableWidth)
                && Inexact(layout.CachedLayout.AvailableHeight, availableHeight)
                && layout.CachedLayout.WidthMeasureMode == MeasureModeOf(widthSizingMode)
                && layout.CachedLayout.HeightMeasureMode == MeasureModeOf(heightSizingMode)
                && layout.CachedLayout.IsPopulated) {
                cachedIsLayout = true;
                cached = 0;
            }
        } else {
            for (var i = 0; i < layout.NextCachedMeasurementsIndex; i++) {
                ref var entry = ref layout.CachedMeasurements[i];
                if (Inexact(entry.AvailableWidth, availableWidth)
                    && Inexact(entry.AvailableHeight, availableHeight)
                    && entry.WidthMeasureMode == MeasureModeOf(widthSizingMode)
                    && entry.HeightMeasureMode == MeasureModeOf(heightSizingMode)) {
                    cached = i;
                    break;
                }
            }
        }

        if (!needToVisit && cached >= 0) {
            ref var entry = ref cachedIsLayout ? ref layout.CachedLayout : ref layout.CachedMeasurements[cached];
            layout.MeasuredDimensions[(int) Dimension.Width] = entry.ComputedWidth;
            layout.MeasuredDimensions[(int) Dimension.Height] = entry.ComputedHeight;
            layout.UnclampedMeasuredDimensions[(int) Dimension.Width] = entry.UnclampedComputedWidth;
            layout.UnclampedMeasuredDimensions[(int) Dimension.Height] = entry.UnclampedComputedHeight;

            // A layout has more outputs than a size once block containers exist. See the remarks on
            // CachedMeasurement.TopCollapsibleMargin for why replaying them is not optional.
            layout.TopCollapsibleMargin = entry.TopCollapsibleMargin;
            layout.BottomCollapsibleMargin = entry.BottomCollapsibleMargin;
            layout.MarginsCollapseThrough = entry.MarginsCollapseThrough;
            layout.InlineBaseline = entry.InlineBaseline;
        } else {
            CalculateLayoutImpl(
                index,
                availableWidth,
                availableHeight,
                ownerDirection,
                widthSizingMode,
                heightSizingMode,
                ownerWidth,
                ownerHeight,
                performLayout,
                currentDepth
            );

            layout = ref results[index];
            layout.LastOwnerDirection = ownerDirection;

            if (cached < 0) {
                if (layout.NextCachedMeasurementsIndex == LayoutLimits.MaximumCachedMeasurements) {
                    layout.NextCachedMeasurementsIndex = 0;
                }

                var entry = new CachedMeasurement {
                    AvailableWidth = availableWidth,
                    AvailableHeight = availableHeight,
                    WidthMeasureMode = MeasureModeOf(widthSizingMode),
                    HeightMeasureMode = MeasureModeOf(heightSizingMode),
                    ComputedWidth = layout.MeasuredDimensions[(int) Dimension.Width],
                    ComputedHeight = layout.MeasuredDimensions[(int) Dimension.Height],
                    UnclampedComputedWidth = layout.UnclampedMeasuredDimensions[(int) Dimension.Width],
                    UnclampedComputedHeight = layout.UnclampedMeasuredDimensions[(int) Dimension.Height],
                    IsPopulated = true,
                    TopCollapsibleMargin = layout.TopCollapsibleMargin,
                    BottomCollapsibleMargin = layout.BottomCollapsibleMargin,
                    MarginsCollapseThrough = layout.MarginsCollapseThrough,
                    InlineBaseline = layout.InlineBaseline
                };

                if (performLayout) {
                    layout.CachedLayout = entry;
                } else {
                    layout.CachedMeasurements[(int) layout.NextCachedMeasurementsIndex] = entry;
                    layout.NextCachedMeasurementsIndex++;
                }
            }
        }

        if (performLayout) {
            layout.Dimensions[(int) Dimension.Width] = layout.MeasuredDimensions[(int) Dimension.Width];
            layout.Dimensions[(int) Dimension.Height] = layout.MeasuredDimensions[(int) Dimension.Height];
            flags[index] |= LayoutNodeState.HasNewLayout;
            flags[index] &= ~LayoutNodeState.Dirty;
        }

        layout.GenerationCount = generation;
        return needToVisit || cached < 0;
    }

    /// <summary>The algorithm proper. Steps are numbered as in the specification and in Yoga.</summary>
    void CalculateLayoutImpl(
        int index,
        float availableWidth,
        float availableHeight,
        Direction ownerDirection,
        SizingMode widthSizingMode,
        SizingMode heightSizingMode,
        float ownerWidth,
        float ownerHeight,
        bool performLayout,
        int currentDepth
    ) {
        results[index].ImplGeneration = generation;
        var direction = StyleResolution.ResolveDirection(in styles[index], ownerDirection);
        results[index].Direction = direction;

        var flexRow = FlexAxis.Resolve(FlexDirection.Row, direction);
        var flexColumn = FlexAxis.Resolve(FlexDirection.Column, direction);
        var startEdge = direction == Direction.Ltr ? Edge.Left : Edge.Right;
        var endEdge = direction == Direction.Ltr ? Edge.Right : Edge.Left;

        var marginRowLeading = StyleResolution.InlineStartMargin(in styles[index], flexRow, direction, ownerWidth);
        var marginRowTrailing = StyleResolution.InlineEndMargin(in styles[index], flexRow, direction, ownerWidth);
        var marginColumnLeading = StyleResolution.InlineStartMargin(in styles[index], flexColumn, direction, ownerWidth);
        var marginColumnTrailing = StyleResolution.InlineEndMargin(in styles[index], flexColumn, direction, ownerWidth);

        results[index].Margin[(int) startEdge] = marginRowLeading;
        results[index].Margin[(int) endEdge] = marginRowTrailing;
        results[index].Margin[(int) Edge.Top] = marginColumnLeading;
        results[index].Margin[(int) Edge.Bottom] = marginColumnTrailing;

        var marginAxisRow = marginRowLeading + marginRowTrailing;
        var marginAxisColumn = marginColumnLeading + marginColumnTrailing;

        results[index].Border[(int) startEdge] = StyleResolution.InlineStartBorder(in styles[index], flexRow, direction);
        results[index].Border[(int) endEdge] = StyleResolution.InlineEndBorder(in styles[index], flexRow, direction);
        results[index].Border[(int) Edge.Top] = StyleResolution.InlineStartBorder(in styles[index], flexColumn, direction);
        results[index].Border[(int) Edge.Bottom] = StyleResolution.InlineEndBorder(in styles[index], flexColumn, direction);

        results[index].Padding[(int) startEdge] = StyleResolution.InlineStartPadding(in styles[index], flexRow, direction, ownerWidth);
        results[index].Padding[(int) endEdge] = StyleResolution.InlineEndPadding(in styles[index], flexRow, direction, ownerWidth);
        results[index].Padding[(int) Edge.Top] = StyleResolution.InlineStartPadding(in styles[index], flexColumn, direction, ownerWidth);
        results[index].Padding[(int) Edge.Bottom] = StyleResolution.InlineEndPadding(in styles[index], flexColumn, direction, ownerWidth);

        // ⚠ Every algorithm has to answer block layout's three extra questions, including the ones
        // that have never heard of it. A flex container, a text leaf and an empty box are all
        // *barriers* to margin collapsing — CSS 2.1 §8.3.1 collapses margins only through boxes in
        // the same block formatting context — so the honest default is "my own margin, and no".
        // Leaving these stale from a previous pass is what makes a margin appear to leak out of a
        // flex container, so they are written before anything can return.
        results[index].TopCollapsibleMargin = CollapsibleMargin.From(marginColumnLeading);
        results[index].BottomCollapsibleMargin = CollapsibleMargin.From(marginColumnTrailing);
        results[index].MarginsCollapseThrough = false;

        // ⚠ And the fourth algorithm's one extra question, cleared for the same reason. "I have no
        // line boxes" is the honest answer from everything that is not an inline formatting context,
        // and it is what sends `CalculateBaseline` to CSS Align §9.3's synthesis rule instead of to a
        // baseline some earlier pass left on this node.
        results[index].InlineBaseline = float.NaN;

        // ⚠ And the fifth question, cleared here for a reason the four above do not have. A node's
        // fragments are written by its *parent's* line walk, after this runs — so clearing them on
        // the way in is what makes "I was two boxes last frame and I am one this frame" work. The
        // case is not hypothetical: widening a container until a span stops crossing a line is the
        // ordinary way a fragment disappears, and a stale second box would go on being painted.
        if (performLayout) {
            WriteFragments(index, default);
        }

        if ((flags[index] & LayoutNodeState.HasMeasureFunction) != 0) {
            MeasureNodeWithMeasureFunction(
                index,
                direction,
                availableWidth - marginAxisRow,
                availableHeight - marginAxisColumn,
                widthSizingMode,
                heightSizingMode,
                ownerWidth,
                ownerHeight
            );

            return;
        }

        // ⚠ Block layout is entered before the childless shortcut, and that is deliberate. An empty
        // `display: block` box with no border, padding or height is exactly §8.3.1's collapse-through
        // case — the margins above and below it meet *through* it — and the shortcut below would
        // return a size without ever reporting that. Twenty fixtures in the block corpus turn on it.
        var childCount = links[index].ChildCount;

        // ⚠ Grid joins block ahead of the childless shortcut, and for a different reason. An empty
        // grid container is not a plain box: `grid-template-rows: 40px 40px` with no items in it is
        // 80 points tall, because §12 sizes the tracks the template declared whether or not anything
        // landed in them. The shortcut below would report the height of its own padding.
        if (styles[index].Display == Display.Grid) {
            CalculateGridLayoutImpl(
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

            return;
        }

        // ⚠ The inner display type decides the algorithm and the outer one decides nothing here.
        // CSS Display §2.1 makes `inline-block` a box whose *outside* is inline and whose *inside* is
        // flow — so it runs block layout, exactly as `block` does, and the only difference is who
        // asks and with which sizing mode. That difference is entirely in the caller: an inline
        // formatting context asks with `FitContent`, and §10.3.9's shrink-to-fit is what the block
        // path already does when it is not asked with `StretchFit`. Nothing about *being* inline-level
        // is visible from in here, which is why this is one condition and not two algorithms.
        if (styles[index].Display is Display.Block or Display.InlineBlock or Display.Inline) {
            // An inline formatting context is not a variant of block layout, it is what a block
            // container does instead when everything in it is inline-level. See LayoutTree.Inline.cs.
            if (EstablishesInlineFormattingContext(index)) {
                CalculateInlineLayoutImpl(
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

                return;
            }

            CalculateBlockLayoutImpl(
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

            return;
        }

        if (childCount == 0) {
            MeasureNodeWithoutChildren(
                index,
                direction,
                availableWidth - marginAxisRow,
                availableHeight - marginAxisColumn,
                widthSizingMode,
                heightSizingMode,
                ownerWidth,
                ownerHeight
            );

            return;
        }

        if (!performLayout
            && MeasureNodeWithFixedSize(
                index,
                direction,
                availableWidth - marginAxisRow,
                availableHeight - marginAxisColumn,
                widthSizingMode,
                heightSizingMode,
                ownerWidth,
                ownerHeight
            )) {
            return;
        }

        results[index].HadOverflow = false;

        // STEP 1: CALCULATE VALUES FOR REMAINDER OF ALGORITHM
        var mainAxis = FlexAxis.Resolve(styles[index].FlexDirection, direction);
        var crossAxis = FlexAxis.ResolveCross(mainAxis, direction);
        var isMainAxisRow = FlexAxis.IsRow(mainAxis);
        var isNodeFlexWrap = styles[index].FlexWrap != Wrap.NoWrap;

        var mainAxisOwnerSize = isMainAxisRow ? ownerWidth : ownerHeight;
        var crossAxisOwnerSize = isMainAxisRow ? ownerHeight : ownerWidth;

        var paddingAndBorderAxisMain = StyleResolution.PaddingAndBorderForAxis(in styles[index], mainAxis, direction, ownerWidth);
        var paddingAndBorderAxisCross = StyleResolution.PaddingAndBorderForAxis(in styles[index], crossAxis, direction, ownerWidth);
        var leadingPaddingAndBorderCross = StyleResolution.FlexStartPaddingAndBorder(in styles[index], crossAxis, direction, ownerWidth);

        var sizingModeMainDim = isMainAxisRow ? widthSizingMode : heightSizingMode;
        var sizingModeCrossDim = isMainAxisRow ? heightSizingMode : widthSizingMode;

        var paddingAndBorderAxisRow = isMainAxisRow ? paddingAndBorderAxisMain : paddingAndBorderAxisCross;
        var paddingAndBorderAxisColumn = isMainAxisRow ? paddingAndBorderAxisCross : paddingAndBorderAxisMain;

        // STEP 2: DETERMINE AVAILABLE SIZE IN MAIN AND CROSS DIRECTIONS
        var availableInnerWidth = AvailableInnerDimension(
            index,
            direction,
            Dimension.Width,
            availableWidth - marginAxisRow,
            paddingAndBorderAxisRow,
            ownerWidth,
            ownerWidth
        );

        var availableInnerHeight = AvailableInnerDimension(
            index,
            direction,
            Dimension.Height,
            availableHeight - marginAxisColumn,
            paddingAndBorderAxisColumn,
            ownerHeight,
            ownerWidth
        );

        var availableInnerMainDim = isMainAxisRow ? availableInnerWidth : availableInnerHeight;
        var availableInnerCrossDim = isMainAxisRow ? availableInnerHeight : availableInnerWidth;

        // STEP 3: DETERMINE FLEX BASIS FOR EACH ITEM
        ComputeFlexBasisForChildren(
            index,
            availableInnerWidth,
            availableInnerHeight,
            widthSizingMode,
            heightSizingMode,
            direction,
            mainAxis,
            performLayout,
            currentDepth
        );

        // ⚠ CSS Flexbox §9.2 step 9: an item's HYPOTHETICAL main size is its flex base size clamped
        // by its used min and max — and for `min-width: auto` the used minimum is §4.5's automatic
        // one. §9.3 then collects items into lines by that hypothetical size, so the floor has to
        // exist before the first line is measured, not after. It used to be computed inside
        // ResolveFlexibleLength, which runs per line and only when something actually flexes; an
        // item that never flexed therefore never saw its own floor, and line breaking never saw it
        // at all. Both are one number, so both are computed here, once, for the whole node.
        //
        // ⚠ AND THE OVERFLOW TEST BELOW IS THE SAME SUM §9.3 IS, so it is taken in the same walk.
        // "Do the items overflow the main axis" is a question about their outer HYPOTHETICAL sizes,
        // not about their flex bases: an item's base is what it would be with nothing constraining
        // it, and a `min-width: 60px` item takes 60 points of the line whatever its base says. This
        // used to add up the bases, which agreed with the hypothetical sizes only by accident —
        // ComputedFlexBasis was read back out of an already-clamped measurement. Once it became a
        // real base, `gap_column_gap_wrap_align_stretch` measured five zero-basis items as 20 points
        // of gap in a 300-point row, decided nothing overflowed, and stretched every item to the
        // container's full height instead of sharing it between the two lines it still broke into.
        var totalMainDim = 0f;
        foreach (var child in ChildIds(index)) {
            if (!IsInFlow(child)) {
                continue;
            }

            results[child].ComputedAutoMinMainSize =
                ComputeAutoMinMainSize(child, mainAxis, direction, mainAxisOwnerSize, availableInnerWidth, availableInnerHeight);

            totalMainDim += HypotheticalMainSize(child, direction, mainAxis, results[child].ComputedFlexBasis, mainAxisOwnerSize, ownerWidth)
                + StyleResolution.MarginForAxis(in styles[child], mainAxis, availableInnerWidth);
        }

        if (childCount > 1) {
            totalMainDim += StyleResolution.GapForAxis(in styles[index], mainAxis, availableInnerMainDim) * (childCount - 1);
        }

        var mainAxisOverflows = sizingModeMainDim != SizingMode.MaxContent && totalMainDim > availableInnerMainDim;
        if (isNodeFlexWrap && mainAxisOverflows && sizingModeMainDim == SizingMode.FitContent) {
            sizingModeMainDim = SizingMode.StretchFit;
        }

        // STEP 4: COLLECT FLEX ITEMS INTO FLEX LINES
        var startOfLine = 0;
        var lineCount = 0;
        var totalLineCrossDim = 0f;
        var crossAxisGap = StyleResolution.GapForAxis(in styles[index], crossAxis, availableInnerCrossDim);
        var maxLineMainDim = 0f;

        while (startOfLine < childCount) {
            var line = CalculateFlexLine(
                index,
                ownerDirection,
                ownerWidth,
                mainAxisOwnerSize,
                availableInnerWidth,
                availableInnerMainDim,
                startOfLine,
                lineCount
            );

            startOfLine = line.EndChild;

            var canSkipFlex = !performLayout && sizingModeCrossDim == SizingMode.StretchFit;

            // STEP 5: RESOLVING FLEXIBLE LENGTHS ON MAIN AXIS
            var sizeBasedOnContent = false;
            if (sizingModeMainDim != SizingMode.StretchFit) {
                var minInnerWidth = StyleResolution.ResolvedMinDimension(in styles[index], Dimension.Width, ownerWidth, ownerWidth, direction) - paddingAndBorderAxisRow;
                var maxInnerWidth = StyleResolution.ResolvedMaxDimension(in styles[index], Dimension.Width, ownerWidth, ownerWidth, direction) - paddingAndBorderAxisRow;
                var minInnerHeight = StyleResolution.ResolvedMinDimension(in styles[index], Dimension.Height, ownerHeight, ownerWidth, direction) - paddingAndBorderAxisColumn;
                var maxInnerHeight = StyleResolution.ResolvedMaxDimension(in styles[index], Dimension.Height, ownerHeight, ownerWidth, direction) - paddingAndBorderAxisColumn;

                var minInnerMainDim = isMainAxisRow ? minInnerWidth : minInnerHeight;
                var maxInnerMainDim = isMainAxisRow ? maxInnerWidth : maxInnerHeight;

                if (!float.IsNaN(minInnerMainDim) && line.SizeConsumed < minInnerMainDim) {
                    availableInnerMainDim = minInnerMainDim;
                } else if (!float.IsNaN(maxInnerMainDim) && line.SizeConsumed > maxInnerMainDim) {
                    availableInnerMainDim = maxInnerMainDim;
                } else {
                    if (line.TotalFlexGrowFactors == 0f
                        || StyleResolution.ResolveFlexGrow(in styles[index], links[index].Parent < 0) == 0f) {
                        // Nothing here can flex, so the space used is all the space needed.
                        availableInnerMainDim = line.SizeConsumed;
                    }

                    sizeBasedOnContent = true;
                }
            }

            // ⚠ §9.7 step 1 picks the factor from the sum of the HYPOTHETICAL main sizes, and step 3
            // then builds the pool out of flex base sizes instead. Both are needed and they are
            // different numbers — see InitialFreeSpace.
            line.UseGrow = line.HypotheticalSizeConsumed < availableInnerMainDim;

            if (!sizeBasedOnContent && !float.IsNaN(availableInnerMainDim)) {
                line.RemainingFreeSpace = InitialFreeSpace(
                    index,
                    ref line,
                    direction,
                    mainAxis,
                    ownerWidth,
                    mainAxisOwnerSize,
                    availableInnerMainDim
                );
            } else if (line.SizeConsumed < 0f) {
                line.RemainingFreeSpace = -line.SizeConsumed;
            }

            if (!canSkipFlex) {
                ResolveFlexibleLength(
                    index,
                    ref line,
                    mainAxis,
                    crossAxis,
                    direction,
                    ownerWidth,
                    mainAxisOwnerSize,
                    availableInnerMainDim,
                    availableInnerCrossDim,
                    availableInnerWidth,
                    availableInnerHeight,
                    mainAxisOverflows,
                    sizingModeCrossDim,
                    performLayout,
                    currentDepth
                );
            }

            results[index].HadOverflow = results[index].HadOverflow || line.RemainingFreeSpace < 0f;

            // STEP 6: MAIN-AXIS JUSTIFICATION & CROSS-AXIS SIZE DETERMINATION
            JustifyMainAxis(
                index,
                ref line,
                mainAxis,
                crossAxis,
                direction,
                sizingModeMainDim,
                sizingModeCrossDim,
                mainAxisOwnerSize,
                ownerWidth,
                availableInnerMainDim,
                availableInnerCrossDim,
                availableInnerWidth,
                performLayout
            );

            var containerCrossAxis = availableInnerCrossDim;
            if (sizingModeCrossDim is SizingMode.MaxContent or SizingMode.FitContent) {
                containerCrossAxis = BoundAxis(
                        index,
                        crossAxis,
                        direction,
                        line.CrossDim + paddingAndBorderAxisCross,
                        crossAxisOwnerSize,
                        ownerWidth
                    )
                    - paddingAndBorderAxisCross;
            }

            if (!isNodeFlexWrap && sizingModeCrossDim == SizingMode.StretchFit) {
                line.CrossDim = availableInnerCrossDim;
            }

            if (!isNodeFlexWrap) {
                line.CrossDim = BoundAxis(
                        index,
                        crossAxis,
                        direction,
                        line.CrossDim + paddingAndBorderAxisCross,
                        crossAxisOwnerSize,
                        ownerWidth
                    )
                    - paddingAndBorderAxisCross;
            }

            // STEP 7: CROSS-AXIS ALIGNMENT
            if (performLayout) {
                var children = ChildIds(index);
                for (var i = line.StartChild; i < line.EndChild; i++) {
                    var child = children[i];
                    if (!IsInFlow(child)) {
                        continue;
                    }

                    var leadingCrossDim = leadingPaddingAndBorderCross;
                    var alignItem = ResolveChildAlignment(index, child);

                    if (alignItem == Align.Stretch
                        && !StyleResolution.FlexStartMarginIsAuto(in styles[child], crossAxis, direction)
                        && !StyleResolution.FlexEndMarginIsAuto(in styles[child], crossAxis, direction)) {
                        if (!HasDefiniteLength(child, FlexAxis.DimensionOf(crossAxis), availableInnerCrossDim)) {
                            var childMainSize = results[child].MeasuredDimensions[(int) FlexAxis.DimensionOf(mainAxis)];

                            // ⚠ A STRETCHED CROSS AXIS IS NOT THE RATIO'S TO DECIDE, and reading the
                            // ratio here instead of the line was four families' worth of wrong.
                            // `align-items: stretch` gives the item the line's cross size outright —
                            // CSS Flexbox §9.4 stretches an item whose cross size is `auto`, and an
                            // aspect ratio does not make it not-`auto`. Deriving the cross size from
                            // the main size instead answered 40x20 where Chrome says 40x100 for
                            // `aspect_ratio_flex_row_stretch_fill_height`, and it is the ratio that
                            // yields: with the cross size stretched the ratio has nothing left to
                            // say, because the main size was already decided by the flex algorithm.
                            //
                            // The ratio is not lost — the item's own layout still transfers the
                            // stretched cross size back into a main size that is `auto`, which is
                            // what `aspect_ratio_flex_column_stretch_fill_max_width` needs, and the
                            // item's own maximum still caps the stretch. What must NOT happen is a
                            // bound transferred across the ratio landing on this axis: the transfer
                            // belongs to the axis the ratio decides, and this one is not it.
                            var childCrossSize = line.CrossDim;

                            childMainSize += StyleResolution.MarginForAxis(in styles[child], mainAxis, availableInnerWidth);

                            var childMainSizingMode = SizingMode.StretchFit;
                            var childCrossSizingMode = SizingMode.StretchFit;
                            ConstrainMaxSizeForMode(child, direction, mainAxis, availableInnerMainDim, availableInnerWidth, ref childMainSizingMode, ref childMainSize);
                            ConstrainMaxSizeForMode(child, direction, crossAxis, availableInnerCrossDim, availableInnerWidth, ref childCrossSizingMode, ref childCrossSize);

                            var childWidth = isMainAxisRow ? childMainSize : childCrossSize;
                            var childHeight = !isMainAxisRow ? childMainSize : childCrossSize;

                            var crossAxisDoesNotGrow = styles[index].AlignContent != Align.Stretch && isNodeFlexWrap;
                            var childWidthSizingMode = float.IsNaN(childWidth) || (!isMainAxisRow && crossAxisDoesNotGrow)
                                ? SizingMode.MaxContent
                                : SizingMode.StretchFit;
                            var childHeightSizingMode = float.IsNaN(childHeight) || (isMainAxisRow && crossAxisDoesNotGrow)
                                ? SizingMode.MaxContent
                                : SizingMode.StretchFit;

                            CalculateLayoutInternal(
                                child,
                                childWidth,
                                childHeight,
                                direction,
                                childWidthSizingMode,
                                childHeightSizingMode,
                                availableInnerWidth,
                                availableInnerHeight,
                                performLayout: true,
                                currentDepth
                            );
                        }
                    } else {
                        var remainingCrossDim = containerCrossAxis - DimensionWithMargin(child, crossAxis, availableInnerWidth);
                        var startIsAuto = StyleResolution.FlexStartMarginIsAuto(in styles[child], crossAxis, direction);
                        var endIsAuto = StyleResolution.FlexEndMarginIsAuto(in styles[child], crossAxis, direction);

                        if (startIsAuto && endIsAuto) {
                            leadingCrossDim += MathF.Max(0f, remainingCrossDim / 2f);
                        } else if (endIsAuto) {
                            // No-op: the space goes after the item.
                        } else if (startIsAuto) {
                            leadingCrossDim += MathF.Max(0f, remainingCrossDim);
                        } else if (alignItem == Align.FlexStart) {
                            // No-op.
                        } else if (alignItem == Align.Center) {
                            leadingCrossDim += remainingCrossDim / 2f;
                        } else {
                            leadingCrossDim += remainingCrossDim;
                        }
                    }

                    results[child].Position[(int) FlexAxis.FlexStartEdge(crossAxis)] += totalLineCrossDim + leadingCrossDim;
                }
            }

            totalLineCrossDim += line.CrossDim + (lineCount != 0 ? crossAxisGap : 0f);
            maxLineMainDim = MathF.Max(maxLineMainDim, line.MainDim);
            lineCount++;
        }

        // STEP 8: MULTI-LINE CONTENT ALIGNMENT
        if (performLayout && (isNodeFlexWrap || IsBaselineLayout(index))) {
            AlignLines(
                index,
                direction,
                crossAxis,
                mainAxis,
                isMainAxisRow,
                lineCount,
                totalLineCrossDim,
                crossAxisGap,
                sizingModeCrossDim,
                availableInnerCrossDim,
                availableInnerWidth,
                availableInnerHeight,
                crossAxisOwnerSize,
                ownerWidth,
                paddingAndBorderAxisCross,
                leadingPaddingAndBorderCross,
                currentDepth
            );
        }

        // STEP 9: COMPUTING FINAL DIMENSIONS
        SetMeasuredDimension(
            index,
            FlexDirection.Row,
            direction,
            availableWidth - marginAxisRow,
            ownerWidth,
            ownerWidth,
            widthSizingMode == SizingMode.StretchFit
        );
        SetMeasuredDimension(
            index,
            FlexDirection.Column,
            direction,
            availableHeight - marginAxisColumn,
            ownerHeight,
            ownerWidth,
            heightSizingMode == SizingMode.StretchFit
        );

        // ⚠ One reading per axis. A scroll container's fit-content size is the room it was offered
        // rather than the room its content wants — that is what stops a list of two hundred rows from
        // making its own panel two hundred rows tall — and it is true only of the axis that scrolls.
        var mainOverflow = OverflowOn(index, FlexAxis.DimensionOf(mainAxis));
        var crossOverflow = OverflowOn(index, FlexAxis.DimensionOf(crossAxis));

        if (sizingModeMainDim == SizingMode.MaxContent
            || (mainOverflow != Overflow.Scroll && sizingModeMainDim == SizingMode.FitContent)) {
            SetMeasuredDimension(index, mainAxis, direction, maxLineMainDim, mainAxisOwnerSize, ownerWidth);
        } else if (sizingModeMainDim == SizingMode.FitContent && mainOverflow == Overflow.Scroll) {
            var scrolledMain = MathF.Max(
                MathF.Min(
                    availableInnerMainDim + paddingAndBorderAxisMain,
                    BoundAxisWithinMinAndMax(index, direction, mainAxis, maxLineMainDim, mainAxisOwnerSize, ownerWidth)
                ),
                paddingAndBorderAxisMain
            );

            SetMeasuredDimension(index, FlexAxis.DimensionOf(mainAxis), scrolledMain, scrolledMain);
        }

        if (sizingModeCrossDim == SizingMode.MaxContent
            || (crossOverflow != Overflow.Scroll && sizingModeCrossDim == SizingMode.FitContent)) {
            SetMeasuredDimension(
                index,
                crossAxis,
                direction,
                totalLineCrossDim + paddingAndBorderAxisCross,
                crossAxisOwnerSize,
                ownerWidth
            );
        } else if (sizingModeCrossDim == SizingMode.FitContent && crossOverflow == Overflow.Scroll) {
            var scrolledCross = MathF.Max(
                MathF.Min(
                    availableInnerCrossDim + paddingAndBorderAxisCross,
                    BoundAxisWithinMinAndMax(
                        index,
                        direction,
                        crossAxis,
                        totalLineCrossDim + paddingAndBorderAxisCross,
                        crossAxisOwnerSize,
                        ownerWidth
                    )
                ),
                paddingAndBorderAxisCross
            );

            SetMeasuredDimension(index, FlexAxis.DimensionOf(crossAxis), scrolledCross, scrolledCross);
        }

        // Only forward wrapping has been done so far; wrap-reverse is that mirrored.
        if (performLayout && styles[index].FlexWrap == Wrap.WrapReverse) {
            var children = ChildIds(index);
            var crossEdge = (int) FlexAxis.FlexStartEdge(crossAxis);
            var crossDimension = (int) FlexAxis.DimensionOf(crossAxis);
            foreach (var child in children) {
                if (styles[child].PositionType != PositionType.Absolute) {
                    results[child].Position[crossEdge] = results[index].MeasuredDimensions[crossDimension]
                        - results[child].Position[crossEdge]
                        - results[child].MeasuredDimensions[crossDimension];
                }
            }
        }

        if (!performLayout) {
            return;
        }

        // STEP 10: SETTING TRAILING POSITIONS FOR CHILDREN
        var needsMainTrailing = NeedsTrailingPosition(mainAxis);
        var needsCrossTrailing = NeedsTrailingPosition(crossAxis);
        if (needsMainTrailing || needsCrossTrailing) {
            foreach (var child in ChildIds(index)) {
                if (styles[child].Display == Display.None || styles[child].PositionType == PositionType.Absolute) {
                    continue;
                }

                if (needsMainTrailing) {
                    SetChildTrailingPosition(index, child, mainAxis);
                }

                if (needsCrossTrailing) {
                    SetChildTrailingPosition(index, child, crossAxis);
                }
            }
        }

        // STEP 11: SIZING AND POSITIONING ABSOLUTE CHILDREN
        if (styles[index].PositionType != PositionType.Static || currentDepth == 1) {
            LayoutAbsoluteDescendants(
                index,
                index,
                isMainAxisRow ? sizingModeMainDim : sizingModeCrossDim,
                direction,
                currentDepth,
                0f,
                0f,
                availableInnerWidth,
                availableInnerHeight
            );
        }
    }
}
