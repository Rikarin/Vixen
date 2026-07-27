// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Layout;

/// <summary>The small operations the algorithm is written out of.</summary>
public sealed partial class LayoutTree {
    const float ComparisonTolerance = 0.0001f;

    /// <summary>Whether two lengths are the same to within a rounding error.</summary>
    /// <remarks>
    ///     Layout arithmetic divides free space and multiplies percentages, so exact equality is
    ///     the wrong question: two values that mean the same thing routinely differ in the last
    ///     bit. Two NaNs count as equal here, because "no size" is a state and comparing it should
    ///     say so.
    /// </remarks>
    internal static bool Inexact(float left, float right) =>
        float.IsNaN(left) && float.IsNaN(right) || MathF.Abs(left - right) < ComparisonTolerance;

    static MeasureMode MeasureModeOf(SizingMode mode) => mode switch {
        SizingMode.StretchFit => MeasureMode.Exactly,
        SizingMode.MaxContent => MeasureMode.Undefined,
        _ => MeasureMode.AtMost
    };

    /// <summary>Whether a node takes part in flow layout at all.</summary>
    bool IsInFlow(int index) =>
        styles[index].Display != Display.None && styles[index].PositionType != PositionType.Absolute;

    /// <summary>Whether a node's size can be decided without measuring it.</summary>
    bool HasDefiniteLength(int index, Dimension dimension, float ownerSize) {
        var value = StyleResolution.ProcessedDimension(in styles[index], dimension).Resolve(ownerSize);
        return !float.IsNaN(value) && value >= 0f;
    }

    /// <summary>The node's written size on an axis, in border-box terms.</summary>
    float ResolvedDimension(int index, Dimension dimension, float referenceLength, float ownerWidth, Direction direction) =>
        StyleResolution.WithBoxSizing(
            in styles[index],
            StyleResolution.ProcessedDimension(in styles[index], dimension).Resolve(referenceLength),
            dimension,
            ownerWidth,
            direction
        );

    /// <summary>Whether a node can grow or shrink at all.</summary>
    bool IsNodeFlexible(int index) {
        if (styles[index].PositionType == PositionType.Absolute) {
            return false;
        }

        var isRoot = links[index].Parent < 0;
        return StyleResolution.ResolveFlexGrow(in styles[index], isRoot) != 0f
            || StyleResolution.ResolveFlexShrink(in styles[index], isRoot) != 0f;
    }

    /// <summary>The node's measured size on an axis plus its margins on that axis.</summary>
    float DimensionWithMargin(int index, FlexDirection axis, float widthSize) =>
        results[index].MeasuredDimensions[(int) FlexAxis.DimensionOf(axis)]
        + StyleResolution.MarginForAxis(in styles[index], axis, widthSize);

    /// <summary>Whether a node came out of layout with a usable size on an axis.</summary>
    bool IsLayoutDimensionDefined(int index, FlexDirection axis) {
        var value = results[index].MeasuredDimensions[(int) FlexAxis.DimensionOf(axis)];
        return !float.IsNaN(value) && value >= 0f;
    }

    /// <summary>How a child should be aligned on the cross axis, once <c>auto</c> is resolved.</summary>
    Align ResolveChildAlignment(int index, int child) {
        var align = styles[child].AlignSelf == Align.Auto ? styles[index].AlignItems : styles[child].AlignSelf;

        // A baseline is a property of a line of text, and a column container has no line to align
        // to, so the request degrades rather than being ignored.
        return align == Align.Baseline && FlexAxis.IsColumn(styles[index].FlexDirection) ? Align.FlexStart : align;
    }

    /// <summary>Clamps a value to a node's own min and max on an axis.</summary>
    float BoundAxisWithinMinAndMax(int index, Direction direction, FlexDirection axis, float value, float axisSize, float widthSize) {
        var dimension = FlexAxis.DimensionOf(axis);
        var min = StyleResolution.ResolvedMinDimension(in styles[index], dimension, axisSize, widthSize, direction);
        var max = StyleResolution.ResolvedMaxDimension(in styles[index], dimension, axisSize, widthSize, direction);

        if (!float.IsNaN(max) && max >= 0f && value > max) {
            return max;
        }

        if (!float.IsNaN(min) && min >= 0f && value < min) {
            return min;
        }

        return value;
    }

