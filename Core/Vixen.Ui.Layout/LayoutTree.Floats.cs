// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Layout;

/// <summary>
///     The float exclusion list: CSS 2.1 §9.5's floats and §9.5.2's clearance.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>A float is the first thing in this store whose geometry is read by a box that is not
///         its parent.</b> Everything else — margins, alignment, tracks, even the collapsible margin
///         sets that cross a block boundary — travels up and down the parent link. A float does not:
///         it is placed by whichever container holds it, and then it narrows a <i>cousin</i>, because
///         §9.5's unit is the block formatting context and not the box. <c>float_bfc_avoids_float_from
///         _sibling_subtree</c> is that fixture exactly — a float two levels down one subtree moves a
///         box in the next one.
///     </para>
///     <para>
///         ⚠ <b>So the list is per formatting context, and the coordinates in it are the context
///         root's.</b> <see cref="floatOriginX" /> and <see cref="floatOriginY" /> hold where the
///         container currently being walked has its <i>border-box</i> top-left in those coordinates,
///         so a child written at container-relative <c>p</c> is at <c>origin + p</c> in the list.
///         Entering a formatting context root resets the pair to <c>(-insetLeft, -insetTop)</c>,
///         which puts the origin on the root's <i>content</i> box — the edge §9.5.1's rules 1, 2 and 7
///         are all stated against.
///     </para>
///     <para>
///         ⚠ <b><see cref="floatScopeStart" /> is why a nested context cannot see the outer one's
///         floats, and why it does not need a second list to prove it.</b> A block formatting context
///         root is defined by not being affected by floats outside it; the entries below the mark are
///         simply invisible, and the mark is restored on the way out along with the entries the inner
///         context added. One list, one integer, no allocation per context.
///     </para>
///     <para>
///         ⚠ <b>Every float path in this file is dead unless <see cref="treeHasFloats" /> is set</b>,
///         and that flag is one linear scan of the style array per <c>CalculateLayout</c>. This is not
///         a micro-optimisation: the float-active walk in <c>LayoutTree.Block</c> measures each child
///         twice and bypasses the layout cache, and a tree that contains no float must not pay either.
///         It also makes the claim "nothing about a float-free tree changed" checkable rather than
///         argued — with the flag clear, control flow through <c>WalkBlockChildren</c> is what it was.
///     </para>
/// </remarks>
public sealed partial class LayoutTree {
    /// <summary>One placed float's margin box, in its formatting context root's content coordinates.</summary>
    /// <remarks>
    ///     ⚠ The MARGIN box, not the border box. §9.5's rules are stated against it throughout: a
    ///     float with <c>margin-right: 10px</c> keeps the next float ten points further away, and a
    ///     box that avoids it avoids the margin too. The border box is what gets written to
    ///     <c>results</c>; this rectangle is the one the exclusion arithmetic uses.
    /// </remarks>
    readonly record struct PlacedFloat(FloatSide Side, float Left, float Right, float Top, float Bottom);

    readonly List<PlacedFloat> floatExclusions = [];

    /// <summary>Whether any node in the tree declares <c>float</c> or <c>clear</c>.</summary>
    bool treeHasFloats;

    /// <summary>The first entry in <see cref="floatExclusions" /> the current context can see.</summary>
    int floatScopeStart;

    /// <summary>The current container's border-box left, in the context root's content coordinates.</summary>
    float floatOriginX;

    /// <summary>The current container's border-box top, in the context root's content coordinates.</summary>
    float floatOriginY;

    /// <summary>The context root's content-box width, which is the right edge floats stop at.</summary>
    float floatContextWidth;

