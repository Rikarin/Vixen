// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using Vixen.Graphics;
using Vixen.Rendering.VirtualGeometry;

namespace Vixen.Rendering;

/// <summary>Where a page's bytes are read from.</summary>
/// <remarks>
///     <para>
///         An interface rather than a call into the asset system, for two reasons that pull the same
///         way. The pool is in <c>Vixen.Rendering</c> and the asset system is above it, so a direct
///         call would be a reference in the wrong direction; and the phase-2 exit criterion asks for
///         a camera path run <em>with a synthetic I/O delay injected</em>, which is a fake
///         implementation of exactly this.
///     </para>
///     <para>
///         Asynchronous, because reading from disk is — and because the whole point of a residency
///         manager is that a frame does not wait for one.
///     </para>
/// </remarks>
public interface IMeshletPageSource {
    /// <summary>Reads one page's bytes.</summary>
    /// <param name="key">Which page of which mesh.</param>
    /// <param name="destination">Where to put them.</param>
    /// <param name="cancellation">Cancelled when the pool is disposed.</param>
    /// <returns>How many bytes were read.</returns>
    ValueTask<int> ReadAsync(PageKey key, Memory<byte> destination, CancellationToken cancellation);
}

/// <summary>
///     Reads pages out of page sets already in memory, which is what an unstreamed build has.
/// </summary>
/// <remarks>
///     Synchronous behind an asynchronous signature, and honestly so: the bytes are already here, and
///     pretending otherwise would put a thread hop in front of a memcpy. What a real content pipeline
///     supplies instead reads from the bundle — see <c>docs/plan/08</c> — and nothing about the pool
///     changes when it does.
/// </remarks>
public sealed class MemoryMeshletPageSource : IMeshletPageSource {
    // Concurrent because the two ends are different threads: Add is called while a mesh is being
    // registered, on the frame's, and the lookup in ReadAsync is on whichever pool thread the load
    // landed on. A plain Dictionary resized under a reader is a lookup that misses or faults.
    readonly ConcurrentDictionary<int, MeshletPageSet> sources = [];

    /// <summary>Registers a mesh's pages under a source id.</summary>
    /// <param name="source">The id <see cref="PageKey.Source" /> will carry.</param>
    /// <param name="pages">Its pages.</param>
    /// <exception cref="ArgumentNullException"><paramref name="pages" /> is null.</exception>
    public void Add(int source, MeshletPageSet pages) {
        ArgumentNullException.ThrowIfNull(pages);
        sources[source] = pages;
    }

    /// <inheritdoc />
    public ValueTask<int> ReadAsync(PageKey key, Memory<byte> destination, CancellationToken cancellation) {
        cancellation.ThrowIfCancellationRequested();

        if (!sources.TryGetValue(key.Source, out var pages) || key.Index >= pages.Pages.Length) {
            return ValueTask.FromResult(0);
        }

        var bytes = pages.BytesOf(key.Index);
        bytes.CopyTo(destination.Span);

        return ValueTask.FromResult(bytes.Length);
    }
}

/// <summary>
///     Reads pages out of seekable blobs, which is what a build's artefacts are.
/// </summary>
/// <remarks>
///     <para>
///         <b>The source a shipping build uses, and the reason
///         <see cref="MeshletPageSet.WithoutData" /> exists.</b> A build writes a mesh's records and
///         its geometry as two artefacts; the records are deserialised whole at load, and this reads
///         the geometry a page at a time from wherever the second one landed. What it never does is
///         read the whole blob, which is the difference between streaming and loading.
///     </para>
///     <para>
///         <b>A stream per source rather than one shared handle.</b> Reads are issued from whatever
///         thread the residency service's continuations land on, and a <see cref="Stream" />'s
///         position is not thread-safe — so the seek and the read are one operation under one lock
///         per source. A file provider that hands out <see cref="System.IO.MemoryMappedFiles" />
///         would not need it; the interface does not promise one.
///     </para>
///     <para>
///         Offsets come from <see cref="MeshletPageSet.OffsetOf" /> rather than from
///         <see cref="MeshletPage.Offset" />, because a data-less set's pages carry the offsets they
///         had in a blob that is no longer attached — the same numbers, and derived rather than
///         trusted.
///     </para>
/// </remarks>
public sealed class StreamMeshletPageSource : IMeshletPageSource, IDisposable {
    // Concurrent for MemoryMeshletPageSource's reason: Add and Remove are the frame's and the lookup
    // in ReadAsync is a pool thread's. The per-blob gate below protects a stream's position; this
    // protects finding the stream at all.
    readonly ConcurrentDictionary<int, Blob> sources = [];
    bool disposed;

    /// <summary>Registers a mesh's page blob under a source id.</summary>
    /// <param name="source">The id <see cref="PageKey.Source" /> will carry.</param>
    /// <param name="pages">Its records, with or without their data.</param>
    /// <param name="data">The blob its pages live in. Owned from here, and disposed with this.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The stream cannot seek, so a page cannot be found in it.</exception>
    public void Add(int source, MeshletPageSet pages, Stream data) {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(data);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (!data.CanSeek) {
            throw new ArgumentException(
                "A page blob has to be seekable — the whole point is to read one page without reading "
                + "the ones before it.",
                nameof(data)
            );
        }

        if (sources.TryRemove(source, out var replaced)) {
            replaced.Data.Dispose();
        }

        sources[source] = new(pages, data);
    }

