// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;

namespace Vixen.Ui.Layout;

/// <summary>
///     The style surface. Every setter marks the node dirty and stops at the first ancestor that is
///     already dirty, so writing a style is cheap and forgetting to invalidate is not possible.
/// </summary>
/// <remarks>
///     Written out one property at a time rather than through a generic mutator. It is more lines
///     and no cleverness, and it is what a source generator emitting property assignments wants to
///     call: a direct store into a struct field with one comparison in front of it.
/// </remarks>
public sealed partial class LayoutTree {
    /// <summary>A node's style, for reading.</summary>
    /// <param name="node">The node.</param>
    /// <returns>A reference to the stored style.</returns>
    public ref readonly LayoutStyle GetStyle(LayoutNodeId node) => ref styles[Validate(node)];

    /// <summary>Replaces a node's whole style.</summary>
    /// <param name="node">The node.</param>
    /// <param name="style">The new style.</param>
    /// <remarks>
    ///     What the styling layer calls once per changed element rather than replaying twenty
    ///     setters. The comparison is over the raw bytes, which can only ever report a difference
    ///     that is not there — never miss one — and which means a recomputed <c>ComputedStyle</c>
    ///     that happens to be identical costs no layout. That is the case doc 09's style-sharing
    ///     cache exists to produce.
    /// </remarks>
    public void SetStyle(LayoutNodeId node, in LayoutStyle style) {
        var index = Validate(node);
        if (StyleEquals(in styles[index], in style)) {
            return;
        }

        // ⚠ Read before the store, because this is the whole-style path the styling layer actually
        // uses — `SetOrder` is for a caller driving the tree by hand. An `order` that changed here
        // and was not noticed would leave the parent's sorted block describing the previous frame,
        // which is worse than not implementing the property: it would be right until something
        // else happened to invalidate the block.
        var reordered = styles[index].Order != style.Order;

        // ⚠ The four grid track lists are handles into this tree's arena and are owned by the node,
        // not by the value being assigned. A caller who read a style off one node and wrote it to
        // another would otherwise leave two nodes naming one block, and the second
        // `DestroyRecursive` would free it twice. So they survive a whole-style write and are
        // changed only through `SetGridTemplateColumns` and its siblings — which is what the
        // `internal` on those fields is there to make the only possibility.
        var columns = styles[index].GridTemplateColumns;
        var rows = styles[index].GridTemplateRows;
        var autoColumns = styles[index].GridAutoColumns;
        var autoRows = styles[index].GridAutoRows;

        styles[index] = style;

        styles[index].GridTemplateColumns = columns;
        styles[index].GridTemplateRows = rows;
        styles[index].GridAutoColumns = autoColumns;
        styles[index].GridAutoRows = autoRows;

        if (reordered) {
            // The new value is the one that decides. Going *back* to zero still rebuilds, because
            // the early-out inside only skips a parent that has no sorted block to be wrong.
            InvalidateChildOrder(links[index].Parent, style.Order);
        }

        MarkDirtyAndPropagate(index);
    }

    /// <summary>Sets the writing direction.</summary>
    /// <param name="node">The node.</param>
    /// <param name="direction">The direction.</param>
    public void SetDirection(LayoutNodeId node, Direction direction) {
        var index = Validate(node);
        if (styles[index].Direction == direction) {
            return;
        }

        styles[index].Direction = direction;
        MarkDirtyAndPropagate(index);
    }

    /// <summary>Sets the main axis.</summary>
    /// <param name="node">The node.</param>
    /// <param name="direction">The main axis.</param>
    public void SetFlexDirection(LayoutNodeId node, FlexDirection direction) {
        var index = Validate(node);
        if (styles[index].FlexDirection == direction) {
            return;
        }

        styles[index].FlexDirection = direction;
        MarkDirtyAndPropagate(index);
    }

