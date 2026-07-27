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

    /// <summary>Main-axis size of the items plus their margins and the gaps between them.</summary>
    public float SizeConsumed;

    /// <summary>How many auto margins there are along the main axis of this line.</summary>
    public int AutoMarginCount;

    /// <summary>Total grow factors, floored to one, decremented as space is distributed.</summary>
    public float TotalFlexGrowFactors;

    /// <summary>Total shrink factors scaled by basis, floored to one, decremented as space is distributed.</summary>
    public float TotalFlexShrinkScaledFactors;

    /// <summary>Main-axis space still to hand out. Negative means the line overflows.</summary>
    public float RemainingFreeSpace;

    /// <summary>The line's main-axis extent once the items are placed.</summary>
    public float MainDim;

    /// <summary>The line's cross-axis extent once the items are placed.</summary>
    public float CrossDim;
}
