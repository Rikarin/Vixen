// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Layout;

/// <summary>
///     Turns a written style into the numbers the algorithm works in: which edge wins, what a
///     percentage resolves to, and what a value means once the writing direction is known.
/// </summary>
/// <remarks>
///     The edge precedence is the part worth reading. CSS lets four declarations claim the same
///     physical edge — <c>margin-left</c>, <c>margin-inline-start</c>, <c>margin-horizontal</c> and
///     <c>margin</c> — and the order they are resolved in is fixed by the specification rather than
///     by which was written last. That is why the store keeps all nine edges and asks here rather
///     than expanding shorthands on the way in.
/// </remarks>
static class StyleResolution {
    /// <summary>The length that applies to one physical edge, by CSS precedence.</summary>
    /// <param name="edges">The nine written lengths.</param>
    /// <param name="edge">Which physical edge is being asked about.</param>
    /// <param name="direction">The writing direction, which decides what start and end mean.</param>
    /// <returns>The winning length, which may be undefined.</returns>
    public static StyleLength EdgeValue(in EdgeLengths edges, Edge edge, Direction direction) => edge switch {
        Edge.Left => LeftEdge(in edges, direction),
        Edge.Top => VerticalEdge(in edges, Edge.Top),
        Edge.Right => RightEdge(in edges, direction),
        _ => VerticalEdge(in edges, Edge.Bottom)
    };

    static StyleLength LeftEdge(in EdgeLengths edges, Direction direction) {
        if (direction == Direction.Ltr && edges[(int) Edge.Start].IsDefined) {
            return edges[(int) Edge.Start];
        }

        if (direction == Direction.Rtl && edges[(int) Edge.End].IsDefined) {
            return edges[(int) Edge.End];
        }

        if (edges[(int) Edge.Left].IsDefined) {
            return edges[(int) Edge.Left];
        }

        return edges[(int) Edge.Horizontal].IsDefined ? edges[(int) Edge.Horizontal] : edges[(int) Edge.All];
    }

    static StyleLength RightEdge(in EdgeLengths edges, Direction direction) {
        if (direction == Direction.Ltr && edges[(int) Edge.End].IsDefined) {
            return edges[(int) Edge.End];
        }

        if (direction == Direction.Rtl && edges[(int) Edge.Start].IsDefined) {
            return edges[(int) Edge.Start];
        }

        if (edges[(int) Edge.Right].IsDefined) {
            return edges[(int) Edge.Right];
        }

        return edges[(int) Edge.Horizontal].IsDefined ? edges[(int) Edge.Horizontal] : edges[(int) Edge.All];
    }

    static StyleLength VerticalEdge(in EdgeLengths edges, Edge edge) {
        if (edges[(int) edge].IsDefined) {
            return edges[(int) edge];
        }

        return edges[(int) Edge.Vertical].IsDefined ? edges[(int) Edge.Vertical] : edges[(int) Edge.All];
    }

    /// <summary>The margin at the start of an axis, in flow terms.</summary>
    /// <param name="style">The style.</param>
    /// <param name="axis">The axis.</param>
    /// <param name="direction">The writing direction.</param>
    /// <param name="widthSize">What a percentage margin is a fraction of — always a width, per CSS.</param>
    /// <returns>The margin, or zero.</returns>
    public static float FlexStartMargin(in LayoutStyle style, FlexDirection axis, Direction direction, float widthSize) =>
        Resolved(EdgeValue(in style.Margin, FlexAxis.FlexStartEdge(axis), direction), widthSize, 0f);

    /// <summary>The margin at the end of an axis, in flow terms.</summary>
    /// <param name="style">The style.</param>
    /// <param name="axis">The axis.</param>
    /// <param name="direction">The writing direction.</param>
    /// <param name="widthSize">What a percentage margin is a fraction of.</param>
    /// <returns>The margin, or zero.</returns>
    public static float FlexEndMargin(in LayoutStyle style, FlexDirection axis, Direction direction, float widthSize) =>
        Resolved(EdgeValue(in style.Margin, FlexAxis.FlexEndEdge(axis), direction), widthSize, 0f);

    /// <summary>The margin at the writing-direction start of an axis.</summary>
    /// <param name="style">The style.</param>
    /// <param name="axis">The axis.</param>
    /// <param name="direction">The writing direction.</param>
    /// <param name="widthSize">What a percentage margin is a fraction of.</param>
    /// <returns>The margin, or zero.</returns>
    public static float InlineStartMargin(in LayoutStyle style, FlexDirection axis, Direction direction, float widthSize) =>
        Resolved(EdgeValue(in style.Margin, FlexAxis.InlineStartEdge(axis, direction), direction), widthSize, 0f);

