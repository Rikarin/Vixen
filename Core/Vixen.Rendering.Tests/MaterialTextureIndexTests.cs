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
///     A material's texture as a <em>value</em> — the gap doc 06 names when it says materials are
///     values and not resources.
/// </summary>
/// <remarks>
///     <para>
///         A material feature could carry channels and could not carry a texture, because sampling
///         one needs a binding index only the compiled shader knows and a feature is composed into a
///         shader it has never seen. With a table it needs no index of the shader's: the texture goes
///         into the table, the slot comes back as a number, and the number goes into the material's
///         own uniform block beside the base colour — where <see cref="EffectConstants" /> writes it
///         without knowing it means a descriptor.
///     </para>
///     <para>
///         Two things carry the whole of it and neither is visible in a frame. The pairing is
///         explicit, because a shader's parameter name and a material's texture name belong to
///         different things and a convention would guess. And the reference counting has to be
///         idempotent, because this runs every frame: a table asked for the same view sixty times a
///         second raises a count nothing lowers, and the slot never comes back.
///     </para>
/// </remarks>
public sealed class MaterialTextureIndexTests : IDisposable {
    static readonly ParameterKey<TextureViewHandle> Albedo = ParameterKeys.New<TextureViewHandle>("Lit.albedo");
    static readonly ParameterKey<TextureViewHandle> Normal = ParameterKeys.New<TextureViewHandle>("Lit.normal");
    static readonly ParameterKey<uint> AlbedoIndex = ParameterKeys.New<uint>("Lit.albedoIndex");
    static readonly ParameterKey<uint> NormalIndex = ParameterKeys.New<uint>("Lit.normalIndex");
    static readonly ParameterKey<Vector3> Tint = ParameterKeys.New<Vector3>("Lit.tint");

    readonly NullDevice device = new(new() { Record = true });
    readonly DescriptorAllocator allocator;
    readonly DescriptorSetLayoutHandle layout;
    readonly EffectSystem effects = new();
    readonly BindlessTable table;
    readonly TextureViewHandle fallback;

    public MaterialTextureIndexTests() {
        allocator = new(device);
        fallback = Texture();
        table = new(device, fallback: fallback, name: "Materials");

        // Only the block. The whole point is that the textures are no longer in the set — the shader
        // declares two `uint`s where it used to declare two textures and two samplers.
        layout = device.CreateDescriptorSetLayout(
            new(
                DescriptorSetSlot.PerMaterial,
                [new(0, DescriptorKind.UniformBuffer, ShaderStage.Vertex | ShaderStage.Fragment)],
                "Lit"
            )
        );

        effects.AddProvider(new Compiles(layout));
    }

    /// <inheritdoc />
    public void Dispose() {
        table.Dispose();
        allocator.Dispose();
        device.Dispose();
    }

    /// <summary>A material's texture gets a slot, and the slot reaches the block.</summary>
    /// <remarks>
    ///     The offset is the effect's, so what is asserted is that the number landed where the
    ///     compiled shader says the parameter is — the same claim every other constant makes, which
    ///     is the point of making a texture a constant at all.
    /// </remarks>
    [Fact]
    public void A_texture_becomes_a_number_in_the_block() {
        using var h = Build();
        var albedo = Texture();

        var material = Lit();
        material.Parameters.Set(Albedo, albedo);

        AddMesh(h, material);
        Frame(h);

        Assert.True(table.TryGetIndex(albedo, out var slot));
        Assert.Equal(slot, material.Parameters.Get(AlbedoIndex));
        Assert.Equal(slot, BitConverter.ToUInt32(Block(h)[16..]));
    }

    /// <summary>Two materials over one texture are one slot.</summary>
    /// <remarks>
    ///     The economy the table exists for, reached through the material path rather than by calling
    ///     it directly. An atlas shared by forty materials is one descriptor.
    /// </remarks>
    [Fact]
    public void Two_materials_over_one_texture_share_its_slot() {
        using var h = Build();
        var shared = Texture();

        var first = Lit();
        first.Parameters.Set(Albedo, shared);

        var second = Lit();
        second.Parameters.Set(Albedo, shared);

        AddMesh(h, first);
        AddMesh(h, second);
        Frame(h);

        Assert.Equal(first.Parameters.Get(AlbedoIndex), second.Parameters.Get(AlbedoIndex));
        Assert.Equal(1, table.Count);
    }

