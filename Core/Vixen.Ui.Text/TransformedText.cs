// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;

namespace Vixen.Ui.Text;

/// <summary>What <c>text-transform</c> does to the characters before they are shaped.</summary>
/// <remarks>
///     CSS Text 3 § 2.1. <c>full-width</c> and <c>full-size-kana</c> are not here: both are
///     compatibility mappings for Japanese input methods, neither has a utility class in any
///     framework this repository follows, and both would want a second table for a case no
///     interface in this tree can reach.
/// </remarks>
public enum TextTransform : byte {
    /// <summary>The text is drawn as it was written. The initial value.</summary>
    None,

    /// <summary>Every character in uppercase.</summary>
    Uppercase,

    /// <summary>Every character in lowercase.</summary>
    Lowercase,

    /// <summary>The first letter of every word in titlecase, and the rest untouched.</summary>
    /// <remarks>
    ///     ⚠ <b>The rest is untouched, not lowercased.</b> <c>capitalize</c> titlecases a word's
    ///     first letter and says nothing about the others, so <c>iPhone</c> stays <c>IPhone</c>
    ///     rather than becoming <c>Iphone</c>. Every browser does this and it surprises people.
    /// </remarks>
    Capitalize
}

/// <summary>An element's text after <c>text-transform</c>, and the map back to what was written.</summary>
/// <remarks>
///     <para>
///         <b>The map is the point, and the four keywords are the easy part.</b> A full Unicode case
///         mapping changes the UTF-16 <i>length</i> — <c>straße</c> uppercases to <c>STRASSE</c>, one
///         character becoming two — and what a text layout hands out is every one of them an index:
///         a caret index, a selection range, the start of a line. Shipping the keywords without this
///         puts the caret in the wrong character of an editable field, silently, and only on the
///         strings where it expands.
///     </para>
///     <para>
///         ⚠ <b>.NET's own casing would never have shown that.</b> <c>string.ToUpperInvariant</c>,
///         <c>ToUpper</c> in every culture, and <c>Rune.ToUpperInvariant</c> over all 1 112 064
///         scalars implement the <i>simple</i> mappings, which are one code point to one by
///         definition — measured, not assumed. So the naive implementation is index-safe and draws
///         <c>STRAßE</c>, which is a different defect and a visible one. The expansions come from
///         <c>SpecialCasingTable</c>, which is the UCD's unconditional rows and is what makes this
///         type necessary at all.
///     </para>
///     <para>
///         ⚠ <b><see cref="ToSource" /> is many-to-one and <see cref="ToDrawn" /> is not.</b> Both
///         units of the <c>SS</c> that came from one <c>ß</c> map back to that <c>ß</c>, because
///         there is no caret position between them — the two letters are one character the author
///         typed. So source → drawn → source is the identity and drawn → source → drawn is not, and
///         a click in the middle of an expansion snaps to its start. That is the behaviour a text
///         field wants and it is the reason the pair is not a single offset.
///     </para>
///     <para>
///         Identity is the overwhelmingly common case — no transform at all, or a transform under
///         which every character keeps its length — and it allocates no arrays and returns the
///         <i>same string instance</i>, which is what keeps the shaping cache's fast path and
///         <c>UiElement.Block</c>'s reference test meaning what they meant.
///     </para>
/// </remarks>
public sealed class TransformedText {
    /// <summary>Where each drawn index came from, or null when the map is the identity.</summary>
    readonly int[]? sourceOf;

    /// <summary>Where each source index went, or null when the map is the identity.</summary>
    readonly int[]? drawnOf;

    TransformedText(string source, string text, int[]? sourceOf, int[]? drawnOf) {
        Source = source;
        Text = text;
        this.sourceOf = sourceOf;
        this.drawnOf = drawnOf;
    }

    /// <summary>The text as it was written.</summary>
    public string Source { get; }

