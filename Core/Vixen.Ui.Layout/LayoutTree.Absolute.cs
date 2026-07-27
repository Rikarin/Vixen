// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Layout;

/// <summary>
///     Absolutely-positioned children, which are laid out by their containing block rather than by
///     their parent.
/// </summary>
/// <remarks>
///     They are done last, and from the containing block down rather than from the parent out,
///     because their position is relative to a node that may be several levels above them and whose
///     size is not known until its own layout has finished. The offset from the containing block to
///     the parent is threaded down the walk so that the result can be written back as a
///     parent-relative position, which is what everything else in the tree stores.
/// </remarks>
public sealed partial class LayoutTree {
    bool LayoutAbsoluteDescendants(
        int containingNode,
        int currentNode,
        SizingMode widthSizingMode,
        Direction currentNodeDirection,
        int currentDepth,
        float leftOffsetFromContainingBlock,
        float topOffsetFromContainingBlock,
        float containingAvailableInnerWidth,
        float containingAvailableInnerHeight
    ) {
        var hasNewLayout = false;
        var children = ChildIds(currentNode);

        for (var i = 0; i < children.Length; i++) {
            var child = children[i];
            if (styles[child].Display == Display.None) {
                continue;
            }

            if (styles[child].PositionType == PositionType.Absolute) {
                var containingBlockWidth = results[containingNode].MeasuredDimensions[(int) Dimension.Width]
                    - StyleResolution.BorderForAxis(in styles[containingNode], FlexDirection.Row);
                var containingBlockHeight = results[containingNode].MeasuredDimensions[(int) Dimension.Height]
                    - StyleResolution.BorderForAxis(in styles[containingNode], FlexDirection.Column);

                LayoutAbsoluteChild(
                    containingNode,
                    currentNode,
                    child,
                    containingBlockWidth,
                    containingBlockHeight,
                    widthSizingMode,
                    currentNodeDirection,
                    currentDepth
                );

                hasNewLayout = hasNewLayout || (flags[child] & LayoutNodeState.HasNewLayout) != 0;

                var parentMainAxis = FlexAxis.Resolve(styles[currentNode].FlexDirection, currentNodeDirection);
                var parentCrossAxis = FlexAxis.ResolveCross(parentMainAxis, currentNodeDirection);

                if (NeedsTrailingPosition(parentMainAxis)) {
                    var insetsDefined = FlexAxis.IsRow(parentMainAxis)
                        ? HorizontalInsetsDefined(child)
                        : VerticalInsetsDefined(child);
                    SetChildTrailingPosition(insetsDefined ? containingNode : currentNode, child, parentMainAxis);
                }

                if (NeedsTrailingPosition(parentCrossAxis)) {
                    var insetsDefined = FlexAxis.IsRow(parentCrossAxis)
                        ? HorizontalInsetsDefined(child)
                        : VerticalInsetsDefined(child);
                    SetChildTrailingPosition(insetsDefined ? containingNode : currentNode, child, parentCrossAxis);
                }

                // The position written above is relative to the containing block where insets were
                // given, and relative to the parent where they were not. Everything downstream
                // expects parent-relative, so the difference comes off here.
                var childLeft = results[child].Position[(int) Edge.Left];
                var childTop = results[child].Position[(int) Edge.Top];

                results[child].Position[(int) Edge.Left] =
                    HorizontalInsetsDefined(child) ? childLeft - leftOffsetFromContainingBlock : childLeft;
                results[child].Position[(int) Edge.Top] =
                    VerticalInsetsDefined(child) ? childTop - topOffsetFromContainingBlock : childTop;
            } else if (styles[child].PositionType == PositionType.Static) {
                var childDirection = StyleResolution.ResolveDirection(in styles[child], currentNodeDirection);
                var childLeft = leftOffsetFromContainingBlock + results[child].Position[(int) Edge.Left];
                var childTop = topOffsetFromContainingBlock + results[child].Position[(int) Edge.Top];

                hasNewLayout = LayoutAbsoluteDescendants(
                        containingNode,
                        child,
                        widthSizingMode,
                        childDirection,
                        currentDepth + 1,
                        childLeft,
                        childTop,
                        containingAvailableInnerWidth,
                        containingAvailableInnerHeight
                    )
                    || hasNewLayout;

                if (hasNewLayout) {
                    flags[child] |= LayoutNodeState.HasNewLayout;
                }
            }
        }

        if (hasNewLayout) {
            // The rounding pass decides whether to descend by asking whether the algorithm ran for a
            // node. Absolute descendants are laid out by walking *through* static nodes whose own
            // algorithm did not run, so without this their subtree would be skipped and their
            // rounded positions would be a frame stale.
            results[currentNode].ImplGeneration = generation;
        }

        return hasNewLayout;
    }

