// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Layout;

/// <summary>
///     The steps <see cref="CalculateLayout" /> is made of: measuring a leaf, finding each item's
///     flex basis, breaking items into lines, handing out the space that is left, and placing what
///     comes out on both axes.
/// </summary>
public sealed partial class LayoutTree {
    /// <summary>Measures a leaf that sizes itself — text, an image, anything with content.</summary>
    void MeasureNodeWithMeasureFunction(
        int index,
        Direction direction,
        float availableWidth,
        float availableHeight,
        SizingMode widthSizingMode,
        SizingMode heightSizingMode,
        float ownerWidth,
        float ownerHeight
    ) {
        if (widthSizingMode == SizingMode.MaxContent) {
            availableWidth = float.NaN;
        }

        if (heightSizingMode == SizingMode.MaxContent) {
            availableHeight = float.NaN;
        }

        ref var layout = ref results[index];
        var paddingAndBorderRow = layout.Padding[(int) Edge.Left] + layout.Padding[(int) Edge.Right]
            + layout.Border[(int) Edge.Left] + layout.Border[(int) Edge.Right];
        var paddingAndBorderColumn = layout.Padding[(int) Edge.Top] + layout.Padding[(int) Edge.Bottom]
            + layout.Border[(int) Edge.Top] + layout.Border[(int) Edge.Bottom];

        var innerWidth = float.IsNaN(availableWidth) ? availableWidth : MathF.Max(0f, availableWidth - paddingAndBorderRow);
        var innerHeight = float.IsNaN(availableHeight) ? availableHeight : MathF.Max(0f, availableHeight - paddingAndBorderColumn);

        if (widthSizingMode == SizingMode.StretchFit && heightSizingMode == SizingMode.StretchFit) {
            // Both sizes are already decided, so there is nothing the content could tell us.
            results[index].MeasuredDimensions[(int) Dimension.Width] =
                BoundAxis(index, FlexDirection.Row, direction, availableWidth, ownerWidth, ownerWidth);
            results[index].MeasuredDimensions[(int) Dimension.Height] =
                BoundAxis(index, FlexDirection.Column, direction, availableHeight, ownerHeight, ownerWidth);
            return;
        }

        var measured = Measure(index, innerWidth, MeasureModeOf(widthSizingMode), innerHeight, MeasureModeOf(heightSizingMode));

        results[index].MeasuredDimensions[(int) Dimension.Width] = BoundAxis(
            index,
            FlexDirection.Row,
            direction,
            widthSizingMode is SizingMode.MaxContent or SizingMode.FitContent
                ? measured.Width + paddingAndBorderRow
                : availableWidth,
            ownerWidth,
            ownerWidth
        );

        results[index].MeasuredDimensions[(int) Dimension.Height] = BoundAxis(
            index,
            FlexDirection.Column,
            direction,
            heightSizingMode is SizingMode.MaxContent or SizingMode.FitContent
                ? measured.Height + paddingAndBorderColumn
                : availableHeight,
            ownerHeight,
            ownerWidth
        );
    }

    /// <summary>Measures a node with nothing in it: the available size, or its own padding.</summary>
    void MeasureNodeWithoutChildren(
        int index,
        Direction direction,
        float availableWidth,
        float availableHeight,
        SizingMode widthSizingMode,
        SizingMode heightSizingMode,
        float ownerWidth,
        float ownerHeight
    ) {
        ref var layout = ref results[index];

        var width = availableWidth;
        if (widthSizingMode is SizingMode.MaxContent or SizingMode.FitContent) {
            width = layout.Padding[(int) Edge.Left] + layout.Padding[(int) Edge.Right]
                + layout.Border[(int) Edge.Left] + layout.Border[(int) Edge.Right];
        }

        results[index].MeasuredDimensions[(int) Dimension.Width] =
            BoundAxis(index, FlexDirection.Row, direction, width, ownerWidth, ownerWidth);

        var height = availableHeight;
        if (heightSizingMode is SizingMode.MaxContent or SizingMode.FitContent) {
            height = layout.Padding[(int) Edge.Top] + layout.Padding[(int) Edge.Bottom]
                + layout.Border[(int) Edge.Top] + layout.Border[(int) Edge.Bottom];
        }

        results[index].MeasuredDimensions[(int) Dimension.Height] =
            BoundAxis(index, FlexDirection.Column, direction, height, ownerHeight, ownerWidth);
    }

