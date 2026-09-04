// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Ui.Layout;
using Vixen.Ui.Text;

namespace Vixen.Ui;

/// <summary>An element's text as the runs it is actually drawn in.</summary>
/// <remarks>
///     <para>
///         <b>One run per face <i>and</i> per bidi level, held in text order and drawn in visual
///         order.</b> Most lines have exactly one, and the code below is written so that a line that
///         does costs no more than the single-run type it replaced. A second run appears when a
///         character is not in the first font — <see cref="FontRegistry" /> hands each grapheme
///         cluster to the first face of the declaration's chain that covers it — or when the text
///         changes direction, and the same shape will carry a rich-text span whose size or weight
///         differs.
///     </para>
///     <para>
///         ⚠ <b><see cref="Runs" /> is logical and <see cref="PenOf" /> is visual, and the two are
///         deliberately not the same order.</b> UAX#9's L2 decides where each run is drawn, and that
///         is applied to the pens alone: everything that walks the runs — the caret, <see cref="Start" />,
///         <see cref="Length" /> — wants them in the order the text is read. A consumer that needs to
///         paint left to right sorts by <see cref="PenOf" />; one that needs to reason about the
///         characters does not have to know reordering happened at all.
///     </para>
///     <para>
///         ⚠ <b>Composition happens in pixels, and that is not an implementation detail.</b> Runs
///         cannot be added up in design units: a 1000-unit face and a 2048-unit face measure an em
///         differently, so an advance from one means nothing beside an advance from the other.
///         Everything here — width, baseline, caret offsets — is in pixels for that reason, and it is
///         why <c>Vixen.Ui.Text</c>'s size-independent <c>ShapedText</c> is deliberately single-font.
///     </para>
/// </remarks>
public sealed class TextLine {
    readonly float[] pens;

    /// <summary>How wide each run is <i>on this line</i>, which is not always its own width.</summary>
    /// <remarks>
    ///     ⚠ Equal to <see cref="TextRun.Width" /> for every run but a tab, whose advance CSS makes
    ///     the distance to the next stop and therefore a fact about where it sits. See
    ///     <see cref="WidthOf" />.
    /// </remarks>
    readonly float[] widths;

    /// <summary>The map between the element's own text and the text the runs were shaped from.</summary>
    /// <remarks>
    ///     Null for the identity, which is every line of text no <c>text-transform</c> touched and
    ///     every line of transformed text whose characters all kept their length.
    /// </remarks>
    readonly TransformedText? transformed;