    /// <summary>The text as it is shaped and drawn.</summary>
    /// <remarks>
    ///     The same instance as <see cref="Source" /> when nothing changed, which is deliberate: a
    ///     substring taken from it hashes into the shaping cache under the entry the untransformed
    ///     string already had.
    /// </remarks>
    public string Text { get; }

    /// <summary>Whether every index means the same thing in both strings.</summary>
    public bool IsIdentity => drawnOf is null;

    /// <summary>Applies a transform, and builds the map if it moved anything.</summary>
    /// <param name="source">The element's own text.</param>
    /// <param name="transform">What to do to it.</param>
    /// <param name="language">
    ///     The content language as a BCP-47 tag — <c>UiElement.ResolvedLanguage</c>. Empty is
    ///     undetermined and takes the language-independent mapping.
    /// </param>
    /// <returns>The drawn text and the map between the two.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The identity test is "did any character change length", not "did the string
    ///         change".</b> <c>hello</c> uppercased is a different string of the same shape, and every
    ///         index in it still means what it meant — so it needs no arrays, and paying for them would
    ///         put an allocation and two indirections on every uppercased label in an interface.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Casing is language-dependent for one alphabet and the difference is a word, not a
    ///         glyph.</b> In Turkish and Azerbaijani <c>i</c> uppercases to <c>İ</c> and <c>I</c>
    ///         lowercases to <c>ı</c>, because the dotted and dotless letters are two different
    ///         letters rather than two cases of one. <c>ISPARTA</c> lowercased with the
    ///         language-independent table is <c>isparta</c>, which is a different word in Turkish and
    ///         is what every interface here drew until this parameter existed.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And one conditional mapping depends on no language at all.</b> Greek sigma
    ///         lowercases to ς at the end of a word and σ everywhere else, in every locale — so
    ///         <c>ΟΔΟΣ</c> drew as <c>οδοσ</c> here until <see cref="IsFinalSigma" /> existed, which
    ///         is wrong in a Greek document whatever its language tag says. It is evaluated in this
    ///         walk rather than looked up, because <c>SpecialCasingTable</c> is the UCD's
    ///         <i>unconditional</i> rows and a condition is a question about the neighbours.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Lithuanian is a third language and its rows are not one set.</b> Three of them —
    ///         the precomposed U+00CC, U+00CD and U+0128 — carry a language and no condition, so they
    ///         are a lookup in <see cref="LithuanianRetainedDot" /> and are implemented. The rest
    ///         (<c>More_Above</c> on <c>I</c>, <c>J</c> and U+012E, <c>After_Soft_Dotted</c> on
    ///         U+0307) are conditional on combining class 230 and the <c>Soft_Dotted</c> property,
    ///         and no generated table in this assembly carries either — so they are owed rather than
    ///         approximated, because <c>More_Above</c> guessed without the classes would be wrong for
    ///         every mark that is not above. Same for <c>tr</c>/<c>az</c>'s <c>Not_Before_Dot</c>,
    ///         whose "no intervening character of class 0 or 230" is the same missing data.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>No <c>CultureInfo</c>, deliberately and not merely incidentally.</b> The tag is
    ///         read from the element, so the same document uppercases the same way on a Turkish
    ///         laptop and on CI — the property <c>TextShaper</c> protects for shaping, held here for
    ///         casing, which changes a string's <i>length</i> and therefore its measured width. It is
    ///         also what makes this testable at all: these assemblies run in globalization-invariant
    ///         mode, where <c>CultureInfo.GetCultureInfo("tr-TR")</c> throws, so an implementation
    ///         routed through .NET's culture casing could not be shown to work.
    ///     </para>
    /// </remarks>
    public static TransformedText Of(string? source, TextTransform transform, string? language = null) {
        source ??= string.Empty;

        if (transform == TextTransform.None || source.Length == 0) {
            return new TransformedText(source, source, null, null);
        }

        var turkic = IsTurkic(language);
        var lithuanian = IsLithuanian(language);

        var text = new StringBuilder(source.Length);

        // ⚠ A list rather than an array, and that is not a style choice. `sourceOf` is indexed by
        // the *drawn* length, which is not known until the walk is over — an earlier draft sized it
        // from the source and then wrote past the end of the entries it had, which put the last
        // character of an expanded string one index out and nothing else wrong.
        var sourceOf = new List<int>(source.Length + 1);
        var drawnOf = new int[source.Length + 1];
        var moved = false;

        // `capitalize` needs to know where the words are, and UAX#29 is what CSS means by a word.
        // The other two transforms never ask, so the list is not built for them.
        var starts = transform == TextTransform.Capitalize ? WordStarts(source) : null;

        var at = 0;

        while (at < source.Length) {
            // ⚠ A lone surrogate is not a scalar and `Rune` refuses it, but a string can hold one —
            // an editable field mid-keystroke does, between the two halves of an astral character
            // being typed. Copied through untouched rather than replaced, because replacing it
            // would change what the field's own value says the next time it is read back.
            if (!Rune.TryGetRuneAt(source, at, out var rune)) {
                Record(sourceOf, drawnOf, at, 1, text.Length, 1);
                text.Append(source[at]);
                at++;
                continue;
            }

            var length = rune.Utf16SequenceLength;

            // ⚠ SpecialCasing.txt's one Turkic row that consumes two characters, and it is here
            // rather than in `Lower` because it is the only mapping in this walk whose *input* is
            // longer than one scalar. `I` followed by COMBINING DOT ABOVE is a dotted capital
            // written the long way, so it lowercases to a plain `i` and the mark goes with it —
            // leaving the mark behind would put a stray dot over the letter that already has one.
            if (turkic
                && transform == TextTransform.Lowercase
                && rune.Value == 'I'
                && at + 1 < source.Length
                && source[at + 1] == CombiningDotAbove) {
                Record(sourceOf, drawnOf, at, 2, text.Length, 1);
                moved = true;
                text.Append('i');
                at += 2;
                continue;
            }

            // ⚠ SpecialCasing.txt's one *language-independent* conditional row, and the reason
            // Greek lowercased wrongly in every locale rather than in a Greek one. `Rune` answers
            // U+03A3 with σ always, so `ΟΔΟΣ` drew as `οδοσ` where every browser and word processor
            // draws `οδος`. Here rather than in `Lower` because the condition is a lookaround over
            // the *source* string, which a per-rune mapping cannot see — the same reason the Turkic
            // two-character row is here.
            if (transform == TextTransform.Lowercase && rune.Value == GreekCapitalSigma
                                                     && IsFinalSigma(source, at, length)) {
                Record(sourceOf, drawnOf, at, length, text.Length, 1);
                text.Append(GreekFinalSigma);
                at += length;
                continue;
            }

            var mapped = transform switch {
                TextTransform.Uppercase => Upper(rune, turkic),
                TextTransform.Lowercase => Lower(rune, turkic, lithuanian),
                _ => starts!.Contains(at) ? Title(rune, turkic) : rune.ToString()
            };

            Record(sourceOf, drawnOf, at, length, text.Length, mapped.Length);
            moved |= mapped.Length != length;
            text.Append(mapped);
            at += length;
        }

        sourceOf.Add(source.Length);
        drawnOf[^1] = text.Length;

        var drawn = text.ToString();

        return moved
            ? new TransformedText(source, drawn, sourceOf.ToArray(), drawnOf)
            : new TransformedText(source, drawn, null, null);
    }

