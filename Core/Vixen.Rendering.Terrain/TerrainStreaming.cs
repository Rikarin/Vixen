// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Terrain;
using TerrainMap = Vixen.Terrain.Terrain;

namespace Vixen.Rendering.Terrain;

/// <summary>Where a tile's height chain comes from.</summary>
/// <remarks>
///     <para>
///         <b>The seam that decides whether this streams from disk or only into the atlas.</b>
///         <see cref="TerrainStreamer" /> is about <em>which</em> tiles a frame pays for; where their
///         bytes are read from is this, and the two are separable because the residency service
///         already is — <see cref="IPageStore.LoadAsync" /> is asynchronous precisely so that a source
///         backed by a file does not stall the frame.
///     </para>
///     <para>
///         ⚠ <b><see cref="TerrainTileSource" /> is the in-memory one and it is not a disk cache.</b>
///         A terrain the editor is sculpting is entirely in host memory by definition — it has an edit
///         stack — so streaming it saves the <em>upload</em> and nothing else. That is still the
///         expensive half: a 128×128-tile terrain is sixteen thousand block copies on its first frame,
///         and a source's radius turns that into a few dozen. A file-backed source is what makes the
///         host bytes optional too, and it needs a tile-addressable file, which
///         <see cref="TerrainStore" />'s v1 layout is not.
///     </para>
/// </remarks>
public interface ITerrainTileSource {
    /// <summary>How many samples a tile is across, which fixes the page size.</summary>
    int TileSamples { get; }

    /// <summary>How many tiles there are along each axis.</summary>
    (int X, int Z) TileCounts { get; }

    /// <summary>Reads one tile's whole mip chain, packed level after level.</summary>
    /// <param name="tileX">Its X index.</param>
    /// <param name="tileZ">Its Z index.</param>
    /// <param name="destination">Where to put them; at least the chain's byte count.</param>
    /// <param name="cancellation">Cancelled when the page is no longer wanted.</param>
    /// <returns>How many bytes were written.</returns>
    ValueTask<int> ReadAsync(int tileX, int tileZ, Memory<byte> destination, CancellationToken cancellation);
}

/// <summary>A tile source over a terrain that is already in memory.</summary>
/// <remarks>
///     ⚠ <b>Synchronous behind an asynchronous signature, and deliberately so.</b> Compositing a
///     tile's chain is arithmetic over arrays this object already holds; wrapping it in a thread would
///     add a scheduling hop to a few microseconds of work. What the signature buys is that the
///     file-backed source can replace this one without <see cref="PageResidency" /> changing.
/// </remarks>
public sealed class TerrainTileSource : ITerrainTileSource {
    readonly TerrainMap terrain;
    readonly ushort[] chain;

    /// <summary>Reads tiles out of a terrain.</summary>
    /// <param name="terrain">The terrain.</param>
    /// <exception cref="ArgumentNullException"><paramref name="terrain" /> is null.</exception>
    public TerrainTileSource(TerrainMap terrain) {
        ArgumentNullException.ThrowIfNull(terrain);

        this.terrain = terrain;
        chain = new ushort[TerrainMips.ChainSamples(terrain.Description.TileSamples)];
    }

    /// <inheritdoc />
    public int TileSamples => terrain.Description.TileSamples;

    /// <inheritdoc />
    public (int X, int Z) TileCounts => (terrain.Description.TilesX, terrain.Description.TilesZ);

    /// <inheritdoc />
    public ValueTask<int> ReadAsync(int tileX, int tileZ, Memory<byte> destination, CancellationToken cancellation) {
        cancellation.ThrowIfCancellationRequested();

        // ⚠ Resolved first. A tile whose edit layers have not been composited reads the last
        // composite, which after a stroke is the ground as it was before it — and a page loaded once
        // and then kept resident would show that for as long as the camera stayed near it.
        terrain.Resolve();

        var written = TerrainMips.Build(terrain, tileX, tileZ, chain);
        var bytes = MemoryMarshal.AsBytes(chain.AsSpan(0, (int)written));

        bytes.CopyTo(destination.Span);

        return ValueTask.FromResult(bytes.Length);
    }
}

