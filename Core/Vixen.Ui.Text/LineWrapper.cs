// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Ui.Text;

/// <summary>What to do with a word that is wider than the line it has to fit in.</summary>
public enum TextWrapMode : byte {
    /// <summary>Let it overflow. CSS's <c>overflow-wrap: normal</c>, and what prose wants.</summary>
    /// <remarks>
    ///     ⚠ The right default and the wrong one for a user interface built entirely of narrow
    ///     columns. A URL in a sidebar overflows and draws over whatever is beside it, which is why
    ///     the other mode exists rather than being left to the caller to notice.
    /// </remarks>
    Word,

    /// <summary>Break inside the word rather than overflow. CSS's <c>overflow-wrap: anywhere</c>.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ At a <i>grapheme</i> boundary, never a UTF-16 one. Breaking between a base letter and
    ///         its combining mark, or in the middle of a surrogate pair, is not a narrow line — it is
    ///         a line with a broken character on it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And at least one grapheme even when the line has room for none, which is the
    ///         whole of what separates this from <see cref="BreakWord" />.</b> A caller that asks for
    ///         a paragraph's width in <i>no</i> room at all is asking what its min-content size is,
    ///         and CSS Sizing §5.2 makes that one grapheme under <c>anywhere</c>. See
    ///         <see cref="BreakWord" /> for the other half of the same sentence.
    ///     </para>
    /// </remarks>
    Anywhere,

    /// <summary>The same break, but not at zero. CSS's <c>overflow-wrap: break-word</c>.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Identical to <see cref="Anywhere" /> at every width a box can be seen at, and
    ///         different at exactly one: nothing.</b> CSS Text §5.3 and CSS Sizing §5.2 separate the
    ///         two keywords only by their <i>min-content</i> contribution — <c>anywhere</c>'s
    ///         intrinsic minimum shrinks to one grapheme, <c>break-word</c>'s stays the longest
    ///         unbreakable run, and both break an overflowing word at line-layout time.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Which is why the distinction is expressed as behaviour in no room rather than as
    ///         a flag on the call.</b> This engine asks a box for its min-content size by measuring
    ///         it with an available width of zero — that is what <c>LayoutTree</c>'s intrinsic probe
    ///         is — so "the width at which the soft wrap opportunities this keyword introduces stop
    ///         counting" is a faithful rendering of the spec's distinction and not a shortcut around
    ///         it. The cost is a wrong picture in a zero-width box, which draws nothing.
    ///     </para>
    /// </remarks>
    BreakWord
}

/// <summary>How a paragraph chooses <i>which</i> of its legal breaks to take. CSS Text 4's
/// <c>text-wrap-style</c>.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A different question from <see cref="TextWrapMode" />, and the two are easy to
///         confuse because CSS's <c>text-wrap</c> shorthand sets both.</b> That one is about a word
///         that does not fit at all; this one is about a paragraph all of whose breaks are legal and
///         some of which look better than others. Neither can answer the other's question.
///     </para>
///     <para>
///         ⚠ <b>Both non-default values cost extra wraps of the same paragraph, which is why
///         <see cref="Auto" /> is the default and stays greedy first-fit.</b> A user interface
///         reflows on every resize and every keystroke; paying for an optimum on text nobody asked
///         to have balanced is the trade this type exists to let an author opt into rather than the
///         one it imposes.
///     </para>
/// </remarks>
public enum TextWrapStyle : byte {
    /// <summary>Greedy first-fit: every line takes as much as it can hold.</summary>
    Auto,

    /// <summary>
    ///     The same number of lines, as even as they can be made. CSS Text 4's <c>balance</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Balancing must never cost a line, which is the constraint that makes it a search
    ///     rather than an algorithm.</b> A heading broken into three even lines where two would have
    ///     done is not balanced, it is wrong — so the answer is the narrowest width that still wraps
    ///     to the line count the full width gave, found by bisection. That is what browsers do and it
    ///     is why the result is judged by line count and widest line rather than by a raggedness
    ///     score nobody could reproduce.
    /// </remarks>
    Balance,

    /// <summary>No last line with a single word on it. CSS Text 4's <c>pretty</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>Local, and deliberately much less than the specification licenses.</b> CSS Text 4
    ///     leaves <c>pretty</c> to the user agent — better hyphenation, fewer rivers, no short last
    ///     line — and the one clause it names outright is the orphan: a last line holding one word.
    ///     That clause needs no search, only the previous break, so it is what is implemented and
    ///     what is claimed. Implementing the rest silently under the same keyword would be a
    ///     different feature wearing this one's name.
    /// </remarks>
    Pretty
}