    /// <summary>Forgets a source and closes its blob.</summary>
    /// <param name="source">Which one.</param>
    /// <returns>Whether there was one.</returns>
    public bool Remove(int source) {
        if (!sources.TryRemove(source, out var blob)) {
            return false;
        }

        blob.Data.Dispose();

        return true;
    }

    /// <inheritdoc />
    public ValueTask<int> ReadAsync(PageKey key, Memory<byte> destination, CancellationToken cancellation) {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellation.ThrowIfCancellationRequested();

        if (!sources.TryGetValue(key.Source, out var blob) || key.Index >= blob.Pages.Pages.Length) {
            return ValueTask.FromResult(0);
        }

        var page = blob.Pages.Pages[key.Index];
        var size = Math.Min(page.Size, destination.Length);

        if (size <= 0) {
            return ValueTask.FromResult(0);
        }

        // Synchronous under the lock, and honestly so: the seek and the read are one indivisible
        // operation on a shared position, and an await between them is exactly the interleaving that
        // gives one page another page's bytes. Making this asynchronous would need a handle whose
        // reads carry their own offset, which is what the interface leaves open.
        lock (blob.Gate) {
            blob.Data.Seek(blob.Pages.OffsetOf(key.Index), SeekOrigin.Begin);

            // ReadAtLeast rather than Read, because a stream may return short for reasons that are
            // not the end of it — and a short page is geometry with a hole, which decodes to
            // triangles at the origin rather than to nothing.
            var read = blob.Data.ReadAtLeast(destination.Span[..size], size, false);

            return ValueTask.FromResult(read);
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        foreach (var blob in sources.Values) {
            blob.Data.Dispose();
        }

        sources.Clear();
    }

    sealed record Blob(MeshletPageSet Pages, Stream Data) {
        public Lock Gate { get; } = new();
    }
}

/// <summary>
///     A device buffer of fixed-size page slots, filled and emptied by <see cref="PageResidency" />.
/// </summary>
/// <remarks>
///     <para>
///         <b>This is <see cref="GeometryBuffer" /> with a residency policy instead of a load-time
///         one</b>, which is what <c>docs/plan/22-virtualized-geometry.md</c> phase 2 says it should be. The
///         same fixed capacity, the same device-local memory written through staging, the same
///         two-call <c>Write</c>/<c>Flush</c> split because a page arrives on one thread and the copy
///         belongs at one known point in one command list. What differs is the allocator: that one
///         hands out ranges of arbitrary size and never takes them back, and this one hands out
///         identical slots and takes them back constantly.
///     </para>
///     <para>
///         <b>Fixed slots, which is the decision that makes eviction free.</b> A page is a page is a
///         page, so a slot vacated by one page fits any other and the free list is a stack of
///         integers. Variable-size allocations would need a defragmenter in the frame path, and a
///         defragmenter moves bytes the device is reading — which is the shape of problem this whole
///         design exists to not have.
///     </para>
///     <para>
///         <b>One buffer, not one per vertex layout.</b> <see cref="GeometryBuffer" /> needs one per
///         layout because a draw's <c>vertexOffset</c> is multiplied by the pipeline's stride; a page
///         is never bound as a vertex buffer at all. It is a storage buffer a shader reads by byte
///         offset, which is what phase 4's raster and phase 5's resolve both want and is why the
///         quantized position format can differ from any pipeline's vertex layout.
///     </para>
/// </remarks>
public sealed class MeshletPagePool : IPageStore, IDisposable {
    readonly IGraphicsDevice device;
    readonly IMeshletPageSource source;
    readonly string name;

    readonly List<(long Source, long Destination, long Size)> pending = [];

    BufferHandle staging;
    long stagingUsed;
    ResourceState state = ResourceState.Undefined;
    bool disposed;

    /// <summary>Creates the pool and the staging region that fills it.</summary>
    /// <param name="device">The device.</param>
    /// <param name="source">Where page bytes are read from.</param>
    /// <param name="slotCount">How many pages it holds.</param>
    /// <param name="pageSize">How many bytes one page occupies.</param>
    /// <param name="name">A name for the debugger and the validation layers.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public MeshletPagePool(
        IGraphicsDevice device,
        IMeshletPageSource source,
        int slotCount,
        int pageSize = 128 * 1024,
        string name = "MeshletPages"
    ) {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(slotCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);

        this.device = device;
        this.source = source;
        this.name = name;

        SlotCount = slotCount;
        PageSize = pageSize;

        Pages = device.CreateBuffer(
            new(
                (long)slotCount * pageSize,
                BufferUsage.Storage | BufferUsage.CopyDestination,
                MemoryAccess.DeviceLocal,
                $"{name}.Pages"
            )
        );

        StagingCapacity = (long)slotCount * pageSize;

        staging = device.CreateBuffer(
            new(StagingCapacity, BufferUsage.CopySource, MemoryAccess.HostUpload, $"{name}.Staging")
        );
    }

