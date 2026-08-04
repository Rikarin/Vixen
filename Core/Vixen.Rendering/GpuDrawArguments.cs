// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vixen.Graphics;
using Vixen.Shaders;
using Vixen.Shaders.Generated;

namespace Vixen.Rendering;

/// <summary>
///     One indexed draw, in the layout every API's indirect argument buffer expects.
/// </summary>
/// <remarks>
///     The field order is <c>vkCmdDrawIndexedIndirect</c>'s and D3D12's, and it is not ours to
///     choose: the GPU's command processor reads these bytes directly. Twenty bytes, which is
///     <see cref="ICommandList.DrawIndexedIndirect" />'s default stride and
///     <see cref="GpuDrawArguments.Stride" />.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct DrawCommand {
    /// <summary>How many indices to draw.</summary>
    public uint IndexCount;

    /// <summary>How many instances. Zero is what culling writes, and draws nothing.</summary>
    public uint InstanceCount;

    /// <summary>The first index into the index buffer.</summary>
    public uint FirstIndex;

    /// <summary>Added to every index before the vertex is fetched.</summary>
    public uint VertexOffset;

    /// <summary>The instance the draw starts at, which the shader reads as its base.</summary>
    public uint FirstInstance;
}

/// <summary>
///     The visibility bits turned into draw calls on the device, so the answer never has to come back
///     to the host.
/// </summary>
/// <remarks>
///     <para>
///         The last part of [docs/plan/06]'s GPU culling line: "output an indirect draw buffer". Its
///         inputs are <see cref="GpuVisibilityGroup.Bits" /> — which never left the device — and a
///         template per object that the host filled during <c>Prepare</c>; its output is a buffer a
///         draw call reads its own arguments out of.
///     </para>
///     <para>
///         <strong>It zeroes instance counts rather than compacting.</strong> The textbook form
///         appends survivors to a list and draws that list. Claiming a slot needs an atomic add,
///         which Raven has — so what stands in the way is the <em>draw</em>, in two independent
///         ways. A single command covers a compacted run only if its count comes from the device,
///         and <see cref="ICommandList.DrawIndexedIndirect" /> takes <c>drawCount</c> as a host
///         integer. And a single command covers several objects only if they share their bindings,
///         which they do not: <see cref="Features.MeshRenderFeature" /> binds a vertex buffer, an
///         index buffer and a material set per object, so a compacted list would be a list nothing
///         could draw in one call.
///     </para>
///     <para>
///         Bindless materials are what change the second of those, and are the prerequisite
///         [docs/plan/14] records against compaction. Until then the buffer holds one record per
///         object slot at that slot's own index and a culled object gets zero instances, which every
///         API defines as a draw that fetches and rasterises nothing. The cost is a command submitted
///         per object rather than per visible object; the saving is the whole round trip.
///     </para>
///     <para>
///         <strong>A record per object per view</strong>, because the bits are: a shadow cascade and
///         the camera disagree about what is visible, and one buffer between them would hold whatever
///         was dispatched last. Twenty bytes each, so a hundred thousand objects across six views is
///         twelve megabytes — the number a project trades against, and the reason this is opt-in
///         rather than always on.
///     </para>
///     <para>
///         <strong>Where it goes next.</strong> With this in place the host does not need the bits at
///         all — see <see cref="GpuVisibilityGroup.ReadBack" />, which turns off the stall and leaves
///         the CPU recording every object while the GPU decides which of them draw anything.
///     </para>
/// </remarks>
public sealed class GpuDrawArguments : IDisposable {
    /// <summary>How many bytes one draw's arguments occupy.</summary>
    public const int Stride = 20;

    readonly IGraphicsDevice device;
    readonly UploadBuffer<DrawCommand> templates = new("DrawArguments.Templates");
    readonly UploadBuffer<uint> batching = new("DrawArguments.Batching");
    readonly DescriptorWrite[] writes = new DescriptorWrite[6];

    DrawCommand[] packed = [];
    uint[] batchOf = [];
    int[] batchSizes = [];
    uint[] batchBases = [];

    BufferHandle commands;
    BufferHandle counts;
    BufferHandle zeros;
    long capacity;
    long countCapacity;

    long batchOffset;
    long baseOffset;