    void LayoutAbsoluteChild(
        int containingNode,
        int node,
        int child,
        float containingBlockWidth,
        float containingBlockHeight,
        SizingMode widthMode,
        Direction direction,
        int currentDepth
    ) {
        var mainAxis = FlexAxis.Resolve(styles[node].FlexDirection, direction);
        var crossAxis = FlexAxis.ResolveCross(mainAxis, direction);
        var isMainAxisRow = FlexAxis.IsRow(mainAxis);

        var childWidth = float.NaN;
        var childHeight = float.NaN;
        var childWidthSizingMode = SizingMode.MaxContent;
        var childHeightSizingMode = SizingMode.MaxContent;

        var marginRow = StyleResolution.MarginForAxis(in styles[child], FlexDirection.Row, containingBlockWidth);
        var marginColumn = StyleResolution.MarginForAxis(in styles[child], FlexDirection.Column, containingBlockWidth);

        if (HasDefiniteLength(child, Dimension.Width, containingBlockWidth)) {
            childWidth = ResolvedDimension(child, Dimension.Width, containingBlockWidth, containingBlockWidth, direction) + marginRow;
        } else if (BothInsetsDefined(child, FlexDirection.Row, direction)) {
            // With both edges pinned and no width of its own, the width is what is left between them.
            childWidth = results[containingNode].MeasuredDimensions[(int) Dimension.Width]
                - (StyleResolution.FlexStartBorder(in styles[containingNode], FlexDirection.Row, direction)
                    + StyleResolution.FlexEndBorder(in styles[containingNode], FlexDirection.Row, direction))
                - (StyleResolution.FlexStartPosition(in styles[child], FlexDirection.Row, direction, containingBlockWidth)
                    + StyleResolution.FlexEndPosition(in styles[child], FlexDirection.Row, direction, containingBlockWidth));

            childWidth = BoundAxis(child, FlexDirection.Row, direction, childWidth, containingBlockWidth, containingBlockWidth);
        }

        if (HasDefiniteLength(child, Dimension.Height, containingBlockHeight)) {
            childHeight = ResolvedDimension(child, Dimension.Height, containingBlockHeight, containingBlockWidth, direction) + marginColumn;
        } else if (BothInsetsDefined(child, FlexDirection.Column, direction)) {
            childHeight = results[containingNode].MeasuredDimensions[(int) Dimension.Height]
                - (StyleResolution.FlexStartBorder(in styles[containingNode], FlexDirection.Column, direction)
                    + StyleResolution.FlexEndBorder(in styles[containingNode], FlexDirection.Column, direction))
                - (StyleResolution.FlexStartPosition(in styles[child], FlexDirection.Column, direction, containingBlockHeight)
                    + StyleResolution.FlexEndPosition(in styles[child], FlexDirection.Column, direction, containingBlockHeight));

            childHeight = BoundAxis(child, FlexDirection.Column, direction, childHeight, containingBlockHeight, containingBlockWidth);
        }

        // An aspect ratio needs exactly one side to anchor to; with both or neither it says nothing.
        var aspectRatio = styles[child].AspectRatio;
        if (float.IsNaN(childWidth) != float.IsNaN(childHeight) && !float.IsNaN(aspectRatio)) {
            if (float.IsNaN(childWidth)) {
                childWidth = marginRow + ((childHeight - marginColumn) * aspectRatio);
            } else {
                childHeight = marginColumn + ((childWidth - marginRow) / aspectRatio);
            }
        }

        if (float.IsNaN(childWidth) || float.IsNaN(childHeight)) {
            childWidthSizingMode = float.IsNaN(childWidth) ? SizingMode.MaxContent : SizingMode.StretchFit;
            childHeightSizingMode = float.IsNaN(childHeight) ? SizingMode.MaxContent : SizingMode.StretchFit;

            // Constraining to the containing block is what lets text inside an absolute child wrap
            // to that block's width, which is what browsers do.
            if (!isMainAxisRow
                && float.IsNaN(childWidth)
                && widthMode != SizingMode.MaxContent
                && !float.IsNaN(containingBlockWidth)
                && containingBlockWidth > 0f) {
                childWidth = containingBlockWidth;
                childWidthSizingMode = SizingMode.FitContent;
            }

            CalculateLayoutInternal(
                child,
                childWidth,
                childHeight,
                direction,
                childWidthSizingMode,
                childHeightSizingMode,
                containingBlockWidth,
                containingBlockHeight,
                performLayout: false,
                currentDepth
            );

            childWidth = results[child].MeasuredDimensions[(int) Dimension.Width]
                + StyleResolution.MarginForAxis(in styles[child], FlexDirection.Row, containingBlockWidth);
            childHeight = results[child].MeasuredDimensions[(int) Dimension.Height]
                + StyleResolution.MarginForAxis(in styles[child], FlexDirection.Column, containingBlockWidth);
        }

        CalculateLayoutInternal(
            child,
            childWidth,
            childHeight,
            direction,
            SizingMode.StretchFit,
            SizingMode.StretchFit,
            containingBlockWidth,
            containingBlockHeight,
            performLayout: true,
            currentDepth
        );

        PositionAbsoluteChild(containingNode, node, child, direction, mainAxis, true, containingBlockWidth, containingBlockHeight);
        PositionAbsoluteChild(containingNode, node, child, direction, crossAxis, false, containingBlockWidth, containingBlockHeight);
    }

