// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Layout;

/// <summary>How a length was written.</summary>
/// <remarks>
///     Every length in a style is a value and one of these, never a bare float, because
///     <c>width: 0</c> and <c>width: auto</c> and "no width was set" are three different things that
///     a float alone cannot tell apart.
/// </remarks>
public enum LayoutUnit : byte {
    /// <summary>Nothing was set.</summary>
    Undefined,

    /// <summary>An absolute length.</summary>
    Point,

    /// <summary>A fraction of the containing block, resolved during layout.</summary>
    Percent,

    /// <summary>Decided by the algorithm: content size, or the space left over.</summary>
    Auto,

    /// <summary>The smallest the content can be without overflowing.</summary>
    /// <remarks>
    ///     ⚠ <b>This store has no separate min-content measure callback, so a min-content request is
    ///     an ordinary one with nothing to spare on the axis</b> — <see cref="MeasureMode.AtMost" />
    ///     against zero, which is what makes a text measurer answer with its longest word. The three
    ///     content keywords are therefore one mechanism with three requests rather than three
    ///     mechanisms; see <c>LayoutTree.Intrinsic.cs</c>.
    /// </remarks>
    MinContent,

    /// <summary>As wide as the content wants to be, ignoring available space.</summary>
    MaxContent,

    /// <summary>Content size, clamped to the available space.</summary>
    FitContent,

    /// <summary>Fill the available space.</summary>
    /// <remarks>
    ///     ⚠ <b>Carried and not implemented, deliberately.</b> Nothing in the styling layer emits it
    ///     — CSS Sizing 4's <c>stretch</c> has no Tailwind sizing class behind it — and the two Yoga
    ///     fixtures that set it, <c>Stretch_width</c> and <c>Stretch_flex_basis_column</c>, pin
    ///     answers that disagree with each other about what it should mean: the first wants the
    ///     containing block's width and the second wants its own content's height. Resolving it the
    ///     way <see cref="MaxContent" /> and its two siblings are resolved would close one and open
    ///     the other. See <c>LayoutTree.Intrinsic.cs</c>.
    /// </remarks>
    Stretch
}

/// <summary>The main axis, and which end of it items start from.</summary>
public enum FlexDirection : byte {
    /// <summary>Top to bottom.</summary>
    Column,

    /// <summary>Bottom to top.</summary>
    ColumnReverse,

    /// <summary>Along the inline direction — left to right in LTR.</summary>
    Row,

    /// <summary>Against the inline direction.</summary>
    RowReverse
}

/// <summary>How leftover main-axis space is distributed.</summary>
public enum Justify : byte {
    /// <summary>Packed at the start.</summary>
    FlexStart,

    /// <summary>Packed in the middle.</summary>
    Center,

    /// <summary>Packed at the end.</summary>
    FlexEnd,

    /// <summary>Space between items, none at the edges.</summary>
    SpaceBetween,

    /// <summary>Half a gap at the edges, a full gap between items.</summary>
    SpaceAround,

    /// <summary>Equal gaps everywhere, including the edges.</summary>
    SpaceEvenly
}

/// <summary>How items are placed on the cross axis.</summary>
public enum Align : byte {
    /// <summary>Defer to the parent's <c>align-items</c>. Only meaningful for <c>align-self</c>.</summary>
    Auto,

    /// <summary>At the cross-start edge.</summary>
    FlexStart,

    /// <summary>Centred.</summary>
    Center,

    /// <summary>At the cross-end edge.</summary>
    FlexEnd,

    /// <summary>Filling the line's cross size.</summary>
    Stretch,

    /// <summary>Aligned so the items' baselines line up.</summary>
    Baseline,

    /// <summary>Lines spread with space between them. <c>align-content</c> only.</summary>
    SpaceBetween,

    /// <summary>Lines spread with half-gaps at the edges. <c>align-content</c> only.</summary>
    SpaceAround,

    /// <summary>Lines spread with equal gaps everywhere. <c>align-content</c> only.</summary>
    SpaceEvenly
}

