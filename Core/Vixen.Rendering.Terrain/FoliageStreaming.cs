// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Foliage;

namespace Vixen.Rendering.Terrain;

/// <summary>The pool of foliage cells a frame keeps uploaded.</summary>
/// <remarks>
///     <para>
///         <b>The second half of [docs/plan/31 § D13], and it is the same shape as the first.</b>
///         <see cref="TerrainTilePages" /> makes a terrain's tiles pages; this makes a volume's cells
///         pages. What differs is that a cell's contents are instances rather than texels, so the
///         "load" is a count rather than a read — the bytes are in the volume already, because a
///         volume is what the scene deserialised.
///     </para>
///     <para>
///         ⚠ <b>The saving is the upload and not the memory, and saying so matters.</b>
///         <see cref="FoliageCullPass.Upload" /> writes every instance of every chunk into a device
///         buffer; over a forest of two thousand cells that is fifty thousand records rewritten every
///         time anything about the volume changes. Streaming makes it the few hundred cells a source
///         can reach. Getting the instances themselves off the heap needs a cell-addressable file,
///         which is the same thing terrain's file-backed tile source is waiting on.
///     </para>
/// </remarks>
public sealed class FoliageCellPages : IPageStore {
    readonly HashSet<int> resident = [];
    readonly int countX;

    /// <summary>Creates a pool over a window of cells.</summary>
    /// <param name="countX">How many cells along X the window is.</param>
    /// <param name="slots">How many cells may be uploaded at once. At least one.</param>
    /// <exception cref="ArgumentOutOfRangeException">The window or the pool has no extent.</exception>
    public FoliageCellPages(int countX, int slots) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(countX);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(slots);

        this.countX = countX;
        SlotCount = slots;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>One, and the budget is therefore a cell count.</b> A cell's real size is however many
    ///     instances somebody painted into it, which is not knowable when the pool is built and
    ///     changes under every stroke — so a byte budget here would be a number that meant a different
    ///     thing every frame. What a person can plan against is "how many cells", and that is what
    ///     <see cref="PageResidency.Budget" /> then holds.
    /// </remarks>
    public int PageSize => 1;

    /// <inheritdoc />
    public int SlotCount { get; }

    /// <summary>How many cells are uploaded.</summary>
    public int Resident => resident.Count;

    /// <summary>Whether a cell of the window is uploaded.</summary>
    /// <param name="index">Its index within the window.</param>
    /// <returns>Whether it is.</returns>
    public bool IsResident(int index) => resident.Contains(index);

    /// <inheritdoc />
    /// <remarks>
    ///     Nothing is read: the instances are in the volume. The page is the <em>decision</em>, and
    ///     the residency service is what makes that decision an LRU with a ceiling rather than a
    ///     radius test that keeps everything it ever touched.
    /// </remarks>
    public ValueTask<int> LoadAsync(PageKey key, Memory<byte> destination, CancellationToken cancellation) {
        cancellation.ThrowIfCancellationRequested();

        if (destination.Length > 0) {
            destination.Span[0] = 1;
        }

        return ValueTask.FromResult(Math.Min(1, destination.Length));
    }

    /// <inheritdoc />
    public bool Place(PageKey key, int slot, ReadOnlySpan<byte> bytes) {
        Changed |= resident.Add(key.Index);

        return true;
    }

    /// <inheritdoc />
    public void Evict(PageKey key, int slot) => Changed |= resident.Remove(key.Index);

    /// <summary>Whether the resident set has moved since it was last read.</summary>
    /// <remarks>
    ///     ⚠ <b>The flag <see cref="FoliageCullPass.Upload" /> is driven from.</b> Re-uploading a
    ///     volume every frame is the cost this exists to avoid, so a host has to be able to tell a
    ///     frame in which the resident set moved from the hundreds in which it did not.
    /// </remarks>
    public bool Changed { get; private set; }

    /// <summary>Says the resident set has been acted on.</summary>
    public void Accept() => Changed = false;

    /// <summary>Which cell of the window an index is.</summary>
    /// <param name="index">The index.</param>
    /// <returns>Its X and Z within the window.</returns>
    public (int X, int Z) CellOf(int index) => (index % countX, index / countX);
}

/// <summary>Which cells of a foliage volume a frame uploads.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The window is derived from the volume rather than declared, because a foliage grid has
///         no edges.</b> <see cref="FoliageCellGrid" /> indexes cells with signed integers and a forest
///         is wherever somebody painted one; <see cref="StreamingGrid" /> is a rectangle of cells with
///         an origin. So the rectangle is the bounding box of the chunks that exist, recomputed by
///         <see cref="Rebuild" /> whenever the volume grows past it.
///     </para>
///     <para>
///         ⚠ <b>A cell outside the window is uploaded rather than skipped.</b> That is the safe
///         direction: the window is stale only when somebody has just painted beyond it, and a tree
///         that appears and then is culled normally is a frame of extra work — where the other choice
///         is a tree an artist just placed and cannot see.
///     </para>
/// </remarks>
public sealed class FoliageStreamer : IDisposable {
    /// <summary>What page source a volume's cells are filed under.</summary>
    public const int PageSource = 0x7E45;

    readonly float cellSize;
    FoliageCellPages pages;
    PageResidency residency;
    StreamingGrid grid;
    FoliageCellKey origin;
    bool disposed;

    /// <summary>Streams a volume's cells around whatever moves through it.</summary>
    /// <param name="volume">The volume. Its extent fixes the window.</param>
    /// <param name="cells">How many cells may be uploaded at once. Positive.</param>
    /// <exception cref="ArgumentNullException"><paramref name="volume" /> is null.</exception>
    public FoliageStreamer(FoliageVolume volume, int cells = 512) {
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cells);

