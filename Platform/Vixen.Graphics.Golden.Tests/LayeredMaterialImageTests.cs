// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Vixen.Core.Yaml;
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

    /// <summary>How wide a square of the project's own splat map the painted comparisons upload.</summary>
    /// <remarks>
    ///     The same 48 <c>PaintedLayersTests.PureSide</c> asserts is pure in the committed PNG. Two
    ///     files rather than one shared constant because they are in different assemblies and neither
    ///     may reference the other; what keeps them honest is that the sample's test fails first if
    ///     the map's pure zones move, rather than this one passing over a blend.
    /// </remarks>
    const int PureSide = 48;

    /// <summary>Where the project's splat map is purely one channel — R, then G, then B.</summary>
    static readonly (int Top, int Left)[] PurelyPaintedAt = [(320, 296), (8, 416), (184, 264)];

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

    /// <summary>⚠ A height map decides which of two equally-painted layers is on top.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The picture the height slice said could not exist</b> —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/615">#615</a>. Its report gave
    ///         "there is no material anywhere that carries a <c>TexturedMaterialLayersFeature</c> to
    ///         render" as the reason there was no GPU verification, and this file had been
    ///         constructing one and photographing it through the real frame since 2026-09-07. ⚠ The
    ///         claim was about the <em>production</em> tree and was written as though it were about
    ///         the whole of it — which is how a feature ships with its arithmetic unmeasured.
    ///     </para>
    ///     <para>
    ///         <b>The oracle is closed-form and comes out on a layer this suite can already draw
    ///         alone.</b> Two layers, weights 1, splat <c>(0.5, 0.5)</c> — so without a height map
    ///         the keys tie at 0.5, the floor is 0, and the surface is a half-and-half blend of two
    ///         colours that is <em>neither</em> of them. Add a height map of <c>(0, 1)</c>: the keys
    ///         become 0.5 and 0.5 + 0.25·1 = 0.75, the peak is 0.75, the floor is
    ///         0.75 − 0.1 = 0.65, layer 0's weight clamps to <b>zero</b> and layer 1 survives alone.
    ///         So the frame must equal the library's own untextured layer 1, exactly.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the tie is asserted first, because it is the instrument.</b> A feature that
    ///         ignored the splat map, or a permutation that never turned on, would draw layer 1
    ///         whatever it was handed — so "the blended frame is layer 1" alone is satisfied by
    ///         several broken shaders. The pair — a tie that is not layer 1, and the same tie broken
    ///         by height that is — is what says the height map is the thing deciding.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_height_map_lifts_one_of_two_tied_layers_clear_of_the_transition() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using (fixture) {
            var splat = Split();
            var layerOne = Render(fixture!, _ => Library(Layers[1]));
            var tied = Render(fixture!, scene => Stack(scene, splat, 2, 2));

            var tieIsNotLayerOne = GoldenImage.Compare(layerOne, tied, Tolerance.Shaded);

            Assert.False(
                tieIsNotLayerOne.Matches,
                $"Two layers painted 0.5 each drew layer 1 alone on {Adapter(fixture!)}, so the "
                + "splat map is not deciding anything and the height comparison below would be "
                + "satisfied by a shader that ignored both maps."
            );

            var lifted = Render(fixture!, scene => Stack(scene, splat, 2, 2, Height()));
            var liftedIsLayerOne = GoldenImage.Compare(layerOne, lifted, Tolerance.Shaded);

            Assert.True(
                liftedIsLayerOne.Matches,
                $"A height map lifting layer 1 by 0.25 over a transition of 0.1 drew something other "
                + $"than layer 1 alone on {Adapter(fixture!)}: {liftedIsLayerOne.DifferingPixels} of "
                + $"{liftedIsLayerOne.TotalPixels} pixels differ, worst channel "
                + $"{liftedIsLayerOne.WorstChannel} at {liftedIsLayerOne.WorstAt}, mean "
                + $"{liftedIsLayerOne.MeanChannel:F3}."
            );
        }
    }

    /// <summary>
    ///     The project's own painted material, with the project's own map, draws the layer that map
    ///     paints.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Every claim above this one is about a material this file constructed</b>, and
    ///         that was the whole of doc 48's M11 remainder —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/1073">#1073</a>: the feature was
    ///         lowered, bound and photographed, and no material an artist could open carried one. So
    ///         this reads <c>Samples/13-ThirdPersonShooter</c>'s <c>plaza.vxmat</c> and
    ///         <c>plaza-splat.png</c> off disk — the authored files, not the imported artefacts —
    ///         and photographs the surface a player sees.
    ///     </para>
    ///     <para>
    ///         <b>The oracle is the plan document's, on production pixels.</b> Doc 48 § M11 asks for
    ///         "a map that is pure in one channel over one region, drawn as that layer's material and
    ///         matching a single-layer material of that layer in the same frame". The map's pure
    ///         zones are where that region is, and a 48-texel square of each is uploaded rather than
    ///         the whole map, because the slab is one surface and a region of it cannot be addressed
    ///         from here — the crop is which part of the map is under test, exactly as a screen
    ///         rectangle would be. <c>PaintedLayersTests</c> asserts those same squares are pure in
    ///         the committed PNG, so a regenerated map cannot leave this comparison quietly
    ///         photographing a blend.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The layer values come out of the file too</b>, and the reference is built from
    ///         them. A test that compared the plaza against colours retyped here would agree with
    ///         itself about what the plaza is made of, which is the one thing it must not do: the
    ///         claim is that <em>this material</em> draws <em>its own</em> layer 1 where its map is
    ///         green, and the roughness differs per layer in the authored file where this suite's
    ///         own fixtures hold it constant.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the instrument first, as everywhere in this file.</b> Two of the project's
    ///         layers are rendered untextured and asserted to differ before any painted frame is
    ///         compared — two frames in which nothing drew agree perfectly, and a missing variant, an
    ///         unpaired name and a culled pass all look like that.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_projects_own_painted_material_draws_the_layer_its_own_map_paints() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using (fixture) {
            var authored = Authored();
            var layered = Assert.Single(authored.Features.OfType<TexturedMaterialLayersFeature>());
            var map = PngCodec.Load(Path.Combine(AppContext.BaseDirectory, "Authored", "plaza-splat.png"));

            var references = layered.Layers
                .Select(layer => Render(fixture!, _ => Library(layer.BaseColor, layer.Metalness, layer.Roughness)))
                .ToArray();

            var control = GoldenImage.Compare(references[0], references[1], Tolerance.Shaded);

            Assert.False(
                control.Matches,
                $"Two of the plaza's own layers drew the same frame on {Adapter(fixture!)}, so this "
                + "scene is not a picture of its material and nothing below it means anything."
            );

            for (var channel = 0; channel < PurelyPaintedAt.Length; channel++) {
                var (top, left) = PurelyPaintedAt[channel];
                var painted = Render(
                    fixture!,
                    scene => Project(scene, authored, layered, Crop(map, top, left))
                );

                var comparison = GoldenImage.Compare(references[channel], painted, Tolerance.Shaded);

                Assert.True(
                    comparison.Matches,
                    $"plaza.vxmat over the part of plaza-splat.png that is pure in channel {channel} "
                    + $"drew a surface that is not its own layer {channel} on {Adapter(fixture!)}: "
                    + $"{comparison.DifferingPixels} of {comparison.TotalPixels} pixels differ, worst "
                    + $"channel {comparison.WorstChannel} at {comparison.WorstAt}, mean "
                    + $"{comparison.MeanChannel:F3}."
                );
            }
        }
    }

    /// <summary>The material the project ships, read as its author wrote it.</summary>
    /// <remarks>
    ///     The source document rather than the imported chunk, for <c>FrameDocumentTests</c>' reason:
    ///     what is under test is the material an artist edits, and reading the artefact would test
    ///     the importer instead.
    /// </remarks>
    static MaterialContent Authored() {
        MathScalars.Register();

        return YamlSerializer.Parse<MaterialContent>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Authored", "plaza.vxmat"))
        );
    }

    /// <summary>The authored material, with a piece of its own splat map on the device.</summary>
    /// <param name="scene">The scene that owns the upload.</param>
    /// <param name="content">The material as its file describes it.</param>
    /// <param name="layered">Its painted-layer feature, for the name the map is bound under.</param>
    /// <param name="crop">The square of the map to sample, RGBA, row-major.</param>
    static Material Project(
        TierScene scene,
        MaterialContent content,
        TexturedMaterialLayersFeature layered,
        byte[] crop
    ) {
        Assert.True(MaterialShading.TryResolve(content.Shading, out var shading));

        var material = Compiled(content.ToDescriptor(shading));

        // ⚠ Named by the feature and not by this file. `WorldRenderer.Paired` keys the frame's one
        // entry off the feature's default, so a material that renamed its map resolves nothing and
        // samples slot zero's checker — and a test that spelled the name itself would still pass.
        material.Parameters.Set(
            ParameterKeys.New<TextureViewHandle>(layered.SplatMap),
            scene.Map($"Plaza.{crop.GetHashCode()}", PureSide, crop, PixelFormat.Rgba8UNorm)
        );

        return material;
    }

    /// <summary>One square of a decoded map, as texels a scene can upload.</summary>
    /// <param name="map">The whole picture.</param>
    /// <param name="top">The square's first row.</param>
    /// <param name="left">The square's first column.</param>
    static byte[] Crop(Bitmap map, int top, int left) {
        var texels = new byte[PureSide * PureSide * 4];

        for (var row = 0; row < PureSide; row++) {
            map.Pixels.AsSpan(map.Offset(left, top + row), PureSide * 4).CopyTo(texels.AsSpan(row * PureSide * 4));
        }

        return texels;
    }

    /// <summary>A splat map painting R and G equally, so two layers tie.</summary>
    /// <returns>The texels, RGBA, row-major.</returns>
    static byte[] Split() {
        var texels = new byte[Side * Side * 4];

        for (var texel = 0; texel < Side * Side; texel++) {
            texels[texel * 4] = 128;
            texels[(texel * 4) + 1] = 128;
            texels[(texel * 4) + 3] = 255;
        }

        return texels;
    }

    /// <summary>A height map that is zero for layer 0 and one for layer 1.</summary>
    /// <returns>The texels, RGBA, row-major.</returns>
    static byte[] Height() {
        var texels = new byte[Side * Side * 4];

        for (var texel = 0; texel < Side * Side; texel++) {
            texels[(texel * 4) + 1] = 255;
            texels[(texel * 4) + 3] = 255;
        }

        return texels;
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
    /// <param name="height">A height map, or null for the unblended variant.</param>
    static Material Stack(TierScene scene, byte[] splat, int layers, int painted, byte[]? height = null) {
        var feature = new TexturedMaterialLayersFeature {
            PaintedChannels = painted,
            HeightBlended = height is not null,
            Layers = [.. Layers.Take(layers).Select(colour => new MaterialLayerValue(colour, 0f, Roughness, 1f))]
        };

        var view = scene.Map($"Splat.{painted}.{layers}", Side, splat, PixelFormat.Rgba8UNorm);
        var material = Compiled(new() { ShaderName = "ForwardPlus", Features = [feature] });

        if (height is not null) {
            material.Parameters.Set(
                ParameterKeys.New<TextureViewHandle>(feature.HeightMap),
                scene.Map($"Height.{layers}", Side, height, PixelFormat.Rgba8UNorm)
            );
        }

        // The pairing the renderer completes: the feature names its map, the host puts the view under
        // that name, and `MaterialRenderFeature` turns it into the slot the shader indexes. ⚠ Without
        // this the index stays zero, the shader samples slot zero's magenta checker, and the stack
        // blends its layers by a checker rather than drawing nothing.
        material.Parameters.Set(ParameterKeys.New<TextureViewHandle>(feature.SplatMap), view);

        return material;
    }

    /// <summary>The same surface spelled with the library's own untextured feature, layer by layer.</summary>
    /// <param name="colour">The layer's base colour.</param>
    /// <param name="metalness">Its metalness.</param>
    /// <param name="roughness">Its roughness — a per-layer number in an authored material.</param>
    /// <remarks>
    ///     ⚠ Separate from the overload above rather than a default on it, because the project's
    ///     layers differ in roughness where this file's fixtures deliberately do not: a comparison
    ///     that held roughness at <see cref="Roughness" /> would be against a surface the plaza is
    ///     not, and would fail for a reason that has nothing to do with which layer was painted.
    /// </remarks>
    static Material Library(Vector3 colour, float metalness, float roughness) =>
        Compiled(
            new() {
                ShaderName = "ForwardPlus",
                Features = [new MetalRoughnessFeature { BaseColor = colour, Metalness = metalness, Roughness = roughness }]
            }
        );

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
