// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Water;
using Xunit;

namespace Vixen.Water.Tests;

/// <summary>
///     The zone: a sliding window, when it moves, and what it costs — [docs/plan/35 § D3, § W3].
/// </summary>
/// <remarks>
///     <para>
///         W3's two exit criteria are here. <b>Two bodies overlapping produce one continuous depth
///         field with no seam at the boundary</b>, and <b>the window scrolls across two kilometres
///         with the shoreline stable to the pixel</b> — the second measured over a swept path rather
///         than looked at, because a shoreline crawl appears only while the camera moves and is
///         therefore invisible in every screenshot anybody takes of it.
///     </para>
///     <para>
///         The derived readouts are tested too, and that is not padding: § D3 says the resolution is
///         "derived and stated, not typed", so a panel that showed a wrong metres-per-texel would be
///         the whole feature failing quietly.
///     </para>
/// </remarks>
public sealed class WaterZoneTests {
    /// <summary>Ground that slopes, so a shoreline is somewhere rather than everywhere.</summary>
    /// <param name="Slope">How much the ground rises per metre of +X.</param>
    /// <param name="Base">How high it is at the origin.</param>
    /// <param name="Ripple">How tall a sandbar ripple sits on the slope, in metres.</param>
    /// <remarks>
    ///     ⚠ <b>The ripple is not decoration, and leaving it out made a test pass for the wrong
    ///     reason.</b> Bilinear interpolation of a <em>linear</em> field is exact whatever grid it is
    ///     sampled on — so a perfectly planar beach cannot show a sampling-grid crawl even when the
    ///     grid is crawling, and a stability test over one is a test that could not have failed. What
    ///     § D3 warns about needs curvature to be visible at all.
    /// </remarks>
    sealed record Beach(float Slope, float Base, float Ripple = 0f) : IWaterGround {
        public float HeightAt(Vector2 ground) =>
            Base + (ground.X * Slope) + (Ripple * MathF.Sin(ground.X * 0.7f) * MathF.Cos(ground.Y * 0.5f));
    }

    static WaterBody Lake(Vector2 centre, float half, float surface, int priority = 0, float falloff = 2f) {
        var spline = new Spline(
            Spline.SmoothTangents(
                [
                    new(centre.X - half, surface, centre.Y - half),
                    new(centre.X + half, surface, centre.Y - half),
                    new(centre.X + half, surface, centre.Y + half),
                    new(centre.X - half, surface, centre.Y + half)
                ],
                closed: true,
                tension: 1f
            ),
            closed: true
        );

        return new(WaterBodyKind.Lake, spline, defaults: new() { Depth = 3f }) {
            SurfaceHeight = surface,
            Priority = priority,
            ShoreFalloff = falloff,
            BedRamp = 4f
        };
    }

    // --- What a panel shows --------------------------------------------------

    /// <summary>The derived numbers are the ones a panel shows instead of a resolution.</summary>
    /// <remarks>
    ///     ⚠ "Two metres a texel" is a sentence an author can act on and "256" is not. A resolution
    ///     somebody types with no idea what it buys is how the reference gets configured wrongly, and
    ///     these three numbers are the whole of the fix.
    /// </remarks>
    [Fact]
    public void TheDerivedReadoutsAreWhatTheyClaim() {
        var zone = WaterZone.Default with { Extent = 512f, Resolution = 257 };

        Assert.Equal(2f, zone.MetresPerTexel, 1e-5f);

        // Four channels of four bytes over 257².
        Assert.Equal(257L * 257 * 4 * 4, zone.Bytes);

        // Half precision is exactly half the memory.
        Assert.Equal(zone.Bytes / 2, (zone with { Precision = WaterInfoPrecision.Half }).Bytes);

        // An eighth of 512 m.
        Assert.Equal(64f, zone.ScrollDistance, 1e-4f);
    }

