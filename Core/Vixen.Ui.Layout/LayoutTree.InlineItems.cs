// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Layout;

/// <summary>What a line box is actually made of, once a box on it may be an inline box's edge.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is the one structural thing fragmentation cost, and it is smaller than the
///         invariant it broke.</b> Before it, a line box was a contiguous range of
///         <c>ChildIds</c> — which is what let the whole inline algorithm allocate nothing, exactly as
///         a flex line does. A non-atomic inline box ends that: a <c>span</c> is not <i>on</i> a line,
///         its <i>children</i> are, and the span contributes only its two horizontal edges wherever
///         they happen to fall. So the walk is over a flattened stream of <see cref="InlineItemKind" />
///         entries rather than over the child span directly.
///     </para>
///     <para>
///         ⚠ <b>A line is still a contiguous range — of this stream instead of of the child list —
///         which is why <c>MeasureLine</c> and <c>PlaceLine</c> kept their shape.</b> The stream lives
///         in a watermarked scratch buffer reused across passes, so the steady state allocates
///         nothing and <c>LayoutPassTests</c>'s zero-byte gate still holds. Nested inline formatting
///         contexts push and pop their own range, which is the same discipline the grid scratch uses.
///     </para>
///     <para>
///         ⚠ <b>The buffer moves when it grows, so this file indexes it and never holds a
///         <c>Span</c> across anything that can append.</b> Sizing an item runs a whole nested layout,
///         which can build a stream of its own; a span captured before that call would be pointing at
///         the old array afterwards. That is the kind of bug that reproduces only on the tree that
///         happens to cross a power of two.
///     </para>
/// </remarks>
public sealed partial class LayoutTree {
    /// <summary>What one entry in the flattened line stream stands for.</summary>
    internal enum InlineItemKind : byte {
        /// <summary>A box that sits on the line and takes room: the ordinary case.</summary>
        Atomic,

        /// <summary>The inline-start edge of a non-atomic inline box.</summary>
        Open,

        /// <summary>The inline-end edge of a non-atomic inline box.</summary>
        Close,

        /// <summary>
        ///     A floated box written between two items, which takes no room on the line and is placed
        ///     at the line's own top instead.
        /// </summary>
        /// <remarks>
        ///     ⚠ <b>CSS 2.1 §9.5.1's rules 5 and 6, and the reason a float has to be <i>in</i> this
        ///     stream rather than beside it.</b> §9.7 makes a floated box block-level whatever its
        ///     <c>display</c> says, so the obvious reading is that it ends the run and is placed by the
        ///     block walk after it — which is where it was, one line lower than Chrome puts it. What
        ///     rules 5 and 6 actually say is narrower: a float may not start <i>above</i> the top of
        ///     any line box holding earlier content, which for a float written mid-run is that line's
        ///     top exactly. So its position is decided by the line walk, and it then shortens the very
        ///     line it was written on — including the part of that line that came before it.
        /// </remarks>
        Float
    }

    /// <summary>One entry in the flattened line stream.</summary>
    internal readonly record struct InlineItem(int Node, InlineItemKind Kind);

    InlineItem[] inlineItems = new InlineItem[16];
    int inlineItemTop;

    LayoutFragment[] fragmentScratch = new LayoutFragment[8];

    // ⚠ <b>A per-owner chain rather than a per-owner range, and nesting is the whole reason.</b> One
    // box's fragments used to be the contiguous slice `[base, top)`, which held while only one box
    // could be open: a `Close` was always the innermost thing outstanding. With spans inside spans
    // both are open when a line ends and both want a continuation fragment, so whichever appends
    // second lands inside the other's slice. Linking each owner's fragments instead makes the order
    // they were appended in irrelevant — the only thing a commit needs is its own chain.
    int[] fragmentScratchNext = new int[8];
    int fragmentScratchTop;

    // Where a chain is copied to so `WriteFragments` gets one span. Reused; never read across calls.
    LayoutFragment[] fragmentGather = new LayoutFragment[4];