    /// <summary>Sets main-axis distribution.</summary>
    /// <remarks>
    ///     ⚠ The overflow half of the value is set with it rather than through a setter of its own,
    ///     because CSS writes them as one declaration and because the pair is only meaningful
    ///     together — see <see cref="OverflowAlignment" />. The same is true of the five siblings of
    ///     this method.
    /// </remarks>
    /// <param name="node">The node.</param>
    /// <param name="justify">The distribution.</param>
    /// <param name="overflow">What it does when the items do not fit.</param>
    public void SetJustifyContent(LayoutNodeId node, Justify justify, OverflowAlignment overflow = OverflowAlignment.Unsafe) {
        var index = Validate(node);
        if (styles[index].JustifyContent == justify && styles[index].JustifyContentOverflow == overflow) {
            return;
        }

        styles[index].JustifyContent = justify;
        styles[index].JustifyContentOverflow = overflow;
        MarkDirtyAndPropagate(index);
    }

    /// <summary>Sets cross-axis distribution of the lines.</summary>
    /// <inheritdoc cref="SetJustifyContent" path="/remarks" />
    /// <param name="node">The node.</param>
    /// <param name="align">The distribution.</param>
    /// <param name="overflow">What it does when the lines do not fit.</param>
    public void SetAlignContent(LayoutNodeId node, Align align, OverflowAlignment overflow = OverflowAlignment.Unsafe) {
        var index = Validate(node);
        if (styles[index].AlignContent == align && styles[index].AlignContentOverflow == overflow) {
            return;
        }

        styles[index].AlignContent = align;
        styles[index].AlignContentOverflow = overflow;
        MarkDirtyAndPropagate(index);
    }

    /// <summary>Sets cross-axis placement of the children.</summary>
    /// <inheritdoc cref="SetJustifyContent" path="/remarks" />
    /// <param name="node">The node.</param>
    /// <param name="align">The placement.</param>
    /// <param name="overflow">What it does for a child that does not fit.</param>
    public void SetAlignItems(LayoutNodeId node, Align align, OverflowAlignment overflow = OverflowAlignment.Unsafe) {
        var index = Validate(node);
        if (styles[index].AlignItems == align && styles[index].AlignItemsOverflow == overflow) {
            return;
        }

        styles[index].AlignItems = align;
        styles[index].AlignItemsOverflow = overflow;
        MarkDirtyAndPropagate(index);
    }

    /// <summary>Sets this node's own cross-axis placement.</summary>
    /// <inheritdoc cref="SetJustifyContent" path="/remarks" />
    /// <param name="node">The node.</param>
    /// <param name="align">The placement.</param>
    /// <param name="overflow">What it does when this node does not fit.</param>
    public void SetAlignSelf(LayoutNodeId node, Align align, OverflowAlignment overflow = OverflowAlignment.Unsafe) {
        var index = Validate(node);
        if (styles[index].AlignSelf == align && styles[index].AlignSelfOverflow == overflow) {
            return;
        }

        styles[index].AlignSelf = align;
        styles[index].AlignSelfOverflow = overflow;
        MarkDirtyAndPropagate(index);
    }

    /// <summary>Sets where a block container puts its block-level children on the inline axis.</summary>
    /// <remarks>
    ///     ⚠ Only the three legacy <c>text-align</c> keywords, and only on a block container. See
    ///     <see cref="LegacyTextAlign" />.
    /// </remarks>
    /// <param name="node">The node.</param>
    /// <param name="textAlign">The alignment.</param>
    public void SetLegacyTextAlign(LayoutNodeId node, LegacyTextAlign textAlign) {
        var index = Validate(node);
        if (styles[index].LegacyTextAlign == textAlign) {
            return;
        }

        styles[index].LegacyTextAlign = textAlign;
        MarkDirtyAndPropagate(index);
    }

    /// <summary>Sets how the node is positioned.</summary>
    /// <param name="node">The node.</param>
    /// <param name="positionType">The positioning scheme.</param>
    public void SetPositionType(LayoutNodeId node, PositionType positionType) {
        var index = Validate(node);
        if (styles[index].PositionType == positionType) {
            return;
        }

        styles[index].PositionType = positionType;
        MarkDirtyAndPropagate(index);
    }

