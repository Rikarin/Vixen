// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering.DistanceFields;
using Xunit;

namespace Vixen.Rendering.SurfaceCache.Tests;

/// <summary>Doc 19 § L4's exit criterion: the Cornell box, converged and measured.</summary>
/// <remarks>
///     <para>
///         The section's exit reads: <i>a Cornell-box fixture converges to a reference within a
///         stated error; the second bounce is visible and measurable rather than asserted.</i> The
///         box is the classic five walls with an emissive panel in the ceiling and an open front,
///         cached as five authored cards; the reference is a five-bounce path tracer over the same
///         field and materials, deterministic so it referees. The chain's answer at a floor point
///         is a cosine gather over the converged cache — exactly what a probe's rays will see.
///     </para>
///     <para>
///         <b>The stated error is five per cent</b>, and its parts are named: twenty-four texels
///         a wall side, point-sampled; a capture that stands a sphere-trace threshold off the true
///         surface; and a bounce series cut where its largest change drops under a thousandth. The
///         second bounce is measured as the red wall's colour arriving on the floor: after one
///         gather the floor has only the white panel's light and its red-to-green ratio is one;
///         converged, the ratio rises by more than a tenth — colour that took two bounces to get
///         there, measured rather than asserted.
///     </para>
/// </remarks>
public class CornellBoxTests {
    const int ReferencePaths = 2048;
    const int ReferenceBounces = 5;

    static readonly Vector3 White = new(0.73f);
    static readonly Vector3 Red = new(0.63f, 0.065f, 0.05f);
    static readonly Vector3 Green = new(0.14f, 0.45f, 0.091f);
    static readonly Vector3 Panel = new(5f);

    [Fact]
    public void TheBoxConvergesToTheReferenceWithinTheStatedError() {
        var cache = Cached(out var radiosity);
        var passes = Converge(cache, radiosity);

        Assert.InRange(passes, 2, 40);

        // Two floor points, mid-box and off-centre: the chain's incident light against the path
        // tracer's, as a fraction of the reference.
        foreach (var x in new[] { 1f, 0.55f }) {
            var point = new Vector3(x, 0f, 1f);
            var truth = PathTraced(point, new(0f, 1f, 0f));
            var chain = GatherFromCache(cache, point, new(0f, 1f, 0f));

            var error = (chain - truth).Length() / MathF.Max(truth.Length(), 1e-3f);

            Assert.True(error < 0.05f, $"at {point}: reference {truth}, chain {chain}, error {error}");
        }
    }

    [Fact]
    public void TheSecondBounceIsVisibleAndMeasurable() {
        var cache = Cached(out var radiosity);
        var point = new Vector3(0.2f, 0f, 1f);
        var up = new Vector3(0f, 1f, 0f);

        // Before any gather, the cards radiate their emissive alone: the floor sees the white
        // panel and nothing else, and its red-to-green ratio is exactly the panel's — one. (One
        // gather pass would already carry the second bounce, because reading the cache from the
        // floor is itself a transport step.)
        var single = GatherFromCache(cache, point, up);
        var singleTint = single.X / MathF.Max(single.Y, 1e-4f);

        Assert.Equal(1f, singleTint, 0.05f);

        // Converged, the red wall's first-bounce light has arrived on the floor: light that took
        // two bounces — panel to wall, wall to floor — and is measured, not asserted.
        Converge(cache, radiosity);

        var converged = GatherFromCache(cache, point, up);
        var tint = converged.X / MathF.Max(converged.Y, 1e-4f);

        Assert.True(tint > singleTint + 0.1f, $"the red wall never arrived: {singleTint} → {tint}");

        // And the bounce raised the floor's light overall — energy went up, not sideways.
        Assert.True(converged.Length() > single.Length(), $"the bounce lost energy: {single} → {converged}");
    }

