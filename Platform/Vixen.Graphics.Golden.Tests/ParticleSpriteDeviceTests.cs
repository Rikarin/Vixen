// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics.Vulkan;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Features;
using Vixen.Rendering.Materials;
using Vixen.ShaderCompiler;
using Vixen.Shaders;
using Vixen.Shaders.Generated;
using Vixen.Rendering.Vfx;
using Vixen.Ui.Testing.Visual;
using Vixen.Vfx;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>
///     The CPU particle path, end to end: a <see cref="VfxSystem" /> becoming pixels.
/// </summary>
/// <remarks>
///     <para>
///         <b><see cref="ParticleRenderFeature" /> was a renderer with nothing to render with.</b> It
///         expands particles on the CPU into <c>ParticleVertex</c> — a world-space position, a texture
///         coordinate and a colour — and no shader in <c>Raven/Library</c> took those three:
///         <c>ParticleBillboard</c> expands the quad itself, from a corner offset and a particle
///         record, which is the shape the GPU path wants and not the shape this feature produces. So
///         every test it had ran against the Null backend and asserted about draw calls, and nothing
///         had ever put a particle on a screen.
///     </para>
///     <para>
///         <c>ParticleSprite.rvn</c> is the missing half and this is what says so. Three separate
///         contracts have to hold at once and each of them fails silently on its own:
///     </para>
///     <list type="bullet">
///         <item><description>
///             the <b>attribute names</b> — <c>ParticleVertices.Schema</c> matches a stage's inputs by
///             name, so <c>color</c> against a stage declaring <c>particleColor</c> is a pipeline the
///             driver refuses and an error that names the layout rather than the attribute;
///         </description></item>
///         <item><description>
///             the <b>set-1 layout</b>, which has to be allocated from this pipeline's own — a
///             particle shader reads nothing per frame where a shading pass reads thirteen bindings,
///             and a set 1 allocated from the shading pass's layout is not bound at all as far as
///             this pipeline is concerned;
///         </description></item>
///         <item><description>
///             the <b>material</b>, because <c>Draw</c> asks its material sub-feature which variant
///             each object resolves to and quietly skips any object with no answer — an effect that
///             expands its quads every frame and draws none of them.
///         </description></item>
///     </list>
///     <para>
///         A picture is the only thing that catches all three, which is the same argument
///         <see cref="ClusteredShadingDeviceTests" /> makes about its own frame.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public class ParticleSpriteDeviceTests {
    /// <summary>How far in front of the camera the particles sit.</summary>
    const float Depth = -6f;

    /// <summary>What the pass clears to: a blue no additive orange can produce.</summary>
    /// <remarks>
    ///     Load-bearing, exactly as it is next door: the blend is additive, so a pass that ran and drew
    ///     nothing leaves the clear untouched and a pass that never ran leaves black. Clearing to
    ///     something is what tells the two apart, and clearing to a colour the sprites cannot add is
    ///     what lets the corner assertion mean "the pass ran" rather than "something is there".
    /// </remarks>
    static Color4 Background => new(0f, 0f, 0.25f, 1f);

    /// <summary>
    ///     A <see cref="VfxSystem" />'s particles reach the screen, in their own colour.
    /// </summary>
    /// <remarks>
    ///     Orange because the particles are orange and the clear is blue, so the assertion cannot be
    ///     satisfied by the clear, by a black frame, or by a sprite drawn in whatever the vertex
    ///     colour attribute reads when it is bound to the wrong offset — which would be the texture
    ///     coordinate, and comes out as a red-green ramp with no blue and no consistency across the
    ///     quad.
    /// </remarks>
    [Fact]
    public void AParticleSystemReachesTheScreen() {
        if (!Fixture.TryOpen(out var fixture, out var reason)) {
            Skip(reason);
            return;
        }

        using var owned = fixture!;
        var image = Render(owned);

        var corner = Pixel(image, 2, 2);

        Assert.True(corner.Z > 0.2f && corner.X < 0.05f, $"the pass did not clear: {corner}");

        var centre = Pixel(image, image.Width / 2, image.Height / 2);

        Assert.True(centre.X > 0.2f, $"nothing was drawn where the particle is: {centre}");

        // Orange, which is the colour the graph set — and more red than green, which a colour read out
        // of the texture coordinate's bytes cannot reliably be. The blue is the clear showing through
        // the additive blend, which is why the margin against it is the narrower of the two.
        Assert.True(centre.X > centre.Y * 1.4f, $"the sprite is not the colour the effect set: {centre}");
        Assert.True(centre.X > centre.Z * 2f, $"the sprite is not the colour the effect set: {centre}");
    }

    // --- The frame ----------------------------------------------------------

    /// <summary>Draws one frame of one effect and reads the picture back.</summary>
    static Bitmap Render(Fixture fixture) {
        var device = fixture.Device;

        var loader = new EffectLoader(device);
        var effects = new EffectSystem();
        effects.AddProvider(new Compiling(loader, _ => RavenEffects.Everything()));

        // ⚠ **The engine's own default, not one built here, and that is the whole point of the
        // assertion.** A material assembled by this fixture would prove that *a* correctly filled
        // block draws; what a game gets is `WorldRenderer.ParticleMaterial`, and the way that fails
        // is a parameter nobody set — written as zero, because a shader's declared default reaches
        // the GPU through the generated key and the reflection carries one only for scalars. A zero
        // `tint` is black at zero alpha, which additively blended is perfectly invisible, and it is
        // exactly what shipped once.
        var material = ParticleSpriteMaterial.Default();

        // Flat, so the whole quad is at the vertex colour rather than a point with a halo — the
        // assertions above are about one pixel and a sharp falloff would make them about the sampling.
        material.Parameters.Set(ParticleSpriteKeys.EdgeSharpness, 0.25f);

        var effect = effects.Resolve(EffectKey.Of("ParticleSprite", [], material.Composition));

        Assert.NotNull(effect);

        using var allocator = new DescriptorAllocator(device);
        using var system = new RenderSystem();

        using var view = new ViewConstants(device) {
            Descriptors = allocator,
            Layout = effect!.SetLayouts[(int)DescriptorSetSlot.PerView]
        };

        // Additive, no depth at all, and nothing culled — a billboard's winding follows the camera.
        // The document that draws these in a real frame says the same three things; see the `Embers`
        // stage in `Samples/13-ThirdPersonShooter/Assets/Frame.vxcompositor`.
        var stage = system.AddStage(
            new("Embers", RenderSortMode.BackToFront) {
                Blend = BlendState.Additive,
                DepthStencil = DepthStencilState.Disabled,
                Rasterizer = RasterizerState.TwoSided
            }
        );

        var describer = new EffectPipelineDescriber(device);

        // Entry zero here, where a real host has the surface schema there and this at one — so the
        // feature is told which. That the index is configurable at all is what this is checking on
        // the way past: a particle put through a surface layout reads its texture coordinate as a
        // normal.
        describer.VertexSchemas.Add(ParticleVertices.Schema);

        var materials = new MaterialRenderFeature { Effects = effects, Device = device, Descriptors = allocator };

        var particles = new ParticleRenderFeature {
            Device = device,
            Pipelines = new(device),
            Describer = describer,
            VertexLayout = 0
        };

        particles.Add(materials);
        system.AddFeature(particles);

        var camera = new RenderView("camera") {
            Camera = Camera,
            Stages = stage.Mask,
            Position = Vector3.Zero
        };

        camera.Frustum = new(camera.ViewProjection);
        system.SetViews([camera]);

        // ⚠ The view the quads are expanded against, and it is the whole difference between a sprite
        // and a one-pixel sliver. Set here even though it is also `Views[0]`, because a frame with a
        // shadow cascade in front of the camera is the ordinary case and this is where a host is
        // reminded.
        particles.View = camera;

        var drift = Effect();

        var id = system.Objects.Add(
            new() {
                Bounds = new(new Vector3(0f, 0f, Depth), 4f),
                Stages = stage.Mask,
                FeatureIndex = particles.Index
            }
        );

        particles.SetSystem(id, drift);
        materials.Assign(system, id, material);

        var pass = new RenderPassRenderer {
            Name = "Sparks",
            ClearColour = Background,
            Children = { new SingleStageRenderer { View = camera, Stage = stage, Constants = view } }
        };

        pass.ColourTargets.Add("Display");

        var display = fixture.Owned("Display", TextureUsage.ColourTarget | TextureUsage.CopySource);

        var compositor = new GraphicsCompositor(system) {
            FrameSize = new(Fixture.Side, Fixture.Side),
            Game = pass
        };

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

        // ⚠ Asserted rather than assumed, because every one of these is a way the picture comes back
        // as the clear and nothing says why: no particles expanded, no material bound, no draw.
        Assert.True(particles.LastParticleCount > 0, "the effect expanded no particles");
        Assert.True(materials.BoundCount > 0, "set 2 was never bound, so every draw was skipped");

        drift.Dispose();

        return picture;
    }

    /// <summary>The camera the quads are expanded against.</summary>
    static RenderCamera Camera =>
        RenderCamera.Default with { Position = Vector3.Zero, AspectRatio = 1f };

    /// <summary>One large orange particle in front of the camera, stepped once.</summary>
    /// <remarks>
    ///     <para>
    ///         A burst rather than a rate, because a rate spawns from the elapsed time and one step of
    ///         a sixtieth of a second at any sane rate is zero particles. A metre across because the
    ///         assertion is about one pixel in the middle of a 128-pixel picture, and a two-centimetre
    ///         ember at six metres does not cover it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Exactly one, and at the sphere's centre — the count is what the colour assertion
    ///         rests on.</b> The blend is additive, so sixty-four overlapping orange sprites sum past
    ///         one in every channel and come back as white, which passes "something is there" and fails
    ///         "it is the colour the effect set" for a reason that has nothing to do with the shader.
    ///     </para>
    /// </remarks>
    static VfxSystem Effect() {
        var graph = VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(1)],
            [
                new(VfxOpcode.PositionInSphere, new Vector4(0f, 0f, Depth, 0f)),
                new(VfxOpcode.SetSize, new Vector4(1f, 1f, 0f, 0f)),

                // The colour the picture is checked for: orange, and nothing else in this frame is.
                new(VfxOpcode.SetColour, new Vector4(1f, 0.45f, 0.08f, 1f)),
                new(VfxOpcode.SetLifetime, new Vector4(100f, 100f, 0f, 0f))
            ],
            [],
            256,
            VfxRenderer.Billboard
        );

        var effect = new VfxSystem(graph);

        effect.Step(1f / 60f);

        return effect;
    }

    /// <summary>One pixel, as channels in 0..1.</summary>
    static Vector3 Pixel(in Bitmap image, int x, int y) {
        var offset = image.Offset(Math.Clamp(x, 0, image.Width - 1), Math.Clamp(y, 0, image.Height - 1));

        return new(image.Pixels[offset] / 255f, image.Pixels[offset + 1] / 255f, image.Pixels[offset + 2] / 255f);
    }

    /// <summary>Passes when there is no device, unless the environment insists on one.</summary>
    static void Skip(string? reason) {
        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set and no device could be opened: {reason}");
        }
    }
}