    /// <summary>Where a source index sits in <see cref="Text" />.</summary>
    /// <param name="index">A UTF-16 index into <see cref="Source" />.</param>
    /// <returns>The matching index into <see cref="Text" />, clamped to it.</returns>
    public int ToDrawn(int index) =>
        drawnOf is null
            ? Math.Clamp(index, 0, Text.Length)
            : drawnOf[Math.Clamp(index, 0, drawnOf.Length - 1)];

    /// <summary>Which source index a drawn index belongs to.</summary>
    /// <param name="index">A UTF-16 index into <see cref="Text" />.</param>
    /// <returns>The matching index into <see cref="Source" />, clamped to it.</returns>
    /// <remarks>
    ///     An index inside a character's expansion comes back as the start of that character: see
    ///     the type's remarks on why the two directions are not inverses.
    /// </remarks>
    public int ToSource(int index) =>
        sourceOf is null
            ? Math.Clamp(index, 0, Source.Length)
            : sourceOf[Math.Clamp(index, 0, sourceOf.Length - 1)];

    /// <summary>Writes one character's worth of both directions of the map.</summary>
    /// <remarks>
    ///     ⚠ <b>Every code unit of the source gets the same drawn index, and every code unit of the
    ///     drawn text gets the same source index.</b> Both are collapses and both are wanted: an
    ///     index between the halves of a surrogate pair, and an index between the two <c>S</c>s of
    ///     an expanded <c>ß</c>, are positions no caret can occupy.
    /// </remarks>
    static void Record(List<int> sourceOf, int[] drawnOf, int source, int sourceLength, int drawn, int drawnLength) {
        for (var i = 0; i < sourceLength; i++) {
            drawnOf[source + i] = drawn;
        }

        for (var i = 0; i < drawnLength; i++) {
            sourceOf.Add(source);
        }
    }

