// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Ui.Text;

/// <summary>Whether a line may be broken inside a word. CSS Text 3 § 5.2's <c>word-break</c>.</summary>
/// <remarks>
///     ⚠ <b>Not <see cref="TextWrapMode" /> under another name, however alike the two look.</b> This
///     one changes the <i>set</i> of break opportunities UAX#14 offers, before any width is known;
///     that one decides what to do when none of them is narrow enough. So the two are read at
///     different stages, they compose, and a single enum carrying both would have to pick a winner
///     for <c>word-break: keep-all; overflow-wrap: anywhere</c> — a combination CSS defines and
///     Korean text in a narrow column actually wants.
/// </remarks>
public enum WordBreakMode : byte {
    /// <summary>UAX#14 decides, unaided. CSS's <c>word-break: normal</c>.</summary>
    Normal,

    /// <summary>
    ///     Every letter offers a break. CSS's <c>word-break: break-all</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Implemented by resolving every letter to the ideographic class rather than by
    ///     scattering opportunities over the string, and the difference is every punctuation rule in
    ///     UAX#14.</b> CSS Text 3 § 5.2 allows a break "between any two typographic character units"
    ///     and still defers to the rules that are not about letters, which is a long list: a line may
    ///     not begin with a closing bracket, a comma or an exclamation mark (LB13), and may not end
    ///     with an opening one (LB14). Adding a break at every grapheme boundary breaks both; making
    ///     the letters behave like Chinese — which is exactly what the property is asking for — keeps
    ///     them, because those rules are written against the punctuation classes and go on holding.
    ///     <para>
    ///         ⚠ <b>This list used to end "and may not start with a small kana (LB21)", and that was
    ///         wrong.</b> A small kana is a <c>CJ</c> — a <i>conditional</i> Japanese starter, whose
    ///         class is a question about typographic strictness rather than about punctuation — and
    ///         <c>break-all</c> is not asking about strictness. ICU4X's <c>break_all("フォ")</c>
    ///         segments as <c>フ|ォ</c>; this store answered <c>フォ</c> until
    ///         <c>CssWordBreakTailoringTests</c> transcribed the case. So <c>CJ</c> resolves to
    ///         <c>ID</c> under both tailorings and to <c>NS</c> under neither.
    ///     </para>
    /// </remarks>
    BreakAll,

    /// <summary>
    ///     No break between two letters. CSS's <c>word-break: keep-all</c>.
    /// </summary>
    /// <remarks>
    ///     What it is for is CJK, and Korean above all: <c>LineBreaker</c> offers an opportunity
    ///     between any two Han characters and between any two Hangul syllables, and this suppresses
    ///     both, leaving spaces and punctuation as the only places a line may end.
    ///     <para>
    ///         ⚠ <b>A small kana counts as one of the letters</b>, which is not obvious from the class
    ///         table: <c>CJ</c> resolves to <c>NS</c> by default and <c>NS</c> is not a letter unit, so
    ///         <c>keep-all</c> left a break standing between <c>ょ</c> and the character after it.
    ///         ICU4X's <c>keep_all("しょう。")</c> keeps the whole string together, and it is right —
    ///         a small kana is part of the word beside it whatever a line's strictness says.
    ///     </para>
    /// </remarks>
    KeepAll
}

/// <summary>Whether a soft hyphen may end a line. CSS Text 4 § 6.1's <c>hyphens</c>.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A third enum beside <see cref="WordBreakMode" /> and <c>TextWrapMode</c> for the
///         reason those two are separate from each other</b>: it is a different stage and it
///         composes. <c>word-break</c> decides whether two letters may be parted; this decides
///         whether one <i>character the author put there to say so</i> is honoured, and the two are
///         asked at the same moment about different things.
///     </para>
///     <para>
///         ⚠ <b>There are only two members and CSS has three, which is the gap stated rather than
///         hidden.</b> <c>hyphens: auto</c> needs a per-language Liang pattern set to choose a
///         hyphenation from. No utility class emits it, so it cannot arrive here.
///     </para>
///     <para>
///         ⚠ <b>Half of that refusal's stated reason has expired, and it is written here rather than
///         left standing.</b> This paragraph used to say that a language to choose the pattern set
///         with was missing too — that <see cref="TextShaper" /> left HarfBuzz's language unset on
///         purpose, so the <i>input</i> was missing as well as the algorithm. It is not any more:
///         <c>UiElement.Language</c> and <c>UiElement.ResolvedLanguage</c> carry a BCP-47 tag that
///         inherits by tree, and <c>TextShaper.ShapeRun</c> takes it. The shaper still
///         refuses to read the process locale, which is the property being protected and was never
///         the blocker. What is left is the pattern data alone — 30-100 kB per language, a licensing
///         and a shipping-shape decision, not a missing model.
///     </para>
/// </remarks>
public enum HyphenMode : byte {
    /// <summary>A soft hyphen offers a break and is drawn when one is taken. CSS's <c>manual</c>.</summary>
    /// <remarks>
    ///     The initial value, and what a paragraph nobody styled does. U+00AD is invisible where the
    ///     line does not end and a hyphen where it does, which is the whole point of the character.
    /// </remarks>
    Manual,