    /// <summary>Clamps to min and max, and never below the node's own padding and border.</summary>
    float BoundAxis(int index, FlexDirection axis, Direction direction, float value, float axisSize, float widthSize) =>
        MathF.Max(
            BoundAxisWithinMinAndMax(index, direction, axis, value, axisSize, widthSize),
            StyleResolution.PaddingAndBorderForAxis(in styles[index], axis, direction, widthSize)
        );

    /// <summary>As <see cref="BoundAxis" />, plus the automatic minimum from CSS Flexbox §4.5.</summary>
    float BoundAxisWithAutoMin(int index, FlexDirection axis, Direction direction, float value, float axisSize, float widthSize) {
        var bounded = BoundAxis(index, axis, direction, value, axisSize, widthSize);
        var autoMin = results[index].ComputedAutoMinMainSize;
        return !float.IsNaN(autoMin) && bounded < autoMin ? autoMin : bounded;
    }

    /// <summary>Narrows a sizing request so it cannot exceed the node's own maximum.</summary>
    void ConstrainMaxSizeForMode(
        int index,
        Direction direction,
        FlexDirection axis,
        float ownerAxisSize,
        float ownerWidth,
        ref SizingMode mode,
        ref float size
    ) {
        var max = StyleResolution.ResolvedMaxDimension(in styles[index], FlexAxis.DimensionOf(axis), ownerAxisSize, ownerWidth, direction);
        if (float.IsNaN(max)) {
            return;
        }

        max += StyleResolution.MarginForAxis(in styles[index], axis, ownerWidth);

        switch (mode) {
            case SizingMode.StretchFit:
            case SizingMode.FitContent:
                size = size < max ? size : max;
                break;
            case SizingMode.MaxContent:
                // An unbounded request becomes a bounded one: the maximum is the bound.
                mode = SizingMode.FitContent;
                size = max;
                break;
            default:
                break;
        }
    }

    /// <summary>
    ///     The automatic minimum main size from CSS Flexbox §4.5, or NaN when none applies.
    /// </summary>
    /// <remarks>
    ///     Without this, a row of text shrinks until the words are one character wide. The floor is
    ///     the smaller of what the content needs and what the item asked for, capped by its maximum
    ///     — and an item with its own <c>min-width</c>, or with <c>overflow</c> other than visible,
    ///     opts out, which is the specification's own escape hatch.
    /// </remarks>
    float ComputeAutoMinMainSize(
        int index,
        FlexDirection mainAxis,
        Direction direction,
        float ownerMainAxisSize,
        float ownerWidth,
        float ownerHeight
    ) {
        var mainDimension = FlexAxis.DimensionOf(mainAxis);

        if (styles[index].Display == Display.None) {
            return float.NaN;
        }

        if (styles[index].MinDimensions[(int) mainDimension].IsDefined) {
            return float.NaN;
        }

        if (styles[index].Overflow != Overflow.Visible) {
            return 0f;
        }

        var isMainAxisRow = FlexAxis.IsRow(mainAxis);
        var crossDimension = isMainAxisRow ? Dimension.Height : Dimension.Width;

        var specified = ResolvedDimension(index, mainDimension, ownerMainAxisSize, ownerWidth, direction);

        var transferred = float.NaN;
        var aspectRatio = styles[index].AspectRatio;
        if (!float.IsNaN(aspectRatio)) {
            var crossOwner = isMainAxisRow ? ownerHeight : ownerWidth;
            var cross = ResolvedDimension(index, crossDimension, crossOwner, ownerWidth, direction);
            if (!float.IsNaN(cross)) {
                transferred = isMainAxisRow ? cross * aspectRatio : cross / aspectRatio;
            }
        }

        var floor = ComputeMinContentSize(index, mainAxis, direction, ownerWidth, ownerHeight);

        if (!float.IsNaN(specified)) {
            if (float.IsNaN(floor) || specified < floor) {
                floor = specified;
            }
        } else if (!float.IsNaN(transferred)) {
            if (float.IsNaN(floor) || transferred < floor) {
                floor = transferred;
            }
        }

        var max = StyleResolution.ResolvedMaxDimension(in styles[index], mainDimension, ownerMainAxisSize, ownerWidth, direction);
        if (!float.IsNaN(max) && floor > max) {
            floor = max;
        }

        return float.IsNaN(floor) || floor < 0f ? 0f : floor;
    }