/// <summary>One line of a wrapped paragraph, as a range of the source.</summary>
/// <param name="Start">Where it begins in the source text, as a UTF-16 index.</param>
/// <param name="Length">How many characters it covers, including any trailing whitespace.</param>
/// <param name="Advance">
///     How wide it is in design units, <b>not</b> counting whitespace at its end.
/// </param>
/// <param name="Mandatory">Whether the text required the line to end where it did.</param>
/// <remarks>
///     ⚠ <b>A range, not a slice of the shaped glyphs</b>, and that is the decision this whole type
///     rests on. Cutting a shaped paragraph at a break keeps whatever the shaper did across it: a
///     ligature spanning the break survives onto one of the two lines, and a cursively joined script
///     keeps a medial form on a letter that is now final. The only correct fix is to shape each line,
///     and the only thing a caller needs in order to do that is where the line starts and ends.
/// </remarks>
public readonly record struct WrappedLine(int Start, int Length, float Advance, bool Mandatory) {
    /// <summary>One past the line's last character.</summary>
    public int End => Start + Length;

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"[{Start}, {End}) {Advance:0.##}{(Mandatory ? " mandatory" : string.Empty)}"
        );
}

/// <summary>Fills a paragraph's break opportunities into lines of a given width.</summary>
/// <remarks>
///     <para>
///         The half <see cref="LineBreaker" /> deliberately does not do. That one answers "where may
///         a line end", which is a question about Unicode and is judged by a conformance suite that
///         has never heard of a font; this one answers "where does it end", which needs measured
///         widths and cannot be judged that way at all. Keeping them apart is what let the first be
///         gated properly.
///     </para>
///     <para>
///         ⚠ <b>Greedy, first-fit.</b> Knuth–Plass minimises the raggedness of a paragraph as a whole
///         and is what a typesetter wants; a user interface reflows on every resize and every
///         keystroke, and paying for an optimum that changes as fast as it is computed is the wrong
///         trade. It is also what every browser does, so text in a panel wraps where somebody
///         expects it to.
///     </para>
/// </remarks>
public static class LineWrapper {
    /// <summary>Wraps a shaped paragraph to a width.</summary>
    /// <param name="shaped">The shaped paragraph.</param>
    /// <param name="maxAdvance">
    ///     How wide a line may be, in the same design units <see cref="ShapedText.Advance" /> is in.
    /// </param>
    /// <param name="lines">Receives the lines, in order. Cleared first.</param>
    /// <param name="mode">What to do with a word wider than the line.</param>
    /// <param name="wordBreak">Whether a break inside a word is allowed, forbidden, or UAX#14's call.</param>
    /// <param name="indent">How much narrower the first line is. CSS's <c>text-indent</c>.</param>
    /// <param name="tabStop">How far apart the tab stops are, or zero for a tab of no width.</param>
    /// <param name="hyphens">Whether a soft hyphen may end a line. CSS's <c>hyphens</c>.</param>
    /// <param name="style">Which of the legal breaks to prefer. CSS's <c>text-wrap-style</c>.</param>
    public static void Wrap(
        ShapedText shaped,
        float maxAdvance,
        List<WrappedLine> lines,
        TextWrapMode mode = TextWrapMode.Word,
        WordBreakMode wordBreak = WordBreakMode.Normal,
        float indent = 0f,
        float tabStop = 0f,
        HyphenMode hyphens = HyphenMode.Manual,
        TextWrapStyle style = TextWrapStyle.Auto
    ) {
        ArgumentNullException.ThrowIfNull(shaped);

        Wrap(
            shaped.Text,
            Advances(shaped),
            maxAdvance,
            lines,
            mode,
            wordBreak,
            indent,
            tabStop,
            hyphens,
            hyphen: 0f,
            style
        );
    }

