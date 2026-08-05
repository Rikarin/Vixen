// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using CsCheck;
using Vixen.Core.Mathematics;
using Vixen.Water;
using Xunit;

namespace Vixen.Water.Tests;

/// <summary>
///     Bodies rasterised into one field — [docs/plan/35 § D3], and W1's first exit criterion.
/// </summary>
/// <remarks>
///     <para>
///         The claim being checked is that a river spline carrying per-point width, depth and
///         velocity produces a field whose values at sampled positions are the ones somebody worked
///         out by hand. Everything downstream — the material's colour, the wave attenuation, the
///         foam's gradient, the shoreline — reads this field and nothing else, so an arithmetic
///         mistake here is a mistake everywhere at once and looks like six unrelated bugs.
///     </para>
///     <para>
///         ⚠ The texel-snap test is the one that would otherwise be found by eye, at one camera
///         position, and attributed to the wrong thing.
///     </para>
/// </remarks>
public sealed class WaterFieldTests {
    /// <summary>A straight river along +X at y = 10, four metres wide, flowing at 2 m/s.</summary>
    /// <remarks>
    ///     Straight and axis-aligned deliberately: every expectation below is then a number a reader
    ///     can check without evaluating a curve, which is what "matches hand-computed expectations"
    ///     has to mean to be worth asserting.
    /// </remarks>
    static WaterBody River(float velocity = 2f, float halfWidth = 2f, float depth = 1.5f) {
        var spline = new Spline([
            SplinePoint.Smooth(new(0f, 10f, 0f), new(20f, 0f, 0f)),
            SplinePoint.Smooth(new(20f, 10f, 0f), new(20f, 0f, 0f)),
            SplinePoint.Smooth(new(40f, 10f, 0f), new(20f, 0f, 0f))
        ]);

        return new(
            WaterBodyKind.River,
            spline,
            defaults: new() {
                HalfWidth = halfWidth,
                Depth = depth,
                Velocity = velocity,
                AudioIntensity = 0.5f
            }
        ) {
            ShoreFalloff = 1f,
            BedRamp = 2f
        };
    }

    /// <summary>A square lake, twenty metres on a side, surfaced at y = 5.</summary>
    static WaterBody Lake(float surface = 5f, float depth = 3f) {
        var spline = new Spline(
            Spline.SmoothTangents(
                [new(-10f, surface, -10f), new(10f, surface, -10f), new(10f, surface, 10f), new(-10f, surface, 10f)],
                closed: true,
                tension: 1f
            ),
            closed: true
        );

        return new(WaterBodyKind.Lake, spline, defaults: new() { Depth = depth, AudioIntensity = 0.1f }) {
            SurfaceHeight = surface,
            ShoreFalloff = 1f,
            BedRamp = 3f
        };
    }

    // --- The body ------------------------------------------------------------

    /// <summary>A river's channel is its half-width, and its surface follows its own curve.</summary>
    /// <remarks>
    ///     ⚠ The second half is the whole difference between a river and a bent lake. A body whose
    ///     surface is one height for every point cannot run downhill, and a river that cannot run
    ///     downhill is a canal drawn in a curve.
    /// </remarks>
    [Fact]
    public void ARiversChannelIsItsHalfWidthAndItsSurfaceFollowsTheCurve() {
        var river = River();

        // On the centreline: fully covered, surface at the spline's own height, flowing along +X.
        var middle = river.Sample(new(20f, 0f));

        Assert.Equal(1f, middle.Coverage, 1e-4f);
        Assert.Equal(10f, middle.SurfaceHeight, 1e-3f);
        Assert.Equal(2f, middle.Flow.X, 1e-3f);
        Assert.Equal(0f, middle.Flow.Y, 1e-3f);

        // At the channel edge: still fully covered — the falloff starts *outside* the half-width.
        Assert.Equal(1f, river.Sample(new(20f, 2f)).Coverage, 1e-3f);

        // Half a metre past it, in a one-metre falloff: half way down the smooth ramp, which is
        // smoothstep(0.5) = 0.5.
        Assert.Equal(0.5f, river.Sample(new(20f, 2.5f)).Coverage, 1e-3f);

        // And past the falloff, nothing at all.
        Assert.Equal(0f, river.Sample(new(20f, 3.5f)).Coverage);
    }

