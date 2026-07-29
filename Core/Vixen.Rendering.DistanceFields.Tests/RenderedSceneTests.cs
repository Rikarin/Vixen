// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Rendering.DistanceFields.Tests;

/// <summary>
///     A sphere over a floor, rendered — bake, place, composite, march, and look at the result.
/// </summary>
/// <remarks>
///     <para>
///         Every other test here checks one link against arithmetic. This one runs the whole chain a
///         frame runs and asserts things about the picture: where the silhouette is, which way the
///         shadow falls, and that the floor darkens toward what stands on it. A sign flipped anywhere
///         between the bake and the march passes every closed-form test and fails here.
///     </para>
///     <para>
///         <b>It is the CPU tracer, not the shader.</b> The GPU path shares the field, the clipmap and
///         the conventions — <see cref="SamplingConventionTests" /> is what ties the sampling of the
///         two together — but nothing here executes a shader, and a defect in Raven's own arithmetic
///         would not show up.
///     </para>
/// </remarks>
public class RenderedSceneTests {
    const float SphereRadius = 1f;
    static readonly Vector3 SphereCentre = new(0f, 1.6f, 0f);

    /// <summary>The camera looks down the −Z axis from in front of and above the sphere.</summary>
    static readonly Vector3 Eye = new(0f, 3.5f, 7f);

    [Fact]
    public void TheSphereIsWhereItWasPut() {
        var scene = Scene();

        // Straight at the sphere's centre.
        var hit = DistanceFieldTracer.Trace(scene, Eye, SphereCentre - Eye, Settings);

        Assert.True(hit.Hit, "the ray aimed at the sphere found nothing");

        // The near surface is one radius in front of the centre, along the ray.
        var expected = Vector3.Distance(Eye, SphereCentre) - SphereRadius;

        Assert.Equal(expected, hit.Distance, 0.3f);

        // And the normal there points back at the camera.
        Assert.True(Vector3.Dot(hit.Normal, Vector3.Normalize(Eye - SphereCentre)) > 0.8f);
    }

    [Fact]
    public void APixelBesideTheSphereFindsTheFloorBehindIt() {
        var scene = Scene();
        // Aimed at the floor beside the sphere rather than level with it. A ray that only grazes
        // downward leaves the scene entirely before it descends far enough to land on anything, which
        // is what the first version of this test did.
        var beside = new Vector3(3f, 0f, 0f);
        var hit = DistanceFieldTracer.Trace(scene, Eye, beside - Eye, Settings);

        Assert.True(hit.Hit, "the ray past the sphere found nothing at all");

        // It went past the sphere and landed on the floor, which is below it.
        Assert.True(hit.Position.Y < SphereCentre.Y - SphereRadius, $"it stopped at {hit.Position}");
    }

    /// <summary>
    ///     The whole point of tracing a field for shadows: the floor under the sphere is dark, the
    ///     floor away from it is lit, and the edge between them is soft.
    /// </summary>
    [Fact]
    public void TheSphereCastsASoftShadowOnTheFloor() {
        var scene = Scene();
        var up = new Vector3(0f, 1f, 0f);

        // Softness four rather than eight: a wider penumbra, so the point sampled inside it is a
        // couple of cells clear of the sphere rather than balanced on one.
        float Sun(float x) =>
            DistanceFieldTracer.Shadow(
                scene,
                new(x, 0.05f, 0f),
                up,
                20f,
                4f,
                new DistanceFieldTraceSettings { StartDistance = 0.2f, SurfaceThreshold = 0.05f, MaxDistance = 20f }
            );

        var under = Sun(0f);
        var edge = Sun(1.3f);
        var away = Sun(6f);

        Assert.True(under < 0.05f, $"directly under the sphere was {under}, which is not shadow");
        Assert.True(away > 0.9f, $"six units away was {away}, which is not lit");
        Assert.True(edge > under && edge < away, $"the edge was {edge}, which is not a penumbra");
    }

    /// <summary>
    ///     And the occlusion integral sees the same thing the shadow does, without a light: the floor
    ///     next to something is more enclosed than the floor in the open.
    /// </summary>
    [Fact]
    public void TheFloorDarkensTowardWhatStandsOnIt() {
        var scene = Scene();
        var up = new Vector3(0f, 1f, 0f);

        float Occlusion(float x) =>
            DistanceFieldTracer.AmbientOcclusion(scene, new(x, 0.02f, 0f), up, 2.5f, 6);

        var under = Occlusion(0f);
        // Four rather than six: the floor ends at six, and a point on its edge is genuinely
        // occluded by the drop beside it.
        var away = Occlusion(4f);

        Assert.True(under < away, $"under the sphere was {under} and the open floor {away}");
        Assert.InRange(under, 0f, 1f);

        // Open floor reads a little under one rather than exactly one, and that is the field's
        // resolution rather than a defect. The floor's cells are two-thirds of a unit, so a trilinear
        // read half a cell above a flat surface under-reports the clearance — and an under-reported
        // clearance is, by definition, occlusion. It is the same conservatism that makes the tracer
        // safe, seen from the other end. A finer field converges on one.
        Assert.True(away > 0.75f, $"open floor read {away}, which is more than a resolution artefact");
    }

