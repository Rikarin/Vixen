// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Numerics;
using Vixen.Ui.Text;
using Vixen.Ui.Text.Outlines;
using Vixen.Ui.Text.Rasterizing;

namespace Vixen.Editor.TextureGraph;

/// <summary>Where a line of text sits across the picture it is drawn into.</summary>
/// <remarks>
///     ⚠ <b>Internal, like everything else this batch adds, and it matters here for a reason beyond
///     the convention.</b> `Docs` refuses any new public type with no guide page and no line in
///     `docs/DocsExempt.txt` — so a `public` enum nobody outside this assembly names is not a wider
///     surface, it is a gate failure two merges later, on a machine that is not the one that wrote
///     it.
/// </remarks>
internal enum TextureTextAlignment : byte {
    /// <summary>The first glyph's pen is at the left edge.</summary>
    Left,

    /// <summary>The line's own width is centred in the picture's.</summary>
    Centre,

    /// <summary>The line ends at the right edge.</summary>
    Right
}

/// <summary>Doc 48 § 4.1's <c>Text</c>: a string shaped and filled into a coverage field.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Not a kernel, and it could not be one.</b> A compute kernel has no rasteriser and
///         cannot reach a font; the evaluator compiles each kernel alone with no reference paths, so
///         there is nowhere for shaping to happen on that side of the line. What doc 48 § 4.1 needs is
///         a picture the caller supplies, which <see cref="TextureImage.External" /> has always been
///         able to express and <see cref="TextureUploads.AddCoverage" /> is the door for —
///         <a href="https://github.com/Rikarin/Vixen/issues/687">#687</a>. This is the half that
///         produces the numbers that go through it.
///     </para>
///     <para>
///         ⚠ <b>The <c>Outlines</c> path rather than <c>GlyphAtlas</c>, which is the decision § 4.1
///         records and the reason the reference costs so little.</b> An atlas is a cache of small
///         rasterisations at screen sizes, with a distance field behind it so that a label scales
///         smoothly; a texture graph draws one string once, at whatever size a 4K bake asks for, and
///         wants the outline filled at exactly that size. <c>GlyphRasterizer</c> is the scanline fill
///         those two share, and it exists in that assembly primarily as an oracle for the distance
///         field — a second, independent route to the same shape — which is why it is the right thing
///         to call and not merely the available one.
///     </para>
///     <para>
///         ⚠ <b>There is no <c>Text</c> node, and this is the shape of defect this repository names
///         most often.</b> A node would have to allocate an <em>external</em> image and carry the
///         bytes to fill it, and <c>TextureGraphCompiler.Allocate</c> only ever builds a pooled one —
///         the same gap that keeps <c>Bitmap</c>, <c>Gradient</c>, <c>Curve</c> and <c>Gradient
///         Map</c> off the graph, <a href="https://github.com/Rikarin/Vixen/issues/732">#732</a>. So
///         what is proved here is the path from a string to a texture a plan reads, end to end and on
///         a device; what is missing is the front end, and it is missing for a reason that is written
///         down and shared with four other nodes rather than for want of this file.
///     </para>
///     <para>
///         <b>Every number here is in texels of the picture being written</b>, which is doc 48 § D8's
///         rule and is what makes the same authored size mean the same physical letter at every bake
///         resolution — the caller scales the em size the way
///         <see cref="TexturePlan.Resolve" /> scales a radius, and nothing in this file knows about
///         levels.
///     </para>
/// </remarks>
internal static class TextureText {
    /// <summary>How far outside a glyph's own bounds the sub-bitmap reaches, in texels.</summary>
    /// <remarks>
    ///     ⚠ <b><see cref="GlyphOutline.Bounds" /> is sampled rather than solved</b> — its own remarks
    ///     say so — so a Bézier's true extreme can sit a fraction of a design unit outside what it
    ///     reports. A tight box would then clip the outermost sliver of a curve, uniformly, on the
    ///     side the curve bulges; a one-texel skirt costs four rows and makes the question moot.
    /// </remarks>
    const int Skirt = 1;

    /// <summary>Shapes a string and fills it into a coverage field.</summary>
    /// <param name="font">The face to shape and fill with.</param>
    /// <param name="text">The line. Empty is legal and draws nothing.</param>
    /// <param name="width">The picture's width in texels.</param>
    /// <param name="height">Its height.</param>
    /// <param name="size">The em size, in texels of this picture.</param>
    /// <param name="alignment">Where the line sits across the width.</param>
    /// <param name="tracking">Extra advance after every glyph, in texels. Negative tightens.</param>
    /// <returns>
    ///     Coverage, row-major, top row first, one value per texel in <c>[0, 1]</c> — which is
    ///     <see cref="TextureUploads.AddCoverage" />'s argument exactly.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="font" /> or <paramref name="text" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An extent or the size is not positive.</exception>
    /// <exception cref="ArgumentException">The face carries no outlines.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>One line, and the refusal to wrap is deliberate.</b> <c>LineBreaker</c> finds where
    ///         a paragraph <em>may</em> break and choosing which of those to take needs measured widths
    ///         — that is layout's job and <c>Vixen.Ui</c>'s <c>LineWrapper</c> already does it. A
    ///         second, worse copy of that decision inside a texture node is how two answers to one
    ///         question get created; § 4.1's parameters are a string, a font, a size, an alignment and
    ///         tracking, and none of them is a wrap width.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Glyphs are composited by <c>max</c> rather than summed.</b> Coverage is not light:
    ///         two glyphs whose boxes overlap — a kerned pair, an Arabic join, any accent — would read
    ///         above one where they touch and clamp to a hard edge exactly along the seam, which looks
    ///         like a rendering fault rather than like an overlap. The union of two coverages is the
    ///         larger of them.
    ///     </para>
    /// </remarks>
    public static float[] Rasterize(
        FontFace font,
        string text,
        int width,
        int height,
        float size,
        TextureTextAlignment alignment = TextureTextAlignment.Centre,
        float tracking = 0f
    ) {
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);

