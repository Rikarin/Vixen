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
///     Survivors appended to a list, and one command that draws as many of them as there were.
/// </summary>
/// <remarks>
///     <para>
///         Step 4 of <c>docs/plan/23-bindless-materials.md</c>, and the payoff for every step before it. The
///         padded form costs one command per <em>candidate</em> object whatever the culling decided;
///         a compacted one costs one command per batch, because
///         <c>DrawIndexedIndirectCount</c> reads how many survived out of a buffer the host never
///         looks at.
///     </para>
///     <para>
///         ⚠ <strong>A merged draw binds once, so nothing may want to happen in between.</strong>
///         That is the condition, and it is not incidental: a sub-feature that pushes this object's
///         world matrix has to be given the chance to push the next one's, and inside one command
///         there is no next one. The gate is checked rather than assumed, and there is a test for
///         each side of it.
///     </para>
/// </remarks>
public sealed class DrawCompactionTests : IDisposable {
    static readonly PermutationKey<bool> UseTransformRecords =
        ParameterKeys.NewPermutation(false, "ForwardPlus.UseTransformRecords");

    readonly NullDevice device;
    readonly EffectSystem effects = new();
    readonly DescriptorSetLayoutHandle arguments;

    public DrawCompactionTests() : this(counting: true) { }

    DrawCompactionTests(bool counting) {
        device = new(
            new() {
                Record = true,
                Features = NullDevice.Everything with { HasDrawIndirectCount = counting }
            }
        );

        arguments = device.CreateDescriptorSetLayout(
            new(
                DescriptorSetSlot.PerMaterial,
                [
                    new(0, DescriptorKind.StorageBuffer, ShaderStage.Compute),
                    new(1, DescriptorKind.StorageBuffer, ShaderStage.Compute),
                    new(2, DescriptorKind.StorageBuffer, ShaderStage.Compute),
                    new(3, DescriptorKind.StorageBuffer, ShaderStage.Compute),
                    new(4, DescriptorKind.StorageBuffer, ShaderStage.Compute),
                    new(5, DescriptorKind.StorageBuffer, ShaderStage.Compute)
                ],
                GpuCulling.ArgumentsShaderName
            )
        );

        effects.AddProvider(new Compiles(arguments, resolved));
    }

    public void Dispose() => device.Dispose();

    // --- The layout ---------------------------------------------------------

    /// <summary>Batches partition the objects, so the runs partition a view's region.</summary>
    /// <remarks>
    ///     <para>
    ///         The buffer is exactly the size the padded form needed. That is the claim worth
    ///         asserting: compaction costs an atomic and no memory, and a layout that overlapped two
    ///         batches' runs would put one batch's arguments in the other's draw.
    ///     </para>
    ///     <para>
    ///         Ids are the source's and need not be dense. A sparse space costs an empty run and
    ///         nothing else, which is far cheaper than making every source renumber.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_runs_partition_a_views_region() {
        using var draws = Compacted(out var bits, [0, 0, 1, 0, 1]);

        Assert.True(draws.IsCompacted);
        Assert.Equal(2, draws.BatchCount);
        Assert.Equal(3, draws.BatchSizeOf(0));
        Assert.Equal(2, draws.BatchSizeOf(1));

        // Batch 0 starts at record 0 and batch 1 immediately after it, in bytes.
        Assert.Equal(0, draws.BatchOffsetOf(0, 0));
        Assert.Equal(3 * GpuDrawArguments.Stride, draws.BatchOffsetOf(0, 1));

        // And the second view's region starts one object count later, not one batch later.
        Assert.Equal(5 * GpuDrawArguments.Stride, draws.BatchOffsetOf(1, 0));

        // The counts are one uint per batch per view, view-major.
        Assert.Equal(0, draws.CountOffsetOf(0, 0));
        Assert.Equal(sizeof(uint), draws.CountOffsetOf(0, 1));
        Assert.Equal(2 * sizeof(uint), draws.CountOffsetOf(1, 0));

