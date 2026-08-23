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
    ///     ⚠ At a <i>grapheme</i> boundary, never a UTF-16 one. Breaking between a base letter and its
    ///     combining mark, or in the middle of a surrogate pair, is not a narrow line — it is a line
    ///     with a broken character on it.
    /// </remarks>
    Anywhere
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
    public static void Wrap(
        ShapedText shaped,
        float maxAdvance,
        List<WrappedLine> lines,
        TextWrapMode mode = TextWrapMode.Word,
        WordBreakMode wordBreak = WordBreakMode.Normal,
        float indent = 0f
    ) {
        ArgumentNullException.ThrowIfNull(shaped);
        Wrap(shaped.Text, Advances(shaped), maxAdvance, lines, mode, wordBreak, indent);
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
    public static void Wrap(
        string text,
        ReadOnlySpan<float> advances,
        float maxAdvance,
        List<WrappedLine> lines,
        TextWrapMode mode = TextWrapMode.Word,
        WordBreakMode wordBreak = WordBreakMode.Normal,
        float indent = 0f
    ) {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(lines);

        lines.Clear();

        if (text.Length == 0) {
            return;
        }

        var opportunities = new List<int>();
        LineBreaker.Collect(text, opportunities, wordBreak);

        var start = 0;
        var candidate = -1;
        var index = 0;

        // ⚠ <b>The first line is narrower, and the lines after it are not — which is what an indent
        // <i>is</i>, and is the half a caller could not add afterwards.</b> Shifting a finished first
        // line by the indent leaves it wrapped to the wrong width, so it runs past the box's edge by
        // exactly the indent. A hanging indent is the same arithmetic with the sign reversed: the
        // first line is wider than the rest and starts to the left of them.
        var room = maxAdvance - indent;

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
                lines.Add(Line(text, advances, start, here, mandatory: true));
                start = here;
                room = maxAdvance;
                candidate = -1;
                index++;
                continue;
            }

            if (Width(text, advances, start, here) <= room) {
                candidate = here;
                index++;
                continue;
            }

            if (candidate > start) {
                // The last opportunity that fitted is where the line ends. The one that did not is
                // reconsidered against the new start rather than skipped — it may well fit now, and a
                // wrapper that dropped it would put two words' worth of text on the next line and
                // then break in the wrong place for the rest of the paragraph.
                lines.Add(Line(text, advances, start, candidate, mandatory: false));
                start = candidate;
                room = maxAdvance;
                candidate = -1;
                continue;
            }

            // Nothing fits: one unbreakable run is wider than the whole line.
            if (mode == TextWrapMode.Anywhere) {
                var forced = Squeeze(text, advances, start, here, room);

                if (forced > start) {
                    lines.Add(Line(text, advances, start, forced, mandatory: false));
                    start = forced;
                    room = maxAdvance;
                    continue;
                }
            }

            lines.Add(Line(text, advances, start, here, mandatory: false));
            start = here;
            room = maxAdvance;
            index++;
        }

        if (start < text.Length) {
            lines.Add(Line(text, advances, start, text.Length, mandatory: false));
        }
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
    static WrappedLine Line(string text, ReadOnlySpan<float> advances, int start, int end, bool mandatory) =>
        new(start, end - start, Width(text, advances, start, end), mandatory);

    /// <summary>How wide a range is, ignoring whitespace at its end.</summary>
    /// <remarks>
    ///     ⚠ <b>Trailing whitespace does not count towards the width</b>, which is not a nicety. A
    ///     break opportunity falls <i>after</i> a space, so the space belongs to the line before it;
    ///     counting it would mean a line ending in a space wraps a word earlier than one that does
    ///     not, and a right-aligned paragraph would come out with a ragged right edge made of
    ///     invisible characters.
    /// </remarks>
    static float Width(string text, ReadOnlySpan<float> advances, int start, int end) {
        var last = end;

        while (last > start && char.IsWhiteSpace(text[last - 1])) {
            last--;
        }

        var total = 0f;

        for (var i = start; i < last; i++) {
            total += advances[i];
        }

        return total;
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
    static int Squeeze(string text, ReadOnlySpan<float> advances, int start, int end, float maxAdvance) {
        var boundaries = new List<int>();
        GraphemeBreaker.Collect(text.AsSpan(start, end - start), boundaries);

        var fitted = start;

        foreach (var boundary in boundaries) {
            var here = start + boundary;

            if (here <= start || here >= end) {
                continue;
            }

            if (Width(text, advances, start, here) > maxAdvance) {
                break;
            }

            fitted = here;
        }

        return fitted;
    }
}
