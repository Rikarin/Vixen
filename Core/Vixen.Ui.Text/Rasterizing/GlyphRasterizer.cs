// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Numerics;
using Vixen.Ui.Text.Outlines;

namespace Vixen.Ui.Text.Rasterizing;

/// <summary>A glyph's coverage, one float per pixel in <c>[0, 1]</c>, row 0 at the top.</summary>
/// <param name="Width">How many pixels across.</param>
/// <param name="Height">How many pixels down.</param>
/// <param name="Coverage">Row-major, <c>Width * Height</c> long.</param>
public readonly record struct CoverageBitmap(int Width, int Height, float[] Coverage) {
    /// <summary>One pixel's coverage.</summary>
    /// <param name="x">Column.</param>
    /// <param name="y">Row.</param>
    public float this[int x, int y] => Coverage[(y * Width) + x];

    /// <summary>The total covered area, in pixels.</summary>
    public float Area {
        get {
            var total = 0f;
            foreach (var value in Coverage) {
                total += value;
            }

            return total;
        }
    }
}

/// <summary>Which crossings of a scanline count as inside the shape.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is not a preference and the two rules disagree on real input.</b> Where an even
///         number of same-wound contours overlap, non-zero fills the overlap and even-odd punches a
///         hole through it. For a glyph that is always the wrong answer — a script that builds a
///         letter out of stacked strokes comes apart — which is why
///         <see cref="GlyphRasterizer" /> was non-zero only, deliberately, and says so.
///     </para>
///     <para>
///         <b>What made it a choice rather than a constant is SVG.</b>
///         <c>fill-rule="evenodd"</c> is a thing a path author writes and means, and doc 48 § 4.1's
///         <c>Svg Path</c> source lists a fill rule among its parameters; a rasteriser that cannot
///         express it would silently draw a different shape from the one in the file.
///         <see href="https://github.com/Rikarin/Vixen/issues/687">#687</see> and
///         <see href="https://github.com/Rikarin/Vixen/issues/753">#753</see> named exactly this as
///         the blocker, and named the cost — a public option on the only rasteriser in
///         <c>Vixen.Ui.Text</c>, for one editor-side caller.
///     </para>
///     <para>
///         ⚠ <b>That editor-side caller does not exist, and until it does this option is a finished
///         thing nothing calls.</b> Swept over <c>.cs</c> and <c>.vxml</c> on 2026-09-08: the only
///         sites naming <see cref="EvenOdd" /> anywhere in the tree are three assertions in
///         <c>RasterizerTests</c>. That is not an argument for deleting it — the blocker it removes
///         is real and #753 now carries the measured route to the node — but a reader who assumes an
///         <c>Svg Path</c> node is drawing through this would be wrong, and this repository builds
///         seams ahead of their callers more often than it builds anything else.
///     </para>
///     <para>
///         ⚠ <b>The other blocker those issues name did not survive a re-measure and this is not it.</b>
///         "Referencing <c>SvgPath</c> would put the whole UI framework behind a bake" rested on a
///         closure comparison that is wrong in both columns: <c>Vixen.Ui</c>'s project closure is a
///         strict subset of <c>Vixen.Editor.TextureGraph</c>'s. What is left of that one is a
///         compile-surface argument about what an evaluator may <em>spell</em>, which is a decision
///         about where the node lives and not about this file.
///     </para>
/// </remarks>
public enum FillRule {
    /// <summary>Inside where the sum of signed crossings is not zero. The default, and what a font wants.</summary>
    NonZero,

    /// <summary>Inside where an odd number of edges have been crossed. What SVG's <c>evenodd</c> means.</summary>
    EvenOdd
}

/// <summary>
///     Fills a glyph outline into a coverage bitmap, by scanline.
/// </summary>
/// <remarks>
///     <para>
///         <b>This exists to be an oracle before it exists to draw anything.</b> A distance field is
///         judged by reconstructing coverage from it and comparing against a rasterisation of the
///         same outline — two independent routes to one shape — and that is a much stronger gate
///         than a golden image, which only says the output has not changed.
///     </para>
///     <para>
///         ⚠ <b>Non-zero winding unless a caller says otherwise, and every caller in this assembly
///         is a font.</b> A counter in an <c>o</c> is a contour wound the other way, and both rules
///         agree about that; they disagree where two <em>same-wound</em> contours overlap, which
///         happens in scripts that build a letter out of stacked strokes and where even-odd is
///         simply wrong. <see cref="FillRule" /> carries why the option exists at all.
///     </para>
/// </remarks>
public static class GlyphRasterizer {
    /// <summary>How many sub-scanlines each pixel row is sampled with.</summary>
    /// <remarks>
    ///     Vertical is sampled and horizontal is exact — a span's ends contribute their fraction of
    ///     a pixel rather than a whole one — so the error is one-dimensional and this is the axis
    ///     that pays for it. Sixteen puts a near-horizontal edge within a sixteenth of a pixel.
    /// </remarks>
    const int SubScanlines = 16;

