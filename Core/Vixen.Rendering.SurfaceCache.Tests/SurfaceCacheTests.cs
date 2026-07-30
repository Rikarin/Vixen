// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering.DistanceFields;
using Xunit;

namespace Vixen.Rendering.SurfaceCache.Tests;

/// <summary>The cards' geometry, the atlas's budget, and the capture's closed forms.</summary>
public class SurfaceCacheTests {
    [Fact]
    public void ACardsFrameIsTheCyclicRule() {
        // +Y: U runs along Z, V along X — cyclic from the axis, whatever the sign.
        var card = new SurfaceCard(2, Vector3.Zero, new(1f, 0.5f, 1f), new(2, 2));

        Assert.Equal(new Vector3(0f, 1f, 0f), card.Direction);
        Assert.Equal(2, card.UComponent);
        Assert.Equal(0, card.VComponent);

        // Texel (0,0) is the low corner of the near plane — the face the capture enters.
        var origin = card.TexelOrigin(new(0, 0));

        Assert.Equal(-0.5f, origin.Z, 1e-5f);
        Assert.Equal(-0.5f, origin.X, 1e-5f);
        Assert.Equal(0.5f, origin.Y, 1e-5f);

        // −Y flips the direction and nothing else.
        Assert.Equal(new Vector3(0f, -1f, 0f), new SurfaceCard(3, Vector3.Zero, Vector3.One, new(1, 1)).Direction);
    }

    [Fact]
    public void ProjectionInvertsTheTexelWalk() {
        var card = new SurfaceCard(2, new(3f, 1f, -2f), new(2f, 0.5f, 1f), new(8, 4));

        // A point a quarter of the way inside, under texel (5, 1)'s centre.
        var world = card.TexelOrigin(new(5, 1)) - (card.Direction * 0.25f);

        Assert.True(card.TryProject(world, out var texel, out var depth));
        Assert.Equal(new Int2(5, 1), texel);
        Assert.Equal(0.25f, depth, 1e-5f);

        // Outside the box on any axis is nobody's texel.
        Assert.False(card.TryProject(card.Centre + new Vector3(0f, 0.6f, 0f), out _, out _));
        Assert.False(card.TryProject(card.Centre + new Vector3(0f, 0f, 2.1f), out _, out _));
    }

    [Fact]
    public void ACubeGrowsSixCardsAndAFloorGrowsOne() {
        Span<Vector3> cube = [
            new(0f, 0f, 0f), new(1f, 0f, 0f), new(1f, 1f, 0f), new(0f, 1f, 0f),
            new(0f, 0f, 1f), new(1f, 0f, 1f), new(1f, 1f, 1f), new(0f, 1f, 1f)
        ];

        Span<int> indices = [
            0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, // -Z, +Z
            0, 1, 5, 0, 5, 4, 3, 7, 6, 3, 6, 2, // -Y, +Y
            0, 4, 7, 0, 7, 3, 1, 2, 6, 1, 6, 5  // -X, +X
        ];

        var cards = CardGenerator.Generate(cube, indices, texelsPerUnit: 8f, margin: 0f);

        Assert.Equal(6, cards.Count);
        Assert.Equal(Enumerable.Range(0, 6), cards.Select(card => card.Axis));

        foreach (var card in cards) {
            Assert.Equal(8, card.Resolution.X);
            Assert.Equal(8, card.Resolution.Y);
        }

        Span<Vector3> floor = [new(0f, 0f, 0f), new(4f, 0f, 0f), new(4f, 0f, 2f), new(0f, 0f, 2f)];
        Span<int> quad = [0, 2, 1, 0, 3, 2];

        var flat = CardGenerator.Generate(floor, quad, texelsPerUnit: 4f, margin: 0f);

        var only = Assert.Single(flat);

        // Facing +Y, U along Z (2 units → 8 texels), V along X (4 units → 16).
        Assert.Equal(2, only.Axis);
        Assert.Equal(new Int2(8, 16), only.Resolution);
    }

