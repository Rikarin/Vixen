// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering;
using Xunit;

namespace Vixen.Editor.SceneView.Tests;

/// <summary>The spline overlay — [docs/plan/31 § T8]'s owed half, the one a person aims at.</summary>
public sealed class SplineOverlayTests {
    static SplineAsset Road(int points = 4, float spacing = 20f) {
        var asset = new SplineAsset("Road", []);

        for (var index = 0; index < points; index++) {
            asset.Add(new SplinePoint(new(index * spacing, 0f, index % 2 == 0 ? 0f : 8f), Vector3.Zero, Vector3.Zero));
        }

        return asset;
    }

    [Fact]
    public void ACurveIsEmittedAsPairs() {
        var lines = new List<LineVertex>();
        var segments = SplineOverlay.Curve(Road().Build(), lines);

        Assert.True(segments > 0);
        Assert.Equal(segments * 2, lines.Count);
    }

    /// <summary>A curve with fewer than two points draws nothing rather than throwing.</summary>
    /// <remarks>
    ///     ⚠ <b>One point is what a spline looks like halfway through being authored.</b> An overlay
    ///     that threw there would take the viewport down on the frame after the first click.
    /// </remarks>
    [Fact]
    public void AnUnfinishedCurveDrawsItsPointAndNoCurve() {
        var lines = new List<LineVertex>();
        var one = new SplineAsset("Road", []);

        one.Add(new SplinePoint(Vector3.Zero, Vector3.Zero, Vector3.Zero));

        // ⚠ `Build` throws for one point, so `Draw` asks `CanBuild` rather than catching. The point
        // is still drawn: somebody who has clicked once has to be able to see where they clicked.
        Assert.False(one.CanBuild);
        Assert.Equal(3, SplineOverlay.Draw(one, lines));
        Assert.All(lines, vertex => Assert.NotEqual(SplineOverlay.CurveColour, vertex.Colour));
    }

    /// <summary>The curve is sampled by arc length, so a longer road gets more lines.</summary>
    /// <remarks>
    ///     ⚠ <b>A fixed count per segment draws a tight corner with the same number of lines as a
    ///     straight kilometre</b> — the corner reads as faceted and the straight bit as wasteful.
    /// </remarks>
    [Fact]
    public void ALongerCurveIsSampledMoreFinely() {
        var near = new List<LineVertex>();
        var far = new List<LineVertex>();

        var shortSegments = SplineOverlay.Curve(Road(points: 3, spacing: 5f).Build(), near);
        var longSegments = SplineOverlay.Curve(Road(points: 3, spacing: 50f).Build(), far);

        Assert.True(longSegments > shortSegments * 4, $"{longSegments} against {shortSegments}.");
    }

    /// <summary>However long it is, it never emits more than the cap.</summary>
    /// <remarks>
    ///     ⚠ <b>A cap and not a hope.</b> A spline whose points an author dragged to opposite ends of
    ///     a level is tens of kilometres, and at half a metre that is a hundred thousand lines in a
    ///     list the viewport rebuilds every frame — which drops the editor rather than the road.
    /// </remarks>
    [Fact]
    public void AnEnormousCurveIsCapped() {
        var lines = new List<LineVertex>();
        var segments = SplineOverlay.Curve(Road(points: 8, spacing: 20_000f).Build(), lines);

        Assert.Equal(SplineOverlay.MaximumSamples, segments);
    }

    /// <summary>A point is three arms and its two tangent handles.</summary>
    [Fact]
    public void APointIsACrossAndItsHandles() {
        var lines = new List<LineVertex>();
        var point = new SplinePoint(new(1f, 2f, 3f), new(0f, 0f, -6f), new(0f, 0f, 6f));

        Assert.Equal(5, SplineOverlay.Point(point, lines));
        Assert.Equal(10, lines.Count);
    }

    /// <summary>A handle is drawn at a third of the tangent, which is where its Bézier point is.</summary>
    /// <remarks>
    ///     ⚠ <b>A Hermite tangent and its Bézier control point differ by exactly that factor</b>, and
    ///     <c>SplineAsset.InsertOn</c> converts between them — so a handle drawn at full length would
    ///     sit somewhere the geometry never passes and a person dragging it would be aiming at the
    ///     wrong place.
    /// </remarks>
    [Fact]
    public void AHandleIsAThirdOfItsTangent() {
        var lines = new List<LineVertex>();
        var point = new SplinePoint(Vector3.Zero, new(0f, 0f, -9f), new(0f, 0f, 9f));

        SplineOverlay.Point(point, lines);

        // The last four vertices are the two handles: origin, tip, origin, tip.
        var outgoing = lines[^1].Position;
        var incoming = lines[^3].Position;

        // ⚠ Both *added*, which is `SplineAsset.InsertOn`'s convention: it builds the Bézier control
        // points as `position + tangent / 3` on both sides, so `TangentIn` already points backwards
        // from its point. Negating it here would draw the incoming handle on the outgoing side, and a
        // person dragging it would move the curve the other way.
        Assert.Equal(3f, outgoing.Z, 4);
        Assert.Equal(-3f, incoming.Z, 4);
    }

    /// <summary>A collapsed tangent draws no handle rather than a zero-length line.</summary>
    [Fact]
    public void ACollapsedTangentHasNoHandle() {
        var lines = new List<LineVertex>();
        var point = new SplinePoint(Vector3.Zero, Vector3.Zero, Vector3.Zero);

        Assert.Equal(3, SplineOverlay.Point(point, lines));
    }

    /// <summary>A selected point is drawn in a different colour.</summary>
    [Fact]
    public void ASelectedPointIsColouredDifferently() {
        var plain = new List<LineVertex>();
        var chosen = new List<LineVertex>();
        var point = new SplinePoint(Vector3.Zero, Vector3.Zero, Vector3.Zero);

        SplineOverlay.Point(point, plain);
        SplineOverlay.Point(point, chosen, selected: true);

        Assert.NotEqual(plain[0].Colour, chosen[0].Colour);
        Assert.Equal(SplineOverlay.SelectedColour, chosen[0].Colour);
    }

    /// <summary>The whole spline draws its curve first, then its handles.</summary>
    /// <remarks>
    ///     ⚠ <b>The line renderer draws in order and does not sort.</b> Points drawn under the curve
    ///     are points a person cannot see on a road that runs along them, which is every road.
    /// </remarks>
    [Fact]
    public void TheCurveIsDrawnBeforeTheHandles() {
        var lines = new List<LineVertex>();
        var asset = Road();

        SplineOverlay.Draw(asset, lines);

        Assert.Equal(SplineOverlay.CurveColour, lines[0].Colour);

        var firstPoint = lines.FindIndex(vertex => vertex.Colour == SplineOverlay.PointColour);
        var lastCurve = lines.FindLastIndex(vertex => vertex.Colour == SplineOverlay.CurveColour);

        Assert.True(firstPoint > lastCurve, "a control point was drawn before the curve was finished.");
    }

    [Fact]
    public void DrawingNothingIsRefused() {
        Assert.Throws<ArgumentNullException>(() => SplineOverlay.Draw(null!, []));
        Assert.Throws<ArgumentNullException>(() => SplineOverlay.Draw(Road(), null!));
    }
}
