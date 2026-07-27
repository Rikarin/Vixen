// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Text;

/// <summary>Where one word ends and the next begins.</summary>
/// <remarks>
///     <para>
///         What double-click selection and ctrl-arrow movement are measured in. Words are not
///         "runs between spaces": <c>can't</c> is one word and <c>1,000.50</c> is one number, while
///         <c>--</c> is neither, and getting it wrong is the kind of thing nobody files a bug about
///         and everybody notices.
///     </para>
///     <para>
///         UAX#29's word rules, which are harder than the cluster rules in one specific way:
///         several of them look <b>past</b> the pair in hand. <c>WB6</c> holds
///         <c>letter × MidLetter letter</c> together, so deciding whether to break before the
///         apostrophe in <c>can't</c> requires knowing there is a <c>t</c> after it. So this works
///         over a decoded array with lookahead rather than a streaming pair, and the ignore rule —
///         <c>WB4</c>, which makes format characters and extenders invisible to every other rule —
///         is applied by skipping rather than by classifying.
///     </para>
///     <para>
///         Judged by the Consortium's 1 944 cases, not by a reading of the specification.
///     </para>
/// </remarks>
public static class WordBreaker {
    /// <summary>Collects every word boundary in a string.</summary>
    /// <param name="text">The text.</param>
    /// <param name="boundaries">Receives the boundaries, ascending, including 0 and the length.</param>
    public static void Collect(ReadOnlySpan<char> text, List<int> boundaries) {
        ArgumentNullException.ThrowIfNull(boundaries);

        boundaries.Clear();
        boundaries.Add(0);

        if (text.Length == 0) {
            return;
        }

        // Decoded up front, because the rules look forward as well as back and a streaming pass
        // would have to buffer anyway.
        var offsets = new List<int>();
        var codePoints = new List<int>();
        var classes = new List<WordBreakClass>();
        var position = 0;

        while (position < text.Length) {
            offsets.Add(position);

            var codePoint = GraphemeBreaker.Decode(text, ref position);
            codePoints.Add(codePoint);
            classes.Add(WordBreakClassTable.Of(codePoint));
        }

        for (var i = 1; i < classes.Count; i++) {
            if (ShouldBreak(classes, codePoints, i)) {
                boundaries.Add(offsets[i]);
            }
        }

        boundaries.Add(text.Length);
    }

    /// <summary>Every word boundary in a string.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The boundaries, ascending, including 0 and the length.</returns>
    public static List<int> Boundaries(string text) {
        ArgumentNullException.ThrowIfNull(text);

        var boundaries = new List<int>();
        Collect(text, boundaries);
        return boundaries;
    }

    /// <summary>The word containing a position.</summary>
    /// <param name="text">The text.</param>
    /// <param name="index">A UTF-16 index inside the word.</param>
    /// <returns>The word's start and end.</returns>
    /// <remarks>What a double-click asks for.</remarks>
    public static (int Start, int End) WordAt(ReadOnlySpan<char> text, int index) {
        var boundaries = new List<int>();
        Collect(text, boundaries);

        for (var i = 1; i < boundaries.Count; i++) {
            if (index < boundaries[i]) {
                return (boundaries[i - 1], boundaries[i]);
            }
        }

        return (boundaries.Count > 1 ? boundaries[^2] : 0, text.Length);
    }

