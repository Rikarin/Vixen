// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Foliage;
using Vixen.Graphics;
using Vixen.Rendering.Features;
using Vixen.Shaders.Generated;

namespace Vixen.Rendering.Terrain;

/// <summary>One instance as the cull reads it, matching <c>FoliageCullInstance</c> in the shader.</summary>
/// <remarks>
///     ⚠ <b>Thirty-two bytes, which is <see cref="FoliageStore.InstanceBytes" />.</b> The stored form
///     and the device form are the same layout on purpose: a volume's instances can be uploaded as
///     they are, and a stride that disagreed would not fail — it would read one tree's rotation out of
///     the next tree's position.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct FoliageCullInstanceRecord {
    /// <summary>Where the instance stands, in world space.</summary>
    public Vector3 Position;

    /// <summary>How big it is, uniformly.</summary>
    public float Scale;

    /// <summary>Which way it faces, as <c>(x, y, z, w)</c>.</summary>
    public Quaternion Rotation;

    /// <summary>How many bytes one of these is.</summary>
    public static int SizeInBytes => Marshal.SizeOf<FoliageCullInstanceRecord>();

    /// <summary>The record a stored instance is.</summary>
    /// <param name="instance">The instance.</param>
    /// <returns>The record.</returns>
    public static FoliageCullInstanceRecord Of(in FoliageInstance instance) =>
        new() { Position = instance.Position, Scale = instance.Scale, Rotation = instance.Rotation };
}

/// <summary>One cell of one type, as <c>FoliageCullBatch</c> packs it.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct FoliageCullBatchRecord {
    /// <summary>Where this batch's instances begin in the instance buffer.</summary>
    public uint FirstInstance;

    /// <summary>How many it has.</summary>
    public uint InstanceCount;

    /// <summary>How many LOD levels it draws, 1…<see cref="FoliageCullPass.MaxLevels" />.</summary>
    public uint LevelCount;

    /// <summary>Whether the cell survived the host's first stage. Zero culls the whole batch.</summary>
    public uint Visible;

    /// <summary>The radius of an instance at scale one.</summary>
    public float Radius;

    /// <summary>How far out instances begin to fade.</summary>
    public float StartCullDistance;

    /// <summary>How far out they are gone entirely.</summary>
    public float EndCullDistance;

    /// <summary>What fraction of them to keep, 0…1.</summary>
    public float DensityScale;

    /// <summary>Where level 1 takes over, in metres.</summary>
    public float Lod0;

    /// <summary>And level 2.</summary>
    public float Lod1;

    /// <summary>And level 3.</summary>
    public float Lod2;

    /// <summary>Declared so the record is forty-eight bytes on both sides.</summary>
    public float Padding0;

    /// <summary>How many bytes one of these is.</summary>
    public static int SizeInBytes => Marshal.SizeOf<FoliageCullBatchRecord>();
}