    [Fact]
    public void TheAtlasIsABudgetWithExactReuse() {
        var atlas = new SurfaceCacheAtlas(new(16, 16));

        Assert.True(atlas.TryAllocate(new(16, 8), out var first));
        Assert.True(atlas.TryAllocate(new(16, 8), out var second));
        Assert.Equal(new Int2(0, 0), first);
        Assert.Equal(new Int2(0, 8), second);

        // Full is a refusal, not a throw.
        Assert.False(atlas.TryAllocate(new(1, 1), out _));

        // A release is reused on exact size match.
        atlas.Release(first, new(16, 8));
        Assert.True(atlas.TryAllocate(new(16, 8), out var reused));
        Assert.Equal(first, reused);
        Assert.Equal(256, atlas.Occupied);
    }

    [Fact]
    public void ACaptureReadsTheSurfaceItLooksAt() {
        // A floor at y = 0 under a +Y card whose near plane floats at y = 0.2: every texel hits,
        // at depth 0.2, facing up, wearing the material's answers.
        var atlas = new SurfaceCacheAtlas(new(32, 32));
        var cache = new SurfaceCacheStore(atlas);
        var card = cache.AddCard(new(2, new(0f, 0f, 0f), new(1f, 0.2f, 1f), new(4, 4)));

        var captured = new TracedCardCapture(new Floor(), new Paint(new(0.5f, 0.25f, 0.125f))).Capture(cache, card);

        Assert.Equal(16, captured);

        var surface = cache.Surface(card, new(1, 2));

        // Within the sphere trace's own arrival threshold — the reference gather's tolerance, not
        // a shrug: a march stops a hair above the zero crossing by design.
        Assert.Equal(0.2f, surface.Depth, 0.01f);
        Assert.Equal(1f, surface.Normal.Y, 1e-3f);
        Assert.Equal(0.5f, surface.Albedo.X, 1e-5f);

        // And the cache can find that surface again from its world position.
        Assert.True(cache.TryRadiance(new(0.1f, 0f, 0.3f), new(0f, 1f, 0f), out _));
        Assert.False(cache.TryRadiance(new(0.1f, 0.15f, 0.3f), new(0f, 1f, 0f), out _));
    }

    [Fact]
    public void SunlightOnTheCacheIsCosineOverPiBehindAShadowRay() {
        var atlas = new SurfaceCacheAtlas(new(32, 32));
        var cache = new SurfaceCacheStore(atlas);
        var card = cache.AddCard(new(2, new(0f, 0f, 0f), new(1f, 0.2f, 1f), new(4, 4)));
        var world = new Floor();

        new TracedCardCapture(world, new Paint(new(0.7f))).Capture(cache, card);

        var radiosity = new CardRadiosity(world);

        radiosity.Light(cache, Vector3.Normalize(new(0f, 1f, 0f)), new(3.14159265f));

        // Sun straight overhead at irradiance π: the direct term is exactly one.
        Assert.Equal(1f, cache.Direct(card, new(2, 2)).X, 1e-4f);

        // Outgoing folds the albedo in: emissive zero, 0.7 · 1.
        Assert.Equal(0.7f, cache.Outgoing(card, new(2, 2)).X, 1e-4f);
    }

    [Fact]
    public void AGatherUnderAUniformSkyIsTheSky() {
        var atlas = new SurfaceCacheAtlas(new(32, 32));
        var cache = new SurfaceCacheStore(atlas);
        var card = cache.AddCard(new(2, new(0f, 0f, 0f), new(1f, 0.2f, 1f), new(4, 4)));
        var world = new Floor();

        new TracedCardCapture(world, new Paint(new(0.7f))).Capture(cache, card);

        var radiosity = new CardRadiosity(world) { Sky = _ => new(0.6f) };

        var change = radiosity.Gather(cache);

        // Every hemisphere ray escapes into the constant sky: the mean is the constant, exactly,
        // and the first pass's change is that constant arriving where zero stood.
        Assert.Equal(0.6f, cache.Gathered(card, new(1, 1)).X, 1e-4f);
        Assert.Equal(0.6f, change, 1e-4f);

        // A second pass changes nothing: the sky does not bounce off a lone floor into itself.
        Assert.Equal(0f, radiosity.Gather(cache), 1e-4f);
    }

