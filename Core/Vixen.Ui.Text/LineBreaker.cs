// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Ui.Text;

/// <summary>Where a line may be broken, and where it must be.</summary>
/// <remarks>
///     <para>
///         UAX#14. Not "break at spaces": a hyphen offers a break and a non-breaking hyphen does not,
///         <c>1,000</c> must not split at the comma, an opening bracket keeps what follows it, CJK
///         breaks almost anywhere, and Korean does not break inside a syllable. Wrapping done on
///         spaces alone looks wrong in English and is unusable in half the world's scripts.
///     </para>
///     <para>
///         This is the <i>opportunity</i> finder. It says where a break is permitted and where one is
///         mandatory; deciding which of the permitted ones to take is line filling, which needs
///         measured widths and is the layout pass's job. Keeping them apart is what lets this be
///         judged by the conformance suite at all — the suite is about opportunities and knows
///         nothing about fonts.
///     </para>
///     <para>
///         <b>The returned positions do not include zero</b>, unlike the segmentation breakers.
///         That is not an inconsistency: LB2 says never break at the start of text, so position zero
///         is not an opportunity, and the conformance data says so too by opening every case with a
///         prohibition rather than a break.
///     </para>
/// </remarks>
public static class LineBreaker {
    /// <summary>Collects every line break opportunity in a string.</summary>
    /// <param name="text">The text.</param>
    /// <param name="opportunities">Receives the positions, ascending, ending with the text length.</param>
    public static void Collect(ReadOnlySpan<char> text, List<int> opportunities) {
        ArgumentNullException.ThrowIfNull(opportunities);

        opportunities.Clear();

        if (text.Length == 0) {
            return;
        }

        var run = LineBreakRun.Resolve(text);

        for (var i = 1; i < run.Count; i++) {
            if (run.ShouldBreak(i)) {
                opportunities.Add(run.OffsetOf(i));
            }
        }

        // LB3 — always break at the end of text.
        opportunities.Add(text.Length);
    }

    /// <summary>Every line break opportunity in a string.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The positions, ascending, ending with the text length.</returns>
    public static List<int> Opportunities(string text) {
        ArgumentNullException.ThrowIfNull(text);

        var opportunities = new List<int>();
        Collect(text, opportunities);
        return opportunities;
    }

    /// <summary>Whether a break at a position is mandatory rather than merely permitted.</summary>
    /// <param name="text">The text.</param>
    /// <param name="index">A UTF-16 index.</param>
    /// <returns>Whether the text requires a line to end there.</returns>
    /// <remarks>
    ///     A hard newline, in any of the spellings Unicode recognises. Distinct from an opportunity
    ///     because a paragraph break is not something line filling gets to decline.
    /// </remarks>
    public static bool IsMandatory(ReadOnlySpan<char> text, int index) {
        if (index <= 0 || index > text.Length) {
            return false;
        }

        if (index == text.Length) {
            return true;
        }

        var position = 0;
        var previous = 0;

        while (position < text.Length) {
            var start = position;
            var codePoint = GraphemeBreaker.Decode(text, ref position);

            if (start == index) {
                var before = LineBreakClassTable.Of(previous);

                // LB4, LB5 — a CRLF is one break, not two.
                return before switch {
                    LineBreakClass.BK or LineBreakClass.LF or LineBreakClass.NL => true,
                    LineBreakClass.CR => LineBreakClassTable.Of(codePoint) != LineBreakClass.LF,
                    _ => false
                };
            }

            previous = codePoint;
        }

        return false;
    }
}

/// <summary>One string, with its line break classes resolved.</summary>
/// <remarks>
///     <para>
///         Resolution is LB1 and LB9/LB10, and doing it up front is what makes the rest readable.
///         <b>LB1</b> maps the classes that mean "ask somebody else" — <c>AI</c>, <c>SG</c>,
///         <c>XX</c>, <c>SA</c>, <c>CJ</c> — onto ones the rules actually mention. <b>LB9</b> makes a
///         combining mark take the class of what it combines with, so <c>e</c> + an acute behaves
///         exactly as <c>é</c> does, and <b>LB10</b> catches the marks that had nothing to attach to.
///     </para>
///     <para>
///         After that, every rule reads two adjacent resolved classes plus a little state: whether
///         spaces intervened, what came before them, how many regional indicators are in the run, and
///         whether a number is in progress.
///     </para>
/// </remarks>
sealed class LineBreakRun {
    readonly List<int> offsets = [];
    readonly List<int> codePoints = [];
    readonly List<LineBreakClass> classes = [];
    readonly List<LineBreakClass> original = [];
    readonly List<bool> attached = [];