/// <summary>The view the cull answers, as <c>FoliageCullView</c> packs it.</summary>
/// <remarks>
///     Six named planes rather than an array, because a fixed-size buffer would make this an unsafe
///     struct for no gain — the shader's <c>float4[6]</c> is six sixteen-byte slots either way.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct FoliageCullViewRecord {
    /// <summary>The near plane, as <c>(normal, d)</c>.</summary>
    public Vector4 Plane0;

    /// <summary>The far plane.</summary>
    public Vector4 Plane1;

    /// <summary>The left plane.</summary>
    public Vector4 Plane2;

    /// <summary>The right plane.</summary>
    public Vector4 Plane3;

    /// <summary>The top plane.</summary>
    public Vector4 Plane4;

    /// <summary>The bottom plane.</summary>
    public Vector4 Plane5;

    /// <summary>Where the view is.</summary>
    public Vector3 Position;

    /// <summary>How many levels the depth pyramid has.</summary>
    public float OccluderLevels;

    /// <summary>The matrix the pyramid was rendered with — <em>last frame's</em>, not this one's.</summary>
    /// <remarks>
    ///     ⚠ <b>A pyramid is a picture of a particular view.</b> Testing against another view's matrix
    ///     occludes by arithmetic that was never about it, which is the mistake that produces trees
    ///     vanishing when the camera turns.
    /// </remarks>
    public Matrix4x4 OccluderProjection;

    /// <summary>How many bytes one of these is.</summary>
    public static int SizeInBytes => Marshal.SizeOf<FoliageCullViewRecord>();

    /// <summary>The record a frustum and a viewpoint are.</summary>
    /// <param name="frustum">The view's frustum.</param>
    /// <param name="position">Where the view is.</param>
    /// <param name="occluders">The depth pyramid, or <see langword="default" /> for none.</param>
    /// <returns>The record.</returns>
    /// <remarks>
    ///     ⚠ <b><see cref="BoundingFrustum.AsSpan" />'s order, and the shader's loop does not care what
    ///     it is</b> — it tests all six. What would matter is the sign convention, which is that a
    ///     plane's positive side is the inside; <c>Plane.D</c> is carried through unchanged for that
    ///     reason.
    /// </remarks>
    public static FoliageCullViewRecord Of(
        in BoundingFrustum frustum,
        Vector3 position,
        FoliageOccluders occluders = default
    ) {
        var planes = frustum.AsSpan();

        return new() {
            OccluderLevels = occluders.Levels,
            OccluderProjection = occluders.Projection,
            Plane0 = new(planes[0].Normal, planes[0].D),
            Plane1 = new(planes[1].Normal, planes[1].D),
            Plane2 = new(planes[2].Normal, planes[2].D),
            Plane3 = new(planes[3].Normal, planes[3].D),
            Plane4 = new(planes[4].Normal, planes[4].D),
            Plane5 = new(planes[5].Normal, planes[5].D),
            Position = position
        };
    }
}

/// <summary>Last frame's depth pyramid, and what it was rendered with.</summary>
/// <param name="View">The min-reduced depth chain. <see cref="HiZPyramid" /> owns it.</param>
/// <param name="Projection">The view-projection the pyramid was rendered with.</param>
/// <param name="Levels">How many levels it has.</param>
/// <remarks>
///     ⚠ <b>The default is "no pyramid", and it is not the same as an empty one.</b> A pass with no
///     occluders runs the <c>Occlusion = false</c> variant, which carries none of the arithmetic; a
///     pass handed a one-texel pyramid would run the test and reject nothing, at the cost of eight
///     matrix multiplies an instance.
/// </remarks>
public readonly record struct FoliageOccluders(TextureViewHandle View, Matrix4x4 Projection, int Levels) {
    /// <summary>Whether there is a pyramid to test against.</summary>
    public bool IsValid => View.IsValid && Levels > 0;
}

/// <summary>
///     The host for <c>FoliageCull.rvn</c>: the instance table, the per-frame batch table, and the two
///     dispatches that turn them into indirect draws.
/// </summary>
/// <remarks>
///     <para>
///         <b>[docs/plan/31 § T5]'s owed device half.</b> <see cref="FoliageRenderer" /> is the CPU
///         reference — what a headless test and a machine with no compute queue use — and this is the
///         same arithmetic where fifty thousand trees can afford it. Neither is the definition:
///         <see cref="InstanceCuller" /> is, and both transliterate it.
///     </para>
///     <para>
///         ⚠ <b>Two dispatches of one shader, because compaction needs a count before it needs a
///         slot.</b> The first counts each level's survivors; the second recomputes every verdict and
///         claims a slot within its level's run. That is <see cref="InstanceCuller" />'s own two-pass
///         shape, and it is there for the same reason: one pass cannot make each level's survivors
///         contiguous without knowing the earlier levels' sizes.
///     </para>
///     <para>
///         ⚠ <b>The instances are uploaded when the volume changes and the batches every frame.</b> A
///         forest's instances are megabytes and they do not move; its batch table is a hundred
///         kilobytes and every field in it is the view's. Re-uploading the instances per frame would
///         be the bandwidth this pass exists to save.
///     </para>
///     <para>
///         ⚠ <b>A batch writes inside its own run.</b> <see cref="FoliageCullBatchRecord.FirstInstance" />
///         is where its survivors go as well as where its instances are, so the output buffer is
///         exactly as long as the input and nothing has to allocate per frame. The survivors of two
///         batches are therefore not adjacent, which costs nothing — they are different meshes and
///         were never one draw.
///     </para>
///     <para>
///         ⚠ <b>The counters are zeroed before the first dispatch, not after the second.</b> A pass
///         that cleared afterwards leaves the buffer holding last frame's numbers for anything that
///         reads it in between, and what reads it is the indirect draw.
///     </para>
/// </remarks>
public sealed class FoliageCullPass : IDisposable {
    /// <summary>How many instances a workgroup covers. <c>[ComputeShader(64)]</c>.</summary>
    public const int GroupSize = 64;