    /// <summary>A river's bed is at its full depth in the middle and at the surface at the edge.</summary>
    /// <remarks>
    ///     Unreal's <c>Curve Ramp Width</c>. ⚠ Without it a lake is a hole with vertical sides, and
    ///     the shoreline is a cliff that the surface meets at a line — which the eye reads as a cut in
    ///     the terrain rather than as a shore.
    /// </remarks>
    [Fact]
    public void TheBedRampsFromTheShorelineToItsFullDepth() {
        var river = River(depth: 1.5f);

        Assert.Equal(1.5f, river.Sample(new(20f, 0f)).BedDepth, 1e-3f);
        Assert.Equal(0f, river.Sample(new(20f, 2f)).BedDepth, 1e-3f);

        // One metre inside a two-metre ramp: smoothstep(0.5) = 0.5 of the depth.
        Assert.Equal(0.75f, river.Sample(new(20f, 1f)).BedDepth, 1e-3f);
    }

    /// <summary>A lake is bounded by its own spline and does not flow.</summary>
    /// <remarks>
    ///     ⚠ A lake with a velocity would be a lake whose whole surface drifts in one direction and
    ///     never arrives anywhere, which is not a thing water does. A current in a bay is a river body
    ///     laid through it.
    /// </remarks>
    [Fact]
    public void ALakeIsItsPolygonAndHasNoFlow() {
        var lake = Lake();

        Assert.True(lake.Contains(Vector2.Zero));
        Assert.False(lake.Contains(new(30f, 0f)));

        var inside = lake.Sample(Vector2.Zero);

        Assert.Equal(1f, inside.Coverage, 1e-4f);
        Assert.Equal(5f, inside.SurfaceHeight, 1e-4f);
        Assert.Equal(Vector2.Zero, inside.Flow);

        Assert.Equal(0f, lake.Sample(new(30f, 0f)).Coverage);
    }

    /// <summary>An open body refuses to be a kind that needs an inside.</summary>
    /// <remarks>
    ///     ⚠ An open curve has no inside, and the containment test would answer arbitrarily depending
    ///     on which way the ray was cast — so a lake drawn without closing its spline would have a
    ///     surface whose shape depended on the world origin.
    /// </remarks>
    [Fact]
    public void AClosedKindNeedsAClosedSpline() {
        var open = new Spline([
            SplinePoint.At(new(0f, 0f, 0f)),
            SplinePoint.At(new(10f, 0f, 0f))
        ]);

        Assert.Throws<ArgumentException>(() => new WaterBody(WaterBodyKind.Lake, open));
        Assert.Throws<ArgumentException>(() => new WaterBody(WaterBodyKind.Ocean, open));

        // A river is the one kind that is open by definition.
        _ = new WaterBody(WaterBodyKind.River, open);
    }

    /// <summary>A profile whose length disagrees with the spline's is refused rather than reassigned.</summary>
    [Fact]
    public void AProfileThatDoesNotMatchTheSplineIsRefused() {
        var spline = new Spline([
            SplinePoint.At(new(0f, 0f, 0f)),
            SplinePoint.At(new(10f, 0f, 0f)),
            SplinePoint.At(new(20f, 0f, 0f))
        ]);

        Assert.Throws<ArgumentException>(
            () => new WaterBody(WaterBodyKind.River, spline, [WaterProfilePoint.Stream, WaterProfilePoint.Stream])
        );
    }

    /// <summary>The profile interpolates between control points, so a river widens where it was told to.</summary>
    [Fact]
    public void TheProfileInterpolatesAlongTheCurve() {
        var spline = new Spline([
            SplinePoint.Smooth(new(0f, 0f, 0f), new(20f, 0f, 0f)),
            SplinePoint.Smooth(new(20f, 0f, 0f), new(20f, 0f, 0f))
        ]);

        var body = new WaterBody(
            WaterBodyKind.River,
            spline,
            [
                new() { HalfWidth = 2f, Depth = 1f, Velocity = 3f, AudioIntensity = 1f },
                new() { HalfWidth = 6f, Depth = 3f, Velocity = 1f, AudioIntensity = 0f }
            ]
        );

        Assert.Equal(2f, body.ProfileAt(0f).HalfWidth, 1e-4f);
        Assert.Equal(4f, body.ProfileAt(0.5f).HalfWidth, 1e-4f);
        Assert.Equal(6f, body.ProfileAt(1f).HalfWidth, 1e-4f);

        // Velocity runs the other way, which is what a river slowing as it widens looks like.
        Assert.Equal(2f, body.ProfileAt(0.5f).Velocity, 1e-4f);
    }