    /// <summary>Sets whether the line wraps.</summary>
    /// <param name="node">The node.</param>
    /// <param name="wrap">The wrapping mode.</param>
    public void SetFlexWrap(LayoutNodeId node, Wrap wrap) {
        var index = Validate(node);
        if (styles[index].FlexWrap == wrap) {
            return;
        }

        styles[index].FlexWrap = wrap;
        MarkDirtyAndPropagate(index);
    }

    /// <summary>Sets what happens to content that does not fit, on both axes.</summary>
    /// <param name="node">The node.</param>
    /// <param name="overflow">The overflow mode.</param>
    /// <remarks>The <c>overflow</c> shorthand: the same mode across and down.</remarks>
    public void SetOverflow(LayoutNodeId node, Overflow overflow) => SetOverflow(node, overflow, overflow);

    /// <summary>Sets what happens to content that does not fit, per axis.</summary>
    /// <param name="node">The node.</param>
    /// <param name="horizontal">What happens across it — <c>overflow-x</c>.</param>
    /// <param name="vertical">What happens down it — <c>overflow-y</c>.</param>
    /// <remarks>
    ///     ⚠ <b>No coercion between the axes.</b> CSS computes a <c>visible</c> to <c>auto</c> when the
    ///     other axis is not visible, because a scrollport is one rectangle and painting outside it on
    ///     one axis only is undefined there. Nothing here has a scrollport: the clip is a rectangle
    ///     with one pair of edges past any viewport, which the renderer expresses exactly. So the axis
    ///     the stylesheet named is the axis that changes, which is also what the author wrote.
    /// </remarks>
    public void SetOverflow(LayoutNodeId node, Overflow horizontal, Overflow vertical) {
        var index = Validate(node);
        if (styles[index].OverflowX == horizontal && styles[index].OverflowY == vertical) {
            return;
        }

        styles[index].OverflowX = horizontal;
        styles[index].OverflowY = vertical;
        MarkDirtyAndPropagate(index);
    }

    /// <summary>Sets how much room this node's scrollbars take out of it.</summary>
    /// <param name="node">The node.</param>
    /// <param name="width">The scrollbar thickness in points. Zero reserves nothing.</param>
    /// <remarks>
    ///     Read only on an axis whose <see cref="Overflow" /> is <see cref="Overflow.Scroll" />, and
    ///     crosswise: this takes width out of a node that scrolls vertically and height out of one
    ///     that scrolls horizontally. See <see cref="LayoutStyle.ScrollbarWidth" /> for the two rules
    ///     it does <i>not</i> reach — the node's own minimum size, and <c>box-sizing</c>.
    /// </remarks>
    public void SetScrollbarWidth(LayoutNodeId node, float width) {
        var index = Validate(node);
        if (styles[index].ScrollbarWidth.Equals(width)) {
            return;
        }

        styles[index].ScrollbarWidth = width;
        MarkDirtyAndPropagate(index);
    }

    /// <summary>Sets whether the node is laid out at all.</summary>
    /// <param name="node">The node.</param>
    /// <param name="display">The display mode.</param>
    public void SetDisplay(LayoutNodeId node, Display display) {
        var index = Validate(node);
        if (styles[index].Display == display) {
            return;
        }

        styles[index].Display = display;
        MarkDirtyAndPropagate(index);
    }

    /// <summary>Sets how this box sits against the line box it is on.</summary>
    /// <param name="node">The node.</param>
    /// <param name="verticalAlign">The alignment.</param>
    /// <remarks>
    ///     ⚠ Read only where the node is an inline-level box in an inline formatting context, which
    ///     is CSS 2.1 §10.8.1's own scope rather than a limitation — the property means nothing on a
    ///     flex item or a block-level box in normal flow. Three of the eight values are honoured; see
    ///     <see cref="VerticalAlign" /> for which five are not and exactly why.
    /// </remarks>
    public void SetVerticalAlign(LayoutNodeId node, VerticalAlign verticalAlign) {
        var index = Validate(node);
        if (styles[index].VerticalAlign == verticalAlign) {
            return;
        }

        styles[index].VerticalAlign = verticalAlign;
        MarkDirtyAndPropagate(index);
    }

