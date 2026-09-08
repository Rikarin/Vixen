// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Materials;
using Vixen.Rendering.PostFx;
using Vixen.Shaders;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>
///     A parallax march, photographed through the real frame against a closed form it has to hit.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>A parallax feature that compiles and shades flat is indistinguishable from one that
///         never ran.</b> The shader binds, the variant lowers, the composition resolves, the pairing
///         writes an index and the surface draws — every one of those is equally true of a march whose
///         sweep came out zero. That is the whole reason this file exists rather than a test that
///         reads the <c>.rvn</c>.
///     </para>
///     <para>
///         <b>The oracle is silhouette-free and view-independent, which is what makes it an oracle
///         rather than an impression.</b> Over a <em>constant</em> height field the march has a closed
///         form: the ray enters at <c>d.uv</c>, leaves at <c>d.uv - sweep</c>, and the field it must
///         cross sits at depth <c>1 - height</c> everywhere — so the answer is
///         <c>uv - sweep · (1 - height)</c>, and <c>sweep</c> is <c>heightScale</c> times a ratio of
///         the tangent-space view vector. Two materials whose <c>heightScale · (1 - height)</c> agree
///         therefore sample the <em>same texel</em> at every pixel of every face, whatever this
///         machine's camera, exposure and sun do — and the frames must be identical. Nothing here
///         needs to know where the camera is.
///     </para>
///     <para>
///         ⚠ <b>No reference PNG, for <c>LayeredMaterialImageTests</c>' reason.</b> Every claim is a
///         differential between two frames rendered on the same device in the same run, so the tone
///         map, the automatic exposure and the sun cancel instead of having to be reproduced.
///     </para>
///     <para>
///         ⚠ <b>And every "these must match" is paired with a "these must differ".</b> Two frames in
///         which the slab never drew agree perfectly, and so do two frames of a feature that
///         displaces nothing — which is precisely the failure being tested for. The controls are what
///         separate "the march is right" from "the march did not run".
///     </para>
/// </remarks>
[Collection("Vulkan")]
public class ParallaxMaterialImageTests {
    /// <summary>How large the maps are, in texels.</summary>
    const int Side = 32;

    /// <summary>How many checker cells the base-colour map carries across the face.</summary>
    /// <remarks>
    ///     ⚠ <b>Coarse deliberately.</b> The claim is that a displacement moved the coordinate by a
    ///     known amount, and a fine checker turns that into an aliasing pattern whose difference is
    ///     dominated by which side of a texel each sample landed on. Four cells over the face is eight
    ///     texels a cell, so <see cref="Depth" /> moves the pattern by most of a cell and the two
    ///     frames differ in whole regions rather than along edges.
    /// </remarks>
    const int Cells = 4;

    /// <summary>The height field's depth, in the surface's own UV units.</summary>
    /// <remarks>
    ///     Large for a real material and right for an oracle: the claim is about where a coordinate
    ///     landed, and a displacement smaller than a texel is a claim the tolerance would absorb.
    /// </remarks>
    const float Depth = 0.15f;

    /// <summary>The mid-grey a height map's byte is, as the sampler decodes it.</summary>
    /// <remarks>
    ///     ⚠ <b>128 and not 0.5, and the difference is the whole of the equivalence below.</b> An
    ///     <c>Rgba8UNorm</c> texel decodes to <c>v / 255</c> exactly, so the field this fixture uploads
    ///     sits at depth <c>1 - 128/255</c> and not at a half. A test that scaled by 2 on the belief
    ///     that it was would be comparing two displacements that differ by half a per cent — inside
    ///     the tolerance, and therefore a test that passes without its arithmetic being right.
    /// </remarks>
    const float MidHeight = 128f / 255f;

