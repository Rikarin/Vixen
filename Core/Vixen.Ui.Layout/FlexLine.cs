// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Layout;

/// <summary>One row of items in a wrapping flex container, and what is known about it.</summary>
/// <remarks>
///     <para>
///         A line is a <i>range</i> of the parent's children rather than a list of them. Yoga
///         collects the items into a <c>std::vector</c> per line; here the observation that makes
///         that unnecessary is that a line is always contiguous in child order — items that are
///         absolutely positioned or <c>display: none</c> are skipped over, never reordered — so a
///         start and an end index describe the same set with no allocation. A layout pass over a
///         settled tree therefore allocates nothing at all, which a <c>List&lt;int&gt;</c> per line
///         per container per frame would have made impossible.
///     </para>
///     <para>
///         The running totals are mutated in place as free space is distributed, which is what the
///         two-pass min/max resolution in <c>resolveFlexibleLength</c> needs.
///     </para>
/// </remarks>
struct FlexLine {
    /// <summary>The first child index this line covers.</summary>
    public int StartChild;

    /// <summary>One past the last child index this line covers.</summary>
    public int EndChild;

    /// <summary>How many of those children are in flow.</summary>
    public int ItemCount;

    /// <summary>The child index of the last in-flow item, or -1.</summary>
    public int LastItemChild;

    /// <summary>
    ///     Main-axis size of the items at their flex base sizes clamped by their STATED min and max,
    ///     plus margins and gaps. What the container's own content-based main size is made of.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>§4.5's automatic minimum is deliberately NOT in this sum, and the reason is that the
    ///     probe behind it is not accurate enough to size a container with.</b> §9.9.1 does not
    ///     define a flex container's intrinsic main size as the sum of its items' hypothetical sizes
    ///     anyway — it is the sum of their min-content or max-content <i>contributions</i> — so this
    ///     was always an approximation, and it is the one the corpus is calibrated against.
    ///     <c>percentage_moderate_complexity</c> is what happens when the floor is let in: the
    ///     middle box's true content height is 26.176 and
    ///     <see cref="LayoutTree.ComputeMinContentSize" /> answers 31.04 for it, because the
    ///     recursion resolves a grandchild's <c>margin: 5%</c> against the outer container's 194
    ///     rather than the 85.36 it is actually a fraction of, and because a childless box returns
    ///     zero without its own padding. Both are real defects in the probe and neither is this
    ///     algorithm's; until they are fixed, an inflated floor must not become a container's height.
    /// </remarks>
    public float SizeConsumed;

    /// <summary>
    ///     The same sum taken over the items' HYPOTHETICAL main sizes — the flex base clamped by the
    ///     USED min, §4.5's automatic one included.
    /// </summary>
    /// <remarks>
    ///     §9.3 breaks lines by this one: an item goes on the line if its outer hypothetical main
    ///     size fits. §9.7 step 1 also picks grow or shrink by comparing it against the container's
    ///     inner main size. Both are questions about the items; <see cref="SizeConsumed" /> answers a
    ///     question about the container, which is why they are two fields.
    /// </remarks>
    public float HypotheticalSizeConsumed;

    /// <summary>Margins and gaps alone, with no item size in them.</summary>
    /// <remarks>
    ///     ⚠ <b>§9.7's free space is not §9.3's, and this is the part the two sums share.</b> §9.3
    ///     breaks lines by the items' outer <i>hypothetical</i> main sizes; §9.7 step 3 subtracts the
    ///     unfrozen items' outer <i>flex base</i> sizes instead, which is the size before its min and
    ///     max are applied. One number served both, so every clamp was charged to the free space
    ///     twice: once by shrinking the pool it came out of, and again by the distribution pass that
    ///     re-applied it. Keeping the margin-and-gap part separate lets <c>InitialFreeSpace</c> build
    ///     §9.7's sum without walking the gaps again.
    /// </remarks>
    public float MarginAndGapConsumed;

    /// <summary>How many auto margins there are along the main axis of this line.</summary>
    public int AutoMarginCount;

    /// <summary>Total grow factors, floored to one, decremented as space is distributed.</summary>
    public float TotalFlexGrowFactors;

    /// <summary>Total shrink factors scaled by basis, floored to one, decremented as space is distributed.</summary>
    public float TotalFlexShrinkScaledFactors;

    /// <summary>Main-axis space still to hand out. Negative means the line overflows.</summary>
    public float RemainingFreeSpace;

    /// <summary>
    ///     Whether §9.7 step 1 chose the grow factor over the shrink factor for this line.
    /// </summary>
    /// <remarks>
    ///     Decided from the sum of the items' HYPOTHETICAL main sizes against the container's inner
    ///     main size, which is what picks the side each item's freeze test is taken on. It is kept on
    ///     the line because <c>InitialFreeSpace</c> and both distribution passes have to agree about
    ///     which items are frozen; a pass that answered the question from the sign of
    ///     <see cref="RemainingFreeSpace" /> instead would disagree with the pool it was handed.
    /// </remarks>
    public bool UseGrow;

    /// <summary>The line's main-axis extent once the items are placed.</summary>
    public float MainDim;

    /// <summary>The line's cross-axis extent once the items are placed.</summary>
    public float CrossDim;
}
