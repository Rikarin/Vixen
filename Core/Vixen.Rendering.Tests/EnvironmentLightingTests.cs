// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using CsCheck;
using Vixen.Core.Mathematics;
using Vixen.Rendering;
using Vixen.Rendering.Lighting;
using Vixen.Shaders;
using Vixen.Shaders.Generated;
using Xunit;

namespace Tests;

/// <summary>
///     Image-based lighting: the cube convention, the spherical-harmonic projection, and the
///     prefiltered chain.
/// </summary>
/// <remarks>
///     <para>
///         All of it is arithmetic with closed-form answers, which is the reason it is on the CPU. A
///         uniform environment has an irradiance anyone can write down; a cube's texels have solid
///         angles that must sum to the sphere; a mip level has a roughness the shader will ask for by
///         name. None of those needs a device, and none of them is checkable by looking at a frame.
///     </para>
/// </remarks>
public class EnvironmentLightingTests {
    // --- The cube convention ------------------------------------------------

    /// <summary>
    ///     A face's centre looks the way the shadow projection says it does.
    /// </summary>
    /// <remarks>
    ///     The tie between this and the rest of the engine. The directions here are unprojected from
    ///     <see cref="ShadowProjections.Cube" /> rather than tabulated, so a probe and a point light's
    ///     shadow cube cannot end up disagreeing about which way <c>+Y</c> is.
    /// </remarks>
    [Theory]
    [InlineData(CubeFace.PositiveX, 1f, 0f, 0f)]
    [InlineData(CubeFace.NegativeX, -1f, 0f, 0f)]
    [InlineData(CubeFace.PositiveY, 0f, 1f, 0f)]
    [InlineData(CubeFace.NegativeY, 0f, -1f, 0f)]
    [InlineData(CubeFace.PositiveZ, 0f, 0f, 1f)]
    [InlineData(CubeFace.NegativeZ, 0f, 0f, -1f)]
    public void A_faces_centre_looks_along_its_axis(CubeFace face, float x, float y, float z) {
        var direction = CubeMapping.Direction(face, 0f, 0f);

        Assert.True(
            Vector3.Distance(direction, new(x, y, z)) < 1e-5f,
            $"{face}'s centre looks at {direction}, not ({x}, {y}, {z})"
        );
    }

    /// <summary>
    ///     Every texel's solid angle, summed over the cube, is the whole sphere.
    /// </summary>
    /// <remarks>
    ///     The one property that says the weighting is right rather than merely plausible. A cube's
    ///     corner texels subtend about a fifth of what its centre texels do, so an unweighted
    ///     projection is an environment pulled toward its corners — and it looks like an environment,
    ///     just not that one.
    /// </remarks>
    [Theory]
    [InlineData(4)]
    [InlineData(16)]
    [InlineData(64)]
    public void The_texels_solid_angles_sum_to_the_sphere(int size) {
        var image = new CubeImage(size);
        var total = 0f;

        for (var y = 0; y < size; y++) {
            for (var x = 0; x < size; x++) {
                total += image.SolidAngleOf(x, y);
            }
        }

        // Six faces of an identical grid, so one face's sum times six is the sphere.
        Assert.Equal(4f * MathF.PI, total * 6f, 3);
    }

