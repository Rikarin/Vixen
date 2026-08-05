// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Rendering.DistanceFields.Tests;

public class DistanceFieldTracerTests {
    static readonly AnalyticFields.Sphere UnitSphereAtOrigin = new(Vector3.Zero, 1f);

    /// <summary>
    ///     The whole claim of sphere tracing, against a field with no error in it: a ray fired at a
    ///     unit sphere from five units away stops four units later.
    /// </summary>
    [Fact]
    public void AMarchStopsWhereArithmeticSaysItShould() {
        var hit = DistanceFieldTracer.Trace(UnitSphereAtOrigin, new(-5, 0, 0), new(1, 0, 0));

        Assert.True(hit.Hit);
        Assert.Equal(4f, hit.Distance, 0.02f);
        Assert.Equal(-1f, hit.Position.X, 0.02f);
    }

    [Fact]
    public void TheNormalAtAHitPointsOutOfTheSurface() {
        var hit = DistanceFieldTracer.Trace(
            UnitSphereAtOrigin,
            new(-3, 3, 0),
            Vector3.Normalize(new(1, -1, 0))
        );

        Assert.True(hit.Hit);

        // On a sphere the outward normal is the direction from the centre, and the march arrived
        // from up and to the left.
        Assert.True(Vector3.Dot(hit.Normal, Vector3.Normalize(hit.Position)) > 0.99f);
    }

    [Fact]
    public void ARayAimedPastEverythingMisses() {
        var hit = DistanceFieldTracer.Trace(UnitSphereAtOrigin, new(-5, 3, 0), new(1, 0, 0));

        Assert.False(hit.Hit);
    }

    /// <summary>
    ///     A ray that begins inside geometry hits at once and at its own origin, rather than marching
    ///     out of the solid it started in — which would answer a question nobody asked, and
    ///     indistinguishably.
    /// </summary>
    [Fact]
    public void ARayStartingInsideHitsImmediately() {
        var hit = DistanceFieldTracer.Trace(UnitSphereAtOrigin, Vector3.Zero, new(1, 0, 0));

        Assert.True(hit.Hit);
        Assert.Equal(0f, hit.Distance, 0.001f);
        Assert.Equal(1, hit.Steps);
    }

    [Fact]
    public void AMarchStopsAtTheDistanceItWasGiven() {
        var hit = DistanceFieldTracer.Trace(
            UnitSphereAtOrigin,
            new(-5, 0, 0),
            new(1, 0, 0),
            new DistanceFieldTraceSettings { MaxDistance = 2f }
        );

        Assert.False(hit.Hit);
        Assert.Equal(2f, hit.Distance, 0.001f);
    }

    [Fact]
    public void AMarchThroughNothingCostsTheStepsItWasAllowedAndNoMore() {
        var hit = DistanceFieldTracer.Trace(new AnalyticFields.Empty(), Vector3.Zero, new(1, 0, 0));

        Assert.False(hit.Hit);
        Assert.True(hit.Steps <= 128);
    }

    /// <summary>
    ///     The direction is normalised for the caller, because a "distance" measured along a ray of
    ///     length three is three times the distance and nothing says so.
    /// </summary>
    [Fact]
    public void ADirectionThatIsNotAUnitVectorIsStillMeasuredInUnits() {
        var unit = DistanceFieldTracer.Trace(UnitSphereAtOrigin, new(-5, 0, 0), new(1, 0, 0));
        var long3 = DistanceFieldTracer.Trace(UnitSphereAtOrigin, new(-5, 0, 0), new(3, 0, 0));

        Assert.Equal(unit.Distance, long3.Distance, 0.001f);
    }

    /// <summary>
    ///     The regression for a trap that costs nothing to write and everything to find:
    ///     <c>default(T)</c> does not run a struct's parameterless constructor, so an optional
    ///     parameter defaulting to <c>default</c> hands over zeroes rather than the documented
    ///     values — a max distance of zero, a step budget of zero.
    /// </summary>
    [Fact]
    public void OmittingTheSettingsTakesTheDocumentedDefaultsRatherThanZeroes() {
        var omitted = DistanceFieldTracer.Trace(UnitSphereAtOrigin, new(-5, 0, 0), new(1, 0, 0));
        var explicitly = DistanceFieldTracer.Trace(
            UnitSphereAtOrigin,
            new(-5, 0, 0),
            new(1, 0, 0),
            new DistanceFieldTraceSettings()
        );

        Assert.True(omitted.Hit);
        Assert.Equal(explicitly.Distance, omitted.Distance, 0.001f);
    }