    void PositionAbsoluteChild(
        int containingNode,
        int parent,
        int child,
        Direction direction,
        FlexDirection axis,
        bool isMainAxis,
        float containingBlockWidth,
        float containingBlockHeight
    ) {
        var containingBlockSize = FlexAxis.IsRow(axis) ? containingBlockWidth : containingBlockHeight;
        var flexStart = (int) FlexAxis.FlexStartEdge(axis);

        // The start inset wins over the end inset when both are set. The result is written to the
        // flex-start edge either way, because that is the edge everything else in the algorithm
        // positions from.
        if (StyleResolution.IsInlineStartPositionDefined(in styles[child], axis, direction)
            && !StyleResolution.IsInlineStartPositionAuto(in styles[child], axis, direction)) {
            var relativeToInlineStart = StyleResolution.InlineStartPosition(in styles[child], axis, direction, containingBlockSize)
                + StyleResolution.InlineStartBorder(in styles[containingNode], axis, direction)
                + StyleResolution.InlineStartMargin(in styles[child], axis, direction, containingBlockSize);

            results[child].Position[flexStart] = FlexAxis.InlineStartEdge(axis, direction) != FlexAxis.FlexStartEdge(axis)
                ? PositionOfOppositeEdge(relativeToInlineStart, axis, containingNode, child)
                : relativeToInlineStart;
            return;
        }

        if (StyleResolution.IsInlineEndPositionDefined(in styles[child], axis, direction)
            && !StyleResolution.IsInlineEndPositionAuto(in styles[child], axis, direction)) {
            var dimension = (int) FlexAxis.DimensionOf(axis);
            var relativeToInlineStart = results[containingNode].MeasuredDimensions[dimension]
                - results[child].MeasuredDimensions[dimension]
                - StyleResolution.InlineEndBorder(in styles[containingNode], axis, direction)
                - StyleResolution.InlineEndMargin(in styles[child], axis, direction, containingBlockSize)
                - StyleResolution.InlineEndPosition(in styles[child], axis, direction, containingBlockSize);

            results[child].Position[flexStart] = FlexAxis.InlineStartEdge(axis, direction) != FlexAxis.FlexStartEdge(axis)
                ? PositionOfOppositeEdge(relativeToInlineStart, axis, containingNode, child)
                : relativeToInlineStart;
            return;
        }

        // With no inset at all, it is placed where the parent's alignment would have put it.
        if (isMainAxis) {
            switch (styles[parent].JustifyContent) {
                case Justify.FlexEnd:
                    SetFlexEndLayoutPosition(parent, child, direction, axis, containingBlockWidth);
                    break;
                case Justify.Center:
                case Justify.SpaceAround:
                case Justify.SpaceEvenly:
                    SetCenterLayoutPosition(parent, child, direction, axis, containingBlockWidth);
                    break;
                default:
                    SetFlexStartLayoutPosition(parent, child, direction, axis, containingBlockWidth);
                    break;
            }

            return;
        }

        var itemAlign = ResolveChildAlignment(parent, child);
        if (styles[parent].FlexWrap == Wrap.WrapReverse) {
            itemAlign = itemAlign == Align.FlexEnd ? Align.FlexStart : itemAlign != Align.Center ? Align.FlexEnd : itemAlign;
        }

        switch (itemAlign) {
            case Align.FlexEnd:
                SetFlexEndLayoutPosition(parent, child, direction, axis, containingBlockWidth);
                break;
            case Align.Center:
                SetCenterLayoutPosition(parent, child, direction, axis, containingBlockWidth);
                break;
            default:
                SetFlexStartLayoutPosition(parent, child, direction, axis, containingBlockWidth);
                break;
        }
    }

