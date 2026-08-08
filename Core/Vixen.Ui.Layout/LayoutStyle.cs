// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Vixen.Ui.Layout;

/// <summary>Nine lengths, one per <see cref="Edge" />.</summary>
[InlineArray(9)]
public struct EdgeLengths {
    StyleLength element;
}

/// <summary>Three lengths, one per <see cref="Gutter" />.</summary>
[InlineArray(3)]
public struct GutterLengths {
    StyleLength element;
}

/// <summary>Two lengths, one per <see cref="Dimension" />.</summary>
[InlineArray(2)]
public struct DimensionLengths {
    StyleLength element;
}

/// <summary>Everything a node's layout depends on, as one unmanaged value.</summary>
/// <remarks>
///     <para>
///         All nine edges are stored, including the <c>Horizontal</c>, <c>Vertical</c> and
///         <c>All</c> shorthands, because CSS resolves them by precedence at read time rather than
///         by expansion at write time: setting <c>All</c> and then <c>Left</c> is not the same
///         document as setting <c>Left</c> and then <c>All</c>, and a store that expanded on write
///         could not tell them apart.
///     </para>
///     <para>
///         This is around 400 bytes — doc 09's estimate of 120 was made before the edge shorthands
///         and the writing-mode-relative pair were counted. A hundred thousand nodes is therefore
///         about 40 MB in four allocations, against the reference port's several hundred thousand
///         heap objects for the same tree, which is the comparison ADR-006 was actually making.
///     </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct LayoutStyle {
    /// <summary>A style with every CSS initial value, which is not the same as all zeroes.</summary>
    public static readonly LayoutStyle Default = CreateDefault();

    /// <summary>Which way the inline axis runs.</summary>
    public Direction Direction;

    /// <summary>The main axis.</summary>
    public FlexDirection FlexDirection;

    /// <summary>Main-axis distribution.</summary>
    public Justify JustifyContent;

    /// <summary>Cross-axis distribution of the lines.</summary>
    public Align AlignContent;

    /// <summary>Cross-axis placement of the items.</summary>
    public Align AlignItems;

    /// <summary>This node's own cross-axis placement, overriding its parent's.</summary>
    public Align AlignSelf;

    /// <summary>How this node is positioned.</summary>
    public PositionType PositionType;

    /// <summary>Whether the line wraps.</summary>
    public Wrap FlexWrap;

    /// <summary>What happens to content that does not fit across the node.</summary>
    /// <remarks>
    ///     ⚠ <b>Two fields rather than one, because the two flexbox rules that read them are each
    ///     about a single axis.</b> The §4.5 automatic minimum size applies to the <i>main</i> axis and
    ///     an item opts out of it by not being <c>visible</c> <i>there</i>; a scroll container takes
    ///     the space it was offered rather than the space its content wants on the axis it scrolls.
    ///     Collapsing the pair — reading "either axis clips" — makes a column with
    ///     <c>overflow-x: auto</c> clamp its own <i>height</i> to whatever it was offered, which is the
    ///     opposite of what a sideways-scrolling panel asked for.
    /// </remarks>
    public Overflow OverflowX;

    /// <summary>What happens to content that does not fit down the node.</summary>
    /// <remarks>See <see cref="OverflowX" /> for why the axes are separate.</remarks>
    public Overflow OverflowY;

    /// <summary>Whether the node is laid out at all.</summary>
    public Display Display;

    /// <summary>What the dimensions measure.</summary>
    public BoxSizing BoxSizing;

    /// <summary>How this box sits against the line box it is on, if it is on one.</summary>
    /// <remarks>
    ///     ⚠ <b>Read only by an inline formatting context, and inert everywhere else — which is CSS's
    ///     own rule and not a limitation.</b> §10.8.1 applies <c>vertical-align</c> to inline-level and
    ///     table-cell boxes; on a flex item, a grid item or a block-level box in normal flow it
    ///     computes to a value nothing consults. That is why the property sat in the editor's inert
    ///     inventory with a task number against it rather than a bug: there was no line box in the
    ///     engine for it to align to.
    /// </remarks>
    public VerticalAlign VerticalAlign;

    /// <summary>Which ordinal group this item is laid out and painted in.</summary>
    /// <remarks>
    ///     ⚠ <b>Not in Yoga, so not in a single one of the 534 ported fixtures.</b> CSS Flexbox §5.4
    ///     lays items out in <i>order-modified document order</i> — sorted by this integer, ties
    ///     broken by document position — and paints them in that same order. It changes neither
    ///     selector matching nor sequential focus navigation, both of which stay on document order;
    ///     those are the three places this reaches and the two it must not.
    ///
    ///     An <see cref="int" /> rather than a byte because CSS allows negatives, and
    ///     <c>order: -1</c> in front of a row of defaulted items is the idiom the property exists
    ///     for. Zero is the initial value, which is also <c>default</c> — the one field here where
    ///     the all-zeroes struct is already right.
    /// </remarks>
    public int Order;

    /// <summary>The <c>flex</c> shorthand, if it was the thing that was set.</summary>
    public float Flex;

    /// <summary>How much of the leftover space this node takes.</summary>
    public float FlexGrow;

    /// <summary>How much of an overflow this node absorbs.</summary>
    public float FlexShrink;

    /// <summary>The main size before growing and shrinking.</summary>
    public StyleLength FlexBasis;

    /// <summary>The width-to-height ratio the node is forced into.</summary>
    public float AspectRatio;

    /// <summary>Outside the border box.</summary>
    public EdgeLengths Margin;

    /// <summary>The offset applied by <see cref="PositionType.Relative" /> and <see cref="PositionType.Absolute" />.</summary>
    public EdgeLengths Position;

    /// <summary>Inside the border, outside the content.</summary>
    public EdgeLengths Padding;

    /// <summary>Between padding and margin.</summary>
    public EdgeLengths Border;

    /// <summary>Between items and between lines.</summary>
    public GutterLengths Gap;

    /// <summary>The requested size.</summary>
    public DimensionLengths Dimensions;

    /// <summary>The floor.</summary>
    public DimensionLengths MinDimensions;

    /// <summary>The ceiling.</summary>
    public DimensionLengths MaxDimensions;

    /// <summary>Which axis grid auto-placement fills, and whether it backfills holes.</summary>
    public GridAutoFlow GridAutoFlow;

    /// <summary>The inline-axis placement of every child of a grid container.</summary>
    /// <remarks>
    ///     ⚠ <b>Grid reuses <see cref="Align" /> for the inline axis, and the member names lie
    ///     slightly.</b> <c>justify-items</c> is CSS Box Alignment's inline-axis property and its
    ///     keywords are <c>start</c>/<c>end</c>, not <c>flex-start</c>/<c>flex-end</c>; the two
    ///     differ only under <c>flex-wrap: wrap-reverse</c>, which a grid container does not have. So
    ///     <see cref="Align.FlexStart" /> read here means the inline start and nothing about flex.
    ///     A second enum whose members were the same five values would be worse.
    /// </remarks>
    public Align JustifyItems;

    /// <summary>This item's own inline-axis placement, overriding its container's.</summary>
    public Align JustifySelf;

    /// <summary>Which row this item starts in.</summary>
    public GridPlacement GridRowStart;

    /// <summary>Which row this item ends before.</summary>
    public GridPlacement GridRowEnd;

    /// <summary>Which column this item starts in.</summary>
    public GridPlacement GridColumnStart;

    /// <summary>Which column this item ends before.</summary>
    public GridPlacement GridColumnEnd;

    /// <summary>
    ///     <c>grid-template-columns</c>, as a handle into the tree's <see cref="TrackArena" />.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Internal, and owned by the node rather than by the value.</b> These four are the only
    ///     fields in this struct that are not self-contained — they name a block in an arena that
    ///     belongs to one <see cref="LayoutTree" /> — so copying a style between trees, or between
    ///     nodes, would alias a block that one of them will later free. <see cref="LayoutTree.SetStyle" />
    ///     therefore carries the destination node's own handles across a whole-style write, and the
    ///     track lists are set only through
    ///     <see cref="LayoutTree.SetGridTemplateColumns(LayoutNodeId,ReadOnlySpan{GridTrackSize})" />
    ///     and its three siblings.
    /// </remarks>
    internal GridTemplate GridTemplateColumns;

    /// <inheritdoc cref="GridTemplateColumns" />
    internal GridTemplate GridTemplateRows;

    /// <inheritdoc cref="GridTemplateColumns" />
    internal GridTemplate GridAutoColumns;

    /// <inheritdoc cref="GridTemplateColumns" />
    internal GridTemplate GridAutoRows;

    static LayoutStyle CreateDefault() {
        var style = default(LayoutStyle);
        style.Direction = Direction.Inherit;
        style.FlexDirection = FlexDirection.Column;
        style.JustifyContent = Justify.FlexStart;
        style.AlignContent = Align.FlexStart;
        style.AlignItems = Align.Stretch;
        style.AlignSelf = Align.Auto;
        style.PositionType = PositionType.Relative;
        style.FlexWrap = Wrap.NoWrap;
        style.OverflowX = Overflow.Visible;
        style.OverflowY = Overflow.Visible;
        style.Display = Display.Flex;
        style.BoxSizing = BoxSizing.BorderBox;
        style.Flex = float.NaN;
        style.FlexGrow = float.NaN;
        style.FlexShrink = float.NaN;
        style.FlexBasis = StyleLength.Auto;
        style.AspectRatio = float.NaN;
        style.GridAutoFlow = GridAutoFlow.Row;

        // ⚠ `normal` behaves as `stretch` for a grid item, per CSS Box Alignment §6.2 — a grid area
        // is a definite rectangle and an item with no size of its own fills it. Yoga's `AlignItems`
        // default is already Stretch for the same reason; these two follow it so that the inline and
        // block axes of a grid agree, which is the whole content of `place-items: normal`.
        style.JustifyItems = Align.Stretch;
        style.JustifySelf = Align.Auto;

        style.GridTemplateColumns = GridTemplate.Empty;
        style.GridTemplateRows = GridTemplate.Empty;
        style.GridAutoColumns = GridTemplate.Empty;
        style.GridAutoRows = GridTemplate.Empty;

        for (var i = 0; i < 2; i++) {
            style.Dimensions[i] = StyleLength.Auto;
        }

        return style;
    }
}