/// <summary>Told that a tile's chain has arrived.</summary>
/// <param name="tileX">Its X index.</param>
/// <param name="tileZ">Its Z index.</param>
/// <param name="chain">Its levels, packed one after another.</param>
/// <remarks>
///     A delegate of its own rather than an <see cref="Action{T1,T2,T3}" />, because a
///     <see cref="ReadOnlySpan{T}" /> cannot be a generic type argument — and the alternative is
///     handing out the pool's array and an offset, which is handing out the pool.
/// </remarks>
public delegate void TerrainTileHandler(int tileX, int tileZ, ReadOnlySpan<byte> chain);

/// <summary>The pool of tiles a terrain keeps resident, and what arrives and leaves it.</summary>
/// <remarks>
///     <para>
///         <b>The pool is host memory holding chains that are about to be copied into the atlas</b>,
///         and a slot is one tile's chain. It is not the atlas itself: a copy into a texture needs a
///         command list, which arrives on the frame's thread long after a load came back off one of
///         the pool's threads. <see cref="Drain" /> is the hand-over.
///     </para>
///     <para>
///         ⚠ <b>A refusal is a frame lost and not an error</b> — <see cref="IPageStore.Place" />'s own
///         bargain. The pending list is bounded because the renderer can only record so many copies in
///         one frame, so a camera that jumps across a large terrain fills it, and the pages that do not
///         fit are asked for again next frame.
///     </para>
/// </remarks>
public sealed class TerrainTilePages : IPageStore {
    readonly ITerrainTileSource source;
    readonly byte[] pool;
    readonly int[] lengths;
    readonly List<(int TileX, int TileZ, int Slot, int Length)> pending = [];
    readonly HashSet<int> resident = [];
    readonly int tilesX;

    /// <summary>Creates a pool over a source.</summary>
    /// <param name="source">Where the bytes come from.</param>
    /// <param name="slots">How many tiles may be staged at once. At least one.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slots" /> is not positive.</exception>
    public TerrainTilePages(ITerrainTileSource source, int slots) {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(slots);

        this.source = source;
        tilesX = Math.Max(1, source.TileCounts.X);

        PageSize = (int)TerrainMips.ChainSamples(source.TileSamples) * sizeof(ushort);
        SlotCount = slots;

        pool = new byte[(long)slots * PageSize <= int.MaxValue ? slots * PageSize : 0];

        if (pool.Length == 0) {
            throw new ArgumentOutOfRangeException(nameof(slots), "That many tiles is more than two gigabytes of staging.");
        }

        lengths = new int[slots];
    }

    /// <inheritdoc />
    public int PageSize { get; }

    /// <inheritdoc />
    public int SlotCount { get; }

    /// <summary>How many tiles may be handed over in one frame.</summary>
    /// <remarks>
    ///     ⚠ <b>The number that keeps a camera cut from becoming a one-second frame.</b> Every pending
    ///     tile is a block copy per mip level, and a jump across a terrain wants every tile at once.
    /// </remarks>
    public int MaxPending { get; set; } = 16;

    /// <summary>How many tiles are staged and waiting to be copied.</summary>
    public int Pending => pending.Count;

    /// <summary>How many tiles the pool holds.</summary>
    public int Resident => resident.Count;

    /// <summary>Whether a tile's chain is in the pool.</summary>
    /// <param name="tileX">Its X index.</param>
    /// <param name="tileZ">Its Z index.</param>
    /// <returns>Whether it is.</returns>
    public bool IsResident(int tileX, int tileZ) => resident.Contains((tileZ * tilesX) + tileX);

    /// <inheritdoc />
    public ValueTask<int> LoadAsync(PageKey key, Memory<byte> destination, CancellationToken cancellation) =>
        source.ReadAsync(key.Index % tilesX, key.Index / tilesX, destination, cancellation);