    /// <summary>Answers a measure-only request whose answer the styles already fix.</summary>
    /// <returns>Whether it could.</returns>
    bool MeasureNodeWithFixedSize(
        int index,
        Direction direction,
        float availableWidth,
        float availableHeight,
        SizingMode widthSizingMode,
        SizingMode heightSizingMode,
        float ownerWidth,
        float ownerHeight
    ) {
        if (!IsFixedSize(availableWidth, widthSizingMode) || !IsFixedSize(availableHeight, heightSizingMode)) {
            return false;
        }

        results[index].MeasuredDimensions[(int) Dimension.Width] = BoundAxis(
            index,
            FlexDirection.Row,
            direction,
            float.IsNaN(availableWidth) || (widthSizingMode == SizingMode.FitContent && availableWidth < 0f)
                ? 0f
                : availableWidth,
            ownerWidth,
            ownerWidth
        );

        results[index].MeasuredDimensions[(int) Dimension.Height] = BoundAxis(
            index,
            FlexDirection.Column,
            direction,
            float.IsNaN(availableHeight) || (heightSizingMode == SizingMode.FitContent && availableHeight < 0f)
                ? 0f
                : availableHeight,
            ownerHeight,
            ownerWidth
        );

        return true;

        static bool IsFixedSize(float size, SizingMode mode) =>
            mode == SizingMode.StretchFit || (!float.IsNaN(size) && mode == SizingMode.FitContent && size <= 0f);
    }

    /// <summary>The space inside a node's padding and border, clamped by its own min and max.</summary>
    float AvailableInnerDimension(
        int index,
        Direction direction,
        Dimension dimension,
        float availableDim,
        float paddingAndBorder,
        float ownerDim,
        float ownerWidth
    ) {
        var available = availableDim - paddingAndBorder;
        if (float.IsNaN(available)) {
            return available;
        }

        var min = StyleResolution.ResolvedMinDimension(in styles[index], dimension, ownerDim, ownerWidth, direction);
        var minInner = float.IsNaN(min) ? 0f : min - paddingAndBorder;

        var max = StyleResolution.ResolvedMaxDimension(in styles[index], dimension, ownerDim, ownerWidth, direction);
        var maxInner = float.IsNaN(max) ? float.MaxValue : max - paddingAndBorder;

        return MathF.Max(MathF.Min(available, maxInner), minInner);
    }

    /// <summary>Works out every in-flow child's flex basis, and returns their total outer size.</summary>
    float ComputeFlexBasisForChildren(
        int index,
        float availableInnerWidth,
        float availableInnerHeight,
        SizingMode widthSizingMode,
        SizingMode heightSizingMode,
        Direction direction,
        FlexDirection mainAxis,
        bool performLayout,
        int currentDepth
    ) {
        var totalOuterFlexBasis = 0f;
        var singleFlexChild = -1;
        var sizingModeMainDim = FlexAxis.IsRow(mainAxis) ? widthSizingMode : heightSizingMode;
        var children = ChildIds(index);

        // One child that can both grow and shrink absorbs the whole line, so its basis need not be
        // measured at all — it is going to be told what size to be.
        if (sizingModeMainDim == SizingMode.StretchFit) {
            foreach (var child in children) {
                if (!IsNodeFlexible(child)) {
                    continue;
                }

                var isRoot = links[child].Parent < 0;
                if (singleFlexChild >= 0
                    || StyleResolution.ResolveFlexGrow(in styles[child], isRoot) == 0f
                    || StyleResolution.ResolveFlexShrink(in styles[child], isRoot) == 0f) {
                    singleFlexChild = -1;
                    break;
                }

                singleFlexChild = child;
            }
        }

        foreach (var child in children) {
            if (styles[child].Display == Display.None) {
                if (performLayout) {
                    ZeroOutLayoutRecursively(child);
                }

                continue;
            }

            if (performLayout) {
                var childDirection = StyleResolution.ResolveDirection(in styles[child], direction);
                SetPosition(child, childDirection, availableInnerWidth, availableInnerHeight);
            }

            if (styles[child].PositionType == PositionType.Absolute) {
                continue;
            }

            if (child == singleFlexChild) {
                results[child].ComputedFlexBasisGeneration = generation;
                results[child].ComputedFlexBasis = 0f;
            } else {
                ComputeFlexBasisForChild(
                    index,
                    child,
                    availableInnerWidth,
                    widthSizingMode,
                    availableInnerHeight,
                    availableInnerWidth,
                    availableInnerHeight,
                    heightSizingMode,
                    direction,
                    currentDepth
                );
            }

            totalOuterFlexBasis += results[child].ComputedFlexBasis
                + StyleResolution.MarginForAxis(in styles[child], mainAxis, availableInnerWidth);
        }

        return totalOuterFlexBasis;
    }

