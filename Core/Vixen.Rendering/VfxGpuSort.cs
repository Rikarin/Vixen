// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Vfx;

namespace Vixen.Rendering;

/// <summary>The order a device-resident effect is drawn in, produced on the device.</summary>
/// <remarks>
///     <para>
///         <b>The last thing a GPU particle system still came home for.</b> Reaping took the
///         compaction off the bus and the indirect draw took the count; this takes the order, which
///         an alpha-blended effect needs and which was the remaining reason a device-resident system
///         had to read itself back every frame.
///     </para>
///     <para>
///         <b>A bitonic sort, because a GPU comparison sort has to be a sorting <i>network</i>.</b>
///         Every invocation runs the same instructions, so a data-dependent partition — which is what
///         a quicksort is — would serialise them. Bitonic is the network with no data dependence at
///         all: a fixed sequence of compare-exchanges, every pass perfectly parallel, and no
///         invocation ever needing to know what another decided. It costs log²(n) passes, which for
///         the 4096 default is seventy-eight dispatches.
///     </para>
///     <para>
///         ⚠ <b>The buffers are the capacity rounded up to a power of two, and the tail is padded
///         rather than the network truncated.</b> A bitonic network is defined for a power-of-two
///         length; a particle system has whatever count the last reap produced. Padding the slots
///         above the count with the largest possible key sorts them past every real particle and
///         leaves the network the same network every frame — truncating instead would mean a
///         different pass list per frame, computed on the host, from a count the host does not have
///         without a readback. Which is the readback this class exists to avoid.
///     </para>
///     <para>
///         ⚠ <b>What comes out is an index buffer, not reordered particles.</b> Moving the particles
///         would be a second compaction over every attribute; an order is one <c>uint</c> each, and
///         the draw reads through it. It is also what keeps a particle's slot stable across the sort,
///         which matters because the simulation addresses particles by slot and the sort runs
///         between frames of it.
///     </para>
/// </remarks>
public sealed class VfxGpuSort : IDisposable {
    /// <summary>The push constants the seed kernel takes.</summary>
    /// <remarks>
    ///     ⚠ The field order <i>is</i> the shader's declaration order, which is what
    ///     <c>VfxShaderUniforms</c> says about its own and is true for the same reason: a field
    ///     inserted into the middle of either one compiles perfectly and moves everything after it.
    /// </remarks>
    /// <remarks>
    ///     ⚠ <b>The offsets are std140's and are stated rather than implied, because getting them
    ///     wrong is what this looks like.</b> A <c>float3</c> occupies twelve bytes and <i>aligns to
    ///     sixteen</i>, so the shader's block puts the camera at 16 and the mode at 28 and is
    ///     thirty-two bytes long; the packed struct anyone writes first is twenty, and every field
    ///     after the first lands where the shader does not read. Written as explicit offsets so a
    ///     reader sees the layout instead of counting padding — and the one merciful thing about
    ///     this particular mistake is that the validation layer catches it, which it did.
    ///     <c>LayoutGateTests</c> is the same hazard put in front of a device deliberately; a
    ///     push-constant block has no descriptor set to build from a reflection, so this one is met
    ///     by hand.
    /// </remarks>
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    struct SeedConstants {
        [FieldOffset(0)] public int ParticleCount;
        [FieldOffset(16)] public float CameraX;
        [FieldOffset(20)] public float CameraY;
        [FieldOffset(24)] public float CameraZ;
        [FieldOffset(28)] public int Mode;
    }

