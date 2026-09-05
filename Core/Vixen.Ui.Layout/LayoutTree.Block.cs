// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Layout;

/// <summary>
///     The block layout algorithm: CSS 2.1 §9.4.1 block formatting contexts and §10.3.3 the
///     inline-axis constraint, over the same store the flex algorithm runs on.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is the store's second algorithm, and the point of writing it before grid was to
///         find out what a second algorithm costs.</b> The answer is in three places and nowhere else:
///         the two <see cref="CollapsibleMargin" /> fields plus a flag that
///         <see cref="LayoutResult" /> now carries out of a layout, the matching fields on
///         <see cref="CachedMeasurement" /> so a cache hit replays them, and one dispatch in
///         <c>CalculateLayoutImpl</c>. Nothing about the arena, the dirty flags, the measure cache or
///         the rounding pass had to change. Grid inherits that.
///     </para>
///     <para>
///         ⚠ <b>Block is not flex-column-with-stretch, and the difference is margin collapsing.</b>
///         CSS 2.1 §8.3.1 makes two adjoining vertical margins into one; CSS Flexbox §9.5 explicitly
///         says flex item margins do not collapse. Approximating block as a stretch flex column is
///         close enough to look right on a mock-up and wrong on the first two stacked cards that both
///         carry a margin — which is why this implements collapsing rather than shipping the
///         approximation under the name <c>block</c>. Sibling collapse, parent/first-child collapse,
///         parent/last-child collapse and collapse-<i>through</i> an empty box are all here, and
///         <c>Taffy/Corpus/block.xml</c> has about 180 fixtures whose only subject is which of them
///         applies.
///     </para>
///     <para>
///         ⚠ <b>Floats arrived, and this file's own prediction about where they would enter was
///         half right — which is worth keeping rather than quietly correcting.</b> It named two
///         points: the intrinsic-width probe in <see cref="DetermineBlockContentWidth" />, which
///         would route a floated child's width into an accumulator instead of the running maximum,
///         and the in-flow walk, which would ask a float context for a content slot. The first is
///         exactly right and is four lines. The second is where the estimate went wrong: a float
///         context is not something the walk <i>asks</i>, it is something the walk has to
///         <i>position itself inside</i>, and that turned into a probe pass per child, a saved and
///         restored origin, a suppressed layout cache and the whole of
///         <c>LayoutTree.Floats.cs</c>. The claim that nothing here cached "the inner width
///         is the whole content box" held up; the claim that both changes were additive did not.
///     </para>
///     <para>
///         ⚠ <b>And the 84 fixtures it was measuring itself against turn out not to test the thing
///         floats are famous for.</b> There is no <c>&lt;text&gt;</c> anywhere in
///         <c>Taffy/Corpus/float.xml</c>: every fixture is block-level, and not one of them puts a
///         line box beside a float. So "a float would narrow a line box", which is how the paragraph
///         above described the whole feature, is the part that is still missing —
///         <c>LayoutTree.Inline</c> has no exclusion awareness at all. See
///         <c>Taffy/FloatKnownGaps.txt</c>, which is empty of failures and is mostly about that.
///     </para>
///     <para>
///         ⚠ <b>Inline formatting arrived with doc 43 § B3, and this file is now reached only when a
///         container's children are <i>not</i> all inline-level.</b> The dispatch asks
///         <c>EstablishesInlineFormattingContext</c> first: a block container whose every in-flow
///         child is inline-level flows them onto line boxes in <c>LayoutTree.Inline</c> instead.
///     </para>
///     <para>
///         ⚠ <b>And <i>mixed</i> content is this file's business, which it did not use to be.</b> CSS
///         2.1 §9.2.1.1 wraps each run of a block container's inline-level children in an
///         <i>anonymous block box</i>, so <c>&lt;div&gt;text&lt;p/&gt;more&lt;/div&gt;</c> stacks three
///         block-level boxes of which two have no node behind them. That used to be recorded as needing
///         somewhere to <i>put</i> a box, and it does not: an anonymous block box takes initial values
///         for every non-inherited property, so it is never painted and never hit-tested and has no
///         stored rectangle at all. <see cref="WalkBlockChildren" /> flows each run through the same
///         <c>WalkInlineLines</c> the pure case uses, over a sub-range of the child list.
///     </para>
/// </remarks>
public sealed partial class LayoutTree {
    /// <summary>
    ///     Whether a node establishes a block formatting context of its own, per CSS 2.1 §9.4.1.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b><c>overflow: hidden</c> counts, and that is the rule people are surprised by.</b> Any
    ///     value of <c>overflow</c> other than <c>visible</c> makes a new formatting context, which is
    ///     why the "add <c>overflow: hidden</c>" folk remedy stops a child's margin escaping its
    ///     parent. Eight fixtures in the block corpus are that exact story
    ///     (<c>margin_y_*_collapse_blocked_by_overflow_{x,y}_{hidden,scroll}</c>), and reading only
    ///     <see cref="Overflow.Scroll" /> here — which is what the §4.5 opt-out one file over reads —
    ///     fails every one of them.
    ///
    ///     ⚠ <b>An inline-level box is one too, and that clause arrived with B3.</b> CSS Display §2.1
    ///     makes <c>inline-block</c> and <c>inline-flex</c> <i>flow-root</i> boxes: they establish a
    ///     formatting context of their own, so nothing inside them collapses a margin out through
    ///     their edges. Without this an <c>inline-block</c> would still be barred from collapsing by
    ///     <see cref="BlockMarginsCollapsibleWithParent" />'s <c>Display.Block</c> test, but its own
    ///     <i>collapse-through</i> answer would be computed as though it were transparent — so an
    ///     empty one between two margins would let them meet through it, which is the one thing a
    ///     formatting context root exists to stop.
    ///
    ///     ⚠ <b>And <see cref="Display.FlowRoot" /> is the keyword that asks for this and nothing
    ///     else, which is why it cannot be derived from the two clauses above it.</b> Every other
    ///     route to a new formatting context is a side effect of something: clipping, floating,
    ///     going inline. A <c>flow-root</c> with <c>overflow: visible</c> reads as
    ///     <c>false</c> on both of them and must still answer <c>true</c> —
    ///     <c>block_flow_root_margin_non_collapse</c> puts one beside a plain <c>block</c> with
    ///     identical content and Chrome makes the first 10 points tall and the second 60.
    ///
    ///     ⚠ <b>And a float is one, which is the clause that keeps the list from eating itself.</b>
    ///     §9.4.1 makes every floated box a formatting context root, so the floats inside a float are
    ///     invisible outside it and its own margins collapse with nothing. Both halves are load-bearing:
    ///     <c>float_shrink_to_fit_contains_floats</c> is an auto-width float whose height comes
    ///     entirely from the four floats inside it, which only works if it contains them, and the
    ///     alternative reading — a float as a plain out-of-flow box — would let a float's child float
    ///     narrow a box in the parent context that is nowhere near it.
    /// </remarks>
    bool EstablishesBlockFormattingContext(int index) =>
        styles[index].OverflowX != Overflow.Visible
        || styles[index].OverflowY != Overflow.Visible
        || IsInlineLevel(styles[index].Display)
        || styles[index].Display == Display.FlowRoot
        || styles[index].Float != FloatSide.None
        // CSS Containment § 3.1 and § 3.3 in one clause: both `layout` and `paint` make the box an
        // independent formatting context, which here is what stops a child's margin collapsing out
        // through it and what keeps a float inside it. `size` does not — a box may size itself as
        // if empty and still be part of its parent's flow.
        || (styles[index].Containment & (Containment.Layout | Containment.Paint)) != 0;

