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

    /// <summary>
    ///     The same measurement, taken before the node's own <c>min-*</c> and <c>max-*</c> were
    ///     applied to it.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>CSS Flexbox §9.2 makes the flex base size and the hypothetical main size two
    ///     different numbers, and only this one is the base.</b> Step 3E sizes the item under a
    ///     max-content constraint and takes the result as the flex base size; step 4 then clamps
    ///     that by the used min and max to get the hypothetical main size. Reading the base back out
    ///     of <see cref="MeasuredDimensions" /> collapses the two, because that value has already
    ///     been through <c>BoundAxis</c> — so an empty <c>min-width: 60px</c> item reports a base of
    ///     60 where §9.2 says 0, its base and its hypothetical size agree, and §9.7 step 2 therefore
    ///     never freezes it. Taffy's <c>min_width</c> is the arithmetic: two <c>flex-grow: 1</c>
    ///     items in a 100pt row answered 80 and 20 rather than Chrome's 60 and 40.
    ///     <para>
    ///         ⚠ It carries the same padding-and-border floor <c>BoundAxis</c> applies and nothing
    ///         else — a box cannot be smaller than its own edges, and that floor is not part of
    ///         §9.2's clamp. Every write of <see cref="MeasuredDimensions" /> writes this beside it;
    ///         a site that wrote one and not the other would hand the next flex basis a measurement
    ///         from some earlier pass, which is worse than the clamped value it replaced.
    ///     </para>
    /// </remarks>
    public DimensionValues UnclampedMeasuredDimensions;

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
    ///     Whether <see cref="ComputedFlexBasis" /> was MEASURED from the node's contents rather than
    ///     read off a definite <c>flex-basis</c> or main-axis size.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>It is what caps CSS Flexbox §4.5's automatic minimum</b>, and the justification is an
    ///     inequality rather than a heuristic: a box's min-content size is by definition no larger
    ///     than the content-derived size it was measured at, so a §4.5 floor that comes out ABOVE
    ///     such a basis is the probe being wrong, not the item needing room. A definite basis carries
    ///     no such guarantee — <c>flex-basis: 50px</c> on a box wrapping a 100px child is exactly the
    ///     case §4.5 exists for — so the cap applies only here.
    /// </remarks>
    public bool FlexBasisFromContent;

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

    /// <summary>
    ///     The vertical margins that escaped past this node's top edge, per CSS 2.1 §8.3.1.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This, its sibling below and <see cref="MarginsCollapseThrough" /> are the entire cost
    ///     of the store carrying a second layout algorithm</b>, and they are worth naming as such
    ///     because grid will want to know. Flexbox needs nothing out of a child's layout but its size:
    ///     the parent asks "how big", places it, and is done. Block layout cannot work that way,
    ///     because a child's top margin may not belong to the child at all — with no border and no
    ///     padding between them it belongs to the child's parent, and to its grandparent after that.
    ///     So a block layout returns three things beside a size, and a node that was laid out by any
    ///     other algorithm still has to answer for them: its own margin, and "no".
    /// </remarks>
    internal CollapsibleMargin TopCollapsibleMargin;

    /// <summary>The vertical margins still hanging off this node's bottom edge.</summary>
    internal CollapsibleMargin BottomCollapsibleMargin;

    /// <summary>Whether this node is transparent to margin collapsing — §8.3.1's "collapse through".</summary>
    internal bool MarginsCollapseThrough;

    /// <summary>
    ///     Where the last line box of an inline formatting context put its baseline, or NaN when this
    ///     node did not establish one.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The third algorithm cost the input side and the fourth costs the output side again —
    ///     but for a reason block's margins were not.</b> A collapsible margin is an output because it
    ///     belongs to somebody else. This is an output because it is <i>not recomputable</i>: CSS 2.1
    ///     §10.8.1 puts an <c>inline-block</c>'s baseline on its <b>last</b> line box, and which line
    ///     is last, and where its baseline fell, is known only inside the line-breaking walk. The
    ///     existing <c>CalculateBaseline</c> reconstructs a flex container's baseline by descending
    ///     into the first child, which works because a flex container's baseline <i>is</i> a child's.
    ///     A line box is not a node — it has no id, no style and no entry in the child arena — so
    ///     there is nothing to descend into and no way to ask the question after the fact.
    ///     <para>
    ///         NaN rather than zero for "no inline formatting context here", because zero is a
    ///         perfectly ordinary baseline — an empty first line has one.
    ///     </para>
    /// </remarks>
    internal float InlineBaseline;

    /// <summary>Where this node's extra boxes start in <see cref="FragmentArena" />, or -1.</summary>
    /// <remarks>
    ///     ⚠ <b>Zero here is the whole compatibility story.</b> <see cref="FragmentCount" /> of zero
    ///     means the node produced exactly one box and that box is <see cref="Position" /> and
    ///     <see cref="Dimensions" />, which is what every node produced before fragments existed and
    ///     what all but a handful produce now. So nothing that reads <see cref="LayoutTree.GetLeft" />
    ///     had to learn anything, and a tree with no non-atomic inline box in it never allocates a
    ///     fragment.
    ///     <para>
    ///         ⚠ When the count is non-zero, <see cref="Position" /> and <see cref="Dimensions" /> hold
    ///         the <b>union</b> of the fragments rather than one of them. That is not a fallback: CSS
    ///         2.1 §10.1 makes the containing block of an absolutely positioned descendant of an inline
    ///         box the bounding box of its first and last fragments, so the union is the answer the
    ///         absolute walk actually wants, and it is also the right rectangle for a scroll extent and
    ///         for a coarse hit test. A consumer that needs the individual boxes — a painter drawing a
    ///         background, a hit test that must not claim the gap between two lines — asks for them.
    ///     </para>
    /// </remarks>
    internal int FragmentOffset;

    /// <summary>How many fragments this node was split into, or 0 for the ordinary one-box case.</summary>
    internal int FragmentCount;

    /// <summary>How big the node's block in <see cref="FragmentArena" /> is.</summary>
    internal int FragmentCapacity;

    /// <summary>Where an out-of-flow child of a block container would have sat in flow.</summary>
    /// <remarks>
    ///     CSS 2.1 §10.6.4's static position. The flex path derives an absolute child's fallback
    ///     placement from <c>justify-content</c> and <c>align-items</c>, which a block container does
    ///     not have; its answer is "after everything before it", and that is only knowable during the
    ///     in-flow walk, which has finished by the time the absolute pass runs.
    /// </remarks>
    internal float BlockStaticLeft;

    /// <inheritdoc cref="BlockStaticLeft" />
    internal float BlockStaticTop;

    /// <summary>The grid area this out-of-flow child of a grid container is positioned against.</summary>
    /// <remarks>
    ///     ⚠ <b>CSS Grid §9.2's containing block, and it is an output for the same reason
    ///     <see cref="BlockStaticLeft" /> is: nothing can reconstruct it afterwards.</b> The rectangle
    ///     is cut out of the track offsets and sizes, which live in the grid's scratch arena and are
    ///     handed back the moment §11 finishes. Four floats — physical, measured from the grid
    ///     container's border-box origin — is what survives that.
    ///     <para>
    ///         NaN width for "no grid recorded one", which the absolute walk reads as the container's
    ///         padding box. That is not a fallback so much as the all-<c>auto</c> answer: §9.2 makes
    ///         every <c>auto</c> line the padding edge, so a grid child that names no line has the
    ///         padding box for an area.
    ///     </para>
    /// </remarks>
    internal float GridAreaLeft;

    /// <inheritdoc cref="GridAreaLeft" />
    internal float GridAreaTop;

    /// <inheritdoc cref="GridAreaLeft" />
    internal float GridAreaWidth;

    /// <inheritdoc cref="GridAreaLeft" />
    internal float GridAreaHeight;
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

    /// <summary>The width that came back, before this node's own min and max clamped it.</summary>
    /// <remarks>
    ///     Replayed for the reason the four below it are. See
    ///     <see cref="LayoutResult.UnclampedMeasuredDimensions" />: a cache hit that restored only
    ///     the clamped pair would leave the next flex basis reading whichever unclamped measurement
    ///     the node's last full run happened to leave behind, which is a wrong answer that appears
    ///     only incrementally.
    /// </remarks>
    public float UnclampedComputedWidth;

    /// <inheritdoc cref="UnclampedComputedWidth" />
    public float UnclampedComputedHeight;

    /// <summary>Whether this entry holds anything.</summary>
    public bool IsPopulated;

    /// <summary>The margin set that came back with this answer. See <see cref="LayoutResult.TopCollapsibleMargin" />.</summary>
    /// <remarks>
    ///     ⚠ <b>A cached answer has to replay every output, not just the two sizes.</b> Block layout's
    ///     margin outputs live on the node, so an entry served from this ring would otherwise hand
    ///     back whichever set the *last* full run happened to leave there — a different question's
    ///     answer, silently, and only when the ring has more than one live entry for the node. That
    ///     is a bug that would surface as a layout that is right cold and wrong incrementally, which
    ///     is the exact failure the rounding pass was already restructured to avoid.
    /// </remarks>
    internal CollapsibleMargin TopCollapsibleMargin;

    /// <inheritdoc cref="TopCollapsibleMargin" />
    internal CollapsibleMargin BottomCollapsibleMargin;

    /// <inheritdoc cref="TopCollapsibleMargin" />
    internal bool MarginsCollapseThrough;

    /// <summary>The inline baseline that came back with this answer.</summary>
    /// <remarks>
    ///     ⚠ Replayed for exactly the reason the three above are, and the failure mode is worse
    ///     because it is quieter: a cache hit that restored the two sizes and not this would align a
    ///     nested <c>inline-block</c> against whichever baseline the node's <i>last full</i> layout
    ///     left behind. Its own box would be the right size and its neighbours on the line would sit
    ///     a few points off, incrementally only. See <see cref="LayoutResult.InlineBaseline" />.
    /// </remarks>
    internal float InlineBaseline;
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

    /// <summary>How many tracks one axis of one grid may have.</summary>
    /// <remarks>
    ///     ⚠ <b>A clamp rather than a limit, and CSS says so.</b> CSS Grid §7.2.3 lets an
    ///     implementation cap the number of tracks a <c>repeat()</c> generates, and Chrome's cap is
    ///     what the corpus recorded: <c>repeat(10000, 0px)</c>, <c>repeat(32768, …)</c> and
    ///     <c>repeat(40000, 10px 10px)</c> are all in there specifically to pin the clamped answer,
    ///     alongside line numbers as large as ±32 768. An implementation with no cap does not fail
    ///     those fixtures, it allocates until it dies — which is the actual reason the spec permits
    ///     one.
    /// </remarks>
    public const int MaximumGridTracks = 10_000;
}
