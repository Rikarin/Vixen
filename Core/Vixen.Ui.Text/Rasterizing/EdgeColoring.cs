// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Numerics;
using Vixen.Ui.Text.Outlines;

namespace Vixen.Ui.Text.Rasterizing;

/// <summary>Which of the three channels an edge contributes its distance to.</summary>
/// <remarks>
///     A channel mask rather than a colour: an edge normally carries two of the three, and only at a
///     corner do the two sides differ in which two.
/// </remarks>
[Flags]
public enum EdgeChannels {
    /// <summary>Carried by nothing.</summary>
    None = 0,

    /// <summary>The red channel.</summary>
    Red = 1,

    /// <summary>The green channel.</summary>
    Green = 2,

    /// <summary>The blue channel.</summary>
    Blue = 4,

    /// <summary>All three, which makes the field an ordinary one at that edge.</summary>
    White = Red | Green | Blue
}

/// <summary>An edge with the channels it contributes to.</summary>
/// <param name="From">Where it starts.</param>
/// <param name="To">Where it ends.</param>
/// <param name="Channels">Which channels take its distance.</param>
public readonly record struct ColouredEdge(Vector2 From, Vector2 To, EdgeChannels Channels);

/// <summary>
///     Decides which channels each edge of a contour contributes to.
/// </summary>
/// <remarks>
///     <para>
///         <b>This is the whole idea behind a multi-channel field.</b> A single distance field
///         rounds every corner, because the distance to the nearest edge is smooth across one. Give
///         the two sides of a corner different channels and each channel stays smooth on its own;
///         the median of the three then reconstructs the corner, because two of the three agree on
///         which side of the shape a point is on and the third is the one that rounded.
///     </para>
///     <para>
///         ⚠ <b>Only at a corner.</b> Along a smooth join the two segments must share their
///         channels, or the median sees a discontinuity where the outline has none and the glyph
///         grows a notch.
///     </para>
///     <para>
///         ⚠ <b>Corners are found from the outline's own tangents, never from the flattened
///         chords.</b> Two things go wrong with chords, and this cost two attempts. A curve cut into
///         twenty pieces has nineteen internal joins that each turn a few degrees, and any threshold
///         small enough to catch a shallow real corner calls all of them corners. And even at a
///         genuine segment boundary the two neighbouring chords differ by about one step's worth of
///         curvature, so a smooth join between two curves reads as a corner as well — which is what
///         a circle is made of, and it came out striped.
///     </para>
/// </remarks>
public static class EdgeColoring {
    /// <summary>How sharp a join has to be before it counts as a corner, as a cosine.</summary>
    /// <remarks>Roughly three degrees, which no smooth join in a font exceeds.</remarks>
    const float CornerCosine = 0.999f;

    /// <summary>The two-channel combinations, which alternate around a contour.</summary>
    static readonly EdgeChannels[] Alternating = [
        EdgeChannels.Red | EdgeChannels.Green,
        EdgeChannels.Green | EdgeChannels.Blue,
        EdgeChannels.Blue | EdgeChannels.Red
    ];

    /// <summary>Colours a flattened outline, using the outline itself to find the corners.</summary>
    /// <param name="outline">The outline the edges were flattened from.</param>
    /// <param name="edges">Its edges, each tagged with the segment it came from.</param>
    /// <returns>The same edges with channels assigned.</returns>
    public static List<ColouredEdge> Colour(GlyphOutline outline, List<Edge> edges) {
        ArgumentNullException.ThrowIfNull(outline);
        ArgumentNullException.ThrowIfNull(edges);

        var channels = ChannelsPerSegment(outline);
        var coloured = new List<ColouredEdge>(edges.Count);

        foreach (var edge in edges) {
            var channel = edge.Source >= 0 && edge.Source < channels.Length
                ? channels[edge.Source]
                : EdgeChannels.White;

            coloured.Add(new ColouredEdge(edge.From, edge.To, channel));
        }

        return coloured;
    }