    /// <summary>Sets what the dimensions measure.</summary>
    /// <param name="node">The node.</param>
    /// <param name="boxSizing">The box model.</param>
    public void SetBoxSizing(LayoutNodeId node, BoxSizing boxSizing) {
        var index = Validate(node);
        if (styles[index].BoxSizing == boxSizing) {
            return;
        }

        styles[index].BoxSizing = boxSizing;
        MarkDirtyAndPropagate(index);
    }

    /// <summary>Sets the <c>flex</c> shorthand.</summary>
    /// <param name="node">The node.</param>
    /// <param name="flex">The factor.</param>
    public void SetFlex(LayoutNodeId node, float flex) {
        var index = Validate(node);
        if (SameFloat(styles[index].Flex, flex)) {
            return;
        }

        styles[index].Flex = flex;
        MarkDirtyAndPropagate(index);
    }

    /// <summary>Sets how much leftover space the node takes.</summary>
    /// <param name="node">The node.</param>
    /// <param name="flexGrow">The factor.</param>
    public void SetFlexGrow(LayoutNodeId node, float flexGrow) {
        var index = Validate(node);
        if (SameFloat(styles[index].FlexGrow, flexGrow)) {
            return;
        }

        styles[index].FlexGrow = flexGrow;
        MarkDirtyAndPropagate(index);
    }

    /// <summary>Sets how much of an overflow the node absorbs.</summary>
    /// <param name="node">The node.</param>
    /// <param name="flexShrink">The factor.</param>
    public void SetFlexShrink(LayoutNodeId node, float flexShrink) {
        var index = Validate(node);
        if (SameFloat(styles[index].FlexShrink, flexShrink)) {
            return;
        }

        styles[index].FlexShrink = flexShrink;
        MarkDirtyAndPropagate(index);
    }

    /// <summary>Sets the main size before growing and shrinking.</summary>
    /// <param name="node">The node.</param>
    /// <param name="basis">The basis.</param>
    public void SetFlexBasis(LayoutNodeId node, StyleLength basis) {
        var index = Validate(node);
        if (styles[index].FlexBasis == basis) {
            return;
        }

        styles[index].FlexBasis = basis;
        MarkDirtyAndPropagate(index);
    }

    /// <summary>Sets the aspect ratio the node is forced into.</summary>
    /// <param name="node">The node.</param>
    /// <param name="aspectRatio">Width divided by height. Zero and infinity mean no ratio at all.</param>
    /// <remarks>
    ///     A degenerate ratio behaves as <c>auto</c> rather than as a ratio — css-sizing-4 says so,
    ///     and the alternative is dividing by it.
    /// </remarks>
    public void SetAspectRatio(LayoutNodeId node, float aspectRatio) {
        if (aspectRatio == 0f || float.IsInfinity(aspectRatio)) {
            aspectRatio = float.NaN;
        }

        var index = Validate(node);
        if (SameFloat(styles[index].AspectRatio, aspectRatio)) {
            return;
        }

        styles[index].AspectRatio = aspectRatio;
        MarkDirtyAndPropagate(index);
    }

    /// <summary>Sets a margin.</summary>
    /// <param name="node">The node.</param>
    /// <param name="edge">Which edge.</param>
    /// <param name="margin">The length.</param>
    public void SetMargin(LayoutNodeId node, Edge edge, StyleLength margin) {
        var index = Validate(node);
        if (styles[index].Margin[(int) edge] == margin) {
            return;
        }

        styles[index].Margin[(int) edge] = margin;
        MarkDirtyAndPropagate(index);
    }