    /// <summary>The most levels a batch may bin into. <c>FoliageCullMath.MaxLevels</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>The stride of the count, head and command buffers, on both sides.</b> A stride that
    ///     disagreed would not fail — it would read level 2 of one cell out of level 0 of the next,
    ///     which draws as the wrong mesh in the right place.
    /// </remarks>
    public const int MaxLevels = 4;

    readonly IGraphicsDevice device;
    readonly DescriptorSetLayoutHandle setLayout;
    readonly PipelineLayoutHandle layout;
    readonly PipelineHandle counting;
    readonly PipelineHandle placing;
    readonly PipelineHandle occludingCounting;
    readonly PipelineHandle occludingPlacing;
    readonly DescriptorSetHandle descriptor;

    readonly BufferHandle instances;
    readonly BufferHandle owners;
    readonly BufferHandle batches;
    readonly BufferHandle views;
    readonly BufferHandle counts;
    readonly BufferHandle heads;
    readonly BufferHandle survivors;
    readonly BufferHandle parameters;
    readonly BufferHandle commands;
    readonly BufferHandle zeroes;

    readonly List<FoliageChunk> chunks = [];
    readonly List<FoliageDraw> templates = [];
    readonly List<FoliageType> settings = [];

    readonly FoliageCullBatchRecord[] records;
    readonly DrawCommand[] arguments;
    readonly FoliageCullInstanceRecord[] scratch;
    readonly uint[] ownerScratch;
    readonly BufferBarrier[] barriers = new BufferBarrier[3];

    bool disposed;