    /// <summary>The margin at the writing-direction end of an axis.</summary>
    /// <param name="style">The style.</param>
    /// <param name="axis">The axis.</param>
    /// <param name="direction">The writing direction.</param>
    /// <param name="widthSize">What a percentage margin is a fraction of.</param>
    /// <returns>The margin, or zero.</returns>
    public static float InlineEndMargin(in LayoutStyle style, FlexDirection axis, Direction direction, float widthSize) =>
        Resolved(EdgeValue(in style.Margin, FlexAxis.InlineEndEdge(axis, direction), direction), widthSize, 0f);

    /// <summary>Both margins on an axis.</summary>
    /// <param name="style">The style.</param>
    /// <param name="axis">The axis.</param>
    /// <param name="widthSize">What a percentage margin is a fraction of.</param>
    /// <returns>The total.</returns>
    /// <remarks>
    ///     The direction is hard-coded to left-to-right, which is not a shortcut: the two margins on
    ///     an axis add up to the same number whichever way round they are.
    /// </remarks>
    public static float MarginForAxis(in LayoutStyle style, FlexDirection axis, float widthSize) =>
        InlineStartMargin(in style, axis, Direction.Ltr, widthSize)
        + InlineEndMargin(in style, axis, Direction.Ltr, widthSize);

    /// <summary>Whether the start margin on an axis is <c>auto</c>.</summary>
    /// <param name="style">The style.</param>
    /// <param name="axis">The axis.</param>
    /// <param name="direction">The writing direction.</param>
    /// <returns>Whether it is auto.</returns>
    public static bool FlexStartMarginIsAuto(in LayoutStyle style, FlexDirection axis, Direction direction) =>
        EdgeValue(in style.Margin, FlexAxis.FlexStartEdge(axis), direction).Unit == LayoutUnit.Auto;

    /// <summary>Whether the end margin on an axis is <c>auto</c>.</summary>
    /// <param name="style">The style.</param>
    /// <param name="axis">The axis.</param>
    /// <param name="direction">The writing direction.</param>
    /// <returns>Whether it is auto.</returns>
    public static bool FlexEndMarginIsAuto(in LayoutStyle style, FlexDirection axis, Direction direction) =>
        EdgeValue(in style.Margin, FlexAxis.FlexEndEdge(axis), direction).Unit == LayoutUnit.Auto;

    /// <summary>Whether the writing-start margin on an axis is <c>auto</c>.</summary>
    /// <param name="style">The style.</param>
    /// <param name="axis">The axis.</param>
    /// <param name="direction">The writing direction.</param>
    /// <returns>Whether it is auto.</returns>
    public static bool InlineStartMarginIsAuto(in LayoutStyle style, FlexDirection axis, Direction direction) =>
        EdgeValue(in style.Margin, FlexAxis.InlineStartEdge(axis, direction), direction).Unit == LayoutUnit.Auto;

    /// <summary>Whether the writing-end margin on an axis is <c>auto</c>.</summary>
    /// <param name="style">The style.</param>
    /// <param name="axis">The axis.</param>
    /// <param name="direction">The writing direction.</param>
    /// <returns>Whether it is auto.</returns>
    public static bool InlineEndMarginIsAuto(in LayoutStyle style, FlexDirection axis, Direction direction) =>
        EdgeValue(in style.Margin, FlexAxis.InlineEndEdge(axis, direction), direction).Unit == LayoutUnit.Auto;

    /// <summary>The border at the writing-direction start of an axis.</summary>
    /// <param name="style">The style.</param>
    /// <param name="axis">The axis.</param>
    /// <param name="direction">The writing direction.</param>
    /// <returns>The border width, or zero.</returns>
    public static float InlineStartBorder(in LayoutStyle style, FlexDirection axis, Direction direction) =>
        MathF.Max(EdgeValue(in style.Border, FlexAxis.InlineStartEdge(axis, direction), direction).Resolve(0f).OrZero(), 0f);

    /// <summary>The border at the writing-direction end of an axis.</summary>
    /// <param name="style">The style.</param>
    /// <param name="axis">The axis.</param>
    /// <param name="direction">The writing direction.</param>
    /// <returns>The border width, or zero.</returns>
    public static float InlineEndBorder(in LayoutStyle style, FlexDirection axis, Direction direction) =>
        MathF.Max(EdgeValue(in style.Border, FlexAxis.InlineEndEdge(axis, direction), direction).Resolve(0f).OrZero(), 0f);

    /// <summary>The border at the flow start of an axis.</summary>
    /// <param name="style">The style.</param>
    /// <param name="axis">The axis.</param>
    /// <param name="direction">The writing direction.</param>
    /// <returns>The border width, or zero.</returns>
    public static float FlexStartBorder(in LayoutStyle style, FlexDirection axis, Direction direction) =>
        MathF.Max(EdgeValue(in style.Border, FlexAxis.FlexStartEdge(axis), direction).Resolve(0f).OrZero(), 0f);