    [Fact]
    public void ABakedFieldMarchesToTheSamePlaceAnAnalyticOneDoes() {
        var (vertices, indices) = Shapes.Sphere(1f, 32, 64);
        var clipmap = new GlobalDistanceField(48, 3f, 1);

        clipmap.Update(Vector3.Zero, [DistanceFieldInstance.At(
            MeshDistanceFieldBaker.Bake(vertices, indices, new() { Resolution = 32 }),
            Vector3.Zero
        )]);

        var settings = new DistanceFieldTraceSettings { MaxDistance = 6f, SurfaceThreshold = 0.02f };
        var baked = DistanceFieldTracer.Trace(clipmap, new(-2.5f, 0, 0), new(1, 0, 0), settings);
        var exact = DistanceFieldTracer.Trace(UnitSphereAtOrigin, new(-2.5f, 0, 0), new(1, 0, 0), settings);

        Assert.True(baked.Hit);

        // Within a cell of the exact answer, which is all a sampled field can promise.
        Assert.Equal(exact.Distance, baked.Distance, clipmap.CellSizeOf(0));
    }

    [Fact]
    public void AClearPathIsFullyLit() {
        var light = DistanceFieldTracer.Shadow(UnitSphereAtOrigin, new(0, -5, 0), new(0, -1, 0), 10f);

        Assert.Equal(1f, light, 0.001f);
    }

    [Fact]
    public void AnOccluderOnTheLineToTheLightIsFullyDark() {
        // Straight up through the sphere sitting at the origin.
        var light = DistanceFieldTracer.Shadow(UnitSphereAtOrigin, new(0, -5, 0), new(0, 1, 0), 10f);

        Assert.Equal(0f, light);
    }

    /// <summary>
    ///     The reason a distance field is worth having for shadows at all: one march, and the
    ///     penumbra falls out of how close the ray passed to something.
    /// </summary>
    [Fact]
    public void ARayGrazingAnOccluderLandsInThePenumbra() {
        var light = DistanceFieldTracer.Shadow(UnitSphereAtOrigin, new(1.2f, -5, 0), new(0, 1, 0), 10f);

        Assert.True(light is > 0f and < 1f, $"a grazing ray returned {light}, which is not a penumbra");
    }

    [Fact]
    public void ASharperLightNarrowsThePenumbra() {
        var origin = new Vector3(1.2f, -5, 0);
        var soft = DistanceFieldTracer.Shadow(UnitSphereAtOrigin, origin, new(0, 1, 0), 10f, 4f);
        var sharp = DistanceFieldTracer.Shadow(UnitSphereAtOrigin, origin, new(0, 1, 0), 10f, 32f);

        // Softness is the reciprocal of the light's angular radius, so raising it lets more light
        // through at the same clearance — the penumbra is narrower and this point is outside it.
        Assert.True(sharp > soft, $"sharp {sharp} did not exceed soft {soft}");
    }

    [Fact]
    public void AnOccluderBeyondTheLightDoesNotShadow() {
        // The sphere is ten units up; the light is one unit up. Nothing is between them.
        var light = DistanceFieldTracer.Shadow(
            new AnalyticFields.Sphere(new(0, 10, 0), 1f),
            new(0, -5, 0),
            new(0, 1, 0),
            6f
        );

        Assert.Equal(1f, light, 0.001f);
    }

    /// <summary>
    ///     A flat floor occludes nothing: the field a given height up reads exactly that height, so
    ///     there is no shortfall to accumulate. Anything else means the integral is measuring the
    ///     step rather than the geometry.
    /// </summary>
    [Fact]
    public void OpenGroundIsNotOccluded() {
        var floor = new AnalyticFields.HalfSpace(new(0, 1, 0), 0f);
        var occlusion = DistanceFieldTracer.AmbientOcclusion(floor, Vector3.Zero, new(0, 1, 0));

        Assert.Equal(1f, occlusion, 0.001f);
    }

    [Fact]
    public void OpenSpaceIsNotOccluded() {
        var occlusion = DistanceFieldTracer.AmbientOcclusion(
            new AnalyticFields.Sphere(new(0, 0, 100), 1f),
            Vector3.Zero,
            new(0, 1, 0)
        );

        Assert.Equal(1f, occlusion, 0.001f);
    }

