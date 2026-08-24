// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Layout;

/// <summary>
///     CSS Sizing § 5's content keywords — <c>min-content</c>, <c>max-content</c>,
///     <c>fit-content</c> — on the six preferred, minimum and maximum size slots.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>A content keyword is not a length, and the whole of this file is the consequence.</b>
///         Every other unit answers <see cref="StyleLength.Resolve" /> with a number given a
///         containing size: a point is itself, a percentage is a fraction. A content keyword is a
///         fact about the <i>subtree</i>, so no containing size settles it and <c>Resolve</c> returns
///         NaN — which every reader in the algorithm takes for "no size was written". That is why
///         <c>width: max-content</c> used to cascade perfectly and move nothing: the declaration
///         arrived, was read, and meant the same as its own absence.
///     </para>
///     <para>
///         <b>So it is resolved before the algorithm rather than inside it.</b>
///         <see cref="ResolveContentSizes" /> walks the tree once at the top of
///         <see cref="CalculateLayout" />, measures whatever asked to be measured, and writes each
///         answer back into the node's own style as an ordinary <see cref="LayoutUnit.Point" />.
///         Flex, block, grid, inline, <c>BoundAxis</c> and the absolute path then read a number and
///         need to know nothing about any of this. The written values are put back at the end of the
///         pass, so a caller that reads <see cref="GetStyle" /> afterwards still sees what it set.
///     </para>
///     <para>
///         ⚠ <b>A whole-tree pre-pass and not a hook on the node's own layout, because the parent
///         reads the child's size first.</b> That was tried and it fails on the commonest case there
///         is: <c>WalkBlockChildren</c> asks <c>HasDefiniteLength(child, …)</c> and settles the
///         child's width <i>before</i> handing it down, so a substitution performed on the way into
///         the child's own layout arrives after the only reader that mattered. The same is true of
///         <c>ConstrainMinSizeForMode</c> and of the grid track sizer. There is no single seam
///         downstream of every such read; there is one upstream of all of them.
///     </para>
///     <para>
///         ⚠ <b>Bottom-up, and that order is the algorithm rather than a convention.</b> A node's
///         content size is a fact about its children, so a child whose own width is still a keyword
///         would be measured as though it had no width at all. Children are therefore resolved
///         before the node that contains them, and the containing sizes handed down are the ones
///         written in the style — a parent whose width is itself a keyword is an indefinite
///         containing block while its children are being settled, which is exactly what CSS
///         Sizing § 5.2.1 says it is.
///     </para>
///     <para>
///         ⚠ <b>The neutralisation is the recursion guard, and it is also the specification.</b>
///         Before a node's own probes run, every content keyword on it is set to
///         <see cref="LayoutUnit.Undefined" />. That stops a probe — which is a layout of the very
///         same node — asking the same question again, and it is separately what
///         <c>min-width: min-content</c> has to mean: a box's min-content size is measured with the
///         box's own intrinsic bounds out of the way, or the definition eats itself.
///     </para>
///     <para>
///         ⚠ <b>The block axis has one content size, not two.</b> On the inline axis
///         <c>min-content</c> and <c>max-content</c> are different questions — the longest word
///         against the whole paragraph. On the block axis CSS Sizing § 5.1 makes them the same
///         number: there is no narrowest height, only the height the content takes at the inline size
///         it has. So <c>h-min</c>, <c>h-max</c> and <c>h-fit</c> agree and the store measures once.
///         That is not a shortcut taken for speed — asking for a height with nothing to spare trips
///         <c>MeasureNodeWithFixedSize</c>, which answers a fixed request of zero with zero.
///     </para>
///     <para>
///         ⚠ <b><see cref="LayoutUnit.Stretch" /> is deliberately not resolved here</b>, and the two
///         fixtures that set it are the reason. <c>Stretch_width</c> wants the containing block's
///         width and <c>Stretch_flex_basis_column</c> wants its own content's height, from the same
///         keyword; both pass today because the keyword behaves as <c>undefined</c> and each falls
///         through to a different default. Nothing in <c>Vixen.Ui.Styling.Utilities</c> emits it, so
///         resolving it would close one fixture by opening the other, for no class.
///     </para>
///     <para>
///         ⚠ <b>What <c>fit-content</c> is measured against, stated rather than left to be found.</b>
///         Its ceiling is the containing block's content extent as the pre-pass estimates it on the
///         way down: a definite size where the box has one, and otherwise the space its parent had
///         left after its own edges — CSS 2.1 § 10.3.3's stretch, which is right for a block-level
///         box and an upper bound for a flex item that will go on to shrink. Where the two differ the
///         box comes out at its max-content size clamped to a slightly generous ceiling, never at
///         zero and never unclamped.
///     </para>
/// </remarks>
public sealed partial class LayoutTree {
    /// <summary>Whether any live node has a content keyword on it, asked once per pass.</summary>
    /// <remarks>
    ///     The same bargain <c>treeHasFloats</c> makes, and for the same reason: one linear scan of
    ///     the flags, against a recursive walk of the whole tree that all but a few documents would
    ///     find nothing in.
    /// </remarks>
    bool treeHasContentSizes;