    /// <summary>The smallest a node can be on an axis without its content overflowing.</summary>
    /// <remarks>
    ///     A leaf answers by being measured with nothing to spare on the axis in question, which is
    ///     what makes a text measurer report its longest word. A container answers by summing its
    ///     children along its own main axis and taking the largest across its cross axis. No layout
    ///     is written along the way — only the leaf measure callbacks see anything happen.
    /// </remarks>
    float ComputeMinContentSize(int index, FlexDirection requestedAxis, Direction ownerDirection, float ownerWidth, float ownerHeight) {
        var wantRow = FlexAxis.IsRow(requestedAxis);
        var axis = wantRow ? 0 : 1;

        // Without this the §4.5 probe measures every flex item on every pass, uncached — which is
        // precisely the per-frame text measurement doc 09 says the measure cache exists to prevent.
        // It is keyed on the owner size because percentage margins and padding resolve against it,
        // and invalidated by the dirty flag, which a change anywhere below this node has already set.
        if (!float.IsNaN(results[index].MinContentSizes[axis])
            && Inexact(results[index].MinContentOwnerWidth, ownerWidth)
            && Inexact(results[index].MinContentOwnerHeight, ownerHeight)) {
            return results[index].MinContentSizes[axis];
        }

        var computed = ComputeMinContentSizeUncached(index, requestedAxis, ownerDirection, ownerWidth, ownerHeight);

        if (!Inexact(results[index].MinContentOwnerWidth, ownerWidth)
            || !Inexact(results[index].MinContentOwnerHeight, ownerHeight)) {
            results[index].MinContentSizes[0] = float.NaN;
            results[index].MinContentSizes[1] = float.NaN;
            results[index].MinContentOwnerWidth = ownerWidth;
            results[index].MinContentOwnerHeight = ownerHeight;
        }

        results[index].MinContentSizes[axis] = computed;
        return computed;
    }

    float ComputeMinContentSizeUncached(int index, FlexDirection requestedAxis, Direction ownerDirection, float ownerWidth, float ownerHeight) {
        var wantRow = FlexAxis.IsRow(requestedAxis);

        if ((flags[index] & LayoutNodeState.HasMeasureFunction) != 0) {
            var size = Measure(
                index,
                wantRow ? 0f : float.NaN,
                wantRow ? MeasureMode.AtMost : MeasureMode.Undefined,
                wantRow ? float.NaN : 0f,
                wantRow ? MeasureMode.Undefined : MeasureMode.AtMost
            );

            var leafDirection = StyleResolution.ResolveDirection(in styles[index], ownerDirection);
            var leafPaddingAndBorder =
                StyleResolution.FlexStartPaddingAndBorder(in styles[index], requestedAxis, leafDirection, ownerWidth)
                + StyleResolution.FlexEndPaddingAndBorder(in styles[index], requestedAxis, leafDirection, ownerWidth);

            return (wantRow ? size.Width : size.Height) + leafPaddingAndBorder;
        }

        if (links[index].ChildCount == 0) {
            return 0f;
        }

        var direction = StyleResolution.ResolveDirection(in styles[index], ownerDirection);
        var nodeMainAxis = FlexAxis.Resolve(styles[index].FlexDirection, direction);
        var nodeCrossAxis = FlexAxis.ResolveCross(nodeMainAxis, direction);

        var mainTotal = 0f;
        var crossMax = 0f;

        foreach (var child in ChildIds(index)) {
            if (!IsInFlow(child)) {
                continue;
            }

            var childMain = ComputeMinContentSize(child, nodeMainAxis, direction, ownerWidth, ownerHeight)
                + StyleResolution.MarginForAxis(in styles[child], nodeMainAxis, ownerWidth);
            var childCross = ComputeMinContentSize(child, nodeCrossAxis, direction, ownerWidth, ownerHeight)
                + StyleResolution.MarginForAxis(in styles[child], nodeCrossAxis, ownerWidth);

            mainTotal += childMain;
            crossMax = MathF.Max(crossMax, childCross);
        }

        mainTotal += StyleResolution.FlexStartPaddingAndBorder(in styles[index], nodeMainAxis, direction, ownerWidth)
            + StyleResolution.FlexEndPaddingAndBorder(in styles[index], nodeMainAxis, direction, ownerWidth);
        crossMax += StyleResolution.FlexStartPaddingAndBorder(in styles[index], nodeCrossAxis, direction, ownerWidth)
            + StyleResolution.FlexEndPaddingAndBorder(in styles[index], nodeCrossAxis, direction, ownerWidth);

        var nodeMainIsRow = FlexAxis.IsRow(nodeMainAxis);
        return wantRow ? nodeMainIsRow ? mainTotal : crossMax : nodeMainIsRow ? crossMax : mainTotal;
    }

