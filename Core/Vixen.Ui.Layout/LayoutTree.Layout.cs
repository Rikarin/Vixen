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
                    IsPopulated = true
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

        var childCount = links[index].ChildCount;
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
        var totalMainDim = ComputeFlexBasisForChildren(
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

            if (!sizeBasedOnContent && !float.IsNaN(availableInnerMainDim)) {
                line.RemainingFreeSpace = availableInnerMainDim - line.SizeConsumed;
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
                            var aspectRatio = styles[child].AspectRatio;
                            var childCrossSize = !float.IsNaN(aspectRatio)
                                ? StyleResolution.MarginForAxis(in styles[child], crossAxis, availableInnerWidth)
                                + (isMainAxisRow ? childMainSize / aspectRatio : childMainSize * aspectRatio)
                                : line.CrossDim;

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
        results[index].MeasuredDimensions[(int) Dimension.Width] =
            BoundAxis(index, FlexDirection.Row, direction, availableWidth - marginAxisRow, ownerWidth, ownerWidth);
        results[index].MeasuredDimensions[(int) Dimension.Height] =
            BoundAxis(index, FlexDirection.Column, direction, availableHeight - marginAxisColumn, ownerHeight, ownerWidth);

        var overflow = styles[index].Overflow;

        if (sizingModeMainDim == SizingMode.MaxContent
            || (overflow != Overflow.Scroll && sizingModeMainDim == SizingMode.FitContent)) {
            results[index].MeasuredDimensions[(int) FlexAxis.DimensionOf(mainAxis)] =
                BoundAxis(index, mainAxis, direction, maxLineMainDim, mainAxisOwnerSize, ownerWidth);
        } else if (sizingModeMainDim == SizingMode.FitContent && overflow == Overflow.Scroll) {
            results[index].MeasuredDimensions[(int) FlexAxis.DimensionOf(mainAxis)] = MathF.Max(
                MathF.Min(
                    availableInnerMainDim + paddingAndBorderAxisMain,
                    BoundAxisWithinMinAndMax(index, direction, mainAxis, maxLineMainDim, mainAxisOwnerSize, ownerWidth)
                ),
                paddingAndBorderAxisMain
            );
        }

        if (sizingModeCrossDim == SizingMode.MaxContent
            || (overflow != Overflow.Scroll && sizingModeCrossDim == SizingMode.FitContent)) {
            results[index].MeasuredDimensions[(int) FlexAxis.DimensionOf(crossAxis)] = BoundAxis(
                index,
                crossAxis,
                direction,
                totalLineCrossDim + paddingAndBorderAxisCross,
                crossAxisOwnerSize,
                ownerWidth
            );
        } else if (sizingModeCrossDim == SizingMode.FitContent && overflow == Overflow.Scroll) {
            results[index].MeasuredDimensions[(int) FlexAxis.DimensionOf(crossAxis)] = MathF.Max(
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