    /// <summary>How many code points there are.</summary>
    public int Count => classes.Count;

    /// <summary>The UTF-16 offset of a code point.</summary>
    /// <param name="index">Its index.</param>
    /// <returns>The offset.</returns>
    public int OffsetOf(int index) => offsets[index];

    /// <summary>Decodes and resolves a string.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The resolved run.</returns>
    public static LineBreakRun Resolve(ReadOnlySpan<char> text) {
        var run = new LineBreakRun();
        var position = 0;

        while (position < text.Length) {
            run.offsets.Add(position);

            var codePoint = GraphemeBreaker.Decode(text, ref position);
            run.codePoints.Add(codePoint);
            run.original.Add(Substitute(codePoint));
        }

        // LB9, LB10 — a combining mark takes the class of its base, unless there is nothing to
        // combine with. `attached` remembers which positions were absorbed, because LB9 also says
        // there is no break in front of one.
        for (var i = 0; i < run.original.Count; i++) {
            var value = run.original[i];

            if (value is not (LineBreakClass.CM or LineBreakClass.ZWJ)) {
                run.classes.Add(value);
                run.attached.Add(false);
                continue;
            }

            var previous = i == 0 ? LineBreakClass.Other : run.classes[i - 1];
            var attachable = i > 0 && previous is not (LineBreakClass.BK or LineBreakClass.CR
                or LineBreakClass.LF or LineBreakClass.NL or LineBreakClass.SP or LineBreakClass.ZW);

            run.classes.Add(attachable ? previous : LineBreakClass.AL);
            run.attached.Add(attachable);
        }

        return run;
    }

    /// <summary>LB1 — the classes that stand for "resolve this some other way".</summary>
    static LineBreakClass Substitute(int codePoint) {
        var value = LineBreakClassTable.Of(codePoint);

        return value switch {
            // `AI` is ambiguous-width, `SG` is a surrogate and `XX` is unassigned. All three take the
            // conservative reading, which is also what the conformance data assumes.
            LineBreakClass.AI or LineBreakClass.SG or LineBreakClass.XX or LineBreakClass.Other => LineBreakClass.AL,

            // South-East Asian scripts need a dictionary to break properly. Without one, a combining
            // mark behaves as a mark and everything else as a letter.
            LineBreakClass.SA => CharUnicodeInfo.GetUnicodeCategory(codePoint)
                is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
                    ? LineBreakClass.CM
                    : LineBreakClass.AL,

            // `CJ` is conditional Japanese starter: small kana, which may or may not start a line
            // depending on typographic strictness. Normal strictness is the default.
            LineBreakClass.CJ => LineBreakClass.NS,
            _ => value
        };
    }