    void SetFlexStartLayoutPosition(int parent, int child, Direction direction, FlexDirection axis, float containingBlockWidth) {
        var edge = (int) FlexAxis.FlexStartEdge(axis);
        results[child].Position[edge] =
            StyleResolution.FlexStartMargin(in styles[child], axis, direction, containingBlockWidth)
            + results[parent].Border[edge]
            + results[parent].Padding[edge];
    }

    void SetFlexEndLayoutPosition(int parent, int child, Direction direction, FlexDirection axis, float containingBlockWidth) {
        var endEdge = (int) FlexAxis.FlexEndEdge(axis);
        var flexEndPosition = results[parent].Border[endEdge]
            + results[parent].Padding[endEdge]
            + StyleResolution.FlexEndMargin(in styles[child], axis, direction, containingBlockWidth);

        results[child].Position[(int) FlexAxis.FlexStartEdge(axis)] =
            PositionOfOppositeEdge(flexEndPosition, axis, parent, child);
    }

    void SetCenterLayoutPosition(int parent, int child, Direction direction, FlexDirection axis, float containingBlockWidth) {
        var startEdge = (int) FlexAxis.FlexStartEdge(axis);
        var endEdge = (int) FlexAxis.FlexEndEdge(axis);
        var dimension = (int) FlexAxis.DimensionOf(axis);

        var parentContentBox = results[parent].MeasuredDimensions[dimension]
            - results[parent].Border[startEdge]
            - results[parent].Border[endEdge]
            - results[parent].Padding[startEdge]
            - results[parent].Padding[endEdge];

        var childOuterSize = results[child].MeasuredDimensions[dimension]
            + StyleResolution.MarginForAxis(in styles[child], axis, containingBlockWidth);

        results[child].Position[startEdge] = ((parentContentBox - childOuterSize) / 2f)
            + results[parent].Border[startEdge]
            + results[parent].Padding[startEdge]
            + StyleResolution.FlexStartMargin(in styles[child], axis, direction, containingBlockWidth);
    }

    bool BothInsetsDefined(int index, FlexDirection axis, Direction direction) =>
        StyleResolution.IsFlexStartPositionDefined(in styles[index], axis, direction)
        && StyleResolution.IsFlexEndPositionDefined(in styles[index], axis, direction)
        && !StyleResolution.IsFlexStartPositionAuto(in styles[index], axis, direction)
        && !StyleResolution.IsFlexEndPositionAuto(in styles[index], axis, direction);

    bool HorizontalInsetsDefined(int index) {
        ref readonly var position = ref styles[index].Position;
        return position[(int) Edge.Left].IsDefined
            || position[(int) Edge.Right].IsDefined
            || position[(int) Edge.Start].IsDefined
            || position[(int) Edge.End].IsDefined
            || position[(int) Edge.Horizontal].IsDefined
            || position[(int) Edge.All].IsDefined;
    }

    bool VerticalInsetsDefined(int index) {
        ref readonly var position = ref styles[index].Position;
        return position[(int) Edge.Top].IsDefined
            || position[(int) Edge.Bottom].IsDefined
            || position[(int) Edge.Vertical].IsDefined
            || position[(int) Edge.All].IsDefined;
    }
}
