// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics.Vulkan;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.DistanceFields;
using Vixen.Rendering.IrradianceFields;
using Vixen.Rendering.Lighting;
using Vixen.Rendering.Materials;
using Vixen.Rendering.PostFx;
using Vixen.Rendering.Reflections;
using Vixen.Rendering.ScreenProbes;
using Vixen.Shaders;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>The reflection node run as a frame: reconstruct, march the screen, publish.</summary>
/// <remarks>
///     The kernel's fixture contract handed positions in; a frame has a depth. This drives the node
///     over an imported depth-and-normals pair under a hand-built orthographic camera, and holds
///     the published answer texel by texel against <c>TracedReflections</c> reading positions off
///     <c>ReconstructedScreenSurface</c> — the same reconstruction, the same march, the same
///     matrices, so the only thing under test is the thing the node adds: the wiring.
/// </remarks>
[Collection("Vulkan")]
public sealed class ReflectionRendererDeviceTests {
    const int Side = 8;

    static readonly Vector3 SkyMiss = new(0.2f, 0.15f, 0.1f);

    [Fact]
    public void TheNodeReconstructsMarchesAndPublishes() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        var device = owned.Device;

        // The camera: top-down orthographic, ndc.x = world.x / 4, ndc.y = world.z / 4, reversed
        // depth = (world.y + 4) / 8 — and its exact inverse, because the host has both.
        var viewProjection = new Matrix4x4(
            new Vector4(0.25f, 0f, 0f, 0f),
            new Vector4(0f, 0f, 0.125f, 0f),
            new Vector4(0f, 0.25f, 0f, 0f),
            new Vector4(0f, 0f, 0.5f, 1f)
        );

        var inverse = new Matrix4x4(
            new Vector4(4f, 0f, 0f, 0f),
            new Vector4(0f, 0f, 4f, 0f),
            new Vector4(0f, 8f, 0f, 0f),
            new Vector4(0f, -4f, 0f, 1f)
        );

        // The frame: a floor at y = 0 filling the view, a ceiling patch at y = 2, and a corner of
        // sky — so reconstruction, a screen hit, a screen miss and an invalid texel all appear.
        var depths = new Vector4[Side * Side];
        var normalPlaneData = new Vector4[Side * Side];
        var colours = new Vector4[Side * Side];

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                var at = (y * Side) + x;
                var ceiling = x is >= 3 and <= 6 && y is >= 2 and <= 5;
                var open = x == 7 && y == 7;