    /// <summary>Whether the tree holds a float or a <c>clear</c> anywhere, deciding it once per pass.</summary>
    /// <remarks>
    ///     ⚠ <b>Scanning is the point.</b> Asking each container whether its own children float would
    ///     be cheaper and would answer the wrong question: a <c>clear</c> reacts to a float placed in
    ///     a different subtree, so the only safe unit is the whole tree. The scan walks the live nodes
    ///     once, which is cheaper than a single extra layout of one of them.
    /// </remarks>
    void RefreshFloatPresence() {
        treeHasFloats = false;

        for (var i = 0; i < capacity; i++) {
            if ((flags[i] & LayoutNodeState.Live) == 0) {
                continue;
            }

            if (styles[i].Float != FloatSide.None || styles[i].Clear != Clear.None) {
                treeHasFloats = true;

                return;
            }
        }
    }

    /// <summary>What a formatting context root saved on the way in.</summary>
    readonly record struct FloatScope(int Start, float OriginX, float OriginY, float ContextWidth);

    /// <summary>Opens a fresh exclusion scope for a box that establishes a formatting context.</summary>
    /// <param name="insetLeft">The root's left border-plus-padding, so the origin lands on the content box.</param>
    /// <param name="insetTop">Its top border-plus-padding.</param>
    /// <param name="innerWidth">Its content-box width.</param>
    /// <returns>What <see cref="EndFloatContext" /> needs to put back.</returns>
    FloatScope BeginFloatContext(float insetLeft, float insetTop, float innerWidth) {
        var saved = new FloatScope(floatScopeStart, floatOriginX, floatOriginY, floatContextWidth);

        floatScopeStart = floatExclusions.Count;
        floatOriginX = -insetLeft;
        floatOriginY = -insetTop;
        floatContextWidth = innerWidth;

        return saved;
    }

    /// <summary>Discards a formatting context's floats and restores the enclosing one.</summary>
    void EndFloatContext(in FloatScope saved) {
        floatExclusions.RemoveRange(floatScopeStart, floatExclusions.Count - floatScopeStart);
        floatScopeStart = saved.Start;
        floatOriginX = saved.OriginX;
        floatOriginY = saved.OriginY;
        floatContextWidth = saved.ContextWidth;
    }

    /// <summary>How far the lowest visible float reaches, in the current container's coordinates.</summary>
    /// <remarks>
    ///     What §10.6.3's last clause needs: a formatting context root's <c>height: auto</c> is tall
    ///     enough to contain the floats in it, which is the whole of what <c>overflow: hidden</c> and
    ///     <c>display: flow-root</c> are asked for in the wild.
    /// </remarks>
    float LowestFloatBottom() {
        var lowest = float.NegativeInfinity;

        for (var i = floatScopeStart; i < floatExclusions.Count; i++) {
            lowest = MathF.Max(lowest, floatExclusions[i].Bottom);
        }

        return float.IsNegativeInfinity(lowest) ? float.NegativeInfinity : lowest - floatOriginY;
    }

    /// <summary>The band left free between the floats crossing a horizontal slice.</summary>
    /// <param name="top">The slice's top, in context coordinates.</param>
    /// <param name="height">Its height; a zero-height probe touches nothing it merely abuts.</param>
    /// <remarks>
    ///     ⚠ <b>A float's own bottom edge does not intrude on the slice starting there.</b> The tests
    ///     are strict on both sides — <c>Bottom &lt;= top</c> and <c>Top &gt;= top + height</c> — which
    ///     is what lets a cleared box sit flush against the float it cleared rather than one point
    ///     below it. <c>float_clear_empty_block_then_margin</c> reads that edge twice.
    /// </remarks>
    (float Left, float Right) FloatBandAt(float top, float height) {
        var left = 0f;
        var right = floatContextWidth;
        var bottom = top + MathF.Max(0f, height);

        for (var i = floatScopeStart; i < floatExclusions.Count; i++) {
            var placed = floatExclusions[i];

            if (placed.Bottom <= top || placed.Top >= bottom) {
                continue;
            }

            if (placed.Side == FloatSide.Left) {
                left = MathF.Max(left, placed.Right);
            } else {
                right = MathF.Min(right, placed.Left);
            }
        }

        return (left, right);
    }

