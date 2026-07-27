// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

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
///     Skinning and instancing — docs/plan/06 § Geometry and materials.
/// </summary>
/// <remarks>
///     <para>
///         Together they are the test of the arrangement's central claim, because both are exactly
///         the case that breaks a renderer built on inheritance: a skinned instanced mesh is two
///         aspects of one object, and neither feature knows the other exists. Both register their own
///         array, both contribute a permutation, and the mesh feature changed by four lines to let
///         one of them supply a draw argument.
///     </para>
///     <para>
///         They also solve the same problem — a variable-length run of matrices per object — in two
///         deliberately different ways, because the right answer differs: skinning pushes a base
///         index as a constant, instancing uses the draw call's own <c>firstInstance</c>, and the
///         lighting feature next door uses a dynamic descriptor offset. Three per-draw data problems,
///         three mechanisms, and each is the cheapest one that fits.
///     </para>
/// </remarks>
public class SkinningAndInstancingTests : IDisposable {
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
        public required SkinningRenderFeature Skinning { get; init; }
        public required InstancingRenderFeature Instancing { get; init; }
        public required BufferHandle Vertices { get; init; }

        public void Dispose() {
            Skinning.Dispose();
            Instancing.Dispose();
            System.Dispose();
        }
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
        var skinning = new SkinningRenderFeature { Device = device };
        var instancing = new InstancingRenderFeature { Device = device };

        meshes.Add(transforms);
        meshes.Add(materials);
        meshes.Add(skinning);
        meshes.Add(instancing);
        system.AddFeature(meshes);

        effects.AddProvider(new AlwaysCompiles());