    /// <summary>Wraps a paragraph whose widths the caller measured.</summary>
    /// <param name="text">The paragraph.</param>
    /// <param name="advances">One entry per UTF-16 index, as <see cref="Advances" /> builds them.</param>
    /// <param name="maxAdvance">How wide a line may be, in whatever unit the advances are in.</param>
    /// <param name="lines">Receives the lines, in order. Cleared first.</param>
    /// <param name="mode">What to do with a word wider than the line.</param>
    /// <param name="wordBreak">Whether a break inside a word is allowed, forbidden, or UAX#14's call.</param>
    /// <param name="indent">
    ///     How much narrower the <i>first</i> line is than the rest. CSS's <c>text-indent</c>, in the
    ///     same unit as the advances; negative for a hanging indent, which makes the first line the
    ///     wide one.
    /// </param>
    /// <remarks>
    ///     ⚠ <b>For a paragraph that is not in one font.</b> The overload above measures a
    ///     <see cref="ShapedText" />, which is one face by construction — so a line mixing a Latin
    ///     face and a fallback has no single design-unit scale and cannot be measured that way at
    ///     all. Its caller builds the advances in pixels instead, one run at a time, and hands them
    ///     here. Nothing else about wrapping depends on the unit.
    /// </remarks>
    /// <param name="tabStop">
    ///     <para>
    ///         How far apart the tab stops are, in the same unit as the advances. CSS Text 3 § 6.1.
    ///         Zero is a distance rather than a sentinel — a tab at it occupies nothing, which is
    ///         <c>tab-size: 0</c> and is equally the right answer for a paragraph with no tab in it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The one advance here that is not a property of the character.</b> Every width in
    ///         this file is a prefix sum over <paramref name="advances" />, and a tab's is the
    ///         distance to the next stop from wherever the pen has got to — so it cannot be written
    ///         into that array, and every range measurement has to know where its range <i>starts</i>
    ///         on the line. That is the argument the sums below carry an origin for, and it is the
    ///         reason a tab needed the wrapper as well as the line: measured as a glyph, a tabbed
    ///         paragraph breaks in one place and draws in another.
    ///     </para>
    /// </param>
    /// <param name="hyphens">
    ///     <para>
    ///         Whether a soft hyphen may end a line. CSS Text 4 § 6.1's <c>hyphens</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Applied as a filter over the opportunities rather than as a mode inside
    ///         <see cref="LineBreaker" />, and that is deliberate.</b> UAX#14 is right to offer a
    ///         break after U+00AD — the character exists to say so — and the algorithm is judged
    ///         against a Consortium test file that has never heard of a CSS property. What
    ///         <see cref="HyphenMode.None" /> asks for is that a break the algorithm was correct to
    ///         offer is not taken, which is a decision about this paragraph and belongs here.
    ///     </para>
    /// </param>
    /// <param name="style">
    ///     <para>
    ///         Which of the legal breaks to prefer. CSS Text 4's <c>text-wrap-style</c>, and the one
    ///         argument here that changes nothing about <i>where</i> a line may end — only about
    ///         which of the places it may end it does.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Applied as a second pass over the greedy answer</b>, so
    ///         <see cref="TextWrapStyle.Auto" /> costs exactly what this method cost before the
    ///         parameter existed. See <see cref="TextWrapStyle" /> for what each of the other two
    ///         costs and what each one guarantees.
    ///     </para>
    /// </param>
    /// <param name="hyphen">
    ///     <para>
    ///         What a drawn hyphen costs, in the same unit as the advances, for a line that ends on a
    ///         soft one. Zero to break as though it were free.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The second advance here that is not a property of the character, and it is the
    ///         tab's problem wearing a different hat.</b> U+00AD has no entry in
    ///         <paramref name="advances" /> worth anything — the shaper deletes it, which is what
    ///         makes it invisible mid-word — and yet a line that *ends* on one draws a hyphen. So its
    ///         width depends on whether this range is a line end, which a prefix sum cannot say.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Passed in rather than measured here, for the reason the whole overload exists:</b>
    ///         a paragraph in several faces has no single scale, so what a hyphen costs is the
    ///         caller's question. Left at zero the wrapper breaks as though the hyphen were free and
    ///         the line then draws it anyway, overflowing its box by exactly one hyphen.
    ///     </para>
    /// </param>
    public static void Wrap(
        string text,
        ReadOnlySpan<float> advances,
        float maxAdvance,
        List<WrappedLine> lines,
        TextWrapMode mode = TextWrapMode.Word,
        WordBreakMode wordBreak = WordBreakMode.Normal,
        float indent = 0f,
        float tabStop = 0f,
        HyphenMode hyphens = HyphenMode.Manual,
        float hyphen = 0f,
        TextWrapStyle style = TextWrapStyle.Auto
    ) {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(lines);

        // `none` draws no hyphen, so it pays for none. Folded here rather than at each of the six
        // measurement sites, and it also means a caller that passes a width but asks for `none` gets
        // the answer the mode implies rather than the one the argument does.
        if (hyphens == HyphenMode.None) {
            hyphen = 0f;
        }

        lines.Clear();

        if (text.Length == 0) {
            return;
        }

        var opportunities = new List<int>();
        LineBreaker.Collect(text, opportunities, wordBreak);

        // ⚠ <b>`hyphens: none` is a filter over the opportunities and not a mode inside UAX#14</b>,
        // which is the same shape `keep-all` takes and for a different reason. `keep-all` changes
        // what the algorithm thinks the *characters* are; this removes a break the algorithm was
        // right to offer, because the property is about whether the author's mark is honoured rather
        // than about the text. Removing it here also keeps `LineBreaker` judged by the Consortium's
        // test file alone, which has never heard of a CSS property.
        //
        // ⚠ Only where a soft hyphen is what created the opportunity. `"co­-op"` breaks after
        // the ASCII hyphen too, and that break is nothing to do with this property — a filter keyed
        // on "the line would end at index i" rather than on "text[i - 1] is U+00AD" would take both.
        if (hyphens == HyphenMode.None) {
            // ⚠ The opportunity at `text.Length` is left alone whatever precedes it. That one is
            // structural — it is where the paragraph ends, not a place a line may be broken — and
            // removing it when the text happens to finish on a soft hyphen would drop the last line.
            opportunities.RemoveAll(at => at > 0 && at < text.Length && text[at - 1] == '­');
        }

        Greedy(text, advances, maxAdvance, opportunities, lines, mode, indent, tabStop, hyphen);

        // ⚠ <b>Both better-break styles are a SECOND pass over the greedy answer rather than a
        // different first-fit, and that is what keeps `auto` costing exactly what it always did.</b>
        // Neither can be decided while the first line is being filled: `balance` needs the line
        // count the full width produced, which is not known until the paragraph is finished, and
        // `pretty` is a statement about the LAST line. A wrapper that tried to decide either as it
        // went would have to guess.
        switch (style) {
            case TextWrapStyle.Balance:
                Rebalance(text, advances, maxAdvance, opportunities, lines, mode, indent, tabStop, hyphen);
                break;

            case TextWrapStyle.Pretty:
                Unorphan(text, advances, maxAdvance, opportunities, lines, indent, tabStop, hyphen);
                break;
        }
    }

