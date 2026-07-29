// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
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
///     The other half of Forward+: a fragment reading the list the culler filled.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="ClusterCullingDeviceTests" /> proves the culler bins what it should. Nothing
///         proved that anything <em>reads</em> it: the clustered variant of <c>ForwardPlus</c> has a
///         <c>positionVS</c> stream that exists in no other variant, a lookup through
///         <c>ClusterGrid.Of</c> whose handedness was wrong until recently, and a light loop over
///         indices — none of which any test had run.
///     </para>
///     <para>
///         <strong>Five things asking for the variant turned up</strong>, and the last two were
///         wrong everywhere rather than only here:
///     </para>
///     <list type="bullet">
///         <item><description>
///             a permutation folds <em>code</em> and not bindings, so the clustered variant still
///             declares the per-object light list it never reads, and one with image-based lighting
///             off still declares the environment cube — a host binds all of it or binds nothing;
///         </description></item>
///         <item><description>
///             the pipeline layout an effect was loaded with declared no push-constant range at all,
///             which is what <c>ForwardPlus</c> hands its world matrix through: every object would
///             have drawn at the origin;
///         </description></item>
///         <item><description>
///             a shader's vertex inputs do not start at location zero, and a host had no way to
///             learn where they do start;
///         </description></item>
///         <item><description>
///             <strong>a composed parameter's name depended on the order the lowerer visited
///             types.</strong> <c>baseColor</c> inside the material chain is
///             <c>CompositeSurface.MetalRoughnessSurface.baseColor</c>, which is what
///             <c>MaterialCompiler</c> predicts without a compiler in the process — but the merge
///             produced that path only when it happened to reach the chain before the pass, and
///             named it <c>MetalRoughnessSurface.baseColor</c> otherwise. The checked-in reflection
///             was on the lucky side of that and a compiled effect was not, so every value a
///             material set arrived at the GPU as zero;
///         </description></item>
///         <item><description>
///             <strong>one Raven struct in both a uniform block and a storage buffer became two
///             SPIR-V types with the same name.</strong> Both were correctly decorated; a translator
///             with one namespace for struct definitions emitted one of them and used it for both,
///             and on Metal the uniform block's padded <c>float3</c> displaced the storage buffer's
///             tight one — so a light read four bytes late in the fragment stage and on the nose in
///             the compute stage that filled it.
///         </description></item>
///     </list>
///     <para>
///         The last two are why this fixture is a <em>picture</em> and not a set of assertions about
///         handles. Nothing was invalid, nothing was refused, and no layer had anything to say: the
///         frame simply came back black.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public class ClusteredShadingDeviceTests {
    /// <summary>Where the quad sits, and how big it is.</summary>
    /// <remarks>
    ///     Six units in front of a camera at the origin, which puts it in the middle of the grid's
    ///     depth slices rather than against either end — a fragment on the near plane and one past the
    ///     far plane are the two cases a clamp would hide.
    /// </remarks>
    const float QuadZ = -6f;

    const float QuadHalf = 3f;

    /// <summary>
    ///     What the pass clears to: a blue no light in this fixture can produce.
    /// </summary>
    /// <remarks>
    ///     ⚠ Deliberate, and load-bearing. <see cref="RenderPassRenderer.ClearColour" /> defaults to
    ///     transparent black, so a fixture that leaves it alone cannot tell a pass that ran and drew
    ///     nothing from a pass that never ran — both are a black picture. Clearing to a colour the
    ///     shading cannot produce splits the two: the corner assertion below is what says the pass
    ///     executed at all.
    /// </remarks>
    static Color4 Background => new(0f, 0f, 0.25f, 1f);

    /// <summary>The camera the culler and the shading pass are both given.</summary>
    /// <remarks>
    ///     Square, because the picture is: the grid's tiles are the screen's, so an aspect ratio that
    ///     disagreed with the target would put a fragment in the tile next door and still look
    ///     plausible.
    /// </remarks>
    static RenderCamera Camera => RenderCamera.Default with { Position = Vector3.Zero, AspectRatio = 1f };

    /// <summary>The variant under test: clustered, and nothing else switched on.</summary>
    /// <remarks>
    ///     Image-based lighting and shadows off, so set 0 is the block, the light buffer and the
    ///     cluster list — the three things this is about — rather than an environment and an atlas the
    ///     fixture would have to supply to bind anything at all.
    /// </remarks>
    static EffectKey Key(ShaderComposition composition) =>
        EffectKey.Of(
            "ForwardPlus",
            [
                new("ForwardPlus.UseClusteredLights", "true"),
                new("ForwardPlus.UseImageBasedLighting", "false"),
                new("ForwardPlus.UseShadows", "false"),
                new("ForwardPlus.UseReflectionProbe", "false")
            ],
            composition
        );

    /// <summary>
    ///     The clustered variant compiles, and its per-frame set is the culler's output.
    /// </summary>
    /// <remarks>
    ///     The first thing to establish, and not a formality: a variant nobody has ever asked the
    ///     compiler for is a variant nobody knows compiles, and what it binds decides what a frame has
    ///     to supply.
    /// </remarks>
    [Fact]
    public void TheClusteredVariantCompilesAndBindsTheClusterList() {
        var material = Composed();
        var data = RavenEffects.Everything().TryGet(Key(material.Composition));

        Assert.NotNull(data);

        var frame = data!.Bindings
            .Where(binding => binding.Set == DescriptorSetSlot.PerFrame)
            .Select(binding => binding.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Contains("clusters", frame);
        Assert.Contains("lightBuffer", frame);

        // ⚠ And every binding the pass declares is still here, switched off or not: the shadow
        // atlas, the environment, the four probe cubes and their samplers, plus a 1296-byte per-draw
        // block for a light list this variant never reads. Permutations fold *code*, not bindings —
        // which the shader's own comments claim otherwise, and which a frame pays for by having to
        // supply resources nothing samples. See the remarks on this class.
        Assert.Contains("shadowMap", frame);
        Assert.Contains("environment", frame);

        Assert.Contains(
            data.Bindings,
            binding => binding.Set == DescriptorSetSlot.PerDraw && binding.Size == 1296
        );

        // The world matrix and the material record are pushed, and the range reaches the runtime — a
        // pipeline layout that declared none would drop every push against it, so every object in the
        // frame would draw at the origin.
        //
        // The members come with it, because an offset a host guessed is the failure this family
        // specialises in: a push at the wrong offset within a declared range is accepted by every
        // layer there is.
        var pushed = Assert.Single(data.PushConstants);

        Assert.InRange(pushed.Size, 68, 128);
        Assert.Equal(0, pushed.Offset);
        Assert.True(pushed.Stages.HasFlag(ShaderStage.Vertex));

        Assert.Equal(
            [("world", 0), ("materialIndex", 64)],
            [.. (pushed.Members ?? []).Select(member => (member.Name, member.Offset))]
        );

        // And the attribute locations, which are not zero-based and are not guessable: a shader's
        // `stream` variables take locations before its vertex inputs do, so these four start at five.
        // A pipeline described against 0 to 3 is refused outright — which is the one merciful failure
        // in this family, the other two being silent.
        Assert.Equal(
            [("position", 5), ("normal", 6), ("tangent", 7), ("texcoord", 8)],
            data.VertexInputs.Select(input => (input.Name, input.Location)).ToArray()
        );

        Assert.Equal(ShaderValueKind.Float4, data.VertexInputs.Single(input => input.Name == "tangent").Kind);

        // ⚠ And the names the material writes are the names the block declares, which is the fact
        // that was false and cost a black frame: a composed parameter is qualified by the whole
        // compose path, and the lowerer produced that path only when it happened to merge the chain
        // before the pass. `MaterialCompiler` predicts these without a compiler in the process, so a
        // disagreement here is every material value silently arriving as zero.
        var written = material.Parameters.Keys.Select(key => key.Name).ToArray();

        Assert.NotEmpty(written);

        foreach (var name in written) {
            Assert.Contains(name, data.Parameters.Select(parameter => parameter.Name));
        }
    }

    /// <summary>
    ///     A fragment is lit by the light its own cluster holds, and by nothing else.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The claim the culler's fixture cannot make and this one cannot make without it: the
    ///         culler put a light in a cluster, and a fragment that computed that same cluster for
    ///         itself found it there. Everything between the two — the <c>positionVS</c> stream, the
    ///         handedness of <c>ClusterGrid.Of</c>, the cluster buffer arriving in set 0, the loop over
    ///         indices — runs here and nowhere else.
    ///     </para>
    ///     <para>
    ///         <strong>Two lights, so the list has to name one.</strong> A red lamp two units in front
    ///         of the quad and a green one two hundred behind the camera, out of range and unable to
    ///         contribute anything. With one light a cluster list would only have to be
    ///         <em>non-empty</em> to look right; with two it has to hold index zero, so the off-by-one
    ///         that reads its neighbour's slot comes back unlit rather than red.
    ///     </para>
    ///     <para>
    ///         ⚠ What this cannot catch is a fragment reading <em>every</em> light instead of its own
    ///         cluster's, because a correct culler bins a light into every froxel its sphere touches —
    ///         so any light that can reach a fragment is already in that fragment's list, and
    ///         <c>Lighting.DistanceAttenuation</c> windows the rest to exactly zero. Over-inclusion is
    ///         invisible to a picture by construction; it is the culler's fixture that bounds it.
    ///     </para>
    ///     <para>
    ///         The frame is the real one: the culler is a <see cref="ComputeRenderer" /> node, the
    ///         shading pass a <see cref="RenderPassRenderer" /> that declares it reads what the culler
    ///         wrote, and the variant is selected through <see cref="MaterialRenderFeature" /> from a
    ///         flag <see cref="ForwardLightingRenderFeature" /> contributes.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AFragmentIsLitByTheLightItsClusterHolds() {
        if (!Fixture.TryOpen(out var fixture, out var reason)) {
            Skip(reason);
            return;
        }

        using var owned = fixture!;
        var image = Render(owned);

        // The corner the quad does not cover is the clear, so the pass ran. Without this the two
        // assertions below cannot distinguish an unlit quad from an unexecuted frame.
        var corner = Pixel(image, 2, 2);

        Assert.True(corner.Z > 0.2f && corner.X < 0.05f, $"the pass did not clear: {corner}");

        var centre = Pixel(image, image.Width / 2, image.Height / 2);

        Assert.True(centre.X > 0.2f, $"the quad is not lit at all: {centre}");
        Assert.True(centre.X > centre.Y * 4f, $"the quad is not lit by the near light: {centre}");
    }

    // --- The frame ----------------------------------------------------------

    /// <summary>Draws one clustered frame and reads the picture back.</summary>
    static Bitmap Render(Fixture fixture) {
        var device = fixture.Device;
        var material = Composed();

        material.Parameters.Set(ForwardPlusKeys.UseImageBasedLighting, false);
        material.Parameters.Set(ForwardPlusKeys.UseShadows, false);
        material.Parameters.Set(ForwardPlusKeys.UseReflectionProbe, false);

        var loader = new EffectLoader(device);
        var effects = new EffectSystem();
        effects.AddProvider(new Compiling(loader, Compiler));

        // Resolved up front for its set-1 layout, which the per-view block has to be created with —
        // and it is the same handle the frame resolves, because the loader caches a layout by shape.
        var effect = effects.Resolve(Key(material.Composition));

        Assert.NotNull(effect);

        using var allocator = new DescriptorAllocator(device);
        using var samplers = new SamplerCache(device);
        using var system = new RenderSystem();

        using var view = new ViewConstants(device) {
            Descriptors = allocator,
            Layout = effect!.SetLayouts[(int)DescriptorSetSlot.PerView]
        };

        using var scene = new SceneConstants(device) { Descriptors = allocator };
        var opaque = system.AddStage(new("Opaque"));

        var describer = new EffectPipelineDescriber(device);

        // Locations five to eight, which is where this shader's inputs actually are — the compile
        // fact above is what says so, and the effect is what a real host would read them from.
        describer.VertexLayouts.Add([
            new VertexBufferLayout(
                Vertex.Stride,
                [
                    new(5, VertexFormat.Float32X3, 0),
                    new(6, VertexFormat.Float32X3, 12),
                    new(7, VertexFormat.Float32X4, 24),
                    new(8, VertexFormat.Float32X2, 40)
                ]
            )
        ]);

        var meshes = new MeshRenderFeature { Pipelines = new(device), Describer = describer };
        var materials = new MaterialRenderFeature { Effects = effects, Device = device, Descriptors = allocator };
        var lighting = new ForwardLightingRenderFeature { Device = device, Clustered = true };
        // ⚠ A device and somewhere to publish to, both of them load-bearing even though this frame
        // pushes its matrix rather than reading a record. `ForwardPlus` declares `transforms` in set 0
        // unconditionally, so every variant needs it bound — and the feature answers that by uploading
        // a single identity matrix when the record path is off. Without a device there is no buffer;
        // without a collection the name is never published; either way set 0 is one binding short,
        // which means it is not bound at all, and the draw reads descriptors nobody wrote.
        var transforms = new TransformRenderFeature { Device = device, Scene = scene.Parameters };

        meshes.Add(transforms);
        meshes.Add(materials);
        meshes.Add(lighting);
        system.AddFeature(meshes);

        // The shader's own permutations, and the one mapping that joins the renderer's flag to the
        // shader's name for it. Without the mapping the key asks for a variant the compiler cannot
        // match, which is what made this whole path unreachable.
        materials.PermutationKeys["ForwardPlus"] = ForwardPlusKeys.UsedPermutationKeys;
        materials.PermutationSources[ForwardPlusKeys.UseClusteredLights] = lighting.PermutationKeys[0];

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

        // ⚠ Identity, and not skippable. The default is the *zero* matrix, which collapses every
        // vertex onto the origin — a frame that clears and draws nothing, which is the same picture as
        // a frame that never ran.
        system.Objects.Data.Data(transforms.World)[quad.Index] = Matrix4x4.Identity;

        materials.Assign(system, quad, material);

        // Red where the quad is, green nowhere near it. The culler decides which cluster each lands
        // in; the fragment decides which cluster it reads.
        RenderLight[] lights = [
            RenderLight.Point(new(0f, 0f, QuadZ + 2f), 6f, new(1f, 0f, 0f), 40f),
            RenderLight.Point(new(0f, 0f, 200f), 8f, new(0f, 1f, 0f), 40f)
        ];

        var gpu = lights.Select(light => light.ToGpu()).ToArray();

        var buffer = fixture.Buffer<PunctualLightData>(gpu, BufferUsage.Storage);

        var unused = Fill(scene, samplers, device, fixture);

        // The culler's own plan says where its block and its set are. Written down here instead, they
        // were a set and a binding this shader does not use, and its uniforms — the view matrix, the
        // grid, the light count — went nowhere: every cluster came back empty and the quad was black.
        var culler = effects.Resolve(EffectKey.Of("ClusterCulling"));

        Assert.NotNull(culler);

        using var culling = new ComputeRenderer {
            Name = "ClusterCulling",
            ShaderName = "ClusterCulling",
            Pipelines = new(device),
            Groups = ClusterGrid.GroupCount,
            ConstantBinding = culler!.Bindings.Single(binding => binding.Kind == DescriptorKind.UniformBuffer).Binding,
            Descriptors = { Allocator = allocator, Slot = culler.Bindings[0].Set }
        };

        culling.BufferReads.Add("SceneLights");
        culling.BufferWrites.Add("Clusters");
        culling.Descriptors.Bindings.Add(new() { Name = "lights", Resource = "SceneLights" });
        culling.Descriptors.Bindings.Add(new() { Name = "clusters", Resource = "Clusters" });

        culling.Parameters.Set(ParameterKeys.New<Matrix4x4>("ClusterCulling.view"), Camera.View);
        culling.Parameters.Set(ParameterKeys.New<int>("ClusterCulling.lightCount"), lights.Length);
        ClusterGrid.Apply(culling.Parameters, Camera, "ClusterCulling");

        var pass = new RenderPassRenderer {
            Name = "Forward",
            ClearColour = Background,
            SceneConstants = scene,
            Children = { new SingleStageRenderer { View = camera, Stage = opaque, Constants = view } }
        };

        pass.ColourTargets.Add("Display");
        pass.SceneBuffers["lightBuffer"] = "SceneLights";
        pass.SceneBuffers["clusters"] = "Clusters";

        var display = fixture.Owned("Display", TextureUsage.ColourTarget | TextureUsage.CopySource);

        var compositor = new GraphicsCompositor(system) {
            FrameSize = new(Fixture.Side, Fixture.Side),
            Game = new SceneRendererSequence { Children = { culling, pass } }
        };

        compositor.Imports["Display"] = new(
            display.Texture,
            display.View,
            display.Description,
            ResourceState.Undefined,
            ResourceState.CopySource
        );

        compositor.BufferImports["SceneLights"] = new(
            buffer,
            new(
                lights.Length * Marshal.SizeOf<PunctualLightData>(),
                BufferUsage.Storage,
                MemoryAccess.HostUpload,
                "SceneLights"
            ),
            ResourceState.ShaderRead,
            ResourceState.ShaderRead
        );

        // Declared rather than imported: it is written and read inside one frame, which is also what
        // lets the graph order the two passes and put the barrier between them.
        compositor.BufferResources.Add(
            new() { Name = "Clusters", Size = ClusterGrid.BufferSize, Usage = BufferUsage.Storage }
        );

        allocator.BeginFrame();
        var frame = compositor.Build(fixture.Graph, effects, device);

        Assert.Empty(effects.Misses);

        // The graph knows nothing about the textures set 0 had to be given, so nobody else can move
        // them out of UNDEFINED — and a set holding one there is a validation error at submit whether
        // or not the shader samples it.
        var picture = fixture.Render(
            frame.Texture("harness", "Display"),
            list => list.Barrier(
                new(
                    [],
                    [.. unused.Select(texture => new TextureBarrier(texture, ResourceState.Undefined, ResourceState.ShaderRead))]
                )
            )
        );

        // ⚠ Asserted rather than assumed. A set 0 that fell one binding short is not bound at all,
        // and the pass then draws with whatever set 0 held before — here, nothing. The picture is
        // black either way, so without this the failure reads as a clustering bug.
        Assert.True(scene.IsComplete, "set 0 was left incomplete, so the frame bound none of it");
        Assert.True(scene.WriteCount > 0, "set 0 was never written");
        Assert.True(materials.BoundCount > 0, "set 2 was left incomplete, so the material bound none of it");

        return picture;
    }

    /// <summary>
    ///     Fills set 0 with everything the variant declares, sampled or not.
    /// </summary>
    /// <remarks>
    ///     The shadow atlas, the environment cube, four probe cubes and three samplers, none of which
    ///     this variant reads and all of which it declares — a set is written wholly or not at all, so
    ///     a frame that left them out would bind no set 0 and the quad would be black for a reason
    ///     that has nothing to do with clusters.
    /// </remarks>
    /// <returns>The textures it created, which still have to be moved out of UNDEFINED.</returns>
    static TextureHandle[] Fill(SceneConstants scene, SamplerCache samplers, VulkanDevice device, Fixture fixture) {
        // No sun. Every photon in the picture came out of the cluster list, which is the point.
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
    /// <remarks>
    ///     Through <see cref="MaterialCompiler" /> rather than by hand, because it binds <em>every</em>
    ///     slot the library declares and not only the two this pass has — the material shaders compose
    ///     into each other, and an unbound slot anywhere in the tree is a compile error for all of it.
    /// </remarks>
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

    /// <summary>The sources one named shader compiles against.</summary>
    /// <remarks>
    ///     Everything for a material pass, and for <c>ClusterCulling</c> only what it imports — see the
    ///     remarks on <see cref="Compiling" /> for why the two cannot share a set.
    /// </remarks>
    static RavenEffectCompiler Compiler(string shaderName) =>
        shaderName == "ClusterCulling"
            ? RavenEffects.Only(["Core", "Geometry", "Shading"], Path.Combine("Pipeline", "ClusterCulling.rvn"))
            : RavenEffects.Everything();

    /// <summary>Passes when there is no device, unless the environment insists on one.</summary>
    static void Skip(string? reason) {
        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set and no device could be opened: {reason}");
        }
    }
}
