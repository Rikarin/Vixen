// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Numerics;
using Vixen.Ui.Text.Outlines;

namespace Vixen.Ui.Text.Rasterizing;

/// <summary>A glyph as three signed distances per pixel, row 0 at the top.</summary>
/// <param name="Width">How many pixels across.</param>
/// <param name="Height">How many pixels down.</param>
/// <param name="Range">The distance, in pixels, that maps to the full <c>[0, 1]</c> span.</param>
/// <param name="Channels">Row-major, three floats per pixel, each in <c>[0, 1]</c> with 0.5 on the edge.</param>
public readonly record struct DistanceFieldBitmap(int Width, int Height, float Range, float[] Channels) {
    /// <summary>The three channels at a pixel.</summary>
    /// <param name="x">Column.</param>
    /// <param name="y">Row.</param>
    public (float R, float G, float B) this[int x, int y] {
        get {
            var at = ((y * Width) + x) * 3;
            return (Channels[at], Channels[at + 1], Channels[at + 2]);
        }
    }

    /// <summary>What a shader reconstructs: the median of the three, above 0.5 inside the glyph.</summary>
    /// <param name="x">Column.</param>
    /// <param name="y">Row.</param>
    /// <returns>The reconstructed signed distance, in the same <c>[0, 1]</c> encoding.</returns>
    public float Median(int x, int y) {
        var (r, g, b) = this[x, y];
        return Math.Max(Math.Min(r, g), Math.Min(Math.Max(r, g), b));
    }
}

/// <summary>
///     Turns an outline into a multi-channel signed distance field.
/// </summary>
/// <remarks>
///     <para>
///         One texture and one shader give crisp text at any scale, and outlines, glows and shadows
///         come out of the same field for nothing. What a plain distance field cannot do is a corner:
///         distance to the nearest edge is smooth across one, so every serif and every stem end
///         arrives rounded. Three channels fix that — see <see cref="EdgeColoring" /> for why.
///     </para>
///     <para>
///         ⚠ <b>Distance to the edge's line, not to the edge's segment.</b> Past a segment's end the
///         distance is measured to the infinite line it lies on, weighted so the nearer segment wins
///         — the pseudo-distance msdfgen introduced. Clamping to the segment instead rounds every
///         convex corner back off again, which is the thing the three channels were for.
///     </para>
/// </remarks>
public static class DistanceField {
    /// <summary>Builds a field for an outline.</summary>
    /// <param name="outline">What to encode.</param>
    /// <param name="width">The field's width in pixels.</param>
    /// <param name="height">Its height.</param>
    /// <param name="scale">How many pixels one outline unit becomes.</param>
    /// <param name="origin">The outline-space point at the field's bottom-left corner.</param>
    /// <param name="range">How many pixels either side of the edge the field spans.</param>
    /// <returns>The field.</returns>
    public static DistanceFieldBitmap Generate(
        GlyphOutline outline,
        int width,
        int height,
        float scale,
        Vector2 origin,
        float range = 4f
    ) {
        ArgumentNullException.ThrowIfNull(outline);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scale);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(range);

        var channels = new float[width * height * 3];
        if (outline.IsEmpty) {
            Array.Fill(channels, 0f);
            return new DistanceFieldBitmap(width, height, range, channels);
        }

        // Flattened finely relative to a pixel, because the field's own resolution is what decides
        // how much of a curve is visible — the same argument the rasteriser makes.
        var edges = EdgeColoring.Colour(outline, OutlineFlattener.Flatten(outline, 0.05f / scale));
        var inside = GlyphRasterizer.Rasterize(outline, width, height, scale, origin);

        // Which way the outline is wound, so "left of the edge" and "inside the shape" agree. Fonts
        // differ, and a hole is wound the other way on purpose — which is exactly what makes a
        // point inside it come out negative without anything special being said about holes.
        var winding = Winding(edges);

        // ⚠ <b>Split by channel and precomputed once, because the inner loop runs a few million
        // times.</b> Every pixel asks three questions and each one used to walk the whole edge list
        // testing a mask, so two thirds of the work was reaching edges that could not answer — and
        // each surviving edge then recomputed its own direction, its length and the reciprocal of
        // both. None of that depends on the pixel. An icon of a hundred and fifty edges took 35ms to
        // encode before this and 3ms after, which is the difference between an atlas that pays for
        // itself on first sight and one that stalls the frame an icon first appears in.
        var red = Prepare(edges, EdgeChannels.Red);
        var green = Prepare(edges, EdgeChannels.Green);
        var blue = Prepare(edges, EdgeChannels.Blue);

