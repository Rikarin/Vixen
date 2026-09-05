// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Layout;

/// <summary>
///     The inline formatting algorithm: CSS 2.1 §9.4.2 line boxes, §10.3.9 shrink-to-fit and §10.8.1
///     vertical alignment, over the same store the other three algorithms run on.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is the store's fourth algorithm, and the first one that asks for something the
///         store cannot give it.</b> The two before it each cost exactly one thing and the price was
///         recorded: block cost three <i>outputs</i>, because a child's margin may belong to its
///         parent; grid cost variable-length <i>input</i>, hence <see cref="TrackArena" /> and a
///         watermarked bump allocator. Inline costs one more output — see
///         <see cref="LayoutResult.InlineBaseline" /> — and then runs into a wall that is not a matter
///         of adding a field.
///     </para>
///     <para>
///         ⚠ <b>The wall was that a <see cref="LayoutResult" /> holds exactly one rectangle, and a
///         non-atomic inline box needs several.</b> Every algorithm here preserved one invariant
///         without ever having to say so: <b>one node produces one box</b>. A flex item is one
///         rectangle, a block-level child is one rectangle, a grid item is one rectangle — so a
///         node's geometry is four floats at a known offset, which is what makes the store five
///         parallel arrays and a hundred thousand nodes four allocations. CSS Display §2.2's
///         non-replaced <c>inline</c> box breaks it: a <c>span</c> crossing a line break is
///         <i>fragmented</i> into one box per line, with the horizontal border and padding drawn at
///         the two real ends and not at the breaks.
///     </para>
///     <para>
///         ⚠ <b>It is no longer a wall, and what moved is smaller than the invariant it relaxed.</b>
///         <see cref="FragmentArena" /> is variable-length <i>output</i>, the same shape
///         <see cref="TrackArena" /> is on the input side, and <see cref="LayoutResult" /> carries a
///         handle into it. A count of zero still means "one box, and it is
///         <see cref="LayoutResult.Position" />", so <c>GetLeft</c>, the rounding pass and the
///         absolute walk were not touched — and when the count is non-zero, the node's own rectangle
///         is the <b>union</b> of its fragments, which is precisely CSS 2.1 §10.1's containing block
///         for an absolutely positioned descendant of an inline box. See
///         <c>LayoutTree.Fragments.cs</c>, and <c>InlineKnownGaps.txt</c> for what is still scope.
///     </para>
///     <para>
///         ⚠ <b>The claim that a line box allocates nothing survived, and it was the thing most at
///         risk.</b> A line used to be a <i>contiguous range of the existing child span</i>, exactly
///         as a flex line is. It is now a contiguous range of a flattened stream, because a span is
///         not <i>on</i> a line — its children are, and it contributes only its two edges. The
///         stream and the fragment scratch are watermarked buffers reused across passes, so the
///         steady state still allocates nothing with a span re-fragmenting every frame. See
///         <c>LayoutTree.InlineItems.cs</c>.
///     </para>
///     <para>
///         ⚠ <b>There is no second text wrapper here, deliberately.</b> <c>Vixen.Ui</c>'s
///         <c>TextLayout</c> already breaks a string into lines across a font-fallback chain, and it
///         reaches this store the way every leaf does — as a measure function. This algorithm treats
///         such a leaf as one atomic item and asks it exactly the question the measure cache is keyed
///         on. A second wrapper that broke text *here* would disagree with that one about kerning,
///         fallback and UAX #14 the moment either changed, and two wrappers that disagree are worse
///         than one. The cost of that choice is stated rather than hidden: a text leaf's own first
///         line is not shortened to the space left on the line box it lands on, because shortening it
///         is fragmentation again.
///     </para>
/// </remarks>
public sealed partial class LayoutTree {
    /// <summary>Whether this box participates in its parent's inline formatting context.</summary>
    /// <remarks>
    ///     CSS Display §2.1's <i>outer</i> display type, which is the half that decides how a box
    ///     relates to its siblings. The inner half — which algorithm runs inside it — is a separate
    ///     question answered at the dispatch, and the two are genuinely independent:
    ///     <c>inline-flex</c> is outer inline and inner flex.
    /// </remarks>
    static bool IsInlineLevel(Display display) =>
        display is Display.Inline or Display.InlineBlock or Display.InlineFlex;

    /// <summary>
    ///     Whether this container lays its children out on lines rather than stacking them.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         §9.2.1: a block container holds either block-level boxes or inline-level boxes, and
    ///         this answers which. It is a question about the <i>children</i>, not about the
    ///         container — the same <c>display: block</c> box stacks or flows depending entirely on
    ///         what is in it, which is why this is a walk and not a field.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Mixed content is <i>not</i> this, and it is no longer refused either.</b> A
    ///         container with both kinds of child answers <c>false</c> here and goes to
    ///         <c>CalculateBlockLayoutImpl</c>, which wraps each run of inline-level children in CSS
    ///         2.1 §9.2.1.1's <i>anonymous block box</i> and flows it through
    ///         <see cref="WalkInlineLines" /> over that run's sub-range. So this predicate still means
    ///         exactly what it says — "is this container's <i>whole</i> content one inline formatting
    ///         context" — and the mixed case is a block container holding a mixture of real boxes and
    ///         anonymous ones, which is what the spec says it is.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The blocker that used to be recorded here was overstated.</b> It said inventing an
    ///         anonymous box meant either allocating nodes during layout or teaching the walk to
    ///         address something that is not a node. Neither was needed: an anonymous block box takes
    ///         initial values for every non-inherited property, so it is never painted and never hit
    ///         tested and has <i>no stored rectangle at all</i> — it is a line walk over a sub-range,
    ///         and that is a parameter rather than a store change.
    ///     </para>
    /// </remarks>
    bool EstablishesInlineFormattingContext(int index) {
        var any = false;

        foreach (var child in ChildIds(index)) {
            if (styles[child].Display == Display.None || styles[child].PositionType == PositionType.Absolute) {
                continue;
            }

            // ⚠ CSS 2.1 §9.7: floating a box makes it BLOCK-LEVEL, whatever `display` says. So a
            // floated `inline-block` is not inline-level content and its container is not a pure
            // inline formatting context — it has to reach `LayoutTree.Block`, which is the only path
            // that knows how to place a float at all. Reading `Display` alone routes such a
            // container into the inline walk, where the float would be laid out on a line as though
            // §9.5 did not exist. No fixture in the corpus writes one; the alternative to this line
            // is a silently wrong answer rather than a missing feature.
            if (!IsInlineLevel(styles[child].Display) || styles[child].Float != FloatSide.None) {
                return false;
            }

            any = true;
        }

        return any;
    }