    /// <summary>The frame all of these renderings share. Deliberately the tier suite's own.</summary>
    static StandardFrameAsset Frame => new() {
        Name = "ParallaxMaterial",
        Shadows = ShadowMode.Cascades,
        Gi = GiMode.Off,
        Reflections = ReflectionsMode.Off,
        Antialiasing = AntialiasingMode.Fxaa,
        Exposure = ExposureMode.Automatic,
        Particles = false
    };

    /// <summary>
    ///     The march moves the coordinate by <c>heightScale · (1 - height)</c>, and by nothing else.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Four frames, and the shape of the argument is the point.</b> A field at height 1 is
    ///         the surface itself, so the march must resolve at depth zero and the picture must be the
    ///         same material with no parallax feature at all — the identity, which says the feature is
    ///         composed, ordered and sampling without changing anything it should not. A field at
    ///         height 0 is the floor, so the march must resolve at full depth and the picture must
    ///         differ — which says the feature runs.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Then the claim, which neither of those makes: the same displacement reached two
    ///         ways.</b> Depth zero at scale <c>s</c> and mid-height at scale <c>s / (1 - h)</c> are
    ///         the same number of UV units, so they are the same picture — while mid-height at scale
    ///         <c>s</c> is about half of it and must not be. A march that ignored the height map
    ///         entirely passes the first pair and fails this one; a march that ignored
    ///         <c>heightScale</c> fails it the other way.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_march_moves_the_coordinate_by_the_scale_times_the_fields_depth() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using (fixture) {
            var plain = Render(fixture!, Plain);
            var flat = Render(fixture!, scene => Slab(scene, height: 255, Depth));

            var identity = GoldenImage.Compare(plain, flat, Tolerance.Shaded);

            Assert.True(
                identity.Matches,
                $"A height field at 1 everywhere is the surface itself, so the march has to resolve at "
                + $"depth zero and draw what the same material draws with no parallax feature — and on "
                + $"{Adapter(fixture!)} it did not: {identity.DifferingPixels} of {identity.TotalPixels} "
                + $"pixels differ, worst channel {identity.WorstChannel} at {identity.WorstAt}, mean "
                + $"{identity.MeanChannel:F3}."
            );

            var full = Render(fixture!, scene => Slab(scene, height: 0, Depth));

            Assert.False(
                GoldenImage.Compare(plain, full, Tolerance.Shaded).Matches,
                $"A height field at 0 everywhere displaced the coordinate by {Depth} UV units and drew "
                + $"the undisplaced surface on {Adapter(fixture!)}, so this scene is not a picture of "
                + "the march and every comparison below it is satisfied by a feature that does nothing."
            );

            // The claim. Zero at `s` and mid-height at `s / (1 - h)` are the same UV displacement at
            // every pixel of every face, because the field is constant and the closed form is linear
            // in both. The tangent-space view vector cancels out of the comparison entirely.
            var scaled = Render(fixture!, scene => Slab(scene, height: 128, Depth / (1f - MidHeight)));
            var same = GoldenImage.Compare(full, scaled, Tolerance.Shaded);

            Assert.True(
                same.Matches,
                $"Depth 1 at scale {Depth} and depth {1f - MidHeight:F4} at scale "
                + $"{Depth / (1f - MidHeight):F4} are the same displacement and drew different frames "
                + $"on {Adapter(fixture!)}: {same.DifferingPixels} of {same.TotalPixels} pixels differ, "
                + $"worst channel {same.WorstChannel} at {same.WorstAt}, mean {same.MeanChannel:F3}."
            );

            // ⚠ And the half that says the equivalence above is not two frames of one displacement
            // reached by ignoring both knobs: the same field at the *unscaled* depth is roughly half
            // as far and has to be a different picture.
            var half = Render(fixture!, scene => Slab(scene, height: 128, Depth));