    /// <summary>Whether a break is permitted before position <paramref name="i" />.</summary>
    public bool ShouldBreak(int i) {
        // LB9 — no break between a base and the marks that combine with it.
        if (attached[i]) {
            return false;
        }

        var before = classes[i - 1];
        var after = classes[i];

        // LB4, LB5 — mandatory breaks, and the CRLF that is only one of them.
        if (before == LineBreakClass.BK) {
            return true;
        }

        if (before == LineBreakClass.CR) {
            return after != LineBreakClass.LF;
        }

        if (before is LineBreakClass.LF or LineBreakClass.NL) {
            return true;
        }

        // LB6 — never break before a mandatory break.
        if (after is LineBreakClass.BK or LineBreakClass.CR or LineBreakClass.LF or LineBreakClass.NL) {
            return false;
        }

        // LB7 — never break before a space or a zero-width space.
        if (after is LineBreakClass.SP or LineBreakClass.ZW) {
            return false;
        }

        // LB8 — a zero-width space offers a break after it, spaces notwithstanding.
        var beforeSpaces = SkipSpacesBackwards(i - 1, out var sawSpaces);
        if (beforeSpaces >= 0 && classes[beforeSpaces] == LineBreakClass.ZW) {
            return true;
        }

        // LB8a — a zero-width joiner holds on to what follows.
        if (before == LineBreakClass.ZWJ || original[i - 1] == LineBreakClass.ZWJ) {
            return false;
        }

        // LB11 — a word joiner binds both ways.
        if (after == LineBreakClass.WJ || before == LineBreakClass.WJ) {
            return false;
        }

        // LB12, LB12a — non-breaking glue.
        if (before == LineBreakClass.GL) {
            return false;
        }

        if (after == LineBreakClass.GL
            && before is not (LineBreakClass.SP or LineBreakClass.BA or LineBreakClass.HY or LineBreakClass.HH)) {
            return false;
        }

        // LB13 — never break before closing punctuation or an exclamation.
        if (after is LineBreakClass.CL or LineBreakClass.CP or LineBreakClass.EX or LineBreakClass.SY) {
            return false;
        }

        // LB14 — an opening bracket keeps what follows it, spaces notwithstanding.
        if (beforeSpaces >= 0 && classes[beforeSpaces] == LineBreakClass.OP) {
            return false;
        }

        // LB15a — an opening quotation mark keeps what follows it, but only when it is itself at the
        // start of something. `("a` binds; `a"b` does not.
        if (beforeSpaces >= 0 && classes[beforeSpaces] == LineBreakClass.QU && IsInitialPunctuation(beforeSpaces)) {
            // The character immediately before the quote, *not* the one before any spaces: SP is
            // itself one of the classes the rule allows there, so skipping over spaces and then
            // asking looks past the answer. `: « E` broke before the E until this stopped skipping.
            var opener = BaseOf(beforeSpaces) - 1;

            if (opener < 0 || classes[opener] is LineBreakClass.BK or LineBreakClass.CR or LineBreakClass.LF
                or LineBreakClass.NL or LineBreakClass.OP or LineBreakClass.QU or LineBreakClass.GL
                or LineBreakClass.SP or LineBreakClass.ZW) {
                return false;
            }
        }

        // LB15b — and a closing quotation mark keeps what precedes it, but only when something that
        // can follow a closing quote comes after it. The end of the text counts as one.
        if (after == LineBreakClass.QU && IsFinalPunctuation(i) && ClosesQuotation(Following(i))) {
            return false;
        }

        // LB15c, LB15d — a separator between a space and a number begins a number rather than
        // ending a sentence, and otherwise a separator binds to what precedes it.
        if (before == LineBreakClass.SP && after == LineBreakClass.IS && Following(i) == LineBreakClass.NU) {
            return true;
        }

        if (after == LineBreakClass.IS) {
            return false;
        }

        // LB16, LB17 — a closing bracket keeps a following non-starter, and a two-em dash keeps
        // another one, in both cases across spaces.
        if (beforeSpaces >= 0
            && classes[beforeSpaces] is LineBreakClass.CL or LineBreakClass.CP
            && after == LineBreakClass.NS) {
            return false;
        }

        if (beforeSpaces >= 0 && classes[beforeSpaces] == LineBreakClass.B2 && after == LineBreakClass.B2) {
            return false;
        }

        // LB18 — and otherwise a space is where a break goes.
        if (sawSpaces || before == LineBreakClass.SP) {
            return true;
        }

        // LB19 — a quotation mark binds, except at the ends where LB15a and LB15b have already had
        // their say: an opening quote does not bind on its left, nor a closing one on its right.
        if (after == LineBreakClass.QU && !IsInitialPunctuation(i)) {
            return false;
        }

        if (before == LineBreakClass.QU && !IsFinalPunctuation(i - 1)) {
            return false;
        }

        // LB19a — and the East Asian conditions, which are what stop a Western quotation mark
        // gluing itself to a CJK character that has its own.
        if (after == LineBreakClass.QU && (!IsEastAsian(i - 1) || !IsEastAsian(i + 1))) {
            return false;
        }

        if (before == LineBreakClass.QU && (!IsEastAsian(i) || i - 2 < 0 || !IsEastAsian(i - 2))) {
            return false;
        }

        // LB20 — a contingent break is a break on both sides.
        if (before == LineBreakClass.CB || after == LineBreakClass.CB) {
            return true;
        }

        // LB20a — a hyphen at the start of a word keeps the word.
        if (before is LineBreakClass.HY or LineBreakClass.HH
            && IsLetter(after)
            && StartsWord(i - 1)) {
            return false;
        }

        // LB21, LB21a, LB21b — hyphens and the Hebrew exception.
        if (after is LineBreakClass.BA or LineBreakClass.HY or LineBreakClass.HH or LineBreakClass.NS) {
            return false;
        }

        if (before == LineBreakClass.BB) {
            return false;
        }

        // LB21a names the hyphens specifically — `HY` and `HH` — and not `BA` at large. A Hebrew
        // letter followed by a mathematical space is not a hyphenated Hebrew word, and one case out
        // of the nineteen thousand is exactly that.
        var hyphenBase = BaseOf(i - 1);
        if (hyphenBase >= 1
            && classes[hyphenBase - 1] == LineBreakClass.HL
            && before is LineBreakClass.HY or LineBreakClass.HH
            && after != LineBreakClass.HL) {
            return false;
        }

        if (before == LineBreakClass.SY && after == LineBreakClass.HL) {
            return false;
        }

        // LB22 — never break before an inseparable, which is what an ellipsis is.
        if (after == LineBreakClass.IN) {
            return false;
        }

        // LB23, LB23a — letters and digits stay together, and so do prefixes and ideographs.
        if (IsLetter(before) && after == LineBreakClass.NU) {
            return false;
        }

        if (before == LineBreakClass.NU && IsLetter(after)) {
            return false;
        }

        if (before == LineBreakClass.PR && after is LineBreakClass.ID or LineBreakClass.EB or LineBreakClass.EM) {
            return false;
        }

        if (before is LineBreakClass.ID or LineBreakClass.EB or LineBreakClass.EM && after == LineBreakClass.PO) {
            return false;
        }

        // LB24 — currency signs and the letters around them.
        if (before is LineBreakClass.PR or LineBreakClass.PO && IsLetter(after)) {
            return false;
        }

        if (IsLetter(before) && after is LineBreakClass.PR or LineBreakClass.PO) {
            return false;
        }

        // LB25 — numbers. `1,000.00` is one thing, and so is `$1,000.00-`.
        if (IsNumberJoin(i)) {
            return false;
        }

        // LB26, LB27 — Korean syllables do not break inside themselves.
        if (before == LineBreakClass.JL
            && after is LineBreakClass.JL or LineBreakClass.JV or LineBreakClass.H2 or LineBreakClass.H3) {
            return false;
        }

        if (before is LineBreakClass.JV or LineBreakClass.H2 && after is LineBreakClass.JV or LineBreakClass.JT) {
            return false;
        }

        if (before is LineBreakClass.JT or LineBreakClass.H3 && after == LineBreakClass.JT) {
            return false;
        }

        if (IsHangul(before) && after == LineBreakClass.PO) {
            return false;
        }

        if (before == LineBreakClass.PR && IsHangul(after)) {
            return false;
        }

        // LB28 — two letters.
        if (IsLetter(before) && IsLetter(after)) {
            return false;
        }

        // LB28a — Brahmic orthographic syllables. The rule names U+25CC DOTTED CIRCLE *literally*
        // rather than by class, because it is the placeholder a mark is shown on when it has no
        // base, and the syllable rules have to treat it as one.
        if (before == LineBreakClass.AP && IsAksaraLike(i)) {
            return false;
        }

        if (IsAksaraLike(i - 1) && after is LineBreakClass.VF or LineBreakClass.VI) {
            return false;
        }

        var viBase = BaseOf(i - 1);
        if (before == LineBreakClass.VI && viBase >= 1 && IsAksaraLike(viBase - 1) && IsAksaraLike(i)) {
            return false;
        }

        if (IsAksaraLike(i - 1) && IsAksaraLike(i) && Following(i) == LineBreakClass.VF) {
            return false;
        }

        // LB29 — a separator before a letter.
        if (before == LineBreakClass.IS && IsLetter(after)) {
            return false;
        }

        // LB30 — brackets that are not East Asian bind to the letters around them.
        if ((IsLetter(before) || before == LineBreakClass.NU)
            && after == LineBreakClass.OP
            && !IsEastAsianWide(i)) {
            return false;
        }

        if (before == LineBreakClass.CP
            && !IsEastAsianWide(i - 1)
            && (IsLetter(after) || after == LineBreakClass.NU)) {
            return false;
        }

        // LB30a — pair up regional indicators, as every other algorithm does.
        if (before == LineBreakClass.RI && after == LineBreakClass.RI && RegionalIndicatorsBefore(i) % 2 == 1) {
            return false;
        }

        // LB30b — an emoji base keeps its modifier.
        if (before == LineBreakClass.EB && after == LineBreakClass.EM) {
            return false;
        }

        // The second clause is about *unassigned* pictographs — code points reserved in an emoji
        // block that no character has been given yet — so that a future emoji keeps its skin tone
        // before this table is regenerated. Read from the base, because a combining mark in between
        // takes the base's class but not its identity.
        var pictographBase = codePoints[BaseOf(i - 1)];

        if (after == LineBreakClass.EM
            && ExtendedPictographicClassTable.Of(pictographBase) == ExtendedPictographicClass.ExtendedPictographic
            && CharUnicodeInfo.GetUnicodeCategory(pictographBase) == UnicodeCategory.OtherNotAssigned) {
            return false;
        }

        // LB31 — otherwise, break.
        return true;
    }