    /// <summary>The written lengths of every node this pass has substituted.</summary>
    readonly List<ContentSizeSubstitution> contentSubstitutions = [];

    void RefreshContentSizePresence() {
        treeHasContentSizes = false;

        for (var i = 0; i < capacity; i++) {
            if ((flags[i] & LayoutNodeState.Live) == 0) {
                continue;
            }

            if (HasContentBasedLength(i)) {
                treeHasContentSizes = true;

                return;
            }
        }
    }

    /// <summary>Turns every content keyword in the subtree into a number, children first.</summary>
    /// <param name="index">The node.</param>
    /// <param name="availableWidth">The containing block's content-box width, or NaN.</param>
    /// <param name="availableHeight">Its content-box height, or NaN.</param>
    /// <param name="ownerDirection">The writing direction the node inherits.</param>
    void ResolveContentSizes(int index, float availableWidth, float availableHeight, Direction ownerDirection) {
        if (styles[index].Display == Display.None) {
            return;
        }

        var direction = StyleResolution.ResolveDirection(in styles[index], ownerDirection);
        var innerWidth = ContentExtent(index, Dimension.Width, direction, availableWidth, availableWidth);
        var innerHeight = ContentExtent(index, Dimension.Height, direction, availableHeight, availableWidth);

        foreach (var child in ChildIds(index)) {
            ResolveContentSizes(child, innerWidth, innerHeight, direction);
        }

        if (HasContentBasedLength(index)) {
            ResolveContentBasedLengths(index, availableWidth, availableHeight, ownerDirection);
        }
    }

    /// <summary>The content-box extent this node offers its children on one axis, or NaN.</summary>
    /// <remarks>
    ///     A definite size where the style has one; otherwise CSS 2.1 § 10.3.3's stretch — whatever
    ///     the parent had left, less this box's own margin, border and padding. A keyword size is
    ///     neither: it has not been measured yet at the moment this is asked, so the box is an
    ///     indefinite containing block while its own children are settled.
    /// </remarks>
    float ContentExtent(int index, Dimension dimension, Direction direction, float reference, float ownerWidth) {
        var axis = dimension == Dimension.Width ? FlexDirection.Row : FlexDirection.Column;
        var inset = StyleResolution.ContentInsetForAxis(in styles[index], axis, direction, ownerWidth);
        var stated = StyleResolution.ProcessedDimension(in styles[index], dimension);

        if (stated.IsResolvable) {
            var value = StyleResolution.WithBoxSizing(in styles[index], stated.Resolve(reference), dimension, ownerWidth, direction);

            return float.IsNaN(value) ? float.NaN : MathF.Max(0f, value - inset);
        }

        if (stated.IsContentBased || float.IsNaN(reference)) {
            return float.NaN;
        }

        var margin = StyleResolution.MarginForAxis(in styles[index], axis, ownerWidth);

        return MathF.Max(0f, reference - margin - inset);
    }