    [Fact]
    public void AScreenProbeInTheBoxInheritsTheBounces() {
        // The seam § L4 exists to fill: the screen-probe gather's hits have returned black since
        // the day it was written, each fixture noting the surface cache was coming. Composed over
        // the converged cache, a probe on the Cornell floor sees the panel, the walls' first-hit
        // light and every bounce behind it — and its sixty-four-texel answer lands within the
        // quadrature's reach of the 2048-ray gather this file already trusts.
        var cache = Cached(out var radiosity);

        Converge(cache, radiosity);

        var atlas = new ScreenProbes.ScreenProbeAtlas(new(new(16, 16)));
        var gather = new ScreenProbes.TracedScreenProbeGather(
            new Box(),
            new SurfaceCacheRadiance(cache, new BlackWorld())
        );

        Assert.True(gather.FillProbe(atlas, new(0, 0), new(1f, 0f, 1f), new(0f, 1f, 0f)));
        atlas.Resolve();

        // Texel for texel, against the cache asked directly — non-circular, because the atlas
        // value crossed the gather and the radiance seam while the expectation queries the store.
        // Straight up is the panel: its emissive plus everything the white ceiling gathered.
        var upTexel = ScreenProbes.OctahedralMap.Texel(new(0f, 1f, 0f), 8);

        Assert.True(cache.TryRadiance(new(1f, 2f, 1f), new(0f, -1f, 0f), out var panel));
        Assert.Equal(panel.X, atlas[new Int2(0, 0), upTexel].X, 0.15f);
        Assert.True(atlas[new Int2(0, 0), upTexel].X > 4f, $"the panel never arrived: {atlas[new Int2(0, 0), upTexel]}");

        // Toward the left wall is red — light that exists only because the radiosity put it there.
        var redTexel = ScreenProbes.OctahedralMap.Texel(Vector3.Normalize(new(-1f, 1f, 0f)), 8);
        var towardWall = atlas[new Int2(0, 0), redTexel];

        Assert.True(
            towardWall.X > towardWall.Y * 2f,
            $"the red wall reads {towardWall}, which is not red"
        );

        // And the resolved probe carries real light where the black world's carries none.
        Assert.True(atlas.Resolved(new(0, 0)).Irradiance(new(0f, 1f, 0f)).Length() > 0.3f);

        // And without the cache the same probe is dark — the before, kept as the discriminator.
        var dark = new ScreenProbes.ScreenProbeAtlas(new(new(16, 16)));

        new ScreenProbes.TracedScreenProbeGather(new Box(), new BlackWorld()).FillProbe(dark, new(0, 0), new(1f, 0f, 1f), new(0f, 1f, 0f));
        dark.Resolve();

        Assert.Equal(0f, dark.Resolved(new(0, 0)).Irradiance(new(0f, 1f, 0f)).Length(), 1e-4f);
    }

    [Fact]
    public void TwoConvergencesAgreeToTheBit() {
        // The property every dispatch comparison will lean on: the gather is deterministic, so two
        // caches built the same way hold identical answers — bit-identical, not merely close.
        var first = Cached(out var firstRadiosity);
        var second = Cached(out var secondRadiosity);

        Converge(first, firstRadiosity);
        Converge(second, secondRadiosity);

        for (var card = 0; card < first.Cards.Count; card++) {
            var resolution = first.Cards[card].Card.Resolution;

            for (var y = 0; y < resolution.Y; y++) {
                for (var x = 0; x < resolution.X; x++) {
                    Assert.Equal(first.Gathered(card, new(x, y)), second.Gathered(card, new(x, y)));
                }
            }
        }
    }

    /// <summary>Five cards, captured and ready to bounce.</summary>
    static SurfaceCacheStore Cached(out CardRadiosity radiosity) {
        var atlas = new SurfaceCacheAtlas(new(128, 128));
        var cache = new SurfaceCacheStore(atlas) { DepthTolerance = 0.15f };
        var box = new Box();
        var capture = new TracedCardCapture(box, new CornellPaint());

        Span<SurfaceCard> cards = [
            new(2, new(1f, 0f, 1f), new(1f, 0.2f, 1f), new(24, 24)),  // floor, facing up
            new(3, new(1f, 2f, 1f), new(1f, 0.2f, 1f), new(24, 24)),  // ceiling, facing down
            new(0, new(0f, 1f, 1f), new(0.2f, 1f, 1f), new(24, 24)),  // left wall, facing +X
            new(1, new(2f, 1f, 1f), new(0.2f, 1f, 1f), new(24, 24)),  // right wall, facing −X
            new(4, new(1f, 1f, 0f), new(1f, 1f, 0.2f), new(24, 24))   // back wall, facing +Z
        ];

        foreach (var card in cards) {
            var index = cache.AddCard(card);

            Assert.True(index >= 0);
            Assert.True(capture.Capture(cache, index) > 0);
        }

        radiosity = new(new Box());

        return cache;
    }

    /// <summary>Gathers until the largest per-texel change drops under a thousandth.</summary>
    static int Converge(SurfaceCacheStore cache, CardRadiosity radiosity) {
        for (var pass = 1; pass <= 40; pass++) {
            if (radiosity.Gather(cache) < 1e-3f) {
                return pass;
            }
        }

        Assert.Fail("the box did not converge in forty passes");

        return 0;
    }

    /// <summary>Incident irradiance over π at a point, out of the converged cache — a probe's view.</summary>
    static Vector3 GatherFromCache(SurfaceCacheStore cache, Vector3 position, Vector3 normal) {
        var box = new Box();
        var trace = new DistanceFieldTraceSettings { MaxDistance = 100f };
        var origin = position + (normal * 0.01f);
        var tangent = Vector3.Normalize(Vector3.Cross(MathF.Abs(normal.Y) < 0.99f ? new(0f, 1f, 0f) : new(1f, 0f, 0f), normal));
        var bitangent = Vector3.Cross(normal, tangent);
        var sum = Vector3.Zero;

        for (var ray = 0; ray < ReferencePaths; ray++) {
            var direction = Cosine(ray, ReferencePaths, 0, tangent, bitangent, normal);
            var hit = DistanceFieldTracer.Trace(box, origin, direction, trace);

            if (hit.Hit && cache.TryRadiance(hit.Position, hit.Normal, out var radiance)) {
                sum += radiance;
            }
        }

        return sum / ReferencePaths;
    }