    /// <summary>A soft hyphen is inert. CSS's <c>hyphens: none</c>.</summary>
    /// <remarks>
    ///     ⚠ Suppresses the opportunity, not the character. U+00AD is default-ignorable and draws
    ///     nothing wherever it sits, so <c>none</c> has nothing to hide — what it does is refuse to
    ///     *end a line* there, which is what a word that must not be split needs.
    /// </remarks>
    None
}

/// <summary>How strict a line break is. CSS Text 3 § 5.2's <c>line-break</c>.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The property <c>CJ</c> is conditional on, and until this existed the class had no
///         condition to read.</b> A conditional Japanese starter — the small kana, the prolonged
///         sound mark, the iteration marks — is the one line break class UAX#14 declines to resolve
///         on its own: § 6.1 says it resolves to <c>NS</c> or to <c>ID</c> "depending on the desired
///         line breaking strictness", and this is the sentence that says which. Resolving it
///         unconditionally, which is what this store did, is picking one of the two answers and
///         calling it the algorithm.
///     </para>
///     <para>
///         ⚠ <b>Not <see cref="WordBreakMode" /> under another name, and CSS Text § 5.2 puts them
///         side by side for the same reason this file keeps three enums apart.</b> <c>word-break</c>
///         says what counts as a <i>word</i> — whether two letters may be parted — and knows nothing
///         about kana; this says how <i>strict</i> the typography is, which is a question about a
///         small set of characters that are almost all Japanese. They compose, and where they
///         disagree about <c>CJ</c> the word-breaking tailoring wins, because <c>break-all</c> and
///         <c>keep-all</c> are statements about every character and this one is a statement about
///         these.
///     </para>
///     <para>
///         ⚠ <b>No utility class emits any of the four, and that is a decision rather than an
///         omission.</b> Tailwind has no <c>line-break</c> root in v3 or v4, so a family invented for
///         it would be a spelling this store made up — the parity ledger is a comparison with
///         Tailwind and a row nothing outside this repository could write is not a comparison. The
///         property is reachable the way every un-utilitied CSS property is, by writing it in a
///         <c>.vcss</c> rule.
///     </para>
/// </remarks>
public enum LineBreakStrictness : byte {
    /// <summary>The user agent decides. CSS's <c>line-break: auto</c>, and the initial value.</summary>
    /// <remarks>
    ///     ⚠ <b>Deliberately identical to <see cref="Strict" /> rather than to <see cref="Normal" />,
    ///     which is a choice and not an oversight.</b> CSS leaves <c>auto</c> to the implementation;
    ///     ICU's untailored rules and ICU4X's default both resolve <c>CJ</c> to <c>NS</c>, and so did
    ///     every line this store has broken since it had a line breaker. Making the initial value
    ///     mean anything else would have moved 19 338 conformance cases and every existing wrap, to
    ///     buy a preference the specification does not express.
    /// </remarks>
    Auto,

    /// <summary>The least strict. CSS's <c>line-break: loose</c>.</summary>
    /// <remarks>
    ///     What a newspaper column does: short lines want every opportunity they can get. Three
    ///     tailorings, all of them from ICU's <c>line_loose.txt</c> — <c>CJ</c> behaves as
    ///     <c>ID</c>, the six iteration marks stop being <c>NS</c>, and a run of inseparables (an
    ///     ellipsis, a two-dot leader) may be broken inside.
    /// </remarks>
    Loose,

    /// <summary>The common tailoring. CSS's <c>line-break: normal</c>.</summary>
    /// <remarks>
    ///     <c>CJ</c> behaves as <c>ID</c>, so a line may begin with a small kana, and nothing else
    ///     moves. ICU's <c>line_normal.txt</c> for languages other than Chinese and Japanese is this
    ///     sentence and no other.
    /// </remarks>
    Normal,

    /// <summary>The strictest. CSS's <c>line-break: strict</c>.</summary>
    /// <remarks>
    ///     <c>CJ</c> behaves as <c>NS</c>: a small kana may not open a line, which is what Japanese
    ///     typography traditionally asks for and what UAX#14's own default tables assume.
    /// </remarks>
    Strict,

