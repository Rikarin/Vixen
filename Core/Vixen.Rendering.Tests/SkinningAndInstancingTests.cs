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

    // --- Per-instance parameters — docs/plan/31 § B3 ------------------------

    /// <summary>
    ///     The parallel buffer stays parallel even when only some batches fill it.
    /// </summary>
    /// <remarks>
    ///     The invariant the shader depends on: an instance's parameters are at its transform's index,
    ///     so <c>gl_InstanceIndex</c> addresses both and no second offset travels in the draw. A batch
    ///     that supplies none writes neutral values rather than nothing, and this is what says so —
    ///     without it, one unparameterised batch shifts every later batch's parameters by its count
    ///     and every forest after it wears the wrong tree's wind.
    /// </remarks>
    [Fact]
    public void Parameters_stay_at_their_transforms_index_across_mixed_batches() {
        using var h = Build();
        var shared = new Material("Lit");

        var plain = AddMesh(h, 10f, shared, radius: 50f);
        var detailed = AddMesh(h, 20f, shared, radius: 50f);

        h.Instancing.Begin();
        h.Instancing.SetInstances(h.System, plain, Transforms(4));
        h.Instancing.SetInstances(h.System, detailed, Transforms(3), Parameters(3, 0.5f));

        Assert.Equal(7, h.Instancing.TransformCount);
        Assert.Equal(7, h.Instancing.ParameterCount);

        var batches = h.System.Objects.Data.Data(h.Instancing.Batches);
        Assert.Equal(0, batches[plain.Index].First);
        Assert.Equal(4, batches[detailed.Index].First);

        // The first four are the neutral run standing in for the batch that supplied none.
        var written = h.Instancing.Parameters;

        for (var index = 0; index < 4; index++) {
            Assert.Equal(1f, written[index].Fade);
            Assert.Equal(1f, written[index].Scale);
            Assert.Equal(0f, written[index].WindPhase);
        }

        for (var index = 4; index < 7; index++) {
            Assert.Equal(0.5f, written[index].Tint);
            Assert.Equal(index - 4, written[index].WindPhase);
        }
    }

    /// <summary>A batch whose two spans disagree is refused rather than zipped to the shorter.</summary>
    [Fact]
    public void A_batch_with_mismatched_parameter_counts_is_refused() {
        using var h = Build();
        var id = AddMesh(h, 10f, new Material("Lit"), radius: 50f);

        h.Instancing.Begin();

        Assert.Throws<ArgumentException>(
            () => h.Instancing.SetInstances(h.System, id, Transforms(4), Parameters(3, 0f))
        );
    }

    /// <summary>
    ///     Parameters are their own permutation, so a crate field does not pay for a wind phase.
    /// </summary>
    /// <remarks>
    ///     Both halves, because the interesting one is the negative. A flag only splits variants for a
    ///     shader that <em>declares</em> it — <c>MaterialRenderFeature.PermutationKeys</c> lists what
    ///     goes in the key — so a lit shader with no per-instance parameters in it draws the
    ///     parameterised batch through the ordinary instanced variant and costs nothing. Adding the
    ///     flag to that list is what a foliage shader does, and only then is there a third pipeline.
    /// </remarks>
    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 3)]
    public void Parameters_split_a_variant_only_for_a_shader_that_declares_them(bool declared, int expected) {
        using var h = Build();
        var shared = new Material("Lit");

        if (declared) {
            h.Materials.PermutationKeys["Lit"] = [
                h.Skinning.PermutationKeys[0],
                h.Instancing.PermutationKeys[0],
                h.Instancing.PermutationKeys[1]
            ];
        }

        var single = AddMesh(h, 10f, shared, radius: 50f);
        var instanced = AddMesh(h, 20f, shared, radius: 50f);
        var parameterised = AddMesh(h, 30f, shared, radius: 50f);

        h.Instancing.Begin();
        h.Instancing.SetInstances(h.System, single, Transforms(1));
        h.Instancing.SetInstances(h.System, instanced, Transforms(5));
        h.Instancing.SetInstances(h.System, parameterised, Transforms(5), Parameters(5, 1f));

        Record(h);

        // Plain and instanced, plus instanced-with-parameters when the shader asked for it. The
        // batch of one shares the plain variant either way.
        Assert.Equal(expected, effects.Count);
        Assert.Equal(expected, h.Meshes.Pipelines!.Count);
    }

    /// <summary>
    ///     A batch of one takes neither flag, even when it supplied parameters.
    /// </summary>
    /// <remarks>
    ///     Parameters are addressed by the instance index, so they mean nothing without one. Letting
    ///     the second flag stand alone would compile a variant that binds a buffer to read index zero.
    /// </remarks>
    [Fact]
    public void A_batch_of_one_takes_neither_flag_even_with_parameters() {
        using var h = Build();
        var id = AddMesh(h, 10f, new Material("Lit"));

        h.Instancing.Begin();
        h.Instancing.SetInstances(h.System, id, Transforms(1), Parameters(1, 1f));

        Assert.False(h.Instancing.ValueOf(h.System, id, 0));
        Assert.False(h.Instancing.ValueOf(h.System, id, 1));
    }

    /// <summary>
    ///     Neutral is not <see langword="default" />, and the difference is visible rather than subtle.
    /// </summary>
    [Fact]
    public void Neutral_is_full_size_and_fully_present() {
        var neutral = InstanceParameters.Neutral;

        Assert.Equal(1f, neutral.Scale);
        Assert.Equal(1f, neutral.Fade);
        Assert.Equal(0f, neutral.Tint);
        Assert.Equal(0f, neutral.WindPhase);

        // The trap this exists to avoid: a zeroed record is an invisible instance of no size.
        Assert.NotEqual(default, neutral);
        Assert.Equal(16, InstanceParameters.SizeInBytes);
    }

    static Matrix4x4[] Transforms(int count) =>
        Enumerable.Range(0, count).Select(i => Matrix4x4.FromTranslation(new(0f, i, 0f))).ToArray();

    static InstanceParameters[] Parameters(int count, float tint) =>
        Enumerable.Range(0, count)
            .Select(i => new InstanceParameters { Tint = tint, WindPhase = i, Scale = 1f, Fade = 1f })
            .ToArray();

    // --- The ring under the buffers -----------------------------------------

    /// <summary>
    ///     A frame's records go somewhere the frames still in flight are not reading.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The hazard these buffers had from the day they were written. <c>Write</c> on a
    ///         host-visible buffer is a memcpy into memory the GPU may still be reading for a frame
    ///         that has not finished, and no API reports it — the symptom is a skeleton or an instance
    ///         list that is briefly a blend of two frames, under load, on somebody else's machine.
    ///     </para>
    ///     <para>
    ///         So the buffer holds one region per frame in flight and the offset moves. Offsets rather
    ///         than shifted indices, so that a push-constant base, a <c>firstInstance</c> and a shader
    ///         indexing from zero all keep working without knowing the ring is there.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Consecutive_frames_write_to_different_regions() {
        using var device = new NullDevice(new() { FramesInFlight = 3 });
        using var upload = new UploadBuffer<Matrix4x4>("Test") { Device = device };

        var seen = new List<long>();

        for (var frame = 0; frame < 3; frame++) {
            upload.Begin();
            upload.Add([Matrix4x4.Identity]);
            upload.Upload();
            seen.Add(upload.Offset);
        }

        Assert.Equal(3, seen.Distinct().Count());
        Assert.All(seen, offset => Assert.Equal(0, offset % upload.Alignment));
    }

    /// <summary>Once the ring has come round, the regions repeat rather than growing.</summary>
    [Fact]
    public void The_ring_comes_round_rather_than_growing() {
        using var device = new NullDevice(new() { FramesInFlight = 2 });
        using var upload = new UploadBuffer<Matrix4x4>("Test") { Device = device };

        var seen = new List<long>();

        for (var frame = 0; frame < 8; frame++) {
            upload.Begin();
            upload.Add([Matrix4x4.Identity]);
            upload.Upload();
            seen.Add(upload.Offset);
        }

        Assert.Equal(2, seen.Distinct().Count());
        Assert.Equal(seen[0], seen[2]);
        Assert.Equal(seen[1], seen[3]);
    }

    /// <summary>The indices a feature hands a shader are still relative to the frame's own region.</summary>
    /// <remarks>
    ///     What makes the ring invisible downstream. A skinned object's first bone is the index its
    ///     push constant carries, and it counts from the start of <em>this frame's</em> palettes —
    ///     which is why the buffer is bound at <see cref="UploadBuffer{T}.Offset" /> rather than at
    ///     zero, and why nothing in a shader changed.
    /// </remarks>
    [Fact]
    public void Indices_stay_relative_to_the_frames_own_region() {
        using var device = new NullDevice(new() { FramesInFlight = 3 });
        using var upload = new UploadBuffer<Matrix4x4>("Test") { Device = device };

        for (var frame = 0; frame < 5; frame++) {
            upload.Begin();

            Assert.Equal(0, upload.Add([Matrix4x4.Identity, Matrix4x4.Identity]));
            Assert.Equal(2, upload.Add([Matrix4x4.Identity]));

            upload.Upload();
        }
    }

    /// <summary>A device with one frame in flight has one region, and the offset never moves.</summary>
    [Fact]
    public void One_frame_in_flight_needs_no_ring() {
        using var device = new NullDevice(new() { FramesInFlight = 1 });
        using var upload = new UploadBuffer<Matrix4x4>("Test") { Device = device };

        for (var frame = 0; frame < 4; frame++) {
            upload.Begin();
            upload.Add([Matrix4x4.Identity]);
            upload.Upload();

            Assert.Equal(0, upload.Offset);
        }
    }
}