    /// <summary>
    ///     Whether this node's vertical margins are allowed to collapse with its parent's.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Derived rather than threaded, which is why <c>CalculateLayoutInternal</c> did not need
    ///     a new parameter.</b> The reference implementation passes this down as a layout input, and
    ///     that would have made it part of the measurement cache key. It does not need to be: it is a
    ///     function of the node's own style and its parent's, both of which are fixed for the whole
    ///     pass, so the same node is asked the same question every time it is laid out. A tree
    ///     mutation that could change the answer dirties the node anyway.
    ///
    ///     ⚠ <b>Both <c>Display.Block</c> tests below stay exactly that and do NOT grow an
    ///     <c>or FlowRoot</c>, which is the one place the new keyword reads as an omission and is
    ///     not one.</b> A flow root's margins do not collapse with its children's in either
    ///     direction: as the <i>node</i> it is barred by the clause below it as well, and as the
    ///     <i>parent</i> there is no other clause, so the literal test is what stops a child's
    ///     margin escaping through it.
    /// </remarks>
    bool BlockMarginsCollapsibleWithParent(int index) {
        var parent = links[index].Parent;

        return parent >= 0
            && styles[parent].Display == Display.Block
            && styles[index].Display == Display.Block
            && styles[index].PositionType != PositionType.Absolute
            && !EstablishesBlockFormattingContext(index);
    }

    /// <summary>Lays a block container out, and reports what its margins collapsed to.</summary>
    /// <remarks>
    ///     The shape mirrors <c>CalculateLayoutImpl</c>'s: the inline size is settled first because
    ///     every child stretches to it and percentages resolve against it, then the children are
    ///     walked once in the block direction accumulating a running collapsible margin, then the
    ///     block size falls out of where the walk finished.
    /// </remarks>
    void CalculateBlockLayoutImpl(
        int index,
        float availableWidth,
        float availableHeight,
        Direction direction,
        SizingMode widthSizingMode,
        SizingMode heightSizingMode,
        float ownerWidth,
        float ownerHeight,
        bool performLayout,
        int currentDepth,
        float marginAxisRow,
        float marginAxisColumn
    ) {
        var insetLeft = results[index].Padding[(int) Edge.Left] + results[index].Border[(int) Edge.Left]
            + ScrollbarGutterAt(index, Edge.Left, direction);
        var insetRight = results[index].Padding[(int) Edge.Right] + results[index].Border[(int) Edge.Right]
            + ScrollbarGutterAt(index, Edge.Right, direction);
        var insetTop = results[index].Padding[(int) Edge.Top] + results[index].Border[(int) Edge.Top]
            + ScrollbarGutterAt(index, Edge.Top, direction);
        var insetBottom = results[index].Padding[(int) Edge.Bottom] + results[index].Border[(int) Edge.Bottom]
            + ScrollbarGutterAt(index, Edge.Bottom, direction);
        var insetRow = insetLeft + insetRight;
        var insetColumn = insetTop + insetBottom;

        // ⚠ The floors below are padding and border WITHOUT the scrollbar gutter, and the difference
        // is a fixture rather than a nicety. `*_overflow_scrollbars_overridden_by_max_size` puts a
        // 15pt bar under a `max-height: 4px` and Chrome answers 4: a box may be laid out smaller
        // than its own scrollbar — the bar simply covers everything — but never smaller than its own
        // edges. Flooring at `insetRow`/`insetColumn` would let the gutter win an argument the
        // author's `max-*` is supposed to.
        var paddingBorderRow = StyleResolution.PaddingAndBorderForAxis(in styles[index], FlexDirection.Row, direction, ownerWidth);
        var paddingBorderColumn = StyleResolution.PaddingAndBorderForAxis(in styles[index], FlexDirection.Column, direction, ownerWidth);

        var collapsibleWithParent = BlockMarginsCollapsibleWithParent(index);
        var isFormattingContextRoot = !collapsibleWithParent;

        // ── The inline axis: CSS 2.1 §10.3.3 ────────────────────────────────────────────────────
        // `width: auto` on a block box in normal flow is *not* shrink-to-fit; it is whatever makes
        // the equation balance, i.e. the containing block's width. So a StretchFit request is taken
        // whole, and only a container whose own width is being asked for — a flex item, a float, an
        // absolute box, the root under a max-content viewport — falls through to the content probe.
        // ⚠ The raw figure is kept beside the bounded one because CSS Flexbox §9.2's flex base size
        // is this measurement BEFORE the box's own min and max — see
        // LayoutResult.UnclampedMeasuredDimensions. A block container is a flex item as often as
        // any other box, so its two answers have to be recorded like everybody else's.
        float rawWidth;
        if (widthSizingMode == SizingMode.StretchFit) {
            rawWidth = availableWidth - marginAxisRow;
        } else {
            var probeWidth = float.IsNaN(availableWidth) ? float.NaN : availableWidth - marginAxisRow - insetRow;

            // ⚠ <b>This probe runs BEFORE this box opens its own exclusion scope, so anything it
            // places has to be taken back.</b> Asking a child how wide it wants to be lays the child
            // out, and laying a block container out appends its floats to whatever context is
            // current — which, here, is still the ENCLOSING one, at an origin that belongs to a box
            // whose position has not been decided. Left alone those entries would narrow a real box
            // somewhere above, for a measurement that was never a placement. A child that opens a
            // scope of its own already cleans up after itself; this covers the ones that do not.
            var mark = floatExclusions.Count;

            var contentWidth = DetermineBlockContentWidth(
                index,
                direction,
                widthSizingMode,
                probeWidth,
                ownerWidth,
                ownerHeight,
                currentDepth
            );

            if (treeHasFloats) {
                floatExclusions.RemoveRange(mark, floatExclusions.Count - mark);
            }

            rawWidth = contentWidth + insetRow;
        }

        var outerWidth = BoundAxis(index, FlexDirection.Row, direction, rawWidth, ownerWidth, ownerWidth);

        var innerWidth = MathF.Max(0f, outerWidth - insetRow);

        // What a child's percentage height resolves against: this container's own definite inner
        // height, or nothing. An indefinite one makes a percentage height behave as `auto`
        // (CSS Sizing §5.2.1), which is the same rule the flex path applies one file over.
        var definiteHeight = heightSizingMode == SizingMode.StretchFit
            ? availableHeight - marginAxisColumn
            : ResolvedDimension(index, Dimension.Height, ownerHeight, ownerWidth, direction);

        var innerHeightForPercentages = float.IsNaN(definiteHeight) ? float.NaN : MathF.Max(0f, definiteHeight - insetColumn);

        // ── Whether this box's own margins take part ────────────────────────────────────────────
        // §8.3.1: a margin collapses with its parent's only when nothing separates them — no border,
        // no padding, and no new formatting context. The bottom pair additionally needs the parent's
        // height to be `auto`, because a fixed height *is* the separation.
        var styleHeight = ResolvedDimension(index, Dimension.Height, ownerHeight, ownerWidth, direction);
        var collapseWithFirstChild = collapsibleWithParent && insetTop == 0f;
        var collapseWithLastChild = collapsibleWithParent && insetBottom == 0f && float.IsNaN(styleHeight);

        var minHeight = StyleResolution.ResolvedMinDimension(in styles[index], Dimension.Height, ownerHeight, ownerWidth, direction);

        // §8.3.1's "collapse through": a box with nothing to separate its own top margin from its own
        // bottom one is not a barrier at all, and the margins on either side of it meet.
        var preventedFromCollapsingThrough = isFormattingContextRoot
            || styles[index].PositionType == PositionType.Absolute
            || insetTop > 0f
            || insetBottom > 0f
            || (!float.IsNaN(styleHeight) && styleHeight > 0f)
            || (!float.IsNaN(minHeight) && minHeight > 0f)
            || !float.IsNaN(styles[index].AspectRatio);

        // ── The block axis ──────────────────────────────────────────────────────────────────────
        // ⚠ A formatting context root opens an exclusion scope of its own, and §10.6.3's last clause
        // then makes its `height: auto` tall enough to hold what it caught. `isFormattingContextRoot`
        // is the right gate and not an approximation of one: it is false for exactly the case that
        // continues the parent's context — a plain block child of a plain block — which is also
        // exactly the case that shares the parent's floats.
        var floatScope = treeHasFloats && isFormattingContextRoot
            ? BeginFloatContext(insetLeft, insetTop, innerWidth)
            : default;

        var walk = WalkBlockChildren(
            index,
            direction,
            outerWidth,
            innerWidth,
            innerHeightForPercentages,
            insetLeft,
            insetRight,
            insetTop,
            insetBottom,
            collapseWithFirstChild,
            collapseWithLastChild,
            performLayout,
            currentDepth
        );

        var intrinsicHeight = walk.ContentHeight;

        if (treeHasFloats && isFormattingContextRoot) {
            var lowest = LowestFloatBottom();

            if (!float.IsNegativeInfinity(lowest)) {
                intrinsicHeight = MathF.Max(intrinsicHeight, lowest + insetBottom);
            }

            EndFloatContext(in floatScope);
        }

        var rawHeight = heightSizingMode == SizingMode.StretchFit ? availableHeight - marginAxisColumn : intrinsicHeight;
        var outerHeight = BoundAxis(index, FlexDirection.Column, direction, rawHeight, ownerHeight, ownerWidth);
        var unboundedHeight = MathF.Max(rawHeight, paddingBorderColumn);

        // A scroll container's fit-content height is the room it was offered, not the room its
        // content wants — the same one-axis reading the flex path applies at its STEP 9.
        if (heightSizingMode == SizingMode.FitContent
            && OverflowOn(index, Dimension.Height) == Overflow.Scroll
            && !float.IsNaN(availableHeight)) {
            outerHeight = MathF.Max(
                MathF.Min(
                    availableHeight - marginAxisColumn,
                    BoundAxisWithinMinAndMax(index, direction, FlexDirection.Column, intrinsicHeight, ownerHeight, ownerWidth)
                ),
                paddingBorderColumn
            );
            unboundedHeight = outerHeight;
        }

        SetMeasuredDimension(index, Dimension.Width, outerWidth, MathF.Max(rawWidth, paddingBorderRow));
        SetMeasuredDimension(index, Dimension.Height, outerHeight, unboundedHeight);

        // ── align-content, as one subject ───────────────────────────────────────────────────────
        // CSS Box Alignment §5.3 applies to block containers too, and a block container's whole
        // in-flow stack is a *single* alignment subject rather than a series of them — so the
        // distribution keywords all fall back and the group moves by one offset.
        if (performLayout && walk.InFlowCount > 0 && styles[index].AlignContent != Align.FlexStart) {
            var freeSpace = (outerHeight - insetColumn) - (intrinsicHeight - insetColumn);
            var offset = SingleSubjectAlignmentOffset(
                SafeFallback(styles[index].AlignContent, styles[index].AlignContentOverflow, freeSpace),
                freeSpace
            );

            if (offset != 0f) {
                foreach (var child in ChildIds(index)) {
                    if (styles[child].Display != Display.None && styles[child].PositionType != PositionType.Absolute) {
                        results[child].Position[(int) Edge.Top] += offset;
                    }
                }
            }
        }

        // ── What this box reports upward ────────────────────────────────────────────────────────
        var ownTopMargin = CollapsibleMargin.From(results[index].Margin[(int) Edge.Top]);
        var ownBottomMargin = CollapsibleMargin.From(results[index].Margin[(int) Edge.Bottom]);

        results[index].TopCollapsibleMargin = collapseWithFirstChild ? walk.FirstChildTopMargin : ownTopMargin;
        results[index].BottomCollapsibleMargin = collapseWithLastChild ? walk.LastChildBottomMargin : ownBottomMargin;
        results[index].MarginsCollapseThrough = !preventedFromCollapsingThrough && walk.AllChildrenCollapseThrough;

        // ⚠ A mixed container can have a line box of its own now, and §10.8.1 makes that its
        // baseline. Only when the last in-flow thing in it was an anonymous block box, though: after
        // a real block-level child the last line box in normal flow is somewhere inside that child,
        // and this store has no way to reach it that is better than CSS Align §9.3's synthesis. NaN
        // is the honest "I have no line boxes" that `CalculateBaseline` already knows how to read,
        // and it is what `CalculateLayoutImpl` wrote on the way in.
        if (!float.IsNaN(walk.TrailingLineBaseline)) {
            results[index].InlineBaseline = walk.TrailingLineBaseline;
        }

        if (!performLayout) {
            return;
        }

        // Absolute descendants last, from the containing block down — the same call and the same
        // condition the flex path ends on.
        if (EstablishesAbsoluteContainingBlock(index) || currentDepth == 1) {
            LayoutAbsoluteDescendants(
                index,
                index,
                widthSizingMode,
                direction,
                currentDepth,
                0f,
                0f,
                innerWidth,
                float.IsNaN(innerHeightForPercentages) ? MathF.Max(0f, outerHeight - insetColumn) : innerHeightForPercentages
            );
        }
    }