    /// <summary>Greedy first-fit: every line takes as much as it can hold.</summary>
    /// <remarks>
    ///     The body this method holds was <see cref="Wrap(string,System.ReadOnlySpan{float},float,System.Collections.Generic.List{WrappedLine},TextWrapMode,WordBreakMode,float,float,HyphenMode,float,TextWrapStyle)" />'s
    ///     own for the whole of its life, and it moved for one reason: <see cref="TextWrapStyle.Balance" />
    ///     has to run it several times at several widths over the <i>same</i> opportunities, and
    ///     collecting those again per attempt would make a bisection quadratic in the paragraph.
    /// </remarks>
    static void Greedy(
        string text,
        ReadOnlySpan<float> advances,
        float maxAdvance,
        List<int> opportunities,
        List<WrappedLine> lines,
        TextWrapMode mode,
        float indent,
        float tabStop,
        float hyphen
    ) {
        lines.Clear();

        var start = 0;
        var candidate = -1;
        var index = 0;

        // ⚠ <b>The first line is narrower, and the lines after it are not — which is what an indent
        // <i>is</i>, and is the half a caller could not add afterwards.</b> Shifting a finished first
        // line by the indent leaves it wrapped to the wrong width, so it runs past the box's edge by
        // exactly the indent. A hanging indent is the same arithmetic with the sign reversed: the
        // first line is wider than the rest and starts to the left of them.
        var room = maxAdvance - indent;

        // ⚠ <b>Where this line's content begins, measured from the line box's start edge — and the
        // only reason it exists is the tab.</b> Every other advance in this file is the same wherever
        // it sits, so a range's width needed no origin; a tab's is the distance to the next stop, and
        // the stops are laid out from the box's edge. On the first line that edge is an indent away,
        // which is exactly what `TextLine` does with `Offset` — the two arithmetics have to agree or
        // the paragraph breaks in one place and draws in another.
        var origin = indent;

        while (index < opportunities.Count) {
            var here = opportunities[index];

            if (here <= start) {
                index++;
                continue;
            }

            // ⚠ Not at the end of the text, however emphatically UAX#14 says so. LB3 is "always
            // break at end of text", which is right for a conformance suite and is not a break a
            // *line* was forced into — the text simply stopped. Left in, every paragraph's last line
            // comes back marked mandatory, and a paragraph that fits on one line comes back as one
            // mandatory line, which is the opposite of what the flag is for.
            if (here < text.Length && LineBreaker.IsMandatory(text, here)) {
                lines.Add(Line(text, advances, start, here, origin, tabStop, hyphen, mandatory: true));
                start = here;
                room = maxAdvance;
                origin = 0f;
                candidate = -1;
                index++;
                continue;
            }

            if (Width(text, advances, start, here, origin, tabStop, hyphen) <= room) {
                candidate = here;
                index++;
                continue;
            }

            if (candidate > start) {
                // The last opportunity that fitted is where the line ends. The one that did not is
                // reconsidered against the new start rather than skipped — it may well fit now, and a
                // wrapper that dropped it would put two words' worth of text on the next line and
                // then break in the wrong place for the rest of the paragraph.
                lines.Add(Line(text, advances, start, candidate, origin, tabStop, hyphen, mandatory: false));
                start = candidate;
                room = maxAdvance;
                origin = 0f;
                candidate = -1;
                continue;
            }

            // Nothing fits: one unbreakable run is wider than the whole line.
            if (mode != TextWrapMode.Word) {
                var forced = Squeeze(text, advances, start, here, room, origin, tabStop, hyphen);

                // ⚠ <b>One grapheme even when none fits, and only under `anywhere`.</b> This is the
                // single line on which the two breaking keywords differ, and it is the whole of
                // #682. `Squeeze` answers "the last grapheme boundary that fits", which in a room of
                // nothing is `start` — so both keywords used to fall through to the run entire, and
                // a min-content probe (an available width of zero, which is how `LayoutTree` asks
                // the question) came back as the longest word for both. CSS Sizing §5.2 makes it one
                // grapheme for `anywhere`; §5.3 leaves it the word for `break-word`.
                if (forced == start && mode == TextWrapMode.Anywhere) {
                    forced = FirstGrapheme(text, start, here);
                }

                if (forced > start) {
                    lines.Add(Line(text, advances, start, forced, origin, tabStop, hyphen, mandatory: false));
                    start = forced;
                    room = maxAdvance;
                    origin = 0f;
                    continue;
                }
            }

            lines.Add(Line(text, advances, start, here, origin, tabStop, hyphen, mandatory: false));
            start = here;
            room = maxAdvance;
            origin = 0f;
            index++;
        }

        if (start < text.Length) {
            lines.Add(Line(text, advances, start, text.Length, origin, tabStop, hyphen, mandatory: false));
        }
    }

