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
///     The frame that actually takes the bindless path, rather than the mechanisms it is made of.
/// </summary>
/// <remarks>
///     <para>
///         Every piece of <c>docs/bindless-materials.md</c> was reachable from a test and from
///         nothing else for a while: no code outside the tests built a <c>BindlessTable</c>, set
///         <c>TextureIndices</c>, turned <c>UseRecords</c> on, or asked for the
///         <c>UseMaterialRecords</c> variant. A mechanism nothing invokes is a mechanism that
///         compiles, and these are the assertions that it runs.
///     </para>
///     <para>
///         ⚠ <strong>The capability decides, and both halves of it move together.</strong>
///         <c>UseRecords</c> says where the host writes a material's bytes and the permutation says
///         which shader is compiled to read them. Either alone draws a picture and the picture is
///         wrong — records nothing reads, or a subscript into a buffer the host never filled.
///     </para>
/// </remarks>
public sealed class BindlessFrameTests : IDisposable {
    static readonly ParameterKey<Vector3> Tint = ParameterKeys.New<Vector3>("Lit.tint");
    static readonly ParameterKey<uint> BaseColorIndex = ParameterKeys.New<uint>("Lit.baseColorIndex");
    static readonly ParameterKey<TextureViewHandle> BaseColor = ParameterKeys.New<TextureViewHandle>("baseColor");
    static readonly PermutationKey<bool> UseRecords = ParameterKeys.NewPermutation(false, "Lit.UseMaterialRecords");

    const int Stride = 32;

    readonly NullDevice device;
    readonly DescriptorAllocator allocator;
    readonly EffectSystem effects = new();
    readonly DescriptorSetLayoutHandle material;
    readonly List<EffectKey> resolved = [];

    public BindlessFrameTests() : this(bindless: true) { }

    BindlessFrameTests(bool bindless) {
        device = new(new() { Record = true, Features = Features(bindless) });
        allocator = new(device);

        material = device.CreateDescriptorSetLayout(
            new(DescriptorSetSlot.PerMaterial, [new(0, DescriptorKind.StorageBuffer, ShaderStage.Fragment)], "Lit")
        );

        effects.AddProvider(new Compiles(material, device, resolved));
    }

    /// <summary>A device that reports bindless, or one that reports it absent.</summary>
    /// <remarks>
    ///     Both halves of the pair, because a device claiming a table with four bindable sets is the
    ///     combination <c>HasBindless</c> exists to rule out — see <c>DescriptorSetSlot.Bindless</c>.
    /// </remarks>
    static GraphicsDeviceFeatures Features(bool bindless) =>
        bindless
            ? NullDevice.Everything
            : NullDevice.Everything with { HasBindless = false, MaxBindlessDescriptors = 0, MaxDescriptorSets = 4 };

    public void Dispose() => device.Dispose();

    // --- The decision -------------------------------------------------------

    /// <summary>On a bindless device, both halves come on together.</summary>
    [Fact]
    public void The_capability_turns_records_on() {
        using var harness = Build();

        Assert.True(harness.Materials.EnableRecords(UseRecords));
        Assert.True(harness.Materials.UseRecords);
        Assert.True(harness.Materials.Permutations.Get(UseRecords));
    }

    /// <summary>And on a device without it, both stay off.</summary>
    /// <remarks>
    ///     The control, and the one that matters most: this is what runs on GL, on WebGL2 and on
    ///     MoltenVK below argument-buffer tier 2 (ADR-011). It is not a degraded path — it is the
    ///     path most devices take, and it draws the same image.
    /// </remarks>
    [Fact]
    public void Without_the_capability_neither_half_comes_on() {
        using var plain = new BindlessFrameTests(bindless: false);
        using var harness = plain.Build();

        Assert.False(harness.Materials.EnableRecords(UseRecords));
        Assert.False(harness.Materials.UseRecords);
        Assert.False(harness.Materials.Permutations.Get(UseRecords));
    }

