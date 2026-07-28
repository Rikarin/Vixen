// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Features;
using Vixen.Shaders;
using Xunit;

namespace Tests;

/// <summary>
///     A material binding itself — docs/plan/06 § Materials, and the half the compositor's nodes
///     closed first.
/// </summary>
/// <remarks>
///     <para>
///         A material knows it has a texture called <c>albedo</c>. Which binding index that is belongs
///         to the compiled shader, and adding a texture above it in the <c>.rvn</c> renumbers it — so
///         until the binding plan reached the runtime, a host had to write the number down and hand
///         over a finished descriptor set. <see cref="Effect.Bindings" /> is what turns the name into
///         the index, and these are the assertions that it does.
///     </para>
/// </remarks>
public sealed class MaterialBindingTests : IDisposable {
    const uint ConstantsBinding = 0;
    const uint AlbedoBinding = 1;
    const uint SamplerBinding = 2;

    static readonly ParameterKey<TextureViewHandle> Albedo = ParameterKeys.New<TextureViewHandle>("Lit.albedo");
    static readonly ParameterKey<SamplerHandle> Sampler = ParameterKeys.New<SamplerHandle>("Lit.albedoSampler");
    static readonly ParameterKey<Vector3> Tint = ParameterKeys.New<Vector3>("Lit.tint");

    /// <summary>The same three, for a shader that declares no uniform block.</summary>
    /// <remarks>
    ///     Its whole purpose is isolation. Every variant gets a uniform buffer of its own, so two
    ///     materials of a shader that has a block can never write identical sets — which would make
    ///     "did the texture reach the write" untestable by comparing sets. Without a block, the
    ///     texture and the sampler are the entire difference.
    /// </remarks>
    static readonly ParameterKey<TextureViewHandle> UnlitAlbedo = ParameterKeys.New<TextureViewHandle>("Unlit.albedo");

    static readonly ParameterKey<SamplerHandle> UnlitSampler = ParameterKeys.New<SamplerHandle>("Unlit.albedoSampler");

    readonly NullDevice device = new(new() { Record = true });

    /// <summary>One sampler for every material here, so a sampler never explains a difference.</summary>
    readonly SamplerHandle shared;

    readonly DescriptorAllocator allocator;
    readonly DescriptorSetLayoutHandle layout;
    readonly DescriptorSetLayoutHandle unlitLayout;

    /// <summary>Set 0's shape, so the frame has something of its own to bind.</summary>
    readonly DescriptorSetLayoutHandle frameLayout;
    readonly EffectSystem effects = new();

    public MaterialBindingTests() {
        allocator = new(device);
        shared = device.CreateSampler(new());

        layout = device.CreateDescriptorSetLayout(
            new(
                DescriptorSetSlot.PerMaterial,
                [
                    new(ConstantsBinding, DescriptorKind.UniformBuffer, ShaderStage.Vertex | ShaderStage.Fragment),
                    new(AlbedoBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment),
                    new(SamplerBinding, DescriptorKind.Sampler, ShaderStage.Fragment)
                ],
                "Lit"
            )
        );

        unlitLayout = device.CreateDescriptorSetLayout(
            new(
                DescriptorSetSlot.PerMaterial,
                [
                    new(ConstantsBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment),
                    new(AlbedoBinding, DescriptorKind.Sampler, ShaderStage.Fragment)
                ],
                "Unlit"
            )
        );

        frameLayout = device.CreateDescriptorSetLayout(
            new(DescriptorSetSlot.PerFrame, [new(0, DescriptorKind.SampledTexture, ShaderStage.Fragment)], "Scene")
        );

        effects.AddProvider(new Compiles(layout, unlitLayout, frameLayout));
    }

    public void Dispose() {
        allocator.Dispose();
        device.Dispose();
    }