        // The shader branches on both, so both belong in its key. A shader that did not would leave
        // them out and get one variant — see MaterialRenderFeature.PermutationKeys.
        materials.PermutationKeys["Lit"] = [skinning.PermutationKeys[0], instancing.PermutationKeys[0]];

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
            Skinning = skinning,
            Instancing = instancing,
            Vertices = device.CreateBuffer(new() { Size = 1024, Usage = BufferUsage.Vertex })
        };
    }

    static RenderObjectId AddMesh(Harness h, float z, Material material, float radius = 1f) {
        var id = h.System.Objects.Add(
            new() {
                Bounds = new(new Vector3(0f, 0f, z), radius),
                Stages = h.Opaque.Mask,
                FeatureIndex = h.Meshes.Index
            }
        );

        h.System.Objects.Data.Data(h.Meshes.Draws)[id.Index] = new() {
            VertexBuffer = h.Vertices,
            IndexBuffer = h.Vertices,
            IndexFormat = IndexFormat.UInt16,
            Count = 36,
            InstanceCount = 1
        };

        h.System.Objects.Data.Data(h.Transforms.World)[id.Index] = Matrix4x4.Identity;
        h.Materials.Assign(h.System, id, material);
        return id;
    }

    static Matrix4x4[] Bones(int count) =>
        Enumerable.Range(0, count).Select(i => Matrix4x4.FromTranslation(new(i, 0f, 0f))).ToArray();

    void Record(Harness h) {
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
    }

    /// <inheritdoc />
    public void Dispose() {
        device.Dispose();
        GC.SuppressFinalize(this);
    }

    // --- Skinning -----------------------------------------------------------

    /// <summary>
    ///     A skinned object and an unskinned one sharing a material do not share a pipeline.
    /// </summary>
    /// <remarks>
    ///     What <see cref="IPermutationSubFeature" /> exists for. An object is skinned because it has
    ///     a skeleton, not because an artist ticked a box on a material — so the feature that knows
    ///     contributes the permutation, and the material feature applies it without knowing skinning
    ///     exists.
    /// </remarks>
    [Fact]
    public void A_skeleton_selects_a_different_variant_from_the_same_material() {
        using var h = Build();
        var shared = new Material("Lit");

        var skinned = AddMesh(h, 10f, shared);
        AddMesh(h, 20f, shared);

        h.Skinning.Begin();
        h.Skinning.SetBones(h.System, skinned, Bones(3));

        Record(h);

        Assert.Equal(2, effects.Count);
        Assert.Equal(2, h.Meshes.Pipelines!.Count);
        Assert.Equal(2, device.Recorder!.CountOf(RecordedCommandKind.BindPipeline));
    }

    /// <summary>Palettes are packed back to back, and each object is told where its own starts.</summary>
    /// <remarks>
    ///     Indices rather than byte offsets, so no palette is padded up to a storage-buffer
    ///     alignment and no maximum bone count has to be picked in advance.
    /// </remarks>
    [Fact]
    public void Palettes_are_packed_and_each_object_knows_its_own_first_bone() {
        using var h = Build();
        var shared = new Material("Lit");

        var first = AddMesh(h, 10f, shared);
        var second = AddMesh(h, 20f, shared);

        h.Skinning.Begin();
        h.Skinning.SetBones(h.System, first, Bones(3));
        h.Skinning.SetBones(h.System, second, Bones(5));

        var palettes = h.System.Objects.Data.Data(h.Skinning.Palettes);

        Assert.Equal(new BonePalette(0, 3), palettes[first.Index]);
        Assert.Equal(new BonePalette(3, 5), palettes[second.Index]);
        Assert.Equal(8, h.Skinning.BoneCount);
    }

    /// <summary>
    ///     The base bone index is pushed per draw, after the transform in the same block.
    /// </summary>
    /// <remarks>
    ///     Four bytes at offset 64, immediately after the transform's <c>mat4</c> — 68 of the 128
    ///     bytes Vulkan guarantees, which is why this needs no descriptor and no dynamic offset.
    /// </remarks>
    [Fact]
    public void The_base_bone_index_is_pushed_after_the_transform() {
        using var h = Build();
        var id = AddMesh(h, 10f, new Material("Lit"));

        h.Skinning.Begin();
        h.Skinning.SetBones(h.System, id, Bones(4));

        Record(h);

        var pushes = device.Recorder!.OfKind(RecordedCommandKind.PushConstants).ToArray();

        Assert.Equal(2, pushes.Length);
        Assert.Equal((0, 64), (pushes[0].B, pushes[0].C));
        Assert.Equal((64, 4), (pushes[1].B, pushes[1].C));
    }

    /// <summary>An object with no bones pushes only its transform.</summary>
    /// <remarks>
    ///     Its variant has no skinning code in it, so the constant would be read by nothing — and an
    ///     unskinned draw should not pay for a feature it does not use.
    /// </remarks>
    [Fact]
    public void An_unskinned_object_pushes_only_its_transform() {
        using var h = Build();
        AddMesh(h, 10f, new Material("Lit"));

        h.Skinning.Begin();

        Record(h);

        Assert.Single(device.Recorder!.OfKind(RecordedCommandKind.PushConstants));
    }

    /// <summary>A new frame's palettes overwrite the last one's rather than accumulating.</summary>
    [Fact]
    public void A_frame_starts_its_palettes_from_scratch() {
        using var h = Build();
        var id = AddMesh(h, 10f, new Material("Lit"));

        h.Skinning.Begin();
        h.Skinning.SetBones(h.System, id, Bones(6));
        Record(h);

        h.Skinning.Begin();
        h.Skinning.SetBones(h.System, id, Bones(6));
        Record(h);

        Assert.Equal(6, h.Skinning.BoneCount);
        Assert.Equal(0, h.System.Objects.Data.Data(h.Skinning.Palettes)[id.Index].FirstBone);
    }

    // --- Instancing ---------------------------------------------------------

    /// <summary>
    ///     A batch is one draw call, with the instance count and the offset its transforms start at.
    /// </summary>
    /// <remarks>
    ///     <c>firstInstance</c> rather than a binding: Vulkan adds it into <c>gl_InstanceIndex</c>
    ///     before the shader runs, so a batch reaches its own run of one shared buffer with no
    ///     descriptor, no dynamic offset and no alignment of its own.
    /// </remarks>
    [Fact]
    public void A_batch_is_one_draw_call_with_a_first_instance() {
        using var h = Build();
        var id = AddMesh(h, 10f, new Material("Lit"), radius: 50f);

        h.Instancing.Begin();
        h.Instancing.SetInstances(h.System, id, Transforms(12));

        Record(h);

        var draw = Assert.Single(device.Recorder!.OfKind(RecordedCommandKind.DrawIndexed));

        Assert.Equal(36, draw.A);
        Assert.Equal(12, draw.B);
        Assert.Equal(0, draw.E);
    }

    /// <summary>Two batches share one buffer and get distinct offsets into it.</summary>
    [Fact]
    public void Two_batches_get_distinct_first_instances() {
        using var h = Build();
        var shared = new Material("Lit");

        var first = AddMesh(h, 10f, shared, radius: 50f);
        var second = AddMesh(h, 20f, shared, radius: 50f);

        h.Instancing.Begin();
        h.Instancing.SetInstances(h.System, first, Transforms(4));
        h.Instancing.SetInstances(h.System, second, Transforms(7));

        Record(h);

        var draws = device.Recorder!.OfKind(RecordedCommandKind.DrawIndexed).ToArray();

        Assert.Equal(2, draws.Length);
        Assert.Equal([(4L, 0L), (7L, 4L)], draws.Select(draw => (draw.B, draw.E)).OrderBy(pair => pair.E));
        Assert.Equal(11, h.Instancing.TransformCount);
    }

    /// <summary>
    ///     A batch of one is not instanced, and does not compile a second pipeline.
    /// </summary>
    /// <remarks>
    ///     It would draw identically either way, so giving it the instanced variant would compile a
    ///     whole extra pipeline to draw one mesh — the cache split that checking the count avoids.
    /// </remarks>
    [Fact]
    public void A_batch_of_one_is_not_instanced() {
        using var h = Build();
        var shared = new Material("Lit");

        var single = AddMesh(h, 10f, shared);
        AddMesh(h, 20f, shared);

        h.Instancing.Begin();
        h.Instancing.SetInstances(h.System, single, Transforms(1));

        Record(h);

        Assert.Equal(1, effects.Count);
        Assert.Equal(1, h.Meshes.Pipelines!.Count);
    }

    /// <summary>An object with no batch draws once, as it did before instancing existed.</summary>
    [Fact]
    public void An_object_with_no_batch_draws_once() {
        using var h = Build();
        AddMesh(h, 10f, new Material("Lit"));

        h.Instancing.Begin();

        Record(h);

        var draw = Assert.Single(device.Recorder!.OfKind(RecordedCommandKind.DrawIndexed));

        Assert.Equal(1, draw.B);
        Assert.Equal(0, draw.E);
    }

    // --- Both at once -------------------------------------------------------

    /// <summary>
    ///     Skinned, instanced, both and neither are four variants of one material.
    /// </summary>
    /// <remarks>
    ///     The combination an inheritance hierarchy would need a class for. Here it is two independent
    ///     flags over one object, and neither feature knows the other exists — which is the whole
    ///     argument for composition over inheritance in this layer, asserted rather than claimed.
    /// </remarks>
    [Fact]
    public void Skinned_and_instanced_combine_into_four_variants() {
        using var h = Build();
        var shared = new Material("Lit");

        var plain = AddMesh(h, 10f, shared, radius: 50f);
        var skinned = AddMesh(h, 20f, shared, radius: 50f);
        var instanced = AddMesh(h, 30f, shared, radius: 50f);
        var both = AddMesh(h, 40f, shared, radius: 50f);

        h.Skinning.Begin();
        h.Instancing.Begin();

        h.Skinning.SetBones(h.System, skinned, Bones(3));
        h.Skinning.SetBones(h.System, both, Bones(3));
        h.Instancing.SetInstances(h.System, instanced, Transforms(5));
        h.Instancing.SetInstances(h.System, both, Transforms(5));

        Record(h);

        Assert.Equal(4, effects.Count);
        Assert.Equal(4, h.Meshes.Pipelines!.Count);
        Assert.Equal(5, h.Materials.VariantCount);

        // One material, four variants, and the plain object still resolves to the default one.
        Assert.Equal(2, h.Materials.Materials.Count);
        Assert.NotNull(h.Materials.EffectOf(h.System, plain));
    }

    static Matrix4x4[] Transforms(int count) =>
        Enumerable.Range(0, count).Select(i => Matrix4x4.FromTranslation(new(0f, i, 0f))).ToArray();
}