    /// <summary>Works out one child's flex basis, measuring it if nothing else settles it.</summary>
    void ComputeFlexBasisForChild(
        int index,
        int child,
        float width,
        SizingMode widthMode,
        float height,
        float ownerWidth,
        float ownerHeight,
        SizingMode heightMode,
        Direction direction,
        int currentDepth
    ) {
        var mainAxis = FlexAxis.Resolve(styles[index].FlexDirection, direction);
        var isMainAxisRow = FlexAxis.IsRow(mainAxis);
        var mainAxisSize = isMainAxisRow ? width : height;
        var mainAxisOwnerSize = isMainAxisRow ? ownerWidth : ownerHeight;

        var childWidth = float.NaN;
        var childHeight = float.NaN;
        var childWidthSizingMode = SizingMode.MaxContent;
        var childHeightSizingMode = SizingMode.MaxContent;

        var resolvedFlexBasis = StyleResolution.WithBoxSizing(
            in styles[child],
            StyleResolution.ProcessedFlexBasis(in styles[child]).Resolve(mainAxisOwnerSize),
            FlexAxis.DimensionOf(mainAxis),
            ownerWidth,
            direction
        );

        var isRowStyleDimDefined = HasDefiniteLength(child, Dimension.Width, ownerWidth);
        var isColumnStyleDimDefined = HasDefiniteLength(child, Dimension.Height, ownerHeight);

        if (!float.IsNaN(resolvedFlexBasis) && !float.IsNaN(mainAxisSize)) {
            if (float.IsNaN(results[child].ComputedFlexBasis)) {
                var paddingAndBorder = StyleResolution.PaddingAndBorderForAxis(in styles[child], mainAxis, direction, ownerWidth);
                results[child].ComputedFlexBasis = MathF.Max(resolvedFlexBasis, paddingAndBorder);
            }
        } else if (isMainAxisRow && isRowStyleDimDefined) {
            var paddingAndBorder = StyleResolution.PaddingAndBorderForAxis(in styles[child], FlexDirection.Row, direction, ownerWidth);
            results[child].ComputedFlexBasis =
                MathF.Max(ResolvedDimension(child, Dimension.Width, ownerWidth, ownerWidth, direction), paddingAndBorder);
        } else if (!isMainAxisRow && isColumnStyleDimDefined) {
            var paddingAndBorder = StyleResolution.PaddingAndBorderForAxis(in styles[child], FlexDirection.Column, direction, ownerWidth);
            results[child].ComputedFlexBasis =
                MathF.Max(ResolvedDimension(child, Dimension.Height, ownerHeight, ownerWidth, direction), paddingAndBorder);
        } else {
            var marginRow = StyleResolution.MarginForAxis(in styles[child], FlexDirection.Row, ownerWidth);
            var marginColumn = StyleResolution.MarginForAxis(in styles[child], FlexDirection.Column, ownerWidth);

            if (isRowStyleDimDefined) {
                childWidth = ResolvedDimension(child, Dimension.Width, ownerWidth, ownerWidth, direction) + marginRow;
                childWidthSizingMode = SizingMode.StretchFit;
            }

            if (isColumnStyleDimDefined) {
                childHeight = ResolvedDimension(child, Dimension.Height, ownerHeight, ownerWidth, direction) + marginColumn;
                childHeightSizingMode = SizingMode.StretchFit;
            }

            // The specification says nothing about `overflow` here; every major browser does this.
            if ((!isMainAxisRow && styles[index].Overflow == Overflow.Scroll) || styles[index].Overflow != Overflow.Scroll) {
                if (float.IsNaN(childWidth) && !float.IsNaN(width)) {
                    childWidth = width;
                    childWidthSizingMode = SizingMode.FitContent;
                }
            }

            if (float.IsNaN(childHeight) && !float.IsNaN(height)) {
                childHeight = height;
                childHeightSizingMode = SizingMode.FitContent;
            }

            var aspectRatio = styles[child].AspectRatio;
            if (!float.IsNaN(aspectRatio)) {
                if (!isMainAxisRow && childWidthSizingMode == SizingMode.StretchFit) {
                    childHeight = marginColumn + ((childWidth - marginRow) / aspectRatio);
                    childHeightSizingMode = SizingMode.StretchFit;
                } else if (isMainAxisRow && childHeightSizingMode == SizingMode.StretchFit) {
                    childWidth = marginRow + ((childHeight - marginColumn) * aspectRatio);
                    childWidthSizingMode = SizingMode.StretchFit;
                }
            }

            // A child with no cross size of its own that is set to stretch is measured against the
            // width it is about to be given, so that text inside it wraps to the right width.
            var hasExactWidth = !float.IsNaN(width) && widthMode == SizingMode.StretchFit;
            var childWidthStretch = ResolveChildAlignment(index, child) == Align.Stretch
                && childWidthSizingMode != SizingMode.StretchFit;
            if (!isMainAxisRow && !isRowStyleDimDefined && hasExactWidth && childWidthStretch) {
                childWidth = width;
                childWidthSizingMode = SizingMode.StretchFit;
                if (!float.IsNaN(aspectRatio)) {
                    childHeight = (childWidth - marginRow) / aspectRatio;
                    childHeightSizingMode = SizingMode.StretchFit;
                }
            }

            var hasExactHeight = !float.IsNaN(height) && heightMode == SizingMode.StretchFit;
            var childHeightStretch = ResolveChildAlignment(index, child) == Align.Stretch
                && childHeightSizingMode != SizingMode.StretchFit;
            if (isMainAxisRow && !isColumnStyleDimDefined && hasExactHeight && childHeightStretch) {
                childHeight = height;
                childHeightSizingMode = SizingMode.StretchFit;
                if (!float.IsNaN(aspectRatio)) {
                    childWidth = (childHeight - marginColumn) * aspectRatio;
                    childWidthSizingMode = SizingMode.StretchFit;
                }
            }

            ConstrainMaxSizeForMode(child, direction, FlexDirection.Row, ownerWidth, ownerWidth, ref childWidthSizingMode, ref childWidth);
            ConstrainMaxSizeForMode(child, direction, FlexDirection.Column, ownerHeight, ownerWidth, ref childHeightSizingMode, ref childHeight);

            CalculateLayoutInternal(
                child,
                childWidth,
                childHeight,
                direction,
                childWidthSizingMode,
                childHeightSizingMode,
                ownerWidth,
                ownerHeight,
                performLayout: false,
                currentDepth
            );

            results[child].ComputedFlexBasis = MathF.Max(
                results[child].MeasuredDimensions[(int) FlexAxis.DimensionOf(mainAxis)],
                StyleResolution.PaddingAndBorderForAxis(in styles[child], mainAxis, direction, ownerWidth)
            );
        }

        results[child].ComputedFlexBasisGeneration = generation;
    }

