// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Vixen.Graphics.Vulkan;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Features;
using Vixen.Rendering.Materials;
using Vixen.ShaderCompiler;
using Vixen.Shaders;
using Vixen.Shaders.Generated;
using Vixen.Ui.Testing.Visual;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>
///     What a per-object light list too short for the scene actually costs, from both sides.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="ForwardLightingRenderFeature.MaxLightsPerObject" /> sizes the block the host
///         writes and <c>ClusteredShading.rvn</c>'s <c>MaxLights</c> sizes the array the shader reads
///         out of it. They shipped as eight against sixteen with nothing between them —
///         <c>CascadeCount</c>'s defect, one array along — and the quality tiers named a third number
///         that reached neither.
///     </para>
///     <para>
///         ⚠ <b>The consequence is not the one the comments in this tree described, and that is what
///         this fixture is for.</b> "The shader reads past what the host filled" cannot happen in
///         either direction: the loop in <c>Punctual</c> is bounded by <c>MaxLights</c> and broken
///         out of at <c>lightCount</c>, and the feature never writes a count longer than the block it
///         sized. What happens instead is that <em>the shorter of the two wins in silence</em>, and
///         which side did the dropping is invisible from the picture alone — so it is measured here
///         by moving one side at a time.
///     </para>
///     <para>
///         Twelve identical lights in a ring around the quad's axis, so that the centre pixel is
///         lit by every one of them equally and its brightness counts them. Identical rather than
///         graded because <c>Select</c> ranks by score and the ranking is not what is under test:
///         with twelve equal candidates any subset of <em>n</em> produces the same picture, so a
///         difference between two frames is a difference in <em>how many</em> and nothing else.
///     </para>
///     <para>
///         What it measures, in the red channel of the centre pixel, on an M1 Max: four lights
///         0.090, eight 0.180, twelve 0.271 — linear in the count, as twelve equal lights should be.
///         (host 12, shader 4) is 0.090, the same as (host 4, shader 4); (host 8, shader 16) is
///         0.180 and (host 12, shader 16) is 0.271. The longer side never costs anything and the
///         shorter side is what the frame gets.
///     </para>
///     <para>
///         The fixture asserts no validation errors, as every fixture here does, which is the other
///         half of the answer: the eight-against-sixteen pairing binds a 768-byte per-draw range at a
///         block the variant declares 1296 bytes of, and no layer on this device has anything to say
///         about it.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public sealed class MaxLightsDeviceTests {
    /// <summary>Where the quad sits, and how big it is.</summary>
    const float QuadZ = -6f;

    const float QuadHalf = 3f;

    /// <summary>How many lights the scene has, which is more than any budget here.</summary>
    const int SceneLights = 12;

    /// <summary>How far the ring of lights stands off the quad's axis.</summary>
    /// <remarks>
    ///     Small against the quad's own half-side, so every light in the ring is the same distance
    ///     from the centre pixel and reaches it at the same angle — which is what makes the centre's
    ///     brightness a count.
    /// </remarks>
    const float RingRadius = 1f;

    /// <summary>How far in front of the quad the ring floats.</summary>
    const float RingOffset = 2f;

    /// <summary>One light's intensity, in lumens.</summary>
    /// <remarks>
    ///     Low, because twelve of them add: the target is <c>Rgba8UNorm</c> and this pass tone-maps
    ///     nothing, so a scene bright enough to clip would make eight lights and twelve look the
    ///     same. <see cref="Twelve_lights_reach_a_budget_of_twelve" /> asserts the headroom rather
    ///     than assuming it.
    /// </remarks>
    const float Intensity = 0.4f;

    /// <summary>What the pass clears to: a blue no light in this fixture can produce.</summary>
    static Color4 Background => new(0f, 0f, 0.25f, 1f);

    static RenderCamera Camera => RenderCamera.Default with { Position = Vector3.Zero, AspectRatio = 1f };

    /// <summary>
    ///     A budget as long as the scene shades with every light, and does not clip.
    /// </summary>
    /// <remarks>
    ///     The reference the other two cases are measured against, and the assertion that makes them
    ///     mean anything: a frame already at white would hide every drop below it.
    /// </remarks>
    [Fact]
    public void Twelve_lights_reach_a_budget_of_twelve() {
        if (!Fixture.TryOpen(out var fixture, out var reason)) {
            Skip(reason);
            return;
        }

        using var owned = fixture!;
        var image = Render(owned, SceneLights, SceneLights);

        var corner = Pixel(image, 2, 2);

        Assert.True(corner.Z > 0.2f && corner.X < 0.05f, $"the pass did not clear: {corner}");

        var centre = Pixel(image, image.Width / 2, image.Height / 2).X;

        Assert.True(centre > 0.2f, $"the quad is not lit at all: {centre:0.000}");
        Assert.True(centre < 0.95f, $"the frame clips at twelve lights, so no drop below it is visible: {centre:0.000}");
    }

    /// <summary>
    ///     A host budget shorter than the scene drops lights, and the shader never sees them.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The engine's own default pairing until this was wired: eight in the block against a
    ///         variant compiled for the <c>.rvn</c>'s sixteen. The shader has room for sixteen and is
    ///         told <c>lightCount</c> is eight, so it shades with eight — which is
    ///         <see cref="ForwardLightingRenderFeature.Select" /> having dropped four before anything
    ///         was written, not the shader reading anything it should not.
    ///     </para>
    ///     <para>
    ///         ⚠ The eight-light frame is <em>dimmer</em> and nothing else: no artefact, no noise, no
    ///         refusal. That is why a scene with more lights than a budget is the only thing that can
    ///         see this at all.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_short_host_budget_drops_lights_before_the_shader_sees_them() {
        if (!Fixture.TryOpen(out var fixture, out var reason)) {
            Skip(reason);
            return;
        }

        using var owned = fixture!;

        var all = Centre(Render(owned, SceneLights, SceneLights));
        var eight = Centre(Render(owned, 8, 16));

        Assert.True(
            eight < all * 0.8f,
            $"a block sized for eight shaded as brightly as one sized for twelve — {eight:0.000} against "
            + $"{all:0.000}, from twelve lights that all reach the object"
        );

        // And it is a count rather than a collapse: two thirds of the lights are still there.
        Assert.True(eight > all * 0.4f, $"eight of twelve lights is not two thirds of the light: {eight:0.000}");
    }

    /// <summary>
    ///     A shader array shorter than the block drops lights the host did write.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The other direction, which is the one a host raising its budget without publishing it
    ///         lands in — sample 13 sets twenty-four and would have kept sixteen. The block holds
    ///         twelve lights and says so; the variant's loop is bounded by its own four and stops
    ///         there.
    ///     </para>
    ///     <para>
    ///         Asserted against a frame whose <em>host</em> budget is four, because the claim is that
    ///         the two are indistinguishable: whichever side is shorter, the picture is the same
    ///         number of lights. A fixture that only compared bright to dim could not tell which side
    ///         had done the dropping.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_short_shader_array_drops_lights_the_host_did_write() {
        if (!Fixture.TryOpen(out var fixture, out var reason)) {
            Skip(reason);
            return;
        }

        using var owned = fixture!;

        var starved = Centre(Render(owned, SceneLights, 4));
        var matched = Centre(Render(owned, 4, 4));
        var all = Centre(Render(owned, SceneLights, SceneLights));

        Assert.True(
            starved < all * 0.6f,
            $"a variant compiled for four shaded with more than four of the twelve the block held: "
            + $"{starved:0.000} against {all:0.000}"
        );

        Assert.True(
            Math.Abs(starved - matched) < 0.02f,
            $"a block of twelve read by an array of four is not the same picture as a block of four: "
            + $"{starved:0.000} against {matched:0.000}"
        );
    }

    // --- The frame ----------------------------------------------------------

    /// <summary>The lit centre of the quad, as a channel in 0..1.</summary>
    static float Centre(in Bitmap image) => Pixel(image, image.Width / 2, image.Height / 2).X;

    /// <summary>Draws one unclustered frame at a host budget and a compiled array length.</summary>
    /// <param name="fixture">The device.</param>
    /// <param name="budget">What the feature sizes its per-object block from.</param>
    /// <param name="declared">What the shading variant sizes <c>lights[]</c> from.</param>
    static Bitmap Render(Fixture fixture, int budget, int declared) {
        var device = fixture.Device;
        var material = Composed();

        // ⚠ Every case here is a *comparison* between two frames, so one fixture draws several — and
        // a graph keeps the resources of the frame it compiled. Without this the second build refuses
        // the import outright, which is a failure that says nothing about lights.
        fixture.Graph.Reset();

        material.Parameters.Set(ForwardPlusKeys.UseImageBasedLighting, false);
        material.Parameters.Set(ForwardPlusKeys.UseShadows, false);
        material.Parameters.Set(ForwardPlusKeys.UseReflectionProbe, false);

        var loader = new EffectLoader(device);
        var effects = new EffectSystem();
        effects.AddProvider(new Compiling(loader, _ => RavenEffects.Everything()));

        var effect = effects.Resolve(Key(material.Composition, declared));

        Assert.NotNull(effect);

        // ⚠ The variant really is the length asked for, before any picture is read off it. Everything
        // below measures a difference between two frames, and two frames compiled the same way differ
        // by nothing at all — which would read as "the shader does not drop lights".
        Assert.Contains(
            effect!.Bindings,
            binding => binding.Set == DescriptorSetSlot.PerDraw
                && binding.Size == ForwardLightingRenderFeature.HeaderSize + (declared * 80)
        );

        using var allocator = new DescriptorAllocator(device);
        using var samplers = new SamplerCache(device);
        using var system = new RenderSystem();

        using var view = new ViewConstants(device) {
            Descriptors = allocator,
            Layout = effect.SetLayouts[(int)DescriptorSetSlot.PerView]
        };

        using var scene = new SceneConstants(device) { Descriptors = allocator };
        var opaque = system.AddStage(new("Opaque"));

        var describer = new EffectPipelineDescriber(device);

        var formats = new[] {
            VertexFormat.Float32X3, VertexFormat.Float32X3, VertexFormat.Float32X4, VertexFormat.Float32X2
        };

        var offsets = new[] { 0, 12, 24, 40 };

        describer.VertexLayouts.Add([
            new VertexBufferLayout(
                Vertex.Stride,
                [
                    .. effect.VertexInputs.Select(
                        (input, index) => new VertexElement((uint)input.Location, formats[index], offsets[index])
                    )
                ]
            )
        ]);

        var meshes = new MeshRenderFeature { Pipelines = new(device), Describer = describer };
        var materials = new MaterialRenderFeature { Effects = effects, Device = device, Descriptors = allocator };

        // ⚠ `Materials` is what the per-object set's layout comes off, and the block this fixture is
        // about is that set: unset, the frame binds no set 3 and every draw in the pass is refused.
        var lighting = new ForwardLightingRenderFeature {
            Device = device,
            Scene = scene.Parameters,
            Materials = materials,
            MaxLightsPerObject = budget
        };

        var transforms = new TransformRenderFeature { Device = device, Scene = scene.Parameters };

        meshes.Add(transforms);
        meshes.Add(materials);
        meshes.Add(lighting);
        system.AddFeature(meshes);

        materials.PermutationKeys["ForwardPlus"] = ForwardPlusKeys.UsedPermutationKeys;
        materials.PermutationSources[ForwardPlusKeys.UseClusteredLights] = lighting.PermutationKeys[0];

        // ⚠ The line the whole task is about, and what `CompositorBuilder.LightBudget` now does for a
        // document. Without it the block is `budget` long and the variant is the shader's declared
        // sixteen, whatever this fixture asked for.
        materials.SetPermutation("ForwardPlus", ForwardLightingRenderFeature.MaxLightsKey("ForwardPlus"), declared);

        var camera = new RenderView("camera") { Camera = Camera, Stages = opaque.Mask };
        camera.Frustum = new(camera.ViewProjection);
        system.SetViews([camera]);

        var quad = system.Objects.Add(
            new() {
                Bounds = new(new Vector3(0f, 0f, QuadZ), QuadHalf * 1.5f),
                Stages = opaque.Mask,
                FeatureIndex = meshes.Index
            }
        );

        system.Objects.Data.Data(meshes.Draws)[quad.Index] = new() {
            VertexBuffer = fixture.Buffer<Vertex>(Vertex.Quad, BufferUsage.Vertex),
            IndexBuffer = fixture.Buffer<ushort>([0, 1, 2, 2, 1, 3], BufferUsage.Index),
            IndexFormat = IndexFormat.UInt16,
            Count = 6,
            InstanceCount = 1
        };

        system.Objects.Data.Data(transforms.World)[quad.Index] = Matrix4x4.Identity;
        materials.Assign(system, quad, material);

        foreach (var light in Ring()) {
            lighting.Lights.Add(light);
        }

        var unused = Fill(scene, samplers, device, fixture);

        var pass = new RenderPassRenderer {
            Name = "Forward",
            ClearColour = Background,
            SceneConstants = scene,
            Children = { new SingleStageRenderer { View = camera, Stage = opaque, Constants = view } }
        };

        pass.ColourTargets.Add("Display");
        pass.SceneBuffers["clusters"] = "Clusters";

        var display = fixture.Owned("Display", TextureUsage.ColourTarget | TextureUsage.CopySource);

        var compositor = new GraphicsCompositor(system) {
            FrameSize = new(Fixture.Side, Fixture.Side),
            Game = new SceneRendererSequence { Children = { pass } }
        };

        compositor.Imports["Display"] = new(
            display.Texture,
            display.View,
            display.Description,
            ResourceState.Undefined,
            ResourceState.CopySource
        );

        // Imported and empty: this frame is unclustered, and a permutation folds code rather than
        // bindings — so the grid is still declared and a set one binding short is not bound at all.
        compositor.BufferImports["Clusters"] = new(
            fixture.Buffer<uint>(new uint[ClusterGrid.BufferSize / sizeof(uint)], BufferUsage.Storage),
            new(ClusterGrid.BufferSize, BufferUsage.Storage, MemoryAccess.HostUpload, "Clusters"),
            ResourceState.ShaderRead,
            ResourceState.ShaderRead
        );

        allocator.BeginFrame();
        var frame = compositor.Build(fixture.Graph, effects, device);

        Assert.Empty(effects.Misses);

        var picture = fixture.Render(
            frame.Texture("harness", "Display"),
            list => list.Barrier(
                new(
                    [],
                    [
                        .. unused.Select(
                            texture => new TextureBarrier(texture, ResourceState.Undefined, ResourceState.ShaderRead)
                        )
                    ]
                )
            )
        );

        Assert.True(scene.IsComplete, "set 0 was left incomplete, so the frame bound none of it");
        Assert.True(materials.BoundCount > 0, "set 2 was left incomplete, so the material bound none of it");

        // What the host decided to write, which is the other half of the answer the picture gives:
        // the count in the block is never longer than the block, whatever the scene holds.
        Assert.Equal(Math.Min(SceneLights, budget), Assigned(system, lighting, quad));

        return picture;
    }

    /// <summary>How many lights the feature actually wrote into one object's block.</summary>
    static int Assigned(RenderSystem system, ForwardLightingRenderFeature lighting, RenderObjectId id) =>
        system.Objects.Data.Data(lighting.Assignments)[id.Index].Count;

    /// <summary>Twelve identical lights, evenly spaced around the quad's axis.</summary>
    static IEnumerable<RenderLight> Ring() =>
        Enumerable.Range(0, SceneLights)
            .Select(
                index => {
                    var angle = index * MathF.Tau / SceneLights;

                    return RenderLight.Point(
                        new(RingRadius * MathF.Cos(angle), RingRadius * MathF.Sin(angle), QuadZ + RingOffset),
                        8f,
                        new(1f, 1f, 1f),
                        Intensity
                    );
                }
            );

    /// <summary>The variant under test: unclustered, with a light array of a given length.</summary>
    static EffectKey Key(ShaderComposition composition, int declared) =>
        EffectKey.Of(
            "ForwardPlus",
            [
                new("ForwardPlus.MaxLights", declared.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new("ForwardPlus.UseClusteredLights", "false"),
                new("ForwardPlus.UseImageBasedLighting", "false"),
                new("ForwardPlus.UseShadows", "false"),
                new("ForwardPlus.UseReflectionProbe", "false")
            ],
            composition
        );

    /// <summary>Fills set 0 with everything the variant declares, sampled or not.</summary>
    static TextureHandle[] Fill(SceneConstants scene, SamplerCache samplers, VulkanDevice device, Fixture fixture) {
        // No sun. Every photon in the picture came out of the per-object list, which is the point.
        scene.Parameters.Set(ForwardPlusKeys.LightDirection, Vector3.Zero);
        scene.Parameters.Set(ForwardPlusKeys.LightColor, Vector3.Zero);
        ClusterGrid.Apply(scene.Parameters, Camera, "ForwardPlus");

        var flat = Flat(device, fixture);
        var cube = Cube(device, fixture);
        var probes = Cube(device, fixture);

        scene.Parameters.Set(ForwardPlusKeys.ShadowMap, flat.View);
        scene.Parameters.Set(ForwardPlusKeys.Environment, cube.View);
        scene.Parameters.Set(ForwardPlusKeys.Probes, probes.View);

        scene.Parameters.Set(ForwardPlusKeys.ShadowSampler, samplers.PointClamp);
        scene.Parameters.Set(ForwardPlusKeys.EnvironmentSampler, samplers.LinearClamp);
        scene.Parameters.Set(ForwardPlusKeys.ProbeSampler, samplers.LinearClamp);

        return [flat.Texture, cube.Texture, probes.Texture];
    }

    static (TextureHandle Texture, TextureViewHandle View) Flat(VulkanDevice device, Fixture fixture) {
        var texture = device.CreateTexture(
            new() {
                Width = 4, Height = 4, Depth = 1, MipLevels = 1, ArrayLayers = 1, SampleCount = 1,
                Dimension = TextureDimension.Texture2D,
                Format = PixelFormat.Rgba8UNorm, Usage = TextureUsage.Sampled, Name = "unused"
            }
        );

        var handle = device.CreateTextureView(texture);
        fixture.Owns(() => device.Destroy(texture));

        return (texture, handle);
    }

    static (TextureHandle Texture, TextureViewHandle View) Cube(VulkanDevice device, Fixture fixture) {
        var texture = device.CreateTexture(
            new() {
                Width = 4, Height = 4, Depth = 1, MipLevels = 1, ArrayLayers = 6, SampleCount = 1,
                Dimension = TextureDimension.TextureCube,
                Format = PixelFormat.Rgba8UNorm, Usage = TextureUsage.Sampled, Name = "unused cube"
            }
        );

        var handle = device.CreateTextureView(texture, arrayLayerCount: 6);
        fixture.Owns(() => device.Destroy(texture));

        return (texture, handle);
    }

    /// <summary>One pixel, as channels in 0..1.</summary>
    static Vector3 Pixel(in Bitmap image, int x, int y) {
        var offset = image.Offset(Math.Clamp(x, 0, image.Width - 1), Math.Clamp(y, 0, image.Height - 1));

        return new(image.Pixels[offset] / 255f, image.Pixels[offset + 1] / 255f, image.Pixels[offset + 2] / 255f);
    }

    /// <summary>What the vertex stage reads: position, normal, tangent, texcoord.</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct Vertex {
        public const int Stride = 48;

        public Vector3 Position;
        public Vector3 Normal;
        public Vector4 Tangent;
        public Vector2 Texcoord;

        /// <summary>A quad facing the camera at <see cref="QuadZ" />, filling most of the view.</summary>
        public static Vertex[] Quad => [
            Corner(-QuadHalf, -QuadHalf),
            Corner(QuadHalf, -QuadHalf),
            Corner(-QuadHalf, QuadHalf),
            Corner(QuadHalf, QuadHalf)
        ];

        static Vertex Corner(float x, float y) =>
            new() {
                Position = new(x, y, QuadZ),
                Normal = new(0f, 0f, 1f),
                Tangent = new(1f, 0f, 0f, 1f),
                Texcoord = new((x / QuadHalf * 0.5f) + 0.5f, (y / QuadHalf * 0.5f) + 0.5f)
            };
    }

    /// <summary>The material whose composition the pass is compiled against.</summary>
    static Material Composed() {
        var compilation = MaterialCompiler.Compile(
            new() {
                ShaderName = "ForwardPlus",
                Features = [new MetalRoughnessFeature { BaseColor = Vector3.One, Metalness = 0f, Roughness = 0.6f }]
            }
        );

        Assert.False(
            compilation.Failed,
            string.Join(Environment.NewLine, compilation.Diagnostics.Select(diagnostic => diagnostic.ToString()))
        );

        return compilation.Material!;
    }

    /// <summary>Skips when there is no device, unless the environment insists on one.</summary>
    static void Skip(string? reason) {
        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set and no device could be opened: {reason}");
        }

        Assert.Skip(reason ?? "no Vulkan");
    }
}
