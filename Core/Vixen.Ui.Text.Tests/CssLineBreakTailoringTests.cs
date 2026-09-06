// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Text.Tests;

/// <summary>
///     CSS Text §5.2's <c>line-break</c> tailoring of UAX #14, against ICU4X's
///     <c>components/segmenter/tests/css_line_break.rs</c>.
/// </summary>
/// <remarks>
///     <para>
///         <b>The sibling of <see cref="CssWordBreakTailoringTests" />, and the half that issue #877
///         had to exist for.</b> <c>LineBreakTest.txt</c>'s 19 338 cases settle UAX #14 as written;
///         these settle what CSS does to it. Each case below is itself a <c>web-platform-tests</c>
///         file, named in the comment above it, reduced to a string and the pieces it segments into.
///     </para>
///     <para>
///         ⚠ <b>Forty-four of ICU4X's seventy-two assertions are here and twenty-eight are not, and
///         the twenty-eight are one reason rather than twenty-eight.</b> ICU4X's helpers take a
///         <c>content_locale</c> and pass <c>ja</c> for the cases whose expectation comes from a
///         Chinese or Japanese <i>document</i> — ICU ships six tailoring files, not four, and
///         <c>line_loose_cj.txt</c> relaxes eight things <c>line_loose.txt</c> does not (a break
///         between an ideograph and a hyphen, before the centred punctuation, before a wide
///         <c>PO</c>, after a wide <c>PR</c>, and before U+301C and U+30A0).
///         <see cref="LineBreakStrictness" /> carries no locale, so those expectations are about a
///         tailoring this store does not have; transcribing them would have produced twenty-eight red
///         tests that say "no content locale", which is a sentence and not a test. They are recorded
///         in <see cref="TheContentLocaleCases" /> and filed as their own issue.
///     </para>
///     <para>
///         ⚠ <b>And the <c>anywhere</c> block is transcribed whole, including the eight cases ICU4X
///         drives with <c>ja</c>.</b> That is not an exception to the paragraph above: ICU4X answers
///         <c>LineBreakStrictness::Anywhere</c> by handing back a <i>grapheme</i> segmenter before it
///         ever looks at the locale, so the flag on those eight is inert by construction rather than
///         by luck.
///     </para>
///     <para>
///         ⚠ <b>What this prints on the day the tailoring stops working</b> is a segmentation and not
///         a boolean, for <see cref="CssWordBreakTailoringTests" />'s reason: a list of integers that
///         is wrong by one says nothing about which character moved. And
///         <see cref="The_tailoring_changes_the_answer_wherever_it_is_supposed_to" /> is the guard
///         that keeps this file evidence about <c>line-break</c> rather than about UAX #14 — most of
///         these strings segment identically under no tailoring at all, so a store that read the
///         property and threw it away would pass the four theories below and fail that one.
///     </para>
///     <para>
///         Licence: ICU4X is Unicode-3.0; the entry is in the repository's <c>NOTICE</c> under the
///         conformance corpora, per doc 43 § T5, and <c>ConformanceCorpusNoticeTests</c> is what keeps
///         it there.
///     </para>
/// </remarks>
public class CssLineBreakTailoringTests {
    /// <summary>ICU4X's <c>linebreak_strict</c>, which needs no content locale at all.</summary>
    /// <remarks>
    ///     Every one of ICU4X's eight active strict cases passes <c>false</c>, because
    ///     <c>line-break: strict</c> in a non-CJK document is ICU's untailored rule set — the file the
    ///     Consortium's own data is written against. The three cases ICU4X has commented out with
    ///     "TODO: Why ID ÷ ID × PR × ID ÷ ID ?" are commented out here too, in
    ///     <see cref="TheContentLocaleCases" />.
    /// </remarks>
    /// <param name="text">The string, as ICU4X writes it.</param>
    /// <param name="pieces">The segments it asserts, joined by <c>|</c>.</param>
    [Theory]
    // css/css-text/line-break/line-break-*-011.xht
    [InlineData("サぁサ", "サぁ|サ")]
    // css/css-text/line-break/line-break-*-012.xht
    [InlineData("サーサ", "サー|サ")]
    // css/css-text/line-break/line-break-*-013.xht
    [InlineData("サ〜サ", "サ〜|サ")]
    // css/css-text/line-break/line-break-*-014.xht
    [InlineData("サ々サ", "サ々|サ")]
    // css/css-text/line-break/line-break-*-015a.xht
    [InlineData("‥‥サ", "‥‥|サ")]
    // css/css-text/line-break/line-break-*-016a.xht
    [InlineData("サ・サ", "サ・|サ")]
    // css/css-text/line-break/line-break-*-017a.xht
    [InlineData("サ°サ", "サ°|サ")]
    // css/css-text/line-break/line-break-*-018.xht
    [InlineData("サ€サ", "サ|€サ")]
    public void Strict(string text, string pieces) =>
        Assert.Equal(pieces, Segment(text, LineBreakStrictness.Strict));

