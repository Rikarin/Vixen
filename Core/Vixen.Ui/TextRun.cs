// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Layout;
using Vixen.Ui.Text;

namespace Vixen.Ui;

/// <summary>One glyph and where to put it, in document space and in pixels.</summary>
/// <remarks>
///     The pen arithmetic and the design-unit scaling are both already done, so a renderer needs the
///     font's scale for nothing but the size of the quad. Everything upstream of this — HarfBuzz,
///     <see cref="ShapedText" /> — works in the font's design units, because shaping is
///     size-independent and a cache keyed on a pixel size would hold one entry per DPI scale of the
///     same string.
/// </remarks>
/// <param name="GlyphId">The glyph's index in the font. Not a character.</param>
/// <param name="X">Its origin's x.</param>
/// <param name="Y">Its origin's y, on the baseline, positive downwards like everything else here.</param>
public readonly record struct PositionedGlyph(ushort GlyphId, float X, float Y);

/// <summary>One stretch of an element's text: one face, shaped and measured at one size.</summary>
/// <remarks>
///     <para>
///         <b>A run is one face, and a line is a list of them.</b> See <see cref="TextLine" />: a
///         string whose characters are not all in one font becomes several runs, and so would a rich
///         text span carrying its own size. Everything on this type is about the one face, so a
///         consumer that reaches for <see cref="Font" /> or <see cref="Scale" /> is asking a question
///         only a run can answer — the line is where a mixed-font width or caret lives, because those
///         can only be composed in pixels.
///     </para>
///     <para>
///         ⚠ <b>One line.</b> Nothing here breaks a paragraph across lines, so a string wider than
///         its element overflows it rather than wrapping — the measure function ignores the width it
///         is offered. <c>Vixen.Ui.Text</c> already has the UAX#14 line breaker this needs and
///         wrapping is the next piece of text work rather than a missing consideration; said here so
///         that a long label overflowing reads as a known edge rather than as a layout bug.
///     </para>
///     <para>
///         The size is carried alongside the shaping rather than baked into it, because the shaping
///         is shared: the cache returns the same <see cref="ShapedText" /> for the same string in the
///         same font whatever size it is drawn at, and this is the per-element view of it.
///     </para>
/// </remarks>
/// <param name="Font">The face it was shaped with.</param>
/// <param name="Shaped">The glyphs, in design units.</param>
/// <param name="Size">The font size in pixels — what an <c>em</c> on this element measures.</param>
/// <param name="Tracking">
///     <c>letter-spacing</c> in pixels, added after every typographic character. Zero for the
///     overwhelming majority of text, and the code below is written so that zero costs nothing.
/// </param>
/// <param name="Leading">
///     The computed <c>line-height</c> in pixels, or <see cref="float.NaN" /> for the font's own
///     recommendation. NaN rather than zero, because zero is a line height somebody might mean.
/// </param>
/// <param name="Start">
///     Where this run's text begins in the element's, as a UTF-16 index. Zero for a line that is one
///     run, and what lets a caret index reach the run it belongs to.
/// </param>
public sealed record TextRun(
    FontFace Font,
    ShapedText Shaped,
    float Size,
    float Tracking = 0f,
    float Leading = float.NaN,
    int Start = 0
) {
    /// <summary>What multiplies a design unit to give a pixel.</summary>
    public float Scale => Size / Font.UnitsPerEm;

    /// <summary>How many typographic characters the line has, for tracking to be added between.</summary>
    /// <remarks>
    ///     ⚠ <b>Clusters, not glyphs.</b> A combining mark is its own glyph and the same cluster as
    ///     the letter it sits on, so counting glyphs would space an accented <c>é</c> as two
    ///     characters — and, worse, <see cref="Place" /> would push the accent off the side of the
    ///     letter. Shaping already gives the cluster and this is the whole reason it is carried.
    /// </remarks>
    public int Clusters {
        get {
            var count = 0;
            var previous = int.MinValue;

            foreach (var placement in Shaped.Placements()) {
                if (placement.Cluster != previous) {
                    count++;
                    previous = placement.Cluster;
                }
            }

            return count;
        }
    }

    /// <summary>How wide the line is.</summary>
    /// <remarks>
    ///     ⚠ Tracking is added after the <i>last</i> character as well as between, which is what CSS
    ///     specifies and what every browser does. It means centred text with a wide tracking sits
    ///     half a step left of true centre — visibly, at the sizes tracking is used at. Matched
    ///     rather than corrected, because a toolkit that quietly disagrees with the specification is
    ///     harder to reason about than one that reproduces a known wart.
    /// </remarks>
    public float Width => Tracking == 0f ? Shaped.Advance * Scale : Shaped.Advance * Scale + Tracking * Clusters;

    /// <summary>How tall one line of it is.</summary>
    /// <remarks>
    ///     The computed <c>line-height</c> when there is one, and the font's own recommendation when
    ///     there is not. <see cref="UiElement.LineHeight" /> is where the cascade's value becomes a
    ///     number this can use — the property takes relative units, so it has to be resolved against
    ///     the element's font size before it gets here.
    /// </remarks>
    public float Height => float.IsNaN(Leading) ? Font.Metrics.LineHeight * Scale : Leading;

    /// <summary>How far below the top of the line the baseline sits.</summary>
    /// <remarks>
    ///     <para>
    ///         CSS's <b>half-leading</b>: the glyphs occupy an area of ascender-plus-descender and
    ///         the line box may be taller or shorter than that, so the difference is split evenly
    ///         above and below rather than all going under the text. Putting it all below is what
    ///         makes a generous <c>line-height</c> look like a top margin.
    ///     </para>
    ///     <para>
    ///         ⚠ Half of a <i>negative</i> leading is negative, which is correct: a line height
    ///         smaller than the glyphs crops them evenly at both ends, and lines overlap. That is
    ///         what CSS says happens and what the author asked for.
    ///     </para>
    /// </remarks>
    public float Baseline {
        get {
            var ascender = Font.Metrics.Ascender * Scale;

            if (float.IsNaN(Leading)) {
                return ascender;
            }

            var content = (Font.Metrics.Ascender - Font.Metrics.Descender) * Scale;
            return ((Leading - content) / 2f) + ascender;
        }
    }

    /// <summary>How far along the run a caret index sits, in pixels.</summary>
    /// <param name="index">A UTF-16 index into the element's text, not into the run's.</param>
    /// <returns>The distance from the run's own start.</returns>
    /// <remarks>
    ///     ⚠ <b><see cref="Tracking" /> is not counted.</b> The shaped text knows nothing about
    ///     letter spacing, so a caret in a tracked run sits progressively short of the glyph it
    ///     belongs to — and <see cref="Width" />, which does count it, disagrees with the offset of
    ///     the last index. Recorded rather than fixed: no control sets tracking on an editable field
    ///     today, and fixing it means counting clusters up to an index, which is a different walk
    ///     from the one the caret code does.
    /// </remarks>
    public float CaretOffset(int index) => Shaped.CaretOffset(Math.Clamp(index - Start, 0, Shaped.Text.Length)) * Scale;

    /// <summary>Which caret index a distance from the run's start lands on.</summary>
    /// <param name="x">The distance, in pixels.</param>
    /// <returns>A UTF-16 index into the element's text.</returns>
    public int CaretIndexAt(float x) => Shaped.CaretIndexAt(x / Scale) + Start;

    /// <summary>Places every glyph relative to the start of the line.</summary>
    /// <param name="into">Where to put them.</param>
    /// <param name="penX">Where the run begins, in pixels from the start of the line.</param>
    /// <remarks>
    ///     ⚠ <b>The y is negated.</b> Shaping puts y positive upwards, because that is how a font's
    ///     design grid is drawn; the draw list is in document space, where y grows downwards. Getting
    ///     this wrong is invisible for Latin — almost every glyph sits on the baseline with a zero
    ///     offset — and flips every mark in Arabic and Devanagari to the other side of the letter it
    ///     belongs to.
    /// </remarks>
    public void Place(List<PositionedGlyph> into, float penX = 0f) {
        ArgumentNullException.ThrowIfNull(into);

        var scale = Scale;

        // Tracking accumulates once per cluster rather than once per glyph, so a combining mark
        // moves with the letter it belongs to instead of away from it. Kept out of the common path
        // entirely: text with no tracking runs the loop it always ran.
        var offset = penX;
        var previous = int.MinValue;

        foreach (var placement in Shaped.Placements()) {
            if (Tracking != 0f) {
                if (previous != int.MinValue && placement.Cluster != previous) {
                    offset += Tracking;
                }

                previous = placement.Cluster;
            }

            into.Add(new PositionedGlyph(placement.GlyphId, placement.X * scale + offset, -placement.Y * scale));
        }
    }

    /// <summary>Where one decoration line sits on this run, relative to its baseline.</summary>
    /// <param name="line">Which line. Exactly one — <see cref="Bars" /> is what walks a set of them.</param>
    /// <param name="decoration">The resolved style, whose two <c>auto</c>s this settles.</param>
    /// <returns>The bar, in pixels, y downwards from the baseline.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>This is where the face's opinion becomes a rectangle</b>, and every number in it but
    ///         the overline's comes out of <see cref="FontFace.Decoration" />. The design-unit values
    ///         are y-up, as a font grid is; the draw list is y-down, so the offsets are negated here
    ///         for the same reason <see cref="Place" /> negates a glyph's.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The <i>centre</i> of the stem is what the font states, so the bar's top is half a
    ///         thickness above it.</b> Placing the top at the stated position instead puts the whole
    ///         line a half-thickness too low, which is invisible at 13px in a text face and obvious in
    ///         a display face whose underline is a ninth of an em.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An overline has no font metric and is placed just above the ascent.</b> No
    ///         OpenType table states one — neither <c>post</c> nor <c>OS/2</c> has the field — so this
    ///         is the one position that is derived rather than read, and the ascender is what every
    ///         other toolkit derives it from. <c>text-underline-offset</c> does not move it: CSS
    ///         applies that property to the underline alone, and a shared offset would be a fifth
    ///         thing to explain for no author's benefit.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An <c>auto</c> thickness is floored at one pixel and an authored one is not.</b>
    ///         A face asking for a hundredth of an em — and one here does — is asking for 0.13px at
    ///         13pt, which the rasteriser draws as a grey smear that reads as a fault rather than as a
    ///         hairline. A thickness the author wrote is theirs, floor included: clamping it would
    ///         make <c>decoration-0</c> draw a line, and would make two adjacent thicknesses stop
    ///         being distinguishable at exactly the sizes somebody would be comparing them at.
    ///     </para>
    /// </remarks>
    public DecorationBar Bar(TextDecorationLine line, TextDecoration decoration) {
        var metrics = Font.Decoration;
        var scale = Scale;

        var thickness = float.IsNaN(decoration.Thickness)
            ? MathF.Max(
                (line == TextDecorationLine.LineThrough ? metrics.StrikeoutThickness : metrics.UnderlineThickness)
                * scale,
                1f
            )
            : decoration.Thickness;

        var centre = line switch {
            // Negated twice over: the metric is negative because it is below the baseline in a y-up
            // grid, and this axis points down.
            TextDecorationLine.Underline => (-metrics.UnderlineOffset * scale) + decoration.Offset,
            TextDecorationLine.LineThrough => -metrics.StrikeoutOffset * scale,
            _ => -Font.Metrics.Ascender * scale
        };

        // ⚠ The overline sits *entirely above* the ascent rather than centred on it or hanging below
        // it, and that is a measurement rather than a preference. An earlier draft put its top edge
        // on the ascent line, on the argument that a thick one should stay inside the line box; in
        // `TestShapeLana` the ascent is 1556 design units and the cap height is 1493, so the bar
        // landed on the tops of the capitals — two pixels of overlap at 60px, and the letters looked
        // struck rather than overlined. A face whose ascent clears its capitals hides that entirely,
        // which is why it took a pixel test to find. The cost is that the bar is outside the line box
        // and an element clipping its overflow will cut it, which is what a browser does too.
        return new DecorationBar(
            line == TextDecorationLine.Overline ? centre - thickness : centre - (thickness / 2f),
            thickness
        );
    }

    /// <summary>The bars a decoration asks for on one side of the glyphs.</summary>
    /// <param name="decoration">The resolved style.</param>
    /// <param name="under">
    ///     True for the lines painted beneath the glyphs, false for the ones painted over them.
    /// </param>
    /// <returns>The bars, in painting order.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The split is CSS Text Decoration 3 § 4.1's painting order, and it is observable.</b>
    ///         The underline and the overline go <i>under</i> the glyphs and the line-through goes
    ///         <i>over</i> them — which is why a descender interrupts an underline and nothing
    ///         interrupts a strikethrough. Emitting all three on one side is a picture that looks
    ///         plausible until a <c>g</c> or a <c>y</c> sits on the line, which is why the caller is
    ///         made to ask twice rather than being handed one list to place wherever is convenient.
    ///     </para>
    ///     <para>
    ///         <see cref="TextDecorationStyle.Double" /> doubles each bar — two of the thickness with
    ///         a gap of the thickness between them, growing downwards so that a doubled underline
    ///         does not creep up into the glyphs it belongs under.
    ///     </para>
    /// </remarks>
    public IEnumerable<DecorationBar> Bars(TextDecoration decoration, bool under) {
        foreach (var line in Order) {
            if ((decoration.Lines & line) == 0 || Under(line) != under) {
                continue;
            }

            var bar = Bar(line, decoration);
            yield return bar;

            if (decoration.Style == TextDecorationStyle.Double) {
                yield return bar with { Top = bar.Top + (bar.Thickness * 2f) };
            }
        }
    }

    /// <summary>Whether a line is painted beneath the glyphs rather than over them.</summary>
    /// <param name="line">The line.</param>
    public static bool Under(TextDecorationLine line) => line != TextDecorationLine.LineThrough;

    /// <summary>The three lines in painting order.</summary>
    static readonly TextDecorationLine[] Order = [
        TextDecorationLine.Underline,
        TextDecorationLine.Overline,
        TextDecorationLine.LineThrough
    ];
}