    // Whether the templates in `packed` have reached the device since the last Fill. A two-phase
    // frame dispatches twice over one set of templates, and uploading them again would advance the
    // upload ring a second time in one frame — which halves the ring's depth in exactly the way the
    // ring exists to prevent.
    bool staged;
    long templateOffset;

    // What the argument buffer was left in. The draw that reads it needs IndirectArgument, and the
    // dispatch that writes it needs ShaderWrite, so it changes twice a frame and never settles.
    ResourceState state = ResourceState.Undefined;
    ResourceState countState = ResourceState.Undefined;

    // One set per frame in flight, advanced with the frame. A set a submitted command buffer still
    // references may not be written — VUID-vkUpdateDescriptorSets-None-03047 — and the no-readback
    // path waits for nothing, so the set this frame rewrites has to be one an earlier frame used and
    // the device has finished with. The same invariant DescriptorAllocator and UploadBuffer are
    // built on, and the reason both of those exist rather than one set and one region.
    DescriptorSetHandle[] descriptors = [];
    int ring;

    // From the reflection checked in beside the shader, not from a name looked up at run time —
    // see GpuCulling.ShaderName for why the generated keys are the source of both.
    const DescriptorSetSlot Slot = (DescriptorSetSlot)DrawArgumentsKeys.TemplatesSet;

    Effect? allocatedFor;
    bool disposed;

    /// <summary>Creates an argument buffer on a device. Nothing is allocated until the first update.</summary>
    /// <param name="device">The device.</param>
    /// <exception cref="ArgumentNullException"><paramref name="device" /> is null.</exception>
    public GpuDrawArguments(IGraphicsDevice device) {
        ArgumentNullException.ThrowIfNull(device);

        this.device = device;
        templates.Device = device;
        batching.Device = device;
    }

    /// <summary>
    ///     Whether survivors are appended to a per-batch list rather than left at their own slot with
    ///     a zeroed instance count.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Step 4 of <c>docs/plan/23-bindless-materials.md</c>, and the payoff for everything before it. A
    ///         padded buffer costs one command per <em>candidate</em> object, whatever the culling
    ///         decided; a compacted one costs one command per <em>batch</em>, because
    ///         <see cref="ICommandList.DrawIndexedIndirectCount" /> reads how many survivors there
    ///         were out of a buffer the host never looks at.
    ///     </para>
    ///     <para>
    ///         ⚠ Needs <see cref="GraphicsDeviceFeatures.HasDrawIndirectCount" />, and turning it on
    ///         without one is a compacted list nothing can draw the right number of. <see cref="Update" />
    ///         checks and falls back rather than refusing, because the decision is a device fact and a
    ///         host that has to branch on it everywhere would branch on it wrongly somewhere.
    ///     </para>
    /// </remarks>
    public bool Compact { get; set; }

    /// <summary>Where the compaction variant is resolved from. Null updates nothing.</summary>
    public EffectSystem? Effects { get; set; }

    /// <summary>Where the compute pipeline comes from. Null updates nothing.</summary>
    public ComputePipelineCache? Pipelines { get; set; }

    /// <summary>
    ///     How many times a frame <see cref="Update" /> is called, which is what the set ring has to
    ///     be deep enough for.
    /// </summary>
    /// <remarks>
    ///     One for an ordinary frame; two for a two-phase cull, whose second dispatch turns the late
    ///     difference into the late draws. The same argument as
    ///     <see cref="HiZPyramid.BuildsPerFrame" />: two updates in one frame are two rewrites of a
    ///     set before a single submission, which sizing the ring to frames in flight alone does not
    ///     cover.
    /// </remarks>
    public int DispatchesPerFrame { get; set; } = 1;

    /// <summary>The arguments a draw call reads. Invalid before the first update.</summary>
    public BufferHandle Commands => commands;

    /// <summary>How many object slots the last update covered.</summary>
    public int ObjectCount { get; private set; }

    /// <summary>How many views it covered.</summary>
    public int ViewCount { get; private set; }

    /// <summary>Whether the buffer holds this frame's arguments.</summary>
    /// <remarks>
    ///     False until an update has run, and the thing a feature has to ask before drawing
    ///     indirectly: a draw reading arguments nobody wrote is a draw of whatever the allocator left
    ///     in that memory, which is a hang on some drivers and a triangle through the world on others.
    /// </remarks>
    public bool IsFilled { get; private set; }