/// <summary>
///     What an alignment does when what it is aligning does not fit: CSS Box Alignment §4.4's
///     <c>&lt;overflow-position&gt;</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Nothing to do with <see cref="Overflow" />, despite the word.</b> That enum is
///         <c>overflow-x</c> and <c>overflow-y</c> — whether content is clipped or scrolled. This one
///         is the first half of an alignment value's grammar, <c>[ safe | unsafe ]? &lt;position&gt;</c>,
///         and it decides what happens to that <i>position</i> when the free space goes negative.
///     </para>
///     <para>
///         ⚠ <b>It is a modifier on a position, not a position, which is why it is a second field
///         beside <see cref="LayoutStyle.AlignSelf" /> and its five siblings rather than four more
///         members on <see cref="Align" />.</b> <c>safe end</c> is not a third place to sit — it is
///         <c>end</c> with a condition attached — and folding it into the position enum would put a
///         new arm in every <c>switch</c> that reads one, each of which would answer <c>start</c> by
///         falling through a <c>default</c> whether or not anything overflowed.
///     </para>
/// </remarks>
public enum OverflowAlignment : byte {
    /// <summary>
    ///     Align as asked however far the subject overflows. The initial behaviour, and what every
    ///     unprefixed keyword means.
    /// </summary>
    /// <remarks>
    ///     So <c>align-self: end</c> on a 150-point item in a 100-point line puts its <i>top</i> 50
    ///     points above the line, rather than giving up and going to the start.
    /// </remarks>
    Unsafe,

    /// <summary>
    ///     Align as asked, unless doing so would overflow, in which case align to start instead.
    /// </summary>
    /// <remarks>
    ///     The point is reachability: overflow towards the start edge scrolls data out of the corner
    ///     the reader begins at and no scrollbar goes back for it, so <c>safe</c> spends the overflow
    ///     at the end instead. The test is on the free space and nothing else — a <c>safe</c>
    ///     alignment with room to spare is indistinguishable from an <c>unsafe</c> one.
    /// </remarks>
    Safe
}

/// <summary>
///     CSS Text §7.1's three legacy <c>text-align</c> values, which move a block container's
///     <i>block-level</i> children.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is not <c>text-align</c>, and the difference is the reason it has its own name
///         and its own enum.</b> <c>text-align: center</c> centres the <i>inline</i> content of a
///         block container and leaves its block-level children exactly where §10.3.3 put them.
///         <c>-webkit-center</c> — the behaviour <c>&lt;center&gt;</c> had, kept alive because pages
///         depend on it — centres the child <i>boxes</i> as well, which is a block-layout rule that
///         needs no line box and no inline formatting context. A field called <c>TextAlign</c>
///         holding both sets of keywords would have to behave differently for each, so it holds only
///         the set this store implements.
///     </para>
///     <para>
///         ⚠ <b>Physical, and they do not flip with <see cref="Direction" />.</b>
///         <see cref="Left" /> is the left in an RTL container too — the keywords predate
///         writing-mode-relative alignment and were never respecified in terms of it.
///         <c>block_text_align_center_rtl</c> in the Taffy corpus is that pin exactly.
///     </para>
///     <para>
///         <c>text-align</c> proper — the inline-axis distribution of the items on a line box — is
///         <see cref="TextAlign" />, a separate field on the same struct. ⚠ This paragraph used to say
///         it was "a separate, unwritten thing"; the second half stopped being true and the first half
///         is what the two enums are. It still has no oracle in either corpus, for the reason
///         <c>InlineKnownGaps.txt</c> opens with, so its fixtures are closed-form rather than
///         recorded.
///     </para>
/// </remarks>
public enum LegacyTextAlign : byte {
    /// <summary>
    ///     None of the three was written, so block-level children sit at the inline start.
    /// </summary>
    /// <remarks>
    ///     Also where every non-legacy value of <c>text-align</c> lands, because none of them moves a
    ///     block-level child at all.
    /// </remarks>
    None,

    /// <summary><c>-webkit-left</c>: against the container's left content edge in both directions.</summary>
    Left,

    /// <summary><c>-webkit-center</c>: centred in the container's content box.</summary>
    Center,

    /// <summary><c>-webkit-right</c>: against the container's right content edge in both directions.</summary>
    Right
}

