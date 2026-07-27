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

    /// <summary>The size before pixel rounding, kept so that rounding never accumulates error.</summary>
    public DimensionValues RawDimensions;

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

    /// <summary>The owner direction of the pass that produced this result.</summary>
    public Direction LastOwnerDirection;

    /// <summary>Where the next measurement goes in the ring of cached ones.</summary>
    public uint NextCachedMeasurementsIndex;

    /// <summary>The measurement cache.</summary>
    public CachedMeasurements CachedMeasurements;

    /// <summary>The last full layout, cached separately from the measurements.</summary>
    public CachedMeasurement CachedLayout;
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