    // The non-atomic inline boxes a line walk is currently inside, outermost first. A field with a
    // watermark rather than a local, for `inlineItems`' reason: a nested container's walk runs inside
    // this one's, so the two need one buffer and a saved top rather than an array each.
    OpenInlineBox[] openBoxes = new OpenInlineBox[4];
    int openBoxTop;

    /// <summary>
    ///     Whether this box's children join its parent's lines instead of the box itself doing so.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         CSS Display §2.2's non-replaced <c>inline</c> box. It generates no box of its own on a
    ///         line; it wraps a run of other boxes and draws its border and padding around whichever
    ///         parts of that run each line ended up with.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A <c>display: inline</c> box with no box children is deliberately NOT this.</b>
    ///         That is the case that dominates a user interface — a span holding text — and for it
    ///         atomic and non-atomic agree exactly, because there is nothing to split. Treating it as
    ///         atomic keeps it on the path the measure cache already serves, and keeps the text
    ///         wrapper in <c>Vixen.Ui</c> the only thing that breaks a string.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Two restrictions, each one a place where the honest answer today is "still
    ///         atomic" rather than a guess.</b> An <i>out-of-flow</i> box is not on a line at all. A
    ///         box with a <i>measure function</i> stays atomic, because a measure function is what
    ///         makes a node a leaf.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A third one is gone: a non-atomic inline box inside another one is flattened
    ///         too, to any depth.</b> This paragraph used to say the missing piece was "the rebasing
    ///         of a nested union", and that was already free — an inner box commits first, writing
    ///         its position in the container's coordinates, and the outer's commit then rebases it
    ///         like any other child. What actually held was the fragment scratch: one box's
    ///         fragments were the contiguous slice <c>[base, top)</c>, and two boxes open when a
    ///         line ends both want a continuation fragment, so the second to append lands inside the
    ///         first's slice. Chaining each owner's fragments instead makes append order irrelevant.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And a fourth, which is not scope but a hole that would swallow a child
    ///         silently.</b> A box with an absolutely positioned <i>direct</i> child stays atomic. The
    ///         absolute walk descends from the node it was called on and recurses only through
    ///         <see cref="PositionType.Static" /> children — and this store's default is
    ///         <see cref="PositionType.Relative" />, Yoga's, not CSS's <c>static</c>. So a flattened
    ///         box, whose own <c>CalculateLayoutImpl</c> never runs, is where that walk stops: its
    ///         out-of-flow children would be sized, given a static position, and then never positioned
    ///         by anybody. Refusing to flatten is a box that is merely un-split; flattening it is a
    ///         child that vanishes. Deeper out-of-flow descendants are safe, because they sit inside
    ///         atomic items that do run their own layout. <c>InlineKnownGaps.txt</c> carries all four.
    ///     </para>
    /// </remarks>
    bool IsNonAtomicInline(int index) {
        if (styles[index].Display != Display.Inline || styles[index].PositionType == PositionType.Absolute) {
            return false;
        }

        if ((flags[index] & LayoutNodeState.HasMeasureFunction) != 0) {
            return false;
        }

        var any = false;

        foreach (var child in ChildIds(index)) {
            if (styles[child].PositionType == PositionType.Absolute) {
                return false;
            }

            // ⚠ And a fifth restriction, for the same shape of reason as the fourth. A float is
            // placed by the line walk in the *container's* coordinates, and a flattened box's
            // children are rebased into the box's own on the way out — so a float inside a flattened
            // span would be moved by the span's origin a second time. Staying atomic sends the span
            // through its own layout, where its float is an ordinary block-level one placed against
            // the span itself, which is a box that is merely un-split rather than one in the wrong
            // place.
            if (styles[child].Float != FloatSide.None) {
                return false;
            }

            any |= ParticipatesInLine(child);
        }

        return any;
    }

    /// <summary>Flattens a container's children into the stream a line walk consumes.</summary>
    /// <returns>Where this container's range starts; it runs to <c>inlineItemTop</c>.</returns>
    int BuildInlineItems(int index) => BuildInlineItems(index, 0, links[index].ChildCount);

