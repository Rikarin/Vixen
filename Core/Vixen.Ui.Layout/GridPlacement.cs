// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Runtime.InteropServices;

namespace Vixen.Ui.Layout;

/// <summary>How one edge of a grid item names its line, per CSS Grid §8.3.</summary>
public enum GridPlacementKind : byte {
    /// <summary>Nothing was said; auto-placement decides.</summary>
    Auto,

    /// <summary>A numbered line. Negative counts back from the end of the explicit grid.</summary>
    Line,

    /// <summary>A number of tracks away from whatever the opposite edge resolved to.</summary>
    Span
}

/// <summary>One of an item's four placement properties.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Line 0 does not exist.</b> CSS Grid §8.3 numbers lines from 1 at the start edge of the
///         explicit grid and from −1 at its end edge, with no zero between them, so a declared
///         <c>0</c> is invalid and computes to <c>auto</c>. The corpus never writes one — its 6 636
///         placement values are all <c>-?&lt;int&gt;</c> or <c>span &lt;int&gt;</c> and none is
///         <c>0</c> — so this rule is one of the ones no fixture can see, and
///         <c>GridPlacementTests</c> holds it instead.
///     </para>
///     <para>
///         ⚠ <b>A negative line is relative to the <i>explicit</i> grid, not the final one.</b> §8.3
///         resolves <c>-1</c> against the end of the explicit grid before any implicit track exists,
///         which means adding an implicit column on the right does not move what <c>-1</c> pointed
///         at. Resolving it against the final grid instead is self-referential — the grid's size
///         depends on the placement that depends on the grid's size — and it is the bug that makes a
///         <c>-1</c> item drift as its siblings are added.
///     </para>
/// </remarks>
/// <param name="Kind">Which of the three forms this is.</param>
/// <param name="Value">The line number, or the span count. Meaningless for <see cref="GridPlacementKind.Auto" />.</param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct GridPlacement(GridPlacementKind Kind, int Value) {
    /// <summary>Auto-placement decides. The initial value of all four properties.</summary>
    public static readonly GridPlacement Auto;

    /// <summary>A numbered line.</summary>
    /// <remarks>Zero is not a line, so it is taken as <see cref="Auto" />.</remarks>
    /// <param name="line">The line number, counted from 1 or from −1.</param>
    /// <returns>The placement.</returns>
    public static GridPlacement Line(int line) =>
        line == 0 ? Auto : new GridPlacement(GridPlacementKind.Line, line);

    /// <summary>A number of tracks from the opposite edge.</summary>
    /// <remarks>
    ///     ⚠ A span of zero or less is invalid and computes to <c>span 1</c>, which is also what a
    ///     bare <c>span</c> keyword means.
    /// </remarks>
    /// <param name="tracks">How many tracks.</param>
    /// <returns>The placement.</returns>
    public static GridPlacement Span(int tracks) =>
        new(GridPlacementKind.Span, int.Max(1, tracks));

    /// <summary>Whether auto-placement decides this edge.</summary>
    public bool IsAuto => Kind == GridPlacementKind.Auto;

    /// <inheritdoc />
    public override string ToString() => Kind switch {
        GridPlacementKind.Line => Value.ToString(CultureInfo.InvariantCulture),
        GridPlacementKind.Span => "span " + Value.ToString(CultureInfo.InvariantCulture),
        _ => "auto"
    };
}

/// <summary>Which axis auto-placement fills, and how hard it tries, per CSS Grid §8.5.</summary>
/// <remarks>
///     ⚠ <b><c>dense</c> is not a tie-breaker, it is a different algorithm.</b> The sparse cursor
///     never moves backwards, so a wide item that does not fit leaves a hole nothing later can use;
///     the dense cursor restarts from the first line for every item, which fills those holes and — as
///     §8.5 says in as many words — may reorder items visually relative to document order. That makes
///     it a distinct member rather than a flag on the other two, because a reader who treats it as
///     cosmetic will not expect the reordering.
/// </remarks>
public enum GridAutoFlow : byte {
    /// <summary>Fill each row in turn, leaving holes rather than going back.</summary>
    Row,

    /// <summary>Fill each column in turn, leaving holes rather than going back.</summary>
    Column,

    /// <summary>Fill each row in turn, going back to fill earlier holes.</summary>
    RowDense,

    /// <summary>Fill each column in turn, going back to fill earlier holes.</summary>
    ColumnDense
}

/// <summary>Whether a <c>repeat()</c> repeats a stated number of times, or as many as will fit.</summary>
/// <remarks>
///     ⚠ <b>The difference between the two automatic ones is <i>collapsing</i>, not counting.</b> CSS
///     Grid §7.2.3.2: <c>auto-fill</c> and <c>auto-fit</c> generate exactly the same number of
///     repetitions, and then <c>auto-fit</c> collapses every generated track that ended up with no
///     item in it — a collapsed track is treated as having a single zero line, so the gutters on
///     either side of it collapse into one too. A grid whose items fill every track cannot tell them
///     apart, which is why an <c>auto-fit</c> bug hides until the last row is short.
/// </remarks>
public enum GridAutoRepeat : byte {
    /// <summary>No automatic repetition in this track list.</summary>
    None,

    /// <summary>As many repetitions as fit, all of them kept.</summary>
    AutoFill,

    /// <summary>As many repetitions as fit, with the empty ones collapsed.</summary>
    AutoFit
}