    /// <summary>The border at the flow end of an axis.</summary>
    /// <param name="style">The style.</param>
    /// <param name="axis">The axis.</param>
    /// <param name="direction">The writing direction.</param>
    /// <returns>The border width, or zero.</returns>
    public static float FlexEndBorder(in LayoutStyle style, FlexDirection axis, Direction direction) =>
        MathF.Max(EdgeValue(in style.Border, FlexAxis.FlexEndEdge(axis), direction).Resolve(0f).OrZero(), 0f);

    /// <summary>The padding at the writing-direction start of an axis.</summary>
    /// <param name="style">The style.</param>
    /// <param name="axis">The axis.</param>
    /// <param name="direction">The writing direction.</param>
    /// <param name="widthSize">What a percentage padding is a fraction of.</param>
    /// <returns>The padding, or zero.</returns>
    public static float InlineStartPadding(in LayoutStyle style, FlexDirection axis, Direction direction, float widthSize) =>
        Resolved(EdgeValue(in style.Padding, FlexAxis.InlineStartEdge(axis, direction), direction), widthSize, 0f, clampToZero: true);

    /// <summary>The padding at the writing-direction end of an axis.</summary>
    /// <param name="style">The style.</param>
    /// <param name="axis">The axis.</param>
    /// <param name="direction">The writing direction.</param>
    /// <param name="widthSize">What a percentage padding is a fraction of.</param>
    /// <returns>The padding, or zero.</returns>
    public static float InlineEndPadding(in LayoutStyle style, FlexDirection axis, Direction direction, float widthSize) =>
        Resolved(EdgeValue(in style.Padding, FlexAxis.InlineEndEdge(axis, direction), direction), widthSize, 0f, clampToZero: true);

    /// <summary>The padding at the flow start of an axis.</summary>
    /// <param name="style">The style.</param>
    /// <param name="axis">The axis.</param>
    /// <param name="direction">The writing direction.</param>
    /// <param name="widthSize">What a percentage padding is a fraction of.</param>
    /// <returns>The padding, or zero.</returns>
    public static float FlexStartPadding(in LayoutStyle style, FlexDirection axis, Direction direction, float widthSize) =>
        Resolved(EdgeValue(in style.Padding, FlexAxis.FlexStartEdge(axis), direction), widthSize, 0f, clampToZero: true);

    /// <summary>The padding at the flow end of an axis.</summary>
    /// <param name="style">The style.</param>
    /// <param name="axis">The axis.</param>
    /// <param name="direction">The writing direction.</param>
    /// <param name="widthSize">What a percentage padding is a fraction of.</param>
    /// <returns>The padding, or zero.</returns>
    public static float FlexEndPadding(in LayoutStyle style, FlexDirection axis, Direction direction, float widthSize) =>
        Resolved(EdgeValue(in style.Padding, FlexAxis.FlexEndEdge(axis), direction), widthSize, 0f, clampToZero: true);

    /// <summary>Padding plus border at the writing-direction start of an axis.</summary>
    /// <param name="style">The style.</param>
    /// <param name="axis">The axis.</param>
    /// <param name="direction">The writing direction.</param>
    /// <param name="widthSize">What a percentage padding is a fraction of.</param>
    /// <returns>The total.</returns>
    public static float InlineStartPaddingAndBorder(in LayoutStyle style, FlexDirection axis, Direction direction, float widthSize) =>
        InlineStartPadding(in style, axis, direction, widthSize) + InlineStartBorder(in style, axis, direction);

    /// <summary>Padding plus border at the writing-direction end of an axis.</summary>
    /// <param name="style">The style.</param>
    /// <param name="axis">The axis.</param>
    /// <param name="direction">The writing direction.</param>
    /// <param name="widthSize">What a percentage padding is a fraction of.</param>
    /// <returns>The total.</returns>
    public static float InlineEndPaddingAndBorder(in LayoutStyle style, FlexDirection axis, Direction direction, float widthSize) =>
        InlineEndPadding(in style, axis, direction, widthSize) + InlineEndBorder(in style, axis, direction);

    /// <summary>Padding plus border at the flow start of an axis.</summary>
    /// <param name="style">The style.</param>
    /// <param name="axis">The axis.</param>
    /// <param name="direction">The writing direction.</param>
    /// <param name="widthSize">What a percentage padding is a fraction of.</param>
    /// <returns>The total.</returns>
    public static float FlexStartPaddingAndBorder(in LayoutStyle style, FlexDirection axis, Direction direction, float widthSize) =>
        FlexStartPadding(in style, axis, direction, widthSize) + FlexStartBorder(in style, axis, direction);

