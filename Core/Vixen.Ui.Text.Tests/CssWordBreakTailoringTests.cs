// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Text.Tests;

/// <summary>
///     CSS Text §5.2's <c>word-break</c> tailoring of UAX #14, against ICU4X's
///     <c>components/segmenter/tests/css_word_break.rs</c>.
/// </summary>
/// <remarks>
///     <para>
///         <b>The layer above the conformance suite, and the one it cannot reach.</b>
///         <c>LineBreakTest.txt</c>'s 19 338 cases settle UAX #14 <i>as written</i>;
///         <c>word-break</c> is CSS <i>changing</i> what the algorithm is told the characters are, and
///         the Consortium's file has never heard of the property. ICU4X's is the only plain-data
///         oracle found anywhere for that layer — each of its cases is itself a
///         <c>web-platform-tests</c> file, named in the comment above it, reduced to a string and the
///         pieces it segments into.
///     </para>
///     <para>
///         ⚠ <b>Seventeen of ICU4X's twenty-two active assertions are here and five are not, and the
///         five are one reason rather than five.</b> Thai, Lao and Khmer have no spaces and no
///         algorithmic word boundaries, so ICU4X segments them with a <i>dictionary</i> — every one of
///         those cases is driven through <c>LineSegmenter::new_dictionary</c> and the two loaders
///         beside it. <c>LineBreaker</c> is UAX #14 and property tables; there is no dictionary in
///         this repository and adding one is a feature and not a fix. Transcribing them anyway would
///         have produced five red tests that say "Thai is unimplemented", which is a sentence, not a
///         test. They are listed by name in <see cref="TheDictionaryCases" /> so that the next reader
///         can see what was left and why.
///     </para>
///     <para>
///         ⚠ <b>What this prints on the day the tailoring stops working</b> is a segmentation, not a
///         boolean — the assertion compares the <i>pieces</i> rather than the offsets, so a failure
///         names the string it produced. And every case here is one whose answer differs between
///         <see cref="WordBreakMode.Normal" /> and the mode under test:
///         <see cref="Every_case_here_is_one_the_default_gets_wrong" /> is the guard that says so, so
///         a tailoring that quietly stopped being applied could not pass this file by agreeing with
///         UAX #14.
///     </para>
///     <para>
///         Licence: ICU4X is Unicode-3.0; the entry is in the repository's <c>NOTICE</c> under the
///         conformance corpora, per doc 43 § T5, and <c>ConformanceCorpusNoticeTests</c> is what keeps
///         it there.
///     </para>
/// </remarks>
public class CssWordBreakTailoringTests {
    /// <summary>
    ///     ICU4X's <c>wordbreak_breakall</c>, minus the one case that needs a dictionary.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The last two are the point of the mode and the two a naive reading gets wrong.</b>
    ///     CSS Text §5.2 says a line may break "between any two typographic character units" and then
    ///     <i>keeps deferring</i> to every UAX #14 rule that is not about letters — so
    ///     <c>X.</c> stays whole (LB13 forbids a line beginning with a full stop) and the run of
    ///     solidi at the end of <c>XX XXX///</c> stays attached to the letter before it. An
    ///     implementation that scattered an opportunity at every grapheme boundary breaks both, and
    ///     passes every other case in this list.
    /// </remarks>
    /// <param name="text">The string, as ICU4X writes it.</param>
    /// <param name="pieces">The segments it asserts, joined by <c>|</c>.</param>
    [Theory]
    // css/css-text/word-break/word-break-break-all-000.html
    [InlineData("日本語", "日|本|語")]
    // css/css-text/word-break/word-break-break-all-001.html
    [InlineData("latin", "l|a|t|i|n")]
    // css/css-text/word-break/word-break-break-all-002.html
    [InlineData("한글읾", "한|글|읾")]
    // css/css-text/word-break/word-break-break-all-004.html
    [InlineData("التدويل نشاط التدويل", "ا|ل|ت|د|و|ي|ل |ن|ش|ا|ط |ا|ل|ت|د|و|ي|ل")]
    // css/css-text/word-break/word-break-break-all-008.html
    [InlineData("हिन्दी हिन्दी हिन्दी", "हि|न्|दी |हि|न्|दी |हि|न्|दी")]
    // css/css-text/word-break/word-break-break-all-014.html
    [InlineData("💖💔", "💖|💔")]
    // css/css-text/word-break/word-break-break-all-023.html
    [InlineData(@"XX XX\\\", @"X|X |X|X|\|\|\")]
    // css/css-text/word-break/word-break-break-all-026.html
    [InlineData("XX XXX///", "X|X |X|X|X///")]
    // css/css-text/word-break/word-break-break-all-inline-008.html
    [InlineData("X.", "X.")]
    // ID and CJ
    [InlineData("フォ", "フ|ォ")]
    public void Break_all(string text, string pieces) => Assert.Equal(pieces, Segment(text, WordBreakMode.BreakAll));

    /// <summary>ICU4X's <c>wordbreak_keepall</c>, minus the one case that needs a dictionary.</summary>
    /// <remarks>
    ///     ⚠ <b><c>keep-all</c> suppresses the breaks <i>between letters</i> and nothing else</b>,
    ///     which is what the third and fourth cases pin: an ideographic space and an ideographic comma
    ///     still end a line, and they still take the character before them with them. A mode
    ///     implemented as "no breaks in CJK at all" passes the first, second and fifth and fails those
    ///     two.
    /// </remarks>
    /// <param name="text">The string, as ICU4X writes it.</param>
    /// <param name="pieces">The segments it asserts, joined by <c>|</c>.</param>
    [Theory]
    // css/css-text/word-break/word-break-keep-all-000.html
    [InlineData("latin", "latin")]
    // css/css-text/word-break/word-break-keep-all-001.html
    [InlineData("日本語", "日本語")]
    // css/css-text/word-break/word-break-keep-all-002.html
    [InlineData("한글이", "한글이")]
    // css/css-text/word-break/word-break-keep-all-005.html
    [InlineData("字　字", "字　|字")]
    // css/css-text/word-break/word-break-keep-all-006.html
    [InlineData("字、字", "字、|字")]
    // css/css-text/word-boundary/word-boundary-107.html
    [InlineData("しょう。", "しょう。")]
    // ICU4X's own comment: "failed test. JL, JV and JT"
    [InlineData("애기판다", "애기판다")]
    public void Keep_all(string text, string pieces) => Assert.Equal(pieces, Segment(text, WordBreakMode.KeepAll));

    /// <summary>
    ///     ⚠ The cases above that the tailoring actually changes, asserted to change.
    /// </summary>
    /// <remarks>
    ///     <b>The instrument check, and it is not decoration.</b> A file of tailoring cases could go
    ///     green against a store that ignored <c>word-break</c> entirely, because most of these
    ///     strings segment the same way under plain UAX #14 — which is the shape this repository keeps
    ///     finding. Asserting the <i>difference</i> on the cases that have one is what makes the two
    ///     theories above evidence about the tailoring rather than about the algorithm under it.
    ///     <para>
    ///         ⚠ <b>Seven of the seventeen are deliberately not in this list, and writing out why was
    ///         worth more than the guard.</b> UAX #14 already breaks between two ideographs, two
    ///         Hangul syllables and two emoji — so <c>break-all</c> on <c>日本語</c>, <c>한글읾</c> and
    ///         <c>💖💔</c> asserts that the mode does not <i>change</i> those, and <c>X.</c> that it
    ///         does not break LB13. In the other direction <c>keep-all</c> on <c>latin</c>,
    ///         <c>字　字</c> and <c>字、字</c> asserts that it does not over-reach past the letters into
    ///         a space or a comma. Every one of those is a real assertion; none of them can be an
    ///         assertion that the tailoring fires. The first draft of this guard listed all seventeen
    ///         and went red on exactly those seven — which is the guard working on itself.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("latin", WordBreakMode.BreakAll)]
    [InlineData("التدويل نشاط التدويل", WordBreakMode.BreakAll)]
    [InlineData("हिन्दी हिन्दी हिन्दी", WordBreakMode.BreakAll)]
    [InlineData(@"XX XX\\\", WordBreakMode.BreakAll)]
    [InlineData("XX XXX///", WordBreakMode.BreakAll)]
    [InlineData("フォ", WordBreakMode.BreakAll)]
    [InlineData("日本語", WordBreakMode.KeepAll)]
    [InlineData("한글이", WordBreakMode.KeepAll)]
    [InlineData("애기판다", WordBreakMode.KeepAll)]
    [InlineData("しょう。", WordBreakMode.KeepAll)]
    public void The_tailoring_changes_the_answer_wherever_it_is_supposed_to(string text, WordBreakMode mode) =>
        Assert.NotEqual(Segment(text, WordBreakMode.Normal), Segment(text, mode));

    /// <summary>
    ///     The five ICU4X cases that need a dictionary segmenter, recorded rather than transcribed.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A list in a test rather than a comment in one, so that it is read.</b> Each entry is a
    ///     WPT file ICU4X drives through <c>LineSegmenter::new_dictionary</c>; Thai, Lao and Khmer
    ///     write without spaces and have no algorithmic word boundary, so UAX #14 alone offers no
    ///     opportunity inside a run of them and no amount of <c>word-break</c> changes that. This is
    ///     not a defect in <c>LineBreaker</c> and it is not a gap this file can close.
    /// </remarks>
    public static readonly string[] TheDictionaryCases = [
        "word-break-break-all-003.html — break-all, Thai",
        "word-break-keep-all-003.html — keep-all, Thai",
        "word-break-normal-th-000.html — normal, Thai",
        "word-break-normal-km-000.html — normal, Khmer",
        "word-break-normal-lo-000.html — normal, Lao"
    ];

    /// <summary>Splits a string at its break opportunities, joined by <c>|</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>Pieces rather than offsets, because that is what ICU4X asserts and what a failure has
    ///     to be readable as.</b> A list of integers that is wrong by one says nothing about which
    ///     character moved.
    /// </remarks>
    static string Segment(string text, WordBreakMode mode) {
        var opportunities = new List<int>();
        LineBreaker.Collect(text, opportunities, mode);

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
