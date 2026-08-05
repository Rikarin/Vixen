// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics.RenderGraph;
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
///     A sun shadow, at every cascade count a quality tier ships.
/// </summary>
/// <remarks>
///     <para>
///         <strong>The count is a permutation, so it is a property of the compiled shader.</strong>
///         <c>ClusteredShading.rvn</c> sizes <c>cascades[CascadeCount]</c> from it and folds the atlas
///         into <c>2 × ceil(count/2)</c> tiles from it, and <see cref="ShadowMapRenderer" /> does the
///         same arithmetic on the host. Nothing joined the two: the host published two cascades into a
///         pass compiled for four, so the lookup folded 2 × 2 against tiles laid out 2 × 1,
///         <c>CascadeContaining</c> answered −1 across most of the screen, and the frame came back
///         <em>with no sun shadow in it at all</em>. Every tier below High ships fewer than four
///         cascades, so every project running on Low drew that frame.
///     </para>
///     <para>
///         <strong>Why this cannot be a structural test, and one exists that proves it.</strong>
///         <c>ForwardFrameTests.Every_cascade_the_shader_declares_is_filled</c> checks that the host
///         fills every slot the shader declares and passes at any count, because its fixture builds
///         both sides from the same number. The disagreement only exists once a <em>variant</em> has
///         been compiled, which needs a compiler and a device — so the assertion has to be a picture.
///     </para>
///     <para>
///         The scene is a plan view with the sun straight down, which makes the whole thing
///         predictable without a matrix: a square caster floating over a ground plane puts a square
///         shadow directly beneath itself, in the middle of the image, whatever the cascades do. What
///         varies across the four cases is only which tile of which fold the lookup has to find.
///     </para>
///     <para>
///         ⚠ <b>The lit probe is what makes the shadow probe mean anything.</b> A frame that failed to
///         draw the ground at all is black everywhere, which is a passing "the middle is dark" on its
///         own — so the ground is asserted lit before it is asserted shadowed.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public sealed class CascadeCountDeviceTests {
    /// <summary>How far above the ground the caster floats.</summary>
    const float CasterHeight = 6f;

    /// <summary>Half the caster's side, in world units.</summary>
    const float CasterHalf = 4f;

    /// <summary>Half the ground's side. Larger than the view, so the frame has no horizon in it.</summary>
    const float GroundHalf = 30f;

    /// <summary>How far up the camera is, looking straight down.</summary>
    const float Eye = 30f;

    /// <summary>One cascade's side in the atlas.</summary>
    const int Resolution = 512;

    /// <summary>
    ///     How far shadows reach — and the range the splits divide, which is what varies the tiles.
    /// </summary>
    /// <remarks>
    ///     Twice the camera's height, so the ground at view depth 30 lands in the <em>last</em>
    ///     cascade at every count: tile 0 of 1, tile 1 of a 2 × 1 fold, tile 2 of a 2 × 2 fold with
    ///     three in it, and tile 3 of a full 2 × 2. Four different tiles from one scene, which is the
    ///     arithmetic the two sides have to agree about.
    /// </remarks>
    const float ShadowDistance = 60f;

    /// <summary>Straight down, so the shadow lands under the caster and needs no trigonometry.</summary>
    static Vector3 Sun => new(0f, -1f, 0f);

    /// <summary>Looking straight down at the ground from <see cref="Eye" />.</summary>
    /// <remarks>
    ///     Up is −Z rather than anything else because <see cref="ShadowCascades.Fit" /> asks for it
    ///     exactly here: a light this close to the world's up axis has no usable reference of its own,
    ///     and the camera's is the tie-break. Any perpendicular choice would do; this one puts +X to
    ///     the right of the image.
    /// </remarks>
    static RenderCamera Camera =>
        RenderCamera.Default with {
            Position = new(0f, Eye, 0f),
            Forward = new(0f, -1f, 0f),
            Up = new(0f, 0f, -1f),
            AspectRatio = 1f
        };

    /// <summary>
    ///     A caster over a ground plane casts a shadow, at one cascade and at four.
    /// </summary>
    /// <param name="cascades">
    ///     What a tier ships: Low is two, and every count below four was broken in the same way.
    /// </param>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void A_caster_shadows_the_ground_at_every_cascade_count(int cascades) {
        if (!Fixture.TryOpen(out var fixture, out var reason)) {
            Skip(reason);
            return;
        }

        using var owned = fixture!;
        var image = Render(owned, cascades);

        // The ground is drawn and lit. Without this the assertion below passes on a black frame,
        // which is what half the failures in this family actually look like.
        var lit = Luminance(image, 12, 12);

        Assert.True(lit > 0.15, $"{cascades} cascade(s): the ground is not lit anywhere: {lit:0.000}");

        // And the middle, which is under the caster, is not.
        var shadowed = Luminance(image, image.Width / 2, image.Height / 2);

        Assert.True(
            shadowed < lit * 0.5,
            $"{cascades} cascade(s): the ground under the caster is not shadowed — {shadowed:0.000} "
            + $"against {lit:0.000} in the open. A shading pass compiled for a different count folds "
            + "the atlas into a differently shaped grid, which is a frame with no sun shadow in it."
        );
    }

    // --- The frame ----------------------------------------------------------

    /// <summary>Draws one shadowed frame at a given cascade count and reads the picture back.</summary>
    static Bitmap Render(Fixture fixture, int cascades) {
        var device = fixture.Device;
        var material = Composed();

        // The ambient term off, so what is in the picture is the sun and the shadow and nothing else:
        // with image-based lighting on, a shadowed fragment keeps a cube's worth of light and the
        // contrast the assertion measures is whatever the environment happened to be.
        material.Parameters.Set(ForwardPlusKeys.UseImageBasedLighting, false);
        material.Parameters.Set(ForwardPlusKeys.UseReflectionProbe, false);
        material.Parameters.Set(ForwardPlusKeys.UseShadows, true);

        var loader = new EffectLoader(device);
        var effects = new EffectSystem();
        effects.AddProvider(new Compiling(loader, _ => RavenEffects.Everything()));

        var shading = effects.Resolve(Key(material.Composition));

        Assert.NotNull(shading);

        using var allocator = new DescriptorAllocator(device);
        using var samplers = new SamplerCache(device);
        using var system = new RenderSystem();

        // One block for both passes, which is what set 1 is: `Bind` allocates it against whichever
        // pipeline is about to read it, so the caster's empty set 0 does not make the shading pass's
        // set 1 unbindable in it. `Layout` is only the fallback for a pass with no effect in hand.
        using var view = new ViewConstants(device) {
            Descriptors = allocator,
            Layout = shading!.SetLayouts[(int)DescriptorSetSlot.PerView]
        };

        using var scene = new SceneConstants(device) { Descriptors = allocator };

        // Two-sided, both of them. The geometry is two flat quads, so a winding mistake in the
        // fixture's own vertices would read as a missing shadow — which is the thing under test.
        var opaque = system.AddStage(new("Opaque") { Rasterizer = RasterizerState.TwoSided });

        // The shipped shadow stage, as `StandardFrame` declares it: the caster's own shader imposed
        // on whatever material an object carries, because a shadow is a depth and not a surface.
        var casters = system.AddStage(new("Shadow") {
            ShaderName = "ShadowCaster",
            Rasterizer = RasterizerState.TwoSided
        });

        var describer = new EffectPipelineDescriber(device);

        // A schema rather than a layout, and it is load-bearing here in a way it is not in a fixture
        // with one pass: one mesh is read by two shaders whose vertex inputs take different locations,
        // and a schema is resolved per effect where a layout has the numbers already baked in.
        describer.VertexSchemas.Add(SurfaceVertex.Schema);

        var meshes = new MeshRenderFeature { Pipelines = new(device), Describer = describer };
        var materials = new MaterialRenderFeature { Effects = effects, Device = device, Descriptors = allocator };
        // ⚠ `Materials` is load-bearing on an unclustered frame: `ForwardPlus` reads its per-object
        // light list out of set 3, and the layout for that set is the shading variant's — which this
        // feature can only learn from the one that resolved it. Unset, the first frame binds no set 3
        // at all and every draw in the pass is refused.
        var lighting = new ForwardLightingRenderFeature {
            Device = device,
            Scene = scene.Parameters,
            Materials = materials
        };
        var transforms = new TransformRenderFeature { Device = device, Scene = scene.Parameters };

        meshes.Add(transforms);
        meshes.Add(materials);
        meshes.Add(lighting);
        system.AddFeature(meshes);

        materials.PermutationKeys["ForwardPlus"] = ForwardPlusKeys.UsedPermutationKeys;

        // What the caster's own set 2 needs and a material has no opinion about — see
        // `RenderStage.Parameters`. A set is written wholly or not at all, so without these the whole
        // shadow pass is refused and the atlas stays at its clear value: an unshadowed frame, for a
        // reason that has nothing to do with cascades.
        var blank = Blank(device, fixture);

        casters.Parameters.Set(ParameterKeys.New<TextureViewHandle>("ShadowCaster.opacityMap"), blank);
        casters.Parameters.Set(ParameterKeys.New<SamplerHandle>("ShadowCaster.opacitySampler"), samplers.PointClamp);

        casters.Parameters.Set(
            ParameterKeys.New<BufferHandle>("ShadowCaster.bones"),
            fixture.Buffer<Matrix4x4>([Matrix4x4.Identity], BufferUsage.Storage)
        );

        var camera = new RenderView("camera") { Camera = Camera, Stages = opaque.Mask };
        camera.Frustum = new(camera.ViewProjection);
        system.SetViews([camera]);

        var quad = fixture.Buffer<SurfaceVertex>(Quad, BufferUsage.Vertex);
        var indices = fixture.Buffer<ushort>([0, 1, 2, 2, 1, 3], BufferUsage.Index);

        // The ground: shaded, and never a caster. A ground plane in the shadow stage would shadow
        // itself, and the acne that produces is not what this fixture is measuring.
        Add(system, meshes, materials, transforms, quad, indices, material, opaque.Mask, Vector3.Zero, GroundHalf);

        // And the caster: in the shadow stage alone, so it is not in the picture at all. What the
        // picture holds is the ground, and the square of it the caster took the sun from.
        Add(
            system,
            meshes,
            materials,
            transforms,
            quad,
            indices,
            material,
            casters.Mask,
            new(0f, CasterHeight, 0f),
            CasterHalf
        );

        var atlasSize = ShadowCascades.AtlasSize(cascades, Resolution);

        var atlas = fixture.Owned(
            "ShadowAtlas",
            TextureUsage.DepthStencilTarget | TextureUsage.Sampled,
            PixelFormat.Depth32Float,
            atlasSize.X,
            atlasSize.Y
        );

        var shadows = new ShadowMapRenderer {
            Name = "Sun",
            CasterStage = casters,
            Atlas = "ShadowAtlas",
            CascadeCount = cascades,
            Resolution = Resolution,
            Camera = camera,
            ShadowDistance = ShadowDistance,
            LightDirection = Sun,
            Constants = view,
            Scene = scene.Parameters,
            Samplers = samplers
        };

        // ⚠ The line this fixture exists for. Without it the host fits `cascades` and the shading pass
        // is compiled for the shader's declared four, whatever the node was told.
        materials.SetPermutation("ForwardPlus", ShadowMapRenderer.CascadeCountKey("ForwardPlus"), cascades);

        var pass = new RenderPassRenderer {
            Name = "Main",
            ClearColour = new(0f, 0f, 0f, 1f),
            SceneConstants = scene,
            Children = { new SingleStageRenderer { View = camera, Stage = opaque, Constants = view } }
        };

        pass.ColourTargets.Add("Display");

        // The atlas reaches set 0 as a frame resource rather than as a handle the fixture wrote: the
        // pass that declares it reads it is the one that may put it in front of a shader, which is
        // what puts the barrier in.
        pass.SceneTextures["shadowMap"] = "ShadowAtlas";
        pass.SceneBuffers["lightBuffer"] = "SceneLights";
        pass.SceneBuffers["clusters"] = "Clusters";

        var display = fixture.Owned("Display", TextureUsage.ColourTarget | TextureUsage.CopySource);
        var unused = Fill(scene, samplers, device, fixture);

        var compositor = new GraphicsCompositor(system) {
            FrameSize = new(Fixture.Side, Fixture.Side),
            Game = new SceneRendererSequence { Children = { shadows, pass } }
        };

        compositor.Imports["Display"] = new(
            display.Texture,
            display.View,
            display.Description,
            ResourceState.Undefined,
            ResourceState.CopySource
        );

        compositor.Imports["ShadowAtlas"] = new(atlas.Texture, atlas.View, atlas.Description);

        // Imported and empty. This frame is unclustered and has no punctual lights at all, and a
        // permutation folds *code* rather than bindings — so both buffers are still declared, and a
        // set one binding short is not bound at all. Imported rather than declared because nothing in
        // the frame writes them, and the graph is right to refuse a read with no producer.
        Import(
            compositor,
            "SceneLights",
            fixture.Buffer<PunctualLightData>([default], BufferUsage.Storage),
            Marshal.SizeOf<PunctualLightData>()
        );

        Import(
            compositor,
            "Clusters",
            fixture.Buffer<uint>(new uint[ClusterGrid.BufferSize / sizeof(uint)], BufferUsage.Storage),
            ClusterGrid.BufferSize
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

        return picture;
    }

    /// <summary>Lends the frame a storage buffer the graph does not have to produce.</summary>
    static void Import(GraphicsCompositor compositor, string name, BufferHandle buffer, long size) =>
        compositor.BufferImports[name] = new(
            buffer,
            new(size, BufferUsage.Storage, MemoryAccess.HostUpload, name),
            ResourceState.ShaderRead,
            ResourceState.ShaderRead
        );

    /// <summary>The variant under test: shadows on, and nothing else that could light a fragment.</summary>
    static EffectKey Key(ShaderComposition composition) =>
        EffectKey.Of(
            "ForwardPlus",
            [
                new("ForwardPlus.UseShadows", "true"),
                new("ForwardPlus.UseClusteredLights", "false"),
                new("ForwardPlus.UseImageBasedLighting", "false"),
                new("ForwardPlus.UseReflectionProbe", "false")
            ],
            composition
        );

    /// <summary>Adds one axis-aligned quad at a height, scaled to a half-side.</summary>
    static void Add(
        RenderSystem system,
        MeshRenderFeature meshes,
        MaterialRenderFeature materials,
        TransformRenderFeature transforms,
        BufferHandle vertices,
        BufferHandle indices,
        Material material,
        RenderStageMask stages,
        Vector3 centre,
        float half
    ) {
        var id = system.Objects.Add(
            new() { Bounds = new(centre, half * 1.5f), Stages = stages, FeatureIndex = meshes.Index }
        );

        system.Objects.Data.Data(meshes.Draws)[id.Index] = new() {
            VertexBuffer = vertices,
            IndexBuffer = indices,
            IndexFormat = IndexFormat.UInt16,
            Count = 6,
            InstanceCount = 1
        };

        // ⚠ Written, and not left alone — the default is the zero matrix, which collapses every vertex
        // onto the origin and draws a frame that is indistinguishable from one that never ran.
        system.Objects.Data.Data(transforms.World)[id.Index] =
            Matrix4x4.FromScale(new(half, 1f, half)) * Matrix4x4.FromTranslation(centre);

        materials.Assign(system, id, material);
    }

    /// <summary>
    ///     Fills set 0 with the sun and with everything the variant declares and does not sample.
    /// </summary>
    /// <returns>The textures it created, which still have to be moved out of UNDEFINED.</returns>
    static TextureHandle[] Fill(SceneConstants scene, SamplerCache samplers, VulkanDevice device, Fixture fixture) {
        // The sun, and it is the whole of the lighting: straight down onto a ground plane whose normal
        // is straight up, so an unshadowed fragment is at full N·L and a shadowed one is at nothing.
        scene.Parameters.Set(ForwardPlusKeys.LightDirection, Sun);
        scene.Parameters.Set(ForwardPlusKeys.LightColor, new Vector3(3f, 3f, 3f));
        ClusterGrid.Apply(scene.Parameters, Camera, "ForwardPlus");

        var cube = Cube(device, fixture);
        var probes = Cube(device, fixture);

        scene.Parameters.Set(ForwardPlusKeys.Environment, cube.View);
        scene.Parameters.Set(ForwardPlusKeys.Probes, probes.View);
        scene.Parameters.Set(ForwardPlusKeys.EnvironmentSampler, samplers.LinearClamp);
        scene.Parameters.Set(ForwardPlusKeys.ProbeSampler, samplers.LinearClamp);

        return [cube.Texture, probes.Texture];
    }

    /// <summary>A one-texel opaque texture, for the caster's alpha map that this fixture has none of.</summary>
    static TextureViewHandle Blank(VulkanDevice device, Fixture fixture) {
        var texture = device.CreateTexture(
            new() {
                Width = 1, Height = 1, Depth = 1, MipLevels = 1, ArrayLayers = 1, SampleCount = 1,
                Dimension = TextureDimension.Texture2D,
                Format = PixelFormat.Rgba8UNorm, Usage = TextureUsage.Sampled, Name = "opaque"
            }
        );

        var view = device.CreateTextureView(texture);
        fixture.Owns(() => device.Destroy(texture));

        return view;
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

    /// <summary>A unit quad in the XZ plane, facing up — the ground and the caster both.</summary>
    static SurfaceVertex[] Quad => [
        Corner(-1f, -1f),
        Corner(1f, -1f),
        Corner(-1f, 1f),
        Corner(1f, 1f)
    ];

    static SurfaceVertex Corner(float x, float z) =>
        new() {
            Position = new(x, 0f, z),
            Normal = new(0f, 1f, 0f),
            Tangent = new(1f, 0f, 0f, 1f),
            TexCoord = new((x * 0.5f) + 0.5f, (z * 0.5f) + 0.5f)
        };

    /// <summary>One pixel's brightness, in 0..1.</summary>
    static double Luminance(in Bitmap image, int x, int y) {
        var offset = image.Offset(Math.Clamp(x, 0, image.Width - 1), Math.Clamp(y, 0, image.Height - 1));

        return ((0.2126 * image.Pixels[offset])
            + (0.7152 * image.Pixels[offset + 1])
            + (0.0722 * image.Pixels[offset + 2])) / 255.0;
    }

    /// <summary>Passes when there is no device, unless the environment insists on one.</summary>
    static void Skip(string? reason) {
        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set and no device could be opened: {reason}");
        }
    }

    /// <summary>The material whose composition the pass is compiled against.</summary>
    static Material Composed() {
        var compilation = MaterialCompiler.Compile(
            new() {
                ShaderName = "ForwardPlus",
                Features = [new MetalRoughnessFeature { BaseColor = Vector3.One, Metalness = 0f, Roughness = 0.9f }]
            }
        );

        Assert.False(
            compilation.Failed,
            string.Join(Environment.NewLine, compilation.Diagnostics.Select(diagnostic => diagnostic.ToString()))
        );

        return compilation.Material!;
    }
}
