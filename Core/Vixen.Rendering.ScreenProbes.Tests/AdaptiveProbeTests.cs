// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering.DistanceFields;
using Vixen.Rendering.IrradianceFields;
using Xunit;

namespace Vixen.Rendering.ScreenProbes.Tests;

/// <summary>Extra probes where the lattice straddles a surface it never sampled.</summary>
/// <remarks>
///     The fallback arithmetic is tested over <i>seeded</i> maps — grid probes holding one constant,
///     the adaptive probe another — because under any physically gathered fixture every probe of an
///     empty world sees the same sky and the fallback's answer is indistinguishable from the blend
///     it replaced. Seeding is honest here for the reason the atlas states: the storage does not
///     know what filled it.
/// </remarks>
public class AdaptiveProbeTests {
    [Fact]
    public void TheAdaptiveRegionSitsBelowTheGrid() {
        var layout = new ScreenProbeLayout(new(64, 48), 16, 8, adaptiveRows: 2);

        Assert.Equal(new Int2(4, 3), layout.GridSize);
        Assert.Equal(8, layout.AdaptiveCapacity);

        // Two more rows of maps, and the grid's own addressing has not moved.
        Assert.Equal(new Int2(32, 40), layout.AtlasSize);
        Assert.Equal(new Int2(8, 8), layout.AtlasOrigin(new(1, 1)));

        Assert.Equal(new Int2(0, 24), layout.AdaptiveOrigin(0));
        Assert.Equal(new Int2(8, 32), layout.AdaptiveOrigin(5));
        Assert.Throws<ArgumentOutOfRangeException>(() => layout.AdaptiveOrigin(8));

        // No rows is today's layout exactly.
        Assert.Equal(0, new ScreenProbeLayout(new(64, 48)).AdaptiveCapacity);
        Assert.Equal(new Int2(32, 24), new ScreenProbeLayout(new(64, 48)).AtlasSize);
    }

    [Fact]
    public void TheCapacityIsABudgetNotAPromise() {
        var atlas = new ScreenProbeAtlas(new(new(64, 32), 16, 8, adaptiveRows: 1));

        for (var i = 0; i < 4; i++) {
            Assert.Equal(i, atlas.PlaceAdaptive(new(i, 0), new(0f, 1f, 0f), new(0f, 1f, 0f)));
        }

        // The fifth is refused quietly — the screen it would have served keeps its lattice.
        Assert.Equal(-1, atlas.PlaceAdaptive(new(4, 0), new(0f, 1f, 0f), new(0f, 1f, 0f)));
        Assert.Equal(4, atlas.AdaptiveCount);

        atlas.AdaptiveSurface(2, out var pixel, out var position, out var normal);
        Assert.Equal(new Int2(2, 0), pixel);
        Assert.Equal(1f, position.Y);
        Assert.Equal(1f, normal.Y);

        atlas.ClearAdaptive();
        Assert.Equal(0, atlas.AdaptiveCount);
        Assert.Throws<ArgumentOutOfRangeException>(() => atlas[0, new Int2(0, 0)]);
    }

    [Fact]
    public void AnAdaptiveMapResolvesLikeAnyOther() {
        var atlas = new ScreenProbeAtlas(new(new(64, 32), 16, 8, adaptiveRows: 1));
        var index = atlas.PlaceAdaptive(new(31, 8), new(0f, 1f, 0f), new(0f, 1f, 0f));

        for (var ty = 0; ty < 8; ty++) {
            for (var tx = 0; tx < 8; tx++) {
                atlas[index, new Int2(tx, ty)] = new(0.5f);
            }
        }

        atlas.Resolve();

        // A constant map is the closed form: irradiance over π equals the radiance, exactly,
        // because the texel weights are the exact solid angles.
        var lit = atlas.ResolvedAdaptive(index).Irradiance(new(0f, 1f, 0f));

        Assert.Equal(0.5f, lit.X, 1e-3f);
    }