    /// <summary>What one pass over a block container's children settled.</summary>
    /// <param name="ContentHeight">The border-box height the stack of children implies.</param>
    /// <param name="FirstChildTopMargin">The margin set that escaped past the top edge.</param>
    /// <param name="LastChildBottomMargin">The margin set still hanging off the bottom edge.</param>
    /// <param name="AllChildrenCollapseThrough">Whether nothing in flow was a barrier.</param>
    /// <param name="InFlowCount">How many children took part in the flow.</param>
    /// <param name="TrailingLineBaseline">
    ///     The baseline of the last line box in normal flow, when the container's last in-flow content
    ///     was an anonymous block box; NaN otherwise.
    /// </param>
    readonly record struct BlockWalk(
        float ContentHeight,
        CollapsibleMargin FirstChildTopMargin,
        CollapsibleMargin LastChildBottomMargin,
        bool AllChildrenCollapseThrough,
        int InFlowCount,
        float TrailingLineBaseline
    );

    /// <summary>
    ///     How far a run of inline-level children reaches, per CSS 2.1 §9.2.1.1.
    /// </summary>
    /// <param name="childIds">The container's children, in order-modified document order.</param>
    /// <param name="start">The position of the run's first inline-level child.</param>
    /// <returns>One past the run's last inline-level child.</returns>
    /// <remarks>
    ///     ⚠ <b>A <c>display: none</c> or out-of-flow child does not end a run, and a run does not end
    ///     <i>on</i> one either.</b> Neither is block-level content, so neither is what §9.2.1.1 breaks
    ///     a run at — two inline-level boxes with a hidden sibling between them still share a line, and
    ///     browsers agree. But a run that swallowed a trailing hidden box would also hand it the
    ///     anonymous box's static position rather than the position after it, so the end is trimmed
    ///     back to the last child that actually goes on a line.
    /// </remarks>
    int AnonymousRunEnd(ReadOnlySpan<int> childIds, int start) {
        var end = start + 1;

        for (var i = start + 1; i < childIds.Length; i++) {
            var child = childIds[i];

            if (!ParticipatesInLine(child)) {
                continue;
            }

            // ⚠ A float does not end a run either, and this is CSS 2.1 §9.5.1's rules 5 and 6 rather
            // than a convenience. §9.7 makes a floated box block-level, which reads like "block-level
            // content ends the run" — but rules 5 and 6 place such a float at the top of the LINE it
            // was written on, not below the run that precedes it, and it then shortens that line. So
            // it belongs to the anonymous block box, and `WalkInlineLines` places it. The run's END
            // is allowed to be a float for the same reason: a trailing float sits on the last line.
            if (styles[child].Float != FloatSide.None) {
                end = i + 1;

                continue;
            }

            if (!IsInlineLevel(styles[child].Display)) {
                break;
            }

            end = i + 1;
        }

        return end;
    }