    /// <summary>How many times the balance search halves its interval.</summary>
    /// <remarks>
    ///     ⚠ <b>A count rather than a tolerance, because the thing being searched is not continuous.</b>
    ///     Wrapping is a step function of the width — it changes only where a word crosses the edge —
    ///     so "stop when the interval is smaller than epsilon" is a promise about the wrong axis. Ten
    ///     halvings put the answer within a thousandth of the box's width, which is well inside the
    ///     narrowest step any real font produces, and it costs ten wraps of a paragraph that asked to
    ///     be balanced.
    /// </remarks>
    const int BalanceAttempts = 10;

    /// <summary>The longest paragraph <see cref="TextWrapStyle.Balance" /> will search.</summary>
    /// <remarks>
    ///     ⚠ <b>A cost bound, and it is the only place this file refuses to do what it was asked.</b>
    ///     The search is <see cref="BalanceAttempts" /> wraps of the whole paragraph, and balancing
    ///     is a rule about headings and pull quotes — the keyword on a page of prose asks for eleven
    ///     wraps of a page of prose on every resize, which is a cost nobody asked for and no reader
    ///     would see the benefit of. Browsers give up too, each at a line count of its own. Above
    ///     this the greedy answer stands, which is what <c>auto</c> would have produced.
    /// </remarks>
    const int BalanceLineLimit = 12;