    /// <summary>Padding plus border at the flow end of an axis.</summary>
    /// <param name="style">The style.</param>
    /// <param name="axis">The axis.</param>
    /// <param name="direction">The writing direction.</param>
    /// <param name="widthSize">What a percentage padding is a fraction of.</param>
    /// <returns>The total.</returns>
    public static float FlexEndPaddingAndBorder(in LayoutStyle style, FlexDirection axis, Direction direction, float widthSize) =>
        FlexEndPadding(in style, axis, direction, widthSize) + FlexEndBorder(in style, axis, direction);

    /// <summary>Padding plus border across a whole axis.</summary>
    /// <param name="style">The style.</param>
    /// <param name="axis">The axis.</param>
    /// <param name="direction">The writing direction.</param>
    /// <param name="widthSize">What a percentage padding is a fraction of.</param>
    /// <returns>The total.</returns>
    public static float PaddingAndBorderForAxis(in LayoutStyle style, FlexDirection axis, Direction direction, float widthSize) =>
        InlineStartPaddingAndBorder(in style, axis, direction, widthSize)
        + InlineEndPaddingAndBorder(in style, axis, direction, widthSize);

    /// <summary>Padding plus border across a dimension.</summary>
    /// <param name="style">The style.</param>
    /// <param name="dimension">The dimension.</param>
    /// <param name="direction">The writing direction.</param>
    /// <param name="widthSize">What a percentage padding is a fraction of.</param>
    /// <returns>The total.</returns>
    public static float PaddingAndBorderForDimension(in LayoutStyle style, Dimension dimension, Direction direction, float widthSize) {
        var axis = dimension == Dimension.Width ? FlexDirection.Row : FlexDirection.Column;
        return FlexStartPaddingAndBorder(in style, axis, direction, widthSize)
            + FlexEndPaddingAndBorder(in style, axis, direction, widthSize);
    }

    /// <summary>The scrollbar gutter across an axis, all of it at one edge.</summary>
    /// <param name="style">The style.</param>
    /// <param name="axis">The axis whose SIZE the gutter comes out of.</param>
    /// <returns>The gutter, or zero.</returns>
    /// <remarks>
    ///     ⚠ <b>The overflow it reads is the one at right angles to the axis it is measuring.</b> A
    ///     horizontal axis is a width, and what takes width away is the VERTICAL scrollbar, which is
    ///     there because <see cref="LayoutStyle.OverflowY" /> scrolls. Reading
    ///     <c>OverflowX</c> for a row because both words sound horizontal transposes every scroll
    ///     container in the tree, and <c>leaf_overflow_scrollbars_take_up_space_x_axis</c> — which
    ///     scrolls across and expects the extra 15 points DOWN — is the fixture that says so.
    /// </remarks>
    public static float ScrollbarGutterForAxis(in LayoutStyle style, FlexDirection axis) {
        var overflow = FlexAxis.IsRow(axis) ? style.OverflowY : style.OverflowX;
        return overflow == Overflow.Scroll && style.ScrollbarWidth > 0f ? style.ScrollbarWidth : 0f;
    }

    /// <summary>The scrollbar gutter at one physical edge of an axis.</summary>
    /// <param name="style">The style.</param>
    /// <param name="axis">The axis whose size the gutter comes out of.</param>
    /// <param name="edge">The edge being asked about.</param>
    /// <param name="direction">The writing direction.</param>
    /// <returns>The whole gutter if it lands on that edge, otherwise zero.</returns>
    /// <remarks>
    ///     The bar is at the inline END of its axis: the right in <c>ltr</c> and the left in
    ///     <c>rtl</c> for a vertical bar, the bottom either way for a horizontal one. It is never
    ///     split between the edges the way a centred gutter would be, so exactly one of the two
    ///     answers is non-zero.
    /// </remarks>
    public static float ScrollbarGutterAtEdge(in LayoutStyle style, FlexDirection axis, Edge edge, Direction direction) =>
        edge == FlexAxis.InlineEndEdge(axis, direction) ? ScrollbarGutterForAxis(in style, axis) : 0f;

    /// <summary>Padding, border and scrollbar gutter at the writing-direction start of an axis.</summary>
    /// <param name="style">The style.</param>
    /// <param name="axis">The axis.</param>
    /// <param name="direction">The writing direction.</param>
    /// <param name="widthSize">What a percentage padding is a fraction of.</param>
    /// <returns>The total.</returns>
    /// <remarks>
    ///     Identical to <see cref="InlineStartPaddingAndBorder" /> today and named separately anyway,
    ///     because the two mean different things and only one of them stays true if a future
    ///     <c>scrollbar-gutter: both-edges</c> ever puts a gutter here.
    /// </remarks>
    public static float InlineStartContentInset(in LayoutStyle style, FlexDirection axis, Direction direction, float widthSize) =>
        InlineStartPaddingAndBorder(in style, axis, direction, widthSize)
        + ScrollbarGutterAtEdge(in style, axis, FlexAxis.InlineStartEdge(axis, direction), direction);