    LayoutSize Measure(int index, float width, MeasureMode widthMode, float height, MeasureMode heightMode) {
        var measure = MeasureFunctionOf(index)!;
        var request = new MeasureRequest(this, new LayoutNodeId(index), ContextOf(index), width, widthMode, height, heightMode);
        return measure(in request);
    }

    /// <summary>Places a node at its relative offset from where flow would have put it.</summary>
    void SetPosition(int index, Direction direction, float ownerWidth, float ownerHeight) {
        // A root is always laid out left to right, so that its own position never goes negative.
        var directionRespectingRoot = links[index].Parent >= 0 ? direction : Direction.Ltr;
        var mainAxis = FlexAxis.Resolve(styles[index].FlexDirection, directionRespectingRoot);
        var crossAxis = FlexAxis.ResolveCross(mainAxis, directionRespectingRoot);

        var relativeMain = RelativePosition(index, mainAxis, directionRespectingRoot, FlexAxis.IsRow(mainAxis) ? ownerWidth : ownerHeight);
        var relativeCross = RelativePosition(index, crossAxis, directionRespectingRoot, FlexAxis.IsRow(mainAxis) ? ownerHeight : ownerWidth);

        var mainStart = (int) FlexAxis.InlineStartEdge(mainAxis, direction);
        var mainEnd = (int) FlexAxis.InlineEndEdge(mainAxis, direction);
        var crossStart = (int) FlexAxis.InlineStartEdge(crossAxis, direction);
        var crossEnd = (int) FlexAxis.InlineEndEdge(crossAxis, direction);

        results[index].Position[mainStart] =
            StyleResolution.InlineStartMargin(in styles[index], mainAxis, direction, ownerWidth) + relativeMain;
        results[index].Position[mainEnd] =
            StyleResolution.InlineEndMargin(in styles[index], mainAxis, direction, ownerWidth) + relativeMain;
        results[index].Position[crossStart] =
            StyleResolution.InlineStartMargin(in styles[index], crossAxis, direction, ownerWidth) + relativeCross;
        results[index].Position[crossEnd] =
            StyleResolution.InlineEndMargin(in styles[index], crossAxis, direction, ownerWidth) + relativeCross;
    }

    float RelativePosition(int index, FlexDirection axis, Direction direction, float axisSize) {
        // position: static ignores inset entirely — https://www.w3.org/TR/css-position-3/#valdef-position-static
        if (styles[index].PositionType == PositionType.Static) {
            return 0f;
        }

        if (StyleResolution.IsInlineStartPositionDefined(in styles[index], axis, direction)
            && !StyleResolution.IsInlineStartPositionAuto(in styles[index], axis, direction)) {
            return StyleResolution.InlineStartPosition(in styles[index], axis, direction, axisSize);
        }

        return -StyleResolution.InlineEndPosition(in styles[index], axis, direction, axisSize);
    }

    static bool NeedsTrailingPosition(FlexDirection axis) =>
        axis is FlexDirection.RowReverse or FlexDirection.ColumnReverse;

    /// <summary>Derives the far edge's offset from the near edge's, now that both sizes are known.</summary>
    void SetChildTrailingPosition(int containing, int child, FlexDirection axis) {
        var dimension = (int) FlexAxis.DimensionOf(axis);
        var position = results[child].Position[(int) FlexAxis.FlexStartEdge(axis)];
        results[child].Position[(int) FlexAxis.FlexEndEdge(axis)] =
            results[containing].MeasuredDimensions[dimension] - results[child].MeasuredDimensions[dimension] - position;
    }

    float PositionOfOppositeEdge(float position, FlexDirection axis, int containing, int child) {
        var dimension = (int) FlexAxis.DimensionOf(axis);
        return results[containing].MeasuredDimensions[dimension] - results[child].MeasuredDimensions[dimension] - position;
    }

