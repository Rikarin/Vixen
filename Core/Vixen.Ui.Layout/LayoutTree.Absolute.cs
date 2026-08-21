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
    /// <summary>The rectangle an out-of-flow box resolves its insets, its size and its margins against.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Not always the containing node's padding box, and CSS Grid §9 is the reason this
    ///         is a rectangle rather than a pair of sizes.</b> For every other parent it <i>is</i> the
    ///         padding box, which is what the four fields say when they are filled in by
    ///         <see cref="LayoutTree.PaddingBoxOf" />: the offsets are the borders and the size is the
    ///         border box less them. For an out-of-flow child of a grid container whose placement
    ///         names at least one line, §9.2 makes it the child's <i>grid area</i> instead — a
    ///         rectangle somewhere inside the padding box that the container's own geometry cannot
    ///         reconstruct.
    ///     </para>
    ///     <para>
    ///         <paramref name="Left" /> and <paramref name="Top" /> are physical and measured from the
    ///         containing node's <i>border box</i> origin, which is the frame every
    ///         <see cref="LayoutResult.Position" /> in the tree is written in.
    ///     </para>
    /// </remarks>
    /// <param name="Left">The offset from the containing node's border-box left edge.</param>
    /// <param name="Top">The offset from the containing node's border-box top edge.</param>
    /// <param name="Width">The rectangle's width.</param>
    /// <param name="Height">The rectangle's height.</param>
    readonly record struct AbsoluteContainingBlock(float Left, float Top, float Width, float Height) {
        /// <summary>The rectangle's size along an axis.</summary>
        public float SizeOn(FlexDirection axis) => FlexAxis.IsRow(axis) ? Width : Height;

        /// <summary>The rectangle's near edge along an axis, physically.</summary>
        public float StartOn(FlexDirection axis) => FlexAxis.IsRow(axis) ? Left : Top;
    }

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
        var isGridParent = styles[currentNode].Display == Display.Grid;
        var gridEstablishesContainingBlock = isGridParent && currentNode == containingNode;

        for (var i = 0; i < children.Length; i++) {
            var child = children[i];
            if (styles[child].Display == Display.None) {
                continue;
            }

            if (styles[child].PositionType == PositionType.Absolute) {
                // ⚠ CSS Grid §9.2 against §9.3, and they disagree about which rectangle is which.
                // When the grid container IS the containing block, the child's grid area is both the
                // rectangle its insets resolve against and the one its alignment falls back to. When
                // the grid is merely a static ancestor the containing block belongs to somebody
                // further up, and all §9.3 gives the child is a static position inside the grid's
                // padding box — its grid-placement properties say nothing at all.
                var area = gridEstablishesContainingBlock ? GridAreaOf(containingNode, child)
                    : isGridParent ? PaddingBoxOf(currentNode)
                    : default;

                var containingBlock = gridEstablishesContainingBlock ? area : PaddingBoxOf(containingNode);

                LayoutAbsoluteChild(
                    containingNode,
                    currentNode,
                    child,
                    in containingBlock,
                    in area,
                    widthSizingMode,
                    currentNodeDirection,
                    currentDepth
                );

                hasNewLayout = hasNewLayout || (flags[child] & LayoutNodeState.HasNewLayout) != 0;

                // ⚠ The same two axes <see cref="LayoutAbsoluteChild" /> positioned against, and they
                // have to be derived the same way or the trailing pass below reads an edge that was
                // never written. A grid parent resolved as `row-reverse` here while the child was
                // placed on a physical `row` puts every RTL grid child at
                // `container − child − Position[Right]`, with a stale zero for the right edge.
                var isPhysicalParent = styles[currentNode].Display is Display.Block or Display.Grid;
                var parentMainAxis = isPhysicalParent
                    ? FlexDirection.Row
                    : FlexAxis.Resolve(styles[currentNode].FlexDirection, currentNodeDirection);
                var parentCrossAxis = isPhysicalParent
                    ? FlexDirection.Column
                    : FlexAxis.ResolveCross(parentMainAxis, currentNodeDirection);

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
        in AbsoluteContainingBlock containingBlock,
        in AbsoluteContainingBlock parentArea,
        SizingMode widthMode,
        Direction direction,
        int currentDepth
    ) {
        var containingBlockWidth = containingBlock.Width;
        var containingBlockHeight = containingBlock.Height;

        // ⚠ A block or grid container's `flex-direction` is meaningless and must not be consulted —
        // it is whatever the stylesheet happened to leave there, and the corpus leaves `row`, which
        // under RTL resolves to `row-reverse` and would send every un-inset absolute child to the
        // wrong physical edge. Both box types are physical: inline is the row, block is the column,
        // and the writing direction is applied by the `Inline*` helpers rather than by the axis.
        var isPhysicalParent = styles[node].Display is Display.Block or Display.Grid;
        var mainAxis = isPhysicalParent ? FlexDirection.Row : FlexAxis.Resolve(styles[node].FlexDirection, direction);
        var crossAxis = isPhysicalParent ? FlexDirection.Column : FlexAxis.ResolveCross(mainAxis, direction);
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
            childWidth = containingBlockWidth
                - (StyleResolution.FlexStartPosition(in styles[child], FlexDirection.Row, direction, containingBlockWidth)
                    + StyleResolution.FlexEndPosition(in styles[child], FlexDirection.Row, direction, containingBlockWidth));

            childWidth = BoundAxis(child, FlexDirection.Row, direction, childWidth, containingBlockWidth, containingBlockWidth);
        }

        if (HasDefiniteLength(child, Dimension.Height, containingBlockHeight)) {
            childHeight = ResolvedDimension(child, Dimension.Height, containingBlockHeight, containingBlockWidth, direction) + marginColumn;
        } else if (BothInsetsDefined(child, FlexDirection.Column, direction)) {
            childHeight = containingBlockHeight
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

        PositionAbsoluteChild(containingNode, node, child, direction, mainAxis, true, in containingBlock, in parentArea);
        PositionAbsoluteChild(containingNode, node, child, direction, crossAxis, false, in containingBlock, in parentArea);
    }

    void PositionAbsoluteChild(
        int containingNode,
        int parent,
        int child,
        Direction direction,
        FlexDirection axis,
        bool isMainAxis,
        in AbsoluteContainingBlock containingBlock,
        in AbsoluteContainingBlock parentArea
    ) {
        var containingBlockWidth = containingBlock.Width;
        var containingBlockSize = containingBlock.SizeOn(axis);
        var flexStart = (int) FlexAxis.FlexStartEdge(axis);

        // The start inset wins over the end inset when both are set. The result is written to the
        // flex-start edge either way, because that is the edge everything else in the algorithm
        // positions from.
        if (StyleResolution.IsInlineStartPositionDefined(in styles[child], axis, direction)
            && !StyleResolution.IsInlineStartPositionAuto(in styles[child], axis, direction)) {
            var relativeToInlineStart = StyleResolution.InlineStartPosition(in styles[child], axis, direction, containingBlockSize)
                + InlineStartOffsetOf(in containingBlock, containingNode, axis, direction)
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
                - InlineEndOffsetOf(in containingBlock, containingNode, axis, direction)
                - StyleResolution.InlineEndMargin(in styles[child], axis, direction, containingBlockSize)
                - StyleResolution.InlineEndPosition(in styles[child], axis, direction, containingBlockSize);

            results[child].Position[flexStart] = FlexAxis.InlineStartEdge(axis, direction) != FlexAxis.FlexStartEdge(axis)
                ? PositionOfOppositeEdge(relativeToInlineStart, axis, containingNode, child)
                : relativeToInlineStart;
            return;
        }

        // ⚠ A grid container aligns an un-inset out-of-flow child inside its GRID AREA, with
        // `justify-self` on the inline axis and `align-self` on the block one — not with the
        // `justify-content`/`align-items` pair the flex fallback below reaches for, and not against
        // the content box. CSS Grid §9.2: the area is the containing block, and its edges are the
        // named grid lines with every `auto` one standing for the container's padding edge. So a
        // grid with no template at all still aligns against its padding box rather than its content
        // box, which is what `grid_absolute_layout_within_border_static` measures.
        if (styles[parent].Display == Display.Grid) {
            var isRow = FlexAxis.IsRow(axis);
            var alignment = isRow
                ? styles[child].JustifySelf == Align.Auto ? styles[parent].JustifyItems : styles[child].JustifySelf
                : styles[child].AlignSelf == Align.Auto ? styles[parent].AlignItems : styles[child].AlignSelf;

            var areaSize = parentArea.SizeOn(axis);
            var childSize = results[child].MeasuredDimensions[(int) FlexAxis.DimensionOf(axis)];
            var startMargin = StyleResolution.InlineStartMargin(in styles[child], axis, direction, containingBlockWidth);
            var endMargin = StyleResolution.InlineEndMargin(in styles[child], axis, direction, containingBlockWidth);

            var free = MathF.Max(0f, areaSize - childSize - startMargin - endMargin);
            var offsetFromInlineStart = startMargin + alignment switch {
                Align.Center => free / 2f,
                Align.FlexEnd => free,
                _ => 0f
            };

            // The offset is measured from the area's inline-start edge, which in RTL is its right.
            results[child].Position[(int) (isRow ? Edge.Left : Edge.Top)] = isRow && direction == Direction.Rtl
                ? parentArea.Left + areaSize - offsetFromInlineStart - childSize
                : parentArea.StartOn(axis) + offsetFromInlineStart;

            return;
        }

        // ⚠ A block container has no alignment to fall back on, so it falls back on flow order.
        // CSS 2.1 §10.6.4: the static position of an out-of-flow box is where its hypothetical box
        // would have been — after every in-flow sibling before it. `WalkBlockChildren` records that
        // as it goes, because by the time this pass runs the walk is over and the cursor is gone.
        if (styles[parent].Display == Display.Block) {
            var isRow = FlexAxis.IsRow(axis);
            var staticEdge = isRow ? Edge.Left : Edge.Top;
            var staticValue = isRow ? results[child].BlockStaticLeft : results[child].BlockStaticTop;

            // ⚠ The margin is the inline-*start* one in both directions, and the RTL case is the one
            // that looks wrong: the box is placed in from the container's right edge, so what
            // separates it from that edge is its `margin-right` — which is what `InlineStartMargin`
            // returns under RTL. Reading the end margin instead swaps `block_absolute_child_with_margin_x`'s
            // first two children, each landing where the other belongs.
            var margin = StyleResolution.InlineStartMargin(in styles[child], axis, direction, containingBlockWidth);

            if (isRow && direction == Direction.Rtl) {
                staticValue -= results[child].MeasuredDimensions[(int) Dimension.Width] + margin;
            } else {
                staticValue += margin;
            }

            results[child].Position[(int) staticEdge] = staticValue;
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

    /// <summary>A node's padding box, which is the containing block of everything below it.</summary>
    AbsoluteContainingBlock PaddingBoxOf(int node) {
        var left = StyleResolution.FlexStartBorder(in styles[node], FlexDirection.Row, Direction.Ltr);
        var right = StyleResolution.FlexEndBorder(in styles[node], FlexDirection.Row, Direction.Ltr);
        var top = StyleResolution.FlexStartBorder(in styles[node], FlexDirection.Column, Direction.Ltr);
        var bottom = StyleResolution.FlexEndBorder(in styles[node], FlexDirection.Column, Direction.Ltr);

        return new AbsoluteContainingBlock(
            left,
            top,
            results[node].MeasuredDimensions[(int) Dimension.Width] - left - right,
            results[node].MeasuredDimensions[(int) Dimension.Height] - top - bottom
        );
    }

    /// <summary>The grid area <see cref="RecordAbsoluteGridAreas" /> recorded for one out-of-flow child.</summary>
    /// <remarks>
    ///     Falls back to the padding box rather than to a NaN rectangle, because a grid whose layout
    ///     pass never reached §11 — a measurement rather than a layout — leaves nothing behind, and
    ///     the padding box is what §9.2 says an all-<c>auto</c> placement resolves to anyway.
    /// </remarks>
    AbsoluteContainingBlock GridAreaOf(int containingNode, int child) =>
        float.IsNaN(results[child].GridAreaWidth)
            ? PaddingBoxOf(containingNode)
            : new AbsoluteContainingBlock(
                results[child].GridAreaLeft,
                results[child].GridAreaTop,
                results[child].GridAreaWidth,
                results[child].GridAreaHeight
            );

    /// <summary>
    ///     How far the containing block's inline-start edge sits from the containing node's, along one
    ///     axis. The containing node's border, for everything but a grid area.
    /// </summary>
    float InlineStartOffsetOf(in AbsoluteContainingBlock block, int containingNode, FlexDirection axis, Direction direction) =>
        FlexAxis.InlineStartEdge(axis, direction) switch {
            Edge.Left => block.Left,
            Edge.Top => block.Top,
            _ => results[containingNode].MeasuredDimensions[(int) FlexAxis.DimensionOf(axis)]
                - block.StartOn(axis)
                - block.SizeOn(axis)
        };

    /// <inheritdoc cref="InlineStartOffsetOf" />
    float InlineEndOffsetOf(in AbsoluteContainingBlock block, int containingNode, FlexDirection axis, Direction direction) =>
        results[containingNode].MeasuredDimensions[(int) FlexAxis.DimensionOf(axis)]
        - block.SizeOn(axis)
        - InlineStartOffsetOf(in block, containingNode, axis, direction);

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