    /// <summary>Padding, border and scrollbar gutter at the writing-direction end of an axis.</summary>
    /// <param name="style">The style.</param>
    /// <param name="axis">The axis.</param>
    /// <param name="direction">The writing direction.</param>
    /// <param name="widthSize">What a percentage padding is a fraction of.</param>
    /// <returns>The total.</returns>
    public static float InlineEndContentInset(in LayoutStyle style, FlexDirection axis, Direction direction, float widthSize) =>
        InlineEndPaddingAndBorder(in style, axis, direction, widthSize)
        + ScrollbarGutterAtEdge(in style, axis, FlexAxis.InlineEndEdge(axis, direction), direction);

    /// <summary>Padding, border and scrollbar gutter at the flow start of an axis.</summary>
    /// <param name="style">The style.</param>
    /// <param name="axis">The axis.</param>
    /// <param name="direction">The writing direction.</param>
    /// <param name="widthSize">What a percentage padding is a fraction of.</param>
    /// <returns>The total.</returns>
    /// <remarks>
    ///     ⚠ <b>Flow start and inline start are not the same edge, and the gutter follows the writing
    ///     direction rather than the flow.</b> A <c>row-reverse</c> container lays its items out from
    ///     the right, but its vertical scrollbar is still on the right in <c>ltr</c> — so on that
    ///     container the gutter lands here, at the flow START, and this is why the question is asked
    ///     by edge rather than answered by position.
    /// </remarks>
    public static float FlexStartContentInset(in LayoutStyle style, FlexDirection axis, Direction direction, float widthSize) =>
        FlexStartPaddingAndBorder(in style, axis, direction, widthSize)
        + ScrollbarGutterAtEdge(in style, axis, FlexAxis.FlexStartEdge(axis), direction);

    /// <summary>Padding, border and scrollbar gutter at the flow end of an axis.</summary>
    /// <param name="style">The style.</param>
    /// <param name="axis">The axis.</param>
    /// <param name="direction">The writing direction.</param>
    /// <param name="widthSize">What a percentage padding is a fraction of.</param>
    /// <returns>The total.</returns>
    /// <remarks>See <see cref="FlexStartContentInset" /> for why this is not simply the gutter.</remarks>
    public static float FlexEndContentInset(in LayoutStyle style, FlexDirection axis, Direction direction, float widthSize) =>
        FlexEndPaddingAndBorder(in style, axis, direction, widthSize)
        + ScrollbarGutterAtEdge(in style, axis, FlexAxis.FlexEndEdge(axis), direction);

    /// <summary>Everything between the border box and the content box, across a whole axis.</summary>
    /// <param name="style">The style.</param>
    /// <param name="axis">The axis.</param>
    /// <param name="direction">The writing direction.</param>
    /// <param name="widthSize">What a percentage padding is a fraction of.</param>
    /// <returns>The total.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This and <see cref="PaddingAndBorderForAxis" /> differ by the scrollbar gutter,
    ///         and choosing between them is not a matter of taste.</b> Subtract THIS to find the room
    ///         a node's children have, and add it back to turn a content size into a border-box one.
    ///         Use the other one for the two places CSS says a gutter does not reach: the floor a box
    ///         may never be clamped below (<c>BoundAxis</c>), and the amount
    ///         <c>box-sizing: content-box</c> adds to a specified size
    ///         (<see cref="WithBoxSizing" />).
    ///     </para>
    ///     <para>
    ///         Both fixtures are in the corpus and they disagree in opposite directions.
    ///         <c>leaf_overflow_scrollbars_take_up_space_both_axis</c> measures 20×10 of text and
    ///         expects 35×25, so the gutter is in the size. <c>..._overridden_by_size</c> asks for
    ///         2×4 and expects 2×4, so the gutter is not in the floor.
    ///     </para>
    /// </remarks>
    public static float ContentInsetForAxis(in LayoutStyle style, FlexDirection axis, Direction direction, float widthSize) =>
        PaddingAndBorderForAxis(in style, axis, direction, widthSize) + ScrollbarGutterForAxis(in style, axis);

    /// <summary>Everything between the border box and the content box, across a dimension.</summary>
    /// <param name="style">The style.</param>
    /// <param name="dimension">The dimension.</param>
    /// <param name="direction">The writing direction.</param>
    /// <param name="widthSize">What a percentage padding is a fraction of.</param>
    /// <returns>The total.</returns>
    /// <remarks>See <see cref="ContentInsetForAxis" /> for when this is the wrong one to reach for.</remarks>
    public static float ContentInsetForDimension(in LayoutStyle style, Dimension dimension, Direction direction, float widthSize) {
        var axis = dimension == Dimension.Width ? FlexDirection.Row : FlexDirection.Column;
        return PaddingAndBorderForDimension(in style, dimension, direction, widthSize)
            + ScrollbarGutterForAxis(in style, axis);
    }

