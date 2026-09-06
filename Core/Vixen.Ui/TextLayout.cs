// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Ui.Layout;
using Vixen.Ui.Text;

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

            // ⚠ The offset counts towards the block's width, and the width alone does not. An
            // indented first line occupies its indent as surely as it occupies its glyphs, so a
            // shrink-to-fit box measured on `Width` alone would come out an indent too narrow and
            // clip the end of the line it was measuring.
            widest = MathF.Max(widest, lines[i].Offset + lines[i].Width);
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
    ///     the next line — is what a caret arriving by pressing Down expects; that is the upstream
    ///     and downstream readings of the same index, and <see cref="LineOf(int, CaretAffinity)" />
    ///     is how a caller says which it meant. This overload keeps answering the first.
    /// </remarks>
    public int LineOf(int index) => LineOf(index, CaretAffinity.Upstream);

    /// <summary>Which line a caret falls on, given which side of the index it belongs to.</summary>
    /// <param name="index">A UTF-16 index into the element's text.</param>
    /// <param name="affinity">Which of the two characters either side of the index it belongs to.</param>
    /// <returns>The line's index in <see cref="Lines" />.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>A wrap is the third place one index names two, and the one a reader meets daily.</b>
    ///         The character before the break ends line <i>n</i>; the character after begins line
    ///         <i>n+1</i>; the index between them is a single number sitting at the end of one and
    ///         the start of the next, which is why a caret pressed Down onto a wrapped line and a
    ///         caret walked right off the end of one land in visibly different places.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Downstream only crosses to a line that actually starts at or before the index.</b>
    ///         A wrap that consumed the space it broke at leaves the next line starting <i>after</i>
    ///         the boundary index, and there the downstream reading has nowhere to go — the caret
    ///         genuinely belongs to the line that ends there, and crossing anyway would put it in
    ///         front of a character on the wrong row.
    ///     </para>
    /// </remarks>
    public int LineOf(int index, CaretAffinity affinity) {
        for (var i = 0; i < Lines.Length; i++) {
            if (index > Lines[i].Start + Lines[i].Length) {
                continue;
            }

            if (affinity == CaretAffinity.Downstream
                && index == Lines[i].Start + Lines[i].Length
                && i + 1 < Lines.Length
                && Lines[i + 1].Start <= index) {
                return i + 1;
            }

            return i;
        }

        return Lines.Length - 1;
    }

    /// <summary>Where a caret sits, in pixels from the top left of the block.</summary>
    /// <param name="index">A UTF-16 index into the element's text.</param>
    /// <returns>The offset along the line, and the top of the line it is on.</returns>
    public (float X, float Y) CaretAt(int index) => CaretAt(index, CaretAffinity.Upstream);

    /// <summary>Where a caret sits, given which side of the index it belongs to.</summary>
    /// <param name="index">A UTF-16 index into the element's text.</param>
    /// <param name="affinity">Which of the two characters either side of the index it belongs to.</param>
    /// <returns>The offset along the line, and the top of the line it is on.</returns>
    /// <remarks>
    ///     ⚠ <b>One bit decides both coordinates, and it must be the same bit.</b> The affinity picks
    ///     the line and is then handed to that line to pick the run and the cluster; deciding the row
    ///     one way and the column the other puts the caret at the end of a line it is not on.
    /// </remarks>
    public (float X, float Y) CaretAt(int index, CaretAffinity affinity) {
        var line = LineOf(index, affinity);
        return (Lines[line].CaretOffset(index, affinity), tops[line]);
    }

    /// <summary>Which caret index a point in the block lands on.</summary>
    /// <param name="x">Distance from the left, in pixels.</param>
    /// <param name="y">Distance from the top, in pixels.</param>
    public int CaretIndexAt(float x, float y) => CaretPositionAt(x, y).Index;

    /// <summary>Which caret a point in the block lands on, and which side of it.</summary>
    /// <param name="x">Distance from the left, in pixels.</param>
    /// <param name="y">Distance from the top, in pixels.</param>
    /// <returns>A UTF-16 index into the element's text, and the affinity that puts it back here.</returns>
    /// <remarks>
    ///     ⚠ <b>The y picks the line and therefore settles the wrap-boundary affinity before the
    ///     line is asked anything.</b> A click on the second row of a wrapped word means the caret is
    ///     on that row whatever the index turns out to be — so a hit at the very start of a
    ///     continuation line comes back <c>Downstream</c>, which is what puts it back on that row
    ///     rather than at the end of the row above.
    /// </remarks>
    public (int Index, CaretAffinity Affinity) CaretPositionAt(float x, float y) {
        var line = 0;

        while (line + 1 < Lines.Length && y >= tops[line + 1]) {
            line++;
        }

        var found = Lines[line].CaretPositionAt(x);

        // The line was chosen by the y and is not up for renegotiation. If the index the line
        // reported would be read onto the previous line at the affinity the line gave, the click
        // decides: it landed on this row, so the caret is downstream of the break.
        return line > 0 && found.Index == Lines[line].Start && LineOf(found.Index, found.Affinity) != line
            ? (found.Index, CaretAffinity.Downstream)
            : found;
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
    ///     <para>
    ///         ⚠ <b>The answer is a whole number of device pixels, and rounding it <i>here</i> rather
    ///         than at the end of the layout is what makes the two ways of writing the same paragraph
    ///         agree.</b> A block of four lines at 27.875 each is 111.5 pixels tall. Reported as
    ///         111.5, it reaches <c>LayoutTree.RoundToPixelGrid</c>, where a node that measured itself
    ///         is ceiled to 112 and a node that merely <i>contains</i> the measured one is rounded to
    ///         nearest — 111. An element's own <c>Text</c> is the first case; the <c>text</c> child a
    ///         markup interpolation emits is the second, so the same string in the same box measured
    ///         112 one way and 111 the other, and the container came out a pixel shorter than the
    ///         child inside it with the last line's descenders clipped. Every <c>.vxml</c> panel uses
    ///         the child form, so the difference showed up as a panel changing height purely by being
    ///         ported.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Ceiled, not rounded, and the reason is the same one the pixel grid gives.</b> A
    ///         line that needs 111.5 pixels does not fit in 111; the half pixel that is lost is the
    ///         bottom of the descenders. Where the fraction is large — .625, .75, .875 — ceiling and
    ///         rounding agree and the bug was invisible, which is why it looked intermittent.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Fixing it in the pixel grid instead was tried and is the wrong shape.</b> Teaching
    ///         a box to ceil because it holds text makes its near edge floor to match, and a box that
    ///         floors its top and ceils its bottom grows by a pixel whenever it sits at a fractional
    ///         offset — so the rows of a uniform list came out 32 and 33 pixels tall depending on
    ///         where they landed. Making the measurement itself integral leaves every rule in that
    ///         pass exactly as it was: there is no longer a fraction for the two rules to disagree
    ///         about.
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

        // ⚠ The tree goes in, and this call site is the only one that may hand it over: a measure
        // function runs while the store is laying this very element out, which is the one moment
        // `LayoutTree.ContentBands` can say where its lines sit among the floats. Everywhere else
        // the paragraph reuses what this pass decided — see `UiElement.Block(float, LayoutTree?)`.
        if (element.Block(width, request.Tree) is not { } block) {
            return new LayoutSize(0f, 0f);
        }

        var scale = request.Tree.PointScaleFactor;
        return new LayoutSize(WholePixels(block.Width, scale), WholePixels(block.Height, scale));
    }

    /// <summary>Rounds a measured length up to the next whole device pixel.</summary>
    /// <param name="value">The length, in layout pixels.</param>
    /// <param name="scale">How many device pixels one layout pixel is.</param>
    /// <returns>The length, no smaller, landing on the device grid.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The <i>device</i> grid and not the layout one, because the two differ wherever the
    ///         surface is not at 1×.</b> At 2× a block of 111.5 already covers exactly 223 device
    ///         pixels and needs nothing; ceiling it to 112 layout pixels would hand it half a device
    ///         pixel it has no use for, on every paragraph in the interface.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The tolerance is not decoration.</b> A line height is a sum of per-line floats, so
    ///         a block that is exactly eight pixels tall arrives as 8.0000005 about as often as as 8,
    ///         and a bare <c>Ceiling</c> would answer nine — a whole pixel of drift, on the case that
    ///         needed no rounding at all. The bound matches <c>LayoutTree</c>'s own comparison
    ///         tolerance, which is what decides the same question one pass later.
    ///     </para>
    /// </remarks>
    static float WholePixels(float value, float scale) {
        if (!float.IsFinite(value) || !float.IsFinite(scale) || scale <= 0f) {
            return value;
        }

        var scaled = value * scale;
        var whole = MathF.Ceiling(scaled - Tolerance);

        return MathF.Max(whole, 0f) / scale;
    }

    /// <summary>How near an integer counts as being one. <c>LayoutTree.ComparisonTolerance</c>.</summary>
    const float Tolerance = 0.0001f;
}