    int SkipSpacesBackwards(int from, out bool sawSpaces) {
        sawSpaces = false;

        while (from >= 0 && classes[from] == LineBreakClass.SP) {
            sawSpaces = true;
            from--;
        }

        return from;
    }

    LineBreakClass Following(int i) => i + 1 < classes.Count ? classes[i + 1] : LineBreakClass.Other;

    /// <summary>LB25 — whether a number spans the boundary at <paramref name="i" />.</summary>
    /// <remarks>
    ///     <para>
    ///         Unicode 15.1 replaced the regular expression this rule used to be with a list of
    ///         pairs, and the pairs are both easier to implement and easier to be sure of. The one
    ///         piece of context they need is <c>NU (SY | IS)*</c> — "a number, possibly followed by
    ///         separators" — which is what <see cref="NumberEndsAt" /> answers.
    ///     </para>
    ///     <para>
    ///         Written as the regex first, which passed most of the suite and failed on
    ///         <c>HY × NU</c> and <c>IS × NU</c>. Those are two of the pairs, and they have no
    ///         regex form because a hyphen before a number is not part of the number.
    ///     </para>
    /// </remarks>
    bool IsNumberJoin(int i) {
        var before = classes[i - 1];
        var after = classes[i];

        // `-5`, `,5` — a sign or separator binds to the digits after it.
        if (before is LineBreakClass.HY or LineBreakClass.IS && after == LineBreakClass.NU) {
            return true;
        }

        // `$5`, `5%` — a currency prefix or suffix binds to the number, with or without a bracket.
        if (before is LineBreakClass.PR or LineBreakClass.PO) {
            if (after == LineBreakClass.NU) {
                return true;
            }

            if (after == LineBreakClass.OP && Following(i) == LineBreakClass.NU) {
                return true;
            }
        }

        // `1,000` and `1,000)-` — the number and its separators, then optionally a bracket, then a
        // suffix.
        if (NumberEndsAt(i - 1)) {
            if (after is LineBreakClass.NU or LineBreakClass.PO or LineBreakClass.PR) {
                return true;
            }
        }

        if (before is LineBreakClass.CL or LineBreakClass.CP
            && after is LineBreakClass.PO or LineBreakClass.PR
            && NumberEndsAt(i - 2)) {
            return true;
        }

        return false;
    }