/// <summary>
///     CSS Text §7.1's <c>text-align</c> proper: where the items on a line box sit along the inline
///     axis.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The other half of <see cref="LegacyTextAlign" />, and the two are separate fields on
///         purpose.</b> One CSS property carries both sets of keywords and they govern different
///         boxes: the legacy three move a block container's <i>block-level</i> children and this one
///         distributes the <i>inline-level</i> items on a line. A container can have both kinds of
///         child, so one field could not hold both answers — which is the argument
///         <see cref="LegacyTextAlign" />'s remarks make and this enum is the other end of.
///     </para>
///     <para>
///         ⚠ <b>Read by the inline walk and by nothing else</b>, so it moves a line's items and never
///         a glyph. Text inside a leaf is aligned a layer out, in <c>Vixen.Ui</c>'s
///         <c>TextAlignShift</c>, because that needs the shaped line's width and this project has no
///         font. The two compose the way CSS says they do — a centred line box holding a
///         shrink-to-fit leaf whose own lines are centred inside it — and neither is the other's
///         approximation.
///     </para>
///     <para>
///         ⚠ <b><c>justify</c> is not here, and it is refused rather than approximated.</b> Justifying
///         distributes a line's slack between its <i>word</i> boundaries, and a text leaf is one
///         atomic item to this walk — so the only slack this store could distribute is the space
///         between whole inline-level boxes, which is not what the keyword asks for and would look
///         like it on a line that happened to hold several. <c>LayoutStyleBuilder</c> drops it, the
///         same way it drops the five font-relative <c>vertical-align</c> values, and the shape falls
///         back to <see cref="Start" /> — which is where CSS puts a justified block's last line
///         anyway.
///     </para>
/// </remarks>
public enum TextAlign : byte {
    /// <summary>The line's items begin at the inline start edge. The initial value.</summary>
    Start,

    /// <summary>The line's items end at the inline end edge.</summary>
    End,

    /// <summary>Against the left edge whatever <see cref="Direction" /> says.</summary>
    Left,

    /// <summary>Against the right edge whatever <see cref="Direction" /> says.</summary>
    Right,

    /// <summary>Centred in the space the line box has.</summary>
    Center
}

/// <summary>Which side a box floats to, per CSS 2.1 §9.5.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A float is out of flow but not out of the picture, which is what makes it unlike
///         <see cref="PositionType.Absolute" />.</b> An absolute box is removed and forgotten: nothing
///         else moves because of it. A float is removed from the flow and then <i>shortens</i> what is
///         left — the line boxes beside it, the border box of any sibling that establishes a
///         formatting context of its own, and the height of the block formatting context that
///         contains it. So the store cannot record it as a positioning scheme and stop there; it has
///         to keep a live exclusion list for the whole formatting context, which is what
///         <c>LayoutTree.Floats</c> is.
///     </para>
///     <para>
///         ⚠ <b>Floating a box also makes it a block formatting context root and a block-level box.</b>
///         §9.7's table turns any <c>display</c> other than <c>none</c> into a block-level equivalent
///         once <c>float</c> is set, and §9.4.1 makes it a context root — which is why
///         <c>EstablishesBlockFormattingContext</c> reads this field and why a float's margins never
///         collapse with anything.
///     </para>
/// </remarks>
public enum FloatSide : byte {
    /// <summary>Not floated: the box stays in normal flow.</summary>
    None,

    /// <summary><c>float: left</c> — against the left content edge, or the last left float's right edge.</summary>
    Left,

    /// <summary><c>float: right</c> — against the right content edge, or the last right float's left edge.</summary>
    Right
}

/// <summary>Which floats a box refuses to sit beside, per CSS 2.1 §9.5.2.</summary>
/// <remarks>
///     <para>
///         ⚠ <b><c>clear</c> does not move floats; it moves the box that declares it.</b> The box's top
///         border edge is pushed down until it is below the bottom margin edge of every earlier float
///         on the named side, by inserting <i>clearance</i> between the box's top margin and its top
///         border. Clearance is not a margin: it does not collapse, and the margin it displaces is
///         spent rather than carried forward.
///     </para>
///     <para>
///         ⚠ <b>Physical, and they do not flip with <see cref="Direction" />.</b> §9.5.2's keywords
///         name the same two sides <see cref="FloatSide" /> does, and neither pair is
///         writing-mode-relative in CSS 2.1.
///     </para>
/// </remarks>
public enum Clear : byte {
    /// <summary>Nothing is cleared: the box sits wherever margin collapsing put it.</summary>
    None,