    /// <summary>
    ///     ⚠ And <c>auto</c> answers every one of them identically, which is a decision rather than an
    ///     accident.
    /// </summary>
    /// <remarks>
    ///     <b>The initial value had to be one of the four and CSS declines to say which.</b>
    ///     <c>line-break: auto</c> is "the user agent decides"; ICU's untailored rules, ICU4X's
    ///     <c>LineBreakOptions::default()</c> and every line this store has broken since it had a line
    ///     breaker all resolve <c>CJ</c> to <c>NS</c>, which is <see cref="LineBreakStrictness.Strict" />.
    ///     Asserting it here rather than only in a doc comment is what makes it a promise: an
    ///     <c>auto</c> that quietly drifted to <c>normal</c> would move 19 338 conformance cases and
    ///     every existing wrap, and this is the file that would say so first.
    /// </remarks>
    /// <param name="text">The string, as ICU4X writes it.</param>
    /// <param name="pieces">The segments <see cref="Strict" /> asserts, joined by <c>|</c>.</param>
    [Theory]
    [InlineData("サぁサ", "サぁ|サ")]
    [InlineData("サーサ", "サー|サ")]
    [InlineData("サ〜サ", "サ〜|サ")]
    [InlineData("サ々サ", "サ々|サ")]
    [InlineData("‥‥サ", "‥‥|サ")]
    [InlineData("サ・サ", "サ・|サ")]
    [InlineData("サ°サ", "サ°|サ")]
    [InlineData("サ€サ", "サ|€サ")]
    public void Auto_answers_strict(string text, string pieces) =>
        Assert.Equal(pieces, Segment(text, LineBreakStrictness.Auto));

    /// <summary>ICU4X's <c>linebreak_normal</c>, minus the six cases that need a content locale.</summary>
    /// <remarks>
    ///     ⚠ <b>The first two are the whole of what <c>line_normal.txt</c> says and the last three are
    ///     the guard on it.</b> ICU's non-CJK <c>normal</c> tailoring is one sentence — "it sets
    ///     characters of class <c>CJ</c> to behave like <c>ID</c>" — so a small kana and a prolonged
    ///     sound mark may open a line and <i>nothing else moves</i>. The three <c>文文</c> cases carry
    ///     no <c>CJ</c> at all, so they assert that the tailoring stops where the specification stops:
    ///     an implementation that relaxed <c>NS</c> at large, or that reached the prefix classes,
    ///     passes the first two and fails these.
    /// </remarks>
    /// <param name="text">The string, as ICU4X writes it.</param>
    /// <param name="pieces">The segments it asserts, joined by <c>|</c>.</param>
    [Theory]
    // css/css-text/line-break/line-break-*-011.xht
    [InlineData("サぁサ", "サ|ぁ|サ")]
    // css/css-text/line-break/line-break-*-012.xht
    [InlineData("サーサ", "サ|ー|サ")]
    // css/css-text/i18n/unknown-lang/css-text-line-break-pr-normal.html
    [InlineData("文文±字字", "文|文|±字|字")]
    [InlineData("文文€字字", "文|文|€字|字")]
    [InlineData("文文№字字", "文|文|№字|字")]
    public void Normal(string text, string pieces) =>
        Assert.Equal(pieces, Segment(text, LineBreakStrictness.Normal));