    /// <summary>Lays a container's inline-level children out onto line boxes.</summary>
    /// <remarks>
    ///     The shape deliberately mirrors <c>CalculateBlockLayoutImpl</c>'s, because the outer half of
    ///     the problem is the same one: settle the inline size first, walk the children once, and let
    ///     the block size fall out of where the walk finished. Only the walk differs.
    /// </remarks>
    void CalculateInlineLayoutImpl(
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

        // ── The inline axis ─────────────────────────────────────────────────────────────────────
        // Identical in shape to block's, and different in one word: where a block container's
        // `width: auto` fills the line (§10.3.3), this one asks its content how wide it wants to be.
        // A container establishing an inline formatting context is still block-level itself unless
        // its own `display` says otherwise, so a StretchFit request is still taken whole — it is the
        // *items* that shrink-to-fit, and they do it one level down.
        // ⚠ The raw figure travels beside the bounded one: CSS Flexbox §9.2's flex base size is this
        // measurement before the box's own min and max. See LayoutResult.UnclampedMeasuredDimensions.
        float rawWidth;
        if (widthSizingMode == SizingMode.StretchFit) {
            rawWidth = availableWidth - marginAxisRow;
        } else {
            var probeWidth = float.IsNaN(availableWidth) ? float.NaN : availableWidth - marginAxisRow - insetRow;

            // ⚠ The same guard `CalculateBlockLayoutImpl` puts round its own content probe, for the
            // same reason: asking a child how wide it wants to be lays the child out, and a nested
            // layout that does not open a scope of its own appends any float it places to the
            // ENCLOSING context — at an origin belonging to a box whose position is not decided yet.
            // Left there, a measurement that was never a placement narrows a real box somewhere above.
            var mark = floatExclusions.Count;

            var contentWidth = DetermineInlineContentWidth(
                index,
                0,
                links[index].ChildCount,
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

        var definiteHeight = heightSizingMode == SizingMode.StretchFit
            ? availableHeight - marginAxisColumn
            : ResolvedDimension(index, Dimension.Height, ownerHeight, ownerWidth, direction);

        var innerHeightForPercentages = float.IsNaN(definiteHeight) ? float.NaN : MathF.Max(0f, definiteHeight - insetColumn);

        // ── The block axis: one pass, breaking into lines ───────────────────────────────────────
        // ⚠ <b>A container that establishes a formatting context opens an exclusion scope here for
        // exactly one reason: to make the floats outside it invisible.</b> It can never place one of
        // its own — <see cref="EstablishesInlineFormattingContext" /> refuses a container with a
        // floated child — so the scope always closes empty and §10.6.3's contains-its-floats clause
        // has nothing to say. What it does buy is §9.5's other half: an `overflow: hidden` paragraph
        // beside a float is moved aside *whole* by its parent's walk, and its LINE boxes must then be
        // full width rather than shortened a second time by a float the box has already cleared.
        var floatScope = treeHasFloats && !BlockMarginsCollapsibleWithParent(index)
            ? BeginFloatContext(insetLeft, insetTop, innerWidth)
            : default;

        var walk = WalkInlineLines(
            index,
            0,
            links[index].ChildCount,
            direction,
            outerWidth,
            innerWidth,
            innerHeightForPercentages,
            insetLeft,
            insetRight,
            insetTop,
            performLayout,
            currentDepth
        );

        if (treeHasFloats && !BlockMarginsCollapsibleWithParent(index)) {
            EndFloatContext(in floatScope);
        }

        var intrinsicHeight = walk.ContentHeight + insetBottom;

        var rawHeight = heightSizingMode == SizingMode.StretchFit ? availableHeight - marginAxisColumn : intrinsicHeight;
        var outerHeight = BoundAxis(index, FlexDirection.Column, direction, rawHeight, ownerHeight, ownerWidth);

        SetMeasuredDimension(index, Dimension.Width, outerWidth, MathF.Max(rawWidth, paddingBorderRow));
        SetMeasuredDimension(index, Dimension.Height, outerHeight, MathF.Max(rawHeight, paddingBorderColumn));

        // ── What this box reports upward ────────────────────────────────────────────────────────
        // ⚠ An inline formatting context is a barrier to margin collapsing in both directions: the
        // boxes inside it are inline-level, and §8.3.1 collapses only the vertical margins of
        // *block-level* boxes in the same formatting context. So the honest answer is the one every
        // non-block algorithm gives — "my own margin, and no" — and `CalculateLayoutImpl` has already
        // written exactly that before dispatching here. Nothing to do but leave it alone.
        //
        // The baseline is the one thing this algorithm does have to report, because nothing else can
        // reconstruct it. §10.8.1: an inline-block's baseline is the baseline of its **last** line
        // box — last, not first, which is the half that is easy to get backwards and is why
        // `WalkInlineLines` carries it rather than returning the first line's.
        results[index].InlineBaseline = walk.LastBaseline;

        if (!performLayout) {
            return;
        }

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

    /// <summary>What one pass over an inline formatting context settled.</summary>
    /// <param name="ContentHeight">The stacked height of every line box, from the content-box top.</param>
    /// <param name="LastBaseline">Where the last line box put its baseline, or NaN if there were none.</param>
    readonly record struct InlineWalk(float ContentHeight, float LastBaseline);

    /// <summary>
    ///     Breaks the inline-level children into line boxes, then aligns each line vertically.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Two passes over the same range, and no storage between them.</b> A line's items
    ///         cannot be positioned until the line's baseline is known, and the baseline is the
    ///         deepest ascent among them — so every line is visited twice. The reason that is free is
    ///         the one named at the top of this file: a line is a contiguous range of
    ///         <c>ChildIds</c>, and each item's size is already sitting in
    ///         <c>results[child].MeasuredDimensions</c> from the sizing pass. Re-reading it costs an
    ///         array index. Grid had to build <see cref="TrackArena" /> to avoid exactly this and
    ///         could not, because a track list is input rather than output.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An item that does not fit still goes on the line when the line is empty.</b>
    ///         §9.4.2: a line box that cannot hold its one atomic item overflows rather than
    ///         producing an empty line and then overflowing anyway. Dropping the <c>placed &gt; 0</c>
    ///         guard turns a single over-wide item into an infinite loop, which is the reason it is a
    ///         guard rather than a clamp.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The range is over the container's <i>children</i>, and that is what an anonymous
    ///         block box is.</b> A container whose every in-flow child is inline-level passes its
    ///         whole child list; a <i>mixed</i> container passes one run at a time from
    ///         <c>WalkBlockChildren</c>, and each such call is CSS 2.1 §9.2.1.1's anonymous block box
    ///         in its entirety. Nothing is stored for it because there is nothing to store: it takes
    ///         initial values for every non-inherited property, so it has no border, no padding and no
    ///         margin, its content box is its margin box, and the boxes it lays out are already
    ///         expressed as offsets from the container that really is their parent.
    ///     </para>
    /// </remarks>
    /// <param name="index">The container whose children are being flowed.</param>
    /// <param name="childStart">The first child position to flow, inclusive.</param>
    /// <param name="childEnd">The last child position to flow, exclusive.</param>
    /// <param name="direction">The inline direction.</param>
    /// <param name="outerWidth">The container's border-box width.</param>
    /// <param name="innerWidth">The container's content-box width, which lines break against.</param>
    /// <param name="innerHeightForPercentages">What a percentage height resolves against, or NaN.</param>
    /// <param name="insetLeft">The container's left padding and border.</param>
    /// <param name="insetRight">The container's right padding and border.</param>
    /// <param name="contentTop">
    ///     Where the first line box's top edge goes, in the container's coordinates. The container's
    ///     own top inset for a whole-container walk; the anonymous block box's top edge for a run.
    /// </param>
    /// <param name="performLayout">Whether to position anything, or only to size it.</param>
    /// <param name="currentDepth">The recursion guard's depth.</param>
    InlineWalk WalkInlineLines(
        int index,
        int childStart,
        int childEnd,
        Direction direction,
        float outerWidth,
        float innerWidth,
        float innerHeightForPercentages,
        float insetLeft,
        float insetRight,
        float contentTop,
        bool performLayout,
        int currentDepth
    ) {
        // ⚠ The stream, and the watermark that makes it free. See LayoutTree.InlineItems.cs: a line
        // is still a contiguous range, of this rather than of the child list, because a non-atomic
        // inline box puts its *children* on the line and only its two edges.
        var streamBase = inlineItemTop;
        var fragmentBase = fragmentScratchTop;
        BuildInlineItems(index, childStart, childEnd, nested: false);
        var streamEnd = inlineItemTop;

        try {
            // ── Sizing pass ─────────────────────────────────────────────────────────────────────
            // Every in-flow item is measured once, against the *container's* inner width rather than
            // against whatever is left on its line. That is §10.3.9 as written — shrink-to-fit
            // resolves against the containing block, not against the remaining space — and it is also
            // what makes the result independent of the order lines happen to break in, so the measure
            // cache can serve the second and third readings below.
            HideAndPositionOutOfFlow(index, childStart, childEnd, direction, outerWidth, insetLeft, insetRight, contentTop, performLayout);

            for (var i = streamBase; i < streamEnd; i++) {
                // ⚠ Read fresh every iteration. Sizing an item runs a whole nested layout, which may
                // build a stream of its own and grow the buffer out from under a cached span.
                var item = inlineItems[i];

                switch (item.Kind) {
                    case InlineItemKind.Atomic:
                        LayoutInlineItem(item.Node, direction, innerWidth, innerHeightForPercentages, performLayout, currentDepth);
                        break;

                    case InlineItemKind.Open:
                        ResolveInlineBoxMetrics(item.Node, direction, innerWidth);
                        HideAndPositionOutOfFlow(item.Node, direction, outerWidth, insetLeft, insetRight, contentTop, performLayout);
                        break;
                }
            }

            // ── Breaking and alignment ──────────────────────────────────────────────────────────
            // ⚠ With no float anywhere in the tree every branch guarded by `floatsActive` below is
            // dead and this loop is the one that passed before floats reached it: `BreakLine` is
            // handed `innerWidth`, the start inset is zero, and there is one pass per line. That is
            // deliberate — the float-active path breaks each line up to three times, and a tree with
            // no floats must not pay for a rule it cannot be subject to.
            var floatsActive = treeHasFloats;
            var y = contentTop;
            var lastBaseline = float.NaN;
            var cursor = streamBase;
            var open = OpenInlineBox.None;

            // ⚠ Monotone, and that is the whole of what stops a float being placed twice. A line is
            // re-broken up to three times and a narrower band can move its end backwards, so "the
            // floats on this line" is not a stable range — but every float in the stream must reach
            // the exclusion list exactly once, and a second entry for the same box is an exclusion
            // that never goes away. So placement is driven by a watermark over the stream rather than
            // by the line's range: everything below it has been placed, and nothing is placed twice.
            var floatsPlacedThrough = streamBase;

            while (cursor < streamEnd) {
                var lineStart = cursor;
                var lineTop = y;
                var lineStartInset = 0f;

                // ⚠ <b>How wide this line box is, which is the container's inner width until a float
                // takes some of it away</b> — and it is a per-line number rather than a per-container
                // one for exactly that reason. `text-align` distributes the slack the line has, and a
                // line beside a float has less of it, so aligning against `innerWidth` would push a
                // centred line half a float's width into the float.
                var lineAvailable = innerWidth;
                var lineEnd = lineStart;
                var placed = 0;
                var metrics = default(LineMetrics);

                if (!floatsActive) {
                    (lineEnd, placed) = BreakLine(lineStart, streamEnd, direction, innerWidth, innerWidth);

                    if (placed == 0) {
                        break;
                    }

                    metrics = MeasureLine(lineStart, lineEnd, direction, innerWidth);
                } else {
                    // ── §9.5's main clause ──────────────────────────────────────────────────────
                    // ⚠ <b>The band a line gets depends on how tall the line is, and how tall the
                    // line is depends on which items fit in the band.</b> So this is a bounded search
                    // rather than a single query: probe the band at zero height, break against it,
                    // measure what came out, and re-ask with that height if the line turned out
                    // taller than the probe assumed. A narrower band can only take items away, so the
                    // second answer is normally final; the third attempt exists because a line that
                    // was SHIFTED past a float can find a wider band and take items back. Three is a
                    // cap and not a fixed point — see `InlineKnownGaps.txt`.
                    //
                    // ⚠ <b>The zero-height first probe is not an optimisation, it is the reason the
                    // loop exists.</b> `FloatBandAt` excludes a float whose top edge is exactly the
                    // slice's top, which is what lets a cleared box sit flush against what it
                    // cleared — so a zero-height probe at the first line's top sees no float at all.
                    // Chrome narrows that line: a 200-wide box with a 50x10 left float puts three
                    // 50x20 items at x=50 and not four at x=0.
                    var needed = FirstChunkWidth(lineStart, streamEnd, direction, innerWidth);
                    var probeHeight = 0f;

                    // ⚠ Five rather than three, and the two extra are the float-in-a-run case rather
                    // than slack. Placing a float mid-line invalidates every measurement taken so far
                    // — the band moved, so the break, the shift and the height all have to be asked
                    // again from scratch — and a line can legitimately do that twice, once for a
                    // left float and once for a right one written beside it. Three attempts is still
                    // the cap on the height search itself; see `InlineKnownGaps.txt`.
                    for (var attempt = 0; attempt < 5; attempt++) {
                        lineTop = ShiftLinePastFloats(lineTop, probeHeight, needed, innerWidth, insetLeft);

                        var (start, available) = InlineBandForLine(lineTop, probeHeight, innerWidth, insetLeft, direction);

                        lineStartInset = start;
                        lineAvailable = available;
                        (lineEnd, placed) = BreakLine(lineStart, streamEnd, direction, innerWidth, available);

                        if (placed == 0) {
                            break;
                        }

                        // ⚠ Measured BEFORE the floats are placed, so that the cap-exhaustion path
                        // still leaves a height belonging to the range `lineEnd` names. Placing a
                        // float cannot change what is on the line without a re-break, and a re-break
                        // measures again.
                        metrics = MeasureLine(lineStart, lineEnd, direction, innerWidth);

                        // ── §9.5.1's rules 5 and 6 ──────────────────────────────────────────────
                        // A float written between two items may not start above the top of the line
                        // box holding what came before it, which for this walk is `lineTop` exactly.
                        // Placing it here rather than in `WalkBlockChildren` is what lets it shorten
                        // the line it is on — including the items ahead of it, which is the half
                        // that looks wrong and is what Chrome does.
                        if (PlaceFloatsOnLine(
                                ref floatsPlacedThrough,
                                lineEnd,
                                direction,
                                innerWidth,
                                innerHeightForPercentages,
                                lineTop,
                                insetLeft,
                                performLayout,
                                currentDepth
                            )) {
                            probeHeight = 0f;

                            continue;
                        }

                        if (metrics.Height <= probeHeight) {
                            break;
                        }

                        probeHeight = metrics.Height;
                    }

                    if (placed == 0) {
                        break;
                    }
                }

                cursor = lineEnd;

                if (performLayout) {
                    PlaceLine(
                        lineStart,
                        lineEnd,
                        direction,
                        outerWidth,
                        innerWidth,
                        insetLeft,
                        insetRight,
                        lineStartInset,
                        lineAvailable,
                        styles[index].TextAlign,
                        lineTop,
                        in metrics,
                        ref open
                    );
                }

                lastBaseline = lineTop + metrics.Ascent;
                y = lineTop + metrics.Height;
            }

            return new InlineWalk(y, lastBaseline);
        } finally {
            // ⚠ Both watermarks, and the second one is belt and braces rather than reachable today:
            // every Open in a well-formed stream has a Close that commits its fragments and rewinds
            // the scratch. Restoring anyway costs an assignment and means a future producer that
            // learns to abandon a box cannot turn that into an arena that grows every frame — which
            // would surface as the zero-allocation gate going red a long way from the cause.
            inlineItemTop = streamBase;
            fragmentScratchTop = fragmentBase;
        }
    }

    /// <summary>
    ///     Clears what <c>display: none</c> left behind and records where an out-of-flow child would
    ///     have sat.
    /// </summary>
    /// <remarks>
    ///     Run for the container and again for every non-atomic inline box in it, because a flattened
    ///     box's own children never reach the container's loop and a hidden grandchild that keeps last
    ///     frame's rectangle is a box that goes on being painted after it was hidden.
    /// </remarks>
    void HideAndPositionOutOfFlow(
        int index,
        Direction direction,
        float outerWidth,
        float insetLeft,
        float insetRight,
        float contentTop,
        bool performLayout
    ) =>
        HideAndPositionOutOfFlow(
            index,
            0,
            links[index].ChildCount,
            direction,
            outerWidth,
            insetLeft,
            insetRight,
            contentTop,
            performLayout
        );

    /// <summary>The same, over one anonymous block box's run of the child list.</summary>
    /// <remarks>
    ///     ⚠ <b>A <c>display: none</c> or out-of-flow child inside a run stays inside it rather than
    ///     ending it</b>, so this is where both are dealt with for a mixed container's inline runs —
    ///     and an out-of-flow child's static position becomes the anonymous box's own top edge, which
    ///     is where the box it replaced would have gone. Leaving it to the block walk instead would
    ///     record the cursor from <i>before</i> the run, because the run's height is not known until
    ///     the run has been flowed.
    /// </remarks>
    void HideAndPositionOutOfFlow(
        int index,
        int childStart,
        int childEnd,
        Direction direction,
        float outerWidth,
        float insetLeft,
        float insetRight,
        float contentTop,
        bool performLayout
    ) {
        var childIds = ChildIds(index);

        for (var i = childStart; i < childEnd; i++) {
            var child = childIds[i];

            if (styles[child].Display == Display.None) {
                if (performLayout) {
                    ZeroOutLayoutRecursively(child);
                }

                continue;
            }

            if (styles[child].PositionType == PositionType.Absolute) {
                results[child].BlockStaticTop = contentTop;
                results[child].BlockStaticLeft = direction == Direction.Ltr ? insetLeft : outerWidth - insetRight;
            }
        }
    }

    /// <summary>
    ///     Places every float the stream carries up to <paramref name="lineEnd" />, at this line's top.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>CSS 2.1 §9.5.1 rules 5 and 6, and the only thing this adds to
    ///         <see cref="PlaceFloatChild" /> is <i>which</i> vertical position it starts the search
    ///         from.</b> The block walk hands it the flow cursor; this hands it the top of the line the
    ///         float was written on. Everything after that — clearance, rule 3's "no higher than an
    ///         earlier float", the band search of rules 1, 2 and 7 — is the same code, which is why
    ///         two floats written on one line stack sideways and a third drops below them without a
    ///         word of new arithmetic.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What is deliberately <i>not</i> here: the float is not pushed below the line when
    ///         the items already on that line leave it no room.</b> The search consults the exclusion
    ///         list only, so a float wider than the band left by earlier floats drops, and a float
    ///         wider than the space left by earlier <i>text</i> does not — it takes the space and the
    ///         line re-breaks around it. Chrome keeps the earlier items and pushes the float down. No
    ///         oracle was read for it; <c>InlineKnownGaps.txt</c> carries it rather than a guess.
    ///     </para>
    /// </remarks>
    /// <param name="floatsPlacedThrough">
    ///     The watermark: everything below it is already in the exclusion list. Advanced past
    ///     <paramref name="lineEnd" /> on the way out, so a re-break cannot place a float twice.
    /// </param>
    /// <param name="lineEnd">One past the last stream entry this line took.</param>
    /// <param name="direction">The container's inline direction.</param>
    /// <param name="innerWidth">The container's content-box width.</param>
    /// <param name="innerHeightForPercentages">What a percentage height resolves against, or NaN.</param>
    /// <param name="lineTop">The line box's top edge, in the container's coordinates.</param>
    /// <param name="insetLeft">The container's left padding and border.</param>
    /// <param name="performLayout">Whether positions are being written.</param>
    /// <param name="currentDepth">The recursion guard's depth.</param>
    /// <returns>Whether anything was placed, which is whether the band the line saw has moved.</returns>
    bool PlaceFloatsOnLine(
        ref int floatsPlacedThrough,
        int lineEnd,
        Direction direction,
        float innerWidth,
        float innerHeightForPercentages,
        float lineTop,
        float insetLeft,
        bool performLayout,
        int currentDepth
    ) {
        var any = false;

        for (var i = floatsPlacedThrough; i < lineEnd; i++) {
            if (inlineItems[i].Kind != InlineItemKind.Float) {
                continue;
            }

            PlaceFloatChild(
                inlineItems[i].Node,
                direction,
                innerWidth,
                innerHeightForPercentages,
                lineTop,
                insetLeft,
                performLayout,
                currentDepth
            );

            any = true;
        }

        if (lineEnd > floatsPlacedThrough) {
            floatsPlacedThrough = lineEnd;
        }

        return any;
    }

    /// <summary>Whether a child takes part in the line-breaking walk at all.</summary>
    bool ParticipatesInLine(int child) =>
        styles[child].Display != Display.None && styles[child].PositionType != PositionType.Absolute;

    /// <summary>Sizes one atomic inline, by whichever algorithm its inner display type names.</summary>
    /// <remarks>
    ///     ⚠ <b><see cref="SizingMode.FitContent" /> is the entire mechanism, and it was already
    ///     here.</b> This is the one call that makes an <c>inline-block</c> not take the whole line,
    ///     and it adds no arithmetic whatsoever: the block path's <c>width: auto</c> branch already
    ///     splits on <see cref="SizingMode.StretchFit" /> versus everything else, and the flex path
    ///     has understood <c>FitContent</c> since Yoga's 534. What was missing for two plan items was
    ///     not shrink-to-fit — it was a <i>caller</i> that asked for it. Doc 43 § F4 read the absence
    ///     as "there is no inline formatting context", which was true, and inferred that the sizing
    ///     was missing too, which was not.
    /// </remarks>
    void LayoutInlineItem(
        int child,
        Direction direction,
        float innerWidth,
        float innerHeightForPercentages,
        bool performLayout,
        int currentDepth
    ) {
        // ⚠ A stated width is resolved here and *imposed*, exactly as block's walk does it, and the
        // reason is that neither algorithm reads its own `width` on the way in — both are told what
        // they are by their caller. Handing a definite-width item `FitContent` against the line would
        // shrink it to its contents and ignore the declaration; handing it `StretchFit` against the
        // line would stretch it to the line, which is the very bug this keyword exists to avoid. Only
        // a box with `width: auto` goes to §10.3.9.
        var box = ResolveBlockChildBox(child, direction, innerWidth, innerHeightForPercentages);
        var marginRow = StyleResolution.MarginForAxis(in styles[child], FlexDirection.Row, innerWidth.OrZero());
        var marginColumn = StyleResolution.MarginForAxis(in styles[child], FlexDirection.Column, innerWidth.OrZero());

        var definiteWidth = !float.IsNaN(box.Width);
        var childWidth = definiteWidth
            ? MathF.Max(
                ClampBlockChildAxis(box.Width, box.MinWidth, box.MaxWidth),
                StyleResolution.PaddingAndBorderForAxis(in styles[child], FlexDirection.Row, direction, innerWidth)
            )
            : float.NaN;

        var childHeight = float.NaN;
        var heightMode = SizingMode.MaxContent;
        if (!float.IsNaN(box.Height)) {
            childHeight = ClampBlockChildAxis(box.Height, box.MinHeight, box.MaxHeight) + marginColumn;
            heightMode = SizingMode.StretchFit;
        }

        CalculateLayoutInternal(
            child,
            definiteWidth ? childWidth + marginRow : innerWidth,
            childHeight,
            direction,
            definiteWidth ? SizingMode.StretchFit : SizingMode.FitContent,
            heightMode,
            innerWidth,
            innerHeightForPercentages,
            performLayout,
            currentDepth
        );
    }

    /// <summary>Fills one line box from the stream, and reports where it ended.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><paramref name="innerWidth" /> and <paramref name="availableWidth" /> are two
    ///         different questions and only the second one floats change.</b> The first is the
    ///         containing block's inline size, which every percentage on the line resolves against —
    ///         CSS 2.1 §8.3 makes even a percentage <c>margin-top</c> a fraction of it. The second is
    ///         how much room this particular line box has, which §9.5 lets a float take away. Passing
    ///         the band as the percentage base instead would make an item's own margins change
    ///         depending on which line it landed on.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An item that does not fit still goes on the line when the line is empty.</b>
    ///         §9.4.2: a line box that cannot hold its one atomic item overflows rather than producing
    ///         an empty line and then overflowing anyway. Dropping the <c>placed &gt; 0</c> guard turns
    ///         a single over-wide item into an infinite loop, which is why it is a guard and not a
    ///         clamp. Beside a float the line is moved down FIRST — see
    ///         <see cref="ShiftLinePastFloats" /> — and only overflows once there is no float left to
    ///         get out from under.
    ///     </para>
    /// </remarks>
    (int End, int Placed) BreakLine(int lineStart, int streamEnd, Direction direction, float innerWidth, float availableWidth) {
        var cursor = lineStart;
        var lineWidth = 0f;
        var placed = 0;

        while (cursor < streamEnd) {
            var item = inlineItems[cursor];

            if (item.Kind != InlineItemKind.Atomic) {
                // An inline box's edge is not a break opportunity and cannot start a line on its own;
                // it just costs its border, padding and margin wherever it falls. A float costs the
                // line nothing at all — it is out of flow, and what it takes from the line it takes
                // by narrowing the band rather than by advancing the pen.
                if (item.Kind != InlineItemKind.Float) {
                    lineWidth += item.Kind == InlineItemKind.Open
                        ? InlineBoxStartEdge(item.Node, direction, innerWidth)
                        : InlineBoxEndEdge(item.Node, direction, innerWidth);
                }

                cursor++;

                continue;
            }

            var advance = InlineOuterWidth(item.Node, direction, innerWidth);

            // ⚠ A tolerance rather than a bare `>`, because the widths being compared are sums of
            // resolved percentages and a line that fits exactly is the commonest case in a
            // hand-written layout. Breaking `width: 50%` twice into two lines because the two halves
            // add to 100.00001 is the failure this prevents.
            if (placed > 0 && lineWidth + advance > availableWidth + LineFitTolerance) {
                break;
            }

            lineWidth += advance;
            placed++;
            cursor++;
        }

        return (cursor, placed);
    }

    /// <summary>How much room a line box needs before it can hold anything at all.</summary>
    /// <remarks>
    ///     §9.5's shift-down clause is stated against "content", and the smallest unit of content this
    ///     walk can put on a line is one atomic item — plus whichever inline box edges open before it,
    ///     which are charged to the line wherever they fall and cannot be deferred to the next one.
    /// </remarks>
    float FirstChunkWidth(int lineStart, int streamEnd, Direction direction, float innerWidth) {
        var width = 0f;

        for (var i = lineStart; i < streamEnd; i++) {
            var item = inlineItems[i];

            if (item.Kind != InlineItemKind.Atomic) {
                if (item.Kind != InlineItemKind.Float) {
                    width += item.Kind == InlineItemKind.Open
                        ? InlineBoxStartEdge(item.Node, direction, innerWidth)
                        : InlineBoxEndEdge(item.Node, direction, innerWidth);
                }

                continue;
            }

            return width + InlineOuterWidth(item.Node, direction, innerWidth);
        }

        return width;
    }

    /// <summary>How much of a line box the floats crossing it leave, and where it starts.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Three coordinate systems meet on these four lines, and getting the wrong one is
    ///         silent.</b> The exclusion list is in the formatting context ROOT's content coordinates;
    ///         <see cref="floatOriginX" /> holds where the container being walked has its BORDER box
    ///         in them, so this container's own content box starts at <c>floatOriginX + insetLeft</c>.
    ///         A line box is addressed in the container's coordinates, like every other output here.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the band is clamped to this container rather than trusted.</b>
    ///         <see cref="FloatBandAt" /> answers with <see cref="floatContextWidth" /> as its far
    ///         edge, which belongs to the context root and not to this box — a narrower box nested in
    ///         one would otherwise be handed a line box wider than itself the moment no float crossed.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The returned inset is inline-relative and the band is physical, which is the whole
    ///         of what RTL costs here.</b> In LTR a line starts at the band's left edge; in RTL it
    ///         starts at the band's right edge, so the distance from the container's own start edge is
    ///         what is left over on the other side. A left float narrows an RTL line without moving
    ///         where it begins, and a right float moves where it begins without narrowing it any
    ///         differently — both are one subtraction rather than two cases.
    ///     </para>
    /// </remarks>
    (float StartInset, float Available) InlineBandForLine(
        float lineTop,
        float lineHeight,
        float innerWidth,
        float insetLeft,
        Direction direction
    ) {
        var contentLeft = floatOriginX + insetLeft;
        var (bandLeft, bandRight) = FloatBandAt(floatOriginY + lineTop, lineHeight);

        var left = MathF.Max(0f, bandLeft - contentLeft);
        var right = MathF.Min(innerWidth, bandRight - contentLeft);

        return (direction == Direction.Ltr ? left : MathF.Max(0f, innerWidth - right), MathF.Max(0f, right - left));
    }

    /// <summary>
    ///     CSS 2.1 §9.5's other clause: a line box with no room for content is moved down until it has
    ///     some, or until there are no floats left to move past.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>"Or until there are no more floats present" is the half that keeps this from being a
    ///     clamp.</b> An item wider than the container itself never fits any band, and the loop must
    ///     still stop — below the last float, where §9.4.2's overflow takes over. Chrome answers
    ///     exactly that: a 150-wide item in a 100-wide box with a 60×40 left float comes out at
    ///     <c>x = 0, y = 40</c>, below the float and hanging 50 points off the right.
    /// </remarks>
    float ShiftLinePastFloats(float lineTop, float lineHeight, float needed, float innerWidth, float insetLeft) {
        var y = lineTop;

        while (true) {
            var (_, available) = InlineBandForLine(y, lineHeight, innerWidth, insetLeft, Direction.Ltr);

            if (available + LineFitTolerance >= needed) {
                return y;
            }

            var next = NextFloatBottomBelow(floatOriginY + y, lineHeight);

            if (float.IsNaN(next)) {
                return y;
            }

            var candidate = next - floatOriginY;

            if (candidate <= y) {
                return y;
            }

            y = candidate;
        }
    }

    /// <summary>The slack a line-fitting comparison is allowed, in points.</summary>
    const float LineFitTolerance = 0.0001f;

    /// <summary>An item's width on the line, margins included.</summary>
    float InlineOuterWidth(int child, Direction direction, float innerWidth) =>
        results[child].MeasuredDimensions[(int) Dimension.Width]
        + StyleResolution.MarginForAxis(in styles[child], FlexDirection.Row, innerWidth.OrZero());

    /// <summary>Where one line box's baseline sits, and how tall the line is.</summary>
    /// <param name="Ascent">From the line's top edge down to its baseline.</param>
    /// <param name="Height">The whole line box.</param>
    readonly record struct LineMetrics(float Ascent, float Height);

    /// <summary>
    ///     Works out a line box's baseline and height from the items on it, per CSS 2.1 §10.8.1.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Two rounds, because the two families of <c>vertical-align</c> value are defined
    ///         against different things and the second depends on the first.</b> A baseline-aligned
    ///         box is positioned relative to the line's baseline, which is not known until every
    ///         baseline-aligned box has been seen — that is round one, and it fixes the baseline. A
    ///         <c>top</c>- or <c>bottom</c>-aligned box is positioned relative to the line's *edges*,
    ///         which are not known until the line's height is, and a tall one of those grows the line
    ///         without moving the baseline: it can only grow the side it is anchored to. Doing this in
    ///         one round puts a tall <c>vertical-align: top</c> image's overflow above the text
    ///         instead of below it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>There is no <i>strut</i>, and it is the largest single approximation in this
    ///         file.</b> §10.8 begins every line box with an imaginary zero-width inline box carrying
    ///         the block container's own font and line height, so an empty line is still a line tall
    ///         and a short image never makes the line shorter than the text around it would be. A
    ///         strut is font metrics, and this project has no font — <c>Vixen.Ui.Layout</c> is
    ///         geometry, and <c>FontRegistry</c> lives a layer out. So a line here is exactly as tall
    ///         as the boxes on it. See <c>InlineKnownGaps.txt</c>.
    ///     </para>
    /// </remarks>
    LineMetrics MeasureLine(int lineStart, int lineEnd, Direction direction, float innerWidth) {
        var ascent = 0f;
        var descent = 0f;

        // ⚠ Only the atomic entries. An inline box's own edges are horizontal — border, padding and
        // margin on the left and right — and CSS 2.1 §10.8 is explicit that a non-replaced inline
        // box's vertical padding and border do not affect the height of the line box it is on. So an
        // Open or a Close contributes nothing here, which is the one place that rule shows up.
        for (var i = lineStart; i < lineEnd; i++) {
            var item = inlineItems[i];
            if (item.Kind != InlineItemKind.Atomic || EffectiveVerticalAlign(item.Node) != VerticalAlign.Baseline) {
                continue;
            }

            var child = item.Node;
            var (top, bottom) = InlineVerticalMargins(child, direction, innerWidth);
            var height = results[child].MeasuredDimensions[(int) Dimension.Height];
            var baseline = InlineItemBaseline(child);

            ascent = MathF.Max(ascent, top + baseline);
            descent = MathF.Max(descent, height - baseline + bottom);
        }

        // Round two: an edge-aligned box can only grow the side it is anchored to.
        for (var i = lineStart; i < lineEnd; i++) {
            var item = inlineItems[i];
            if (item.Kind != InlineItemKind.Atomic) {
                continue;
            }

            var child = item.Node;
            var alignment = EffectiveVerticalAlign(child);
            if (alignment == VerticalAlign.Baseline) {
                continue;
            }

            var (top, bottom) = InlineVerticalMargins(child, direction, innerWidth);
            var outer = results[child].MeasuredDimensions[(int) Dimension.Height] + top + bottom;
            var slack = outer - (ascent + descent);

            if (slack <= 0f) {
                continue;
            }

            if (alignment == VerticalAlign.Bottom) {
                ascent += slack;
            } else {
                descent += slack;
            }
        }

        return new LineMetrics(ascent, ascent + descent);
    }

    /// <summary>Which non-atomic inline box the walk is currently inside, and where it started.</summary>
    /// <remarks>
    ///     ⚠ <b>It outlives a line, which is the whole point of it.</b> A span that crosses a break is
    ///     open at the end of one line and still open at the start of the next, and the two rectangles
    ///     that come out of that are exactly the fragmentation this file exists to produce. One box at
    ///     a time is enough because flattening is one level deep; see <c>IsNonAtomicInline</c>.
    /// </remarks>
    struct OpenInlineBox {
        public int Node;
        public float Start;
        public int ScratchBase;
        public bool IsFirstFragment;

        public static OpenInlineBox None => new() { Node = -1 };
    }

    /// <summary>How far along the inline axis a line's items are pushed by <c>text-align</c>.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Inline-relative, and that is what makes RTL free.</b> The returned number is added
    ///         to the same <c>x</c> the placement loop advances, which
    ///         <see cref="PlaceLine" /> already mirrors — so a positive offset moves items toward the
    ///         inline <i>end</i>, which is rightwards in LTR and leftwards in RTL, with no second
    ///         branch. Only the two physical keywords need to know the direction at all, and they
    ///         need it precisely because they are the two that do not flip.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Negative slack returns zero rather than a negative offset</b>, which is the same
    ///         rule <c>Vixen.Ui</c>'s <c>TextAlignShift</c> states for a line of glyphs and for the
    ///         same reason: content wider than its line overflows past the <i>end</i> edge whatever
    ///         the alignment says, because centring it would hide the beginning — and the beginning is
    ///         the part that says what has been cut off.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Against the line's own available width and not the container's</b>, so a line
    ///         beside a float centres in the band the float left it. The two differ only when
    ///         <c>treeHasFloats</c> is set, which is why the parameter is threaded from the walk
    ///         rather than read here.
    ///     </para>
    /// </remarks>
    float TextAlignOffset(
        TextAlign textAlign,
        Direction direction,
        int lineStart,
        int lineEnd,
        float innerWidth,
        float lineAvailable
    ) {
        // Start is the initial value and is where every line already sat, so the common path costs a
        // comparison and never walks the line.
        if (textAlign == TextAlign.Start
            || (textAlign == TextAlign.Left && direction == Direction.Ltr)
            || (textAlign == TextAlign.Right && direction == Direction.Rtl)) {
            return 0f;
        }

        var slack = lineAvailable - LineInlineExtent(lineStart, lineEnd, direction, innerWidth);

        if (slack <= 0f) {
            return 0f;
        }

        return textAlign == TextAlign.Center ? slack * 0.5f : slack;
    }

    /// <summary>How much of the inline axis one line's items take up, edges and margins included.</summary>
    /// <remarks>
    ///     ⚠ <b>The same sum <see cref="PlaceLine" />'s <c>x</c> accumulates, and it has to stay that
    ///     way.</b> A line that measured wider here than it places would be pushed off its own end
    ///     edge by the difference. In particular an inline box that was already open when the line
    ///     began contributes no start edge to either sum — its <c>Open</c> entry is on an earlier
    ///     line — and a box still open when the line ends contributes no end edge to either.
    /// </remarks>
    float LineInlineExtent(int lineStart, int lineEnd, Direction direction, float innerWidth) {
        var width = 0f;

        for (var i = lineStart; i < lineEnd; i++) {
            var item = inlineItems[i];

            width += item.Kind switch {
                InlineItemKind.Open => InlineBoxStartEdge(item.Node, direction, innerWidth),
                InlineItemKind.Close => InlineBoxEndEdge(item.Node, direction, innerWidth),
                _ => InlineOuterWidth(item.Node, direction, innerWidth)
            };
        }

        return width;
    }

    /// <summary>Positions every item on one line box, and closes off any fragment it ends.</summary>
    /// <remarks>
    ///     ⚠ <b><c>lineStartInset</c> is how far this line box's own start edge sits inside the
    ///     container's content start edge</b>, which is zero unless a float took some of it away. It
    ///     is inline-relative, so seeding <c>x</c> with it adds it on the left in LTR and subtracts it
    ///     from the right in RTL without a second branch — see <see cref="InlineBandForLine" />.
    /// </remarks>
    void PlaceLine(
        int lineStart,
        int lineEnd,
        Direction direction,
        float outerWidth,
        float innerWidth,
        float insetLeft,
        float insetRight,
        float lineStartInset,
        float lineAvailable,
        TextAlign textAlign,
        float lineTop,
        in LineMetrics metrics,
        ref OpenInlineBox open
    ) {
        var x = lineStartInset + TextAlignOffset(textAlign, direction, lineStart, lineEnd, innerWidth, lineAvailable);

        // ⚠ A box that was still open when the last line ended reopens at THIS line's start edge, and
        // beside a float that is not the same place the last one started. Resetting it when the
        // fragment was emitted could only guess at an inset the next line had not chosen yet.
        if (open.Node >= 0) {
            open.Start = x;
        }

        for (var i = lineStart; i < lineEnd; i++) {
            var item = inlineItems[i];

            // Already placed against the exclusion list, in the container's coordinates and not the
            // line's. It advances no pen and belongs to no fragment.
            if (item.Kind == InlineItemKind.Float) {
                continue;
            }

            if (item.Kind == InlineItemKind.Open) {
                open.Node = item.Node;
                open.Start = x;
                open.ScratchBase = fragmentScratchTop;
                open.IsFirstFragment = true;
                x += InlineBoxStartEdge(item.Node, direction, innerWidth);

                continue;
            }

            if (item.Kind == InlineItemKind.Close) {
                x += InlineBoxEndEdge(item.Node, direction, innerWidth);
                EmitInlineBoxFragment(
                    ref open,
                    direction,
                    outerWidth,
                    insetLeft,
                    insetRight,
                    lineTop,
                    metrics.Height,
                    x,
                    LayoutFragmentEnds.End
                );

                CommitInlineBoxFragments(item.Node, open.ScratchBase, direction, innerWidth);
                open.Node = -1;

                continue;
            }

            var child = item.Node;
            var (marginTop, marginBottom) = InlineVerticalMargins(child, direction, innerWidth);
            var marginStart = StyleResolution.InlineStartMargin(in styles[child], FlexDirection.Row, direction, innerWidth);
            var marginEnd = StyleResolution.InlineEndMargin(in styles[child], FlexDirection.Row, direction, innerWidth);
            var width = results[child].MeasuredDimensions[(int) Dimension.Width];
            var height = results[child].MeasuredDimensions[(int) Dimension.Height];

            var top = EffectiveVerticalAlign(child) switch {
                VerticalAlign.Top => lineTop + marginTop,
                VerticalAlign.Bottom => lineTop + metrics.Height - marginBottom - height,
                _ => lineTop + metrics.Ascent - InlineItemBaseline(child)
            };

            // ⚠ Physical, and mirrored rather than negated. The item advances along the *inline*
            // axis, so in RTL the first item on a line is the rightmost one and each subsequent one
            // sits further left — which is `outerWidth - insetRight` minus the distance travelled,
            // minus the item's own width because a rectangle is addressed from its left edge either
            // way. The relative-inset negation is block's rule and applies here for the same reason:
            // §9.4.3's offsets are physical even where the flow is not.
            var relativeX = direction == Direction.Ltr
                ? RelativePosition(child, FlexDirection.Row, direction, innerWidth)
                : -RelativePosition(child, FlexDirection.Row, direction, innerWidth);
            var relativeY = RelativePosition(child, FlexDirection.Column, direction, metrics.Height);

            results[child].Position[(int) Edge.Left] = direction == Direction.Ltr
                ? insetLeft + x + marginStart + relativeX
                : outerWidth - insetRight - x - marginStart - width + relativeX;
            results[child].Position[(int) Edge.Top] = top + relativeY;

            x += width + marginStart + marginEnd;
        }

        // ⚠ A box still open when the line ends is the fragmentation case itself: it gets a fragment
        // that carries neither real end unless this was also its first, and it reopens at the content
        // start of the next line. Nothing else in this store produces a second rectangle for a node.
        if (open.Node >= 0) {
            EmitInlineBoxFragment(
                ref open,
                direction,
                outerWidth,
                insetLeft,
                insetRight,
                lineTop,
                metrics.Height,
                x,
                LayoutFragmentEnds.None
            );

            open.IsFirstFragment = false;
        }
    }

    /// <summary>Records one fragment of the box currently open, in the container's coordinates.</summary>
    /// <remarks>
    ///     ⚠ <b>Physical, and mirrored rather than negated, for the same reason the item placement
    ///     above is.</b> The logical span the fragment covers is <c>[open.Start, xEnd)</c> measured
    ///     from the line's start edge; in RTL the start edge is the right one, so the physical left is
    ///     the container's right inset minus the far end of the span. Getting this by negating an
    ///     offset instead puts a right-to-left span's background off the left of the container, which
    ///     is a bug that only ever shows up in a locale nobody is testing in.
    /// </remarks>
    void EmitInlineBoxFragment(
        ref OpenInlineBox open,
        Direction direction,
        float outerWidth,
        float insetLeft,
        float insetRight,
        float lineTop,
        float lineHeight,
        float xEnd,
        LayoutFragmentEnds closing
    ) {
        var ends = closing | (open.IsFirstFragment ? LayoutFragmentEnds.Start : LayoutFragmentEnds.None);

        AppendFragmentScratch(
            new LayoutFragment {
                Left = direction == Direction.Ltr ? insetLeft + open.Start : outerWidth - insetRight - xEnd,
                Top = lineTop,
                Width = xEnd - open.Start,
                Height = lineHeight,
                Ends = ends
            }
        );
    }

    /// <summary>An item's resolved vertical margins.</summary>
    /// <remarks>
    ///     ⚠ Percentage margins resolve against the containing block's <i>inline</i> size on all four
    ///     edges (CSS 2.1 §8.3), which is the same rule block layout applies and the same one that
    ///     surprises: a percentage <c>margin-top</c> is a fraction of the container's width.
    /// </remarks>
    (float Top, float Bottom) InlineVerticalMargins(int child, Direction direction, float innerWidth) => (
        StyleResolution.InlineStartMargin(in styles[child], FlexDirection.Column, direction, innerWidth),
        StyleResolution.InlineEndMargin(in styles[child], FlexDirection.Column, direction, innerWidth)
    );

    /// <summary>
    ///     Which <c>vertical-align</c> this box is actually laid out with.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The five font-relative values fall back to <see cref="VerticalAlign.Baseline" /> here,
    ///     and the honest place to notice that is the bridge rather than this line.</b>
    ///     <c>middle</c>, <c>text-top</c>, <c>text-bottom</c>, <c>sub</c> and <c>super</c> are each
    ///     defined against the parent's strut — its font's x-height, ascent or descent — and this
    ///     project has no font to ask. Falling back is what a layout engine must do with a value it
    ///     cannot honour; what it must *not* do is let the layer above report the family as supported.
    ///     So <c>LayoutStyleBuilder</c> maps only the three that work, the utilities that emit the
    ///     other five stay in the editor's inert inventory with a task number, and
    ///     <c>InlineKnownGaps.txt</c> says what each one would need. See <see cref="VerticalAlign" />.
    /// </remarks>
    VerticalAlign EffectiveVerticalAlign(int child) =>
        styles[child].VerticalAlign switch {
            VerticalAlign.Top => VerticalAlign.Top,
            VerticalAlign.Bottom => VerticalAlign.Bottom,
            _ => VerticalAlign.Baseline
        };

    /// <summary>How far below an atomic inline's top edge its baseline sits, per CSS 2.1 §10.8.1.</summary>
    /// <remarks>
    ///     ⚠ <b>The clause everybody drops is the <c>overflow</c> one, and it is not an edge case —
    ///     it is the idiom.</b> §10.8.1: an <c>inline-block</c>'s baseline is the baseline of its last
    ///     in-flow line box, <i>unless</i> it has no in-flow line boxes or its <c>overflow</c> is
    ///     something other than <c>visible</c>, in which case the baseline is its bottom margin edge.
    ///     A scrolling or clipping box has no business hanging its neighbours off a line that might
    ///     scroll away, and since a clipped box is exactly what a card, a badge and a chip are, the
    ///     rule fires constantly. The synthesised answer for everything else is the same one CSS
    ///     Align §9.3 gives: the bottom margin edge.
    /// </remarks>
    float InlineItemBaseline(int child) {
        var height = results[child].MeasuredDimensions[(int) Dimension.Height];

        if (styles[child].OverflowX != Overflow.Visible || styles[child].OverflowY != Overflow.Visible) {
            return height;
        }

        var inlineBaseline = results[child].InlineBaseline;

        return float.IsNaN(inlineBaseline) ? CalculateBaseline(child) : inlineBaseline;
    }

    /// <summary>
    ///     How wide this container's line boxes want to be, for the case where its own width is being
    ///     asked for rather than imposed.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Shrink-to-fit is three numbers, and CSS 2.1 §10.3.9 writes it as
    ///     <c>min(max(preferred minimum, available), preferred)</c>.</b> The <i>preferred</i> width of
    ///     a run of atomic inlines is their sum — every one on a single line. The <i>preferred
    ///     minimum</i> is the widest single one, because an atomic inline is by definition the thing
    ///     that cannot be broken up. Getting the minimum wrong as the sum makes every inline container
    ///     refuse to wrap; getting it wrong as zero makes one narrow enough to overflow on every line.
    ///     <para>
    ///         ⚠ <b>An anonymous block box needs this too, and taking the container's block-level
    ///         answer instead is the mistake that is easy to miss.</b> A mixed container asked for its
    ///         own width would otherwise report the widest <i>single</i> inline-level child — which is
    ///         the run's <i>minimum</i> — because the block probe takes a maximum over children and
    ///         has no way to know that a run of them shares a line. That is not a rounding error: a
    ///         shrink-to-fit container holding three 20-point boxes beside a block sibling would come
    ///         out 20 wide and then wrap the run onto three lines.
    ///     </para>
    /// </remarks>
    float DetermineInlineContentWidth(
        int index,
        int childStart,
        int childEnd,
        Direction direction,
        SizingMode widthSizingMode,
        float availableInnerWidth,
        float ownerWidth,
        float ownerHeight,
        int currentDepth
    ) {
        var preferred = 0f;
        var minimum = 0f;

        var streamBase = inlineItemTop;
        BuildInlineItems(index, childStart, childEnd, nested: false);
        var streamEnd = inlineItemTop;

        try {
            for (var i = streamBase; i < streamEnd; i++) {
                var item = inlineItems[i];

                // ⚠ An inline box's horizontal edges are content width — a padded span is wider than
                // what is in it — but they are not a *minimum*, because they cannot be alone on a
                // line. Charging them to `preferred` and not to `minimum` is what keeps a heavily
                // padded span from refusing to wrap.
                if (item.Kind is InlineItemKind.Open or InlineItemKind.Close) {
                    preferred += item.Kind == InlineItemKind.Open
                        ? InlineBoxStartEdge(item.Node, direction, availableInnerWidth.OrZero())
                        : InlineBoxEndEdge(item.Node, direction, availableInnerWidth.OrZero());

                    continue;
                }

                // ⚠ <b>A float in a run contributes exactly as an atomic item does, and the reason is
                // not that it sits on the line — it is that it sits BESIDE it.</b> A float takes
                // horizontal room away from every line it crosses, so the widest arrangement is the
                // one where the run and the float share a row: their sum, which is the same
                // accumulator an atomic feeds. The min-content side is the same too — the widest
                // single unbreakable thing — because the narrowest useful arrangement is the float
                // alone on a row with the run wrapped under it. This is the sum
                // `DetermineBlockContentWidth` already keeps for a float that is a block-level
                // sibling, moved to where a float in a run can reach it.

                var width = InlineItemContentWidth(
                    item.Node,
                    direction,
                    availableInnerWidth,
                    ownerWidth,
                    ownerHeight,
                    currentDepth
                );

                preferred += width;
                minimum = MathF.Max(minimum, width);
            }
        } finally {
            inlineItemTop = streamBase;
        }

        if (widthSizingMode == SizingMode.MaxContent || float.IsNaN(availableInnerWidth)) {
            return preferred;
        }

        return MathF.Max(minimum, MathF.Min(preferred, availableInnerWidth));
    }

    /// <summary>How wide one atomic item wants to be, for the container's shrink-to-fit.</summary>
    float InlineItemContentWidth(
        int child,
        Direction direction,
        float availableInnerWidth,
        float ownerWidth,
        float ownerHeight,
        int currentDepth
    ) {
        var margin = StyleResolution.MarginForAxis(in styles[child], FlexDirection.Row, availableInnerWidth.OrZero());

        // ⚠ A definitely-sized box contributes its own width and its contents are never consulted
        // — CSS Sizing §5.2.2's distinction between a min-content *size* and a min-content
        // *contribution*, which this engine learned late and expensively on the flex side. Asking
        // an empty `width: 400px` box to measure itself under an undefined available width
        // answers zero, because a box with no contents needs no room for them.
        if (HasDefiniteLength(child, Dimension.Width, availableInnerWidth)) {
            return BoundAxis(
                child,
                FlexDirection.Row,
                direction,
                ResolvedDimension(child, Dimension.Width, availableInnerWidth, availableInnerWidth, direction),
                availableInnerWidth,
                availableInnerWidth
            ) + margin;
        }

        CalculateLayoutInternal(
            child,
            float.NaN,
            float.NaN,
            direction,
            SizingMode.MaxContent,
            SizingMode.MaxContent,
            ownerWidth,
            ownerHeight,
            performLayout: false,
            currentDepth
        );

        return results[child].MeasuredDimensions[(int) Dimension.Width] + margin;
    }
}