    [Fact]
    public void TheIndexAgreesWithTheLinearScanItReplaced() {
        // Deterministic pseudo-random cards straddling many grid cells, some overlapping, some tiny
        // against the cell size and some larger than it — the shapes that catch an off-by-one at a
        // cell boundary.
        var atlas = new SurfaceCacheAtlas(new(128, 128));
        var cache = new SurfaceCacheStore(atlas);
        var seed = 12345u;

        float Next() {
            seed = (seed * 1664525u) + 1013904223u;

            return (seed >> 8) * (1f / 16777216f);
        }

        for (var added = 0; added < 24; added++) {
            var centre = new Vector3((Next() * 20f) - 10f, (Next() * 20f) - 10f, (Next() * 20f) - 10f);
            var half = new Vector3(0.1f + (Next() * 6f), 0.1f + (Next() * 6f), 0.1f + (Next() * 6f));
            var card = cache.AddCard(new((int)(Next() * 6f), centre, half, new(4, 4)));

            Assert.True(card >= 0);

            // Every texel valid at mid-depth, facing the card, so depth agreement can pass.
            var shape = cache.Cards[card].Card;

            for (var y = 0; y < 4; y++) {
                for (var x = 0; x < 4; x++) {
                    cache.SetSurface(
                        card,
                        new(x, y),
                        new(new(0.5f), shape.Direction, shape.Extents.Depth, new(added + 1f, 0f, 0f))
                    );
                }
            }
        }

        // The linear scan, written out again here as the referee the index must agree with.
        bool Linear(Vector3 position, Vector3 normal, out Vector3 radiance) {
            radiance = default;

            var best = -1;
            var bestFacing = 0f;
            var bestTexel = default(Int2);

            for (var card = 0; card < cache.Cards.Count; card++) {
                var shape = cache.Cards[card].Card;
                var facing = Vector3.Dot(normal, shape.Direction);

                if (facing <= bestFacing || !shape.TryProject(position, out var texel, out var depth)) {
                    continue;
                }

                if (!cache.IsValid(card, texel) || MathF.Abs(cache.Surface(card, texel).Depth - depth) > cache.DepthTolerance) {
                    continue;
                }

                best = card;
                bestFacing = facing;
                bestTexel = texel;
            }

            if (best < 0) {
                return false;
            }

            radiance = cache.Outgoing(best, bestTexel);

            return true;
        }

        var found = 0;

        for (var query = 0; query < 512; query++) {
            Vector3 position;
            Vector3 normal;

            if ((query & 1) == 0) {
                // On a stored surface, give or take a jitter inside the depth tolerance — the
                // queries that must find something, or the comparison is comparing misses.
                var card = Math.Min((int)(Next() * cache.Cards.Count), cache.Cards.Count - 1);
                var shape = cache.Cards[card].Card;
                var texel = new Int2((int)(Next() * 4f), (int)(Next() * 4f));

                position = shape.TexelOrigin(texel)
                    - (shape.Direction * (shape.Extents.Depth + ((Next() - 0.5f) * 0.15f)));
                normal = shape.Direction;
            } else {
                // Anywhere at all — mostly misses, and both scans must agree those are misses too.
                position = new((Next() * 24f) - 12f, (Next() * 24f) - 12f, (Next() * 24f) - 12f);
                normal = new SurfaceCard(Math.Min((int)(Next() * 6f), 5), Vector3.Zero, Vector3.One, new(1, 1)).Direction;
            }

            var hit = cache.TryRadiance(position, normal, out var indexed);
            var expected = Linear(position, normal, out var scanned);

            Assert.Equal(expected, hit);
            Assert.Equal(scanned, indexed);

            if (hit) {
                found++;
            }
        }

        // A comparison that never found anything compared nothing.
        Assert.True(found > 200, $"only {found} of 512 queries landed on a card — the fixture is too sparse to referee");
    }

    /// <summary>A solid half-space below y = 0.</summary>
    sealed class Floor : IDistanceField {
        public float Sample(Vector3 position) => position.Y;

        public Vector3 SampleGradient(Vector3 position) => new(0f, 1f, 0f);
    }

    sealed class Paint(Vector3 albedo) : ISurfaceMaterial {
        public Vector3 Albedo(Vector3 position, Vector3 normal) => albedo;

        public Vector3 Emissive(Vector3 position, Vector3 normal) => Vector3.Zero;
    }
}
