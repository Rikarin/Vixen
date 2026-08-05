// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Foliage;
using Vixen.Graphics;
using Vixen.Shaders.Generated;

namespace Vixen.Rendering.Terrain;

/// <summary>One cell the scatter dispatch covers, as <c>GrassScatter.rvn</c> packs it.</summary>
/// <remarks>
///     ⚠ <b>Sixteen bytes, and the layout is the shader's declaration order.</b> A field added
///     carelessly here does not fail — it reads cell one out of the middle of cell zero, which draws
///     as a field of grass in the wrong place that moves when anything else changes.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct GrassCellRecord {
    /// <summary>The cell's X coordinate. Signed: a world has no origin corner.</summary>
    public int CellX;

    /// <summary>And its Z.</summary>
    public int CellZ;

    /// <summary>Where this cell's blades begin in the instance buffer.</summary>
    public uint First;

    /// <summary>How many it may write before it must stop.</summary>
    public uint Capacity;

    /// <summary>How many bytes one of these is.</summary>
    public static int SizeInBytes => Marshal.SizeOf<GrassCellRecord>();
}

/// <summary>One blade as the dispatch writes it, matching <c>GrassInstance</c> in the shader.</summary>
/// <remarks>
///     Forty-eight bytes: a position and a uniform scale, a quaternion, and the two per-instance
///     numbers the draw reads. <c>FoliageStore.InstanceBytes</c> plus an <c>InstanceParameters</c>,
///     which is what lets a seam test compare the dispatch's output against
///     <see cref="GrassScatter" />'s bytes rather than against an interpretation of them.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct GrassInstanceRecord {
    /// <summary>Where the blade stands, in world space.</summary>
    public Vector3 Position;

    /// <summary>How big it is, uniformly.</summary>
    public float Scale;

    /// <summary>Which way it faces, as <c>(x, y, z, w)</c>.</summary>
    public Quaternion Rotation;

    /// <summary>A per-blade variation, 0…1.</summary>
    public float Tint;

    /// <summary>A phase offset in radians, so a clump does not move as one object.</summary>
    public float WindPhase;

    /// <summary>Declared so the record is forty-eight bytes on both sides.</summary>
    public float Padding0;

    /// <summary>And the second half of it.</summary>
    public float Padding1;

    /// <summary>How many bytes one of these is.</summary>
    public static int SizeInBytes => Marshal.SizeOf<GrassInstanceRecord>();

    /// <summary>The record a blade the CPU reference produced would be.</summary>
    /// <param name="blade">The blade.</param>
    /// <returns>The record.</returns>
    /// <remarks>
    ///     What the seam test compares against. The dispatch and
    ///     <see cref="GrassScatter.Scatter" /> are the same arithmetic, so a blade from either has to
    ///     pack to the same forty-eight bytes.
    /// </remarks>
    public static GrassInstanceRecord Of(in GrassBlade blade) =>
        new() {
            Position = blade.Instance.Position,
            Scale = blade.Instance.Scale,
            Rotation = blade.Instance.Rotation,
            Tint = blade.Tint,
            WindPhase = blade.WindPhase,
            Padding0 = 0f,
            Padding1 = 0f
        };
}

/// <summary>
///     The host for <c>GrassScatter.rvn</c>: the cell records, the ring of device buffers, and the
///     dispatch that fills them.
/// </summary>
/// <remarks>
///     <para>
///         <b>[docs/plan/31 § T6]'s owed device half.</b> <see cref="GrassRenderer" /> is the CPU
///         reference — what a headless test and a machine with no compute queue use — and this is the
///         dispatch that does the same arithmetic where a forest can afford it. Neither is the
///         definition: <see cref="GrassScatter.Hash" /> is, and both transliterate it.
///     </para>
///     <para>
///         ⚠ <b>One counter per cell, not one for the dispatch.</b> A global head would serialise
///         every invocation of every cell on one cache line, and it would make a cell's run depend on
///         the order the workgroups happened to retire in — which is the one thing a scatter that has
///         to be reproducible cannot afford.
///     </para>
///     <para>
///         ⚠ <b>The instance buffer is the ring, and its slots are assigned by the host.</b> Each
///         resident cell owns a fixed run, so an invocation never negotiates for space with another
///         cell — only with the other candidates of its own. That is what makes the atomic local and
///         the memory bounded.
///     </para>
///     <para>
///         ⚠ <b>The counts buffer is cleared before every dispatch and not after.</b> A pass that
///         cleared afterwards leaves the buffer holding last frame's numbers for anything that reads
///         it in between — and what reads it is the indirect draw, which would then draw a cell's
///         previous population out of its current bytes.
///     </para>
/// </remarks>
public sealed class GrassDispatch : IDisposable {
    /// <summary>How many candidates a workgroup covers on each axis. `[ComputeShader(8, 8, 1)]`.</summary>
    public const int GroupSize = 8;

