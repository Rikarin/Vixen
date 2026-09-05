// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics.Vulkan;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.DistanceFields;
using Vixen.Rendering.Lighting;
using Vixen.Rendering.SurfaceCache;
using Vixen.Shaders;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>The node that keeps the cache, run as frames — capture, light, bounce, swap, publish.</summary>
/// <remarks>
///     Each mode is held to the closed forms the passes it schedules already pinned: a sun overhead
///     at irradiance π writes a direct term of one, a gather under a uniform sky is that sky, and a
///     capture decodes one frame after it records. What these facts add is the <i>ordering</i> —
///     that the node runs the right passes in the right order with the right authors, frame after
///     frame, which is exactly the class of mistake a node exists to make impossible.
/// </remarks>
[Collection("Vulkan")]
public sealed class SurfaceCacheRendererDeviceTests {
    [Fact]
    public void TheCpuModeLightsBouncesAndUploadsEachFrame() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        var device = owned.Device;

        var store = Captured();

        using var node = new SurfaceCacheRenderer {
            Name = "SurfaceCache",
            Store = store,
            Radiosity = new CardRadiosity(new Floor()) { Sky = _ => new(0.6f) },
            TowardSun = new(0f, 1f, 0f),
            SunIrradiance = new(MathF.PI),
            Device = device
        };

        var effects = new EffectSystem();

        Run(owned, node, effects, frames: 2);

        Assert.Equal(2, node.Bounces);
        Assert.Equal(2, node.Texture!.Uploads);

        // The CPU pair's own closed forms, reached through the node's scheduling.
        Assert.Equal(1f, store.Direct(0, new(2, 2)).X, 1e-4f);
        Assert.Equal(0.6f, store.Gathered(0, new(1, 1)).X, 1e-4f);

        // And the second gather changed nothing: a lone floor under a constant sky is converged.
        Assert.Equal(0f, node.LastChange, 1e-4f);