    /// <summary>Where one object's arguments start, for one view.</summary>
    /// <param name="viewIndex">Which view.</param>
    /// <param name="id">Which object.</param>
    /// <remarks>Meaningless when <see cref="IsCompacted" />: a survivor's record is not at its own
    /// slot, which is the entire point.</remarks>
    public long OffsetOf(int viewIndex, RenderObjectId id) =>
        (((long)viewIndex * ObjectCount) + id.Index) * Stride;

    /// <summary>Whether the last update wrote a compacted list rather than a padded one.</summary>
    /// <remarks>
    ///     Distinct from <see cref="Compact" />, which is what a host asked for. This is what
    ///     happened — false where the device has no count-buffer draw, and false in a frame that did
    ///     not dispatch. A draw loop must read this rather than the request, because reading a
    ///     compacted list as a padded one draws every object's arguments at every other object's
    ///     slot.
    /// </remarks>
    public bool IsCompacted { get; private set; }

    /// <summary>The per-batch survivor counts a count-buffer draw reads.</summary>
    public BufferHandle Counts => counts;

    /// <summary>How many batches the last update covered.</summary>
    public int BatchCount { get; private set; }

    /// <summary>Which batch an object was put in, or <c>0</c> for one nothing assigned.</summary>
    public int BatchOf(RenderObjectId id) =>
        id.Index >= 0 && id.Index < batchOf.Length ? (int)batchOf[id.Index] : 0;

    /// <summary>
    ///     How many objects are in a batch — the ceiling a count-buffer draw is given.
    /// </summary>
    /// <remarks>
    ///     ⚠ Candidates, not survivors. Survivors are what the count buffer says and only the device
    ///     knows; this is how long the region is, which is what <c>maxDrawCount</c> means. It is also
    ///     what a caller compares its run length against before drawing a whole batch at once — see
    ///     <c>MeshRenderFeature</c>.
    /// </remarks>
    public int BatchSizeOf(int batch) =>
        batch >= 0 && batch < batchSizes.Length ? batchSizes[batch] : 0;

    /// <summary>Where a batch's compacted run starts, for one view.</summary>
    public long BatchOffsetOf(int viewIndex, int batch) =>
        (((long)viewIndex * ObjectCount) + (batch >= 0 && batch < batchBases.Length ? batchBases[batch] : 0)) * Stride;

    /// <summary>Where a batch's survivor count is, for one view.</summary>
    public long CountOffsetOf(int viewIndex, int batch) =>
        (((long)viewIndex * BatchCount) + batch) * sizeof(uint);

    /// <summary>
    ///     One batch id per object slot, to be filled before an update.
    /// </summary>
    /// <param name="objectCount">How many slots the store holds.</param>
    /// <returns>A span to write into, cleared to zero.</returns>
    /// <remarks>
    ///     <para>
    ///         A batch is objects that bind everything alike — same pipeline, same geometry, same
    ///         material set — which is a fact about the renderer rather than about geometry, so the
    ///         source says and this only lays them out. Ids need not be dense or ordered; what
    ///         matters is that two objects share one exactly when their draws could be merged.
    ///     </para>
    ///     <para>
    ///         Zero is a real batch and the default, so an object nobody assigned joins batch zero
    ///         with everything else nobody assigned — which is correct when they genuinely bind
    ///         alike and is why a source is expected to fill every slot it owns.
    ///     </para>
    /// </remarks>
    public Span<uint> Batches(int objectCount) {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(objectCount);

        if (batchOf.Length < objectCount) {
            Array.Resize(ref batchOf, Math.Max(objectCount, Math.Max(batchOf.Length * 2, 256)));
        }

        var span = batchOf.AsSpan(0, objectCount);
        span.Clear();

        return span;
    }

    /// <summary>
    ///     The templates to fill, one per object slot, before an update.
    /// </summary>
    /// <param name="objectCount">How many slots the store holds.</param>
    /// <returns>A span to write into, cleared to zero — which is a draw of nothing.</returns>
    /// <remarks>
    ///     Cleared rather than left alone, so a slot nothing fills is a draw of no indices rather
    ///     than the last frame's mesh at the wrong index. The caller fills what it knows about: the
    ///     mesh feature fills every drawable object, and everything else stays a draw of nothing.
    /// </remarks>
    public Span<DrawCommand> Fill(int objectCount) {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(objectCount);

        if (packed.Length < objectCount) {
            Array.Resize(ref packed, Math.Max(objectCount, Math.Max(packed.Length * 2, 256)));
        }

        var span = packed.AsSpan(0, objectCount);
        span.Clear();
        staged = false;

        return span;
    }

