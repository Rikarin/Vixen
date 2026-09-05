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
        // ⚠ The gutter belongs on BOTH sides of this function and for two different reasons: it is
        // room the content does not get to measure into, and it is room the border box has to carry
        // afterwards. `leaf_overflow_scrollbars_take_up_space_both_axis` measures 20x10 of Ahem text
        // and expects 35x25.
        var insetRow = layout.Padding[(int) Edge.Left] + layout.Padding[(int) Edge.Right]
            + layout.Border[(int) Edge.Left] + layout.Border[(int) Edge.Right]
            + StyleResolution.ScrollbarGutterForAxis(in styles[index], FlexDirection.Row);
        var insetColumn = layout.Padding[(int) Edge.Top] + layout.Padding[(int) Edge.Bottom]
            + layout.Border[(int) Edge.Top] + layout.Border[(int) Edge.Bottom]
            + StyleResolution.ScrollbarGutterForAxis(in styles[index], FlexDirection.Column);

        var innerWidth = float.IsNaN(availableWidth) ? availableWidth : MathF.Max(0f, availableWidth - insetRow);
        var innerHeight = float.IsNaN(availableHeight) ? availableHeight : MathF.Max(0f, availableHeight - insetColumn);

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
                ? measured.Width + insetRow
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
                ? measured.Height + insetColumn
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
            // ⚠ Including the gutter, which is what makes an EMPTY scroll container as wide as its
            // own scrollbar. `leaf_overflow_scrollbars_overridden_by_max_size` needs it: the box has
            // no content and no width, so 15 is the only number `max-width: 2px` has to clamp.
            width = layout.Padding[(int) Edge.Left] + layout.Padding[(int) Edge.Right]
                + layout.Border[(int) Edge.Left] + layout.Border[(int) Edge.Right]
                + StyleResolution.ScrollbarGutterForAxis(in styles[index], FlexDirection.Row);
        }

        SetMeasuredDimension(index, FlexDirection.Row, direction, width, ownerWidth, ownerWidth, widthSizingMode == SizingMode.StretchFit);

        var height = availableHeight;
        if (heightSizingMode is SizingMode.MaxContent or SizingMode.FitContent) {
            height = layout.Padding[(int) Edge.Top] + layout.Padding[(int) Edge.Bottom]
                + layout.Border[(int) Edge.Top] + layout.Border[(int) Edge.Bottom]
                + StyleResolution.ScrollbarGutterForAxis(in styles[index], FlexDirection.Column);
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

        var processedFlexBasis = StyleResolution.ProcessedFlexBasis(in styles[child]);
        var resolvedFlexBasis = StyleResolution.WithBoxSizing(
            in styles[child],
            processedFlexBasis.Resolve(mainAxisOwnerSize),
            FlexAxis.DimensionOf(mainAxis),
            ownerWidth,
            direction
        );

        var isRowStyleDimDefined = HasDefiniteLength(child, Dimension.Width, ownerWidth);
        var isColumnStyleDimDefined = HasDefiniteLength(child, Dimension.Height, ownerHeight);

        // Whether the basis about to be written was read off a declaration or measured. See
        // LayoutResult.FlexBasisFromContent — it is what caps §4.5's automatic minimum.
        results[child].FlexBasisFromContent = false;

        // ⚠ <b>An indefinite main size means two different things on the two axes, and
        // `flex_basis_unconstraint_row` and `flex_basis_unconstraint_column` are the same
        // declaration answered two different ways because of it.</b> Both are `flex-basis: 50px` on
        // the only child of a container with no main size of its own. Chrome makes the column 50
        // tall and the row 0 wide.
        //
        // The row is being sized under a max-content constraint, so §9.9.3's CONTRIBUTION rules
        // decide it: an item's max-content contribution is its own outer max-content size — zero,
        // for an empty box — clamped from ABOVE by its flex base size when the item is not growable.
        // Not floored by it. `flex-basis: 50px` is a ceiling there, not a size, and falling through
        // to the content branch below is how this store arrives at Chrome's zero.
        //
        // The column has no such rule to apply. §9.9 is about a flex container's INTRINSIC main
        // size, which is a question about the inline axis; a column container with an indefinite
        // height simply runs §9.3–§9.7 with indefinite free space, every item freezes at its
        // hypothetical main size, and the container is their sum. So the declaration IS the size,
        // and `mainAxisSize` being NaN is not a reason to go and measure an empty box instead.
        //
        // ⚠ Requiring a definite `mainAxisSize` on BOTH axes is Yoga's guard, ported with the rest
        // of the order of operations, and it is right for exactly one of them. Relaxing it for both
        // measures as +12 and −6: it closes this fixture, `taffy_issue_696_min_height` and
        // `absolute_child_with_max_height_larger_shrinkable_grandchild`, and it opens
        // `flex_basis_unconstraint_row` and both content-box halves of
        // `padding_border_overrides_size_flex_basis_0_growable`, whose `flex-basis: 0px` row items
        // stop being measured and start being believed. The regression is the rule.
        //
        // ⚠ The percentage half of the guard stays on both axes, and it is the half that was always
        // load-bearing: §5.2.1 makes a percentage against an indefinite main size behave as
        // `content`. It is belt and braces here — `Resolve(NaN)` is already NaN — but the two are
        // different reasons and only one of them is being relaxed.
        // ⚠ <b>WRITTEN UNCONDITIONALLY, and the `if (isUndefined(computedFlexBasis))` that used to
        // stand here was Yoga's memo and is a trap the moment one node can be asked twice in a
        // pass.</b> The branch below it overwrites the basis every time; only this one declined to,
        // so the two disagreed about whether the field is a cache. Which branch a child takes is not
        // a function of the child — it is a function of whether the CONTAINER's main size is
        // definite, and the paragraph above is why: under a max-content constraint a declared
        // `flex-basis: 0px` deliberately falls through to the content branch. So a probe that sizes
        // the container at max-content leaves the child holding a measured basis, and the real pass
        // that follows — where the declaration should win — kept the probe's number.
        //
        // `KeyValueListTests.A_list_in_a_row_does_not_collapse` is the fixture: two `flex-basis: 0`
        // halves that must split their row evenly came out 88 and 53, which are their max-content
        // widths. ⚠ It needed §9.2 step 3E's max-content pass to become reachable — that is what
        // asks a container for its max-content size mid-layout — so neither the memo nor the
        // fall-through was wrong on its own.
        if (!float.IsNaN(resolvedFlexBasis)
            && (!float.IsNaN(mainAxisSize) || (!isMainAxisRow && processedFlexBasis.Unit != LayoutUnit.Percent))) {
            var paddingAndBorder = StyleResolution.PaddingAndBorderForAxis(in styles[child], mainAxis, direction, ownerWidth);
            results[child].ComputedFlexBasis = MathF.Max(resolvedFlexBasis, paddingAndBorder);
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

            // ⚠ …and the minimum, which §5.1 clamps by just as hard. See ConstrainMinSizeForMode:
            // the other axis is measured off this one, so both of the item's own clamps have to be
            // applied to the offer before the measurement rather than to the answer after it. After
            // the maxima on purpose — §5.1 orders the two clamps max-then-min, so a minimum above a
            // maximum wins, which is the rule BoundAxisWithinMinAndMax already implements.
            ConstrainMinSizeForMode(child, direction, FlexDirection.Row, ownerWidth, ownerWidth, childWidthSizingMode, ref childWidth);
            ConstrainMinSizeForMode(child, direction, FlexDirection.Column, ownerHeight, ownerWidth, childHeightSizingMode, ref childHeight);

            // ⚠ <b>§9.2 step 3E's flex base is the item's MAX-CONTENT size, and the offer above is
            // not a max-content constraint — it is the container's available space.</b> The two
            // agree for everything that fits and part company for everything that does not, which is
            // exactly the population §9.7 then has to shrink: an item measured at the offer reports
            // a base equal to the room it was given, so the pool it is shrunk out of was computed
            // from the room rather than from the content, and every item on the line gets the wrong
            // share. `measure_child_with_flex_shrink_hidden` is 500 points of text and a 50-point box
            // in a 100-point row — bases 500 and 50 shrink to Chrome's 91 and 9, and a base of 100
            // for the text shrinks them to 67 and 33.
            //
            // ⚠ <b>The offer is kept for the measurement itself, and that is not a compromise.</b>
            // The clause is what makes text wrap to the width it is about to be given, so the item's
            // CROSS size is a function of it — dropping it to measure at max-content would report
            // one line's height for a paragraph and is load-bearing for the whole of Yoga's suite.
            // So the max-content pass runs FIRST and only its main-axis answer is kept; the real
            // offer measures second and is what every other consumer reads.
            //
            // Only a content-sized item on an offered main axis pays the extra pass: an item with a
            // declared basis or a declared main size never reaches this branch, and one whose offer
            // is a stretch or a max-content constraint already answers the question being asked.
            //
            // ⚠ <b>AND ONLY AN ITEM THAT CAN SHRINK, WHICH IS A WORKAROUND AND NOT THE RULE.</b>
            // §9.2 asks this of every item; the reason it could not be asked of every item HERE was
            // that `flex-shrink`'s initial value in this store is Yoga's 0 and not CSS's 1 — see
            // `LayoutStyle.Default` and `TaffyStyleMap.ApplyCssInitialValues`, which resets it per
            // node precisely because the corpus is Chrome's. An item given its true max-content base
            // and no way to shrink back simply overflows.
            //
            // ⚠ <b>#628 HAS SINCE MADE `LayoutStyleBuilder.CssInitial` WRITE A SHRINK OF 1, so this
            // gate no longer excludes anything a `.vcss` document builds — and "the day the initial
            // value is CSS's, this clause comes out and nothing moves" is REFUTED.</b> Six tests
            // across two projects went red the moment the two met, and none of them was this gate's
            // stated case of an item overflowing with no way back. They were three separate
            // invariants that only a second, unconstrained pass can break, each fixed where it
            // lives and each with its own note: the measurement cache handing back a CLAMPED
            // unconstrained answer for a stricter question (`CanUseCachedMeasurement`), the declared
            // flex basis being memoised by a branch that would not overwrite it
            // (`ComputeFlexBasisForChild`, above), and §4.5's cap reading this max-content basis
            // where it meant the offer-measured one (`ComputeAutoMinMainSize`). The gate stays as
            // written because it is now inert rather than because it is still load-bearing.
            //
            // ⚠ The seventh was not repairable here and is a real behaviour change: a `ScrollView`
            // with no width filled its flex row only while its base was the width it was OFFERED,
            // and its max-content width is the scrollbar. That is CSS's answer and Chrome's; what it
            // means is that a control expected to fill has to say so. `Rikarin/Vixen#682` was the
            // remaining §9.2 step 3E question — an intrinsic-minimum stage that reads
            // `overflow-wrap` — and this belongs beside it. ⚠ That stage exists now, and it is NOT
            // in this file: a box's min-content size here is its measurement at an available width
            // of zero, so `overflow-wrap: anywhere` is read by the measurer rather than by the
            // layout. See `TextWrapMode.BreakWord`. What is still owed under #682 is the §4.5 cap in
            // `ComputeAutoMinMainSize` — "the smaller of two measurements" — which was put there to
            // hold the floor down while no such stage existed.
            var mainSizingMode = isMainAxisRow ? childWidthSizingMode : childHeightSizingMode;
            var contentBase = float.NaN;

            if (mainSizingMode == SizingMode.FitContent
                && StyleResolution.ResolveFlexShrink(in styles[child], links[child].Parent < 0) != 0f) {
                CalculateLayoutInternal(
                    child,
                    isMainAxisRow ? float.NaN : childWidth,
                    isMainAxisRow ? childHeight : float.NaN,
                    direction,
                    isMainAxisRow ? SizingMode.MaxContent : childWidthSizingMode,
                    isMainAxisRow ? childHeightSizingMode : SizingMode.MaxContent,
                    ownerWidth,
                    ownerHeight,
                    performLayout: false,
                    currentDepth
                );

                contentBase = results[child].UnclampedMeasuredDimensions[(int) FlexAxis.DimensionOf(mainAxis)];
            }

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
                float.IsNaN(contentBase)
                    ? results[child].UnclampedMeasuredDimensions[(int) FlexAxis.DimensionOf(mainAxis)]
                    : contentBase,
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
    /// <remarks>
    ///     ⚠ <b>Every item in this pass is distributed from the SAME pool and the SAME factor sum,
    ///     and the freezing happens after the loop rather than inside it.</b> §9.7 step 4 hands out
    ///     step 4b's remaining free space to all the unfrozen items at once (4c), clamps them all
    ///     (4d), and only then freezes the violators and starts a new iteration (4e) — so an item
    ///     frozen part-way through cannot change what its later siblings were offered in the
    ///     iteration that froze it. Taking the factor out of the divisor as each violation was found,
    ///     while still dividing the full pool the freeze has not yet repaid, inflated every later
    ///     item's share and pushed items past bounds they do not really violate.
    ///     <para>
    ///         ⚠ <b>The window that exposed it is bounded on both sides</b>, which is why no fixture
    ///         in any of the corpora saw it. Two `flex-basis: 60; flex-shrink: 1` siblings with a
    ///         `min-width: 20` on the first only, in a 30-point row: the first violates and is
    ///         frozen, the second is then measured against half the divisor, shoots past its own zero
    ///         floor and is frozen too — and with BOTH clamps charged to it the pool handed back goes
    ///         POSITIVE. The second pass reads a positive pool, takes the grow branch, finds
    ///         `flex-grow: 0` and returns both items their unshrunk 60-point bases. Above 40 nothing
    ///         violates and below 20 every item is out of the divisor, so only the middle was wrong.
    ///     </para>
    /// </remarks>
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

        // The divisor this iteration distributes by. The reductions the freezes owe it are collected
        // and applied once the loop is over, so no item is sized against a sum a sibling shrank.
        var growDivisor = line.TotalFlexGrowFactors;
        var shrinkDivisor = line.TotalFlexShrinkScaledFactors;
        var frozenGrowFactors = 0f;
        var frozenShrinkScaledFactors = 0f;

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

                var baseMainSize = childFlexBasis + (line.RemainingFreeSpace / shrinkDivisor * shrinkScaled);
                var boundMainSize = BoundAxisWithAutoMin(child, mainAxis, direction, baseMainSize, availableInnerMainDim, availableInnerWidth);

                if (!float.IsNaN(baseMainSize) && !float.IsNaN(boundMainSize) && baseMainSize != boundMainSize) {
                    // Excluding this item from the pool makes its constraint trigger again in the
                    // second pass, so the two passes agree on its size.
                    deltaFreeSpace += boundMainSize - childFlexBasis;
                    frozenShrinkScaledFactors +=
                        -StyleResolution.ResolveFlexShrink(in styles[child], isRoot) * results[child].ComputedFlexBasis;
                }
            } else if (line.RemainingFreeSpace > 0f) {
                var growFactor = StyleResolution.ResolveFlexGrow(in styles[child], isRoot);
                if (float.IsNaN(growFactor) || growFactor == 0f) {
                    continue;
                }

                var baseMainSize = childFlexBasis + (line.RemainingFreeSpace / growDivisor * growFactor);

                // ⚠ <b>§9.7 step 4b clamps by the USED minimum, and for a flex item `min-width: auto`
                // resolves to §4.5's automatic one — when GROWING as well as when shrinking.</b> The
                // two branches of this pass had drifted apart: the shrink half already asked
                // BoundAxisWithAutoMin, the grow half asked BoundAxis and saw stated bounds only. So
                // a growing item whose content floors it above its share never registered as a
                // MIN VIOLATION, was never frozen by step 4c, and the space its floor consumed was
                // never taken back off the pool — the second pass then applied the floor to the item
                // anyway, and the line overflowed by exactly the difference. The clamp was being
                // charged to the item and not to the pool, which is the same mistake InitialFreeSpace
                // was written to fix from the other side.
                //
                // `flex_basis_smaller_then_content_with_flex_grow_large_size` is the arithmetic in
                // four numbers: two `flex-grow: 1; flex-basis: 0` items in a 100-point row, wrapping
                // a 70-point box and a 20-point one. The pool splits 50/50, the first violates its
                // 70-point floor, and Chrome freezes it there and gives the whole remaining 30 to its
                // sibling. Detecting the violation is what makes the second item 30 rather than 50.
                var boundMainSize = BoundAxisWithAutoMin(child, mainAxis, direction, baseMainSize, availableInnerMainDim, availableInnerWidth);

                if (!float.IsNaN(baseMainSize) && !float.IsNaN(boundMainSize) && baseMainSize != boundMainSize) {
                    deltaFreeSpace += boundMainSize - childFlexBasis;
                    frozenGrowFactors += growFactor;
                }
            }
        }

        line.TotalFlexGrowFactors -= frozenGrowFactors;
        line.TotalFlexShrinkScaledFactors -= frozenShrinkScaledFactors;
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
                // ⚠ <b>An item in a MULTI-LINE container is stretched to its LINE, and its line has
                // not been measured yet.</b> §9.4 step 7 is the rule that hands a stretched item the
                // container's inner cross size, and it says "if the flex container is single-line" —
                // step 8 sizes a multi-line container's lines from the largest HYPOTHETICAL cross
                // size on each, which is the unstretched one. Stretching here instead answers the
                // question the line is about to ask with the number the line was supposed to
                // produce. This used to read `!(isNodeFlexWrap && mainAxisOverflows)`, which is
                // Yoga's guard and only declines to stretch when the line already overflowed.
                //
                // `align_content_stretch_row_wrap` is one line in a `flex-wrap: wrap` box, which is
                // multi-line whether or not anything wrapped: a 150-point-tall grandchild in a
                // 100-point container. Chrome's line is 150 and the item overflows; pre-stretching
                // measured the item at the container's 100, the line took 100 from it, and
                // `align-content: stretch` then had nothing to stretch. The item is stretched to the
                // line in LayoutTree.Align, which is where it always belonged.
                && !isNodeFlexWrap
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