    /// <summary>Whether this child begins a run that §9.2.1.1 wraps in an anonymous block box.</summary>
    /// <remarks>
    ///     ⚠ <b>A floated child never <i>starts</i> one, and — since §9.5.1's rules 5 and 6 landed —
    ///     it does not end one either.</b> The two are not in tension. A float written between two
    ///     inline-level boxes is placed by <see cref="WalkInlineLines" /> at the top of the line it was
    ///     written on, which is what rule 6 says and what <c>InlineItemKind.Float</c> exists for; a
    ///     float with no inline-level box before it has no line to sit on, so the float branch in
    ///     <see cref="WalkBlockChildren" /> places it where the flow cursor is, which is the same
    ///     answer. What this test buys is that the second case stays reachable at all. The clause in
    ///     <c>EstablishesInlineFormattingContext</c> one file over is still absolute for a different
    ///     reason: that path has no float scope of its own to place one into.
    /// </remarks>
    bool StartsAnonymousRun(int child) =>
        ParticipatesInLine(child) && IsInlineLevel(styles[child].Display) && styles[child].Float == FloatSide.None;

    /// <summary>
    ///     Stacks the in-flow children down the block axis, collapsing margins as it goes.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The running <c>active</c> set is the whole of §8.3.1's machinery.</b> It holds the
    ///         margins that have been seen but not yet <i>spent</i> — the previous child's bottom
    ///         margin, plus the top and bottom of every zero-height box that has collapsed through
    ///         since. The next child's top margin joins that set, and only then does the set resolve
    ///         into a single offset. That is why a 10px bottom next to a 10px top advances 10 and not
    ///         20, and why an empty box between two 10s still advances only 10.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A collapsed-through child does not move <c>committed</c>.</b> Its own box is placed
    ///         where the margins so far put it — which is why the corpus expects two zero-height
    ///         siblings at the *same* y — but the cursor the next child measures from stays where the
    ///         last real box ended. Advancing it is the mistake that makes
    ///         <c>margin_y_collapse_through_positive</c> come out 10 points too tall.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A run of inline-level children is one <i>anonymous block box</i> here, and it is
    ///         the only box in this walk with no node behind it.</b> CSS 2.1 §9.2.1.1: a block
    ///         container holding both kinds of child wraps each maximal run of the inline-level ones
    ///         in an anonymous block box, so <c>&lt;div&gt;text&lt;p/&gt;more&lt;/div&gt;</c> is three
    ///         block-level boxes and not three stacked children. What that costs here is a range
    ///         rather than a store: an anonymous block box takes initial values for every
    ///         non-inherited property, so it has no background, no border, no padding, no margin and
    ///         no event target, is never painted and never hit-tested, and <b>needs no stored
    ///         rectangle</b>. Its geometry is entirely "where the run starts" and "how tall the run
    ///         came out", and the boxes it flows are written as offsets from the container that
    ///         really is their parent — which is where <see cref="WalkInlineLines" /> already put
    ///         them.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And its zero margins are why margin collapsing needed nothing.</b> A box whose top
    ///         and bottom margins are both zero contributes nothing to any adjoining set, so the run
    ///         resolves whatever was running, advances by its own height, and starts a fresh empty
    ///         set. It is never a collapse-<i>through</i> box either, however short it comes out: by
    ///         construction it holds at least one line box, which is exactly what §8.3.1 means by
    ///         something separating a box's two margins.
    ///     </para>
    /// </remarks>
    BlockWalk WalkBlockChildren(
        int index,
        Direction direction,
        float outerWidth,
        float innerWidth,
        float innerHeightForPercentages,
        float insetLeft,
        float insetRight,
        float insetTop,
        float insetBottom,
        bool collapseWithFirstChild,
        bool collapseWithLastChild,
        bool performLayout,
        int currentDepth
    ) {
        var committed = insetTop;
        var staticPositionY = insetTop;
        var firstChildTopMargin = CollapsibleMargin.Zero;
        var active = CollapsibleMargin.Zero;
        var stillCollapsingWithFirst = true;
        var allCollapseThrough = true;
        var inFlowCount = 0;
        var trailingLineBaseline = float.NaN;

        // ⚠ With no float anywhere in the tree every `floatsActive` branch below is dead and this
        // walk is byte-for-byte the one that passed 6 140 fixtures before floats existed. That is
        // deliberate: the float-active path measures each child twice and defeats the layout cache,
        // and neither cost belongs to a tree that has no floats to pay it for.
        var floatsActive = treeHasFloats;
        var containerOriginX = floatOriginX;
        var containerOriginY = floatOriginY;

        var childIds = ChildIds(index);

        for (var position = 0; position < childIds.Length; position++) {
            var child = childIds[position];

            // ── §9.2.1.1: one anonymous block box per run of inline-level children ──────────────
            if (StartsAnonymousRun(child)) {
                var runEnd = AnonymousRunEnd(childIds, position);

                inFlowCount++;

                // Its own margins are zero, so the only thing to spend is whatever was already
                // running — and nothing of it escapes past the container's top edge either, which is
                // what leaves `firstChildTopMargin` alone below.
                var runAdvance = stillCollapsingWithFirst && collapseWithFirstChild ? 0f : active.Resolve();

                var run = WalkInlineLines(
                    index,
                    position,
                    runEnd,
                    direction,
                    outerWidth,
                    innerWidth,
                    innerHeightForPercentages,
                    insetLeft,
                    insetRight,
                    committed + runAdvance,
                    performLayout,
                    currentDepth
                );

                allCollapseThrough = false;
                stillCollapsingWithFirst = false;
                committed = run.ContentHeight;
                active = CollapsibleMargin.Zero;
                staticPositionY = committed;
                trailingLineBaseline = run.LastBaseline;

                position = runEnd - 1;

                continue;
            }

            if (styles[child].Display == Display.None) {
                if (performLayout) {
                    ZeroOutLayoutRecursively(child);
                }

                continue;
            }

            if (styles[child].PositionType == PositionType.Absolute) {
                // The static position is where the box *would* have gone. Recorded now, because the
                // absolute pass runs after the walk and cannot reconstruct it.
                results[child].BlockStaticTop = staticPositionY;
                results[child].BlockStaticLeft = direction == Direction.Ltr ? insetLeft : outerWidth - insetRight;
                continue;
            }

            // ── §9.5: out of flow, but not out of the picture ──────────────────────────────────
            // ⚠ A float is placed where a box with no margins of its own would have gone — after the
            // margins seen so far are resolved — and then it moves nothing. It does not advance
            // `committed`, does not join `active`, and is not an in-flow child for `align-content` or
            // for the absolute static position. `float_between_collapsing_margins` is the fixture
            // that pins all four at once: a 16-point bottom margin and a 40-point top margin on
            // either side of a float still collapse to 40, and the float sits at the 16.
            if (floatsActive && styles[child].Float != FloatSide.None) {
                PlaceFloatChild(
                    child,
                    direction,
                    innerWidth,
                    innerHeightForPercentages,
                    committed + (stillCollapsingWithFirst && collapseWithFirstChild ? 0f : active.Resolve()),
                    insetLeft,
                    performLayout,
                    currentDepth
                );

                continue;
            }

            inFlowCount++;

            // A real block-level box after a run means the run's last line box is no longer the
            // container's last one, and this store cannot see into the child to find the new one.
            trailingLineBaseline = float.NaN;

            // Inline-relative, so one branch covers both directions; the physical mapping happens
            // once, where the position is written.
            var marginStartIsAuto = StyleResolution.InlineStartMarginIsAuto(in styles[child], FlexDirection.Row, direction);
            var marginEndIsAuto = StyleResolution.InlineEndMarginIsAuto(in styles[child], FlexDirection.Row, direction);

            // ⚠ Percentage margins resolve against the containing block's *inline* size — all four of
            // them, CSS 2.1 §8.3. A percentage `margin-top` is a fraction of the container's width,
            // not its height, which is the rule that surprises and the one
            // `margin_y_sibling_collapse_positive_percentage` is written to catch.
            var marginStart = StyleResolution.InlineStartMargin(in styles[child], FlexDirection.Row, direction, innerWidth);
            var marginEnd = StyleResolution.InlineEndMargin(in styles[child], FlexDirection.Row, direction, innerWidth);
            var marginTop = StyleResolution.InlineStartMargin(in styles[child], FlexDirection.Column, direction, innerWidth);
            var marginBottom = StyleResolution.InlineEndMargin(in styles[child], FlexDirection.Column, direction, innerWidth);

            var box = ResolveBlockChildBox(child, direction, innerWidth, innerHeightForPercentages);

            // §10.3.3 again, one level down: an in-flow block-level box with `width: auto` fills the
            // line, and its margins come out of that fill rather than being added to it. A ratio is
            // the one thing that overrides the fill — an `aspect-ratio` box with a definite height is
            // as wide as the ratio says, not as wide as the line.
            var stretchWidth = MathF.Max(0f, innerWidth - marginStart - marginEnd);
            var childWidth = MathF.Max(
                ClampBlockChildAxis(float.IsNaN(box.Width) ? stretchWidth : box.Width, box.MinWidth, box.MaxWidth),
                StyleResolution.PaddingAndBorderForAxis(in styles[child], FlexDirection.Row, direction, innerWidth)
            );

            var childHeight = float.NaN;
            var childHeightMode = SizingMode.MaxContent;
            if (!float.IsNaN(box.Height)) {
                childHeight = ClampBlockChildAxis(box.Height, box.MinHeight, box.MaxHeight) + marginTop + marginBottom;
                childHeightMode = SizingMode.StretchFit;
            } else if (!float.IsNaN(styles[child].AspectRatio) && styles[child].AspectRatio > 0f) {
                // ⚠ The transfer runs the other way too, and this direction is the one a *stretched*
                // box needs. `ResolveBlockChildBox` can only transfer from lengths the style states;
                // a `width: auto` box has none, and its width arrives from §10.3.3's fill instead —
                // which is still a definite size, so CSS Sizing §4.1 still makes the block axis
                // definite. Without this an `aspect-ratio` card that fills its column is zero high.
                var ratioHeight = ClampBlockChildAxis(
                    HeightAcrossRatio(child, direction, childWidth, innerWidth),
                    box.MinHeight,
                    box.MaxHeight
                );

                // ⚠ …and a ratio never *shrinks* a box below its own contents. CSS Sizing §5.1 gives
                // a box with a preferred aspect ratio a content-based automatic minimum size in the
                // block axis, and §5.1's other half — the one this engine already learned from eight
                // flex fixtures — makes a minimum beat a maximum. So a paragraph six lines tall under
                // `max-width: 40px; aspect-ratio: 2` is 60 points tall and not 20, even though the
                // ratio and the derived maximum both say 20. Only `min-height: auto` behaves this
                // way; a stated minimum has already been applied above and this floor is skipped.
                if (!styles[child].MinDimensions[(int) Dimension.Height].IsDefined) {
                    CalculateLayoutInternal(
                        child,
                        childWidth + marginStart + marginEnd,
                        float.NaN,
                        direction,
                        SizingMode.StretchFit,
                        SizingMode.MaxContent,
                        innerWidth,
                        innerHeightForPercentages,
                        performLayout: false,
                        currentDepth
                    );

                    ratioHeight = MathF.Max(ratioHeight, results[child].MeasuredDimensions[(int) Dimension.Height]);
                }

                childHeight = ratioHeight + marginTop + marginBottom;
                childHeightMode = SizingMode.StretchFit;
            }

            // ── §9.5.2 and §9.5, for a container that has floats to react to ───────────────────
            // ⚠ <b>The child has to be measured before it can be placed, and placed before it can be
            // laid out, and the float context is what makes those two different passes.</b> Where a
            // box goes depends on the margins that escape from inside it, which only its own layout
            // knows; where its floats go depends on where the box went. So the float-active path runs
            // a probe first, throws away the floats the probe placed, works out the position, and
            // only then lays the child out for real at an origin the exclusion list can trust.
            var clearanceApplied = false;
            var floatLeft = float.NaN;
            var advance = 0f;

            // ⚠ <b>A box that has to be measured against the exclusion list cannot also be letting a
            // margin escape past the container's top edge, because the two are the same question.</b>
            // Deciding whether a formatting context root overlaps a float means deciding where it is,
            // and deciding where it is is what spending the margin means — so §8.3.1's parent/first
            // child collapse is over for this container the moment such a child arrives.
            // <c>float_new_fc_separates</c> is the fixture that costs: its 200-point margin escaped AND
            // positioned the box, so the box came out at 200 inside a container that had itself moved
            // to 200. The container is 201 tall and sits at zero.
            var escapesTopEdge = stillCollapsingWithFirst
                && collapseWithFirstChild
                && !(floatsActive && EstablishesBlockFormattingContext(child));

            if (floatsActive) {
                var mark = floatExclusions.Count;

                floatOriginX = containerOriginX + insetLeft;
                floatOriginY = containerOriginY + committed
                    + (escapesTopEdge ? 0f : active.With(marginTop).Resolve());

                CalculateLayoutInternal(
                    child,
                    childWidth + marginStart + marginEnd,
                    childHeight,
                    direction,
                    SizingMode.StretchFit,
                    childHeightMode,
                    innerWidth,
                    innerHeightForPercentages,
                    performLayout: false,
                    currentDepth
                );

                floatExclusions.RemoveRange(mark, floatExclusions.Count - mark);

                // ⚠ Back to the CONTAINER's origin before anything is measured against the exclusion
                // list. Clearance and float avoidance both answer in container-relative points, and
                // both subtract `floatOriginY` to get there — so leaving the probe's origin in place
                // shifts every one of their answers by the margin the probe was guessing at.
                floatOriginX = containerOriginX;
                floatOriginY = containerOriginY;

                advance = escapesTopEdge
                    ? 0f
                    : active.With(results[child].TopCollapsibleMargin.With(marginTop)).Resolve();

                // ⚠ Clearance SPENDS the margin that would have positioned the box, rather than being
                // added to it. `float_forced_clearance_adjoining_float` is a 400-point top margin over
                // a 50-point float and Chrome answers 50, not 450: the margin had collapsed all the
                // way out through the container's top edge, so the box's hypothetical position was
                // zero and clearance simply replaced the whole set. Discarding it is what the tail of
                // this loop does with `clearanceApplied`.
                var clearPoint = ClearancePoint(ResolveClear(styles[child].Clear, direction));

                if (!float.IsNaN(clearPoint) && committed + advance < clearPoint) {
                    advance = clearPoint - committed;
                    clearanceApplied = true;
                }

                if (EstablishesBlockFormattingContext(child)) {
                    var avoided = AvoidFloats(
                        direction,
                        committed + advance,
                        float.IsNaN(box.Width) ? float.NaN : childWidth,
                        results[child].MeasuredDimensions[(int) Dimension.Height],
                        direction == Direction.Ltr ? marginStart : marginEnd,
                        direction == Direction.Ltr ? marginEnd : marginStart
                    );

                    advance = avoided.Top - committed;

                    // A NaN left edge means the box cleared every float on the way down and is an
                    // ordinary §10.3.3 box again — only its `Top` was decided here.
                    if (!float.IsNaN(avoided.Left)) {
                        floatLeft = avoided.Left;

                        if (float.IsNaN(box.Width)) {
                            childWidth = MathF.Max(
                                avoided.Width,
                                StyleResolution.PaddingAndBorderForAxis(in styles[child], FlexDirection.Row, direction, innerWidth)
                            );
                        }
                    }
                }

                // ⚠ <b>The origin the child's own floats are written against, and the `auto` inline
                // margin is resolved into it rather than read as its stated zero.</b> This used to
                // say resolving it needed the used width of the layout about to start — which was
                // wrong twice over. `childWidth` IS the used width: it is what the
                // <c>SizingMode.StretchFit</c> call below is handed, so the box comes out that wide
                // by construction. And the consequence was not the one recorded either: a float in a
                // block centred by `margin: 0 auto` was drawn at the centred edge and EXCLUDED at the
                // uncentred one, so the box that avoided it moved to the wrong place while the float
                // itself looked right.
                var floatAutoInline = 0f;

                if (marginStartIsAuto || marginEndIsAuto) {
                    var autoCount = (marginStartIsAuto ? 1 : 0) + (marginEndIsAuto ? 1 : 0);
                    floatAutoInline = MathF.Max(0f, stretchWidth - childWidth) / autoCount;
                }

                var floatMarginStart = marginStartIsAuto ? floatAutoInline : marginStart;
                var floatMarginEnd = marginEndIsAuto ? floatAutoInline : marginEnd;

                floatOriginX = containerOriginX
                    + insetLeft
                    + (float.IsNaN(floatLeft)
                        ? LegacyTextAlignOffset(
                            styles[index].LegacyTextAlign,
                            direction,
                            innerWidth,
                            childWidth,
                            direction == Direction.Ltr ? floatMarginStart : floatMarginEnd,
                            direction == Direction.Ltr ? floatMarginEnd : floatMarginStart
                        )
                        : floatLeft);

                floatOriginY = containerOriginY + committed + advance;
            }

            CalculateLayoutInternal(
                child,
                childWidth + marginStart + marginEnd,
                childHeight,
                direction,
                SizingMode.StretchFit,
                childHeightMode,
                innerWidth,
                innerHeightForPercentages,
                performLayout,
                currentDepth
            );

            if (floatsActive) {
                floatOriginX = containerOriginX;
                floatOriginY = containerOriginY;
            }

            var childOuterHeight = results[child].MeasuredDimensions[(int) Dimension.Height];
            var childOuterWidth = results[child].MeasuredDimensions[(int) Dimension.Width];

            // The child's own reported sets already fold in whatever escaped from *its* children.
            var topSet = results[child].TopCollapsibleMargin.With(marginTop);
            var bottomSet = results[child].BottomCollapsibleMargin.With(marginBottom);

            // §8.3.1: a box with clearance is not transparent to the margins on either side of it.
            var collapsesThrough = results[child].MarginsCollapseThrough && !clearanceApplied;

            allCollapseThrough = allCollapseThrough && collapsesThrough;

            // A margin adjoining the container's own top edge is not spent here — it escapes, and
            // the container reports it upward for its own parent to spend.
            if (!floatsActive) {
                advance = escapesTopEdge ? 0f : active.With(topSet).Resolve();
            }

            if (performLayout) {
                // Auto margins on the inline axis are what centres a block box: §10.3.3 solves the
                // over-constrained equation by giving the slack to whichever margins said `auto`.
                var freeInline = MathF.Max(0f, stretchWidth - childOuterWidth);
                var autoCount = (marginStartIsAuto ? 1 : 0) + (marginEndIsAuto ? 1 : 0);
                var autoInline = autoCount > 0 ? freeInline / autoCount : 0f;
                var usedStart = marginStartIsAuto ? autoInline : marginStart;
                var usedEnd = marginEndIsAuto ? autoInline : marginEnd;

                // ⚠ `RelativePosition` answers in inline-start terms, and this position is physical.
                // In RTL the two run opposite ways: `left: 2px` on an RTL box still moves it two
                // points to the *right*, because §9.4.3's offsets are physical even though which of
                // `left` and `right` wins when both are set is not. Hence the negation, and hence
                // `block_inset_fixed__*_rtl` reporting x = −2 where Chrome says +2 without it.
                var relativeX = direction == Direction.Ltr
                    ? RelativePosition(child, FlexDirection.Row, direction, innerWidth)
                    : -RelativePosition(child, FlexDirection.Row, direction, innerWidth);
                var relativeY = RelativePosition(child, FlexDirection.Column, direction, innerHeightForPercentages.OrZero());

                // The box sits against the inline-start edge — the left in LTR, the right in RTL —
                // and it is the inline-*start* margin that separates it from that edge either way.
                // Unless CSS Text §7.1's legacy `text-align` says otherwise, which is the one thing
                // in block layout that names a PHYSICAL edge rather than a flow-relative one.
                var usedLeft = direction == Direction.Ltr ? usedStart : usedEnd;
                var usedRight = direction == Direction.Ltr ? usedEnd : usedStart;

                // A box that had to slide out from under a float is where §9.5 put it, and §10.3.3's
                // inline-start rule no longer has a say — the slide already consumed the start margin
                // it would have applied.
                results[child].Position[(int) Edge.Left] = insetLeft
                    + (float.IsNaN(floatLeft)
                        ? LegacyTextAlignOffset(
                            styles[index].LegacyTextAlign,
                            direction,
                            innerWidth,
                            childOuterWidth,
                            usedLeft,
                            usedRight
                        )
                        : floatLeft)
                    + relativeX;
                results[child].Position[(int) Edge.Top] = committed + advance + relativeY;
                results[child].Margin[(int) (direction == Direction.Ltr ? Edge.Left : Edge.Right)] = usedStart;
                results[child].Margin[(int) (direction == Direction.Ltr ? Edge.Right : Edge.Left)] = usedEnd;
            }

            if (stillCollapsingWithFirst) {
                // ⚠ A cleared box contributes NOTHING to the set that escapes the container's top
                // edge, which is the other half of "clearance spends the margin". If the set escaped
                // as well as being spent, the 400 points of `float_forced_clearance_adjoining_float`
                // would reappear as the container's own top margin one level up. A box that had to be
                // placed against the floats is out for the same reason, one step earlier: its margin
                // was spent deciding where it goes.
                if (!clearanceApplied && escapesTopEdge) {
                    firstChildTopMargin = collapsesThrough
                        ? firstChildTopMargin.With(topSet).With(bottomSet)
                        : firstChildTopMargin.With(topSet);
                }

                stillCollapsingWithFirst = collapsesThrough && escapesTopEdge;
            }

            if (clearanceApplied && results[child].MarginsCollapseThrough) {
                // ⚠ <b>Clearance does not shorten a margin that was collapsing through the box; it
                // cuts that margin in two and sits in the gap.</b> The set keeps its whole length —
                // the part that was going to be spent above the box already has been, inside
                // `advance`, and the remainder carries on below it.
                // <c>float_clear_self_collapsing_margins</c> is a 40-over-140 pair collapsing to 140
                // across a zero-height box that clears a 100-point float, and Chrome answers a
                // 200-point container: 40 of margin, 60 of clearance, the box, and the 100 of margin
                // that is left over.
                //
                // ⚠ And none of it escapes. §8.3.1 stops a parent's bottom margin collapsing with its
                // last child's when that child's bottom margin is collapsing with a top margin that
                // HAS clearance, which is this box exactly — so the remainder goes into `committed`
                // rather than staying in `active` for the container to report upward.
                var aboveTheBox = active.With(topSet).Resolve();
                var wholeSet = active.With(topSet).With(bottomSet).Resolve();

                committed += advance + childOuterHeight + MathF.Max(0f, wholeSet - aboveTheBox);
                active = CollapsibleMargin.Zero;
                staticPositionY = committed;
            } else if (collapsesThrough) {
                // Nothing separates this box's two margins, so they join the running set and the
                // cursor does not move.
                active = active.With(topSet).With(bottomSet);
                staticPositionY = committed + advance + childOuterHeight;
            } else {
                committed += advance + childOuterHeight;
                active = bottomSet;
                staticPositionY = committed + active.Resolve();
            }
        }

        var lastChildBottomMargin = active;
        var trailing = collapseWithLastChild ? 0f : lastChildBottomMargin.Resolve();
        var contentHeight = MathF.Max(0f, committed + trailing + insetBottom);

        return new BlockWalk(
            contentHeight,
            firstChildTopMargin,
            lastChildBottomMargin,
            allCollapseThrough,
            inFlowCount,
            trailingLineBaseline
        );
    }

