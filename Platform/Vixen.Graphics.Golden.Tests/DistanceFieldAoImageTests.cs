// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.PostFx;
using Vixen.Shaders;
using Vixen.Ui.Testing.Visual;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>
///     The traced pass, drawing pixels for the first time.
/// </summary>
/// <remarks>
///     <para>
///         Everything about the distance-field path has so far been checked against something that
///         agrees with it: the bake against closed forms, the clipmap against the bake, the shader
///         against the CPU tracer by reading its text. <see cref="DistanceFieldDeviceTests" /> went one
///         step further and compiled the variant a frame asks for — but nothing had ever <i>run</i> the
///         shader's arithmetic, and the parts checked only against their own mirrors are the parts most
///         likely to be wrong.
///     </para>
///     <para>
///         <b>It starts with the null field, because that is the case with an exactly knowable answer.</b>
///         <c>NoDistanceField</c> reports that nothing is near, so the occlusion integral finds nothing
///         to occlude and the shadow march finds nothing to block: every pixel is <c>(1, 1, 0)</c>,
///         whatever the depth and normals happen to be. A shader that did not run gives black, and the
///         two are as far apart as a frame can be — which is what makes this worth doing before the
///         one with volumes in it.
///     </para>
///     <para>
///         Building it found the thing worth finding: <b>a full-screen pass had no way to fill a
///         compose slot at all</b>, so <c>DistanceFieldAo</c> could not be built by a compositor —
///         under any composition, traced or not. See <see cref="FullScreenRenderer.Composition" />.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public sealed class DistanceFieldAoImageTests {
    /// <summary>
    ///     A world with nothing in it is fully open and fully lit, and the pass says so in every pixel.
    /// </summary>
    [Fact]
    public void TheNullFieldIsOpenAndLitEverywhere() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        var image = Render(owned, traced: false);

        for (var y = 0; y < image.Height; y += image.Height / 8) {
            for (var x = 0; x < image.Width; x += image.Width / 8) {
                var pixel = Pixel(image, x, y);

                // Occlusion in red, sun visibility in green, and nothing in blue — the pass keeps the
                // two apart on purpose, because pre-combining them darkens direct lighting with
                // ambient occlusion.
                Assert.True(pixel.X > 0.98f, $"({x}, {y}) came back occluded by nothing: {pixel}");
                Assert.True(pixel.Y > 0.98f, $"({x}, {y}) came back shadowed by nothing: {pixel}");
                Assert.True(pixel.Z < 0.02f, $"({x}, {y}) put something in blue: {pixel}");
            }
        }
    }

    /// <summary>
    ///     Without a sun there is no shadow march, and green is the constant the shader writes rather
    ///     than the answer to a question. The permutation is what makes the difference — with it off
    ///     the march is not merely skipped, it is not compiled — so this is the second variant of the
    ///     pass to have executed at all.
    /// </summary>
    [Fact]
    public void TheVariantWithoutASunAlsoDraws() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        var image = Render(owned, traced: false, sun: false);
        var pixel = Pixel(image, image.Width / 2, image.Height / 2);

        Assert.True(pixel.X > 0.98f, $"the centre came back occluded by nothing: {pixel}");
        Assert.True(pixel.Y > 0.98f, $"the centre came back shadowed with no sun to shadow it: {pixel}");
    }

    /// <summary>
    ///     <b>And the traced variant reaches a pipeline too, which is the half that had never been
    ///     built by a compositor at all.</b>
    /// </summary>
    /// <remarks>
    ///     It stops before the draw, and the reason is worth naming rather than working around: the
    ///     clipmap's volumes live in the frame's <b>set 0</b>, put there by
    ///     <c>GlobalDistanceFieldRenderer</c>, and a frame without one binds no set 0 — which is a
    ///     validation error at submit whether or not the shader reads it. Giving this frame a real
    ///     clipmap needs a volume-texture upload path on <see cref="Fixture" />, which can upload a 2D
    ///     texture and nothing else. That is the next piece, and it is plumbing rather than a question.
    /// </remarks>
    [Fact]
    public void TheTracedVariantBuildsIntoAFrame() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;

        Render(owned, traced: true, draw: false);
    }

    /// <summary>Builds one frame of the pass, and reads the picture back when it can be drawn.</summary>
    static Bitmap Render(Fixture fixture, bool traced, bool sun = true, bool draw = true) {
        var device = fixture.Device;

        // Nothing draws into these and nothing needs to: with the null field the answer does not
        // depend on them, which is exactly why this is the frame to start with.
        var depth = fixture.Owned("depth", TextureUsage.ColourTarget | TextureUsage.Sampled);
        var normals = fixture.Owned("normals", TextureUsage.ColourTarget | TextureUsage.Sampled);
        var display = fixture.Owned("display", TextureUsage.ColourTarget | TextureUsage.CopySource);

        using var allocator = new DescriptorAllocator(device);
        using var samplers = new SamplerCache(device);
        using var system = new RenderSystem();

        var describer = new EffectPipelineDescriber(device);
        var loader = new EffectLoader(device);
        var effects = new EffectSystem();

        // ⚠ Not the whole library, and it is the same reason ClusterCulling cannot have it: a source
        // set that holds the material tree compiles nothing without a material's composition, because
        // every slot the sources declare has to be bound whether or not this shader reaches it. What
        // this pass actually needs is what it imports, plus the full-screen vertex stage.
        effects.AddProvider(
            new Compiling(
                loader,
                _ => RavenEffects.Only(
                    ["Core", "Geometry", "Shading", "DistanceFields"],
                    Path.Combine("PostFx", "Fullscreen.rvn"),
                    Path.Combine("PostFx", "DistanceFieldAo.rvn")
                )
            )
        );

        // A pass that clears and draws nothing, which is all this needs: it exists so the graph moves
        // the two textures into a state the AO pass can sample, rather than leaving them UNDEFINED.
        var gbuffer = new RenderPassRenderer { Name = "GBuffer", ClearColour = new(0.5f, 0.5f, 1f, 1f) };

        gbuffer.ColourTargets.Add("Depth");
        gbuffer.ColourTargets.Add("Normals");

        using var ao = new DistanceFieldAoRenderer {
            Name = "DistanceFieldAo",
            Depth = "Depth",
            Normals = "Normals",
            Output = "Display",
            Source = traced ? "GlobalDistanceField" : "NoDistanceField",
            SunShadow = sun,

            // Full resolution, so the picture read back is the pass's own output rather than an
            // upsample of it — there is no upsampling node here to do that honestly.
            Scale = 1f,
            Modules = describer,
            Device = device,
            Samplers = samplers,
            Allocator = allocator
        };

        var compositor = new GraphicsCompositor(system) {
            FrameSize = new(Fixture.Side, Fixture.Side),
            Game = new SceneRendererSequence { Children = { gbuffer, ao } }
        };

        compositor.Imports["Depth"] = new(depth.Texture, depth.View, depth.Description);
        compositor.Imports["Normals"] = new(normals.Texture, normals.View, normals.Description);

        compositor.Imports["Display"] = new(
            display.Texture,
            display.View,
            display.Description,
            ResourceState.Undefined,
            ResourceState.CopySource
        );

        allocator.BeginFrame();

        var frame = compositor.Build(fixture.Graph, effects, device);

        // ⚠ Asserted rather than assumed, and it is the assertion that would have caught the missing
        // composition: an effect the system cannot resolve is a miss, and a node that got no effect
        // draws nothing — which is a picture indistinguishable from a pass nobody scheduled.
        Assert.Empty(effects.Misses);
        Assert.True(ao.Pass.PipelineCount > 0, "the pass compiled no pipeline, so it drew nothing");

        return draw ? fixture.Render(frame.Texture("harness", "Display")) : default;
    }

    static Vector3 Pixel(in Bitmap image, int x, int y) {
        var offset = image.Offset(Math.Clamp(x, 0, image.Width - 1), Math.Clamp(y, 0, image.Height - 1));

        return new(image.Pixels[offset] / 255f, image.Pixels[offset + 1] / 255f, image.Pixels[offset + 2] / 255f);
    }

    static bool TryOpen(out Fixture? fixture) {
        if (Fixture.TryOpen(out fixture, out var reason)) {
            return true;
        }

        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set and no device could be opened: {reason}");
        }

        Assert.Skip(reason ?? "no Vulkan");

        return false;
    }
}
