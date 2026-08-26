// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Graphics;
using Vixen.Rendering.Ecs;
using Vixen.Rendering.Materials;
using Vixen.Shaders;
using Vixen.Shaders.Generated;

namespace Vixen.Rendering.Features;

/// <summary>
///     Blend shapes in the frame: a vertex buffer per morphed instance, and the pre-pass that fills it.
/// </summary>
/// <remarks>
///     <para>
///         <b>The wiring <c>MorphKernel</c> and <c>Pipeline/MorphScatter.rvn</c> were built for and
///         nothing had.</b> Those two are the arithmetic, checked against each other on a device; this
///         is what allocates the buffer, copies the rest pose into it, dispatches once per active
///         shape and points <see cref="MeshDraw.VertexBuffer" /> at the result. Without it a mesh with
///         twenty blend shapes imported, stored, packed and dispatched-for still drew at rest, because
///         nothing in a frame path had a buffer for the answer to go in.
///     </para>
///     <para>
///         <b>The seam is <see cref="MeshDraw" />, and that is what makes the passes agree.</b> A draw
///         record is per render object and every stage reads the same array, so overwriting one
///         object's handle morphs its shading pass, its shadow pass, its velocity pass and its depth
///         pre-pass together — by construction rather than by four features remembering to. That is
///         <a href="../../docs/plan/33-character-creator.md">doc 33</a> § D4's whole argument for a
///         pre-pass, and it only holds because the handle is written in one place.
///     </para>
///     <para>
///         ⚠ <b>The vertices are per instance and the deltas are per mesh.</b> Two characters wearing
///         the same head share one entry run — 1.28 MB for a twenty-shape face, resident, as
///         <see cref="MorphTargetData.SizeInBytes" /> reports — and have a vertex range each, because
///         their weights differ. So the cost of a second instance is the mesh's vertices again and not
///         its shapes: forty-eight bytes a vertex, which is why <see cref="VertexCapacity" /> is a
///         budget the host sets rather than a number here.
///     </para>
///     <para>
///         ⚠ <b>The rest pose is copied out of the scene's own geometry buffer, not held twice.</b>
///         <see cref="Source" /> already has the mesh at
///         <see cref="GeometrySlice.BaseVertex" /> and a device-to-device copy is what restores it, so
///         the only extra vertex memory is the destination. What that costs instead is a state
///         transition on a buffer the whole scene draws from — see
///         <see cref="GeometryBuffer.Transition" /> for why it is that type's to make and not this
///         one's.
///     </para>
///     <para>
///         ⚠ <b>A vertex range is copied and dispatched only when its weights have changed, and the
///         first frame counts as a change.</b> A character standing still costs nothing; a character
///         that has never been recorded costs a copy, because a dispatch onto an uninitialised range
///         does not look like a morph gone wrong — it looks like the geometry is missing. Which is
///         also why <see cref="Attach" /> marks the instance dirty rather than leaving the flag at its
///         zero.
///     </para>
/// </remarks>
public sealed class MorphRenderFeature : SubRenderFeature, IDisposable {
    /// <summary>How many floats one <see cref="SurfaceVertex" /> occupies.</summary>
    /// <remarks>
    ///     Derived rather than written down: the kernel addresses the stream as bare floats — see
    ///     <c>MorphScatter.rvn</c> on why a <c>float3</c>'s std430 alignment makes a struct the wrong
    ///     way to say this — and the stride it wants is this buffer's, which is the vertex the scene
    ///     is drawn with.
    /// </remarks>
    public static int VertexFloats => SurfaceVertex.SizeInBytes / sizeof(float);

    /// <summary>Where the position is within a vertex, in floats.</summary>
    public const int PositionFloat = 0;

    /// <summary>Where the normal is within a vertex, in floats.</summary>
    public const int NormalFloat = 3;

    /// <summary>How many invocations one workgroup of the kernel has.</summary>
    /// <remarks><c>MorphScatter.rvn</c>'s <c>[ComputeShader(64)]</c>, and a dispatch rounds up to it.</remarks>
    public const int GroupSize = 64;

