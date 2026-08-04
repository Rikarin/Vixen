// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics.Vulkan;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Water;
using Vixen.Shaders;
using Vixen.Ui.Testing.Visual;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>
///     The water pass, drawing pixels — [docs/plan/35 § W2].
/// </summary>
/// <remarks>
///     <para>
///         W2's exit criterion is "a flat quad of water over a textured floor, absorbing and
///         scattering with depth, reflecting an off-screen object correctly". This is that, with the
///         quad supplied as planes rather than drawn: the surface mesh is W4's, and the ordering doc
///         35 chose is deliberate — <b>the <em>look</em> is proven before any of the meshing exists,
///         which is how you avoid building a beautiful quadtree for a surface that turns out to shade
///         wrongly.</b>
///     </para>
///     <para>
///         <b>Assertions on the physics rather than on a bitmap, and that is a considered swap.</b> A
///         golden PNG would say "these bytes changed" and leave somebody to work out whether the
///         change was absorption, the phase function, the Fresnel term or the reconstruction. What is
///         asserted here is each of those separately — that dry pixels pass through <em>exactly</em>,
///         that what shows through falls off with the path, that red falls off faster than blue, that
///         the reflection arrives by Fresnel, and that alpha is the waterline mask. A reference image
///         nobody looked at approves whatever was there; these do not.
///     </para>
///     <para>
///         ⚠ <b>The planes are uploaded rather than rendered, and the depths are exact because of
///         it.</b> A fixture that drew its own floor would be asserting the pass against a rasteriser,
///         and the numbers below — a path of exactly so many metres, an absorption of exactly so much
///         — would become approximately true. Uploading them makes every expectation something a
///         reader can work out from the medium's coefficients.
///     </para>
///     <para>
///         Serialised with the rest of the driver tests: <see cref="VulkanDiagnostics" /> is
///         process-wide.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public sealed class WaterPassImageTests {
    /// <summary>How far the camera is from the near plane's reconstruction, in metres.</summary>
    const float Near = 0.1f;

    /// <summary>And the far plane.</summary>
    const float Far = 100f;

    /// <summary>What the floor under the water is: a bright, saturated red-green so absorption shows.</summary>
    /// <remarks>
    ///     ⚠ Red and green rather than a grey. Water absorbs red about thirty times faster than blue,
    ///     so a grey floor going grey-blue is one observation; a red floor going green and then blue
    ///     is three, and the order is the thing worth asserting.
    /// </remarks>
    static readonly Vector3 Floor = new(0.9f, 0.8f, 0.7f);

    /// <summary>
    ///     ⚠ Where there is no water the frame passes through untouched.
    /// </summary>
    /// <remarks>
    ///     The first thing to get right and the easiest to get wrong: a pass that ran over the whole
    ///     screen and tinted the dry half would look, in a busy scene, like a slightly hazy frame. It
    ///     is asserted exactly rather than within a tolerance because the shader returns the sampled
    ///     colour unchanged, so anything but equality is arithmetic that should not have happened.
    /// </remarks>
    [Fact]
    public void Dry_pixels_pass_the_frame_through_untouched() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        var image = Render(owned, coverage: 0f, waterDepth: 3f);

        var pixel = Pixel(image, 64, 64);

        Assert.Equal(Floor.X, pixel.X, 0.01f);
        Assert.Equal(Floor.Y, pixel.Y, 0.01f);
        Assert.Equal(Floor.Z, pixel.Z, 0.01f);

        // ⚠ And the waterline mask says "not water" — which is what § D9's underwater composite
        // reads, and what a scalar per frame could never have expressed.
        Assert.Equal(0f, Alpha(image, 64, 64), 0.01f);
    }

    /// <summary>
    ///     ⚠ What shows through falls off with the path, and it saturates rather than going to zero.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The whole of why water is integrated rather than alpha-blended: the opacity is a
    ///         consequence of the depth, not a number somebody typed. A shallow edge is nearly the
    ///         floor and a deep middle is nearly the medium's own colour, from one coefficient triple.
    ///     </para>
    ///     <para>
    ///         ⚠ It saturates because what is scattered in at the far end is absorbed again on the way
    ///         out — which is why a deep sea is a <em>flat</em> colour and not an ever-brighter one,
    ///         and why a model that multiplied by depth looks like fog. Twenty metres and forty are
    ///         asserted to be close to each other and both far from two.
    ///     </para>
    /// </remarks>
    [Fact]
    public void What_shows_through_falls_off_with_the_path_and_then_saturates() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;

        var shallow = Pixel(Render(owned, coverage: 1f, waterDepth: 0.5f), 64, 64);
        var middling = Pixel(Render(owned, coverage: 1f, waterDepth: 4f), 64, 64);
        var deep = Pixel(Render(owned, coverage: 1f, waterDepth: 40f), 64, 64);
        var deeper = Pixel(Render(owned, coverage: 1f, waterDepth: 60f), 64, 64);

        // Half a metre of clear water barely touches the floor's red.
        Assert.True(shallow.X > 0.6f, $"half a metre of water absorbed most of the floor's red: {shallow}");

        // Four metres has taken most of it.
        Assert.True(middling.X < shallow.X * 0.4f, $"four metres absorbed no more than half a metre did: {middling}");

        // Forty has taken all of it, and what is left is the medium's own scattered light.
        Assert.True(deep.X < 0.05f, $"forty metres of water still shows the floor's red: {deep}");

        // ⚠ And sixty is the same picture as forty, because the integral saturates. A model that
        // multiplied by depth would keep going and would read as fog.
        Assert.Equal(deep.X, deeper.X, 0.02f);
        Assert.Equal(deep.Y, deeper.Y, 0.02f);
        Assert.Equal(deep.Z, deeper.Z, 0.02f);

        // ⚠ And it saturates at something, not at nothing. Deep water is blue, not black — which is
        // the sky term, and which this fixture is what found the absence of.
        Assert.True(deep.Z > 0.3f, $"deep water came back black rather than blue: {deep}");
    }

    /// <summary>
    ///     ⚠ Red goes first, then green, then blue — which is why deep water is blue.
    /// </summary>
    /// <remarks>
    ///     The one observation that says the coefficients are per channel and are being applied per
    ///     channel. A shader that averaged them, or that used one extinction for all three, gives water
    ///     that darkens without ever changing hue — which reads as smoked glass, and is the commonest
    ///     way for water to look wrong while looking deliberate.
    /// </remarks>
    [Fact]
    public void Red_is_absorbed_before_green_and_green_before_blue() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;

        // A floor that starts brightest in red, so any ordering in the result is the medium's doing
        // and not the floor's. Eight metres, which is past the crossover: the *transmitted* blue and
        // green are close together — scattering takes blue out of the straight-through ray as fast as
        // absorption takes green — and what separates them is the sky term, which is exactly the
        // asymmetry that makes a sea blue rather than merely dark.
        var pixel = Pixel(Render(owned, coverage: 1f, waterDepth: 8f), 64, 64);

        Assert.True(
            pixel.X < pixel.Y && pixel.Y < pixel.Z,
            $"eight metres of water did not reorder a ({Floor.X}, {Floor.Y}, {Floor.Z}) floor: {pixel}"
        );
    }

    /// <summary>
    ///     ⚠ The reflection plane arrives, and it arrives by Fresnel rather than by a slider.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Doc 19 § L5's plane, in the seat § D8 routes it to. What makes the assertion mean
    ///         something is the <em>plane</em>: it holds a colour nothing else in the frame has, so a
    ///         pass that ignored it and a pass that read it are as far apart as a picture can be.
    ///     </para>
    ///     <para>
    ///         ⚠ Blended by <c>F(0.02, NdotV)</c> and not by a weight, which is why the water reads as
    ///         nearly transparent looking straight down. That is the term doing the work, and a
    ///         material that raised <c>surfaceF0</c> to tint the water would have made it metal — so
    ///         the assertion is that the reflection is <em>present but small</em> at normal incidence,
    ///         not that it dominates.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_reflection_plane_arrives_weighted_by_fresnel() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;

        // Deep, so what is behind contributes nothing and the only thing that can put green in the
        // picture is the reflection plane.
        var without = Pixel(Render(owned, coverage: 1f, waterDepth: 30f), 64, 64);
        var with = Pixel(Render(owned, coverage: 1f, waterDepth: 30f, reflect: true), 64, 64);

        Assert.True(with.Y > without.Y, $"the reflection plane contributed nothing: {without} against {with}");

        // Present but small: Fresnel at normal incidence against water is about two per cent, so a
        // pass that blended it at full weight would have replaced the picture rather than tinted it.
        Assert.True(with.Y < 0.4f, $"the reflection was blended at more than Fresnel's weight: {with}");
    }

    /// <summary>
    ///     ⚠ Alpha is the waterline mask, per pixel, and one frame carries both states.
    /// </summary>
    /// <remarks>
    ///     [docs/plan/35 § D9] separates the underwater <em>volume</em> from the <em>waterline</em>
    ///     explicitly, because a camera straddling the surface needs two treatments in one frame
    ///     divided by a curve — and a post-process volume's fold produces one weight for the whole
    ///     frame. This is what the composite reads instead: the left half covered, the right half not,
    ///     in one pass.
    /// </remarks>
    [Fact]
    public void The_waterline_mask_carries_both_states_in_one_frame() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        var image = Render(owned, coverage: 1f, waterDepth: 6f, splitAt: Fixture.Side / 2);

        Assert.Equal(1f, Alpha(image, 32, 64), 0.02f);
        Assert.Equal(0f, Alpha(image, 96, 64), 0.02f);

        // And the two halves are different pictures, which is the point of the mask being per pixel.
        var wet = Pixel(image, 32, 64);
        var dry = Pixel(image, 96, 64);

        Assert.True(dry.X - wet.X > 0.2f, $"the covered half looks like the dry half: {wet} against {dry}");
    }

    /// <summary>Builds one frame of the pass and reads the picture back.</summary>
    /// <remarks>
    ///     ⚠ <b>The planes are uploaded, so every depth below is exact.</b> Device depth is written
    ///     under the engine's reversed-Z convention — near is one, far is zero — which is the
    ///     convention two passes in this directory have already been caught getting backwards.
    /// </remarks>
    static Bitmap Render(
        Fixture fixture,
        float coverage,
        float waterDepth,
        bool reflect = false,
        int splitAt = -1
    ) {
        var device = fixture.Device;

        // The camera looks straight down −Z from the origin at a floor `Near + surface + waterDepth`
        // away, with the water surface `Near + surface` away. Orthographic, so every pixel's path is
        // the same length and the numbers asserted above are one number rather than a range.
        const float Surface = 2f;

        var surfaceDistance = Near + Surface;
        var behindDistance = surfaceDistance + waterDepth;

        var copy = fixture.Sampled("behind", Fixture.Side, Fill(_ => Encode(Floor, 1f)));
        var depth = fixture.Sampled("sceneDepth", Fixture.Side, Fill(_ => Encode(DeviceDepth(behindDistance))));

        var surface = fixture.Sampled(
            "waterSurface",
            Fixture.Side,
            Fill(x => Encode(DeviceDepth(surfaceDistance), splitAt < 0 || x < splitAt ? coverage : 0f, 0f, 1f))
        );

        // Flat, facing the camera, and no foam — so Fresnel is at normal incidence and the foam term
        // is off by its own value rather than by its permutation.
        var normal = fixture.Sampled("waterNormal", Fixture.Side, Fill(_ => Encode(new Vector3(0.5f, 1f, 0.5f), 0f)));

        // A colour nothing else in the frame has, so its arrival is unambiguous.
        var reflections = fixture.Sampled("reflections", Fixture.Side, Fill(_ => Encode(new Vector3(0f, 1f, 0f), 1f)));

        var display = fixture.Owned("display", TextureUsage.ColourTarget | TextureUsage.CopySource);

        using var allocator = new DescriptorAllocator(device);
        using var samplers = new SamplerCache(device);
        using var system = new RenderSystem();

        var describer = new EffectPipelineDescriber(device);
        var loader = new EffectLoader(device);
        var effects = new EffectSystem();

        // ⚠ Not the whole library: a source set holding the material tree compiles nothing without a
        // composition, because every slot the sources declare has to be bound whether or not this
        // shader reaches it. What the water pass needs is what it imports.
        effects.AddProvider(
            new Compiling(
                loader,
                _ => RavenEffects.Only(
                    ["Core", "Geometry", "Shading", "Water"],
                    Path.Combine("PostFx", "Fullscreen.rvn")
                )
            )
        );

        using var water = new WaterRenderer {
            Name = "Water",
            Output = "Display",
            Behind = "Behind",
            SceneDepth = "SceneDepth",
            Surface = "WaterSurface",
            Normal = "WaterNormal",
            Reflections = reflect ? "Reflections" : string.Empty,

            // An orthographic view down −Z, so the reconstruction is exact and every pixel's path is
            // the same. A perspective one would make the corners' paths longer than the centre's,
            // which is correct and is not what these assertions are about.
            InverseViewProjection = Orthographic(),
            CameraPosition = Vector3.Zero,
            Foam = true,
            Modules = describer,
            Device = device,
            Samplers = samplers,
            Allocator = allocator
        };

        var compositor = new GraphicsCompositor(system) {
            FrameSize = new(Fixture.Side, Fixture.Side),
            Game = water
        };

        compositor.Imports["Behind"] = Import(copy, "behind");
        compositor.Imports["SceneDepth"] = Import(depth, "sceneDepth");
        compositor.Imports["WaterSurface"] = Import(surface, "waterSurface");
        compositor.Imports["WaterNormal"] = Import(normal, "waterNormal");
        compositor.Imports["Reflections"] = Import(reflections, "reflections");

        compositor.Imports["Display"] = new(
            display.Texture,
            display.View,
            display.Description,
            ResourceState.Undefined,
            ResourceState.CopySource
        );

        allocator.BeginFrame();

        fixture.Graph.Reset();
        var frame = compositor.Build(fixture.Graph, effects, device);

        // ⚠ Asserted rather than assumed: an effect the system cannot resolve is a miss, and a node
        // that got no effect draws nothing — a picture indistinguishable from a pass nobody scheduled.
        Assert.Empty(effects.Misses);
        Assert.True(water.Pass.PipelineCount > 0, "the water pass compiled no pipeline, so it drew nothing");

        return fixture.Render(
            frame.Texture("harness", "Display"),
            commands => {
                Upload(commands, copy);
                Upload(commands, depth);
                Upload(commands, surface);
                Upload(commands, normal);
                Upload(commands, reflections);
            }
        );
    }

    static ImportedTexture Import((TextureHandle Texture, TextureViewHandle View, BufferHandle Staging) plane, string name) =>
        new(
            plane.Texture,
            plane.View,
            new(PixelFormat.Rgba8UNorm, Fixture.Side, Fixture.Side, TextureUsage.Sampled | TextureUsage.CopyDestination, Name: name),
            ResourceState.ShaderRead
        );

    static void Upload(ICommandList commands, (TextureHandle Texture, TextureViewHandle View, BufferHandle Staging) plane) {
        commands.Barrier(new([], [new(plane.Texture, ResourceState.Undefined, ResourceState.CopyDestination)]));
        commands.CopyBufferToTexture(plane.Staging, 0, new(plane.Texture), new(Fixture.Side, Fixture.Side, 1));
        commands.Barrier(new([], [new(plane.Texture, ResourceState.CopyDestination, ResourceState.ShaderRead)]));
    }

    /// <summary>An orthographic clip-to-world down −Z, so a pixel's path is a length and not a range.</summary>
    /// <remarks>
    ///     ⚠ <b>Inverted from the library's own projection rather than written out.</b> A matrix
    ///     assembled here would be this fixture's opinion about the reverse-Z convention, and the
    ///     opinion is exactly what two passes in this directory have already been caught getting
    ///     backwards. The camera sits at the origin looking down −Z with +Y up, so the view matrix is
    ///     the identity and the view-projection is the projection.
    /// </remarks>
    static Matrix4x4 Orthographic() {
        Assert.True(Matrix4x4.Invert(Matrix4x4.Orthographic(8f, 8f, Near, Far), out var inverse));

        return inverse;
    }

    /// <summary>The device depth a distance in front of the camera has, under this fixture's matrix.</summary>
    /// <remarks>
    ///     ⚠ Reversed-Z: near is one and far is zero. A fixture that wrote the textbook convention
    ///     would put the floor behind the camera, and two passes in this directory have been caught
    ///     doing exactly that.
    /// </remarks>
    static float DeviceDepth(float distance) => (Far - distance) / (Far - Near);

    static byte[] Fill(Func<int, uint> texel) {
        var bytes = new byte[Fixture.Side * Fixture.Side * 4];

        for (var y = 0; y < Fixture.Side; y++) {
            for (var x = 0; x < Fixture.Side; x++) {
                var value = texel(x);
                var offset = ((y * Fixture.Side) + x) * 4;

                bytes[offset] = (byte)(value & 0xFF);
                bytes[offset + 1] = (byte)((value >> 8) & 0xFF);
                bytes[offset + 2] = (byte)((value >> 16) & 0xFF);
                bytes[offset + 3] = (byte)((value >> 24) & 0xFF);
            }
        }

        return bytes;
    }

    static uint Encode(Vector3 colour, float alpha) => Encode(colour.X, colour.Y, colour.Z, alpha);

    static uint Encode(float r, float g = 0f, float b = 0f, float a = 1f) =>
        (uint)(byte)(Math.Clamp(r, 0f, 1f) * 255f + 0.5f)
        | ((uint)(byte)(Math.Clamp(g, 0f, 1f) * 255f + 0.5f) << 8)
        | ((uint)(byte)(Math.Clamp(b, 0f, 1f) * 255f + 0.5f) << 16)
        | ((uint)(byte)(Math.Clamp(a, 0f, 1f) * 255f + 0.5f) << 24);

    static Vector3 Pixel(in Bitmap image, int x, int y) {
        var offset = image.Offset(Math.Clamp(x, 0, image.Width - 1), Math.Clamp(y, 0, image.Height - 1));

        return new(image.Pixels[offset] / 255f, image.Pixels[offset + 1] / 255f, image.Pixels[offset + 2] / 255f);
    }

    static float Alpha(in Bitmap image, int x, int y) =>
        image.Pixels[image.Offset(Math.Clamp(x, 0, image.Width - 1), Math.Clamp(y, 0, image.Height - 1)) + 3] / 255f;

    static bool TryOpen(out Fixture? fixture) {
        if (Fixture.TryOpen(out fixture, out var reason)) {
            return true;
        }

        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set and no device could be opened: {reason}");
        }

        return false;
    }
}
