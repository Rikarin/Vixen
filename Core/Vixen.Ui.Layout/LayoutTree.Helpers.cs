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

    /// <summary>
    ///     Whether a node is an item of a flex or grid container, and so takes its automatic minimum
    ///     size from that algorithm rather than from CSS Sizing §4.1's content-based one.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is the test that decides whether a bound may travel across an aspect ratio</b>,
    ///     and it is a question about the PARENT. See <see cref="BoundAxisWithinMinAndMax" /> for the
    ///     pair of fixtures that forced the distinction: the same box under a flex parent and a block
    ///     parent gets two different answers, because a block box's automatic minimum is its content
    ///     and an item's is its algorithm's, which its own maximum caps.
    ///     <para>
    ///         An absolutely positioned child is an item of nothing — it is sized against a containing
    ///         block by <c>LayoutTree.Absolute</c>, which applies the ratio rules it needs itself, and
    ///         applying them here as well would clamp it twice.
    ///     </para>
    /// </remarks>
    bool IsFlexOrGridItem(int index) {
        var parent = links[index].Parent;
        return parent >= 0
            && styles[parent].Display is Display.Flex or Display.InlineFlex or Display.Grid
            && styles[index].PositionType != PositionType.Absolute
            && styles[index].Display != Display.None;
    }

    /// <summary>Whether a node is an item of a flex container specifically.</summary>
    /// <remarks>
    ///     ⚠ <b>Only flex items are exempt from a transferred bound on an axis that was handed to
    ///     them</b>, and the two corpora insist on the difference in the same words.
    ///     <c>aspect_ratio_flex_column_stretch_fill_max_width</c> and
    ///     <c>grid_aspect_ratio_fill_child_max_width</c> are both <c>max-height: 20px;
    ///     aspect-ratio: 2</c> on a box whose inline axis its parent fills, and the <c>max-width</c>
    ///     of 40 that the ratio carries across is ignored by the flex item (100 wide) and obeyed by
    ///     the grid item (40 wide). CSS Flexbox §9.4 stretches an item whose cross size is
    ///     <c>auto</c> unconditionally; CSS Box Alignment §6.2's <c>normal</c> does not treat a box
    ///     with a preferred aspect ratio that way. Same declaration, two containers, two answers.
    /// </remarks>
    bool IsFlexItem(int index) {
        var parent = links[index].Parent;
        return parent >= 0
            && styles[parent].Display is Display.Flex or Display.InlineFlex
            && styles[index].PositionType != PositionType.Absolute
            && styles[index].Display != Display.None;
    }

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
    /// <remarks>
    ///     ⚠ <b>A column container's <c>baseline</c> degrades to <c>flex-start</c> and that is only
    ///     half of the rule</b> — see <see cref="DegradedBaselineShift" />, which supplies the other
    ///     half in RTL. The items of such a group share their LINE-LEFT edge, which
    ///     <c>direction</c> does not mirror, and the group as a whole is then aligned flow-start.
    ///     Flow-start is all this method can say; the shift is a function of the group.
    /// </remarks>
    Align ResolveChildAlignment(int index, int child) {
        var align = styles[child].AlignSelf == Align.Auto ? styles[index].AlignItems : styles[child].AlignSelf;

        // A baseline is a property of a line of text, and a column container has no line to align
        // to, so the request degrades rather than being ignored.
        return align == Align.Baseline && FlexAxis.IsColumn(styles[index].FlexDirection) ? Align.FlexStart : align;
    }

    /// <summary>Whether this child asked for a baseline its container's cross axis cannot give.</summary>
    bool IsDegradedBaseline(int index, int child) =>
        FlexAxis.IsColumn(styles[index].FlexDirection)
        && (styles[child].AlignSelf == Align.Auto ? styles[index].AlignItems : styles[child].AlignSelf) == Align.Baseline;

    /// <summary>
    ///     How far a degraded-baseline item moves off its line's cross-start edge, which is nothing
    ///     at all except in RTL.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The items of a baseline-sharing group share their LINE-LEFT edge, and
    ///         <c>direction</c> does not mirror that edge.</b> CSS Writing Modes §6.3 makes line-left
    ///         and line-right depend on the writing mode alone, and a baseline is line-relative by
    ///         construction. So in a column container — where the cross axis is the inline axis and
    ///         there is no real baseline to share — every item in the group is placed with its
    ///         line-left edge on the group's, and the GROUP is what the flow-relative
    ///         <c>flex-start</c> fallback then aligns. In LTR the two statements coincide and the
    ///         shift is zero; in RTL the group's flow-start edge is its line-right one, so an item
    ///         narrower than the group hangs back by the difference.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The group's extent is the widest item in the LINE, and it is emphatically not the
    ///         line's cross size.</b> `align_baseline_column__border_box_rtl` is the fixture that
    ///         tells them apart: a single-line column has a line as wide as the container by CSS
    ///         Flexbox §9.4 step 8, and Chrome still puts two 50-wide items at x=50 in a 100-wide
    ///         container rather than at 0. Measured with a 30-wide second item, which the corpus does
    ///         not have: Chrome puts it at 50 too — the group is 50 wide, sits against the flow-start
    ///         edge, and every item's left edge is on the group's left edge.
    ///     </para>
    /// </remarks>
    float DegradedBaselineShift(int index, int child, FlexDirection crossAxis, float groupExtent, float availableInnerWidth) {
        if (results[index].Direction != Direction.Rtl || !IsDegradedBaseline(index, child)) {
            return 0f;
        }

        return MathF.Max(0f, groupExtent - DimensionWithMargin(child, crossAxis, availableInnerWidth));
    }

    /// <summary>The cross-axis extent of the degraded baseline group formed by one line's items.</summary>
    float BaselineGroupExtent(int index, int startChild, int endChild, FlexDirection crossAxis, float availableInnerWidth) {
        if (results[index].Direction != Direction.Rtl || !FlexAxis.IsColumn(styles[index].FlexDirection)) {
            return 0f;
        }

        var children = ChildIds(index);
        var extent = 0f;

        for (var i = startChild; i < endChild && i < children.Length; i++) {
            var child = children[i];

            if (IsInFlow(child) && IsDegradedBaseline(index, child)) {
                extent = MathF.Max(extent, DimensionWithMargin(child, crossAxis, availableInnerWidth));
            }
        }

        return extent;
    }

    /// <summary>Whether that alignment was written <c>safe</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>It has to follow the same <c>auto</c> resolution as the position it modifies, or the
    ///     two halves of one declaration come from two different elements.</b> <c>align-items: safe
    ///     end</c> on a container is inherited whole by a child whose <c>align-self</c> is
    ///     <c>auto</c> — position and prefix together — and reading the prefix off the child instead
    ///     would silently drop it.
    /// </remarks>
    OverflowAlignment ResolveChildAlignmentOverflow(int index, int child) =>
        styles[child].AlignSelf == Align.Auto ? styles[index].AlignItemsOverflow : styles[child].AlignSelfOverflow;

    /// <summary>The border-box height an already-decided border-box width implies through the ratio.</summary>
    /// <remarks>
    ///     ⚠ <b>Which box the ratio describes is <c>box-sizing</c>'s decision</b>, so a
    ///     <c>content-box</c> child's padding and border come off one axis before the division and go
    ///     back on to the other after it. Dividing the border boxes directly is right for the default
    ///     and silently wrong for every padded box, in an amount equal to the padding.
    /// </remarks>
    float HeightAcrossRatio(int child, Direction direction, float borderBoxWidth, float innerWidth) {
        var ratio = styles[child].AspectRatio;

        if (styles[child].BoxSizing == BoxSizing.BorderBox) {
            return borderBoxWidth / ratio;
        }

        var acrossInset = StyleResolution.ContentInsetForAxis(in styles[child], FlexDirection.Row, direction, innerWidth);
        var downInset = StyleResolution.ContentInsetForAxis(in styles[child], FlexDirection.Column, direction, innerWidth);

        return (MathF.Max(0f, borderBoxWidth - acrossInset) / ratio) + downInset;
    }

    /// <summary>The border-box width an already-decided border-box height implies through the ratio.</summary>
    /// <remarks>
    ///     The exact inverse of <see cref="HeightAcrossRatio" />, and it lives beside it so the two
    ///     cannot drift apart on the <c>box-sizing</c> question: composing them round-trips a length
    ///     back to itself, which is what lets <see cref="ResolveAspectBounds" /> merge a bound in one
    ///     axis with a bound in the other and get a pair that agrees with itself.
    /// </remarks>
    float WidthAcrossRatio(int child, Direction direction, float borderBoxHeight, float innerWidth) {
        var ratio = styles[child].AspectRatio;

        if (styles[child].BoxSizing == BoxSizing.BorderBox) {
            return borderBoxHeight * ratio;
        }

        var acrossInset = StyleResolution.ContentInsetForAxis(in styles[child], FlexDirection.Row, direction, innerWidth);
        var downInset = StyleResolution.ContentInsetForAxis(in styles[child], FlexDirection.Column, direction, innerWidth);

        return (MathF.Max(0f, borderBoxHeight - downInset) * ratio) + acrossInset;
    }

    /// <summary>A box's minimums and maximums on both axes, made consistent with its aspect ratio.</summary>
    /// <param name="MinWidth">The inline floor, or NaN.</param>
    /// <param name="MinHeight">The block floor, or NaN.</param>
    /// <param name="MaxWidth">The inline ceiling, or NaN.</param>
    /// <param name="MaxHeight">The block ceiling, or NaN.</param>
    readonly record struct AspectBounds(float MinWidth, float MinHeight, float MaxWidth, float MaxHeight);

    /// <summary>
    ///     A node's minimums and maximums on both axes, each transferred through the aspect ratio into
    ///     the other axis and merged with what that axis already said.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The transfer is the half that makes a single clamp enough.</b> CSS Sizing §5.1
    ///         applies a box's minimums and maximums in both axes, and §4.1 makes a bound in one axis
    ///         a bound in the other whenever a preferred aspect ratio links them: <c>max-height: 20px</c>
    ///         with <c>aspect-ratio: 2</c> <i>is</i> a <c>max-width</c> of 40. Every fixture in the
    ///         <c>aspect_ratio_*_fill_{min,max}_*</c> families is that sentence and nothing else.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Merging in both directions leaves the pair self-consistent</b> —
    ///         <c>MaxWidth</c> across the ratio is exactly <c>MaxHeight</c>, and likewise for the
    ///         minimums — because <c>max(a, b·r)/r == max(a/r, b)</c>. That is what lets a caller
    ///         clamp whichever axis it decided first and then derive the other, without the derived
    ///         axis landing outside its own bounds and needing a second clamp that would undo the
    ///         first. Transferring one way only re-introduces the very asymmetry these families fail on.
    ///     </para>
    ///     <para>
    ///         The bounds come back in border-box terms, but the transfer itself happens in whichever
    ///         box <c>box-sizing</c> names, because that is the box the ratio describes.
    ///     </para>
    /// </remarks>
    AspectBounds ResolveAspectBounds(int index, Direction direction, float ownerWidth, float ownerHeight) {
        var minWidth = StyleResolution.ResolvedMinDimension(in styles[index], Dimension.Width, ownerWidth, ownerWidth, direction);
        var minHeight = StyleResolution.ResolvedMinDimension(in styles[index], Dimension.Height, ownerHeight, ownerWidth, direction);
        var maxWidth = StyleResolution.ResolvedMaxDimension(in styles[index], Dimension.Width, ownerWidth, ownerWidth, direction);
        var maxHeight = StyleResolution.ResolvedMaxDimension(in styles[index], Dimension.Height, ownerHeight, ownerWidth, direction);

        var ratio = styles[index].AspectRatio;
        if (float.IsNaN(ratio) || ratio <= 0f) {
            return new AspectBounds(minWidth, minHeight, maxWidth, maxHeight);
        }

        // ⚠ Each transfer reads the *original* bound on the other axis, never an already-merged one.
        // Feeding a merged value back through would apply the same constraint twice and, for a
        // content-box node, add its padding a second time with it.
        var widthFromMinHeight = Across(minHeight, true);
        var heightFromMinWidth = Across(minWidth, false);
        var widthFromMaxHeight = Across(maxHeight, true);
        var heightFromMaxWidth = Across(maxWidth, false);

        return new AspectBounds(
            Merge(minWidth, widthFromMinHeight, true),
            Merge(minHeight, heightFromMinWidth, true),
            Merge(maxWidth, widthFromMaxHeight, false),
            Merge(maxHeight, heightFromMaxWidth, false)
        );

        float Across(float value, bool toWidth) {
            if (float.IsNaN(value) || value < 0f) {
                return float.NaN;
            }

            return toWidth
                ? WidthAcrossRatio(index, direction, value, ownerWidth)
                : HeightAcrossRatio(index, direction, value, ownerWidth);
        }

        static float Merge(float own, float transferred, bool isMinimum) {
            if (float.IsNaN(own) || own < 0f) {
                return transferred;
            }

            if (float.IsNaN(transferred)) {
                return own;
            }

            return isMinimum ? MathF.Max(own, transferred) : MathF.Min(own, transferred);
        }
    }

    /// <summary>Clamps a value to a node's own min and max on an axis.</summary>
    /// <remarks>
    ///     ⚠ <b>The maximum is applied first and the minimum second, so that a minimum larger than
    ///     the maximum wins.</b> CSS Sizing §5.1 is explicit: "if the max size is less than the min
    ///     size, the min size wins", which the specification expresses as clamping to the max and
    ///     then to the min rather than as a special case. This used to <c>return max</c> the moment
    ///     the value exceeded it, so the minimum was never consulted and
    ///     <c>min-width: 50px; max-width: 40px</c> answered 40. Taffy's
    ///     <c>absolute_minmax_bottom_right_min_max</c> is the fixture; Chrome answers 50.
    /// </remarks>
    float BoundAxisWithinMinAndMax(
        int index,
        Direction direction,
        FlexDirection axis,
        float value,
        float axisSize,
        float widthSize,
        bool axisSizeIsImposed = false
    ) {
        var dimension = FlexAxis.DimensionOf(axis);
        var isRow = FlexAxis.IsRow(axis);

        float min;
        float max;

        if (!IsFlexOrGridItem(index)
            || (axisSizeIsImposed && IsFlexItem(index))
            || float.IsNaN(styles[index].AspectRatio)
            || styles[index].AspectRatio <= 0f) {
            min = StyleResolution.ResolvedMinDimension(in styles[index], dimension, axisSize, widthSize, direction);
            max = StyleResolution.ResolvedMaxDimension(in styles[index], dimension, axisSize, widthSize, direction);
        } else {
            // ⚠ A RATIO MAKES THE OTHER AXIS'S BOUNDS THIS AXIS'S BOUNDS. `max-height: 20px` with
            // `aspect-ratio: 2` is a `max-width` of 40, and every `aspect_ratio_flex_*_fill_{min,max}_*`
            // family is that one sentence and nothing else.
            //
            // ⚠ FLEX ITEMS ONLY, and the reason is a pair of fixtures with identical styles and
            // identical text: `aspect_ratio_flex_column_fill_max_height` and
            // `block_aspect_ratio_fill_max_height` are both `max-width: 40px; aspect-ratio: 2` around
            // eleven syllables of Ahem, and Chrome answers 40x20 for the flex item and 40x60 for the
            // block. The transferred maximum is the same in both; what differs is the automatic
            // MINIMUM that argues with it. A block box gets CSS Sizing §4.1's content-based automatic
            // minimum in the ratio-dependent axis, which floors it at the six lines its text needs —
            // `ResolveBlockChildBox` and the floor below it already implement that, and applying the
            // transferred bound here as well would overrule it. A flex item instead gets Flexbox
            // §4.5's automatic minimum, which is explicitly capped by the item's own maximum, so the
            // transferred 20 wins. Two formatting contexts, two rules; this is not a gate around an
            // inconvenience.
            //
            // ⚠ AN AXIS WHOSE SIZE WAS HANDED TO IT IS NOT THE RATIO'S TO BOUND, which is what
            // `axisSizeIsImposed` says. A transferred bound belongs to the axis the ratio DECIDES; an
            // axis the flex algorithm stretched to the line, or that a definite length fixed, was
            // decided by something else and a bound carried across the ratio has no standing on it.
            // `aspect_ratio_flex_row_stretch_fill_max_height` is the fixture that insists: its
            // `max-width: 40px` transfers to a `max-height` of 20, and the item is still 100 tall,
            // because 100 is what `align-items: stretch` gave it. Its own `max-height` would still
            // have applied — only the borrowed one does not.
            //
            // ⚠ The block axis's owner size is only in hand when the clamp is *about* the block axis,
            // so a PERCENTAGE `min-height`/`max-height` does not transfer into the inline axis. That
            // is a real limit rather than an oversight: it degrades to the pre-existing behaviour
            // instead of resolving a percentage against a length this call was never given.
            var bounds = ResolveAspectBounds(index, direction, widthSize, isRow ? float.NaN : axisSize);
            min = isRow ? bounds.MinWidth : bounds.MinHeight;
            max = isRow ? bounds.MaxWidth : bounds.MaxHeight;
        }

        if (!float.IsNaN(max) && max >= 0f && value > max) {
            value = max;
        }

        if (!float.IsNaN(min) && min >= 0f && value < min) {
            value = min;
        }

        return value;
    }

    /// <summary>Clamps to min and max, and never below the node's own padding and border.</summary>
    float BoundAxis(
        int index,
        FlexDirection axis,
        Direction direction,
        float value,
        float axisSize,
        float widthSize,
        bool axisSizeIsImposed = false
    ) =>
        MathF.Max(
            BoundAxisWithinMinAndMax(index, direction, axis, value, axisSize, widthSize, axisSizeIsImposed),
            StyleResolution.PaddingAndBorderForAxis(in styles[index], axis, direction, widthSize)
        );

    /// <summary>Records a node's size on an axis, clamped and unclamped, from one raw measurement.</summary>
    /// <remarks>
    ///     ⚠ <b>The pair is written together so that it cannot come apart.</b>
    ///     <see cref="LayoutResult.UnclampedMeasuredDimensions" /> exists because CSS Flexbox §9.2's
    ///     flex base size is the measurement <i>before</i> the item's own min and max, and a site
    ///     that updated <see cref="LayoutResult.MeasuredDimensions" /> alone would leave a stale
    ///     unclamped value standing beside a fresh clamped one.
    /// </remarks>
    void SetMeasuredDimension(
        int index,
        FlexDirection axis,
        Direction direction,
        float value,
        float axisSize,
        float widthSize,
        bool axisSizeIsImposed = false
    ) {
        var dimension = (int) FlexAxis.DimensionOf(axis);
        results[index].MeasuredDimensions[dimension] = BoundAxis(index, axis, direction, value, axisSize, widthSize, axisSizeIsImposed);
        results[index].UnclampedMeasuredDimensions[dimension] =
            MathF.Max(value, StyleResolution.PaddingAndBorderForAxis(in styles[index], axis, direction, widthSize));
    }

    /// <summary>Records a size on an axis whose two answers the caller already has.</summary>
    /// <remarks>
    ///     For the paths that do their own clamping — a block, grid or inline container's outer
    ///     size, a scroll container's capped fit-content size, an inline span's union. Where no min
    ///     or max was applied at all the two arguments are the same value, which is what those sites
    ///     meant implicitly before the unclamped measurement existed.
    /// </remarks>
    void SetMeasuredDimension(int index, Dimension dimension, float bounded, float unbounded) {
        results[index].MeasuredDimensions[(int) dimension] = bounded;
        results[index].UnclampedMeasuredDimensions[(int) dimension] = unbounded;
    }

    /// <summary>CSS Flexbox §9.2 step 9: a flex base size clamped by the item's USED min and max.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The used minimum of a <c>min-width: auto</c> flex item is §4.5's automatic one</b>,
    ///         which is the whole difference between this and <see cref="BoundAxisWithinMinAndMax" />.
    ///         Everything that consumes a hypothetical main size has to consume the same one: §9.3
    ///         breaks lines by it, <see cref="FlexLine.SizeConsumed" /> accumulates it, and the two
    ///         distribution passes measure their deltas from it. Applying the floor at any one of
    ///         those and not the others makes the free space disagree with the sizes it was derived
    ///         from — measured, not assumed: flooring only the non-flexing path of
    ///         <see cref="DistributeFreeSpaceSecondPass" /> closes four families and breaks two.
    ///     </para>
    ///     <para>
    ///         Deliberately not routed through <see cref="BoundAxis" />: the padding-and-border floor
    ///         that adds is not part of §9.2's clamp, and every caller here previously used the
    ///         unfloored <see cref="BoundAxisWithinMinAndMax" />.
    ///     </para>
    /// </remarks>
    float HypotheticalMainSize(int index, Direction direction, FlexDirection mainAxis, float flexBasis, float mainAxisOwnerSize, float ownerWidth) {
        var bounded = BoundAxisWithinMinAndMax(index, direction, mainAxis, flexBasis, mainAxisOwnerSize, ownerWidth);
        var autoMin = results[index].ComputedAutoMinMainSize;
        return !float.IsNaN(autoMin) && bounded < autoMin ? autoMin : bounded;
    }

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

    /// <summary>The mirror of <see cref="ConstrainMaxSizeForMode" />: a stated minimum widens it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A box's BLOCK size is a function of the INLINE size it is actually given, and the
    ///         size it is actually given has been through both of its own clamps.</b> CSS Sizing §5.1
    ///         clamps the used size by the minimum as well as the maximum; measuring the other axis
    ///         off the unclamped available space asks the box a question about a width it will never
    ///         have. The maximum half of this has been here all along and is why
    ///         <see cref="ConstrainMaxSizeForMode" /> exists at all — the minimum half was simply
    ///         never written, and it is the same sentence of the same section.
    ///     </para>
    ///     <para>
    ///         <c>measure_child_with_min_size_greater_than_available_space</c> is the whole rule in
    ///         one box: sixteen Ahem characters with <c>min-width: 200px</c> in a 100-point column.
    ///         Measured at the offered 100 the text takes two lines and the item is 20 tall; measured
    ///         at the 200 it is going to be given it takes one and is 10, which is Chrome's answer.
    ///         The used inline size does not move — it was always going to be clamped up to 200 on
    ///         the way out — so only the other axis changes, which is why this reads as a
    ///         line-breaking bug and is an ordering one. Grid found the same rule from the other end
    ///         and wrote it up under <c>grid_size_child_fixed_tracks</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ A <see cref="SizingMode.MaxContent" /> request is deliberately left alone. A minimum
    ///         does not bound an unbounded question, and raising the offer under max-content would
    ///         hand a percentage child a definite size to resolve against that the box has not got.
    ///     </para>
    /// </remarks>
    void ConstrainMinSizeForMode(
        int index,
        Direction direction,
        FlexDirection axis,
        float ownerAxisSize,
        float ownerWidth,
        SizingMode mode,
        ref float size
    ) {
        if (mode is not (SizingMode.StretchFit or SizingMode.FitContent) || float.IsNaN(size)) {
            return;
        }

        var min = StyleResolution.ResolvedMinDimension(in styles[index], FlexAxis.DimensionOf(axis), ownerAxisSize, ownerWidth, direction);
        if (float.IsNaN(min)) {
            return;
        }

        min += StyleResolution.MarginForAxis(in styles[index], axis, ownerWidth);
        size = size > min ? size : min;
    }

    /// <summary>A node's <c>overflow</c> along one axis.</summary>
    /// <remarks>
    ///     ⚠ <b>Every rule in the algorithm that reads <c>overflow</c> is about one axis</b>, and each
    ///     of them used to read a single field that meant both. That was invisible while the two could
    ///     not differ; the moment <c>overflow-x</c> became real it would have been a column that
    ///     scrolls sideways clamping its own height. Going through here rather than at the field makes
    ///     each caller name the axis it means.
    /// </remarks>
    Overflow OverflowOn(int index, Dimension dimension) =>
        dimension == Dimension.Width ? styles[index].OverflowX : styles[index].OverflowY;

    /// <summary>The scrollbar gutter this node reserves at one physical edge.</summary>
    /// <remarks>
    ///     <para>
    ///         The companion to <c>results[index].Padding[edge] + results[index].Border[edge]</c>,
    ///         which is how block, grid, inline and the two leaf paths all ask for their own edges.
    ///         Those two arrays are resolved once and stored physically; this is not stored, because
    ///         it needs no resolving — the gutter is an absolute length and the only thing that
    ///         varies is which edge it lands on.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Add it to the inset, never to <see cref="LayoutResult.Padding" /> itself.</b>
    ///         Folding it into the stored arrays would make every one of those sites correct in one
    ///         edit and would also make <see cref="GetComputedPadding" /> report a padding the
    ///         stylesheet never wrote, which is the number a renderer paints the background inset by.
    ///     </para>
    /// </remarks>
    float ScrollbarGutterAt(int index, Edge edge, Direction direction) {
        var axis = edge is Edge.Left or Edge.Right ? FlexDirection.Row : FlexDirection.Column;
        return StyleResolution.ScrollbarGutterAtEdge(in styles[index], axis, edge, direction);
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
        float ownerHeight,
        int currentDepth
    ) {
        var mainDimension = FlexAxis.DimensionOf(mainAxis);

        if (styles[index].Display == Display.None) {
            return float.NaN;
        }

        if (styles[index].MinDimensions[(int) mainDimension].IsDefined) {
            return float.NaN;
        }

        // ⚠ The *main* axis's overflow, which is what CSS Flexbox §4.5 says: an item opts out of the
        // automatic minimum by not being visible along the axis the minimum is about. A row of text
        // in a box that only clips vertically still refuses to shrink below its longest word.
        if (OverflowOn(index, mainDimension) != Overflow.Visible) {
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

        // ⚠ `ownerWidth` twice, and the two arguments mean different things. The fifth is the
        // percentage basis; the sixth is the inline room this item's margin box will have, and at
        // this entry point the containing block's inline size is the nearest honest value for both.
        // They part company one box down — see `ProbeInlineSize`.
        var floor = ComputeMinContentSize(index, mainAxis, direction, ownerWidth, ownerHeight, ownerWidth, currentDepth);

        // ⚠ <b>§4.5's CONTENT SIZE SUGGESTION is itself clamped through the ratio, and that is a
        // different clamp from the transferred size suggestion below.</b> The specification: "the
        // content size suggestion is the min-content size in the main axis, clamped, if it has an
        // aspect ratio, by any definite min and max cross size properties converted through the
        // aspect ratio". The transferred suggestion needs a definite cross SIZE; this one needs only
        // a cross BOUND, so an item with `max-width: 40px; aspect-ratio: 2` and no width of its own
        // reaches this and not that.
        //
        // ⚠ It earns its place because of the measurement above rather than on its own. While the
        // block-axis probe answered one line, the floor was small enough that nothing noticed the
        // missing clamp; measuring at the real inline size made the floor three lines tall and it
        // overruled a ratio that says the item is 20 points high.
        // `aspect_ratio_flex_column_stretch_fill_max_height` is the fixture, and it went red on the
        // measurement change alone — the two halves are one change and neither is worth landing by
        // itself.
        if (!float.IsNaN(floor) && !float.IsNaN(aspectRatio) && aspectRatio > 0f) {
            var bounds = ResolveAspectBounds(index, direction, ownerWidth, isMainAxisRow ? ownerHeight : ownerMainAxisSize);
            var ratioMax = isMainAxisRow ? bounds.MaxWidth : bounds.MaxHeight;
            var ratioMin = isMainAxisRow ? bounds.MinWidth : bounds.MinHeight;

            if (!float.IsNaN(ratioMax) && ratioMax >= 0f && floor > ratioMax) {
                floor = ratioMax;
            }

            if (!float.IsNaN(ratioMin) && ratioMin >= 0f && floor < ratioMin) {
                floor = ratioMin;
            }
        }

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

        // ⚠ <b>Named rather than inlined, and the name is the point.</b> It is a CEILING WITH A
        // RULE BEHIND IT — a box's min-content size cannot exceed the size its own contents were
        // measured at — and not a workaround for a number that comes out wrong. It read as the
        // second for as long as it was four unnamed lines under sixty of archaeology, which is how
        // a reader arrives at "delete it and see". See <see cref="MeasuredContentCeiling" />.
        var ceiling = MeasuredContentCeiling(index, mainDimension);

        if (!float.IsNaN(ceiling) && floor > ceiling) {
            floor = ceiling;
        }

        return float.IsNaN(floor) || floor < 0f ? 0f : floor;
    }

    /// <summary>
    ///     The size this item's own contents were measured at, which <see cref="ComputeAutoMinMainSize" />'s
    ///     floor is held under — or NaN when the item's basis did not come from a measurement.
    /// </summary>
    /// <param name="index">The node.</param>
    /// <param name="mainDimension">The main axis's dimension.</param>
    /// <returns>The ceiling, or NaN.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A ceiling with a rule behind it rather than a workaround, and the difference is
    ///         what this method exists to say.</b> §4.5's automatic minimum is an intrinsic minimum,
    ///         and an intrinsic minimum that exceeds the size the contents were actually measured at
    ///         is a probe reporting more room than the contents ever asked for. That sentence is the
    ///         rule; every paragraph in the body is the evidence for what it is standing in front of
    ///         at each point in this file's history, and the last of them is the one that matters —
    ///         it stands in front of no over-report at all now, and it still cannot go.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The reason it cannot go is a framework decision and not a measurement</b>, which
    ///         is why nothing here should be read as a defect waiting to be fixed. Chrome does not
    ///         cap this floor — measured, and the table is in <c>Taffy/KnownGaps.txt</c> — but this
    ///         engine's initial <c>Display</c> is <c>Flex</c> and its initial <c>FlexDirection</c> is
    ///         <c>Row</c> where a browser's initial display is <c>block</c>, so every plain element
    ///         here is the first row of that table and is given the last row's picture. Removing the
    ///         term would be right about Chrome and wrong about the markup an author believes they
    ///         wrote. <c>Rikarin/Vixen#682</c> is where that call is owed.
    ///     </para>
    /// </remarks>
    float MeasuredContentCeiling(int index, Dimension mainDimension) {
        // ⚠ A FLOOR ABOVE A CONTENT-MEASURED BASIS IS THE PROBE BEING WRONG. A box's min-content size
        // cannot exceed the size its own contents were measured at, so when ComputedFlexBasis came
        // from that measurement rather than from a declaration, it caps the floor. Live defects in
        // ComputeMinContentSize are why the cap earns its place rather than being a tautology, and
        // each was found by turning the floor on.
        //
        // ⚠ <b>ONE DEFECT IS LEFT, and it is what the cap is now for.</b> The grid half is closed —
        // ComputeGridMinContentSize sizes a grid's tracks instead of reading its items as a flex
        // line, and with the cap deleted the four `gridflex_row_integration` variants go from red to
        // green. What still fails without the cap is one family and one sentence: the block-axis
        // probe measures a leaf at `ownerWidth`, which is the CONTAINING BLOCK's inline size and not
        // the item's own used width. Where an item is about to be stretched wider than its owner,
        // the text is measured too narrow, wraps to more lines than it will really take, and the
        // floor comes out a multiple of the true height. `blitz_issue_88` is 600 points of row
        // holding one line of text: 10 points tall in Chrome, 50 from the probe. `bevy_issue_9530`,
        // `bevy_issue_9530_reduced` and `measure_child_with_min_size_greater_than_available_space`
        // are the same sentence — sixteen fixtures across four families, and nothing else in any
        // corpus. Until that is fixed an over-reported floor must not be allowed to inflate a real
        // box. ⚠ The block-axis sentence above is CLOSED — `probeWidth` is threaded beside
        // `ownerWidth` now and `ProbeInlineSize` is where they separate — and the cap still cannot
        // go: `bevy_issue_9530` and `measure_child_with_min_size_greater_than_available_space`
        // remain, the second under a different rule this file's `KnownGaps.txt` already files.
        //
        // ⚠ <b>ONE FAMILY LEFT, MEASURED: the cap off costs four `bevy_issue_9530` and nothing
        // else.</b> `measure_child_with_min_size_greater_than_available_space` closed with
        // `ClampProbeInlineSize` — §5.1's minimum applied to the probe's offer, which is the same
        // sentence `ConstrainMinSizeForMode` says to the layout. So this cap is now standing in front
        // of exactly one over-report, and the `width: 50%` percentage half it used to be blamed on is
        // not one: `ProbeContentWidth`'s zero is §5.2.1 obeyed and `ProbeInlineSize` is the other
        // number.
        //
        // ⚠ <b>AND NOW IT STANDS IN FRONT OF NO OVER-REPORT AT ALL, WHICH IS A DIFFERENT THING FROM
        // BEING REMOVABLE.</b> `bevy_issue_9530` closed with the mirror of the sentence above:
        // `ProbeInlineSize` takes a box's MARGINS off the room it was offered, which is right for a
        // box whose width comes from what is left over and wrong for one that declares a percentage
        // and overflows instead. A full Vixen.Ui.Layout.Tests run with this cap deleted is now 6 417
        // of 6 417, pinned corpus counts included. What deleting it costs is three
        // `TextWrappingPixelTests` in Vixen.Ui.Controls.Tests, and that is `Rikarin/Vixen#682`'s
        // half rather than a probe defect: `break-word`'s intrinsic minimum is specified NOT to
        // shrink, so with step 3E's max-content base the §4.5 floor really is the whole word and the
        // word never wraps into its box. ⚠ So the next move on this cap is to NAME it — it is a
        // ceiling with a rule behind it, not a workaround for a wrong number — and the measurement
        // that would settle its shape is a browser one nobody has taken.
        //
        // ⚠ <b>AND THE CAP IS THE SMALLER OF TWO MEASUREMENTS NOW, because §9.2 step 3E made
        // `ComputedFlexBasis` stop being the one this sentence is about.</b> The justification is
        // "a box's min-content size cannot exceed the size its own contents were MEASURED at" —
        // which was the flex basis only while the basis WAS that measurement. Step 3E makes it the
        // item's MAX-CONTENT size instead, taken with the main axis unconstrained, and that is a
        // strictly weaker ceiling: an unbreakable word under `overflow-wrap: break-word` measures as
        // wide as the word, so the cap stopped holding the floor down to the box and the word never
        // wrapped. `TextWrappingPixelTests` is four fixtures of exactly that.
        // `UnclampedMeasuredDimensions` is the offer-measured number the cap always meant — the
        // second, real pass's answer — so the two are taken together and the smaller wins.
        //
        // ⚠ <b>MEASURED IN CHROME, AND CHROME DOES NOT DO THIS.</b> One unbreakable word at 28px
        // monospace, max-content 269.72, in a 120-point container: a `flex-direction: row` item under
        // `overflow-wrap: break-word` comes back 269.72 wide on ONE line — it keeps the whole word and
        // overflows, because §4.5's floor is an intrinsic minimum that `break-word` is specified not
        // to shrink and nothing holds it down to the box. The wrapping picture comes from the CROSS
        // axis, where there is no §4.5 floor: the item stretches to the container and the overflowing
        // word breaks at line layout, which is why `display: block` gives the same numbers. So the
        // second term is not Chrome's mechanism, and the question this cap was left open on is
        // answered — see `Taffy/KnownGaps.txt` for the whole table.
        //
        // ⚠ <b>It still cannot simply go, and the reason is now a DIVERGENCE rather than a gap.</b>
        // This engine's initial `Display` is `Flex` and its initial `FlexDirection` is `Row`, where a
        // browser's initial display is `block` — so every plain element here is the first row of that
        // table and gets the last row's picture. Dropping the term would be right about Chrome and
        // wrong about the markup an author believes they wrote, which is a framework call and not a
        // measurement.
        //
        // ⚠ <b>AND UNTIL NOW NOTHING IN THIS PROJECT SAID SO.</b> With this term deleted the whole
        // layout suite is green — eight corpora and 6 431 tests — and the only red is three
        // `TextWrappingPixelTests` in `Vixen.Ui.Controls.Tests`, a different assembly two layers
        // out. A decision whose only witness lives in another project is one the next reader deletes
        // in good faith while every fixture in front of them agrees. `AutomaticMinimumSizeTests.
        // An_item_whose_content_refuses_to_shrink_is_still_floored_at_what_it_was_measured_at` is the
        // pin: it is not evidence that the cap is right, it is the record that removing it is the
        // framework call above rather than a cleanup.
        //
        // ⚠ <b>RE-MEASURED AFTER §5.2.2's CLAUSE CAME BACK OUT, WHICH IS THE ONE EVENT THAT COULD
        // HAVE MOVED THIS, AND IT DID NOT.</b> That removal (`Rikarin/Vixen#932`) turns 24 grid
        // conformance fixtures green and so changes a great many min-content answers; with this term
        // deleted on top of it, Vixen.Ui.Layout.Tests is 6 439 of 6 441 — the two red being two rows
        // of the pin itself — and Vixen.Ui.Controls.Tests is 862 of 865 with the same three
        // `TextWrappingPixelTests` and the same numbers. The bill is a property of the rule rather
        // than of the probe's state on a given day.
        if (!results[index].FlexBasisFromContent) {
            return float.NaN;
        }

        var ceiling = results[index].ComputedFlexBasis;
        var offered = results[index].UnclampedMeasuredDimensions[(int) mainDimension];

        return !float.IsNaN(offered) && (float.IsNaN(ceiling) || offered < ceiling) ? offered : ceiling;
    }

    /// <summary>What a child <i>adds</i> to its parent's min-content size.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A box's min-content <i>size</i> and its min-content <i>contribution</i> are two
    ///         different numbers, and conflating them is what left the largest hole the Taffy corpus
    ///         found.</b> CSS Sizing §5.2.2 defines the contribution as the box's <i>preferred</i>
    ///         size when that is definite, clamped by its own min and max — the contents are not
    ///         consulted at all. Only when the preferred size is <c>auto</c> does the intrinsic
    ///         min-content size answer.
    ///     </para>
    ///     <para>
    ///         Without the distinction an empty <c>width: 50px</c> box reported <b>zero</b>, because
    ///         <see cref="ComputeMinContentSizeUncached" /> takes the childless branch and a box with
    ///         no contents needs no room for them. Every §4.5 floor computed from such a descendant
    ///         was therefore missing. <c>align_baseline_child_padding</c> is the demonstration: a
    ///         50px item wrapping a 50px child in 5px of padding must not shrink below 60, and Chrome
    ///         gives the whole 10px of overflow to its sibling instead. Vixen used to split it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The node's own §4.5 floor deliberately does not go through here</b>, and the same
    ///         fixture is why. Its first item is an empty <c>width: 50px</c> box that Chrome shrinks
    ///         to 40, so §4.5's <i>content size suggestion</i> for that item is 0 — the intrinsic
    ///         size, not the contribution. §4.5 already reads the preferred size separately as the
    ///         <i>specified size suggestion</i> and takes the smaller of the two; routing the content
    ///         suggestion through the contribution as well would make both terms the same number and
    ///         freeze every definitely-sized item at its own width.
    ///     </para>
    /// </remarks>
    float MinContentContribution(
        int index,
        FlexDirection axis,
        Direction ownerDirection,
        float ownerWidth,
        float ownerHeight,
        float probeWidth,
        int currentDepth
    ) {
        var dimension = FlexAxis.DimensionOf(axis);
        var reference = FlexAxis.IsRow(axis) ? ownerWidth : ownerHeight;
        var direction = StyleResolution.ResolveDirection(in styles[index], ownerDirection);

        // ⚠ A *percentage* preferred size is not a definite one here, and reading it as one is
        // wrong in both corpora at once — Yoga's Percent_within_flex_grow and Taffy's
        // percent_within_flex_grow are the same case. CSS Sizing §5.2.1: while calculating
        // intrinsic contributions, a percentage against a containing block whose size is not yet
        // known behaves as `auto`. That is exactly the situation this probe is in — it is being
        // asked how small the parent may be, so the parent's size does not exist to be a fraction
        // of, and the `ownerWidth` threaded through here belongs to an ancestor further out.
        // `width: 100%` therefore falls through to the contents, which is what Chrome answers.
        var preferred = StyleResolution.ProcessedDimension(in styles[index], dimension).Unit == LayoutUnit.Point
            ? ResolvedDimension(index, dimension, reference, ownerWidth, direction)
            : float.NaN;

        var contribution = float.IsNaN(preferred)
            ? ComputeMinContentSize(index, axis, ownerDirection, ownerWidth, ownerHeight, probeWidth, currentDepth)
            : preferred;

        contribution = BoundAxisWithinMinAndMax(index, direction, axis, contribution, reference, ownerWidth);
        return float.IsNaN(contribution) || contribution < 0f ? 0f : contribution;
    }

    /// <summary>The smallest a node can be on an axis without its content overflowing.</summary>
    /// <remarks>
    ///     A leaf answers by being measured with nothing to spare on the axis in question, which is
    ///     what makes a text measurer report its longest word. A container answers by summing its
    ///     children's <see cref="MinContentContribution" /> along its own main axis and taking the
    ///     largest across its cross axis. No layout is written along the way — only the leaf measure
    ///     callbacks see anything happen.
    /// </remarks>
    float ComputeMinContentSize(
        int index,
        FlexDirection requestedAxis,
        Direction ownerDirection,
        float ownerWidth,
        float ownerHeight,
        float probeWidth,
        int currentDepth
    ) {
        var wantRow = FlexAxis.IsRow(requestedAxis);
        var axis = wantRow ? 0 : 1;

        // Without this the §4.5 probe measures every flex item on every pass, uncached — which is
        // precisely the per-frame text measurement doc 09 says the measure cache exists to prevent.
        // It is keyed on the owner size because percentage margins and padding resolve against it,
        // and invalidated by the dirty flag, which a change anywhere below this node has already set.
        // ⚠ The probe width is part of the key and not only the owner size. They are two different
        // numbers since the block-axis probe stopped measuring text at whatever the percentage basis
        // happened to be — a node probed as its container's item and the same node probed again from
        // an ancestor's recursion can share an `ownerWidth` and be measured at different widths, and
        // the answer is a different number in each case.
        if (!float.IsNaN(results[index].MinContentSizes[axis])
            && Inexact(results[index].MinContentOwnerWidth, ownerWidth)
            && Inexact(results[index].MinContentOwnerHeight, ownerHeight)
            && Inexact(results[index].MinContentProbeWidth, probeWidth)) {
            return results[index].MinContentSizes[axis];
        }

        var computed = ComputeMinContentSizeUncached(index, requestedAxis, ownerDirection, ownerWidth, ownerHeight, probeWidth, currentDepth);

        if (!Inexact(results[index].MinContentOwnerWidth, ownerWidth)
            || !Inexact(results[index].MinContentOwnerHeight, ownerHeight)
            || !Inexact(results[index].MinContentProbeWidth, probeWidth)) {
            results[index].MinContentSizes[0] = float.NaN;
            results[index].MinContentSizes[1] = float.NaN;
            results[index].MinContentOwnerWidth = ownerWidth;
            results[index].MinContentOwnerHeight = ownerHeight;
            results[index].MinContentProbeWidth = probeWidth;
        }

        results[index].MinContentSizes[axis] = computed;
        return computed;
    }

    /// <summary>
    ///     The content-box width a descendant's percentages are a fraction of, during an intrinsic
    ///     probe.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Zero when this box's own width is not a length</b>, and that is CSS Sizing §5.2.1
    ///     rather than a shortcut: a percentage against a containing block whose size is still being
    ///     computed behaves as <c>auto</c>, and a probe asking "how small may this be" is exactly
    ///     that situation. A <c>width: 50%</c> box is included — the percentage makes it indefinite
    ///     here even though it will have a used width later, which is the same rule
    ///     <see cref="MinContentContribution" /> applies to a preferred size.
    /// </remarks>
    float ProbeContentWidth(int index, Direction direction, float ownerWidth) {
        if (StyleResolution.ProcessedDimension(in styles[index], Dimension.Width).Unit != LayoutUnit.Point) {
            return 0f;
        }

        var width = ResolvedDimension(index, Dimension.Width, ownerWidth, ownerWidth, direction);
        if (float.IsNaN(width)) {
            return 0f;
        }

        return MathF.Max(0f, width - StyleResolution.ContentInsetForAxis(in styles[index], FlexDirection.Row, direction, ownerWidth));
    }

    /// <summary>The inline size a text leaf under this box should be measured at.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Not <see cref="ProbeContentWidth" />, and the difference is the whole of
    ///         <c>Rikarin/Vixen#623</c>.</b> That one answers "what are a descendant's percentages a
    ///         fraction of", and its zero for an undeclared box is CSS Sizing §5.2.1 being obeyed.
    ///         This one answers "how much room will the text actually have", and zero is never the
    ///         answer to that: a box with no width of its own is as wide as what it is inside, less
    ///         its own edges. The recursion used to conflate them, so every paragraph below a box
    ///         that did not declare a width was measured in a width of nothing.
    ///     </para>
    ///     <para>
    ///         It is an upper bound rather than the used width in a <i>row</i>, where siblings will
    ///         divide this between them — but a bound in the right direction and off by a factor of
    ///         the sibling count beats a bound in the wrong one and off by the length of the text.
    ///         An indefinite width stays indefinite: NaN in, NaN out, and the leaf measures unbounded.
    ///     </para>
    /// </remarks>
    float ProbeInlineSize(int index, Direction direction, float ownerWidth, float probeWidth) {
        var inset = StyleResolution.ContentInsetForAxis(in styles[index], FlexDirection.Row, direction, ownerWidth);

        var declaredUnit = StyleResolution.ProcessedDimension(in styles[index], Dimension.Width).Unit;

        if (declaredUnit == LayoutUnit.Point) {
            var declared = ResolvedDimension(index, Dimension.Width, ownerWidth, ownerWidth, direction);
            if (!float.IsNaN(declared)) {
                return ClampProbeInlineSize(index, direction, ownerWidth, MathF.Max(0f, declared - inset), inset);
            }
        }

        if (float.IsNaN(probeWidth)) {
            return float.NaN;
        }

        // ⚠ <b>A PERCENTAGE WIDTH IS A DECLARED WIDTH TO THIS QUESTION, and it is resolved against
        // `probeWidth` rather than against `ownerWidth`.</b> The two sentences either side of this
        // one are the same distinction said twice: `ProbeContentWidth` answers "what are a
        // descendant's percentages a fraction of" and CSS Sizing §5.2.1 makes that ZERO for a box
        // whose own width is not a length, while this method answers "how much room will the text
        // have" — and a box that says `width: 100%` inside a definite offer is going to be that
        // wide, margins or no margins. Subtracting its margins from the offer is right only for a
        // box whose width comes FROM the remaining space; a percentage box overflows its container
        // instead, which is what `bevy_issue_9530` draws. `probeWidth` is the containing block's
        // content width by construction — it is what the level above passed down as room for this
        // margin box — so it is also the percentage's basis, and `ownerWidth` one box down is the
        // zero §5.2.1 asks for and would make every percentage width nothing.
        if (declaredUnit == LayoutUnit.Percent) {
            var resolved = ResolvedDimension(index, Dimension.Width, probeWidth, probeWidth, direction);
            if (!float.IsNaN(resolved)) {
                return ClampProbeInlineSize(index, direction, ownerWidth, MathF.Max(0f, resolved - inset), inset);
            }
        }

        // ⚠ The margins come off as well as the padding and border, because `probeWidth` is what is
        // available to this box's MARGIN box. `bevy_issue_9530` is the fixture that says so: a text
        // with 20 points of margin either side inside a 260-wide column is laid out in 220, and
        // measuring it in 260 puts six of its chunks on a line where Chrome fits five — twenty lines
        // against twenty-four, and the item is floored four lines short of what it needs.
        var available = MathF.Max(
            0f,
            probeWidth
            - StyleResolution.MarginForAxis(in styles[index], FlexDirection.Row, ownerWidth)
            - inset
        );

        return ClampProbeInlineSize(index, direction, ownerWidth, available, inset);
    }

    /// <summary>CSS Sizing §5.1 over a probe's inline size: the room is clamped by the box's own bounds.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A box is never laid out at a width its own <c>min-width</c> forbids, so it must
    ///         never be MEASURED at one either.</b> The block-axis intrinsic size is a function of the
    ///         used inline size, and the used inline size is the offer clamped by the bounds — not the
    ///         offer. Sixteen Ahem characters with <c>min-width: 200px</c> in a 100-point column were
    ///         probed at 100, took two lines, and floored the item at 20 points where Chrome gives it
    ///         the 200 it was always going to have and one line. That is
    ///         <c>measure_child_with_min_size_greater_than_available_space</c>, and
    ///         <see cref="ConstrainMinSizeForMode" /> is the same sentence said to the LAYOUT: the
    ///         probe had only ever been told the maximum half of it, through nothing at all.
    ///     </para>
    ///     <para>
    ///         ⚠ The minimum is applied last, which is CSS Sizing §5.1's min-over-max precedence and
    ///         not an ordering accident: a <c>min-width</c> above a <c>max-width</c> wins.
    ///     </para>
    /// </remarks>
    float ClampProbeInlineSize(int index, Direction direction, float ownerWidth, float size, float inset) {
        var max = StyleResolution.ResolvedMaxDimension(in styles[index], Dimension.Width, ownerWidth, ownerWidth, direction);
        if (!float.IsNaN(max)) {
            size = MathF.Min(size, MathF.Max(0f, max - inset));
        }

        var min = StyleResolution.ResolvedMinDimension(in styles[index], Dimension.Width, ownerWidth, ownerWidth, direction);
        if (!float.IsNaN(min)) {
            size = MathF.Max(size, MathF.Max(0f, min - inset));
        }

        return size;
    }

    /// <summary>As <see cref="ProbeContentWidth" />, for the block axis.</summary>
    float ProbeContentHeight(int index, Direction direction, float ownerWidth, float ownerHeight) {
        if (StyleResolution.ProcessedDimension(in styles[index], Dimension.Height).Unit != LayoutUnit.Point) {
            return 0f;
        }

        var height = ResolvedDimension(index, Dimension.Height, ownerHeight, ownerWidth, direction);
        if (float.IsNaN(height)) {
            return 0f;
        }

        return MathF.Max(0f, height - StyleResolution.ContentInsetForAxis(in styles[index], FlexDirection.Column, direction, ownerWidth));
    }

    float ComputeMinContentSizeUncached(
        int index,
        FlexDirection requestedAxis,
        Direction ownerDirection,
        float ownerWidth,
        float ownerHeight,
        float probeWidth,
        int currentDepth
    ) {
        var wantRow = FlexAxis.IsRow(requestedAxis);

        // ⚠ <b>A BOX THAT CLIPS OR SCROLLS STILL CONTRIBUTES WHAT IS INSIDE IT, and a clause here
        // used to say otherwise.</b> It returned a scroll container's own padding and border and
        // nothing else, reading CSS Sizing §5.2.2's exclusion of scrollable overflow as a rule about
        // intrinsic sizes in general. It is not: it is §4.5's rule about a box's OWN automatic
        // minimum, which `ComputeAutoMinMainSize` and `LayoutTree.Grid`'s `AutomaticMinimumIsZero`
        // each say for themselves — and since the former returns 0 before it ever calls this method,
        // every firing of the clause was already somebody else's contribution.
        //
        // ⚠ Measured in Chrome rather than argued: a `width: min-content` box around an
        // `overflow: scroll` container holding a 500-point box is 500 wide, not zero, and `hidden`,
        // `scroll` and no scroll container at all give identical numbers. It cost 24 grid fixtures
        // and its only witness was a hand-written test whose expectation had never been put in front
        // of a browser and turned out to be the defect.
        //
        // ⚠ <b>"THE EDITOR'S DOCKING CHAIN NEVER NEEDED IT BECAUSE EVERY BOX IN IT CLIPS" IS THE ONE
        // SENTENCE OF THAT ANALYSIS THAT WAS FALSE, and deleting this on the strength of it put the
        // inspector off the right of the window.</b> Every box in the chain from `docking-host` down
        // does clip and does declare both zero minimums; `editor-shell` and `editor-workspace`, the
        // two boxes between it and the root and the two written in the editor's own stylesheet
        // rather than the control set's, declared neither. So §4.5's opt-out reached every pane and
        // not the frame, this clause had been standing in for it, and removing the two at once is
        // what makes the removal safe: shell 1 049 wide inside 900 and a docking area 40 138 tall
        // inside 700, now `Vixen.Editor.Ui.Tests.ShellFitsItsWindowTests`. A layout store is not
        // where "an application's flex item forgot `min-width: 0`" is fixed.
        // `Rikarin/Vixen#932` and `#259`, and the whole write-up is under §5.2.2 in
        // `Taffy/GridKnownGaps.txt`.
        if ((flags[index] & LayoutNodeState.HasMeasureFunction) != 0) {
            // ⚠ <b>A leaf's min-content size in the BLOCK axis is the height its content takes at the
            // inline size it has, not at an unbounded one.</b> There is no such thing as the
            // min-content height of a paragraph on its own: CSS Sizing makes the block-axis intrinsic
            // sizes a function of the used inline size, which is the one asymmetry between the axes
            // that this probe has to carry. Measuring with the width undefined puts the whole text on
            // one line and reports a single line's height, so §4.5's floor for a column flex item was
            // one line tall however many lines the item really needs — and the items then shrank
            // below their own content. `grid_min_content_flex_column` is three two-line texts in a
            // 40-point row that Chrome overflows and this store squeezed to 13.3 each.
            //
            // ⚠ <b>`probeWidth` and not `ownerWidth`, and the two part company one box down.</b>
            // `ownerWidth` is the percentage basis, which CSS Sizing §5.2.1 makes ZERO for a box
            // with no definite width of its own — a descendant's `margin: 5%` inside such a box is
            // 5% of nothing. Handing that same zero to a text measurer says the paragraph is being
            // laid out in no width at all, so it broke at every opportunity and reported a line per
            // word. `blitz_issue_88` is the arithmetic: one line of text that Chrome draws 600 wide
            // and 10 tall came back 50 tall, five lines measured in a width of 0, and §4.5 then
            // floored the item at five lines. See `ProbeInlineSize` for where the two numbers
            // separate. `Rikarin/Vixen#623`.
            var ownProbe = ProbeInlineSize(index, StyleResolution.ResolveDirection(in styles[index], ownerDirection), ownerWidth, probeWidth);

            var size = Measure(
                index,
                wantRow ? 0f : ownProbe,
                wantRow ? MeasureMode.AtMost : float.IsNaN(ownProbe) ? MeasureMode.Undefined : MeasureMode.AtMost,
                wantRow ? float.NaN : 0f,
                wantRow ? MeasureMode.Undefined : MeasureMode.AtMost
            );

            var leafDirection = StyleResolution.ResolveDirection(in styles[index], ownerDirection);
            var leafPaddingAndBorder =
                StyleResolution.FlexStartContentInset(in styles[index], requestedAxis, leafDirection, ownerWidth)
                + StyleResolution.FlexEndContentInset(in styles[index], requestedAxis, leafDirection, ownerWidth);

            return (wantRow ? size.Width : size.Height) + leafPaddingAndBorder;
        }

        // ⚠ <b>An empty box is not a zero-sized box.</b> Its contents need no room, but its own
        // padding and border are part of the border-box size every other branch of this method
        // reports — the leaf-with-measure branch above adds them and the clipping branch further up
        // returns nothing else. Returning a bare zero here made this the one shape whose min-content
        // size was a CONTENT-box number while its siblings' were border-box ones, so §4.5's floor for
        // an empty `padding: 10px` item came out ten points short in each direction.
        if (links[index].ChildCount == 0) {
            var emptyDirection = StyleResolution.ResolveDirection(in styles[index], ownerDirection);

            return StyleResolution.FlexStartContentInset(in styles[index], requestedAxis, emptyDirection, ownerWidth)
                + StyleResolution.FlexEndContentInset(in styles[index], requestedAxis, emptyDirection, ownerWidth);
        }

        var direction = StyleResolution.ResolveDirection(in styles[index], ownerDirection);

        // ⚠ <b>A GRID IS NOT A FLEX ROW, and summing its items along one axis counts the same track
        // once per row of it.</b> CSS Grid §12 is where a grid container's intrinsic sizes come from:
        // the tracks are sized under a min-content constraint and the container's min-content size is
        // what they then occupy, gutters included. `gridflex_row_integration` is four 20-point boxes
        // in a 2x2 grid — two columns of 20, so 40 in Chrome, where reading the children as one flex
        // row adds all four to 80. Both mistakes are in the sum: the second row's items are not
        // beside the first row's, and the second column's are not stacked on the first's.
        //
        // ⚠ Two branches for `Rikarin/Vixen#265` were written independently and this is the wider
        // one. The other covered the INLINE axis alone and let the block axis keep the flex-line
        // reading, on the ground that a grid's min-content height needs the column pass run first;
        // ComputeGridMinContentSize does run it — the column axis, then each item's resolved inline
        // size, then the rows — so the block axis is answered here rather than left owed.
        if (styles[index].Display == Display.Grid) {
            return ComputeGridMinContentSize(index, wantRow, direction, ownerWidth, ownerHeight, currentDepth);
        }

        var nodeMainAxis = FlexAxis.Resolve(styles[index].FlexDirection, direction);
        var nodeCrossAxis = FlexAxis.ResolveCross(nodeMainAxis, direction);

        // ⚠ A wrapping container's min-content main size is the largest item, not the sum.
        // CSS Flexbox §9.9.1: the min-content main size of a *single-line* flex container is the sum
        // of its items' contributions, but a multi-line one may break between any two of them, so
        // the smallest it can be is the widest single item. This was unreachable while every
        // childless item contributed zero — the sum and the maximum were both zero — and it is the
        // one thing MinContentContribution broke in Yoga's corpus and not in Taffy's:
        // Align_content_flex_start_stretch_doesnt_influence_line_box_dim wraps five 30px items and
        // must report 50, not 250.
        var wraps = styles[index].FlexWrap != Wrap.NoWrap;
        var mainTotal = 0f;
        var crossMax = 0f;

        // ⚠ A DESCENDANT'S PERCENTAGES ARE A FRACTION OF THIS BOX, not of whatever this probe was
        // entered from. The recursion used to hand `ownerWidth` straight down, so a grandchild's
        // `margin: 5%` inside a `width: 50%` box was read as 5% of the outer container.
        // percentage_moderate_complexity is the arithmetic: 5% of 194 is 9.7 where 5% of the 85.36
        // it is really inside is 4.268, and the two margins put the answer 10.86 over a true content
        // height of 26.176. That was invisible while nothing consumed the number; §4.5's floor
        // consumes it, and an inflated floor becomes a real box.
        var innerWidth = ProbeContentWidth(index, direction, ownerWidth);
        var innerHeight = ProbeContentHeight(index, direction, ownerWidth, ownerHeight);
        var innerProbe = ProbeInlineSize(index, direction, ownerWidth, probeWidth);

        foreach (var child in ChildIds(index)) {
            if (!IsInFlow(child)) {
                continue;
            }

            var childMain = MinContentContribution(child, nodeMainAxis, direction, innerWidth, innerHeight, innerProbe, currentDepth)
                + StyleResolution.MarginForAxis(in styles[child], nodeMainAxis, innerWidth);
            var childCross = MinContentContribution(child, nodeCrossAxis, direction, innerWidth, innerHeight, innerProbe, currentDepth)
                + StyleResolution.MarginForAxis(in styles[child], nodeCrossAxis, innerWidth);

            mainTotal = wraps ? MathF.Max(mainTotal, childMain) : mainTotal + childMain;
            crossMax = MathF.Max(crossMax, childCross);
        }

        mainTotal += StyleResolution.FlexStartContentInset(in styles[index], nodeMainAxis, direction, ownerWidth)
            + StyleResolution.FlexEndContentInset(in styles[index], nodeMainAxis, direction, ownerWidth);
        crossMax += StyleResolution.FlexStartContentInset(in styles[index], nodeCrossAxis, direction, ownerWidth)
            + StyleResolution.FlexEndContentInset(in styles[index], nodeCrossAxis, direction, ownerWidth);

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
        //
        // ⚠ And `sticky` ignores it here too, for the opposite reason: its inset is not a layout
        // offset at all but a floor against a scroll position, applied in `UiDocument.Accumulate`
        // where the scroll offsets are. Reading it as a relative offset would apply it twice, which
        // is the trap that kept `sticky` out of `PositionType` until there was a member that could
        // be a containing block without being one. See `PositionType.Sticky`.
        if (styles[index].PositionType is PositionType.Static or PositionType.Sticky) {
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

        // ⚠ Before the wholesale `default` below, which would zero the arena handle rather than
        // clearing it — and zero is a valid offset, so a `display: none` span would keep pointing at
        // fragments the arena had already handed to somebody else.
        ReleaseFragments(index);

        results[index] = default;
        results[index].ComputedFlexBasis = float.NaN;
        results[index].ComputedAutoMinMainSize = float.NaN;
        results[index].GridAreaWidth = float.NaN;
        results[index].FragmentOffset = -1;
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

        // ⚠ <b>THE THREE SHORTCUTS BELOW ALL READ `Computed*` AS THE SIZE THE NODE CHOSE, and it is
        // not that when the node's own minimum or maximum cut the measurement down.</b> Each of them
        // argues "the old answer already satisfies the new constraint, so re-asking cannot change
        // it" — sound only while the old answer is what the node's CONTENTS produced. A clamped
        // entry is a measurement taken at one size and REPORTED at another, so the pair it holds was
        // never true together: a `max-width: 80px` label measured with the width unconstrained
        // wrapped to one line, came back 200 wide and 28 tall, and was reported as 80 × 28 — and a
        // later question of exactly 80 then matched the width and inherited the ONE-LINE height,
        // where the true answer at 80 is three lines and 83.6.
        // `TextWrapTests.A_label_with_no_width_of_its_own_wraps_at_its_container` is the fixture.
        //
        // So a clamped entry answers only the question it was actually asked — an exact spec match —
        // and never a shortcut. ⚠ It took two branches meeting to become reachable: §9.2 step 3E's
        // max-content pass is what seeds an unconstrained entry for a content-sized item, and #628's
        // CSS initial `flex-shrink` is what opened that clause's gate for `Vixen.Ui` documents.
        // Neither side alone shows it and no fixture in the eight corpora does either, which is why
        // the guard is here rather than in the corpus.
        var wasClamped =
            (!float.IsNaN(entry.UnclampedComputedWidth) && !Inexact(entry.UnclampedComputedWidth, entry.ComputedWidth))
            || (!float.IsNaN(entry.UnclampedComputedHeight) && !Inexact(entry.UnclampedComputedHeight, entry.ComputedHeight));

        var widthCompatible = sameWidthSpec
            || (!wasClamped
                && (ExactAndMatchesOld(widthMode, availableWidth - marginRow, entry.ComputedWidth)
                    || OldWasMaxContentAndStillFits(widthMode, availableWidth - marginRow, lastWidthMode, entry.ComputedWidth)
                    || NewIsStricterAndStillValid(widthMode, availableWidth - marginRow, lastWidthMode, entry.AvailableWidth, entry.ComputedWidth)));

        var heightCompatible = sameHeightSpec
            || (!wasClamped
                && (ExactAndMatchesOld(heightMode, availableHeight - marginColumn, entry.ComputedHeight)
                    || OldWasMaxContentAndStillFits(heightMode, availableHeight - marginColumn, lastHeightMode, entry.ComputedHeight)
                    || NewIsStricterAndStillValid(heightMode, availableHeight - marginColumn, lastHeightMode, entry.AvailableHeight, entry.ComputedHeight)));

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