    /// <summary>Builds a line from its runs.</summary>
    /// <param name="runs">The runs, in text order. At least one.</param>
    /// <param name="width">
    ///     What to report as the line's width, or <see cref="float.NaN" /> for the sum of the runs.
    /// </param>
    /// <param name="offset">
    ///     How far in from the start of the line box the first glyph sits, in pixels.
    ///     <c>text-indent</c>, and zero for every line but a first one.
    /// </param>
    /// <param name="transformed">
    ///     <para>
    ///         What <c>text-transform</c> did to the element's text, or null when it did nothing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This is what keeps every public index on this type in the element's own
    ///         string.</b> The runs are shaped from the <i>transformed</i> text and
    ///         <see cref="TextRun.Start" /> indexes that; <see cref="Start" />,
    ///         <see cref="CaretOffset(int)" /> and <see cref="CaretPositionAt" /> index what the
    ///         author wrote, because that is what <c>TextField</c>'s selection and every caret in
    ///         the tree are expressed in. Where a case mapping expands — <c>straße</c> to
    ///         <c>STRASSE</c> — the two disagree by a character per expansion, and a consumer
    ///         handed the wrong one puts the caret in the wrong place with nothing to see.
    ///     </para>
    /// </param>
    /// <param name="tabStop">
    ///     <para>
    ///         How far apart the tab stops are, in pixels. Zero is a real distance and not a
    ///         sentinel: a tab at it occupies nothing, which is what <c>tab-size: 0</c> asks for and
    ///         is also the right answer for a line with no tab in it, since it has none to occupy
    ///         anything. See <see cref="NextStop" /> for why the two had to be made to coincide.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Measured from the start of the line <i>box</i> and not from the first glyph</b>,
    ///         so <see cref="Offset" /> is inside the arithmetic. An indented paragraph's columns
    ///         line up with the block's stops, which is what CSS specifies and what makes a tabbed
    ///         table under a hanging indent readable.
    ///     </para>
    /// </param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The width is separable from the runs because of trailing whitespace.</b> A break
    ///         opportunity falls *after* a space, so the space belongs to the line before it and is
    ///         drawn there — but it must not count towards the width, or a line ending in a space
    ///         wraps a word earlier than one that does not and a right-aligned paragraph comes out
    ///         ragged with invisible characters. The wrapper already measured that width; this is
    ///         where it arrives.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the offset is separable from the width, which is the distinction
    ///         <c>text-indent</c> turns on.</b> <see cref="Width" /> is how wide the glyphs are and is
    ///         what the alignment subtracts from the content box; <see cref="Offset" /> is where they
    ///         start, and is what every position on this line is measured from. Folding the two
    ///         together would align an indented line as though its text were wider than it is, so a
    ///         centred first line would sit half an indent to the left of where it belongs.
    ///     </para>
    /// </remarks>
    public TextLine(
        ImmutableArray<TextRun> runs,
        float width = float.NaN,
        float offset = 0f,
        TransformedText? transformed = null,
        float tabStop = 0f
    ) {
        if (runs.IsDefaultOrEmpty) {
            throw new ArgumentException("a line has at least one run", nameof(runs));
        }

        Runs = runs;
        Offset = offset;

        // An identity map is stored as no map, so that every line in the tree that never met a
        // transform costs the same null test rather than two array lookups per caret question.
        this.transformed = transformed is { IsIdentity: false } ? transformed : null;
        pens = new float[runs.Length];
        widths = new float[runs.Length];

        var above = 0f;
        var below = 0f;

        for (var i = 0; i < runs.Length; i++) {
            // ⚠ Ascent and descent are taken separately and *both* maximised, rather than the taller
            // run's height being used whole. Runs sit on a shared baseline, so a face with a deep
            // descender and a face with a tall ascender each contribute their own side; taking one
            // run's height would crop the other at whichever end it was larger.
            above = MathF.Max(above, runs[i].Baseline);
            below = MathF.Max(below, runs[i].Height - runs[i].Baseline);
        }

        // ⚠ **The pens are laid down in visual order and stored against the logical index**, which is
        // the whole of this type's bidi handling and the reason `Runs` can stay in text order. UAX#9's
        // L2 decides which run is drawn where; everything else here — the caret walk, `Start`,
        // `Length` — wants the runs in the order they are read, and rewriting the array into visual
        // order would break all of it for a reordering that only the pen arithmetic needs.
        //
        // Reordering runs is sound because each run has one level throughout: see `TextRun.Level`,
        // and see `UiElement.Runs`, which is what has to cut on level as well as on face to make that
        // true. A run whose level was taken from its first character would be reordered as a unit and
        // would carry its own neutrals to the wrong end of the line.
        //
        // Free for the overwhelmingly common line — `VisualOrder` of a single level is the identity,
        // and one run of level 0 is every label in an interface.
        //
        // ⚠ <b>And this loop is where a tab gets its width, because it is the first place that knows
        // where a run starts.</b> `TextRun.Width` is `Shaped.Advance * Scale`, which for a tab is
        // whatever glyph the face mapped U+0009 to — usually .notdef. CSS Text 3 § 6.1 says the
        // advance is the distance to the next stop instead, measured from the start of the *line
        // box*, so `Offset` is inside the arithmetic: an indented first line's stops are where the
        // block's are and not where its glyphs begin. Getting that wrong puts every tabbed line
        // after an indent a fraction of a stop out, which reads as a wobbly column.
        var pen = 0f;

        foreach (var index in Order(runs)) {
            pens[index] = pen;
            widths[index] = runs[index].IsTab
                ? NextStop(offset + pen, tabStop) - (offset + pen)
                : runs[index].Width;

            pen += widths[index];
        }

        Width = float.IsNaN(width) ? pen : width;
        Baseline = above;
        Height = above + below;

        Start = ToSource(runs[0].Start);
        Length = ToSource(runs[^1].Start + runs[^1].Shaped.Text.Length) - Start;
    }