    /// <summary>Blanks a subtree that is not being laid out, so it reports nothing rather than stale.</summary>
    void ZeroOutLayoutRecursively(int index) {
        var cached = results[index].CachedLayout;
        results[index] = default;
        results[index].ComputedFlexBasis = float.NaN;
        results[index].ComputedAutoMinMainSize = float.NaN;
        results[index].CachedLayout = cached;
        flags[index] |= LayoutNodeState.HasNewLayout;
        flags[index] &= ~LayoutNodeState.Dirty;

        foreach (var child in ChildIds(index)) {
            ZeroOutLayoutRecursively(child);
        }
    }

    /// <summary>Whether a remembered measurement answers the question being asked.</summary>
    /// <remarks>
    ///     Three ways it can, beyond the question being identical: an exact request whose answer was
    ///     already that size; an unbounded measurement that still fits inside a new bound; and a
    ///     tighter bound than last time whose old answer already fit inside it. All three are worth
    ///     having because a measure function is the most expensive thing in a layout pass.
    /// </remarks>
    bool CanUseCachedMeasurement(
        SizingMode widthMode,
        float availableWidth,
        SizingMode heightMode,
        float availableHeight,
        in CachedMeasurement entry,
        float marginRow,
        float marginColumn
    ) {
        if (!entry.IsPopulated) {
            return false;
        }

        if ((!float.IsNaN(entry.ComputedHeight) && entry.ComputedHeight < 0f)
            || (!float.IsNaN(entry.ComputedWidth) && entry.ComputedWidth < 0f)) {
            return false;
        }

        var scale = PointScaleFactor;
        var rounded = scale != 0f;
        var effectiveWidth = rounded ? RoundToPixelGrid(availableWidth, scale, false, false) : availableWidth;
        var effectiveHeight = rounded ? RoundToPixelGrid(availableHeight, scale, false, false) : availableHeight;
        var effectiveLastWidth = rounded ? RoundToPixelGrid(entry.AvailableWidth, scale, false, false) : entry.AvailableWidth;
        var effectiveLastHeight = rounded ? RoundToPixelGrid(entry.AvailableHeight, scale, false, false) : entry.AvailableHeight;

        var lastWidthMode = SizingModeOf(entry.WidthMeasureMode);
        var lastHeightMode = SizingModeOf(entry.HeightMeasureMode);

        var sameWidthSpec = lastWidthMode == widthMode && Inexact(effectiveLastWidth, effectiveWidth);
        var sameHeightSpec = lastHeightMode == heightMode && Inexact(effectiveLastHeight, effectiveHeight);

        var widthCompatible = sameWidthSpec
            || ExactAndMatchesOld(widthMode, availableWidth - marginRow, entry.ComputedWidth)
            || OldWasMaxContentAndStillFits(widthMode, availableWidth - marginRow, lastWidthMode, entry.ComputedWidth)
            || NewIsStricterAndStillValid(widthMode, availableWidth - marginRow, lastWidthMode, entry.AvailableWidth, entry.ComputedWidth);

        var heightCompatible = sameHeightSpec
            || ExactAndMatchesOld(heightMode, availableHeight - marginColumn, entry.ComputedHeight)
            || OldWasMaxContentAndStillFits(heightMode, availableHeight - marginColumn, lastHeightMode, entry.ComputedHeight)
            || NewIsStricterAndStillValid(heightMode, availableHeight - marginColumn, lastHeightMode, entry.AvailableHeight, entry.ComputedHeight);

        return widthCompatible && heightCompatible;

        static bool ExactAndMatchesOld(SizingMode mode, float size, float lastComputed) =>
            mode == SizingMode.StretchFit && Inexact(size, lastComputed);

        static bool OldWasMaxContentAndStillFits(SizingMode mode, float size, SizingMode lastMode, float lastComputed) =>
            mode == SizingMode.FitContent && lastMode == SizingMode.MaxContent
            && (size >= lastComputed || Inexact(size, lastComputed));

        static bool NewIsStricterAndStillValid(SizingMode mode, float size, SizingMode lastMode, float lastSize, float lastComputed) =>
            lastMode == SizingMode.FitContent && mode == SizingMode.FitContent
            && !float.IsNaN(lastSize) && !float.IsNaN(size) && !float.IsNaN(lastComputed)
            && lastSize > size && (lastComputed <= size || Inexact(size, lastComputed));

        static SizingMode SizingModeOf(MeasureMode mode) => mode switch {
            MeasureMode.Exactly => SizingMode.StretchFit,
            MeasureMode.Undefined => SizingMode.MaxContent,
            _ => SizingMode.FitContent
        };
    }
}