    /// <summary>The next slice boundary below <paramref name="top" />, or NaN if there is none.</summary>
    float NextFloatBottomBelow(float top, float height) {
        var bottom = top + MathF.Max(0f, height);
        var next = float.NaN;

        for (var i = floatScopeStart; i < floatExclusions.Count; i++) {
            var placed = floatExclusions[i];

            if (placed.Bottom <= top || placed.Top >= bottom) {
                continue;
            }

            if (float.IsNaN(next) || placed.Bottom < next) {
                next = placed.Bottom;
            }
        }

        return next;
    }

    /// <summary>
    ///     The first slice at or below <paramref name="top" /> with room for a box this wide.
    /// </summary>
    /// <remarks>
    ///     §9.5.1's rules 1, 2, 3 and 6 in one loop: try the requested position, and if the band there
    ///     is too narrow, drop to the next float's bottom edge and try again. A box wider than the
    ///     context itself never fits, and the loop ends when no float crosses the slice any more —
    ///     which is the overflow §9.5.1 explicitly permits rather than a failure.
    /// </remarks>
    float FirstFloatBandFitting(float top, float width, float height) {
        var y = top;

        while (true) {
            var (left, right) = FloatBandAt(y, height);

            if (right - left >= width) {
                return y;
            }

            var next = NextFloatBottomBelow(y, height);

            if (float.IsNaN(next) || next <= y) {
                return y;
            }

            y = next;
        }
    }

    /// <summary>How far down a box has to start to have cleared what it named, or NaN for nothing.</summary>
    /// <param name="clear">The sides being cleared.</param>
    /// <returns>The clearance point, in the current container's coordinates.</returns>
    float ClearancePoint(Clear clear) {
        if (clear == Clear.None) {
            return float.NaN;
        }

        var point = float.NaN;

        for (var i = floatScopeStart; i < floatExclusions.Count; i++) {
            var placed = floatExclusions[i];

            var relevant = clear == Clear.Both
                || (clear == Clear.Left && placed.Side == FloatSide.Left)
                || (clear == Clear.Right && placed.Side == FloatSide.Right);

            if (relevant && (float.IsNaN(point) || placed.Bottom > point)) {
                point = placed.Bottom;
            }
        }

        return float.IsNaN(point) ? float.NaN : point - floatOriginY;
    }

    /// <summary>§9.5.1 rule 3: no float starts higher than one that came before it.</summary>
    float HighestPermittedFloatTop() {
        var top = float.NegativeInfinity;

        for (var i = floatScopeStart; i < floatExclusions.Count; i++) {
            top = MathF.Max(top, floatExclusions[i].Top);
        }

        return top;
    }