    /// <summary>Turns an index into the shaped text into one into the element's own.</summary>
    int ToSource(int index) => transformed?.ToSource(index) ?? index;

    /// <summary>Turns an index into the element's own text into one into the shaped text.</summary>
    int ToDrawn(int index) => transformed?.ToDrawn(index) ?? index;

    /// <summary>The runs' indices in the order they are drawn, left to right.</summary>
    /// <remarks>
    ///     ⚠ <b>Delegated to <see cref="TextItemizer.VisualOrder(ReadOnlySpan{int})" /> rather than
    ///     written here.</b> L2 is four lines and they are four lines this repository already has,
    ///     conformance-tested against 91 707 of the Consortium's cases through the itemiser. A second
    ///     copy would be a second thing to get right about the one rule whose being wrong produces a
    ///     picture that looks fine to anyone who does not read the script.
    /// </remarks>
    static int[] Order(ImmutableArray<TextRun> runs) {
        var levels = new int[runs.Length];

        for (var i = 0; i < levels.Length; i++) {
            levels[i] = runs[i].Level;
        }

        return TextItemizer.VisualOrder(levels);
    }

    /// <summary>Where this line's text begins in the element's, as a UTF-16 index.</summary>
    /// <remarks>
    ///     ⚠ <b>In the element's own text and not in the text the runs were shaped from</b>, which
    ///     are the same string unless a <c>text-transform</c> expanded something. See the
    ///     <c>transformed</c> parameter of the constructor.
    /// </remarks>
    public int Start { get; }

    /// <summary>How many UTF-16 units it covers.</summary>
    /// <remarks>
    ///     ⚠ Measured from the last run's end rather than by adding the runs up, which is the same
    ///     number and stays the same number if a run is ever allowed to cover a range the one before
    ///     it did not end at — a rich-text span with something dropped between two of them.
    /// </remarks>
    public int Length { get; }

    /// <summary>The runs, in text order.</summary>
    public ImmutableArray<TextRun> Runs { get; }

    /// <summary>How wide the line is, in pixels, not counting whitespace at its end.</summary>
    /// <remarks>
    ///     ⚠ <b>The glyphs' width, and it does <i>not</i> include <see cref="Offset" />.</b> A caller
    ///     aligning the line wants how much room the text takes; a caller measuring the block wants
    ///     <c>Offset + Width</c>, which is what <see cref="TextLayout.Width" /> maximises over.
    /// </remarks>
    public float Width { get; }

    /// <summary>Where the first glyph sits, in pixels from the start of the line box.</summary>
    /// <remarks>
    ///     <c>text-indent</c> on the first line of a block, and zero everywhere else. It is already
    ///     inside <see cref="PenOf" />, <see cref="Place" />, <see cref="CaretOffset(int)" /> and
    ///     <see cref="CaretIndexAt" />, so a consumer that goes through any of those needs to know
    ///     nothing about it — which is deliberate, and is the reason the caret cannot land a
    ///     character out on an indented line. A negative value is CSS's hanging indent and needs
    ///     nothing extra anywhere.
    /// </remarks>
    public float Offset { get; }

    /// <summary>How tall it is, in pixels.</summary>
    public float Height { get; }

    /// <summary>How far below the top of the line its shared baseline sits, in pixels.</summary>
    public float Baseline { get; }

    /// <summary>Where a run begins, in pixels from the start of the line box.</summary>
    /// <param name="run">The run's index in <see cref="Runs" />.</param>
    /// <remarks><see cref="Offset" /> is included, so this is where the glyphs actually go.</remarks>
    public float PenOf(int run) => Offset + pens[run];