    /// <summary>What each ring slot's block is aligned to.</summary>
    /// <remarks>
    ///     ⚠ <b>256, which is the largest <c>minUniformBufferOffsetAlignment</c> and
    ///     <c>minStorageBufferOffsetAlignment</c> in practice</b> — <c>ImpostorCapturePass</c>'s
    ///     number, for its reason: an offset a device refuses is a descriptor write that fails, and a
    ///     block read from an address the host did not write is a frame scattered with another
    ///     frame's cells.
    /// </remarks>
    const int SlotAlignment = 256;

    readonly IGraphicsDevice device;
    readonly DescriptorSetLayoutHandle setLayout;
    readonly DescriptorSetLayoutHandle emptySetLayout;
    readonly PipelineLayoutHandle layout;
    readonly PipelineHandle pipeline;
    readonly PipelineHandle argumentPipeline;
    readonly DescriptorSetHandle[] descriptors;

    readonly BufferHandle cells;
    readonly BufferHandle instances;
    readonly BufferHandle counts;
    readonly BufferHandle commands;
    readonly BufferHandle constants;

    readonly BufferHandle zeroes;

    readonly GrassCellRecord[] records;
    readonly DrawCommand[] arguments;
    readonly byte[] scratch;
    readonly BufferBarrier[] barriers = new BufferBarrier[3];

    readonly int slots;
    readonly long cellStride;
    readonly long commandStride;
    readonly long constantStride;

    int slot;
    bool disposed;

    /// <summary>Creates the pass and its buffers.</summary>
    /// <param name="device">The device.</param>
    /// <param name="shader">The compiled <c>GrassScatter</c> compute stage — the scattering variant.</param>
    /// <param name="arguments">And the <c>Arguments = true</c> one's.</param>
    /// <param name="capacity">How many cells may be scattered in one dispatch.</param>
    /// <param name="bladesPerCell">How many blades a cell's run holds.</param>
    /// <exception cref="ArgumentNullException">There is no device.</exception>
    /// <exception cref="ArgumentException">A shader is not valid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A capacity is not positive.</exception>
    public GrassDispatch(
        IGraphicsDevice device,
        ShaderHandle shader,
        ShaderHandle arguments,
        int capacity = 256,
        int bladesPerCell = 4096
    ) {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bladesPerCell);

        if (!shader.IsValid || !arguments.IsValid) {
            throw new ArgumentException(
                "A grass scatter needs both of its compute stages — the scattering variant and the "
                + "one that turns its counts into indirect draws.",
                shader.IsValid ? nameof(arguments) : nameof(shader)
            );
        }

        this.device = device;

        Capacity = capacity;
        BladesPerCell = bladesPerCell;

        // ⚠ One block per frame in flight for everything the host writes. `device.Write` is an
        // immediate memcpy and recorded work executes at submit, so a single-buffered block would be
        // rewritten while the previous frame still reads it — as its cell list, its constants, and
        // worst of all as the indirect arguments its draws are consuming.
        slots = Math.Max(1, device.FramesInFlight);
        cellStride = Align((long)capacity * GrassCellRecord.SizeInBytes);
        commandStride = Align((long)capacity * DrawCommandBytes);
        constantStride = Align(GrassScatterKeys.ConstantBufferSize);

        records = new GrassCellRecord[capacity];
        this.arguments = new DrawCommand[capacity];
        scratch = new byte[Math.Max(capacity * GrassCellRecord.SizeInBytes, GrassScatterKeys.ConstantBufferSize)];

