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
///     Materials as records of one buffer, rather than a descriptor set each.
/// </summary>
/// <remarks>
///     <para>
///         The engine half of what <c>[MaterialIndex]</c> made expressible. A draw that binds a set
///         per material cannot be merged with a draw that binds a different one; a buffer bound once
///         and subscripted by a number the draw carries can be.
///     </para>
///     <para>
///         <strong>Per effect, which is a correction to the plan.</strong> The sketch said one buffer
///         per <em>variant</em>. A variant is a <c>(material, flags, shader)</c> triple, so that is one
///         buffer per material with a single record in it — the opposite of the point. What several
///         materials share is their effect, and the sort group is already the engine's name for
///         exactly that.
///     </para>
/// </remarks>
public sealed class MaterialRecordBufferTests : IDisposable {
    static readonly ParameterKey<Vector3> Tint = ParameterKeys.New<Vector3>("Lit.tint");
    static readonly ParameterKey<float> Roughness = ParameterKeys.New<float>("Lit.roughness");

    const int Stride = 32;

    readonly NullDevice device = new(new() { Record = true });
    readonly DescriptorAllocator allocator;
    readonly EffectSystem effects = new();
    readonly DescriptorSetLayoutHandle plain;
    readonly DescriptorSetLayoutHandle lit;

    public MaterialRecordBufferTests() {
        allocator = new(device);

        // A real layout for the shader that keeps its set, so "it still binds one" is a claim about
        // the renderer rather than about a fixture with nowhere to bind.
        plain = device.CreateDescriptorSetLayout(
            new(
                DescriptorSetSlot.PerMaterial,
                [new(0, DescriptorKind.UniformBuffer, ShaderStage.Fragment)],
                "Plain"
            )
        );

        // And one for the records shader, which has a per-material set too — holding the buffer
        // rather than the block. A fixture without it would make "no set is bound" pass for the
        // reason that there was nowhere to bind rather than the reason under test.
        lit = device.CreateDescriptorSetLayout(
            new(
                DescriptorSetSlot.PerMaterial,
                [new(0, DescriptorKind.StorageBuffer, ShaderStage.Fragment)],
                "Lit"
            )
        );

        effects.AddProvider(new Compiles(plain, lit));
    }

    /// <inheritdoc />
    public void Dispose() {
        allocator.Dispose();
        device.Dispose();
    }

    /// <summary>Two materials of one effect are two records of one buffer.</summary>
    /// <remarks>
    ///     The claim the whole thing rests on. Two buffers here would mean a bind between the two
    ///     draws, which is the cost being removed.
    /// </remarks>
    [Fact]
    public void Two_materials_of_one_effect_share_a_buffer() {
        using var h = Build();

        var first = Lit(new(1f, 0f, 0f));
        var second = Lit(new(0f, 1f, 0f));

        var a = AddMesh(h, first);
        var b = AddMesh(h, second);

        Frame(h);

        var left = h.Materials.RecordOf(h.System, a);
        var right = h.Materials.RecordOf(h.System, b);

        Assert.NotNull(left);
        Assert.NotNull(right);
        Assert.Equal(left!.Value.Group, right!.Value.Group);
        Assert.NotEqual(left.Value.Index, right.Value.Index);
        Assert.Single(h.Materials.Records);
    }

    /// <summary>And each record holds its own material's values.</summary>
    /// <remarks>
    ///     Read out of the bytes the device was given, at the record the shader will subscript. Two
    ///     materials writing to one record is the failure this catches, and it would look like every
    ///     object being the colour of whichever material was prepared last.
    /// </remarks>
    [Fact]
    public void Each_record_holds_its_own_materials_values() {
        using var h = Build();

        var first = Lit(new(1f, 0f, 0f));
        var second = Lit(new(0f, 1f, 0f));

        var a = AddMesh(h, first);
        var b = AddMesh(h, second);

        Frame(h);

        var buffer = h.Materials.Records.Values.Single();
        var left = h.Materials.RecordOf(h.System, a)!.Value.Index;
        var right = h.Materials.RecordOf(h.System, b)!.Value.Index;

        Assert.Equal(1f, Read(buffer, left));
        Assert.Equal(0f, Read(buffer, right));
        Assert.Equal(2, buffer.Count);
    }