        // The upload carried the CPU answer: the direct plane reads back as the closed form.
        Assert.Equal(1f, ReadDirect(owned, node)[At(store, 2, 2)].X, 1e-4f);
    }

    [Fact]
    public void TheDispatchModeAuthorsThePlanesAndTheSwapAdvances() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        var device = owned.Device;

        var store = Captured();

        using var allocator = new DescriptorAllocator(device);

        var loader = new EffectLoader(device);
        var effects = new EffectSystem();

        effects.AddProvider(new Compiling(loader, _ => RavenEffects.Only(["Core", "DistanceFields", "SurfaceCache"])));

        var pipelines = new ComputePipelineCache(device);

        using var light = new SurfaceCacheLightFill(device) {
            Effects = effects,
            Pipelines = pipelines,
            Descriptors = allocator,
            SunIrradiance = new(MathF.PI)
        };

        using var gather = new SurfaceCacheGatherFill(device) {
            Effects = effects,
            Pipelines = pipelines,
            Descriptors = allocator,
            SkyColour = new(0.6f)
        };

        using var node = new SurfaceCacheRenderer {
            Name = "SurfaceCache",
            Store = store,
            LightFill = light,
            GatherFill = gather,
            Device = device
        };

        Run(owned, node, effects, frames: 2, allocator);

        Assert.Null(light.Skipped);
        Assert.Null(gather.Skipped);
        Assert.Empty(effects.Misses);
        Assert.Equal(2, node.Bounces);

        // The dispatches authored the planes: direct is the closed form, and the bounce the node
        // swapped forward is the sky — read off the front, which one more host-side swap exposes
        // as the back the readback path copies.
        Assert.Equal(1f, ReadDirect(owned, node)[At(store, 2, 2)].X, 1e-4f);

        node.Texture!.SwapGather();

        var gathered = new Vector4[store.Atlas.Size.X * store.Atlas.Size.Y];

        VulkanDiagnostics.Reset();
        device.BeginFrame();

        using (var commands = device.BeginCommandList(QueueKind.Graphics, "read-gather")) {
            Assert.True(node.Texture.RecordGatherReadback(commands));
            commands.Finish();
            device.GraphicsQueue.Submit([commands]);
        }

        device.EndFrame();
        device.WaitIdle();
        AssertClean();

        Assert.True(node.Texture.TryReadGather(gathered));
        Assert.Equal(0.6f, gathered[At(store, 1, 1)].X, 1e-4f);
    }

    [Fact]
    public void TheCaptureFeedsTheStoreOneCardAFrameOneFrameLate() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        var device = owned.Device;

        // An uncaptured card: every texel invalid until the node's capture feeds it.
        var store = new SurfaceCacheStore(new SurfaceCacheAtlas(new(16, 16)));
        var card = store.AddCard(new(2, new(0f, 0f, 0f), new(1f, 0.2f, 1f), new(8, 8)));

        Assert.False(store.IsValid(card, new(0, 0)));

        var lines = owned.Lines();

        var pipeline = owned.Pipeline(
            lines.Vertex,
            lines.Fragment,
            BlendState.Opaque,
            new DepthStencilState { DepthTest = true, DepthWrite = true, DepthCompare = CompareFunction.Greater },
            [new VertexBufferLayout(Vertex.Stride, [new(0, VertexFormat.Float32X3, 0), new(1, VertexFormat.Float32X4, 12)])],
            pushConstantBytes: 64,
            rasterizer: RasterizerState.TwoSided,
            targets: [new ColourTargetState(PixelFormat.Rgba32Float, BlendState.Opaque)]
        );

        var quads = new Dictionary<SurfaceCapturePlane, BufferHandle> {
            [SurfaceCapturePlane.Albedo] = owned.Buffer<Vertex>(Quad(new(0.7f, 0.7f, 0.7f)), BufferUsage.Vertex),
            [SurfaceCapturePlane.Normal] = owned.Buffer<Vertex>(Quad(new(0f, 1f, 0f)), BufferUsage.Vertex),
            [SurfaceCapturePlane.Emissive] = owned.Buffer<Vertex>(Quad(Vector3.Zero), BufferUsage.Vertex)
        };

        using var capture = new SurfaceCardCapture(device) { MaxResolution = 16 };

        using var node = new SurfaceCacheRenderer {
            Name = "SurfaceCache",
            Store = store,
            Capture = capture,
            Draw = (list, plane, viewProjection) => {
                list.BindPipeline(pipeline);
                list.PushConstants(ShaderStage.Vertex, 0, MemoryMarshal.AsBytes([viewProjection]));
                list.BindVertexBuffer(0, quads[plane]);
                list.Draw(6, 1);
            },
            Device = device
        };

        var effects = new EffectSystem();

        Run(owned, node, effects, frames: 1);

        // One frame in: recorded, not yet readable — the latency is the contract, not a bug.
        Assert.Equal(0, node.Captured);
        Assert.False(store.IsValid(card, new(0, 0)));

        Run(owned, node, effects, frames: 1);

        // Two frames in: last frame's record decoded, the whole card valid, wearing the draw.
        Assert.Equal(64, node.Captured);
        Assert.True(store.IsValid(card, new(3, 3)));
        Assert.Equal(0.7f, store.Surface(card, new(3, 3)).Albedo.X, 1e-5f);
        Assert.Equal(0.2f, store.Surface(card, new(3, 3)).Depth, 1e-4f);
    }

    [Fact]
    public void TwoAuthorsAreRefused() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        var device = owned.Device;

        using var light = new SurfaceCacheLightFill(device);

        using var node = new SurfaceCacheRenderer {
            Name = "SurfaceCache",
            Store = Captured(),
            Radiosity = new CardRadiosity(new Floor()),
            LightFill = light,
            Device = device
        };

        using var system = new RenderSystem();

        var compositor = new GraphicsCompositor(system) {
            FrameSize = new(Fixture.Side, Fixture.Side),
            Game = new SceneRendererSequence { Children = { node } }
        };

        owned.Graph.Reset();

        Assert.Throws<InvalidOperationException>(() => compositor.Build(owned.Graph, new EffectSystem(), device));

        owned.Graph.Reset();
    }

    // --- The frame -----------------------------------------------------------

    /// <summary>Builds and executes the node's frame, without a picture — the pass is a side effect.</summary>
    static void Run(
        Fixture fixture,
        SurfaceCacheRenderer node,
        EffectSystem effects,
        int frames,
        DescriptorAllocator? allocator = null
    ) {
        var device = fixture.Device;

        using var system = new RenderSystem();

        var compositor = new GraphicsCompositor(system) {
            FrameSize = new(Fixture.Side, Fixture.Side),
            Game = new SceneRendererSequence { Children = { node } }
        };

        for (var frame = 0; frame < frames; frame++) {
            allocator?.BeginFrame();
            fixture.Graph.Reset();
            compositor.Build(fixture.Graph, effects, device);

            VulkanDiagnostics.Reset();
            device.BeginFrame();

            using (var commands = device.BeginCommandList(QueueKind.Graphics, "surface-cache-frame")) {
                fixture.Graph.Execute(commands);
                commands.Finish();
                device.GraphicsQueue.Submit([commands]);
            }

            device.EndFrame();
            device.WaitIdle();
            AssertClean();
        }

        fixture.Graph.Reset();
    }

    /// <summary>Reads the direct plane back after the frames have settled.</summary>
    static Vector4[] ReadDirect(Fixture fixture, SurfaceCacheRenderer node) {
        var device = fixture.Device;
        var texture = node.Texture!;
        var texels = new Vector4[texture.Store.Atlas.Size.X * texture.Store.Atlas.Size.Y];

        VulkanDiagnostics.Reset();
        device.BeginFrame();

        using (var commands = device.BeginCommandList(QueueKind.Graphics, "read-direct")) {
            Assert.True(texture.RecordDirectReadback(commands));
            commands.Finish();
            device.GraphicsQueue.Submit([commands]);
        }

        device.EndFrame();
        device.WaitIdle();
        AssertClean();

        Assert.True(texture.TryReadDirect(texels));

        return texels;
    }

    /// <summary>A floor card, captured by the traced reference — the store every mode starts from.</summary>
    static SurfaceCacheStore Captured() {
        var store = new SurfaceCacheStore(new SurfaceCacheAtlas(new(16, 16)));
        var card = store.AddCard(new(2, new(0f, 0f, 0f), new(1f, 0.2f, 1f), new(8, 8)));

        Assert.Equal(64, new TracedCardCapture(new Floor(), new Paint()).Capture(store, card));

        return store;
    }

    static int At(SurfaceCacheStore store, int x, int y) {
        var origin = store.Cards[0].Origin;

        return ((origin.Y + y) * store.Atlas.Size.X) + origin.X + x;
    }

    /// <summary>The whole floor as two triangles, wider than the card.</summary>
    static Vertex[] Quad(Vector3 colour) {
        Vector3 a = new(-1.5f, 0f, -1.5f);
        Vector3 b = new(1.5f, 0f, -1.5f);
        Vector3 c = new(-1.5f, 0f, 1.5f);
        Vector3 d = new(1.5f, 0f, 1.5f);

        return [
            new(a, colour), new(b, colour), new(c, colour),
            new(c, colour), new(b, colour), new(d, colour)
        ];
    }

    sealed class Floor : IDistanceField {
        public float Sample(Vector3 position) => position.Y;

        public Vector3 SampleGradient(Vector3 position) => new(0f, 1f, 0f);
    }

    sealed class Paint : ISurfaceMaterial {
        public Vector3 Albedo(Vector3 position, Vector3 normal) => new(0.7f);

        public Vector3 Emissive(Vector3 position, Vector3 normal) => Vector3.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Vertex(Vector3 position, Vector3 colour) {
        public const int Stride = 28;

        public Vector3 Position = position;
        public Vector4 Colour = new(colour, 1f);
    }

    static void AssertClean() {
        if (VulkanDiagnostics.ErrorCount > 0) {
            Assert.Fail(
                "The run produced validation errors, so what it wrote is meaningless: "
                + string.Join(Environment.NewLine, VulkanDiagnostics.Messages)
            );
        }
    }

    static bool TryOpen(out Fixture? fixture) {
        if (Fixture.TryOpen(out fixture, out var reason)) {
            return true;
        }

        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set and no device could be opened: {reason}");
        }

        Assert.Skip(reason ?? "no Vulkan device is available");

        return false;
    }
}