    /// <summary>The one buffer every resident page lives in.</summary>
    public BufferHandle Pages { get; }

    /// <inheritdoc />
    public int PageSize { get; }

    /// <inheritdoc />
    public int SlotCount { get; }

    /// <summary>How many bytes are waiting to be copied into the pool.</summary>
    public long PendingBytes => stagingUsed;

    /// <summary>How many pages have been placed since this was created.</summary>
    public long Placements { get; private set; }

    /// <summary>How many have been evicted.</summary>
    public long Evictions { get; private set; }

    /// <summary>
    ///     How many placements were refused because the staging region was full.
    /// </summary>
    /// <remarks>
    ///     A positive number means the frame streamed more than one flush cycle can carry, which is
    ///     a frame that drew something coarser than it could have. Worth a counter rather than a log:
    ///     it is the difference between "the budget is too small" and "the frame is not flushing",
    ///     and those have opposite fixes.
    /// </remarks>
    public long Refusals { get; private set; }

    /// <summary>How many bytes the staging region holds between flushes.</summary>
    public long StagingCapacity { get; }

    /// <inheritdoc />
    public ValueTask<int> LoadAsync(PageKey key, Memory<byte> destination, CancellationToken cancellation) =>
        source.ReadAsync(key, destination, cancellation);

    /// <inheritdoc />
    /// <remarks>
    ///     The same three tests <see cref="Place" /> applies, asked before the residency service has
    ///     evicted anything to make room. Without it a frame that overruns the staging region pays for
    ///     each refusal with a resident page it then does not replace — see
    ///     <see cref="IPageStore.CanPlace" />, which is what this exists to answer honestly.
    /// </remarks>
    public bool CanPlace(PageKey key, int bytes) {
        _ = key;

        return !disposed && bytes > 0 && bytes <= PageSize && stagingUsed + bytes <= StagingCapacity;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Staged rather than written, for <see cref="GeometryBuffer" />'s reason: the pool is
    ///     device-local, so the only way in is a copy recorded on a list. The staging region is one
    ///     slot per pool slot, which is more than a frame ever stages and is the size at which the
    ///     bookkeeping disappears — a ring would save memory a page pool has already decided not to
    ///     care about.
    /// </remarks>
    public bool Place(PageKey key, int slot, ReadOnlySpan<byte> bytes) {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slot, SlotCount);

        if (bytes.Length > PageSize) {
            throw new ArgumentException(
                $"Page {key} is {bytes.Length} bytes and a slot is {PageSize}.",
                nameof(bytes)
            );
        }

        if (bytes.IsEmpty) {
            return false;
        }

        // Refused rather than written off the end. The staging region holds one flush cycle's worth
        // — a slot per pool slot, which is more than a frame can place, since a placement needs a
        // free slot and eviction only frees one by taking another page's. What overruns it is a
        // caller that never flushes, and telling it so beats a bounds error out of the driver.
        if (stagingUsed + bytes.Length > StagingCapacity) {
            Refusals++;
            return false;
        }

        var at = stagingUsed;
        device.Write(staging, at, bytes);
        stagingUsed += bytes.Length;

        pending.Add((at, (long)slot * PageSize, bytes.Length));
        Placements++;

        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Nothing is erased and nothing is copied: the next <see cref="Place" /> into this slot
    ///     overwrites it. What matters is that the frame that draws from the slot has already been
    ///     submitted, which is the residency service's contract with its caller rather than
    ///     something this can check.
    /// </remarks>
    public void Evict(PageKey key, int slot) {
        _ = key;
        _ = slot;

        Evictions++;
    }

    /// <summary>Records the copies everything placed since the last flush needs.</summary>
    /// <param name="list">An open command list, outside a render pass.</param>
    /// <returns>How many copies were recorded.</returns>
    /// <remarks>
    ///     The barriers are here rather than at the draw, on <see cref="GeometryBuffer.Flush" />'s
    ///     terms: this is the side that knows a write happened. The buffer is left in
    ///     <see cref="ResourceState.ShaderRead" /> rather than <c>VertexInput</c>, because a page
    ///     is read by a shader indexing bytes rather than fetched by the input assembler.
    /// </remarks>
    public int Flush(ICommandList list) {
        ArgumentNullException.ThrowIfNull(list);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (pending.Count == 0) {
            return 0;
        }

        list.Barrier(new([new(Pages, state, ResourceState.CopyDestination)], []));

        foreach (var (from, to, size) in pending) {
            list.CopyBuffer(staging, from, Pages, to, size);
        }

        list.Barrier(new([new(Pages, ResourceState.CopyDestination, ResourceState.ShaderRead)], []));

        state = ResourceState.ShaderRead;

        var count = pending.Count;
        pending.Clear();
        stagingUsed = 0;

        return count;
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        device.Destroy(Pages);

        if (staging.IsValid) {
            device.Destroy(staging);
            staging = default;
        }

        pending.Clear();
        _ = name;
    }
}