    /// <summary>
    ///     Two materials of one effect are one bind, not two.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <strong>The whole point, measured against the command stream.</strong> Everything else
    ///         here is about where a material's values live; this is the thing that living there was
    ///         for. Two materials used to be two <c>BindDescriptorSet</c>s between two draws, and a
    ///         draw that binds cannot be merged with a draw that binds something else.
    ///     </para>
    ///     <para>
    ///         It falls out of two pieces that were already there rather than a third: every variant
    ///         of a group asks the allocator for the same layout and the same single write, and the
    ///         allocator is content-addressed — so they get one handle, and the mesh feature's
    ///         "did this differ from the last one" check does the rest.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Two_materials_of_one_effect_are_one_bind() {
        using var h = Build();

        AddMesh(h, Lit(new(1f, 0f, 0f)));
        AddMesh(h, Lit(new(0f, 1f, 0f)));

        Frame(h);
        Record(h);

        Assert.Equal(1, device.Recorder!.CountOf(RecordedCommandKind.BindDescriptorSet));
    }

    /// <summary>And the same two materials bound per material are two binds.</summary>
    /// <remarks>
    ///     The measurement the one above is a claim against. Without it "one bind" could be a fixture
    ///     that binds nothing at all, which is what the first version of this test actually asserted.
    /// </remarks>
    [Fact]
    public void Bound_per_material_the_same_two_are_two_binds() {
        using var h = Build(records: false);

        AddMesh(h, new Material("Plain"));
        AddMesh(h, new Material("Plain"));

        Frame(h);
        Record(h);

        Assert.Equal(2, device.Recorder!.CountOf(RecordedCommandKind.BindDescriptorSet));
    }

    /// <summary>Every object of a group is handed the same set.</summary>
    [Fact]
    public void One_set_serves_a_whole_group() {
        using var h = Build();

        var a = AddMesh(h, Lit(new(1f, 0f, 0f)));
        var b = AddMesh(h, Lit(new(0f, 1f, 0f)));

        Frame(h);

        var left = h.Materials.DescriptorsOf(h.System, a);

        Assert.True(left.IsValid);
        Assert.Equal(left, h.Materials.DescriptorsOf(h.System, b));
    }

    /// <summary>A settled scene uploads nothing, however many frames run.</summary>
    /// <remarks>
    ///     The same economy <see cref="EffectConstants" /> has, one level up: the bytes are what
    ///     costs, and a buffer nobody changed must not go back on the bus because a frame happened.
    /// </remarks>
    [Fact]
    public void A_settled_scene_uploads_nothing() {
        using var h = Build();

        AddMesh(h, Lit(Vector3.One));
        AddMesh(h, Lit(new(0f, 0f, 1f)));
        Frame(h);

        var uploads = h.Materials.Records.Values.Single().UploadCount;
        Assert.True(uploads > 0, "the first frame uploaded nothing");

        for (var frame = 0; frame < 20; frame++) {
            Frame(h);
        }

        Assert.Equal(uploads, h.Materials.Records.Values.Single().UploadCount);
    }

    /// <summary>A value that changes re-uploads, and lands in that material's record alone.</summary>
    [Fact]
    public void A_changed_value_reaches_its_own_record() {
        using var h = Build();

        var first = Lit(new(1f, 0f, 0f));
        var second = Lit(new(0f, 1f, 0f));

        var a = AddMesh(h, first);
        AddMesh(h, second);
        Frame(h);

        var buffer = h.Materials.Records.Values.Single();
        var before = buffer.UploadCount;
        var untouched = Read(buffer, h.Materials.RecordOf(h.System, AtIndex(h, 1))!.Value.Index);

        first.Parameters.Set(Tint, new(0.25f, 0f, 0f));
        Frame(h);

        Assert.True(buffer.UploadCount > before, "a changed material did not re-upload");
        Assert.Equal(0.25f, Read(buffer, h.Materials.RecordOf(h.System, a)!.Value.Index));
        Assert.Equal(untouched, Read(buffer, h.Materials.RecordOf(h.System, AtIndex(h, 1))!.Value.Index));
    }

    /// <summary>
    ///     Turned off, everything is exactly as it was.
    /// </summary>
    /// <remarks>
    ///     The control that matters most. A descriptor set per material is what runs on GL, on WebGL2
    ///     and on every device with no bindless at all, so it is not a legacy branch and cannot be
    ///     allowed to rot behind the path that replaced it. A different claim from the test below it:
    ///     this one is about the <em>setting</em> being off, that one about the shader not having
    ///     asked — and a renderer could get either right and the other wrong.
    /// </remarks>
    [Fact]
    public void Turned_off_a_material_still_binds_a_set() {
        using var h = Build(records: false);

        var id = AddMesh(h, new Material("Plain"));
        Frame(h);

        Assert.Empty(h.Materials.Records);
        Assert.Null(h.Materials.RecordOf(h.System, id));
        Assert.True(h.Materials.DescriptorsOf(h.System, id).IsValid);
    }

    /// <summary>
    ///     And a shader that declared no record keeps its set even when records are on.
    /// </summary>
    /// <remarks>
    ///     Which is what makes a mixed frame work: a pass that asked for records and one that did not
    ///     are the same renderer, and the fork is the effect's rather than a setting's.
    /// </remarks>
    [Fact]
    public void A_shader_without_a_record_keeps_its_set() {
        using var h = Build();

        var id = AddMesh(h, new Material("Plain"));
        Frame(h);

        Assert.Empty(h.Materials.Records);
        Assert.Null(h.Materials.RecordOf(h.System, id));
        Assert.True(h.Materials.DescriptorsOf(h.System, id).IsValid);
    }

    /// <summary>The record index reaches the per-draw block, where the shader reads it.</summary>
    /// <remarks>
    ///     The last join in the chain, and the one nothing else asserts: a buffer full of correct
    ///     records is useless if the number saying which record an object is never leaves the
    ///     renderer. Read out of the bytes at the offset `ForwardPlus.rvn` declares — which the
    ///     checked-in reflection puts at 12, in the padding the three scalars before it already left.
    /// </remarks>
    [Fact]
    public void The_record_index_reaches_the_per_draw_block() {
        using var h = Build(lighting: true);

        var first = Lit(new(1f, 0f, 0f));
        var second = Lit(new(0f, 1f, 0f));

        var a = AddMesh(h, first);
        var b = AddMesh(h, second);

        Frame(h);

        Assert.Equal(
            (uint)h.Materials.RecordOf(h.System, a)!.Value.Index,
            IndexInBlock(h, a)
        );

        Assert.Equal(
            (uint)h.Materials.RecordOf(h.System, b)!.Value.Index,
            IndexInBlock(h, b)
        );
    }

    /// <summary>What the lighting feature wrote into one object's per-draw header.</summary>
    static uint IndexInBlock(Harness h, RenderObjectId id) {
        var block = h.Lighting!.Block(h.System, id);
        Assert.False(block.IsEmpty, "the object has no per-draw block");

        return BitConverter.ToUInt32(block[ForwardLightingRenderFeature.MaterialIndexOffset..]);
    }

    // --- The fixture -------------------------------------------------------

    static float Read(MaterialRecords buffer, int index) =>
        BitConverter.ToSingle(buffer.Bytes[(index * Stride)..]);

    static RenderObjectId AtIndex(Harness h, int index) => h.Objects[index];

    static Material Lit(Vector3 tint) {
        var material = new Material("Lit");
        material.Parameters.Set(Tint, tint);
        material.Parameters.Set(Roughness, 0.5f);
        return material;
    }

    void Frame(Harness h) {
        allocator.BeginFrame();
        h.System.Draw();
    }

    /// <summary>Records the stage, so the binds can be counted rather than inferred.</summary>
    void Record(Harness h) {
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

    Harness Build(bool records = true, bool lighting = false) {
        var system = new RenderSystem();
        var opaque = system.AddStage(new("Opaque"));

        var meshes = new MeshRenderFeature { Pipelines = new(device), Describer = new EffectPipelineDescriber(device) };

        var materials = new MaterialRenderFeature {
            Effects = effects,
            Device = device,
            Descriptors = allocator,
            UseRecords = records
        };

        meshes.Add(new TransformRenderFeature());
        meshes.Add(materials);

        ForwardLightingRenderFeature? lights = null;

        if (lighting) {
            lights = new() { Device = device };
            meshes.Add(lights);
        }

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
            Lighting = lights,
            Vertices = device.CreateBuffer(new() { Size = 1024, Usage = BufferUsage.Vertex })
        };
    }

    static RenderObjectId AddMesh(Harness h, Material material) {
        var id = h.System.Objects.Add(
            new() { Bounds = new(new Vector3(0f, 0f, 10f), 1f), Stages = h.Opaque.Mask, FeatureIndex = h.Meshes.Index }
        );

        h.System.Objects.Data.Data(h.Meshes.Draws)[id.Index] = new() {
            VertexBuffer = h.Vertices, Count = 3, InstanceCount = 1
        };

        h.Materials.Assign(h.System, id, material);
        h.Objects.Add(id);
        return id;
    }

    sealed class Harness : IDisposable {
        public required RenderSystem System { get; init; }
        public required RenderStage Opaque { get; init; }
        public required MeshRenderFeature Meshes { get; init; }
        public required MaterialRenderFeature Materials { get; init; }
        public ForwardLightingRenderFeature? Lighting { get; init; }
        public required BufferHandle Vertices { get; init; }

        public List<RenderObjectId> Objects { get; } = [];

        public void Dispose() => System.Dispose();
    }

    /// <summary>
    ///     One shader whose per-material values are a record, and one whose are a block.
    /// </summary>
    sealed class Compiles(DescriptorSetLayoutHandle plain, DescriptorSetLayoutHandle lit) : IEffectProvider {
        public Effect? TryGet(EffectKey key) =>
            key.ShaderName switch {
                "Lit" => new() {
                    Key = key,
                    Stages = Modules,
                    SetLayouts = [default, default, lit, default],
                    Parameters = [
                        new(Tint, 0, 12) { Set = DescriptorSetSlot.PerMaterial },
                        new(Roughness, 16, 4) { Set = DescriptorSetSlot.PerMaterial }
                    ],

                    // A storage buffer with members is a material record; its Size is one record's
                    // stride, which is what the reflection reports for a buffer's element.
                    Bindings = [
                        new("materials", DescriptorSetSlot.PerMaterial, 0, DescriptorKind.StorageBuffer) {
                            Size = Stride, Count = 0
                        }
                    ]
                },
                _ => new() {
                    Key = key,
                    Stages = Modules,
                    SetLayouts = [default, default, plain, default],
                    ConstantBufferSize = 16,
                    Parameters = [new(Tint, 0, 12) { Set = DescriptorSetSlot.PerMaterial }],
                    Bindings = [
                        new("constants", DescriptorSetSlot.PerMaterial, 0, DescriptorKind.UniformBuffer) {
                            Size = 16
                        }
                    ]
                }
            };

        static ImmutableArray<EffectStage> Modules =>
            [new(ShaderStage.Vertex, [1, 2, 3, 4], "main"), new(ShaderStage.Fragment, [5, 6, 7, 8], "main")];
    }
}