    /// <summary>
    ///     An effect that reports its binding plan, which is what a baked bundle now does.
    /// </summary>
    /// <remarks>
    ///     Named the way the shader names them — bare — while the parameter keys the material sets are
    ///     qualified by shader. Bridging those two is the thing under test, so a fake that named them
    ///     identically would assert nothing.
    /// </remarks>
    sealed class Compiles(DescriptorSetLayoutHandle lit, DescriptorSetLayoutHandle unlit, DescriptorSetLayoutHandle frame) : IEffectProvider {
        public Effect? TryGet(EffectKey key) =>
            key.ShaderName switch {
                "Lit" => new() {
                    Key = key,
                    Stages = Modules,
                    SetLayouts = [frame, default, lit, default],
                    ConstantBufferSize = 16,
                    Parameters = [new(Tint, 0, 12)],
                    Bindings = [
                        new("environment", DescriptorSetSlot.PerFrame, 0, DescriptorKind.SampledTexture),
                        new("constants", DescriptorSetSlot.PerMaterial, ConstantsBinding, DescriptorKind.UniformBuffer),
                        new("albedo", DescriptorSetSlot.PerMaterial, AlbedoBinding, DescriptorKind.SampledTexture),
                        new("albedoSampler", DescriptorSetSlot.PerMaterial, SamplerBinding, DescriptorKind.Sampler)
                    ]
                },
                "Unlit" => new() {
                    Key = key,
                    Stages = Modules,
                    SetLayouts = [default, default, unlit, default],
                    Bindings = [
                        new("albedo", DescriptorSetSlot.PerMaterial, ConstantsBinding, DescriptorKind.SampledTexture),
                        new("albedoSampler", DescriptorSetSlot.PerMaterial, AlbedoBinding, DescriptorKind.Sampler)
                    ]
                },
                // A stage override — a prepass, a shadow caster — reads no material at all.
                _ => new() { Key = key, Stages = Modules, SetLayouts = [default, default, default, default] }
            };

        static ImmutableArray<EffectStage> Modules =>
            [new(ShaderStage.Vertex, [1, 2, 3, 4], "main"), new(ShaderStage.Fragment, [5, 6, 7, 8], "main")];
    }

    sealed class Harness : IDisposable {
        public required RenderSystem System { get; init; }
        public required RenderStage Opaque { get; init; }
        public required RenderView Camera { get; init; }
        public required MeshRenderFeature Meshes { get; init; }
        public required MaterialRenderFeature Materials { get; init; }
        public required BufferHandle Vertices { get; init; }
        public required BufferHandle Indices { get; init; }

        public void Dispose() => System.Dispose();
    }

    Harness Build(bool bindItself = true) {
        var system = new RenderSystem();
        var opaque = system.AddStage(new("Opaque"));

        var meshes = new MeshRenderFeature { Pipelines = new(device), Describer = new EffectPipelineDescriber(device) };

        var materials = new MaterialRenderFeature {
            Effects = effects,
            Device = bindItself ? device : null,
            Descriptors = bindItself ? allocator : null
        };

        meshes.Add(new TransformRenderFeature());
        meshes.Add(materials);
        system.AddFeature(meshes);

        var view = Matrix4x4.LookAt(Vector3.Zero, new(0f, 0f, 1f), new(0f, 1f, 0f));
        var projection = Matrix4x4.PerspectiveFieldOfView(MathF.PI / 3f, 1f, 0.1f, 1000f);

        var camera = new RenderView("camera") {
            Stages = opaque.Mask,
            Position = Vector3.Zero,
            Frustum = new(view * projection)
        };

        system.SetViews([camera]);

        return new() {
            System = system,
            Opaque = opaque,
            Camera = camera,
            Meshes = meshes,
            Materials = materials,
            Vertices = device.CreateBuffer(new() { Size = 1024, Usage = BufferUsage.Vertex }),
            Indices = device.CreateBuffer(new() { Size = 1024, Usage = BufferUsage.Index })
        };
    }

    static RenderObjectId AddMesh(Harness h, Material material) {
        var id = h.System.Objects.Add(
            new() { Bounds = new(new Vector3(0f, 0f, 10f), 1f), Stages = h.Opaque.Mask, FeatureIndex = h.Meshes.Index }
        );

        h.System.Objects.Data.Data(h.Meshes.Draws)[id.Index] = new() {
            VertexBuffer = h.Vertices,
            IndexBuffer = h.Indices,
            IndexFormat = IndexFormat.UInt16,
            Count = 36,
            InstanceCount = 1
        };

        h.Materials.Assign(h.System, id, material);
        return id;
    }