    /// <summary>Flow is continuous along the curve — [docs/plan/35 § Part 4]'s property test.</summary>
    /// <remarks>
    ///     ⚠ A discontinuity in the flow is a line across the river where anything floating on it
    ///     changes speed instantly, which reads as a physics glitch rather than as a current.
    /// </remarks>
    [Fact]
    public void FlowIsContinuousInTheSplineParameter() {
        var river = River();
        var previous = river.Sample(new(0.5f, 0f)).Flow;

        for (var step = 1; step <= 400; step++) {
            var flow = river.Sample(new(0.5f + (step * 0.09f), 0f)).Flow;

            Assert.True(
                Vector2.Distance(flow, previous) < 0.05f,
                $"the flow jumped by {Vector2.Distance(flow, previous)} m/s in nine centimetres"
            );

            previous = flow;
        }
    }

    // --- The field -----------------------------------------------------------

    /// <summary>The rasterised field carries the four channels, at the values the body gave.</summary>
    /// <remarks>
    ///     W1's exit criterion, stated as arithmetic a reader can follow: the river is straight, the
    ///     ground is flat, and every number below follows from the two.
    /// </remarks>
    [Fact]
    public void TheFieldCarriesWhatTheBodySaid() {
        var field = new WaterField(new() { Origin = new(0f, -16f), Extent = 32f, Resolution = 33 });

        // Ground at 9.5, which is above the bed the river wants — so the river cuts into it.
        field.Rasterize([River()], new FlatWaterGround(9.5f));

        // On the centreline (world z = 0 is texel row 16), the surface is the river's own height and
        // the bed has been cut a metre and a half below it.
        var middle = field.At(16, 16);

        Assert.Equal(1f, middle.Coverage, 1e-3f);
        Assert.Equal(10f, middle.SurfaceHeight, 1e-2f);
        Assert.Equal(8.5f, middle.GroundHeight, 1e-2f);
        Assert.Equal(1.5f, middle.Depth, 1e-2f);
        Assert.Equal(2f, middle.Flow.X, 1e-2f);

        // Well outside the channel: dry, and the ground is what it always was.
        var outside = field.At(16, 30);

        Assert.Equal(0f, outside.Coverage);
        Assert.Equal(9.5f, outside.GroundHeight, 1e-3f);
        Assert.Equal(0f, outside.Depth);

        Assert.True(field.CoveredTexels > 0);

        // ⚠ And carving only ever deepens. Ground that is already below the bed the body wants is
        // left alone — a river laid across a canyon does not fill the canyon in to make itself a bed.
        var deep = new WaterField(field.Description);

        deep.Rasterize([River()], new FlatWaterGround(2f));
        Assert.Equal(2f, deep.At(16, 16).GroundHeight, 1e-3f);
    }

    /// <summary>
    ///     ⚠ Two overlapping bodies rasterise to the same field in either order.
    /// </summary>
    /// <remarks>
    ///     [docs/plan/35 § Part 4]. A field that depended on the order a scene happened to walk its
    ///     entities in is one where moving an unrelated entity changes the shoreline — and the change
    ///     is a texel wide, so it is found months later as "the water flickers near the bridge".
    /// </remarks>
    [Fact]
    public void OverlappingBodiesRasteriseTheSameInEitherOrder() {
        var river = River();
        var lake = Lake(surface: 9.5f);

        var description = new WaterFieldDescription { Origin = new(-16f, -16f), Extent = 32f, Resolution = 33 };
        var ground = new FlatWaterGround(6f);

        var forward = new WaterField(description);
        var backward = new WaterField(description);

        forward.Rasterize([river, lake], ground);
        backward.Rasterize([lake, river], ground);

        for (var z = 0; z < description.Resolution; z++) {
            for (var x = 0; x < description.Resolution; x++) {
                Assert.Equal(forward.At(x, z), backward.At(x, z));
            }
        }
    }

