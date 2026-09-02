// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering;

namespace Vixen.Editor.SceneView;

/// <summary>
///     Drawing a spline in the viewport: the curve, its control points and its tangent handles.
/// </summary>
/// <remarks>
///     <para>
///         <b>[docs/plan/31 § T8]'s owed overlay.</b> <see cref="SplineEdit" /> is what a drag does to
///         a curve and it has been testable since it was written; what was missing is the half a
///         person aims at. Without it a spline is a set of numbers in a panel, which is the state
///         every curve tool is unusable in.
///     </para>
///     <para>
///         ⚠ <b>Line vertices rather than draw calls, and the reason is the test.</b> This produces
///         the same <see cref="LineVertex" /> list <c>SceneLines</c> produces for everything else, so
///         what it emits can be asserted without a viewport, a device or a window — and the failure
///         it is most likely to have is arithmetic, not binding.
///     </para>
///     <para>
///         ⚠ <b>Every method has a <see cref="GizmoDraw" /> overload, and that one is the
///         implementation.</b> A pane hands a drawer a <see cref="GizmoDraw" /> and never its list —
///         <c>SceneViewport.Cursor</c> and <c>ComponentGizmos</c> both — so an overlay that only took
///         a <c>List&lt;LineVertex&gt;</c> was one no viewport could call, which is what it was for
///         two phases. The list overloads wrap a <see cref="GizmoDraw" /> over the list rather than
///         emitting twice; a second copy of the arc-length sampling is the thing that would drift.
///     </para>
///     <para>
///         ⚠ <b>The curve is sampled by arc length, not by parameter.</b> A Hermite segment's
///         parameter runs at a different speed on every segment, so a fixed count per segment draws a
///         tight corner with the same number of lines as a straight kilometre — the corner reads as
///         faceted and the straight bit as wasteful. Sampling by distance spends the vertices where
///         the curve bends.
///     </para>
///     <para>
///         ⚠ <b>A tangent handle is drawn at a third of its length, which is Bézier's convention and
///         not an aesthetic one.</b> A Hermite tangent and its Bézier control point differ by exactly
///         that factor, and <see cref="SplineAsset.InsertOn" /> converts between them — so a handle
///         drawn at full length would sit somewhere the geometry never passes and a person dragging
///         it would be aiming at the wrong place.
///     </para>
/// </remarks>
public static class SplineOverlay {
    /// <summary>How far apart the curve is sampled, in metres.</summary>
    /// <remarks>
    ///     Half a metre, which is under a pixel at the distance a person edits a road from and cheap
    ///     enough that a kilometre of road is two thousand lines.
    /// </remarks>
    public const float SampleSpacing = 0.5f;

    /// <summary>The most samples one spline is drawn with, however long it is.</summary>
    /// <remarks>
    ///     ⚠ <b>A cap and not a hope.</b> A spline whose points an author dragged to opposite ends of
    ///     a level is tens of kilometres, and at half a metre that is a hundred thousand lines in a
    ///     list the viewport rebuilds every frame — which drops the editor rather than the road.
    /// </remarks>
    public const int MaximumSamples = 4096;

    /// <summary>What the curve itself is drawn in.</summary>
    public static Color4 CurveColour => new(0.35f, 0.75f, 1f, 1f);

    /// <summary>And a control point.</summary>
    public static Color4 PointColour => new(1f, 0.85f, 0.25f, 1f);

    /// <summary>And a selected one.</summary>
    public static Color4 SelectedColour => new(1f, 0.45f, 0.1f, 1f);

    /// <summary>And a tangent handle.</summary>
    /// <remarks>
    ///     Dimmer than a point, because a tangent is a thing you grab second — a viewport where the
    ///     handles are as loud as the points is one where the points are hard to see.
    /// </remarks>
    public static Color4 TangentColour => new(0.55f, 0.55f, 0.6f, 1f);

    /// <summary>Emits the curve as a line strip.</summary>
    /// <remarks>
    ///     Takes a built <see cref="Spline" /> rather than an asset, because building one is what
    ///     refuses a single control point — <see cref="Draw" /> is the entry point that guards.
    /// </remarks>
    /// <param name="spline">The curve.</param>
    /// <param name="into">Where the vertices go, appended as pairs.</param>
    /// <returns>How many line segments were emitted.</returns>
    /// <exception cref="ArgumentNullException">There is nowhere to put them.</exception>
    /// <remarks>
    ///     ⚠ <b>Pairs rather than a strip, because that is what the line renderer takes.</b> A strip
    ///     would halve the vertices and would make a closed spline's last segment a special case at
    ///     every call site.
    /// </remarks>
    public static int Curve(in Spline spline, List<LineVertex> into) {
        ArgumentNullException.ThrowIfNull(into);

        return Curve(spline, new GizmoDraw(into));
    }

