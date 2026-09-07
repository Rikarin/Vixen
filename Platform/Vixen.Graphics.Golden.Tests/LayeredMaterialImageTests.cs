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
///     A layered material whose weights come out of a splat map, photographed through the real frame.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Doc 48 § B1's M11 shipped without a picture, and said so.</b> Its own closing note is
///         that "no frame has been drawn through any of this" — the shader binds, the variant lowers,
///         the parameter names match Raven's reflection, <c>paintedChannels</c> reaches the block, and
///         every one of those is equally true of a material that puts nothing on the screen. Worse,
///         they are all equally true of a material that puts the <em>wrong layer</em> on the screen,
///         which is exactly what
///         <see href="https://github.com/Rikarin/Vixen/issues/622">#622</see> was: a channel-to-layer
///         mapping proved by reading the shader rather than by drawing it.
///     </para>
///     <para>
///         <b>The oracle is a differential, so there is no reference PNG here.</b> A splat map that is
///         1 in one channel and 0 in the others gives that channel's layer weight 1 and every other
///         layer weight 0; the total is then exactly 1, the scale exactly 1, and
///         <c>TexturedMaterialLayersSurface</c> writes <c>diffuseColor</c>, <c>f0</c> and
///         <c>perceptualRoughness</c> from the same three numbers, through the same
///         <c>Brdf.F0FromMetalness</c>, that <c>MetalRoughnessSurface</c> writes them from. So a
///         painted stack and a one-number material are two spellings of one surface and the frames
///         must agree — whatever this machine's tone map, exposure and sun do, they do identically to
///         both.
///     </para>
///     <para>
///         ⚠ <b>Three channels rather than one, because "the stack drew something" is not the claim.</b>
///         A renderer that ignored the map and always took layer 0 would satisfy a single-channel
///         test perfectly. Each of R, G and B is painted in turn and has to produce <em>its own</em>
///         layer's colour, which is the mapping that had never been drawn.
///     </para>
///     <para>
///         ⚠ <b>And the guard #622 added is photographed rather than read.</b> The second test paints
///         R over a four-layer stack through a map whose alpha is 1 everywhere — which every one- and
///         three-channel texture's alpha is. With <c>PaintedChannels</c> at its default of three the
///         frame is layer 0; declaring four makes the same map paint layer 3 at weight 1 over the
///         whole surface, and the frames must differ. That difference <em>is</em> the defect, held in
///         the suite as the half that can be false: without it, a <c>Painted</c> that returned zero
///         for every layer would pass the first test's control and the guard would be indistinguishable
///         from a broken feature.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public class LayeredMaterialImageTests {
    /// <summary>How large the splat map is, in texels.</summary>
    /// <remarks>
    ///     Small deliberately: every map here is constant, so resolution buys nothing and the claim is
    ///     about which channel a layer reads rather than about filtering.
    /// </remarks>
    const int Side = 16;

    /// <summary>Every layer's roughness, and the untextured spelling's.</summary>
    /// <remarks>
    ///     One number across the stack, because roughness is not the variable under test and a layer
    ///     that differed in it would make a specular lobe part of the comparison.
    /// </remarks>
    const float Roughness = 0.45f;

    /// <summary>The colour of each layer, in the channel order the map paints them.</summary>
    /// <remarks>
    ///     ⚠ <b>Saturated and each dominant in a different channel.</b> A stack of near-greys would
    ///     blend into something the tolerance's mean absorbs, and the failure this suite exists to
    ///     catch is "layer 3 covers everything" — which against a grey stack is a picture that looks
    ///     right. The fourth is deliberately the loudest, since it is the one an unpainted alpha
    ///     would smear over the whole surface.
    /// </remarks>
    static readonly Vector3[] Layers = [
        new(0.82f, 0.21f, 0.11f),
        new(0.11f, 0.62f, 0.21f),
        new(0.13f, 0.24f, 0.81f),
        new(0.86f, 0.81f, 0.09f)
    ];

    /// <summary>The frame all of these renderings share. Deliberately the tier suite's own.</summary>
    static StandardFrameAsset Frame => new() {
        Name = "LayeredMaterial",
        Shadows = ShadowMode.Cascades,
        Gi = GiMode.Off,
        Reflections = ReflectionsMode.Off,
        Antialiasing = AntialiasingMode.Fxaa,
        Exposure = ExposureMode.Automatic,
        Particles = false
    };

    /// <summary>
    ///     Each of the splat map's channels paints its own layer, and the painted stack draws what the
    ///     layer it selected draws.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The instrument is checked before the claim.</b> Two frames in which the slab never
    ///     drew agree perfectly, and "nothing drew" is what a missing variant, an unpaired texture
    ///     name and a culled pass all look like. So the run first asserts that two <em>untextured</em>
    ///     materials of different colours produce different pictures; only then does it assert that a
    ///     painted channel produces its layer's.
    /// </remarks>
    [Fact]
    public void Each_splat_channel_paints_its_own_layer() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using (fixture) {
            var references = Layers.Take(3).Select(colour => Render(fixture!, _ => Library(colour))).ToArray();

            // ⚠ First: this scene is a picture of its material at all.
            var control = GoldenImage.Compare(references[0], references[1], Tolerance.Shaded);

            Assert.False(
                control.Matches,
                $"Two untextured materials of different colours produced the same frame on "
                + $"{Adapter(fixture!)}, so this scene is not a picture of its material and nothing "
                + "below it means anything."
            );

            for (var channel = 0; channel < 3; channel++) {
                var painted = Render(fixture!, scene => Stack(scene, Channel(channel), 3, 3));
                var comparison = GoldenImage.Compare(references[channel], painted, Tolerance.Shaded);

                Assert.True(
                    comparison.Matches,
                    $"A splat map painted only in channel {channel} drew a surface that is not layer "
                    + $"{channel} on {Adapter(fixture!)}: {comparison.DifferingPixels} of "
                    + $"{comparison.TotalPixels} pixels differ, worst channel "
                    + $"{comparison.WorstChannel} at {comparison.WorstAt}, mean "
                    + $"{comparison.MeanChannel:F3}."
                );
            }
        }
    }

    /// <summary>
    ///     The alpha of a splat map that has no painted alpha does not paint a fourth layer — and
    ///     declaring that it does changes the picture, which is what says the guard guards something.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Both halves, because either alone is satisfied by a broken feature.</b> "The
    ///         three-channel frame is layer 0" is satisfied by a <c>Painted</c> that returns zero for
    ///         every layer past 0 whatever the material declares; "the four-channel frame differs" is
    ///         satisfied by a feature that ignores the map entirely and reads a different uniform.
    ///         Together they say <c>paintedChannels</c> is the thing deciding, and that the default
    ///         decides in favour of the picture #622 asked for.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The map's alpha is 1 because that is not a choice.</b> A one- or three-channel
    ///         texture samples alpha as 1 — the hazard <c>TexturedOpacitySurface</c> documents and the
    ///         one this feature inherited. Uploading an <c>Rgba8UNorm</c> map whose alpha bytes are
    ///         255 is the same number the sampler would hand a shader reading an RGB texture, so this
    ///         is the real case rather than a stand-in for it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_alpha_of_an_unpainted_channel_does_not_become_a_fourth_layer() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using (fixture) {
            var splat = Channel(0);

            // Four layers, three painted channels: the shipping default, and the material the
            // compiler warns about — see TexturedMaterialTests for that half.
            var guarded = Render(fixture!, scene => Stack(scene, splat, 4, 3));
            var declared = Render(fixture!, scene => Stack(scene, splat, 4, 4));
            var layerZero = Render(fixture!, _ => Library(Layers[0]));

            var guardedIsLayerZero = GoldenImage.Compare(layerZero, guarded, Tolerance.Shaded);

            Assert.True(
                guardedIsLayerZero.Matches,
                $"A four-layer stack painted only in R drew something other than layer 0 on "
                + $"{Adapter(fixture!)}, so the map's alpha reached a layer it does not paint: "
                + $"{guardedIsLayerZero.DifferingPixels} of {guardedIsLayerZero.TotalPixels} pixels "
                + $"differ, worst channel {guardedIsLayerZero.WorstChannel} at "
                + $"{guardedIsLayerZero.WorstAt}, mean {guardedIsLayerZero.MeanChannel:F3}."
            );

            var declaredDiffers = GoldenImage.Compare(guarded, declared, Tolerance.Shaded);

            Assert.False(
                declaredDiffers.Matches,
                $"Declaring the same splat map four-channel drew the same frame as three on "
                + $"{Adapter(fixture!)}, so paintedChannels is not what decides which layers a map "
                + "paints and the frame above proves nothing about the guard."
            );
        }
    }

    /// <summary>A constant splat map, painted in one channel and zero in the others.</summary>
    /// <param name="channel">Which of R, G, B carries the paint.</param>
    /// <remarks>
    ///     ⚠ <b>Alpha is 255 in every texel, deliberately.</b> The whole of #622 is that a map with no
    ///     painted alpha still samples alpha as 1; a fixture that wrote zero there would be a splat
    ///     map no importer produces and could not see the defect it was written for.
    /// </remarks>
    static byte[] Channel(int channel) {
        var texels = new byte[Side * Side * 4];

        for (var texel = 0; texel < Side * Side; texel++) {
            texels[(texel * 4) + channel] = 255;
            texels[(texel * 4) + 3] = 255;
        }

        return texels;
    }

    /// <summary>The painted stack, with its splat map on the device.</summary>
    /// <param name="scene">The scene that owns the upload.</param>
    /// <param name="splat">The map's texels, RGBA, row-major.</param>
    /// <param name="layers">How many layers the stack has.</param>
    /// <param name="painted">How many of the map's channels the material declares as painted.</param>
    /// <remarks>
    ///     ⚠ <b>The map is uploaded through a linear view and not an sRGB one.</b> A splat weight is a
    ///     number rather than a colour, and an sRGB view over the same bytes would hand the shader
    ///     0.0 and 1.0 unchanged but every value between them transferred — which for this fixture's
    ///     0-or-1 map is invisible, and for a real painted mask is a blend curve nobody authored.
    ///     Uploading it correctly here is what stops this file from teaching the wrong thing.
    /// </remarks>
    static Material Stack(TierScene scene, byte[] splat, int layers, int painted) {
        var feature = new TexturedMaterialLayersFeature {
            PaintedChannels = painted,
            Layers = [.. Layers.Take(layers).Select(colour => new MaterialLayerValue(colour, 0f, Roughness, 1f))]
        };

        var view = scene.Map($"Splat.{painted}", Side, splat, PixelFormat.Rgba8UNorm);
        var material = Compiled(new() { ShaderName = "ForwardPlus", Features = [feature] });

        // The pairing the renderer completes: the feature names its map, the host puts the view under
        // that name, and `MaterialRenderFeature` turns it into the slot the shader indexes. ⚠ Without
        // this the index stays zero, the shader samples slot zero's magenta checker, and the stack
        // blends its layers by a checker rather than drawing nothing.
        material.Parameters.Set(ParameterKeys.New<TextureViewHandle>(feature.SplatMap), view);

        return material;
    }

    /// <summary>The same surface spelled with the library's own untextured feature.</summary>
    static Material Library(Vector3 colour) =>
        Compiled(
            new() {
                ShaderName = "ForwardPlus",
                Features = [new MetalRoughnessFeature { BaseColor = colour, Metalness = 0f, Roughness = Roughness }]
            }
        );

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
    ///     What the slab is made of, given the scene — because a textured material's map is the
    ///     scene's to own and to upload, and the material cannot be built before there is one.
    /// </param>
    static Bitmap Render(Fixture fixture, Func<TierScene, Material> slab) {
        var effects = new EffectSystem();

        effects.AddProvider(new Compiling(new(fixture.Device)));

        using var scene = TierScene.Open(fixture, effects, new() { Game = Frame }, QualityTier.High);

        var casters = scene.Stages.TryGetValue("Shadow", out var shadow) ? shadow.Mask : default;
        var opaque = scene.Stages["Opaque"].Mask;

        // A grey floor, so the slab is the only thing in the frame whose surface is the variable.
        scene.Box(new(0.4f, -0.25f, -0.6f), new(9f, 0.25f, 9f), Library(new(0.35f, 0.35f, 0.35f)), opaque);

        // GraphMaterialImageTests' slab, in its place, for its reason: big enough that a difference
        // in it is a difference in the picture rather than pixels the mean absorbs.
        scene.Box(new(0.2f, 0.9f, -0.4f), new(1.5f, 0.9f, 1.1f), slab(scene), opaque | casters);

        scene.Commit(opaque);

        // Several frames, because the automatic exposure is a filter over its own history and a
        // single frame is whatever it started at.
        return scene.Frames(8);
    }

    /// <summary>What ran, said in every message here so that no number is anonymous.</summary>
    static string Adapter(Fixture fixture) =>
        $"{fixture.Device.Adapter.Name} ({fixture.Device.Adapter.Kind}, {fixture.Device.Adapter.DriverVersion})";

    /// <summary>A device, or a loud skip — and a second loud skip for the capability this needs.</summary>
    /// <remarks>
    ///     ⚠ <b>Bindless is checked and skipped on, not assumed</b>, for
    ///     <c>BakedMaterialImageTests.TryOpen</c>'s reason: a splat map is a <c>uint</c> into
    ///     <c>WorldRenderer</c>'s table, and without <c>HasBindless</c> there is no table. ADR-011
    ///     calls that a supported configuration rather than a degraded one, so
    ///     <c>VIXEN_REQUIRE_VULKAN</c> does not turn it into a failure — the capability is genuinely
    ///     absent rather than a run that failed to find a device.
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
            + "sample a splat map and there is nothing here to photograph."
        );

        return false;
    }
}
