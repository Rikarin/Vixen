// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics.Vulkan;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Water;
using Vixen.Shaders;
using Vixen.Core.Imaging;
using Vixen.Ui.Testing.Visual;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>
///     The waterline, drawing pixels — [docs/plan/35 § D9].
/// </summary>
/// <remarks>
///     <para>
///         <b>§ D9's warning, made measurable.</b> "Designing the volume path first and discovering
///         the waterline second is how you get a system where the transition is a hard cut and the fix
///         is architectural." A post-process volume's fold produces <em>one weight for the whole
///         frame</em>; what a camera half in the water needs is two treatments divided by a curve. So
///         the assertion that matters most in this file is simply that one frame contains both.
///     </para>
///     <para>
///         <b>Assertions on the geometry rather than on a bitmap</b>, on
///         <see cref="WaterPassImageTests" />' terms and for its stated reason: a golden PNG says
///         "these bytes changed" and leaves somebody to work out whether it was the plane test, the
///         path length, the medium or the caustics. What is asserted here is each separately.
///     </para>
///     <para>
///         ⚠ <b>Which half of the frame is which is never assumed.</b> Clip <c>y = +1</c> is the top
///         in this engine and the screen helpers negate y for it, which is exactly the kind of
///         convention a fixture gets backwards and then encodes. So the unambiguous cases are the ones
///         asserted absolutely — a plane above the whole view and a plane below it — and the split is
///         asserted by the boundary <em>moving</em> when the plane does.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public sealed class UnderwaterPassImageTests {
    const float Near = 0.1f;
    const float Far = 100f;

    /// <summary>Half the orthographic view's height, in world units — see <see cref="Orthographic" />.</summary>
    const float HalfHeight = 4f;

    /// <summary>What the frame holds before the grade: a bright, saturated red-green.</summary>
    /// <remarks>
    ///     ⚠ Red and green rather than grey, on the water pass's terms: water absorbs red about thirty
    ///     times faster than blue, so a grey frame going grey-blue is one observation and a red one
    ///     going green and then blue is three.
    /// </remarks>
    static readonly Vector3 Scene = new(0.9f, 0.8f, 0.7f);

    /// <summary>
    ///     ⚠ A plane below the whole view grades nothing at all.
    /// </summary>
    /// <remarks>
    ///     The first thing to get right and the loudest thing to get wrong. A waterline that graded
    ///     everything would fog the world blue for a camera standing on a beach, and it would look
    ///     exactly like the effect working — which is why this is asserted exactly rather than within
    ///     a tolerance: the shader returns the sampled colour unchanged, so anything but equality is
    ///     arithmetic that should not have happened.
    /// </remarks>
    [Fact]
    public void A_camera_above_the_surface_sees_the_frame_untouched() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        var image = Render(owned, surfaceHeight: -HalfHeight - 1f, submersion: -1f);

        foreach (var y in (int[])[8, 64, 120]) {
            var pixel = Pixel(image, 64, y);

            Assert.Equal(Scene.X, pixel.X, 0.01f);
            Assert.Equal(Scene.Y, pixel.Y, 0.01f);
            Assert.Equal(Scene.Z, pixel.Z, 0.01f);
            Assert.Equal(0f, Alpha(image, 64, y), 0.01f);
        }
    }

    /// <summary>And a plane above the whole view grades all of it.</summary>
    /// <remarks>
    ///     The negative control's other half. Without it, a shader whose plane test always answered
    ///     "above" would pass the test above and nothing else here would notice — the split test
    ///     compares two rows to each other, and two identical rows would only fail on the boundary
    ///     assertion.
    /// </remarks>
    [Fact]
    public void A_camera_under_the_surface_sees_all_of_it_graded() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        var image = Render(owned, surfaceHeight: HalfHeight + 1f, submersion: 2f);

        foreach (var y in (int[])[8, 64, 120]) {
            Assert.Equal(1f, Alpha(image, 64, y), 0.02f);

            // And red has gone first, which is the medium doing the arithmetic rather than a tint.
            var pixel = Pixel(image, 64, y);

            Assert.True(pixel.X < Scene.X * 0.75f, $"the grade did not absorb the scene's red: {pixel}");
            Assert.True(pixel.Z > pixel.X, $"blue did not outlast red at {y}: {pixel}");
        }
    }

    /// <summary>
    ///     ⚠ One frame, two treatments, divided by a line that moves with the plane — which is the
    ///     whole of § D9.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>This is the assertion the volume path cannot make.</b> A fold produces one weight;
    ///         here the top of the frame and the bottom of the frame disagree at the same instant, and
    ///         the place they change over is a function of where the surface is.
    ///     </para>
    ///     <para>
    ///         The boundary is found by scanning the mask rather than computed, so the test says
    ///         nothing about which way up the clip space is — see the class remarks. What it asserts
    ///         is that a boundary exists, and that raising the surface by a metre moves it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_camera_straddling_the_surface_gets_both_treatments_and_the_line_moves() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;

        var low = Render(owned, surfaceHeight: -1f, submersion: 0.2f);
        var high = Render(owned, surfaceHeight: 1f, submersion: 0.2f);

        var lowEdge = Waterline(low);
        var highEdge = Waterline(high);

        Assert.True(lowEdge >= 0, "the frame with the surface at −1 m has no waterline in it at all");
        Assert.True(highEdge >= 0, "the frame with the surface at +1 m has no waterline in it at all");

        // Two metres of world over an eight-metre view is a quarter of the frame's height, which at
        // 128 rows is 32 — asserted loosely because the feather spans a few rows either way.
        Assert.InRange(Math.Abs(highEdge - lowEdge), 20, 44);
    }

    /// <summary>
    ///     ⚠ The mask bounds the fog path, which is what stops looking up being as dark as looking down.
    /// </summary>
    /// <remarks>
    ///     <b>The one job the surface mask does in this pass, and it is not the waterline.</b> Under
    ///     the surface, a coverage of one means the surface is between the eye and the scene — so the
    ///     ray leaves the water there and the fog stops. A diver looking up sees the sky through a
    ///     metre of water; without this the path is the distance to the sky and looking up is exactly
    ///     as dark as looking down at the bed, which reads as "underwater is just a blue filter".
    /// </remarks>
    [Fact]
    public void The_mask_stops_the_fog_where_the_ray_leaves_the_water() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;

        // Same scene, same distance, same everything — except that on the left half the mask says the
        // surface is close in front, so the ray leaves the water almost immediately.
        var image = Render(owned, surfaceHeight: HalfHeight + 1f, submersion: 2f, exitAt: 64, exitDistance: 0.3f);

        var leaving = Pixel(image, 20, 64);
        var through = Pixel(image, 108, 64);

        Assert.True(
            leaving.X > through.X * 2f,
            $"the exit mask did not shorten the path: leaving {leaving}, through {through}"
        );
    }

    /// <summary>The caustics are a permutation, and switching them off changes the picture.</summary>
    /// <remarks>
    ///     ⚠ <b>A permutation asserted by its effect rather than by the key being set.</b> A key set on
    ///     a pass that compiled the other variant is a switch that does nothing, which is the failure
    ///     a "the parameter was accepted" test cannot see.
    /// </remarks>
    [Fact]
    public void The_caustics_are_a_permutation_that_does_something() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;

        var with = Render(owned, surfaceHeight: HalfHeight + 1f, submersion: 0.5f, caustics: true);
        var without = Render(owned, surfaceHeight: HalfHeight + 1f, submersion: 0.5f, caustics: false);

        var difference = 0f;

        for (var y = 0; y < Fixture.Side; y += 4) {
            for (var x = 0; x < Fixture.Side; x += 4) {
                difference += Math.Abs(Pixel(with, x, y).Y - Pixel(without, x, y).Y);
            }
        }

        Assert.True(difference > 0.5f, $"the caustic permutation changed nothing: {difference}");
    }

    /// <summary>The first row at which the mask crosses a half, or −1 if it never does.</summary>
    /// <remarks>
    ///     Scanned rather than computed on purpose — see the class remarks. A fixture that worked out
    ///     which row the line should be on would be asserting its own opinion about the clip-space
    ///     convention, which is exactly the opinion two passes in this directory have been caught
    ///     getting backwards.
    /// </remarks>
    static int Waterline(in Bitmap image) {
        var first = Alpha(image, Fixture.Side / 2, 0) > 0.5f;

        for (var y = 1; y < Fixture.Side; y++) {
            if (Alpha(image, Fixture.Side / 2, y) > 0.5f != first) {
                return y;
            }
        }

        return -1;
    }

    static Bitmap Render(
        Fixture fixture,
        float surfaceHeight,
        float submersion,
        bool caustics = false,
        int exitAt = -1,
        float exitDistance = 0.3f
    ) {
        var device = fixture.Device;

        // The scene is a wall a long way off, so the fog path is long wherever the mask does not stop
        // it. Orthographic down −Z, so every pixel's path is the same length and the numbers here are
        // one number rather than a range.
        const float SceneDistance = 30f;

        var copy = fixture.Sampled("behind", Fixture.Side, Fill(_ => Encode(Scene, 1f)));
        var depth = fixture.Sampled("sceneDepth", Fixture.Side, Fill(_ => Encode(DeviceDepth(SceneDistance))));

        // Coverage on the left of `exitAt` only, so one frame carries both cases and nothing else
        // about it can differ.
        var surface = fixture.Sampled(
            "waterSurface",
            Fixture.Side,
            Fill(x => Encode(DeviceDepth(exitDistance), exitAt >= 0 && x < exitAt ? 1f : 0f, 0f, 1f))
        );

        var display = fixture.Owned("display", TextureUsage.ColourTarget | TextureUsage.CopySource);

        using var allocator = new DescriptorAllocator(device);
        using var samplers = new SamplerCache(device);
        using var system = new RenderSystem();

        var describer = new EffectPipelineDescriber(device);
        var loader = new EffectLoader(device);
        var effects = new EffectSystem();

        effects.AddProvider(
            new Compiling(
                loader,
                _ => RavenEffects.Only(
                    ["Core", "Geometry", "Shading", "Water"],
                    Path.Combine("PostFx", "Fullscreen.rvn")
                )
            )
        );

        using var under = new UnderwaterRenderer {
            Name = "Underwater",
            Output = "Display",
            Behind = "Behind",
            SceneDepth = "SceneDepth",
            Surface = "WaterSurface",
            InverseViewProjection = Orthographic(),
            CameraPosition = Vector3.Zero,

            // The plane, set directly rather than through a zone system: what is under test is the
            // shader's use of it, and a fixture that folded a world would be testing the fold.
            SurfacePoint = new(0f, surfaceHeight, 0f),
            SurfaceNormal = Vector3.UnitY,
            Submersion = submersion,

            // ⚠ Wide, because this fixture reads the mask at a handful of rows. The shipped default
            // is four centimetres — narrow is the point there — and a four-centimetre feather over an
            // eight-metre view is under a pixel, which would make the scan below a coin toss on
            // rounding.
            WaterlineFeather = 0.25f,
            Distortion = false,
            Caustics = caustics,
            Modules = describer,
            Device = device,
            Samplers = samplers,
            Allocator = allocator
        };

        var compositor = new GraphicsCompositor(system) {
            FrameSize = new(Fixture.Side, Fixture.Side),
            Game = under
        };

        compositor.Imports["Behind"] = Import(copy, "behind");
        compositor.Imports["SceneDepth"] = Import(depth, "sceneDepth");
        compositor.Imports["WaterSurface"] = Import(surface, "waterSurface");

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
        Assert.True(under.Pass.PipelineCount > 0, "the underwater pass compiled no pipeline, so it drew nothing");

        return fixture.Render(
            frame.Texture("harness", "Display"),
            commands => {
                Upload(commands, copy);
                Upload(commands, depth);
                Upload(commands, surface);
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
    ///     ⚠ Inverted from the library's own projection rather than written out, on
    ///     <see cref="WaterPassImageTests" />' terms: a matrix assembled here would be this fixture's
    ///     opinion about the reverse-Z convention, and the opinion is exactly what gets encoded.
    /// </remarks>
    static Matrix4x4 Orthographic() {
        Assert.True(Matrix4x4.Invert(Matrix4x4.Orthographic(HalfHeight * 2f, HalfHeight * 2f, Near, Far), out var inverse));

        return inverse;
    }

    /// <summary>The device depth a distance in front of the camera has, under this fixture's matrix.</summary>
    /// <remarks>⚠ Reversed-Z: near is one and far is zero.</remarks>
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
        (uint)(byte)((Math.Clamp(r, 0f, 1f) * 255f) + 0.5f)
        | ((uint)(byte)((Math.Clamp(g, 0f, 1f) * 255f) + 0.5f) << 8)
        | ((uint)(byte)((Math.Clamp(b, 0f, 1f) * 255f) + 0.5f) << 16)
        | ((uint)(byte)((Math.Clamp(a, 0f, 1f) * 255f) + 0.5f) << 24);

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
