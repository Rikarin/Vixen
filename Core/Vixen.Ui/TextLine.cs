// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Ui.Layout;

namespace Vixen.Ui;

/// <summary>An element's text as the runs it is actually drawn in.</summary>
/// <remarks>
///     <para>
///         <b>One run per face, in text order.</b> Most lines have exactly one, and the code below is
///         written so that a line that does costs no more than the single-run type it replaced. A
///         second run appears when a character is not in the first font — <see cref="FontRegistry" />
///         hands each grapheme cluster to the first face of the declaration's chain that covers it —
///         and the same shape will carry a rich-text span whose size or weight differs.
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
    /// <remarks>
    ///     ⚠ <b>The width is separable from the runs because of trailing whitespace.</b> A break
    ///     opportunity falls *after* a space, so the space belongs to the line before it and is
    ///     drawn there — but it must not count towards the width, or a line ending in a space wraps a
    ///     word earlier than one that does not and a right-aligned paragraph comes out ragged with
    ///     invisible characters. The wrapper already measured that width; this is where it arrives.
    /// </remarks>
    public TextLine(ImmutableArray<TextRun> runs, float width = float.NaN) {
        if (runs.IsDefaultOrEmpty) {
            throw new ArgumentException("a line has at least one run", nameof(runs));
        }

        Runs = runs;
        pens = new float[runs.Length];

        var pen = 0f;
        var above = 0f;
        var below = 0f;

        for (var i = 0; i < runs.Length; i++) {
            pens[i] = pen;
            pen += runs[i].Width;

            // ⚠ Ascent and descent are taken separately and *both* maximised, rather than the taller
            // run's height being used whole. Runs sit on a shared baseline, so a face with a deep
            // descender and a face with a tall ascender each contribute their own side; taking one
            // run's height would crop the other at whichever end it was larger.
            above = MathF.Max(above, runs[i].Baseline);
            below = MathF.Max(below, runs[i].Height - runs[i].Baseline);
        }

        Width = float.IsNaN(width) ? pen : width;
        Baseline = above;
        Height = above + below;

        Start = runs[0].Start;
        Length = runs[^1].Start + runs[^1].Shaped.Text.Length - Start;
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
    public float Width { get; }

    /// <summary>How tall it is, in pixels.</summary>
    public float Height { get; }

    /// <summary>How far below the top of the line its shared baseline sits, in pixels.</summary>
    public float Baseline { get; }

    /// <summary>Where a run begins, in pixels from the start of the line.</summary>
    /// <param name="run">The run's index in <see cref="Runs" />.</param>
    public float PenOf(int run) => pens[run];

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
            Runs[i].Place(into, pens[i]);
        }
    }

    /// <summary>Where a caret sits, in pixels from the start of the line.</summary>
    /// <param name="index">A UTF-16 index into the element's text.</param>
    /// <returns>The distance from the start of the line.</returns>
    /// <remarks>
    ///     ⚠ <b>An index on a run boundary belongs to the run that ends there.</b> Both answers are
    ///     the same pixel, so the choice only shows in which font's metrics decide — and the earlier
    ///     run is the one the character before the caret was drawn in, which is what a caret is
    ///     conventionally attached to. Caret <i>affinity</i>, which is the general form of this
    ///     question and matters at a wrap, is owed with the editor.
    /// </remarks>
    public float CaretOffset(int index) {
        for (var i = 0; i < Runs.Length; i++) {
            var run = Runs[i];

            if (index <= run.Start + run.Shaped.Text.Length || i == Runs.Length - 1) {
                return pens[i] + run.CaretOffset(index);
            }
        }

        return Width;
    }

    /// <summary>Which caret index a distance along the line lands on.</summary>
    /// <param name="x">The distance from the start of the line, in pixels.</param>
    /// <returns>A UTF-16 index into the element's text.</returns>
    public int CaretIndexAt(float x) {
        for (var i = 0; i < Runs.Length; i++) {
            if (x < pens[i] + Runs[i].Width || i == Runs.Length - 1) {
                return Runs[i].CaretIndexAt(x - pens[i]);
            }
        }

        return 0;
    }

}
