// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
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
    /// <summary>How many bytes a <c>DrawIndexedIndirect</c> command occupies.</summary>
    /// <remarks>
    ///     Five <c>uint</c>s — index count, instance count, first index, vertex offset, first
    ///     instance — and the same twenty <see cref="ICommandList.DrawIndexedIndirect" /> defaults its
    ///     stride to.
    /// </remarks>
    public const int DrawArgumentsSize = 20;

    readonly IGraphicsDevice device;
    readonly VfxShaderBinding[] bindings;
    readonly VfxShaderBinding[] particles;

    // Two full sets of the particle attributes, and which one is the live set flips on every reap.
    // See `Reap`: a compaction cannot be done in place, so the alternative to a second set is
    // copying the whole of it back every frame.
    readonly BufferHandle[][] sets;
    readonly DescriptorSetHandle[] descriptorSets;
    int current;

    readonly long[] offsets;
    readonly byte[] scratch;

    // Rebuilt in place rather than allocated per call: a dispatch pair is a per-frame path, and this
    // is the only array in it.
    readonly BufferBarrier[] barriers;

    readonly BufferHandle staging;
    readonly BufferHandle readback;

    // Only when the shader reaps. An invalid handle is what "this effect has no lifetime" looks like
    // everywhere below, which is one test rather than a parallel `hasReap` flag to keep in step.
    readonly BufferHandle counter;
    readonly BufferHandle zero;
    readonly BufferHandle counterReadback;
    readonly BufferHandle template;
    readonly BufferHandle arguments;

    readonly DescriptorSetLayoutHandle setLayout;
    readonly PipelineLayoutHandle layout;

    // What the storage buffers were last left in, so a barrier can name a truthful `before`. One
    // state for all of them because every path here touches all of them together.
    ResourceState state = ResourceState.Undefined;
    ResourceState argumentState = ResourceState.Undefined;
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
        HasReap = shader.HasReap;

        bindings = [.. shader.Bindings];
        particles = [.. bindings.Where(binding => binding.Role == VfxBindingRole.Particle)];
        offsets = new long[particles.Length];

        var total = 0L;

        for (var index = 0; index < particles.Length; index++) {
            offsets[index] = total;
            total += VfxShaderPacking.Size(particles[index], capacity);
        }

        Bytes = total;
        scratch = new byte[total];

        // ⚠ **A second full set of the attribute buffers when the shader reaps, and it is the whole
        // memory cost of GPU compaction.** A survivor claims its destination with an atomic, so two
        // invocations can be given slots in either order and writing into the buffer being read
        // would let one overwrite a particle the other has not looked at yet. The alternative to a
        // second set is copying the whole live set back after every reap, which is the same
        // bandwidth every frame instead of the same memory once.
        var copies = shader.HasReap ? 2 : 1;

        sets = new BufferHandle[copies][];

        for (var copy = 0; copy < copies; copy++) {
            sets[copy] = new BufferHandle[particles.Length];

            for (var index = 0; index < particles.Length; index++) {
                sets[copy][index] = device.CreateBuffer(new(
                    VfxShaderPacking.Size(particles[index], capacity),
                    BufferUsage.Storage | BufferUsage.CopySource | BufferUsage.CopyDestination,
                    MemoryAccess.DeviceLocal,
                    $"{shader.Name}.{particles[index].Name}{(copy == 0 ? "" : "B")}"
                ));
            }
        }

        // One staging buffer and one readback buffer covering every attribute, rather than a pair
        // each. A transfer moves all of them or none of them, and one buffer means one copy list and
        // one host write instead of a walk of small ones.
        staging = device.CreateBuffer(
            new(total, BufferUsage.CopySource, MemoryAccess.HostUpload, $"{shader.Name}.Staging")
        );

        readback = device.CreateBuffer(
            new(total, BufferUsage.CopyDestination, MemoryAccess.HostReadback, $"{shader.Name}.Readback")
        );

        if (shader.HasReap) {
            counter = device.CreateBuffer(new(
                sizeof(uint),
                BufferUsage.Storage | BufferUsage.CopySource | BufferUsage.CopyDestination,
                MemoryAccess.DeviceLocal,
                $"{shader.Name}.Survivors"
            ));

            // ⚠ A four-byte upload buffer holding zero, written once here and copied over the counter
            // before every reap. `Vixen.Graphics` has no fill, and a host write during recording is
            // the hazard `Upload` documents — so the zero lives on the device and the clear is a copy.
            zero = device.CreateBuffer(
                new(sizeof(uint), BufferUsage.CopySource, MemoryAccess.HostUpload, $"{shader.Name}.Zero")
            );

            device.Write(zero, 0, new byte[sizeof(uint)]);

            counterReadback = device.CreateBuffer(new(
                sizeof(uint),
                BufferUsage.CopyDestination,
                MemoryAccess.HostReadback,
                $"{shader.Name}.SurvivorsReadback"
            ));

            template = device.CreateBuffer(new(
                DrawArgumentsSize,
                BufferUsage.CopySource,
                MemoryAccess.HostUpload,
                $"{shader.Name}.DrawTemplate"
            ));

            // ⚠ `CopySource` as well, which a draw does not need. A command a device assembles and
            // nothing can read is a command nobody can check: the gate that says this buffer really
            // did get the reap's count copies it back, and so would anyone debugging a frame that
            // drew the wrong number of particles. Twenty bytes, and the usage costs nothing else.
            arguments = device.CreateBuffer(new(
                DrawArgumentsSize,
                BufferUsage.Indirect | BufferUsage.CopyDestination | BufferUsage.CopySource,
                MemoryAccess.DeviceLocal,
                $"{shader.Name}.DrawArguments"
            ));

            IndicesPerParticle = 6;
        }

        barriers = new BufferBarrier[(particles.Length * copies) + (shader.HasReap ? 1 : 0)];

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

        // ⚠ **One descriptor set per direction, rather than one set rewritten between frames.**
        // Updating a set names buffers a submission in flight is still reading, and the two sets
        // differ only in which way round they are — so a reap is a flip of an index rather than a
        // device call. The emitted binding order is source, then out, then the counter, which is the
        // order `VfxShaderEmitter.Emit` assembles them in.
        descriptorSets = new DescriptorSetHandle[copies];

        for (var copy = 0; copy < copies; copy++) {
            descriptorSets[copy] = device.CreateDescriptorSet(setLayout, $"{shader.Name}.Particles{copy}");

            var writes = new DescriptorWrite[bindings.Length];

            for (var index = 0; index < bindings.Length; index++) {
                writes[index] = DescriptorWrite.Storage((uint)index, bindings[index].Role switch {
                    VfxBindingRole.Particle => sets[copy][index],
                    VfxBindingRole.Compacted => sets[1 - copy][index - particles.Length],
                    _ => counter
                });
            }

            device.UpdateDescriptorSet(descriptorSets[copy], writes);
        }
    }

    /// <summary>The most particles that can be alive at once.</summary>
    public int Capacity { get; }

    /// <summary>How much one set of the attributes occupies, in bytes.</summary>
    /// <remarks>
    ///     One set, which is also the size of a transfer. A reaping effect holds two of them — see
    ///     <see cref="HasReap" /> — so what it costs on the device is twice this plus the counter.
    /// </remarks>
    public long Bytes { get; }

    /// <summary>Whether the shader has a reap kernel, and so whether this has the storage for one.</summary>
    public bool HasReap { get; }

    /// <summary>How many compute dispatches this has recorded since it was built.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Counted because "the GPU path ran" and "the GPU path was constructed" look identical
    ///         from everywhere else.</b> A host that builds one of these, never records a dispatch and
    ///         draws the CPU expansion produces exactly the frame a working device path produces, at
    ///         exactly the cost — and there is no validation error, no log line and no counter to tell
    ///         the two apart. This is that counter.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Recorded, not executed.</b> It rises when a dispatch is written into a command
    ///         list, which is before the list is submitted and long before the device has run it — so
    ///         it answers "did this path get as far as asking" rather than "did the device finish".
    ///         The honest reading is the one this class can actually make: a zero here is proof of a
    ///         backend nothing drove.
    ///     </para>
    ///     <para>
    ///         A dispatch over no particles is not recorded and does not count, which is
    ///         <see cref="Dispatch" />'s own early return rather than a special case here.
    ///     </para>
    /// </remarks>
    public int Dispatches { get; private set; }

    /// <summary>The buffers, in the order the shader declares them.</summary>
    public IReadOnlyList<VfxShaderBinding> Bindings => bindings;

    /// <summary>The layout every kernel is created against.</summary>
    public PipelineLayoutHandle Layout => layout;

    /// <summary>The set naming the live buffers as the source and the spare as the destination.</summary>
    /// <remarks>
    ///     ⚠ <b>Changes when <see cref="Reap" /> flips the sets</b>, so anything that cached it across
    ///     a reap would bind the buffers the wrong way round — every dispatch reading last frame's
    ///     survivors and writing into the set the draw is about to read.
    /// </remarks>
    public DescriptorSetHandle Descriptors => descriptorSets[current];

    /// <summary>The indirect draw command <see cref="WriteDrawArguments" /> keeps up to date.</summary>
    /// <remarks>
    ///     Invalid for an effect that does not reap, because the count it exists to carry is the one
    ///     the reap produces — an effect whose particles never die knows how many it has without
    ///     asking a device.
    /// </remarks>
    public BufferHandle DrawArguments => arguments;

    /// <summary>How many indices one particle's geometry uses.</summary>
    /// <remarks>
    ///     Six, for the two triangles of a billboard — <c>VfxGeometryBuilder</c>'s four vertices
    ///     share an edge. Settable because a mesh renderer's is its mesh's. ⚠ Written to a host
    ///     buffer, so set it between frames rather than while a list that reads it is recording.
    /// </remarks>
    public int IndicesPerParticle {
        get => indicesPerParticle;

        set {
            ArgumentOutOfRangeException.ThrowIfNegative(value);

            indicesPerParticle = value;

            if (!template.IsValid) {
                return;
            }

            // indexCount, instanceCount, firstIndex, vertexOffset, firstInstance. The instance count
            // is left at zero and overwritten from the counter, which is the one field a device
            // decides.
            Span<uint> command = [(uint)value, 0u, 0u, 0u, 0u];

            device.Write(template, 0, MemoryMarshal.AsBytes(command));
        }
    }

    int indicesPerParticle;

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
        for (var index = 0; index < particles.Length; index++) {
            if (particles[index].Slot < 0 && particles[index].Attribute == attribute) {
                return sets[current][index];
            }
        }

        return default;
    }

    /// <summary>The buffer holding one custom attribute.</summary>
    /// <param name="slot">Which slot the graph gave it.</param>
    /// <returns>Its buffer, or an invalid handle when nothing in the graph touches that slot.</returns>
    public BufferHandle Custom(int slot) {
        for (var index = 0; index < particles.Length; index++) {
            if (particles[index].Slot == slot) {
                return sets[current][index];
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

        for (var index = 0; index < this.particles.Length; index++) {
            var bytes = VfxShaderPacking.Size(this.particles[index], count);

            VfxShaderPacking.Pack(particles, this.particles[index], count, scratch.AsSpan((int)offsets[index], bytes));
        }

        device.Write(staging, 0, scratch);
        Transition(list, ResourceState.CopyDestination);

        for (var index = 0; index < this.particles.Length; index++) {
            list.CopyBuffer(
                staging,
                offsets[index],
                sets[current][index],
                0,
                VfxShaderPacking.Size(this.particles[index], count)
            );
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
    /// <param name="origin">
    ///     Where the emitter is — <c>VfxSystem.Origin</c>'s counterpart. Added to every
    ///     position an initializer writes, and read here rather than in <see cref="Update" /> for the
    ///     reason that property gives: a particle is born where the emitter is and then lives in world
    ///     space.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="list" /> is null.</exception>
    /// <remarks>
    ///     The step is zero, which is what the CPU backend passes an updater that appears in the
    ///     initializer list. The two backends have to agree about what a birth is, and this is where
    ///     that agreement is spelled.
    /// </remarks>
    public void Initialize(
        ICommandList list,
        PipelineHandle pipeline,
        int first,
        int count,
        uint seed,
        float time,
        Vector3 origin = default
    ) =>
        Dispatch(
            list,
            pipeline,
            new() {
                DeltaTime = 0f, Seed = seed, First = first, ParticleCount = count, Time = time, Origin = origin
            }
        );

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

        for (var index = 0; index < particles.Length; index++) {
            list.CopyBuffer(
                sets[current][index],
                0,
                readback,
                offsets[index],
                VfxShaderPacking.Size(particles[index], count)
            );
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

        for (var index = 0; index < this.particles.Length; index++) {
            var bytes = VfxShaderPacking.Size(this.particles[index], count);

            VfxShaderPacking.Unpack(
                scratch.AsSpan((int)offsets[index], bytes),
                this.particles[index],
                count,
                particles
            );
        }
    }

    /// <summary>Records the compaction: every survivor claims a slot, and the dead leave no hole.</summary>
    /// <param name="list">An open command list.</param>
    /// <param name="pipeline">The compiled reap kernel.</param>
    /// <param name="count">How many particles are alive going in.</param>
    /// <exception cref="ArgumentNullException"><paramref name="list" /> is null.</exception>
    /// <exception cref="InvalidOperationException">The shader has no reap kernel.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>What was owed after the dispatch pair: the last thing the CPU still did for a device
    ///         effect.</b> The kernels age a particle and stop; this is what removes the finished ones
    ///         without the state leaving the device. The counter is zeroed by a copy first — an
    ///         <c>atomicAdd</c> onto last frame's count appends past the end of the buffer, which is
    ///         the failure <c>DrawArguments.rvn</c> warns about in the same words.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The live set flips, so <see cref="Descriptors" /> and <see cref="Storage" /> mean
    ///         something different after this returns.</b> Nothing is copied back: the survivors are
    ///         in the set that was the spare, and it becomes the live one. A renderer that cached a
    ///         buffer handle across a reap draws the particles as they were before it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The survivors come out in an order neither backend promises and the two do not
    ///         share.</b> The CPU fills each hole from the tail; here a slot is whatever the atomic
    ///         handed back, which depends on how the invocations interleaved and is not reproducible
    ///         between two runs of one frame. A particle's randomness follows its identifier rather
    ///         than its slot exactly so that this cannot matter — which is what
    ///         <c>VfxRandom</c> was built for, spent here.
    ///     </para>
    ///     <para>
    ///         The count is copied to a readback buffer in the same list, so <see cref="ReadSurvivors" />
    ///         has something to read once the submission completes. Four bytes, and it is what lets a
    ///         host that is not drawing indirectly still know how many it has.
    ///     </para>
    /// </remarks>
    public void Reap(ICommandList list, PipelineHandle pipeline, int count) {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(list);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, Capacity);

        if (!HasReap) {
            throw new InvalidOperationException(
                "This effect's shader has no reap kernel, so there is nothing to compact and no buffers to "
                + "compact into. A graph needs both an age and a lifetime before a particle can be finished — "
                + "see VfxShader.HasReap."
            );
        }

        if (count == 0) {
            return;
        }

        Transition(list, ResourceState.CopyDestination);
        list.CopyBuffer(zero, 0, counter, 0, sizeof(uint));

        Transition(list, ResourceState.ShaderWrite);

        list.BindPipeline(pipeline);
        list.BindDescriptorSet(DescriptorSetSlot.PerFrame, descriptorSets[current]);
        list.PushConstants(
            ShaderStage.Compute,
            0,
            Raw(new() { DeltaTime = 0f, Seed = 0u, First = 0, ParticleCount = count, Time = 0f })
        );

        list.Dispatch(Groups(count));
        Dispatches++;

        state = ResourceState.ShaderWrite;

        Transition(list, ResourceState.CopySource);
        list.CopyBuffer(counter, 0, counterReadback, 0, sizeof(uint));
        state = ResourceState.CopySource;

        current = 1 - current;
    }

    /// <summary>How many particles the last <see cref="Reap" /> kept.</summary>
    /// <returns>The survivor count.</returns>
    /// <exception cref="InvalidOperationException">The shader has no reap kernel.</exception>
    /// <remarks>
    ///     Pairs with <see cref="Reap" /> the way <see cref="Read" /> pairs with
    ///     <see cref="Download" />: the submission has to have completed, and only the caller knows
    ///     when. ⚠ A host drawing through <see cref="DrawArguments" /> does not need this at all,
    ///     which is the point of the indirect path — reading a count back is a stall, and the whole
    ///     reason for putting the compaction on the device was to stop having one.
    /// </remarks>
    public int ReadSurvivors() {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (!HasReap) {
            throw new InvalidOperationException("This effect's shader has no reap kernel, so nothing counted.");
        }

        Span<byte> bytes = stackalloc byte[sizeof(uint)];

        device.Read(counterReadback, 0, bytes);

        return (int)BitConverter.ToUInt32(bytes);
    }

    /// <summary>Records the copies that put the survivor count into the indirect draw command.</summary>
    /// <param name="list">An open command list.</param>
    /// <exception cref="ArgumentNullException"><paramref name="list" /> is null.</exception>
    /// <exception cref="InvalidOperationException">The shader has no reap kernel.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>Two copies and no dispatch, which is the whole of it.</b> The four fixed fields of
    ///         a <c>DrawIndexedIndirect</c> command are a host-side template written once; the fifth
    ///         — the instance count — is four bytes the reap already produced. A compute kernel to
    ///         assemble a command that is one <c>uint</c> away from a constant would be a dispatch,
    ///         a barrier and a pipeline to compile for a memcpy.
    ///     </para>
    ///     <para>
    ///         Call it after <see cref="Reap" /> and before the pass that draws. The buffer is left in
    ///         <see cref="ResourceState.IndirectArgument" />, which is what
    ///         <see cref="ICommandList.DrawIndexedIndirect" /> reads it in.
    ///     </para>
    /// </remarks>
    public void WriteDrawArguments(ICommandList list) {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(list);

        if (!HasReap) {
            throw new InvalidOperationException(
                "This effect's shader has no reap kernel, so there is no device-side count to draw from. An "
                + "effect whose particles never die knows how many it has without asking."
            );
        }

        list.Barrier(new([new(arguments, argumentState, ResourceState.CopyDestination)], []));

        list.CopyBuffer(template, 0, arguments, 0, DrawArgumentsSize);

        // The counter is in CopySource from the end of `Reap`, which is where a copy out of it wants
        // it — so the only barrier this needs is the argument buffer's own.
        list.CopyBuffer(counter, 0, arguments, sizeof(uint), sizeof(uint));

        list.Barrier(new([new(arguments, ResourceState.CopyDestination, ResourceState.IndirectArgument)], []));
        argumentState = ResourceState.IndirectArgument;
    }

    /// <summary>Frees every device object it made.</summary>
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        foreach (var set in descriptorSets) {
            device.Destroy(set);
        }

        device.Destroy(layout);
        device.Destroy(setLayout);
        device.Destroy(readback);
        device.Destroy(staging);

        foreach (var set in sets) {
            foreach (var buffer in set) {
                device.Destroy(buffer);
            }
        }

        if (!HasReap) {
            return;
        }

        device.Destroy(arguments);
        device.Destroy(template);
        device.Destroy(counterReadback);
        device.Destroy(zero);
        device.Destroy(counter);
    }

    void Dispatch(ICommandList list, PipelineHandle pipeline, VfxShaderUniforms constants) {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(list);

        if (constants.ParticleCount <= 0) {
            return;
        }

        Transition(list, ResourceState.ShaderWrite);

        list.BindPipeline(pipeline);
        list.BindDescriptorSet(DescriptorSetSlot.PerFrame, descriptorSets[current]);
        list.PushConstants(ShaderStage.Compute, 0, Raw(constants));
        list.Dispatch(Groups(constants.ParticleCount));
        Dispatches++;

        // Left in ShaderWrite, so a second dispatch — an initialize followed by an update, the usual
        // pair — gets a barrier between them rather than reading what the first had not finished
        // writing. Two dispatches with no barrier is the bug this exists to make impossible.
        state = ResourceState.ShaderWrite;
    }

    /// <summary>Barriers every attribute buffer into a state, from whatever it was left in.</summary>
    /// <remarks>
    ///     ⚠ <b>Both sets and the counter, not only the live one.</b> A reap reads one set and writes
    ///     the other in a single dispatch, so a barrier naming only the live set would leave the
    ///     destination in whatever the <i>previous</i> reap left it — and the hazard between the two
    ///     dispatches is exactly the one this exists to declare. One state for all of them because no
    ///     path here touches them separately.
    /// </remarks>
    void Transition(ICommandList list, ResourceState next) {
        var at = 0;

        foreach (var set in sets) {
            foreach (var buffer in set) {
                barriers[at++] = new(buffer, state, next);
            }
        }

        if (HasReap) {
            barriers[at] = new(counter, state, next);
        }

        list.Barrier(new(barriers, []));
    }

    static ReadOnlySpan<byte> Raw(in VfxShaderUniforms constants) =>
        MemoryMarshal.AsBytes(new ReadOnlySpan<VfxShaderUniforms>(in constants))[..VfxShaderUniforms.Size];
}