    /// <summary>The reference: iterative path tracing, five bounces, deterministic.</summary>
    static Vector3 PathTraced(Vector3 position, Vector3 normal) {
        var box = new Box();
        var paint = new CornellPaint();
        var trace = new DistanceFieldTraceSettings { MaxDistance = 100f };
        var total = Vector3.Zero;

        for (var path = 0; path < ReferencePaths; path++) {
            var origin = position + (normal * 0.01f);
            var facing = normal;
            var throughput = Vector3.One;

            for (var bounce = 0; bounce < ReferenceBounces; bounce++) {
                var tangent = Vector3.Normalize(
                    Vector3.Cross(MathF.Abs(facing.Y) < 0.99f ? new(0f, 1f, 0f) : new(1f, 0f, 0f), facing)
                );
                var direction = Cosine(path, ReferencePaths, bounce, tangent, Vector3.Cross(facing, tangent), facing);
                var hit = DistanceFieldTracer.Trace(box, origin, direction, trace);

                if (!hit.Hit) {
                    break;
                }

                total += throughput * paint.Emissive(hit.Position, hit.Normal);
                throughput *= paint.Albedo(hit.Position, hit.Normal);
                origin = hit.Position + (hit.Normal * 0.01f);
                facing = hit.Normal;
            }
        }

        return total / ReferencePaths;
    }

    /// <summary>A cosine-weighted direction, deterministic per path and bounce.</summary>
    /// <remarks>
    ///     Halton, two fresh prime bases per bounce. The first version shifted one sequence by the
    ///     golden ratio per bounce, which correlates successive bounces perfectly — a
    ///     one-dimensional lattice threading a four-dimensional domain — and biased the two-bounce
    ///     estimate by a fifth. A reference has to cover the joint domain, not each marginal.
    /// </remarks>
    static Vector3 Cosine(int path, int paths, int bounce, Vector3 tangent, Vector3 bitangent, Vector3 normal) {
        Span<int> primes = [2, 3, 5, 7, 11, 13, 17, 19, 23, 29];

        var u = Halton(path + 1, primes[bounce * 2]);
        var v = Halton(path + 1, primes[(bounce * 2) + 1]);
        var r = MathF.Sqrt(u);
        var phi = 2f * MathF.PI * v;

        return (tangent * (r * MathF.Cos(phi))) + (bitangent * (r * MathF.Sin(phi))) + (normal * MathF.Sqrt(1f - u));
    }

    static float Halton(int index, int prime) {
        var result = 0f;
        var fraction = 1f / prime;
        var remaining = index;

        while (remaining > 0) {
            result += fraction * (remaining % prime);
            remaining /= prime;
            fraction /= prime;
        }

        return result;
    }

    /// <summary>A world with no sky and dark surfaces — what the tracers saw before § L4.</summary>
    sealed class BlackWorld : Rendering.IrradianceFields.IRadianceSource {
        public Vector3 Sky(Vector3 direction) => Vector3.Zero;

        public Vector3 Surface(Vector3 position, Vector3 normal, Vector3 direction) => Vector3.Zero;
    }

    /// <summary>Five solid walls around a 2×2×2 interior, open toward +Z.</summary>
    sealed class Box : IDistanceField {
        public float Sample(Vector3 position) =>
            MathF.Min(
                MathF.Min(position.Y, 2f - position.Y),
                MathF.Min(MathF.Min(position.X, 2f - position.X), position.Z)
            );

        public Vector3 SampleGradient(Vector3 position) {
            var distance = Sample(position);

            if (distance == position.Y) {
                return new(0f, 1f, 0f);
            }

            if (distance == 2f - position.Y) {
                return new(0f, -1f, 0f);
            }

            if (distance == position.X) {
                return new(1f, 0f, 0f);
            }

            if (distance == 2f - position.X) {
                return new(-1f, 0f, 0f);
            }

            return new(0f, 0f, 1f);
        }
    }

    /// <summary>White walls, a red left, a green right, and the emissive panel in the ceiling.</summary>
    sealed class CornellPaint : ISurfaceMaterial {
        public Vector3 Albedo(Vector3 position, Vector3 normal) {
            if (position.X < 0.05f) {
                return Red;
            }

            return position.X > 1.95f ? Green : White;
        }

        public Vector3 Emissive(Vector3 position, Vector3 normal) =>
            position.Y > 1.9f && MathF.Abs(position.X - 1f) < 0.5f && MathF.Abs(position.Z - 1f) < 0.5f
                ? Panel
                : Vector3.Zero;
    }
}
