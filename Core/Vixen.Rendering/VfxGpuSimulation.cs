// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Graphics;
using Vixen.Vfx;

namespace Vixen.Rendering;

/// <summary>
///     The device-side half of a particle system: its storage, its descriptors, and its dispatches.
/// </summary>
/// <remarks>
///     <para>
///         <b>What this owns and what it does not.</b> It owns the buffers a
///         <see cref="VfxShaderEmitter" />-emitted shader binds, the descriptor set that names them,
///         and the pipeline layout the two kernels share. It does not own the pipelines: turning
///         Raven source into a module is the shader compiler's job, and a runtime that linked against
///         one would be a runtime that shipped a compiler. The caller compiles and hands the
///         <see cref="PipelineHandle" /> to <see cref="Initialize" /> and <see cref="Update" />.
///     </para>
///     <para>
///         <b>Why it lives here rather than in <c>Vixen.Vfx</c>.</b> The particle runtime is
///         device-free by design — it is testable, jobifiable and shippable without a graphics API,
///         and its README says so. This is the module that already owns an <see cref="IGraphicsDevice" />.
///         What crosses the line is the <i>layout</i>, and that stayed behind: this file asks
///         <see cref="VfxShaderPacking" /> what the bytes look like rather than knowing.
///     </para>
///     <para>
///         <b>Upload and download are stalls, and are meant to be rare.</b> A GPU effect exists so
///         that particle state never leaves the device; the two transfer paths are here for the two
///         cases where it must — seeding a system from a CPU spawn, and reading the result back to
///         compare it against the CPU backend, which is the only way to ask whether the two agree.
///         Neither belongs in a frame. The dispatches do, and they touch neither.
///     </para>
///     <para>
///         <b>The push constants are the whole per-dispatch state.</b> Twenty bytes written into the
///         command buffer, so an initialize and an update can be recorded into one list with
///         different values — which a uniform buffer could not do without being two buffers. See
///         <see cref="VfxShaderUniforms" />, whose field order is the shader's declaration order.
///     </para>
/// </remarks>
public sealed class VfxGpuSimulation : IDisposable {
    readonly IGraphicsDevice device;
    readonly VfxShaderBinding[] bindings;
    readonly BufferHandle[] storage;
    readonly long[] offsets;
    readonly byte[] scratch;

    // Rebuilt in place rather than allocated per call: a dispatch pair is a per-frame path, and this
    // is the only array in it.
    readonly BufferBarrier[] barriers;

    readonly BufferHandle staging;
    readonly BufferHandle readback;
    readonly DescriptorSetLayoutHandle setLayout;
    readonly DescriptorSetHandle descriptors;
    readonly PipelineLayoutHandle layout;

    // What the storage buffers were last left in, so a barrier can name a truthful `before`. One
    // state for all of them because every path here touches all of them together.
    ResourceState state = ResourceState.Undefined;
    bool disposed;

    /// <summary>Allocates the storage one emitted shader expects, and the descriptors naming it.</summary>
    /// <param name="device">The device.</param>
    /// <param name="shader">The emitted shader, for its bindings.</param>
    /// <param name="capacity">The most particles that can be alive at once.</param>
    /// <exception cref="ArgumentNullException"><paramref name="device" /> or <paramref name="shader" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity" /> is not positive.</exception>
    public VfxGpuSimulation(IGraphicsDevice device, VfxShader shader, int capacity) {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(shader);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        this.device = device;
        Capacity = capacity;

        bindings = [.. shader.Bindings];
        storage = new BufferHandle[bindings.Length];
        offsets = new long[bindings.Length];

        var total = 0L;

        for (var index = 0; index < bindings.Length; index++) {
            var binding = bindings[index];
            var bytes = VfxShaderPacking.Size(binding, capacity);

            offsets[index] = total;
            total += bytes;

            storage[index] = device.CreateBuffer(new(
                bytes,
                BufferUsage.Storage | BufferUsage.CopySource | BufferUsage.CopyDestination,
                MemoryAccess.DeviceLocal,
                $"{shader.Name}.{binding.Name}"
            ));
        }

        Bytes = total;
        scratch = new byte[total];
        barriers = new BufferBarrier[bindings.Length];

        // One staging buffer and one readback buffer covering every attribute, rather than a pair
        // each. A transfer moves all of them or none of them, and one buffer means one copy list and
        // one host write instead of a walk of small ones.
        staging = device.CreateBuffer(
            new(total, BufferUsage.CopySource, MemoryAccess.HostUpload, $"{shader.Name}.Staging")
        );

        readback = device.CreateBuffer(
            new(total, BufferUsage.CopyDestination, MemoryAccess.HostReadback, $"{shader.Name}.Readback")
        );

        // Set 0, because the emitted bindings are [PerFrame] — see VfxShaderEmitter, which explains
        // why a compute pipeline with nothing else in it does not want the material set.
        var entries = new DescriptorBinding[bindings.Length];

        for (var index = 0; index < bindings.Length; index++) {
            entries[index] = new((uint)index, DescriptorKind.StorageBuffer, ShaderStage.Compute);
        }

        setLayout = device.CreateDescriptorSetLayout(
            new(DescriptorSetSlot.PerFrame, entries, $"{shader.Name}.Particles")
        );

        layout = device.CreatePipelineLayout(new(
            [setLayout],
            [new(ShaderStage.Compute, 0, VfxShaderUniforms.Size)],
            $"{shader.Name}.Layout"
        ));

        descriptors = device.CreateDescriptorSet(setLayout, $"{shader.Name}.Particles");

        var writes = new DescriptorWrite[bindings.Length];

        for (var index = 0; index < bindings.Length; index++) {
            writes[index] = DescriptorWrite.Storage((uint)index, storage[index]);
        }

        device.UpdateDescriptorSet(descriptors, writes);
    }

