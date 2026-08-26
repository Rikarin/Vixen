// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics;

namespace Vixen.Rendering;

/// <summary>Where one mesh's geometry sits inside a shared buffer.</summary>
/// <param name="BaseVertex">
///     Which vertex the mesh starts at. Added to every index before the vertex is fetched, which is
///     what <see cref="MeshDraw.VertexOffset" /> and every API's <c>vertexOffset</c> mean.
/// </param>
/// <param name="VertexCount">How many vertices it occupies.</param>
/// <param name="FirstIndex">Which index it starts at.</param>
/// <param name="IndexCount">How many indices it occupies.</param>
/// <remarks>
///     Counts and not bytes. A vertex offset is a <em>vertex</em> count on every API — the stride
///     comes from the pipeline's vertex layout, not from the draw — and an index offset is an index
///     count. Storing bytes would mean dividing by a stride at every use and getting a fraction
///     wherever the two disagreed.
/// </remarks>
public readonly record struct GeometrySlice(int BaseVertex, int VertexCount, int FirstIndex, int IndexCount) {
    /// <summary>Whether this names a real allocation.</summary>
    public bool IsValid => VertexCount > 0;
}

/// <summary>
///     Many meshes in one vertex buffer and one index buffer, each at its own offset.
/// </summary>
/// <remarks>
///     <para>
///         <strong>The other half of the sentence <c>docs/plan/23-bindless-materials.md</c> opens with.</strong>
///         <c>MeshRenderFeature</c> binds "a vertex buffer, an index buffer and a material set per
///         object", and a draw that binds anything per object cannot be merged with its neighbour.
///         Records removed the third; this removes the first two. A run of objects whose geometry
///         came from one of these binds nothing between them, so what separates two draws is the
///         numbers in their arguments — which is exactly what an indirect buffer holds.
///     </para>
///     <para>
///         <strong>One buffer per vertex layout, and it has to be.</strong> A draw's
///         <c>vertexOffset</c> is a vertex count that the GPU multiplies by the <em>pipeline's</em>
///         stride. Two meshes of different formats in one buffer would be read at each other's
///         stride, so the format is a property of the buffer rather than of the mesh in it — which is
///         the same reason <see cref="MeshDraw.VertexLayout" /> is part of <c>PipelineKey</c>.
///     </para>
///     <para>
///         ⚠ <strong>Fixed capacity, and dropped rather than grown.</strong> Growing means a larger
///         buffer, a copy recorded on some command list, and new handles — and the handles are the
///         problem: every <see cref="MeshDraw" /> already built holds the old ones, so a grow would
///         have to find and rewrite all of them or leave draws pointing at a destroyed buffer. A
///         caller that needs more space makes a second buffer, which costs one bind between the two
///         runs and nothing within either. Same trade <see cref="MeshRenderer" /> makes, for a
///         related reason.
///     </para>
///     <para>
///         <strong>Device-local, so writing means staging.</strong> The whole point is geometry that
///         does not cross the bus every frame, which rules out host-visible memory. So
///         <see cref="Write" /> fills a staging region and <see cref="Flush" /> records the copies —
///         two calls because they happen at different times: a level loads over many frames and the
///         copies belong at one known point in one command list.
///     </para>
/// </remarks>
public sealed class GeometryBuffer : IDisposable {
    readonly IGraphicsDevice device;
    readonly string name;

    // Free ranges, kept sorted by offset so a release can coalesce with its neighbours. A list
    // rather than a tree: allocations are meshes, so there are thousands at most and a linear
    // first-fit over a handful of free ranges is faster than the bookkeeping a tree would add.
    readonly List<(int Offset, int Count)> freeVertices = [];
    readonly List<(int Offset, int Count)> freeIndices = [];

    readonly List<(long Source, long Destination, long Size, bool Index)> pending = [];

    BufferHandle staging;
    long stagingUsed;
    long stagingCapacity;

    ResourceState vertexState = ResourceState.Undefined;
    ResourceState indexState = ResourceState.Undefined;
    bool disposed;