    /// <summary>
    ///     Locating a direction agrees with unprojecting the texel it lands on.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The two halves of the convention are two implementations: <c>Direction</c> unprojects a
    ///         matrix and <c>Locate</c> is the major-axis rule, because a prefilter takes millions of
    ///         samples and cannot afford six matrix multiplies each. This is what stops them drifting.
    ///     </para>
    ///     <para>
    ///         It catches the whole family of orientation mistakes — a mirrored face, a face rotated
    ///         by ninety degrees, a swapped pair — none of which any single-direction test would see,
    ///         and all of which produce an environment that looks like an environment.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Locating_a_direction_and_unprojecting_it_agree() {
        Gen.Select(Gen.Float[-1f, 1f], Gen.Float[-1f, 1f], Gen.Float[-1f, 1f])
            .Where(components => Length(components) > 0.1f)
            .Sample(
                components => {
                    var direction = Vector3.Normalize(
                        new(components.Item1, components.Item2, components.Item3)
                    );

                    var (face, u, v) = CubeMapping.Locate(direction);
                    var round = CubeMapping.Direction(face, u, v);

                    Assert.True(
                        Vector3.Distance(direction, round) < 1e-4f,
                        $"{direction} located to {face} ({u}, {v}), which unprojects to {round}"
                    );
                }
            );
    }

    // --- The projection -----------------------------------------------------

    /// <summary>
    ///     A uniform environment lights every direction equally, and by the right amount.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The closed form the whole projection is checked against: for a uniform environment of
    ///         radiance <c>L</c>, irradiance is <c>πL</c> in every direction. Every band above the
    ///         first has to integrate to zero for that to come out, so a sign error or a bad basis
    ///         function shows up here as a direction-dependent answer rather than as a wrong constant.
    ///     </para>
    ///     <para>
    ///         It is also the test that pins the factor of π to one side. <c>Ibl.Diffuse</c> divides
    ///         by it, so a white surface under a white environment comes back exactly as bright as the
    ///         environment; a projection that folded the π in instead would make everything ambient-lit
    ///         π times too bright, which is the kind of error that gets corrected on the exposure.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_uniform_environment_has_uniform_irradiance() {
        var coefficients = SphericalHarmonics.Project(CubeImage.Uniform(32, new(0.5f, 0.25f, 0.125f)));

        foreach (var normal in Directions()) {
            var irradiance = SphericalHarmonics.Evaluate(coefficients, normal);

            Assert.Equal(MathF.PI * 0.5f, irradiance.X, 2);
            Assert.Equal(MathF.PI * 0.25f, irradiance.Y, 2);
            Assert.Equal(MathF.PI * 0.125f, irradiance.Z, 2);
        }
    }

    /// <summary>
    ///     A white surface under a uniform environment reflects exactly the environment.
    /// </summary>
    /// <remarks>
    ///     The same claim from the other end, and the one a person can check by looking: a Lambertian
    ///     surface reflects <c>albedo/π</c> of the irradiance, so the π introduced above has to come
    ///     back out. This is the arithmetic <c>Ibl.Diffuse</c> does, written here because the shader
    ///     cannot be run in a unit test.
    /// </remarks>
    [Fact]
    public void A_white_surface_under_a_uniform_environment_matches_it() {
        var radiance = new Vector3(0.4f, 0.6f, 0.8f);
        var coefficients = SphericalHarmonics.Project(CubeImage.Uniform(32, radiance));

        var irradiance = SphericalHarmonics.Evaluate(coefficients, Vector3.UnitY);
        var reflected = irradiance * (1f / MathF.PI);

        Assert.Equal(radiance.X, reflected.X, 2);
        Assert.Equal(radiance.Y, reflected.Y, 2);
        Assert.Equal(radiance.Z, reflected.Z, 2);
    }

    /// <summary>
    ///     Light from one side is brightest facing it and dark facing away.
    /// </summary>
    /// <remarks>
    ///     Uniform environments cannot tell a working projection from one that dropped every band but
    ///     the first — which is most of what a projection does. This one is directional, so the linear
    ///     band has to carry it, and it pins the axis assignment: <c>l11</c> takes x, <c>l1m1</c>
    ///     takes y, <c>l10</c> takes z, and any permutation of those rotates the environment.
    /// </remarks>
    [Fact]
    public void A_bright_face_lights_what_faces_it() {
        var image = new CubeImage(32);
        image.Face(CubeFace.PositiveX).Fill(Vector3.One);

        var coefficients = SphericalHarmonics.Project(image);

        var toward = SphericalHarmonics.Evaluate(coefficients, Vector3.UnitX).X;
        var away = SphericalHarmonics.Evaluate(coefficients, -Vector3.UnitX).X;
        var across = SphericalHarmonics.Evaluate(coefficients, Vector3.UnitY).X;

        Assert.True(toward > across, $"facing the light ({toward}) is not brighter than across it ({across})");
        Assert.True(across > away, $"across the light ({across}) is not brighter than away from it ({away})");

        // A surface facing the lit face sees a quarter of the sphere at full radiance, weighted by
        // the cosine — appreciably brighter than the eighth-ish an order-2 fit leaves on the far side.
        Assert.True(toward > 1f, $"facing a full-radiance face gives only {toward}");
    }

    /// <summary>Blending two projections is the projection of the blend.</summary>
    /// <remarks>
    ///     Linearity, which is the property probe interpolation rests on: four probes weighted and
    ///     summed cost nine multiply-adds rather than four evaluations, and only because this holds.
    /// </remarks>
    [Fact]
    public void Projections_add_and_scale_linearly() {
        var first = SphericalHarmonics.Project(CubeImage.Uniform(16, new(1f, 0f, 0f)));
        var second = SphericalHarmonics.Project(CubeImage.Uniform(16, new(0f, 1f, 0f)));
        var both = SphericalHarmonics.Project(CubeImage.Uniform(16, new(0.25f, 0.75f, 0f)));

        var blended = (first * 0.25f) + (second * 0.75f);

        var direct = SphericalHarmonics.Evaluate(both, Vector3.UnitZ);
        var mixed = SphericalHarmonics.Evaluate(blended, Vector3.UnitZ);

        Assert.Equal(direct.X, mixed.X, 4);
        Assert.Equal(direct.Y, mixed.Y, 4);
    }

    // --- The prefiltered chain ----------------------------------------------

    /// <summary>
    ///     A mip's roughness is the inverse of the level the shader will ask for.
    /// </summary>
    /// <remarks>
    ///     The contract between <see cref="EnvironmentBaker.RoughnessOf" /> and
    ///     <c>Ibl.SpecularLod</c>, which picks a level as <c>roughness × (mipCount − 1)</c>. Written
    ///     out here as the round trip, because the two live in different languages and nothing else
    ///     would notice them disagreeing.
    /// </remarks>
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(8)]
    public void A_mips_roughness_round_trips_through_the_shaders_lod(int mipCount) {
        for (var mip = 0; mip < mipCount; mip++) {
            var roughness = EnvironmentBaker.RoughnessOf(mip, mipCount);

            // Ibl.SpecularLod, in C#.
            var lod = Math.Clamp(roughness, 0f, 1f) * Math.Max(mipCount - 1, 0);

            Assert.Equal(mip, lod, 4);
        }
    }

    /// <summary>A uniform environment survives prefiltering unchanged, at every roughness.</summary>
    /// <remarks>
    ///     The integral of a constant against a normalised lobe is the constant, whatever the lobe.
    ///     It is the cheapest possible check and it catches the expensive mistakes: a weight that does
    ///     not sum to one, a sample below the horizon counted anyway, an alpha that grows without
    ///     bound.
    /// </remarks>
    [Fact]
    public void Prefiltering_a_uniform_environment_changes_nothing() {
        var chain = EnvironmentBaker.Prefilter(CubeImage.Uniform(16, new(0.3f, 0.6f, 0.9f)), 4);

        Assert.Equal(4, chain.Length);

        foreach (var level in chain) {
            foreach (var texel in level.Pixels) {
                Assert.Equal(0.3f, texel.X, 3);
                Assert.Equal(0.6f, texel.Y, 3);
                Assert.Equal(0.9f, texel.Z, 3);
            }
        }
    }

    /// <summary>Level zero is the environment itself, and later levels are blurrier.</summary>
    /// <remarks>
    ///     Roughness zero is a mirror, so its level has to be the source rather than an integral over
    ///     it — hundreds of samples of a delta lobe is the same texel with noise on it. The rest is
    ///     what a prefilter is for: measured as the contrast across the sharp edge between a lit face
    ///     and a dark one, which has to fall monotonically as the lobe widens.
    /// </remarks>
    [Fact]
    public void The_chain_starts_sharp_and_gets_blurrier() {
        var source = new CubeImage(32);
        source.Face(CubeFace.PositiveX).Fill(Vector3.One);

        var chain = EnvironmentBaker.Prefilter(source, 4, samples: 128);

        Assert.Equal(Vector3.One, chain[0].At(CubeFace.PositiveX, 16, 16));
        Assert.Equal(Vector3.Zero, chain[0].At(CubeFace.NegativeX, 16, 16));

        var previous = float.MaxValue;

        foreach (var level in chain) {
            var half = level.Size / 2;
            var contrast = level.At(CubeFace.PositiveX, half, half).X - level.At(CubeFace.PositiveZ, half, half).X;

            Assert.True(contrast <= previous + 1e-3f, $"level {level.Size} sharpened rather than blurred");
            previous = contrast;
        }
    }

    // --- Reflection probes --------------------------------------------------

    /// <summary>
    ///     A probe is at full strength inside itself and fades to nothing at its boundary.
    /// </summary>
    /// <remarks>
    ///     Measured inward from the boundary rather than outward from the centre, which is what lets
    ///     a probe covering a corridor be at full strength along all of it. The fade is the whole
    ///     reason probes do not pop as a surface crosses an edge.
    /// </remarks>
    [Fact]
    public void A_probe_fades_out_at_its_boundary() {
        var probe = new ReflectionProbe {
            Bounds = new(new(-10f, -10f, -10f), new(10f, 10f, 10f)),
            CapturePosition = Vector3.Zero,
            BlendDistance = 2f
        };

        Assert.Equal(1f, probe.WeightAt(Vector3.Zero));
        Assert.Equal(1f, probe.WeightAt(new(7f, 0f, 0f)));
        Assert.Equal(0.5f, probe.WeightAt(new(9f, 0f, 0f)), 4);
        Assert.Equal(0f, probe.WeightAt(new(10.5f, 0f, 0f)));
        Assert.False(probe.Contains(new(10.5f, 0f, 0f)));
    }

    /// <summary>A spherical probe fades by distance from its capture point.</summary>
    [Fact]
    public void A_spherical_probe_fades_by_radius() {
        var probe = new ReflectionProbe { CapturePosition = Vector3.Zero, Radius = 10f, BlendDistance = 4f };

        Assert.Equal(1f, probe.WeightAt(new(5f, 0f, 0f)));
        Assert.Equal(0.5f, probe.WeightAt(new(8f, 0f, 0f)), 4);
        Assert.Equal(0f, probe.WeightAt(new(11f, 0f, 0f)));
    }

    /// <summary>
    ///     A small probe inside a large one wins where they overlap.
    /// </summary>
    /// <remarks>
    ///     Probes nest — a cupboard inside a room inside a building — so overlap is the normal case
    ///     rather than a mistake. Resolved by priority and then by volume, both of which an author can
    ///     see, rather than by the order they were registered in, which they cannot.
    /// </remarks>
    [Fact]
    public void The_innermost_probe_wins_where_probes_overlap() {
        var room = new ReflectionProbe {
            Bounds = new(new(-20f, -20f, -20f), new(20f, 20f, 20f)),
            BlendDistance = 0f
        };

        var cupboard = new ReflectionProbe {
            Bounds = new(new(-2f, -2f, -2f), new(2f, 2f, 2f)),
            BlendDistance = 0f
        };

        var selector = new ReflectionProbeSelector();
        selector.Probes.Add(room);
        selector.Probes.Add(cupboard);

        Assert.Same(cupboard, selector.Select(Vector3.Zero)!.Value.Probe);
        Assert.Same(room, selector.Select(new(10f, 0f, 0f))!.Value.Probe);
        Assert.Null(selector.Select(new(100f, 0f, 0f)));
    }

    /// <summary>Priority beats size, for the case where the smaller probe is the wrong one.</summary>
    [Fact]
    public void Priority_beats_size() {
        var big = new ReflectionProbe {
            Bounds = new(new(-20f, -20f, -20f), new(20f, 20f, 20f)),
            BlendDistance = 0f,
            Priority = 1
        };

        var small = new ReflectionProbe {
            Bounds = new(new(-2f, -2f, -2f), new(2f, 2f, 2f)),
            BlendDistance = 0f
        };

        var selector = new ReflectionProbeSelector();
        selector.Probes.Add(small);
        selector.Probes.Add(big);

        Assert.Same(big, selector.Select(Vector3.Zero)!.Value.Probe);
    }

    // --- Reaching the shader ------------------------------------------------

    /// <summary>
    ///     An environment writes itself under the names the shader's own generated keys have.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <see cref="EnvironmentLight.Apply" /> interns its keys from strings, which the rest of
    ///         the engine deliberately does not do — a render feature says
    ///         <c>ForwardPlusKeys.World</c> and never <c>"ForwardPlus.world"</c>. It is by name here
    ///         because one environment feeds several passes and the generated keys exist per shader,
    ///         so the type would otherwise have to name every pass that might read it.
    ///     </para>
    ///     <para>
    ///         The cost of that is a typo landing in a parameter nothing asks for, which is silent:
    ///         the value is written, no layout claims it, and the surface is lit by whatever the
    ///         shader declared as the default. This is what makes it loud instead — same key object,
    ///         because keys are interned by name.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_environment_writes_the_keys_the_shader_generated() {
        var parameters = new ParameterCollection();

        var environment = new EnvironmentLight {
            Irradiance = SphericalHarmonics.Project(CubeImage.Uniform(8, Vector3.One)),
            MipCount = 6,
            Intensity = 2f
        };

        environment.Apply(parameters);

        Assert.Equal(6f, parameters.Get(ForwardPlusKeys.EnvironmentMipCount));
        Assert.Equal(2f, parameters.Get(ForwardPlusKeys.AmbientIntensity));
        Assert.Equal(environment.Irradiance.L00, parameters.Get(ForwardPlusKeys.EnvironmentShL00));
        Assert.Equal(environment.Irradiance.L22, parameters.Get(ForwardPlusKeys.EnvironmentShL22));

        // Every band, not only the two spot-checked above: nine names is nine chances to mistype one,
        // and a band that never arrives is a rotation of the ambient light rather than a missing one.
        Assert.Equal(11, parameters.Count);
    }

    static float Length((float X, float Y, float Z) components) =>
        (components.X * components.X) + (components.Y * components.Y) + (components.Z * components.Z);

    /// <summary>A spread of directions, for the claims that must hold in all of them.</summary>
    static IEnumerable<Vector3> Directions() {
        yield return Vector3.UnitX;
        yield return -Vector3.UnitX;
        yield return Vector3.UnitY;
        yield return -Vector3.UnitY;
        yield return Vector3.UnitZ;
        yield return -Vector3.UnitZ;
        yield return Vector3.Normalize(new(1f, 1f, 1f));
        yield return Vector3.Normalize(new(-1f, 0.5f, 0.25f));
    }
}
