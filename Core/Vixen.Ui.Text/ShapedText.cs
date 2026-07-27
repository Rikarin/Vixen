// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Text;

/// <summary>One glyph as the shaper produced it, in the font's design units.</summary>
/// <param name="GlyphId">The glyph's index in the font. Not a character.</param>
/// <param name="Cluster">
///     The UTF-16 index of the first character this glyph belongs to. Several glyphs may share one
///     cluster and one glyph may cover several characters; the cluster is a <i>range</i>, and where
///     it ends is only knowable from where the next one starts.
/// </param>
/// <param name="XAdvance">How far the pen moves after drawing it.</param>
/// <param name="YAdvance">How far the pen moves vertically. Zero for horizontal text.</param>
/// <param name="XOffset">Where to draw it relative to the pen.</param>
/// <param name="YOffset">Ditto, positive upwards.</param>
public readonly record struct ShapedGlyph(
    ushort GlyphId,
    int Cluster,
    float XAdvance,
    float YAdvance,
    float XOffset,
    float YOffset
);

/// <summary>A glyph and where it goes, with the pen arithmetic already done.</summary>
/// <param name="GlyphId">The glyph.</param>
/// <param name="Cluster">The UTF-16 index of the first character it belongs to.</param>
/// <param name="X">Its origin, in design units from the start of the line.</param>
/// <param name="Y">Ditto, positive upwards from the baseline.</param>
public readonly record struct GlyphPlacement(ushort GlyphId, int Cluster, float X, float Y);

/// <summary>One shaped run: a stretch of one direction, one script and one font.</summary>
/// <remarks>
///     ⚠ The glyphs are in <b>visual</b> order, which for a right-to-left run means their clusters
///     <i>descend</i>. Code that walks glyphs and expects clusters to increase is correct on Latin
///     and wrong on everything else — the spike that cleared HarfBuzz recorded this as the first
///     thing to know before writing against it.
/// </remarks>
public sealed class ShapedRun {
    internal ShapedRun(TextItem item, FontFace font, ShapedGlyph[] glyphs, float advance) {
        Item = item;
        Font = font;
        Glyphs = glyphs;
        Advance = advance;
    }

    /// <summary>Which stretch of the source this came from.</summary>
    public TextItem Item { get; }

    /// <summary>The font it was shaped with.</summary>
    public FontFace Font { get; }

    /// <summary>The glyphs, in visual order.</summary>
    public IReadOnlyList<ShapedGlyph> Glyphs { get; }

    /// <summary>The run's total width, in design units.</summary>
    public float Advance { get; }
}

/// <summary>A whole paragraph, shaped and ordered for drawing.</summary>
/// <remarks>
///     Everything here is in the font's design units, not pixels — see <see cref="FontFace" /> for
///     why. Multiply by <c>size / UnitsPerEm</c> at the point of drawing.
/// </remarks>
public sealed class ShapedText {
    internal ShapedText(string text, IReadOnlyList<ShapedRun> runs, float advance) {
        Text = text;
        Runs = runs;
        Advance = advance;
    }

    /// <summary>The text it was shaped from.</summary>
    public string Text { get; }

    /// <summary>The runs, in the order they are drawn — left to right.</summary>
    public IReadOnlyList<ShapedRun> Runs { get; }

    /// <summary>The paragraph's width in design units.</summary>
    public float Advance { get; }

    /// <summary>Walks every glyph with the pen arithmetic already done.</summary>
    /// <returns>The glyphs, left to right, at their origins.</returns>
    public IEnumerable<GlyphPlacement> Placements() {
        var penX = 0f;
        var penY = 0f;

        foreach (var run in Runs) {
            foreach (var glyph in run.Glyphs) {
                yield return new GlyphPlacement(
                    glyph.GlyphId,
                    glyph.Cluster,
                    penX + glyph.XOffset,
                    penY + glyph.YOffset
                );

                penX += glyph.XAdvance;
                penY += glyph.YAdvance;
            }
        }
    }
}