    /// <summary>
    ///     Records the dispatch that writes this frame's arguments.
    /// </summary>
    /// <param name="list">An open command list, outside a render pass.</param>
    /// <param name="visibility">The device-side bits, from <see cref="GpuVisibilityGroup.Bits" />.</param>
    /// <param name="viewCount">How many views the bits cover.</param>
    /// <param name="objectCount">How many object slots, and how many templates <see cref="Fill" /> was given.</param>
    /// <returns>False when there is nothing to dispatch with, in which case nothing was recorded.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="list" /> is null.</exception>
    /// <remarks>
    ///     The buffer is left in <see cref="ResourceState.IndirectArgument" />, which is what the
    ///     draw that reads it needs — the barrier between the dispatch and the draw is here rather
    ///     than at the draw, because this is the side that knows the write happened.
    /// </remarks>
    public bool Update(ICommandList list, BufferHandle visibility, int viewCount, int objectCount) {
        ArgumentNullException.ThrowIfNull(list);
        ObjectDisposedException.ThrowIf(disposed, this);

        IsFilled = false;

        if (Effects is null
            || Pipelines is null
            || !visibility.IsValid
            || viewCount <= 0
            || objectCount <= 0
            || !GpuCulling.IsSupported(device)) {
            return false;
        }

        // What a host asked for, narrowed to what the device can draw. Falling back rather than
        // refusing, because the capability is a machine fact: a host that had to branch on it at
        // every call site would branch on it wrongly at one of them, and the wrong branch here is a
        // compacted list read as a padded one — every object drawn with another's arguments.
        var compacting = Compact && device.Features.HasDrawIndirectCount;

        // With the default fillers, for GpuCulling.Key's reason: a whole-library compiler refuses
        // a key that leaves the material chain's slots unbound, and this dispatch composes nothing.
        var key = EffectKey.Of(
            GpuCulling.ArgumentsShaderName,
            [new(DrawArgumentsKeys.Compact.Name, compacting ? "true" : "false")],
            Materials.MaterialCompiler.PassComposition()
        );

        if (Effects.Resolve(key) is not { IsPlaceholder: false } effect) {
            return false;
        }

        var pipeline = Pipelines.GetOrCreate(effect);

        if (!pipeline.IsValid || !TryAllocateSet(effect) || !EnsureCommands((long)viewCount * objectCount * Stride)) {
            return false;
        }

        if (packed.Length < objectCount) {
            throw new InvalidOperationException(
                $"Update was given {objectCount} objects and Fill was last given {packed.Length}. The "
                + "templates are what this pass edits one field of, so a frame that dispatched without "
                + "them would draw every object with whatever the previous frame left — call Fill "
                + "first, and let the sources write into what it returns."
            );
        }

        ObjectCount = objectCount;
        ViewCount = viewCount;

        // Once per Fill, not once per dispatch. The templates are what the host would have drawn and
        // do not change between a frame's two phases — only the bits they are filtered by do.
        if (!staged) {
            templates.Begin();
            templates.Add(packed.AsSpan(0, objectCount));
            templates.Upload();

            templateOffset = templates.Offset;
            staged = true;
        }

        writes[0] = DescriptorWrite.Storage(
            DrawArgumentsKeys.TemplatesBinding,
            templates.Buffer,
            templateOffset,
            (long)objectCount * Unsafe.SizeOf<DrawCommand>()
        );

        // The exact word count, because the shader derives the stride between views from the object
        // count and would read the next view's words if this said "the rest of the buffer".
        writes[1] = DescriptorWrite.Storage(
            DrawArgumentsKeys.VisibilityBinding,
            visibility,
            0,
            GpuCulling.BufferSize(viewCount, GpuCulling.WordsFor(objectCount))
        );

        writes[2] = DescriptorWrite.Storage(DrawArgumentsKeys.CommandsBinding, commands, 0, (long)viewCount * objectCount * Stride);

        // The batch layout, whether or not this variant reads it. A binding is a declared field and
        // survives its last reader folding away — the same fact `[MaterialIndex("…")]` turns on — so
        // the padded variant declares these three too and a set short of them is a validation error
        // rather than an unused slot.
        if (!Layout(objectCount, viewCount)) {
            return false;
        }

        writes[3] = DescriptorWrite.Storage(
            DrawArgumentsKeys.BatchesBinding,
            batching.Buffer,
            batchOffset,
            (long)objectCount * sizeof(uint)
        );

        writes[4] = DescriptorWrite.Storage(
            DrawArgumentsKeys.BasesBinding,
            batching.Buffer,
            baseOffset,
            (long)BatchCount * sizeof(uint)
        );

        writes[5] = DescriptorWrite.Storage(
            DrawArgumentsKeys.CountsBinding,
            counts,
            0,
            (long)viewCount * BatchCount * sizeof(uint)
        );

        ring = descriptors.Length == 0 ? 0 : (ring + 1) % descriptors.Length;
        device.UpdateDescriptorSet(descriptors[ring], writes);

        // ⚠ Before the dispatch, every frame, and on the device. An atomicAdd onto last frame's
        // count appends past the end of a batch's run and into the next batch's — which draws one
        // batch's geometry with another's arguments, and does so only in the frames where something
        // became invisible. Copied from a buffer of zeros rather than written from the host, because
        // a host write into a buffer an unfinished frame may still be reading is the hazard the
        // whole upload ring exists for and a constant source has none of it.
        if (compacting) {
            list.Barrier(new([new(counts, countState, ResourceState.CopyDestination)], []));
            list.CopyBuffer(zeros, 0, counts, 0, (long)viewCount * BatchCount * sizeof(uint));
            list.Barrier(new([new(counts, ResourceState.CopyDestination, ResourceState.ShaderWrite)], []));
            countState = ResourceState.ShaderWrite;
        }

        list.Barrier(new([new(commands, state, ResourceState.ShaderWrite)], []));
        list.BindPipeline(pipeline);
        list.BindDescriptorSet(Slot, descriptors[ring]);

        list.Dispatch(
            Math.Max(1, (objectCount + GpuCulling.WorkgroupSize - 1) / GpuCulling.WorkgroupSize),
            viewCount
        );

        list.Barrier(new([new(commands, ResourceState.ShaderWrite, ResourceState.IndirectArgument)], []));
        state = ResourceState.IndirectArgument;

        // The counts are read as indirect arguments too — by the *count* parameter of the draw
        // rather than by its arguments — so they need the same transition and would otherwise be
        // read while the dispatch that wrote them was still running.
        if (compacting) {
            list.Barrier(new([new(counts, ResourceState.ShaderWrite, ResourceState.IndirectArgument)], []));
            countState = ResourceState.IndirectArgument;
        }

        IsCompacted = compacting;
        IsFilled = true;
        return true;
    }