        setLayout = device.CreateDescriptorSetLayout(
            new(
                DescriptorSetSlot.PerMaterial,
                [
                    new(GrassScatterKeys.ConstantBufferBinding, DescriptorKind.UniformBuffer, ShaderStage.Compute),
                    new(GrassScatterKeys.HeightMapBinding, DescriptorKind.SampledTexture, ShaderStage.Compute),
                    new(GrassScatterKeys.WeightMapBinding, DescriptorKind.SampledTexture, ShaderStage.Compute),
                    new(GrassScatterKeys.HeightSamplerBinding, DescriptorKind.Sampler, ShaderStage.Compute),
                    new(GrassScatterKeys.WeightSamplerBinding, DescriptorKind.Sampler, ShaderStage.Compute),
                    new(GrassScatterKeys.HoleMapBinding, DescriptorKind.SampledTexture, ShaderStage.Compute),
                    new(GrassScatterKeys.HoleSamplerBinding, DescriptorKind.Sampler, ShaderStage.Compute),
                    new(GrassScatterKeys.CellsBinding, DescriptorKind.StorageBuffer, ShaderStage.Compute),
                    new(GrassScatterKeys.InstancesBinding, DescriptorKind.StorageBuffer, ShaderStage.Compute),
                    new(GrassScatterKeys.CountsBinding, DescriptorKind.StorageBuffer, ShaderStage.Compute),
                    new(GrassScatterKeys.CommandsBinding, DescriptorKind.StorageBuffer, ShaderStage.Compute)
                ],
                "grass scatter"
            )
        );

        // Padded below the material set on TerrainRenderer's terms: the shader's bindings are set 2
        // and a pipeline layout is positional, so the two unused slots hold one empty layout twice.
        emptySetLayout = device.CreateDescriptorSetLayout(new(DescriptorSetSlot.PerFrame, [], "grass scatter empty"));
        layout = device.CreatePipelineLayout(new([emptySetLayout, emptySetLayout, setLayout], [], "grass scatter"));
        pipeline = device.CreateComputePipeline(new(shader, layout, "grass scatter"));

        // One layout and two pipelines, because the two phases are one shader and a permutation.
        argumentPipeline = device.CreateComputePipeline(new(arguments, layout, "grass arguments"));

        cells = device.CreateBuffer(
            new(
                cellStride * slots,
                BufferUsage.Storage,
                MemoryAccess.HostUpload,
                "grass cells"
            )
        );

        // ⚠ Device-local, and it is the one buffer here that is. The host never reads a blade — the
        // draw does — so staging it back would be the whole point of scattering on the device thrown
        // away.
        instances = device.CreateBuffer(
            new(
                (long)capacity * bladesPerCell * GrassInstanceRecord.SizeInBytes,
                BufferUsage.Storage,
                MemoryAccess.DeviceLocal,
                "grass instances"
            )
        );

        counts = device.CreateBuffer(
            new(
                (long)capacity * sizeof(uint),
                BufferUsage.Storage | BufferUsage.CopyDestination,
                MemoryAccess.DeviceLocal,
                "grass counts"
            )
        );

        // Written by the host with the blade mesh's template, patched by the argument phase with two
        // fields, then read by the draw as its arguments — three usages of one buffer, all named.
        // The ring matters most here: a host write into the slot an in-flight frame is drawing from
        // lands mid-consumption of its indirect arguments.
        commands = device.CreateBuffer(
            new(
                commandStride * slots,
                BufferUsage.Storage | BufferUsage.Indirect,
                MemoryAccess.HostUpload,
                "grass draw arguments"
            )
        );

        constants = device.CreateBuffer(
            new(
                constantStride * slots,
                BufferUsage.Uniform,
                MemoryAccess.HostUpload,
                "grass scatter constants"
            )
        );

        // A buffer of zeros to copy from. A command list can copy and cannot fill, so this is what
        // "clear the counters" is spelled as — a few kilobytes written once and read every frame.
        zeroes = device.CreateBuffer(
            new(
                (long)capacity * sizeof(uint),
                BufferUsage.CopySource,
                MemoryAccess.HostUpload,
                "grass count zeroes"
            )
        );

        device.Write(zeroes, 0, new byte[capacity * sizeof(uint)]);

        // One set per ring slot, because a set an in-flight frame's dispatch reads cannot be
        // rewritten any more than the buffers it points at.
        descriptors = new DescriptorSetHandle[slots];

