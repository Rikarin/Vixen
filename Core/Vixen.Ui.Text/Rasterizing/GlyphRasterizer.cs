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

/// <summary>
///     Fills a glyph outline into a coverage bitmap, by scanline and non-zero winding.
/// </summary>
/// <remarks>
///     <para>
///         <b>This exists to be an oracle before it exists to draw anything.</b> A distance field is
///         judged by reconstructing coverage from it and comparing against a rasterisation of the
///         same outline — two independent routes to one shape — and that is a much stronger gate
///         than a golden image, which only says the output has not changed.
///     </para>
///     <para>
///         ⚠ <b>Non-zero winding, not even-odd.</b> A counter in an <c>o</c> is a contour wound the
///         other way, and every font relies on it; even-odd gives the same answer for one hole and
///         the wrong one for a glyph whose contours overlap, which happens in scripts that build a
///         letter out of stacked strokes.
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
    /// <returns>The coverage.</returns>
    public static CoverageBitmap Rasterize(
        GlyphOutline outline,
        int width,
        int height,
        float scale,
        Vector2 origin
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
                Fill(row, crossings, scale, origin.X, width);
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
    static void Fill(float[] row, List<(float X, int Winding)> crossings, float scale, float originX, int width) {
        var winding = 0;
        var spanStart = 0f;

        foreach (var (x, direction) in crossings) {
            var wasInside = winding != 0;
            winding += direction;
            var isInside = winding != 0;

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