    /// <summary>A block child's six specified lengths, after <c>aspect-ratio</c> has had its say.</summary>
    /// <param name="Width">The border-box width, or NaN for <c>auto</c>.</param>
    /// <param name="Height">The border-box height, or NaN for <c>auto</c>.</param>
    /// <param name="MinWidth">The floor, or NaN.</param>
    /// <param name="MinHeight">The floor, or NaN.</param>
    /// <param name="MaxWidth">The ceiling, or NaN.</param>
    /// <param name="MaxHeight">The ceiling, or NaN.</param>
    readonly record struct BlockChildBox(float Width, float Height, float MinWidth, float MinHeight, float MaxWidth, float MaxHeight);

    /// <summary>
    ///     Resolves a block child's sizes, transferring each one through its aspect ratio.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The ratio transfers through the minimums and maximums too, and that is the half
    ///         people leave out.</b> CSS Sizing §4.1 says a box with a preferred aspect ratio and a
    ///         definite size in one axis has a definite size in the other — and §5 applies that to
    ///         <c>min-*</c> and <c>max-*</c> the same way. <c>block_aspect_ratio_fill_max_width</c> is
    ///         the case that only the second half solves: <c>max-height: 20px</c> with
    ///         <c>aspect-ratio: 2</c> is a <c>max-width</c> of 40, so a text box that would have
    ///         stretched to 100 is 40 wide and wraps to two lines. Nothing about its own width said so.
    ///     </para>
    ///     <para>
    ///         ⚠ The transfer happens <i>before</i> box sizing is applied, because <c>aspect-ratio</c>
    ///         describes the box named by <c>box-sizing</c> — so a <c>content-box</c> child's ratio is
    ///         a ratio of content boxes and padding is added to both sides afterwards, not folded into
    ///         one of them first.
    ///     </para>
    /// </remarks>
    BlockChildBox ResolveBlockChildBox(int child, Direction direction, float innerWidth, float innerHeight) {
        var ratio = styles[child].AspectRatio;

        var width = StyleResolution.ProcessedDimension(in styles[child], Dimension.Width).Resolve(innerWidth);
        var height = StyleResolution.ProcessedDimension(in styles[child], Dimension.Height).Resolve(innerHeight);
        var minWidth = styles[child].MinDimensions[(int) Dimension.Width].Resolve(innerWidth);
        var minHeight = styles[child].MinDimensions[(int) Dimension.Height].Resolve(innerHeight);
        var maxWidth = styles[child].MaxDimensions[(int) Dimension.Width].Resolve(innerWidth);
        var maxHeight = styles[child].MaxDimensions[(int) Dimension.Height].Resolve(innerHeight);

        if (!float.IsNaN(ratio) && ratio > 0f) {
            Transfer(ref width, ref height, ratio);
            Transfer(ref minWidth, ref minHeight, ratio);
            Transfer(ref maxWidth, ref maxHeight, ratio);
        }

        return new BlockChildBox(
            Sized(width, Dimension.Width),
            Sized(height, Dimension.Height),
            Sized(minWidth, Dimension.Width),
            Sized(minHeight, Dimension.Height),
            Sized(maxWidth, Dimension.Width),
            Sized(maxHeight, Dimension.Height)
        );

        static void Transfer(ref float across, ref float down, float ratio) {
            if (float.IsNaN(across) && !float.IsNaN(down)) {
                across = down * ratio;
            } else if (float.IsNaN(down) && !float.IsNaN(across)) {
                down = across / ratio;
            }
        }

        float Sized(float value, Dimension dimension) =>
            value < 0f ? float.NaN : StyleResolution.WithBoxSizing(in styles[child], value, dimension, innerWidth, direction);
    }

