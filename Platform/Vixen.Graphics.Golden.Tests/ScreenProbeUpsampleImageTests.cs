// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics.Vulkan;
using Vixen.Ui.Testing.Visual;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.DistanceFields;
using Vixen.Rendering.IrradianceFields;
using Vixen.Rendering.Lighting;
using Vixen.Rendering.PostFx;
using Vixen.Rendering.ScreenProbes;
using Vixen.Shaders;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>The upsample drawn by a real compositor, and the picture read back.</summary>
/// <remarks>
///     <para>
///         The frame this track said would find something. The probes are gathered on the CPU under a
///         uniform sky of radiance <i>L</i>, resolved by the device dispatch, and read by the pass
///         through a real render graph — so a flat frame of <i>L</i> has crossed the atlas, the
///         solid-angle table, the four planes, the lattice walk and the compositor's scheduling, and
///         is the same closed form every layer below was held to alone.
///     </para>
///     <para>
///         The dispatches run in their own submit before the frame, deliberately: the node that
///         schedules trace, resolve and upsample as one graph is the next increment, and testing the
///         pass's own frame first is what separates "the node schedules wrongly" from "the pass draws
///         wrongly" before that node exists.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public sealed class ScreenProbeUpsampleImageTests {
    const float Radiance = 0.75f;

    /// <summary>Depth of a half — a surface everywhere, in reversed depth.</summary>
    const float SurfaceDepth = 0.5f;

    [Fact]
    public void AUniformSkyComesBackAsAFlatFrame() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        var picture = Render(owned, SurfaceDepth);

        var centre = Pixel(picture, picture.Width / 2, picture.Height / 2);
        var corner = Pixel(picture, 2, 2);

        Assert.Equal(Radiance, centre.X, 0.02f);
        Assert.Equal(Radiance, centre.Y, 0.02f);
        Assert.Equal(Radiance, centre.Z, 0.02f);
        Assert.Equal(Radiance, corner.X, 0.02f);
    }

    /// <summary>And a frame whose depth says nothing was drawn is dark.</summary>
    /// <remarks>
    ///     Zero is the sky in reversed depth, and this frame is what keeps the spelling honest on a
    ///     device — the wrong comparison lights the sky from a probe at a far-plane position, which is
    ///     exactly what it did in two shaders before this one.
    /// </remarks>
    [Fact]
    public void TheSkyGetsNothing() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        var picture = Render(owned, 0f);

        var centre = Pixel(picture, picture.Width / 2, picture.Height / 2);

        Assert.True(centre.X < 0.02f, $"the sky was lit: {centre}");
    }

    static Bitmap Render(Fixture fixture, float clearDepth) {
        var device = fixture.Device;

        var atlas = new ScreenProbeAtlas(new(new(Fixture.Side, Fixture.Side)));

        new TracedScreenProbeGather(new EmptyWorld(), new UniformSky(Radiance)).Fill(atlas, new Floor());

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

        // The dispatches, in their own submit — see the class remarks.
        allocator.BeginFrame();
        VulkanDiagnostics.Reset();
        device.BeginFrame();

        using (var commands = device.BeginCommandList(QueueKind.Graphics, "screen probe seed")) {
            texture.Upload(device, commands);

            Assert.Equal(atlas.Layout.ProbeCount, resolve.Record(commands, texture));

            commands.Finish();
            device.GraphicsQueue.Submit([commands]);
        }

        device.EndFrame();
        device.WaitIdle();

        Assert.Null(resolve.Skipped);

        var depth = fixture.Owned("depth", TextureUsage.ColourTarget | TextureUsage.Sampled);
        var normals = fixture.Owned("normals", TextureUsage.ColourTarget | TextureUsage.Sampled);
        var display = fixture.Owned("display", TextureUsage.ColourTarget | TextureUsage.CopySource);

        var describer = new EffectPipelineDescriber(device);

        // Depth in .r and a normal of +Z out of one clear, exactly as the indirect-diffuse frame
        // arranges it — and with a uniform sky the answer depends on neither, which is why this is
        // the frame to start with.
        var gbuffer = new RenderPassRenderer { Name = "GBuffer", ClearColour = new(clearDepth, clearDepth, 1f, 1f) };

        gbuffer.ColourTargets.Add("Depth");
        gbuffer.ColourTargets.Add("Normals");

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

        var compositor = new GraphicsCompositor(system) {
            FrameSize = new(Fixture.Side, Fixture.Side),
            Game = new SceneRendererSequence { Children = { gbuffer, upsample } }
        };

        compositor.Imports["Depth"] = new(depth.Texture, depth.View, depth.Description);
        compositor.Imports["Normals"] = new(normals.Texture, normals.View, normals.Description);

        // The resolved planes, imported already in ShaderRead — the graph must not transition what
        // the resolve owns, and a ResourceBinding resolves textures against the graph and nothing
        // else.
        var planeNames = new[] { "ProbeL0", "ProbeL1R", "ProbeL1G", "ProbeL1B" };

        for (var plane = 0; plane < 4; plane++) {
            compositor.Imports[planeNames[plane]] = new(
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
        Assert.True(upsample.Pass.PipelineCount > 0, "the pass compiled no pipeline, so it drew nothing");

        var picture = fixture.Render(frame.Texture("harness", "Display"));

        if (VulkanDiagnostics.ErrorCount > 0) {
            Assert.Fail(
                "The frame produced validation errors, so the picture is meaningless: "
                + string.Join(Environment.NewLine, VulkanDiagnostics.Messages)
            );
        }

        return picture;
    }

    sealed class EmptyWorld : IDistanceField {
        public float Sample(Vector3 position) => 1e6f;

        public Vector3 SampleGradient(Vector3 position) => new(0f, 1f, 0f);
    }

    sealed class UniformSky(float radiance) : IRadianceSource {
        public Vector3 Sky(Vector3 direction) => new(radiance);

        public Vector3 Surface(Vector3 position, Vector3 normal, Vector3 direction) => Vector3.Zero;
    }

    sealed class Floor : IScreenSurface {
        public bool TrySurface(Int2 pixel, out Vector3 position, out Vector3 normal) {
            position = new((pixel.X - 16) * 0.1f, 0f, (pixel.Y - 16) * 0.1f);
            normal = new(0f, 1f, 0f);

            return true;
        }
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