            Assert.False(
                GoldenImage.Compare(full, half, Tolerance.Shaded).Matches,
                $"A field at depth {1f - MidHeight:F4} drew the same frame as one at depth 1 on "
                + $"{Adapter(fixture!)}, so the height map is not what the march is reading and the "
                + "equivalence above holds for a reason that is not the arithmetic."
            );
        }
    }

    /// <summary>
    ///     ⚠ The occlusion refinement, which is what makes a flat field exact at any step count.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Steep parallax alone quantises the answer to a step boundary.</b> Walk a flat field
    ///         at depth <c>D</c> in <c>n</c> equal steps and the first sample below the ray is at
    ///         <c>ceil(nD) / n</c>, which is <c>D</c> only when <c>nD</c> happens to be whole. The
    ///         refinement intersects the two straight lines the last two samples define instead, and
    ///         for a flat field that is exactly <c>D</c> — <em>whatever</em> <c>n</c> was. So one step
    ///         and sixty-four have to draw the same picture.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The control is the exact signature of a march with no refinement.</b> At one step
    ///         the walk's only sample is at depth 1, every field is below it, and an unrefined march
    ///         therefore answers "full sweep" for <em>every</em> height map — so a flat field at
    ///         mid-height and one at the floor would draw the same frame. That they must differ is the
    ///         half of this that can be false, and it is false for precisely the defect being
    ///         excluded.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_refinement_resolves_a_flat_field_the_same_way_at_one_step_and_at_sixty_four() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using (fixture) {
            var coarse = Render(fixture!, scene => Slab(scene, height: 128, Depth, steps: 1));
            var fine = Render(fixture!, scene => Slab(scene, height: 128, Depth, steps: 64));

            var exact = GoldenImage.Compare(coarse, fine, Tolerance.Shaded);

            Assert.True(
                exact.Matches,
                $"A flat height field resolved differently at one step and at sixty-four on "
                + $"{Adapter(fixture!)}, so the crossing is being snapped to a step boundary rather "
                + $"than interpolated between the two samples that straddle it: {exact.DifferingPixels} "
                + $"of {exact.TotalPixels} pixels differ, worst channel {exact.WorstChannel} at "
                + $"{exact.WorstAt}, mean {exact.MeanChannel:F3}."
            );

            var floor = Render(fixture!, scene => Slab(scene, height: 0, Depth, steps: 1));

            Assert.False(
                GoldenImage.Compare(coarse, floor, Tolerance.Shaded).Matches,
                $"At one step a field at mid-height and a field at the floor drew the same frame on "
                + $"{Adapter(fixture!)} — which is what a march with no refinement does, because its "
                + "only sample sits at depth 1 and every field is below it. The comparison above then "
                + "holds for a march that always answers with the full sweep."
            );
        }
    }

    /// <summary>The parallaxed material: a checker to displace, and a flat field to displace it by.</summary>
    /// <param name="scene">The scene that owns the uploads.</param>
    /// <param name="height">The height map's byte, uniform across the map.</param>
    /// <param name="scale">How deep the field's floor is, in UV units.</param>
    /// <param name="steps">The step count, head-on and edge-on alike, so the walk is not a variable.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The parallax feature is first in the list and the compiler enforces it.</b> Behind
    ///         the base surface it would displace the coordinate that surface has already sampled at,
    ///         which is <see cref="MaterialDiagnosticId.CoordinateFeatureOutOfOrder" /> — so
    ///         <see cref="Compiled" /> failing loudly is part of what this fixture asserts.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both maps are uploaded through a linear view.</b> A height is a distance and a
    ///         weight rather than a colour; and the checker is compared only against itself here, so
    ///         an sRGB view would put a transfer function into both halves of every difference for
    ///         nothing. What matters is that the two frames of a pair are read identically.
    ///     </para>
    ///     <para>
    ///         The step count is pinned at both ends rather than left at its defaults, because the
    ///         shipping default interpolates between them by the view angle — which is right for a
    ///         material and would make "how many steps this pixel took" a variable of the camera in a
    ///         file whose whole argument is that the camera cancels.
    ///     </para>
    /// </remarks>
    static Material Slab(TierScene scene, byte height, float scale, int steps = 32) {
        var parallax = new ParallaxOcclusionFeature {
            HeightScale = scale,
            MinSteps = steps,
            MaxSteps = steps
        };

        var textured = new TexturedMetalRoughnessFeature { Metalness = 0f, Roughness = 0.5f };

        var material = Compiled(
            new() { ShaderName = "ForwardPlus", Features = [parallax, textured] }
        );

        // The pairing the renderer completes: a feature names its map, the host puts the view under
        // that name, and `MaterialRenderFeature` turns it into the slot the shader indexes. ⚠ The two
        // names are deliberately different words — `parallaxHeightMap` is not the layered feature's
        // `heightMap` — and without both of these the index stays zero and the march reads the
        // fallback checker's red as a height field.
        material.Parameters.Set(
            ParameterKeys.New<TextureViewHandle>(parallax.HeightMap),
            scene.Map($"Height.{height}", Side, Flat(height), PixelFormat.Rgba8UNorm)
        );

        material.Parameters.Set(
            ParameterKeys.New<TextureViewHandle>(textured.BaseColorMap),
            scene.Map("Checker", Side, Checker(), PixelFormat.Rgba8UNorm)
        );

        return material;
    }

    /// <summary>The same checker with no parallax feature at all — the undisplaced surface.</summary>
    static Material Plain(TierScene scene) {
        var textured = new TexturedMetalRoughnessFeature { Metalness = 0f, Roughness = 0.5f };
        var material = Compiled(new() { ShaderName = "ForwardPlus", Features = [textured] });

        material.Parameters.Set(
            ParameterKeys.New<TextureViewHandle>(textured.BaseColorMap),
            scene.Map("Checker", Side, Checker(), PixelFormat.Rgba8UNorm)
        );

        return material;
    }

    /// <summary>A two-colour checker, so a coordinate that moved shows as a region that changed.</summary>
    /// <returns>The texels, RGBA, row-major.</returns>
    /// <remarks>
    ///     ⚠ <b>Two saturated colours rather than black and white.</b> A black cell is a surface with
    ///     no albedo, and a frame's automatic exposure reads a half-black slab differently from a
    ///     half-dark-red one — which would put the exposure into a comparison that is supposed to be
    ///     about a coordinate. Both cells reflect a similar amount of light in different channels.
    /// </remarks>
    static byte[] Checker() {
        var texels = new byte[Side * Side * 4];
        var cell = Side / Cells;

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                var texel = ((y * Side) + x) * 4;
                var dark = ((x / cell) + (y / cell)) % 2 == 0;

                texels[texel] = dark ? (byte)210 : (byte)40;
                texels[texel + 1] = dark ? (byte)50 : (byte)190;
                texels[texel + 2] = dark ? (byte)45 : (byte)60;
                texels[texel + 3] = 255;
            }
        }

        return texels;
    }

    /// <summary>A height map that is one value everywhere.</summary>
    /// <param name="height">The value, in red.</param>
    /// <returns>The texels, RGBA, row-major.</returns>
    /// <remarks>
    ///     ⚠ <b>Red, and the other channels are left at zero on purpose.</b> The march reads
    ///     <c>.r</c> because a height bake writes one channel — a single-channel texture samples green
    ///     and blue as 0 and alpha as 1 — so a fixture that filled all four would be unable to tell a
    ///     shader reading red from one reading alpha.
    /// </remarks>
    static byte[] Flat(byte height) {
        var texels = new byte[Side * Side * 4];

        for (var texel = 0; texel < Side * Side; texel++) {
            texels[texel * 4] = height;
        }

        return texels;
    }

    static Material Compiled(MaterialDescriptor descriptor) {
        var compilation = MaterialCompiler.Compile(descriptor);

        Assert.False(
            compilation.Failed,
            string.Join(Environment.NewLine, compilation.Diagnostics.Select(diagnostic => diagnostic.ToString()))
        );

        return compilation.Material!;
    }

    /// <summary>Stages one slab over a floor, renders the frame, and hands back the picture.</summary>
    /// <param name="fixture">The device.</param>
    /// <param name="slab">
    ///     What the slab is made of, given the scene — a textured material's maps are the scene's to
    ///     own and to upload, so the material cannot be built before there is one.
    /// </param>
    /// <remarks>
    ///     <c>LayeredMaterialImageTests.Render</c>'s scene, unchanged and deliberately: the slab is a
    ///     box, so the camera sees three of its faces at three different angles at once — and a
    ///     displacement along the tangent-space view ray is a different sweep on each of them. One
    ///     frame is therefore three tests of the closed form rather than one.
    /// </remarks>
    static Bitmap Render(Fixture fixture, Func<TierScene, Material> slab) {
        var effects = new EffectSystem();

        effects.AddProvider(new Compiling(new(fixture.Device)));

        using var scene = TierScene.Open(fixture, effects, new() { Game = Frame }, QualityTier.High);

        var casters = scene.Stages.TryGetValue("Shadow", out var shadow) ? shadow.Mask : default;
        var opaque = scene.Stages["Opaque"].Mask;

        // A grey floor, so the slab is the only thing in the frame whose surface is the variable.
        scene.Box(new(0.4f, -0.25f, -0.6f), new(9f, 0.25f, 9f), Library(new(0.35f, 0.35f, 0.35f)), opaque);
        scene.Box(new(0.2f, 0.9f, -0.4f), new(1.5f, 0.9f, 1.1f), slab(scene), opaque | casters);

        scene.Commit(opaque);

        // Several frames, because the automatic exposure is a filter over its own history and a
        // single frame is whatever it started at.
        return scene.Frames(8);
    }

    /// <summary>The floor, spelled with the library's own untextured feature.</summary>
    static Material Library(Vector3 colour) =>
        Compiled(
            new() {
                ShaderName = "ForwardPlus",
                Features = [new MetalRoughnessFeature { BaseColor = colour, Metalness = 0f, Roughness = 0.5f }]
            }
        );

    /// <summary>What ran, said in every message here so that no number is anonymous.</summary>
    static string Adapter(Fixture fixture) =>
        $"{fixture.Device.Adapter.Name} ({fixture.Device.Adapter.Kind}, {fixture.Device.Adapter.DriverVersion})";

    /// <summary>A device, or a loud skip — and a second loud skip for the capability this needs.</summary>
    /// <remarks>
    ///     ⚠ <b>Bindless is checked and skipped on, not assumed</b>, on
    ///     <c>LayeredMaterialImageTests.TryOpen</c>'s terms: both maps here are <c>uint</c> slots into
    ///     the frame's table, and without <c>HasBindless</c> there is no table. ADR-011 calls that a
    ///     supported configuration rather than a degraded one, so <c>VIXEN_REQUIRE_VULKAN</c> does not
    ///     turn it into a failure — the capability is genuinely absent rather than a run that failed to
    ///     find a device.
    /// </remarks>
    static bool TryOpen(out Fixture? fixture) {
        if (!Fixture.TryOpen(out fixture, out var reason)) {
            if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
                Assert.Fail($"VIXEN_REQUIRE_VULKAN is set and no device could be opened: {reason}");
            }

            Assert.Skip(reason ?? "no Vulkan");

            return false;
        }

        if (BindlessTable.IsSupportedBy(fixture!.Device)) {
            return true;
        }

        var without = Adapter(fixture);

        fixture.Dispose();
        fixture = null;

        Assert.Skip(
            $"{without} offers no bindless descriptor indexing (ADR-011), so no material on it can "
            + "sample a height map and there is nothing here to photograph."
        );

        return false;
    }
}