    /// <summary>Lays a floated child out, places it, and adds it to the exclusion list.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A float's <c>width: auto</c> is shrink-to-fit, not fill.</b> §10.3.5 sends a float
    ///         down the same path a table cell and an absolute box take, which is why the width mode
    ///         below is <see cref="SizingMode.FitContent" /> where an in-flow block-level sibling
    ///         would get <see cref="SizingMode.StretchFit" />. <c>float_shrink_to_fit_contains_floats</c>
    ///         puts four 120-point floats in a 400-point parent and expects the auto-width float
    ///         around them to come out 400 and two rows tall: max-content clamped by what was offered,
    ///         which is the definition and not an approximation of it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The float is placed against its own <c>clear</c> first and its width second.</b>
    ///         §9.5.2 applies to floats as much as to in-flow boxes — <c>float_clear_no_preceding_float</c>
    ///         is two right floats where the second clears the first — so clearance raises the search
    ///         floor before rule 3 and the band search ever run.
    ///     </para>
    /// </remarks>
    /// <param name="child">The floated node.</param>
    /// <param name="direction">The container's inline direction.</param>
    /// <param name="innerWidth">The container's content-box width.</param>
    /// <param name="innerHeightForPercentages">Its definite content height, or NaN.</param>
    /// <param name="flowTop">Where the box would have gone in flow, container-relative.</param>
    /// <param name="performLayout">Whether positions are being written.</param>
    /// <param name="currentDepth">The recursion guard's counter.</param>
    void PlaceFloatChild(
        int child,
        Direction direction,
        float innerWidth,
        float innerHeightForPercentages,
        float flowTop,
        bool performLayout,
        int currentDepth
    ) {
        var marginStart = StyleResolution.InlineStartMargin(in styles[child], FlexDirection.Row, direction, innerWidth);
        var marginEnd = StyleResolution.InlineEndMargin(in styles[child], FlexDirection.Row, direction, innerWidth);
        var marginTop = StyleResolution.InlineStartMargin(in styles[child], FlexDirection.Column, direction, innerWidth);
        var marginBottom = StyleResolution.InlineEndMargin(in styles[child], FlexDirection.Column, direction, innerWidth);

        var box = ResolveBlockChildBox(child, direction, innerWidth, innerHeightForPercentages);

        var offered = MathF.Max(0f, innerWidth - marginStart - marginEnd);

        float availableWidth;
        SizingMode widthMode;

        if (float.IsNaN(box.Width)) {
            availableWidth = offered + marginStart + marginEnd;
            widthMode = SizingMode.FitContent;
        } else {
            availableWidth = ClampBlockChildAxis(box.Width, box.MinWidth, box.MaxWidth) + marginStart + marginEnd;
            widthMode = SizingMode.StretchFit;
        }

        var availableHeight = float.NaN;
        var heightMode = SizingMode.MaxContent;

        if (!float.IsNaN(box.Height)) {
            availableHeight = ClampBlockChildAxis(box.Height, box.MinHeight, box.MaxHeight) + marginTop + marginBottom;
            heightMode = SizingMode.StretchFit;
        }

        CalculateLayoutInternal(
            child,
            availableWidth,
            availableHeight,
            direction,
            widthMode,
            heightMode,
            innerWidth,
            innerHeightForPercentages,
            performLayout,
            currentDepth
        );

        var borderWidth = results[child].MeasuredDimensions[(int) Dimension.Width];
        var borderHeight = results[child].MeasuredDimensions[(int) Dimension.Height];

        var usedLeftMargin = direction == Direction.Ltr ? marginStart : marginEnd;
        var usedRightMargin = direction == Direction.Ltr ? marginEnd : marginStart;

        var marginWidth = borderWidth + usedLeftMargin + usedRightMargin;
        var marginHeight = borderHeight + marginTop + marginBottom;

        // Container-relative to context-relative, then §9.5.2 and §9.5.1 rule 3 raise the floor.
        var top = floatOriginY + flowTop;
        var clearance = ClearancePoint(styles[child].Clear);

        if (!float.IsNaN(clearance)) {
            top = MathF.Max(top, floatOriginY + clearance);
        }

        var permitted = HighestPermittedFloatTop();

        if (!float.IsNegativeInfinity(permitted)) {
            top = MathF.Max(top, permitted);
        }

        top = FirstFloatBandFitting(top, marginWidth, marginHeight);

        var (bandLeft, bandRight) = FloatBandAt(top, marginHeight);

        var marginLeft = styles[child].Float == FloatSide.Left ? bandLeft : bandRight - marginWidth;

        floatExclusions.Add(
            new PlacedFloat(styles[child].Float, marginLeft, marginLeft + marginWidth, top, top + marginHeight)
        );

        if (!performLayout) {
            return;
        }

        results[child].Position[(int) Edge.Left] = marginLeft + usedLeftMargin - floatOriginX
            + (direction == Direction.Ltr
                ? RelativePosition(child, FlexDirection.Row, direction, innerWidth)
                : -RelativePosition(child, FlexDirection.Row, direction, innerWidth));

        results[child].Position[(int) Edge.Top] = top + marginTop - floatOriginY
            + RelativePosition(child, FlexDirection.Column, direction, innerHeightForPercentages.OrZero());

        results[child].Margin[(int) Edge.Left] = usedLeftMargin;
        results[child].Margin[(int) Edge.Right] = usedRightMargin;
    }