    /// <summary>Whether <c>NU (SY | IS)*</c> ends at position <paramref name="i" />.</summary>
    bool NumberEndsAt(int i) {
        while (i >= 0 && classes[i] is LineBreakClass.SY or LineBreakClass.IS) {
            i--;
        }

        return i >= 0 && classes[i] == LineBreakClass.NU;
    }

    /// <summary>How many regional indicators run up to position <paramref name="i" />.</summary>
    /// <remarks>
    ///     Attached combining marks are skipped rather than counted. They inherit the indicator's
    ///     class under LB9, so counting them turns one flag into two and breaks the pairing — which
    ///     is what the suite said, in the one case with a diaeresis between two indicators.
    /// </remarks>
    int RegionalIndicatorsBefore(int i) {
        var count = 0;

        for (var j = i - 1; j >= 0; j--) {
            if (attached[j]) {
                continue;
            }

            if (classes[j] != LineBreakClass.RI) {
                break;
            }

            count++;
        }

        return count;
    }

    /// <summary>Whether a position begins a word, for LB20a's leading hyphen.</summary>
    /// <remarks>
    ///     The character immediately before, not the one before any spaces. <c>SP</c> is itself in
    ///     the set the rule allows, so skipping spaces and then asking looks straight past the
    ///     answer — the same mistake LB15a made, and the reason <c>Mac Pro -tietokone</c> broke after
    ///     its hyphen.
    /// </remarks>
    bool StartsWord(int i) {
        var previous = BaseOf(i) - 1;
        return previous < 0 || classes[previous] is LineBreakClass.BK or LineBreakClass.CR or LineBreakClass.LF
            or LineBreakClass.NL or LineBreakClass.SP or LineBreakClass.ZW or LineBreakClass.CB or LineBreakClass.GL;
    }