    /// <summary>The most particles that can be alive at once.</summary>
    public int Capacity { get; }

    /// <summary>How much device storage the attributes occupy, in bytes.</summary>
    public long Bytes { get; }

    /// <summary>The buffers, in the order the shader declares them.</summary>
    public IReadOnlyList<VfxShaderBinding> Bindings => bindings;

    /// <summary>The layout both kernels are created against.</summary>
    public PipelineLayoutHandle Layout => layout;

    /// <summary>The set holding every attribute buffer.</summary>
    public DescriptorSetHandle Descriptors => descriptors;

    /// <summary>The buffer holding one attribute, for a renderer that wants to draw from it.</summary>
    /// <param name="attribute">Which attribute.</param>
    /// <returns>Its buffer, or an invalid handle when the shader binds no buffer for it.</returns>
    /// <remarks>
    ///     An invalid handle rather than a throw, because "this graph does not have that attribute"
    ///     is a normal answer — a graph that never rotates its particles has no rotation buffer, and
    ///     a renderer that would have read one can skip it far more usefully than a frame can catch
    ///     an exception.
    /// </remarks>
    public BufferHandle Storage(VfxAttribute attribute) {
        for (var index = 0; index < bindings.Length; index++) {
            if (bindings[index].Slot < 0 && bindings[index].Attribute == attribute) {
                return storage[index];
            }
        }

        return default;
    }

    /// <summary>The buffer holding one custom attribute.</summary>
    /// <param name="slot">Which slot the graph gave it.</param>
    /// <returns>Its buffer, or an invalid handle when nothing in the graph touches that slot.</returns>
    public BufferHandle Custom(int slot) {
        for (var index = 0; index < bindings.Length; index++) {
            if (bindings[index].Slot == slot) {
                return storage[index];
            }
        }

        return default;
    }

    /// <summary>How many workgroups cover a run of particles.</summary>
    public static int Groups(int count) =>
        count <= 0 ? 0 : ((count + VfxShader.WorkgroupSize - 1) / VfxShader.WorkgroupSize);

    /// <summary>Records the copies that put a CPU buffer's particles on the device.</summary>
    /// <param name="list">An open command list.</param>
    /// <param name="particles">Where the particles are.</param>
    /// <param name="count">How many, from the start of the buffer.</param>
    /// <exception cref="ArgumentNullException"><paramref name="list" /> or <paramref name="particles" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count" /> is negative or above the capacity.</exception>
    /// <remarks>
    ///     The host write happens now and the copies happen when the list runs, so the staging buffer
    ///     must not be written again until this submission has completed. That is the usual shape of
    ///     a one-shot transfer and the reason this is not something to do every frame.
    /// </remarks>
    public void Upload(ICommandList list, ParticleBuffer particles, int count) {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(particles);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, Capacity);

        if (count == 0) {
            return;
        }

        for (var index = 0; index < bindings.Length; index++) {
            var bytes = VfxShaderPacking.Size(bindings[index], count);

            VfxShaderPacking.Pack(particles, bindings[index], count, scratch.AsSpan((int)offsets[index], bytes));
        }

        device.Write(staging, 0, scratch);
        Transition(list, ResourceState.CopyDestination);

        for (var index = 0; index < bindings.Length; index++) {
            list.CopyBuffer(staging, offsets[index], storage[index], 0, VfxShaderPacking.Size(bindings[index], count));
        }