    /// <summary>A texture view of its own, so two of them are genuinely different resources.</summary>
    TextureViewHandle Texture() =>
        device.CreateTextureView(
            device.CreateTexture(
                new() {
                    Width = 4, Height = 4, Depth = 1, MipLevels = 1, ArrayLayers = 1, SampleCount = 1,
                    Format = PixelFormat.Rgba8UNorm, Usage = TextureUsage.Sampled
                }
            )
        );

    Material Lit(TextureViewHandle? albedo = null, Vector3? tint = null) {
        var material = new Material("Lit");

        material.Parameters.Set(Albedo, albedo ?? Texture());
        material.Parameters.Set(Sampler, shared);
        material.Parameters.Set(Tint, tint ?? Vector3.One);

        return material;
    }

    Material Unlit(TextureViewHandle albedo) {
        var material = new Material("Unlit");

        material.Parameters.Set(UnlitAlbedo, albedo);
        material.Parameters.Set(UnlitSampler, shared);

        return material;
    }

    void Frame(Harness h, SceneConstants? scene = null) {
        allocator.BeginFrame();
        h.System.Draw();

        var target = device.CreateTextureView(
            device.CreateTexture(
                new() {
                    Width = 16, Height = 16, Depth = 1, MipLevels = 1, ArrayLayers = 1, SampleCount = 1,
                    Format = PixelFormat.Rgba8UNorm, Usage = TextureUsage.ColourTarget
                }
            )
        );

        var list = device.BeginCommandList();
        list.BeginRenderPass(new([new(target)], name: "Opaque"));

        h.System.Record(
            h.Camera,
            h.Opaque,
            new(list, effects) {
                Device = device,
                Output = new([PixelFormat.Rgba8UNorm]),
                SceneConstants = scene
            }
        );

        list.EndRenderPass();
        list.Finish();
        device.GraphicsQueue.Submit([list]);
    }

    // --- Writing the set ----------------------------------------------------

    /// <summary>
    ///     A material with a texture binds one, without anybody writing a binding index down.
    /// </summary>
    [Fact]
    public void A_material_binds_its_own_textures() {
        using var h = Build();
        var id = AddMesh(h, Lit());

        Frame(h);

        Assert.Equal(1, h.Materials.BoundCount);
        Assert.True(h.Materials.DescriptorsOf(h.System, id).IsValid);

        // And it reached the command stream, which is the only thing that decides what the draw read.
        Assert.Equal(1, device.Recorder!.CountOf(RecordedCommandKind.BindDescriptorSet));
        Assert.Equal(1, device.Recorder.CountOf(RecordedCommandKind.DrawIndexed));
    }

    /// <summary>
    ///     The texture a material set is the one that lands in the write.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The behavioural proof, and it needs a shader with no uniform block to make it: two
    ///         materials of a shader that has one can never write identical sets, because every
    ///         variant gets a buffer of its own. Without a block, the texture and the sampler are the
    ///         entire content of the set — so two materials naming one texture share a set and two
    ///         naming different ones do not.
    ///     </para>
    ///     <para>
    ///         A feature that resolved the index correctly and then bound the wrong handle would pass
    ///         a test that only counted sets. It cannot pass this one, because the sharing is decided
    ///         by comparing the writes themselves.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_texture_a_material_set_is_what_lands_in_the_write() {
        using var h = Build();

        var texture = Texture();
        var first = AddMesh(h, Unlit(texture));
        var second = AddMesh(h, Unlit(texture));
        var other = AddMesh(h, Unlit(Texture()));

        Frame(h);

        var a = h.Materials.DescriptorsOf(h.System, first);
        var b = h.Materials.DescriptorsOf(h.System, second);
        var c = h.Materials.DescriptorsOf(h.System, other);