    /// <inheritdoc />
    public bool Place(PageKey key, int slot, ReadOnlySpan<byte> bytes) {
        if (pending.Count >= MaxPending) {
            return false;
        }

        bytes.CopyTo(pool.AsSpan(slot * PageSize));
        lengths[slot] = bytes.Length;

        pending.Add((key.Index % tilesX, key.Index / tilesX, slot, bytes.Length));
        resident.Add(key.Index);

        return true;
    }

    /// <inheritdoc />
    public void Evict(PageKey key, int slot) {
        resident.Remove(key.Index);
        lengths[slot] = 0;

        // ⚠ Any pending hand-over for the slot goes with it. The slot is about to hold another tile's
        // bytes, so copying it after the eviction would put one tile's heights into another's block —
        // which is not a missing tile but a wrong one, and it reads as terrain from somewhere else.
        pending.RemoveAll(entry => entry.Slot == slot);
    }

    /// <summary>Hands over the tiles that have arrived since the last call.</summary>
    /// <param name="into">Told each tile's indices and its chain.</param>
    /// <exception cref="ArgumentNullException"><paramref name="into" /> is null.</exception>
    public void Drain(TerrainTileHandler into) {
        ArgumentNullException.ThrowIfNull(into);

        foreach (var (tileX, tileZ, slot, length) in pending) {
            into(tileX, tileZ, pool.AsSpan(slot * PageSize, length));
        }

        pending.Clear();
    }
}

/// <summary>Which tiles of a terrain a frame keeps loaded, and at what level the rest draw.</summary>
/// <remarks>
///     <para>
///         <b>[docs/plan/31 § D13]'s consumer, which was the piece that did not exist.</b>
///         <see cref="StreamingGrid" /> decides which cells a frame's sources want and
///         <see cref="PageResidency" /> loads, places and evicts them; both were built, tested and
///         reachable from nothing. This is the terrain that asks.
///     </para>
///     <para>
///         ⚠ <b>The coarse tail of every tile is pinned, and that is what makes a non-resident tile a
///         coarse tile rather than a hole.</b> A chain's last levels are a few hundred bytes — a 128
///         tile reduced to 16 is a 256th of it — so every tile in the world can afford its tail, and
///         nothing has to be dropped from the selection. What streaming decides is whether a tile has
///         its <em>fine</em> levels, and <see cref="LevelOf" /> is where that becomes a number the
///         vertex stage reads. Dropping the node instead would put a hole in the distance on the frame
///         a camera turned, which is the failure this arrangement exists to rule out.
///     </para>
///     <para>
///         ⚠ <b>The radius is a distance in metres and the grid is in tiles, so a small terrain
///         streams nothing.</b> That is correct rather than a degenerate case: a terrain whose every
///         tile is within the near radius is one that fits, and paying for the machinery would be
///         paying for a decision with one answer.
///     </para>
/// </remarks>
public sealed class TerrainStreamer : IDisposable {
    /// <summary>What page source a terrain's tiles are filed under.</summary>
    /// <remarks>
    ///     Arbitrary and distinct from the geometry pool's, which is all
    ///     <see cref="PageKey.Source" /> has ever meant — <see cref="PageResidency" />'s own remarks
    ///     say it never interprets one.
    /// </remarks>
    public const int PageSource = 0x7E44;

    readonly TerrainDescription description;
    readonly TerrainTilePages pages;
    readonly PageResidency residency;
    readonly StreamingGrid grid;
    bool disposed;

    /// <summary>Streams a terrain's tiles around whatever moves through it.</summary>
    /// <param name="description">The terrain's shape.</param>
    /// <param name="source">Where a tile's bytes come from.</param>
    /// <param name="budget">How many bytes of staged chains may be held. Positive.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source" /> is null.</exception>
    public TerrainStreamer(in TerrainDescription description, ITerrainTileSource source, long budget = 64L << 20) {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budget);

        this.description = description;

        var chainBytes = (int)TerrainMips.ChainSamples(description.TileSamples) * sizeof(ushort);