    /// <summary>Both borders across an axis.</summary>
    /// <param name="style">The style.</param>
    /// <param name="axis">The axis.</param>
    /// <returns>The total.</returns>
    public static float BorderForAxis(in LayoutStyle style, FlexDirection axis) =>
        InlineStartBorder(in style, axis, Direction.Ltr) + InlineEndBorder(in style, axis, Direction.Ltr);

    /// <summary>The gap between items along an axis.</summary>
    /// <param name="style">The style.</param>
    /// <param name="axis">The axis.</param>
    /// <param name="ownerSize">What a percentage gap is a fraction of.</param>
    /// <returns>The gap, or zero.</returns>
    public static float GapForAxis(in LayoutStyle style, FlexDirection axis, float ownerSize) {
        var gap = FlexAxis.IsRow(axis) ? ColumnGap(in style) : RowGap(in style);
        return MathF.Max(gap.Resolve(ownerSize).OrZero(), 0f);
    }

    static StyleLength ColumnGap(in LayoutStyle style) =>
        style.Gap[(int) Gutter.Column].IsDefined ? style.Gap[(int) Gutter.Column] : style.Gap[(int) Gutter.All];

    static StyleLength RowGap(in LayoutStyle style) =>
        style.Gap[(int) Gutter.Row].IsDefined ? style.Gap[(int) Gutter.Row] : style.Gap[(int) Gutter.All];

    /// <summary>Whether an inset is set at the writing-direction start of an axis.</summary>
    /// <param name="style">The style.</param>
    /// <param name="axis">The axis.</param>
    /// <param name="direction">The writing direction.</param>
    /// <returns>Whether it is set.</returns>
    public static bool IsInlineStartPositionDefined(in LayoutStyle style, FlexDirection axis, Direction direction) =>
        EdgeValue(in style.Position, FlexAxis.InlineStartEdge(axis, direction), direction).IsDefined;

    /// <summary>Whether an inset is set at the writing-direction end of an axis.</summary>
    /// <param name="style">The style.</param>
    /// <param name="axis">The axis.</param>
    /// <param name="direction">The writing direction.</param>
    /// <returns>Whether it is set.</returns>
    public static bool IsInlineEndPositionDefined(in LayoutStyle style, FlexDirection axis, Direction direction) =>
        EdgeValue(in style.Position, FlexAxis.InlineEndEdge(axis, direction), direction).IsDefined;

    /// <summary>Whether the writing-start inset on an axis is <c>auto</c>.</summary>
    /// <param name="style">The style.</param>
    /// <param name="axis">The axis.</param>
    /// <param name="direction">The writing direction.</param>
    /// <returns>Whether it is auto.</returns>
    public static bool IsInlineStartPositionAuto(in LayoutStyle style, FlexDirection axis, Direction direction) =>
        EdgeValue(in style.Position, FlexAxis.InlineStartEdge(axis, direction), direction).Unit == LayoutUnit.Auto;

    /// <summary>Whether the writing-end inset on an axis is <c>auto</c>.</summary>
    /// <param name="style">The style.</param>
    /// <param name="axis">The axis.</param>
    /// <param name="direction">The writing direction.</param>
    /// <returns>Whether it is auto.</returns>
    public static bool IsInlineEndPositionAuto(in LayoutStyle style, FlexDirection axis, Direction direction) =>
        EdgeValue(in style.Position, FlexAxis.InlineEndEdge(axis, direction), direction).Unit == LayoutUnit.Auto;

    /// <summary>Whether an inset is set at the flow start of an axis.</summary>
    /// <param name="style">The style.</param>
    /// <param name="axis">The axis.</param>
    /// <param name="direction">The writing direction.</param>
    /// <returns>Whether it is set.</returns>
    public static bool IsFlexStartPositionDefined(in LayoutStyle style, FlexDirection axis, Direction direction) =>
        EdgeValue(in style.Position, FlexAxis.FlexStartEdge(axis), direction).IsDefined;

    /// <summary>Whether an inset is set at the flow end of an axis.</summary>
    /// <param name="style">The style.</param>
    /// <param name="axis">The axis.</param>
    /// <param name="direction">The writing direction.</param>
    /// <returns>Whether it is set.</returns>
    public static bool IsFlexEndPositionDefined(in LayoutStyle style, FlexDirection axis, Direction direction) =>
        EdgeValue(in style.Position, FlexAxis.FlexEndEdge(axis), direction).IsDefined;

