// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Rendering.IrradianceFields.Tests;

/// <summary>The first thing here that computes light rather than storing it.</summary>
public class TracedIrradianceFillerTests {
    /// <summary>
    ///     <b>The exact one, and the one that catches a missing solid angle.</b> A uniform environment
    ///     of radiance <i>L</i> lights every surface with <i>L</i>, whichever way it faces — the same
    ///     closed form <c>SphericalHarmonicsL1Tests</c> checks the projection against, now reached
    ///     through sixty-four traced rays instead of a loop over directions.
    /// </summary>
    [Theory]
    [InlineData(1f)]
    [InlineData(0.25f)]
    [InlineData(7f)]
    public void AUniformEnvironmentLightsEverythingWithItself(float sky) {
        var filler = new TracedIrradianceFiller(AnalyticFields.Empty, Radiance.Uniform(sky));
        var probe = filler.Trace(new(3f, -2f, 5f), IrradianceProbe.Empty);

        Assert.Equal(1f, probe.Validity);
        Assert.Equal(1f, probe.SunShadow);

        // Relative, because the error is the ray count's and scales with the answer: sixty-four
        // Fibonacci directions cover the sphere to about a third of a per cent, and a probe is that
        // far from exact however bright the environment is.
        foreach (var normal in (Vector3[]) [
            new(0, 1, 0), new(0, -1, 0), new(1, 0, 0), Vector3.Normalize(new(1, 2, -3))
        ]) {
            Assert.Equal(sky, probe.Irradiance(normal).X, sky * 0.005f);
        }
    }

    /// <summary>And more rays is nearer, which says the error above is the sampling and not a bias.</summary>
    [Fact]
    public void MoreRaysIsNearerTheTruth() {
        static float Error(int rays) {
            var filler = new TracedIrradianceFiller(
                AnalyticFields.Empty,
                Radiance.Uniform(1f),
                new IrradianceFillSettings { RayCount = rays }
            );

            return MathF.Abs(1f - filler.Trace(Vector3.Zero, IrradianceProbe.Empty).Irradiance(new(0, 1, 0)).X);
        }

        Assert.True(Error(1024) < Error(64), "a finer sphere of directions did not converge");
    }

    /// <summary>
    ///     <b>Doc 19's L2 exit criterion, with a real trace behind it.</b> A closed box lit from
    ///     outside stays dark — not because the numbers were arranged that way, but because every one
    ///     of the probe's rays hits the inside of the shell before it reaches anything bright.
    /// </summary>
    [Fact]
    public void AClosedBoxKeepsTheSkyOut() {
        var filler = new TracedIrradianceFiller(AnalyticFields.HollowBox(4f, 3f), Radiance.Uniform(10f));
        var probe = filler.Trace(Vector3.Zero, IrradianceProbe.Empty);

        // The rays hit the cavity's walls from the inside, which is their front — so the probe is in
        // open air and knows it, and it is dark because the walls give nothing back.
        Assert.Equal(1f, probe.Validity);
        Assert.Equal(0f, probe.Irradiance(new(0, 1, 0)).X, 4);
        Assert.Equal(0f, probe.Irradiance(new(1, 0, 0)).X, 4);
    }

    /// <summary>
    ///     And the same box from outside is bright, so the test above is not passing because
    ///     everything is dark.
    /// </summary>
    [Fact]
    public void OutsideThatBoxTheSkyIsStillThere() {
        var filler = new TracedIrradianceFiller(AnalyticFields.HollowBox(4f, 3f), Radiance.Uniform(10f));
        var probe = filler.Trace(new(20f, 0f, 0f), IrradianceProbe.Empty);

        Assert.True(probe.Irradiance(new(1, 0, 0)).X > 5f, "the sky did not reach a probe standing in it");
    }

    /// <summary>
    ///     A probe the field calls solid is invalid without tracing anything. The backface vote would
    ///     get there too; the sign is exact and free, so it answers first.
    /// </summary>
    [Fact]
    public void AProbeInsideGeometryIsNotBelieved() {
        var filler = new TracedIrradianceFiller(AnalyticFields.Sphere(Vector3.Zero, 5f), Radiance.Uniform(10f));
        var probe = filler.Trace(Vector3.Zero, IrradianceProbe.Empty);

        Assert.Equal(IrradianceProbe.Empty, probe);
    }

    /// <summary>
    ///     <b>A probe inside a wall is caught by the sign, and the backface vote never gets a say.</b>
    ///     Worth asserting with the reason attached, because doc 19 § L2 names the vote as <i>the</i>
    ///     mechanism and against an exact field it cannot fire at all: sphere tracing stops where the
    ///     field crosses zero on the way down, and the gradient there always opposes the ray. The vote
    ///     earns its place against a <i>sampled</i> field, whose over-reported step can land past a
    ///     thin wall — and the probe's own position says nothing about that.
    /// </summary>
    [Fact]
    public void AProbeInsideAWallIsCaughtByTheSign() {
        var filler = new TracedIrradianceFiller(AnalyticFields.HollowBox(4f, 3f), Radiance.Uniform(1f));

        Assert.Equal(0f, filler.Trace(new(3.5f, 0f, 0f), IrradianceProbe.Empty).Validity);
    }

    /// <summary>
    ///     And a probe pressed right against a wall from the open side is still believed — the vote
    ///     does not misfire on a probe that merely has geometry very close to it, which is every probe
    ///     that matters.
    /// </summary>
    [Fact]
    public void AProbeBesideAWallIsStillBelieved() {
        var filler = new TracedIrradianceFiller(AnalyticFields.HollowBox(4f, 3f), Radiance.Uniform(1f));

        Assert.Equal(1f, filler.Trace(new(2.9f, 0f, 0f), IrradianceProbe.Empty).Validity);
    }