    /// <summary>The two the step kernel takes: the sequence size and the pair distance.</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct StepConstants {
        public uint K;
        public uint J;
    }

    readonly IGraphicsDevice device;
    readonly VfxGpuSimulation simulation;
    readonly BufferHandle keys;
    readonly BufferHandle indices;
    readonly BufferHandle readback;
    readonly DescriptorSetLayoutHandle seedLayout;
    readonly DescriptorSetLayoutHandle stepLayout;
    readonly DescriptorSetHandle[] seedSets;
    readonly BufferHandle[] boundPosition;
    readonly BufferHandle[] boundAge;
    readonly DescriptorSetHandle stepSet;
    readonly BufferBarrier[] barriers;

    ResourceState state = ResourceState.Undefined;
    bool disposed;

    /// <summary>Allocates the key and index buffers for one system.</summary>
    /// <param name="device">The device.</param>
    /// <param name="simulation">The system being sorted, for its attribute buffers.</param>
    /// <param name="mode">Which key to sort on.</param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    /// <exception cref="ArgumentException">The graph has no attribute the mode needs.</exception>
    public VfxGpuSort(IGraphicsDevice device, VfxGpuSimulation simulation, VfxSortMode mode) {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(simulation);

        this.device = device;
        this.simulation = simulation;
        Mode = mode;

        var source = mode == VfxSortMode.ByAge
            ? simulation.Storage(VfxAttribute.Age)
            : simulation.Storage(VfxAttribute.Position);

        if (!source.IsValid) {
            throw new ArgumentException(
                $"Sorting by {mode} needs the {(mode == VfxSortMode.ByAge ? "age" : "position")} attribute and "
                + "this graph does not touch it. A graph that never places its particles has no order to be "
                + "put in.",
                nameof(mode)
            );
        }

        Capacity = Rounded(simulation.Capacity);

        keys = device.CreateBuffer(new(
            Capacity * sizeof(uint),
            BufferUsage.Storage | BufferUsage.CopySource,
            MemoryAccess.DeviceLocal,
            "vfx.sort.keys"
        ));

        indices = device.CreateBuffer(new(
            Capacity * sizeof(uint),
            BufferUsage.Storage | BufferUsage.CopySource,
            MemoryAccess.DeviceLocal,
            "vfx.sort.indices"
        ));

        readback = device.CreateBuffer(new(
            Capacity * sizeof(uint),
            BufferUsage.CopyDestination,
            MemoryAccess.HostReadback,
            "vfx.sort.readback"
        ));

        // The seed kernel reads a position and an age and writes the two outputs; the step kernel
        // touches nothing but the two outputs. Two layouts rather than one, because a set holding a
        // binding a pipeline never declares is a set the layout check refuses.
        seedLayout = device.CreateDescriptorSetLayout(new(
            DescriptorSetSlot.PerFrame,
            [
                new(0, DescriptorKind.StorageBuffer, ShaderStage.Compute),
                new(1, DescriptorKind.StorageBuffer, ShaderStage.Compute),
                new(2, DescriptorKind.StorageBuffer, ShaderStage.Compute),
                new(3, DescriptorKind.StorageBuffer, ShaderStage.Compute)
            ],
            "vfx.sort.seed"
        ));

        stepLayout = device.CreateDescriptorSetLayout(new(
            DescriptorSetSlot.PerFrame,
            [
                new(0, DescriptorKind.StorageBuffer, ShaderStage.Compute),
                new(1, DescriptorKind.StorageBuffer, ShaderStage.Compute)
            ],
            "vfx.sort.step"
        ));

        SeedLayout = device.CreatePipelineLayout(new(
            [seedLayout],
            [new(ShaderStage.Compute, 0, Marshal.SizeOf<SeedConstants>())],
            "vfx.sort.seed"
        ));

        StepLayout = device.CreatePipelineLayout(new(
            [stepLayout],
            [new(ShaderStage.Compute, 0, Marshal.SizeOf<StepConstants>())],
            "vfx.sort.step"
        ));

        // ⚠ One seed set per attribute generation, not one seed set. A reaping simulation
        // double-buffers its attribute buffers and flips at the end of every `Reap`, so the handles
        // `Storage` answers with are not the handles it answered with last frame. A set written once
        // in this constructor names the generation that was current when the sort was built and goes
        // on naming it forever — the sort then orders the pre-reap particles, which is an order that
        // is right on frame one, wrong from frame two, and drawn as blending in the wrong order
        // rather than as anything a validation layer would mention. Two sets, written on demand and
        // never rewritten, is the same answer `VfxGpuSimulation` gives itself for `descriptorSets`,
        // and it is the one that does not touch a set the device may still be reading.
        seedSets = new DescriptorSetHandle[simulation.HasReap ? 2 : 1];
        boundPosition = new BufferHandle[seedSets.Length];
        boundAge = new BufferHandle[seedSets.Length];

        for (var generation = 0; generation < seedSets.Length; generation++) {
            seedSets[generation] = device.CreateDescriptorSet(seedLayout, $"vfx.sort.seed{generation}");
        }

        stepSet = device.CreateDescriptorSet(stepLayout, "vfx.sort.step");

        Bind();

        device.UpdateDescriptorSet(stepSet, [
            DescriptorWrite.Storage(0, keys),
            DescriptorWrite.Storage(1, indices)
        ]);

        barriers = new BufferBarrier[2];
    }

    /// <summary>How many slots the network covers — the capacity rounded up to a power of two.</summary>
    public int Capacity { get; }

    /// <summary>Which key this sorts on.</summary>
    public VfxSortMode Mode { get; }

    /// <summary>How many compute dispatches this has recorded since it was built.</summary>
    /// <remarks>
    ///     <see cref="VfxGpuSimulation.Dispatches" />' counterpart and for the same reason: a sort
    ///     nothing drove and a sort that ran are indistinguishable from the drawn frame, because the
    ///     order is only read by a draw that does not exist yet. One <see cref="Record" /> adds
    ///     <c>Passes(Capacity) + 1</c> — the network's passes and the seed — so a caller can check the
    ///     count against the number this class already promised rather than against a capture.
    /// </remarks>
    public int Dispatches { get; private set; }

    /// <summary>The layout the seed kernel's pipeline is created against.</summary>
    public PipelineLayoutHandle SeedLayout { get; }

    /// <summary>The layout the step kernel's pipeline is created against.</summary>
    public PipelineLayoutHandle StepLayout { get; }

    /// <summary>The sorted order — one particle slot per entry, furthest or oldest first.</summary>
    /// <remarks>
    ///     Valid for the first <c>count</c> entries of the last <see cref="Record" />; the rest are
    ///     the padding, which holds slots above the live count in whatever order the network left
    ///     them.
    /// </remarks>
    public BufferHandle Order => indices;

    /// <summary>How many passes the network runs for a capacity.</summary>
    /// <param name="capacity">The slot count, which need not be a power of two.</param>
    /// <returns>The number of step dispatches.</returns>
    /// <remarks>
    ///     <c>log₂(n)(log₂(n)+1)/2</c> — the triangular number, because the merge that finishes a
    ///     sequence of size <c>k</c> is one pass shorter than the one for <c>2k</c>. Exposed because
    ///     "seventy-eight dispatches" is the kind of cost a caller should be able to read rather than
    ///     discover in a capture.
    /// </remarks>
    public static int Passes(int capacity) {
        var stages = 0;

        for (var size = Rounded(capacity); size > 1; size >>= 1) {
            stages++;
        }

        return stages * (stages + 1) / 2;
    }

    /// <summary>Records the whole network: one seed dispatch and the compare-exchange passes.</summary>
    /// <param name="list">An open command list.</param>
    /// <param name="seed">The compiled <c>ParticleSortSeed</c> kernel.</param>
    /// <param name="step">The compiled <c>ParticleSortStep</c> kernel.</param>
    /// <param name="count">How many particles are alive.</param>
    /// <param name="camera">Where the camera is, for a depth key.</param>
    /// <exception cref="ArgumentNullException"><paramref name="list" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>A barrier between every pass, and it is not optional.</b> Each pass reads what the
    ///     previous one wrote at a distance the previous one chose, so two passes without a barrier
    ///     between them is a race whose symptom is an <i>almost</i> sorted array — which draws as
    ///     occasional particles blended in the wrong order, on some frames, on some drivers. There is
    ///     no cheaper synchronisation available: a dispatch boundary is the only barrier the whole
    ///     grid observes.
    /// </remarks>
    public void Record(
        ICommandList list,
        PipelineHandle seed,
        PipelineHandle step,
        int count,
        Vector3 camera = default
    ) {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(list);
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        Transition(list, ResourceState.ShaderWrite);

        var constants = new SeedConstants {
            ParticleCount = Math.Min(count, Capacity),
            CameraX = camera.X,
            CameraY = camera.Y,
            CameraZ = camera.Z,
            Mode = Mode == VfxSortMode.ByAge ? 1 : 0
        };

        list.BindPipeline(seed);
        list.BindDescriptorSet(DescriptorSetSlot.PerFrame, Bind());
        list.PushConstants(ShaderStage.Compute, 0, Raw(constants));
        list.Dispatch(Groups(Capacity));
        Dispatches++;

        list.BindPipeline(step);
        list.BindDescriptorSet(DescriptorSetSlot.PerFrame, stepSet);

        for (var k = 2u; k <= (uint) Capacity; k <<= 1) {
            for (var j = k >> 1; j > 0; j >>= 1) {
                Transition(list, ResourceState.ShaderWrite);
                list.PushConstants(ShaderStage.Compute, 0, Raw(new StepConstants { K = k, J = j }));
                list.Dispatch(Groups(Capacity));
                Dispatches++;
            }
        }

        state = ResourceState.ShaderWrite;
    }

    /// <summary>Records the copy that brings the order back, for a caller checking it.</summary>
    /// <param name="list">An open command list.</param>
    /// <exception cref="ArgumentNullException"><paramref name="list" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>For tests and for a debugger, and never for a frame.</b> A draw reads
    ///     <see cref="Order" /> on the device; reading it back is the stall the whole class exists to
    ///     remove. It is here because an order nothing can read is an order nothing can check.
    /// </remarks>
    public void Download(ICommandList list) {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(list);

        Transition(list, ResourceState.CopySource);
        list.CopyBuffer(indices, 0, readback, 0, Capacity * sizeof(uint));
        state = ResourceState.CopySource;
    }

    /// <summary>Reads a completed <see cref="Download" />.</summary>
    /// <param name="order">Filled with the particle slot at each position.</param>
    /// <exception cref="ArgumentException"><paramref name="order" /> is longer than the capacity.</exception>
    public void Read(Span<uint> order) {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (order.Length > Capacity) {
            throw new ArgumentException(
                $"The network covers {Capacity} slots and the span is {order.Length}.",
                nameof(order)
            );
        }

        var bytes = new byte[Capacity * sizeof(uint)];

        device.Read(readback, 0, bytes);

        for (var index = 0; index < order.Length; index++) {
            order[index] = BitConverter.ToUInt32(bytes, index * sizeof(uint));
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        device.Destroy(stepSet);

        foreach (var set in seedSets) {
            device.Destroy(set);
        }

        device.Destroy(StepLayout);
        device.Destroy(SeedLayout);
        device.Destroy(stepLayout);
        device.Destroy(seedLayout);
        device.Destroy(readback);
        device.Destroy(indices);
        device.Destroy(keys);
    }

    /// <summary>How many workgroups cover a run of slots.</summary>
    static int Groups(int count) =>
        count <= 0 ? 0 : ((count + VfxShader.WorkgroupSize - 1) / VfxShader.WorkgroupSize);

    /// <summary>The next power of two at or above a count, and at least two.</summary>
    static int Rounded(int capacity) {
        var size = 2;

        while (size < capacity) {
            size <<= 1;
        }

        return size;
    }

    /// <summary>The seed set naming the simulation's <i>current</i> attribute buffers.</summary>
    /// <remarks>
    ///     ⚠ Written on first sight of a generation and never rewritten. A reaping simulation has
    ///     exactly two, so the second call after the first reap claims the second set and every call
    ///     after that finds one — which is what keeps this off the path of updating a descriptor set
    ///     the device may still be reading from an earlier submission.
    /// </remarks>
    DescriptorSetHandle Bind() {
        // Both attribute buffers are bound whichever mode this is. A descriptor a shader declares
        // and does not read still has to be bound — an incomplete set is not bound at all — and the
        // one the mode does not use is the other attribute's buffer where the graph has it and the
        // used one's where it does not, because binding an invalid handle is what a validation layer
        // objects to rather than binding a buffer nothing reads.
        var position = simulation.Storage(VfxAttribute.Position);
        var age = simulation.Storage(VfxAttribute.Age);
        var source = Mode == VfxSortMode.ByAge ? age : position;

        position = position.IsValid ? position : source;
        age = age.IsValid ? age : source;

        for (var generation = 0; generation < seedSets.Length; generation++) {
            if (boundPosition[generation] == position && boundAge[generation] == age) {
                return seedSets[generation];
            }
        }

        for (var generation = 0; generation < seedSets.Length; generation++) {
            if (boundPosition[generation].IsValid) {
                continue;
            }

            boundPosition[generation] = position;
            boundAge[generation] = age;

            device.UpdateDescriptorSet(seedSets[generation], [
                DescriptorWrite.Storage(0, position),
                DescriptorWrite.Storage(1, age),
                DescriptorWrite.Storage(2, keys),
                DescriptorWrite.Storage(3, indices)
            ]);

            return seedSets[generation];
        }

        throw new InvalidOperationException(
            $"The simulation has produced more than {seedSets.Length} attribute generation(s), which is "
            + "more than it double-buffers. Either the sort is bound to a different simulation than the "
            + "one it was built from, or VfxGpuSimulation grew a third copy without this growing a third "
            + "seed set."
        );
    }

    void Transition(ICommandList list, ResourceState next) {
        barriers[0] = new(keys, state, next);
        barriers[1] = new(indices, state, next);

        list.Barrier(new(barriers, []));
    }

    static ReadOnlySpan<byte> Raw<T>(in T constants) where T : unmanaged =>
        MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in constants));
}