        // ⚠ At least one slot, and never more than the terrain has tiles. A pool larger than the
        // world is slots that can never be filled, and the budget is then a number that describes
        // nothing — see PageResidency's own clamp of the budget to the pool.
        var slots = Math.Clamp((int)Math.Min(int.MaxValue, budget / Math.Max(1, chainBytes)), 1, description.TileCount);

        pages = new(source, slots);
        residency = new(pages, Math.Max(chainBytes, (long)slots * chainBytes));

        grid = new(
            PageSource,
            Vector2.Zero,
            description.TileQuads * description.MetresPerQuad,
            description.TilesX,
            description.TilesZ
        );
    }

    /// <summary>The residency service, for anything that wants its counters.</summary>
    public PageResidency Residency => residency;

    /// <summary>The grid the cells are decided on, for its lead and its bounds.</summary>
    public StreamingGrid Grid => grid;

    /// <summary>The pool, for its pending hand-over.</summary>
    public TerrainTilePages Pages => pages;

    /// <summary>
    ///     The finest level a tile with no fine data may be drawn at.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Two by default, which is a tile drawn at a quarter of its samples.</b> Lower is a
    ///     larger pinned tail and a smaller visible difference when a tile arrives; higher is cheaper
    ///     and a visible pop. Zero would mean nothing is pinned and a non-resident tile is a hole.
    /// </remarks>
    public int CoarseLevel { get; set; } = 2;

    /// <summary>How many tiles the last <see cref="Update" /> counted as in use.</summary>
    public int TouchedTiles { get; private set; }

    /// <summary>Whether a tile's fine levels are loaded.</summary>
    /// <param name="tileX">Its X index.</param>
    /// <param name="tileZ">Its Z index.</param>
    /// <returns>Whether they are.</returns>
    public bool IsResident(int tileX, int tileZ) => pages.IsResident(tileX, tileZ);

    /// <summary>The finest level a node over a tile may be drawn at.</summary>
    /// <param name="tileX">The tile's X index.</param>
    /// <param name="tileZ">Its Z index.</param>
    /// <param name="level">The level the quadtree chose.</param>
    /// <returns>That level, or the coarse floor if the tile's fine data has not arrived.</returns>
    /// <remarks>
    ///     ⚠ <b>A floor rather than a rejection, and it is the whole degradation story.</b> A node
    ///     whose tile is still loading is drawn from the pinned tail — coarser than it asked for and
    ///     in the right place — which is what a person walking towards unloaded ground sees sharpen
    ///     rather than appear.
    /// </remarks>
    public float LevelOf(int tileX, int tileZ, float level) =>
        IsResident(tileX, tileZ) ? level : MathF.Max(level, CoarseLevel);

    /// <summary>Which tile a world position on the terrain's XZ plane is in.</summary>
    /// <param name="position">The place, in the terrain's own space.</param>
    /// <returns>Its tile indices, clamped to the terrain.</returns>
    public (int X, int Z) TileAt(Vector2 position) {
        var size = description.TileQuads * description.MetresPerQuad;

        return (
            Math.Clamp((int)MathF.Floor(position.X / size), 0, description.TilesX - 1),
            Math.Clamp((int)MathF.Floor(position.Y / size), 0, description.TilesZ - 1)
        );
    }

    /// <summary>Tells the pool what this frame's sources want, and services what it can.</summary>
    /// <param name="sources">Where the world has to be loaded around, in the terrain's own space.</param>
    /// <param name="maxLoads">How many loads may start this frame.</param>
    /// <returns>How many tiles are in use.</returns>
    /// <remarks>
    ///     ⚠ <b>Serviced here rather than by the caller, because a grid that requests and nothing that
    ///     services is a queue that grows for ever</b> — which is exactly the state the grid was left
    ///     in when it had no consumer at all.
    /// </remarks>
    public int Update(ReadOnlySpan<StreamingSource> sources, int maxLoads = 8) {
        ObjectDisposedException.ThrowIf(disposed, this);

        TouchedTiles = grid.Update(sources, residency);
        residency.Service(maxLoads);

        return TouchedTiles;
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