    /// <summary>
    ///     A settled scene registers nothing and uploads nothing, however many frames run.
    /// </summary>
    /// <remarks>
    ///     <strong>The one that matters.</strong> This runs in <c>Prepare</c>, so a naive
    ///     implementation asks the table for the same view every frame — and every ask is a reference
    ///     the slot never gets back. The symptom is not a wrong picture: it is a table that fills up
    ///     over a few minutes of play and then refuses a texture. The upload count is the same claim
    ///     one level up, since a parameter set to what it already holds must not bump the version.
    /// </remarks>
    [Fact]
    public void A_settled_material_costs_nothing_per_frame() {
        using var h = Build();

        var material = Lit();
        material.Parameters.Set(Albedo, Texture());

        AddMesh(h, material);
        Frame(h);

        var writes = table.WriteCount;
        var uploads = h.Materials.UploadCount;

        for (var frame = 0; frame < 30; frame++) {
            Frame(h);
        }

        Assert.Equal(writes, table.WriteCount);
        Assert.Equal(uploads, h.Materials.UploadCount);
        Assert.Equal(1, h.Materials.IndexedTextureCount);
    }

    /// <summary>A texture swapped for another takes a new slot and gives the old one back.</summary>
    /// <remarks>
    ///     Which is what an artist moving a slider does, and what a streaming system does when a
    ///     higher mip arrives. The old slot is released after the new one is taken, so a view shared
    ///     with whatever is being released does not lose its last reference in between.
    /// </remarks>
    [Fact]
    public void A_swapped_texture_releases_the_slot_it_left() {
        using var h = Build();
        var first = Texture();
        var second = Texture();

        var material = Lit();
        material.Parameters.Set(Albedo, first);

        AddMesh(h, material);
        Frame(h);

        var before = material.Parameters.Get(AlbedoIndex);

        material.Parameters.Set(Albedo, second);
        Frame(h);

        Assert.NotEqual(before, material.Parameters.Get(AlbedoIndex));
        Assert.False(table.TryGetIndex(first, out _));
        Assert.True(table.TryGetIndex(second, out _));
        Assert.Equal(1, table.Count);
    }

    /// <summary>A material with no texture names slot zero rather than nothing.</summary>
    /// <remarks>
    ///     A shader samples <c>textures[albedoIndex]</c> whatever the host had to say — there is no
    ///     branch that could skip it — so an unset index has to name a slot that exists.
    ///     <see cref="BindlessTable" />'s fallback is what makes zero a defined thing to sample rather
    ///     than whatever the driver left there.
    /// </remarks>
    [Fact]
    public void A_material_with_no_texture_names_slot_zero() {
        using var h = Build();

        var material = Lit();
        AddMesh(h, material);
        Frame(h);

        Assert.Equal(0u, material.Parameters.Get(AlbedoIndex));
        Assert.Equal(0, h.Materials.IndexedTextureCount);
    }

    /// <summary>Each paired parameter is filled from its own texture.</summary>
    /// <remarks>
    ///     Two of them, because one would pass with a loop that wrote the last pair's index into
    ///     every parameter — and a normal map sampled as a base colour is a plausible-looking frame.
    /// </remarks>
    [Fact]
    public void Each_pairing_is_filled_from_its_own_texture() {
        using var h = Build();
        var albedo = Texture();
        var normal = Texture();

        var material = Lit();
        material.Parameters.Set(Albedo, albedo);
        material.Parameters.Set(Normal, normal);

        AddMesh(h, material);
        Frame(h);

        Assert.True(table.TryGetIndex(albedo, out var albedoSlot));
        Assert.True(table.TryGetIndex(normal, out var normalSlot));

        Assert.NotEqual(albedoSlot, normalSlot);
        Assert.Equal(albedoSlot, material.Parameters.Get(AlbedoIndex));
        Assert.Equal(normalSlot, material.Parameters.Get(NormalIndex));
    }

    /// <summary>
    ///     Without a table nothing is indexed, which is the non-bindless path unchanged.
    /// </summary>
    /// <remarks>
    ///     Not a legacy concession — it is what runs on GL, on WebGL2 and on MoltenVK below
    ///     argument-buffer tier 2 (ADR-011), so it has to stay exactly as it was rather than becoming
    ///     a path that happens to still compile.
    /// </remarks>
    [Fact]
    public void Without_a_table_nothing_is_indexed() {
        using var h = Build(indexed: false);

        var material = Lit();
        material.Parameters.Set(Albedo, Texture());

        AddMesh(h, material);
        Frame(h);

        Assert.Equal(0, table.Count);
        Assert.Equal(0, h.Materials.IndexedTextureCount);
        Assert.False(material.Parameters.Has(AlbedoIndex));
    }