        state = ResourceState.CopyDestination;
    }

    /// <summary>Records the initializer dispatch over one run of newly spawned particles.</summary>
    /// <param name="list">An open command list.</param>
    /// <param name="pipeline">The compiled initialize kernel.</param>
    /// <param name="first">The first particle it touches.</param>
    /// <param name="count">How many it touches.</param>
    /// <param name="seed">The system instance's seed.</param>
    /// <param name="time">How long the system has been running, in seconds.</param>
    /// <exception cref="ArgumentNullException"><paramref name="list" /> is null.</exception>
    /// <remarks>
    ///     The step is zero, which is what the CPU backend passes an updater that appears in the
    ///     initializer list. The two backends have to agree about what a birth is, and this is where
    ///     that agreement is spelled.
    /// </remarks>
    public void Initialize(ICommandList list, PipelineHandle pipeline, int first, int count, uint seed, float time) =>
        Dispatch(list, pipeline, new() { DeltaTime = 0f, Seed = seed, First = first, ParticleCount = count, Time = time });

    /// <summary>Records the update dispatch over every live particle.</summary>
    /// <param name="list">An open command list.</param>
    /// <param name="pipeline">The compiled update kernel.</param>
    /// <param name="count">How many particles are alive.</param>
    /// <param name="deltaTime">The step, in seconds.</param>
    /// <param name="seed">The system instance's seed.</param>
    /// <param name="time">How long the system has been running at the <i>start</i> of this step.</param>
    /// <exception cref="ArgumentNullException"><paramref name="list" /> is null.</exception>
    public void Update(ICommandList list, PipelineHandle pipeline, int count, float deltaTime, uint seed, float time) =>
        Dispatch(list, pipeline, new() { DeltaTime = deltaTime, Seed = seed, First = 0, ParticleCount = count, Time = time });

    /// <summary>Records the copies that bring the device's particles back.</summary>
    /// <param name="list">An open command list.</param>
    /// <param name="count">How many particles, from the start of the buffer.</param>
    /// <exception cref="ArgumentNullException"><paramref name="list" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count" /> is negative or above the capacity.</exception>
    /// <remarks>
    ///     Pairs with <see cref="Read" />, which is the half that has to happen after the submission
    ///     has completed. Split rather than combined because only the caller knows when that is.
    /// </remarks>
    public void Download(ICommandList list, int count) {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(list);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, Capacity);

        if (count == 0) {
            return;
        }

        Transition(list, ResourceState.CopySource);

        for (var index = 0; index < bindings.Length; index++) {
            list.CopyBuffer(storage[index], 0, readback, offsets[index], VfxShaderPacking.Size(bindings[index], count));
        }

        state = ResourceState.CopySource;
    }

    /// <summary>Puts a completed <see cref="Download" /> into a CPU buffer.</summary>
    /// <param name="particles">Where to put them. Its count is not changed.</param>
    /// <param name="count">How many, and the same number <see cref="Download" /> was given.</param>
    /// <exception cref="ArgumentNullException"><paramref name="particles" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count" /> is negative or above the capacity.</exception>
    public void Read(ParticleBuffer particles, int count) {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(particles);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, Capacity);

        if (count == 0) {
            return;
        }

        device.Read(readback, 0, scratch);

        for (var index = 0; index < bindings.Length; index++) {
            var bytes = VfxShaderPacking.Size(bindings[index], count);

            VfxShaderPacking.Unpack(scratch.AsSpan((int)offsets[index], bytes), bindings[index], count, particles);
        }
    }

    /// <summary>Frees every device object it made.</summary>
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        device.Destroy(descriptors);
        device.Destroy(layout);
        device.Destroy(setLayout);
        device.Destroy(readback);
        device.Destroy(staging);

        foreach (var buffer in storage) {
            device.Destroy(buffer);
        }
    }

    void Dispatch(ICommandList list, PipelineHandle pipeline, VfxShaderUniforms constants) {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(list);

        if (constants.ParticleCount <= 0) {
            return;
        }

        Transition(list, ResourceState.ShaderWrite);

        list.BindPipeline(pipeline);
        list.BindDescriptorSet(DescriptorSetSlot.PerFrame, descriptors);
        list.PushConstants(ShaderStage.Compute, 0, Raw(constants));
        list.Dispatch(Groups(constants.ParticleCount));

        // Left in ShaderWrite, so a second dispatch — an initialize followed by an update, the usual
        // pair — gets a barrier between them rather than reading what the first had not finished
        // writing. Two dispatches with no barrier is the bug this exists to make impossible.
        state = ResourceState.ShaderWrite;
    }

    /// <summary>Barriers every attribute buffer into a state, from whatever it was left in.</summary>
    void Transition(ICommandList list, ResourceState next) {
        for (var index = 0; index < storage.Length; index++) {
            barriers[index] = new(storage[index], state, next);
        }

        list.Barrier(new(barriers, []));
    }

    static ReadOnlySpan<byte> Raw(in VfxShaderUniforms constants) =>
        MemoryMarshal.AsBytes(new ReadOnlySpan<VfxShaderUniforms>(in constants))[..VfxShaderUniforms.Size];
}
