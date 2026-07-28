// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Numerics;
using Vixen.Ui.Text.Outlines;

namespace Vixen.Ui.Text.Rasterizing;

/// <summary>One straight edge of a flattened contour.</summary>
/// <param name="From">Where it starts.</param>
/// <param name="To">Where it ends.</param>
/// <param name="Source">Which of the outline's segments this is a piece of.</param>
/// <remarks>
///     ⚠ <b><see cref="Source" /> is what keeps flattening from inventing corners.</b> A curve cut
///     into twenty chords has nineteen joins that turn by a few degrees each, and every one of them
///     looks exactly like a shallow corner to anything reading the flattened edges alone. Carrying
///     the segment each chord came from means a corner can be looked for where the outline actually
///     has one — at the joins between its own segments.
/// </remarks>
public readonly record struct Edge(Vector2 From, Vector2 To, int Source);

/// <summary>Turns an outline's curves into line segments, at a tolerance the caller chooses.</summary>
/// <remarks>
///     <para>
///         <b>This is where the decision to keep curves as curves is finally spent.</b>
///         <see cref="GlyphOutline" /> holds Béziers because how finely to flatten one depends on how
///         large it will be, and nothing knew that until now: a caller rasterising into an N-pixel
///         cell knows exactly how much error is invisible.
///     </para>
///     <para>
///         ⚠ <b>The subdivision count comes from the curve's control polygon, not from a constant.</b>
///         A fixed count is either wasteful on a nearly-straight curve — most of a glyph's segments —
///         or visibly faceted on a tight one, and a glyph has both within one contour.
///     </para>
/// </remarks>
public static class OutlineFlattener {
    /// <summary>The most pieces one curve is ever cut into.</summary>
    /// <remarks>
    ///     A bound rather than a target. Without it a pathological control polygon — a curve that
    ///     doubles back on itself across the whole em — asks for thousands of segments to describe a
    ///     shape a distance field samples at 32 pixels.
    /// </remarks>
    const int MaxSubdivisions = 64;

    /// <summary>Flattens an outline into edges, in the outline's own units.</summary>
    /// <param name="outline">What to flatten.</param>
    /// <param name="tolerance">
    ///     How far a chord may sit from the curve it replaces, in the outline's units. A rasteriser
    ///     passes something under half a pixel.
    /// </param>
    /// <returns>Every edge of every contour, each contour closed.</returns>
    public static List<Edge> Flatten(GlyphOutline outline, float tolerance) {
        ArgumentNullException.ThrowIfNull(outline);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tolerance);

        var edges = new List<Edge>(outline.Segments.Length * 4);
        var cursor = Vector2.Zero;
        var start = Vector2.Zero;
        var source = -1;

        foreach (var segment in outline.Segments) {
            source++;
            switch (segment.Verb) {
                case OutlineVerb.Move:
                    // ⚠ An unclosed contour is closed here rather than left open. A rasteriser fills
                    // by winding, and an open contour lets the fill leak along whatever line the
                    // scanline happens to cross next.
                    Close(edges, ref cursor, start, source);
                    cursor = start = new Vector2(segment.X0, segment.Y0);
                    break;

                case OutlineVerb.Line: {
                    var to = new Vector2(segment.X0, segment.Y0);
                    Add(edges, cursor, to, source);
                    cursor = to;
                    break;
                }

                case OutlineVerb.Quadratic: {
                    var control = new Vector2(segment.X0, segment.Y0);
                    var to = new Vector2(segment.X1, segment.Y1);
                    Quadratic(edges, cursor, control, to, tolerance, source);
                    cursor = to;
                    break;
                }

                case OutlineVerb.Cubic: {
                    var first = new Vector2(segment.X0, segment.Y0);
                    var second = new Vector2(segment.X1, segment.Y1);
                    var to = new Vector2(segment.X2, segment.Y2);
                    Cubic(edges, cursor, first, second, to, tolerance, source);
                    cursor = to;
                    break;
                }

                case OutlineVerb.Close:
                    Close(edges, ref cursor, start, source);
                    break;

                default:
                    break;
            }
        }

        Close(edges, ref cursor, start, source);
        return edges;
    }

    static void Close(List<Edge> edges, ref Vector2 cursor, Vector2 start, int source) {
        if (cursor != start) {
            Add(edges, cursor, start, source);
            cursor = start;
        }
    }

    static void Add(List<Edge> edges, Vector2 from, Vector2 to, int source) {
        if (from != to) {
            edges.Add(new Edge(from, to, source));
        }
    }

    static void Quadratic(List<Edge> edges, Vector2 from, Vector2 control, Vector2 to, float tolerance, int source) {
        // How far the control point strays from the chord bounds the curve's own deviation.
        var steps = Steps(Vector2.Distance(from, control) + Vector2.Distance(control, to), tolerance);
        var previous = from;

        for (var i = 1; i <= steps; i++) {
            var t = (float)i / steps;
            var u = 1 - t;
            var point = (u * u * from) + (2 * u * t * control) + (t * t * to);
            Add(edges, previous, point, source);
            previous = point;
        }
    }

    static void Cubic(List<Edge> edges, Vector2 from, Vector2 first, Vector2 second, Vector2 to, float tolerance, int source) {
        var polygon = Vector2.Distance(from, first) + Vector2.Distance(first, second) + Vector2.Distance(second, to);
        var steps = Steps(polygon, tolerance);
        var previous = from;

        for (var i = 1; i <= steps; i++) {
            var t = (float)i / steps;
            var u = 1 - t;
            var point = (u * u * u * from)
                        + (3 * u * u * t * first)
                        + (3 * u * t * t * second)
                        + (t * t * t * to);
            Add(edges, previous, point, source);
            previous = point;
        }
    }

    /// <summary>How many pieces a curve of that control-polygon length needs.</summary>
    /// <remarks>
    ///     The chord of a curve cut into <c>n</c> pieces deviates by roughly <c>L / n²</c>, so the
    ///     count grows with the square root of the length over the tolerance.
    /// </remarks>
    static int Steps(float polygon, float tolerance) =>
        Math.Clamp((int)Math.Ceiling(Math.Sqrt(polygon / tolerance)), 1, MaxSubdivisions);
}