    /// <summary>A break around every typographic character unit. CSS Text 4 § 5.1's <c>anywhere</c>.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Not <see cref="WordBreakMode.BreakAll" /> and not
    ///         <see cref="TextWrapMode.Anywhere" />, and it is the only value here that is not a class
    ///         substitution at all.</b> CSS Text 4 § 5.1 says the opportunity is there "disregarding
    ///         any prohibition against line breaks, even those introduced by characters with the GL,
    ///         WJ, or ZWJ character class or mandated by the <c>word-break</c> property" — so a line
    ///         may begin with a comma, a word joiner does not join, and <c>keep-all</c> is overruled.
    ///         Every other tailoring in this enum keeps UAX#14's rules and changes what the
    ///         characters are; this one keeps the characters and discards the rules.
    ///     </para>
    ///     <para>
    ///         ⚠ Which is why it is implemented as grapheme cluster segmentation, and that is not an
    ///         approximation — "typographic character unit" is the specification's name for an
    ///         extended grapheme cluster, and ICU4X answers <c>line-break: anywhere</c> by handing
    ///         back a grapheme segmenter wearing a line segmenter's coat.
    ///     </para>
    /// </remarks>
    Anywhere
}

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
    public static void Collect(ReadOnlySpan<char> text, List<int> opportunities) =>
        Collect(text, opportunities, WordBreakMode.Normal);

    /// <summary>Collects every line break opportunity in a string, under a <c>word-break</c>.</summary>
    /// <param name="text">The text.</param>
    /// <param name="opportunities">Receives the positions, ascending, ending with the text length.</param>
    /// <param name="mode">Whether breaking inside a word is allowed, forbidden, or left to UAX#14.</param>
    /// <remarks>
    ///     ⚠ <b>This is where <c>word-break</c> belongs and <c>overflow-wrap</c> does not, and the two
    ///     look interchangeable until you write them down.</b> <c>overflow-wrap</c> is a decision the
    ///     line <i>filler</i> takes when nothing fits — see <see cref="TextWrapMode" /> — so the
    ///     opportunity list is the same either way and only the last resort changes.
    ///     <c>word-break</c> changes which breaks exist at all, so a word that <i>would</i> have fitted
    ///     on the next line is still split at the end of this one under <c>break-all</c>. Two
    ///     properties, two stages, and they compose: <c>keep-all</c> with
    ///     <see cref="TextWrapMode.Anywhere" /> suppresses the breaks between CJK characters and still
    ///     squeezes an over-long run, which is what CSS says and what one merged enum could not say.
    /// </remarks>
    public static void Collect(ReadOnlySpan<char> text, List<int> opportunities, WordBreakMode mode) =>
        Collect(text, opportunities, mode, LineBreakStrictness.Auto);

    /// <summary>Collects every line break opportunity, under a <c>word-break</c> and a strictness.</summary>
    /// <param name="text">The text.</param>
    /// <param name="opportunities">Receives the positions, ascending, ending with the text length.</param>
    /// <param name="mode">Whether breaking inside a word is allowed, forbidden, or left to UAX#14.</param>
    /// <param name="strictness">How strict the typography is. CSS's <c>line-break</c>.</param>
    /// <remarks>
    ///     ⚠ <b>Two CSS properties reach the same list and they are asked in this order</b>:
    ///     <see cref="LineBreakStrictness.Anywhere" /> is answered first and alone, because CSS Text 4
    ///     § 5.1 says it disregards the prohibitions <c>word-break</c> mandates; everywhere else the
    ///     two are class substitutions that compose, and where both have an opinion about <c>CJ</c>
    ///     the <c>word-break</c> tailoring wins — see <see cref="LineBreakStrictness" />.
    /// </remarks>
    public static void Collect(
        ReadOnlySpan<char> text,
        List<int> opportunities,
        WordBreakMode mode,
        LineBreakStrictness strictness
    ) =>
        Collect(text, opportunities, mode, strictness, contentLanguage: null);

    /// <summary>
    ///     Collects every line break opportunity, under a <c>word-break</c>, a strictness and the
    ///     language the content is written in.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="opportunities">Receives the positions, ascending, ending with the text length.</param>
    /// <param name="mode">Whether breaking inside a word is allowed, forbidden, or left to UAX#14.</param>
    /// <param name="strictness">How strict the typography is. CSS's <c>line-break</c>.</param>
    /// <param name="contentLanguage">
    ///     <para>
    ///         The language the text is written in, as a BCP-47 tag — <c>UiElement.ResolvedLanguage</c>.
    ///         <see langword="null" /> or empty means undetermined, which is the default and is
    ///         <i>not</i> the machine's locale.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The fifth argument, and it is a <i>document</i> fact rather than a typographic
    ///         preference — which is why it is not another value of
    ///         <see cref="LineBreakStrictness" />.</b> ICU ships <b>six</b> line-breaking rule files
    ///         and not four: <c>line.txt</c>, <c>line_normal.txt</c> and <c>line_loose.txt</c>, each
    ///         with a <c>_cj</c> sibling that relaxes things its non-CJK twin does not. A break
    ///         before U+301C, between an ideograph and a hyphen, before the centred punctuation,
    ///         before a wide suffix sign and after a wide prefix sign are all things a Japanese or
    ///         Chinese column does and an English one must not, whatever <c>line-break</c> says. So
    ///         the tailoring is the product of two axes, and this is the second one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Only <c>ja</c> and <c>zh</c> select it, matching ICU4X's
    ///         <c>LineBreakOptions::content_locale</c> exactly, and <c>ko</c> deliberately does
    ///         not.</b> ICU has no Korean line-breaking file — Korean reads the untailored rules —
    ///         and neither does it have one for <c>yue</c>, <c>wuu</c> or the other Sinitic tags,
    ///         which ICU4X does not map onto <c>zh</c> either. Script and region subtags are ignored,
    ///         so <c>zh-Hant-TW</c> and <c>ja-JP</c> both count: the two <c>_cj</c> files are shared
    ///         by <c>zh-Hans</c>, <c>zh-Hant</c> and <c>ja</c> alike.
    ///     </para>
    /// </param>
    /// <remarks>
    ///     ⚠ <b>An undetermined language is the initial value and answers exactly what this method
    ///     answered before the parameter existed</b>, which is what keeps the Consortium's 19 338
    ///     conformance cases where they were: <c>LineBreakTest.txt</c> is written against
    ///     <c>line.txt</c>, and it is judged with no language and no tailoring. ⚠ Nothing here reads
    ///     <c>CultureInfo.CurrentCulture</c>, for <c>UiElement.ResolvedLanguage</c>'s reason — a
    ///     paragraph that wrapped differently on a Japanese developer's laptop than on CI would
    ///     surface as a golden image red on one machine only.
    /// </remarks>
    public static void Collect(
        ReadOnlySpan<char> text,
        List<int> opportunities,
        WordBreakMode mode,
        LineBreakStrictness strictness,
        string? contentLanguage
    ) {
        ArgumentNullException.ThrowIfNull(opportunities);

        opportunities.Clear();

        if (text.Length == 0) {
            return;
        }

        // CSS Text 4 § 5.1 — `anywhere` is not a tailoring of UAX#14, it is a refusal of it. Every
        // typographic character unit offers a break on both sides, which is the definition of a
        // grapheme cluster boundary; the only edit is dropping the boundary at zero, which LB2 says
        // is not an opportunity and which every caller of this method reads as "the first line is
        // empty".
        if (strictness == LineBreakStrictness.Anywhere) {
            GraphemeBreaker.Collect(text, opportunities);

            if (opportunities.Count > 0 && opportunities[0] == 0) {
                opportunities.RemoveAt(0);
            }

            return;
        }

        var run = LineBreakRun.Resolve(text, mode, strictness, contentLanguage);

        for (var i = 1; i < run.Count; i++) {
            if (!run.ShouldBreak(i)) {
                continue;
            }

            // CSS Text 3 § 5.2 `keep-all` — "breaking is forbidden within words": the *implicit*
            // opportunities between two typographic letter units are suppressed, and nothing else is.
            // A space still breaks, a hyphen still breaks, and a newline is still mandatory, because
            // in all three the class on one side of the position is not a letter.
            if (mode == WordBreakMode.KeepAll && IsLetterUnit(run.ClassAt(i - 1)) && IsLetterUnit(run.ClassAt(i))) {
                continue;
            }

            opportunities.Add(run.OffsetOf(i));
        }

        // LB3 — always break at the end of text.
        opportunities.Add(text.Length);
    }

    /// <summary>
    ///     The classes CSS Text 3 § 5.2 calls typographic letter units, whose implicit opportunities
    ///     <c>keep-all</c> suppresses.
    /// </summary>
    /// <remarks>
    ///     The specification names <c>NU</c>, <c>AL</c>, <c>AI</c> and <c>ID</c>. <c>AI</c> never
    ///     reaches here — <see cref="LineBreakRun" />'s LB1 pass has already resolved it to <c>AL</c>
    ///     — and the four Hangul classes are added because they are what the rule is <i>for</i>: LB26
    ///     and LB27 are the only reason Korean has an opportunity between two syllables in the first
    ///     place, and a <c>keep-all</c> that left them alone would do nothing to the script it is most
    ///     often written for.
    /// </remarks>
    static bool IsLetterUnit(LineBreakClass value) =>
        value is LineBreakClass.AL or LineBreakClass.HL or LineBreakClass.NU or LineBreakClass.ID
            or LineBreakClass.H2 or LineBreakClass.H3 or LineBreakClass.JL or LineBreakClass.JV
            or LineBreakClass.JT;

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

    // ⚠ The first CSS tailoring that could not be expressed as a class substitution, kept on the run
    // so that `ShouldBreak` can read it — and, since the content language arrived, not the last: see
    // `RelaxedBefore`, whose whole set is of this kind.
    // `line-break: loose` allows a break *between* two inseparables —
    // a two-dot leader broken across lines — and LB22 is written as "× IN" with no left-hand side, so
    // there is no class either character could be given that would relax the pair without also
    // relaxing `ID IN`, which loose does not.
    internal LineBreakStrictness Strictness;

    // ⚠ The second tailoring axis, and it is a `bool` rather than the tag it was resolved from
    // because that resolution is a question about a *document* and this is a question about a
    // *pair*. `SelectsCjkTailoring` is asked once per run; asking it per position would compare
    // strings nineteen thousand times over the conformance suite to answer "no" every time.
    internal bool ChineseOrJapanese;

    /// <summary>How many code points there are.</summary>
    public int Count => classes.Count;

    /// <summary>The UTF-16 offset of a code point.</summary>
    /// <param name="index">Its index.</param>
    /// <returns>The offset.</returns>
    public int OffsetOf(int index) => offsets[index];

    /// <summary>The resolved line break class of a code point.</summary>
    /// <param name="index">Its index.</param>
    /// <returns>The class, after LB1, LB9 and LB10.</returns>
    public LineBreakClass ClassAt(int index) => classes[index];

    /// <summary>Decodes and resolves a string.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The resolved run.</returns>
    public static LineBreakRun Resolve(ReadOnlySpan<char> text) => Resolve(text, WordBreakMode.Normal);

    /// <summary>Decodes and resolves a string under a <c>word-break</c>.</summary>
    /// <param name="text">The text.</param>
    /// <param name="mode">Whether breaking inside a word is allowed, forbidden, or left to UAX#14.</param>
    /// <returns>The resolved run.</returns>
    public static LineBreakRun Resolve(ReadOnlySpan<char> text, WordBreakMode mode) =>
        Resolve(text, mode, LineBreakStrictness.Auto);

    /// <summary>Decodes and resolves a string under a <c>word-break</c> and a <c>line-break</c>.</summary>
    /// <param name="text">The text.</param>
    /// <param name="mode">Whether breaking inside a word is allowed, forbidden, or left to UAX#14.</param>
    /// <param name="strictness">How strict the typography is. CSS's <c>line-break</c>.</param>
    /// <returns>The resolved run.</returns>
    public static LineBreakRun Resolve(ReadOnlySpan<char> text, WordBreakMode mode, LineBreakStrictness strictness) =>
        Resolve(text, mode, strictness, contentLanguage: null);

    /// <summary>Decodes and resolves a string under both tailoring axes.</summary>
    /// <param name="text">The text.</param>
    /// <param name="mode">Whether breaking inside a word is allowed, forbidden, or left to UAX#14.</param>
    /// <param name="strictness">How strict the typography is. CSS's <c>line-break</c>.</param>
    /// <param name="contentLanguage">The content language as a BCP-47 tag, or <see langword="null" />.</param>
    /// <returns>The resolved run.</returns>
    public static LineBreakRun Resolve(
        ReadOnlySpan<char> text,
        WordBreakMode mode,
        LineBreakStrictness strictness,
        string? contentLanguage
    ) {
        var run = new LineBreakRun {
            Strictness = strictness,
            ChineseOrJapanese = SelectsCjkTailoring(contentLanguage)
        };

        var position = 0;

        while (position < text.Length) {
            run.offsets.Add(position);

            var codePoint = GraphemeBreaker.Decode(text, ref position);
            run.codePoints.Add(codePoint);
            run.original.Add(Ideographic(Substitute(codePoint, mode, strictness), mode));
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

    /// <summary>Whether a BCP-47 tag selects one of ICU's three <c>_cj</c> rule files.</summary>
    /// <param name="contentLanguage">The tag, or <see langword="null" /> for undetermined.</param>
    /// <returns>Whether the CJK tailoring applies.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>The primary language subtag alone, compared case-insensitively against <c>ja</c>
    ///         and <c>zh</c>.</b> Everything after the first separator is discarded, because the
    ///         three <c>_cj</c> files are shared by <c>zh-Hans</c>, <c>zh-Hant</c> and <c>ja</c>
    ///         alike — ICU names all three in one line of each header — so a script or region subtag
    ///         cannot change the answer.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An empty or absent tag is undetermined and reads the non-CJK files</b>, which is
    ///         the same decision <c>UiElement.ResolvedLanguage</c> takes and for the same reason: the
    ///         alternative is guessing from the process locale, and a paragraph whose line breaks
    ///         depend on the machine is a golden image that is red on one developer's laptop.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both separators, because BCP-47 is written with <c>-</c> and .NET culture names
    ///         are written with either.</b> <c>zh_Hant</c> arrives from a resource file often enough
    ///         that accepting only the hyphen would silently drop the tailoring on it.
    ///     </para>
    /// </remarks>
    static bool SelectsCjkTailoring(string? contentLanguage) {
        if (string.IsNullOrEmpty(contentLanguage)) {
            return false;
        }

        var end = contentLanguage.AsSpan().IndexOfAny('-', '_');
        var primary = end < 0 ? contentLanguage.AsSpan() : contentLanguage.AsSpan(0, end);

        return primary.Equals("ja", StringComparison.OrdinalIgnoreCase)
            || primary.Equals("zh", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>LB1 — the classes that stand for "resolve this some other way".</summary>
    /// <param name="codePoint">The code point.</param>
    /// <param name="mode">
    ///     The <c>word-break</c> in force, which decides what a conditional Japanese starter is.
    /// </param>
    /// <param name="strictness">
    ///     The <c>line-break</c> in force, which is what the class is conditional <i>on</i> where
    ///     <paramref name="mode" /> has no opinion.
    /// </param>
    static LineBreakClass Substitute(int codePoint, WordBreakMode mode, LineBreakStrictness strictness) {
        var value = LineBreakClassTable.Of(codePoint);

        // CSS Text 3 § 5.2 `line-break: loose`, from ICU's `line_loose.txt`: "allows breaks before
        // iteration marks 3005, 303B, 309D, 309E, 30FD, 30FE (all NS)".
        //
        // ⚠ <b>Six code points named one by one rather than a class, because there is no class they
        // are alone in.</b> `NS` holds the closing brackets and the centred punctuation too, and a
        // loose line may still not begin with one of those — ICU expresses this by subtracting the
        // six from `$NS` and putting them in no set at all, which leaves no rule mentioning them.
        // `ID` is the same answer said positively: an ideograph offers a break on both sides and is
        // named by no prohibition either.
        if (strictness == LineBreakStrictness.Loose && IsIterationMark(codePoint)) {
            return LineBreakClass.ID;
        }

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
            //
            // ⚠ <b>And both CSS tailorings override that, in the same direction and for the same
            // reason.</b> `NS` is a class about *typographic strictness* — whether a small kana is
            // allowed to open a line — and `word-break` is not asking about strictness at all: it is
            // saying what counts as a word. Under `break-all` a small kana is a typographic character
            // unit like any other and a line may end before it; under `keep-all` it is part of the
            // word beside it and a line may not. `ID` gives both, because `break-all` already breaks
            // between ideographs and `keep-all`'s letter-unit suppression already covers them —
            // whereas `NS` gives neither, since LB21 forbids the first and `IsLetterUnit` declines
            // the second.
            //
            // ⚠ Found by `CssWordBreakTailoringTests`, transcribed from ICU4X: `break_all("フォ")`
            // segments as `フ|ォ` and `keep_all("しょう。")` stays whole, and this store gave the
            // opposite of each. ⚠ `Normal` is untouched, which is what keeps the Consortium's 19 338
            // cases out of it — they are judged with no tailoring at all.
            // ⚠ And `line-break` is the property the class is *named* for, which arrived after the
            // `word-break` half above and does not displace it: a tailoring that has resolved every
            // letter to `ID` or suppressed every letter break has already answered for the kana, so
            // the strictness is only asked where `word-break` had no opinion.
            LineBreakClass.CJ => mode != WordBreakMode.Normal
                || strictness is LineBreakStrictness.Loose or LineBreakStrictness.Normal
                    ? LineBreakClass.ID
                    : LineBreakClass.NS,
            _ => value
        };
    }

    /// <summary>The six iteration marks CSS's <c>line-break: loose</c> lets a line begin with.</summary>
    /// <remarks>
    ///     U+3005 IDEOGRAPHIC ITERATION MARK, U+303B VERTICAL IDEOGRAPHIC ITERATION MARK, and the two
    ///     hiragana and two katakana iteration marks. All six are <c>NS</c> in the Unicode tables;
    ///     ICU's <c>line_loose.txt</c> subtracts exactly these from <c>$NS</c> and nothing else.
    /// </remarks>
    static bool IsIterationMark(int codePoint) =>
        codePoint is 0x3005 or 0x303B or 0x309D or 0x309E or 0x30FD or 0x30FE;

    /// <summary>
    ///     Whether ICU's <c>_cj</c> rule files allow a break <i>before</i> the code point at
    ///     <paramref name="i" />.
    /// </summary>
    /// <param name="i">The position.</param>
    /// <returns>Whether the CJK tailoring in force relaxes a prohibition there.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A rule tailoring and not a class substitution, which is why it could not join
    ///         <see cref="Substitute" /> the way the iteration marks did.</b> ICU writes each of
    ///         these by <i>subtracting</i> a handful of code points from <c>$NS</c>, <c>$EX</c> or
    ///         <c>$PO</c>, leaving them in no set and therefore in no prohibition — and there is no
    ///         class that means "in no prohibition". Giving them <c>ID</c> comes close and is wrong
    ///         in the two rules <c>ID</c> is itself named in: LB23a would then bind a wide suffix to
    ///         an ideograph again, which is the exact break being relaxed.
    ///     </para>
    ///     <para>
    ///         The three files nest, so this reads as three widening steps: <c>line_cj.txt</c> at
    ///         every strictness, <c>line_normal_cj.txt</c> from <c>normal</c> down, and
    ///         <c>line_loose_cj.txt</c> at <c>loose</c> alone.
    ///     </para>
    /// </remarks>
    bool RelaxedBefore(int i) {
        var codePoint = codePoints[BaseOf(i)];

        // `line_cj.txt`, and inherited by both of the others: "It allows breaking before 201C and
        // after 201D, for zh_Hans, zh_Hant, and ja."
        //
        // ⚠ The one relaxation that reaches `strict` and `auto`, so a Japanese document breaks
        // before an opening quote whatever `line-break` says — and the only thing that makes
        // `line_cj.txt` differ from `line.txt` at all. No ICU4X case covers it, its own strict block
        // passing a non-CJK locale throughout, so the rule file's header is the oracle instead.
        if (codePoint == 0x201C) {
            return true;
        }

        if (Strictness is not (LineBreakStrictness.Normal or LineBreakStrictness.Loose)) {
            return false;
        }

        // `line_normal_cj.txt`: "It allows breaks: * before 301C, 30A0 (both NS)". U+301C WAVE DASH
        // and U+30A0 KATAKANA-HIRAGANA DOUBLE HYPHEN, and this is the whole of what `normal` adds
        // over `strict` in a Japanese document — the iteration marks and the inseparables are
        // `line_loose_cj.txt`'s, which is why `normal("サ々サ", ja)` still keeps the mark attached.
        if (codePoint is 0x301C or 0x30A0) {
            return true;
        }

        if (Strictness != LineBreakStrictness.Loose) {
            return false;
        }

        // `line_loose_cj.txt`: "before some centered punct 203C, 2047, 2048, 2049, 30FB, FF1A,
        // FF1B, FF65 (all NS) and FF01, FF1F (both EX)."
        if (codePoint is 0x203C or 0x2047 or 0x2048 or 0x2049 or 0x30FB or 0xFF1A or 0xFF1B or 0xFF65
            or 0xFF01 or 0xFF1F) {
            return true;
        }

        // `line_loose_cj.txt`: "before suffix characters with LineBreak class PO and EastAsianWidth
        // A,F,W."
        //
        // ⚠ Derived from the two tables rather than transcribed as ICU's ten code points, because
        // that is how ICU derives it too — and a list typed out here would stop agreeing with the
        // generated tables the first time either property moved. `H` is excluded on purpose: the
        // header names A, F and W, and `IsEastAsianWide` includes halfwidth for LB30's sake.
        if (classes[i] == LineBreakClass.PO && IsWideOrAmbiguous(codePoint)) {
            return true;
        }

        // `line_loose_cj.txt`: "between ID and HYPHEN 2010 (as well as the rest of the HH class),
        // and between ID and 2013 EN DASH."
        //
        // ⚠ The one relaxation here that reads both sides, so it is the one that could not have been
        // a set subtraction: an ideograph followed by a hyphen breaks and a Latin word followed by
        // the same hyphen does not, which is what `loose("aa‐", …)` asserts from the other side.
        //
        // ⚠ U+2010 is named by code point *and* by class, because ICU's header does. `HH` is a
        // UAX#14 revision 51 class and which characters it holds is a fact about the generated
        // table's Unicode version, so a rule that read only the class would quietly stop covering
        // the one character ICU names.
        return classes[i - 1] == LineBreakClass.ID
            && (classes[i] == LineBreakClass.HH || codePoint is 0x2010 or 0x2013);
    }

    /// <summary>
    ///     Whether ICU's <c>_cj</c> rule files allow a break <i>after</i> the code point at
    ///     <paramref name="i" />.
    /// </summary>
    /// <param name="i">The position.</param>
    /// <returns>Whether the CJK tailoring in force relaxes a prohibition there.</returns>
    bool RelaxedAfter(int i) {
        var codePoint = codePoints[BaseOf(i)];

        // `line_cj.txt`: "It allows breaking before 201C and after 201D".
        if (codePoint == 0x201D) {
            return true;
        }

        // `line_loose_cj.txt`: "after prefix characters with LineBreak class PR and EastAsianWidth
        // A,F,W." The mirror of the suffix rule above, and the reason `文€文` is three pieces in a
        // Japanese document and two in an undetermined one.
        return Strictness == LineBreakStrictness.Loose
            && classes[i] == LineBreakClass.PR
            && IsWideOrAmbiguous(codePoint);
    }

    /// <summary>The <c>EastAsianWidth</c> values ICU's <c>_cj</c> prefix and suffix rules name.</summary>
    /// <param name="codePoint">The code point.</param>
    /// <returns>Whether its width is <c>A</c>, <c>F</c> or <c>W</c>.</returns>
    static bool IsWideOrAmbiguous(int codePoint) =>
        EastAsianWidthClassTable.Of(codePoint)
            is EastAsianWidthClass.A or EastAsianWidthClass.F or EastAsianWidthClass.W;

    /// <summary>CSS Text 3 § 5.2 <c>break-all</c> — every letter behaves as an ideograph.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Four classes and not "everything", and each of the four is a letter in the sense
    ///         the property means.</b> <c>AL</c> is the Latin, Greek and Cyrillic bucket that
    ///         <see cref="Substitute" /> has already folded <c>AI</c>, <c>SA</c> and the unassigned
    ///         into; <c>HL</c> is Hebrew, which UAX#14 separates only so that LB21a can keep a hyphen
    ///         attached to it; <c>NU</c> is the digits, and folding them is what makes
    ///         <c>break-all</c> split a long number — LB25's numeric prohibitions are written against
    ///         <c>NU</c> and stop applying the moment it is gone, which is the intent rather than a
    ///         casualty.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What is deliberately left alone is every class that is not a letter</b>, which is
    ///         what makes this faithful rather than approximate: the punctuation, the quotes, the
    ///         spaces, the emoji sequences and the regional indicators keep their classes, so every
    ///         rule written against them still fires and <c>break-all</c> still cannot start a line
    ///         with a comma.
    ///     </para>
    /// </remarks>
    static LineBreakClass Ideographic(LineBreakClass value, WordBreakMode mode) =>
        mode == WordBreakMode.BreakAll
        && value is LineBreakClass.AL or LineBreakClass.HL or LineBreakClass.NU
            ? LineBreakClass.ID
            : value;

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

        // CSS Text 3 § 5.2's *second* axis, the content language, from ICU's three `_cj` rule files.
        //
        // ⚠ <b>Asked here rather than as an early `return true`, and the difference is every rule
        // above this line.</b> ICU expresses each of these by taking a code point out of `$NS`,
        // `$EX`, `$PO` or `$PR` — so the prohibitions that survive are the ones written against the
        // classes it is still in, and the ones that vanish are the ones written against the class it
        // left. A relaxation that returned early would also overrule LB6 through LB12a, which name
        // the mandatory breaks, the spaces, the joiners and the glue: `！` taken out of `$EX` is
        // still not a word joiner, and a line still may not begin with a space. So each of the rules
        // below carries the guard instead, exactly as LB22 already carries `line-break: loose`'s.
        var relaxedBefore = ChineseOrJapanese && RelaxedBefore(i);
        var relaxedAfter = ChineseOrJapanese && RelaxedAfter(i - 1);

        // LB13 — never break before closing punctuation or an exclamation.
        if (after is LineBreakClass.CL or LineBreakClass.CP or LineBreakClass.EX or LineBreakClass.SY
            && !relaxedBefore) {
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
            && after == LineBreakClass.NS
            && !relaxedBefore) {
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
        if (after == LineBreakClass.QU && (!IsEastAsian(i - 1) || !IsEastAsian(i + 1)) && !relaxedBefore) {
            return false;
        }

        if (before == LineBreakClass.QU && (!IsEastAsian(i) || i - 2 < 0 || !IsEastAsian(i - 2))
            && !relaxedAfter) {
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
        if (after is LineBreakClass.BA or LineBreakClass.HY or LineBreakClass.HH or LineBreakClass.NS
            && !relaxedBefore) {
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
        //
        // ⚠ Except between two of them under `line-break: loose`, which is ICU's `line_loose.txt`
        // "allows breaks between characters of LineBreak class IN". A two-dot leader may be split
        // across lines in a loose column and an ellipsis after an ideograph may still not be pulled
        // off it, so the relaxation is about the *pair* and not about the class.
        if (after == LineBreakClass.IN
            && !(Strictness == LineBreakStrictness.Loose && before == LineBreakClass.IN)) {
            return false;
        }

        // LB23, LB23a — letters and digits stay together, and so do prefixes and ideographs.
        if (IsLetter(before) && after == LineBreakClass.NU) {
            return false;
        }

        if (before == LineBreakClass.NU && IsLetter(after)) {
            return false;
        }

        if (before == LineBreakClass.PR && after is LineBreakClass.ID or LineBreakClass.EB or LineBreakClass.EM
            && !relaxedAfter) {
            return false;
        }

        if (before is LineBreakClass.ID or LineBreakClass.EB or LineBreakClass.EM && after == LineBreakClass.PO
            && !relaxedBefore) {
            return false;
        }

        // LB24 — currency signs and the letters around them.
        if (before is LineBreakClass.PR or LineBreakClass.PO && IsLetter(after) && !relaxedAfter) {
            return false;
        }

        if (IsLetter(before) && after is LineBreakClass.PR or LineBreakClass.PO && !relaxedBefore) {
            return false;
        }

        // LB25 — numbers. `1,000.00` is one thing, and so is `$1,000.00-`.
        //
        // ⚠ Guarded by both halves, because a wide prefix or suffix taken out of its class takes
        // its numeric pairs with it: `￥100` in a loose Japanese column may break after the yen
        // sign, which is the same subtraction LB23a and LB24 above are reading.
        if (IsNumberJoin(i) && !relaxedBefore && !relaxedAfter) {
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

        if (IsHangul(before) && after == LineBreakClass.PO && !relaxedBefore) {
            return false;
        }

        if (before == LineBreakClass.PR && IsHangul(after) && !relaxedAfter) {
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
