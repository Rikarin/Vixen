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

/// <summary>An element's text, shaped and measured at its font size.</summary>
/// <remarks>
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
public sealed record TextRun(
    FontFace Font,
    ShapedText Shaped,
    float Size,
    float Tracking = 0f,
    float Leading = float.NaN
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

    /// <summary>Places every glyph relative to the start of the line.</summary>
    /// <param name="into">Where to put them.</param>
    /// <remarks>
    ///     ⚠ <b>The y is negated.</b> Shaping puts y positive upwards, because that is how a font's
    ///     design grid is drawn; the draw list is in document space, where y grows downwards. Getting
    ///     this wrong is invisible for Latin — almost every glyph sits on the baseline with a zero
    ///     offset — and flips every mark in Arabic and Devanagari to the other side of the letter it
    ///     belongs to.
    /// </remarks>
    public void Place(List<PositionedGlyph> into) {
        ArgumentNullException.ThrowIfNull(into);

        var scale = Scale;

        // Tracking accumulates once per cluster rather than once per glyph, so a combining mark
        // moves with the letter it belongs to instead of away from it. Kept out of the common path
        // entirely: text with no tracking runs the loop it always ran.
        var offset = 0f;
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

    /// <summary>Measures a leaf whose size is its text.</summary>
    /// <param name="request">What the layout algorithm is asking.</param>
    /// <returns>How big the line is.</returns>
    /// <remarks>
    ///     ⚠ <b>The available width is ignored</b>, which is exactly the single-line limitation above
    ///     wearing its working clothes: a wrapping implementation reads
    ///     <see cref="MeasureRequest.AvailableWidth" /> and this one has nothing to do with it. The
    ///     measure cache keys on the request, so an answer that ignores part of the question is still
    ///     a pure function of it and the cache stays correct.
    /// </remarks>
    public static LayoutSize Measure(in MeasureRequest request) =>
        request.Context is UiElement element && element.Run() is { } run
            ? new LayoutSize(run.Width, run.Height)
            : new LayoutSize(0f, 0f);
}
