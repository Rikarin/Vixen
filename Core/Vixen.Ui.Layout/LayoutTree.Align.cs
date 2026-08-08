// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Layout;

/// <summary>Placing what the sizing steps produced: along the line, and between the lines.</summary>
public sealed partial class LayoutTree {
    /// <summary>Places a line's items along the main axis and measures what came out.</summary>
    void JustifyMainAxis(
        int index,
        ref FlexLine line,
        FlexDirection mainAxis,
        FlexDirection crossAxis,
        Direction direction,
        SizingMode sizingModeMainDim,
        SizingMode sizingModeCrossDim,
        float mainAxisOwnerSize,
        float ownerWidth,
        float availableInnerMainDim,
        float availableInnerCrossDim,
        float availableInnerWidth,
        bool performLayout
    ) {
        var leadingPaddingAndBorderMain = StyleResolution.FlexStartPaddingAndBorder(in styles[index], mainAxis, direction, ownerWidth);
        var trailingPaddingAndBorderMain = StyleResolution.FlexEndPaddingAndBorder(in styles[index], mainAxis, direction, ownerWidth);
        var gap = StyleResolution.GapForAxis(in styles[index], mainAxis, availableInnerMainDim);
        var mainDimension = FlexAxis.DimensionOf(mainAxis);

        // Under "at most" rules there is no free space to hand out unless a minimum forces some.
        if (sizingModeMainDim == SizingMode.FitContent && line.RemainingFreeSpace > 0f) {
            var min = StyleResolution.ResolvedMinDimension(in styles[index], mainDimension, mainAxisOwnerSize, ownerWidth, direction);
            if (styles[index].MinDimensions[(int) mainDimension].IsDefined && !float.IsNaN(min)) {
                var minAvailableMainDim = min - leadingPaddingAndBorderMain - trailingPaddingAndBorderMain;
                var occupied = availableInnerMainDim - line.RemainingFreeSpace;
                line.RemainingFreeSpace = MathF.Max(0f, minAvailableMainDim - occupied);
            } else {
                line.RemainingFreeSpace = 0f;
            }
        }

        var leadingMainDim = 0f;
        var betweenMainDim = gap;
        var justifyContent = line.RemainingFreeSpace >= 0f
            ? styles[index].JustifyContent
            : FallbackJustify(styles[index].JustifyContent);

        // Auto margins eat the free space before any of the distribution keywords see it.
        if (line.AutoMarginCount == 0) {
            switch (justifyContent) {
                case Justify.Center:
                    leadingMainDim = line.RemainingFreeSpace / 2f;
                    break;
                case Justify.FlexEnd:
                    leadingMainDim = line.RemainingFreeSpace;
                    break;
                case Justify.SpaceBetween:
                    if (line.ItemCount > 1) {
                        betweenMainDim += line.RemainingFreeSpace / (line.ItemCount - 1);
                    }

                    break;
                case Justify.SpaceEvenly:
                    leadingMainDim = line.RemainingFreeSpace / (line.ItemCount + 1);
                    betweenMainDim += leadingMainDim;
                    break;
                case Justify.SpaceAround:
                    leadingMainDim = 0.5f * line.RemainingFreeSpace / line.ItemCount;
                    betweenMainDim += leadingMainDim * 2f;
                    break;
                case Justify.FlexStart:
                default:
                    break;
            }
        }

        line.MainDim = leadingPaddingAndBorderMain + leadingMainDim;
        line.CrossDim = 0f;

        var maxAscent = 0f;
        var maxDescent = 0f;
        var isBaseline = IsBaselineLayout(index);
        var children = ChildIds(index);
        var mainStartEdge = (int) FlexAxis.FlexStartEdge(mainAxis);

        for (var i = line.StartChild; i < line.EndChild; i++) {
            var child = children[i];
            if (!IsInFlow(child)) {
                continue;
            }

            if (line.RemainingFreeSpace > 0f && StyleResolution.FlexStartMarginIsAuto(in styles[child], mainAxis, direction)) {
                line.MainDim += line.RemainingFreeSpace / line.AutoMarginCount;
            }

            if (performLayout) {
                results[child].Position[mainStartEdge] += line.MainDim;
            }

            if (i != line.LastItemChild) {
                line.MainDim += betweenMainDim;
            }

            if (line.RemainingFreeSpace > 0f && StyleResolution.FlexEndMarginIsAuto(in styles[child], mainAxis, direction)) {
                line.MainDim += line.RemainingFreeSpace / line.AutoMarginCount;
            }

            if (!performLayout && sizingModeCrossDim == SizingMode.StretchFit) {
                // The flex step was skipped, so the measured sizes were never computed and the
                // basis is all there is to go on.
                line.MainDim += StyleResolution.MarginForAxis(in styles[child], mainAxis, availableInnerWidth)
                    + BoundAxisWithinMinAndMax(
                        child,
                        direction,
                        mainAxis,
                        results[child].ComputedFlexBasis,
                        mainAxisOwnerSize,
                        ownerWidth
                    );

                line.CrossDim = availableInnerCrossDim;
                continue;
            }

            line.MainDim += DimensionWithMargin(child, mainAxis, availableInnerWidth);

            if (isBaseline) {
                var ascent = CalculateBaseline(child)
                    + StyleResolution.FlexStartMargin(in styles[child], FlexDirection.Column, direction, availableInnerWidth);
                var descent = results[child].MeasuredDimensions[(int) Dimension.Height]
                    + StyleResolution.MarginForAxis(in styles[child], FlexDirection.Column, availableInnerWidth)
                    - ascent;

                maxAscent = MathF.Max(maxAscent, ascent);
                maxDescent = MathF.Max(maxDescent, descent);
            } else {
                line.CrossDim = MathF.Max(line.CrossDim, DimensionWithMargin(child, crossAxis, availableInnerWidth));
            }
        }

        line.MainDim += trailingPaddingAndBorderMain;

        if (isBaseline) {
            line.CrossDim = maxAscent + maxDescent;
        }
    }

