// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Ui;

/// <summary>What one step of a path does.</summary>
public enum PathVerb : byte {
    /// <summary>Lifts the pen and puts it down somewhere, starting a new contour.</summary>
    Move,

    /// <summary>A straight line to a point.</summary>
    Line,

    /// <summary>A quadratic Bézier through one control point.</summary>
    Quadratic,

    /// <summary>A cubic Bézier through two.</summary>
    Cubic,

    /// <summary>Joins the current contour back to where it started.</summary>
    Close
}

/// <summary>One step of a path.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>One fixed-size struct for every verb</b>, with the points a verb does not use left at
///         zero. Skia's design is a verb array beside a point array, which is smaller — a line costs
///         one point rather than three — and it needs two ranges on the command that refers to it and
///         two cursors to walk. This keeps the frame diff a comparison of one contiguous array and
///         the command's reference to it one range, which is worth more here than the bytes.
///     </para>
///     <para>
///         The end point is always <see cref="P2" />, whatever the verb, so walking a path for its
///         current position never has to switch on the verb first.
///     </para>
/// </remarks>
/// <param name="Verb">What this step does.</param>
/// <param name="P0">The first control point, for a curve.</param>
/// <param name="P1">The second, for a cubic.</param>
/// <param name="P2">Where the pen ends up.</param>
public readonly record struct PathSegment(PathVerb Verb, Vector2 P0, Vector2 P1, Vector2 P2);

/// <summary>How a path decides what is inside it.</summary>
public enum PathFillRule : byte {
    /// <summary>Inside if a ray out of it crosses more contours one way than the other.</summary>
    /// <remarks>
    ///     What a renderer does by default and what most drawing means. Two overlapping circles wound
    ///     the same way fill as one shape.
    /// </remarks>
    NonZero,

    /// <summary>Inside if a ray out of it crosses an odd number of contours.</summary>
    /// <remarks>
    ///     What punches a hole regardless of which way the inner contour is wound, which is how most
    ///     icon sets draw a letter <c>o</c> — and why leaving it out would be noticed on the first
    ///     icon someone imported.
    /// </remarks>
    EvenOdd
}

/// <summary>Builds a path out of lines and curves.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Curves are kept as curves.</b> Nothing here flattens a Bézier into line segments, and
///         that is deliberate: how finely to flatten depends on how large the curve will be on
///         screen, which is a device scale this does not know. Flattened here, a path built once and
///         drawn at two zoom levels is faceted at one of them or over-tessellated at the other, and
///         nothing downstream can recover the curve to do better.
///     </para>
///     <para>
///         Owned by the caller and reusable — <see cref="Clear" /> keeps the capacity. A control that
///         draws every frame should keep one in a field rather than making a new one, for the same
///         reason any other per-frame allocation is worth avoiding.
///     </para>
/// </remarks>
public sealed class PathBuilder {
    readonly List<PathSegment> segments = [];
    Vector2 start;
    Vector2 current;

    /// <summary>The steps, in order.</summary>
    public IReadOnlyList<PathSegment> Segments => segments;

    /// <summary>How many steps it has.</summary>
    public int Count => segments.Count;

    /// <summary>Where the pen is.</summary>
    public Vector2 Current => current;

    /// <summary>Empties it, keeping the memory.</summary>
    /// <returns>Itself, so a build can start with it.</returns>
    public PathBuilder Clear() {
        segments.Clear();
        start = default;
        current = default;

        return this;
    }

    /// <summary>Lifts the pen and puts it down, starting a new contour.</summary>
    /// <param name="point">Where.</param>
    /// <returns>Itself.</returns>
    public PathBuilder MoveTo(Vector2 point) {
        segments.Add(new PathSegment(PathVerb.Move, default, default, point));
        start = point;
        current = point;

        return this;
    }

    /// <summary>Draws a straight line.</summary>
    /// <param name="point">To where.</param>
    /// <returns>Itself.</returns>
    public PathBuilder LineTo(Vector2 point) {
        segments.Add(new PathSegment(PathVerb.Line, default, default, point));
        current = point;

        return this;
    }