                depths[at] = new(open ? 0f : ceiling ? 0.75f : 0.5f, 0f, 0f, 0f);
                normalPlaneData[at] = new(0f, 1f, 0f, 0f);
                colours[at] = new(0.1f * x, 0.1f * y, 0.3f, 1f);
            }
        }

        using var allocator = new DescriptorAllocator(device);

        var loader = new EffectLoader(device);
        var effects = new EffectSystem();

        effects.AddProvider(
            new Compiling(
                loader,
                _ => RavenEffects.Only(["Core", "Shading", "Geometry", "DistanceFields", "IrradianceFields", "SurfaceCache", "Reflections"])
            )
        );

        var pipelines = new ComputePipelineCache(device);

        using var node = new ReflectionRenderer {
            Name = "Reflections",
            Effects = effects,
            Pipelines = pipelines,
            Allocator = allocator,
            Device = device,
            ViewProjection = viewProjection,
            InverseViewProjection = inverse,
            CameraPosition = new(-4f, 6f, 0f),
            Colour = "SceneColour"
        };

        // The imports the node reads, uploaded before the graph runs.
        var depthPlane = owned.Owned("frame-depth", TextureUsage.Sampled | TextureUsage.CopyDestination, PixelFormat.Rgba32Float, Side, Side);
        var normalPlane = owned.Owned("frame-normals", TextureUsage.Sampled | TextureUsage.CopyDestination, PixelFormat.Rgba32Float, Side, Side);
        var colourPlane = owned.Owned("frame-colour", TextureUsage.Sampled | TextureUsage.CopyDestination, PixelFormat.Rgba32Float, Side, Side);

        var staging = owned.Buffer<Vector4>([.. depths, .. normalPlaneData, .. colours], BufferUsage.CopySource);

        using var system = new RenderSystem();

        var compositor = new GraphicsCompositor(system) {
            FrameSize = new(Side, Side),
            Game = new SceneRendererSequence { Children = { node } }
        };

        compositor.Imports["Depth"] = new(depthPlane.Texture, depthPlane.View, depthPlane.Description, ResourceState.ShaderRead, ResourceState.ShaderRead);
        compositor.Imports["Normals"] = new(normalPlane.Texture, normalPlane.View, normalPlane.Description, ResourceState.ShaderRead, ResourceState.ShaderRead);
        compositor.Imports["SceneColour"] = new(colourPlane.Texture, colourPlane.View, colourPlane.Description, ResourceState.ShaderRead, ResourceState.ShaderRead);

        allocator.BeginFrame();
        owned.Graph.Reset();
        compositor.Build(owned.Graph, effects, device);

        // Between the build and the execution, because the fill exists only after the build: the
        // miss slot's one colour, and a thickness wide enough for the fixture's step length.
        node.Trace!.ScreenThickness = 0.05f;
        node.Trace.MaxDistance = 8f;
        node.Trace.Parameters.Set(
            ParameterKeys.New<Vector3>($"{ReflectionTraceFill.ShaderName}.{MaterialCompiler.SkyReflectionMissShader}.missSkyColor"),
            SkyMiss
        );

        var readback = device.CreateBuffer(
            new BufferDescription(Side * Side * 16, BufferUsage.CopyDestination, MemoryAccess.HostReadback, "node-readback")
        );

        VulkanDiagnostics.Reset();
        device.BeginFrame();

        using (var commands = device.BeginCommandList(QueueKind.Graphics, "reflection-node")) {
            commands.Barrier(
                new(
                    [],
                    [
                        new TextureBarrier(depthPlane.Texture, ResourceState.Undefined, ResourceState.CopyDestination),
                        new TextureBarrier(normalPlane.Texture, ResourceState.Undefined, ResourceState.CopyDestination),
                        new TextureBarrier(colourPlane.Texture, ResourceState.Undefined, ResourceState.CopyDestination)
                    ]
                )
            );

            var plane = Side * Side * 16;

            commands.CopyBufferToTexture(staging, 0, new TextureRegion(depthPlane.Texture), new(Side, Side, 1));
            commands.CopyBufferToTexture(staging, plane, new TextureRegion(normalPlane.Texture), new(Side, Side, 1));
            commands.CopyBufferToTexture(staging, plane * 2, new TextureRegion(colourPlane.Texture), new(Side, Side, 1));

            commands.Barrier(
                new(
                    [],
                    [
                        new TextureBarrier(depthPlane.Texture, ResourceState.CopyDestination, ResourceState.ShaderRead),
                        new TextureBarrier(normalPlane.Texture, ResourceState.CopyDestination, ResourceState.ShaderRead),
                        new TextureBarrier(colourPlane.Texture, ResourceState.CopyDestination, ResourceState.ShaderRead)
                    ]
                )
            );

            owned.Graph.Execute(commands);

            // The node left its answer in ShaderRead — read it back off the node's own texture.
            commands.Barrier(
                new([], [new TextureBarrier(node.Output, ResourceState.ShaderRead, ResourceState.CopySource)])
            );

            commands.CopyTextureToBuffer(new TextureRegion(node.Output), new(Side, Side, 1), readback, 0);
            commands.Barrier(
                new([], [new TextureBarrier(node.Output, ResourceState.CopySource, ResourceState.ShaderRead)])
            );

            commands.Finish();
            device.GraphicsQueue.Submit([commands]);
        }

        device.EndFrame();
        device.WaitIdle();

        Assert.Null(node.Skipped);
        Assert.Empty(effects.Misses);
        AssertClean();

        var bytes = new float[Side * Side * 4];

        device.Read(readback, 0, MemoryMarshal.AsBytes(bytes.AsSpan()));
        device.Destroy(readback);

        // The reference: the same reconstruction, the same march, the same matrices.
        var surface = new ReconstructedScreenSurface(new(Side, Side)) { InverseViewProjection = inverse };

        for (var at = 0; at < Side * Side; at++) {
            surface.Depth[at] = depths[at].X;

            // Encoded, because the surface decodes — the kernel reads its raw plane instead, and
            // the fixture's normal survives both conventions.
            surface.Normals[at] = new(0.5f, 1f, 0.5f, 0f);
        }

        var sky = new UniformSky(SkyMiss);

        var reference = new TracedReflections(new EmptyWorld(), new UniformSky(Vector3.Zero), new SkyFallback(sky)) {
            MaxDistance = 8f,
            ScreenTrace = new ScreenSpaceTrace(surface) { ViewProjection = viewProjection, Steps = 32, Thickness = 0.05f },
            ScreenColour = pixel => new(0.1f * pixel.X, 0.1f * pixel.Y, 0.3f)
        };

        var worst = 0f;
        var screened = 0;

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                var at = (y * Side) + x;
                var answer = new Vector4(bytes[at * 4], bytes[(at * 4) + 1], bytes[(at * 4) + 2], bytes[(at * 4) + 3]);

                if (!surface.TrySurface(new(x, y), out var position, out _)) {
                    Assert.Equal(0f, answer.W);

                    continue;
                }

                Assert.Equal(1f, answer.W);

                var view = Vector3.Normalize(position - node.CameraPosition);
                var expected = reference.Reflect(position, new(0f, 1f, 0f), view, 0f);

                worst = MathF.Max(worst, (new Vector3(answer.X, answer.Y, answer.Z) - expected).Length());

                if ((new Vector3(answer.X, answer.Y, answer.Z) - new Vector3(SkyMiss.X, SkyMiss.Y, SkyMiss.Z)).Length() > 0.05f) {
                    screened++;
                }
            }
        }

        owned.Graph.Reset();

        // The same arithmetic over the same planes; the wiring is the only thing that could drift.
        Assert.True(worst < 1e-4f, $"the node drifted {worst} from the reference");

        // And the frame answered: some rays found the ceiling and reflect the colour at its pixel.
        Assert.True(screened >= 2, $"only {screened} texels read the frame's colour — the screen march never ran");
    }

    sealed class EmptyWorld : IDistanceField {
        public float Sample(Vector3 position) => 1e6f;

        public Vector3 SampleGradient(Vector3 position) => new(0f, 1f, 0f);
    }

    sealed class UniformSky(Vector3 radiance) : IRadianceSource {
        public Vector3 Sky(Vector3 direction) => radiance;

        public Vector3 Surface(Vector3 position, Vector3 normal, Vector3 direction) => Vector3.Zero;
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
