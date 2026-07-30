// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics.Vulkan;
using Vixen.Ui.Testing.Visual;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Lighting;
using Vixen.Rendering.PostFx;
using Vixen.Shaders;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>The whole gather scheduled by one node, over frames that feed each other.</summary>
/// <remarks>
///     <para>
///         The other image test seeds the atlas by hand and draws the upsample alone; this one hands
///         <see cref="ScreenProbeGatherRenderer" /> a G-buffer and a camera and asks for frames. The
///         first frame has no placement — the depth its probes stand on has not come back yet — and
///         must be honestly dark. The second places probes from the first frame's depth,
///         traces them under a uniform sky, resolves and upsamples in the same graph, and the flat
///         frame of the sky's radiance is the closed form again, now with nothing done by hand.
///     </para>
///     <para>
///         The camera is the CPU placement tests' orthographic one, deliberately: under a uniform sky
///         the traced answer does not depend on where a probe stands, so what this frame checks is
///         the <i>schedule</i> — placement ran, validity flowed, the passes ran in order — with the
///         positions themselves pinned by the closed forms one package down.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public sealed class ScreenProbeGatherImageTests {
    const float Radiance = 0.75f;

    [Fact]
    public void TheSecondFrameLightsUpAndTheFirstIsHonestlyDark() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        var pictures = Render(owned, 0.5f, frames: 3, out var node);

        // Frame zero traced nothing: its placement data was still on the device.
        Assert.True(Pixel(pictures[0], 16, 16).X < 0.02f, $"the first frame was lit: {Pixel(pictures[0], 16, 16)}");

        // Frame one placed from frame zero's depth — every probe found the surface — and the sky's
        // radiance crossed placement, trace, resolve, upsample and the graph in one schedule.
        Assert.Equal(node.Probes, node.PlacedSeen);

        foreach (var picture in pictures[1..]) {
            var centre = Pixel(picture, 16, 16);
            var corner = Pixel(picture, 2, 2);

            Assert.Equal(Radiance, centre.X, 0.02f);
            Assert.Equal(Radiance, centre.Y, 0.02f);
            Assert.Equal(Radiance, centre.Z, 0.02f);
            Assert.Equal(Radiance, corner.X, 0.02f);
        }

        Assert.Null(node.TraceSkippedSeen);
        Assert.Null(node.ResolveSkippedSeen);
    }

    [Fact]
    public void ASkyOnlyFrameStaysDark() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        var pictures = Render(owned, 0f, frames: 3, out var node);

        // Placement ran and found nothing to stand on — dark because every probe is invalid, not
        // because the schedule never happened.
        Assert.True(node.Placements > 0, "placement never ran, so this dark frame proves nothing");
        Assert.Equal(0, node.PlacedSeen);
        Assert.True(Pixel(pictures[^1], 16, 16).X < 0.02f, $"the sky was lit: {Pixel(pictures[^1], 16, 16)}");
    }

    sealed record Observed(int Probes, int PlacedSeen, int Placements, string? TraceSkippedSeen, string? ResolveSkippedSeen);

    static Bitmap[] Render(Fixture fixture, float clearDepth, int frames, out Observed observed) {
        var device = fixture.Device;

        using var allocator = new DescriptorAllocator(device);
        using var samplers = new SamplerCache(device);
        var pipelines = new ComputePipelineCache(device);
        using var system = new RenderSystem();

        var loader = new EffectLoader(device);
        var effects = new EffectSystem();

        effects.AddProvider(
            new Compiling(
                loader,
                _ => RavenEffects.Only(
                    ["Core", "Geometry", "Shading", "DistanceFields", "IrradianceFields", "ScreenProbes"],
                    Path.Combine("PostFx", "Fullscreen.rvn"),
                    Path.Combine("PostFx", "ScreenProbeUpsample.rvn")
                )
            )
        );

        using var tracer = new ScreenProbeTraceFill(device) {
            Effects = effects,
            Pipelines = pipelines,
            Descriptors = allocator,
            SkyColour = new(Radiance)
        };

        using var resolver = new ScreenProbeResolve(device) {
            Effects = effects,
            Pipelines = pipelines,
            Descriptors = allocator
        };

        // The CPU placement tests' camera: reconstruction under it is pinned by hand over there,
        // and under a uniform sky nothing here depends on the positions it produces.
        Assert.True(Matrix4x4.Invert(Matrix4x4.Orthographic(4f, 4f, 1f, 9f), out var inverse));

        using var node = new ScreenProbeGatherRenderer {
            Name = "ScreenProbes",
            Depth = "Depth",
            Normals = "Normals",
            Output = "Display",
            Samplers = samplers,
            Allocator = allocator,
            Tracer = tracer,
            Resolver = resolver,
            InverseViewProjection = inverse,

            // One frame of latency, because this loop waits the device idle every frame.
            Latency = 1
        };

        // Depth in .r and a +Z normal out of one clear, the arrangement every image test here uses.
        var gbuffer = new RenderPassRenderer { Name = "GBuffer", ClearColour = new(clearDepth, clearDepth, 1f, 1f) };

        gbuffer.ColourTargets.Add("Depth");
        gbuffer.ColourTargets.Add("Normals");

        var compositor = new GraphicsCompositor(system) {
            FrameSize = new(Fixture.Side, Fixture.Side),
            Game = new SceneRendererSequence { Children = { gbuffer, node } }
        };

        // Float targets, because placement reads these back — and CopySource, because that is the
        // readback. Sampled is the upsample's own tap of them.
        var gbufferUsage = TextureUsage.ColourTarget | TextureUsage.Sampled | TextureUsage.CopySource;
        var depth = fixture.Owned("depth", gbufferUsage, PixelFormat.Rgba32Float);
        var normals = fixture.Owned("normals", gbufferUsage, PixelFormat.Rgba32Float);
        var display = fixture.Owned("display", TextureUsage.ColourTarget | TextureUsage.CopySource);

        compositor.Imports["Depth"] = new(depth.Texture, depth.View, depth.Description);
        compositor.Imports["Normals"] = new(normals.Texture, normals.View, normals.Description);

        compositor.Imports["Display"] = new(
            display.Texture,
            display.View,
            display.Description,
            ResourceState.Undefined,
            ResourceState.CopySource
        );

        var pictures = new Bitmap[frames];

        for (var index = 0; index < frames; index++) {
            fixture.Graph.Reset();
            allocator.BeginFrame();

            var frame = compositor.Build(fixture.Graph, effects, device);

            Assert.Empty(effects.Misses);

            pictures[index] = fixture.Render(frame.Texture("harness", "Display"));
        }

        Assert.True(node.Upsample!.Pass.PipelineCount > 0, "the upsample compiled no pipeline, so it drew nothing");

        observed = new(
            node.Texture!.Probes.Layout.ProbeCount,
            node.Placed,
            node.Placements,
            node.TraceSkipped,
            node.ResolveSkipped
        );

        return pictures;
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