    /// <summary>Whether a position is an opening quotation mark.</summary>
    /// <remarks>
    ///     Read from <see cref="BaseOf" />, and every code-point test in this file has to be. LB9
    ///     gives a combining mark its base's <i>class</i>, which is enough for the rules that read
    ///     classes and silently wrong for the ones that read identity — a quotation mark followed by
    ///     a diaeresis stopped being a quotation mark, and the suite caught it three separate times
    ///     before the rule was stated this plainly.
    /// </remarks>
    bool IsInitialPunctuation(int i) =>
        CharUnicodeInfo.GetUnicodeCategory(codePoints[BaseOf(i)]) == UnicodeCategory.InitialQuotePunctuation;

    /// <summary>Whether a position is a closing quotation mark.</summary>
    bool IsFinalPunctuation(int i) =>
        CharUnicodeInfo.GetUnicodeCategory(codePoints[BaseOf(i)]) == UnicodeCategory.FinalQuotePunctuation;

    /// <summary>The classes LB15b allows to follow a closing quotation mark, plus the end of text.</summary>
    static bool ClosesQuotation(LineBreakClass value) =>
        value == LineBreakClass.Other ||
        value is LineBreakClass.SP or LineBreakClass.GL or LineBreakClass.WJ or LineBreakClass.CL
            or LineBreakClass.QU or LineBreakClass.CP or LineBreakClass.EX or LineBreakClass.IS
            or LineBreakClass.SY or LineBreakClass.BK or LineBreakClass.CR or LineBreakClass.LF
            or LineBreakClass.NL or LineBreakClass.ZW;

    /// <summary>Whether a position holds an East Asian character.</summary>
    /// <remarks>
    ///     Out of range answers <see langword="false" />, which is what LB19a wants: the rule reads
    ///     "not East Asian, or the end of the text", and the two cases behave the same way.
    /// </remarks>
    bool IsEastAsian(int i) =>
        i >= 0
        && i < codePoints.Count
        && EastAsianWidthClassTable.Of(codePoints[i])
            is EastAsianWidthClass.F or EastAsianWidthClass.W or EastAsianWidthClass.H;

    bool IsEastAsianWide(int i) =>
        EastAsianWidthClassTable.Of(codePoints[i])
            is EastAsianWidthClass.F or EastAsianWidthClass.W or EastAsianWidthClass.H;

    static bool IsLetter(LineBreakClass value) => value is LineBreakClass.AL or LineBreakClass.HL;

    static bool IsHangul(LineBreakClass value) =>
        value is LineBreakClass.JL or LineBreakClass.JV or LineBreakClass.JT
            or LineBreakClass.H2 or LineBreakClass.H3;

    /// <summary>Whether a position is an aksara, a start, or the dotted-circle placeholder.</summary>
    bool IsAksaraLike(int i) =>
        i >= 0
        && i < classes.Count
        && (classes[i] is LineBreakClass.AK or LineBreakClass.AS || codePoints[BaseOf(i)] == DottedCircle);

    /// <summary>The position a run of combining marks is attached to.</summary>
    /// <remarks>
    ///     LB9 gives a mark the <i>class</i> of its base, which is enough for every rule that reads
    ///     classes. It is not enough for the two that do not: LB28a names U+25CC by code point, and
    ///     LB20a asks what came before the base. Both were wrong until this existed — a dotted circle
    ///     followed by a diaeresis stopped being a dotted circle.
    /// </remarks>
    int BaseOf(int i) {
        while (i > 0 && attached[i]) {
            i--;
        }

        return i;
    }

    /// <summary>U+25CC DOTTED CIRCLE, which LB28a names by code point rather than by class.</summary>
    const int DottedCircle = 0x25CC;
}