        for (var index = 0; index < slots; index++) {
            descriptors[index] = device.CreateDescriptorSet(setLayout, $"grass scatter {index}");
        }
    }

    /// <summary>Rounds a slot's block up to the offset granularity every target accepts.</summary>
    static long Align(long size) => (size + SlotAlignment - 1) / SlotAlignment * SlotAlignment;

    /// <summary>How many bytes one indirect command is — <c>DrawIndexedIndirect</c>'s stride.</summary>
    public static int DrawCommandBytes => Marshal.SizeOf<DrawCommand>();

    /// <summary>How many cells one dispatch may cover.</summary>
    public int Capacity { get; }

    /// <summary>
    ///     What one blade is drawn with: the template the last <see cref="Prepare" /> baked.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A parameter of <see cref="Prepare" /> rather than a settable property.</b> A stored
    ///     template is only coherent if it is written before the <see cref="Prepare" /> that bakes it
    ///     into the uploaded commands, and nothing enforced that order — the wrong one drew nothing,
    ///     silently, for exactly one frame per cell.
    /// </remarks>
    public DrawCommand Mesh { get; private set; }

    /// <summary>How many blades a cell's run holds.</summary>
    public int BladesPerCell { get; }

    /// <summary>How many cells the last <see cref="Prepare" /> wrote records for.</summary>
    public int CellCount { get; private set; }

    /// <summary>How many blades the last dispatch could produce at most.</summary>
    public long Ceiling => (long)CellCount * BladesPerCell;

    /// <summary>How many workgroups the last <see cref="Prepare" /> would dispatch, on each axis.</summary>
    public (int X, int Y, int Z) Groups { get; private set; }

    /// <summary>How many cells were refused because the dispatch was full.</summary>
    /// <remarks>
    ///     ⚠ <b>Announced rather than silent.</b> A dispatch that quietly covered the first two
    ///     hundred and fifty-six of three hundred resident cells is a field that stops at a distance
    ///     nobody chose — the same reason <see cref="GrassResidency.Refused" /> exists one level up.
    /// </remarks>
    public int Refused { get; private set; }

    /// <summary>Writes the cell records and the constants for one grass type.</summary>
    /// <param name="type">Which grass.</param>
    /// <param name="grid">The cell grid.</param>
    /// <param name="resident">Which cells to scatter, in the order their runs are assigned.</param>
    /// <param name="terrain">What the shader samples the ground from.</param>
    /// <param name="mesh">
    ///     What one blade is drawn with: its index count, its first index and its vertex offset.
    ///     ⚠ The instance count and the first instance in it are ignored, because those are the two
    ///     fields the device writes. <see cref="GrassDrawPass.MeshTemplate" /> is the one a pass's
    ///     own blade answers.
    /// </param>
    /// <param name="densityScale">A runtime scalar over the whole field, 0…1.</param>
    /// <returns>How many cells the dispatch will cover.</returns>
    /// <exception cref="ArgumentNullException">There are no cells.</exception>
    /// <exception cref="ArgumentException">The type is not one a scatter can grow.</exception>
    public int Prepare(
        in GrassType type,
        FoliageCellGrid grid,
        IReadOnlyList<GrassSlot> resident,
        in GrassTerrainSource terrain,
        in DrawCommand mesh,
        float densityScale = 1f
    ) {
        ArgumentNullException.ThrowIfNull(resident);
        ObjectDisposedException.ThrowIf(disposed, this);

        // Refused loudly rather than scattered as nothing: a zero density, a backwards range or a
        // broken wind profile all dispatch a field that grows no blade, and no counter says why.
        if (type.Validate() is { } refusal) {
            throw new ArgumentException(refusal, nameof(type));
        }

        slot = (slot + 1) % slots;
        Mesh = mesh;

        CellCount = Math.Min(resident.Count, Capacity);
        Refused = resident.Count - CellCount;

        for (var index = 0; index < CellCount; index++) {
            records[index] = new() {
                CellX = resident[index].Cell.X,
                CellZ = resident[index].Cell.Z,

                // ⚠ The *ring slot*, not the loop index. A cell keeps its buffer across frames — that
                // is what the ring is — so filing its run under where it happened to appear in this
                // frame's list would move every blade whenever a nearer cell arrived.
                First = (uint)((long)resident[index].Slot * BladesPerCell),
                Capacity = (uint)BladesPerCell
            };
        }

        device.Write(cells, slot * cellStride, MemoryMarshal.AsBytes(records.AsSpan(0, Math.Max(CellCount, 1))));

        for (var index = 0; index < CellCount; index++) {
            // The two fields the device writes are zeroed rather than guessed, so a cell whose
            // argument write never lands draws nothing instead of drawing somebody else's blades.
            arguments[index] = mesh with { InstanceCount = 0u, FirstInstance = 0u };
        }

        if (CellCount > 0) {
            device.Write(commands, slot * commandStride, MemoryMarshal.AsBytes(arguments.AsSpan(0, CellCount)));
        }

        var side = type.GridOf(grid.CellSize);
        var groups = (side + GroupSize - 1) / GroupSize;

        Groups = (groups, groups, Math.Max(CellCount, 1));

        WriteConstants(in type, grid, in terrain, side, densityScale);

        device.UpdateDescriptorSet(
            descriptors[slot],
            [
                DescriptorWrite.Uniform(
                    GrassScatterKeys.ConstantBufferBinding,
                    constants,
                    slot * constantStride,
                    GrassScatterKeys.ConstantBufferSize
                ),
                DescriptorWrite.Texture(GrassScatterKeys.HeightMapBinding, terrain.HeightMap),
                DescriptorWrite.Texture(GrassScatterKeys.WeightMapBinding, terrain.WeightMap),
                DescriptorWrite.SamplerAt(GrassScatterKeys.HeightSamplerBinding, terrain.HeightSampler),
                DescriptorWrite.SamplerAt(GrassScatterKeys.WeightSamplerBinding, terrain.WeightSampler),
                DescriptorWrite.Texture(GrassScatterKeys.HoleMapBinding, terrain.HoleMap),
                DescriptorWrite.SamplerAt(GrassScatterKeys.HoleSamplerBinding, terrain.HoleSampler),
                DescriptorWrite.Storage(GrassScatterKeys.CellsBinding, cells, slot * cellStride, cellStride),
                DescriptorWrite.Storage(GrassScatterKeys.InstancesBinding, instances),
                DescriptorWrite.Storage(GrassScatterKeys.CountsBinding, counts),
                DescriptorWrite.Storage(GrassScatterKeys.CommandsBinding, commands, slot * commandStride, commandStride)
            ]
        );

        return CellCount;
    }

    /// <summary>Records the dispatch.</summary>
    /// <param name="commands">Where to record.</param>
    /// <returns>How many workgroups were dispatched, or zero if there was nothing to scatter.</returns>
    /// <exception cref="ArgumentNullException">There is no command list.</exception>
    public int Record(ICommandList commands) {
        ArgumentNullException.ThrowIfNull(commands);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (CellCount == 0) {
            return 0;
        }

        // ⚠ Zeroed before the dispatch, not after. A pass that cleared afterwards leaves the buffer
        // holding last frame's numbers for anything that reads it in between — and what reads it is
        // the indirect draw, which would then draw a cell's previous population out of its current
        // bytes. The zeros are copied from a host buffer because a fill is not in the command list's
        // vocabulary and a copy is.
        commands.CopyBuffer(zeroes, 0, counts, 0, (long)CellCount * sizeof(uint));

        barriers[0] = new(counts, ResourceState.CopyDestination, ResourceState.ShaderWrite);
        barriers[1] = new(instances, ResourceState.ShaderRead, ResourceState.ShaderWrite);
        barriers[2] = new(this.commands, ResourceState.IndirectArgument, ResourceState.ShaderWrite);
        commands.Barrier(new(barriers, []));

        commands.BindPipeline(pipeline);
        commands.BindDescriptorSet(DescriptorSetSlot.PerMaterial, descriptors[slot]);
        commands.Dispatch(Groups.X, Groups.Y, Groups.Z);

        // ⚠ A second dispatch, and it cannot be folded into the first. The invocation that claims
        // slot zero does not know how many will follow it, and an indirect draw needs the *final*
        // count — so the argument write has to be after every candidate of the cell has retired.
        barriers[0] = new(counts, ResourceState.ShaderWrite, ResourceState.ShaderRead);
        barriers[1] = new(instances, ResourceState.ShaderWrite, ResourceState.ShaderRead);
        barriers[2] = new(this.commands, ResourceState.ShaderWrite, ResourceState.ShaderWrite);
        commands.Barrier(new(barriers, []));

        commands.BindPipeline(argumentPipeline);
        commands.Dispatch(1, 1, CellCount);

        // The draw reads its own arguments and the blades they point at.
        barriers[0] = new(this.commands, ResourceState.ShaderWrite, ResourceState.IndirectArgument);
        barriers[1] = new(instances, ResourceState.ShaderRead, ResourceState.ShaderRead);
        barriers[2] = new(counts, ResourceState.ShaderRead, ResourceState.ShaderRead);
        commands.Barrier(new(barriers, []));

        return (Groups.X * Groups.Y * Groups.Z) + CellCount;
    }

    /// <summary>Issues one indirect draw per resident cell.</summary>
    /// <param name="commandList">Where to record.</param>
    /// <returns>How many draws were issued.</returns>
    /// <exception cref="ArgumentNullException">There is no command list.</exception>
    /// <exception cref="ObjectDisposedException">The pass is gone.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>It binds nothing.</b> The pipeline, the blade mesh's vertex and index buffers and
    ///         the material's descriptor sets are the caller's, because they are a material's and this
    ///         class is a compute dispatch. What it owns is the argument buffer and the knowledge of
    ///         which slot each cell's command is in.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>One draw per cell rather than one for the field</b>, and the reason is the ring: a
    ///         cell's blades are at its own slot's offset, and the slots a frame is using are not
    ///         contiguous. A single multi-draw would need the commands packed in slot order, which
    ///         would mean rewriting them whenever any cell was evicted.
    ///     </para>
    /// </remarks>
    public int RecordDraws(ICommandList commandList) {
        ArgumentNullException.ThrowIfNull(commandList);
        ObjectDisposedException.ThrowIf(disposed, this);

        for (var index = 0; index < CellCount; index++) {
            commandList.DrawIndexedIndirect(commands, CommandOf(index));
        }

        return CellCount;
    }

    /// <summary>Where a cell's indirect command is, for the draw that reads it.</summary>
    /// <param name="index">
    ///     Which record — the cell's place in the list <see cref="Prepare" /> was given, which is what
    ///     the dispatch's <c>z</c> indexes.
    /// </param>
    /// <returns>The byte offset into <see cref="Commands" />.</returns>
    /// <exception cref="ArgumentOutOfRangeException">There is no such record.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The record index, not the ring slot — and <see cref="RunOf" /> is the other
    ///         one.</b> The commands are indexed the way the shader indexes <c>cells</c>, which is
    ///         this frame's list; the blades are indexed by the slot the cell has kept across frames.
    ///         Confusing the two draws the right number of blades from the wrong place.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Valid for the frame the last <see cref="Prepare" /> wrote</b>, because the
    ///         commands ring by frames in flight and the offset carries the current ring slot.
    ///     </para>
    /// </remarks>
    public long CommandOf(int index) {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Capacity);

        return (slot * commandStride) + ((long)index * DrawCommandBytes);
    }

    /// <summary>Where a cell's blades are, for the draw that reads them.</summary>
    /// <param name="slot">Which ring slot.</param>
    /// <returns>The byte offset into the instance buffer, and how many bytes the run is.</returns>
    public (long Offset, long Length) RunOf(int slot) =>
        ((long)slot * BladesPerCell * GrassInstanceRecord.SizeInBytes,
            (long)BladesPerCell * GrassInstanceRecord.SizeInBytes);

    /// <summary>The buffer the draw reads its instances from.</summary>
    public BufferHandle Instances => instances;

    /// <summary>And the one holding how many each cell produced.</summary>
    public BufferHandle Counts => counts;

    /// <summary>And the indirect arguments the draw reads.</summary>
    public BufferHandle Commands => commands;

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        foreach (var set in descriptors) {
            device.Destroy(set);
        }

        device.Destroy(constants);
        device.Destroy(zeroes);
        device.Destroy(commands);
        device.Destroy(counts);
        device.Destroy(instances);
        device.Destroy(cells);
        device.Destroy(argumentPipeline);
        device.Destroy(pipeline);
        device.Destroy(layout);
        device.Destroy(setLayout);
        device.Destroy(emptySetLayout);
    }

    /// <summary>Packs the uniform block the shader reads.</summary>
    /// <remarks>
    ///     ⚠ <b>By generated offset, never by writing the fields in order.</b> Std140 pads a
    ///     <c>float3</c> to sixteen bytes and a <c>float2</c> to eight, so a block written in
    ///     declaration order puts every field after the first vector at the wrong address — and the
    ///     symptom is a field of grass whose density is somebody else's slope limit.
    /// </remarks>
    void WriteConstants(
        in GrassType type,
        FoliageCellGrid grid,
        in GrassTerrainSource terrain,
        int side,
        float densityScale
    ) {
        var block = scratch.AsSpan(0, GrassScatterKeys.ConstantBufferSize);

        block.Clear();

        // The generated block, whose offsets are baked from the reflection. Writing the fields in
        // declaration order instead would put every one after the first vector at the wrong address,
        // because std140 pads a float3 to sixteen bytes — and the symptom is a field of grass whose
        // density is somebody else's slope limit.
        new GrassScatterConstants {
            WeightChannel = terrain.WeightChannel,
            HeightMapSize = terrain.HeightMapSize,
            TileSamples = terrain.TileSamples,
            TileQuads = terrain.TileQuads,
            AtlasTiles = terrain.AtlasTiles,
            HeightRange = terrain.HeightRange,
            MetresPerQuad = terrain.MetresPerQuad,
            TerrainOrigin = terrain.Origin,
            CellSize = grid.CellSize,
            GridSide = side,
            Jitter = Math.Clamp(type.Jitter, 0f, 1f),
            WeightRange = new(type.MinWeight, type.MaxWeight),
            ScaleRange = new(type.MinScale, type.MaxScale),
            AlignToNormal = Math.Clamp(type.AlignToNormal, 0f, 1f),
            MaxSlope = type.MaxSlope,
            DensityScale = Math.Clamp(densityScale, 0f, 1f)
        }.Write(block);

        device.Write(constants, slot * constantStride, block);
    }
}