    /// <summary>
    ///     The frame's permutation reaches the effect key, so the right shader is compiled.
    /// </summary>
    /// <remarks>
    ///     <strong>What a wrong answer would hide.</strong> Setting <c>UseRecords</c> without the
    ///     permutation is the failure with no symptom: the host fills a record buffer, the shader is
    ///     compiled to read a uniform block, and the block is whatever the allocator left there. It
    ///     draws — which is why the assertion is on the resolved key rather than on the picture.
    /// </remarks>
    [Fact]
    public void The_frames_permutation_reaches_the_effect_key() {
        using var harness = Build();
        harness.Materials.EnableRecords(UseRecords);

        AddMesh(harness, Material());
        Frame(harness);

        var key = Assert.Single(resolved, candidate => candidate.ShaderName == "Lit");
        Assert.Contains("UseMaterialRecords=true", key.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    ///     A material cannot claim a capability the device does not have.
    /// </summary>
    /// <remarks>
    ///     The frame's values are applied after the material's and after every sub-feature's, and
    ///     that ordering is the point. A material is authored on one machine and drawn on another; a
    ///     material that had set this key for itself would otherwise compile a shader indexing a
    ///     descriptor array the device cannot create.
    /// </remarks>
    [Fact]
    public void A_material_cannot_overrule_the_device() {
        using var plain = new BindlessFrameTests(bindless: false);
        using var harness = plain.Build();

        harness.Materials.EnableRecords(UseRecords);

        var claiming = Material();
        claiming.Parameters.Set(UseRecords, true);

        AddMesh(harness, claiming);
        plain.Frame(harness);

        var key = Assert.Single(plain.resolved, candidate => candidate.ShaderName == "Lit");
        Assert.Contains("UseMaterialRecords=false", key.ToString(), StringComparison.Ordinal);
    }

    // --- The table's set ----------------------------------------------------

    /// <summary>The table's set is bound once for the frame, at set 4.</summary>
    /// <remarks>
    ///     Once, not once per object and not once per material — the table is not rewritten by a
    ///     frame at all, so all a frame does is name it. Counting the binds at set 4 is what says so:
    ///     a version that bound it per draw would draw the same image and throw away the economy.
    /// </remarks>
    [Fact]
    public void The_tables_set_is_bound_once_for_the_frame() {
        using var harness = Build(table: true);
        harness.Materials.EnableRecords(UseRecords);

        AddMesh(harness, Material(new(1f, 0f, 0f)));
        AddMesh(harness, Material(new(0f, 1f, 0f)));

        Frame(harness);
        device.Recorder!.Clear();
        RecordStage(harness);

        Assert.Equal(1, BindsAt(DescriptorSetSlot.Bindless));
    }

    /// <summary>
    ///     And it is not bound at all for an effect whose layout does not declare it.
    /// </summary>
    /// <remarks>
    ///     <strong>The failure this rules out.</strong> A variant compiled without the table has a
    ///     four-set pipeline layout, and binding a fifth set against one is a validation error rather
    ///     than a harmless extra call. That is not a hypothetical mixed frame: the non-bindless
    ///     variant of the same shader is what every device without descriptor indexing compiles, and
    ///     a depth prepass overriding the shader produces one in the middle of a bindless frame.
    /// </remarks>
    [Fact]
    public void An_effect_without_the_set_is_not_bound_one() {
        using var harness = Build();
        harness.Materials.EnableRecords(UseRecords);

        AddMesh(harness, Material());

        Frame(harness);
        device.Recorder!.Clear();
        RecordStage(harness);

        Assert.Equal(0, BindsAt(DescriptorSetSlot.Bindless));
    }

    /// <summary>A table registers a material's texture and the slot lands in the material.</summary>
    /// <remarks>
    ///     <para>
    ///         The whole of 2a in one assertion: the shader declares a <c>uint</c>, the host puts the
    ///         view in the table, and the index goes into the material's own parameters where
    ///         <c>EffectConstants</c> writes it out beside the base colour with no idea it means a
    ///         descriptor.
    ///     </para>
    ///     <para>
    ///         Two materials, and the index checked against the table rather than against zero.
    ///         Slot zero is a real slot a real texture can occupy — it is the <em>unwritten</em>
    ///         index's value, not a reserved one — so "is it non-zero" would be a coin toss on which
    ///         material happened to register first.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_materials_texture_becomes_an_index_in_its_own_block() {
        using var harness = Build(table: true);
        var table = harness.Materials.Textures!;

        var first = Material();
        var firstView = View();
        first.Parameters.Set(BaseColor, firstView);

        var second = Material();
        var secondView = View();
        second.Parameters.Set(BaseColor, secondView);

        // And one with no map at all, which must still name a slot the shader can sample.
        var bare = Material();

        AddMesh(harness, first);
        AddMesh(harness, second);
        AddMesh(harness, bare);
        Frame(harness);

        Assert.True(table.TryGetIndex(firstView, out var firstSlot));
        Assert.True(table.TryGetIndex(secondView, out var secondSlot));

        Assert.Equal(firstSlot, first.Parameters.Get(BaseColorIndex));
        Assert.Equal(secondSlot, second.Parameters.Get(BaseColorIndex));
        Assert.NotEqual(firstSlot, secondSlot);

        Assert.Equal(0u, bare.Parameters.Get(BaseColorIndex));
        Assert.Equal(2, harness.Materials.IndexedTextureCount);
    }

    /// <summary>
    ///     And a settled scene stops registering, which is what keeps the table from filling up.
    /// </summary>
    /// <remarks>
    ///     ⚠ <strong>The failure has no wrong picture.</strong> This runs in <c>Prepare</c>, every
    ///     frame. A table asked for the same view sixty times a second raises a reference count
    ///     nothing lowers, and the symptom is a table that refuses a texture some minutes into a
    ///     session — long after the frame that caused it.
    /// </remarks>
    [Fact]
    public void A_settled_material_does_not_register_again() {
        using var harness = Build(table: true);
        var table = harness.Materials.Textures!;

        var textured = Material();
        textured.Parameters.Set(BaseColor, View());

        AddMesh(harness, textured);
        Frame(harness);

        var writes = table.WriteCount;

        for (var frame = 0; frame < 8; frame++) {
            Frame(harness);
        }

        Assert.Equal(writes, table.WriteCount);
        Assert.Equal(1, harness.Materials.IndexedTextureCount);
    }

    // --- The set writer -----------------------------------------------------

    /// <summary>
    ///     A set holding an unbounded binding is still complete.
    /// </summary>
    /// <remarks>
    ///     <strong>The regression this is here for.</strong> <c>EffectSetWriter</c> counts what a set
    ///     wants and refuses to bind a set short of one entry, which is right. An unbounded array is
    ///     not one of those entries — the table owns it — so counting it would make every set holding
    ///     one permanently incomplete. And the caller's answer to incomplete is to bind
    ///     <em>nothing</em>: a shader that gained a table would lose the set it was already binding,
    ///     and the frame would go dark rather than untextured.
    /// </remarks>
    [Fact]
    public void An_unbounded_binding_does_not_make_a_set_incomplete() {
        var effect = new Effect {
            Key = EffectKey.Of("Probe"),
            Stages = [new(ShaderStage.Fragment, [1, 2, 3, 4], "main")],
            Bindings = [
                new("block", DescriptorSetSlot.PerFrame, 0, DescriptorKind.UniformBuffer) { Size = 16 },
                new("table", DescriptorSetSlot.PerFrame, 1, DescriptorKind.SampledTexture) { Count = 0 }
            ]
        };

        using var constants = new EffectConstants(device, "Probe");
        constants.Update(effect, 16, [new(Tint, 0, 12)], new());

        List<DescriptorWrite> writes = [];

        Assert.True(EffectSetWriter.TryWrite(effect, DescriptorSetSlot.PerFrame, new(), constants, writes));
        Assert.Single(writes);
    }

    // --- The fixture --------------------------------------------------------

    static Material Material(Vector3 tint = default) {
        var made = new Material("Lit");
        made.Parameters.Set(Tint, tint);
        return made;
    }

    TextureViewHandle View() =>
        device.CreateTextureView(device.CreateTexture(new(PixelFormat.Rgba8UNorm, 4, 4, TextureUsage.Sampled)));

    int BindsAt(DescriptorSetSlot slot) =>
        device.Recorder!.OfKind(RecordedCommandKind.BindDescriptorSet).Count(command => command.A == (long)slot);

    void Frame(Harness h) {
        allocator.BeginFrame();
        h.System.Draw();
    }

    void RecordStage(Harness h) {
        var target = device.CreateTextureView(
            device.CreateTexture(new(PixelFormat.Rgba8UNorm, 16, 16, TextureUsage.ColourTarget, Name: "target"))
        );

        using var list = device.BeginCommandList();
        list.BeginRenderPass(new([new(target)], name: "Opaque"));

        h.System.Record(
            h.System.Views[0],
            h.Opaque,
            new(list, effects) { Device = device, Output = new([PixelFormat.Rgba8UNorm]) }
        );

        list.EndRenderPass();
        list.Finish();
        device.GraphicsQueue.Submit([list]);
    }

    Harness Build(bool table = false) {
        var system = new RenderSystem();
        var opaque = system.AddStage(new("Opaque"));

        var meshes = new MeshRenderFeature { Pipelines = new(device), Describer = new EffectPipelineDescriber(device) };

        var materials = new MaterialRenderFeature { Effects = effects, Device = device, Descriptors = allocator };

        materials.PermutationKeys["Lit"] = [UseRecords];

        if (table) {
            materials.Textures = new(device, capacity: 64, fallback: View());
            materials.TextureIndices[BaseColorIndex] = BaseColor;
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
    }

    sealed class Harness : IDisposable {
        public required RenderSystem System { get; init; }
        public required RenderStage Opaque { get; init; }
        public required MeshRenderFeature Meshes { get; init; }
        public required MaterialRenderFeature Materials { get; init; }
        public required BufferHandle Vertices { get; init; }

        /// <remarks>
        ///     ⚠ The system first. Disposing the feature is what gives its table slots back, so a
        ///     table disposed ahead of it is one the release walks into — which is the same ordering
        ///     a host has to get right and worth having the fixture demonstrate rather than dodge.
        /// </remarks>
        public void Dispose() {
            System.Dispose();
            Materials.Textures?.Dispose();
        }
    }

    /// <summary>
    ///     One shader, in two shapes: with the table's set when the permutation asked for it.
    /// </summary>
    /// <remarks>
    ///     The two shapes are the point. A variant compiled with <c>UseMaterialRecords</c> declares
    ///     five sets; the one compiled without it declares four, which is what every device with no
    ///     descriptor indexing gets and what the <c>[Bindless]</c> marker's own permutation gate
    ///     produces.
    /// </remarks>
    sealed class Compiles(DescriptorSetLayoutHandle material, NullDevice device, List<EffectKey> resolved)
        : IEffectProvider {
        DescriptorSetLayoutHandle table;

        public Effect? TryGet(EffectKey key) {
            resolved.Add(key);

            var bindless = key.ToString().Contains("UseMaterialRecords=true", StringComparison.Ordinal)
                && device.Features.HasBindless;

            if (bindless && !table.IsValid) {
                table = device.CreateDescriptorSetLayout(
                    new(
                        DescriptorSetSlot.Bindless,
                        [new(0, DescriptorKind.SampledTexture, ShaderStage.Fragment, 0)],
                        "Table",
                        64
                    )
                );
            }

            return new() {
                Key = key,
                Stages = Modules,
                SetLayouts = bindless
                    ? [default, default, material, default, table]
                    : [default, default, material, default],
                Parameters = [new(Tint, 0, 12) { Set = DescriptorSetSlot.PerMaterial }],
                Bindings = [
                    new("materials", DescriptorSetSlot.PerMaterial, 0, DescriptorKind.StorageBuffer) {
                        Size = Stride, Count = 0
                    }
                ]
            };
        }

        static ImmutableArray<EffectStage> Modules =>
            [new(ShaderStage.Vertex, [1, 2, 3, 4], "main"), new(ShaderStage.Fragment, [5, 6, 7, 8], "main")];
    }
}
