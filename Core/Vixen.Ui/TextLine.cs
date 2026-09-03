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

    /// <summary>Builds a line from its runs.</summary>
    /// <param name="runs">The runs, in text order. At least one.</param>
    /// <param name="width">
    ///     What to report as the line's width, or <see cref="float.NaN" /> for the sum of the runs.
    /// </param>
    /// <param name="offset">
    ///     How far in from the start of the line box the first glyph sits, in pixels.
    ///     <c>text-indent</c>, and zero for every line but a first one.
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
    public TextLine(ImmutableArray<TextRun> runs, float width = float.NaN, float offset = 0f) {
        if (runs.IsDefaultOrEmpty) {
            throw new ArgumentException("a line has at least one run", nameof(runs));
        }

        Runs = runs;
        Offset = offset;
        pens = new float[runs.Length];

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
        var pen = 0f;

        foreach (var index in Order(runs)) {
            pens[index] = pen;
            pen += runs[index].Width;
        }

        Width = float.IsNaN(width) ? pen : width;
        Baseline = above;
        Height = above + below;

        Start = runs[0].Start;
        Length = runs[^1].Start + runs[^1].Shaped.Text.Length - Start;
    }

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
                return PenOf(i) + run.CaretOffset(index, affinity);
            }
        }

        return Offset + Width;
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
            if (x < PenOf(i) + Runs[i].Width || i == Runs.Length - 1) {
                return Runs[i].CaretPositionAt(x - PenOf(i));
            }
        }

        return (0, CaretAffinity.Downstream);
    }

}