    /// <summary>Distributes the lines of a wrapping container across the cross axis.</summary>
    void AlignLines(
        int index,
        Direction direction,
        FlexDirection crossAxis,
        FlexDirection mainAxis,
        bool isMainAxisRow,
        int lineCount,
        float totalLineCrossDim,
        float crossAxisGap,
        SizingMode sizingModeCrossDim,
        float availableInnerCrossDim,
        float availableInnerWidth,
        float availableInnerHeight,
        float crossAxisOwnerSize,
        float ownerWidth,
        float paddingAndBorderAxisCross,
        float leadingPaddingAndBorderCross,
        int currentDepth
    ) {
        var crossDimension = FlexAxis.DimensionOf(crossAxis);
        var leadPerLine = 0f;
        var currentLead = leadingPaddingAndBorderCross;
        var extraSpacePerLine = 0f;

        var unclampedCrossDim = sizingModeCrossDim == SizingMode.StretchFit
            ? availableInnerCrossDim + paddingAndBorderAxisCross
            : HasDefiniteLength(index, crossDimension, crossAxisOwnerSize)
                ? ResolvedDimension(index, crossDimension, crossAxisOwnerSize, ownerWidth, direction)
                : totalLineCrossDim + paddingAndBorderAxisCross;

        var innerCrossDim =
            BoundAxis(index, crossAxis, direction, unclampedCrossDim, crossAxisOwnerSize, ownerWidth) - paddingAndBorderAxisCross;
        var remaining = innerCrossDim - totalLineCrossDim;
        var alignContent = remaining >= 0f ? styles[index].AlignContent : FallbackAlign(styles[index].AlignContent);

        switch (alignContent) {
            case Align.FlexEnd:
                currentLead += remaining;
                break;
            case Align.Center:
                currentLead += remaining / 2f;
                break;
            case Align.Stretch:
                extraSpacePerLine = remaining / lineCount;
                break;
            case Align.SpaceAround:
                currentLead += remaining / (2 * lineCount);
                leadPerLine = remaining / lineCount;
                break;
            case Align.SpaceEvenly:
                currentLead += remaining / (lineCount + 1);
                leadPerLine = remaining / (lineCount + 1);
                break;
            case Align.SpaceBetween:
                if (lineCount > 1) {
                    leadPerLine = remaining / (lineCount - 1);
                }

                break;
            default:
                break;
        }

        var children = ChildIds(index);
        var crossStartEdge = (int) FlexAxis.FlexStartEdge(crossAxis);
        var start = 0;

        for (var lineIndex = 0; lineIndex < lineCount; lineIndex++) {
            var lineHeight = 0f;
            var maxAscent = 0f;
            var maxDescent = 0f;
            var end = start;

            for (; end < children.Length; end++) {
                var child = children[end];
                if (styles[child].Display == Display.None) {
                    continue;
                }

                if (styles[child].PositionType == PositionType.Absolute) {
                    continue;
                }

                if (results[child].LineIndex != lineIndex) {
                    break;
                }

                if (IsLayoutDimensionDefined(child, crossAxis)) {
                    lineHeight = MathF.Max(
                        lineHeight,
                        results[child].MeasuredDimensions[(int) crossDimension]
                        + StyleResolution.MarginForAxis(in styles[child], crossAxis, availableInnerWidth)
                    );
                }

                if (ResolveChildAlignment(index, child) == Align.Baseline) {
                    var ascent = CalculateBaseline(child)
                        + StyleResolution.FlexStartMargin(in styles[child], FlexDirection.Column, direction, availableInnerWidth);
                    var descent = results[child].MeasuredDimensions[(int) Dimension.Height]
                        + StyleResolution.MarginForAxis(in styles[child], FlexDirection.Column, availableInnerWidth)
                        - ascent;

                    maxAscent = MathF.Max(maxAscent, ascent);
                    maxDescent = MathF.Max(maxDescent, descent);
                    lineHeight = MathF.Max(lineHeight, maxAscent + maxDescent);
                }
            }

            currentLead += lineIndex != 0 ? crossAxisGap : 0f;
            lineHeight += extraSpacePerLine;

            for (var i = start; i < end; i++) {
                var child = children[i];
                if (styles[child].Display == Display.None || styles[child].PositionType == PositionType.Absolute) {
                    continue;
                }

                switch (ResolveChildAlignment(index, child)) {
                    case Align.FlexStart:
                        results[child].Position[crossStartEdge] = currentLead
                            + StyleResolution.FlexStartPosition(in styles[child], crossAxis, direction, availableInnerWidth);
                        break;

                    case Align.FlexEnd:
                        results[child].Position[crossStartEdge] = currentLead
                            + lineHeight
                            - StyleResolution.FlexEndMargin(in styles[child], crossAxis, direction, availableInnerWidth)
                            - results[child].MeasuredDimensions[(int) crossDimension];
                        break;

                    case Align.Center:
                        results[child].Position[crossStartEdge] =
                            currentLead + ((lineHeight - results[child].MeasuredDimensions[(int) crossDimension]) / 2f);
                        break;

                    case Align.Stretch: {
                        results[child].Position[crossStartEdge] = currentLead
                            + StyleResolution.FlexStartMargin(in styles[child], crossAxis, direction, availableInnerWidth);

                        // It was measured against the container's cross size, not the line's.
                        if (HasDefiniteLength(child, crossDimension, availableInnerCrossDim)) {
                            break;
                        }

                        var childWidth = isMainAxisRow
                            ? results[child].MeasuredDimensions[(int) Dimension.Width]
                            + StyleResolution.MarginForAxis(in styles[child], mainAxis, availableInnerWidth)
                            : leadPerLine + lineHeight;

                        var childHeight = !isMainAxisRow
                            ? results[child].MeasuredDimensions[(int) Dimension.Height]
                            + StyleResolution.MarginForAxis(in styles[child], crossAxis, availableInnerWidth)
                            : leadPerLine + lineHeight;

                        if (Inexact(childWidth, results[child].MeasuredDimensions[(int) Dimension.Width])
                            && Inexact(childHeight, results[child].MeasuredDimensions[(int) Dimension.Height])) {
                            break;
                        }

                        CalculateLayoutInternal(
                            child,
                            childWidth,
                            childHeight,
                            direction,
                            SizingMode.StretchFit,
                            SizingMode.StretchFit,
                            availableInnerWidth,
                            availableInnerHeight,
                            performLayout: true,
                            currentDepth
                        );

                        break;
                    }

                    case Align.Baseline:
                        results[child].Position[(int) Edge.Top] = currentLead
                            + maxAscent
                            - CalculateBaseline(child)
                            + StyleResolution.FlexStartPosition(in styles[child], FlexDirection.Column, direction, availableInnerCrossDim);
                        break;

                    default:
                        break;
                }
            }

            currentLead += leadPerLine + lineHeight;
            start = end;
        }
    }

