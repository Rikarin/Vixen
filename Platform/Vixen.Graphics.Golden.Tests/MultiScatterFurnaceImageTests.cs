// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Vixen.Graphics.RenderGraph;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.PostFx;
using Vixen.Shaders;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>
///     The white furnace: a perfect white metal under a uniform environment reflects all of it, at
///     every roughness.
/// </summary>
/// <remarks>
///     <para>
///         <b>The one property a specular scale can be held to exactly.</b> Put a surface whose
///         <c>f0</c> is one under an environment of constant radiance <c>L</c> and it must send back
///         exactly <c>L</c>: it absorbs nothing, so whatever the microfacets do to the light between
///         them, all of it leaves. That makes it a closed-form oracle rather than a tolerance, and it
///         is the only assertion in this file that does not depend on the DFG fit's own numbers.
///     </para>
///     <para>
///         <b>What it caught.</b> The split-sum approximation counts one bounce between microfacets,
///         and at high roughness most of the light takes more than one — with this library's own
///         <c>Ibl.EnvironmentDfg</c> at normal incidence a white metal keeps 1.000 of the light at
///         mirror roughness, 0.835 at 0.3, 0.670 at 0.6 and 0.450 at 1. So every rough metal in the
///         engine was rendered at up to half the radiance it sends, and darker the rougher it got,
///         which is backwards. <c>docs/plan/06</c> described the compensation for that as <em>on by
///         default</em>; <c>SpecularModels.MultiScatter</c> existed and had no caller anywhere.
///     </para>
///     <para>
///         ⚠ <b>And the function itself was wrong, which is the part no audit had reached.</b> Its
///         single-scatter term dropped <c>f0</c>, it returned one grey number for three tinted
///         channels, and its <c>1/dfg.y</c> factor diverges — this fit's bias term is 0.0059 at
///         mirror roughness and turns <em>negative</em> above about 0.75. At <c>f0 = 1</c> and normal
///         incidence the old body returns 170 at roughness 0, 737 at 0.6 and 505000 at 0.9. Wiring it
///         as written, which is what <c>#1155</c> proposed, would have made every metal in the frame
///         a white rail.
///     </para>
///     <para>
///         <b>How the picture is read.</b> The furnace is staged through
///         <c>AmbientCombine.Reflectance</c>, which is the one place in the tree where the same
///         specular scale meets a plane of radiance the fixture controls: a traced reflections plane
///         of constant <c>0.5</c>, an <c>f0</c> plane, and the normals plane's alpha carrying
///         perceptual roughness. The frame that comes back is <c>0.5 × scale</c> and nothing else —
///         no direct term, no albedo, no sky.
///     </para>
///     <para>
///         ⚠ <b>The dielectric column is what stops the furnace column being vacuous.</b> A scale
///         that returned one unconditionally would pass the metal half perfectly. At <c>f0 = 0</c> the
///         answer is the fit's bias term alone, a few per cent, and the two columns have to be an
///         order of magnitude apart.
///     </para>
///     <para>
///         ⚠ <b>What this cannot see, said plainly:</b> <c>Reflectance</c> saturates, so a scale that
///         overshoots one is clipped to one and reads as a pass. The old body's 505000 and a correct
///         1.000 are the same pixel here. What separates them is the dielectric column, where nothing
///         saturates.
///     </para>
///     <para>
///         <b>Sabotage, on device at the commit that added these.</b> Putting the bare split sum back
///         — <c>f0 · dfg.x + dfg.y</c> — turns both red immediately: the furnace fails at the very
///         first row, at perceptual roughness 0.02, reading 126 against the 128 it requires, and the
///         gap between the two columns closes to 100 by the bottom of the frame.
///     </para>
///     <para>
///         ⚠ So the second fixture is not independent of the first under <em>this</em> sabotage, and
///         saying so is better than implying two witnesses. What it is independent for is the failure
///         the first cannot see at all: a scale that ignores <c>f0</c> and returns one.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public sealed class MultiScatterFurnaceImageTests {
    const int Side = Fixture.Side;

    /// <summary>The radiance the traced plane carries, so a scale of one reads as half of full.</summary>
    /// <remarks>
    ///     ⚠ Half rather than one, so that the furnace's answer lands in the middle of the eight-bit
    ///     range instead of on the rail. On the rail, "exactly one" and "far too much" are the same
    ///     pixel — and the body this replaced returned five hundred thousand.
    /// </remarks>
    const float Radiance = 0.5f;

    /// <summary>Opens a device, or skips — unless the environment promised one.</summary>
    static bool TryOpen(out Fixture? fixture) {
        if (Fixture.TryOpen(out fixture, out var reason)) {
            return true;
        }

        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set, so the golden images may not be skipped: {reason}");
        }

        Assert.Skip(reason ?? "no Vulkan");
        return false;
    }

    /// <summary>A white metal reflects the whole environment at every roughness in the frame.</summary>
    /// <remarks>
    ///     Every row is a different perceptual roughness, from mirror at the top to fully rough at the
    ///     bottom, and all of them have to read the same number. ⚠ The rows are asserted individually
    ///     rather than as a mean: the loss the compensation puts back grows with roughness, so a mean
    ///     over the frame would let the bottom rows pay for the top ones.
    /// </remarks>
    [Fact]
    public void AWhiteMetalReflectsAllOfTheEnvironmentAtEveryRoughness() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        var image = Render(owned);

        var expected = (int)MathF.Round(Radiance * 255f);

        for (var row = 0; row < Side; row++) {
            var value = Metal(image, row);

            Assert.True(
                Math.Abs(value - expected) <= 1,
                $"at perceptual roughness {(row + 0.5f) / Side:F2} a white metal reflected {value} of "
                + $"an environment worth {expected} — it is absorbing light it cannot absorb."
            );
        }
    }

    /// <summary>And a dielectric reflects a few per cent of it, which is what stops that being vacuous.</summary>
    /// <remarks>
    ///     ⚠ A scale that returned one for everything would pass the furnace perfectly, so this is
    ///     the half that says the answer depends on <c>f0</c> at all. At <c>f0 = 0</c> the whole
    ///     answer is the fit's bias term plus the little multi-scatter a zero average Fresnel earns,
    ///     which is under five per cent everywhere and falls with roughness.
    /// </remarks>
    [Fact]
    public void ADielectricReflectsAlmostNoneOfIt() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        var image = Render(owned);

        for (var row = 0; row < Side; row++) {
            var metal = Metal(image, row);
            var dielectric = Dielectric(image, row);

            Assert.True(
                dielectric < 20,
                $"at perceptual roughness {(row + 0.5f) / Side:F2} a surface with no specular "
                + $"reflectance at all returned {dielectric}, so the scale is not reading f0."
            );

            Assert.True(
                metal - dielectric > 100,
                $"a white metal ({metal}) and a black dielectric ({dielectric}) are the same surface "
                + "to this scale, which cannot be right at any roughness."
            );
        }
    }

    // ── The fixture ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     A frame whose every pixel is <c>Radiance × Reflectance(f0, roughness)</c>: <c>f0 = 1</c> in
    ///     the left half, <c>f0 = 0</c> in the right, roughness rising down the rows.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The direct plane is black and the albedo plane is black, so nothing but the reflection
    ///         term reaches the output; no lighting is attached, so the sky contributes nothing
    ///         either. The reflections plane's alpha is one — validity, which the pass reads as "the
    ///         trace answered here".
    ///     </para>
    ///     <para>
    ///         ⚠ <b>No camera, deliberately, and the node degrades because of it.</b> Without one the
    ///         pass weighs every surface at normal incidence, which is exactly what this fixture
    ///         wants: through a real camera the view angle is a function of screen position, so
    ///         <c>NdotV</c> would vary down the rows along with the roughness and neither could be
    ///         held to a closed form. The furnace identity holds at every angle, so nothing is lost.
    ///     </para>
    /// </remarks>
    static Bitmap Render(Fixture fixture) {
        var device = fixture.Device;

        fixture.Graph.Reset();

        var direct = new Vector4[Side * Side];
        var albedo = new Vector4[Side * Side];
        var normals = new Vector4[Side * Side];
        var reflections = new Vector4[Side * Side];
        var specular = new Vector4[Side * Side];

        for (var y = 0; y < Side; y++) {
            var roughness = (y + 0.5f) / Side;

            for (var x = 0; x < Side; x++) {
                var index = (y * Side) + x;
                var metal = x < Side / 2 ? 1f : 0f;

                direct[index] = new(0f, 0f, 0f, 1f);
                albedo[index] = new(0f, 0f, 0f, 1f);
                normals[index] = new(0f, 0f, 1f, roughness);
                reflections[index] = new(Radiance, Radiance, Radiance, 1f);
                specular[index] = new(metal, metal, metal, 1f);
            }
        }

        var directPlane = Stage(fixture, "direct", direct);
        var albedoPlane = Stage(fixture, "albedo", albedo);
        var normalPlane = Stage(fixture, "normals", normals);
        var reflectionPlane = Stage(fixture, "reflections", reflections);
        var specularPlane = Stage(fixture, "specular", specular);

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
                    ["Core", "Geometry", "Shading"],
                    Path.Combine("PostFx", "Fullscreen.rvn"),
                    Path.Combine("PostFx", "AmbientCombine.rvn")
                )
            )
        );

        using var combine = new AmbientCombineRenderer {
            Name = "Combine",
            Direct = "Direct",
            Albedo = "Albedo",
            Normals = "Normals",
            Reflections = "Reflections",
            Specular = "Specular",
            Output = "Display",
            Modules = describer,
            Device = device,
            Samplers = samplers,
            Allocator = allocator
        };

        var compositor = new GraphicsCompositor(system) { FrameSize = new(Side, Side), Game = combine };

        compositor.Imports["Direct"] = Import(directPlane, "direct");
        compositor.Imports["Albedo"] = Import(albedoPlane, "albedo");
        compositor.Imports["Normals"] = Import(normalPlane, "normals");
        compositor.Imports["Reflections"] = Import(reflectionPlane, "reflections");
        compositor.Imports["Specular"] = Import(specularPlane, "specular");

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
        Assert.True(combine.Pass.PipelineCount > 0, "the pass compiled no pipeline, so it drew nothing");

        return fixture.Render(
            frame.Texture("harness", "Display"),
            commands => {
                Upload(commands, directPlane);
                Upload(commands, albedoPlane);
                Upload(commands, normalPlane);
                Upload(commands, reflectionPlane);
                Upload(commands, specularPlane);
            }
        );
    }

    static (TextureHandle Texture, TextureViewHandle View, BufferHandle Staging) Stage(
        Fixture fixture,
        string name,
        Vector4[] texels
    ) =>
        fixture.Sampled(name, Side, MemoryMarshal.AsBytes<Vector4>(texels), PixelFormat.Rgba32Float);

    static ImportedTexture Import(
        (TextureHandle Texture, TextureViewHandle View, BufferHandle Staging) plane,
        string name
    ) =>
        new(
            plane.Texture,
            plane.View,
            new(
                PixelFormat.Rgba32Float,
                Side,
                Side,
                TextureUsage.Sampled | TextureUsage.CopyDestination,
                Name: name
            ),
            ResourceState.ShaderRead
        );

    static void Upload(
        ICommandList commands,
        (TextureHandle Texture, TextureViewHandle View, BufferHandle Staging) plane
    ) {
        commands.Barrier(new([], [new(plane.Texture, ResourceState.Undefined, ResourceState.CopyDestination)]));
        commands.CopyBufferToTexture(plane.Staging, 0, new(plane.Texture), new(Side, Side, 1));
        commands.Barrier(new([], [new(plane.Texture, ResourceState.CopyDestination, ResourceState.ShaderRead)]));
    }

    /// <summary>The metal half's value on a row — a quarter in, well away from the seam.</summary>
    static int Metal(in Bitmap image, int y) => image.Pixels[image.Offset(Side / 4, y)];

    /// <summary>And the dielectric half's, the same distance from the other edge.</summary>
    static int Dielectric(in Bitmap image, int y) => image.Pixels[image.Offset(Side * 3 / 4, y)];
}