        device.Destroy(bits);
    }

    /// <summary>The counts are zeroed on the device before every dispatch.</summary>
    /// <remarks>
    ///     ⚠ <strong>The failure appears only in the frames where something became invisible.</strong>
    ///     An <c>atomicAdd</c> onto last frame's count appends past the end of a batch's run and into
    ///     the next batch's, which draws one batch's geometry with another's arguments. Copied from a
    ///     buffer of zeros rather than written from the host, because a host write into a buffer an
    ///     unfinished frame may still be reading is exactly the hazard the upload ring exists for —
    ///     and a source that is written once and never again has none of it.
    /// </remarks>
    [Fact]
    public void The_counts_are_cleared_before_each_dispatch() {
        using var draws = Compacted(out var bits, [0, 1]);

        var copies = device.Recorder!.OfKind(RecordedCommandKind.CopyBuffer);
        var copy = Assert.Single(copies);

        Assert.Equal((long)draws.Counts.Value.Packed, copy.C);
        Assert.Equal(0, copy.D);

        // One count per batch per *view*, so two batches across two views is four — not two. A clear
        // sized per batch alone would leave the second view's counts holding last frame's.
        Assert.Equal(2 * draws.BatchCount * sizeof(uint), copy.E);

        device.Destroy(bits);
    }

    /// <summary>Without the device capability, nothing is compacted and nothing pretends to be.</summary>
    /// <remarks>
    ///     <strong>Falling back rather than refusing</strong> — the capability is a machine fact, and
    ///     a host that had to branch on it at every call site would branch on it wrongly at one of
    ///     them. <c>IsCompacted</c> is what a draw loop reads, distinct from <c>Compact</c> which is
    ///     what a host asked for: reading a compacted list as a padded one draws every object with
    ///     another's arguments.
    /// </remarks>
    [Fact]
    public void Without_the_capability_the_buffer_stays_padded() {
        using var plain = new DrawCompactionTests(counting: false);
        using var draws = plain.Compacted(out var bits, [0, 1]);

        Assert.True(draws.Compact);
        Assert.False(draws.IsCompacted);
        Assert.Equal(0, plain.device.Recorder!.CountOf(RecordedCommandKind.CopyBuffer));

        plain.device.Destroy(bits);
    }

    /// <summary>And the variant asked for follows the same decision.</summary>
    /// <remarks>
    ///     The two write different things to the same buffer, so a host that compacted on the CPU
    ///     side and resolved the padded shader would fill a buffer nothing wrote the way it reads.
    /// </remarks>
    [Fact]
    public void The_resolved_variant_follows_the_capability() {
        using var capable = Compacted(out var first, [0]);
        Assert.Contains("Compact=true", resolved[^1], StringComparison.Ordinal);
        device.Destroy(first);

        using var plain = new DrawCompactionTests(counting: false);
        using var padded = plain.Compacted(out var second, [0]);

        Assert.Contains("Compact=false", plain.resolved[^1], StringComparison.Ordinal);
        plain.device.Destroy(second);
    }

    // --- The draw -----------------------------------------------------------

    /// <summary>Three objects of one batch are one command.</summary>
    /// <remarks>
    ///     The whole plan, ending here. Three objects sharing an effect, a geometry buffer and a
    ///     record buffer bind nothing between them and are covered by one
    ///     <c>DrawIndexedIndirectCount</c> whose count the host never learns.
    /// </remarks>
    [Fact]
    public void A_whole_batch_is_one_command() {
        using var harness = Build();

        AddMeshes(harness, 3);
        Frame(harness);

        device.Recorder!.Clear();
        RecordStage(harness);

        Assert.Equal(1, device.Recorder.CountOf(RecordedCommandKind.DrawIndexedIndirectCount));
        Assert.Equal(0, device.Recorder.CountOf(RecordedCommandKind.DrawIndexedIndirect));
    }

    /// <summary>And without the capability the same three are three.</summary>
    /// <remarks>
    ///     The control the count above is a measurement against. This is what GL, WebGL2 and Metal
    ///     run permanently, so it is not a legacy branch — and it draws the same image.
    /// </remarks>
    [Fact]
    public void Without_the_capability_they_are_three_commands() {
        using var plain = new DrawCompactionTests(counting: false);
        using var harness = plain.Build();

        AddMeshes(harness, 3);
        plain.Frame(harness);

        plain.device.Recorder!.Clear();
        plain.RecordStage(harness);

        Assert.Equal(0, plain.device.Recorder.CountOf(RecordedCommandKind.DrawIndexedIndirectCount));
        Assert.Equal(3, plain.device.Recorder.CountOf(RecordedCommandKind.DrawIndexedIndirect));
    }

    /// <summary>
    ///     A sub-feature that records per object stops the merge, because it has to.
    /// </summary>
    /// <remarks>
    ///     A transform feature left to push is the case: it puts each object's world matrix in the
    ///     command buffer, and a merged command has no point inside it at which the second object's
    ///     could go. The gate keeps the picture right rather than fast, and this is the side of it
    ///     that gives up the merge.
    /// </remarks>
    [Fact]
    public void A_per_object_contributor_stops_the_merge() {
        using var harness = Build();
        using var transforms = new TransformRenderFeature { Device = device };

        harness.Meshes.Add(transforms);

        AddMeshes(harness, 3);
        Frame(harness);

        device.Recorder!.Clear();
        RecordStage(harness);

        Assert.False(transforms.UseRecords);
        Assert.Equal(0, device.Recorder.CountOf(RecordedCommandKind.DrawIndexedIndirectCount));
        Assert.Equal(3, device.Recorder.CountOf(RecordedCommandKind.DrawIndexedIndirect));
    }

    /// <summary>
    ///     And with its records on it stops nothing: the same three objects are one command.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <strong>The pair above and below is the whole fix.</strong> Same feature, same three
    ///         objects, same device — the only difference is where the matrix goes. Pushed, it is a
    ///         command per node and there are three draws; recorded, the vertex stage reads it out of
    ///         a buffer at the slot the draw's own <c>firstInstance</c> names, nothing at all happens
    ///         between the nodes, and the run collapses into one.
    ///     </para>
    ///     <para>
    ///         Asserted through the recorded commands rather than through <c>IsRecording</c>, because
    ///         the flag is the mechanism and the command count is the claim.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_transform_in_a_buffer_does_not() {
        using var harness = Build();
        using var transforms = new TransformRenderFeature { Device = device };

        harness.Meshes.Add(transforms);
        Assert.True(transforms.EnableRecords(UseTransformRecords));

        AddMeshes(harness, 3);
        Frame(harness);

        device.Recorder!.Clear();
        RecordStage(harness);

        Assert.Equal(1, device.Recorder.CountOf(RecordedCommandKind.DrawIndexedIndirectCount));
        Assert.Equal(0, device.Recorder.CountOf(RecordedCommandKind.PushConstants));
    }

    /// <summary>
    ///     Each object's record index is in the argument the GPU will draw with.
    /// </summary>
    /// <remarks>
    ///     ⚠ <strong>The host never records these draws, so this is the only place the index can
    ///     be.</strong> A merged command reads its arguments out of the compacted buffer, and the
    ///     compaction shader copies <c>firstInstance</c> from the template unchanged — so a template
    ///     left at zero would draw every object in the batch with object zero's matrix. Which is a
    ///     picture, and a plausible one: everything in the batch sitting on top of the first thing.
    /// </remarks>
    [Fact]
    public void The_record_index_reaches_the_argument_template() {
        using var harness = Build();
        using var transforms = new TransformRenderFeature { Device = device };

        harness.Meshes.Add(transforms);
        transforms.EnableRecords(UseTransformRecords);

        AddMeshes(harness, 3);
        harness.System.Draw();

        var commands = harness.Arguments.Fill(harness.System.Objects.Count);
        harness.Meshes.FillArguments(harness.System, commands);

        Assert.Equal(0u, commands[0].FirstInstance);
        Assert.Equal(1u, commands[1].FirstInstance);
        Assert.Equal(2u, commands[2].FirstInstance);
    }

    /// <summary>
    ///     Without a device-side draw count the records stay off, and the matrix stays pushed.
    /// </summary>
    /// <remarks>
    ///     <strong>The gate is not about what can read a buffer.</strong> Any device can read a matrix
    ///     out of one in the vertex stage. What the capability decides is whether it is worth it:
    ///     with no <c>DrawIndexedIndirectCount</c> there is no compacted list, with no compacted list
    ///     there is no merged command, and a dependent buffer read per vertex against a constant
    ///     already in the command stream is a straight loss. This is what GL, WebGL2 and Metal run.
    /// </remarks>
    [Fact]
    public void Without_a_device_draw_count_the_matrix_stays_pushed() {
        using var plain = new DrawCompactionTests(counting: false);
        using var harness = plain.Build();
        using var transforms = new TransformRenderFeature { Device = plain.device };

        harness.Meshes.Add(transforms);

        Assert.False(transforms.EnableRecords(UseTransformRecords));
        Assert.True(transforms.IsRecording);

        AddMeshes(harness, 3);
        plain.Frame(harness);

        plain.device.Recorder!.Clear();
        plain.RecordStage(harness);

        Assert.Equal(3, plain.device.Recorder.CountOf(RecordedCommandKind.PushConstants));
    }

    /// <summary>
    ///     A light block bound per object stops the merge too, and clustered lighting does not.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <strong>The transform was not the only per-node contributor, and this is the other
    ///         one.</strong> With a uniform light list, each object's block is at its own dynamic
    ///         offset in one buffer — and a dynamic offset travels in the bind, not in the block, so
    ///         there is nowhere inside a merged command to change it. With clustering on the feature
    ///         binds nothing per object at all, because a fragment finds its own lights in the grid.
    ///     </para>
    ///     <para>
    ///         So the gate asks what a sub-feature is <em>doing</em> this frame rather than what type
    ///         it is. Asking the type would have given the same answer to both of these, and the
    ///         answer would have been wrong for one of them.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData(true, 1, 0)]
    [InlineData(false, 0, 3)]
    public void Clustered_lighting_leaves_the_merge_alone(bool clustered, int merged, int separate) {
        using var harness = Build();
        using var transforms = new TransformRenderFeature { Device = device };
        // The shape ForwardPlus declares for set 3. The feature takes its layout rather than inventing
        // one, so a harness with a faked effect has to say what it is — see ForwardLightingRenderFeature.Layout.
        var perDraw = device.CreateDescriptorSetLayout(
            new(
                DescriptorSetSlot.PerDraw,
                [new(0, DescriptorKind.DynamicUniformBuffer, ShaderStage.Vertex | ShaderStage.Fragment)],
                "ForwardPlus.PerDraw"
            )
        );

        using var lights = new ForwardLightingRenderFeature {
            Device = device,
            Clustered = clustered,
            Layout = perDraw
        };

        harness.Meshes.Add(transforms);
        harness.Meshes.Add(lights);
        transforms.EnableRecords(UseTransformRecords);

        AddMeshes(harness, 3);
        Frame(harness);

        device.Recorder!.Clear();
        RecordStage(harness);

        Assert.Equal(merged, device.Recorder.CountOf(RecordedCommandKind.DrawIndexedIndirectCount));
        Assert.Equal(separate, device.Recorder.CountOf(RecordedCommandKind.DrawIndexedIndirect));
    }

    /// <summary>Two batches are two commands, not one.</summary>
    /// <remarks>
    ///     Meshes out of two geometry buffers cannot share a command, because a command binds one
    ///     vertex buffer. The batch key says so and this is what says the key is being read.
    /// </remarks>
    [Fact]
    public void Two_geometry_buffers_are_two_commands() {
        using var harness = Build();

        AddMeshes(harness, 2);
        AddMeshes(harness, 2, second: true);

        Frame(harness);
        device.Recorder!.Clear();
        RecordStage(harness);

        Assert.Equal(2, device.Recorder.CountOf(RecordedCommandKind.DrawIndexedIndirectCount));
    }

    // --- The fixture --------------------------------------------------------

    readonly List<string> resolved = [];

    /// <summary>An update over one batch layout, with the dispatch recorded.</summary>
    GpuDrawArguments Compacted(out BufferHandle bits, ReadOnlySpan<uint> batches, int viewCount = 2) {
        var draws = new GpuDrawArguments(device) { Effects = effects, Pipelines = new(device), Compact = true };
        var objectCount = batches.Length;

        draws.Fill(objectCount);
        batches.CopyTo(draws.Batches(objectCount));

        bits = device.CreateBuffer(new(256, BufferUsage.Storage, MemoryAccess.DeviceLocal, "Bits"));

        using var list = device.BeginCommandList(QueueKind.Compute);
        Assert.True(draws.Update(list, bits, viewCount, objectCount));
        list.Finish();
        device.ComputeQueue.Submit([list]);

        return draws;
    }

    void Frame(Harness h) {
        h.System.Draw();

        h.Meshes.FillArguments(h.System, h.Arguments.Fill(h.System.Objects.Count));
        h.Meshes.FillBatches(h.System, h.Arguments.Batches(h.System.Objects.Count));

        using var list = device.BeginCommandList(QueueKind.Compute);
        h.Arguments.Update(list, h.Bits, h.System.Views.Count, h.System.Objects.Count);
        list.Finish();
        device.ComputeQueue.Submit([list]);
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

    Harness Build() {
        var system = new RenderSystem();
        var opaque = system.AddStage(new("Opaque"));

        var meshes = new MeshRenderFeature { Pipelines = new(device), Describer = new EffectPipelineDescriber(device) };
        var materials = new MaterialRenderFeature { Effects = effects };

        meshes.Add(materials);
        system.AddFeature(meshes);

        var view = Matrix4x4.LookAt(Vector3.Zero, new(0f, 0f, 1f), new(0f, 1f, 0f));
        var projection = Matrix4x4.PerspectiveFieldOfView(MathF.PI / 3f, 1f, 0.1f, 1000f);

        system.SetViews(
            [new("camera") { Stages = opaque.Mask, Position = Vector3.Zero, Frustum = new(view * projection) }]
        );

        var draws = new GpuDrawArguments(device) { Effects = effects, Pipelines = new(device), Compact = true };
        meshes.Arguments = draws;

        return new() {
            System = system,
            Opaque = opaque,
            Meshes = meshes,
            Materials = materials,
            Arguments = draws,
            Bits = device.CreateBuffer(new(4096, BufferUsage.Storage, MemoryAccess.DeviceLocal, "Bits")),
            First = new(device, 32, 1024, 1024),
            Second = new(device, 32, 1024, 1024)
        };
    }

    static void AddMeshes(Harness h, int count, bool second = false) {
        var geometry = second ? h.Second : h.First;
        var material = new Material("Lit");

        for (var index = 0; index < count; index++) {
            Assert.True(geometry.TryAllocate(8, 12, out var slice));

            var draw = new MeshDraw { InstanceCount = 1 };
            geometry.Apply(ref draw, slice, vertexLayout: second ? 1 : 0);

            var id = h.System.Objects.Add(
                new() {
                    Bounds = new(new Vector3(0f, 0f, 10f), 1f),
                    Stages = h.Opaque.Mask,
                    FeatureIndex = h.Meshes.Index
                }
            );

            h.System.Objects.Data.Data(h.Meshes.Draws)[id.Index] = draw;
            h.Materials.Assign(h.System, id, material);
        }
    }

    sealed class Harness : IDisposable {
        public required RenderSystem System { get; init; }
        public required RenderStage Opaque { get; init; }
        public required MeshRenderFeature Meshes { get; init; }
        public required MaterialRenderFeature Materials { get; init; }
        public required GpuDrawArguments Arguments { get; init; }
        public required BufferHandle Bits { get; init; }
        public required GeometryBuffer First { get; init; }
        public required GeometryBuffer Second { get; init; }

        public void Dispose() {
            System.Dispose();
            Arguments.Dispose();
            First.Dispose();
            Second.Dispose();
        }
    }

    /// <summary>The argument shader in both variants, and a drawable one for the meshes.</summary>
    sealed class Compiles(DescriptorSetLayoutHandle arguments, List<string> resolved) : IEffectProvider {
        public Effect? TryGet(EffectKey key) {
            resolved.Add(key.ToString());

            if (key.ShaderName != GpuCulling.ArgumentsShaderName) {
                return new() { Key = key, Stages = Modules(ShaderStage.Vertex, ShaderStage.Fragment) };
            }

            var layouts = new DescriptorSetLayoutHandle[(int)DescriptorSetSlot.PerMaterial + 1];
            layouts[(int)DescriptorSetSlot.PerMaterial] = arguments;

            return new() {
                Key = key,
                SetLayouts = [.. layouts],
                Stages = Modules(ShaderStage.Compute),
                Bindings = [
                    new("templates", DescriptorSetSlot.PerMaterial, 0, DescriptorKind.StorageBuffer),
                    new("visibility", DescriptorSetSlot.PerMaterial, 1, DescriptorKind.StorageBuffer),
                    new("commands", DescriptorSetSlot.PerMaterial, 2, DescriptorKind.StorageBuffer),
                    new("batches", DescriptorSetSlot.PerMaterial, 3, DescriptorKind.StorageBuffer),
                    new("bases", DescriptorSetSlot.PerMaterial, 4, DescriptorKind.StorageBuffer),
                    new("counts", DescriptorSetSlot.PerMaterial, 5, DescriptorKind.StorageBuffer)
                ]
            };
        }

        static ImmutableArray<EffectStage> Modules(params ShaderStage[] stages) =>
            [.. stages.Select(stage => new EffectStage(stage, [1, 2, 3, 4], "main"))];
    }
}