    /// <summary>How far below a node's top edge its first line of text sits.</summary>
    /// <remarks>
    ///     A node with no baseline function of its own borrows one: the first in-flow child on the
    ///     first line that asks to be baseline-aligned, or failing that the first in-flow child at
    ///     all. A node with no children at all is its own baseline, at its bottom edge.
    /// </remarks>
    float CalculateBaseline(int index) {
        var baselineFunction = BaselineFunctionOf(index);
        if (baselineFunction is not null) {
            var baseline = baselineFunction(
                new LayoutNodeId(index),
                results[index].MeasuredDimensions[(int) Dimension.Width],
                results[index].MeasuredDimensions[(int) Dimension.Height],
                ContextOf(index)
            );

            if (float.IsNaN(baseline)) {
                throw new InvalidOperationException(
                    $"The baseline function for node {index} returned NaN. A baseline is a distance from the top "
                    + "edge, and there is no sensible thing to do with one that is not a number."
                );
            }

            return baseline;
        }

        // ⚠ A block container's baseline is its own bottom margin edge, not its first child's.
        // CSS 2.1 §10.8.1 puts a block-level box's baseline on the baseline of its last *line box*,
        // and a block container with no inline content has none — so CSS Align §9.3's synthesis rule
        // applies and the box's own bottom edge is used. The flex rule one line down, "borrow the
        // first child's", is a flex-item rule; applying it to a block box puts a 20-point card with
        // a 10-point child ten points too low, which is `block_align_baseline_child` exactly.
        if (styles[index].Display == Display.Block) {
            return results[index].MeasuredDimensions[(int) Dimension.Height];
        }

        var baselineChild = -1;
        foreach (var child in ChildIds(index)) {
            if (results[child].LineIndex > 0) {
                break;
            }

            if (styles[child].PositionType == PositionType.Absolute) {
                continue;
            }

            if (ResolveChildAlignment(index, child) == Align.Baseline
                || (flags[child] & LayoutNodeState.IsReferenceBaseline) != 0) {
                baselineChild = child;
                break;
            }

            if (baselineChild < 0) {
                baselineChild = child;
            }
        }

        if (baselineChild < 0) {
            return results[index].MeasuredDimensions[(int) Dimension.Height];
        }

        return CalculateBaseline(baselineChild) + results[baselineChild].Position[(int) Edge.Top];
    }

    /// <summary>Whether anything in this container asks to be aligned on a baseline.</summary>
    bool IsBaselineLayout(int index) {
        if (FlexAxis.IsColumn(styles[index].FlexDirection)) {
            return false;
        }

        if (styles[index].AlignItems == Align.Baseline) {
            return true;
        }

        foreach (var child in ChildIds(index)) {
            if (styles[child].PositionType != PositionType.Absolute && styles[child].AlignSelf == Align.Baseline) {
                return true;
            }
        }

        return false;
    }

    static Align FallbackAlign(Align align) => align switch {
        // Nothing to distribute means nothing to distribute, so these collapse to packing at the
        // start rather than producing negative gaps.
        Align.SpaceBetween or Align.Stretch or Align.SpaceAround or Align.SpaceEvenly => Align.FlexStart,
        _ => align
    };

    static Justify FallbackJustify(Justify justify) => justify switch {
        Justify.SpaceBetween or Justify.SpaceAround or Justify.SpaceEvenly => Justify.FlexStart,
        _ => justify
    };
}