    /// <summary>Whether the flow-start inset on an axis is <c>auto</c>.</summary>
    /// <param name="style">The style.</param>
    /// <param name="axis">The axis.</param>
    /// <param name="direction">The writing direction.</param>
    /// <returns>Whether it is auto.</returns>
    public static bool IsFlexStartPositionAuto(in LayoutStyle style, FlexDirection axis, Direction direction) =>
        EdgeValue(in style.Position, FlexAxis.FlexStartEdge(axis), direction).Unit == LayoutUnit.Auto;

    /// <summary>Whether the flow-end inset on an axis is <c>auto</c>.</summary>
    /// <param name="style">The style.</param>
    /// <param name="axis">The axis.</param>
    /// <param name="direction">The writing direction.</param>
    /// <returns>Whether it is auto.</returns>
    public static bool IsFlexEndPositionAuto(in LayoutStyle style, FlexDirection axis, Direction direction) =>
        EdgeValue(in style.Position, FlexAxis.FlexEndEdge(axis), direction).Unit == LayoutUnit.Auto;

    /// <summary>The inset at the writing-direction start of an axis.</summary>
    /// <param name="style">The style.</param>
    /// <param name="axis">The axis.</param>
    /// <param name="direction">The writing direction.</param>
    /// <param name="axisSize">What a percentage inset is a fraction of.</param>
    /// <returns>The inset, or zero.</returns>
    public static float InlineStartPosition(in LayoutStyle style, FlexDirection axis, Direction direction, float axisSize) =>
        Resolved(EdgeValue(in style.Position, FlexAxis.InlineStartEdge(axis, direction), direction), axisSize, 0f);

    /// <summary>The inset at the writing-direction end of an axis.</summary>
    /// <param name="style">The style.</param>
    /// <param name="axis">The axis.</param>
    /// <param name="direction">The writing direction.</param>
    /// <param name="axisSize">What a percentage inset is a fraction of.</param>
    /// <returns>The inset, or zero.</returns>
    public static float InlineEndPosition(in LayoutStyle style, FlexDirection axis, Direction direction, float axisSize) =>
        Resolved(EdgeValue(in style.Position, FlexAxis.InlineEndEdge(axis, direction), direction), axisSize, 0f);

    /// <summary>The inset at the flow start of an axis.</summary>
    /// <param name="style">The style.</param>
    /// <param name="axis">The axis.</param>
    /// <param name="direction">The writing direction.</param>
    /// <param name="axisSize">What a percentage inset is a fraction of.</param>
    /// <returns>The inset, or zero.</returns>
    public static float FlexStartPosition(in LayoutStyle style, FlexDirection axis, Direction direction, float axisSize) =>
        Resolved(EdgeValue(in style.Position, FlexAxis.FlexStartEdge(axis), direction), axisSize, 0f);

    /// <summary>The inset at the flow end of an axis.</summary>
    /// <param name="style">The style.</param>
    /// <param name="axis">The axis.</param>
    /// <param name="direction">The writing direction.</param>
    /// <param name="axisSize">What a percentage inset is a fraction of.</param>
    /// <returns>The inset, or zero.</returns>
    public static float FlexEndPosition(in LayoutStyle style, FlexDirection axis, Direction direction, float axisSize) =>
        Resolved(EdgeValue(in style.Position, FlexAxis.FlexEndEdge(axis), direction), axisSize, 0f);

    /// <summary>The size a node asks for on one axis, after min and max have had their say.</summary>
    /// <param name="style">The style.</param>
    /// <param name="dimension">Which axis.</param>
    /// <returns>The written length.</returns>
    /// <remarks>
    ///     A node whose minimum and maximum are the same has a definite size whatever <c>width</c>
    ///     says, and treating that as the written size early is what lets the rest of the algorithm
    ///     stop asking.
    /// </remarks>
    public static StyleLength ProcessedDimension(in LayoutStyle style, Dimension dimension) {
        var max = style.MaxDimensions[(int) dimension];
        var min = style.MinDimensions[(int) dimension];
        return max.IsDefined && max == min ? max : style.Dimensions[(int) dimension];
    }

    /// <summary>The resolved minimum on one axis, or NaN.</summary>
    /// <param name="style">The style.</param>
    /// <param name="dimension">Which axis.</param>
    /// <param name="axisSize">What a percentage is a fraction of.</param>
    /// <param name="widthSize">What a percentage padding is a fraction of.</param>
    /// <param name="direction">The writing direction.</param>
    /// <returns>The minimum, or NaN.</returns>
    public static float ResolvedMinDimension(
        in LayoutStyle style,
        Dimension dimension,
        float axisSize,
        float widthSize,
        Direction direction
    ) => WithBoxSizing(in style, style.MinDimensions[(int) dimension].Resolve(axisSize), dimension, widthSize, direction);

