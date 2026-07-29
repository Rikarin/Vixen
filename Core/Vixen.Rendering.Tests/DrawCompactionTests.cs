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
///         Step 4 of <c>docs/bindless-materials.md</c>, and the payoff for every step before it. The
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
    ///     ⚠ <strong>This is the reason the shipped forward pass is not merged yet.</strong>
    ///     <c>TransformRenderFeature</c> pushes each object's world matrix as a push constant, and a
    ///     merged command has no place to push the second object's. The fix is the same one
    ///     <c>[MaterialIndex]</c> was: put the transforms in a buffer and carry the index in the
    ///     draw's own <c>firstInstance</c>, which the compaction shader already copies. Until then
    ///     the gate keeps the picture right rather than fast.
    /// </remarks>
    [Fact]
    public void A_per_object_contributor_stops_the_merge() {
        using var harness = Build();
        harness.Meshes.Add(new TransformRenderFeature());

        AddMeshes(harness, 3);
        Frame(harness);

        device.Recorder!.Clear();
        RecordStage(harness);

        Assert.Equal(0, device.Recorder.CountOf(RecordedCommandKind.DrawIndexedIndirectCount));
        Assert.Equal(3, device.Recorder.CountOf(RecordedCommandKind.DrawIndexedIndirect));
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