    /// <summary>
    ///     Re-wraps the paragraph as narrow as it will go without costing a line. CSS Text 4's
    ///     <c>balance</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The property being minimised is the widest line, not the raggedness</b>, and the
    ///         two agree on every case anybody writes the keyword for. Minimum raggedness is a sum of
    ///         squares over a set of breaks and needs Knuth–Plass to optimise; the narrowest width
    ///         that still fits in the same number of lines is a bisection over a monotone predicate,
    ///         and for a two- or three-line heading — which is what <c>balance</c> is for — it lands
    ///         on the same breaks. It is also what browsers do.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The acceptance test is not "the same line count" alone, and the second half is
    ///         insurance rather than a case anybody has produced — which is worth saying plainly,
    ///         because a guard nobody can redden is a guard nobody can trust.</b> Dropping
    ///         <c>Widest(attempt) &lt;= widest</c> leaves every test in
    ///         <c>LineWrapTests</c> green, and the argument for why is nearly a proof: every line of
    ///         a trial wrap is capped by the trial width except an unbreakable run, and such a run
    ///         was already that wide in the greedy answer. What the argument does not cover is the
    ///         two widths that depend on <i>which</i> line a range lands on — a tab measures to the
    ///         next stop from where the line's content begins, and an indent narrows the first line
    ///         only — so a range moved between lines can genuinely come out wider. It is one
    ///         comparison per attempt and it is kept for those.
    ///     </para>
    /// </remarks>
    static void Rebalance(
        string text,
        ReadOnlySpan<float> advances,
        float maxAdvance,
        List<int> opportunities,
        List<WrappedLine> lines,
        TextWrapMode mode,
        float indent,
        float tabStop,
        float hyphen
    ) {
        // ⚠ A short circuit rather than a correctness guard, and the sabotage says so: letting a
        // one-line paragraph through leaves every test green, because the search asks for a width
        // that still wraps to ONE line and the only such width is one the whole paragraph fits in —
        // so nothing is accepted and nothing changes. What the clause buys is the ten wraps that
        // would have found that out, on every label in an interface that carries the class.
        if (lines.Count is < 2 or > BalanceLineLimit || maxAdvance <= 0f) {
            return;
        }

        var target = lines.Count;
        var widest = Widest(lines);
        var accepted = -1f;
        var low = 0f;
        var high = maxAdvance;

        var attempt = new List<WrappedLine>(lines.Count);

        for (var i = 0; i < BalanceAttempts; i++) {
            var middle = (low + high) / 2f;
            Greedy(text, advances, middle, opportunities, attempt, mode, indent, tabStop, hyphen);

            if (attempt.Count <= target && Widest(attempt) <= widest) {
                accepted = middle;
                high = middle;
            } else {
                low = middle;
            }
        }

        // ⚠ Re-wrapped at the accepted width rather than keeping the last attempt, because the last
        // attempt is whatever the final halving tried and that one may be the rejected side of the
        // interval. Nothing is written unless a candidate was accepted, so a paragraph that cannot be
        // narrowed at all keeps the greedy answer exactly.
        if (accepted >= 0f) {
            Greedy(text, advances, accepted, opportunities, lines, mode, indent, tabStop, hyphen);
        }
    }

    /// <summary>
    ///     Pulls a word down so that the last line is not one word on its own. CSS Text 4's
    ///     <c>pretty</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Local, and needs no search — which is the whole reason it is a separate style
    ///         from <see cref="TextWrapStyle.Balance" /> rather than a flag on it.</b> The orphan is
    ///         a property of the last two lines only, and the cure is to end the second-to-last line
    ///         one opportunity earlier. Everything above it is untouched, so a hundred-line paragraph
    ///         costs two measurements.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Refused where the cure would overflow, and that refusal is the half worth
    ///         testing.</b> Moving a word down makes the last line longer; if the pair being moved
    ///         does not fit, taking it anyway trades an orphan for a line that runs out of its box —
    ///         which is worse and is the failure mode a naïve orphan fix ships with. Refused equally
    ///         where the penultimate line has no earlier opportunity of its own to end at, since a
    ///         line cannot be emptied to feed the one below it.
    ///     </para>
    /// </remarks>
    static void Unorphan(
        string text,
        ReadOnlySpan<float> advances,
        float maxAdvance,
        List<int> opportunities,
        List<WrappedLine> lines,
        float indent,
        float tabStop,
        float hyphen
    ) {
        if (lines.Count < 2) {
            return;
        }

        var last = lines[^1];
        var previous = lines[^2];

        // ⚠ A line the TEXT ended is not an orphan this property may touch. `pretty` chooses between
        // breaks the wrapper was free to take, and a mandatory break is not one of them: pulling a
        // word across a newline the author wrote would change what the paragraph says.
        if (previous.Mandatory || HasOpportunityInside(opportunities, last.Start, last.End)) {
            return;
        }

        var cut = LastOpportunityInside(opportunities, previous.Start, previous.End);
        if (cut < 0) {
            return;
        }

        // The first line is the one an indent narrows, and the penultimate line is it whenever the
        // paragraph is two lines long — so the origin follows the line's position rather than being
        // assumed to be zero.
        var origin = lines.Count == 2 ? indent : 0f;
        var moved = Line(text, advances, cut, last.End, 0f, tabStop, hyphen, last.Mandatory);

        if (moved.Advance > maxAdvance) {
            return;
        }

        lines[^2] = Line(text, advances, previous.Start, cut, origin, tabStop, hyphen, mandatory: false);
        lines[^1] = moved;
    }

    /// <summary>The widest line of a wrap, which is what balancing minimises.</summary>
    static float Widest(List<WrappedLine> lines) {
        var widest = 0f;

        foreach (var line in lines) {
            widest = MathF.Max(widest, line.Advance);
        }

        return widest;
    }