    [Fact]
    public void AMismatchedPixelReadsItsOwnProbe() {
        // Grid probes on the floor plane y = 0 holding radiance one; an adaptive probe on a ledge
        // at y = 1 holding two. The pixel's position decides which surface it belongs to.
        var atlas = new ScreenProbeAtlas(new(new(64, 32), 16, 8, adaptiveRows: 1));
        var layout = atlas.Layout;

        for (var y = 0; y < layout.GridSize.Y; y++) {
            for (var x = 0; x < layout.GridSize.X; x++) {
                var probe = new Int2(x, y);
                var anchor = layout.Anchor(probe);

                atlas.SetSurface(probe, new(anchor.X * 0.1f, 0f, anchor.Y * 0.1f), new(0f, 1f, 0f));

                for (var ty = 0; ty < 8; ty++) {
                    for (var tx = 0; tx < 8; tx++) {
                        atlas[probe, new(tx, ty)] = new(1f);
                    }
                }
            }
        }

        var ledge = atlas.PlaceAdaptive(new(31, 8), new(3.1f, 1f, 0.8f), new(0f, 1f, 0f));

        for (var ty = 0; ty < 8; ty++) {
            for (var tx = 0; tx < 8; tx++) {
                atlas[ledge, new Int2(tx, ty)] = new(2f);
            }
        }

        atlas.Resolve();

        var up = new Vector3(0f, 1f, 0f);

        // On the ledge, every lattice tap is a different surface: the adaptive probe answers.
        Assert.Equal(2f, atlas.Irradiance(new(30, 8), new(3f, 1f, 0.8f), up, 0.1f).X, 1e-3f);

        // On the floor, the lattice answers as it always did.
        Assert.Equal(1f, atlas.Irradiance(new(30, 8), new(3f, 0f, 0.8f), up, 0.1f).X, 1e-3f);

        // A surface nothing stands on falls back to the unfiltered lattice — the bleed the
        // tolerance prevents elsewhere, chosen over a black hole.
        Assert.Equal(1f, atlas.Irradiance(new(30, 8), new(3f, 5f, 0.8f), up, 0.1f).X, 1e-3f);

        // And the position-blind overload never sees any of it.
        Assert.Equal(1f, atlas.Irradiance(new(30, 8), up).X, 1e-3f);
    }

    [Fact]
    public void TheGatherStandsProbesOnTheStraddledLedge() {
        // A ledge at y = 1 over pixels x ∈ [28, 35] — straddled by tiles 1 and 2, whose anchors at
        // x = 24 and 40 both stand on the floor. The tile corners at x = 31 and 32 are the
        // detectors, four rows of corners each: exactly eight adaptive probes.
        var gather = new TracedScreenProbeGather(
            new EmptySpace(),
            new UniformSky(),
            new ScreenProbeGatherSettings { AdaptiveTolerance = 0.1f }
        );

        var atlas = new ScreenProbeAtlas(new(new(64, 32), 16, 8, adaptiveRows: 2));

        gather.Fill(atlas, new Ledge());

        Assert.Equal(8, atlas.AdaptiveCount);

        for (var index = 0; index < atlas.AdaptiveCount; index++) {
            atlas.AdaptiveSurface(index, out var pixel, out var position, out _);

            Assert.InRange(pixel.X, 28, 35);
            Assert.Equal(1f, position.Y);

            // Under a uniform sky an empty world's probe sees the whole sky wherever it stands —
            // the closed form, now for a probe the lattice did not place.
            Assert.Equal(0.75f, atlas.ResolvedAdaptive(index).Irradiance(new(0f, 1f, 0f)).X, 1e-3f);
        }

        // A smaller budget stops quietly at the budget.
        var small = new ScreenProbeAtlas(new(new(64, 32), 16, 8, adaptiveRows: 1));

        gather.Fill(small, new Ledge());

        Assert.Equal(4, small.AdaptiveCount);
    }

    sealed class Ledge : IScreenSurface {
        public bool TrySurface(Int2 pixel, out Vector3 position, out Vector3 normal) {
            var y = pixel.X is >= 28 and <= 35 ? 1f : 0f;

            position = new(pixel.X * 0.1f, y, pixel.Y * 0.1f);
            normal = new(0f, 1f, 0f);

            return true;
        }
    }

    sealed class EmptySpace : IDistanceField {
        public float Sample(Vector3 position) => 1e6f;

        public Vector3 SampleGradient(Vector3 position) => new(0f, 1f, 0f);
    }

    sealed class UniformSky : IRadianceSource {
        public Vector3 Sky(Vector3 direction) => new(0.75f);

        public Vector3 Surface(Vector3 position, Vector3 normal, Vector3 direction) => Vector3.Zero;
    }
}