    /// <summary>Emits the curve into a viewport channel.</summary>
    /// <param name="spline">The curve.</param>
    /// <param name="draw">Where the segments go — a pane's overlay or its depth-tested lines.</param>
    /// <returns>How many line segments were emitted.</returns>
    /// <exception cref="ArgumentNullException">There is nowhere to put them.</exception>
    public static int Curve(in Spline spline, GizmoDraw draw) {
        ArgumentNullException.ThrowIfNull(draw);

        if (spline.Points.Length < 2) {
            return 0;
        }

        var length = spline.Length;

        if (length <= 0f) {
            return 0;
        }

        var samples = Math.Clamp((int)MathF.Ceiling(length / SampleSpacing), 1, MaximumSamples);
        var previous = spline.EvaluateAtDistance(0f);
        var emitted = 0;

        for (var step = 1; step <= samples; step++) {
            var next = spline.EvaluateAtDistance(length * step / samples);

            draw.Line(previous, next, CurveColour);

            previous = next;
            emitted++;
        }

        return emitted;
    }

    /// <summary>Emits a control point's cross and its two tangent handles.</summary>
    /// <param name="point">The control point.</param>
    /// <param name="into">Where the vertices go.</param>
    /// <param name="size">How long the cross's arms are, in metres.</param>
    /// <param name="selected">Whether it is selected.</param>
    /// <returns>How many line segments were emitted.</returns>
    /// <exception cref="ArgumentNullException">There is nowhere to put them.</exception>
    public static int Point(in SplinePoint point, List<LineVertex> into, float size = 0.4f, bool selected = false) {
        ArgumentNullException.ThrowIfNull(into);

        return Point(point, new GizmoDraw(into), size, selected);
    }

    /// <summary>Emits a control point's cross and its two tangent handles into a viewport channel.</summary>
    /// <param name="point">The control point.</param>
    /// <param name="draw">Where the segments go.</param>
    /// <param name="size">How long the cross's arms are, in metres.</param>
    /// <param name="selected">Whether it is selected.</param>
    /// <returns>How many line segments were emitted.</returns>
    /// <exception cref="ArgumentNullException">There is nowhere to put them.</exception>
    public static int Point(in SplinePoint point, GizmoDraw draw, float size = 0.4f, bool selected = false) {
        ArgumentNullException.ThrowIfNull(draw);

        var colour = selected ? SelectedColour : PointColour;
        var arms = 0;

        foreach (var axis in (Vector3[]) [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ]) {
            draw.Line(point.Position - (axis * size), point.Position + (axis * size), colour);

            arms++;
        }

        // ⚠ A third of the tangent, *added* — which is `SplineAsset.InsertOn`'s own convention and
        // not a sign to be reasoned about afresh. That method builds the Bézier control points as
        // `start.Position + start.TangentOut / 3` and `end.Position + end.TangentIn / 3`, so
        // `TangentIn` already points backwards from its point. Negating it here would draw the
        // incoming handle on the outgoing side, and a person dragging it would move the curve the
        // other way.
        foreach (var tangent in (Vector3[]) [point.TangentIn, point.TangentOut]) {
            if (tangent.LengthSquared() <= 0f) {
                continue;
            }

            draw.Line(point.Position, point.Position + (tangent / 3f), TangentColour);

            arms++;
        }

        return arms;
    }

    /// <summary>Emits a whole spline: its curve, every point and every handle.</summary>
    /// <param name="asset">The authored curve.</param>
    /// <param name="into">Where the vertices go.</param>
    /// <param name="selected">Which control point is selected, or −1 for none.</param>
    /// <param name="size">How long a control point's arms are, in metres.</param>
    /// <returns>How many line segments were emitted.</returns>
    /// <exception cref="ArgumentNullException">There is no asset or nowhere to put them.</exception>
    /// <remarks>
    ///     ⚠ <b>The curve first and the handles after, because the line renderer draws in order and
    ///     does not sort.</b> Points drawn under the curve are points a person cannot see on a road
    ///     that runs along them, which is every road.
    /// </remarks>
    public static int Draw(SplineAsset asset, List<LineVertex> into, int selected = -1, float size = 0.4f) {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(into);

        return Draw(asset, new GizmoDraw(into), selected, size);
    }

    /// <summary>Emits a whole spline into a viewport channel: its curve, every point and every handle.</summary>
    /// <param name="asset">The authored curve.</param>
    /// <param name="draw">Where the segments go.</param>
    /// <param name="selected">Which control point is selected, or −1 for none.</param>
    /// <param name="size">How long a control point's arms are, in metres.</param>
    /// <returns>How many line segments were emitted.</returns>
    /// <exception cref="ArgumentNullException">There is no asset or nowhere to put them.</exception>
    /// <remarks>
    ///     ⚠ <b>The curve first and the handles after, because the line renderer draws in order and
    ///     does not sort.</b> Points drawn under the curve are points a person cannot see on a road
    ///     that runs along them, which is every road.
    /// </remarks>
    public static int Draw(SplineAsset asset, GizmoDraw draw, int selected = -1, float size = 0.4f) {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(draw);

        var emitted = 0;

        // ⚠ Asked rather than caught. `SplineAsset.Build` throws for one point, and one point is what
        // a spline looks like halfway through being authored — an overlay that let that exception out
        // would take the viewport down on the frame after the first click.
        if (asset.CanBuild) {
            emitted += Curve(asset.Build(), draw);
        }

        for (var index = 0; index < asset.Points.Count; index++) {
            emitted += Point(asset.Points[index], draw, size, index == selected);
        }

        return emitted;
    }
}