    /// <summary>
    ///     ⚠ Half precision over a large zone is a quantised surface, and the number says so.
    /// </summary>
    /// <remarks>
    ///     The panel shows this beside the switch precisely so nobody has to discover a stepped
    ///     horizon. At full precision it is fractions of a millimetre and nobody thinks about it; at
    ///     half precision over a two-kilometre zone it is around a metre, and the horizon terraces.
    /// </remarks>
    [Fact]
    public void HalfPrecisionOverALargeZoneQuantisesTheSurface() {
        var large = WaterZone.Default with { Extent = 2_048f };

        var full = (large with { Precision = WaterInfoPrecision.Full }).HeightQuantum;
        var half = (large with { Precision = WaterInfoPrecision.Half }).HeightQuantum;

        Assert.True(full < 0.001f, $"full precision quantised a 2 km zone at {full} m");
        Assert.True(half >= 0.5f, $"half precision over a 2 km zone claimed to resolve {half} m");

        // Thirteen bits of mantissa between them, which is what the two formats differ by.
        Assert.Equal(8192f, half / full, 1f);
    }

    /// <summary>
    ///     ⚠ A scroll threshold at or past a half is refused, because the view reaches the edge.
    /// </summary>
    /// <remarks>
    ///     The window is centred on the view, so the view is at worst this fraction of the extent from
    ///     the centre — and at a half it is standing on the boundary looking out over water the field
    ///     has never been rasterised for. That is a wall of dry land at the horizon, arriving only
    ///     when somebody walks far enough in one go.
    /// </remarks>
    [Fact]
    public void AZoneThatWouldLetTheViewReachTheEdgeIsRefused() {
        Assert.Null(WaterZone.Default.Validate());
        Assert.NotNull((WaterZone.Default with { ScrollThreshold = 0.5f }).Validate());
        Assert.NotNull((WaterZone.Default with { ScrollThreshold = -0.1f }).Validate());
        Assert.NotNull((WaterZone.Default with { Resolution = 1 }).Validate());
        Assert.NotNull((WaterZone.Default with { Extent = 0f }).Validate());

        Assert.Throws<ArgumentException>(() => new WaterZoneState(WaterZone.Default with { Extent = 0f }));
    }

    // --- The window ----------------------------------------------------------

    /// <summary>The field is rasterised once and then not again until something asks.</summary>
    /// <remarks>
    ///     ⚠ The whole reason the threshold exists. A zone that rasterised every frame would spend a
    ///     field's cost on a camera that had moved a centimetre — and the field is what every consumer
    ///     reads, so that cost is paid whether or not anything is looking at water.
    /// </remarks>
    [Fact]
    public void TheFieldIsRasterisedOnceAndThenLeftAlone() {
        var state = new WaterZoneState(WaterZone.Default);
        var ground = new Beach(0f, 0f);

        state.SetBodies([Lake(Vector2.Zero, 60f, 4f)]);

        Assert.Equal(WaterZoneUpdate.First, state.Update(Vector2.Zero, ground));
        Assert.Equal(1, state.RasterCount);

        // A hundred frames of standing still, and of walking a metre at a time.
        for (var step = 0; step < 100; step++) {
            Assert.Equal(WaterZoneUpdate.None, state.Update(new(step * 0.5f, 0f), ground));
        }

        Assert.Equal(1, state.RasterCount);

        // Past the threshold — a sixty-fourth-metre short of it does not count, and past it does.
        Assert.Equal(WaterZoneUpdate.None, state.Update(new(63.9f, 0f), ground));
        Assert.Equal(WaterZoneUpdate.Scrolled, state.Update(new(64.1f, 0f), ground));
        Assert.Equal(2, state.RasterCount);
    }

    /// <summary>A body that moved re-rasterises wholesale, whatever the view did.</summary>
    /// <remarks>
    ///     ⚠ Wholesale rather than incrementally, for doc 31 § D4's reason: a body that moved changes
    ///     the field where it <em>was</em> as well as where it is, and the half that gets forgotten is
    ///     the one that leaves a river behind after the river has gone.
    /// </remarks>
    [Fact]
    public void AChangedBodyRerasterisesWithoutTheViewMoving() {
        var state = new WaterZoneState(WaterZone.Default);
        var ground = new Beach(0f, 0f);

        state.SetBodies([Lake(Vector2.Zero, 60f, 4f)]);
        state.Update(Vector2.Zero, ground);

        Assert.Equal(WaterZoneUpdate.None, state.Update(Vector2.Zero, ground));

        state.SetBodies([Lake(new(200f, 0f), 60f, 4f)]);

        Assert.Equal(WaterZoneUpdate.Changed, state.Update(Vector2.Zero, ground));

        // And the water is where the body now is, not where it was.
        Assert.Equal(0f, state.Field!.Sample(Vector2.Zero).Coverage);
        Assert.True(state.Field.Sample(new(200f, 0f)).Coverage > 0.9f);

        state.Invalidate();
        Assert.Equal(WaterZoneUpdate.Changed, state.Update(Vector2.Zero, ground));
    }