    /// <summary>COMBINING DOT ABOVE, U+0307.</summary>
    const char CombiningDotAbove = '\u0307';

    /// <summary>GREEK CAPITAL LETTER SIGMA, U+03A3.</summary>
    const int GreekCapitalSigma = 0x03A3;

    /// <summary>GREEK SMALL LETTER FINAL SIGMA, U+03C2.</summary>
    const char GreekFinalSigma = '\u03c2';

    /// <summary>Whether a sigma at an offset is the last letter of its word.</summary>
    /// <param name="source">The untransformed text.</param>
    /// <param name="at">Where the sigma starts.</param>
    /// <param name="length">Its length in UTF-16 code units.</param>
    /// <returns>Whether it lowercases to \u03c2 rather than to \u03c3.</returns>
    /// <remarks>
    ///     <para>
    ///         UAX #21's <c>Final_Sigma</c>, verbatim: preceded by a cased letter with only
    ///         case-ignorable characters in between, and <i>not</i> followed by one on the same
    ///         terms. Both halves are needed and the second is the one an implementation forgets \u2014
    ///         without it <c>\u039f\u0394\u039f\u03a3 \u039c\u039f\u03a5</c> would end its first word correctly and <c>\u03a3\u039f\u03a6\u039f\u03a3</c> would
    ///         turn its leading sigma final as well.
    ///     </para>
    ///     <para>
    ///         \u26a0 <b>Read against the source and not against what has been written so far.</b> The
    ///         text ahead has not been transformed yet, so the two are different strings, and the
    ///         condition is defined on the input. Casing does not change whether a character is
    ///         cased or ignorable, so reading backwards from the source is the same answer for less
    ///         bookkeeping.
    ///     </para>
    ///     <para>
    ///         \u26a0 <b>No <c>CultureInfo</c> here either.</b> <c>Cased</c> and <c>Case_Ignorable</c>
    ///         come out of the Unicode general categories and this assembly's own word-break table,
    ///         both of which are the same on every machine \u2014 see the remarks on
    ///         <see cref="Of" />.
    ///     </para>
    /// </remarks>
    static bool IsFinalSigma(string source, int at, int length) =>
        PrecededByCased(source, at) && !FollowedByCased(source, at + length);

    /// <summary>Whether the scalar before an offset, ignoring case-ignorables, is cased.</summary>
    static bool PrecededByCased(string source, int at) {
        while (at > 0) {
            var start = at - 1;

            if (char.IsLowSurrogate(source[start]) && start > 0 && char.IsHighSurrogate(source[start - 1])) {
                start--;
            }

            // A lone surrogate is neither cased nor ignorable, so it ends the walk with "no".
            if (!Rune.TryGetRuneAt(source, start, out var rune) || start + rune.Utf16SequenceLength != at) {
                return false;
            }

            if (!IsCaseIgnorable(rune)) {
                return IsCased(rune);
            }

            at = start;
        }

        return false;
    }