    /// <summary>Priority decides which body's surface wins where two overlap.</summary>
    /// <remarks>
    ///     Resolved once, here, rather than per pixel — which is what lets a tile of the surface carry
    ///     one material instead of a blend that has to know which two bodies it is between.
    /// </remarks>
    [Fact]
    public void PriorityDecidesTheOverlap() {
        var description = new WaterFieldDescription { Origin = new(-4f, -4f), Extent = 8f, Resolution = 9 };
        var ground = new FlatWaterGround(0f);

        var low = Lake(surface: 5f);
        var high = new WaterBody(
            WaterBodyKind.Lake,
            Lake(surface: 7f).Spline,
            defaults: new() { Depth = 1f }
        ) {
            SurfaceHeight = 7f,
            Priority = 10,
            ShoreFalloff = 1f,
            BedRamp = 3f
        };

        var field = new WaterField(description);

        field.Rasterize([low, high], ground);
        Assert.Equal(7f, field.At(4, 4).SurfaceHeight, 1e-2f);

        // And the same two the other way round, which the sort has to make irrelevant.
        field.Rasterize([high, low], ground);
        Assert.Equal(7f, field.At(4, 4).SurfaceHeight, 1e-2f);
    }

    /// <summary>An island subtracts, which is the same mechanism with the sign flipped.</summary>
    [Fact]
    public void AnIslandRemovesWaterAndRaisesTheGround() {
        var description = new WaterFieldDescription { Origin = new(-16f, -16f), Extent = 32f, Resolution = 33 };
        var lake = Lake(surface: 5f);

        var island = new WaterBody(
            WaterBodyKind.Island,
            new Spline(
                Spline.SmoothTangents(
                    [new(-3f, 5f, -3f), new(3f, 5f, -3f), new(3f, 5f, 3f), new(-3f, 5f, 3f)],
                    closed: true,
                    tension: 1f
                ),
                closed: true
            ),
            defaults: new() { Depth = 2f }
        ) {
            SurfaceHeight = 5f,
            Priority = 10,
            ShoreFalloff = 0.5f,
            BedRamp = 1f
        };

        var field = new WaterField(description);

        field.Rasterize([lake, island], new FlatWaterGround(0f));

        // The middle of the island is dry and stands above the lake's surface.
        var middle = field.At(16, 16);

        Assert.Equal(0f, middle.Coverage, 1e-3f);
        Assert.True(middle.GroundHeight >= 5f, $"the island's ground is at {middle.GroundHeight}");

        // And the lake outside it is untouched.
        var lakeTexel = field.At(24, 16);

        Assert.True(lakeTexel.Coverage > 0.9f);
        Assert.Equal(5f, lakeTexel.SurfaceHeight, 1e-2f);
    }

    /// <summary>Sampling between texels is bilinear, and outside the window is nothing.</summary>
    /// <remarks>
    ///     ⚠ Clamped at the edges rather than answering nothing, because a boat at the edge of a
    ///     window that is about to scroll would otherwise drop through the surface for the one frame
    ///     before the window caught up.
    /// </remarks>
    [Fact]
    public void SamplingIsBilinearInsideAndNothingOutside() {
        var field = new WaterField(new() { Origin = new(0f, -16f), Extent = 32f, Resolution = 33 });

        field.Rasterize([River()], new FlatWaterGround(6f));

        Assert.Equal(10f, field.Sample(new(16f, 0f)).SurfaceHeight, 1e-2f);
        Assert.Equal(0f, field.Sample(new(500f, 0f)).Coverage);
        Assert.Equal(WaterFieldSample.None, field.Sample(new(-100f, -100f)));
    }

    // --- The window ----------------------------------------------------------