    /// <summary>
    ///     How many bytes one dispatch's uniform block occupies in the ring.
    /// </summary>
    /// <remarks>
    ///     Two hundred and fifty-six, where the block is thirty-two — <see cref="UploadBuffer{T}" />'s
    ///     constant and its reason: it is the largest <c>minUniformBufferOffsetAlignment</c> any
    ///     desktop or mobile device reports, and a binding offset that is not a multiple of it is a
    ///     validation error rather than a slow path.
    /// </remarks>
    const int BlockStride = 256;

    readonly IGraphicsDevice device;

    // ⚠ Null until something with blend shapes is attached, and that is the whole reason the two are
    // fields rather than readonly. The default capacities are a face's — 65 536 vertices and 262 144
    // entries — which is seven megabytes of device memory, and a project that never imports a morphed
    // mesh would otherwise pay all of it for a feature `WorldRenderer` constructs unconditionally.
    // Constructing it unconditionally is right; allocating for it is not.
    MorphArena? vertices;
    MorphArena? entries;

    readonly Dictionary<GeometryKey, Shapes> shapes = [];
    readonly Dictionary<int, Instance> instances = [];
    readonly List<Instance> dirty = [];
    readonly List<Dispatch> dispatches = [];

    byte[] staged = [];
    BufferHandle blocks;
    int blockSlots;
    int ring;
    bool registered;
    bool disposed;

