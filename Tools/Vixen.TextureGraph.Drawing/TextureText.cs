// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Numerics;
using Vixen.Ui.Text;
using Vixen.Ui.Text.Outlines;
using Vixen.Ui.Text.Rasterizing;

namespace Vixen.TextureGraph.Drawing;

/// <summary>Where a line of text sits against its anchor.</summary>
internal enum TextureTextAlignment : byte {
    /// <summary>The first glyph's pen is at the anchor.</summary>
    Left,

    /// <summary>The line's own width is centred on the anchor.</summary>
    Centre,

    /// <summary>The line ends at the anchor.</summary>
    Right
}

/// <summary>Doc 48 § 4.1's <c>Text</c>: a string shaped and filled into a coverage field.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Not a kernel, and it could not be one.</b> A compute kernel has no rasteriser and cannot reach a
///         font: a <c>.rvn</c> cannot call managed code whatever is in its compilation. What doc 48 § 4.1 needs
///         is a picture the caller supplies, which <see cref="TextureImage.External" /> has always been able to
///         express and <see cref="TextureUploads.AddCoverage" /> is the door for —
///         <a href="https://github.com/Rikarin/Vixen/issues/687">#687</a>. This is the half that produces the
///         numbers that go through it, and <c>Source/Text</c> (Patina #139) is the node in front of it.
///     </para>
///     <para>
///         ⚠ <b>It moved here from <c>Vixen.Editor.TextureGraph</c>, where it had no caller but its tests.</b> A
///         host that fills a plan — a headless content build, a CLI, Patina's worker — must be able to draw a
///         <c>Source/Text</c>, and an editor assembly is the one place such a host cannot reach. The evaluator
///         cannot take it either: <c>Vixen.TextureGraph</c> links no interface assembly (#720), and a font is
///         <c>Vixen.Ui.Text</c>'s. So it lives in an assembly of its own, beside the evaluator.
///     </para>
///     <para>
///         ⚠ <b>The <c>Outlines</c> path rather than <c>GlyphAtlas</c>.</b> An atlas is a cache of small
///         rasterisations at screen sizes, with a distance field behind it so that a label scales smoothly; a
///         texture graph draws one string once, at whatever size a 4K bake asks for, and wants the outline
///         filled at exactly that size. <c>GlyphRasterizer</c> is the scanline fill those two share.
///     </para>
///     <para>
///         ⚠ <b>The face is resolved by the host at fill time, never by a compilation.</b> A line of text is
///         the picture, so a compile-time bake would allocate a full-size field per keystroke; doc 48 § 4.1
///         carries the decision. When the node names no face, <see cref="TextureDrawings" /> draws with the
///         Open Sans this assembly embeds — the editor's own UI face, OFL-1.1, linked rather than copied.
///     </para>
///     <para>
///         <b>Every number here is in texels of the picture being written</b>, which is doc 48 § D8's rule and is
///         what makes the same authored size mean the same physical letter at every bake resolution: the node
///         speaks in fractions of the image and <see cref="TextureDrawings" /> converts once, at the bake's size.
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


    /// <summary>Shapes a string and fills it into a coverage field, one line centred in the picture.</summary>
    /// <param name="font">The face to shape and fill with.</param>
    /// <param name="text">The line. Empty is legal and draws nothing.</param>
    /// <param name="width">The picture's width in texels.</param>
    /// <param name="height">Its height.</param>
    /// <param name="size">The em size, in texels of this picture.</param>
    /// <param name="alignment">Where the line sits across the width: from the left edge, centred, or to the right.</param>
    /// <param name="tracking">Extra advance after every glyph, in texels. Negative tightens.</param>
    /// <returns>Coverage, row-major, top row first, one value per texel in <c>[0, 1]</c>.</returns>
    public static float[] Rasterize(
        FontFace font,
        string text,
        int width,
        int height,
        float size,
        TextureTextAlignment alignment = TextureTextAlignment.Centre,
        float tracking = 0f
    ) =>
        Rasterize(
            font,
            new(
                text,
                size,
                alignment,
                tracking,
                alignment switch {
                    TextureTextAlignment.Left => 0f,
                    TextureTextAlignment.Right => width,
                    _ => width * 0.5f
                },
                height * 0.5f
            ),
            width,
            height
        );

    /// <summary>Shapes a block of lines and fills it into a coverage field.</summary>
    /// <param name="font">The face to shape and fill with.</param>
    /// <param name="layout">What to write and where, in texels of this picture.</param>
    /// <param name="width">The picture's width in texels.</param>
    /// <param name="height">Its height.</param>
    /// <param name="wrapX">Whether ink past the left or right edge comes back in at the other, so the picture tiles.</param>
    /// <param name="wrapY">The same, top and bottom.</param>
    /// <returns>
    ///     Coverage, row-major, top row first, one value per texel in <c>[0, 1]</c> — which is
    ///     <see cref="TextureUploads.AddCoverage" />'s argument exactly.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="font" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An extent or the size is not positive.</exception>
    /// <exception cref="ArgumentException">The face carries no outlines.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Lines break where the text says and nowhere else.</b> <c>LineBreaker</c> finds where a paragraph
    ///         <em>may</em> break, and choosing which of those to take needs a wrap width — layout's decision, which
    ///         <c>Vixen.Ui</c>'s <c>LineWrapper</c> already makes. A second, worse copy of it inside a texture node is
    ///         how two answers to one question get created, so an author who wants two lines writes two.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Glyphs are composited by <c>max</c> rather than summed.</b> Coverage is not light: two glyphs
    ///         whose boxes overlap — a kerned pair, an Arabic join, any accent — would read above one where they
    ///         touch and clamp to a hard edge exactly along the seam. The union of two coverages is the larger.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The block is placed by the face's line height, not by its ink.</b> The middle of the block is the
    ///         middle of the band between the first line's ascender and the last line's descender, so two strings
    ///         set at one size share a baseline; centring on the ink would make "acme" and "Acme" sit at different
    ///         heights, which only shows up beside a second bake.
    ///     </para>
    /// </remarks>
    public static float[] Rasterize(
        FontFace font,
        TextureTextLayout layout,
        int width,
        int height,
        bool wrapX = false,
        bool wrapY = false
    ) {
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(layout.Text);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(layout.Size);

        if (!font.HasOutlines) {
            throw new ArgumentException(
                $"'{font.Name}' carries no glyf or CFF table, so there are no outlines to fill. A bitmap-only or "
                + "colour-only face cannot be a Text node's font.",
                nameof(font)
            );
        }

        var coverage = new float[width * height];
        var lines = layout.Text.ReplaceLineEndings("\n").Split('\n');

        // ⚠ Pixels per *design unit*, and the shaping is held at design-unit scale for the reason
        // `Vixen.Ui.Text`'s README gives: HarfBuzz's OpenType path has no hinting, so one shaping
        // serves every size and the size enters only here.
        var scale = layout.Size / font.UnitsPerEm;
        var band = (font.Metrics.Ascender - font.Metrics.Descender) * scale;
        var pitch = font.Metrics.LineHeight * scale * Math.Max(layout.Leading, 0f);
        var block = band + pitch * (lines.Length - 1);
        var baseline = layout.CentreY - block * 0.5f + font.Metrics.Ascender * scale;

        // Tracking is authored in texels and the pen is in design units, so it converts once.
        var step = layout.Tracking / scale;

        foreach (var line in lines) {
            if (line.Length > 0) {
                var shaped = TextShaper.Shape(font, line);
                var glyphs = shaped.Placements().ToArray();
                var advance = (shaped.Advance + step * Math.Max(glyphs.Length - 1, 0)) * scale;

                var left = layout.Alignment switch {
                    TextureTextAlignment.Left => layout.AnchorX,
                    TextureTextAlignment.Centre => layout.AnchorX - advance * 0.5f,
                    TextureTextAlignment.Right => layout.AnchorX - advance,
                    _ => throw new ArgumentOutOfRangeException(nameof(layout), layout.Alignment, "No such alignment.")
                };

                for (var index = 0; index < glyphs.Length; index++) {
                    var placement = glyphs[index];
                    var outline = font.GetOutline(placement.GlyphId);

                    if (outline.IsEmpty) {
                        continue;
                    }

                    Draw(
                        coverage,
                        new(width, height, wrapX, wrapY),
                        outline,
                        scale,
                        left + (placement.X + step * index) * scale,
                        baseline - placement.Y * scale
                    );
                }
            }

            baseline += pitch;
        }

        return coverage;
    }

    /// <summary>Fills one glyph into the field, at a pen given in texels from the top left.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Rasterised into a box its own size and composited</b>, rather than handed the whole picture.
    ///         <see cref="GlyphRasterizer.Rasterize" /> walks every row of whatever bitmap it is given against every
    ///         edge of the outline, so filling a 4K field once per glyph is the string's length times the picture's
    ///         area for a shape that covers a fiftieth of it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The two coordinate systems meet here and they disagree about which way is up.</b> A coverage
    ///         field's row 0 is its top; an outline's <c>y</c> grows upwards from the baseline, and the rasteriser's
    ///         <c>origin</c> is the outline point that lands on the bitmap's <em>bottom</em>-left. Getting it wrong
    ///         flips every glyph about its own baseline — which for a symmetric letter is invisible.
    ///     </para>
    /// </remarks>
    static void Draw(
        float[] coverage,
        TextureCanvas canvas,
        GlyphOutline outline,
        float scale,
        float penX,
        float penY
    ) {
        var bounds = outline.Bounds();

        if (canvas.Box(
                penX + bounds.MinX * scale,
                penY - bounds.MaxY * scale,
                penX + bounds.MaxX * scale,
                penY - bounds.MinY * scale
            ) is not { } box) {
            return;
        }

        var origin = new Vector2((box.Left - penX) / scale, (penY - (box.Top + box.Height)) / scale);
        var filled = GlyphRasterizer.Rasterize(outline, box.Width, box.Height, scale, origin);

        canvas.Union(coverage, box, filled.Coverage);
    }
}

/// <summary>What a block of text says and where it sits, in texels of the picture it is drawn into.</summary>
/// <param name="Text">The lines, separated by line breaks.</param>
/// <param name="Size">The em size.</param>
/// <param name="Alignment">Whether each line starts, centres or ends at <paramref name="AnchorX" />.</param>
/// <param name="Tracking">Extra advance after every glyph. Negative tightens.</param>
/// <param name="AnchorX">The x each line is aligned to.</param>
/// <param name="CentreY">The y the middle of the block sits on.</param>
/// <param name="Leading">The distance between baselines, as a multiple of the face's own line height.</param>
internal readonly record struct TextureTextLayout(
    string Text,
    float Size,
    TextureTextAlignment Alignment,
    float Tracking,
    float AnchorX,
    float CentreY,
    float Leading = 1f
);