    /// <summary>Clamps a length between a minimum and a maximum, minimum last.</summary>
    /// <remarks>
    ///     CSS Sizing §5.1 expresses the rule as two clamps in that order, which is what makes the
    ///     minimum win when it is larger than the maximum. The flex path learned this the hard way —
    ///     eight fixtures — and the same ordering is repeated here rather than shared, because this
    ///     one clamps against ratio-derived bounds the style does not carry.
    /// </remarks>
    static float ClampBlockChildAxis(float value, float minimum, float maximum) {
        if (!float.IsNaN(maximum) && maximum >= 0f && value > maximum) {
            value = maximum;
        }

        if (!float.IsNaN(minimum) && minimum >= 0f && value < minimum) {
            value = minimum;
        }

        return value;
    }

    /// <summary>
    ///     The widest thing this container has to hold, for the case where its own width is being
    ///     asked for rather than imposed.
    /// </summary>
    /// <remarks>
    ///     ⚠ Absolutely positioned children are skipped: they are out of flow, so they contribute
    ///     nothing to their container's intrinsic size (CSS Sizing §5.2.2). A child's own padding and
    ///     border floor its contribution even when its measured width came out smaller.
    ///     <para>
    ///         ⚠ <b>An anonymous block box contributes as one box and not as its members.</b> The
    ///         maximum below is the right operator over block-level children and the wrong one over a
    ///         run that shares a line: three 20-point boxes beside a block sibling want 60 and this
    ///         loop would say 20, which is the run's <i>minimum</i> rather than its preferred width.
    ///         So a run is handed to the inline probe, which is where §10.3.9's three numbers live.
    ///     </para>
    /// </remarks>
    float DetermineBlockContentWidth(
        int index,
        Direction direction,
        SizingMode widthSizingMode,
        float availableInnerWidth,
        float ownerWidth,
        float ownerHeight,
        int currentDepth
    ) {
        var widest = 0f;

        // ⚠ Floats go SIDE BY SIDE, so they are the one thing in this loop that adds rather than
        // maxes — which is the accumulator this file's header predicted a float would need. A
        // container's max-content width with three 15-point floats in it is 45 and not 15;
        // `float_shrink_to_fit_contains_floats` asserts exactly that, twice, at 45 and at 480.
        // `floatWidest` is the min-content side of the same measurement — the widest single float is
        // as narrow as the run can be made by wrapping — and it is what stops the fit-content clamp
        // below from returning less than one float.
        var floatRun = 0f;
        var floatWidest = 0f;

        var childIds = ChildIds(index);

        for (var position = 0; position < childIds.Length; position++) {
            var child = childIds[position];

            if (StartsAnonymousRun(child)) {
                var runEnd = AnonymousRunEnd(childIds, position);

                widest = MathF.Max(
                    widest,
                    DetermineInlineContentWidth(
                        index,
                        position,
                        runEnd,
                        direction,
                        widthSizingMode,
                        availableInnerWidth,
                        ownerWidth,
                        ownerHeight,
                        currentDepth
                    )
                );

                position = runEnd - 1;

                continue;
            }

            if (styles[child].Display == Display.None || styles[child].PositionType == PositionType.Absolute) {
                continue;
            }

            var marginRow = StyleResolution.MarginForAxis(in styles[child], FlexDirection.Row, availableInnerWidth.OrZero());
            var paddingAndBorder = StyleResolution.PaddingAndBorderForAxis(in styles[child], FlexDirection.Row, direction, availableInnerWidth.OrZero());

            float childWidth;
            if (HasDefiniteLength(child, Dimension.Width, availableInnerWidth)) {
                childWidth = BoundAxis(child, FlexDirection.Row, direction, ResolvedDimension(child, Dimension.Width, availableInnerWidth, availableInnerWidth, direction), availableInnerWidth, availableInnerWidth);
            } else {
                var offered = float.IsNaN(availableInnerWidth) ? float.NaN : MathF.Max(0f, availableInnerWidth - marginRow);
                var mode = widthSizingMode == SizingMode.MaxContent || float.IsNaN(offered)
                    ? SizingMode.MaxContent
                    : SizingMode.FitContent;

                CalculateLayoutInternal(
                    child,
                    float.IsNaN(offered) ? float.NaN : offered + marginRow,
                    float.NaN,
                    direction,
                    mode,
                    SizingMode.MaxContent,
                    ownerWidth,
                    ownerHeight,
                    performLayout: false,
                    currentDepth
                );

                childWidth = results[child].MeasuredDimensions[(int) Dimension.Width];
            }

            var contribution = MathF.Max(childWidth, paddingAndBorder) + marginRow;

            if (treeHasFloats && styles[child].Float != FloatSide.None) {
                floatRun += contribution;
                floatWidest = MathF.Max(floatWidest, contribution);

                continue;
            }

            widest = MathF.Max(widest, contribution);
        }

        if (floatRun > 0f) {
            // Shrink-to-fit over the run: max-content is the whole row, and a fit-content request
            // takes what it was offered instead — floored at one float, because a float narrower
            // than itself is not an available wrapping.
            widest = MathF.Max(
                widest,
                widthSizingMode == SizingMode.MaxContent || float.IsNaN(availableInnerWidth)
                    ? floatRun
                    : MathF.Min(floatRun, MathF.Max(floatWidest, availableInnerWidth))
            );
        }

        return widest;
    }