    /// <summary>Where a float-avoiding box goes, and how wide it is allowed to be.</summary>
    /// <param name="Top">Its border-box top, container-relative.</param>
    /// <param name="Left">Its border-box left offset from the container's content edge.</param>
    /// <param name="Width">Its border-box width, when the box asked for <c>auto</c>.</param>
    readonly record struct FloatAvoidance(float Top, float Left, float Width);

    /// <summary>
    ///     Slides a box that establishes a formatting context out from under the floats beside it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is the rule people do not know is a rule.</b> §9.5: a float overlaps the
    ///         border box of a normal block-level box and only shortens its <i>line</i> boxes — but a
    ///         box that establishes a block formatting context of its own may not overlap the float's
    ///         margin box at all. So <c>overflow: hidden</c> and <c>display: flow-root</c> quietly
    ///         change a sibling from "text wraps around the float" to "the whole box moves", which is
    ///         what all ten <c>float_bfc_*</c> families and <c>block_flow_root_avoids_sibling_float</c>
    ///         are written to pin.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An <c>auto</c> width narrows and a stated one moves down, and the two are not the
    ///         same test.</b> <c>float_bfc_narrows_beside_float</c> and <c>float_bfc_moves_below_float</c>
    ///         are the same shape with and without a <c>width</c>: the first comes out 50 wide beside
    ///         the float, the second keeps its 200 and goes underneath. So the stated width is asked
    ///         to fit and the automatic one is simply told what it gets.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The inline-start margin is absorbed by the slide, and the end margin is not.</b>
    ///         <c>float_bfc_positive_margin_absorbed_by_float</c> puts a 20-point left margin beside a
    ///         50-point float and Chrome answers x=50, not x=70 — the margin is spent getting clear
    ///         rather than added to the clearing. <c>float_bfc_trailing_margin_fixed_width</c> is its
    ///         opposite number: a 10-point right margin that pushes the margin box past the band and
    ///         is nonetheless allowed to stay, because §9.5's non-overlap rule names the border box.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A large enough negative start margin gives up and goes below.</b> The margin box
    ///         has to survive the slide as a real rectangle: shifting the border edge to the band's
    ///         left edge moves the margin edge to <c>border − margin</c>, and once that passes the
    ///         band's right edge there is nothing left to sit in.
    ///         <c>float_bfc_negative_margin_beside_float</c> at −20 fits and
    ///         <c>float_bfc_large_negative_margin_moves_below_float</c> at −60 does not, in a band 50
    ///         wide, which is where the comparison comes from.
    ///     </para>
    /// </remarks>
    /// <param name="direction">The container's inline direction.</param>
    /// <param name="flowTop">Where margin collapsing put the box, container-relative.</param>
    /// <param name="statedWidth">Its border-box width if the style gave one, or NaN.</param>
    /// <param name="height">Its border-box height.</param>
    /// <param name="marginLeft">Its physical left margin.</param>
    /// <param name="marginRight">Its physical right margin.</param>
    /// <returns>Where it ends up and how wide it may be.</returns>
    FloatAvoidance AvoidFloats(
        Direction direction,
        float flowTop,
        float statedWidth,
        float height,
        float marginLeft,
        float marginRight
    ) {
        var y = floatOriginY + flowTop;

        while (true) {
            if (FitsBesideFloats(direction, y, statedWidth, height, marginLeft, marginRight)) {
                break;
            }

            var next = NextFloatBottomBelow(y, height);

            if (float.IsNaN(next) || next <= y) {
                break;
            }

            y = next;
        }

        var top = y - floatOriginY;
        var (bandLeft, bandRight) = FloatBandAt(y, height);

        // ⚠ A box that ended up clear of every float is NOT a float-avoiding box any more, and saying
        // so is what `float_bfc_large_negative_margin_moves_below_float` turns on: once it is below,
        // §10.3.3 puts its −60 margin back and it is 160 wide, where the absorbing rule would have
        // pinned it to the content edge at 100. NaN is the signal, and the caller reads it as "use the
        // ordinary inline-axis rule".
        if (bandLeft <= 0f && bandRight >= floatContextWidth) {
            return new FloatAvoidance(top, float.NaN, float.NaN);
        }

        // ⚠ The ABSORPTION is physical and does not consult `direction`, which is the half that looks
        // like an oversight and is not. Seven of the ten `float_bfc_*` families ship an RTL variant
        // whose input AND expectations are byte-identical to the LTR one — the float is on the left
        // and the box moves right in both — because §9.5 names the float's SIDE rather than the
        // container's inline start. Every one of the three that do differ states a `width`, which is
        // the only thing the anchor below can change.
        if (direction == Direction.Rtl && !float.IsNaN(statedWidth)) {
            return new FloatAvoidance(top, MathF.Min(bandRight, floatContextWidth - marginRight) - statedWidth, statedWidth);
        }

        var borderLeft = bandLeft > 0f ? MathF.Max(bandLeft, marginLeft) : marginLeft;
        var width = float.IsNaN(statedWidth) ? MathF.Max(0f, bandRight - borderLeft - marginRight) : statedWidth;

        return new FloatAvoidance(top, borderLeft, width);
    }

