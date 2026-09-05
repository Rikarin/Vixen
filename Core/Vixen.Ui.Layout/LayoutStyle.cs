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

    /// <summary>Whether <see cref="JustifyContent" /> gives up and packs at the start on overflow.</summary>
    /// <remarks>
    ///     ⚠ <b>An alignment value in CSS is two things, and this is the other one.</b> The grammar is
    ///     <c>[ safe | unsafe ]? &lt;position&gt;</c> — see <see cref="OverflowAlignment" /> for why
    ///     the pair is two fields rather than four more members on <see cref="Align" />. The six
    ///     <c>*Overflow</c> fields here are the six places CSS Box Alignment lets the prefix be
    ///     written, and every one of them is <see cref="OverflowAlignment.Unsafe" /> by default
    ///     because that is what a bare keyword means.
    /// </remarks>
    public OverflowAlignment JustifyContentOverflow;

    /// <inheritdoc cref="JustifyContentOverflow" />
    public OverflowAlignment AlignContentOverflow;

    /// <inheritdoc cref="JustifyContentOverflow" />
    public OverflowAlignment AlignItemsOverflow;

    /// <inheritdoc cref="JustifyContentOverflow" />
    public OverflowAlignment AlignSelfOverflow;

    /// <summary>
    ///     Where this block container puts its block-level children on the inline axis.
    /// </summary>
    /// <remarks>
    ///     Read only by the block algorithm, and only by it: a flex or grid container has
    ///     <see cref="JustifyContent" /> and <see cref="JustifyItems" /> to say the same thing
    ///     properly. See <see cref="LegacyTextAlign" /> for why this is not called <c>TextAlign</c>.
    /// </remarks>
    public LegacyTextAlign LegacyTextAlign;

    /// <summary>Where the items on this container's line boxes sit along the inline axis.</summary>
    /// <remarks>
    ///     Read only by the inline walk, and only by it — the other half of the CSS property
    ///     <see cref="LegacyTextAlign" /> holds. See <see cref="Layout.TextAlign" />.
    /// </remarks>
    public TextAlign TextAlign;

    /// <summary>Which side this box floats to, or <see cref="FloatSide.None" />.</summary>
    /// <remarks>
    ///     ⚠ A non-<c>None</c> value takes the box out of flow, makes it block-level whatever
    ///     <see cref="Display" /> says, and makes it a block formatting context root. See
    ///     <see cref="FloatSide" />.
    /// </remarks>
    public FloatSide Float;

    /// <summary>Which earlier floats this box refuses to sit beside.</summary>
    /// <remarks>See <see cref="Layout.Clear" />; the effect is clearance, not a margin.</remarks>
    public Clear Clear;

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

    /// <summary>How much room a scrollbar takes out of this node, in points.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The axes are crossed, and getting that backwards is the whole bug.</b> The
    ///         scrollbar an axis needs is drawn along the <i>other</i> one:
    ///         <see cref="OverflowY" /> is <see cref="Overflow.Scroll" /> means a vertical bar, and a
    ///         vertical bar eats <i>width</i>. So this reserves
    ///         <c>OverflowY == Scroll ? ScrollbarWidth : 0</c> across the node and
    ///         <c>OverflowX == Scroll ? ScrollbarWidth : 0</c> down it. One field for both because
    ///         CSS has one <c>scrollbar-width</c> and a scrollbar is as thick either way round.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It shrinks the content box but does not raise the node's minimum size, and those
    ///         are two different rules that <see cref="Overflow.Scroll" /> makes look like one.</b>
    ///         A box cannot be laid out narrower than its own padding and border; it CAN be laid out
    ///         narrower than its scrollbar, and then the bar simply covers everything.
    ///         <c>leaf_overflow_scrollbars_overridden_by_size</c> is that fixture exactly — a 2pt box
    ///         reserving a 15pt gutter comes out 2pt wide, not 15. For the same reason
    ///         <c>box-sizing: content-box</c> adds padding and border to a specified size and does
    ///         <i>not</i> add this; the gutter is taken out of the content box rather than pushed
    ///         outside it.
    ///     </para>
    ///     <para>
    ///         The gutter sits at the inline-END edge of each axis — the right in <c>ltr</c>, the
    ///         <i>left</i> in <c>rtl</c>, and the bottom either way. <c>grid_scrollbar_rtl</c> is what
    ///         pins the flip: an <c>rtl</c> scroll container puts its only child at <c>x = 15</c>.
    ///     </para>
    ///     <para>
    ///         Zero is the initial value and also <c>default</c>, so nothing reserves a gutter until
    ///         something asks. Only <see cref="Overflow.Scroll" /> reads it:
    ///         <see cref="Overflow.Hidden" /> clips without a bar, which is why declaring a width
    ///         beside <c>hidden</c> is inert rather than wrong.
    ///     </para>
    /// </remarks>
    public float ScrollbarWidth;

    /// <summary>What this node promises about its contents. CSS Containment 2's <c>contain</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>Flags rather than a mode, because the property is five independent effects and the
    ///     useful spellings are combinations of them.</b> <see cref="Containment.None" /> is the
    ///     initial value and also <c>default</c>, so nothing changes until something asks.
    /// </remarks>
    public Containment Containment;

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

    /// <summary>
    ///     How far <see cref="VerticalAlign.Offset" /> raises this box off the baseline. Negative
    ///     lowers it.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A distance and never a percentage, because the percentage's base is the one number
    ///     this store does not have.</b> CSS 2.1 §10.8.1 resolves a percentage <c>vertical-align</c>
    ///     against the <i>element's own</i> <c>line-height</c> — not the container's, not the line
    ///     box's — and an atomic inline-level box here is a rectangle with no line height of its own.
    ///     The layer that resolved the line height resolves this with it, which is the same division
    ///     of labour <see cref="Strut" /> is built on.
    /// </remarks>
    public float VerticalAlignOffset;

    /// <summary>
    ///     The font metrics every line box in this container starts from. CSS 2.1 §10.8's strut.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Read on the <i>container</i> and never on the item</b> — the strut belongs to the
    ///     block container that established the inline formatting context, so it is the one field
    ///     here that an inline-level box's own copy of has no meaning. All-zero is the initial value
    ///     and means "no font supplied", which lays out exactly as this store did before the field
    ///     existed. See <see cref="StrutMetrics" />.
    /// </remarks>
    public StrutMetrics Strut;

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

    /// <inheritdoc cref="JustifyContentOverflow" />
    public OverflowAlignment JustifyItemsOverflow;

    /// <inheritdoc cref="JustifyContentOverflow" />
    public OverflowAlignment JustifySelfOverflow;

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

        // An unprefixed alignment keyword is the `unsafe` one, so all six start there. Stated rather
        // than left to the zero, like every other initial value in this method.
        style.JustifyContentOverflow = OverflowAlignment.Unsafe;
        style.AlignContentOverflow = OverflowAlignment.Unsafe;
        style.AlignItemsOverflow = OverflowAlignment.Unsafe;
        style.AlignSelfOverflow = OverflowAlignment.Unsafe;
        style.JustifyItemsOverflow = OverflowAlignment.Unsafe;
        style.JustifySelfOverflow = OverflowAlignment.Unsafe;

        style.LegacyTextAlign = LegacyTextAlign.None;
        style.TextAlign = TextAlign.Start;
        style.Float = FloatSide.None;
        style.Clear = Clear.None;
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
