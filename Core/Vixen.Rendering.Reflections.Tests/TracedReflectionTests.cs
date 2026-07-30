// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering.DistanceFields;
using Vixen.Rendering.IrradianceFields;
using Xunit;

namespace Vixen.Rendering.Reflections.Tests;

/// <summary>The mirror, the threshold, the fallback order and the bias — § L5's closed forms.</summary>
public class TracedReflectionTests {
    static readonly Vector3 SurfaceAnswer = new(0.8f, 0.2f, 0.1f);
    static readonly Vector3 SkyAnswer = new(0f, 0f, 1f);

    [Fact]
    public void TheMirrorFormulaIsTheTextbookOne() {
        // A camera looking down at forty-five degrees onto a floor: the reflection leaves at
        // forty-five degrees the other way, exactly. The scene is empty, so the answer reaches the
        // fallback along the mirror direction — which is how the direction is observed at all.
        var seen = new RecordingFallback();
        var tracer = new TracedReflections(new Empty(), new Constant(), seen);

        tracer.Reflect(Vector3.Zero, new(0f, 1f, 0f), Vector3.Normalize(new(1f, -1f, 0f)), 0f);

        var expected = Vector3.Normalize(new(1f, 1f, 0f));

        Assert.Equal(expected.X, seen.Direction.X, 1e-6f);
        Assert.Equal(expected.Y, seen.Direction.Y, 1e-6f);
        Assert.Equal(0f, seen.Direction.Z, 1e-6f);
    }

    [Fact]
    public void AMirrorSeesTheSurfaceItHitsAndAMissSeesTheFallback() {
        // A wall filling x ≥ 2. The mirror ray from the floor at forty-five degrees runs up and
        // toward it, hits its face, and answers with what the radiance source says that surface
        // radiates — L4's seam, which is the entire point of asking through IRadianceSource.
        var radiance = new Constant();
        var tracer = new TracedReflections(new Wall(), radiance, new SkyFallback(radiance));
        var reflected = tracer.Reflect(Vector3.Zero, new(0f, 1f, 0f), Vector3.Normalize(new(1f, -1f, 0f)), 0f);

        Assert.Equal(SurfaceAnswer, reflected);
        Assert.Equal(2f, radiance.Hit.X, 0.02f);

        // The same ray through an empty world reaches the sky — the fallback, not black.
        var open = new TracedReflections(new Empty(), radiance, new SkyFallback(radiance));

        Assert.Equal(SkyAnswer, open.Reflect(Vector3.Zero, new(0f, 1f, 0f), Vector3.Normalize(new(1f, -1f, 0f)), 0f));
    }

    [Fact]
    public void TheBiasKeepsAMirrorOffItsOwnSurface() {
        // A mirror lying ON the floor it reflects from. Without the bias the march's first sample
        // is the mirror's own surface at distance nothing — every reflection comes back as the
        // reflector's own colour, which is the failure the bias exists to prevent, held here so
        // the why survives the parameter.
        var radiance = new Constant();
        var floor = new Floor();
        var view = Vector3.Normalize(new(1f, -1f, 0f));

        var biased = new TracedReflections(floor, radiance, new SkyFallback(radiance));

        Assert.Equal(SkyAnswer, biased.Reflect(Vector3.Zero, new(0f, 1f, 0f), view, 0f));

        var unbiased = new TracedReflections(floor, radiance, new SkyFallback(radiance)) { Bias = 0f };

        Assert.Equal(SurfaceAnswer, unbiased.Reflect(Vector3.Zero, new(0f, 1f, 0f), view, 0f));
    }

    [Fact]
    public void RoughnessReadsTheFieldInsteadOfTracing() {
        // A field filled under a uniform sky holds that sky exactly — the field's own closed form —
        // and a wall stands where the mirror ray would hit. Below the threshold the wall appears;
        // at it, the field answers and the wall does not, which is the discriminating pair: rough
        // is not a darker mirror, it is a different read.
        const float Uniform = 2.5f;

        var field = new IrradianceField(new BoundingBox(new(-4f), new(4f)), new(2));

        field.AllocateAll();
        new TracedIrradianceFiller(new Empty(), new Constant { SkyValue = new(Uniform) }).Fill(field);

        var radiance = new Constant();
        var view = Vector3.Normalize(new(1f, -1f, 0f));

        var tracer = new TracedReflections(new Wall(), radiance, new SkyFallback(radiance)) { Field = field };

        Assert.Equal(SurfaceAnswer, tracer.Reflect(Vector3.Zero, new(0f, 1f, 0f), view, 0.49f));

        var rough = tracer.Reflect(Vector3.Zero, new(0f, 1f, 0f), view, 0.5f);

        Assert.Equal(Uniform, rough.X, 0.05f);
        Assert.Equal(Uniform, rough.Y, 0.05f);

        // And with no field to read, a rough reflection is the fallback's whole — the probes' seam,
        // not a quiet zero.
        var fieldless = new TracedReflections(new Wall(), radiance, new SkyFallback(radiance));

        Assert.Equal(SkyAnswer, fieldless.Reflect(Vector3.Zero, new(0f, 1f, 0f), view, 0.9f));
    }

