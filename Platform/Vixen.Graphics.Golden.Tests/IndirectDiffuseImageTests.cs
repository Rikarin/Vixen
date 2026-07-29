// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.DistanceFields;
using Vixen.Rendering.IrradianceFields;
using Vixen.Rendering.PostFx;
using Vixen.Shaders;
using Vixen.Ui.Testing.Visual;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>
///     The irradiance field, read by a shader, on a device.
/// </summary>
/// <remarks>
///     <para>
///         Every part of <c>docs/plan/19</c> § L2 has so far been checked against something that agrees
///         with it: the storage against closed forms, the filler against the projection's own exact
///         answer, the shader's addressing against the field's by walking the same arithmetic in C#.
///         None of it had executed.
///     </para>
///     <para>
///         <b>The scene is chosen so the answer is a number rather than a shape.</b> An empty world
///         under a uniform sky of radiance <i>L</i> lights every surface with exactly <i>L</i>,
///         whichever way it faces — the closed form the projection, the filler and now the shader are
///         each held against. So the frame is a flat field of <i>L</i>, and every step between the
///         probe and the pixel is in the path: the fill, the dilation, the border sync, the pack into
///         four volumes, the copy, the index fetch, the trilinear read, and the basis evaluation.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public sealed class IndirectDiffuseImageTests {
    /// <summary>How bright the sky is, and therefore what every pixel has to be.</summary>
    /// <remarks>
    ///     Deliberately not a half, and not a one. The g-buffer is cleared to halves and the alpha the
    ///     shader writes is a one, so a value equal to either would pass this test for a picture that
    ///     had merely copied something through — which is the shape of most of the ways a path like
    ///     this can be wrong.
    /// </remarks>
    const float Radiance = 0.75f;

    /// <summary>
    ///     A uniform environment comes back as itself, through the whole L2 path.
    /// </summary>
    [Fact]
    public void AUniformEnvironmentReachesEveryPixel() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        var image = Render(owned, filled: true);

        for (var y = 0; y < image.Height; y += image.Height / 8) {
            for (var x = 0; x < image.Width; x += image.Width / 8) {
                var pixel = Pixel(image, x, y);

                Assert.Equal(Radiance, pixel.X, 0.02f);
                Assert.Equal(Radiance, pixel.Y, 0.02f);
                Assert.Equal(Radiance, pixel.Z, 0.02f);
            }
        }
    }

    /// <summary>
    ///     <b>And the null field answers its two different right answers.</b> No indirect light, and a
    ///     sun nothing shadows — a field that is absent contributes nothing to the ambient term and
    ///     does not put the world into shadow either.
    /// </summary>
    /// <remarks>
    ///     The alpha is what makes this test say anything: black in <c>rgb</c> is also what an
    ///     unwritten target holds, and the pass writes with <c>DontCare</c>. A one in alpha is a
    ///     number only the shader puts there.
    /// </remarks>
    [Fact]
    public void TheNullFieldContributesNothingAndShadowsNothing() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        var image = Render(owned, filled: false);

        var pixel = Pixel(image, image.Width / 2, image.Height / 2);
        var alpha = Alpha(image, image.Width / 2, image.Height / 2);

        Assert.Equal(0f, pixel.X, 0.01f);
        Assert.Equal(0f, pixel.Y, 0.01f);
        Assert.Equal(0f, pixel.Z, 0.01f);
        Assert.Equal(1f, alpha, 0.01f);
    }

    /// <summary>Runs one frame of the pass and reads the picture back.</summary>
    static Bitmap Render(Fixture fixture, bool filled) {
        var device = fixture.Device;

        // Depth of a half and a normal of +Z, so every pixel reconstructs to a point on the plane
        // z = 0.5 inside the field. With a uniform environment the answer does not depend on either,
        // which is exactly why this is the frame to start with.
        var depth = fixture.Owned("depth", TextureUsage.ColourTarget | TextureUsage.Sampled);
        var normals = fixture.Owned("normals", TextureUsage.ColourTarget | TextureUsage.Sampled);
        var display = fixture.Owned("display", TextureUsage.ColourTarget | TextureUsage.CopySource);

        using var allocator = new DescriptorAllocator(device);
        using var samplers = new SamplerCache(device);
        using var system = new RenderSystem();
        using var scene = new SceneConstants(device) { Descriptors = allocator };

        var field = new IrradianceField(new BoundingBox(new(-2f), new(2f)), new(2));

        field.AllocateAll();

        using var probes = new IrradianceFieldRenderer {
            Name = "IrradianceField",
            Field = field,
            Filler = new(new EmptyWorld(), new UniformSky(Radiance)),
            SceneConstants = scene,
            Device = device,

            // Every brick in one frame, so the picture is the converged answer rather than whatever a
            // round robin had reached by the time it was read.
            Budget = field.BrickCount
        };

        var describer = new EffectPipelineDescriber(device);
        var loader = new EffectLoader(device);
        var effects = new EffectSystem();

        // ⚠ Not the whole library: a source set holding the material tree compiles nothing without a
        // material's composition, because every slot the sources declare has to be bound whether or
        // not this shader reaches it.
        effects.AddProvider(
            new Compiling(
                loader,
                _ => RavenEffects.Only(
                    ["Core", "Geometry", "Shading", "IrradianceFields"],
                    Path.Combine("PostFx", "Fullscreen.rvn"),
                    Path.Combine("PostFx", "IndirectDiffuse.rvn")
                )
            )
        );

        var gbuffer = new RenderPassRenderer { Name = "GBuffer", ClearColour = new(0.5f, 0.5f, 1f, 1f) };

        gbuffer.ColourTargets.Add("Depth");
        gbuffer.ColourTargets.Add("Normals");

        using var indirect = new IndirectDiffuseRenderer {
            Name = "IndirectDiffuse",
            Depth = "Depth",
            Normals = "Normals",
            Output = "Display",
            Source = filled ? "IrradianceFieldProbes" : "NoIrradiance",
            SceneConstants = filled ? scene : null,
            Modules = describer,
            Device = device,
            Samplers = samplers,
            Allocator = allocator
        };

        var sequence = new SceneRendererSequence { Children = { gbuffer } };

        if (filled) {
            sequence.Children.Add(probes);
        }

        sequence.Children.Add(indirect);

        var compositor = new GraphicsCompositor(system) {
            FrameSize = new(Fixture.Side, Fixture.Side),
            Game = sequence
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

        Assert.Empty(effects.Misses);
        Assert.True(indirect.Pass.PipelineCount > 0, "the pass compiled no pipeline, so it drew nothing");

        var picture = fixture.Render(frame.Texture("harness", "Display"));

        if (filled) {
            // ⚠ Asserted rather than assumed. A set 0 that fell one binding short is not bound at all,
            // and the pass then reads whatever set 0 held before — which here is nothing, and comes
            // back as a field with no light in it.
            Assert.Equal(field.BrickCount, probes.Filled);
            Assert.True(scene.IsComplete, "set 0 was left incomplete, so the frame bound none of it");
            Assert.True(scene.WriteCount > 0, "set 0 was never written");
        }

        return picture;
    }

    /// <summary>A world with nothing in it, so every ray reaches the sky.</summary>
    sealed class EmptyWorld : IDistanceField {
        public float Sample(Vector3 position) => 1e6f;

        public Vector3 SampleGradient(Vector3 position) => Vector3.Zero;
    }

    /// <summary>One radiance from every direction, and surfaces that give back nothing.</summary>
    sealed class UniformSky(float radiance) : IRadianceSource {
        public Vector3 Sky(Vector3 direction) => new(radiance);

        public Vector3 Surface(Vector3 position, Vector3 normal, Vector3 direction) => Vector3.Zero;
    }

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

        Assert.Skip(reason ?? "no Vulkan");

        return false;
    }
}
