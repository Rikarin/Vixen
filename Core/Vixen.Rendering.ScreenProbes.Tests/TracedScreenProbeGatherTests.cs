// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering.DistanceFields;
using Vixen.Rendering.IrradianceFields;
using Xunit;

namespace Vixen.Rendering.ScreenProbes.Tests;

/// <summary>The reference gather, held against environments with closed forms.</summary>
/// <remarks>
///     The same discipline as the irradiance field's reference filler, one probe kind over: a uniform
///     sky has an exact answer, a linear sky has one within the quadrature's stated tolerance, and
///     everything else in the file is about probes knowing when they have nothing to say.
/// </remarks>
public class TracedScreenProbeGatherTests {
    const float Radiance = 0.75f;

    /// <summary>
    ///     Under a uniform sky, every pixel's answer is the sky — whichever way its surface faces.
    /// </summary>
    /// <remarks>
    ///     The screen-probe restatement of the closed form every layer of doc 19 § L2 was held
    ///     against: a uniform environment of radiance <i>L</i> lights every surface with exactly
    ///     <i>L</i>. It reaches the answer through the anchor lookup, the trace, the map, the exact
    ///     solid angles, the projection, and the bilinear resolve — so a wrong weight anywhere in
    ///     that chain is a pixel that is not <i>L</i>.
    /// </remarks>
    [Fact]
    public void AUniformSkyComesBackAsItself() {
        var atlas = new ScreenProbeAtlas(new(new(64, 48)));
        var gather = new TracedScreenProbeGather(new EmptyWorld(), new UniformSky(Radiance));

        Assert.Equal(atlas.Layout.ProbeCount, gather.Fill(atlas, new Floor()));

        foreach (var pixel in Pixels(atlas.Layout.Viewport)) {
            var up = atlas.Irradiance(pixel, new(0f, 1f, 0f));
            var sideways = atlas.Irradiance(pixel, Vector3.Normalize(new(1f, 0.5f, -0.25f)));

            Assert.Equal(Radiance, up.X, 1e-3f);
            Assert.Equal(Radiance, up.Y, 1e-3f);
            Assert.Equal(Radiance, up.Z, 1e-3f);
            Assert.Equal(Radiance, sideways.X, 1e-3f);
        }
    }

    /// <summary>
    ///     A linear sky resolves to its own closed form: <c>a + ⅔·b·(n·ŷ)</c>, within the sixty-four
    ///     ray quadrature's tolerance.
    /// </summary>
    /// <remarks>
    ///     Two per cent, which is filler B's stated tolerance for the same reason: sixty-four
    ///     directions are a quadrature, and the octahedral map's texels are not placed by symmetry
    ///     around the y axis the way a cube's are. What the exact solid angles buy is that the error
    ///     is the quadrature's and nothing else's.
    /// </remarks>
    [Fact]
    public void ALinearSkyResolvesToItsClosedForm() {
        const float Base = 0.6f;
        const float Tilt = 0.3f;

        var atlas = new ScreenProbeAtlas(new(new(32, 32)));
        var gather = new TracedScreenProbeGather(new EmptyWorld(), new LinearSky(Base, Tilt));

        gather.Fill(atlas, new Floor());

        var probe = new Int2(1, 1);
        var resolved = atlas.Resolved(probe);

        Assert.Equal(Base + (2f / 3f * Tilt), resolved.Irradiance(new(0f, 1f, 0f)).X, Base * 0.02f);
        Assert.Equal(Base - (2f / 3f * Tilt), resolved.Irradiance(new(0f, -1f, 0f)).X, Base * 0.02f);
        Assert.Equal(Base, resolved.Irradiance(new(1f, 0f, 0f)).X, Base * 0.02f);
    }

