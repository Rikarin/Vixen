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
public sealed record TextRun(FontFace Font, ShapedText Shaped, float Size) {
    /// <summary>What multiplies a design unit to give a pixel.</summary>
    public float Scale => Size / Font.UnitsPerEm;

    /// <summary>How wide the line is.</summary>
    public float Width => Shaped.Advance * Scale;

    /// <summary>How tall one line of it is, as the font's own metrics ask for.</summary>
    /// <remarks>
    ///     ⚠ The font's line height, not the cascade's <c>line-height</c>. That property is one of
    ///     the four still inherited as a specified value rather than a computed one — see the
    ///     cascade's remarks — and honouring it here would mean resolving a relative unit against the
    ///     wrong font size. Owed with the computed-value stage.
    /// </remarks>
    public float Height => Font.Metrics.LineHeight * Scale;

    /// <summary>How far below the top of the line the baseline sits.</summary>
    public float Baseline => Font.Metrics.Ascender * Scale;

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
        foreach (var placement in Shaped.Placements()) {
            into.Add(new PositionedGlyph(placement.GlyphId, placement.X * scale, -placement.Y * scale));
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