    /// <summary>Collects children into one line, stopping where the line is full.</summary>
    FlexLine CalculateFlexLine(
        int index,
        Direction ownerDirection,
        float ownerWidth,
        float mainAxisOwnerSize,
        float availableInnerWidth,
        float availableInnerMainDim,
        int startChild,
        int lineIndex
    ) {
        var line = new FlexLine { StartChild = startChild, EndChild = startChild, LastItemChild = -1 };
        var direction = StyleResolution.ResolveDirection(in styles[index], ownerDirection);
        var mainAxis = FlexAxis.Resolve(styles[index].FlexDirection, direction);
        var isNodeFlexWrap = styles[index].FlexWrap != Wrap.NoWrap;
        var gap = StyleResolution.GapForAxis(in styles[index], mainAxis, availableInnerMainDim);

        var children = ChildIds(index);
        var firstElementSeen = false;
        var sizeConsumedIncludingMinConstraint = 0f;

        for (var i = startChild; i < children.Length; i++) {
            var child = children[i];
            line.EndChild = i + 1;

            if (!IsInFlow(child)) {
                continue;
            }

            if (StyleResolution.FlexStartMarginIsAuto(in styles[child], mainAxis, ownerDirection)) {
                line.AutoMarginCount++;
            }

            if (StyleResolution.FlexEndMarginIsAuto(in styles[child], mainAxis, ownerDirection)) {
                line.AutoMarginCount++;
            }

            results[child].LineIndex = lineIndex;

            var childMarginMainAxis = StyleResolution.MarginForAxis(in styles[child], mainAxis, availableInnerWidth);
            var childLeadingGap = firstElementSeen ? gap : 0f;
            var flexBasisWithConstraints = BoundAxisWithinMinAndMax(
                child,
                direction,
                mainAxis,
                results[child].ComputedFlexBasis,
                mainAxisOwnerSize,
                ownerWidth
            );

            if (sizeConsumedIncludingMinConstraint + flexBasisWithConstraints + childMarginMainAxis + childLeadingGap
                > availableInnerMainDim
                && isNodeFlexWrap
                && line.ItemCount > 0) {
                // This item does not fit, so the line ends before it.
                line.EndChild = i;
                break;
            }

            firstElementSeen = true;
            sizeConsumedIncludingMinConstraint += flexBasisWithConstraints + childMarginMainAxis + childLeadingGap;
            line.SizeConsumed += flexBasisWithConstraints + childMarginMainAxis + childLeadingGap;

            if (IsNodeFlexible(child)) {
                var isRoot = links[child].Parent < 0;
                line.TotalFlexGrowFactors += StyleResolution.ResolveFlexGrow(in styles[child], isRoot);

                // The shrink factor is scaled by the item's own size, unlike the grow factor.
                line.TotalFlexShrinkScaledFactors +=
                    -StyleResolution.ResolveFlexShrink(in styles[child], isRoot) * results[child].ComputedFlexBasis;
            }

            line.ItemCount++;
            line.LastItemChild = i;
        }

        if (line.TotalFlexGrowFactors is > 0f and < 1f) {
            line.TotalFlexGrowFactors = 1f;
        }

        if (line.TotalFlexShrinkScaledFactors is > 0f and < 1f) {
            line.TotalFlexShrinkScaledFactors = 1f;
        }

        return line;
    }