    /// <summary>
    ///     A whole image rather than a probe, so a defect that is not on any axis has somewhere to
    ///     show. The sphere's silhouette has to be a contiguous blob in the middle, and the floor
    ///     around it has to be found rather than missed.
    /// </summary>
    [Fact]
    public void TheRenderedSilhouetteIsASphereOverAFloor() {
        var scene = Scene();
        const int size = 24;
        var hits = new bool[size, size];
        var sphere = 0;
        var floor = 0;

        for (var y = 0; y < size; y++) {
            for (var x = 0; x < size; x++) {
                var u = ((x + 0.5f) / size * 2f) - 1f;
                var v = 1f - ((y + 0.5f) / size * 2f);

                // A proper basis rather than a hand-written direction. The first version tilted the
                // rays with a constant, so the bottom of the image descended so gently that it left
                // the scene before reaching the floor and the render had no floor in it at all.
                var direction = Forward + (Right * (u * 0.6f)) + (Up * (v * 0.6f));

                var hit = DistanceFieldTracer.Trace(scene, Eye, direction, Settings);
                hits[x, y] = hit.Hit;

                if (!hit.Hit) {
                    continue;
                }

                if (Vector3.Distance(hit.Position, SphereCentre) < SphereRadius * 1.35f) {
                    sphere++;
                } else if (hit.Position.Y < 0.4f) {
                    floor++;
                }
            }
        }

        Assert.True(sphere > 20, $"only {sphere} pixels landed on the sphere");
        Assert.True(floor > 20, $"only {floor} pixels landed on the floor");

        // The middle of the image is the sphere, and it is solid rather than speckled — a field with
        // a step scale too long would let rays through it in a scatter of pixels. A tight block,
        // because the sphere subtends about a quarter of this frame and anything wider is asserting
        // that its silhouette is somewhere it is not.
        for (var y = (size / 2) - 1; y <= (size / 2) + 1; y++) {
            for (var x = (size / 2) - 1; x <= (size / 2) + 1; x++) {
                Assert.True(hits[x, y], $"a hole in the sphere at ({x}, {y})");
            }
        }
    }

    /// <summary>Looking at the sphere from in front of and above it.</summary>
    static Vector3 Forward => Vector3.Normalize(SphereCentre - Eye);

    static Vector3 Right => new(1f, 0f, 0f);

    static Vector3 Up => Vector3.Normalize(Vector3.Cross(Right, Forward));

    static DistanceFieldTraceSettings Settings =>
        new() { MaxDistance = 30f, MaxSteps = 192, SurfaceThreshold = 0.03f };

    /// <summary>The scene, through everything a frame goes through: bake, place, composite.</summary>
    static GlobalDistanceField Scene() {
        var (sphereVertices, sphereIndices) = Shapes.Sphere(SphereRadius, 24, 48);
        var sphere = MeshDistanceFieldBaker.Bake(sphereVertices, sphereIndices, new() { Resolution = 24 });

        // A floor with real thickness, and the reason is worth knowing. Resolution is derived per
        // axis from the LONGEST one, so a 20 x 0.5 x 20 slab asked for 24 gets two samples across its
        // thickness — the two ends of one cell, both outside the slab, and an interior that is never
        // sampled at all. The first version of this test used exactly that and rendered a world with
        // no floor in it. See ThinGeometryTests: it is the documented limit, reached far sooner than
        // "thinner than a voxel" sounds like it would be.
        var (floorVertices, floorIndices) = Shapes.Box(new(6f, 1.5f, 6f));
        var floor = MeshDistanceFieldBaker.Bake(floorVertices, floorIndices, new() { Resolution = 24 });

        var clipmap = new GlobalDistanceField(48, 6f, 2);

        clipmap.Update(
            Vector3.Zero,
            [
                DistanceFieldInstance.At(sphere, SphereCentre),
                DistanceFieldInstance.At(floor, new(0f, -1.5f, 0f))
            ]
        );

        return clipmap;
    }
}
