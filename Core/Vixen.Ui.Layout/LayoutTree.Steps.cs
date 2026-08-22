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
            SetMeasuredDimension(index, FlexDirection.Row, direction, availableWidth, ownerWidth, ownerWidth, true);
            SetMeasuredDimension(index, FlexDirection.Column, direction, availableHeight, ownerHeight, ownerWidth, true);
            return;
        }

        var measured = Measure(index, innerWidth, MeasureModeOf(widthSizingMode), innerHeight, MeasureModeOf(heightSizingMode));

        SetMeasuredDimension(
            index,
            FlexDirection.Row,
            direction,
            widthSizingMode is SizingMode.MaxContent or SizingMode.FitContent
                ? measured.Width + paddingAndBorderRow
                : availableWidth,
            ownerWidth,
            ownerWidth,
            widthSizingMode == SizingMode.StretchFit
        );

        SetMeasuredDimension(
            index,
            FlexDirection.Column,
            direction,
            heightSizingMode is SizingMode.MaxContent or SizingMode.FitContent
                ? measured.Height + paddingAndBorderColumn
                : availableHeight,
            ownerHeight,
            ownerWidth,
            heightSizingMode == SizingMode.StretchFit
        );

        // ⚠ THE CONTENT ANSWERED FOR AN AXIS THE RATIO OWNS, so the ratio answers again over the top.
        // A measured leaf reports both axes from its text, and the two have no reason to agree with a
        // ratio that relates them: `aspect_ratio_flex_column_fill_max_width` measured 80x10, was
        // clamped to 40 wide by the `max-width` its `max-height: 20px` transfers, and stayed 10 tall
        // — where Chrome says 20, because 40 across a ratio of 2 is 20 and nothing about the text
        // gets a say once the ratio has one. The inline axis anchors the pair, which is the order
        // CSS Sizing §4.1 resolves them in and the order every one of these fixtures agrees with.
        if (heightSizingMode == SizingMode.StretchFit || !IsFlexOrGridItem(index)) {
            return;
        }

        var ratio = styles[index].AspectRatio;
        if (float.IsNaN(ratio) || ratio <= 0f) {
            return;
        }

        SetMeasuredDimension(
            index,
            FlexDirection.Column,
            direction,
            HeightAcrossRatio(index, direction, results[index].MeasuredDimensions[(int) Dimension.Width], ownerWidth),
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

        SetMeasuredDimension(index, FlexDirection.Row, direction, width, ownerWidth, ownerWidth, widthSizingMode == SizingMode.StretchFit);

        var height = availableHeight;
        if (heightSizingMode is SizingMode.MaxContent or SizingMode.FitContent) {
            height = layout.Padding[(int) Edge.Top] + layout.Padding[(int) Edge.Bottom]
                + layout.Border[(int) Edge.Top] + layout.Border[(int) Edge.Bottom];
        }

        SetMeasuredDimension(index, FlexDirection.Column, direction, height, ownerHeight, ownerWidth, heightSizingMode == SizingMode.StretchFit);
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

        SetMeasuredDimension(
            index,
            FlexDirection.Row,
            direction,
            float.IsNaN(availableWidth) || (widthSizingMode == SizingMode.FitContent && availableWidth < 0f)
                ? 0f
                : availableWidth,
            ownerWidth,
            ownerWidth,
            widthSizingMode == SizingMode.StretchFit
        );

        SetMeasuredDimension(
            index,
            FlexDirection.Column,
            direction,
            float.IsNaN(availableHeight) || (heightSizingMode == SizingMode.FitContent && availableHeight < 0f)
                ? 0f
                : availableHeight,
            ownerHeight,
            ownerWidth,
            heightSizingMode == SizingMode.StretchFit
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

    /// <summary>Works out every in-flow child's flex basis.</summary>
    /// <remarks>
    ///     ⚠ It used to hand back the sum of those bases and <c>CalculateLayoutImpl</c> used the sum
    ///     to decide whether the main axis overflows. That is §9.3's question and §9.3 asks it of the
    ///     outer HYPOTHETICAL main sizes, so the caller now takes the sum itself, in the walk that
    ///     computes each item's automatic minimum. See the note at STEP 3.
    /// </remarks>
    void ComputeFlexBasisForChildren(
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

        }
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

        // Whether the basis about to be written was read off a declaration or measured. See
        // LayoutResult.FlexBasisFromContent — it is what caps §4.5's automatic minimum.
        results[child].FlexBasisFromContent = false;

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
            results[child].FlexBasisFromContent = true;

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
            //
            // ⚠ The *main* axis's overflow, which is the reading this always had written out: a
            // column that scrolls still hands its width down, a row that scrolls does not. Reading
            // `overflow-x` here instead would be more principled and would also change what plain
            // `overflow: scroll` does to a column — and that answer is Yoga's conformance suite
            // rather than a judgement this change gets to make.
            var mainOverflow = OverflowOn(index, FlexAxis.DimensionOf(mainAxis));
            if (!isMainAxisRow || mainOverflow != Overflow.Scroll) {
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

            // ⚠ THE UNCLAMPED MEASUREMENT, because §9.2's flex base size and hypothetical main size
            // are two numbers and this is the first of them. Step 3E sizes the item under a
            // max-content constraint and takes the result; step 4 clamps THAT by the item's used min
            // and max. Reading MeasuredDimensions here made the base equal to the hypothetical size
            // by construction, so §9.7 step 2's freeze test — base on the far side of hypothetical —
            // could never fire, and the clamp was instead charged to the free-space pool. An empty
            // `min-width: 60px` item in a 100pt row reported a base of 60 and came out 80 wide;
            // Chrome freezes it at 60 and gives the other 40 to its sibling.
            results[child].ComputedFlexBasis = MathF.Max(
                results[child].UnclampedMeasuredDimensions[(int) FlexAxis.DimensionOf(mainAxis)],
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
            // §9.3 collects items into lines by their OUTER HYPOTHETICAL MAIN SIZE, which is the
            // flex base size clamped by the used min — §4.5's automatic one included. The stated
            // clamp is accumulated separately for the container's own content size; see
            // FlexLine.SizeConsumed for why the automatic minimum is kept out of that one.
            var flexBasisWithConstraints = HypotheticalMainSize(
                child,
                direction,
                mainAxis,
                results[child].ComputedFlexBasis,
                mainAxisOwnerSize,
                ownerWidth
            );

            var statedFlexBasis = BoundAxisWithinMinAndMax(
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
            line.HypotheticalSizeConsumed += flexBasisWithConstraints + childMarginMainAxis + childLeadingGap;
            line.SizeConsumed += statedFlexBasis + childMarginMainAxis + childLeadingGap;
            line.MarginAndGapConsumed += childMarginMainAxis + childLeadingGap;

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

    /// <summary>CSS Flexbox §9.7 step 3: the free space the distribution passes start from.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is a different sum from <see cref="FlexLine.SizeConsumed" /> and the
    ///         difference is the whole of the §9.7 bucket.</b> §9.3 breaks lines by each item's outer
    ///         HYPOTHETICAL main size — its flex base clamped by its used min and max. §9.7 step 3
    ///         subtracts the FROZEN items' target sizes and the unfrozen items' <i>flex base</i>
    ///         sizes, unclamped. Vixen used the §9.3 sum for both, so a clamp was charged twice: once
    ///         by shrinking the pool it came out of, and again by the distribution pass that
    ///         re-applied it to the item.
    ///     </para>
    ///     <para>
    ///         <c>min_width</c> is the arithmetic in four numbers. Two <c>flex-grow: 1</c> items in a
    ///         100px row, <c>min-width: 60px</c> on the first, both flex base sizes 0. The pool was
    ///         100 − 60 = 40, split evenly to 20 each, and then the 60 clamped back on top — 80 and
    ///         20, and the line overflows its own container by 20. The pool is 100: the first pass
    ///         finds the first item violating its minimum, freezes it at 60 and takes it out of the
    ///         pool, and the 40 that is left all goes to its sibling. 60 and 40, which is Chrome's
    ///         answer. ⚠ <b>The heading this bucket carried blamed a missing re-distribution loop.
    ///         That loop is present and correct</b> — <see cref="DistributeFreeSpaceFirstPass" /> is
    ///         exactly it. What it was given was a pool that had already paid for the clamp.
    ///     </para>
    ///     <para>
    ///         ⚠ Step 1 picks grow or shrink from the HYPOTHETICAL sum, not from this one, which is
    ///         why the direction is decided before the sum is taken rather than from its sign. An
    ///         item whose maximum clamps it far below its base can make the two disagree.
    ///     </para>
    ///     <para>
    ///         Takes the line by reference because step 2 also removes the frozen items' flex factors
    ///         from the totals the pool is divided by.
    ///     </para>
    /// </remarks>
    float InitialFreeSpace(
        int index,
        ref FlexLine line,
        Direction direction,
        FlexDirection mainAxis,
        float ownerWidth,
        float mainAxisOwnerSize,
        float availableInnerMainDim
    ) {
        var useGrow = line.UseGrow;
        var children = ChildIds(index);
        var consumed = line.MarginAndGapConsumed;

        for (var i = line.StartChild; i < line.EndChild; i++) {
            var child = children[i];
            if (!IsInFlow(child)) {
                continue;
            }

            var isRoot = links[child].Parent < 0;
            consumed += DistributionStartSize(child, direction, mainAxis, ownerWidth, mainAxisOwnerSize, useGrow);

            // ⚠ A FROZEN ITEM IS OUT OF THE POOL AND OUT OF THE DIVISOR, and leaving it in the
            // divisor is the half of step 2 that is easy to miss. Yoga's Child_min_max_width_flexing
            // is a 120px row holding a `flex-basis: 0; min-width: 60px` item and a
            // `flex-basis: 50%; max-width: 20px` one. The second freezes at 20 immediately, so the
            // whole 100 that is left belongs to the first and it ends up 100 wide. Counting the
            // frozen item's grow factor as well splits that 100 two ways, the first item's 50
            // violates its own 60 minimum, and BOTH items end up frozen — which leaves the second
            // pass dividing by a total of zero and handing back a NaN.
            if (!IsFrozenByInflexibility(child, direction, mainAxis, ownerWidth, mainAxisOwnerSize, useGrow)) {
                continue;
            }

            if (!IsNodeFlexible(child)) {
                continue;
            }

            if (useGrow) {
                line.TotalFlexGrowFactors -= StyleResolution.ResolveFlexGrow(in styles[child], isRoot);
            } else {
                line.TotalFlexShrinkScaledFactors -=
                    -StyleResolution.ResolveFlexShrink(in styles[child], isRoot) * results[child].ComputedFlexBasis;
            }
        }

        // The same floor CalculateFlexLine applies, re-applied because the totals just moved.
        if (line.TotalFlexGrowFactors is > 0f and < 1f) {
            line.TotalFlexGrowFactors = 1f;
        }

        if (line.TotalFlexShrinkScaledFactors is > 0f and < 1f) {
            line.TotalFlexShrinkScaledFactors = 1f;
        }

        return availableInnerMainDim - consumed;
    }

    /// <summary>
    ///     What CSS Flexbox §9.7 step 3 counts an item as, and what the distribution passes start it
    ///     from: its flex base size while it can still flex, its hypothetical main size once frozen.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Step 2's "size inflexible items" is the reason the §4.5 leftovers close here rather
    ///     than in a line of their own.</b> An item with no usable flex factor in the direction being
    ///     resolved is frozen at its HYPOTHETICAL main size, and that size has §4.5's automatic
    ///     minimum in it. <c>flex_basis_smaller_than_content_row</c> is <c>flex-basis: 50px</c> on a
    ///     column wrapping a 100px box with nothing to grow and nothing to shrink: it freezes at 100,
    ///     where it used to stay at 50 because the non-flexing path handed back the base untouched.
    ///     Applying the floor there alone was measured and does not work — the pool and the item have
    ///     to be counting the same number, which is what routing both through here guarantees.
    /// </remarks>
    float DistributionStartSize(
        int child,
        Direction direction,
        FlexDirection mainAxis,
        float ownerWidth,
        float mainAxisOwnerSize,
        bool useGrow
    ) =>
        IsFrozenByInflexibility(child, direction, mainAxis, ownerWidth, mainAxisOwnerSize, useGrow)
            ? HypotheticalMainSize(child, direction, mainAxis, results[child].ComputedFlexBasis, mainAxisOwnerSize, ownerWidth)
            : results[child].ComputedFlexBasis;

    /// <summary>CSS Flexbox §9.7 step 2: whether an item cannot flex in the direction being used.</summary>
    /// <remarks>
    ///     Frozen at its hypothetical main size, out of the free-space pool and out of the divisor
    ///     the pool is shared by. Deterministic in the item's own style and measurements, so the two
    ///     distribution passes can re-ask it rather than carrying a per-item flag.
    /// </remarks>
    bool IsFrozenByInflexibility(
        int child,
        Direction direction,
        FlexDirection mainAxis,
        float ownerWidth,
        float mainAxisOwnerSize,
        bool useGrow
    ) {
        var isRoot = links[child].Parent < 0;
        var factor = useGrow
            ? StyleResolution.ResolveFlexGrow(in styles[child], isRoot)
            : StyleResolution.ResolveFlexShrink(in styles[child], isRoot);

        if (float.IsNaN(factor) || factor == 0f) {
            return true;
        }

        // A base already on the far side of the hypothetical size means the clamp has spoken and
        // the item cannot flex away from it.
        var flexBase = results[child].ComputedFlexBasis;
        var hypothetical = HypotheticalMainSize(child, direction, mainAxis, flexBase, mainAxisOwnerSize, ownerWidth);
        return useGrow ? flexBase > hypothetical : flexBase < hypothetical;
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

        // ⚠ CSS Flexbox §4.5's automatic minimum is NOT computed here any more. It is the used
        // minimum in §9.2's hypothetical main size, which §9.3 needs before it can break lines, so
        // it is computed once per node in STEP 4's caller instead. Computing it here made it
        // invisible to line breaking and to any item that never flexed.
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

            // Already frozen by §9.7 step 2, and InitialFreeSpace has taken both its size and its
            // factor out. Handing it a share here would spend space that is not in the pool.
            if (IsFrozenByInflexibility(child, direction, mainAxis, ownerWidth, mainAxisOwnerSize, line.UseGrow)) {
                continue;
            }

            // ⚠ The same number InitialFreeSpace counted this item as. If the two disagree the pool
            // and the sizes drawn from it are measured from different baselines, and the line
            // silently over- or under-fills; see DistributionStartSize.
            var childFlexBasis = DistributionStartSize(child, direction, mainAxis, ownerWidth, mainAxisOwnerSize, line.UseGrow);

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
            // ⚠ The same number InitialFreeSpace counted this item as. If the two disagree the pool
            // and the sizes drawn from it are measured from different baselines, and the line
            // silently over- or under-fills; see DistributionStartSize.
            var childFlexBasis = DistributionStartSize(child, direction, mainAxis, ownerWidth, mainAxisOwnerSize, line.UseGrow);

            // ⚠ A frozen item's size IS its hypothetical main size, and this is where §4.5's
            // automatic minimum finally reaches an item that never flexes. It used to be handed back
            // its flex base size untouched, which is why flex_basis_smaller_than_content_row stayed
            // 50 wide around a 100px box.
            var frozen = IsFrozenByInflexibility(child, direction, mainAxis, ownerWidth, mainAxisOwnerSize, line.UseGrow);
            var updatedMainSize = childFlexBasis;

            if (frozen) {
                // Nothing to distribute to it; its size was decided by step 2.
            } else if (!float.IsNaN(line.RemainingFreeSpace) && line.RemainingFreeSpace < 0f) {
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
