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

        // Whether any float path runs at all this pass. One scan, before the first box is touched:
        // see LayoutTree.Floats.cs for why the question has to be asked of the tree and not of a
        // container's own children.
        RefreshFloatPresence();

        // Likewise for CSS Sizing § 5's content keywords, and likewise before the first box is
        // touched — the answer to `width: max-content` is a measurement, and a parent settles a
        // child's width before it ever hands it down. See LayoutTree.Intrinsic.cs.
        RefreshContentSizePresence();

        floatExclusions.Clear();
        floatScopeStart = 0;
        floatOriginX = 0f;
        floatOriginY = 0f;
        floatContextWidth = 0f;

        generation++;

        // ⚠ <b>`display: none` generates no box, and that is true of the box the caller handed us
        // as well.</b> Every container in this store already skips a `display: none` CHILD and
        // blanks its subtree; the root was the one box nobody was skipping, so a hidden tree still
        // reported the size its style asked for. `display_none_only_node` is a 100×100 div with
        // `display: none` and Chrome answers 0×0. Yoga lays it out regardless, which is where this
        // behaviour came from — but Yoga's own corpus never asks, and no Yoga fixture moves.
        if (styles[index].Display == Display.None) {
            ZeroOutLayoutRecursively(index);
            return;
        }

        // ⚠ The `finally` is not defensive tidying. A content keyword is resolved by rewriting the
        // node's own style with the number that was measured for it, and the caller has to get its
        // declarations back — a measure function that throws must not leave `GetStyle` answering
        // with a width nobody set. See LayoutTree.Intrinsic.cs.
        try {
            if (treeHasContentSizes) {
                ResolveContentSizes(index, ownerWidth, ownerHeight, ownerDirection);
            }

            LayoutResolvedTree(index, ownerWidth, ownerHeight, ownerDirection);
        } finally {
            RestoreContentBasedLengths();
        }
    }

    /// <summary>The root's own sizing decision, and the pass it starts.</summary>
    /// <remarks>
    ///     Split out of <see cref="CalculateLayout" /> only so that the content-keyword resolution
    ///     can run between the tree's guards and the first read of the root's own width without
    ///     putting a hundred lines inside a <c>try</c>.
    /// </remarks>
    void LayoutResolvedTree(int index, float ownerWidth, float ownerHeight, Direction ownerDirection) {
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

        // ⚠ <b>A cache hit does not place floats, and a float that is not placed is not merely slow —
        // it is absent from the exclusion list every later box reads.</b> The cache answers with a
        // node's SIZE; a block container's layout also has the side effect of appending its floats to
        // the formatting context around it, and replaying six numbers cannot replay that. The same
        // node is also legitimately laid out at two different float origins in one pass — the probe
        // and the real pass in `WalkBlockChildren` are exactly that — so the entry is not even keyed
        // on something that distinguishes them. Bypassing is the honest fix; a float-bearing tree pays
        // for it, and `treeHasFloats` is what keeps every other tree from doing so.
        if (treeHasFloats) {
            cached = -1;
            cachedIsLayout = false;
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

        // ⚠ <b>Size containment, and it is deliberately NOT a branch that skips the subtree.</b> CSS
        // Containment § 3.2 says the box is sized as if it had no contents; it goes on laying them
        // out, painting them and hit-testing them. So the intervention is to settle the contained
        // axes here — `MeasureNodeWithoutChildren` IS "as if it had no contents", padding and border
        // and whatever the styles fix — and then to re-enter the ordinary algorithm below with those
        // axes pinned. Every path underneath already answers a `StretchFit` request with the size it
        // was offered rather than with one its content chose, the measure-function leaf included, so
        // the children run against a box they cannot move.
        //
        // ⚠ And the intrinsic half comes with it rather than needing a second rule.
        // `ProbeContentSize` asks a `min-content` or `max-content` keyword through this very
        // function, and `MeasureNodeWithoutChildren` answers a `MaxContent` request with the padding
        // box — so a content keyword on a contained box resolves to zero content without
        // `LayoutTree.Intrinsic` knowing the property exists.
        var containment = styles[index].Containment;
        if ((containment & (Containment.Size | Containment.InlineSize)) != 0) {
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

            availableWidth = results[index].MeasuredDimensions[(int) Dimension.Width] + marginAxisRow;
            widthSizingMode = SizingMode.StretchFit;

            // ⚠ Only `size` takes the block axis. `inline-size` leaves the height to the contents,
            // laid out at the width containment just fixed — which is the whole of what makes it a
            // separate keyword rather than a shorthand.
            if ((containment & Containment.Size) != 0) {
                availableHeight = results[index].MeasuredDimensions[(int) Dimension.Height] + marginAxisColumn;
                heightSizingMode = SizingMode.StretchFit;
            }
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
        //
        // ⚠ `flow-root` adds nothing to the dispatch for the same reason: its inner display is flow,
        // so it runs the very same algorithm as `block`. Everything the keyword means is one answer
        // from `EstablishesBlockFormattingContext`, which the block path asks on its own way in.
        if (styles[index].Display is Display.Block or Display.InlineBlock or Display.Inline or Display.FlowRoot) {
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

        var contentInsetAxisMain = StyleResolution.ContentInsetForAxis(in styles[index], mainAxis, direction, ownerWidth);
        var contentInsetAxisCross = StyleResolution.ContentInsetForAxis(in styles[index], crossAxis, direction, ownerWidth);
        var leadingContentInsetCross = StyleResolution.FlexStartContentInset(in styles[index], crossAxis, direction, ownerWidth);

        var sizingModeMainDim = isMainAxisRow ? widthSizingMode : heightSizingMode;
        var sizingModeCrossDim = isMainAxisRow ? heightSizingMode : widthSizingMode;

        var contentInsetAxisRow = isMainAxisRow ? contentInsetAxisMain : contentInsetAxisCross;
        var contentInsetAxisColumn = isMainAxisRow ? contentInsetAxisCross : contentInsetAxisMain;

        // STEP 2: DETERMINE AVAILABLE SIZE IN MAIN AND CROSS DIRECTIONS
        var availableInnerWidth = AvailableInnerDimension(
            index,
            direction,
            Dimension.Width,
            availableWidth - marginAxisRow,
            contentInsetAxisRow,
            ownerWidth,
            ownerWidth
        );

        var availableInnerHeight = AvailableInnerDimension(
            index,
            direction,
            Dimension.Height,
            availableHeight - marginAxisColumn,
            contentInsetAxisColumn,
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
                ComputeAutoMinMainSize(child, mainAxis, direction, mainAxisOwnerSize, availableInnerWidth, availableInnerHeight, currentDepth);

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

        // ⚠ <b>A percentage main-axis gutter on a content-sized box is cyclic, and the box's size is
        // decided WITHOUT it.</b> Stays NaN unless one is found; see the cyclic-gutter note below.
        var contentMainDimBeforeCyclicGap = float.NaN;

        while (startOfLine < childCount) {
            // What CalculateFlexLine is about to resolve this line's gutter against. Under a
            // content-based main size it is NaN, which is what makes a percentage gutter zero.
            var gapBasis = availableInnerMainDim;

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
                var minInnerWidth = StyleResolution.ResolvedMinDimension(in styles[index], Dimension.Width, ownerWidth, ownerWidth, direction) - contentInsetAxisRow;
                var maxInnerWidth = StyleResolution.ResolvedMaxDimension(in styles[index], Dimension.Width, ownerWidth, ownerWidth, direction) - contentInsetAxisRow;
                var minInnerHeight = StyleResolution.ResolvedMinDimension(in styles[index], Dimension.Height, ownerHeight, ownerWidth, direction) - contentInsetAxisColumn;
                var maxInnerHeight = StyleResolution.ResolvedMaxDimension(in styles[index], Dimension.Height, ownerHeight, ownerWidth, direction) - contentInsetAxisColumn;

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

            // ⚠ <b>The container has just been sized from its items' §9.9.1 CONTRIBUTIONS, and that
            // makes its main size definite — so §9.2's declared flex BASES become owed.</b> The two
            // are one field until here; see AdoptDeclaredFlexBases for why they have to move
            // together and for why a percentage is excluded.
            if (sizeBasedOnContent
                && float.IsNaN(gapBasis)
                && !float.IsNaN(availableInnerMainDim)
                && AdoptDeclaredFlexBases(index, ref line, direction, mainAxis, ownerWidth, mainAxisOwnerSize)) {
                sizeBasedOnContent = false;
            }

            // ── The cyclic percentage gutter ────────────────────────────────────────────────────
            // ⚠ <b>A percentage main-axis gap on a box whose main size comes from its content
            // depends on the size it helps decide, and CSS Sizing §5.2.1 breaks the cycle by
            // resolving it as ZERO for the purpose of deciding that size.</b> CalculateFlexLine
            // already did exactly that, by accident of having been handed NaN — so the box's
            // content size, which the branch above has just taken out of `line.SizeConsumed`, is a
            // gapless one and is the right answer. What was wrong is everything after it: the
            // gutter became resolvable the moment that size existed, JustifyMainAxis re-resolved it
            // and laid the items out with it, and `line.MainDim` — the box's own measured size —
            // GREW to hold a gutter the box had already been sized without.
            //
            // `column-gap: 20%` on a shrink-to-fit row of three 20-point items is Chrome's clean
            // reading: the box is 60, the gutter is 20% of 60 = 12, and the line therefore needs 84
            // in a 60-point box. The items shrink into it and come out 12 apiece. Growing the box to
            // 84 instead makes the gutter self-fulfilling and nothing ever shrinks, which is what
            // this store did for all three of the cyclic fixtures.
            //
            // So: the gutter is charged to the LINE, which is what §9.7 distributes over, and not to
            // the BOX, whose size was already decided. The two are different numbers whenever the
            // items cannot absorb the difference — `_unshrinkable` is 60 wide with its last item
            // ending at 84 — and that overflow is what Chrome draws.
            var cyclicGap = StyleResolution.GapForAxis(in styles[index], mainAxis, availableInnerMainDim)
                - StyleResolution.GapForAxis(in styles[index], mainAxis, gapBasis);
            var cyclicGutter = cyclicGap * int.Max(0, line.ItemCount - 1);

            if (cyclicGutter > 0f) {
                var gapless = availableInnerMainDim + contentInsetAxisMain;
                contentMainDimBeforeCyclicGap = float.IsNaN(contentMainDimBeforeCyclicGap)
                    ? gapless
                    : MathF.Max(contentMainDimBeforeCyclicGap, gapless);

                // ⚠ NOT `SizeConsumed`: that field is documented as what the container's own
                // content-based main size is made of, and the whole rule here is that the gutter is
                // not part of it. The pool §9.7 divides is built from MarginAndGapConsumed, and
                // step 1 picks its direction from the hypothetical sum, so those are the two.
                line.MarginAndGapConsumed += cyclicGutter;
                line.HypotheticalSizeConsumed += cyclicGutter;
                sizeBasedOnContent = false;
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
                        line.CrossDim + contentInsetAxisCross,
                        crossAxisOwnerSize,
                        ownerWidth
                    )
                    - contentInsetAxisCross;
            }

            if (!isNodeFlexWrap && sizingModeCrossDim == SizingMode.StretchFit) {
                line.CrossDim = availableInnerCrossDim;
            }

            if (!isNodeFlexWrap) {
                line.CrossDim = BoundAxis(
                        index,
                        crossAxis,
                        direction,
                        line.CrossDim + contentInsetAxisCross,
                        crossAxisOwnerSize,
                        ownerWidth
                    )
                    - contentInsetAxisCross;
            }

            // STEP 7: CROSS-AXIS ALIGNMENT
            if (performLayout) {
                var children = ChildIds(index);
                for (var i = line.StartChild; i < line.EndChild; i++) {
                    var child = children[i];
                    if (!IsInFlow(child)) {
                        continue;
                    }

                    var leadingCrossDim = leadingContentInsetCross;
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

                        // §4.4: the free space is finally known here, so this is where a `safe`
                        // alignment gets to change its mind. `flex_safe_align_self_end_overflow` is a
                        // 150-point item in a 100-point container: `unsafe end` puts its top at −50
                        // and `safe end` at 0.
                        alignItem = SafeFallback(alignItem, ResolveChildAlignmentOverflow(index, child), remainingCrossDim);

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
                contentInsetAxisCross,
                leadingContentInsetCross,
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
            // ⚠ The line may have been laid out wider than the box that was sized to hold it, and
            // when a cyclic percentage gutter is what widened it that is not the box's size — see
            // the cyclic-gutter note in STEP 5. `maxLineMainDim` is where the box would otherwise
            // grow to hold a gutter that only exists because the box is that size.
            var measuredMainDim = float.IsNaN(contentMainDimBeforeCyclicGap)
                ? maxLineMainDim
                : contentMainDimBeforeCyclicGap;

            SetMeasuredDimension(index, mainAxis, direction, measuredMainDim, mainAxisOwnerSize, ownerWidth);
        } else if (sizingModeMainDim == SizingMode.FitContent && mainOverflow == Overflow.Scroll) {
            var scrolledMain = MathF.Max(
                MathF.Min(
                    availableInnerMainDim + contentInsetAxisMain,
                    BoundAxisWithinMinAndMax(index, direction, mainAxis, maxLineMainDim, mainAxisOwnerSize, ownerWidth)
                ),
                StyleResolution.PaddingAndBorderForAxis(in styles[index], mainAxis, direction, ownerWidth)
            );

            SetMeasuredDimension(index, FlexAxis.DimensionOf(mainAxis), scrolledMain, scrolledMain);
        }

        if (sizingModeCrossDim == SizingMode.MaxContent
            || (crossOverflow != Overflow.Scroll && sizingModeCrossDim == SizingMode.FitContent)) {
            SetMeasuredDimension(
                index,
                crossAxis,
                direction,
                totalLineCrossDim + contentInsetAxisCross,
                crossAxisOwnerSize,
                ownerWidth
            );
        } else if (sizingModeCrossDim == SizingMode.FitContent && crossOverflow == Overflow.Scroll) {
            var scrolledCross = MathF.Max(
                MathF.Min(
                    availableInnerCrossDim + contentInsetAxisCross,
                    BoundAxisWithinMinAndMax(
                        index,
                        direction,
                        crossAxis,
                        totalLineCrossDim + contentInsetAxisCross,
                        crossAxisOwnerSize,
                        ownerWidth
                    )
                ),
                StyleResolution.PaddingAndBorderForAxis(in styles[index], crossAxis, direction, ownerWidth)
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
        if (EstablishesAbsoluteContainingBlock(index) || currentDepth == 1) {
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