    /// <summary>A probe standing on a lit floor still answers the sky for the floor's own normal.</summary>
    /// <remarks>
    ///     The world has actual geometry here — a solid half-space — so the lower hemisphere of every
    ///     map is hits rather than sky, and the hits give back nothing. For the upward normal the L1
    ///     truncation of a hemispherical environment is exact, which is what makes this a closed form
    ///     rather than a tolerance: the answer is the sky, undimmed, despite half the sphere being
    ///     floor.
    /// </remarks>
    [Fact]
    public void AProbeOnAFloorSeesTheWholeSkyUpward() {
        var atlas = new ScreenProbeAtlas(new(new(32, 32)));
        var gather = new TracedScreenProbeGather(new HalfSpace(), new UniformSky(Radiance));

        Assert.Equal(atlas.Layout.ProbeCount, gather.Fill(atlas, new Floor()));

        var up = atlas.Irradiance(new(16, 16), new(0f, 1f, 0f));

        Assert.Equal(Radiance, up.X, Radiance * 0.02f);
    }

    /// <summary>A surface next to an occluder is darker facing it than facing away.</summary>
    /// <remarks>
    ///     The away side is allowed to read <i>brighter than the sky</i>, within a bound. That is not
    ///     a defect in the gather — it is the positive mirror of the finding doc 19 § L2's bounce
    ///     recorded: four coefficients cannot hold a one-sided distribution, so the linear band that
    ///     answers below zero facing the dark side overshoots the constant facing the bright one. A
    ///     test asserting <c>away ≤ L</c> here would be asserting L1 is something it is not.
    /// </remarks>
    [Fact]
    public void AnOccluderDarkensTheDirectionItStandsIn() {
        var atlas = new ScreenProbeAtlas(new(new(16, 16)));
        var gather = new TracedScreenProbeGather(new Ball(), new UniformSky(Radiance));
        var probe = new Int2(0, 0);

        Assert.True(gather.FillProbe(atlas, probe, new(2f, 0f, 0f), new(1f, 0f, 0f)));

        atlas.Resolve();

        var resolved = atlas.Resolved(probe);
        var away = resolved.Irradiance(new(1f, 0f, 0f)).X;
        var toward = resolved.Irradiance(new(-1f, 0f, 0f)).X;

        Assert.True(
            toward < away && toward >= 0f && away <= Radiance * 1.25f,
            $"facing the ball reads {toward} against {away} away from it"
        );
    }

    /// <summary>
    ///     A ray that runs out of budget terminates in the far field where the field has an answer,
    ///     and falls back to the sky where it does not.
    /// </summary>
    /// <remarks>
    ///     Doc 19 § L3's trace order, last stage: distant lighting is amortised in § L2's field rather
    ///     than re-traced per probe. The two halves are one fixture with two budgets — a short one
    ///     whose rays end inside the field and read it, and a long one whose rays end beyond it and
    ///     read the sky — because "the field answers nothing outside its own box" is a property a
    ///     project has to get right, already written down once in § L2's bounce.
    /// </remarks>
    [Fact]
    public void AMissTerminatesInTheFarField() {
        const float FarRadiance = 0.4f;
        const float SkyRadiance = 0.9f;

        var field = new IrradianceField(new BoundingBox(new(-8f), new(8f)), new(2));

        field.AllocateAll();
        new TracedIrradianceFiller(new EmptyWorld(), new UniformSky(FarRadiance)).Fill(field);
        field.SyncBorders();

        var atlas = new ScreenProbeAtlas(new(new(16, 16)));

        var near = new TracedScreenProbeGather(
            new EmptyWorld(),
            new UniformSky(SkyRadiance),
            new ScreenProbeGatherSettings { MaxDistance = 4f }
        ) { FarField = field };

        near.Fill(atlas, new Floor());

        Assert.Equal(FarRadiance, atlas.Irradiance(new(8, 8), new(0f, 1f, 0f)).X, 5e-3f);

        var beyond = new TracedScreenProbeGather(
            new EmptyWorld(),
            new UniformSky(SkyRadiance),
            new ScreenProbeGatherSettings { MaxDistance = 50f }
        ) { FarField = field };

        beyond.Fill(atlas, new Floor());

        Assert.Equal(SkyRadiance, atlas.Irradiance(new(8, 8), new(0f, 1f, 0f)).X, 1e-3f);
    }