    /// <summary>Creates the pass and its buffers.</summary>
    /// <param name="device">The device.</param>
    /// <param name="counting">The <c>Place = false</c> variant's compute stage.</param>
    /// <param name="placing">And the <c>Place = true</c> one's.</param>
    /// <param name="instanceCapacity">How many instances may be resident.</param>
    /// <param name="batchCapacity">How many batches may be culled in one frame.</param>
    /// <param name="occludingCount">The <c>Occlusion = true, Place = false</c> variant, or none.</param>
    /// <param name="occludingPlace">And the <c>Occlusion = true, Place = true</c> one.</param>
    /// <exception cref="ArgumentNullException">There is no device.</exception>
    /// <exception cref="ArgumentException">A shader is not valid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A capacity is not positive.</exception>
    public FoliageCullPass(
        IGraphicsDevice device,
        ShaderHandle counting,
        ShaderHandle placing,
        int instanceCapacity = 1 << 16,
        int batchCapacity = 1024,
        ShaderHandle occludingCount = default,
        ShaderHandle occludingPlace = default
    ) {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(instanceCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchCapacity);

        if (!counting.IsValid || !placing.IsValid) {
            throw new ArgumentException(
                "A foliage cull needs both of its compute stages — the counting variant and the "
                + "placing one.",
                counting.IsValid ? nameof(placing) : nameof(counting)
            );
        }

        this.device = device;

        InstanceCapacity = instanceCapacity;
        BatchCapacity = batchCapacity;

        records = new FoliageCullBatchRecord[batchCapacity];
        arguments = new DrawCommand[batchCapacity * MaxLevels];
        scratch = new FoliageCullInstanceRecord[instanceCapacity];
        ownerScratch = new uint[instanceCapacity];

        setLayout = device.CreateDescriptorSetLayout(
            new(
                DescriptorSetSlot.PerMaterial,
                [
                    new(FoliageCullKeys.InstancesBinding, DescriptorKind.StorageBuffer, ShaderStage.Compute),
                    new(FoliageCullKeys.OwnersBinding, DescriptorKind.StorageBuffer, ShaderStage.Compute),
                    new(FoliageCullKeys.BatchesBinding, DescriptorKind.StorageBuffer, ShaderStage.Compute),
                    new(FoliageCullKeys.ViewsBinding, DescriptorKind.StorageBuffer, ShaderStage.Compute),
                    new(FoliageCullKeys.CountsBinding, DescriptorKind.StorageBuffer, ShaderStage.Compute),
                    new(FoliageCullKeys.HeadsBinding, DescriptorKind.StorageBuffer, ShaderStage.Compute),
                    new(FoliageCullKeys.SurvivorsBinding, DescriptorKind.StorageBuffer, ShaderStage.Compute),
                    new(FoliageCullKeys.ParametersBinding, DescriptorKind.StorageBuffer, ShaderStage.Compute),
                    new(FoliageCullKeys.CommandsBinding, DescriptorKind.StorageBuffer, ShaderStage.Compute),
                    new(FoliageCullKeys.OccludersBinding, DescriptorKind.SampledTexture, ShaderStage.Compute)
                ],
                "foliage cull"
            )
        );

        layout = device.CreatePipelineLayout(new([setLayout], [], "foliage cull"));

        // One layout and two pipelines, because the two phases are one shader and a permutation. A
        // pipeline per phase is what a permutation *is* on a device.
        this.counting = device.CreateComputePipeline(new(counting, layout, "foliage cull count"));
        this.placing = device.CreateComputePipeline(new(placing, layout, "foliage cull place"));

        // ⚠ The occluding variants are optional, and a host that supplies neither simply never
        // occludes. A permutation that removes eight matrix multiplies from every instance of every
        // frame is worth two more pipelines; making them mandatory would mean a target with no depth
        // pass had to compile a variant it can never dispatch.
        if (occludingCount.IsValid && occludingPlace.IsValid) {
            occludingCounting = device.CreateComputePipeline(new(occludingCount, layout, "foliage cull count hi-z"));
            occludingPlacing = device.CreateComputePipeline(new(occludingPlace, layout, "foliage cull place hi-z"));
        }

        instances = device.CreateBuffer(
            new(
                (long)instanceCapacity * FoliageCullInstanceRecord.SizeInBytes,
                BufferUsage.Storage,
                MemoryAccess.HostUpload,
                "foliage instances"
            )
        );

        owners = device.CreateBuffer(
            new((long)instanceCapacity * sizeof(uint), BufferUsage.Storage, MemoryAccess.HostUpload, "foliage owners")
        );

        batches = device.CreateBuffer(
            new(
                (long)batchCapacity * FoliageCullBatchRecord.SizeInBytes,
                BufferUsage.Storage,
                MemoryAccess.HostUpload,
                "foliage batches"
            )
        );

        views = device.CreateBuffer(
            new(FoliageCullViewRecord.SizeInBytes, BufferUsage.Storage, MemoryAccess.HostUpload, "foliage cull view")
        );

        counts = device.CreateBuffer(
            new(
                (long)batchCapacity * MaxLevels * sizeof(uint),
                BufferUsage.Storage | BufferUsage.CopyDestination,
                MemoryAccess.DeviceLocal,
                "foliage level counts"
            )
        );

        heads = device.CreateBuffer(
            new(
                (long)batchCapacity * MaxLevels * sizeof(uint),
                BufferUsage.Storage | BufferUsage.CopyDestination,
                MemoryAccess.DeviceLocal,
                "foliage level heads"
            )
        );

        // ⚠ Device-local, and they are the two buffers here that are. The host never reads a
        // survivor — the draw does — so staging them back would be the whole point of culling on the
        // device thrown away.
        survivors = device.CreateBuffer(
            new(
                (long)instanceCapacity * sizeof(uint),
                BufferUsage.Storage,
                MemoryAccess.DeviceLocal,
                "foliage survivors"
            )
        );

        parameters = device.CreateBuffer(
            new(
                (long)instanceCapacity * InstanceParameters.SizeInBytes,
                BufferUsage.Storage,
                MemoryAccess.DeviceLocal,
                "foliage instance parameters"
            )
        );

        // Written by the host with the templates and patched by the device with two fields, then read
        // by the draw as arguments — which is three usages of one buffer and why all three are named.
        commands = device.CreateBuffer(
            new(
                (long)batchCapacity * MaxLevels * DrawCommandBytes,
                BufferUsage.Storage | BufferUsage.Indirect,
                MemoryAccess.HostUpload,
                "foliage draw arguments"
            )
        );

        // A buffer of zeros to copy from. A command list can copy and cannot fill, so this is what
        // "clear the counters" is spelled as.
        zeroes = device.CreateBuffer(
            new(
                (long)batchCapacity * MaxLevels * sizeof(uint),
                BufferUsage.CopySource,
                MemoryAccess.HostUpload,
                "foliage counter zeroes"
            )
        );

        device.Write(zeroes, 0, new byte[batchCapacity * MaxLevels * sizeof(uint)]);

        descriptor = device.CreateDescriptorSet(setLayout, "foliage cull");

        device.UpdateDescriptorSet(
            descriptor,
            [
                DescriptorWrite.Storage(FoliageCullKeys.InstancesBinding, instances),
                DescriptorWrite.Storage(FoliageCullKeys.OwnersBinding, owners),
                DescriptorWrite.Storage(FoliageCullKeys.BatchesBinding, batches),
                DescriptorWrite.Storage(FoliageCullKeys.ViewsBinding, views),
                DescriptorWrite.Storage(FoliageCullKeys.CountsBinding, counts),
                DescriptorWrite.Storage(FoliageCullKeys.HeadsBinding, heads),
                DescriptorWrite.Storage(FoliageCullKeys.SurvivorsBinding, survivors),
                DescriptorWrite.Storage(FoliageCullKeys.ParametersBinding, parameters),
                DescriptorWrite.Storage(FoliageCullKeys.CommandsBinding, commands)
            ]
        );
    }