        if (!font.HasOutlines) {
            throw new ArgumentException(
                $"'{font.Name}' carries no glyf or CFF table, so there are no outlines to fill. A bitmap-only or "
                + "colour-only face cannot be a Text node's font.",
                nameof(font)
            );
        }

        var coverage = new float[width * height];

        if (text.Length == 0) {
            return coverage;
        }

        // ⚠ Pixels per *design unit*, and the shaping is held at design-unit scale for the reason
        // `Vixen.Ui.Text`'s README gives: HarfBuzz's OpenType path has no hinting, so one shaping
        // serves every size and the size enters only here.
        var scale = size / font.UnitsPerEm;
        var shaped = TextShaper.Shape(font, text);
        var glyphs = shaped.Placements().ToArray();

        if (glyphs.Length == 0) {
            return coverage;
        }

        // Tracking is authored in texels and the pen is in design units, so it converts once.
        var step = tracking / scale;
        var advance = shaped.Advance + (step * (glyphs.Length - 1));

        var left = alignment switch {
            TextureTextAlignment.Left => 0f,
            TextureTextAlignment.Centre => (width - (advance * scale)) * 0.5f,
            TextureTextAlignment.Right => width - (advance * scale),
            _ => throw new ArgumentOutOfRangeException(nameof(alignment), alignment, "No such alignment.")
        };

        // ⚠ The *face's* ascender and descender rather than the tallest glyph's, so that two strings
        // set at one size share a baseline. Centring on the ink would make "acme" and "Acme" sit at
        // different heights, which is the kind of wrong that only shows up beside a second bake.
        var band = (font.Metrics.Ascender - font.Metrics.Descender) * scale;
        var baseline = ((height - band) * 0.5f) + (font.Metrics.Ascender * scale);

        for (var index = 0; index < glyphs.Length; index++) {
            var placement = glyphs[index];
            var outline = font.GetOutline(placement.GlyphId);

            if (outline.IsEmpty) {
                continue;
            }

            Draw(
                coverage,
                width,
                height,
                outline,
                scale,
                left + ((placement.X + (step * index)) * scale),
                baseline - (placement.Y * scale)
            );
        }

        return coverage;
    }

    /// <summary>Fills one glyph into the field, at a pen given in texels from the top left.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Rasterised into a box its own size and composited</b>, rather than handed the whole
    ///         picture. <see cref="GlyphRasterizer.Rasterize" /> walks every row of whatever bitmap it
    ///         is given against every edge of the outline, so filling a 4K field once per glyph is the
    ///         string's length times the picture's area for a shape that covers a fiftieth of it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The two coordinate systems meet here and they disagree about which way is up.</b>
    ///         A coverage field's row 0 is its top; an outline's <c>y</c> grows upwards from the
    ///         baseline, and <see cref="GlyphRasterizer.Rasterize" />'s <c>origin</c> is the outline
    ///         point that lands on the bitmap's <em>bottom</em>-left. So the box's origin carries a
    ///         subtraction from the bottom, and getting it wrong flips every glyph about its own
    ///         baseline — which for a symmetric letter is invisible.
    ///     </para>
    /// </remarks>
    static void Draw(
        float[] coverage,
        int width,
        int height,
        GlyphOutline outline,
        float scale,
        float penX,
        float penY
    ) {
        var bounds = outline.Bounds();

        var boxLeft = (int)MathF.Floor(penX + (bounds.MinX * scale)) - Skirt;
        var boxTop = (int)MathF.Floor(penY - (bounds.MaxY * scale)) - Skirt;
        var boxRight = (int)MathF.Ceiling(penX + (bounds.MaxX * scale)) + Skirt;
        var boxBottom = (int)MathF.Ceiling(penY - (bounds.MinY * scale)) + Skirt;

        // Clipped to the picture, which is also what keeps a glyph placed far outside it from
        // allocating a box nobody reads.
        var clippedLeft = Math.Max(boxLeft, 0);
        var clippedTop = Math.Max(boxTop, 0);
        var boxWidth = Math.Min(boxRight, width) - clippedLeft;
        var boxHeight = Math.Min(boxBottom, height) - clippedTop;

        if (boxWidth <= 0 || boxHeight <= 0) {
            return;
        }

        var origin = new Vector2(
            (clippedLeft - penX) / scale,
            (penY + 1 - clippedTop - boxHeight) / scale
        );

        var box = GlyphRasterizer.Rasterize(outline, boxWidth, boxHeight, scale, origin);

        for (var row = 0; row < boxHeight; row++) {
            var target = ((clippedTop + row) * width) + clippedLeft;
            var source = row * boxWidth;

            for (var column = 0; column < boxWidth; column++) {
                coverage[target + column] = Math.Max(coverage[target + column], box.Coverage[source + column]);
            }
        }
    }
}