    /// <summary>Flattens a <i>sub-range</i> of a container's children into that same stream.</summary>
    /// <param name="index">The container.</param>
    /// <param name="childStart">The first child position to take, inclusive.</param>
    /// <param name="childEnd">The last child position to take, exclusive.</param>
    /// <returns>Where this range starts in the stream; it runs to <c>inlineItemTop</c>.</returns>
    /// <remarks>
    ///     ⚠ <b>The sub-range is the whole of what CSS 2.1 §9.2.1.1's anonymous block box costs.</b>
    ///     An anonymous block box wraps one <i>run</i> of a mixed container's inline-level children,
    ///     and it takes initial values for every non-inherited property — no background, no border, no
    ///     padding, no margin, no event target — so it is never painted and never hit-tested and needs
    ///     no stored rectangle. What it needs is exactly this: the ability to point the line walk at
    ///     part of a child list rather than all of it. Everything downstream — <c>MeasureLine</c>,
    ///     <c>PlaceLine</c>, the fragment scratch — already worked on a range and did not move.
    /// </remarks>
    int BuildInlineItems(int index, int childStart, int childEnd) {
        var start = inlineItemTop;
        var childIds = ChildIds(index);

        for (var i = childStart; i < childEnd; i++) {
            var child = childIds[i];

            if (!ParticipatesInLine(child)) {
                continue;
            }

            // ⚠ Before the flattening test, because a floated `inline-block` answers `true` to
            // `IsInlineLevel` and would otherwise be laid out as an ordinary box on the line with
            // §9.5 never consulted — which is what it was, and is worse than the block-level float
            // being one line low: the exclusion list never heard of it at all.
            if (styles[child].Float != FloatSide.None) {
                AppendInlineItem(new InlineItem(child, InlineItemKind.Float));

                continue;
            }

            // ⚠ Any depth. A span inside a span used to stop here and be an atomic item — a correct
            // box that could not split — and what kept it there was the shared fragment scratch,
            // not the arena: two boxes open at one line's end both want a continuation fragment and
            // only one of them could own the contiguous slice. `fragmentScratchNext` retires that.
            if (IsNonAtomicInline(child)) {
                AppendInlineItem(new InlineItem(child, InlineItemKind.Open));
                BuildInlineItems(child);
                AppendInlineItem(new InlineItem(child, InlineItemKind.Close));

                continue;
            }

            AppendInlineItem(new InlineItem(child, InlineItemKind.Atomic));
        }

        return start;
    }

    void AppendInlineItem(InlineItem item) {
        if (inlineItemTop == inlineItems.Length) {
            Array.Resize(ref inlineItems, inlineItems.Length * 2);
        }

        inlineItems[inlineItemTop++] = item;
    }

    /// <summary>Records one fragment and returns its slot, so its owner can chain it.</summary>
    int AppendFragmentScratch(in LayoutFragment fragment) {
        if (fragmentScratchTop == fragmentScratch.Length) {
            Array.Resize(ref fragmentScratch, fragmentScratch.Length * 2);
            Array.Resize(ref fragmentScratchNext, fragmentScratch.Length);
        }

        fragmentScratch[fragmentScratchTop] = fragment;
        fragmentScratchNext[fragmentScratchTop] = -1;

        return fragmentScratchTop++;
    }

    /// <summary>How much the inline-start edge of a non-atomic inline box advances the line.</summary>
    float InlineBoxStartEdge(int index, Direction direction, float innerWidth) =>
        StyleResolution.InlineStartMargin(in styles[index], FlexDirection.Row, direction, innerWidth)
        + StyleResolution.InlineStartBorder(in styles[index], FlexDirection.Row, direction)
        + StyleResolution.InlineStartPadding(in styles[index], FlexDirection.Row, direction, innerWidth);

