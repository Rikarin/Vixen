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
    ///     How near two edges' distances have to be, in proportion, before the corner rule decides
    ///     between them rather than the distance.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Proportional and never absolute, because the same outline is encoded at a font's
    ///     design units and at an icon's document pixels.</b> The two edges meeting at a corner are
    ///     exactly equidistant from every point in its exterior wedge — that is what a corner is —
    ///     but they reach that point from opposite ends of themselves, so the arithmetic differs in
    ///     the last bit or two. A fixed epsilon would be the whole shape at one scale and nothing at
    ///     the other.
    /// </remarks>
    const float Tie = 1e-5f;

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
    ///         ⚠ <b>Which of two equidistant edges wins is the corner, and picking the first of them
    ///         drew a phantom bar across every icon.</b> Every point in the exterior wedge of a convex
    ///         corner is equidistant from <i>both</i> edges that meet there, because both of them
    ///         clamp to the shared vertex — so the ordinary distance cannot separate them and whichever
    ///         happened to be listed first supplied the pseudo-distance for the whole wedge. Above a
    ///         rectangle's top-left corner that is the top edge, whose line runs away to the left
    ///         forever: the field then reads "half a texel outside the shape" two texels clear of it,
    ///         and every channel that edge carries reads the same, so the median does too. Measured on
    ///         the editor's pause glyph, that put a uniform band of exactly 0.5 across the full width
    ///         of the cell half a texel above the bars — <b>bridging the gap between them</b> — and the
    ///         same band below. Two bars became an I-beam. It is not a thin-feature problem and no
    ///         amount of resolution or range reaches it; a plain square has it too, and only escapes
    ///         notice because its band has nothing to bridge.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The tie goes to the edge most perpendicular to the point, which is msdfgen's
    ///         <c>SignedDistance</c> ordering and not an invention here.</b> Of the two edges at a
    ///         corner, the one the point lies most nearly <i>alongside</i> is the one whose line is
    ///         being extended past its own end; the one it lies most nearly <i>off the side of</i> is
    ///         the one still describing a real boundary. Taking that one makes the median in the wedge
    ///         the smaller of the two half-plane distances — which is the intersection of the two half
    ///         planes, which is the sharp corner the three channels exist to keep. So this is what
    ///         makes a corner sharp <i>and</i> what stops it leaking; they were always the same
    ///         mechanism.
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

        // ⚠ Larger is worse, so <c>MaxValue</c> is "nothing has answered yet" for this as well —
        // and a first edge that is exactly alongside its point still beats it.
        var bestAlignment = float.MaxValue;
        var signed = 0f;

        foreach (var edge in edges) {
            // ⚠ <b>An exact rejection, not an approximate one.</b> Nothing on a segment can be nearer
            // to a point than the distance to its midpoint less its half length, so an edge whose
            // whole extent lies further away than the best so far cannot improve it and cannot reach
            // the assignment below — which is what makes skipping it produce the identical field
            // rather than a cheaper one. On the first edge `best` is <c>MaxValue</c> and the squared
            // reach is infinite, so nothing is skipped before there is an answer to skip against.
            //
            // ⚠ Strictly further, because an edge exactly at the reach is exactly tied — and a tie is
            // now an answer rather than a nuisance. A corner's second edge lands on this boundary.
            var toMiddle = point - edge.Middle;
            var reach = best + edge.HalfLength;

            if (toMiddle.LengthSquared() > reach * reach) {
                continue;
            }

            var t = Math.Clamp(Vector2.Dot(point - edge.From, edge.Direction) * edge.InverseLengthSquared, 0f, 1f);

            // ⚠ The endpoints are the stored ones rather than <c>From + Direction</c>, so that two
            // edges clamping to one shared vertex clamp to the <i>same</i> vertex. Reconstructing the
            // far end by addition loses the last bit, which is enough to turn an exact tie into a
            // near one — and the whole of the rule below is about what happens at that tie.
            var closest = t <= 0f ? edge.From : t >= 1f ? edge.To : edge.From + (t * edge.Direction);
            var offset = point - closest;
            var distanceSquared = offset.LengthSquared();

            // Clearly further than the best so far, which is the ordinary case and settles it without
            // the square root below.
            if (distanceSquared > bestSquared * (1f + Tie)) {
                continue;
            }

            var unit = edge.Direction * edge.InverseLength;
            var distance = MathF.Sqrt(distanceSquared);

            // How nearly the point lies <i>along</i> this edge rather than off the side of it: nought
            // is square on, one is straight off the end. Nought for anything the segment itself is
            // nearest to, which is what keeps a real edge ahead of a corner's extension.
            var alignment = (t > 0f && t < 1f) || distance <= 0f
                ? 0f
                : MathF.Abs(Vector2.Dot(unit, offset / distance));

            // Tied on distance, so the corner rule decides.
            if (distanceSquared >= bestSquared * (1f - Tie) && alignment >= bestAlignment) {
                continue;
            }

            bestSquared = distanceSquared;
            best = distance;
            bestAlignment = alignment;
            signed = winding * Cross(unit, point - edge.From);
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
                    edge.To,
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
        Vector2 To,
        Vector2 Direction,
        float InverseLengthSquared,
        float InverseLength,
        Vector2 Middle,
        float HalfLength
    );

    static float Cross(Vector2 a, Vector2 b) => (a.X * b.Y) - (a.Y * b.X);
}
