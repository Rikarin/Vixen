// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Text;

/// <summary>Where one user-perceived character ends and the next begins.</summary>
/// <remarks>
///     <para>
///         A grapheme cluster is what a person means by "a character", and it is very often not one
///         code point: <c>é</c> may be two, a flag is two, a family emoji with skin tones is eleven,
///         and a Devanagari conjunct is however many the orthography needs. Everything a text editor
///         does in units of "characters" — arrow keys, backspace, selection, the count in a
///         character-limited field — is wrong unless it is done in these.
///     </para>
///     <para>
///         UAX#29's rules over the generated property tables. Most are a decision about two adjacent
///         code points; two are not, and those are where implementations go wrong. <b>GB12/GB13</b>
///         pairs regional indicators, so four of them are two flags rather than a flag and two
///         halves. <b>GB9c</b> holds an indic conjunct together across its virama, which needs to
///         know what has been seen since the last consonant and needs a property that lives in a
///         different UCD file from all the others.
///     </para>
///     <para>
///         Written against the conformance suite rather than against a reading of the specification.
///         The two are not the same thing, and the suite is the arbiter.
///     </para>
/// </remarks>
public static class GraphemeBreaker {
    /// <summary>Collects every cluster boundary in a string.</summary>
    /// <param name="text">The text.</param>
    /// <param name="boundaries">Receives the boundaries, ascending, including 0 and the length.</param>
    /// <remarks>
    ///     Boundaries are UTF-16 indices, because that is what a <see cref="string" /> is indexed by
    ///     and therefore what a caret position has to be. The conformance suite is written per code
    ///     point, and the translation happens in the test helper rather than here.
    /// </remarks>
    public static void Collect(ReadOnlySpan<char> text, List<int> boundaries) {
        ArgumentNullException.ThrowIfNull(boundaries);

        boundaries.Clear();
        boundaries.Add(0);

        if (text.Length == 0) {
            return;
        }

        var state = default(GraphemeState);
        var position = 0;
        var previous = Decode(text, ref position);

        state.Observe(previous);

        while (position < text.Length) {
            var start = position;
            var next = Decode(text, ref position);

            if (state.ShouldBreak(previous, next)) {
                boundaries.Add(start);
            }

            state.Observe(next);
            previous = next;
        }

        boundaries.Add(text.Length);
    }

    /// <summary>Every cluster boundary in a string.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The boundaries, ascending, including 0 and the length.</returns>
    public static List<int> Boundaries(string text) {
        ArgumentNullException.ThrowIfNull(text);

        var boundaries = new List<int>();
        Collect(text, boundaries);
        return boundaries;
    }

    /// <summary>Whether a cluster ends immediately before a position.</summary>
    /// <param name="text">The text.</param>
    /// <param name="index">A UTF-16 index.</param>
    /// <returns>Whether it is a boundary.</returns>
    public static bool IsBoundary(ReadOnlySpan<char> text, int index) {
        if (index <= 0 || index >= text.Length) {
            // GB1 and GB2 — the edges are always boundaries.
            return true;
        }

        var boundaries = new List<int>();
        Collect(text, boundaries);
        return boundaries.Contains(index);
    }

    /// <summary>The cluster containing a position.</summary>
    /// <param name="text">The text.</param>
    /// <param name="index">A UTF-16 index inside the cluster.</param>
    /// <returns>The cluster's start and end.</returns>
    /// <remarks>
    ///     What a text editor asks when the caret has to move, and what makes backspace delete a
    ///     family emoji rather than one of its members.
    /// </remarks>
    public static (int Start, int End) ClusterAt(ReadOnlySpan<char> text, int index) {
        var boundaries = new List<int>();
        Collect(text, boundaries);

        for (var i = 1; i < boundaries.Count; i++) {
            if (index < boundaries[i]) {
                return (boundaries[i - 1], boundaries[i]);
            }
        }

        return (boundaries.Count > 1 ? boundaries[^2] : 0, text.Length);
    }

    /// <summary>How many clusters a string has.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The count — what a character-limited field should be counting.</returns>
    public static int Count(ReadOnlySpan<char> text) {
        var boundaries = new List<int>();
        Collect(text, boundaries);
        return boundaries.Count - 1;
    }

    /// <summary>Decodes one code point and advances past it.</summary>
    /// <param name="text">The text.</param>
    /// <param name="position">The UTF-16 index, advanced past what was read.</param>
    /// <returns>The code point.</returns>
    /// <remarks>
    ///     An unpaired surrogate is returned as itself rather than replaced. It is not a character,
    ///     and substituting U+FFFD would move a boundary that a text editor is about to put a caret
    ///     at — a malformed string still has to be editable.
    /// </remarks>
    internal static int Decode(ReadOnlySpan<char> text, ref int position) {
        var first = text[position++];

        if (!char.IsHighSurrogate(first) || position >= text.Length || !char.IsLowSurrogate(text[position])) {
            return first;
        }

        return char.ConvertToUtf32(first, text[position++]);
    }
}