    /// <summary>
    ///     ⚠ The window scrolls across two kilometres with the shoreline stable to the texel.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         [docs/plan/35 § W3]'s second exit criterion, and § D3's warning tested on the arithmetic
    ///         rather than looked at: the snap has to be to a texel of the <em>coarsest</em> thing that
    ///         reads the field, because two grids at different rates beat against each other and
    ///         produce a crawl along the shoreline that appears <em>only while the camera moves</em>.
    ///     </para>
    ///     <para>
    ///         What is measured is the field's own answer at a fixed world position as the window
    ///         slides underneath it. A shoreline that crawled would make the depth at that position
    ///         wobble as the sampling grid shifted; a snapped window keeps every texel centre where it
    ///         was, so the interpolated answer is the same to a hair.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheShorelineIsStableWhileTheWindowScrollsTwoKilometres() {
        // A ripple simulation at four metres a texel is the coarse consumer; the field is at two.
        var zone = WaterZone.Default with { Extent = 512f, Resolution = 257, CoarsestTexel = 4f };

        // ⚠ The zone refuses a snap grid that is not a whole number of its own texels, and this sweep
        // is what that check exists for — 512 m over 257 samples is exactly 2 m, so a 4 m grid is two
        // texels and the window lands on the same texel centres every time. At 256 samples it would be
        // 2.0078 m, the grids would beat, and the shoreline would crawl.
        Assert.Null(zone.Validate());
        Assert.Equal(2f, zone.MetresPerTexel, 1e-5f);
        Assert.NotNull((zone with { CoarsestTexel = 3f }).Validate());
        var state = new WaterZoneState(zone);

        // A beach with a sandbar ripple on it: the ground rises across +X so there is a shoreline at a
        // definite place, and the ripple is what makes the depth at a fixed point a number a crawling
        // grid can move. A planar bed would be reconstructed exactly on any grid — see Beach.
        var ground = new Beach(0.02f, -6f, Ripple: 0.6f);

        // One enormous lake, so the window is inside it for the whole walk and the shoreline it is
        // being asked about is the ground's rather than the body's edge.
        state.SetBodies([Lake(new(1_000f, 0f), 2_000f, 0f)]);

        var probes = new[] {
            new Vector2(120f, 40f),
            new Vector2(121.3f, -70f),
            new Vector2(119.05f, 12.5f)
        };

        var first = new float?[probes.Length];
        var worst = 0f;
        var scrolls = 0;

        // Two kilometres, a metre at a time.
        for (var step = 0; step <= 2_000; step++) {
            var view = new Vector2(step, 0f);

            if (state.Update(view, ground) is WaterZoneUpdate.Scrolled) {
                scrolls++;
            }

            for (var probe = 0; probe < probes.Length; probe++) {
                var sample = state.Field!.Sample(probes[probe]);

                // Only while the probe is inside the window — past that it has left, which is the
                // window doing its job rather than a wobble.
                if (sample.Coverage <= 0f) {
                    continue;
                }

                first[probe] ??= sample.Depth;
                worst = MathF.Max(worst, MathF.Abs(sample.Depth - first[probe]!.Value));
            }
        }

        Assert.True(scrolls > 20, $"the window only scrolled {scrolls} times across two kilometres");