    [Fact]
    public void ACornerIsOccludedAndDarkensTowardIt() {
        // A floor and a wall meeting along x = 0.
        var corner = new AnalyticFields.Union(
            new AnalyticFields.HalfSpace(new(0, 1, 0), 0f),
            new AnalyticFields.HalfSpace(new(1, 0, 0), 0f)
        );

        var tight = DistanceFieldTracer.AmbientOcclusion(corner, new(0.1f, 0, 0), new(0, 1, 0));
        var looser = DistanceFieldTracer.AmbientOcclusion(corner, new(0.5f, 0, 0), new(0, 1, 0));
        var open = DistanceFieldTracer.AmbientOcclusion(corner, new(5f, 0, 0), new(0, 1, 0));

        Assert.True(tight < looser, $"the corner ({tight}) was not darker than a step out ({looser})");
        Assert.True(looser < open, $"a step out ({looser}) was not darker than the open floor ({open})");
        Assert.Equal(1f, open, 0.001f);
    }

    /// <summary>
    ///     The shells stand where the shader puts them: the first at the field's own cell, the rest
    ///     spread evenly out to the radius.
    /// </summary>
    /// <remarks>
    ///     ⚠ The lockstep assertion, and the reason it records positions rather than compares a
    ///     number. <c>DistanceField.Occlusion</c> in the shader library is this arithmetic exactly,
    ///     and the two drifting apart is a frame lit differently from every test that passed — so
    ///     what is pinned here is the sample placement itself, which is the half that changed.
    /// </remarks>
    [Fact]
    public void TheShellsStandWhereTheShaderPutsThem() {
        var recorder = new Recording(new AnalyticFields.HalfSpace(new(0, 1, 0), 0f));

        DistanceFieldTracer.AmbientOcclusion(recorder, Vector3.Zero, new(0, 1, 0), 2f, 5, cell: 0.5f);

        // First at the cell, last at the radius, three evenly between.
        Assert.Equal([0.5f, 0.875f, 1.25f, 1.625f, 2f], recorder.Heights.Select(h => MathF.Round(h, 5)));
    }

    /// <summary>Without a cell the shells are the even spacing they always were.</summary>
    /// <remarks>
    ///     The default has to stay what an analytic field wants — one that resolves everything has
    ///     no cell to anchor to, and inventing one for it would move every existing answer.
    /// </remarks>
    [Fact]
    public void WithoutACellTheShellsAreEvenlySpaced() {
        var recorder = new Recording(new AnalyticFields.HalfSpace(new(0, 1, 0), 0f));

        DistanceFieldTracer.AmbientOcclusion(recorder, Vector3.Zero, new(0, 1, 0), 2f, 5);

        Assert.Equal([0.4f, 0.8f, 1.2f, 1.6f, 2f], recorder.Heights.Select(h => MathF.Round(h, 5)));
    }

    /// <summary>
    ///     A first shell at the cell needs no bias: over open ground the field one cell up reads
    ///     exactly one cell, so the shortfall is zero by construction.
    /// </summary>
    /// <remarks>
    ///     What lets the integral sample the near field at all. A screen-space march standing this
    ///     close to its receiver has to floor the horizon to survive its own quantisation; a signed
    ///     distance field has no such error, and the difference is why only one of the two fixes
    ///     carries a bias term.
    /// </remarks>
    [Fact]
    public void AShellAtTheCellDoesNotOccludeOpenGround() {
        var floor = new AnalyticFields.HalfSpace(new(0, 1, 0), 0f);
        var occlusion = DistanceFieldTracer.AmbientOcclusion(floor, Vector3.Zero, new(0, 1, 0), 2f, 5, cell: 0.5f);

        Assert.Equal(1f, occlusion, 0.001f);
    }