/// <summary>The part of the grapheme rules that two adjacent code points cannot decide.</summary>
struct GraphemeState {
    int regionalIndicators;
    bool consonantSeen;
    bool linkerSeen;
    bool pictographicRun;

    /// <summary>Records what has just been consumed.</summary>
    /// <param name="codePoint">The code point.</param>
    public void Observe(int codePoint) {
        var grapheme = GraphemeBreakClassTable.Of(codePoint);
        var conjunct = IndicConjunctClassTable.Of(codePoint);

        regionalIndicators = grapheme == GraphemeBreakClass.RegionalIndicator ? regionalIndicators + 1 : 0;

        // GB11 is `Pictographic Extend* ZWJ × Pictographic`, and the leading pictograph is the part
        // that has to be remembered: without it, a joiner between two letters would glue them
        // together. Extenders and the joiner itself keep the run alive; anything else ends it.
        pictographicRun = IsPictographic(codePoint)
            || (grapheme is GraphemeBreakClass.Extend or GraphemeBreakClass.ZWJ && pictographicRun);

        switch (conjunct) {
            case IndicConjunctClass.Consonant:
                consonantSeen = true;
                linkerSeen = false;
                break;

            case IndicConjunctClass.Linker:
                linkerSeen |= consonantSeen;
                break;

            case IndicConjunctClass.Extend:
                // An extender neither starts nor ends a conjunct; it is transparent to the rule,
                // which is exactly why GB9c needs state rather than a look at the previous code
                // point.
                break;

            default:
                consonantSeen = false;
                linkerSeen = false;
                break;
        }
    }

    /// <summary>Whether a cluster boundary falls between two adjacent code points.</summary>
    /// <param name="left">The code point before.</param>
    /// <param name="right">The code point after.</param>
    /// <returns>Whether to break.</returns>
    public readonly bool ShouldBreak(int left, int right) {
        var before = GraphemeBreakClassTable.Of(left);
        var after = GraphemeBreakClassTable.Of(right);

        // GB3, GB4, GB5 — a CRLF is one cluster, and every other edge of a control is a boundary.
        if (before == GraphemeBreakClass.CR && after == GraphemeBreakClass.LF) {
            return false;
        }

        if (before is GraphemeBreakClass.Control or GraphemeBreakClass.CR or GraphemeBreakClass.LF
            || after is GraphemeBreakClass.Control or GraphemeBreakClass.CR or GraphemeBreakClass.LF) {
            return true;
        }

        // GB6, GB7, GB8 — Hangul syllables hold together in the shapes the script allows.
        if (before == GraphemeBreakClass.L
            && after is GraphemeBreakClass.L or GraphemeBreakClass.V or GraphemeBreakClass.LV or GraphemeBreakClass.LVT) {
            return false;
        }

        if (before is GraphemeBreakClass.LV or GraphemeBreakClass.V
            && after is GraphemeBreakClass.V or GraphemeBreakClass.T) {
            return false;
        }

        if (before is GraphemeBreakClass.LVT or GraphemeBreakClass.T && after == GraphemeBreakClass.T) {
            return false;
        }

        // GB9, GB9a, GB9b — anything that extends, joins, or prepends stays with its neighbour.
        if (after is GraphemeBreakClass.Extend or GraphemeBreakClass.ZWJ or GraphemeBreakClass.SpacingMark) {
            return false;
        }

        if (before == GraphemeBreakClass.Prepend) {
            return false;
        }

        // GB9c — consonant, linker, consonant, held together across the virama that joins them.
        // The linker may be separated from either consonant by extenders, which is what the state
        // is tracking and why this cannot be read off the two code points in hand.
        if (IndicConjunctClassTable.Of(right) == IndicConjunctClass.Consonant && linkerSeen) {
            return false;
        }

        // GB11 — an emoji ZWJ sequence, and only when the sequence began with a pictograph.
        if (pictographicRun && before == GraphemeBreakClass.ZWJ && IsPictographic(right)) {
            return false;
        }

        // GB12, GB13 — pair up regional indicators, so four of them are two flags.
        if (before == GraphemeBreakClass.RegionalIndicator
            && after == GraphemeBreakClass.RegionalIndicator
            && regionalIndicators % 2 == 1) {
            return false;
        }

        // GB999 — otherwise, break.
        return true;
    }

    /// <summary>Whether a code point is Extended_Pictographic.</summary>
    /// <remarks>
    ///     A separate table rather than a class, because a code point can be pictographic *and*
    ///     something else — U+24C2 is both a letter and a pictograph — and folding the two into one
    ///     class makes one silently shadow the other.
    /// </remarks>
    static bool IsPictographic(int codePoint) =>
        ExtendedPictographicClassTable.Of(codePoint) == ExtendedPictographicClass.ExtendedPictographic;
}