    /// <summary>Whether any of the node's six size slots was written as a content keyword.</summary>
    bool HasContentBasedLength(int index) {
        ref var style = ref styles[index];

        for (var axis = 0; axis < 2; axis++) {
            if (style.Dimensions[axis].IsContentBased
                || style.MinDimensions[axis].IsContentBased
                || style.MaxDimensions[axis].IsContentBased) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Measures whatever the node asked for by keyword and writes the answers into its style.</summary>
    void ResolveContentBasedLengths(int index, float availableWidth, float availableHeight, Direction ownerDirection) {
        var written = new ContentSizeSubstitution {
            Index = index,
            Dimensions = styles[index].Dimensions,
            MinDimensions = styles[index].MinDimensions,
            MaxDimensions = styles[index].MaxDimensions
        };

        contentSubstitutions.Add(written);

        for (var axis = 0; axis < 2; axis++) {
            Neutralise(ref styles[index].Dimensions[axis]);
            Neutralise(ref styles[index].MinDimensions[axis]);
            Neutralise(ref styles[index].MaxDimensions[axis]);
        }

        var direction = StyleResolution.ResolveDirection(in styles[index], ownerDirection);

        // ── The inline axis first ───────────────────────────────────────────────────────────────
        // CSS Sizing § 4.1's order, and load-bearing rather than tidy: the block-axis answer is a
        // function of the used inline size — a paragraph has no height until it knows how wide it is
        // — so the width has to be a number before the height is asked for.
        ResolveAxis(
            index,
            Dimension.Width,
            in written,
            direction,
            ownerDirection,
            availableWidth,
            float.NaN,
            SizingMode.MaxContent,
            availableWidth,
            availableHeight
        );

        var crossAvailable = float.NaN;
        var crossMode = SizingMode.MaxContent;
        var usedWidth = StyleResolution.ProcessedDimension(in styles[index], Dimension.Width);

        if (usedWidth.IsResolvable) {
            var resolved = StyleResolution.WithBoxSizing(
                in styles[index],
                usedWidth.Resolve(availableWidth),
                Dimension.Width,
                availableWidth,
                direction
            );

            if (!float.IsNaN(resolved)) {
                crossAvailable = resolved + StyleResolution.MarginForAxis(in styles[index], FlexDirection.Row, availableWidth);
                crossMode = SizingMode.StretchFit;
            }
        }

        ResolveAxis(
            index,
            Dimension.Height,
            in written,
            direction,
            ownerDirection,
            availableHeight,
            crossAvailable,
            crossMode,
            availableWidth,
            availableHeight
        );
    }

    /// <summary>Substitutes the three slots on one axis from at most two measurements.</summary>
    void ResolveAxis(
        int index,
        Dimension dimension,
        in ContentSizeSubstitution written,
        Direction direction,
        Direction ownerDirection,
        float available,
        float crossAvailable,
        SizingMode crossMode,
        float ownerWidth,
        float ownerHeight
    ) {
        var axis = (int) dimension;
        var preferred = written.Dimensions[axis];
        var minimum = written.MinDimensions[axis];
        var maximum = written.MaxDimensions[axis];

        if (!preferred.IsContentBased && !minimum.IsContentBased && !maximum.IsContentBased) {
            return;
        }

        var flexAxis = dimension == Dimension.Width ? FlexDirection.Row : FlexDirection.Column;
        var margin = StyleResolution.MarginForAxis(in styles[index], flexAxis, ownerWidth);

        // ⚠ A probe lays the subtree out, and laying a block container out appends its floats to
        // whichever exclusion context is current — which here is the one belonging to a box whose
        // position nobody has decided yet. `DetermineBlockContentWidth`'s own probe takes the same
        // precaution: left alone the entries narrow a real box somewhere above, for a measurement
        // that was never a placement.
        var mark = floatExclusions.Count;

        var maxContent = ProbeContentSize(
            index,
            dimension,
            float.NaN,
            SizingMode.MaxContent,
            crossAvailable,
            crossMode,
            ownerDirection,
            ownerWidth,
            ownerHeight
        );

        // ⚠ Only the inline axis has a second answer. See the remarks on the class: there is no
        // narrowest height, and asking for one would be answered with zero rather than refused.
        var minContent = dimension == Dimension.Width
            ? ProbeContentSize(
                index,
                dimension,
                margin,
                SizingMode.FitContent,
                crossAvailable,
                crossMode,
                ownerDirection,
                ownerWidth,
                ownerHeight
            )
            : maxContent;

        if (treeHasFloats) {
            floatExclusions.RemoveRange(mark, floatExclusions.Count - mark);
        }

        // What the containing block leaves for this box's border box, which is what `fit-content`
        // fits into. The margin comes off because a box does not get to stretch over its own.
        var room = float.IsNaN(available) ? float.NaN : MathF.Max(0f, available - margin);

        Substitute(index, dimension, preferred, minContent, maxContent, room, direction, ownerWidth, ref styles[index].Dimensions[axis]);
        Substitute(index, dimension, minimum, minContent, maxContent, room, direction, ownerWidth, ref styles[index].MinDimensions[axis]);
        Substitute(index, dimension, maximum, minContent, maxContent, room, direction, ownerWidth, ref styles[index].MaxDimensions[axis]);
    }

    /// <summary>Writes one slot's keyword back as the number it stands for.</summary>
    void Substitute(
        int index,
        Dimension dimension,
        StyleLength keyword,
        float minContent,
        float maxContent,
        float room,
        Direction direction,
        float ownerWidth,
        ref StyleLength slot
    ) {
        if (!keyword.IsContentBased) {
            return;
        }

        var value = keyword.Unit switch {
            LayoutUnit.MinContent => minContent,

            // CSS Sizing § 5.1 exactly: the max-content size, floored by the min-content size and
            // capped by the space on offer. With nothing on offer there is nothing to cap it with,
            // which is the sentence that makes `fit-content` in an unconstrained container mean
            // `max-content` rather than zero.
            LayoutUnit.FitContent => float.IsNaN(room)
                ? maxContent
                : MathF.Max(minContent, MathF.Min(maxContent, room)),
            _ => maxContent
        };

        if (float.IsNaN(value)) {
            slot = StyleLength.Undefined;

            return;
        }

        // ⚠ The probe answered with a BORDER box and the slot is read back through `WithBoxSizing`,
        // which adds the padding and border again for a `content-box` node. Writing the border box
        // straight in counts both of them twice, in an amount equal to the padding — which reads as
        // a layout quirk rather than as the arithmetic error it is.
        if (styles[index].BoxSizing == BoxSizing.ContentBox) {
            value -= StyleResolution.PaddingAndBorderForDimension(in styles[index], dimension, direction, ownerWidth);
        }

        slot = StyleLength.Points(MathF.Max(0f, value));
    }

    /// <summary>Lays the node out for its own size and hands back the axis that was asked about.</summary>
    /// <remarks>
    ///     <c>CalculateLayoutImpl</c> and not <c>CalculateLayoutInternal</c>, so a probe never lands
    ///     in the measurement cache. The cache is keyed on the available size and the mode, and a
    ///     probe of a neutralised node and a real request for the substituted one can agree on both
    ///     — the entry would then answer the second question with the first one's number, which is
    ///     the same node measured under two different styles.
    /// </remarks>
    float ProbeContentSize(
        int index,
        Dimension dimension,
        float available,
        SizingMode mode,
        float crossAvailable,
        SizingMode crossMode,
        Direction ownerDirection,
        float ownerWidth,
        float ownerHeight
    ) {
        if (dimension == Dimension.Width) {
            CalculateLayoutImpl(
                index,
                available,
                crossAvailable,
                ownerDirection,
                mode,
                crossMode,
                ownerWidth,
                ownerHeight,
                performLayout: false,
                0
            );
        } else {
            CalculateLayoutImpl(
                index,
                crossAvailable,
                available,
                ownerDirection,
                crossMode,
                mode,
                ownerWidth,
                ownerHeight,
                performLayout: false,
                0
            );
        }

        return results[index].MeasuredDimensions[(int) dimension];
    }

    /// <summary>Puts every substituted style back the way the caller wrote it.</summary>
    /// <remarks>
    ///     ⚠ From a <c>finally</c>, and that is not defensive tidying: a measure function that throws
    ///     must not leave a tree whose <see cref="GetStyle" /> answers with a width nobody set.
    /// </remarks>
    void RestoreContentBasedLengths() {
        foreach (var entry in contentSubstitutions) {
            styles[entry.Index].Dimensions = entry.Dimensions;
            styles[entry.Index].MinDimensions = entry.MinDimensions;
            styles[entry.Index].MaxDimensions = entry.MaxDimensions;
        }

        contentSubstitutions.Clear();
    }

    static void Neutralise(ref StyleLength length) {
        if (length.IsContentBased) {
            length = StyleLength.Undefined;
        }
    }

    /// <summary>One node's three pairs of size slots, as the caller wrote them.</summary>
    struct ContentSizeSubstitution {
        public int Index;
        public DimensionLengths Dimensions;
        public DimensionLengths MinDimensions;
        public DimensionLengths MaxDimensions;
    }
}