    /// <summary>How many bytes one indirect command is — <c>DrawIndexedIndirect</c>'s stride.</summary>
    public static int DrawCommandBytes => Marshal.SizeOf<DrawCommand>();

    /// <summary>How many instances may be resident.</summary>
    public int InstanceCapacity { get; }

    /// <summary>How many batches may be culled in one frame.</summary>
    public int BatchCapacity { get; }

    /// <summary>How many instances the last <see cref="Upload" /> wrote.</summary>
    public int InstanceCount { get; private set; }

    /// <summary>How many batches it filed them under.</summary>
    public int BatchCount { get; private set; }

    /// <summary>How many chunks were refused because a capacity was full.</summary>
    /// <remarks>
    ///     ⚠ <b>Announced rather than silent</b>, for <see cref="GrassDispatch.Refused" />'s reason: a
    ///     pass that quietly covered the first sixty-five thousand trees of eighty thousand is a
    ///     forest that stops at a distance nobody chose.
    /// </remarks>
    public int Refused { get; private set; }

    /// <summary>How many batches the last <see cref="Prepare" /> found visible.</summary>
    public int VisibleBatches { get; private set; }

    /// <summary>Whether the last <see cref="Prepare" /> will also test against last frame's depth.</summary>
    public bool Occluding { get; private set; }

    /// <summary>How many workgroups each phase dispatches.</summary>
    public int Groups { get; private set; }

    /// <summary>How many indirect draws a frame issues — <see cref="BatchCount" /> levels' worth.</summary>
    /// <remarks>
    ///     Counting the empty ones, because they are issued. A level with no survivors keeps the
    ///     host's zero instance count, which is what lets a caller bind level N's mesh at slot N
    ///     instead of reading back which levels survived.
    /// </remarks>
    public int Draws { get; private set; }

    /// <summary>Which cells are uploaded, or null while every cell always is.</summary>
    /// <remarks>
    ///     ⚠ <b>Null is every cell, and for a volume somebody is painting that is right.</b> The cost
    ///     a streamer removes is the rewrite of every instance in the world whenever anything changes,
    ///     and a forest small enough to hold in one buffer does not have that cost.
    /// </remarks>
    public FoliageStreamer? Streaming { get; set; }

    /// <summary>How many cells the last <see cref="Upload" /> left out because they are not resident.</summary>
    /// <remarks>
    ///     ⚠ <b>Distinct from <see cref="Refused" />, which is the buffer being full.</b> One is a
    ///     decision and the other is a limit, and a person looking at a forest with holes in it needs
    ///     to know which — raising the capacity fixes the second and does nothing for the first.
    /// </remarks>
    public int Skipped { get; private set; }

    /// <summary>The buffer the draw reads its survivors' indices from.</summary>
    public BufferHandle Survivors => survivors;