    /// <summary>Creates the feature. The buffers wait until something with shapes is attached.</summary>
    /// <param name="device">The device the morphed vertices will live on.</param>
    /// <param name="vertexCapacity">How many morphed vertices the buffer holds, over every instance.</param>
    /// <param name="entryCapacity">How many blend-shape entries it holds, over every mesh.</param>
    /// <exception cref="ArgumentNullException"><paramref name="device" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>Fixed, and an instance that does not fit draws at rest rather than growing it.</b>
    ///     <see cref="GeometryBuffer" /> makes the argument at length and it is the same one: every
    ///     <see cref="MeshDraw" /> already attached holds the handle, so growing would mean finding
    ///     and rewriting all of them or leaving draws pointing at a destroyed buffer.
    ///     <see cref="Dropped" /> is what says a scene asked for more than was reserved, which
    ///     otherwise looks like a character whose face stopped animating.
    /// </remarks>
    public MorphRenderFeature(IGraphicsDevice device, int vertexCapacity = 1 << 16, int entryCapacity = 1 << 18) {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(vertexCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(entryCapacity);

        this.device = device;

        VertexCapacity = vertexCapacity;
        EntryCapacity = entryCapacity;
    }

    /// <inheritdoc />
    public override string Name => "Morphing";

    /// <summary>Where each object's morphed vertices are, and which shapes move them.</summary>
    public RenderDataKey<MorphInstance> Instances { get; private set; }

    /// <summary>Where variants are compiled. Unset, nothing is dispatched.</summary>
    public EffectSystem? Effects { get; set; }

    /// <summary>Where the compute pipeline comes from. Unset, nothing is dispatched.</summary>
    public ComputePipelineCache? Pipelines { get; set; }

    /// <summary>Where a dispatch's descriptor set comes from. Unset, nothing is dispatched.</summary>
    /// <remarks>
    ///     The frame's allocator rather than one of this feature's own, because the number of sets a
    ///     frame needs is the number of <em>active shapes over morphed instances</em> — which changes
    ///     every frame a character opens its mouth. A fixed pool sized for the worst case would be
    ///     sized for a face nobody is making.
    /// </remarks>
    public DescriptorAllocator? Descriptors { get; set; }

    /// <summary>The buffer the rest pose is copied out of — the scene's shared geometry.</summary>
    /// <remarks>
    ///     ⚠ <b>Unset, nothing morphs at all</b>, and it is not an error: a host that has not wired
    ///     this has no shared geometry buffer either, so there is nothing to copy from and nothing has
    ///     been attached. <see cref="Degraded" /> says so on a host that attached and then lost it.
    /// </remarks>
    public GeometryBuffer? Source { get; set; }

    /// <summary>The buffer the morphed vertices live in, or invalid until something has attached.</summary>
    public BufferHandle Buffer => vertices?.Buffer ?? default;

    /// <summary>How many morphed vertices the buffer holds.</summary>
    public int VertexCapacity { get; }

    /// <summary>How many blend-shape entries it holds.</summary>
    public int EntryCapacity { get; }

    /// <summary>How many distinct meshes have their shapes resident.</summary>
    public int MeshCount => shapes.Count;

    /// <summary>How many objects are being morphed.</summary>
    public int InstanceCount => instances.Count;

    /// <summary>How many vertices the attached instances occupy.</summary>
    public int UsedVertices => vertices?.Used ?? 0;

    /// <summary>How many entries the resident meshes occupy.</summary>
    public int UsedEntries => entries?.Used ?? 0;

    /// <summary>How many dispatches the last <see cref="Record" /> put in the frame.</summary>
    /// <remarks>
    ///     One per <em>active</em> shape of each instance whose weights moved — not one per instance,
    ///     and not one per shape. Zero on a frame where every morphed character held still, which is
    ///     the number that says the dirty check is doing something.
    /// </remarks>
    public int Dispatches { get; private set; }

    /// <summary>How many vertex ranges the last <see cref="Record" /> restored to the rest pose.</summary>
    public int Copies { get; private set; }

    /// <summary>How many attachments were refused for want of room, since this feature was made.</summary>
    /// <remarks>
    ///     A vertex range that did not fit, an entry run that did not, or a mesh's deltas that could
    ///     not be staged in the frame that asked. All three draw the mesh at rest — a correct picture
    ///     of a face that never moves — so this is the only thing that says a scene asked for more than
    ///     was reserved. <see cref="MeshExtractionSystem.Dropped" /> counts the same shape of failure
    ///     one layer up.
    /// </remarks>
    public int Dropped { get; private set; }

    /// <summary>
    ///     Why this frame morphed less than it was asked to, or null.
    /// </summary>
    /// <remarks>
    ///     <see cref="RootRenderFeature.Degraded" />'s shape, one layer down again, and set on both
    ///     paths every <see cref="Record" /> so that recovering is as visible as degrading. A frame
    ///     that cannot resolve the kernel draws every attached mesh at whatever its buffer last held —
    ///     which is the rest pose on the first frame and a stale expression afterwards, and neither
    ///     reads as a missing shader.
    /// </remarks>
    public string? Degraded { get; private set; }

    /// <inheritdoc />
    protected internal override void Initialize(RenderSystem system) {
        ArgumentNullException.ThrowIfNull(system);

        Instances = system.Objects.Data.Register<MorphInstance>();
        registered = true;
    }

    /// <summary>
    ///     Gives an object its own morphed copy of a mesh, and points its draw at it.
    /// </summary>
    /// <param name="system">The render system whose store the object is in.</param>
    /// <param name="id">The object.</param>
    /// <param name="key">Which mesh, as the residency cache keys it.</param>
    /// <param name="mesh">The mesh, for its shapes. Read only on the first instance of it.</param>
    /// <param name="rest">Where the unmorphed mesh is in <see cref="Source" />.</param>
    /// <param name="draw">The draw record, whose vertex buffer and offset are rewritten on success.</param>
    /// <returns>Whether the object is now morphed.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>False is the ordinary answer and means "draw it at rest".</b> A mesh with no blend
    ///         shapes, a host with no <see cref="Source" />, or a buffer with no room left — all three
    ///         leave <paramref name="draw" /> exactly as it was, which is a correct picture of an
    ///         unmorphed mesh. Only the third is a problem, and <see cref="Dropped" /> counts it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The index buffer is not touched, and must not be.</b> A morph moves vertices and
    ///         never renumbers them, so the object keeps drawing the scene buffer's indices — which is
    ///         also why <see cref="MeshDraw.VertexOffset" /> has to be rewritten in the same breath as
    ///         the handle: an index is relative to it, and a copy at a different base with the old
    ///         offset draws some other mesh's vertices in this one's shape.
    ///     </para>
    /// </remarks>
    public bool Attach(
        RenderSystem system,
        RenderObjectId id,
        GeometryKey key,
        MeshData mesh,
        in GeometrySlice rest,
        ref MeshDraw draw
    ) {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(mesh);
        ObjectDisposedException.ThrowIf(disposed, this);

        // ⚠ Loudly, because the alternative is silent corruption of another feature's data. A
        // RenderDataKey is a slot index and its default is zero, so a feature that was never added to
        // a root feature would write its records over whichever array was registered first — which for
        // a mesh feature is the draws, and a MorphInstance read as a MeshDraw is a draw call built out
        // of a vertex count.
        if (!registered) {
            throw new InvalidOperationException(
                "This MorphRenderFeature has not been added to a root feature, so it has registered no "
                + "per-object array and its key names somebody else's. Call RootRenderFeature.Add "
                + "before attaching anything to it."
            );
        }

        if (!mesh.IsMorphed || Source is null || !rest.IsValid) {
            return false;
        }

        Allocate();

        if (!Resident(key, mesh, out var resident)) {
            return false;
        }

        if (!vertices!.TryAllocate(rest.VertexCount, out var baseVertex)) {
            // The mesh's entries stay resident: another instance of it may well fit, and dropping
            // them here would re-upload the same 1.28 MB the next time one did.
            Dropped++;
            return false;
        }

        resident.Claims++;

        instances[id.Index] = new() {
            Id = id,
            Key = key,
            Shapes = resident,
            BaseVertex = baseVertex,
            Rest = rest,
            Weights = new float[resident.Targets.Length],

            // ⚠ Dirty from the start, and this is the field whose zero is a wrong picture. Nothing has
            // copied the rest pose in yet, so an instance that reached its first Record clean would be
            // drawn out of whatever the allocator left in its range.
            Dirty = true
        };

        system.Objects.Data.Data(Instances)[id.Index] = new(baseVertex, rest.VertexCount, resident.Targets.Length);

        draw.VertexBuffer = vertices!.Buffer;
        draw.VertexOffset = baseVertex;

        return true;
    }

    /// <summary>Gives an object this frame's blend-shape weights.</summary>
    /// <param name="id">The object.</param>
    /// <param name="weights">One per target. Shorter is read as zero for the rest.</param>
    /// <returns>Whether the object is morphed at all.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>Idempotent on purpose.</b> Setting the weights an instance already has costs a
    ///         comparison and leaves it clean, so a system that writes every character's weights every
    ///         frame — which is what an animation system does — pays for the characters that moved.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Every weight is applied, including a negative one.</b> An exporter that authored a
    ///         shape as the inverse of its neighbour relies on it; <see cref="MorphKernel.Apply" />
    ///         says the same and the two must agree, because the device test compares them.
    ///     </para>
    /// </remarks>
    public bool SetWeights(RenderObjectId id, ReadOnlySpan<float> weights) {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (!instances.TryGetValue(id.Index, out var instance)) {
            return false;
        }

        var stored = instance.Weights;

        for (var index = 0; index < stored.Length; index++) {
            var weight = index < weights.Length ? weights[index] : 0f;

            if (stored[index] != weight) {
                stored[index] = weight;
                instance.Dirty = true;
            }
        }

        return true;
    }

    /// <summary>What an object's weights currently are.</summary>
    /// <param name="id">The object.</param>
    /// <returns>Its weights, or empty when it is not morphed.</returns>
    public ReadOnlySpan<float> WeightsOf(RenderObjectId id) =>
        instances.TryGetValue(id.Index, out var instance) ? instance.Weights : [];

    /// <summary>What an object's mesh calls each of its weight slots.</summary>
    /// <param name="id">The object.</param>
    /// <returns>The shape names, in slot order, or empty when it is not morphed.</returns>
    /// <remarks>
    ///     ⚠ <b>This is the only authoritative answer to "which slot is <c>jawOpen</c>".</b> An
    ///     animation names a shape and this component is addressed by slot, and the ordinal a source
    ///     file used is not the ordinal <c>MeshData.MorphTargets</c> ended up with — the import drops
    ///     a shape that moves nothing above the threshold. The feature was handed the targets the
    ///     mesh actually carries, so it is the one thing that has seen both ends.
    /// </remarks>
    public ReadOnlySpan<string> ShapesOf(RenderObjectId id) {
        if (!instances.TryGetValue(id.Index, out var instance)) {
            return [];
        }

        var targets = instance.Shapes.Targets;
        var names = new string[targets.Length];

        for (var index = 0; index < targets.Length; index++) {
            names[index] = targets[index].Name;
        }

        return names;
    }

    /// <summary>Gives up an object's morphed range, and its mesh's shapes with the last instance.</summary>
    /// <param name="id">The object.</param>
    /// <returns>Whether it had one.</returns>
    /// <remarks>
    ///     ⚠ <b>The draw record is not put back</b>, because by the time an object is forgotten it is
    ///     being removed from the store — and a record rewritten here would be rewritten on a slot the
    ///     next object to appear is about to take.
    /// </remarks>
    public bool Forget(RenderObjectId id) {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (!instances.Remove(id.Index, out var instance)) {
            return false;
        }

        vertices!.Free(instance.BaseVertex, instance.Rest.VertexCount);

        if (--instance.Shapes.Claims <= 0) {
            entries!.Free(instance.Shapes.FirstEntry, instance.Shapes.EntryCount);
            shapes.Remove(instance.Key);
        }

        return true;
    }

    /// <summary>
    ///     Records this frame's pre-pass: the rest poses that need restoring, then a dispatch per
    ///     active shape.
    /// </summary>
    /// <param name="list">An open command list, outside a render pass.</param>
    /// <returns>Whether anything was recorded.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="list" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>Before the frame's draws and outside any pass</b>, which is both what the copies
    ///         need — Vulkan forbids a transfer inside a render pass — and what the draws reading the
    ///         result need. The buffer is left in <see cref="ResourceState.VertexInput" />, so the
    ///         barrier between the dispatch and the draw is here rather than at the draw: this is the
    ///         side that knows the write happened, the argument <see cref="GeometryBuffer.Flush" />
    ///         and <c>GpuDrawArguments</c> both make.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>One dispatch per active target with a barrier between, not one per instance.</b>
    ///         Two shapes may move the same vertex — that is what a corrective <em>is</em> — so a
    ///         single dispatch over their concatenated entries would have two invocations
    ///         read-modify-writing one vertex and the answer would be whichever landed last. There is
    ///         no float atomic here to fix that with. Within one target the indices are distinct by
    ///         construction. See <c>MorphKernel</c>, which spells the same argument out and is the
    ///         reference this is held to.
    ///     </para>
    /// </remarks>
    public bool Record(ICommandList list) {
        ArgumentNullException.ThrowIfNull(list);
        ObjectDisposedException.ThrowIf(disposed, this);

        Dispatches = 0;
        Copies = 0;
        Degraded = null;

        // Nothing has ever attached, so there are no buffers and there is nothing to record. A scene
        // with no blend shapes in it reaches exactly this line every frame.
        if (entries is null || vertices is null) {
            return false;
        }

        // The deltas first and unconditionally: a mesh registered this frame has its entries staged
        // and nothing else copies them, and a dispatch reading an unwritten entry run scatters
        // whatever the allocator left onto real vertices.
        var uploaded = entries.Flush(list, ResourceState.ShaderRead) > 0;

        dirty.Clear();

        foreach (var instance in instances.Values) {
            if (instance.Dirty) {
                dirty.Add(instance);
            }
        }

        if (dirty.Count == 0) {
            return uploaded;
        }

        if (Source is null) {
            Degraded = "Blend shapes are attached and the geometry buffer they are copied out of is "
                + "unset, so every morphed mesh is drawn from whatever its range last held.";

            return uploaded;
        }

        Plan();

        // ⚠ The kernel is resolved only when there is something to dispatch, and the copies happen
        // whether or not it resolves. An instance whose weights are all zero — which is every instance
        // on the frame it first appears — needs its rest pose in the buffer and needs no shader at
        // all, and a Record that gave up here because there was nothing to dispatch would leave that
        // buffer holding whatever the allocator left. That is not a morph gone wrong; it is geometry
        // that is missing.
        //
        // The whole frame's blocks go in one write, before anything is recorded. A block written
        // inside the loop would be one host write per dispatch into memory the previous frame may be
        // reading — the ring inside Stage is what makes that safe, and one write is what makes it
        // cheap.
        var pipeline = default(PipelineHandle);
        var layout = default(DescriptorSetLayoutHandle);
        var blockBase = 0L;

        var scattering = dispatches.Count > 0 && Prepare(out pipeline, out layout) && Stage(out blockBase);

        Source.Transition(list, ResourceState.CopySource);
        vertices.Transition(list, ResourceState.CopyDestination);

        foreach (var instance in dirty) {
            list.CopyBuffer(
                Source.Vertices,
                (long)instance.Rest.BaseVertex * SurfaceVertex.SizeInBytes,
                vertices.Buffer,
                (long)instance.BaseVertex * SurfaceVertex.SizeInBytes,
                (long)instance.Rest.VertexCount * SurfaceVertex.SizeInBytes
            );

            Copies++;
        }

        // Back before the draws, and by the buffer's own bookkeeping rather than a barrier written
        // here — see GeometryBuffer.Transition.
        Source.Transition(list, ResourceState.VertexInput);

        if (scattering) {
            Scatter(list, pipeline, layout, blockBase);
        }

        vertices.Transition(list, ResourceState.VertexInput);

        // ⚠ Only when the shapes were actually applied. An instance left dirty because the kernel had
        // not compiled is one that asks again next frame — where clearing it here would be a face
        // frozen at rest for the rest of the run, on the strength of one frame that happened to
        // precede the first successful compile.
        if (dispatches.Count == 0 || scattering) {
            foreach (var instance in dirty) {
                instance.Dirty = false;
            }
        }

        return true;
    }

    /// <summary>The dispatches themselves, once there is a pipeline and a block to bind.</summary>
    void Scatter(ICommandList list, PipelineHandle pipeline, DescriptorSetLayoutHandle layout, long blockBase) {
        vertices!.Transition(list, ResourceState.ShaderWrite);

        list.BindPipeline(pipeline);

        for (var index = 0; index < dispatches.Count; index++) {
            var work = dispatches[index];

            var set = Descriptors!.Allocate(
                layout,
                [
                    DescriptorWrite.Uniform(
                        MorphScatterKeys.ConstantBufferBinding,
                        blocks,
                        blockBase + ((long)index * BlockStride),
                        MorphScatterConstants.Size
                    ),
                    DescriptorWrite.Storage(MorphScatterKeys.VerticesBinding, vertices.Buffer),
                    DescriptorWrite.Storage(
                        MorphScatterKeys.EntriesBinding,
                        entries!.Buffer,
                        (long)work.FirstEntry * entries.Stride,
                        (long)work.EntryCount * entries.Stride
                    )
                ]
            );

            list.BindDescriptorSet((DescriptorSetSlot)MorphScatterKeys.ConstantBufferSet, set);
            list.Dispatch((work.EntryCount + GroupSize - 1) / GroupSize);

            // ⚠ Between every pair, including two dispatches for different instances. Their ranges do
            // not overlap, so the barrier is not needed for correctness there — but the alternative is
            // a loop that knows which neighbours share an instance, and one that gets that wrong is a
            // corrective applied before the shape it corrects.
            list.Barrier(
                new([new(vertices.Buffer, ResourceState.ShaderWrite, ResourceState.ShaderWrite)], [])
            );

            Dispatches++;
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        if (blocks.IsValid) {
            device.Destroy(blocks);
            blocks = default;
        }

        vertices?.Dispose();
        entries?.Dispose();
    }

    /// <summary>Creates the two buffers, the first time anything needs them.</summary>
    void Allocate() {
        if (vertices is not null) {
            return;
        }

        // Vertex and Storage on the same buffer, which is the whole trick: the compute pre-pass writes
        // it as a storage buffer and the draws read it as a vertex buffer, and it is one allocation
        // because a copy between two would be the work the pre-pass exists to avoid.
        //
        // CopySource so that what the pre-pass produced can be read back — which is how
        // MorphScatterDeviceTests holds this to MorphKernel's floats, and what a debugger or a physics
        // cook would want of a morphed mesh. It is a capability of the allocation rather than a
        // residency or a layout, so it costs nothing to declare and cannot be added later.
        vertices = new(
            device,
            SurfaceVertex.SizeInBytes,
            VertexCapacity,
            1,
            BufferUsage.Vertex | BufferUsage.Storage | BufferUsage.CopyDestination | BufferUsage.CopySource,
            "Morph.Vertices"
        );

        // Sixteen bytes an entry, and aligned to sixteen *entries* so that a target's own run starts
        // at a byte offset a storage binding may name. See MorphArena.Alignment.
        entries = new(
            device,
            MorphKernel.EntryWords * sizeof(uint),
            EntryCapacity,
            BlockStride / (MorphKernel.EntryWords * sizeof(uint)),
            BufferUsage.Storage | BufferUsage.CopyDestination,
            "Morph.Entries"
        );
    }

    /// <summary>The mesh's shapes, uploaded on the first instance of it.</summary>
    bool Resident(GeometryKey key, MeshData mesh, out Shapes resident) {
        if (shapes.TryGetValue(key, out resident!)) {
            return true;
        }

        var total = 0;

        foreach (var target in mesh.MorphTargets) {
            total += target.Count;
        }

        if (total == 0 || !entries!.TryAllocate(total, out var first)) {
            Dropped++;
            resident = null!;

            return false;
        }

        var words = new uint[total * MorphKernel.EntryWords];
        var firsts = new int[mesh.MorphTargets.Length];
        var at = 0;

        for (var index = 0; index < mesh.MorphTargets.Length; index++) {
            var target = mesh.MorphTargets[index];

            firsts[index] = first + at;
            MorphKernel.Pack(target, words.AsSpan(at * MorphKernel.EntryWords));
            at += target.Count;
        }

        // ⚠ Given back on a refusal, and this is the allocation that would leak silently. The staging
        // region holds one flush's worth and can only be grown while nothing refers to it, so a scene
        // whose morphed meshes all appear on one frame can have a later one refused — and the entry run
        // reserved for a mesh whose deltas were never written is space nothing would claim again.
        //
        // ⚠ The mesh is then drawn at rest for as long as its entities stay settled, not "next frame":
        // MeshExtractionSystem stamps a RenderHandle whether or not this attached, and Resettle is what
        // asks again. Dropped is what says so.
        if (!entries.Write(first, MemoryMarshal.AsBytes(words.AsSpan()))) {
            entries.Free(first, total);
            Dropped++;
            resident = null!;

            return false;
        }

        resident = new() {
            Targets = mesh.MorphTargets,
            FirstEntry = first,
            EntryCount = total,
            Firsts = firsts
        };

        shapes[key] = resident;

        return true;
    }

    /// <summary>Which dispatches this frame needs, in the order they have to run.</summary>
    /// <remarks>
    ///     ⚠ <b>A weight of exactly zero is skipped rather than dispatched for</b>, which is what makes
    ///     twenty shapes cost the two or three a face is actually making — and what
    ///     <see cref="MorphKernel.Apply" /> matches, so the reference does not quietly touch vertices
    ///     the device never wrote.
    /// </remarks>
    void Plan() {
        dispatches.Clear();

        foreach (var instance in dirty) {
            var resident = instance.Shapes;

            for (var index = 0; index < resident.Targets.Length; index++) {
                var weight = instance.Weights[index];
                var target = resident.Targets[index];

                if (weight == 0f || target.Count == 0) {
                    continue;
                }

                dispatches.Add(
                    new(
                        resident.Firsts[index],
                        target.Count,
                        new() {
                            EntryCount = target.Count,
                            BaseVertex = instance.BaseVertex,
                            Stride = VertexFloats,
                            PositionOffset = PositionFloat,

                            // ⚠ Negative leaves normals alone, and a target with no normal deltas is
                            // not that case: MorphKernel.Pack writes zeros into their half, so the
                            // arithmetic is an addition of zero and both processors do it.
                            NormalOffset = NormalFloat,
                            Weight = weight,
                            PositionStep = MorphKernel.Step(target.PositionScale),
                            NormalStep = MorphKernel.Step(target.NormalScale)
                        }
                    )
                );
            }
        }
    }

    /// <summary>The kernel and the set layout it wants, or a reason there are none.</summary>
    bool Prepare(out PipelineHandle pipeline, out DescriptorSetLayoutHandle layout) {
        pipeline = default;
        layout = default;

        if (Effects is null || Pipelines is null || Descriptors is null) {
            Degraded = "Blend shapes are attached and the effect system, the pipeline cache or the "
                + "descriptor allocator is unset, so nothing dispatches and every morphed mesh is "
                + "drawn at rest.";

            return false;
        }

        // With the default fillers, for GpuDrawArguments' reason: a whole-library compiler refuses a
        // key that leaves the material chain's slots unbound, and this kernel composes nothing.
        var key = EffectKey.Of(MorphScatterKeys.ShaderName, [], MaterialCompiler.PassComposition());

        if (Effects.Resolve(key) is not { IsPlaceholder: false } effect) {
            Degraded = $"'{MorphScatterKeys.ShaderName}' has not compiled, so no blend shape is applied.";
            return false;
        }

        pipeline = Pipelines.GetOrCreate(effect);

        if (!pipeline.IsValid) {
            Degraded = $"'{MorphScatterKeys.ShaderName}' resolved to a variant with no compute stage.";
            return false;
        }

        if (MorphScatterKeys.ConstantBufferSet >= effect.SetLayouts.Length
            || !effect.SetLayouts[MorphScatterKeys.ConstantBufferSet].IsValid) {
            Degraded = $"'{MorphScatterKeys.ShaderName}' declares no set "
                + $"{MorphScatterKeys.ConstantBufferSet}, which is where its whole interface lives.";

            return false;
        }

        layout = effect.SetLayouts[MorphScatterKeys.ConstantBufferSet];

        return true;
    }

    /// <summary>Puts this frame's uniform blocks in the ring and says where they start.</summary>
    bool Stage(out long offset) {
        offset = 0;

        var frames = Math.Max(1, device.FramesInFlight);

        if (!blocks.IsValid || dispatches.Count > blockSlots) {
            if (blocks.IsValid) {
                device.Destroy(blocks);
            }

            blockSlots = Math.Max(64, dispatches.Count);

            blocks = device.CreateBuffer(
                new(
                    (long)frames * blockSlots * BlockStride,
                    BufferUsage.Uniform,
                    MemoryAccess.HostUpload,
                    "Morph.Constants"
                )
            );

            ring = 0;
        }

        // One region per frame in flight, and the caller binds at the offset. Writing the same bytes
        // every frame is a race the API cannot report — see UploadBuffer, whose ring this is.
        ring = (ring + 1) % frames;
        offset = (long)ring * blockSlots * BlockStride;

        var wanted = dispatches.Count * BlockStride;

        // Kept across frames rather than allocated per frame: this runs every frame a character's
        // expression changes, and a hundred dispatches is twenty-five kilobytes of garbage a frame for
        // nothing. Cleared where it is grown, because the padding between blocks travels too.
        if (staged.Length < wanted) {
            staged = new byte[Math.Max(wanted, 64 * BlockStride)];
        } else {
            Array.Clear(staged, 0, wanted);
        }

        for (var index = 0; index < dispatches.Count; index++) {
            dispatches[index].Constants.Write(staged.AsSpan(index * BlockStride, MorphScatterConstants.Size));
        }

        device.Write(blocks, offset, staged.AsSpan(0, wanted));

        return true;
    }

    /// <summary>One mesh's shapes, on the device, shared by every instance of it.</summary>
    sealed class Shapes {
        public required MorphTargetData[] Targets { get; init; }
        public required int FirstEntry { get; init; }
        public required int EntryCount { get; init; }
        public required int[] Firsts { get; init; }
        public int Claims { get; set; }
    }

    /// <summary>One object's morphed vertices and the weights that produced them.</summary>
    sealed class Instance {
        public required RenderObjectId Id { get; init; }
        public required GeometryKey Key { get; init; }
        public required Shapes Shapes { get; init; }
        public required int BaseVertex { get; init; }
        public required GeometrySlice Rest { get; init; }
        public required float[] Weights { get; init; }
        public bool Dirty { get; set; }
    }

    /// <summary>One dispatch: which entries, and the block that describes them.</summary>
    readonly record struct Dispatch(int FirstEntry, int EntryCount, MorphScatterConstants Constants);
}

/// <summary>Where one object's morphed vertices are.</summary>
/// <param name="BaseVertex">
///     Which vertex of <see cref="MorphRenderFeature.Buffer" /> its copy starts at, which is also its
///     <see cref="MeshDraw.VertexOffset" />.
/// </param>
/// <param name="VertexCount">How many it has.</param>
/// <param name="TargetCount">How many blend shapes its mesh carries.</param>
/// <remarks>
///     ⚠ <b>A <see cref="TargetCount" /> of zero is "this object is not morphed", and that is what the
///     zeroed array means for every object that never attached.</b> <see cref="BaseVertex" /> cannot
///     carry that meaning — zero is a real base, and the first instance to attach gets it.
/// </remarks>
public readonly record struct MorphInstance(int BaseVertex, int VertexCount, int TargetCount) {
    /// <summary>Whether this object draws from the morph buffer.</summary>
    public bool IsMorphed => TargetCount > 0;
}