    /// <summary>How wide a run is on this line, in pixels.</summary>
    /// <param name="run">The run's index in <see cref="Runs" />.</param>
    /// <remarks>
    ///     ⚠ <b>Read this and not <see cref="TextRun.Width" /> wherever a run's extent is wanted.</b>
    ///     They are the same number for everything but a tab, whose advance is the distance to the
    ///     next stop and therefore depends on where the run begins — which a run cannot know and a
    ///     line can. The two agreeing everywhere else is what makes the difference easy to miss: a
    ///     consumer that reads the run's own width is correct on every line without a tab in it.
    /// </remarks>
    public float WidthOf(int run) => widths[run];

    /// <summary>The next tab stop after a position, or the position itself when there are none.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Strictly after, never at.</b> A tab that begins exactly on a stop advances to the
    ///         next one — CSS Text 3 § 6.1, and the only reading under which two tabs in a row are
    ///         two columns rather than one. A rule that snapped to the nearest stop at or after the
    ///         pen would make the second tab of a pair zero wide, which looks like the tab was
    ///         dropped.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A stop of zero or less means a tab occupies nothing, and that is a value rather
    ///         than a sentinel.</b> It is what <c>tab-size: 0</c> asks for, and it is also the only
    ///         answer consistent with <see cref="TextRun.Place" />, which suppresses U+0009's glyph
    ///         unconditionally: measuring the tab as whatever the face mapped it to would reserve the
    ///         width of a .notdef box and then draw nothing in it. An earlier arrangement here read
    ///         a non-positive stop as "measure it as a glyph", which made <c>tab-size: 0</c> and
    ///         "this line has no tabs" the same number and gave the first of them invisible width.
    ///     </para>
    /// </remarks>
    static float NextStop(float x, float stop) => stop > 0f ? (MathF.Floor(x / stop) + 1f) * stop : x;

    /// <summary>Places every glyph of every run relative to the start of the line.</summary>
    /// <param name="into">Where to put them.</param>
    /// <remarks>
    ///     ⚠ <b>A drawing consumer wants the runs one at a time instead.</b> A draw command names one
    ///     font, so a mixed line is several commands and each needs its own glyphs — this flattens
    ///     them into one list, which is right for measuring and for a test and wrong for a batch. See
    ///     <c>DrawListBuilder.EmitText</c>, which walks <see cref="Runs" /> and calls
    ///     <see cref="TextRun.Place" /> with <see cref="PenOf" />.
    /// </remarks>
    public void Place(List<PositionedGlyph> into) {
        ArgumentNullException.ThrowIfNull(into);

        for (var i = 0; i < Runs.Length; i++) {
            Runs[i].Place(into, PenOf(i));
        }
    }

    /// <summary>Where a caret sits, in pixels from the start of the line.</summary>
    /// <param name="index">A UTF-16 index into the element's text.</param>
    /// <returns>The distance from the start of the line.</returns>
    /// <remarks>
    ///     ⚠ <b>An index on a run boundary belongs to the run that ends there</b>, which is the
    ///     upstream reading and is what this overload keeps answering. Where the two runs face the
    ///     same way both answers are the same pixel and the choice only shows in which font's
    ///     metrics decide; where they face opposite ways they are at opposite ends of a run, and
    ///     <see cref="CaretOffset(int, CaretAffinity)" /> is how a caller says which it meant.
    /// </remarks>
    public float CaretOffset(int index) => CaretOffset(index, CaretAffinity.Upstream);