    /// <summary>How much the inline-end edge of a non-atomic inline box advances the line.</summary>
    float InlineBoxEndEdge(int index, Direction direction, float innerWidth) =>
        StyleResolution.InlineEndMargin(in styles[index], FlexDirection.Row, direction, innerWidth)
        + StyleResolution.InlineEndBorder(in styles[index], FlexDirection.Row, direction)
        + StyleResolution.InlineEndPadding(in styles[index], FlexDirection.Row, direction, innerWidth);

    /// <summary>
    ///     Writes the box metrics of a non-atomic inline box, which nothing else will.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A flattened box never reaches <c>CalculateLayoutImpl</c>, and that is where every
    ///     other node's resolved margin, border and padding get written.</b> A consumer reading
    ///     <c>GetComputedPadding</c> on a span would otherwise get whatever the last pass that did lay
    ///     it out left there, or zeroes — and the draw list reads exactly that to inset a background.
    ///     So the one thing the dispatch would have done for it is done here.
    /// </remarks>
    void ResolveInlineBoxMetrics(int index, Direction direction, float ownerWidth) {
        var row = FlexDirection.Row;
        var column = FlexDirection.Column;
        var startEdge = direction == Direction.Rtl ? Edge.Right : Edge.Left;
        var endEdge = direction == Direction.Rtl ? Edge.Left : Edge.Right;

        results[index].Margin[(int) startEdge] = StyleResolution.InlineStartMargin(in styles[index], row, direction, ownerWidth);
        results[index].Margin[(int) endEdge] = StyleResolution.InlineEndMargin(in styles[index], row, direction, ownerWidth);
        results[index].Margin[(int) Edge.Top] = StyleResolution.InlineStartMargin(in styles[index], column, direction, ownerWidth);
        results[index].Margin[(int) Edge.Bottom] = StyleResolution.InlineEndMargin(in styles[index], column, direction, ownerWidth);

        results[index].Border[(int) startEdge] = StyleResolution.InlineStartBorder(in styles[index], row, direction);
        results[index].Border[(int) endEdge] = StyleResolution.InlineEndBorder(in styles[index], row, direction);
        results[index].Border[(int) Edge.Top] = StyleResolution.InlineStartBorder(in styles[index], column, direction);
        results[index].Border[(int) Edge.Bottom] = StyleResolution.InlineEndBorder(in styles[index], column, direction);

        results[index].Padding[(int) startEdge] = StyleResolution.InlineStartPadding(in styles[index], row, direction, ownerWidth);
        results[index].Padding[(int) endEdge] = StyleResolution.InlineEndPadding(in styles[index], row, direction, ownerWidth);
        results[index].Padding[(int) Edge.Top] = StyleResolution.InlineStartPadding(in styles[index], column, direction, ownerWidth);
        results[index].Padding[(int) Edge.Bottom] = StyleResolution.InlineEndPadding(in styles[index], column, direction, ownerWidth);

        results[index].Direction = direction;
        results[index].InlineBaseline = float.NaN;
    }

