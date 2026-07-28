// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Ui.Layout;

namespace Vixen.Ui;

/// <summary>An element's text, wrapped into the lines it is drawn on.</summary>
/// <remarks>
///     <para>
///         <b>Three types, one for each thing a line of text is made of.</b> A <see cref="TextRun" />
///         is one face; a <see cref="TextLine" /> is the runs sharing a baseline; a block is the
///         lines stacked down the page. Most text is one line of one run and costs no more than it
///         did when that was the only shape available.
///     </para>
///     <para>
///         ⚠ <b>Wrapped in pixels, and each line shaped on its own.</b> The break opportunities come
///         from UAX#14 and are about the characters; the widths are measured across whatever faces
///         the fallback chose, which is why the wrapping happens here rather than in
///         <c>Vixen.Ui.Text</c>'s <c>LineWrapper</c> overload that takes a <c>ShapedText</c> — that
///         one is a single font by construction. Once the breaks are known, each line's text is
///         covered and shaped by itself, which is what a line <i>is</i>: a break stops a ligature and
///         unjoins an Arabic word, and it is supposed to.
///     </para>
/// </remarks>
public sealed class TextLayout {
    readonly float[] tops;

    /// <summary>Builds a block from its lines, in order.</summary>
    /// <param name="lines">The lines. At least one.</param>
    public TextLayout(ImmutableArray<TextLine> lines) {
        if (lines.IsDefaultOrEmpty) {
            throw new ArgumentException("a block has at least one line", nameof(lines));
        }

        Lines = lines;
        tops = new float[lines.Length];

        var y = 0f;
        var widest = 0f;

        for (var i = 0; i < lines.Length; i++) {
            tops[i] = y;
            y += lines[i].Height;
            widest = MathF.Max(widest, lines[i].Width);
        }

        Height = y;
        Width = widest;
    }

    /// <summary>The lines, top to bottom.</summary>
    public ImmutableArray<TextLine> Lines { get; }

    /// <summary>How wide the widest line is, in pixels.</summary>
    /// <remarks>
    ///     ⚠ <b>The widest line, not the width it was wrapped to.</b> A paragraph wrapped to 300
    ///     pixels whose longest line is 240 measures 240 — reporting the wrapping width instead would
    ///     make every centred paragraph sit off-centre by half its own slack, and would stop a
    ///     shrink-to-fit box ever shrinking.
    /// </remarks>
    public float Width { get; }

    /// <summary>How tall all the lines together are, in pixels.</summary>
    public float Height { get; }

    /// <summary>Where a line's top edge sits, in pixels from the top of the block.</summary>
    /// <param name="line">The line's index in <see cref="Lines" />.</param>
    public float TopOf(int line) => tops[line];

    /// <summary>Which line a caret index falls on.</summary>
    /// <param name="index">A UTF-16 index into the element's text.</param>
    /// <returns>The line's index in <see cref="Lines" />.</returns>
    /// <remarks>
    ///     ⚠ <b>An index on a break belongs to the line that ends there</b>, which is one of the two
    ///     answers and is the one a caret arriving from the left expects. The other — the start of
    ///     the next line — is what a caret arriving by pressing Down expects, and telling those apart
    ///     is <i>caret affinity</i>, which needs a bit of state this does not carry. Owed with the
    ///     editor, and named here rather than left as a surprise.
    /// </remarks>
    public int LineOf(int index) {
        for (var i = 0; i < Lines.Length; i++) {
            if (index <= Lines[i].Start + Lines[i].Length) {
                return i;
            }
        }

        return Lines.Length - 1;
    }

    /// <summary>Where a caret sits, in pixels from the top left of the block.</summary>
    /// <param name="index">A UTF-16 index into the element's text.</param>
    /// <returns>The offset along the line, and the top of the line it is on.</returns>
    public (float X, float Y) CaretAt(int index) {
        var line = LineOf(index);
        return (Lines[line].CaretOffset(index), tops[line]);
    }

    /// <summary>Which caret index a point in the block lands on.</summary>
    /// <param name="x">Distance from the left, in pixels.</param>
    /// <param name="y">Distance from the top, in pixels.</param>
    public int CaretIndexAt(float x, float y) {
        var line = 0;

        while (line + 1 < Lines.Length && y >= tops[line + 1]) {
            line++;
        }

        return Lines[line].CaretIndexAt(x);
    }

    /// <summary>Measures a leaf whose size is its text.</summary>
    /// <param name="request">What the layout algorithm is asking.</param>
    /// <returns>How big the text is.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The available width is used only when the algorithm is offering one.</b> Flexbox
    ///         asks a leaf twice — once for its intrinsic size, with the width undefined, and once
    ///         against a real constraint — and a measure function that wrapped to an undefined width
    ///         would report one character per line and make every paragraph a column.
    ///     </para>
    ///     <para>
    ///         The measure cache keys on the request, so this stays a pure function of it: the same
    ///         question gets the same answer, and a different available width is a different
    ///         question.
    ///     </para>
    /// </remarks>
    public static LayoutSize Measure(in MeasureRequest request) {
        if (request.Context is not UiElement element) {
            return new LayoutSize(0f, 0f);
        }

        // ⚠ The mode alone, and an earlier draft also tested the width for `NaN`. The layout hands
        // out `NaN` and `Undefined` together — a min-content probe is both — so the second half never
        // decided anything, and sabotaging it failed no test. One condition, one meaning.
        var width = request.WidthMode == MeasureMode.Undefined ? float.PositiveInfinity : request.AvailableWidth;

        return element.Block(width) is { } block ? new LayoutSize(block.Width, block.Height) : new LayoutSize(0f, 0f);
    }
}
