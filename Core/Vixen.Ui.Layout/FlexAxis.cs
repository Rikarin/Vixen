// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;

namespace Vixen.Ui.Layout;

/// <summary>
///     The translation between flow-relative reasoning and physical edges.
/// </summary>
/// <remarks>
///     The whole of flexbox is written in terms of "main start" and "cross end", and every one of
///     those has to become a left, top, right or bottom before anything is drawn. Keeping that
///     translation in one place is what lets the algorithm below be written once and work in four
///     flex directions times two writing directions, instead of eight times.
/// </remarks>
static class FlexAxis {
    /// <summary>Whether the axis runs horizontally.</summary>
    /// <param name="direction">The axis.</param>
    /// <returns>Whether it is a row.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsRow(FlexDirection direction) =>
        direction is FlexDirection.Row or FlexDirection.RowReverse;

    /// <summary>Whether the axis runs vertically.</summary>
    /// <param name="direction">The axis.</param>
    /// <returns>Whether it is a column.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsColumn(FlexDirection direction) =>
        direction is FlexDirection.Column or FlexDirection.ColumnReverse;

    /// <summary>Applies the writing direction to a flex direction.</summary>
    /// <param name="flexDirection">The style's axis.</param>
    /// <param name="direction">The writing direction.</param>
    /// <returns>The axis as it actually runs.</returns>
    public static FlexDirection Resolve(FlexDirection flexDirection, Direction direction) {
        if (direction == Direction.Rtl) {
            if (flexDirection == FlexDirection.Row) {
                return FlexDirection.RowReverse;
            }

            if (flexDirection == FlexDirection.RowReverse) {
                return FlexDirection.Row;
            }
        }

        return flexDirection;
    }

    /// <summary>The axis at right angles to the main one.</summary>
    /// <param name="flexDirection">The resolved main axis.</param>
    /// <param name="direction">The writing direction.</param>
    /// <returns>The cross axis.</returns>
    public static FlexDirection ResolveCross(FlexDirection flexDirection, Direction direction) =>
        IsColumn(flexDirection) ? Resolve(FlexDirection.Row, direction) : FlexDirection.Column;

    /// <summary>The physical edge an axis starts at.</summary>
    /// <param name="direction">The axis.</param>
    /// <returns>The edge.</returns>
    public static Edge FlexStartEdge(FlexDirection direction) => direction switch {
        FlexDirection.Column => Edge.Top,
        FlexDirection.ColumnReverse => Edge.Bottom,
        FlexDirection.Row => Edge.Left,
        _ => Edge.Right
    };

    /// <summary>The physical edge an axis ends at.</summary>
    /// <param name="direction">The axis.</param>
    /// <returns>The edge.</returns>
    public static Edge FlexEndEdge(FlexDirection direction) => direction switch {
        FlexDirection.Column => Edge.Bottom,
        FlexDirection.ColumnReverse => Edge.Top,
        FlexDirection.Row => Edge.Right,
        _ => Edge.Left
    };

    /// <summary>The physical edge the writing direction starts at.</summary>
    /// <param name="axis">The axis.</param>
    /// <param name="direction">The writing direction.</param>
    /// <returns>The edge.</returns>
    /// <remarks>
    ///     Not the same as <see cref="FlexStartEdge" />, and the difference is the one that catches
    ///     everybody: <c>margin-left</c> is on the left in a <c>row-reverse</c> container too, even
    ///     though the items start from the right.
    /// </remarks>
    public static Edge InlineStartEdge(FlexDirection axis, Direction direction) =>
        IsRow(axis) ? direction == Direction.Rtl ? Edge.Right : Edge.Left : Edge.Top;

    /// <summary>The physical edge the writing direction ends at.</summary>
    /// <param name="axis">The axis.</param>
    /// <param name="direction">The writing direction.</param>
    /// <returns>The edge.</returns>
    public static Edge InlineEndEdge(FlexDirection axis, Direction direction) =>
        IsRow(axis) ? direction == Direction.Rtl ? Edge.Left : Edge.Right : Edge.Bottom;

    /// <summary>Which dimension an axis measures.</summary>
    /// <param name="direction">The axis.</param>
    /// <returns>The dimension.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Dimension DimensionOf(FlexDirection direction) =>
        IsRow(direction) ? Dimension.Width : Dimension.Height;
}

/// <summary>What kind of question a sizing pass is asking.</summary>
/// <remarks>
///     The CSS names, deliberately, because they are the ones the specification argues in. Each maps
///     one to one onto a <see cref="MeasureMode" />, which is what a measure function is handed.
/// </remarks>
enum SizingMode : byte {
    /// <summary>Fill the available space. The available space must be definite.</summary>
    StretchFit,

    /// <summary>The size with infinite space available: as large as the content wants.</summary>
    MaxContent,

    /// <summary>The content size, clamped to the available space.</summary>
    FitContent
}