    /// <summary>Whether the scalar after an offset, ignoring case-ignorables, is cased.</summary>
    static bool FollowedByCased(string source, int at) {
        while (at < source.Length) {
            if (!Rune.TryGetRuneAt(source, at, out var rune)) {
                return false;
            }

            if (!IsCaseIgnorable(rune)) {
                return IsCased(rune);
            }

            at += rune.Utf16SequenceLength;
        }

        return false;
    }

    /// <summary>The <c>Cased</c> derived property.</summary>
    /// <remarks>
    ///     Uppercase, lowercase or titlecase. \u26a0 <b>Titlecase is the third one and is a real
    ///     category</b> \u2014 <c>\u01c5</c> is neither <c>Lu</c> nor <c>Ll</c>, so a test written as
    ///     "upper or lower" would read a Latin digraph as uncased and break a sigma's word at it.
    /// </remarks>
    static bool IsCased(Rune rune) =>
        Rune.IsUpper(rune)
        || Rune.IsLower(rune)
        || Rune.GetUnicodeCategory(rune) == UnicodeCategory.TitlecaseLetter;

    /// <summary>The <c>Case_Ignorable</c> derived property.</summary>
    /// <remarks>
    ///     \u26a0 <b>Five categories <i>and</i> three word-break classes</b>, which is DerivedCoreProperties'
    ///     own definition and not a simplification of it. The word-break half is what makes
    ///     <c>\u039c.\u039f.\u03a3.</c> and an apostrophe inside a word behave: a full stop between two letters is
    ///     <c>MidNumLet</c>, so the sigma before it is still followed by a cased letter and stays
    ///     non-final. Dropping that half would be invisible in every fixture written out of one word.
    /// </remarks>
    static bool IsCaseIgnorable(Rune rune) =>
        Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark
            or UnicodeCategory.EnclosingMark
            or UnicodeCategory.Format
            or UnicodeCategory.ModifierLetter
            or UnicodeCategory.ModifierSymbol
        || WordBreakClassTable.Of(rune.Value) is WordBreakClass.MidLetter
            or WordBreakClass.MidNumLet
            or WordBreakClass.SingleQuote;

    /// <summary>LATIN CAPITAL LETTER I WITH DOT ABOVE, U+0130.</summary>
    const string DottedCapitalI = "\u0130";

    /// <summary>LATIN SMALL LETTER DOTLESS I, U+0131.</summary>
    const string DotlessSmallI = "\u0131";

    /// <summary>Whether a language tag selects the Turkish and Azerbaijani case mappings.</summary>
    /// <remarks>
    ///     <para>
    ///         The primary subtag alone, which is what SpecialCasing.txt keys these rows on:
    ///         <c>tr</c> and <c>az</c>, so <c>tr-TR</c>, <c>az-Latn-AZ</c> and a bare <c>tr</c> all
    ///         take them. ⚠ <c>az-Cyrl</c> takes them too and should not, strictly — Cyrillic
    ///         Azerbaijani has no dotless i — but the Unicode data does not distinguish the scripts
    ///         either, and inventing a narrower rule here would disagree with every other
    ///         implementation.
    ///     </para>
    ///     <para>
    ///         ⚠ Ordinal and ASCII-cased rather than <c>StringComparison.CurrentCulture</c>: a
    ///         culture-sensitive comparison of the string <c>"tr"</c> is precisely the joke this
    ///         function exists inside, and in Turkish it is not even a joke — <c>"TR"</c> and
    ///         <c>"tr"</c> compare differently under the very mapping being selected.
    ///     </para>
    /// </remarks>
    /// <param name="language">The BCP-47 tag.</param>
    /// <returns>Whether Turkic casing applies.</returns>
    static bool IsTurkic(string? language) =>
        HasPrimarySubtag(language, "tr") || HasPrimarySubtag(language, "az");