    /// <summary>
    ///     Turns the batch ids into a run per batch, and puts both on the device.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A histogram and a prefix sum. Batches partition the objects, so the runs partition a
    ///         view's region and the commands buffer is exactly the size the padded form needed —
    ///         compaction costs an atomic and no memory.
    ///     </para>
    ///     <para>
    ///         Ids are the source's and need not be dense, so the count is one past the largest.
    ///         A sparse id space costs an empty run and nothing else, which is far cheaper than
    ///         making every source renumber.
    ///     </para>
    /// </remarks>
    bool Layout(int objectCount, int viewCount) {
        if (batchOf.Length < objectCount) {
            Batches(objectCount);
        }

        var highest = 0u;

        for (var index = 0; index < objectCount; index++) {
            highest = Math.Max(highest, batchOf[index]);
        }

        BatchCount = (int)highest + 1;

        if (batchSizes.Length < BatchCount) {
            Array.Resize(ref batchSizes, Math.Max(BatchCount, batchSizes.Length * 2));
            Array.Resize(ref batchBases, batchSizes.Length);
        }

        Array.Clear(batchSizes, 0, BatchCount);

        for (var index = 0; index < objectCount; index++) {
            batchSizes[batchOf[index]]++;
        }

        var running = 0u;

        for (var batch = 0; batch < BatchCount; batch++) {
            batchBases[batch] = running;
            running += (uint)batchSizes[batch];
        }

        // One upload holding both, at two offsets. Two UploadBuffers would be two rings advancing at
        // the same rate for two arrays written in the same breath.
        batching.Begin();
        batchOffset = batching.Offset;
        batching.Add(batchOf.AsSpan(0, objectCount));
        baseOffset = batching.Offset + ((long)objectCount * sizeof(uint));
        batching.Add(batchBases.AsSpan(0, BatchCount));
        batching.Upload();

        batchOffset = batching.Offset;
        baseOffset = batchOffset + ((long)objectCount * sizeof(uint));

        return EnsureCounts((long)viewCount * BatchCount * sizeof(uint));
    }