    /// <summary>
    ///     Where the first shell stands decides what the near field contributes: a shell pushed out
    ///     past the geometry reports a ceiling as closed, one held in at the cell does not.
    /// </summary>
    /// <remarks>
    ///     Each shell asks "what fraction of <em>this</em> distance is blocked", so a shell's answer
    ///     depends on where it stands — which is what makes an arbitrary <c>radius / samples</c> a
    ///     defect rather than a taste. Sample 13's clipmap has half-metre cells and its march asked
    ///     for five samples over two metres, so the first shell sat at 0.4 — inside one cell, where
    ///     a trilinear read is the surface's own interpolation and the sample measures nothing.
    /// </remarks>
    [Fact]
    public void TheFirstShellDecidesWhatTheNearFieldContributes() {
        // A ceiling one metre up, and nothing else: clearance at height h is 1 - h.
        var ceiling = new AnalyticFields.HalfSpace(new(0, -1, 0), -1f);

        float Occlusion(float cell) =>
            DistanceFieldTracer.AmbientOcclusion(ceiling, Vector3.Zero, new(0, 1, 0), 2f, 5, cell: cell);

        var near = Occlusion(0.1f);
        var far = Occlusion(0.8f);

        Assert.True(near > far, $"a shell held in at the cell ({near}) was not more open than one pushed out ({far})");
        Assert.InRange(near, 0f, 1f);
        Assert.InRange(far, 0f, 1f);
    }

    /// <summary>A cell wider than the radius collapses every shell onto the radius.</summary>
    /// <remarks>
    ///     The clamp, and it has to hold: a coarse clipmap level's cell can genuinely exceed a
    ///     crease-scale radius, and shells marching backwards from it would sample below the
    ///     surface — which is a field reporting a hit and a floor painted black.
    /// </remarks>
    [Fact]
    public void ACellWiderThanTheRadiusClampsToIt() {
        var recorder = new Recording(new AnalyticFields.HalfSpace(new(0, 1, 0), 0f));

        var occlusion = DistanceFieldTracer.AmbientOcclusion(
            recorder,
            Vector3.Zero,
            new(0, 1, 0),
            1f,
            4,
            cell: 5f
        );

        Assert.All(recorder.Heights, height => Assert.Equal(1f, height, 0.001f));
        Assert.InRange(occlusion, 0f, 1f);
    }

    /// <summary>A field that remembers where it was asked, for the placement assertions above.</summary>
    sealed class Recording(IDistanceField inner) : IDistanceField {
        readonly List<float> heights = [];

        /// <summary>How far up the y axis each sample stood, in order.</summary>
        public IReadOnlyList<float> Heights => heights;

        public float Sample(Vector3 position) {
            heights.Add(position.Y);
            return inner.Sample(position);
        }

        public Vector3 SampleGradient(Vector3 position) => inner.SampleGradient(position);
    }

    [Fact]
    public void OcclusionStaysInsideItsRange() {
        var box = new AnalyticFields.Union(
            new AnalyticFields.HalfSpace(new(0, 1, 0), 0f),
            new AnalyticFields.HalfSpace(new(1, 0, 0), 0f),
            new AnalyticFields.HalfSpace(new(-1, 0, 0), -0.05f),
            new AnalyticFields.HalfSpace(new(0, 0, 1), 0f),
            new AnalyticFields.HalfSpace(new(0, 0, -1), -0.05f)
        );

        // Wedged into a slot narrower than the sampling radius, with the strength wound up past
        // where the integral could ever reach on its own.
        var occlusion = DistanceFieldTracer.AmbientOcclusion(
            box,
            new(0.025f, 0, 0.025f),
            new(0, 1, 0),
            strength: 10f
        );

        Assert.InRange(occlusion, 0f, 1f);
    }

    [Fact]
    public void TracingNothingIsRejected() =>
        Assert.Throws<ArgumentNullException>(
            () => DistanceFieldTracer.Trace(null!, Vector3.Zero, new(1, 0, 0))
        );

    [Theory]
    [InlineData(0f, 128, 0.01f, 0.9f)]
    [InlineData(10f, 0, 0.01f, 0.9f)]
    [InlineData(10f, 128, 0f, 0.9f)]
    [InlineData(10f, 128, 0.01f, 1.5f)]
    public void SettingsThatCannotMarchAreRejected(
        float maxDistance,
        int maxSteps,
        float threshold,
        float stepScale
    ) =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => DistanceFieldTracer.Trace(
                UnitSphereAtOrigin,
                Vector3.Zero,
                new(1, 0, 0),
                new DistanceFieldTraceSettings {
                    MaxDistance = maxDistance,
                    MaxSteps = maxSteps,
                    SurfaceThreshold = threshold,
                    StepScale = stepScale
                }
            )
        );
}