    /// <summary>And their per-instance parameters.</summary>
    public BufferHandle Parameters => parameters;

    /// <summary>And the indirect arguments.</summary>
    public BufferHandle Commands => commands;

    /// <summary>And how many survived at each level.</summary>
    public BufferHandle Counts => counts;

    /// <summary>Uploads a volume's instances. Call when the volume changes, not every frame.</summary>
    /// <param name="volume">The instances.</param>
    /// <param name="draws">One entry per type that is drawn. Types not listed are skipped.</param>
    /// <returns>How many instances were uploaded.</returns>
    /// <exception cref="ArgumentNullException">There is no volume or no draw list.</exception>
    /// <exception cref="ObjectDisposedException">The pass is gone.</exception>
    public int Upload(FoliageVolume volume, IReadOnlyList<FoliageDraw> draws) {
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentNullException.ThrowIfNull(draws);
        ObjectDisposedException.ThrowIf(disposed, this);

        chunks.Clear();
        templates.Clear();
        settings.Clear();

        InstanceCount = 0;
        BatchCount = 0;
        Refused = 0;
        Skipped = 0;

        var byType = new Dictionary<int, FoliageDraw>(draws.Count);

        foreach (var draw in draws) {
            byType[draw.Type] = draw;
        }

        foreach (var chunk in volume.Chunks) {
            if (chunk.IsEmpty || !byType.TryGetValue(chunk.Type, out var draw)) {
                continue;
            }

            // ⚠ Skipped rather than uploaded-and-culled, and the difference is where the cost falls.
            // A cell the streamer has not made resident is one whose instances do not go into the
            // buffer at all; culling it on the device would still have written its fifty records and
            // read them once per frame.
            if (Streaming?.IsResident(chunk.Cell) == false) {
                Skipped++;
                continue;
            }

            if (BatchCount >= BatchCapacity || InstanceCount + chunk.Count > InstanceCapacity) {
                Refused++;
                continue;
            }

            var first = InstanceCount;

            for (var index = 0; index < chunk.Count; index++) {
                scratch[first + index] = FoliageCullInstanceRecord.Of(chunk.Instances[index]);
                ownerScratch[first + index] = (uint)BatchCount;
            }

            chunks.Add(chunk);
            templates.Add(draw);
            settings.Add(volume.Palette[chunk.Type]);

            records[BatchCount] = new() {
                FirstInstance = (uint)first,
                InstanceCount = (uint)chunk.Count,
                LevelCount = (uint)Math.Clamp(draw.Levels.Length, 1, MaxLevels)
            };

            BatchCount++;
            InstanceCount += chunk.Count;
        }

        if (InstanceCount > 0) {
            device.Write(instances, 0, MemoryMarshal.AsBytes(scratch.AsSpan(0, InstanceCount)));
            device.Write(owners, 0, MemoryMarshal.AsBytes(ownerScratch.AsSpan(0, InstanceCount)));
        }

        Groups = (InstanceCount + GroupSize - 1) / GroupSize;
        Draws = BatchCount * MaxLevels;

        return InstanceCount;
    }