    /// <summary>Whether a float-avoiding box can sit in the band at this height.</summary>
    /// <remarks>
    ///     Two questions, and they are asked of different edges. A stated width has to fit between the
    ///     band's edges as a BORDER box — the far margin is allowed to hang out, which is what
    ///     <c>float_bfc_trailing_margin_fixed_width</c> pins at 50 + 10 in a band of 50. An automatic
    ///     width always fits, unless a negative near margin would drag the box's own margin edge past
    ///     the far side of the band and leave no rectangle to occupy: −20 in a band of 50 fits and −60
    ///     does not, which is the whole difference between two fixtures that are otherwise identical.
    /// </remarks>
    bool FitsBesideFloats(Direction direction, float y, float statedWidth, float height, float marginLeft, float marginRight) {
        var (bandLeft, bandRight) = FloatBandAt(y, height);

        if (bandLeft <= 0f && bandRight >= floatContextWidth) {
            return true;
        }

        var borderLeft = bandLeft > 0f ? MathF.Max(bandLeft, marginLeft) : marginLeft;

        // The physical test, and the only one an `auto` width has to pass: absorbing the near margin
        // moves the box's own margin edge to `border − margin`, and once that is past the far side of
        // the band the rectangle has inverted and there is nothing to sit in.
        if (borderLeft - marginLeft > bandRight) {
            return false;
        }

        if (float.IsNaN(statedWidth)) {
            return true;
        }

        // ⚠ A STATED width is the one place direction gets a say, and it is worth being exact about
        // why. An auto-width box fills whatever the band leaves, so which end it is anchored to
        // cannot change the answer. A box that insists on 50 points has to be put somewhere, and
        // §10.3.3 puts it at the inline start — the left of the band in LTR, the right in RTL. That is
        // the entire difference between the two halves of `float_bfc_trailing_margin_fixed_width`,
        // which have byte-identical input and answer x=50 beside the float in LTR and x=40 below it
        // in RTL, because the 10-point margin is on the anchored side in one and the free side in the
        // other.
        if (direction == Direction.Ltr) {
            return borderLeft + statedWidth <= bandRight;
        }

        return MathF.Min(bandRight, floatContextWidth - marginRight) - statedWidth >= bandLeft;
    }
}