    /// <summary>The counts buffer and the buffer of zeros that clears it.</summary>
    bool EnsureCounts(long bytes) {
        if (counts.IsValid && bytes <= countCapacity) {
            return true;
        }

        if (counts.IsValid) {
            device.Destroy(counts);
        }

        if (zeros.IsValid) {
            device.Destroy(zeros);
        }

        countCapacity = Math.Max(bytes, Math.Max(countCapacity * 2, 256));

        counts = device.CreateBuffer(
            new(
                countCapacity,
                BufferUsage.Storage | BufferUsage.Indirect | BufferUsage.CopyDestination | BufferUsage.CopySource,
                MemoryAccess.DeviceLocal,
                "DrawArguments.Counts"
            )
        );

        // Written once and never again, which is what makes it safe to copy from every frame with no
        // ring behind it: nothing the host does to this buffer can race a frame still reading it,
        // because the host does nothing to it after this line.
        zeros = device.CreateBuffer(
            new(countCapacity, BufferUsage.CopySource, MemoryAccess.HostUpload, "DrawArguments.Zeros")
        );

        if (zeros.IsValid) {
            device.Write(zeros, 0, new byte[countCapacity]);
        }

        countState = ResourceState.Undefined;
        return counts.IsValid && zeros.IsValid;
    }

    bool TryAllocateSet(Effect effect) {
        var slots = Math.Max(1, device.FramesInFlight) * Math.Max(1, DispatchesPerFrame);

        if (ReferenceEquals(allocatedFor, effect) && descriptors.Length == slots) {
            return true;
        }

        if ((int)Slot >= effect.SetLayouts.Length || !effect.SetLayouts[(int)Slot].IsValid) {
            return false;
        }

        DestroySets();
        descriptors = new DescriptorSetHandle[slots];

        for (var index = 0; index < slots; index++) {
            descriptors[index] = device.CreateDescriptorSet(effect.SetLayouts[(int)Slot], "DrawArguments");

            if (!descriptors[index].IsValid) {
                return false;
            }
        }

        ring = 0;
        allocatedFor = effect;

        return true;
    }

    void DestroySets() {
        foreach (var set in descriptors) {
            if (set.IsValid) {
                device.Destroy(set);
            }
        }

        descriptors = [];
        allocatedFor = null;
    }

    bool EnsureCommands(long bytes) {
        if (commands.IsValid && bytes <= capacity) {
            return true;
        }

        if (commands.IsValid) {
            device.Destroy(commands);
        }

        capacity = Math.Max(bytes, capacity * 2);

        // Copyable as well as drawable, for the same reason the visibility bits are: what the GPU
        // decided is otherwise unobservable, and a decision nothing can read is a decision nothing
        // can test.
        commands = device.CreateBuffer(
            new(
                capacity,
                BufferUsage.Storage | BufferUsage.Indirect | BufferUsage.CopySource,
                MemoryAccess.DeviceLocal,
                "DrawArguments"
            )
        );

        state = ResourceState.Undefined;
        return commands.IsValid;
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        IsFilled = false;
        IsCompacted = false;

        DestroySets();

        if (commands.IsValid) {
            device.Destroy(commands);
            commands = default;
        }

        if (counts.IsValid) {
            device.Destroy(counts);
            counts = default;
        }

        if (zeros.IsValid) {
            device.Destroy(zeros);
            zeros = default;
        }

        templates.Dispose();
        batching.Dispose();

        packed = [];
        batchOf = [];
        batchSizes = [];
        batchBases = [];
        capacity = 0;
        countCapacity = 0;
    }
}
