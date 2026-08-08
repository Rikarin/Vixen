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

    /// <summary>As wide as the content wants to be, ignoring available space.</summary>
    MaxContent,

    /// <summary>Content size, clamped to the available space.</summary>
    FitContent,

    /// <summary>Fill the available space.</summary>
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
///         Per axis, because <c>overflow-x</c> and <c>overflow-y</c> are separate properties and the
///         flexbox rules that read this — the §4.5 automatic minimum size, and the fit-content size of
///         a scroll container — are each about one axis. See <see cref="LayoutStyle.OverflowX" />.
///     </para>
/// </remarks>
public enum Overflow : byte {
    /// <summary>It spills out.</summary>
    Visible,

    /// <summary>It is clipped.</summary>
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
    Block
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
