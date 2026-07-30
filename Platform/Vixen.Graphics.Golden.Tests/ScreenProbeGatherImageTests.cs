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

    /// <summary>The screen trace wired through the node changes what the probes see.</summary>
    /// <remarks>
    ///     A wiring assertion, not a closed form — the arithmetic is pinned texel by texel in
    ///     <c>ScreenProbeTraceDeviceTests</c>. Every probe stands on the one flat surface the frame
    ///     drew, so the screen trace blackens a cone of directions behind each probe — and the
    ///     surface's own normal faces <i>away</i> from that cone, so the frame lands <i>above</i>
    ///     the sky, not below it: the L1 truncation's overshoot, doc 19 § L3's "beside an occluder
    ///     the away-facing answer overshoots the sky", drawn by a whole frame. The traceless frame
    ///     is the sky exactly, so anything but the sky here is the screen trace's doing.
    /// </remarks>
    [Fact]
    public void TheScreenTraceDarkensWhatItStops() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        var pictures = Render(owned, 0.5f, frames: 3, out var node, screenTraces: true);

        var centre = Pixel(pictures[^1], 16, 16);

        Assert.Null(node.TraceSkippedSeen);
        Assert.True(centre.X > Radiance + 0.03f, $"the screen trace stopped nothing: {centre}");
        Assert.True(centre.X < 1f, $"the overshoot has no ceiling: {centre}");
    }

    /// <summary>The accumulated and filtered path draws the same flat frame a raw resolve draws.</summary>
    /// <remarks>
    ///     The running mean of a constant is the constant and a spatial filter of a uniform field is
    ///     the field — so routing the upsample through the whole denoise chain must change nothing
    ///     about this picture, to the tolerance, while the arithmetic itself is pinned against the
    ///     CPU in <c>ScreenProbeAccumulateDeviceTests</c>. A wrong swap, a stale set, a mismatched
    ///     camera or an unwritten filtered plane all show here as a dark or half-dark frame, because
    ///     the first accumulation keeps nothing.
    /// </remarks>
    [Fact]
    public void TheDenoisedPathDrawsTheSameClosedForm() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        var pictures = Render(owned, 0.5f, frames: 4, out var node, accumulate: true);

        Assert.Null(node.AccumulateSkippedSeen);
        Assert.Null(node.FilterSkippedSeen);

        var centre = Pixel(pictures[^1], 16, 16);

        Assert.Equal(Radiance, centre.X, 0.02f);
        Assert.Equal(Radiance, centre.Y, 0.02f);
        Assert.Equal(Radiance, centre.Z, 0.02f);
    }

    /// <summary>The bilateral taps accept a whole flat frame — and reject everything gracefully.</summary>
    /// <remarks>
    ///     One surface, so every probe's plane holds every pixel: the bilateral picture is the
    ///     bilinear picture exactly, which proves the surface planes are bound and read without
    ///     changing the closed form. A tolerance too tight for the frame's own float noise then
    ///     rejects every tap, and the fallback hands back the unfiltered blend rather than a black
    ///     hole — the CPU overload's own last resort, drawn. The discriminating case — a depth edge
    ///     the plane test must not bleed across — needs real geometry in the frame, and is owed with
    ///     the first lit-scene fixture.
    /// </remarks>
    [Fact]
    public void TheBilateralTapsKeepTheFlatFrame() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        var pictures = Render(owned, 0.5f, frames: 4, out var node, accumulate: true, planeTolerance: 0.5f);

        Assert.Null(node.FilterSkippedSeen);

        var centre = Pixel(pictures[^1], 16, 16);

        Assert.Equal(Radiance, centre.X, 0.02f);
        Assert.Equal(Radiance, centre.Y, 0.02f);
        Assert.Equal(Radiance, centre.Z, 0.02f);
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

    /// <summary>A resized frame is refused until the host says so, and honoured once it does.</summary>
    /// <remarks>
    ///     Build-only, deliberately — the point under test is the lattice's lifecycle, not a picture.
    ///     Rebuilding textures mid-flight while frames still reference them is a use-after-free with
    ///     latency, so the refusal is loud; <c>Reset</c> after an idle is the sanctioned path, and
    ///     it starts the temporal chain over, because a resize is a camera cut.
    /// </remarks>
    [Fact]
    public void AResizeIsADeliberateStep() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        var device = owned.Device;

        using var allocator = new DescriptorAllocator(device);
        using var samplers = new SamplerCache(device);
        using var system = new RenderSystem();

        var effects = new EffectSystem();

        using var node = new ScreenProbeGatherRenderer {
            Name = "ScreenProbes",
            Depth = "Depth",
            Normals = "Normals",
            Output = "Display",
            Samplers = samplers,
            Allocator = allocator
        };

        var gbufferUsage = TextureUsage.ColourTarget | TextureUsage.Sampled | TextureUsage.CopySource;

        Bitmapless(owned, system, node, Fixture.Side, gbufferUsage, effects, device);

        Assert.Equal(new Int2(Fixture.Side, Fixture.Side), node.Texture!.Probes.Layout.Viewport);

        // The window shrank: refused, loudly, until the host resets after an idle.
        Assert.Throws<InvalidOperationException>(
            () => Bitmapless(owned, system, node, 64, gbufferUsage, effects, device)
        );

        device.WaitIdle();
        node.Reset();

        Bitmapless(owned, system, node, 64, gbufferUsage, effects, device);

        Assert.Equal(new Int2(64, 64), node.Texture!.Probes.Layout.Viewport);
        Assert.Equal(0, node.Placements);
    }

    /// <summary>Builds one frame at a size without executing it — the lifecycle, not the picture.</summary>
    static void Bitmapless(
        Fixture fixture,
        RenderSystem system,
        ScreenProbeGatherRenderer node,
        int side,
        TextureUsage gbufferUsage,
        EffectSystem effects,
        Vulkan.VulkanDevice device
    ) {
        var depth = fixture.Owned($"depth {side}", gbufferUsage, PixelFormat.Rgba32Float, side, side);
        var normals = fixture.Owned($"normals {side}", gbufferUsage, PixelFormat.Rgba32Float, side, side);
        var display = fixture.Owned($"display {side}", TextureUsage.ColourTarget | TextureUsage.CopySource, PixelFormat.Rgba8UNorm, side, side);

        var compositor = new GraphicsCompositor(system) {
            FrameSize = new(side, side),
            Game = new SceneRendererSequence { Children = { node } }
        };

        compositor.Imports["Depth"] = new(depth.Texture, depth.View, depth.Description);
        compositor.Imports["Normals"] = new(normals.Texture, normals.View, normals.Description);
        compositor.Imports["Display"] = new(display.Texture, display.View, display.Description);

        fixture.Graph.Reset();
        compositor.Build(fixture.Graph, effects, device);
        fixture.Graph.Reset();
    }

    sealed record Observed(
        int Probes,
        int PlacedSeen,
        int Placements,
        string? TraceSkippedSeen,
        string? ResolveSkippedSeen,
        string? AccumulateSkippedSeen,
        string? FilterSkippedSeen
    );

    static Bitmap[] Render(
        Fixture fixture,
        float clearDepth,
        int frames,
        out Observed observed,
        bool screenTraces = false,
        bool accumulate = false,
        float planeTolerance = 0f
    ) {
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
                    ["Core", "Geometry", "Shading", "DistanceFields", "IrradianceFields", "ScreenProbes", "SurfaceCache"],
                    Path.Combine("PostFx", "Fullscreen.rvn"),
                    Path.Combine("PostFx", "ScreenProbeUpsample.rvn")
                )
            )
        );

        // Eight units of ray, matching the screen-trace closed forms: at the default hundred, a
        // fixed-step march samples too coarsely to land in a surface's shell at all.
        using var tracer = new ScreenProbeTraceFill(device) {
            Effects = effects,
            Pipelines = pipelines,
            Descriptors = allocator,
            SkyColour = new(Radiance),
            MaxDistance = 8f
        };

        using var resolver = new ScreenProbeResolve(device) {
            Effects = effects,
            Pipelines = pipelines,
            Descriptors = allocator
        };

        using var accumulator = new ScreenProbeAccumulateFill(device) {
            Effects = effects,
            Pipelines = pipelines,
            Descriptors = allocator
        };

        using var spatial = new ScreenProbeFilterFill(device) {
            Effects = effects,
            Pipelines = pipelines,
            Descriptors = allocator
        };

        // The CPU placement tests' camera: reconstruction under it is pinned by hand over there,
        // and under a uniform sky nothing here depends on the positions it produces.
        var camera = Matrix4x4.Orthographic(4f, 4f, 1f, 9f);

        Assert.True(Matrix4x4.Invert(camera, out var inverse));

        using var node = new ScreenProbeGatherRenderer {
            Name = "ScreenProbes",
            Depth = "Depth",
            Normals = "Normals",
            Output = "Display",
            Samplers = samplers,
            Allocator = allocator,
            Tracer = tracer,
            Resolver = resolver,
            Accumulator = accumulate ? accumulator : null,
            SpatialFilter = accumulate ? spatial : null,
            PlaneTolerance = planeTolerance,
            InverseViewProjection = inverse,
            ViewProjection = camera,
            ScreenTraces = screenTraces,

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
            node.ResolveSkipped,
            node.AccumulateSkipped,
            node.FilterSkipped
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