    /// <summary>Rasterises an outline into a bitmap of a given size.</summary>
    /// <param name="outline">What to fill.</param>
    /// <param name="width">The bitmap's width in pixels.</param>
    /// <param name="height">Its height.</param>
    /// <param name="scale">How many pixels one outline unit becomes.</param>
    /// <param name="origin">The outline-space point that lands on the bitmap's bottom-left corner.</param>
    /// <param name="rule">Which crossings count as inside. Defaults to what a glyph wants.</param>
    /// <returns>The coverage.</returns>
    /// <remarks>
    ///     ⚠ <b>The rule is the last parameter and has a default, so the four call sites in this
    ///     assembly and the two in the editor are unchanged and still say "a font".</b> A rule
    ///     threaded through as a required argument would have made every one of them state a choice
    ///     it does not have — and the one that matters, the distance-field oracle, would then be one
    ///     edit away from judging a field against a shape the rasteriser filled differently.
    /// </remarks>
    public static CoverageBitmap Rasterize(
        GlyphOutline outline,
        int width,
        int height,
        float scale,
        Vector2 origin,
        FillRule rule = FillRule.NonZero
    ) {
        ArgumentNullException.ThrowIfNull(outline);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scale);

        var coverage = new float[width * height];
        if (outline.IsEmpty) {
            return new CoverageBitmap(width, height, coverage);
        }

        // Flattened in *pixel* space, at half a sub-scanline's worth of tolerance — which is the
        // point of having carried the curves this far.
        var tolerance = 0.5f / (scale * SubScanlines);
        var edges = OutlineFlattener.Flatten(outline, tolerance);

        var crossings = new List<(float X, int Winding)>(32);
        var row = new float[width];

        for (var y = 0; y < height; y++) {
            Array.Clear(row);

            for (var sub = 0; sub < SubScanlines; sub++) {
                // Bitmap rows run down and the outline runs up, so the sample's y is measured from
                // the bottom. Half-offsets keep the samples off the pixel boundaries, where an edge
                // that lands exactly on one would otherwise be counted twice or not at all.
                var pixelY = height - 1 - y + ((sub + 0.5f) / SubScanlines);
                var sampleY = (pixelY / scale) + origin.Y;

                crossings.Clear();
                foreach (var edge in edges) {
                    Cross(edge, sampleY, crossings);
                }

                if (crossings.Count == 0) {
                    continue;
                }

                crossings.Sort(static (a, b) => a.X.CompareTo(b.X));
                Fill(row, crossings, scale, origin.X, width, rule);
            }

            var offset = y * width;
            for (var x = 0; x < width; x++) {
                coverage[offset + x] = Math.Clamp(row[x] / SubScanlines, 0f, 1f);
            }
        }

        return new CoverageBitmap(width, height, coverage);
    }

    /// <summary>Where an edge crosses a horizontal line, and which way it is going.</summary>
    static void Cross(Edge edge, float y, List<(float X, int Winding)> crossings) {
        var from = edge.From;
        var to = edge.To;

        // Half-open in y: an edge covers [min, max). Without that a vertex shared by two edges is
        // counted twice, which flips the winding and punches a hole through the shape.
        var down = from.Y > to.Y;
        var top = down ? to.Y : from.Y;
        var bottom = down ? from.Y : to.Y;

        if (y < top || y >= bottom) {
            return;
        }

        var t = (y - from.Y) / (to.Y - from.Y);
        crossings.Add((from.X + (t * (to.X - from.X)), down ? -1 : 1));
    }

    /// <summary>Adds one sub-scanline's spans to a row, with exact coverage at the ends.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The two rules differ in one expression and in nothing else.</b> Non-zero asks
    ///         whether the accumulated signed winding is non-zero; even-odd asks whether an odd
    ///         number of edges have been crossed. That is what makes an overlap of two same-wound
    ///         contours solid under the first and a hole under the second.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Counting is written here rather than <c>winding &amp; 1</c>, and the claim that
    ///         the two could disagree is false.</b> It was written into this remark first and a
    ///         sabotage refuted it: <see cref="Cross" /> adds ±1 and nothing else, and the parity of
    ///         a sum of ±1 is the parity of how many there were — so the two spellings are equal for
    ///         every input this rasteriser can produce, and no test can tell them apart. The count
    ///         stays because it is the rule's own definition and does not rest on that invariant; a
    ///         reader who changes <see cref="Cross" /> to add a weight is then changing one
    ///         expression rather than silently unbinding two.
    ///     </para>
    /// </remarks>
    static void Fill(
        float[] row,
        List<(float X, int Winding)> crossings,
        float scale,
        float originX,
        int width,
        FillRule rule
    ) {
        var winding = 0;
        var crossed = 0;
        var spanStart = 0f;

        foreach (var (x, direction) in crossings) {
            var wasInside = rule == FillRule.EvenOdd ? (crossed & 1) != 0 : winding != 0;

            winding += direction;
            crossed++;

            var isInside = rule == FillRule.EvenOdd ? (crossed & 1) != 0 : winding != 0;

            if (!wasInside && isInside) {
                spanStart = x;
            } else if (wasInside && !isInside) {
                Span(row, (spanStart - originX) * scale, (x - originX) * scale, width);
            }
        }
    }

    /// <summary>One horizontal span, in pixels, contributing fractions at both ends.</summary>
    static void Span(float[] row, float from, float to, int width) {
        if (to <= from) {
            return;
        }

        from = Math.Max(from, 0);
        to = Math.Min(to, width);
        if (to <= from) {
            return;
        }

        var first = (int)Math.Floor(from);
        var last = (int)Math.Ceiling(to) - 1;

        if (first == last) {
            row[first] += to - from;
            return;
        }

        row[first] += first + 1 - from;
        for (var x = first + 1; x < last; x++) {
            row[x] += 1;
        }

        row[last] += to - last;
    }
}