    /// <summary>Whether a BCP-47 tag's primary subtag is a given two-letter code.</summary>
    /// <remarks>
    ///     ⚠ The subtag has to <i>end</i> there and not merely start there, which is the check a
    ///     prefix comparison drops: <c>tra</c> and <c>lto</c> are well-formed tags for other
    ///     languages, and a rule that took them would case them as Turkish and Lithuanian.
    /// </remarks>
    static bool HasPrimarySubtag(string? language, string subtag) {
        if (language is null || language.Length < 2) {
            return false;
        }

        if (language.Length > 2 && language[2] != '-') {
            return false;
        }

        return language.AsSpan(0, 2).Equals(subtag, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The full uppercase mapping, which is not always one code point.</summary>
    static string Upper(Rune rune, bool turkic) =>
        turkic && rune.Value == 'i' ? DottedCapitalI :
        SpecialCasingTable.TryUpper(rune.Value, out var mapping) ? mapping :
        Rune.ToUpperInvariant(rune).ToString();

    /// <summary>The full lowercase mapping.</summary>
    /// <remarks>
    ///     ⚠ The Lithuanian arm is <i>before</i> the table, for the reason the Turkic ones are: these
    ///     three code points also have unconditional rows, and the language-tagged mapping is the one
    ///     that wins where a language says so.
    /// </remarks>
    static string Lower(Rune rune, bool turkic, bool lithuanian) =>
        turkic && rune.Value == 'I' ? DotlessSmallI :
        turkic && rune.Value == 0x0130 ? "i" :
        lithuanian && LithuanianRetainedDot.TryGetValue(rune.Value, out var retained) ? retained :
        SpecialCasingTable.TryLower(rune.Value, out var mapping) ? mapping :
        Rune.ToLowerInvariant(rune).ToString();

    /// <summary>SpecialCasing.txt's three Lithuanian rows that carry a language and no condition.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Lithuanian keeps the dot on a lowercase <c>i</c> when an accent sits above it</b>,
    ///         so <c>Ì</c> lowercases to <c>i</c> + COMBINING DOT ABOVE + COMBINING GRAVE rather than
    ///         to the precomposed <c>ì</c>, whose dot the accent has replaced. Written out as three
    ///         scalars because that is what the mapping is; a font with the precomposed glyph will
    ///         still reach it through normalisation in the shaper, and one without stacks the marks.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>These three needed neither of the two data tables the rest of the Lithuanian set
    ///         is blocked on, which is why they are here alone.</b> The <c>lt</c> rows split into two
    ///         groups that look alike in the file and are not: <c>More_Above</c> (<c>I</c>, <c>J</c>,
    ///         <c>Į</c>) and <c>After_Soft_Dotted</c> (U+0307) are language-tagged <i>and</i>
    ///         context-conditional, and their conditions are questions about combining class 230 and
    ///         the <c>Soft_Dotted</c> property — neither of which any generated table in this
    ///         assembly carries. These three carry a language and nothing else, so they are a lookup.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And landing them alone is not "half a feature", which is the objection that kept
    ///         them out.</b> They are disjoint from the conditional rows: implementing them makes
    ///         these three code points right and leaves <c>I</c>-with-a-mark-above exactly as
    ///         unimproved as it was, rather than newly wrong. What would have been worse than nothing
    ///         is <c>More_Above</c> guessed without the combining classes it is defined on.
    ///     </para>
    /// </remarks>
    static readonly Dictionary<int, string> LithuanianRetainedDot = new() {
        // Ì → i + dot above + grave.
        [0x00CC] = "\u0069\u0307\u0300",

        // Í → i + dot above + acute.
        [0x00CD] = "\u0069\u0307\u0301",

        // Ĩ → i + dot above + tilde.
        [0x0128] = "\u0069\u0307\u0303"
    };

    /// <summary>Whether a language tag selects the Lithuanian case mappings.</summary>
    /// <remarks>
    ///     The primary subtag alone, on <see cref="IsTurkic" />'s terms and for its reasons — the
    ///     comparison is ordinal because a culture-sensitive one is the trap this whole file is about.
    /// </remarks>
    /// <param name="language">The BCP-47 tag.</param>
    /// <returns>Whether Lithuanian casing applies.</returns>
    static bool IsLithuanian(string? language) => HasPrimarySubtag(language, "lt");

    /// <summary>The full titlecase mapping.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Titlecase is a third case and not a synonym for uppercase.</b> Most of where the
    ///         two differ is in <see cref="SpecialCasingTable" /> — the Greek iota-subscript letters,
    ///         which are also the ones that expand. What is not there is the <i>simple</i> titlecase
    ///         column, which lives in UnicodeData.txt and which this generator does not read: .NET
    ///         exposes no equivalent either, because <c>TextInfo.ToTitleCase</c> is a word-by-word
    ///         operation with rules of its own rather than a per-code-point mapping.
    ///     </para>
    ///     <para>
    ///         <see cref="Digraph" /> is the whole of that column that differs from simple uppercase:
    ///         four Latin digraph triples, closed since Unicode 1.1, whose middle member is the
    ///         titlecase of all three. Everything else titlecases exactly as it uppercases, which is
    ///         why the fallback is <see cref="Rune.ToUpperInvariant" /> and not a gap.
    ///     </para>
    /// </remarks>
    static string Title(Rune rune, bool turkic) =>
        turkic && rune.Value == 'i' ? DottedCapitalI :
        SpecialCasingTable.TryTitle(rune.Value, out var mapping) ? mapping :
        Digraph(rune.Value) is { } digraph ? digraph :
        Rune.ToUpperInvariant(rune).ToString();

    /// <summary>The titlecase of a Latin digraph, or null for everything else.</summary>
    /// <remarks>
    ///     <c>Ǆ ǅ ǆ</c>, <c>Ǉ ǈ ǉ</c>, <c>Ǌ ǋ ǌ</c> and <c>Ǳ ǲ ǳ</c>. Each triple is upper, title,
    ///     lower in code point order, so the titlecase of any member is the middle of its own three.
    /// </remarks>
    static string? Digraph(int value) => value switch {
        >= 0x01C4 and <= 0x01CC => char.ToString((char) (value - ((value - 0x01C4) % 3) + 1)),
        >= 0x01F1 and <= 0x01F3 => "ǲ",
        _ => null
    };

    /// <summary>Where each word's first letter is, as indices into the text.</summary>
    /// <remarks>
    ///     <para>
    ///         CSS says the first <i>typographic letter unit</i> of each word, and the two halves of
    ///         that matter separately. UAX#29 says where a word starts; <see cref="Rune.IsLetter" />
    ///         says which character in it is the first letter — so <c>"hello</c> capitalises the
    ///         <c>h</c> and not the quotation mark, which is the whole reason this is not simply
    ///         "the character after a space".
    ///     </para>
    ///     <para>
    ///         ⚠ A digit is not a letter unit, so <c>1st</c> becomes <c>1St</c>. That reads oddly and
    ///         it is what the specification says and what browsers do.
    ///     </para>
    /// </remarks>
    static HashSet<int> WordStarts(string source) {
        var boundaries = new List<int>();
        WordBreaker.Collect(source, boundaries);

        var starts = new HashSet<int>();

        for (var i = 0; i + 1 < boundaries.Count; i++) {
            var from = boundaries[i];
            var to = boundaries[i + 1];

            for (var at = from; at < to;) {
                if (!Rune.TryGetRuneAt(source, at, out var rune)) {
                    at++;
                    continue;
                }

                if (Rune.IsLetter(rune)) {
                    starts.Add(at);
                    break;
                }

                at += rune.Utf16SequenceLength;
            }
        }

        return starts;
    }
}