    /// <summary><c>clear: left</c> — below every earlier left float.</summary>
    Left,

    /// <summary><c>clear: right</c> — below every earlier right float.</summary>
    Right,

    /// <summary><c>clear: both</c> — below every earlier float on either side.</summary>
    Both
}

/// <summary>How a node is positioned relative to its parent.</summary>
public enum PositionType : byte {
    /// <summary>In flow, and <c>inset</c> is ignored.</summary>
    Static,

    /// <summary>In flow, then offset by <c>inset</c> without disturbing anything else.</summary>
    Relative,

    /// <summary>Out of flow, positioned against the containing block.</summary>
    Absolute
}

/// <summary>Whether items overflow the main axis or move to a new line.</summary>
public enum Wrap : byte {
    /// <summary>One line, however much it overflows.</summary>
    NoWrap,

    /// <summary>New lines towards the cross-end.</summary>
    Wrap,

    /// <summary>New lines towards the cross-start.</summary>
    WrapReverse
}

/// <summary>What happens to content that does not fit, on one axis.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>There is no <c>Auto</c>, and that is a decision rather than a gap.</b> CSS's
///         <c>auto</c> and <c>scroll</c> establish the same scroll container and lay out identically;
///         the only difference between them is whether the scrollbar gutter is reserved when there is
///         nothing to scroll. Nothing in this framework draws a scrollbar of its own — <c>ScrollView</c>
///         is a control that builds its own — so a fourth member would encode a distinction no
///         consumer could act on while splitting every <c>== Scroll</c> test in the flex algorithm
///         into <c>is Scroll or Auto</c>. The stylesheet's own keyword survives in the computed style,
///         so an engine that later grows automatic gutters can still tell the two apart there.
///     </para>
///     <para>
///         ⚠ <b>There is no <c>Clip</c> either, on the same argument and a stronger version of it.</b>
///         CSS separates <c>clip</c> from <c>hidden</c> by what <c>hidden</c> grants and <c>clip</c>
///         does not — a scroll container, and programmatic scrolling — and <see cref="Hidden" /> here
///         grants neither. <c>ScrollView</c> reads no <c>overflow</c> of its own at all, and
///         <c>ScrollTop</c> is a control's property that no stylesheet reaches. So the two keywords
///         cannot be told apart by any consumer in this framework, and a fourth member would split
///         every <c>!= Visible</c> test in the flex, block, grid and inline algorithms into
///         <c>is Hidden or Clip</c> to no effect. <c>LayoutStyleBuilder</c> maps the keyword onto
///         <see cref="Hidden" /> and the computed style keeps the author's own word.
///     </para>
///     <para>
///         Per axis, because <c>overflow-x</c> and <c>overflow-y</c> are separate properties and the
///         flexbox rules that read this — the §4.5 automatic minimum size, and the fit-content size of
///         a scroll container — are each about one axis. See <see cref="LayoutStyle.OverflowX" />.
///     </para>
/// </remarks>
public enum Overflow : byte {
    /// <summary>It spills out.</summary>
    Visible,

    /// <summary>It is clipped. Both <c>hidden</c> and <c>clip</c> arrive here — see the remark.</summary>
    Hidden,

    /// <summary>It is clipped and scrollable, which changes the minimum content size.</summary>
    Scroll
}

/// <summary>Which formatting context a node establishes for its children.</summary>
/// <remarks>
///     ⚠ <b>This was <c>{ Flex, None }</c>, and the one keyword added here is a whole second
///     algorithm.</b> Doc 43 § B1. A <see cref="Block" /> container is not a flex column with
///     <c>align-items: stretch</c>: its children's vertical margins <i>collapse</i> into each other
///     and into the container's own, per CSS 2.1 §8.3.1, and CSS Flexbox §9.5 says in as many words
///     that flex item margins do not. Anything that reads this enum and treats an unrecognised member
///     as flex is therefore wrong in a way that only shows on a stacked layout with margins, which is
///     most of them.
/// </remarks>
public enum Display : byte {
    /// <summary>A flex container.</summary>
    Flex,

    /// <summary>Not laid out, and neither are its children.</summary>
    None,

    /// <summary>
    ///     A block container: children stack down the block axis and fill the inline axis, and
    ///     adjoining vertical margins collapse.
    /// </summary>
    Block,