    /// <summary>Whether a line holds a break the wrapper chose not to take — i.e. more than one word.</summary>
    static bool HasOpportunityInside(List<int> opportunities, int start, int end) {
        foreach (var opportunity in opportunities) {
            if (opportunity > start && opportunity < end) {
                return true;
            }
        }

        return false;
    }

    /// <summary>The last break a line could have ended at instead, or -1 where it had no choice.</summary>
    static int LastOpportunityInside(List<int> opportunities, int start, int end) {
        var found = -1;

        foreach (var opportunity in opportunities) {
            if (opportunity > start && opportunity < end) {
                found = opportunity;
            }
        }

        return found;
    }

    /// <summary>Wraps a shaped paragraph to a width.</summary>
    /// <param name="shaped">The shaped paragraph.</param>
    /// <param name="maxAdvance">How wide a line may be, in design units.</param>
    /// <param name="mode">What to do with a word wider than the line.</param>
    /// <param name="wordBreak">Whether a break inside a word is allowed, forbidden, or UAX#14's call.</param>
    /// <returns>The lines, in order.</returns>
    public static List<WrappedLine> Lines(
        ShapedText shaped,
        float maxAdvance,
        TextWrapMode mode = TextWrapMode.Word,
        WordBreakMode wordBreak = WordBreakMode.Normal
    ) {
        var lines = new List<WrappedLine>();
        Wrap(shaped, maxAdvance, lines, mode, wordBreak);

        return lines;
    }

    /// <summary>The advance of every character, indexed by its position in the source.</summary>
    /// <param name="shaped">The shaped paragraph.</param>
    /// <returns>One entry per UTF-16 index, plus one, in the font's design units.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Accumulated by cluster, not by walking the glyphs in order.</b> A right-to-left run
    ///         hands its glyphs back in visual order, so their clusters <i>descend</i> — a running sum
    ///         over the glyph list measures a bidi paragraph as though it were Latin. What a line's
    ///         width actually is, is the total advance of the characters in it, and that does not
    ///         depend on the order they are drawn in.
    ///     </para>
    ///     <para>
    ///         One entry per UTF-16 index, with a cluster's whole advance recorded at its first
    ///         character and zero at the rest. Summing a range then measures exactly the clusters that
    ///         begin inside it, which is the same set for either direction.
    ///     </para>
    /// </remarks>
    public static float[] Advances(ShapedText shaped) {
        var advances = new float[shaped.Text.Length + 1];

        foreach (var run in shaped.Runs) {
            foreach (var glyph in run.Glyphs) {
                if ((uint) glyph.Cluster < (uint) advances.Length) {
                    advances[glyph.Cluster] += glyph.XAdvance;
                }
            }
        }

        return advances;
    }

    /// <summary>One line, with its trailing whitespace measured out of it.</summary>
    static WrappedLine Line(
        string text,
        ReadOnlySpan<float> advances,
        int start,
        int end,
        float origin,
        float tabStop,
        float hyphen,
        bool mandatory
    ) =>
        new(start, end - start, Width(text, advances, start, end, origin, tabStop, hyphen), mandatory);

