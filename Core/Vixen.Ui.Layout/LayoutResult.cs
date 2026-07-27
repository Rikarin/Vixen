// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Vixen.Ui.Layout;

/// <summary>Four resolved lengths, one per physical edge, in left-top-right-bottom order.</summary>
[InlineArray(4)]
public struct EdgeValues {
    float element;
}

/// <summary>Two resolved lengths, width then height.</summary>
[InlineArray(2)]
public struct DimensionValues {
    float element;
}

/// <summary>What one layout pass decided about a node.</summary>
/// <remarks>
///     Everything here is physical and resolved — the flow-relative reasoning happens inside the
///     algorithm and does not survive it. A consumer reading a rectangle should not have to know
///     which way the text runs.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct LayoutResult {
    /// <summary>The offset from the parent's content box, per physical edge.</summary>
    public EdgeValues Position;

    /// <summary>The border-box size.</summary>
    public DimensionValues Dimensions;

    /// <summary>The offset after pixel rounding. What <see cref="LayoutTree.GetLeft" /> returns.</summary>
    /// <remarks>
    ///     Kept apart from <see cref="Position" /> rather than replacing it, which is the difference
    ///     between this and the reference implementation. Yoga rounds in place, so the next pass
    ///     reads rounded values for every node it does not recompute and the rounding compounds:
    ///     laying out incrementally and laying out from cold stop agreeing. Writing the rounded
    ///     result somewhere else makes rounding a pure function of the raw layout, which is both
    ///     easier to reason about and the thing that lets the pass skip unchanged subtrees.
    /// </remarks>
    public EdgeValues RoundedPosition;

    /// <summary>The size after pixel rounding. What <see cref="LayoutTree.GetWidth" /> returns.</summary>
    public DimensionValues RoundedDimensions;

    /// <summary>The size the algorithm settled on, before it was written to <see cref="Dimensions" />.</summary>
    public DimensionValues MeasuredDimensions;

    /// <summary>The resolved margin.</summary>
    public EdgeValues Margin;

    /// <summary>The resolved border.</summary>
    public EdgeValues Border;

    /// <summary>The resolved padding.</summary>
    public EdgeValues Padding;

    /// <summary>The direction this node was laid out in.</summary>
    public Direction Direction;

    /// <summary>Whether the content did not fit.</summary>
    public bool HadOverflow;

    /// <summary>The main size this node contributed before growing and shrinking. NaN if unset.</summary>
    public float ComputedFlexBasis;

    /// <summary>The pass in which <see cref="ComputedFlexBasis" /> was computed.</summary>
    public uint ComputedFlexBasisGeneration;

    /// <summary>
    ///     The automatic minimum main size from CSS Flexbox §4.5, or NaN when none applies.
    /// </summary>
    public float ComputedAutoMinMainSize;

    /// <summary>The pass in which this node was last laid out.</summary>
    public uint GenerationCount;

    /// <summary>
    ///     The pass in which the algorithm actually ran for this node, as opposed to answering from
    ///     cache.
    /// </summary>
    /// <remarks>
    ///     The distinction is what makes incremental pixel rounding possible. Answering from cache
    ///     rewrites this node's own size and nothing else; running the algorithm rewrites its
    ///     children's positions. So a node whose algorithm did not run this pass has a subtree that
    ///     nothing touched.
    /// </remarks>
    public uint ImplGeneration;

    /// <summary>The absolute offset this node's children were last rounded against.</summary>
    public float RoundedAbsoluteLeft;

    /// <summary>The absolute offset this node's children were last rounded against.</summary>
    public float RoundedAbsoluteTop;

    /// <summary>The owner direction of the pass that produced this result.</summary>
    public Direction LastOwnerDirection;

    /// <summary>Where the next measurement goes in the ring of cached ones.</summary>
    public uint NextCachedMeasurementsIndex;

    /// <summary>The measurement cache.</summary>
    public CachedMeasurements CachedMeasurements;

    /// <summary>The last full layout, cached separately from the measurements.</summary>
    public CachedMeasurement CachedLayout;

    /// <summary>Which line of a wrapping container this node landed on.</summary>
    public int LineIndex;

    /// <summary>
    ///     The node's min-content size on each axis, or NaN for "not computed since it last changed".
    /// </summary>
    /// <remarks>
    ///     Cached separately from the measurement cache because it is a different question. A
    ///     measurement asks "how big are you given this much room" and the answer depends on the
    ///     room; a min-content size asks "how small can you be" and the answer depends only on the
    ///     subtree and on what percentages resolve against. So it is keyed on the owner size rather
    ///     than on the available size, and it is invalidated by the dirty flag — which propagates
    ///     upward, so a change anywhere below a node clears the node's own entry too.
    /// </remarks>
    public DimensionValues MinContentSizes;

    /// <summary>The owner width <see cref="MinContentSizes" /> was computed against.</summary>
    public float MinContentOwnerWidth;

    /// <summary>The owner height <see cref="MinContentSizes" /> was computed against.</summary>
    public float MinContentOwnerHeight;
}

/// <summary>One remembered answer to "how big are you, given this much room".</summary>
[StructLayout(LayoutKind.Sequential)]
public struct CachedMeasurement {
    /// <summary>The width that was offered.</summary>
    public float AvailableWidth;

    /// <summary>The height that was offered.</summary>
    public float AvailableHeight;

    /// <summary>How the width was offered.</summary>
    public MeasureMode WidthMeasureMode;

    /// <summary>How the height was offered.</summary>
    public MeasureMode HeightMeasureMode;

    /// <summary>The width that came back.</summary>
    public float ComputedWidth;

    /// <summary>The height that came back.</summary>
    public float ComputedHeight;

    /// <summary>Whether this entry holds anything.</summary>
    public bool IsPopulated;
}

/// <summary>The per-node measurement cache.</summary>
/// <remarks>
///     Eight entries, which is Yoga's own figure from measuring real layouts — 98 % of them need
///     fewer than eight. Doc 09 says sixteen; the number came from an older version of the same
///     comment and doubling it would double the largest single term in a node's footprint for the
///     remaining 2 %.
/// </remarks>
[InlineArray(LayoutLimits.MaximumCachedMeasurements)]
[StructLayout(LayoutKind.Sequential)]
public struct CachedMeasurements {
    CachedMeasurement element;
}

/// <summary>The fixed sizes the layout store is built around.</summary>
public static class LayoutLimits {
    /// <summary>How many measurements one node remembers.</summary>
    public const int MaximumCachedMeasurements = 8;

    /// <summary>
    ///     How many times a subtree may be re-entered before layout is declared non-terminating.
    /// </summary>
    /// <remarks>
    ///     A measure function that returns a different answer for the same question makes the
    ///     algorithm oscillate. Yoga has the same guard for the same reason: a UI that hangs inside
    ///     layout gives no clue where, and this turns it into a message that names the node.
    /// </remarks>
    public const int MaximumLayoutDepth = 60;
}