    /// <summary>A probe whose anchor shows the sky has nothing to stand on, and says so.</summary>
    [Fact]
    public void ASkyPixelLeavesItsProbeInvalid() {
        var atlas = new ScreenProbeAtlas(new(new(16, 16)));
        var gather = new TracedScreenProbeGather(new EmptyWorld(), new UniformSky(Radiance));

        Assert.Equal(0, gather.Fill(atlas, new Sky()));
        Assert.Equal(0, atlas.ValidCount);
        Assert.Equal(Vector3.Zero, atlas.Irradiance(new(8, 8), new(0f, 1f, 0f)));
    }

    /// <summary>A probe standing inside geometry is invalid before any ray is cast.</summary>
    [Fact]
    public void ABuriedProbeIsInvalid() {
        var atlas = new ScreenProbeAtlas(new(new(16, 16)));
        var gather = new TracedScreenProbeGather(new Ball(), new UniformSky(Radiance));

        Assert.False(gather.FillProbe(atlas, new(0, 0), Vector3.Zero, new(0f, 1f, 0f)));
        Assert.False(atlas.IsValid(new(0, 0)));
    }

    /// <summary>Two gathers of one scene agree to the bit — there is nothing stochastic to average.</summary>
    [Fact]
    public void TheGatherIsDeterministic() {
        var first = new ScreenProbeAtlas(new(new(32, 32)));
        var second = new ScreenProbeAtlas(new(new(32, 32)));
        var gather = new TracedScreenProbeGather(new Ball(), new LinearSky(0.6f, 0.3f));

        gather.Fill(first, new Floor());
        gather.Fill(second, new Floor());

        for (var y = 0; y < first.Layout.GridSize.Y; y++) {
            for (var x = 0; x < first.Layout.GridSize.X; x++) {
                Assert.Equal(first.Resolved(new(x, y)), second.Resolved(new(x, y)));
            }
        }
    }

    static IEnumerable<Int2> Pixels(Int2 viewport) {
        for (var y = 0; y < viewport.Y; y += 7) {
            for (var x = 0; x < viewport.X; x += 5) {
                yield return new(x, y);
            }
        }
    }

    // --- The scenes -------------------------------------------------------

    /// <summary>Nothing anywhere, so every ray reaches the sky.</summary>
    sealed class EmptyWorld : IDistanceField {
        public float Sample(Vector3 position) => 1e6f;

        public Vector3 SampleGradient(Vector3 position) => new(0f, 1f, 0f);
    }

    /// <summary>A solid half-space below y = 0.</summary>
    sealed class HalfSpace : IDistanceField {
        public float Sample(Vector3 position) => position.Y;

        public Vector3 SampleGradient(Vector3 position) => new(0f, 1f, 0f);
    }

    /// <summary>A sphere of radius one at the origin.</summary>
    sealed class Ball : IDistanceField {
        public float Sample(Vector3 position) => position.Length() - 1f;

        public Vector3 SampleGradient(Vector3 position) =>
            position.LengthSquared() > 1e-12f ? Vector3.Normalize(position) : new(0f, 1f, 0f);
    }

    /// <summary>One radiance from every direction, and surfaces that give back nothing.</summary>
    sealed class UniformSky(float radiance) : IRadianceSource {
        public Vector3 Sky(Vector3 direction) => new(radiance);

        public Vector3 Surface(Vector3 position, Vector3 normal, Vector3 direction) => Vector3.Zero;
    }

    /// <summary>A sky that brightens toward +y, and surfaces that give back nothing.</summary>
    sealed class LinearSky(float baseline, float tilt) : IRadianceSource {
        public Vector3 Sky(Vector3 direction) => new(baseline + (tilt * direction.Y));

        public Vector3 Surface(Vector3 position, Vector3 normal, Vector3 direction) => Vector3.Zero;
    }

    /// <summary>Every pixel shows a floor at y = 0, facing up.</summary>
    sealed class Floor : IScreenSurface {
        public bool TrySurface(Int2 pixel, out Vector3 position, out Vector3 normal) {
            position = new((pixel.X - 16) * 0.1f, 0f, (pixel.Y - 16) * 0.1f);
            normal = new(0f, 1f, 0f);

            return true;
        }
    }

    /// <summary>Every pixel shows the sky.</summary>
    sealed class Sky : IScreenSurface {
        public bool TrySurface(Int2 pixel, out Vector3 position, out Vector3 normal) {
            position = default;
            normal = default;

            return false;
        }
    }
}