    /// <summary>A feature disposed gives its slots back to a table that outlives it.</summary>
    /// <remarks>
    ///     The table is the frame's and the feature is a scene's, so tearing a scene down and building
    ///     another must not walk the table's high-water mark up by a scene's worth of textures each
    ///     time. Asserted on <see cref="BindlessTable.Count" /> rather than on the mark, since the
    ///     released slots only become available again after the frames that could name them retire.
    /// </remarks>
    [Fact]
    public void A_disposed_feature_gives_its_slots_back() {
        var h = Build();
        var material = Lit();
        material.Parameters.Set(Albedo, Texture());

        AddMesh(h, material);
        Frame(h);

        Assert.Equal(1, table.Count);

        h.Dispose();

        Assert.Equal(0, table.Count);
        Assert.Equal(0, h.Materials.IndexedTextureCount);
    }

    // --- The fixture -------------------------------------------------------

    /// <summary>The block as it was last filled, so an offset can be read out of it.</summary>
    static ReadOnlySpan<byte> Block(Harness h) => h.Materials.ConstantsOf(h.System, h.Objects[0]);

    static Material Lit() => new("Lit");

    /// <summary>A texture view of its own, so two of them are genuinely different resources.</summary>
    TextureViewHandle Texture() =>
        device.CreateTextureView(
            device.CreateTexture(new(PixelFormat.Rgba8UNorm, 4, 4, TextureUsage.Sampled, Name: "material texture"))
        );

    void Frame(Harness h) {
        allocator.BeginFrame();
        h.System.Draw();
    }

    Harness Build(bool indexed = true) {
        var system = new RenderSystem();
        var opaque = system.AddStage(new("Opaque"));

        var meshes = new MeshRenderFeature { Pipelines = new(device), Describer = new EffectPipelineDescriber(device) };

        var materials = new MaterialRenderFeature {
            Effects = effects,
            Device = device,
            Descriptors = allocator,
            Textures = indexed ? table : null
        };

        if (indexed) {
            materials.TextureIndices[AlbedoIndex] = Albedo;
            materials.TextureIndices[NormalIndex] = Normal;
        }

        meshes.Add(new TransformRenderFeature());
        meshes.Add(materials);
        system.AddFeature(meshes);

        var view = Matrix4x4.LookAt(Vector3.Zero, new(0f, 0f, 1f), new(0f, 1f, 0f));
        var projection = Matrix4x4.PerspectiveFieldOfView(MathF.PI / 3f, 1f, 0.1f, 1000f);

        system.SetViews(
            [new("camera") { Stages = opaque.Mask, Position = Vector3.Zero, Frustum = new(view * projection) }]
        );

        return new() {
            System = system,
            Opaque = opaque,
            Meshes = meshes,
            Materials = materials,
            Vertices = device.CreateBuffer(new() { Size = 1024, Usage = BufferUsage.Vertex })
        };
    }

    static void AddMesh(Harness h, Material material) {
        var id = h.System.Objects.Add(
            new() { Bounds = new(new Vector3(0f, 0f, 10f), 1f), Stages = h.Opaque.Mask, FeatureIndex = h.Meshes.Index }
        );

        h.System.Objects.Data.Data(h.Meshes.Draws)[id.Index] = new() {
            VertexBuffer = h.Vertices, Count = 3, InstanceCount = 1
        };

        h.Materials.Assign(h.System, id, material);
        h.Objects.Add(id);
    }

    sealed class Harness : IDisposable {
        public required RenderSystem System { get; init; }
        public required RenderStage Opaque { get; init; }
        public required MeshRenderFeature Meshes { get; init; }
        public required MaterialRenderFeature Materials { get; init; }
        public required BufferHandle Vertices { get; init; }

        public List<RenderObjectId> Objects { get; } = [];

        public void Dispose() => System.Dispose();
    }

    /// <summary>
    ///     A shader whose material textures are <c>uint</c>s, which is what a bindless material is.
    /// </summary>
    sealed class Compiles(DescriptorSetLayoutHandle lit) : IEffectProvider {
        public Effect? TryGet(EffectKey key) =>
            key.ShaderName == "Lit"
                ? new() {
                    Key = key,
                    Stages = [
                        new(ShaderStage.Vertex, [1, 2, 3, 4], "main"),
                        new(ShaderStage.Fragment, [5, 6, 7, 8], "main")
                    ],
                    SetLayouts = [default, default, lit, default],
                    ConstantBufferSize = 32,

                    // The tint first and the two indices after it, so an index written at the wrong
                    // offset lands on a colour rather than on another index.
                    Parameters = [new(Tint, 0, 12), new(AlbedoIndex, 16, 4), new(NormalIndex, 20, 4)],
                    Bindings = [
                        new("constants", DescriptorSetSlot.PerMaterial, 0, DescriptorKind.UniformBuffer) {
                            Size = 32
                        }
                    ]
                }
                : new() { Key = key, Stages = [], SetLayouts = [default, default, default, default] };
    }
}