    /// <summary>Where a caret sits, given which side of the index it belongs to.</summary>
    /// <param name="index">A UTF-16 index into the element's text.</param>
    /// <param name="affinity">Which of the two characters either side of the index it belongs to.</param>
    /// <returns>The distance from the start of the line.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The affinity is asked twice on the way down and answers two different questions.</b>
    ///         Here it decides <i>which run</i> an index on a run boundary belongs to; inside the run
    ///         it decides which cluster. They agree in direction — downstream always means the
    ///         character after the index — which is why one bit carries both.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Runs are walked in logical order and the pens are visual</b>, so "the next run"
    ///         is the next one <i>logically</i> and may be drawn to the left. That is the point: at a
    ///         direction change the downstream answer is supposed to be somewhere else entirely.
    ///     </para>
    /// </remarks>
    public float CaretOffset(int index, CaretAffinity affinity) {
        // ⚠ Translated once, at the top, and everything below is in the shaped text's indices. The
        // runs were shaped from the transformed string and know nothing else; converting inside the
        // loop instead would compare an untransformed index against a transformed run boundary and
        // pick the wrong run on any line holding an expansion.
        index = ToDrawn(index);

        for (var i = 0; i < Runs.Length; i++) {
            var run = Runs[i];
            var end = run.Start + run.Shaped.Text.Length;

            // A downstream caret on a run boundary belongs to the character *after* it, which is the
            // first character of the next run. Falling through to the earlier run would make the two
            // affinities the same answer and quietly undo the distinction one line down.
            if (affinity == CaretAffinity.Downstream && index == end && i + 1 < Runs.Length) {
                continue;
            }

            if (index <= end || i == Runs.Length - 1) {
                // ⚠ A tab has two caret positions and no interior, and the run cannot answer either
                // of them: `TextRun.CaretOffset` would return the .notdef glyph's advance, which is
                // not this tab's width on this line. Before it or after it, and after it is the next
                // stop — which is what puts the caret where the next column starts.
                return run.IsTab
                    ? PenOf(i) + (index >= end ? widths[i] : 0f)
                    : PenOf(i) + run.CaretOffset(index, affinity);
            }
        }

        return Offset + Width;
    }

    /// <summary>The stretches of the line a logical span covers, left to right.</summary>
    /// <param name="from">Where the span begins, as a UTF-16 index into the element's text.</param>
    /// <param name="to">Where it ends. Either order; the two are sorted.</param>
    /// <param name="into">Where to put them, as (left edge, width) pairs in pixels.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A logically contiguous span is not a visually contiguous one, and painting it as
    ///         one rectangle from the lower offset to the higher is wrong by whole runs.</b> Select
    ///         <c>bc</c> and the first Arabic letter of <c>abcلسان</c> and the covered glyphs are the
    ///         end of the Latin run and the <i>far</i> end of the Arabic one — with the rest of the
    ///         Arabic, which is not selected, sitting between them. One rectangle over that span
    ///         highlights text the user did not select and, since the two ends can arrive in either
    ///         order, can just as easily paint a band that covers none of it.
    ///     </para>
    ///     <para>
    ///         This is the same rule <c>Vixen.Ui.Text</c>'s README states for cutting runs, applied
    ///         to a highlight: intersect the span with the itemiser's boundaries <i>before</i>
    ///         reordering. Each run carries one level throughout — see <see cref="TextRun.Level" /> —
    ///         so the intersection with a run is one interval of x, and the ends only have to be
    ///         sorted because an interval in a right-to-left run is measured backwards.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Touching ranges are merged, so a line of one direction yields exactly one.</b>
    ///         That is what makes the count an oracle: a caller can assert that a selection crossing
    ///         a direction boundary is <i>two</i> ranges and that one inside a single run is one,
    ///         and neither claim could be made if a run split produced a range apiece. It also keeps
    ///         a font fallback — a second run facing the same way — from painting a seam.
    ///     </para>
    /// </remarks>
    public void VisualRanges(int from, int to, List<(float X, float Width)> into) {
        ArgumentNullException.ThrowIfNull(into);

        // ⚠ Translated here for the reason `CaretOffset` translates at its top: the runs index the
        // transformed string, and comparing an author's index against a run boundary on a line
        // holding a case expansion selects the wrong characters.
        var start = ToDrawn(Math.Min(from, to));
        var end = ToDrawn(Math.Max(from, to));

        if (end <= start) {
            return;
        }

        var found = into.Count;

        for (var i = 0; i < Runs.Length; i++) {
            var run = Runs[i];
            var first = Math.Max(start, run.Start);
            var last = Math.Min(end, run.Start + run.Shaped.Text.Length);

            if (last <= first) {
                continue;
            }

            // ⚠ A tab is covered whole or not at all. It has no interior for an offset to land in,
            // and asking the run would measure whatever glyph the face mapped U+0009 to rather than
            // this tab's distance to its stop — see `WidthOf`.
            var left = run.IsTab ? PenOf(i) : PenOf(i) + run.CaretOffset(first, CaretAffinity.Downstream);
            var right = run.IsTab ? PenOf(i) + widths[i] : PenOf(i) + run.CaretOffset(last, CaretAffinity.Upstream);

            into.Add((MathF.Min(left, right), MathF.Abs(right - left)));
        }

        into.Sort(found, into.Count - found, XOrder.Instance);

        for (var i = into.Count - 1; i > found; i--) {
            var previous = into[i - 1];
            var current = into[i];

            // Touching, to within a rounding of the pen arithmetic. Overlapping is impossible —
            // runs tile the line — so this is only ever closing a seam.
            if (current.X <= previous.X + previous.Width + 0.01f) {
                into[i - 1] = (previous.X, MathF.Max(previous.Width, current.X + current.Width - previous.X));
                into.RemoveAt(i);
            }
        }
    }