    /// <summary>
    ///     How far a block-level child's left edge sits from its container's left content edge.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Physical throughout, which is why the two margins are passed as left and right
    ///         rather than as start and end.</b> CSS Text §7.1's legacy keywords are physical — a
    ///         <c>-webkit-left</c> container puts its children on the left in RTL too — and they are
    ///         the only thing in block layout that is. <c>block_item_text_align_left__border_box_rtl</c>
    ///         is the fixture: an RTL container whose 100-point child Chrome puts at x=0, where
    ///         §10.3.3 alone would have put it at x=100.
    ///     </para>
    ///     <para>
    ///         <see cref="LegacyTextAlign.None" /> is §10.3.3 unchanged, and the two arms it
    ///         reduces to are exactly the two the caller used to write inline: the inline-start edge,
    ///         which is <see cref="LegacyTextAlign.Left" />'s answer in LTR and
    ///         <see cref="LegacyTextAlign.Right" />'s in RTL.
    ///     </para>
    ///     <para>
    ///         The centre arm floors its free space at zero rather than halving a negative, which
    ///         matches the auto-margin resolution three lines above the call site and matches
    ///         §10.3.3's rule that an over-constrained <c>auto</c> margin becomes zero —
    ///         <c>-webkit-center</c> is that behaviour under another name. ⚠ No fixture in the
    ///         corpus overflows a legacy-aligned child, so this arm is reasoned rather than
    ///         measured; the three keywords' non-overflowing behaviour is what the 16 pin.
    ///     </para>
    /// </remarks>
    /// <param name="textAlign">The container's legacy alignment.</param>
    /// <param name="direction">The container's inline direction, which only <c>None</c> consults.</param>
    /// <param name="innerWidth">The container's content-box width.</param>
    /// <param name="childOuterWidth">The child's border-box width.</param>
    /// <param name="usedLeft">The child's used left margin, auto margins already resolved.</param>
    /// <param name="usedRight">Its used right margin.</param>
    /// <returns>The offset from the content box's left edge.</returns>
    static float LegacyTextAlignOffset(
        LegacyTextAlign textAlign,
        Direction direction,
        float innerWidth,
        float childOuterWidth,
        float usedLeft,
        float usedRight
    ) {
        var resolved = textAlign == LegacyTextAlign.None
            ? direction == Direction.Ltr ? LegacyTextAlign.Left : LegacyTextAlign.Right
            : textAlign;

        return resolved switch {
            LegacyTextAlign.Center => usedLeft + (MathF.Max(0f, innerWidth - usedLeft - usedRight - childOuterWidth) / 2f),
            LegacyTextAlign.Right => innerWidth - childOuterWidth - usedRight,
            _ => usedLeft
        };
    }

    /// <summary>
    ///     Where one alignment subject sits in a given amount of free space.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A block container's in-flow stack is one subject, not <i>n</i>.</b> CSS Box Alignment
    ///     §4.2's fallback rules say the distribution keywords degenerate when there is a single
    ///     subject or no free space: <c>space-between</c> and <c>stretch</c> behave as <c>start</c>,
    ///     <c>space-around</c> and <c>space-evenly</c> as <c>center</c>. Treating the children as
    ///     separate subjects instead would space them out, which no browser does — a block container
    ///     moves the whole stack or nothing.
    /// </remarks>
    static float SingleSubjectAlignmentOffset(Align alignContent, float freeSpace) =>
        alignContent switch {
            Align.Center or Align.SpaceAround or Align.SpaceEvenly => freeSpace / 2f,
            Align.FlexEnd => freeSpace,
            _ => 0f
        };
}
