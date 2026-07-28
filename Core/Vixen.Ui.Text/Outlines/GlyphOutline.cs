// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;

namespace Vixen.Ui.Text.Outlines;

/// <summary>What one segment of a glyph's outline is.</summary>
public enum OutlineVerb {
    /// <summary>Start a contour at a point.</summary>
    Move,

    /// <summary>A straight line to a point.</summary>
    Line,

    /// <summary>A quadratic Bézier: one control point, then the end. What TrueType draws with.</summary>
    Quadratic,

    /// <summary>A cubic Bézier: two control points, then the end. What CFF draws with.</summary>
    Cubic,

    /// <summary>Close the contour back to where it started.</summary>
    Close
}

/// <summary>One segment of an outline: a verb and the points it needs.</summary>
/// <param name="Verb">Which kind.</param>
/// <param name="X0">First point's x — the end for a move or line, the first control otherwise.</param>
/// <param name="Y0">First point's y.</param>
/// <param name="X1">Second point's x. The end of a quadratic, the second control of a cubic.</param>
/// <param name="Y1">Second point's y.</param>
/// <param name="X2">Third point's x. The end of a cubic.</param>
/// <param name="Y2">Third point's y.</param>
/// <remarks>
///     One fixed-size struct per verb rather than a verb array beside a point array — the same
///     decision, for the same reason, as <c>PathBuilder</c> in <c>Vixen.Ui</c>: the split form is
///     smaller and needs two cursors to walk and two ranges to name.
/// </remarks>
public readonly record struct OutlineSegment(
    OutlineVerb Verb,
    float X0,
    float Y0,
    float X1 = 0,
    float Y1 = 0,
    float X2 = 0,
    float Y2 = 0
);

/// <summary>A glyph's contours, in the font's design units.</summary>
/// <remarks>
///     <para>
///         <b>Curves are kept as curves.</b> How finely to flatten one depends on how large it will
///         be on screen, and a distance field wants the curve itself — the exact distance to a
///         Bézier is a closed form, and a flattened one is a polygon whose facets show up as ripples
///         in the field.
///     </para>
///     <para>
///         ⚠ <b>Both quadratic and cubic segments appear, and neither is converted to the other.</b>
///         TrueType draws in quadratics and CFF in cubics. A quadratic promotes to a cubic exactly,
///         so that direction would be lossless — but it doubles the control points a distance
///         function has to solve against for no gain, and the other direction is an approximation.
///         A consumer handles both verbs.
///     </para>
/// </remarks>
public sealed class GlyphOutline {
    internal GlyphOutline(ImmutableArray<OutlineSegment> segments) => Segments = segments;

    /// <summary>An outline with nothing in it — a space, or a glyph the font does not draw.</summary>
    public static GlyphOutline Empty { get; } = new([]);

    /// <summary>The segments, in order, contour by contour.</summary>
    public ImmutableArray<OutlineSegment> Segments { get; }

    /// <summary>Whether the glyph draws nothing.</summary>
    public bool IsEmpty => Segments.IsDefaultOrEmpty;

    /// <summary>The bounds of the drawn curve, in design units.</summary>
    /// <param name="samples">How finely each curve is sampled. Higher is tighter and slower.</param>
    /// <returns>The box, or all zeroes for an empty outline.</returns>
    /// <remarks>
    ///     ⚠ <b>Sampled, not solved.</b> The exact bounds of a Bézier need its derivative's roots,
    ///     which is worth writing when something depends on the last half-unit; nothing does yet, and
    ///     an atlas pads its cells anyway. Named <c>samples</c> rather than hidden so the cost is the
    ///     caller's to choose.
    /// </remarks>
    public GlyphBounds Bounds(int samples = 32) {
        if (IsEmpty) {
            return default;
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(samples, 1);

        var minX = float.MaxValue;
        var minY = float.MaxValue;
        var maxX = float.MinValue;
        var maxY = float.MinValue;

        float x = 0, y = 0, startX = 0, startY = 0;

        void Hit(float px, float py) {
            minX = Math.Min(minX, px);
            minY = Math.Min(minY, py);
            maxX = Math.Max(maxX, px);
            maxY = Math.Max(maxY, py);
        }

        foreach (var segment in Segments) {
            switch (segment.Verb) {
                case OutlineVerb.Move:
                    x = startX = segment.X0;
                    y = startY = segment.Y0;
                    Hit(x, y);
                    break;

                case OutlineVerb.Line:
                    x = segment.X0;
                    y = segment.Y0;
                    Hit(x, y);
                    break;

                case OutlineVerb.Quadratic:
                    for (var i = 1; i <= samples; i++) {
                        var t = (float)i / samples;
                        var u = 1 - t;
                        Hit(
                            (u * u * x) + (2 * u * t * segment.X0) + (t * t * segment.X1),
                            (u * u * y) + (2 * u * t * segment.Y0) + (t * t * segment.Y1)
                        );
                    }

                    x = segment.X1;
                    y = segment.Y1;
                    break;

                case OutlineVerb.Cubic:
                    for (var i = 1; i <= samples; i++) {
                        var t = (float)i / samples;
                        var u = 1 - t;
                        Hit(
                            (u * u * u * x) + (3 * u * u * t * segment.X0) + (3 * u * t * t * segment.X1)
                            + (t * t * t * segment.X2),
                            (u * u * u * y) + (3 * u * u * t * segment.Y0) + (3 * u * t * t * segment.Y1)
                            + (t * t * t * segment.Y2)
                        );
                    }

                    x = segment.X2;
                    y = segment.Y2;
                    break;

                case OutlineVerb.Close:
                    x = startX;
                    y = startY;
                    break;

                default:
                    break;
            }
        }

        return new GlyphBounds(minX, minY, maxX, maxY);
    }
}

/// <summary>A glyph's extent, in design units, y up.</summary>
/// <param name="MinX">Left edge.</param>
/// <param name="MinY">Bottom edge — below the baseline is negative.</param>
/// <param name="MaxX">Right edge.</param>
/// <param name="MaxY">Top edge.</param>
public readonly record struct GlyphBounds(float MinX, float MinY, float MaxX, float MaxY) {
    /// <summary>How wide.</summary>
    public float Width => MaxX - MinX;

    /// <summary>How tall.</summary>
    public float Height => MaxY - MinY;
}
