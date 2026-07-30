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

    /// <summary>
    ///     The view bias moves the lookup toward the camera, on a device, by the amount it says.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>A field whose probes carry their own <i>Z</i>, so the pixel reports where the lookup
    ///         landed.</b> Every other test here uses a uniform environment on purpose — it makes the
    ///         answer a number rather than a shape — and a uniform environment is exactly what a bias
    ///         cannot be seen through: moving the lookup anywhere reads the same light. So this is the
    ///         one frame in the file with a gradient in it.
    ///     </para>
    ///     <para>
    ///         With an identity inverse view-projection the reconstruction is its own coordinate
    ///         system: a pixel at device depth <see cref="SurfaceDepth" /> lands at
    ///         <c>z = SurfaceDepth</c>, and the near plane — <b>one</b>, because depth is reversed —
    ///         is at <c>z = 1</c>. The view direction is therefore exactly <c>+Z</c> at every pixel,
    ///         which turns "toward the camera" into an axis a probe value can measure.
    ///     </para>
    ///     <para>
    ///         The normal bias is zero throughout, so what moves is the view term alone. Getting its
    ///         sign backwards, dropping it, or reading the near plane at zero all produce a different
    ///         number here — and none of them produce a different number in any other test in this
    ///         file.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheViewBiasMovesTheLookupTowardTheCamera() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;

        // Two whole probe spacings, which is one world unit here — see Spacing.
        //
        // ⚠ Eight times the shipping default, and deliberately. At a quarter of a spacing the lookup
        // moves an eighth of a unit and the ramp reports a change of 0.031, which against an 8-bit
        // target and a 0.02 tolerance is a signal the size of its own noise floor: the test would pass
        // for a bias applied at a third of its strength. The arithmetic is linear, so measuring it
        // where it is legible measures it everywhere.
        const float Bias = 2f;

        var unbiased = Pixel(Render(owned, filled: true, ramp: true, viewBias: 0f), Fixture.Side / 2, Fixture.Side / 2);

        // A graph builds one frame. Two frames from one fixture is two graphs, and the second would
        // otherwise declare into one that has already been compiled and culled.
        owned.Graph.Reset();

        var biased = Pixel(Render(owned, filled: true, ramp: true, viewBias: Bias), Fixture.Side / 2, Fixture.Side / 2);

        // The ramp is what the probes carry, so the pixel is Ramp(z) and z is where the lookup went.
        Assert.Equal(Ramp(SurfaceDepth), unbiased.X, 0.02f);
        Assert.Equal(Ramp(SurfaceDepth + (Bias * Spacing)), biased.X, 0.02f);

        // And the step is far larger than the tolerance that accepted each end, which is what makes
        // the pair a measurement rather than two assertions that happen to overlap.
        Assert.True(
            biased.X - unbiased.X > 0.15f,
            $"the view bias moved the lookup by nothing: {unbiased.X} then {biased.X}"
        );
    }

    /// <summary>
    ///     <b>And the sky is left alone, because depth is reversed.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A depth attachment clears to zero and zero is the <i>far</i> plane — near is one. The
    ///         pass's own test for "nothing was drawn here" read <c>&gt;= 1</c>, so it fired on the
    ///         surfaces nearest the camera and never on the sky, which then got a field lookup at
    ///         whatever the far plane reconstructs to and came back lit.
    ///     </para>
    ///     <para>
    ///         ⚠ Nothing caught it because every frame in this file clears its stand-in depth to a
    ///         half, where both spellings behave the same. <c>DistanceFieldAo</c> had the identical
    ///         inversion, for the identical reason. <c>Fog.rvn</c> has always had it right, which is
    ///         what says the convention was never in doubt — only this reading of it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheFarPlaneIsSkyAndGetsNoIndirectLight() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;

        // A filled field, so a pass that did the lookup anyway would come back at Radiance rather
        // than at zero — the two answers are as far apart as this fixture can make them.
        var image = Render(owned, filled: true, clearDepth: 0f);
        var pixel = Pixel(image, Fixture.Side / 2, Fixture.Side / 2);

        Assert.True(pixel.X < 0.02f, $"the sky was given indirect light: {pixel}");

        // Alpha is what says the shader ran at all: black in rgb is also what an unwritten target
        // holds, and a one here is a number only this branch puts there.
        Assert.Equal(1f, Alpha(image, Fixture.Side / 2, Fixture.Side / 2), 0.01f);
    }

    /// <summary>Where the g-buffer's cleared depth reconstructs to, in world Z.</summary>
    /// <remarks>Identity inverse view-projection, so a device depth of one half is a world Z of one half.</remarks>
    const float SurfaceDepth = 0.5f;

    /// <summary>How far apart the ramped field's probes are, in world units.</summary>
    /// <remarks>
    ///     Four units across two indirection cells is a cell of two, and a brick spans four probe gaps
    ///     rather than five — so a brick of size one has probes a half apart. The bias is measured in
    ///     these, which is what makes it a number a coarse region scales rather than a fixed distance.
    /// </remarks>
    const float Spacing = 0.5f;

    /// <summary>What a probe at a world Z carries, chosen to stay inside an 8-bit target.</summary>
    static float Ramp(float z) => 0.25f + (0.25f * z);

    /// <summary>Writes the ramp into every probe of a field, borders included.</summary>
    /// <remarks>
    ///     By hand rather than through a filler, because a filler answers "what light is here" and this
    ///     needs "where am I" — and the borders are synced explicitly, since the renderer only runs the
    ///     repair after a fill it did itself.
    /// </remarks>
    static void Author(IrradianceField field) {
        const int Resolution = IrradianceBrickPool.BrickResolution;

        foreach (var brick in field.Bricks) {
            for (var z = 0; z < Resolution; z++) {
                for (var y = 0; y < Resolution; y++) {
                    for (var x = 0; x < Resolution; x++) {
                        var value = Ramp(field.ProbePosition(brick, x, y, z).Z);

                        // The constant band alone, divided by its own basis function, so what comes
                        // back out of Irradiance is the number that went in rather than the
                        // coefficient behind it.
                        field.SetProbe(
                            brick,
                            x,
                            y,
                            z,
                            IrradianceProbe.Lit(new(new Vector3(value / 0.282095f), default, default, default))
                        );
                    }
                }
            }
        }

        field.SyncBorders();
    }

    /// <summary>Runs one frame of the pass and reads the picture back.</summary>
    static Bitmap Render(Fixture fixture, bool filled, bool ramp = false, float viewBias = 0f, float clearDepth = SurfaceDepth) {
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

        var field = new IrradianceField(new BoundingBox(new(-2f), new(2f)), new(2)) {
            // Zero throughout, so a ramped frame measures the view term alone. The normal here is +Z
            // and so is the view direction, and two biases along one axis are one number nobody can
            // attribute.
            NormalBias = 0f,
            ViewBias = viewBias
        };

        field.AllocateAll();

        if (ramp) {
            Author(field);
        }

        using var probes = new IrradianceFieldRenderer {
            Name = "IrradianceField",
            Field = field,

            // A ramped field is authored rather than traced: what its probes carry is where they
            // stand, which no filler would ever produce.
            Filler = ramp ? null : new TracedIrradianceFiller(new EmptyWorld(), new UniformSky(Radiance)),
            SceneConstants = scene,
            Device = device,

            // Every brick in one frame, so the picture is the converged answer rather than whatever a
            // round robin had reached by the time it was read.
            Budget = ramp ? 0 : field.BrickCount
        };

        var describer = new EffectPipelineDescriber(device);
        var loader = new EffectLoader(device);
        var effects = new EffectSystem();

        // ⚠ Not the whole library: a source set holding the material tree compiles nothing without a
        // material's composition, because every slot the sources declare has to be bound whether or
        // not this shader reaches it. DistanceFields is in the set for that reason and not because
        // this pass traces anything — IrradianceFill declares a slot, so this compilation has to name
        // a filler for it, and the filler has to be somewhere the compiler can see.
        effects.AddProvider(
            new Compiling(
                loader,
                _ => RavenEffects.Only(
                    ["Core", "Geometry", "Shading", "DistanceFields", "IrradianceFields"],
                    Path.Combine("PostFx", "Fullscreen.rvn"),
                    Path.Combine("PostFx", "IndirectDiffuse.rvn")
                )
            )
        );

        var gbuffer = new RenderPassRenderer { Name = "GBuffer", ClearColour = new(clearDepth, clearDepth, 1f, 1f) };

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
            Assert.Equal(ramp ? 0 : field.BrickCount, probes.Filled);
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