        Assert.True(a.IsValid);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    /// <summary>
    ///     Objects sharing a material share one set between them, however many there are.
    /// </summary>
    /// <remarks>
    ///     Per variant, not per object — the economy the variant table exists for, applied to
    ///     descriptor sets. Ten thousand objects over twenty materials is twenty sets.
    /// </remarks>
    [Fact]
    public void Objects_sharing_a_material_share_one_set() {
        using var h = Build();
        var material = Lit();

        var first = AddMesh(h, material);

        for (var i = 0; i < 5; i++) {
            AddMesh(h, material);
        }

        Frame(h);

        Assert.Equal(1, h.Materials.BoundCount);
        Assert.Equal(1, allocator.WriteCount);
        Assert.Equal(1, device.Recorder!.CountOf(RecordedCommandKind.BindDescriptorSet));
        Assert.Equal(6, device.Recorder.CountOf(RecordedCommandKind.DrawIndexed));
        Assert.True(h.Materials.DescriptorsOf(h.System, first).IsValid);
    }

    /// <summary>
    ///     A material nobody changed does not re-upload its uniform block.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The set itself is written once a frame, because it comes from the frame allocator whose
    ///         cache is a frame long — a material's values <em>can</em> change, and a set rewritten in
    ///         place is one rewritten while an unfinished frame may still be reading it. One
    ///         descriptor write per variant per frame is the price, and it is the same price every
    ///         compositor node pays.
    ///     </para>
    ///     <para>
    ///         The bytes are the part worth not repeating, and they are not:
    ///         <c>EffectConstants</c> compares the collection's version and uploads only when it
    ///         moved.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_material_that_did_not_change_does_not_upload_again() {
        using var h = Build();
        AddMesh(h, Lit());

        Frame(h);

        Assert.Equal(1, h.Materials.UploadCount);

        Frame(h);
        Frame(h);

        Assert.Equal(1, h.Materials.UploadCount);

