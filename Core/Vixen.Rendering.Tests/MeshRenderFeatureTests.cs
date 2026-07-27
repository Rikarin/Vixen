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
///     The first concrete renderable, end to end — docs/plan/06 § Geometry and materials.
/// </summary>
/// <remarks>
///     A mesh feature with a transform and a material sub-feature, driven through the whole frame
///     and recorded into the Null backend, so what is asserted is the actual command stream rather
///     than an intention. This is where every piece built so far has to agree at once: per-feature
///     arrays, sort groups derived from the resolved effect, contiguous runs, the effect cache, and
///     push constants.
/// </remarks>
public class MeshRenderFeatureTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true });
    readonly EffectSystem effects = new();

    // --- Fixture ------------------------------------------------------------

    static Effect Compiled(EffectKey key) =>
        new() {
            Key = key,
            Stages = [
                new(ShaderStage.Vertex, [1, 2, 3, 4], "main"),
                new(ShaderStage.Fragment, [5, 6, 7, 8], "main")
            ]
        };

    sealed class AlwaysCompiles : IEffectProvider {
        public Effect? TryGet(EffectKey key) => Compiled(key);
    }

    sealed class Harness : IDisposable {
        public required RenderSystem System { get; init; }
        public required RenderStage Opaque { get; init; }
        public required RenderView Camera { get; init; }
        public required MeshRenderFeature Meshes { get; init; }
        public required TransformRenderFeature Transforms { get; init; }
        public required MaterialRenderFeature Materials { get; init; }
        public required BufferHandle Vertices { get; init; }
        public required BufferHandle Indices { get; init; }

        public void Dispose() => System.Dispose();
    }

    Harness Build() {
        var system = new RenderSystem();
        var opaque = system.AddStage(new("Opaque"));

        var meshes = new MeshRenderFeature {
            Pipelines = new(device),
            Describer = new EffectPipelineDescriber(device)
        };

        var transforms = new TransformRenderFeature();
        var materials = new MaterialRenderFeature { Effects = effects };

        meshes.Add(transforms);
        meshes.Add(materials);
        system.AddFeature(meshes);

        effects.AddProvider(new AlwaysCompiles());

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
            Transforms = transforms,
            Materials = materials,
            Vertices = device.CreateBuffer(new() { Size = 1024, Usage = BufferUsage.Vertex }),
            Indices = device.CreateBuffer(new() { Size = 1024, Usage = BufferUsage.Index })
        };
    }

    static RenderObjectId AddMesh(Harness h, float z, Material material, int indices = 36) {
        var id = h.System.Objects.Add(
            new() {
                Bounds = new(new Vector3(0f, 0f, z), 1f),
                Stages = h.Opaque.Mask,
                FeatureIndex = h.Meshes.Index
            }
        );

        h.System.Objects.Data.Data(h.Meshes.Draws)[id.Index] = new() {
            VertexBuffer = h.Vertices,
            IndexBuffer = h.Indices,
            IndexFormat = IndexFormat.UInt16,
            Count = indices,
            InstanceCount = 1
        };

        h.System.Objects.Data.Data(h.Transforms.World)[id.Index] = Matrix4x4.Identity;
        h.Materials.Assign(h.System, id, material);
        return id;
    }

    ICommandList Record(Harness h) {
        h.System.Draw();

        var target = device.CreateTextureView(
            device.CreateTexture(
                new() {
                    Width = 16, Height = 16, Depth = 1,
                    MipLevels = 1, ArrayLayers = 1, SampleCount = 1,
                    Format = PixelFormat.Rgba8UNorm, Usage = TextureUsage.ColourTarget
                }
            )
        );

        var list = device.BeginCommandList();
        list.BeginRenderPass(new([new(target)], name: "Opaque"));

        h.System.Record(
            h.Camera,
            h.Opaque,
            new(list, effects) { Device = device, Output = new([PixelFormat.Rgba8UNorm]) }
        );

        list.EndRenderPass();
        list.Finish();
        device.GraphicsQueue.Submit([list]);

        return list;
    }

    /// <summary>A one-texture per-material descriptor set, which is the smallest realistic one.</summary>
    DescriptorSetHandle MaterialSet() =>
        device.CreateDescriptorSet(
            device.CreateDescriptorSetLayout(
                new(
                    DescriptorSetSlot.PerMaterial,
                    [new(0, DescriptorKind.SampledTexture, ShaderStage.Fragment)]
                )
            )
        );

    /// <inheritdoc />
    public void Dispose() {
        device.Dispose();
        GC.SuppressFinalize(this);
    }

    // --- The draw itself ----------------------------------------------------

    [Fact]
    public void An_indexed_mesh_produces_an_indexed_draw() {
        using var h = Build();
        AddMesh(h, 10f, new Material("Lit"), indices: 36);

        Record(h);

        var draw = Assert.Single(device.Recorder!.OfKind(RecordedCommandKind.DrawIndexed));
        Assert.Equal(36, draw.A);
        Assert.Equal(1, draw.B);

        Assert.Single(device.Recorder.OfKind(RecordedCommandKind.BindVertexBuffer));
        Assert.Single(device.Recorder.OfKind(RecordedCommandKind.BindIndexBuffer));
    }

    /// <summary>A mesh with no index buffer draws non-indexed rather than not at all.</summary>
    [Fact]
    public void A_mesh_with_no_index_buffer_draws_unindexed() {
        using var h = Build();
        var id = AddMesh(h, 10f, new Material("Lit"));

        h.System.Objects.Data.Data(h.Meshes.Draws)[id.Index].IndexBuffer = default;

        Record(h);

        Assert.Single(device.Recorder!.OfKind(RecordedCommandKind.Draw));
        Assert.Empty(device.Recorder.OfKind(RecordedCommandKind.DrawIndexed));
    }

    [Fact]
    public void An_object_with_no_mesh_draws_nothing() {
        using var h = Build();
        var id = AddMesh(h, 10f, new Material("Lit"));

        h.System.Objects.Data.Data(h.Meshes.Draws)[id.Index].Count = 0;

        Record(h);

        Assert.Empty(device.Recorder!.OfKind(RecordedCommandKind.DrawIndexed));
    }

    /// <summary>
    ///     An object whose material resolved to no effect is skipped, not drawn with the last one.
    /// </summary>
    /// <remarks>
    ///     Skipping is the only safe answer: drawing it would put one object's shader on another's
    ///     geometry, which is an image that is wrong rather than one that is missing something — and
    ///     wrong is much harder to notice.
    /// </remarks>
    [Fact]
    public void An_object_whose_effect_is_missing_is_skipped_rather_than_mis_drawn() {
        using var h = Build();
        AddMesh(h, 10f, new Material("Lit"));

        // A second system whose effect system has no provider at all.
        var starved = new EffectSystem();
        h.Materials.Effects = starved;

        Record(h);

        Assert.Empty(device.Recorder!.OfKind(RecordedCommandKind.DrawIndexed));
        Assert.Equal(1, starved.MissCount);
    }

    // --- What the sort buys -------------------------------------------------

    /// <summary>
    ///     Objects sharing a material bind one pipeline between them, however many there are.
    /// </summary>
    /// <remarks>
    ///     The whole chain paying off at once: the material feature gives equal effects equal sort
    ///     groups, the sort key puts groups above depth, the collector emits them adjacent, and the
    ///     mesh feature therefore sees one run and binds once. Break any link and this becomes four
    ///     binds.
    /// </remarks>
    [Fact]
    public void Objects_sharing_an_effect_bind_one_pipeline_between_them() {
        using var h = Build();
        var shared = new Material("Lit");

        for (var i = 0; i < 4; i++) {
            AddMesh(h, 10f + i, shared);
        }

        Record(h);

        Assert.Equal(4, device.Recorder!.CountOf(RecordedCommandKind.DrawIndexed));
        Assert.Equal(1, device.Recorder.CountOf(RecordedCommandKind.BindPipeline));
        Assert.Equal(1, h.Meshes.Pipelines!.Count);
    }

    /// <summary>
    ///     Two materials on one shader with different permutations are two pipelines, and the
    ///     objects are grouped rather than interleaved.
    /// </summary>
    /// <remarks>
    ///     Interleaved by depth in the scene — near, far, near, far — so a sort that ignored the
    ///     group would bind four times. Grouping brings it to two, which is the number a
    ///     state-change-minimising sort is measured by.
    /// </remarks>
    [Fact]
    public void Two_variants_are_two_pipelines_and_the_objects_are_grouped() {
        using var h = Build();

        var shadows = ParameterKeys.NewPermutation(false, "Test.Mesh.UseShadows");
        h.Materials.PermutationKeys["Lit"] = [shadows];

        var lit = new Material("Lit");
        lit.Parameters.Set(shadows, true);

        var unlit = new Material("Lit");
        unlit.Parameters.Set(shadows, false);

        AddMesh(h, 10f, lit);
        AddMesh(h, 20f, unlit);
        AddMesh(h, 30f, lit);
        AddMesh(h, 40f, unlit);

        Record(h);

        Assert.Equal(4, device.Recorder!.CountOf(RecordedCommandKind.DrawIndexed));
        Assert.Equal(2, device.Recorder.CountOf(RecordedCommandKind.BindPipeline));
        Assert.Equal(2, h.Meshes.Pipelines!.Count);
        Assert.Equal(2, effects.Count);
    }

    /// <summary>A descriptor set is bound when the material changes, not per draw.</summary>
    [Fact]
    public void A_descriptor_set_is_bound_once_per_material_run() {
        using var h = Build();

        var first = new Material("Lit") { Descriptors = MaterialSet() };
        var second = new Material("Lit") { Descriptors = MaterialSet() };

        AddMesh(h, 10f, first);
        AddMesh(h, 20f, first);
        AddMesh(h, 30f, second);

        Record(h);

        // Two materials, two binds — not three, and not one.
        Assert.Equal(2, device.Recorder!.CountOf(RecordedCommandKind.BindDescriptorSet));
    }

    // --- The transform ------------------------------------------------------

    /// <summary>
    ///     The transform is pushed per draw, and the bytes are the host's matrix unchanged.
    /// </summary>
    /// <remarks>
    ///     Unchanged is the assertion that matters. The engine stores row-major with the translation
    ///     in <c>M41..M43</c> and the shader reads the same bytes as <c>ColMajor</c>, which makes its
    ///     matrix the transpose <c>mul(v, M)</c> wants — so transposing here would compute the wrong
    ///     transform more expensively. See docs/plan/07 § E.
    /// </remarks>
    [Fact]
    public void The_world_matrix_is_pushed_per_draw() {
        using var h = Build();
        var id = AddMesh(h, 10f, new Material("Lit"));

        h.System.Objects.Data.Data(h.Transforms.World)[id.Index] = new(
            new Vector4(1f, 0f, 0f, 0f),
            new Vector4(0f, 1f, 0f, 0f),
            new Vector4(0f, 0f, 1f, 0f),
            new Vector4(7f, 8f, 9f, 1f)
        );

        Record(h);

        var push = Assert.Single(device.Recorder!.OfKind(RecordedCommandKind.PushConstants));

        // 64 bytes at offset 0 — one mat4, well inside Vulkan's guaranteed 128.
        Assert.Equal(0, push.B);
        Assert.Equal(64, push.C);
    }

    [Fact]
    public void Every_drawn_object_gets_its_own_push() {
        using var h = Build();
        var shared = new Material("Lit");

        AddMesh(h, 10f, shared);
        AddMesh(h, 20f, shared);
        AddMesh(h, 30f, shared);

        Record(h);

        Assert.Equal(3, device.Recorder!.CountOf(RecordedCommandKind.PushConstants));
    }

    // --- The arrangement itself ---------------------------------------------

    /// <summary>
    ///     Each feature registered its own array, and none of them knows about the others'.
    /// </summary>
    /// <remarks>
    ///     The claim `RenderDataHolder` exists to make, asserted rather than described: three
    ///     features, three arrays, one object count. A fourth — skinning — would be a fourth array
    ///     and no change to any of these three.
    /// </remarks>
    [Fact]
    public void The_three_features_hold_three_independent_arrays() {
        using var h = Build();
        AddMesh(h, 10f, new Material("Lit"));

        var data = h.System.Objects.Data;

        Assert.Equal(3, data.ArrayCount);
        Assert.True(data.Data(h.Meshes.Draws).Length >= h.System.Objects.Count);
        Assert.True(data.Data(h.Transforms.World).Length >= h.System.Objects.Count);
        Assert.True(data.Data(h.Materials.MaterialIndex).Length >= h.System.Objects.Count);
    }

    /// <summary>
    ///     An object nobody assigned a material to does not silently claim the first one.
    /// </summary>
    /// <remarks>
    ///     The arrays start zeroed and zero is a valid list index, so without a sentinel at slot 0
    ///     every unassigned object would render with whichever material happened to be registered
    ///     first — plausible enough to ship.
    /// </remarks>
    [Fact]
    public void An_unassigned_object_has_no_material_rather_than_the_first_one() {
        using var h = Build();
        h.Materials.Add(new("Lit"));

        var id = h.System.Objects.Add(
            new() {
                Bounds = new(new Vector3(0f, 0f, 10f), 1f),
                Stages = h.Opaque.Mask,
                FeatureIndex = h.Meshes.Index
            }
        );

        h.System.Objects.Data.Data(h.Meshes.Draws)[id.Index] = new() {
            VertexBuffer = h.Vertices, Count = 3, InstanceCount = 1
        };

        Record(h);

        Assert.Null(h.Materials.EffectOf(h.System, id));
        Assert.Empty(device.Recorder!.OfKind(RecordedCommandKind.Draw));
    }

    /// <summary>A culled object costs no effect resolution and no draw.</summary>
    [Fact]
    public void A_culled_object_is_never_drawn() {
        using var h = Build();
        AddMesh(h, -10f, new Material("Lit"));

        Record(h);

        Assert.Empty(device.Recorder!.OfKind(RecordedCommandKind.DrawIndexed));
    }
}