    /// <summary>Draws a quadratic Bézier.</summary>
    /// <param name="control">Its control point.</param>
    /// <param name="point">Its end.</param>
    /// <returns>Itself.</returns>
    public PathBuilder QuadraticTo(Vector2 control, Vector2 point) {
        segments.Add(new PathSegment(PathVerb.Quadratic, control, default, point));
        current = point;

        return this;
    }

    /// <summary>Draws a cubic Bézier.</summary>
    /// <param name="first">Its first control point.</param>
    /// <param name="second">Its second.</param>
    /// <param name="point">Its end.</param>
    /// <returns>Itself.</returns>
    public PathBuilder CubicTo(Vector2 first, Vector2 second, Vector2 point) {
        segments.Add(new PathSegment(PathVerb.Cubic, first, second, point));
        current = point;

        return this;
    }

    /// <summary>Joins the current contour back to where it started.</summary>
    /// <returns>Itself.</returns>
    /// <remarks>
    ///     ⚠ <b>Carries the point it closes to</b> rather than leaving the renderer to remember it.
    ///     A stroked path's closing join is drawn differently from a line back to the same place, so
    ///     the verb has to survive — and a consumer that wants the coordinates should not have to
    ///     walk backwards to the last <see cref="MoveTo" /> to find them.
    /// </remarks>
    public PathBuilder Close() {
        segments.Add(new PathSegment(PathVerb.Close, default, default, start));
        current = start;

        return this;
    }

    /// <summary>Adds a rectangle as its own closed contour.</summary>
    /// <param name="rectangle">The rectangle.</param>
    /// <returns>Itself.</returns>
    /// <remarks>Clockwise, which is what the non-zero fill rule reads as solid.</remarks>
    public PathBuilder AddRectangle(Rectangle rectangle) =>
        MoveTo(new Vector2(rectangle.Left, rectangle.Top))
            .LineTo(new Vector2(rectangle.Right, rectangle.Top))
            .LineTo(new Vector2(rectangle.Right, rectangle.Bottom))
            .LineTo(new Vector2(rectangle.Left, rectangle.Bottom))
            .Close();

    /// <summary>Adds an ellipse inscribed in a rectangle.</summary>
    /// <param name="bounds">What to inscribe it in.</param>
    /// <returns>Itself.</returns>
    /// <remarks>
    ///     Four cubics with the usual constant. A Bézier cannot be a circular arc exactly, and
    ///     <c>4/3 × tan(π/8)</c> is the control-point distance that minimises the error of a quarter
    ///     turn — about one part in ten thousand of the radius, which is invisible at any size a user
    ///     interface draws at. Written out rather than derived, because deriving it at runtime would
    ///     be arithmetic in a hot path to reproduce a constant.
    /// </remarks>
    public PathBuilder AddEllipse(Rectangle bounds) {
        const float Kappa = 0.5522847498f;

        var rx = bounds.Width * 0.5f;
        var ry = bounds.Height * 0.5f;
        var cx = bounds.X + rx;
        var cy = bounds.Y + ry;
        var ox = rx * Kappa;
        var oy = ry * Kappa;

        return MoveTo(new Vector2(cx, cy - ry))
            .CubicTo(new Vector2(cx + ox, cy - ry), new Vector2(cx + rx, cy - oy), new Vector2(cx + rx, cy))
            .CubicTo(new Vector2(cx + rx, cy + oy), new Vector2(cx + ox, cy + ry), new Vector2(cx, cy + ry))
            .CubicTo(new Vector2(cx - ox, cy + ry), new Vector2(cx - rx, cy + oy), new Vector2(cx - rx, cy))
            .CubicTo(new Vector2(cx - rx, cy - oy), new Vector2(cx - ox, cy - ry), new Vector2(cx, cy - ry))
            .Close();
    }
}