    /// <summary>
    ///     ⚠ The window's origin lands on the coarsest consumer's grid, over a swept path.
    /// </summary>
    /// <remarks>
    ///     [docs/plan/35 § D3]'s warning, tested on the arithmetic and before there is a renderer to
    ///     see it in. Snapping to the field's own texel is not enough once something samples it at a
    ///     different rate: the two grids beat against each other and produce a crawl along the
    ///     shoreline that appears only while the camera moves — which is exactly the kind of thing
    ///     that gets found by eye, at one camera position, and attributed to the wrong thing.
    /// </remarks>
    [Fact]
    public void TheWindowSnapsToTheCoarsestGridAlongASweptPath() {
        // ⚠ 513 and not 512. The samples include both edges, so 256 m over 513 of them is half a metre
        // exactly — where 512 would be 0.50098, and the four-metre snap grid below would then not be a
        // whole number of texels.
        var description = new WaterFieldDescription { Extent = 256f, Resolution = 513 };

        Assert.Equal(0.5f, description.MetresPerTexel, 1e-6f);

        // The ripple simulation is the coarse one here: four metres a texel against the field's half.
        const float Coarsest = 4f;

        for (var step = 0; step < 2_000; step++) {
            var centre = new Vector2(step * 0.37f, step * -0.113f);
            var moved = description.Snap(centre, Coarsest);

            AssertOnGrid(moved.Origin.X, Coarsest);
            AssertOnGrid(moved.Origin.Y, Coarsest);
        }
    }

    /// <summary>And it never steps backwards while the view moves forwards.</summary>
    /// <remarks>
    ///     ⚠ Floor rather than round. Rounding changes direction at the midpoint, so a window
    ///     following a camera in a straight line goes back one texel and forward two — and a shoreline
    ///     that stutters is harder to notice, and harder to explain, than one that slides.
    /// </remarks>
    [Fact]
    public void TheWindowNeverStepsBackwardsWhileTheViewMovesForwards() {
        var description = new WaterFieldDescription { Extent = 128f, Resolution = 256 };
        var previous = float.NegativeInfinity;

        for (var step = 0; step < 4_000; step++) {
            var origin = description.Snap(new(step * 0.05f, 0f), 2f).Origin.X;

            Assert.True(origin >= previous, $"the window slid back from {previous} to {origin}");
            previous = origin;
        }
    }

    /// <summary>A window with no coarse consumer snaps to its own texel.</summary>
    [Fact]
    public void AWindowWithNoCoarseConsumerSnapsToItsOwnTexel() {
        var description = new WaterFieldDescription { Extent = 64f, Resolution = 65 };

        Gen.Float[-5_000f, 5_000f]
            .Sample(
                x => AssertOnGrid(description.Snap(new(x, 0f)).Origin.X, description.MetresPerTexel),
                iter: 2_000
            );
    }

    /// <summary>Asserts a coordinate is a whole number of steps from the origin.</summary>
    /// <remarks>
    ///     ⚠ <b>A division and not a remainder.</b> <c>x % step</c> answers a value just under
    ///     <c>step</c> rather than zero whenever the float arithmetic lands a hair below a multiple —
    ///     so a test written that way fails on a value that is on the grid, and reports the step as the
    ///     error. Dividing and comparing against the nearest whole number has no such boundary.
    /// </remarks>
    static void AssertOnGrid(float value, float step) {
        var steps = value / step;

        Assert.True(
            MathF.Abs(steps - MathF.Round(steps)) < 1e-3f,
            $"{value} is {steps} steps of {step}, which is not on the grid"
        );
    }

    /// <summary>Moving a window keeps its memory and forgets its contents.</summary>
    /// <remarks>
    ///     ⚠ Forgets rather than shifts: a body may have moved and the ground may have been carved,
    ///     so copying the overlap carries the stale answer for both — and the stale part is the part
    ///     the camera has been looking at.
    /// </remarks>
    [Fact]
    public void MovingAWindowClearsItAndRefusesAResize() {
        var field = new WaterField(new() { Origin = Vector2.Zero, Extent = 32f, Resolution = 33 });

        field.Rasterize([Lake()], new FlatWaterGround(0f));
        Assert.True(field.CoveredTexels > 0);

        field.Move(field.Description with { Origin = new(1_000f, 1_000f) });

        Assert.Equal(0, field.CoveredTexels);
        Assert.Equal(0f, field.At(16, 16).SurfaceHeight);

        Assert.Throws<ArgumentException>(() => field.Move(field.Description with { Resolution = 64 }));
    }
}