        // ⚠ A tenth of a millimetre. An unsnapped window over the same bed moves the depth at a fixed
        // point by a hundred times that — see AnUnsnappedWindowIsWhatTheStabilityTestCatches, which is
        // what makes this bound mean something rather than being a bound on nothing.
        Assert.True(worst < 1e-4f, $"the depth at a fixed point wobbled by {worst} m as the window slid");
    }

    /// <summary>And an unsnapped window is what that test would have caught.</summary>
    /// <remarks>
    ///     ⚠ <b>The negative half, and it is what makes the positive one mean something.</b> A test
    ///     that only asserts stability passes just as well against a window that never moves — so this
    ///     asserts that the *same* sweep against a window snapped to nothing wobbles, by two orders of
    ///     magnitude more.
    /// </remarks>
    [Fact]
    public void AnUnsnappedWindowIsWhatTheStabilityTestCatches() {
        var description = new WaterFieldDescription { Extent = 512f, Resolution = 257 };

        // ⚠ Rippled. See Beach: a planar bed is exactly reconstructed by bilinear interpolation on
        // any grid, so the control could not wobble however badly the window was placed.
        var ground = new Beach(0.02f, -6f, Ripple: 0.6f);
        var body = Lake(new(1_000f, 0f), 2_000f, 0f);
        var probe = new Vector2(120f, 40f);

        float? snappedFirst = null;
        float? looseFirst = null;
        var snappedWorst = 0f;
        var looseWorst = 0f;

        var snapped = new WaterField(description);
        var loose = new WaterField(description);

        for (var step = 0; step < 24; step++) {
            var view = new Vector2(step * 3.7f, 0f);

            snapped.Move(description.Snap(view, 4f));
            snapped.Rasterize([body], ground);

            // The same window, centred exactly on the view — which is what "no snap" is.
            loose.Move(description with { Origin = view - new Vector2(description.Extent * 0.5f) });
            loose.Rasterize([body], ground);

            var a = snapped.Sample(probe);
            var b = loose.Sample(probe);

            if (a.Coverage > 0f) {
                snappedFirst ??= a.Depth;
                snappedWorst = MathF.Max(snappedWorst, MathF.Abs(a.Depth - snappedFirst.Value));
            }

            if (b.Coverage > 0f) {
                looseFirst ??= b.Depth;
                looseWorst = MathF.Max(looseWorst, MathF.Abs(b.Depth - looseFirst.Value));
            }
        }

        Assert.True(snappedWorst < 0.001f, $"the snapped window wobbled by {snappedWorst} m");

        Assert.True(
            looseWorst > snappedWorst * 100f,
            $"an unsnapped window wobbled by {looseWorst} m against the snapped one's {snappedWorst} m, "
                + "which is not enough of a difference for the stability test above to be testing anything"
        );
    }

    // --- The overlap ---------------------------------------------------------

    /// <summary>
    ///     ⚠ Two overlapping bodies produce one continuous depth field, with no seam at the boundary.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         [docs/plan/35 § W3]'s first exit criterion, and § D3's reason for making the <em>zone</em>
    ///         the render unit: a river mouth is the one place two flow models meet, and resolving it
    ///         once — by rasterising in priority order into one field — is what lets a tile of the
    ///         surface carry one material instead of a blend that has to know which two bodies it is
    ///         between.
    ///     </para>
    ///     <para>
    ///         Two bodies at the <em>same</em> height are one surface, so the field across their
    ///         overlap has to be continuous to a millimetre — anything the compositing arrangement
    ///         itself adds shows up here. The case where they disagree is the test below it, and it is
    ///         a different question with a different instrument.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TwoOverlappingBodiesMakeOneContinuousField() {
        var zone = WaterZone.Default with { Extent = 256f, Resolution = 257 };
        var state = new WaterZoneState(zone);

        // Two lakes at the *same* height and different priorities, overlapping across a band. This is
        // the claim: resolving two bodies into one field introduces no discontinuity of its own.
        state.SetBodies([
            Lake(new(-30f, 0f), 50f, 4f),
            Lake(new(30f, 0f), 50f, 4f, priority: 1)
        ]);

        state.Update(Vector2.Zero, new Beach(0f, -6f));

        var field = state.Field!;
        var worst = Roughest(field, zone.Resolution, out var worstAt);

        // ⚠ A millimetre. Two bodies at one height are one surface, and anything the compositing
        // arrangement adds shows up here as a step at the boundary between them.
        Assert.True(
            worst < 0.001f,
            $"the depth field steps by {worst} m at x = {worstAt} where two level bodies overlap"
        );

        Assert.True(field.Sample(new(-60f, 0f)).Coverage > 0.99f);
        Assert.True(field.Sample(new(60f, 0f)).Coverage > 0.99f);
    }

    /// <summary>
    ///     ⚠ And two bodies at <em>different</em> heights meet over a ramp rather than at a cut.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The other half, and the one that says what "no seam" means when the two bodies genuinely
    ///         disagree — a river running into a lake half a metre below it. There has to be a
    ///         transition; what there must not be is a step. So the assertion counts the texels the
    ///         transition is spread across, which a cut would make one.
    ///     </para>
    ///     <para>
    ///         ⚠ Written this way because the first version of it asserted a maximum step of a tenth
    ///         of a metre and failed at 0.34 — which was not a seam at all, it was the ramp: half a
    ///         metre of surface difference over a two-metre shoreline band at one metre a texel
    ///         <em>is</em> a quarter of a metre per texel. A bound on the gradient cannot tell a cut
    ///         from a legitimately steep ramp; a count of how far the transition is spread can.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TwoBodiesAtDifferentHeightsMeetOverARampAndNotACut() {
        var zone = WaterZone.Default with { Extent = 256f, Resolution = 257 };
        var state = new WaterZoneState(zone);

        // ⚠ A shoreline band of eight metres against a one-metre texel, and the width is the point.
        // A band narrower than a few texels cannot be *resolved* by the field however smooth the
        // arithmetic is — which is a real constraint on an author and is why the panel shows metres per
        // texel: the first version of this test used the default two-metre band and measured a
        // transition two texels wide, which is neither a cut nor a ramp, it is a band the field cannot
        // see.
        state.SetBodies([
            Lake(new(-30f, 0f), 50f, 4f, falloff: 8f),
            Lake(new(30f, 0f), 50f, 4.5f, priority: 1, falloff: 8f)
        ]);

        state.Update(Vector2.Zero, new Beach(0f, -6f));

        var field = state.Field!;
        var row = zone.Resolution / 2;
        var transitional = 0;

        for (var x = 0; x < zone.Resolution; x++) {
            var sample = field.At(x, row);

            if (sample.Coverage > 0.99f && sample.SurfaceHeight > 4.02f && sample.SurfaceHeight < 4.48f) {
                transitional++;
            }
        }

        // Eight metres of band at one metre a texel: the transition is spread across most of it. A cut
        // would be zero or one.
        Assert.True(
            transitional >= 5,
            $"the two surfaces met across {transitional} texels, which is a cut rather than a ramp"
        );

        Assert.Equal(4f, field.Sample(new(-60f, 0f)).SurfaceHeight, 0.05f);
        Assert.Equal(4.5f, field.Sample(new(60f, 0f)).SurfaceHeight, 0.05f);
    }

    /// <summary>The largest single-texel step in the depth field along a line through the middle.</summary>
    /// <remarks>
    ///     Only where both texels are fully covered: the edge of a body is a shoreline, which is a real
    ///     boundary and is what the falloff is for.
    /// </remarks>
    static float Roughest(WaterField field, int resolution, out int worstAt) {
        var worst = 0f;

        worstAt = 0;

        for (var x = 1; x < resolution - 1; x++) {
            var here = field.At(x, resolution / 2);
            var next = field.At(x + 1, resolution / 2);

            if (here.Coverage <= 0.99f || next.Coverage <= 0.99f) {
                continue;
            }

            var difference = MathF.Abs(next.Depth - here.Depth);

            if (difference > worst) {
                worst = difference;
                worstAt = x;
            }
        }

        return worst;
    }

    /// <summary>A query built from a zone reads the window the zone is holding now.</summary>
    /// <remarks>
    ///     ⚠ The field rather than a snapshot of it, so a window that scrolls is a window the boat
    ///     floating on it sees scroll. A query built from a copy would answer for a window that no
    ///     longer exists, which is a boat that keeps floating where the water used to be.
    /// </remarks>
    [Fact]
    public void AQueryFollowsTheWindowAsItScrolls() {
        var state = new WaterZoneState(WaterZone.Default);
        var ground = new Beach(0f, -6f);

        state.SetBodies([Lake(new(1_000f, 0f), 2_000f, 0f)]);
        state.Update(Vector2.Zero, ground);

        var query = state.Query(WaterWaveSpectrum.Calm);

        Assert.True(query.Sample(Vector2.Zero, 0f).IsWet);

        // Somewhere the first window never covered.
        Assert.False(query.Sample(new(900f, 0f), 0f).IsWet);

        state.Update(new(900f, 0f), ground);

        // The same query object, now answering for the window that moved under it.
        Assert.True(query.Sample(new(900f, 0f), 0f).IsWet);
    }
}
