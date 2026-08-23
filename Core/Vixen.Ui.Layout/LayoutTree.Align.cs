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
        var leadingContentInsetMain = StyleResolution.FlexStartContentInset(in styles[index], mainAxis, direction, ownerWidth);
        var trailingContentInsetMain = StyleResolution.FlexEndContentInset(in styles[index], mainAxis, direction, ownerWidth);
        var gap = StyleResolution.GapForAxis(in styles[index], mainAxis, availableInnerMainDim);
        var mainDimension = FlexAxis.DimensionOf(mainAxis);

        // Under "at most" rules there is no free space to hand out unless a minimum forces some.
        if (sizingModeMainDim == SizingMode.FitContent && line.RemainingFreeSpace > 0f) {
            var min = StyleResolution.ResolvedMinDimension(in styles[index], mainDimension, mainAxisOwnerSize, ownerWidth, direction);
            if (styles[index].MinDimensions[(int) mainDimension].IsDefined && !float.IsNaN(min)) {
                var minAvailableMainDim = min - leadingContentInsetMain - trailingContentInsetMain;
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

        line.MainDim = leadingContentInsetMain + leadingMainDim;
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

        line.MainDim += trailingContentInsetMain;

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
        float contentInsetAxisCross,
        float leadingContentInsetCross,
        int currentDepth
    ) {
        var crossDimension = FlexAxis.DimensionOf(crossAxis);
        var leadPerLine = 0f;
        var currentLead = leadingContentInsetCross;
        var extraSpacePerLine = 0f;

        var unclampedCrossDim = sizingModeCrossDim == SizingMode.StretchFit
            ? availableInnerCrossDim + contentInsetAxisCross
            : HasDefiniteLength(index, crossDimension, crossAxisOwnerSize)
                ? ResolvedDimension(index, crossDimension, crossAxisOwnerSize, ownerWidth, direction)
                : totalLineCrossDim + contentInsetAxisCross;

        var innerCrossDim =
            BoundAxis(index, crossAxis, direction, unclampedCrossDim, crossAxisOwnerSize, ownerWidth) - contentInsetAxisCross;
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
                        // ⚠ THE ITEM'S LEADING CROSS MARGIN, which this case alone used to drop. The
                        // line's height is measured with the margins in it a few lines up, and
                        // Align.Stretch below adds it, and Align.FlexEnd subtracts the trailing one —
                        // only flex-start placed the item hard against the line's edge. bevy_issue_8082
                        // is four 50px boxes with `margin: 10px` wrapped two-by-two under
                        // `align-items: flex-start`: Chrome puts the first row at y=10 and the second
                        // at y=80, and every one of them came out exactly 10 high.
                        results[child].Position[crossStartEdge] = currentLead
                            + StyleResolution.FlexStartMargin(in styles[child], crossAxis, direction, availableInnerWidth)
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

                    case Align.Baseline: {
                        // ⚠ <b>§8.3 measures the offset from the item's baseline to its CROSS-START
                        // margin edge, and `wrap-reverse` moves that edge to the bottom.</b> The item
                        // with the largest such distance is placed flush against the line's
                        // cross-start; the rest hang off the baseline it establishes. Above the
                        // fold that distance is the ascent, and this store computes it as one.
                        //
                        // ⚠ Under wrap-reverse it is the DESCENT, and no amount of mirroring
                        // afterwards produces that. STEP 9 reflects each child's box — `newTop =
                        // container − oldTop − height` — which turns "tops offset by the ascent
                        // difference" into "bottoms offset by the ascent difference", and a set of
                        // boxes whose bottoms are spread apart is precisely a set whose baselines are
                        // not aligned. `align_baseline_wrap_reverse` is four childless boxes, so
                        // every synthesised baseline is a bottom edge and every descent is zero:
                        // Chrome puts all four bottoms on their line's bottom edge, and the mirror
                        // put the short ones 20 and 20 points above it. The reflection is right for
                        // the tallest item in each line and wrong for every other one, which is what
                        // made this read as an off-by-one in align-content rather than as the axis
                        // question it is.
                        //
                        // Written in the UNFLIPPED frame, because STEP 9 has not run yet: an offset
                        // of `maxDescent - descent` from the line's top reflects into an offset of
                        // `maxDescent - descent` from its bottom, which is the rule.
                        var offset = maxAscent - CalculateBaseline(child);

                        if (styles[index].FlexWrap == Wrap.WrapReverse) {
                            var ascent = CalculateBaseline(child)
                                + StyleResolution.FlexStartMargin(in styles[child], FlexDirection.Column, direction, availableInnerWidth);
                            var descent = results[child].MeasuredDimensions[(int) Dimension.Height]
                                + StyleResolution.MarginForAxis(in styles[child], FlexDirection.Column, availableInnerWidth)
                                - ascent;

                            offset = maxDescent - descent;
                        }

                        results[child].Position[(int) Edge.Top] = currentLead
                            + offset
                            + StyleResolution.FlexStartPosition(in styles[child], FlexDirection.Column, direction, availableInnerCrossDim);
                        break;
                    }

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
        // ⚠ A box that laid its own children out on line boxes answers with the last one's baseline,
        // and it is the only box here that can: §10.8.1 puts a flow container's baseline on its last
        // line box, and a line box is not a node, so there is nothing for the walk below to descend
        // into. `LayoutTree.Inline` records it during the walk for exactly this call.
        if (!float.IsNaN(results[index].InlineBaseline)
            && styles[index].OverflowX == Overflow.Visible
            && styles[index].OverflowY == Overflow.Visible) {
            return results[index].InlineBaseline;
        }

        if (styles[index].Display is Display.Block or Display.InlineBlock or Display.Inline) {
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