    /// <summary>The resolved maximum on one axis, or NaN.</summary>
    /// <param name="style">The style.</param>
    /// <param name="dimension">Which axis.</param>
    /// <param name="axisSize">What a percentage is a fraction of.</param>
    /// <param name="widthSize">What a percentage padding is a fraction of.</param>
    /// <param name="direction">The writing direction.</param>
    /// <returns>The maximum, or NaN.</returns>
    public static float ResolvedMaxDimension(
        in LayoutStyle style,
        Dimension dimension,
        float axisSize,
        float widthSize,
        Direction direction
    ) => WithBoxSizing(in style, style.MaxDimensions[(int) dimension].Resolve(axisSize), dimension, widthSize, direction);

    /// <summary>Adds padding and border to a content-box length.</summary>
    /// <param name="style">The style.</param>
    /// <param name="value">The resolved length, or NaN.</param>
    /// <param name="dimension">Which axis it is on.</param>
    /// <param name="widthSize">What a percentage padding is a fraction of.</param>
    /// <param name="direction">The writing direction.</param>
    /// <returns>The border-box length, or NaN.</returns>
    /// <remarks>
    ///     ⚠ <b>Padding and border, and deliberately not the scrollbar gutter.</b>
    ///     <c>box-sizing: content-box</c> grows the border box by the edges the author did not count;
    ///     a scrollbar is not one of them, because it is taken out of the content box rather than
    ///     added outside it. <c>leaf_overflow_scrollbars_overridden_by_size__content_box_ltr</c> asks
    ///     for a 2pt content box beside a 15pt gutter and Chrome answers 2, not 17.
    /// </remarks>
    public static float WithBoxSizing(
        in LayoutStyle style,
        float value,
        Dimension dimension,
        float widthSize,
        Direction direction
    ) {
        if (style.BoxSizing == BoxSizing.BorderBox || float.IsNaN(value)) {
            return value;
        }

        return value + PaddingAndBorderForDimension(in style, dimension, direction, widthSize);
    }

    /// <summary>The flex basis, after the <c>flex</c> shorthand has had its say.</summary>
    /// <param name="style">The style.</param>
    /// <returns>The basis to resolve.</returns>
    public static StyleLength ProcessedFlexBasis(in LayoutStyle style) {
        var basis = style.FlexBasis;
        if (basis.IsDefined && basis.Unit != LayoutUnit.Auto) {
            return basis;
        }

        // `flex: 1` is `flex: 1 1 0%`, and the zero is what makes three equal children equal
        // regardless of their content. Without it they would divide only the *leftover* space.
        return !float.IsNaN(style.Flex) && style.Flex > 0f ? StyleLength.Zero : StyleLength.Auto;
    }

    /// <summary>How much of the leftover space this node takes.</summary>
    /// <param name="style">The style.</param>
    /// <param name="isRoot">Whether the node has no parent.</param>
    /// <returns>The grow factor.</returns>
    public static float ResolveFlexGrow(in LayoutStyle style, bool isRoot) {
        if (isRoot) {
            return 0f;
        }

        if (!float.IsNaN(style.FlexGrow)) {
            return style.FlexGrow;
        }

        return !float.IsNaN(style.Flex) && style.Flex > 0f ? style.Flex : 0f;
    }

    /// <summary>How much of an overflow this node absorbs.</summary>
    /// <param name="style">The style.</param>
    /// <param name="isRoot">Whether the node has no parent.</param>
    /// <returns>The shrink factor.</returns>
    public static float ResolveFlexShrink(in LayoutStyle style, bool isRoot) {
        if (isRoot) {
            return 0f;
        }

        if (!float.IsNaN(style.FlexShrink)) {
            return style.FlexShrink;
        }

        return !float.IsNaN(style.Flex) && style.Flex < 0f ? -style.Flex : 0f;
    }

    /// <summary>Resolves the writing direction a node is laid out in.</summary>
    /// <param name="style">The style.</param>
    /// <param name="ownerDirection">The parent's direction.</param>
    /// <returns>The direction.</returns>
    public static Direction ResolveDirection(in LayoutStyle style, Direction ownerDirection) =>
        style.Direction == Direction.Inherit
            ? ownerDirection == Direction.Inherit ? Direction.Ltr : ownerDirection
            : style.Direction;

    static float Resolved(StyleLength length, float reference, float fallback, bool clampToZero = false) {
        var value = length.Resolve(reference);
        if (float.IsNaN(value)) {
            return fallback;
        }

        return clampToZero ? MathF.Max(value, 0f) : value;
    }

    /// <summary>The value, or zero if it is undefined.</summary>
    /// <param name="value">A possibly-NaN float.</param>
    /// <returns>The value or zero.</returns>
    public static float OrZero(this float value) => float.IsNaN(value) ? 0f : value;
}