    /// <summary>How wide a range is, ignoring whitespace at its end.</summary>
    /// <remarks>
    ///     ⚠ <b>Trailing whitespace does not count towards the width</b>, which is not a nicety. A
    ///     break opportunity falls <i>after</i> a space, so the space belongs to the line before it;
    ///     counting it would mean a line ending in a space wraps a word earlier than one that does
    ///     not, and a right-aligned paragraph would come out with a ragged right edge made of
    ///     invisible characters.
    /// </remarks>
    /// <param name="text">The paragraph.</param>
    /// <param name="advances">One entry per UTF-16 index.</param>
    /// <param name="start">Where the range begins.</param>
    /// <param name="end">One past its last character.</param>
    /// <param name="origin">Where the range begins on the line, for the tab stops to be measured from.</param>
    /// <param name="tabStop">How far apart the stops are, or zero for a tab of no width.</param>
    /// <param name="hyphen">
    ///     What a hyphen costs, for a range that ends on a soft one, or zero when none will be drawn.
    /// </param>
    static float Width(
        string text,
        ReadOnlySpan<float> advances,
        int start,
        int end,
        float origin,
        float tabStop,
        float hyphen
    ) {
        var last = end;

        while (last > start && char.IsWhiteSpace(text[last - 1])) {
            last--;
        }

        var x = origin;

        for (var i = start; i < last; i++) {
            // ⚠ Snapped rather than added, and to the next stop *strictly* after the pen — the same
            // rule `TextLine.NextStop` applies, written twice because the two are different passes
            // over different data. Two tabs in a row are two columns under this rule and one under a
            // "nearest stop at or after" one.
            //
            // ⚠ And a non-positive stop leaves the pen where it is rather than falling back to the
            // character's own advance: a tab is a *space* whose width the stops decide, so with no
            // stops it is a space of no width. That is `tab-size: 0`, and it is also what keeps this
            // agreeing with `TextRun.Place`, which never draws U+0009's glyph whatever the face said.
            x = text[i] == '\t'
                ? tabStop > 0f ? (MathF.Floor(x / tabStop) + 1f) * tabStop : x
                : x + advances[i];
        }

        // ⚠ <b>A second advance that is not a property of the character, and it is the tab's problem
        // wearing a different hat.</b> A soft hyphen has no advance in the array — the shaper deleted
        // it, which is what makes it invisible mid-line — and yet a line that *ends* on one draws a
        // hyphen, because that is what the character is for. So its width depends on whether this
        // range is a line end, which is exactly the thing a prefix sum cannot express.
        //
        // ⚠ Without this the paragraph breaks as though the hyphen were free and then draws it
        // anyway, so a hyphenated line overflows its box by one hyphen. That is not hypothetical: it
        // is what `HyphensTests.The_broken_line_measures_like_a_real_hyphen` caught, and the sizing
        // for this feature missed it — "one character for one" is true of the *indices* and false of
        // the width, since U+00AD measures zero and U+002D does not.
        //
        // ⚠ `end` and not `last`: a range ending in whitespace did not end at the hyphen. And
        // `end < text.Length`, because the last line of a paragraph ends where the text does rather
        // than at a break, so a trailing soft hyphen is never drawn and must not be paid for.
        if (hyphen > 0f && end < text.Length && last == end && last > start && text[last - 1] == '­') {
            x += hyphen;
        }

        return x - origin;
    }

    /// <summary>The end of the first grapheme of a run.</summary>
    /// <param name="text">The source.</param>
    /// <param name="start">Where the run begins.</param>
    /// <param name="end">Where it ends.</param>
    /// <returns>The first boundary after <paramref name="start" />, or it if there is none.</returns>
    /// <remarks>
    ///     ⚠ <b>A grapheme and not a <c>char</c>.</b> Taking one UTF-16 unit would split a surrogate
    ///     pair and put half an emoji on each line — and it would also make a combining sequence's
    ///     min-content size the width of its base letter alone, which is not a width the text can be
    ///     drawn at.
    /// </remarks>
    static int FirstGrapheme(string text, int start, int end) {
        var boundaries = new List<int>();
        GraphemeBreaker.Collect(text.AsSpan(start, end - start), boundaries);

        foreach (var boundary in boundaries) {
            if (start + boundary > start && start + boundary < end) {
                return start + boundary;
            }
        }

        return start;
    }

    /// <summary>
    ///     How much of an over-long word fits, cut at a grapheme boundary rather than anywhere.
    /// </summary>
    /// <returns>Where to break, or <paramref name="start" /> if not even one cluster fits.</returns>
    /// <remarks>
    ///     ⚠ <b>Asking <see cref="GraphemeBreaker" /> is insurance rather than a covered claim, and it
    ///     took a sabotage to find that out.</b> Replacing the boundaries with every UTF-16 index
    ///     changes nothing, and the reason is two files away: <see cref="Advances" /> records a
    ///     cluster's whole advance at its first character and zero at the rest, and the shaper's
    ///     clusters are already reconciled with grapheme clusters. So every cut inside a cluster
    ///     measures exactly the same as the cut at its end, and a rule that takes the <i>largest</i>
    ///     index that fits therefore lands on the end every time. What this insures against is that
    ///     reconciliation going away — the moment one grapheme cluster carries two advances, the
    ///     largest fitting UTF-16 index is a broken character.
    /// </remarks>
    static int Squeeze(
        string text,
        ReadOnlySpan<float> advances,
        int start,
        int end,
        float maxAdvance,
        float origin,
        float tabStop,
        float hyphen
    ) {
        var boundaries = new List<int>();
        GraphemeBreaker.Collect(text.AsSpan(start, end - start), boundaries);

        var fitted = start;

        foreach (var boundary in boundaries) {
            var here = start + boundary;

            if (here <= start || here >= end) {
                continue;
            }

            if (Width(text, advances, start, here, origin, tabStop, hyphen) > maxAdvance) {
                break;
            }

            fitted = here;
        }

        return fitted;
    }
}