    /// <summary>Creates the pair of buffers and the staging region that fills them.</summary>
    /// <param name="device">The device.</param>
    /// <param name="vertexStride">How many bytes one vertex occupies, which is the layout's.</param>
    /// <param name="vertexCapacity">How many vertices the buffer holds.</param>
    /// <param name="indexCapacity">How many indices it holds.</param>
    /// <param name="indexFormat">Whether indices are 16- or 32-bit.</param>
    /// <param name="name">A name for the debugger and the validation layers.</param>
    public GeometryBuffer(
        IGraphicsDevice device,
        int vertexStride,
        int vertexCapacity,
        int indexCapacity,
        IndexFormat indexFormat = IndexFormat.UInt32,
        string name = "Geometry"
    ) {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(vertexStride);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(vertexCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(indexCapacity);

        this.device = device;
        this.name = name;

        VertexStride = vertexStride;
        VertexCapacity = vertexCapacity;
        IndexCapacity = indexCapacity;
        IndexFormat = indexFormat;
        IndexStride = indexFormat == IndexFormat.UInt16 ? 2 : 4;

        freeVertices.Add((0, vertexCapacity));
        freeIndices.Add((0, indexCapacity));

        // ⚠ CopySource as well, and it is not speculative: it is what a blend-shaped mesh's rest pose
        // is read *out* of. `MorphRenderFeature` copies a slice's vertices into a per-instance buffer
        // and scatters deltas onto the copy, so this buffer is the source of that copy — and a buffer
        // created without the usage is a validation error at the copy rather than at the creation,
        // which is a frame that records and then dies in the layer. The flag costs nothing: it is a
        // capability of the allocation, not a residency or a layout.
        Vertices = device.CreateBuffer(
            new(
                (long)vertexCapacity * vertexStride,
                BufferUsage.Vertex | BufferUsage.CopyDestination | BufferUsage.CopySource,
                MemoryAccess.DeviceLocal,
                $"{name}.Vertices"
            )
        );

        Indices = device.CreateBuffer(
            new(
                (long)indexCapacity * IndexStride,
                BufferUsage.Index | BufferUsage.CopyDestination,
                MemoryAccess.DeviceLocal,
                $"{name}.Indices"
            )
        );
    }

    /// <summary>The one vertex buffer every mesh in here is drawn from.</summary>
    public BufferHandle Vertices { get; }

    /// <summary>The one index buffer.</summary>
    public BufferHandle Indices { get; }

    /// <summary>How many bytes one vertex occupies.</summary>
    public int VertexStride { get; }

    /// <summary>How many bytes one index occupies.</summary>
    public int IndexStride { get; }

    /// <summary>Whether indices are 16- or 32-bit.</summary>
    public IndexFormat IndexFormat { get; }

    /// <summary>How many vertices fit.</summary>
    public int VertexCapacity { get; }

    /// <summary>How many indices fit.</summary>
    public int IndexCapacity { get; }

    /// <summary>How many vertices are allocated.</summary>
    public int UsedVertices { get; private set; }

    /// <summary>How many indices are allocated.</summary>
    public int UsedIndices { get; private set; }

    /// <summary>How many slices are outstanding.</summary>
    public int SliceCount { get; private set; }

    /// <summary>What the vertex buffer was last left in, which is what a barrier has to say it is.</summary>
    /// <remarks>
    ///     Readable because the tracking is here and the transition may be somebody else's — see
    ///     <see cref="Transition" />. <see cref="ResourceState.Undefined" /> until the first
    ///     <see cref="Flush" />, which is a buffer nothing has written and nothing may read.
    /// </remarks>
    public ResourceState VertexState => vertexState;

    /// <summary>Moves the vertex buffer into another state, and remembers that it did.</summary>
    /// <param name="list">An open command list, outside a render pass.</param>
    /// <param name="state">What it is about to be used as.</param>
    /// <exception cref="ArgumentNullException"><paramref name="list" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>Here rather than at the caller, because the state is here.</b> A caller that emitted
    ///         its own barrier would be writing down a second opinion about what this buffer was last
    ///         left in, and the two would agree until <see cref="Flush" /> ran on a frame the caller
    ///         did not — which is a transition declared from a state the buffer is not in, and a
    ///         validation error rather than a wrong picture.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Whoever moves it away is responsible for moving it back.</b> The draws that read
    ///         this need <see cref="ResourceState.VertexInput" />, and nothing between here and them
    ///         checks. <c>MorphRenderFeature</c> — the one caller — brackets its copies with a pair.
    ///     </para>
    ///     <para>
    ///         A transition to the state it is already in records nothing. That is not an
    ///         optimisation: a barrier from a state to itself is legal and would be a memory
    ///         dependency nobody asked for, on a buffer the whole scene draws from.
    ///     </para>
    /// </remarks>
    public void Transition(ICommandList list, ResourceState state) {
        ArgumentNullException.ThrowIfNull(list);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (vertexState == state) {
            return;
        }

        list.Barrier(new([new(Vertices, vertexState, state)], []));
        vertexState = state;
    }

    /// <summary>
    ///     How many free ranges the vertex space is in, which is what fragmentation looks like.
    /// </summary>
    /// <remarks>
    ///     The number a test asserts on to say that releasing coalesces. One free range after
    ///     everything is released means the buffer came back to where it started; a buffer that
    ///     accumulated a range per freed mesh would refuse a large allocation while reporting most of
    ///     itself unused, which is the failure that looks like a capacity bug and is not one.
    /// </remarks>
    public int VertexFragmentCount => freeVertices.Count;

    /// <summary>How many bytes are staged and not yet copied.</summary>
    public long PendingBytes => stagingUsed;

    /// <summary>How many bytes the staging region can hold before it has to be flushed.</summary>
    public long StagingCapacity => stagingCapacity;

    /// <summary>Whether a write of this size can be staged without flushing first.</summary>
    /// <param name="bytes">How many bytes the write is.</param>
    /// <returns>Whether it fits.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>What a caller asks instead of finding out by being thrown at.</b> The staging region
    ///         holds one flush's worth of writes and can only be grown while nothing refers to it, so
    ///         a caller filling it in a loop has to know when to stop — and the honest answer for
    ///         everything after that point is "next frame", not an exception in the middle of a frame
    ///         that had already recorded half its draws.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An empty region always answers yes, however large the write.</b> Nothing refers to
    ///         it, so <see cref="Stage" /> will grow it to fit — which is what makes a single mesh
    ///         larger than the whole staging region registrable at all rather than permanently
    ///         deferred.
    ///     </para>
    /// </remarks>
    public bool CanStage(long bytes) => stagingUsed == 0 || !staging.IsValid || stagingUsed + bytes <= stagingCapacity;

    /// <summary>Asks for the staging region to be at least this big, if nothing is using it.</summary>
    /// <param name="bytes">How much room the caller expects to want.</param>
    /// <returns>Whether it is now that big.</returns>
    /// <remarks>
    ///     ⚠ <b>What stops a large scene appearing one buffer-full at a time.</b> Without it a frame
    ///     that wanted four megabytes of new geometry would register sixty-four kilobytes and defer
    ///     the rest, then do the same next frame — converging, over a second, in visible steps. A
    ///     caller that knows how much it was asked for can say so once and have the whole of it land
    ///     on the following frame.
    /// </remarks>
    public bool Reserve(long bytes) {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (bytes <= stagingCapacity) {
            return true;
        }

        if (stagingUsed > 0) {
            return false;
        }

        EnsureStaging(bytes);

        return true;
    }

    /// <summary>Finds room for one mesh, or answers that there is none.</summary>
    /// <param name="vertexCount">How many vertices it has.</param>
    /// <param name="indexCount">How many indices, or zero for a mesh drawn without them.</param>
    /// <param name="slice">Where it goes.</param>
    /// <returns>False when either space is too full or too fragmented to hold it.</returns>
    /// <remarks>
    ///     ⚠ Both or neither. A mesh given vertex space and refused index space would leave the
    ///     vertices allocated to nothing, and the leak would be invisible until the buffer filled.
    /// </remarks>
    public bool TryAllocate(int vertexCount, int indexCount, out GeometrySlice slice) {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(vertexCount);
        ArgumentOutOfRangeException.ThrowIfNegative(indexCount);

        slice = default;

        if (!TryTake(freeVertices, vertexCount, out var baseVertex)) {
            return false;
        }

        var firstIndex = 0;

        if (indexCount > 0 && !TryTake(freeIndices, indexCount, out firstIndex)) {
            // Given back rather than kept. See the remark above: a half-allocation is a leak with no
            // symptom until the buffer is full.
            Release(freeVertices, baseVertex, vertexCount);
            return false;
        }

        UsedVertices += vertexCount;
        UsedIndices += indexCount;
        SliceCount++;

        slice = new(baseVertex, vertexCount, firstIndex, indexCount);
        return true;
    }

    /// <summary>Gives one mesh's space back.</summary>
    /// <param name="slice">What <see cref="TryAllocate" /> handed out.</param>
    public void Free(in GeometrySlice slice) {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (!slice.IsValid) {
            return;
        }

        Release(freeVertices, slice.BaseVertex, slice.VertexCount);

        if (slice.IndexCount > 0) {
            Release(freeIndices, slice.FirstIndex, slice.IndexCount);
        }

        UsedVertices -= slice.VertexCount;
        UsedIndices -= slice.IndexCount;
        SliceCount--;
    }

    /// <summary>Stages one mesh's bytes, to be copied by the next <see cref="Flush" />.</summary>
    /// <param name="slice">Where they go.</param>
    /// <param name="vertices">The vertex bytes, <c>VertexCount × VertexStride</c> of them.</param>
    /// <param name="indices">The index bytes, or empty.</param>
    /// <exception cref="ArgumentException">The bytes do not match what the slice reserved.</exception>
    public void Write(in GeometrySlice slice, ReadOnlySpan<byte> vertices, ReadOnlySpan<byte> indices) {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (!slice.IsValid) {
            throw new ArgumentException("An unallocated slice was written to.", nameof(slice));
        }

        var wantedVertices = (long)slice.VertexCount * VertexStride;
        var wantedIndices = (long)slice.IndexCount * IndexStride;

        // Exactly, not at most. Short bytes leave the tail of a mesh as whatever the last mesh at
        // that offset was, which draws — as a piece of the previous level's geometry welded to this
        // one's — where a refusal names the mesh.
        if (vertices.Length != wantedVertices) {
            throw new ArgumentException(
                $"'{name}' was given {vertices.Length} vertex bytes for a slice of {wantedVertices}. The "
                + $"slice holds {slice.VertexCount} vertices of {VertexStride} bytes, so a different "
                + "length means the layout the bytes were built from is not this buffer's.",
                nameof(vertices)
            );
        }

        if (indices.Length != wantedIndices) {
            throw new ArgumentException(
                $"'{name}' was given {indices.Length} index bytes for a slice of {wantedIndices}.",
                nameof(indices)
            );
        }

        Stage(vertices, (long)slice.BaseVertex * VertexStride, index: false);

        if (!indices.IsEmpty) {
            Stage(indices, (long)slice.FirstIndex * IndexStride, index: true);
        }
    }

    /// <summary>Records the copies everything staged since the last flush needs.</summary>
    /// <param name="list">An open command list, outside a render pass.</param>
    /// <returns>How many copies were recorded.</returns>
    /// <remarks>
    ///     <para>
    ///         The barriers are here rather than at the draw, on the same terms as
    ///         <see cref="GpuDrawArguments" />': this is the side that knows a write happened. The
    ///         buffers are left in <see cref="ResourceState.VertexInput" />, which is what the draw
    ///         that reads them needs.
    ///     </para>
    ///     <para>
    ///         ⚠ The staging region is reset here, so the bytes must have reached the device before
    ///         the next <see cref="Write" /> overwrites them — which is what recording the copy into
    ///         a list and submitting that list means. A caller that stages, flushes and stages again
    ///         without submitting in between is overwriting bytes a recorded copy still refers to.
    ///     </para>
    /// </remarks>
    public int Flush(ICommandList list) {
        ArgumentNullException.ThrowIfNull(list);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (pending.Count == 0) {
            return 0;
        }

        list.Barrier(
            new(
                [
                    new(Vertices, vertexState, ResourceState.CopyDestination),
                    new(Indices, indexState, ResourceState.CopyDestination)
                ],
                []
            )
        );

        foreach (var (source, destination, size, index) in pending) {
            list.CopyBuffer(staging, source, index ? Indices : Vertices, destination, size);
        }

        list.Barrier(
            new(
                [
                    new(Vertices, ResourceState.CopyDestination, ResourceState.VertexInput),
                    new(Indices, ResourceState.CopyDestination, ResourceState.VertexInput)
                ],
                []
            )
        );

        vertexState = ResourceState.VertexInput;
        indexState = ResourceState.VertexInput;

        var count = pending.Count;
        pending.Clear();
        stagingUsed = 0;

        return count;
    }

    /// <summary>Fills a draw record from a slice, leaving everything else alone.</summary>
    /// <param name="draw">The record to fill.</param>
    /// <param name="slice">Where the mesh is.</param>
    /// <param name="vertexLayout">Which layout this buffer's vertices are in.</param>
    /// <remarks>
    ///     The one place the four numbers are joined, so a caller cannot pair this buffer's handles
    ///     with another's offsets. <see cref="MeshDraw.InstanceCount" /> is left alone because it is
    ///     not the geometry's to say.
    /// </remarks>
    public void Apply(ref MeshDraw draw, in GeometrySlice slice, int vertexLayout = 0) {
        ObjectDisposedException.ThrowIf(disposed, this);

        draw.VertexBuffer = Vertices;
        draw.IndexBuffer = slice.IndexCount > 0 ? Indices : default;
        draw.IndexFormat = IndexFormat;
        draw.Count = slice.IndexCount > 0 ? slice.IndexCount : slice.VertexCount;
        draw.FirstIndex = slice.FirstIndex;
        draw.VertexOffset = slice.BaseVertex;
        draw.VertexLayout = vertexLayout;
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        device.Destroy(Vertices);
        device.Destroy(Indices);

        if (staging.IsValid) {
            device.Destroy(staging);
            staging = default;
        }

        pending.Clear();
        freeVertices.Clear();
        freeIndices.Clear();
    }

    /// <summary>First fit, splitting the range it lands in.</summary>
    /// <remarks>
    ///     First fit rather than best fit, and the difference is smaller than it reads: best fit
    ///     leaves the smallest remainder, which is also the remainder least likely to be usable
    ///     again. Neither is right in general and first fit is the one that does not walk the whole
    ///     list on every allocation.
    /// </remarks>
    static bool TryTake(List<(int Offset, int Count)> free, int count, out int offset) {
        for (var index = 0; index < free.Count; index++) {
            var range = free[index];

            if (range.Count < count) {
                continue;
            }

            offset = range.Offset;

            if (range.Count == count) {
                free.RemoveAt(index);
            } else {
                free[index] = (range.Offset + count, range.Count - count);
            }

            return true;
        }

        offset = 0;
        return false;
    }

    /// <summary>Puts a range back, joined to whichever neighbours it touches.</summary>
    /// <remarks>
    ///     ⚠ Coalescing is not tidiness. Without it a buffer that loaded and unloaded a level twice
    ///     holds a free range per mesh and refuses a large allocation while reporting itself almost
    ///     entirely free — which reads as a capacity bug and is not one.
    /// </remarks>
    static void Release(List<(int Offset, int Count)> free, int offset, int count) {
        var index = 0;

        while (index < free.Count && free[index].Offset < offset) {
            index++;
        }

        free.Insert(index, (offset, count));

        // Forwards first, then backwards, so a range that fills the gap between two others joins
        // both — which is the case that a single pass in one direction leaves half-done.
        if (index + 1 < free.Count && free[index].Offset + free[index].Count == free[index + 1].Offset) {
            free[index] = (free[index].Offset, free[index].Count + free[index + 1].Count);
            free.RemoveAt(index + 1);
        }

        if (index > 0 && free[index - 1].Offset + free[index - 1].Count == free[index].Offset) {
            free[index - 1] = (free[index - 1].Offset, free[index - 1].Count + free[index].Count);
            free.RemoveAt(index);
        }
    }

    void Stage(ReadOnlySpan<byte> bytes, long destination, bool index) {
        EnsureStaging(stagingUsed + bytes.Length);
        device.Write(staging, stagingUsed, bytes);
        pending.Add((stagingUsed, destination, bytes.Length, index));
        stagingUsed += bytes.Length;
    }

    /// <remarks>
    ///     Grown rather than fixed, unlike the geometry itself, and the asymmetry is the point: a
    ///     staging region holds one flush's worth of writes and nothing refers to it afterwards, so
    ///     replacing it costs a buffer and breaks no handle anybody kept.
    /// </remarks>
    void EnsureStaging(long required) {
        if (staging.IsValid && required <= stagingCapacity) {
            return;
        }

        if (stagingUsed > 0) {
            throw new InvalidOperationException(
                $"'{name}' ran out of staging room with {stagingUsed} bytes already staged. Growing now "
                + "would abandon the bytes a pending copy refers to — flush and submit, then write the "
                + "rest."
            );
        }

        if (staging.IsValid) {
            device.Destroy(staging);
        }

        stagingCapacity = Math.Max(required, Math.Max(stagingCapacity * 2, 1 << 16));

        staging = device.CreateBuffer(
            new(stagingCapacity, BufferUsage.CopySource, MemoryAccess.HostUpload, $"{name}.Staging")
        );
    }
}