    /// <summary>
    ///     A grid container: children are placed into a two-dimensional set of tracks, sized by CSS
    ///     Grid §12 and aligned within their areas by CSS Box Alignment.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The third algorithm, and the one that is not a variation on either of the others.</b>
    ///     A flex container's children are a sequence it breaks into lines; a block container's are a
    ///     stack. A grid's are placed into a rectangle of tracks that are themselves sized <i>by</i>
    ///     the items in them, which is why §12 runs its intrinsic pass over items grouped by how many
    ///     tracks they span rather than over the children in order. Nothing in the flex line machinery
    ///     applies.
    /// </remarks>
    Grid,

    /// <summary>
    ///     An inline-level box with flow content: it sits on a line beside its siblings rather than
    ///     taking one of its own.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This store treats an <c>inline</c> box as <i>atomic</i>, and that is the one place the
    ///     keyword is not the CSS one.</b> CSS Display §2.2 makes a non-replaced <c>inline</c> box
    ///     non-atomic: when it does not fit, it <i>fragments</i> — one box becomes several, one per
    ///     line it crosses, each with its own rectangle and with the horizontal border and padding
    ///     drawn only at the two real ends. A <see cref="LayoutResult" /> holds exactly one rectangle
    ///     per node, so a fragmented box has nowhere to put its second half. See
    ///     <c>LayoutTree.Inline.cs</c> for why that is the whole boundary of B3 and not an oversight,
    ///     and <c>InlineKnownGaps.txt</c> for what it costs in practice.
    ///     <para>
    ///         For the case that dominates a user interface — a <c>span</c> holding text and no
    ///         box children — atomic and non-atomic agree exactly, because there is nothing to split.
    ///     </para>
    /// </remarks>
    Inline,

    /// <summary>
    ///     An inline-level box whose inside is a block container: it sits on a line, and its children
    ///     stack.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The difference from <see cref="Block" /> is entirely on the outside, and it is the
    ///     reason this keyword was left unmapped until there was an inline formatting context to map
    ///     it into.</b> A <see cref="Block" /> box in normal flow takes the whole line — CSS 2.1
    ///     §10.3.3 solves <c>width: auto</c> to the containing block's width. An <c>inline-block</c>
    ///     resolves the same <c>width: auto</c> by §10.3.9's <i>shrink-to-fit</i>, and shares its line
    ///     with whatever comes before and after. Aliasing this onto <see cref="Block" /> would have
    ///     given it the whole line, which is the single behaviour an author writes it to prevent.
    /// </remarks>
    InlineBlock,

    /// <summary>
    ///     An inline-level box whose inside is a flex container.
    /// </summary>
    /// <remarks>
    ///     Outer display inline, inner display flex, per CSS Display §2.1. Everything inside is
    ///     <see cref="Flex" />'s algorithm unchanged; only the outside — line participation and
    ///     shrink-to-fit — differs.
    /// </remarks>
    InlineFlex,

    /// <summary>
    ///     A block container that always establishes a block formatting context of its own.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Not an alias for <see cref="Block" />, and the difference is the whole content of
    ///         the keyword.</b> Every other way of getting a new formatting context has a side effect
    ///         somebody has to live with — <c>overflow: hidden</c> clips, <c>float</c> takes the box
    ///         out of the stack, <c>inline-block</c> shrink-to-fits and shares a line. <c>flow-root</c>
    ///         is the one that asks for the formatting context and nothing else, so a
    ///         <c>flow-root</c> with <c>overflow: visible</c> still stops its children's vertical
    ///         margins escaping through its edges and still contains its floats.
    ///     </para>
    ///     <para>
    ///         Everything else about it is <see cref="Block" />: outer display block, inner display
    ///         flow, same algorithm, same <c>width: auto</c> filling the containing block. The only
    ///         reads that separate the two are <c>EstablishesBlockFormattingContext</c> and
    ///         <c>BlockMarginsCollapsibleWithParent</c>.
    ///     </para>
    /// </remarks>
    FlowRoot
}