    sealed class XOrder : IComparer<(float X, float Width)> {
        public static readonly XOrder Instance = new();

        public int Compare((float X, float Width) left, (float X, float Width) right) =>
            left.X.CompareTo(right.X);
    }

    /// <summary>Which caret index a distance along the line lands on.</summary>
    /// <param name="x">The distance from the start of the line, in pixels.</param>
    /// <returns>A UTF-16 index into the element's text.</returns>
    /// <remarks>
    ///     ⚠ <b><see cref="Offset" /> comes off first, and that is what stops a click on an indented
    ///     first line landing a character out.</b> A point inside the indent itself is before every
    ///     glyph on the line and comes back as its first index — the run clamps a negative distance —
    ///     which is what clicking in the white space at the start of a paragraph should do.
    /// </remarks>
    public int CaretIndexAt(float x) => CaretPositionAt(x).Index;

    /// <summary>Which caret a distance along the line lands on, and which side of it.</summary>
    /// <param name="x">The distance from the start of the line, in pixels.</param>
    /// <returns>A UTF-16 index into the element's text, and the affinity that puts it back here.</returns>
    /// <remarks>
    ///     <see cref="CaretIndexAt" />'s remark about <see cref="Offset" /> applies here too. Feeding
    ///     the pair back to <see cref="CaretOffset(int, CaretAffinity)" /> gives the x again; feeding
    ///     the index alone need not, which is the whole reason the pair exists.
    /// </remarks>
    public (int Index, CaretAffinity Affinity) CaretPositionAt(float x) {
        for (var i = 0; i < Runs.Length; i++) {
            if (x < PenOf(i) + widths[i] || i == Runs.Length - 1) {
                var run = Runs[i];

                // ⚠ A tab has no interior to hit-test, so the click goes to whichever of its two
                // ends is nearer. Asking the run instead would divide by the .notdef advance and put
                // the boundary at a fraction of the tab that has nothing to do with its width — the
                // caret landing before the tab for most of a wide one.
                if (run.IsTab) {
                    var after = x - PenOf(i) >= widths[i] / 2f;

                    return (
                        ToSource(run.Start + (after ? run.Shaped.Text.Length : 0)),
                        after ? CaretAffinity.Downstream : CaretAffinity.Upstream
                    );
                }

                var found = run.CaretPositionAt(x - PenOf(i));
                return (ToSource(found.Index), found.Affinity);
            }
        }

        return (0, CaretAffinity.Downstream);
    }

}