    /// <summary>Sets an inset.</summary>
    /// <param name="node">The node.</param>
    /// <param name="edge">Which edge.</param>
    /// <param name="position">The length.</param>
    public void SetPosition(LayoutNodeId node, Edge edge, StyleLength position) {
        var index = Validate(node);
        if (styles[index].Position[(int) edge] == position) {
            return;
        }

        styles[index].Position[(int) edge] = position;
        MarkDirtyAndPropagate(index);
    }

    /// <summary>Sets a padding.</summary>
    /// <param name="node">The node.</param>
    /// <param name="edge">Which edge.</param>
    /// <param name="padding">The length.</param>
    public void SetPadding(LayoutNodeId node, Edge edge, StyleLength padding) {
        var index = Validate(node);
        if (styles[index].Padding[(int) edge] == padding) {
            return;
        }

        styles[index].Padding[(int) edge] = padding;
        MarkDirtyAndPropagate(index);
    }

    /// <summary>Sets a border width.</summary>
    /// <param name="node">The node.</param>
    /// <param name="edge">Which edge.</param>
    /// <param name="border">The length. Only points are meaningful.</param>
    public void SetBorder(LayoutNodeId node, Edge edge, StyleLength border) {
        var index = Validate(node);
        if (styles[index].Border[(int) edge] == border) {
            return;
        }

        styles[index].Border[(int) edge] = border;
        MarkDirtyAndPropagate(index);
    }

    /// <summary>Sets a gap.</summary>
    /// <param name="node">The node.</param>
    /// <param name="gutter">Which gap.</param>
    /// <param name="gap">The length.</param>
    public void SetGap(LayoutNodeId node, Gutter gutter, StyleLength gap) {
        var index = Validate(node);
        if (styles[index].Gap[(int) gutter] == gap) {
            return;
        }

        styles[index].Gap[(int) gutter] = gap;
        MarkDirtyAndPropagate(index);
    }

    /// <summary>Sets the requested size on one axis.</summary>
    /// <param name="node">The node.</param>
    /// <param name="dimension">Which axis.</param>
    /// <param name="length">The length.</param>
    public void SetDimension(LayoutNodeId node, Dimension dimension, StyleLength length) {
        var index = Validate(node);
        if (styles[index].Dimensions[(int) dimension] == length) {
            return;
        }

        styles[index].Dimensions[(int) dimension] = length;
        MarkDirtyAndPropagate(index);
    }

    /// <summary>Sets the minimum size on one axis.</summary>
    /// <param name="node">The node.</param>
    /// <param name="dimension">Which axis.</param>
    /// <param name="length">The length.</param>
    public void SetMinDimension(LayoutNodeId node, Dimension dimension, StyleLength length) {
        var index = Validate(node);
        if (styles[index].MinDimensions[(int) dimension] == length) {
            return;
        }

        styles[index].MinDimensions[(int) dimension] = length;
        MarkDirtyAndPropagate(index);
    }

    /// <summary>Sets the maximum size on one axis.</summary>
    /// <param name="node">The node.</param>
    /// <param name="dimension">Which axis.</param>
    /// <param name="length">The length.</param>
    public void SetMaxDimension(LayoutNodeId node, Dimension dimension, StyleLength length) {
        var index = Validate(node);
        if (styles[index].MaxDimensions[(int) dimension] == length) {
            return;
        }

        styles[index].MaxDimensions[(int) dimension] = length;
        MarkDirtyAndPropagate(index);
    }

    /// <summary>The resolved offset of one physical edge.</summary>
    /// <param name="node">The node.</param>
    /// <param name="edge">Which edge. Only the four physical ones are meaningful.</param>
    /// <returns>The offset from the parent's content box.</returns>
    public float GetPosition(LayoutNodeId node, Edge edge) => results[Validate(node)].RoundedPosition[(int) edge];

    /// <summary>The laid-out left edge.</summary>
    /// <param name="node">The node.</param>
    /// <returns>The offset.</returns>
    public float GetLeft(LayoutNodeId node) => results[Validate(node)].RoundedPosition[(int) Edge.Left];