        for (var y = 0; y < height; y++) {
            for (var x = 0; x < width; x++) {
                // The pixel's centre, back in the outline's own units.
                var point = new Vector2(
                    ((x + 0.5f) / scale) + origin.X,
                    ((height - 1 - y + 0.5f) / scale) + origin.Y
                );

                // ⚠ <b>Each channel carries its own sign, and that is the mechanism rather than a
                // detail.</b> Taking one sign from the fill and applying it to all three makes the
                // three values differ only in magnitude, so their median can never disagree with a
                // single channel about which side of the shape a point is on — which is the whole of
                // what the median was for. The first version did exactly that and reconstructed a
                // square's corner no better than a plain field.
                var redDistance = Nearest(red, point, winding);
                var greenDistance = Nearest(green, point, winding);
                var blueDistance = Nearest(blue, point, winding);

                // ⚠ The fill still settles the *overall* answer. A sign taken from an edge's
                // orientation is wrong wherever two contours overlap, and the rasteriser already had
                // to be right about that — so where the two disagree, the three channels flip
                // together and keep the structure that sharpens the corner.
                if (inside[x, y] >= 0.5f != Median(redDistance, greenDistance, blueDistance) >= 0) {
                    redDistance = -redDistance;
                    greenDistance = -greenDistance;
                    blueDistance = -blueDistance;
                }

                var at = ((y * width) + x) * 3;
                channels[at] = Encode(redDistance, scale, range);
                channels[at + 1] = Encode(greenDistance, scale, range);
                channels[at + 2] = Encode(blueDistance, scale, range);
            }
        }

        return new DistanceFieldBitmap(width, height, range, channels);
    }

    /// <summary>Maps a signed distance in outline units into the texture's <c>[0, 1]</c>.</summary>
    static float Encode(float distance, float scale, float range) =>
        Math.Clamp((distance * scale / range) + 0.5f, 0f, 1f);

    static float Median(float a, float b, float c) => Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));

    /// <summary>Which way round the outline runs, as +1 or −1.</summary>
    static float Winding(List<ColouredEdge> edges) {
        var area = 0f;
        foreach (var edge in edges) {
            area += Cross(edge.From, edge.To);
        }

        return area >= 0 ? 1f : -1f;
    }

    /// <summary>
    ///     The signed distance to the nearest edge carrying a channel, positive inside.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Past a segment's end the distance is to the line it lies on rather than to its
    ///         endpoint — msdfgen's pseudo-distance. The nearest edge is still chosen by ordinary
    ///         distance, so a distant edge's line cannot win.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Kept as insurance, and labelled as insurance rather than as a covered claim.</b>
    ///         A sabotage clamping to the segment fails nothing here, and two shapes were built to
    ///         try to reach it: a right angle, where the two answers barely differ, and a sharp wedge,
    ///         where they differ a great deal in <i>magnitude</i> and not at all in sign. Since a
    ///         threshold reads the sign, the reconstructed tip moves by 0.02 of a texel — 6.4317
    ///         against 6.4507, measured — which is far below what the sampling itself can resolve.
    ///         What it should buy is a truer gradient for the shader's own antialiasing, and nothing
    ///         in this repository looks at the gradient yet.
    ///     </para>
    /// </remarks>
    static float Nearest(Prepared[] edges, Vector2 point, float winding) {
        var best = float.MaxValue;
        var bestSquared = float.MaxValue;
        var signed = 0f;

        foreach (var edge in edges) {
            // ⚠ <b>An exact rejection, not an approximate one.</b> Nothing on a segment can be nearer
            // to a point than the distance to its midpoint less its half length, so an edge whose
            // whole extent lies further away than the best so far cannot improve it and cannot reach
            // the assignment below — which is what makes skipping it produce the identical field
            // rather than a cheaper one. On the first edge `best` is <c>MaxValue</c> and the squared
            // reach is infinite, so nothing is skipped before there is an answer to skip against.
            var toMiddle = point - edge.Middle;
            var reach = best + edge.HalfLength;

            if (toMiddle.LengthSquared() >= reach * reach) {
                continue;
            }

            var t = Math.Clamp(Vector2.Dot(point - edge.From, edge.Direction) * edge.InverseLengthSquared, 0f, 1f);
            var offset = point - (edge.From + (t * edge.Direction));
            var distanceSquared = offset.LengthSquared();

            if (distanceSquared >= bestSquared) {
                continue;
            }

            bestSquared = distanceSquared;
            best = MathF.Sqrt(distanceSquared);
            signed = winding * Cross(edge.Direction * edge.InverseLength, point - edge.From);
        }

        return best == float.MaxValue ? 0f : signed;
    }

    /// <summary>The edges carrying one channel, with everything pixel-independent worked out.</summary>
    static Prepared[] Prepare(List<ColouredEdge> edges, EdgeChannels channel) {
        var prepared = new List<Prepared>(edges.Count);

        foreach (var edge in edges) {
            if ((edge.Channels & channel) == 0) {
                continue;
            }

            var direction = edge.To - edge.From;
            var lengthSquared = direction.LengthSquared();

            // A zero-length edge has no direction to measure against, and dropping it here is what
            // the per-pixel loop used to do on every pixel.
            if (lengthSquared <= 0f) {
                continue;
            }

            var length = MathF.Sqrt(lengthSquared);

            prepared.Add(
                new Prepared(
                    edge.From,
                    direction,
                    1f / lengthSquared,
                    1f / length,
                    edge.From + (direction * 0.5f),
                    length * 0.5f
                )
            );
        }

        return [.. prepared];
    }

    /// <summary>One edge, with the parts that do not depend on the pixel already computed.</summary>
    readonly record struct Prepared(
        Vector2 From,
        Vector2 Direction,
        float InverseLengthSquared,
        float InverseLength,
        Vector2 Middle,
        float HalfLength
    );

    static float Cross(Vector2 a, Vector2 b) => (a.X * b.Y) - (a.Y * b.X);
}