        cellSize = volume.Grid.CellSize;
        Cells = cells;

        (pages, residency, grid, origin) = Window(volume, cells, cellSize);
    }

    /// <summary>How many cells may be uploaded at once.</summary>
    public int Cells { get; }

    /// <summary>The residency service, for its counters.</summary>
    public PageResidency Residency => residency;

    /// <summary>The grid the cells are decided on, for its lead.</summary>
    public StreamingGrid Grid => grid;

    /// <summary>Whether the resident set moved since it was last accepted.</summary>
    public bool Changed => pages.Changed;

    /// <summary>Says the resident set has been acted on, so <see cref="Changed" /> is false again.</summary>
    public void Accept() => pages.Accept();

    /// <summary>Whether a cell's instances should be uploaded.</summary>
    /// <param name="cell">Which cell.</param>
    /// <returns>Whether it is resident, or outside the window this streamer knows about.</returns>
    public bool IsResident(FoliageCellKey cell) {
        var x = cell.X - origin.X;
        var z = cell.Z - origin.Z;

        if (x < 0 || z < 0 || x >= grid.CountX || z >= grid.CountZ) {
            return true;
        }

        return pages.IsResident((z * grid.CountX) + x);
    }

    /// <summary>Rebuilds the window from a volume whose extent has changed.</summary>
    /// <param name="volume">The volume.</param>
    /// <exception cref="ArgumentNullException"><paramref name="volume" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Everything resident is dropped, which is why this is not called every frame.</b> A
    ///         window that moved renumbers every cell — the index is a position within the rectangle —
    ///         so keeping the old residency would keep it against the wrong cells.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Nothing in the frame calls this, and that is a fact about the volumes rather than
    ///         an omission.</b> The one place a streamer is built is <c>TerrainSceneRenderer</c>'s
    ///         foliage stand, and the volumes it streams come from <c>AssetTerrainSource.Volume</c>,
    ///         which deserialises a volume once and — when a palette moves — constructs a <em>new</em>
    ///         one rather than mutating the cached instance. A new instance is a new key in the
    ///         renderer's stand table, so it gets a fresh stand and a correctly windowed streamer: the
    ///         re-windowing happens by construction. The paths that do paint into a volume
    ///         (<c>FoliageEdit</c>, <c>FoliageGrowth.Simulate</c>) are the editor's, and the editor
    ///         draws through <c>VegetationPresenter</c>, which uploads a volume whole and owns no
    ///         streamer at all.
    ///     </para>
    ///     <para>
    ///         What would need this is a writer that mutates a volume <em>in place</em> after a frame
    ///         has built a stand for it — planting or felling in a running game, an ecology stepped at
    ///         run time, or a hot-reload that refills the cached volume instead of replacing it. The
    ///         symptom would not be missing trees: <see cref="IsResident" /> answers true outside the
    ///         window, deliberately, so a cell painted beyond it uploads. It is that the window stops
    ///         covering the volume and the budget stops bounding the upload — which is the whole
    ///         saving. The call belongs beside the grown-volume test in
    ///         <c>TerrainSceneRenderer.UploadFoliage</c>, guarded on the volume's bounds having left
    ///         <see cref="Grid" />, so that the residency is dropped when the window moved and not
    ///         merely when an instance was added.
    ///     </para>
    /// </remarks>
    public void Rebuild(FoliageVolume volume) {
        ArgumentNullException.ThrowIfNull(volume);
        ObjectDisposedException.ThrowIf(disposed, this);

        residency.Dispose();
        (pages, residency, grid, origin) = Window(volume, Cells, cellSize);
    }

    /// <summary>Tells the pool what this frame's sources want, and services what it can.</summary>
    /// <param name="sources">Where the world has to be populated around.</param>
    /// <param name="maxLoads">How many cells may arrive this frame.</param>
    /// <returns>How many cells are in use.</returns>
    public int Update(ReadOnlySpan<StreamingSource> sources, int maxLoads = 64) {
        ObjectDisposedException.ThrowIf(disposed, this);

        var touched = grid.Update(sources, residency);

        residency.Service(maxLoads);

        return touched;
    }

    static (FoliageCellPages Pages, PageResidency Residency, StreamingGrid Grid, FoliageCellKey Origin) Window(
        FoliageVolume volume,
        int cells,
        float cellSize
    ) {
        var minX = int.MaxValue;
        var minZ = int.MaxValue;
        var maxX = int.MinValue;
        var maxZ = int.MinValue;

        foreach (var chunk in volume.Chunks) {
            minX = Math.Min(minX, chunk.Cell.X);
            minZ = Math.Min(minZ, chunk.Cell.Z);
            maxX = Math.Max(maxX, chunk.Cell.X);
            maxZ = Math.Max(maxZ, chunk.Cell.Z);
        }

        // An empty volume is a one-cell window at the origin rather than a refusal: a streamer built
        // before anything was painted is the ordinary case in an editor, and Rebuild is what a first
        // stroke calls.
        if (minX > maxX) {
            (minX, minZ, maxX, maxZ) = (0, 0, 0, 0);
        }

        var countX = maxX - minX + 1;
        var countZ = maxZ - minZ + 1;
        var pages = new FoliageCellPages(countX, Math.Clamp(cells, 1, countX * countZ));

        return (
            pages,
            new(pages, pages.SlotCount),
            new(PageSource, new(minX * cellSize, minZ * cellSize), cellSize, countX, countZ),
            new(minX, minZ)
        );
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        residency.Dispose();
    }
}