    /// <summary>The laid-out top edge.</summary>
    /// <param name="node">The node.</param>
    /// <returns>The offset.</returns>
    public float GetTop(LayoutNodeId node) => results[Validate(node)].RoundedPosition[(int) Edge.Top];

    /// <summary>The laid-out right edge.</summary>
    /// <param name="node">The node.</param>
    /// <returns>The offset.</returns>
    public float GetRight(LayoutNodeId node) => results[Validate(node)].RoundedPosition[(int) Edge.Right];

    /// <summary>The laid-out bottom edge.</summary>
    /// <param name="node">The node.</param>
    /// <returns>The offset.</returns>
    public float GetBottom(LayoutNodeId node) => results[Validate(node)].RoundedPosition[(int) Edge.Bottom];

    /// <summary>The laid-out width.</summary>
    /// <param name="node">The node.</param>
    /// <returns>The border-box width.</returns>
    public float GetWidth(LayoutNodeId node) => results[Validate(node)].RoundedDimensions[(int) Dimension.Width];

    /// <summary>The laid-out height.</summary>
    /// <param name="node">The node.</param>
    /// <returns>The border-box height.</returns>
    public float GetHeight(LayoutNodeId node) => results[Validate(node)].RoundedDimensions[(int) Dimension.Height];

    /// <summary>The resolved margin on one physical edge.</summary>
    /// <param name="node">The node.</param>
    /// <param name="edge">Which edge.</param>
    /// <returns>The margin.</returns>
    public float GetComputedMargin(LayoutNodeId node, Edge edge) => results[Validate(node)].Margin[(int) edge];

    /// <summary>The resolved border on one physical edge.</summary>
    /// <param name="node">The node.</param>
    /// <param name="edge">Which edge.</param>
    /// <returns>The border width.</returns>
    public float GetComputedBorder(LayoutNodeId node, Edge edge) => results[Validate(node)].Border[(int) edge];

    /// <summary>The resolved padding on one physical edge.</summary>
    /// <param name="node">The node.</param>
    /// <param name="edge">Which edge.</param>
    /// <returns>The padding.</returns>
    public float GetComputedPadding(LayoutNodeId node, Edge edge) => results[Validate(node)].Padding[(int) edge];

    /// <summary>The scrollbar gutter reserved on one physical edge.</summary>
    /// <param name="node">The node.</param>
    /// <param name="edge">Which edge.</param>
    /// <returns>The gutter, which is zero on three of the four edges.</returns>
    /// <remarks>
    ///     What a renderer needs to draw the bar in the space layout kept for it, and the reason
    ///     <see cref="GetComputedPadding" /> alone is no longer enough to reconstruct the content box.
    ///     The whole gutter sits at one edge — the right in <c>ltr</c> and the left in <c>rtl</c> for
    ///     the vertical bar, the bottom for the horizontal one — so the other three answer zero.
    /// </remarks>
    public float GetComputedScrollbarGutter(LayoutNodeId node, Edge edge) {
        var index = Validate(node);
        var direction = results[index].Direction;
        var axis = edge is Edge.Left or Edge.Right ? FlexDirection.Row : FlexDirection.Column;
        return StyleResolution.ScrollbarGutterAtEdge(in styles[index], axis, edge, direction);
    }

    /// <summary>The direction the node was laid out in.</summary>
    /// <param name="node">The node.</param>
    /// <returns>The direction.</returns>
    public Direction GetComputedDirection(LayoutNodeId node) => results[Validate(node)].Direction;

    /// <summary>Whether the node's content did not fit.</summary>
    /// <param name="node">The node.</param>
    /// <returns>Whether it overflowed.</returns>
    public bool GetHadOverflow(LayoutNodeId node) => results[Validate(node)].HadOverflow;

    static bool SameFloat(float left, float right) => float.IsNaN(left) ? float.IsNaN(right) : left.Equals(right);

    static bool StyleEquals(in LayoutStyle left, in LayoutStyle right) =>
        MemoryMarshal.AsBytes(new ReadOnlySpan<LayoutStyle>(in left))
            .SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<LayoutStyle>(in right)));
}
