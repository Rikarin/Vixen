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
///         ⚠ <b>The wall is that a <see cref="LayoutResult" /> holds exactly one rectangle, and a
///         non-atomic inline box needs several.</b> Every algorithm here has so far preserved one
///         invariant without ever having to say so: <b>one node produces one box</b>. A flex item is
///         one rectangle, a block-level child is one rectangle, a grid item is one rectangle — so a
///         node's geometry is four floats at a known offset, which is what makes the store five
///         parallel arrays and a hundred thousand nodes four allocations. CSS Display §2.2's
///         non-replaced <c>inline</c> box breaks it: a <c>span</c> crossing a line break is
///         <i>fragmented</i> into one box per line, each with its own rectangle, with the horizontal
///         border and padding drawn at the two real ends and not at the breaks. There is nowhere in
///         this store to put the second fragment, and nowhere in <c>GetLeft</c>/<c>GetWidth</c> to
///         return it from.
///     </para>
///     <para>
///         So this implements the half that fits the invariant and says where the line is:
///         <b>atomic inlines</b>. An <c>inline-block</c>, an <c>inline-flex</c> and — for the case
///         that dominates a user interface, a leaf holding text — an <c>inline</c> box are each
///         <i>one</i> box that happens to sit on a line beside its siblings rather than taking one to
///         itself. That is the whole of what the store can represent honestly, and it is also the
///         whole of what doc 43 § F4 said was missing: the reason those keywords were left unmapped
///         was that aliasing <c>inline-block</c> onto <see cref="Display.Block" /> gives it the whole
///         line. It no longer does. See <c>InlineKnownGaps.txt</c> for the rest, one rule at a time.
///     </para>
///     <para>
///         ⚠ <b>What it did <i>not</i> cost is worth as much as what it did.</b> Grid needed a second
///         arena because a track list is arbitrary-length input. A line box is not: a line is a
///         <i>contiguous range of the existing child span</i>, exactly as a flex line is, and every
///         item's size is already on the item. So the whole algorithm reads
///         <c>results[child].MeasuredDimensions</c> as many times as it likes and allocates nothing —
///         no arena, no scratch, no watermark. The one thing a line box needs that a flex line does
///         not is a <i>baseline</i>, and that is the output above.
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
    ///         ⚠ <b>Mixed content is refused and falls back to stacking, and that is a listed gap
    ///         rather than a reading of the spec.</b> §9.2.1.1 says a block container with both kinds
    ///         of child wraps every run of inline-level boxes in an <i>anonymous block box</i>. An
    ///         anonymous box is a box with no node — no id, no style, no entry in the child arena —
    ///         and inventing one means either allocating nodes during layout or teaching the walk to
    ///         address something that is not a node. Both are real changes to the store rather than to
    ///         this file, so mixed content stacks: every child gets its own line, which is what the
    ///         engine did before this algorithm existed and is wrong in the same direction rather than
    ///         in a new one.
    ///     </para>
    /// </remarks>
    bool EstablishesInlineFormattingContext(int index) {
        var any = false;

        foreach (var child in ChildIds(index)) {
            if (styles[child].Display == Display.None || styles[child].PositionType == PositionType.Absolute) {
                continue;
            }

            if (!IsInlineLevel(styles[child].Display)) {
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
        var insetLeft = results[index].Padding[(int) Edge.Left] + results[index].Border[(int) Edge.Left];
        var insetRight = results[index].Padding[(int) Edge.Right] + results[index].Border[(int) Edge.Right];
        var insetTop = results[index].Padding[(int) Edge.Top] + results[index].Border[(int) Edge.Top];
        var insetBottom = results[index].Padding[(int) Edge.Bottom] + results[index].Border[(int) Edge.Bottom];
        var insetRow = insetLeft + insetRight;
        var insetColumn = insetTop + insetBottom;

        // ── The inline axis ─────────────────────────────────────────────────────────────────────
        // Identical in shape to block's, and different in one word: where a block container's
        // `width: auto` fills the line (§10.3.3), this one asks its content how wide it wants to be.
        // A container establishing an inline formatting context is still block-level itself unless
        // its own `display` says otherwise, so a StretchFit request is still taken whole — it is the
        // *items* that shrink-to-fit, and they do it one level down.
        float outerWidth;
        if (widthSizingMode == SizingMode.StretchFit) {
            outerWidth = BoundAxis(index, FlexDirection.Row, direction, availableWidth - marginAxisRow, ownerWidth, ownerWidth);
        } else {
            var probeWidth = float.IsNaN(availableWidth) ? float.NaN : availableWidth - marginAxisRow - insetRow;
            var contentWidth = DetermineInlineContentWidth(
                index,
                direction,
                widthSizingMode,
                probeWidth,
                ownerWidth,
                ownerHeight,
                currentDepth
            );

            outerWidth = BoundAxis(index, FlexDirection.Row, direction, contentWidth + insetRow, ownerWidth, ownerWidth);
        }

        var innerWidth = MathF.Max(0f, outerWidth - insetRow);

        var definiteHeight = heightSizingMode == SizingMode.StretchFit
            ? availableHeight - marginAxisColumn
            : ResolvedDimension(index, Dimension.Height, ownerHeight, ownerWidth, direction);

        var innerHeightForPercentages = float.IsNaN(definiteHeight) ? float.NaN : MathF.Max(0f, definiteHeight - insetColumn);

        // ── The block axis: one pass, breaking into lines ───────────────────────────────────────
        var walk = WalkInlineLines(
            index,
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

        var intrinsicHeight = walk.ContentHeight + insetBottom;

        float outerHeight;
        if (heightSizingMode == SizingMode.StretchFit) {
            outerHeight = BoundAxis(index, FlexDirection.Column, direction, availableHeight - marginAxisColumn, ownerHeight, ownerWidth);
        } else {
            outerHeight = BoundAxis(index, FlexDirection.Column, direction, intrinsicHeight, ownerHeight, ownerWidth);
        }

        results[index].MeasuredDimensions[(int) Dimension.Width] = outerWidth;
        results[index].MeasuredDimensions[(int) Dimension.Height] = outerHeight;

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

        if (styles[index].PositionType != PositionType.Static || currentDepth == 1) {
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
    /// </remarks>
    InlineWalk WalkInlineLines(
        int index,
        Direction direction,
        float outerWidth,
        float innerWidth,
        float innerHeightForPercentages,
        float insetLeft,
        float insetRight,
        float insetTop,
        bool performLayout,
        int currentDepth
    ) {
        // ── Sizing pass ─────────────────────────────────────────────────────────────────────────
        // Every in-flow item is measured once, against the *container's* inner width rather than
        // against whatever is left on its line. That is §10.3.9 as written — shrink-to-fit resolves
        // against the containing block, not against the remaining space — and it is also what makes
        // the result independent of the order lines happen to break in, so the measure cache can
        // serve the second and third readings below.
        foreach (var child in ChildIds(index)) {
            if (styles[child].Display == Display.None) {
                if (performLayout) {
                    ZeroOutLayoutRecursively(child);
                }

                continue;
            }

            if (styles[child].PositionType == PositionType.Absolute) {
                results[child].BlockStaticTop = insetTop;
                results[child].BlockStaticLeft = direction == Direction.Ltr ? insetLeft : outerWidth - insetRight;
                continue;
            }

            LayoutInlineItem(child, direction, innerWidth, innerHeightForPercentages, performLayout, currentDepth);
        }

        // ── Breaking and alignment ──────────────────────────────────────────────────────────────
        var y = insetTop;
        var lastBaseline = float.NaN;
        var cursor = 0;

        while (true) {
            var children = ChildIds(index);
            if (cursor >= children.Length) {
                break;
            }

            // Which items this line holds, and how wide it is.
            var lineStart = cursor;
            var lineWidth = 0f;
            var placed = 0;

            while (cursor < children.Length) {
                var child = children[cursor];

                if (!ParticipatesInLine(child)) {
                    cursor++;
                    continue;
                }

                var advance = InlineOuterWidth(child, direction, innerWidth);

                // ⚠ A tolerance rather than a bare `>`, because the widths being compared are sums of
                // resolved percentages and a line that fits exactly is the commonest case in a
                // hand-written layout. Breaking `width: 50%` twice into two lines because the two
                // halves add to 100.00001 is the failure this prevents.
                if (placed > 0 && lineWidth + advance > innerWidth + 0.0001f) {
                    break;
                }

                lineWidth += advance;
                placed++;
                cursor++;
            }

            if (placed == 0) {
                break;
            }

            var metrics = MeasureLine(index, lineStart, cursor, direction, innerWidth);

            if (performLayout) {
                PlaceLine(index, lineStart, cursor, direction, outerWidth, innerWidth, insetLeft, insetRight, y, in metrics);
            }

            lastBaseline = y + metrics.Ascent;
            y += metrics.Height;
        }

        return new InlineWalk(y, lastBaseline);
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
    LineMetrics MeasureLine(int index, int lineStart, int lineEnd, Direction direction, float innerWidth) {
        var ascent = 0f;
        var descent = 0f;

        // Round one: the baseline-aligned boxes fix where the baseline is.
        var children = ChildIds(index);
        for (var i = lineStart; i < lineEnd; i++) {
            var child = children[i];
            if (!ParticipatesInLine(child) || EffectiveVerticalAlign(child) != VerticalAlign.Baseline) {
                continue;
            }

            var (top, bottom) = InlineVerticalMargins(child, direction, innerWidth);
            var height = results[child].MeasuredDimensions[(int) Dimension.Height];
            var baseline = InlineItemBaseline(child);

            ascent = MathF.Max(ascent, top + baseline);
            descent = MathF.Max(descent, height - baseline + bottom);
        }

        // Round two: an edge-aligned box can only grow the side it is anchored to.
        children = ChildIds(index);
        for (var i = lineStart; i < lineEnd; i++) {
            var child = children[i];
            if (!ParticipatesInLine(child)) {
                continue;
            }

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

    /// <summary>Positions every item on one line box.</summary>
    void PlaceLine(
        int index,
        int lineStart,
        int lineEnd,
        Direction direction,
        float outerWidth,
        float innerWidth,
        float insetLeft,
        float insetRight,
        float lineTop,
        in LineMetrics metrics
    ) {
        var x = 0f;
        var children = ChildIds(index);

        for (var i = lineStart; i < lineEnd; i++) {
            var child = children[i];
            if (!ParticipatesInLine(child)) {
                continue;
            }

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
    /// </remarks>
    float DetermineInlineContentWidth(
        int index,
        Direction direction,
        SizingMode widthSizingMode,
        float availableInnerWidth,
        float ownerWidth,
        float ownerHeight,
        int currentDepth
    ) {
        var preferred = 0f;
        var minimum = 0f;

        foreach (var child in ChildIds(index)) {
            if (!ParticipatesInLine(child)) {
                continue;
            }

            var margin = StyleResolution.MarginForAxis(in styles[child], FlexDirection.Row, availableInnerWidth.OrZero());

            // ⚠ A definitely-sized box contributes its own width and its contents are never consulted
            // — CSS Sizing §5.2.2's distinction between a min-content *size* and a min-content
            // *contribution*, which this engine learned late and expensively on the flex side. Asking
            // an empty `width: 400px` box to measure itself under an undefined available width
            // answers zero, because a box with no contents needs no room for them.
            float width;
            if (HasDefiniteLength(child, Dimension.Width, availableInnerWidth)) {
                width = BoundAxis(
                    child,
                    FlexDirection.Row,
                    direction,
                    ResolvedDimension(child, Dimension.Width, availableInnerWidth, availableInnerWidth, direction),
                    availableInnerWidth,
                    availableInnerWidth
                ) + margin;
            } else {
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

                width = results[child].MeasuredDimensions[(int) Dimension.Width] + margin;
            }

            preferred += width;
            minimum = MathF.Max(minimum, width);
        }

        if (widthSizingMode == SizingMode.MaxContent || float.IsNaN(availableInnerWidth)) {
            return preferred;
        }

        return MathF.Max(minimum, MathF.Min(preferred, availableInnerWidth));
    }
}