    /// <summary>ICU4X's <c>linebreak_loose</c>, minus the twenty-two that need a content locale.</summary>
    /// <remarks>
    ///     ⚠ <b>Every case here is one <c>loose</c> is asserted <i>not</i> to change, and that is what
    ///     they are worth.</b> The relaxations ICU's <c>line_loose.txt</c> names are three, and none of
    ///     them touches an infix full stop, an inseparable standing alone beside an ideograph, a
    ///     prefix sign or a hyphen at the end of a Latin word. So these eleven pin the tailoring's
    ///     <i>edges</i>: a <c>loose</c> that relaxed <c>IN</c> as a class rather than the
    ///     <c>IN IN</c> pair would break <c>文|‥|文</c> and fail the second, and one that reached the
    ///     prefix classes — which <c>line_loose_cj.txt</c> does and this one does not — would fail the
    ///     four <c>文±文</c> cases.
    ///     <para>
    ///         What <c>loose</c> actually relaxes has no ICU4X case without a locale, and is asserted
    ///         from ICU's own rule file in <see cref="The_relaxations_ICU_names_for_loose" />.
    ///     </para>
    /// </remarks>
    /// <param name="text">The string, as ICU4X writes it.</param>
    /// <param name="pieces">The segments it asserts, joined by <c>|</c>.</param>
    [Theory]
    // css/css-text/i18n/unknown-lang/css-text-line-break-in-loose.html
    [InlineData("文․文", "文․|文")]
    [InlineData("文‥文", "文‥|文")]
    [InlineData("文…文", "文…|文")]
    [InlineData("文⋯文", "文⋯|文")]
    [InlineData("文︙文", "文︙|文")]
    // css/css-text/i18n/unknown-lang/css-text-line-break-pr-loose.html
    [InlineData("文±文", "文|±文")]
    [InlineData("文€文", "文|€文")]
    [InlineData("文№文", "文|№文")]
    [InlineData("文＄文", "文|＄文")]
    // css/css-text/line-break/line-break-loose-hyphens-003.html
    [InlineData("aa‐", "aa‐")]
    [InlineData("aa–", "aa–")]
    public void Loose(string text, string pieces) =>
        Assert.Equal(pieces, Segment(text, LineBreakStrictness.Loose));

    /// <summary>
    ///     The three things <c>loose</c> relaxes, from ICU's <c>line_loose.txt</c> rather than from
    ///     ICU4X's tests.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A second oracle, needed because the first one cannot reach here.</b> ICU4X
    ///         exercises each of these three relaxations only through a case it drives with a
    ///         Japanese content locale — so the eleven cases in <see cref="Loose" /> are all
    ///         <i>negative</i>, and a <c>Loose</c> that was a synonym for <c>Auto</c> would pass every
    ///         one of them. The oracle here is the header of ICU's own non-CJK loose rule file, which
    ///         names its three tailorings in three lines: <c>CJ</c> behaves like <c>ID</c>; a break is
    ///         allowed before the six iteration marks U+3005, U+303B, U+309D, U+309E, U+30FD, U+30FE;
    ///         a break is allowed between two characters of class <c>IN</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ The strings are ICU4X's own, taken from the cases it asserts under
    ///         <c>loose(…, ja)</c> — <c>サ々サ</c> is <c>line-break-*-014.xht</c> and <c>‥‥サ</c> is
    ///         <c>line-break-*-015.xht</c>. What is <i>not</i> borrowed is any expectation that
    ///         depends on the six CJK-only relaxations, which is why <c>サ・サ</c> and <c>サ°サ</c> are
    ///         in <see cref="TheContentLocaleCases" /> and these three are not.
    ///     </para>
    /// </remarks>
    /// <param name="text">The string.</param>
    /// <param name="pieces">What ICU's rule file says it segments into, joined by <c>|</c>.</param>
    [Theory]
    // "It sets characters of class CJ to behave like ID."
    [InlineData("サぁサ", "サ|ぁ|サ")]
    // "allows breaks before iteration marks 3005, 303B, 309D, 309E, 30FD, 30FE (all NS)"
    [InlineData("サ々サ", "サ|々|サ")]
    // "allows breaks between characters of LineBreak class IN"
    [InlineData("‥‥サ", "‥|‥|サ")]
    public void The_relaxations_ICU_names_for_loose(string text, string pieces) =>
        Assert.Equal(pieces, Segment(text, LineBreakStrictness.Loose));