    /// <summary>Hands out the free space along a line's main axis, in two passes.</summary>
    /// <remarks>
    ///     The specification describes a loop that repeats an unbounded number of times; two passes
    ///     is Yoga's deviation and it is deliberate — the first freezes the items whose min or max
    ///     triggers and takes them out of the pool, the second divides what is left among the rest.
    ///     It does not cover every case the specification does; it covers the ones that occur, in a
    ///     known number of passes, which is what a frame budget needs.
    /// </remarks>
    void ResolveFlexibleLength(
        int index,
        ref FlexLine line,
        FlexDirection mainAxis,
        FlexDirection crossAxis,
        Direction direction,
        float ownerWidth,
        float mainAxisOwnerSize,
        float availableInnerMainDim,
        float availableInnerCrossDim,
        float availableInnerWidth,
        float availableInnerHeight,
        bool mainAxisOverflows,
        SizingMode sizingModeCrossDim,
        bool performLayout,
        int currentDepth
    ) {
        var originalFreeSpace = line.RemainingFreeSpace;
        var children = ChildIds(index);

        // CSS Flexbox §4.5: an item with no explicit minimum still has an automatic one, so that
        // shrinking a row of text cannot squeeze a word to nothing.
        for (var i = line.StartChild; i < line.EndChild; i++) {
            var child = children[i];
            if (!IsInFlow(child)) {
                continue;
            }

            results[child].ComputedAutoMinMainSize =
                ComputeAutoMinMainSize(child, mainAxis, direction, mainAxisOwnerSize, availableInnerWidth, availableInnerHeight);
        }

        DistributeFreeSpaceFirstPass(index, ref line, direction, mainAxis, ownerWidth, mainAxisOwnerSize, availableInnerMainDim, availableInnerWidth);

        var distributed = DistributeFreeSpaceSecondPass(
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

        line.RemainingFreeSpace = originalFreeSpace - distributed;
    }

    /// <summary>Freezes the items whose min or max triggers, and takes them out of the pool.</summary>
    void DistributeFreeSpaceFirstPass(
        int index,
        ref FlexLine line,
        Direction direction,
        FlexDirection mainAxis,
        float ownerWidth,
        float mainAxisOwnerSize,
        float availableInnerMainDim,
        float availableInnerWidth
    ) {
        var deltaFreeSpace = 0f;
        var children = ChildIds(index);

        for (var i = line.StartChild; i < line.EndChild; i++) {
            var child = children[i];
            if (!IsInFlow(child)) {
                continue;
            }

            var isRoot = links[child].Parent < 0;
            var childFlexBasis = BoundAxisWithinMinAndMax(
                child,
                direction,
                mainAxis,
                results[child].ComputedFlexBasis,
                mainAxisOwnerSize,
                ownerWidth
            );

            if (line.RemainingFreeSpace < 0f) {
                var shrinkScaled = -StyleResolution.ResolveFlexShrink(in styles[child], isRoot) * childFlexBasis;
                if (float.IsNaN(shrinkScaled) || shrinkScaled == 0f) {
                    continue;
                }

                var baseMainSize = childFlexBasis
                    + (line.RemainingFreeSpace / line.TotalFlexShrinkScaledFactors * shrinkScaled);
                var boundMainSize = BoundAxisWithAutoMin(child, mainAxis, direction, baseMainSize, availableInnerMainDim, availableInnerWidth);

                if (!float.IsNaN(baseMainSize) && !float.IsNaN(boundMainSize) && baseMainSize != boundMainSize) {
                    // Excluding this item from the pool makes its constraint trigger again in the
                    // second pass, so the two passes agree on its size.
                    deltaFreeSpace += boundMainSize - childFlexBasis;
                    line.TotalFlexShrinkScaledFactors -=
                        -StyleResolution.ResolveFlexShrink(in styles[child], isRoot) * results[child].ComputedFlexBasis;
                }
            } else if (line.RemainingFreeSpace > 0f) {
                var growFactor = StyleResolution.ResolveFlexGrow(in styles[child], isRoot);
                if (float.IsNaN(growFactor) || growFactor == 0f) {
                    continue;
                }

                var baseMainSize = childFlexBasis + (line.RemainingFreeSpace / line.TotalFlexGrowFactors * growFactor);
                var boundMainSize = BoundAxis(child, mainAxis, direction, baseMainSize, availableInnerMainDim, availableInnerWidth);

                if (!float.IsNaN(baseMainSize) && !float.IsNaN(boundMainSize) && baseMainSize != boundMainSize) {
                    deltaFreeSpace += boundMainSize - childFlexBasis;
                    line.TotalFlexGrowFactors -= growFactor;
                }
            }
        }

        line.RemainingFreeSpace -= deltaFreeSpace;
    }

    /// <summary>Sizes every flexible item, and lays it out at that size.</summary>
    /// <returns>How much free space was actually consumed.</returns>
    float DistributeFreeSpaceSecondPass(
        int index,
        ref FlexLine line,
        FlexDirection mainAxis,
        FlexDirection crossAxis,
        Direction direction,
        float ownerWidth,
        float mainAxisOwnerSize,
        float availableInnerMainDim,
        float availableInnerCrossDim,
        float availableInnerWidth,
        float availableInnerHeight,
        bool mainAxisOverflows,
        SizingMode sizingModeCrossDim,
        bool performLayout,
        int currentDepth
    ) {
        var deltaFreeSpace = 0f;
        var isMainAxisRow = FlexAxis.IsRow(mainAxis);
        var isNodeFlexWrap = styles[index].FlexWrap != Wrap.NoWrap;
        var children = ChildIds(index);

        for (var i = line.StartChild; i < line.EndChild; i++) {
            var child = children[i];
            if (!IsInFlow(child)) {
                continue;
            }

            var isRoot = links[child].Parent < 0;
            var childFlexBasis = BoundAxisWithinMinAndMax(
                child,
                direction,
                mainAxis,
                results[child].ComputedFlexBasis,
                mainAxisOwnerSize,
                ownerWidth
            );

            var updatedMainSize = childFlexBasis;

            if (!float.IsNaN(line.RemainingFreeSpace) && line.RemainingFreeSpace < 0f) {
                var shrinkScaled = -StyleResolution.ResolveFlexShrink(in styles[child], isRoot) * childFlexBasis;
                if (shrinkScaled != 0f) {
                    var childSize = line.TotalFlexShrinkScaledFactors == 0f
                        ? childFlexBasis + shrinkScaled
                        : childFlexBasis + (line.RemainingFreeSpace / line.TotalFlexShrinkScaledFactors * shrinkScaled);

                    updatedMainSize = BoundAxisWithAutoMin(child, mainAxis, direction, childSize, availableInnerMainDim, availableInnerWidth);
                }
            } else if (!float.IsNaN(line.RemainingFreeSpace) && line.RemainingFreeSpace > 0f) {
                var growFactor = StyleResolution.ResolveFlexGrow(in styles[child], isRoot);
                if (!float.IsNaN(growFactor) && growFactor != 0f) {
                    updatedMainSize = BoundAxisWithAutoMin(
                        child,
                        mainAxis,
                        direction,
                        childFlexBasis + (line.RemainingFreeSpace / line.TotalFlexGrowFactors * growFactor),
                        availableInnerMainDim,
                        availableInnerWidth
                    );
                }
            }

            deltaFreeSpace += updatedMainSize - childFlexBasis;

            var marginMain = StyleResolution.MarginForAxis(in styles[child], mainAxis, availableInnerWidth);
            var marginCross = StyleResolution.MarginForAxis(in styles[child], crossAxis, availableInnerWidth);

            var childCrossSize = float.NaN;
            var childMainSize = updatedMainSize + marginMain;
            SizingMode childCrossSizingMode;
            var childMainSizingMode = SizingMode.StretchFit;

            var aspectRatio = styles[child].AspectRatio;
            var crossDimension = FlexAxis.DimensionOf(crossAxis);

            if (!float.IsNaN(aspectRatio)) {
                childCrossSize = isMainAxisRow
                    ? (childMainSize - marginMain) / aspectRatio
                    : (childMainSize - marginMain) * aspectRatio;
                childCrossSizingMode = SizingMode.StretchFit;
                childCrossSize += marginCross;
            } else if (!float.IsNaN(availableInnerCrossDim)
                && !HasDefiniteLength(child, crossDimension, availableInnerCrossDim)
                && sizingModeCrossDim == SizingMode.StretchFit
                && !(isNodeFlexWrap && mainAxisOverflows)
                && ResolveChildAlignment(index, child) == Align.Stretch
                && !StyleResolution.FlexStartMarginIsAuto(in styles[child], crossAxis, direction)
                && !StyleResolution.FlexEndMarginIsAuto(in styles[child], crossAxis, direction)) {
                childCrossSize = availableInnerCrossDim;
                childCrossSizingMode = SizingMode.StretchFit;
            } else if (!HasDefiniteLength(child, crossDimension, availableInnerCrossDim)) {
                childCrossSize = availableInnerCrossDim;
                childCrossSizingMode = float.IsNaN(childCrossSize) ? SizingMode.MaxContent : SizingMode.FitContent;
            } else {
                childCrossSize = ResolvedDimension(child, crossDimension, availableInnerCrossDim, availableInnerWidth, direction)
                    + marginCross;
                var isLoosePercentageMeasurement =
                    StyleResolution.ProcessedDimension(in styles[child], crossDimension).Unit == LayoutUnit.Percent
                    && sizingModeCrossDim != SizingMode.StretchFit;
                childCrossSizingMode = float.IsNaN(childCrossSize) || isLoosePercentageMeasurement
                    ? SizingMode.MaxContent
                    : SizingMode.StretchFit;
            }

            ConstrainMaxSizeForMode(child, direction, mainAxis, availableInnerMainDim, availableInnerWidth, ref childMainSizingMode, ref childMainSize);
            ConstrainMaxSizeForMode(child, direction, crossAxis, availableInnerCrossDim, availableInnerWidth, ref childCrossSizingMode, ref childCrossSize);

            var requiresStretchLayout = !HasDefiniteLength(child, crossDimension, availableInnerCrossDim)
                && ResolveChildAlignment(index, child) == Align.Stretch
                && !StyleResolution.FlexStartMarginIsAuto(in styles[child], crossAxis, direction)
                && !StyleResolution.FlexEndMarginIsAuto(in styles[child], crossAxis, direction);

            var childWidth = isMainAxisRow ? childMainSize : childCrossSize;
            var childHeight = !isMainAxisRow ? childMainSize : childCrossSize;
            var childWidthSizingMode = isMainAxisRow ? childMainSizingMode : childCrossSizingMode;
            var childHeightSizingMode = !isMainAxisRow ? childMainSizingMode : childCrossSizingMode;

            var isLayoutPass = performLayout && !requiresStretchLayout;

            CalculateLayoutInternal(
                child,
                childWidth,
                childHeight,
                results[index].Direction,
                childWidthSizingMode,
                childHeightSizingMode,
                availableInnerWidth,
                availableInnerHeight,
                isLayoutPass,
                currentDepth
            );

            results[index].HadOverflow = results[index].HadOverflow || results[child].HadOverflow;
        }

        return deltaFreeSpace;
    }
}