    /// <summary>One channel set per segment of the outline.</summary>
    static EdgeChannels[] ChannelsPerSegment(GlyphOutline outline) {
        var segments = outline.Segments;
        var channels = new EdgeChannels[segments.Length];
        Array.Fill(channels, EdgeChannels.White);

        var contour = new List<int>();
        var cursor = Vector2.Zero;
        var start = Vector2.Zero;
        var points = new List<(Vector2 In, Vector2 Out)>();

        for (var i = 0; i < segments.Length; i++) {
            var segment = segments[i];

            switch (segment.Verb) {
                case OutlineVerb.Move:
                    Finish(channels, contour, points);
                    cursor = start = new Vector2(segment.X0, segment.Y0);
                    break;

                case OutlineVerb.Line: {
                    var to = new Vector2(segment.X0, segment.Y0);
                    Record(contour, points, i, to - cursor, to - cursor);
                    cursor = to;
                    break;
                }

                case OutlineVerb.Quadratic: {
                    var control = new Vector2(segment.X0, segment.Y0);
                    var to = new Vector2(segment.X1, segment.Y1);
                    Record(contour, points, i, Nonzero(control - cursor, to - cursor), Nonzero(to - control, to - cursor));
                    cursor = to;
                    break;
                }

                case OutlineVerb.Cubic: {
                    var first = new Vector2(segment.X0, segment.Y0);
                    var second = new Vector2(segment.X1, segment.Y1);
                    var to = new Vector2(segment.X2, segment.Y2);
                    Record(contour, points, i, Nonzero(first - cursor, to - cursor), Nonzero(to - second, to - cursor));
                    cursor = to;
                    break;
                }

                case OutlineVerb.Close:
                    if (cursor != start) {
                        Record(contour, points, i, start - cursor, start - cursor);
                    }

                    Finish(channels, contour, points);
                    cursor = start;
                    break;

                default:
                    break;
            }
        }

        Finish(channels, contour, points);
        return channels;
    }

    static void Record(List<int> contour, List<(Vector2 In, Vector2 Out)> points, int index, Vector2 entering, Vector2 leaving) {
        contour.Add(index);
        points.Add((entering, leaving));
    }

    static Vector2 Nonzero(Vector2 preferred, Vector2 fallback) => preferred == Vector2.Zero ? fallback : preferred;

    static void Finish(EdgeChannels[] channels, List<int> contour, List<(Vector2 In, Vector2 Out)> points) {
        var count = contour.Count;
        if (count == 0) {
            return;
        }

        var corners = new List<int>();
        for (var i = 0; i < count; i++) {
            var incoming = points[(i + count - 1) % count].Out;
            var outgoing = points[i].In;
            if (IsCorner(incoming, outgoing)) {
                corners.Add(i);
            }
        }

        // ⚠ A contour with no corner at all — an o, a dot, a bowl — gets one channel set throughout.
        // Alternating along a smooth curve would put a seam in the middle of it.
        if (corners.Count == 0) {
            foreach (var index in contour) {
                channels[index] = EdgeChannels.White;
            }

            contour.Clear();
            points.Clear();
            return;
        }

        // Which run each segment belongs to, walking from the first corner.
        var runOf = new int[count];
        var run = 0;

        for (var step = 0; step < count; step++) {
            var i = (corners[0] + step) % count;
            if (step > 0 && corners.Contains(i)) {
                run++;
            }

            runOf[i] = run;
        }

        var runs = run + 1;
        var colours = new EdgeChannels[runs];

        // ⚠ <b>A run must differ from its neighbour, and the last one wraps.</b> Cycling the three
        // combinations in order gives four corners the sequence RG, GB, BR, RG — and the fourth
        // corner, where the last run meets the first, then has both sides the same, which is exactly
        // the corner the three channels were supposed to keep. A square lost its corner by a third
        // of a texel until this picked around the collision instead of counting modulo three.
        for (var i = 0; i < runs; i++) {
            var previous = i == 0 ? EdgeChannels.None : colours[i - 1];
            var wrap = i == runs - 1 && runs > 1 ? colours[0] : EdgeChannels.None;

            colours[i] = Alternating.FirstOrDefault(
                candidate => candidate != previous && candidate != wrap,
                Alternating[i % Alternating.Length]
            );
        }

        for (var i = 0; i < count; i++) {
            channels[contour[i]] = colours[runOf[i]];
        }

        contour.Clear();
        points.Clear();
    }

    static bool IsCorner(Vector2 incoming, Vector2 outgoing) {
        if (incoming == Vector2.Zero || outgoing == Vector2.Zero) {
            return false;
        }

        return Vector2.Dot(Vector2.Normalize(incoming), Vector2.Normalize(outgoing)) < CornerCosine;
    }
}