    /// <summary>Writes the frame's batch table, its view and its draw templates.</summary>
    /// <param name="frustum">What the camera can see.</param>
    /// <param name="viewPosition">Where it is.</param>
    /// <param name="densityScale">How much of the foliage to draw, 0…1.</param>
    /// <param name="occluders">Last frame's depth pyramid, or <see langword="default" /> for none.</param>
    /// <returns>How many batches survived the first stage.</returns>
    /// <exception cref="ObjectDisposedException">The pass is gone.</exception>
    /// <remarks>
    ///     ⚠ <b>The first stage — the cell as one object — stays here.</b> A forest is a few thousand
    ///     cells, and testing them beside the code that already walks the chunks is cheaper than
    ///     uploading their bounds so a dispatch can walk them again. What the device is for is the
    ///     fifty thousand instances inside them.
    /// </remarks>
    public int Prepare(
        in BoundingFrustum frustum,
        Vector3 viewPosition,
        float densityScale = 1f,
        FoliageOccluders occluders = default
    ) {
        ObjectDisposedException.ThrowIf(disposed, this);

        VisibleBatches = 0;

        // ⚠ Occlusion is only on when there is a pyramid *and* a variant that can read one. A host
        // that supplied a pyramid but not the shaders would otherwise get a silent non-answer, which
        // is the worst of the three outcomes: it costs nothing, rejects nothing, and looks enabled.
        Occluding = occluders.IsValid && occludingCounting.IsValid;

        device.UpdateDescriptorSet(
            descriptor,
            [DescriptorWrite.Texture(FoliageCullKeys.OccludersBinding, occluders.View)]
        );

        var density = Math.Clamp(densityScale, 0f, 1f);
        var view = frustum;

        for (var index = 0; index < BatchCount; index++) {
            var type = settings[index];
            var draw = templates[index];
            var visible = view.Intersects(chunks[index].Bounds);

            if (visible) {
                VisibleBatches++;
            }

            records[index].Visible = visible ? 1u : 0u;

            // The type's spacing, erring upwards, for FoliageRenderer.Fill's reason: there is no mesh
            // here to ask how big it is, and a radius that is too small culls an instance while part
            // of it is still on screen.
            records[index].Radius = MathF.Max(type.Radius, 0.5f);
            records[index].StartCullDistance = type.StartCullDistance;
            records[index].EndCullDistance = type.EndCullDistance;
            records[index].DensityScale = density;

            records[index].Lod0 = LevelDistance(draw, 0);
            records[index].Lod1 = LevelDistance(draw, 1);
            records[index].Lod2 = LevelDistance(draw, 2);
            records[index].Padding0 = 0f;

            for (var level = 0; level < MaxLevels; level++) {
                // A level the batch does not have still gets a record, so the stride is constant and
                // the slot arithmetic is the same on both sides. Its instance count stays zero.
                arguments[(index * MaxLevels) + level] = level < draw.Levels.Length
                    ? draw.Levels[level] with { InstanceCount = 0u, FirstInstance = 0u }
                    : default;
            }
        }

        if (BatchCount > 0) {
            device.Write(batches, 0, MemoryMarshal.AsBytes(records.AsSpan(0, BatchCount)));
            device.Write(commands, 0, MemoryMarshal.AsBytes(arguments.AsSpan(0, BatchCount * MaxLevels)));
        }

        var record = FoliageCullViewRecord.Of(in frustum, viewPosition, occluders);

        device.Write(views, 0, MemoryMarshal.AsBytes(new ReadOnlySpan<FoliageCullViewRecord>(in record)));

        return VisibleBatches;
    }

    /// <summary>Records both dispatches.</summary>
    /// <param name="commandList">Where to record.</param>
    /// <returns>How many workgroups were dispatched in total, or zero if there was nothing to cull.</returns>
    /// <exception cref="ArgumentNullException">There is no command list.</exception>
    /// <exception cref="ObjectDisposedException">The pass is gone.</exception>
    public int Record(ICommandList commandList) {
        ArgumentNullException.ThrowIfNull(commandList);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (InstanceCount == 0 || BatchCount == 0) {
            return 0;
        }

        var counterBytes = (long)BatchCount * MaxLevels * sizeof(uint);

        // ⚠ Zeroed before the counting phase, not after the placing one. A pass that cleared
        // afterwards leaves the buffers holding last frame's numbers for anything that reads them in
        // between — and what reads them is the indirect draw, which would then draw a cell's previous
        // population out of its current indices.
        commandList.CopyBuffer(zeroes, 0, counts, 0, counterBytes);
        commandList.CopyBuffer(zeroes, 0, heads, 0, counterBytes);

        barriers[0] = new(counts, ResourceState.CopyDestination, ResourceState.ShaderWrite);
        barriers[1] = new(heads, ResourceState.CopyDestination, ResourceState.ShaderWrite);
        barriers[2] = new(survivors, ResourceState.ShaderRead, ResourceState.ShaderWrite);
        commandList.Barrier(new(barriers, []));

        commandList.BindPipeline(Occluding ? occludingCounting : counting);
        commandList.BindDescriptorSet(DescriptorSetSlot.PerMaterial, descriptor);
        commandList.Dispatch(Groups);

        // ⚠ The whole of what makes this two dispatches rather than one. The placing phase reads the
        // counting phase's totals to find each level's base, so every invocation of the first has to
        // have retired before any invocation of the second runs.
        barriers[0] = new(counts, ResourceState.ShaderWrite, ResourceState.ShaderRead);
        barriers[1] = new(heads, ResourceState.ShaderWrite, ResourceState.ShaderWrite);
        barriers[2] = new(this.commands, ResourceState.IndirectArgument, ResourceState.ShaderWrite);
        commandList.Barrier(new(barriers, []));

        // ⚠ The same variant in both phases, always. The placing phase recomputes the counting
        // phase's verdict, so a pair that disagreed about occlusion would place survivors the counts
        // never accounted for — which writes past the end of a level's run.
        commandList.BindPipeline(Occluding ? occludingPlacing : placing);
        commandList.Dispatch(Groups);

        // The draw reads the survivors, their parameters and its own arguments, so all three have to
        // be visible to it before it runs.
        barriers[0] = new(survivors, ResourceState.ShaderWrite, ResourceState.ShaderRead);
        barriers[1] = new(parameters, ResourceState.ShaderWrite, ResourceState.ShaderRead);
        barriers[2] = new(this.commands, ResourceState.ShaderWrite, ResourceState.IndirectArgument);
        commandList.Barrier(new(barriers, []));

        return Groups * 2;
    }

