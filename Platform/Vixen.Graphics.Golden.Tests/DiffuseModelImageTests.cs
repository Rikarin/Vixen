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
///     The two diffuse models a material may name, on a rough dielectric, in pixels.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b><see href="https://github.com/Rikarin/Vixen/issues/1193">#1193</see>'s picture, which
///         was the half that was owed rather than the wiring.</b> <c>OrenNayarShading</c> and
///         <c>BurleyShading</c> became models a material can name by writing two shaders and two
///         entries in <c>MaterialShading.All</c>; what stayed owed is that <c>LibraryTreeTests</c> can
///         only say the emitted unit <em>differs</em> from the standard model's, and a unit that
///         differs is not a surface that looks different. Nothing in the tree rendered two shading
///         models and compared them.
///     </para>
///     <para>
///         <b>The oracle is a differential over one scene, not a committed PNG.</b> The same box, the
///         same albedo, the same roughness, the same sun, rendered three times with the shading slot
///         as the only variable — so nothing below depends on this machine's tone map, its exposure or
///         its sun, all of which do the same thing to all three.
///     </para>
///     <para>
///         ⚠ <b>The floor is the instrument, and it is read before anything is claimed.</b> The frame
///         is metered, so a model that changed the slab's brightness could move the exposure and with
///         it every pixel in the picture — which reads as "the model reached the frame" while saying
///         nothing about the surface. The floor is a <c>StandardShading</c> grey in all three renders,
///         so it has to come back unmoved; that it does is what makes the slab's numbers the slab's.
///     </para>
///     <para>
///         ⚠ <b>And "the picture changed" is the claim this file most has to avoid making alone.</b> A
///         diffuse model that returned a constant fraction of Lambert would change it, and would be a
///         darker albedo wearing a model's name. ⚠ <b>The obvious separator does not work</b>: the
///         deficit differing between three differently-angled faces looks like the redistribution a
///         multiplier cannot do, and it is not — the faces carry different amounts of specular and
///         ambient, so a flat <c>Lambert × 0.75</c> in the slot reads 0.966 / 0.978 / 0.993 where the
///         model itself reads 0.941 / 0.971 / 0.985, and no threshold over that spread tells them
///         apart. That sabotage was written, run on the device and left this file green, which is why
///         the claim is made by <see cref="A_smooth_surface_is_Lambert_whichever_diffuse_model_it_names" />
///         instead: both models are functions of the roughness, so on a smooth surface they have to
///         vanish, and a multiplier cannot vanish.
///     </para>
///     <para>
///         <b>Measured on an M1 Max through MoltenVK</b>, where a second render of the same scene came
///         back byte for byte identical — so the floor is zero and the margins below are about half
///         the measured effect: at roughness 0.9 Oren-Nayar takes 5.9% off the lit face turned toward
///         the camera, 2.9% off the top and 1.5% off the one the sun does not reach, Burley puts 1.0%
///         back on the top and almost nothing on the unlit one, and at roughness 0.15 all three
///         models agree to within a part in a thousand.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public class DiffuseModelImageTests {
    /// <summary>A dielectric, because both models differ from Lambert only on one.</summary>
    const float Metalness = 0f;

    /// <summary>Rough, because both models' extra terms are scaled by the roughness.</summary>
    /// <remarks>
    ///     ⚠ Oren-Nayar's <c>A</c> and <c>B</c> are functions of σ² alone and Burley's <c>f90</c> is
    ///     <c>0.5 + 2α·cos²θ_d</c>, so at the library's default roughness a fixture would be
    ///     asserting almost nothing.
    /// </remarks>
    const float Rough = 0.9f;

    /// <summary>And nearly smooth, which is the other half of the claim rather than a control.</summary>
    /// <remarks>
    ///     ⚠ <b>This is what tells a diffuse model from a darker albedo, and the spread across faces
    ///     is not.</b> Three faces of one box differ in how much specular and ambient they carry, so
    ///     <em>any</em> change to the diffuse term — including a flat multiplier — comes back as a
    ///     different ratio on each: sabotaging <c>OrenNayarShading</c> into
    ///     <c>Lambert × 0.75</c> reads 0.966 / 0.978 / 0.993 against the model's own
    ///     0.941 / 0.971 / 0.985, and no threshold over that spread separates them.
    ///     <para>
    ///         At σ² ≈ 0.0005 Oren-Nayar's <c>A</c> is 0.9992 and its <c>B</c> is 0.0025, so a smooth
    ///         surface <em>is</em> Lambert to a part in a thousand — while a multiplier is still a
    ///         multiplier. That is the assertion the sabotage above turns red.
    ///     </para>
    /// </remarks>
    const float Smooth = 0.15f;

    /// <summary>Unglazed clay, which is what Oren-Nayar is for.</summary>
    static readonly Vector3 Clay = new(0.72f, 0.55f, 0.42f);

    /// <summary>What the floor is made of, in all three renders.</summary>
    static readonly Vector3 Grey = new(0.35f, 0.35f, 0.35f);

    /// <summary>How far apart two renders of one scene may be before the margins mean nothing.</summary>
    /// <remarks>
    ///     ⚠ <b>Zero on the machine this was written on, and written as two rather than zero on
    ///     purpose.</b> A driver is not obliged to be deterministic, and a fixture asserting bitwise
    ///     equality would be asserting something about MoltenVK. Two codes is far below the smallest
    ///     effect claimed here, which is a 1% move in a mean over 256 pixels.
    /// </remarks>
    const int Noise = 2;

    /// <summary>The frame all three renders share.</summary>
    /// <remarks>
    ///     ⚠ Metered rather than fixed, and not as a preference: the scene is lit at 12 000 lux, and a
    ///     fixed exposure at <c>TierScene.Camera</c>'s authored EV comes back as a white frame in
    ///     which every material looks the same — measured, not assumed. The cost is that the exposure
    ///     is then shared between the three renders, which is what the floor patch exists to bound.
    /// </remarks>
    static StandardFrameAsset Frame => new() {
        Name = "DiffuseModel",
        Shadows = ShadowMode.Cascades,
        Gi = GiMode.Off,
        Reflections = ReflectionsMode.Off,
        Antialiasing = AntialiasingMode.Fxaa,
        Exposure = ExposureMode.Automatic,
        Particles = false
    };

    /// <summary>One face of the slab, or a patch of the floor, in the rendered frame.</summary>
    /// <param name="Name">What it is, for the failure message.</param>
    /// <param name="X0">Its left edge.</param>
    /// <param name="Y0">Its top edge.</param>
    /// <param name="X1">One past its right edge.</param>
    /// <param name="Y1">One past its bottom edge.</param>
    /// <remarks>
    ///     Well inside each face, because FXAA's blend sits on a luminance comparison and a shading
    ///     change moves it — an edge pixel would differ between two models for a reason that is the
    ///     antialiasing rather than the BRDF.
    /// </remarks>
    readonly record struct Patch(string Name, int X0, int Y0, int X1, int Y1);

    /// <summary>The lit face turned toward the camera, the top, and the one the sun does not reach.</summary>
    /// <remarks>
    ///     Three rather than one because the models' terms are functions of the geometry, so one face
    ///     reports one point of a curve: the sun arrives from <c>(-0.67, 0.59, -0.45)</c> and the
    ///     camera sits toward <c>(-0.55, 0.31, 0.77)</c>, which puts the light and the view on
    ///     opposite sides of the left face's normal and on the same side of the top's — the azimuth
    ///     term <c>LdotV − NdotL·NdotV</c> that Oren-Nayar has and Lambert has not.
    ///     <para>
    ///         ⚠ <b>What three faces are not is a way of telling a model from a multiplier</b>, which
    ///         is what they were first written to be. See the remarks on <see cref="Smooth" />.
    ///     </para>
    /// </remarks>
    static Patch[] Faces => [new("left", 32, 58, 52, 76), new("top", 44, 44, 76, 52), new("unlit", 72, 58, 92, 76)];

    /// <summary>A patch of floor the slab neither covers nor shadows.</summary>
    static Patch Floor => new("floor", 10, 100, 40, 120);

    /// <summary>
    ///     A material that names a diffuse model draws a surface the standard model does not.
    /// </summary>
    /// <remarks>
    ///     The claim <see href="https://github.com/Rikarin/Vixen/issues/1193">#1193</see> asked for and
    ///     the one <c>MaterialShading.All</c>'s two entries can silently lose: the name a material
    ///     writes reaches the pass, and the pass shades with it. ⚠ On its own this is satisfied by
    ///     anything in the slot that is not Lambert — see
    ///     <see cref="A_smooth_surface_is_Lambert_whichever_diffuse_model_it_names" />, which is the
    ///     half that says it is <em>these</em> models.
    /// </remarks>
    [Fact]
    public void A_named_diffuse_model_changes_the_surface_a_material_draws() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using (fixture) {
            var standard = Render(fixture!, new StandardShading(), Rough);
            var oren = Render(fixture!, new OrenNayarShading(), Rough);
            var burley = Render(fixture!, new BurleyShading(), Rough);

            // The floor first: the meter did not move, so what follows is about the slab.
            foreach (var (name, picture) in ((string Name, Bitmap Picture)[])[
                ("Oren-Nayar", oren),
                ("Burley", burley)
            ]) {
                var moved = Ratio(standard, picture, Floor);

                Assert.True(
                    Math.Abs(moved - 1d) < 0.005,
                    $"{name} moved the floor by {(moved - 1d) * 100:F2}%, and the floor is a "
                    + "StandardShading grey in both frames — so the exposure moved, and every number "
                    + "below is a picture of the meter rather than of the material."
                );
            }

            // Oren-Nayar takes light out of every one of these directions, on a rough surface.
            var deficits = Faces.Select(face => (face.Name, Ratio(standard, oren, face))).ToArray();
            var darker = string.Join(", ", deficits.Select(pair => $"{pair.Name} {pair.Item2:F4}"));

            foreach (var (name, ratio) in deficits) {
                Assert.True(
                    ratio < 0.995,
                    $"Oren-Nayar left the {name} face at {ratio:F4} of Lambert's, so the shading slot "
                    + $"was filled by something that shades like Lambert. Faces: {darker}."
                );
            }

            // Burley puts some back, which is the retroreflection it is for.
            var gains = Faces.Select(face => (face.Name, Ratio(standard, burley, face))).ToArray();
            var brighter = string.Join(", ", gains.Select(pair => $"{pair.Name} {pair.Item2:F4}"));

            foreach (var (name, ratio) in gains) {
                Assert.True(
                    ratio > 0.998,
                    $"Burley darkened the {name} face to {ratio:F4} of Lambert's. Its two scatter "
                    + $"terms are at least one wherever f90 is, which on a surface this rough it is. "
                    + $"Faces: {brighter}."
                );
            }

            Assert.True(
                gains.Max(pair => pair.Item2) > 1.005,
                $"Burley put nothing back on any face, and retroreflection is what it is for. "
                + $"Faces: {brighter}."
            );

            // And the two models are not each other, which nothing above says.
            Assert.True(
                Math.Abs(Ratio(oren, burley, Faces[0]) - 1d) > 0.02,
                "Oren-Nayar and Burley drew the same left face, so the name a material writes chose "
                + "nothing."
            );
        }
    }

    /// <summary>
    ///     And on a smooth surface the same model is Lambert, which is what a multiplier is not.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The assertion that carries the claim, and the one a plausible wrong implementation
    ///     fails.</b> Everything in the fixture above is satisfied by a shading model that returned a
    ///     constant fraction of Lambert — a darker albedo wearing a model's name — because the three
    ///     faces carry different amounts of specular and ambient and so answer a flat multiplier with
    ///     three different ratios. Oren-Nayar's <c>A</c> and <c>B</c> are functions of the roughness
    ///     alone, so at <see cref="Smooth" /> the model has to <em>disappear</em>, and a multiplier
    ///     cannot.
    /// </remarks>
    [Fact]
    public void A_smooth_surface_is_Lambert_whichever_diffuse_model_it_names() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using (fixture) {
            var standard = Render(fixture!, new StandardShading(), Smooth);
            var oren = Render(fixture!, new OrenNayarShading(), Smooth);

            var ratios = Faces.Select(face => (face.Name, Ratio(standard, oren, face))).ToArray();
            var report = string.Join(", ", ratios.Select(pair => $"{pair.Name} {pair.Item2:F4}"));

            foreach (var (name, ratio) in ratios) {
                Assert.True(
                    Math.Abs(ratio - 1d) < 0.005,
                    $"On a nearly smooth surface Oren-Nayar drew the {name} face at {ratio:F4} of "
                    + $"Lambert's, where its own A is 0.9992 and its B is 0.0025 — so what is in the "
                    + $"shading slot is not Oren-Nayar but something that changes the diffuse term "
                    + $"whatever the roughness. Faces: {report}."
                );
            }
        }
    }

    /// <summary>
    ///     The noise floor those margins are set against: one scene rendered twice is one picture.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The instrument, and what says a failure above is about a BRDF.</b> Every claim in this
    ///     file is a percentage of a region mean, and a percentage is evidence only while two renders
    ///     of the same thing agree. Measured at exactly zero here; asserted at <see cref="Noise" />
    ///     so that a driver which is legitimately not bit-deterministic reports its own noise rather
    ///     than this fixture's opinion of it.
    /// </remarks>
    [Fact]
    public void One_scene_rendered_twice_is_one_picture() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using (fixture) {
            var first = Render(fixture!, new StandardShading(), Rough);
            var second = Render(fixture!, new StandardShading(), Rough);

            var comparison = GoldenImage.Compare(first, second, new(Noise, 0d));

            Assert.True(
                comparison.Matches,
                $"Two renders of one scene differ: {comparison.DifferingPixels} pixels over "
                + $"{Noise}/255, worst {comparison.WorstChannel} at {comparison.WorstAt}. Every margin "
                + "in this file is a fraction of a region mean, and none of them means anything until "
                + "this passes."
            );
        }
    }

    /// <summary>How bright one patch is in the second picture, as a fraction of the first.</summary>
    static double Ratio(in Bitmap first, in Bitmap second, in Patch patch) =>
        Mean(second, patch) / Mean(first, patch);

    /// <summary>The mean of a patch's colour channels, alpha excluded.</summary>
    static double Mean(in Bitmap image, in Patch patch) {
        var total = 0L;
        var count = 0;

        for (var y = patch.Y0; y < patch.Y1; y++) {
            for (var x = patch.X0; x < patch.X1; x++) {
                var offset = image.Offset(x, y);

                total += image.Pixels[offset] + image.Pixels[offset + 1] + image.Pixels[offset + 2];
                count += 3;
            }
        }

        return (double)total / count;
    }

    /// <summary>Stages the slab over a floor and renders the frame.</summary>
    /// <param name="fixture">The device.</param>
    /// <param name="shading">The model the slab's material names.</param>
    /// <param name="roughness">How rough the slab is.</param>
    static Bitmap Render(Fixture fixture, IMaterialShading shading, float roughness) {
        var effects = new EffectSystem();

        effects.AddProvider(new Compiling(new(fixture.Device), _ => RavenEffects.Everything()));

        using var scene = TierScene.Open(fixture, effects, new() { Game = Frame }, QualityTier.High);

        var casters = scene.Stages.TryGetValue("Shadow", out var shadow) ? shadow.Mask : default;
        var opaque = scene.Stages["Opaque"].Mask;

        // ⚠ The floor is the standard model in every render, deliberately: it is what says the
        // exposure did not move, and a floor that changed with the slab could not say it.
        scene.Box(new(0.4f, -0.25f, -0.6f), new(9f, 0.25f, 9f), Slab(new StandardShading(), Grey, Rough), opaque);

        // GraphMaterialImageTests' slab, in its place and for its reason: large enough in frame that
        // a difference in the surface is a difference in the picture.
        scene.Box(new(0.2f, 0.9f, -0.4f), new(1.5f, 0.9f, 1.1f), Slab(shading, Clay, roughness), opaque | casters);

        scene.Commit(opaque);

        // Eight, because the meter is a filter over its own history and a single frame is whatever it
        // started at — which would differ between two renders for a reason that is not the material.
        return scene.Frames(8);
    }

    /// <summary>One surface, spelled with the library's own feature and a named shading model.</summary>
    /// <param name="shading">The model this surface is shaded by.</param>
    /// <param name="colour">Its base colour.</param>
    /// <param name="roughness">Its roughness, which is what both models' terms are scaled by.</param>
    static Material Slab(IMaterialShading shading, Vector3 colour, float roughness) {
        var compilation = MaterialCompiler.Compile(
            new() {
                ShaderName = "ForwardPlus",
                Shading = shading,
                Features = [
                    new MetalRoughnessFeature { BaseColor = colour, Metalness = Metalness, Roughness = roughness }
                ]
            }
        );

        Assert.False(
            compilation.Failed,
            string.Join(Environment.NewLine, compilation.Diagnostics.Select(diagnostic => diagnostic.ToString()))
        );

        return compilation.Material!;
    }

    /// <summary>Opens a device, or skips — unless the environment promised one.</summary>
    static bool TryOpen(out Fixture? fixture) {
        if (Fixture.TryOpen(out fixture, out var reason)) {
            return true;
        }

        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set, so the diffuse models may not be skipped: {reason}");
        }

        Assert.Skip(reason ?? "no Vulkan");
        return false;
    }
}