    /// <summary>What the sun scalar is for: something between a probe and the light.</summary>
    [Fact]
    public void TheSunIsShadowedByWhatIsBetween() {
        var filler = new TracedIrradianceFiller(
            AnalyticFields.Sphere(new(0f, 5f, 0f), 2f),
            Radiance.Uniform(1f),
            new IrradianceFillSettings { SunDirection = new(0f, 1f, 0f) }
        );

        Assert.Equal(0f, filler.Trace(Vector3.Zero, IrradianceProbe.Empty).SunShadow);
        Assert.Equal(1f, filler.Trace(new(20f, 0f, 0f), IrradianceProbe.Empty).SunShadow);
    }

    /// <summary>
    ///     Hysteresis keeps most of the previous answer, which is what averages away the noise in a
    ///     sixty-four-ray estimate over frames instead of letting it flicker.
    /// </summary>
    [Fact]
    public void HysteresisKeepsMostOfTheOldAnswer() {
        var filler = new TracedIrradianceFiller(
            AnalyticFields.Empty,
            Radiance.Uniform(4f),
            new IrradianceFillSettings { Hysteresis = 0.75f }
        );

        var once = filler.Trace(Vector3.Zero, IrradianceProbe.Empty);

        Assert.Equal(0.25f, once.Validity, 4);
        Assert.Equal(1f, once.Irradiance(new(0, 1, 0)).X, 0.02f);

        var twice = filler.Trace(Vector3.Zero, once);

        Assert.Equal(1.75f, twice.Irradiance(new(0, 1, 0)).X, 0.03f);
    }

    /// <summary>Two fills of one scene agree to the bit, because the directions are a spiral.</summary>
    [Fact]
    public void TwoFillsOfOneSceneAgreeExactly() {
        var filler = new TracedIrradianceFiller(AnalyticFields.HollowBox(4f, 3f), Radiance.Uniform(3f));

        Assert.Equal(
            filler.Trace(new(1f, 0.5f, -2f), IrradianceProbe.Empty),
            filler.Trace(new(1f, 0.5f, -2f), IrradianceProbe.Empty)
        );
    }

    /// <summary>A budget walks the lattice and comes back round, one probe at a time.</summary>
    [Fact]
    public void ABudgetedFillWalksTheLatticeAndWrapsAround() {
        var field = new IrradianceField(new BoundingBox(new(-1f), new(1f)), new(1));
        var filler = new TracedIrradianceFiller(AnalyticFields.Empty, Radiance.Uniform(2f));

        field.AllocateAll();

        Assert.Equal(0, filler.Cursor);
        Assert.Equal(10, filler.Fill(field, 10));
        Assert.Equal(10, filler.Cursor);

        // Sixty-four probes in one brick, so the rest of them plus a wrap of two.
        Assert.Equal(56, filler.Fill(field, 56));
        Assert.Equal(2, filler.Cursor);
    }

    /// <summary>
    ///     A budget spends itself on cells with no brick too, so one call cannot walk a mostly-empty
    ///     lattice looking for work — which is the frame-time spike a budget exists to prevent.
    /// </summary>
    [Fact]
    public void ABudgetIsSpentOnEmptyCellsAsWell() {
        var field = new IrradianceField(new BoundingBox(new(-4f), new(4f)), new(2));
        var filler = new TracedIrradianceFiller(AnalyticFields.Empty, Radiance.Uniform(2f));

        // One brick out of eight, and it is not the first one the cursor reaches.
        Assert.True(field.TryAllocate(new(1, 1, 1), out _));

        Assert.Equal(0, filler.Fill(field, 20));
        Assert.Equal(20, filler.Cursor);
    }

    /// <summary>
    ///     A cursor into a lattice that no longer exists is not a position, so a field that changed
    ///     shape starts again rather than visiting some probes twice and others never.
    /// </summary>
    [Fact]
    public void ChangingTheLatticeRestartsTheWalk() {
        var filler = new TracedIrradianceFiller(AnalyticFields.Empty, Radiance.Uniform(1f));
        var small = new IrradianceField(new BoundingBox(new(-1f), new(1f)), new(1));

        small.AllocateAll();
        filler.Fill(small, 10);

        Assert.Equal(10, filler.Cursor);

        var large = new IrradianceField(new BoundingBox(new(-4f), new(4f)), new(2));

        large.AllocateAll();

        Assert.Equal(3, filler.Fill(large, 3));
        Assert.Equal(3, filler.Cursor);
    }

    /// <summary>
    ///     <b>The whole chain, on a scene rather than on one probe.</b> Fill a field inside a closed
    ///     box, dilate, sync, and ask what a surface in the room receives — which is the exit criterion
    ///     of doc 19 § L2 read end to end rather than in parts.
    /// </summary>
    [Fact]
    public void AFieldFilledInsideAClosedBoxIsDarkThroughout() {
        var field = new IrradianceField(new BoundingBox(new(-2.5f), new(2.5f)), new(1));
        var filler = new TracedIrradianceFiller(AnalyticFields.HollowBox(4f, 3f), Radiance.Uniform(10f));

        field.AllocateAll();

        Assert.Equal(64, filler.Fill(field));

        field.Dilate();
        field.SyncBorders();

        foreach (var point in (Vector3[]) [
            Vector3.Zero, new(2f, 0f, 0f), new(-2f, 1f, 2f), new(2.4f, 2.4f, 2.4f)
        ]) {
            Assert.True(
                field.Irradiance(point, new(0, 1, 0)).X < 0.01f,
                $"the sky reached {point} inside a closed box"
            );
        }
    }
}
