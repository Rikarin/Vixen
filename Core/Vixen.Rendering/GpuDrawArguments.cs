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
    readonly DescriptorWrite[] writes = new DescriptorWrite[3];

    DrawCommand[] packed = [];
    BufferHandle commands;
    long capacity;

    // Whether the templates in `packed` have reached the device since the last Fill. A two-phase
    // frame dispatches twice over one set of templates, and uploading them again would advance the
    // upload ring a second time in one frame — which halves the ring's depth in exactly the way the
    // ring exists to prevent.
    bool staged;
    long templateOffset;

    // What the argument buffer was left in. The draw that reads it needs IndirectArgument, and the
    // dispatch that writes it needs ShaderWrite, so it changes twice a frame and never settles.
    ResourceState state = ResourceState.Undefined;

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
    }

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
    public long OffsetOf(int viewIndex, RenderObjectId id) =>
        (((long)viewIndex * ObjectCount) + id.Index) * Stride;

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

        if (Effects.Resolve(EffectKey.Of(GpuCulling.ArgumentsShaderName)) is not { IsPlaceholder: false } effect) {
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
        ring = descriptors.Length == 0 ? 0 : (ring + 1) % descriptors.Length;
        device.UpdateDescriptorSet(descriptors[ring], writes);

        list.Barrier(new([new(commands, state, ResourceState.ShaderWrite)], []));
        list.BindPipeline(pipeline);
        list.BindDescriptorSet(Slot, descriptors[ring]);

        list.Dispatch(
            Math.Max(1, (objectCount + GpuCulling.WorkgroupSize - 1) / GpuCulling.WorkgroupSize),
            viewCount
        );

        list.Barrier(new([new(commands, ResourceState.ShaderWrite, ResourceState.IndirectArgument)], []));
        state = ResourceState.IndirectArgument;

        IsFilled = true;
        return true;
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

        DestroySets();

        if (commands.IsValid) {
            device.Destroy(commands);
            commands = default;
        }

        templates.Dispose();
        packed = [];
        capacity = 0;
    }
}