/// <summary>
///     How an inline-level box sits against the line box it is on, per CSS 2.1 §10.8.1.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Three of these eight are implemented and five are refused, and the split is not
///         arbitrary — it is exactly the line between the values defined against the <i>line box</i>
///         and the values defined against a <i>font</i>.</b> <see cref="Baseline" />,
///         <see cref="Top" /> and <see cref="Bottom" /> are geometry this store already has: a
///         baseline it computes, and the two edges of a box it just laid out.
///         <see cref="Middle" />, <see cref="TextTop" />, <see cref="TextBottom" />,
///         <see cref="Sub" /> and <see cref="Super" /> are each defined against the parent's
///         <i>strut</i> — its font's ascent, descent or x-height — and
///         <c>Vixen.Ui.Layout</c> has no font, no font size and no way to ask for one. It is a
///         geometry store; the fonts live one layer out in <c>Vixen.Ui</c>.
///     </para>
///     <para>
///         So the five are carried as computed values and refused by the algorithm rather than
///         silently rounded to <see cref="Baseline" />. A silent fallback would put
///         <c>vertical-align: middle</c> a half-x-height out and look like a rounding error;
///         <c>InlineKnownGaps.txt</c> names them and says what each would need.
///     </para>
/// </remarks>
public enum VerticalAlign : byte {
    /// <summary>The box's baseline sits on the line's. The initial value.</summary>
    Baseline,

    /// <summary>The box's top edge sits against the top of the line box.</summary>
    Top,

    /// <summary>The box's bottom edge sits against the bottom of the line box.</summary>
    Bottom,

    /// <summary>Centred on the parent's baseline plus half its x-height. ⚠ Needs font metrics.</summary>
    Middle,

    /// <summary>Aligned with the top of the parent's strut. ⚠ Needs font metrics.</summary>
    TextTop,

    /// <summary>Aligned with the bottom of the parent's strut. ⚠ Needs font metrics.</summary>
    TextBottom,

    /// <summary>Lowered to the parent's subscript position. ⚠ Needs font metrics.</summary>
    Sub,

    /// <summary>Raised to the parent's superscript position. ⚠ Needs font metrics.</summary>
    Super
}

/// <summary>Which way the inline axis runs.</summary>
public enum Direction : byte {
    /// <summary>Take the parent's, or <see cref="Ltr" /> at the root.</summary>
    Inherit,

    /// <summary>Left to right.</summary>
    Ltr,

    /// <summary>Right to left.</summary>
    Rtl
}

/// <summary>What <c>width</c> and <c>height</c> measure.</summary>
public enum BoxSizing : byte {
    /// <summary>The border box: padding and border are inside the given size.</summary>
    BorderBox,

    /// <summary>The content box: padding and border are added to the given size.</summary>
    ContentBox
}

/// <summary>One side of a box, or a group of them.</summary>
/// <remarks>
///     <see cref="Start" /> and <see cref="End" /> are the writing-mode-relative pair and win over
///     the physical <see cref="Left" /> and <see cref="Right" /> when both are set, which is what
///     makes one stylesheet work in both directions.
/// </remarks>
public enum Edge : byte {
    /// <summary>The physical left.</summary>
    Left,

    /// <summary>The top.</summary>
    Top,

    /// <summary>The physical right.</summary>
    Right,

    /// <summary>The bottom.</summary>
    Bottom,

    /// <summary>The inline start — left in LTR, right in RTL.</summary>
    Start,

    /// <summary>The inline end — right in LTR, left in RTL.</summary>
    End,

    /// <summary>Left and right together.</summary>
    Horizontal,

    /// <summary>Top and bottom together.</summary>
    Vertical,

    /// <summary>All four.</summary>
    All
}

/// <summary>Which gap a value applies to.</summary>
public enum Gutter : byte {
    /// <summary>Between columns.</summary>
    Column,

    /// <summary>Between rows.</summary>
    Row,

    /// <summary>Both.</summary>
    All
}

/// <summary>One of the two axes of a box, in physical terms.</summary>
public enum Dimension : byte {
    /// <summary>The horizontal extent.</summary>
    Width,

    /// <summary>The vertical extent.</summary>
    Height
}

/// <summary>What a measure function is being asked for.</summary>
public enum MeasureMode : byte {
    /// <summary>Nothing is imposed; return the content's natural size.</summary>
    Undefined,

    /// <summary>The size is fixed; the answer is ignored on that axis.</summary>
    Exactly,

    /// <summary>An upper bound; return the content size clamped to it.</summary>
    AtMost
}