    [Fact]
    public void TheBandBlendsWhatTheThresholdWouldCut() {
        // A field of 2.5 against a wall answer of 0.8: at the band's midpoint the reflection is the
        // midpoint of the two, which no single path produces — the discriminator for the lerp
        // running rather than either side winning outright.
        const float Uniform = 2.5f;

        var field = new IrradianceField(new BoundingBox(new(-4f), new(4f)), new(2));

        field.AllocateAll();
        new TracedIrradianceFiller(new Empty(), new Constant { SkyValue = new(Uniform) }).Fill(field);

        var radiance = new Constant();
        var view = Vector3.Normalize(new(1f, -1f, 0f));

        var tracer = new TracedReflections(new Wall(), radiance, new SkyFallback(radiance)) {
            Field = field,
            RoughnessThreshold = 0.5f,
            RoughnessBlend = 0.2f
        };

        // Below the band: the wall whole. At the threshold: the field whole. Midway: half of each.
        Assert.Equal(SurfaceAnswer.X, tracer.Reflect(Vector3.Zero, new(0f, 1f, 0f), view, 0.29f).X, 1e-3f);
        Assert.Equal(Uniform, tracer.Reflect(Vector3.Zero, new(0f, 1f, 0f), view, 0.5f).X, 0.05f);

        var middle = tracer.Reflect(Vector3.Zero, new(0f, 1f, 0f), view, 0.4f);

        Assert.Equal((SurfaceAnswer.X + Uniform) * 0.5f, middle.X, 0.05f);
    }

    [Fact]
    public void AReflectionCarriesTheCachesBounces() {
        // The reuse § L5 exists for, in one assertion: hand the tracer L4's SurfaceCacheRadiance
        // and a mirror facing a cached wall reflects the wall's outgoing radiance — emissive plus
        // albedo times its lighting — with this package knowing nothing about cards.
        var store = new SurfaceCache.SurfaceCacheStore(new SurfaceCache.SurfaceCacheAtlas(new(16, 16)));
        var card = store.AddCard(new(1, new(2.1f, 1f, 0f), new(0.2f, 1.5f, 1.5f), new(4, 4)));
        var wall = new Wall();

        new SurfaceCache.TracedCardCapture(wall, new Paint()).Capture(store, card);
        new SurfaceCache.CardRadiosity(wall) { Sky = _ => new(1f) }.Gather(store);

        var dark = new Constant { SurfaceValue = Vector3.Zero, SkyValue = Vector3.Zero };
        var cached = new SurfaceCache.SurfaceCacheRadiance(store, dark);
        var tracer = new TracedReflections(wall, cached, new SkyFallback(dark));

        var reflected = tracer.Reflect(Vector3.Zero, new(0f, 1f, 0f), Vector3.Normalize(new(1f, -1f, 0f)), 0f);

        // Emissive (0.3) plus albedo (0.5) times the gathered sky (1): brighter than either alone,
        // and exactly the store's own outgoing convention.
        Assert.Equal(0.3f + 0.5f, reflected.X, 0.1f);
        Assert.True(reflected.X > 0.5f, $"the reflection ({reflected.X}) never read the cache");
    }

    /// <summary>Solid half-space x ≥ 2 — the wall the mirror rays aim at.</summary>
    sealed class Wall : IDistanceField {
        public float Sample(Vector3 position) => 2f - position.X;

        public Vector3 SampleGradient(Vector3 position) => new(-1f, 0f, 0f);
    }

    /// <summary>Solid half-space below y = 0 — the floor a mirror lies on.</summary>
    sealed class Floor : IDistanceField {
        public float Sample(Vector3 position) => position.Y;

        public Vector3 SampleGradient(Vector3 position) => new(0f, 1f, 0f);
    }

    sealed class Empty : IDistanceField {
        public float Sample(Vector3 position) => 1e6f;

        public Vector3 SampleGradient(Vector3 position) => new(0f, 1f, 0f);
    }

    /// <summary>Constant answers, remembering where the last surface question was asked.</summary>
    sealed class Constant : IRadianceSource {
        public Vector3 SkyValue { get; init; } = SkyAnswer;

        public Vector3 SurfaceValue { get; init; } = SurfaceAnswer;

        public Vector3 Hit { get; private set; }

        public Vector3 Sky(Vector3 direction) => SkyValue;

        public Vector3 Surface(Vector3 position, Vector3 normal, Vector3 direction) {
            Hit = position;

            return SurfaceValue;
        }
    }

    sealed class RecordingFallback : IReflectionFallback {
        public Vector3 Direction { get; private set; }

        public Vector3 Miss(Vector3 position, Vector3 direction, float roughness) {
            Direction = direction;

            return Vector3.Zero;
        }
    }

    sealed class Paint : SurfaceCache.ISurfaceMaterial {
        public Vector3 Albedo(Vector3 position, Vector3 normal) => new(0.5f);

        public Vector3 Emissive(Vector3 position, Vector3 normal) => new(0.3f);
    }
}
