// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Vixen.Graphics.Vulkan;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Lighting;
using Vixen.Rendering.PostFx;
using Vixen.Rendering.ScreenProbes;
using Vixen.Shaders;
using Vixen.Ui.Testing.Visual;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>The picture the bilateral upsample exists for: a depth edge light must not cross.</summary>
/// <remarks>
///     <para>
///         Two planes four world units apart share one screen — bright probes on the far side, dark
///         on the near — and the frame's own depth carries the step. The bilinear taps bleed across
///         it by exactly the lattice weight, which is the number this test pins first, because a
///         discriminator that cannot show the failure proves nothing about the fix. With the plane
///         tolerance on, each pixel rejects the probes standing on the other surface and the sides
///         come back pure: bright stays whole, dark stays black, at the same pixels that just bled.
///     </para>
///     <para>
///         The G-buffer is uploaded rather than drawn — a synthetic depth step is exactly as real to
///         the reconstruction as a rasterised one, and it is knowable to the bit. The probes' surface
///         and normal planes are uploaded the same way, standing in for the history's, which is what
///         the gather node feeds the pass in a live frame.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public sealed class ScreenProbeBilateralImageTests {
    /// <summary>Device depth of the far plane (z = −7) and the near one (z = −3), reversed.</summary>
    const float FarDepth = 0.25f;

    const float NearDepth = 0.75f;

    [Fact]
    public void TheBilinearTapsBleedAcrossTheEdgeByTheLatticeWeight() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        var picture = Render(owned, planeTolerance: 0f);

        // Pixel 60 sits a quarter of a tile past the bright side's last anchor at 56, so a quarter
        // of its answer comes from the dark side's probes — and mirrored at 68. The failure,
        // quantified, so the fix below is measured against it rather than against a feeling.
        Assert.Equal(0.75f, Pixel(picture, 60, 64).X, 0.02f);
        Assert.Equal(0.25f, Pixel(picture, 68, 64).X, 0.02f);
    }

    [Fact]
    public void ThePlaneTestKeepsEachSidePure() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        var picture = Render(owned, planeTolerance: 0.5f);

        // The same two pixels: the far-side one rejects the near plane's probes and keeps the whole
        // bright answer; the near-side one rejects the far plane's and stays black. Four world units
        // of separation against half a unit of tolerance — nothing marginal.
        Assert.Equal(1f, Pixel(picture, 60, 64).X, 0.02f);
        Assert.True(Pixel(picture, 68, 64).X < 0.02f, $"light bled onto the near plane: {Pixel(picture, 68, 64)}");

        // Away from the edge, both variants agree.
        Assert.Equal(1f, Pixel(picture, 20, 64).X, 0.02f);
        Assert.True(Pixel(picture, 110, 64).X < 0.02f, $"the far half of the near plane lit up: {Pixel(picture, 110, 64)}");
    }

    static Bitmap Render(Fixture fixture, float planeTolerance) {
        var device = fixture.Device;
        var atlas = new ScreenProbeAtlas(new(new(Fixture.Side, Fixture.Side)));
        var layout = atlas.Layout;
        var half = layout.GridSize.X / 2;

        // Bright maps on the far-plane probes, dark on the near — all valid.
        for (var y = 0; y < layout.GridSize.Y; y++) {
            for (var x = 0; x < layout.GridSize.X; x++) {
                var probe = new Int2(x, y);

                atlas.SetSurface(probe, Vector3.Zero, new(0f, 0f, 1f));

                for (var ty = 0; ty < layout.MapResolution; ty++) {
                    for (var tx = 0; tx < layout.MapResolution; tx++) {
                        atlas[probe, new(tx, ty)] = new(x < half ? 1f : 0f);
                    }
                }
            }
        }

        using var allocator = new DescriptorAllocator(device);
        using var samplers = new SamplerCache(device);
        using var system = new RenderSystem();
        using var texture = new ScreenProbeTexture(atlas);

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

        using var resolve = new ScreenProbeResolve(device) {
            Effects = effects,
            Pipelines = new ComputePipelineCache(device),
            Descriptors = allocator
        };

        // The frame's own buffers, synthesised: the depth step, flat +Z normals, and the probes'
        // surface and normal planes the bilateral taps test against.
        var usage = TextureUsage.Sampled | TextureUsage.CopyDestination;
        var depth = fixture.Owned("depth", usage, PixelFormat.Rgba32Float);
        var normals = fixture.Owned("normals", usage, PixelFormat.Rgba32Float);
        var surfacePlane = fixture.Owned("probe surfaces", usage, PixelFormat.Rgba32Float, layout.GridSize.X, layout.GridSize.Y);
        var normalPlane = fixture.Owned("probe normals", usage, PixelFormat.Rgba32Float, layout.GridSize.X, layout.GridSize.Y);

        var depthTexels = new float[Fixture.Side * Fixture.Side * 4];
        var normalTexels = new float[Fixture.Side * Fixture.Side * 4];

        for (var y = 0; y < Fixture.Side; y++) {
            for (var x = 0; x < Fixture.Side; x++) {
                var at = ((y * Fixture.Side) + x) * 4;

                depthTexels[at] = x < Fixture.Side / 2 ? FarDepth : NearDepth;
                normalTexels[at] = 0.5f;
                normalTexels[at + 1] = 0.5f;
                normalTexels[at + 2] = 1f;
                normalTexels[at + 3] = 1f;
            }
        }

        var surfaceTexels = new float[layout.GridSize.X * layout.GridSize.Y * 4];
        var planeNormalTexels = new float[layout.GridSize.X * layout.GridSize.Y * 4];

        for (var y = 0; y < layout.GridSize.Y; y++) {
            for (var x = 0; x < layout.GridSize.X; x++) {
                var at = ((y * layout.GridSize.X) + x) * 4;

                // Only the plane matters to the test — position z per side, normal +Z.
                surfaceTexels[at + 2] = x < half ? -7f : -3f;
                planeNormalTexels[at + 2] = 1f;
                surfaceTexels[at + 3] = 1f;
            }
        }

        var staging = new[] {
            Staged(device, depthTexels, "depth staging"),
            Staged(device, normalTexels, "normal staging"),
            Staged(device, surfaceTexels, "surface staging"),
            Staged(device, planeNormalTexels, "plane normal staging")
        };

        allocator.BeginFrame();
        VulkanDiagnostics.Reset();
        device.BeginFrame();

        using (var commands = device.BeginCommandList(QueueKind.Graphics, "bilateral seed")) {
            Upload(commands, staging[0], depth.Texture, Fixture.Side, Fixture.Side);
            Upload(commands, staging[1], normals.Texture, Fixture.Side, Fixture.Side);
            Upload(commands, staging[2], surfacePlane.Texture, layout.GridSize.X, layout.GridSize.Y);
            Upload(commands, staging[3], normalPlane.Texture, layout.GridSize.X, layout.GridSize.Y);

            texture.Upload(device, commands);

            Assert.Equal(layout.ProbeCount, resolve.Record(commands, texture));

            commands.Finish();
            device.GraphicsQueue.Submit([commands]);
        }

        device.EndFrame();
        device.WaitIdle();

        foreach (var buffer in staging) {
            device.Destroy(buffer);
        }

        Assert.Null(resolve.Skipped);
        Assert.True(Matrix4x4.Invert(Matrix4x4.Orthographic(4f, 4f, 1f, 9f), out var inverse));

        var display = fixture.Owned("display", TextureUsage.ColourTarget | TextureUsage.CopySource);
        var describer = new EffectPipelineDescriber(device);

        using var upsample = new ScreenProbeUpsampleRenderer {
            Name = "ScreenProbeUpsample",
            Depth = "Depth",
            Normals = "Normals",
            Output = "Display",
            Probes = texture,
            Planes = ["ProbeL0", "ProbeL1R", "ProbeL1G", "ProbeL1B"],
            Modules = describer,
            Device = device,
            Samplers = samplers,
            Allocator = allocator
        };

        upsample.SurfacePlane = "ProbeSurface";
        upsample.NormalPlane = "ProbeNormal";
        upsample.PlaneTolerance = planeTolerance;
        upsample.InverseViewProjection = inverse;

        var compositor = new GraphicsCompositor(system) {
            FrameSize = new(Fixture.Side, Fixture.Side),
            Game = new SceneRendererSequence { Children = { upsample } }
        };

        compositor.Imports["Depth"] = new(depth.Texture, depth.View, depth.Description, ResourceState.ShaderRead, ResourceState.ShaderRead);
        compositor.Imports["Normals"] = new(normals.Texture, normals.View, normals.Description, ResourceState.ShaderRead, ResourceState.ShaderRead);
        compositor.Imports["ProbeSurface"] = new(surfacePlane.Texture, surfacePlane.View, surfacePlane.Description, ResourceState.ShaderRead, ResourceState.ShaderRead);
        compositor.Imports["ProbeNormal"] = new(normalPlane.Texture, normalPlane.View, normalPlane.Description, ResourceState.ShaderRead, ResourceState.ShaderRead);

        for (var plane = 0; plane < 4; plane++) {
            compositor.Imports[upsample.Planes[plane]] = new(
                texture.ProbePlane(plane),
                texture.ProbeView(plane),
                texture.ProbePlaneDescription,
                ResourceState.ShaderRead,
                ResourceState.ShaderRead
            );
        }

        compositor.Imports["Display"] = new(
            display.Texture,
            display.View,
            display.Description,
            ResourceState.Undefined,
            ResourceState.CopySource
        );

        allocator.BeginFrame();

        var frame = compositor.Build(fixture.Graph, effects, device);

        Assert.Empty(effects.Misses);

        var picture = fixture.Render(frame.Texture("harness", "Display"));

        if (VulkanDiagnostics.ErrorCount > 0) {
            Assert.Fail(
                "The frame produced validation errors, so the picture is meaningless: "
                + string.Join(Environment.NewLine, VulkanDiagnostics.Messages)
            );
        }

        return picture;
    }

    static BufferHandle Staged(VulkanDevice device, float[] texels, string name) {
        var buffer = device.CreateBuffer(
            new((long)texels.Length * sizeof(float), BufferUsage.CopySource, MemoryAccess.HostUpload, name)
        );

        device.Write(buffer, 0, MemoryMarshal.AsBytes(texels.AsSpan()));

        return buffer;
    }

    static void Upload(ICommandList commands, BufferHandle staging, TextureHandle target, int width, int height) {
        commands.Barrier(new([], [new TextureBarrier(target, ResourceState.Undefined, ResourceState.CopyDestination)]));
        commands.CopyBufferToTexture(staging, 0, new TextureRegion(target), new(width, height, 1));
        commands.Barrier(new([], [new TextureBarrier(target, ResourceState.CopyDestination, ResourceState.ShaderRead)]));
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
