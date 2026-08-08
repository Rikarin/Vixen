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

    /// <summary>Reads one <c>grid-{row,column}-{start,end}</c> value.</summary>
    /// <remarks>
    ///     ⚠ <b>A named line is refused rather than taken as <c>auto</c>.</b> There is nowhere in
    ///     this struct to put a name, and <c>grid-column-start: sidebar</c> silently becoming
    ///     auto-placement is precisely the failure this whole grammar is written to avoid — the item
    ///     lands somewhere plausible and nothing says the line it named was never found. Named lines
    ///     arrive with <c>grid-template-areas</c> or not at all.
    /// </remarks>
    /// <param name="value">The value, verbatim.</param>
    /// <param name="placement">Receives the placement.</param>
    /// <returns>Whether it was understood.</returns>
    public static bool TryParse(ReadOnlySpan<char> value, out GridPlacement placement) {
        value = value.Trim();
        placement = Auto;

        if (value.IsEmpty) {
            return false;
        }

        if (value.Equals("auto", StringComparison.Ordinal)) {
            return true;
        }

        // §8.3's `span` may be written with the count either side of the keyword in the grammar's
        // full form, but the only shape that occurs anywhere — corpus, Tailwind, hand-written CSS —
        // is a leading keyword, and a bare `span` means one track.
        if (value.StartsWith("span", StringComparison.Ordinal)) {
            var rest = value["span".Length..].Trim();

            if (rest.IsEmpty) {
                placement = Span(1);
                return true;
            }

            if (rest.Length == value.Length - "span".Length) {
                // No separator between `span` and what follows: this is an identifier such as
                // `spanish`, not a span of anything.
                return false;
            }

            if (!int.TryParse(rest, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var tracks)) {
                return false;
            }

            placement = Span(tracks);
            return true;
        }

        if (!int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var line)) {
            return false;
        }

        placement = Line(line);
        return true;
    }

    /// <summary>Reads a <c>grid-column</c> or <c>grid-row</c> shorthand.</summary>
    /// <remarks>
    ///     ⚠ <b>An omitted second value is <c>auto</c>, and that is not the same as repeating the
    ///     first.</b> CSS Grid §8.4: when the slash is absent the end edge is <c>auto</c> unless the
    ///     start was a <c>&lt;custom-ident&gt;</c>, which this grammar has no reading of anyway. So
    ///     <c>grid-column: span 2</c> spans two tracks from wherever auto-placement puts it, while
    ///     <c>grid-column: span 2 / span 2</c> is over-constrained and §8.3 drops the end edge —
    ///     the two are written down as different declarations and are stored as different ones.
    /// </remarks>
    /// <param name="value">The shorthand's value, verbatim.</param>
    /// <param name="start">Receives the start edge.</param>
    /// <param name="end">Receives the end edge.</param>
    /// <returns>Whether the whole shorthand was understood.</returns>
    public static bool TryParseShorthand(ReadOnlySpan<char> value, out GridPlacement start, out GridPlacement end) {
        start = Auto;
        end = Auto;

        var slash = value.IndexOf('/');

        if (slash < 0) {
            return TryParse(value, out start);
        }

        // ⚠ A second slash is `grid-area`, which names four edges and is a different property. It is
        // refused rather than read as the first two, because taking the first half of a four-edge
        // placement puts the item in a real but wrong cell.
        var tail = value[(slash + 1)..];
        if (tail.Contains('/')) {
            return false;
        }

        return TryParse(value[..slash], out start) && TryParse(tail, out end);
    }
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