    /// <summary>Whether a word boundary falls before position <paramref name="i" />.</summary>
    static bool ShouldBreak(List<WordBreakClass> classes, List<int> codePoints, int i) {
        var before = classes[i - 1];
        var after = classes[i];

        // WB3 — a CRLF is never split.
        if (before == WordBreakClass.CR && after == WordBreakClass.LF) {
            return false;
        }

        // WB3a, WB3b — every other edge of a newline is a boundary.
        if (before is WordBreakClass.Newline or WordBreakClass.CR or WordBreakClass.LF
            || after is WordBreakClass.Newline or WordBreakClass.CR or WordBreakClass.LF) {
            return true;
        }

        // WB3c — an emoji ZWJ sequence.
        // Extended_Pictographic is a separate property from Word_Break, not a value of it: U+24C2
        // is Word_Break=ALetter *and* pictographic. Asking the class table would mean one of the two
        // had shadowed the other.
        if (before == WordBreakClass.ZWJ
            && ExtendedPictographicClassTable.Of(codePoints[i]) == ExtendedPictographicClass.ExtendedPictographic) {
            return false;
        }

        // WB3d — a run of segment space stays together.
        if (before == WordBreakClass.WSegSpace && after == WordBreakClass.WSegSpace) {
            return false;
        }

        // WB4 — format characters and extenders are invisible to everything below, and attach to
        // whatever precedes them. Applied by *skipping* rather than by classifying, because the
        // rules that look ahead have to look past them too.
        if (IsIgnorable(after)) {
            return false;
        }

        var left = PrecedingIndex(classes, i);
        if (left < 0) {
            // Nothing but ignorables before this, which after WB3a/WB3b can only mean the start of
            // the text — and the boundary there is already recorded.
            return true;
        }

        before = classes[left];

        // WB5, WB6, WB7 — letters, and the letter-medial punctuation that joins two of them.
        // Deciding the apostrophe in `can't` needs the `t`, which is what makes this lookahead.
        if (IsLetter(before) && IsLetter(after)) {
            return false;
        }

        if (IsLetter(before) && (after is WordBreakClass.MidLetter or WordBreakClass.MidNumLet or WordBreakClass.SingleQuote)
            && IsLetter(FollowingClass(classes, i))) {
            return false;
        }

        if ((before is WordBreakClass.MidLetter or WordBreakClass.MidNumLet or WordBreakClass.SingleQuote)
            && IsLetter(after)
            && IsLetter(PrecedingClass(classes, left))) {
            return false;
        }

        // WB7a, WB7b, WB7c — Hebrew's quotation marks behave differently from everyone else's.
        if (before == WordBreakClass.HebrewLetter && after == WordBreakClass.SingleQuote) {
            return false;
        }

        if (before == WordBreakClass.HebrewLetter
            && after == WordBreakClass.DoubleQuote
            && FollowingClass(classes, i) == WordBreakClass.HebrewLetter) {
            return false;
        }

        if (before == WordBreakClass.DoubleQuote
            && after == WordBreakClass.HebrewLetter
            && PrecedingClass(classes, left) == WordBreakClass.HebrewLetter) {
            return false;
        }

        // WB8 to WB11 — numbers, and the number-medial punctuation that joins two of them.
        if (before == WordBreakClass.Numeric && after == WordBreakClass.Numeric) {
            return false;
        }

        if (IsLetter(before) && after == WordBreakClass.Numeric) {
            return false;
        }

        if (before == WordBreakClass.Numeric && IsLetter(after)) {
            return false;
        }

        if (before == WordBreakClass.Numeric
            && after is WordBreakClass.MidNum or WordBreakClass.MidNumLet or WordBreakClass.SingleQuote
            && FollowingClass(classes, i) == WordBreakClass.Numeric) {
            return false;
        }

        if ((before is WordBreakClass.MidNum or WordBreakClass.MidNumLet or WordBreakClass.SingleQuote)
            && after == WordBreakClass.Numeric
            && PrecedingClass(classes, left) == WordBreakClass.Numeric) {
            return false;
        }

        // WB13 — Katakana holds together, which is what makes it selectable at all: Japanese does
        // not use spaces, and this is the only word rule that applies to it.
        if (before == WordBreakClass.Katakana && after == WordBreakClass.Katakana) {
            return false;
        }

        // WB13a, WB13b — an extending connector joins what is on either side of it.
        if ((IsLetter(before)
                || before is WordBreakClass.Numeric or WordBreakClass.Katakana or WordBreakClass.ExtendNumLet)
            && after == WordBreakClass.ExtendNumLet) {
            return false;
        }

        if (before == WordBreakClass.ExtendNumLet
            && (IsLetter(after) || after is WordBreakClass.Numeric or WordBreakClass.Katakana)) {
            return false;
        }

        // WB15, WB16 — pair up regional indicators, as the cluster rules do.
        if (before == WordBreakClass.RegionalIndicator && after == WordBreakClass.RegionalIndicator) {
            return CountRegionalIndicators(classes, left) % 2 == 0;
        }

        // WB999 — otherwise, break.
        return true;
    }

    static bool IsLetter(WordBreakClass value) =>
        value is WordBreakClass.ALetter or WordBreakClass.HebrewLetter;

    static bool IsIgnorable(WordBreakClass value) =>
        value is WordBreakClass.Extend or WordBreakClass.Format or WordBreakClass.ZWJ;

    /// <summary>The index of the last non-ignorable code point before position <paramref name="i" />.</summary>
    static int PrecedingIndex(List<WordBreakClass> classes, int i) {
        for (var j = i - 1; j >= 0; j--) {
            if (!IsIgnorable(classes[j])) {
                return j;
            }
        }

        return -1;
    }

    static WordBreakClass PrecedingClass(List<WordBreakClass> classes, int i) {
        var index = PrecedingIndex(classes, i);
        return index < 0 ? WordBreakClass.Other : classes[index];
    }

    /// <summary>The class of the next non-ignorable code point after position <paramref name="i" />.</summary>
    static WordBreakClass FollowingClass(List<WordBreakClass> classes, int i) {
        for (var j = i + 1; j < classes.Count; j++) {
            if (!IsIgnorable(classes[j])) {
                return classes[j];
            }
        }

        return WordBreakClass.Other;
    }

    /// <summary>How many regional indicators run up to and including position <paramref name="i" />.</summary>
    static int CountRegionalIndicators(List<WordBreakClass> classes, int i) {
        var count = 0;

        for (var j = i; j >= 0; j--) {
            if (classes[j] == WordBreakClass.RegionalIndicator) {
                count++;
                continue;
            }

            if (!IsIgnorable(classes[j])) {
                break;
            }
        }

        return count;
    }
}