    /// <summary>
    ///     Turns the fragments a span accumulated into its stored layout, and rebases what is inside
    ///     it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The union is the node's own rectangle, and it is the right answer rather than a
    ///         compromise.</b> CSS 2.1 §10.1 makes the containing block of an absolutely positioned
    ///         descendant of an inline box the bounding box of its first and last fragments — so the
    ///         union is what the absolute walk wants — and it is also the rectangle a scroll extent and
    ///         a coarse hit test want. The individual boxes are still there for whoever needs them.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Rebasing the children is not bookkeeping, it is the thing that keeps every existing
    ///         consumer correct.</b> A position in this store is an offset from the <i>parent's</i>
    ///         border box, and the parent of a span's child is the span. The line walk placed those
    ///         children in the container's coordinates because that is where lines live, so each one is
    ///         moved back by the union's origin. Skip it and the absolute walk adds the span's offset a
    ///         second time, which puts the contents of every indented span at twice the indent — right
    ///         at zero and wrong everywhere else, which is the failure mode that survives a demo.
    ///     </para>
    /// </remarks>
    void CommitInlineBoxFragments(int index, int first, Direction direction, float innerWidth) {
        var count = 0;

        for (var at = first; at >= 0; at = fragmentScratchNext[at]) {
            count++;
        }

        if (count == 0) {
            WriteFragments(index, default);

            return;
        }

        if (fragmentGather.Length < count) {
            Array.Resize(ref fragmentGather, Math.Max(count, fragmentGather.Length * 2));
        }

        var written = 0;

        for (var at = first; at >= 0; at = fragmentScratchNext[at]) {
            fragmentGather[written++] = fragmentScratch[at];
        }

        var left = float.PositiveInfinity;
        var top = float.PositiveInfinity;
        var right = float.NegativeInfinity;
        var bottom = float.NegativeInfinity;

        for (var i = 0; i < count; i++) {
            ref var box = ref fragmentGather[i];
            left = MathF.Min(left, box.Left);
            top = MathF.Min(top, box.Top);
            right = MathF.Max(right, box.Left + box.Width);
            bottom = MathF.Max(bottom, box.Top + box.Height);
        }

        for (var i = 0; i < count; i++) {
            fragmentGather[i].Left -= left;
            fragmentGather[i].Top -= top;
        }

        foreach (var child in ChildIds(index)) {
            // ⚠ Only what the line walk actually placed. A `display: none` child was zeroed on the
            // way in, and moving a zero by the union's origin turns "nowhere" into a real negative
            // rectangle just off the top-left of the span — which is not nowhere.
            if (!ParticipatesInLine(child)) {
                continue;
            }

            results[child].Position[(int) Edge.Left] -= left;
            results[child].Position[(int) Edge.Top] -= top;
        }

        // ⚠ §9.4.3's offset is applied to the union and not to the fragments, which is both cheaper
        // and the only reading that is right: `position: relative` moves a box *and everything in
        // it*, and the children are already expressed relative to this origin. Shifting each fragment
        // instead would move the boxes and leave their contents behind. The negation in RTL is the
        // same rule `PlaceLine` applies to an atomic item — §9.4.3's offsets are physical even where
        // the flow is not.
        var relativeX = direction == Direction.Ltr
            ? RelativePosition(index, FlexDirection.Row, direction, innerWidth)
            : -RelativePosition(index, FlexDirection.Row, direction, innerWidth);

        results[index].Position[(int) Edge.Left] = left + relativeX;
        results[index].Position[(int) Edge.Top] = top + RelativePosition(index, FlexDirection.Column, direction, bottom - top);
        results[index].Dimensions[(int) Dimension.Width] = right - left;
        results[index].Dimensions[(int) Dimension.Height] = bottom - top;
        // Nothing clamped this union, so the two measurements are the same number.
        SetMeasuredDimension(index, Dimension.Width, right - left, right - left);
        SetMeasuredDimension(index, Dimension.Height, bottom - top, bottom - top);
        results[index].Direction = direction;

        // ⚠ Claimed on the span's behalf because the claim is true and the rounding pass acts on it:
        // an algorithm DID run for this node's subtree this pass — the parent's — and its children's
        // raw positions were just rewritten. Leaving the previous generation here lets the rounding
        // pass take its "nothing below me moved" shortcut over a subtree that moved.
        results[index].ImplGeneration = generation;
        results[index].GenerationCount = generation;
        flags[index] |= LayoutNodeState.HasNewLayout;
        flags[index] &= ~LayoutNodeState.Dirty;

        // ⚠ No rewind of `fragmentScratchTop` here, and its absence is what makes nesting work. An
        // inner box closes while its outer is still open and still holding fragments further up the
        // array; rewinding to the inner's first slot would drop them. The whole line walk's
        // allocation is released at once by `WalkInlineLines`' `finally`, which is where the
        // watermark belonged all along — the per-box rewind was an optimisation that only happened
        // to be sound while a box could not contain one.
        WriteFragments(index, fragmentGather.AsSpan(0, count));
    }
}
