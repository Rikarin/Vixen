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
        Close
    }

    /// <summary>One entry in the flattened line stream.</summary>
    internal readonly record struct InlineItem(int Node, InlineItemKind Kind);

    InlineItem[] inlineItems = new InlineItem[16];
    int inlineItemTop;

    LayoutFragment[] fragmentScratch = new LayoutFragment[8];
    int fragmentScratchTop;

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
    ///         ⚠ <b>Three restrictions, each one a place where the honest answer today is "still
    ///         atomic" rather than a guess.</b> An <i>out-of-flow</i> box is not on a line at all. A
    ///         box with a <i>measure function</i> stays atomic, because a measure function is what
    ///         makes a node a leaf. And a non-atomic inline box <i>inside</i> another one stays atomic
    ///         — one level of flattening, not a recursion — which is scope rather than a limit of the
    ///         representation: the arena holds any number of fragments for any node, and what is
    ///         missing is the rebasing of a nested union, not somewhere to put it.
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

            any |= ParticipatesInLine(child);
        }

        return any;
    }

    /// <summary>Flattens a container's children into the stream a line walk consumes.</summary>
    /// <returns>Where this container's range starts; it runs to <c>inlineItemTop</c>.</returns>
    int BuildInlineItems(int index, bool nested) {
        var start = inlineItemTop;

        foreach (var child in ChildIds(index)) {
            if (!ParticipatesInLine(child)) {
                continue;
            }

            // One level only: a span inside a span is an ordinary atomic item, which is what it was
            // before any of this and is still a correct box — just one that does not split.
            if (!nested && IsNonAtomicInline(child)) {
                AppendInlineItem(new InlineItem(child, InlineItemKind.Open));
                BuildInlineItems(child, nested: true);
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

    void AppendFragmentScratch(in LayoutFragment fragment) {
        if (fragmentScratchTop == fragmentScratch.Length) {
            Array.Resize(ref fragmentScratch, fragmentScratch.Length * 2);
        }

        fragmentScratch[fragmentScratchTop++] = fragment;
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
    void CommitInlineBoxFragments(int index, int scratchBase, Direction direction, float innerWidth) {
        var count = fragmentScratchTop - scratchBase;
        if (count <= 0) {
            WriteFragments(index, default);

            return;
        }

        var left = float.PositiveInfinity;
        var top = float.PositiveInfinity;
        var right = float.NegativeInfinity;
        var bottom = float.NegativeInfinity;

        for (var i = scratchBase; i < fragmentScratchTop; i++) {
            ref var box = ref fragmentScratch[i];
            left = MathF.Min(left, box.Left);
            top = MathF.Min(top, box.Top);
            right = MathF.Max(right, box.Left + box.Width);
            bottom = MathF.Max(bottom, box.Top + box.Height);
        }

        for (var i = scratchBase; i < fragmentScratchTop; i++) {
            fragmentScratch[i].Left -= left;
            fragmentScratch[i].Top -= top;
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
        results[index].MeasuredDimensions[(int) Dimension.Width] = right - left;
        results[index].MeasuredDimensions[(int) Dimension.Height] = bottom - top;
        results[index].Direction = direction;

        // ⚠ Claimed on the span's behalf because the claim is true and the rounding pass acts on it:
        // an algorithm DID run for this node's subtree this pass — the parent's — and its children's
        // raw positions were just rewritten. Leaving the previous generation here lets the rounding
        // pass take its "nothing below me moved" shortcut over a subtree that moved.
        results[index].ImplGeneration = generation;
        results[index].GenerationCount = generation;
        flags[index] |= LayoutNodeState.HasNewLayout;
        flags[index] &= ~LayoutNodeState.Dirty;

        WriteFragments(index, fragmentScratch.AsSpan(scratchBase, count));
        fragmentScratchTop = scratchBase;
    }
}