    /// <summary>ICU4X's <c>linebreak_anywhere</c>, whole.</summary>
    /// <remarks>
    ///     ⚠ <b>The one value here that is not a class substitution, and the cheap one.</b> CSS Text 4
    ///     § 5.1 puts a soft wrap opportunity around every typographic character unit "disregarding
    ///     any prohibition against line breaks, even those introduced by characters with the GL, WJ,
    ///     or ZWJ character class" — which the last two cases are exactly about: a word joiner does
    ///     not join and a zero width space does not merge into the stops around it. A store that
    ///     implemented this as <see cref="WordBreakMode.BreakAll" /> keeps LB13 and fails
    ///     <c>aa-a.a)a,a)…</c> on the very first punctuation mark.
    /// </remarks>
    /// <param name="text">The string, as ICU4X writes it.</param>
    /// <param name="pieces">The segments it asserts, joined by <c>|</c>.</param>
    [Theory]
    [InlineData("الخيل والليل", "ا|ل|خ|ي|ل| |و|ا|ل|ل|ي|ل")]
    // css/css-text/line-break/line-break-anywhere-001.html
    [InlineData("aa-a.a)a,a) a aa⁠aa･a", "a|a|-|a|.|a|)|a|,|a|)| |a| |a|a|⁠|a|a|･|a")]
    // css/css-text/line-break/line-break-anywhere-002.html
    [InlineData("no hyphenation", "n|o| |h|y|p|h|e|n|a|t|i|o|n")]
    // css/css-text/line-break/line-break-anywhere-003.html
    [InlineData("latin", "l|a|t|i|n")]
    // css/css-text/line-break/line-break-anywhere-004.html
    [InlineData("XX XXX", "X|X| |X|X|X")]
    // css/css-text/line-break/line-break-anywhere-005.html
    [InlineData("X X", "X| |X")]
    // css/css-text/line-break/line-break-anywhere-006.html
    [InlineData("XXXX XXXX", "X|X|X|X| |X|X|X|X")]
    // css/css-text/line-break/line-break-anywhere-007.html and -008.html
    [InlineData("X XX...", "X| |X|X|.|.|.")]
    // css/css-text/line-break/line-break-anywhere-011.html
    [InlineData("XX///", "X|X|/|/|/")]
    // css/css-text/line-break/line-break-anywhere-012.html
    [InlineData(@"X XX\\\", @"X| |X|X|\|\|\")]
    // css/css-text/line-break/line-break-anywhere-013.html and -016.html
    [InlineData("XXX/X", "X|X|X|/|X")]
    // css/css-text/line-break/line-break-anywhere-014.html and -015.html
    [InlineData(@"XXX\X", @"X|X|X|\|X")]
    // css/css-text/line-break/line-break-anywhere-017.html
    [InlineData("XXXX X", "X|X|X|X| |X")]
    // line-break-anywhere-overrides-uax-behavior-001.htm
    [InlineData("XX⁠XX", "X|X|⁠|X|X")]
    // line-break-anywhere-overrides-uax-behavior-004.htm
    [InlineData("..​...X", ".|.|​|.|.|.|X")]
    public void Anywhere(string text, string pieces) =>
        Assert.Equal(pieces, Segment(text, LineBreakStrictness.Anywhere));

    /// <summary>
    ///     ⚠ <b><c>anywhere</c> beats <c>word-break</c>, which is the one place the two properties
    ///     do not merely compose.</b>
    /// </summary>
    /// <remarks>
    ///     CSS Text 4 § 5.1 spells it out — the opportunity is there "disregarding any prohibition
    ///     against line breaks … or mandated by the <c>word-break</c> property" — so
    ///     <c>keep-all</c> cannot hold a Hangul word together against it. ⚠ ICU4X resolves the same
    ///     collision the other way, because its options match on <c>word_option</c> before
    ///     <c>strictness</c> and never reaches the <c>Anywhere</c> arm; the specification is the
    ///     authority here and the deviation is deliberate rather than unnoticed.
    /// </remarks>
    [Fact]
    public void Anywhere_overrules_keep_all() {
        var opportunities = new List<int>();
        LineBreaker.Collect("한글이", opportunities, WordBreakMode.KeepAll, LineBreakStrictness.Anywhere);

        Assert.Equal([1, 2, 3], opportunities);
    }

    /// <summary>
    ///     ⚠ The cases above whose answer the tailoring actually changes, asserted to change.
    /// </summary>
    /// <remarks>
    ///     <b>The instrument check, and for this property it is the load-bearing test in the file.</b>
    ///     Forty of the forty-four cases transcribed above segment identically under
    ///     <see cref="LineBreakStrictness.Auto" /> — that is what makes them assertions about where
    ///     each tailoring <i>stops</i> — so a <c>LineBreakOf</c> that returned <c>Auto</c> whatever it
    ///     read, or a <c>Collect</c> that took the strictness and dropped it, would pass every theory
    ///     above. Asserting the difference on the cases that have one is what makes this file evidence
    ///     about <c>line-break</c>.
    ///     <para>
    ///         ⚠ <c>Strict</c> contributes nothing to this list and cannot: it <i>is</i> what
    ///         <c>Auto</c> means here, which is the promise <see cref="Auto_answers_strict" /> makes
    ///         from the other side.
    ///     </para>
    /// </remarks>
    /// <param name="text">The string.</param>
    /// <param name="strictness">The tailoring under test.</param>
    [Theory]
    [InlineData("サぁサ", LineBreakStrictness.Normal)]
    [InlineData("サーサ", LineBreakStrictness.Normal)]
    [InlineData("サぁサ", LineBreakStrictness.Loose)]
    [InlineData("サ々サ", LineBreakStrictness.Loose)]
    [InlineData("‥‥サ", LineBreakStrictness.Loose)]
    [InlineData("latin", LineBreakStrictness.Anywhere)]
    [InlineData("XXX/X", LineBreakStrictness.Anywhere)]
    [InlineData("..​...X", LineBreakStrictness.Anywhere)]
    public void The_tailoring_changes_the_answer_wherever_it_is_supposed_to(
        string text,
        LineBreakStrictness strictness
    ) => Assert.NotEqual(Segment(text, LineBreakStrictness.Auto), Segment(text, strictness));

    /// <summary>
    ///     The twenty-eight ICU4X cases that need a content locale, recorded rather than transcribed.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A list in a test rather than a comment in one, so that it is read.</b> Each entry is an
    ///     assertion ICU4X drives with <c>content_locale = ja</c>, which selects ICU's
    ///     <c>line_normal_cj.txt</c> or <c>line_loose_cj.txt</c> instead of the non-CJK file of the
    ///     same strictness. Those two relax things their non-CJK siblings do not — a break before
    ///     U+301C and U+30A0, between an ideograph and a hyphen, before the centred punctuation
    ///     (U+203C, U+2047-9, U+30FB, U+FF1A, U+FF1B, U+FF65, U+FF01, U+FF1F), before a <c>PO</c> of
    ///     East Asian width A/F/W and after a <c>PR</c> of the same — so the expectations are about a
    ///     sixth tailoring that <see cref="LineBreakStrictness" /> cannot name.
    ///     <para>
    ///         This is not a defect in <see cref="LineBreaker" /> and it is not a gap this file can
    ///         close: it needs a language on the call, which is a feature and not a fixture. The three
    ///         entries at the end are ICU4X's own commented-out cases, left out for ICU4X's reason
    ///         rather than for this one.
    ///     </para>
    /// </remarks>
    public static readonly string[] TheContentLocaleCases = [
        "line-break-*-013.xht — normal/loose, ja: U+301C WAVE DASH",
        "line-break-*-014.xht — normal, ja: U+3005 iteration mark",
        "line-break-*-015.xht — normal, ja: U+2025 two dot leader",
        "line-break-*-016a.xht — normal/loose, ja: U+30FB katakana middle dot",
        "line-break-*-017a.xht — normal/loose, ja: U+00B0 degree sign",
        "line-break-*-018.xht — normal/loose, ja: U+20AC and the wide currency prefixes",
        "line-break-*-011.xht — loose, ja: U+3041 small kana",
        "line-break-*-012.xht — loose, ja: U+30FC prolonged sound mark",
        "css-text-line-break-ja-pr-loose.html — loose, ja: U+00B1, U+20AC, U+FF04",
        "css-text-line-break-zh-in-loose.xht — loose, zh: the five inseparables",
        "line-break-loose-hyphens-001.html — loose, ja: U+2010 and U+2013 after an ideograph",
        "css-text-line-break-ja-pr-strict.html — strict, ja: ICU4X's own three commented-out cases"
    ];

    /// <summary>Splits a string at its break opportunities, joined by <c>|</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>Pieces rather than offsets, because that is what ICU4X asserts and what a failure has
    ///     to be readable as</b> — the same helper <see cref="CssWordBreakTailoringTests" /> uses, and
    ///     deliberately not shared with it: each file's helper is the thing that would have to be
    ///     wrong for that file to go quietly green, so the two are kept where they can be read beside
    ///     what they judge.
    /// </remarks>
    static string Segment(string text, LineBreakStrictness strictness) {
        var opportunities = new List<int>();
        LineBreaker.Collect(text, opportunities, WordBreakMode.Normal, strictness);

        List<string> pieces = [];
        var at = 0;

        foreach (var opportunity in opportunities) {
            if (opportunity <= at) {
                continue;
            }

            pieces.Add(text[at..opportunity]);
            at = opportunity;
        }

        if (at < text.Length) {
            pieces.Add(text[at..]);
        }

        return string.Join('|', pieces);
    }
}