    /// <summary>Where a batch's level's indirect command is, for the draw that reads it.</summary>
    /// <param name="batch">Which batch.</param>
    /// <param name="level">Which level.</param>
    /// <returns>The byte offset into <see cref="Commands" />.</returns>
    /// <exception cref="ArgumentOutOfRangeException">There is no such batch or level.</exception>
    public long CommandOf(int batch, int level) {
        ArgumentOutOfRangeException.ThrowIfNegative(batch);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(batch, BatchCapacity);
        ArgumentOutOfRangeException.ThrowIfNegative(level);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(level, MaxLevels);

        return (((long)batch * MaxLevels) + level) * DrawCommandBytes;
    }

    /// <summary>Where a batch's survivors are, and how many there is room for.</summary>
    /// <param name="batch">Which batch.</param>
    /// <returns>The first index into <see cref="Survivors" />, and the run's length in entries.</returns>
    /// <exception cref="ArgumentOutOfRangeException">There is no such batch.</exception>
    /// <remarks>
    ///     The length is the batch's instance count, not its survivor count — the latter is on the
    ///     device and reading it back is what an indirect draw exists to avoid.
    /// </remarks>
    public (int First, int Length) RunOf(int batch) {
        ArgumentOutOfRangeException.ThrowIfNegative(batch);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(batch, BatchCount);

        return ((int)records[batch].FirstInstance, (int)records[batch].InstanceCount);
    }

    /// <summary>The batch record a batch has, as the frame's last <see cref="Prepare" /> left it.</summary>
    /// <param name="batch">Which batch.</param>
    /// <returns>The record.</returns>
    /// <exception cref="ArgumentOutOfRangeException">There is no such batch.</exception>
    public FoliageCullBatchRecord BatchOf(int batch) {
        ArgumentOutOfRangeException.ThrowIfNegative(batch);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(batch, BatchCount);

        return records[batch];
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        device.Destroy(descriptor);
        device.Destroy(zeroes);
        device.Destroy(commands);
        device.Destroy(parameters);
        device.Destroy(survivors);
        device.Destroy(heads);
        device.Destroy(counts);
        device.Destroy(views);
        device.Destroy(batches);
        device.Destroy(owners);
        device.Destroy(instances);
        if (occludingPlacing.IsValid) {
            device.Destroy(occludingPlacing);
            device.Destroy(occludingCounting);
        }

        device.Destroy(placing);
        device.Destroy(counting);
        device.Destroy(layout);
        device.Destroy(setLayout);
    }

    /// <summary>Where a level takes over, or an unreachable distance if the batch has no such level.</summary>
    /// <remarks>
    ///     Unreachable rather than zero: the shader's chain only consults a threshold when
    ///     <c>levelCount</c> says the level exists, and a zero left in an unused slot would put every
    ///     instance at the last level the day somebody raised the count and forgot the distance.
    /// </remarks>
    static float LevelDistance(in FoliageDraw draw, int level) =>
        level < draw.Distances.Length ? draw.Distances[level] : float.MaxValue;
}