        // And the set is still written once on each of those frames, which is the price of a cache
        // that is a frame long.
        Assert.Equal(1, allocator.WriteCount);
    }

    /// <summary>Changing a material's texture changes what is bound.</summary>
    [Fact]
    public void Changing_a_texture_rebinds() {
        using var h = Build();
        var material = Lit();
        var id = AddMesh(h, material);

        Frame(h);
        var before = h.Materials.DescriptorsOf(h.System, id);

        material.Parameters.Set(Albedo, Texture());

        Frame(h);

        Assert.NotEqual(before, h.Materials.DescriptorsOf(h.System, id));
    }

    // --- One block per set --------------------------------------------------

    /// <summary>
    ///     A pass with a block in every set gives each filler the right one.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <see cref="Effect.ConstantBufferSize" /> names one block, which is all a shader that
    ///         marks none of its bindings has. A pass that says which set each binding is in has up to
    ///         four, and the thing filling set 0's buffer must not be handed set 2's size and set 2's
    ///         member offsets — that writes the right values into the wrong buffer, which is a frame
    ///         lit by whatever those bytes happened to mean.
    ///     </para>
    ///     <para>
    ///         Asserted on <see cref="Effect.BlockOf" /> directly, because the two callers that use it
    ///         — the material feature and <see cref="SceneConstants" /> — would each pass over a wrong
    ///         answer without noticing, and the shapes they write are what a driver sees rather than
    ///         what a test does.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Each_set_gets_its_own_block() {
        var effect = new Effect {
            Key = EffectKey.Of("Layered"),
            Stages = [],
            ConstantBufferSize = 544,
            Bindings = [
                new("frame", DescriptorSetSlot.PerFrame, 0, DescriptorKind.UniformBuffer) { Size = 544 },
                new("material", DescriptorSetSlot.PerMaterial, 0, DescriptorKind.UniformBuffer) { Size = 32 }
            ],
            Parameters = [
                new(ParameterKeys.New<float>("Layered.ambient"), 0, 4) { Set = DescriptorSetSlot.PerFrame },
                new(ParameterKeys.New<float>("Layered.tint"), 16, 4) { Set = DescriptorSetSlot.PerMaterial }
            ]
        };

        var frame = effect.BlockOf(DescriptorSetSlot.PerFrame);
        var material = effect.BlockOf(DescriptorSetSlot.PerMaterial);

        Assert.Equal(544, frame.Size);
        Assert.Equal("Layered.ambient", Assert.Single(frame.Members).Key.Name);

        Assert.Equal(32, material.Size);
        Assert.Equal("Layered.tint", Assert.Single(material.Members).Key.Name);

        // A set with no block at all says so, rather than answering with somebody else's.
        Assert.False(effect.BlockOf(DescriptorSetSlot.PerDraw).Exists);
    }

    /// <summary>The frame's set is written from the shader's own names, once.</summary>
    /// <remarks>
    ///     What <see cref="SceneConstants" /> is: set 0's counterpart to <c>ViewConstants</c>. Unlike
    ///     set 1 it holds resources as well as a block, which is why it takes its shape from the
    ///     effect rather than from a host's configuration — set 1 is a contract between shaders and
    ///     set 0 belongs to whichever pass is drawing.
    /// </remarks>
    [Fact]
    public void The_frames_set_is_written_from_the_shaders_names() {
        using var device2 = new NullDevice(new() { Record = true });
        using var frameAllocator = new DescriptorAllocator(device2);

        var frameLayout = device2.CreateDescriptorSetLayout(
            new(
                DescriptorSetSlot.PerFrame,
                [
                    new(0, DescriptorKind.UniformBuffer, ShaderStage.Fragment),
                    new(1, DescriptorKind.SampledTexture, ShaderStage.Fragment)
                ],
                "Scene"
            )
        );

        var effect = new Effect {
            Key = EffectKey.Of("Scene"),
            Stages = [],
            SetLayouts = [frameLayout, default, default, default],
            ConstantBufferSize = 16,
            Bindings = [
                new("constants", DescriptorSetSlot.PerFrame, 0, DescriptorKind.UniformBuffer) { Size = 16 },
                new("environment", DescriptorSetSlot.PerFrame, 1, DescriptorKind.SampledTexture)
            ],
            Parameters = [new(ParameterKeys.New<float>("Scene.ambient"), 0, 4) { Set = DescriptorSetSlot.PerFrame }]
        };

        using var scene = new SceneConstants(device2) { Descriptors = frameAllocator };

        var list = device2.BeginCommandList();

        // Nothing set yet: the environment has no texture, so the set is short of a binding and
        // nothing is bound — rather than a set with a hole in it.
        frameAllocator.BeginFrame();

        Assert.False(scene.Bind(list, effect));
        Assert.False(scene.IsComplete);

        scene.Parameters.Set(ParameterKeys.New<float>("Scene.ambient"), 2f);
        scene.Parameters.Set(
            ParameterKeys.New<TextureViewHandle>("Scene.environment"),
            device2.CreateTextureView(
                device2.CreateTexture(
                    new() {
                        Width = 4, Height = 4, Depth = 1, MipLevels = 1, ArrayLayers = 1, SampleCount = 1,
                        Format = PixelFormat.Rgba8UNorm, Usage = TextureUsage.Sampled
                    }
                )
            )
        );

        Assert.True(scene.Bind(list, effect));
        Assert.True(scene.IsComplete);
        Assert.Equal(1, scene.WriteCount);

        list.Finish();
    }

    /// <summary>
    ///     The frame's set is bound once for a run, after the first pipeline.
    /// </summary>
    /// <remarks>
    ///     Both halves matter. <em>After</em>, because <c>BindDescriptorSet</c> takes no pipeline
    ///     layout and infers one from what is bound, so a set before the first pipeline is undefined
    ///     and the Vulkan backend refuses it outright. <em>Once</em>, because the four-set convention
    ///     makes every pipeline in a frame layout-compatible up to set 1 — so a set 0 bound at the top
    ///     of a run survives every pipeline change inside it.
    /// </remarks>
    [Fact]
    public void The_frames_set_is_bound_once_after_the_first_pipeline() {
        using var h = Build();

        using var scene = new SceneConstants(device) { Descriptors = allocator };
        scene.Parameters.Set(ParameterKeys.New<TextureViewHandle>("Lit.environment"), Texture());

        for (var i = 0; i < 4; i++) {
            AddMesh(h, Lit());
        }

        Frame(h, scene);

        var commands = device.Recorder!.OfKind(RecordedCommandKind.BindDescriptorSet).ToArray();
        var frame = commands.Where(command => command.A == (long)DescriptorSetSlot.PerFrame).ToArray();

        Assert.Single(frame);
        Assert.Equal(1, scene.WriteCount);

        // And it came after a pipeline rather than before one, which is the half a driver enforces.
        var all = device.Recorder.Commands;
        var firstPipeline = -1;
        var firstFrameSet = -1;

        for (var i = 0; i < all.Count; i++) {
            if (firstPipeline < 0 && all[i].Kind == RecordedCommandKind.BindPipeline) {
                firstPipeline = i;
            }

            if (firstFrameSet < 0
                && all[i].Kind == RecordedCommandKind.BindDescriptorSet
                && all[i].A == (long)DescriptorSetSlot.PerFrame) {
                firstFrameSet = i;
            }
        }

        Assert.True(firstPipeline >= 0);
        Assert.True(firstPipeline < firstFrameSet);
    }

    // --- Declining ----------------------------------------------------------

    /// <summary>
    ///     A material missing a texture the shader declares binds nothing at all.
    /// </summary>
    /// <remarks>
    ///     Rather than a set with a hole in it. A partly-written descriptor set is a validation error
    ///     on one backend and a sampled black texture on another, and neither says which material
    ///     forgot which texture — where an object that does not draw is at least unmistakable.
    /// </remarks>
    [Fact]
    public void A_material_missing_a_texture_writes_no_set() {
        using var h = Build();

        var material = new Material("Lit");
        material.Parameters.Set(Tint, Vector3.One);

        var id = AddMesh(h, material);

        Frame(h);

        Assert.Equal(0, h.Materials.BoundCount);
        Assert.False(h.Materials.DescriptorsOf(h.System, id).IsValid);
        Assert.Equal(0, device.Recorder!.CountOf(RecordedCommandKind.BindDescriptorSet));
    }

    /// <summary>
    ///     A host that builds the set itself still has it.
    /// </summary>
    /// <remarks>
    ///     The path every host used before this existed, and the one a host with an unusual set — a
    ///     bindless table, an array of textures — still wants. Leaving either the device or the
    ///     allocator unset is what selects it.
    /// </remarks>
    [Fact]
    public void A_host_that_binds_for_itself_is_left_alone() {
        using var h = Build(bindItself: false);

        var material = Lit();
        material.Descriptors = device.CreateDescriptorSet(layout, "by hand");

        var id = AddMesh(h, material);

        Frame(h);

        Assert.Equal(0, h.Materials.BoundCount);
        Assert.Equal(material.Descriptors, h.Materials.DescriptorsOf(h.System, id));
        Assert.Equal(1, device.Recorder!.CountOf(RecordedCommandKind.BindDescriptorSet));
    }

    /// <summary>
    ///     A stage that overrode the shader binds no material set.
    /// </summary>
    /// <remarks>
    ///     A depth prepass draws something that reads no material at all, and its effect declares no
    ///     per-material layout — so there is nothing to write and nothing to bind. Asserted because
    ///     the set is per <em>variant</em>: the override is a different variant of the same material,
    ///     and one that took the material's own set would bind a set the pipeline layout does not
    ///     declare.
    /// </remarks>
    [Fact]
    public void A_stage_override_binds_no_material_set() {
        using var h = Build();
        var prepass = h.System.AddStage(new("Prepass") { ShaderName = "DepthOnly" });

        var id = h.System.Objects.Add(
            new() {
                Bounds = new(new Vector3(0f, 0f, 10f), 1f),
                Stages = h.Opaque.Mask | prepass.Mask,
                FeatureIndex = h.Meshes.Index
            }
        );

        h.System.Objects.Data.Data(h.Meshes.Draws)[id.Index] = new() {
            VertexBuffer = h.Vertices,
            IndexBuffer = h.Indices,
            IndexFormat = IndexFormat.UInt16,
            Count = 36,
            InstanceCount = 1
        };

        h.Materials.Assign(h.System, id, Lit());
        h.Camera.Stages = h.Opaque.Mask | prepass.Mask;

        allocator.BeginFrame();
        h.System.Draw();

        Assert.False(h.Materials.DescriptorsOf(h.System, id, prepass).IsValid);
        Assert.True(h.Materials.DescriptorsOf(h.System, id, h.Opaque).IsValid);
    }
}