/// <summary>What the scatter samples the ground from, on the device.</summary>
/// <param name="HeightMap">The composited heights, one texel per sample.</param>
/// <param name="WeightMap">The weightmap holding the bound layer.</param>
/// <param name="HoleMap">Where the terrain has no surface at all.</param>
/// <param name="HeightSampler">How the heights are read.</param>
/// <param name="WeightSampler">And the weights.</param>
/// <param name="HoleSampler">And the holes — clamped, unfiltered, at level 0.</param>
/// <param name="WeightChannel">Which of the weightmap's four channels the layer is.</param>
/// <param name="HeightMapSize">How many texels the atlas is.</param>
/// <param name="HeightRange">What a stored height of 0 and 1 mean, in metres.</param>
/// <param name="MetresPerQuad">How far apart two samples are.</param>
/// <param name="Origin">Where the terrain's low corner is, in world space.</param>
/// <param name="TileSamples">How many samples one tile's block is, on a side.</param>
/// <param name="TileQuads">How many quads that is — one fewer, because the blocks duplicate their boundary samples.</param>
/// <param name="AtlasTiles">How many tiles the atlas holds, along each axis.</param>
/// <remarks>
///     <para>
///         ⚠ <b>The textures the terrain renderer already owns, named rather than re-created.</b> A
///         scatter with its own copy of the heightmap would be a second upload of the same megabytes
///         and a second thing to invalidate when a stroke lands.
///     </para>
///     <para>
///         ⚠ <b>The tile geometry is required, not defaulted</b>, because the atlas packs each tile
///         as its own block and a flat map over <paramref name="HeightMapSize" /> is right only for a
///         single-tile terrain. <see cref="Vixen.Terrain.TerrainAtlas" /> is where the numbers come
///         from — the same three <c>Terrain.rvn</c> binds.
///     </para>
/// </remarks>
public readonly record struct GrassTerrainSource(
    TextureViewHandle HeightMap,
    TextureViewHandle WeightMap,
    TextureViewHandle HoleMap,
    SamplerHandle HeightSampler,
    SamplerHandle WeightSampler,
    SamplerHandle HoleSampler,
    int WeightChannel,
    Vector2 HeightMapSize,
    Vector2 HeightRange,
    float MetresPerQuad,
    Vector3 Origin,
    float TileSamples,
    float TileQuads,
    Vector2 AtlasTiles
);
