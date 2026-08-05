// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
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

    /// <summary>Brings whatever <see cref="ReadAsync" /> reads up to date, on the caller's thread.</summary>
    /// <remarks>
    ///     ⚠ <b>The half of the source that is allowed to mutate anything.</b>
    ///     <see cref="ReadAsync" /> runs on a thread-pool thread — <see cref="PageResidency" /> starts
    ///     every load through <see cref="Task.Run(Action)" /> — so anything it recomputed there would be
    ///     a write to shared state racing the frame. <see cref="TerrainStreamer.Update" /> calls this
    ///     immediately before it services the queue, which is the frame thread, and every load it then
    ///     dispatches reads state that is already current.
    /// </remarks>
    void Prepare();

    /// <summary>What a tile's bytes are at, as a number that changes whenever they change.</summary>
    /// <param name="tileX">Its X index.</param>
    /// <param name="tileZ">Its Z index.</param>
    /// <returns>Its revision. Compared for equality and never for order.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>What tells an arrival from a stale one, and the reason a load is allowed to read
    ///         a terrain the frame is still editing.</b> <see cref="Prepare" /> makes the source
    ///         current at the moment loads are dispatched, and says nothing about the frames a load is
    ///         in flight for. A sculpt, a spline or a layer toggle between those two moments rewrites
    ///         the very tile being read, and what comes back is then a chain of two terrains —
    ///         <see cref="TerrainTilePages" /> reads this before the load and again when the bytes are
    ///         back, and throws away anything whose number moved.
    ///     </para>
    ///     <para>
    ///         <b>Called from a loading thread as well as the frame's</b>, so an implementation over
    ///         mutable state owes it the same publication its bytes get — see
    ///         <see cref="TerrainMap.RevisionOf" />, which is the one that has to. A source whose bytes
    ///         cannot change under it, a file being the case this interface exists for, answers a
    ///         constant and is never refused.
    ///     </para>
    /// </remarks>
    int RevisionOf(int tileX, int tileZ);

    /// <summary>Reads one tile's whole mip chain, packed level after level.</summary>
    /// <param name="tileX">Its X index.</param>
    /// <param name="tileZ">Its Z index.</param>
    /// <param name="destination">Where to put them; at least the chain's byte count.</param>
    /// <param name="cancellation">Cancelled when the page is no longer wanted.</param>
    /// <returns>How many bytes were written.</returns>
    /// <remarks>
    ///     ⚠ <b>Called concurrently, and on threads that are not the frame's.</b> Up to
    ///     <c>maxLoads</c> reads are in flight at once and none of them is serialised against the
    ///     others, so an implementation may not write to anything it also owns — see
    ///     <see cref="IPageStore.LoadAsync" />, whose contract this one inherits. What is per-load is
    ///     <paramref name="destination" />, which the residency service allocates before it dispatches.
    /// </remarks>
    ValueTask<int> ReadAsync(int tileX, int tileZ, Memory<byte> destination, CancellationToken cancellation);
}

/// <summary>A tile source over a terrain that is already in memory.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Synchronous behind an asynchronous signature, and deliberately so.</b> Compositing a
///         tile's chain is arithmetic over arrays this object already holds; wrapping it in a thread
///         would add a scheduling hop to a few microseconds of work. What the signature buys is that
///         the file-backed source can replace this one without <see cref="PageResidency" /> changing.
///     </para>
///     <para>
///         ⚠ <b>Synchronous is not the same as single-threaded.</b> The residency service dispatches
///         every load through the thread pool whatever the source does with it, so several
///         <see cref="ReadAsync" /> calls run at once and none of them may touch the terrain's
///         composite, its dirty flags or a buffer of this object's own. Resolving is
///         <see cref="Prepare" />'s, and the chain is built straight into the caller's destination.
///     </para>
/// </remarks>
public sealed class TerrainTileSource : ITerrainTileSource {
    readonly TerrainMap terrain;

    /// <summary>Reads tiles out of a terrain.</summary>
    /// <param name="terrain">The terrain.</param>
    /// <exception cref="ArgumentNullException"><paramref name="terrain" /> is null.</exception>
    public TerrainTileSource(TerrainMap terrain) {
        ArgumentNullException.ThrowIfNull(terrain);

        this.terrain = terrain;
    }

    /// <inheritdoc />
    public int TileSamples => terrain.Description.TileSamples;

    /// <inheritdoc />
    public (int X, int Z) TileCounts => (terrain.Description.TilesX, terrain.Description.TilesZ);

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>A tile whose edit layers have not been composited reads the last composite</b>, which
    ///     after a stroke is the ground as it was before it — and a page loaded once and then kept
    ///     resident would show that for as long as the camera stayed near it. So the resolve has to
    ///     happen, and the only question was which thread pays for it. This one, because
    ///     <see cref="TerrainMap.Resolve" /> writes the composite and the frame thread calls it too.
    /// </remarks>
    public void Prepare() => terrain.Resolve();

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Per tile rather than per terrain, and that is the difference between a stroke costing
    ///     one tile a frame and costing every loaded tile one.</b> A sculpt dirties the handful of
    ///     tiles it touched; a terrain-wide counter would refuse every load in flight anywhere in the
    ///     world for as long as somebody was dragging a brush, which on a large terrain is the
    ///     streaming turning itself off whenever it is being used.
    /// </remarks>
    public int RevisionOf(int tileX, int tileZ) => terrain.RevisionOf(tileX, tileZ);

    /// <inheritdoc />
    public ValueTask<int> ReadAsync(int tileX, int tileZ, Memory<byte> destination, CancellationToken cancellation) {
        cancellation.ThrowIfCancellationRequested();

        // ⚠ Built straight into the destination rather than through a buffer this source owns. Up to
        // maxLoads reads run at once and a shared scratch array is one tile's heights landing in
        // another tile's page — which reads as corrupt content rather than as a threading fault. The
        // destination is per load by construction: PageResidency allocates it before it dispatches.
        //
        // Reading a composite the frame may be rewriting is deliberate and is not made safe here: a
        // chain built across a recomposite is a mixture, and what rules it out is the revision the
        // pool stamps this read with and re-checks before the bytes are allowed anywhere.
        var chain = MemoryMarshal.Cast<byte, ushort>(destination.Span);
        var written = TerrainMips.Build(terrain, tileX, tileZ, chain);

        return ValueTask.FromResult((int)written * sizeof(ushort));
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
///     <para>
///         ⚠ <b>A page is a chain and the revision it was read at, in that order, and the revision is
///         why the second half exists.</b> A load runs for as many frames as the thread pool takes to
///         get to it, and the terrain underneath it is one an editor may be sculpting — so a chain
///         that arrives is a statement about a tile as it was, and whether it is still true is a
///         question only the frame thread can answer. <see cref="Place" /> asks it, and refuses what
///         has gone out of date; the tile is then simply not resident, the grid asks for it again next
///         frame, and the one after that has the stroke in it.
///     </para>
/// </remarks>
public sealed class TerrainTilePages : IPageStore {
    /// <summary>How many bytes of a page come before its chain.</summary>
    /// <remarks>
    ///     ⚠ <b>The revision travels inside the page rather than beside it, because
    ///     <see cref="IPageStore" /> has nowhere to put per-load state.</b> <see cref="LoadAsync" />
    ///     is handed a destination and <see cref="Place" /> is handed bytes, and the only thing
    ///     joining them is the key. A table keyed on the tile would work exactly as long as no two
    ///     loads for one tile are ever alive at once — which is <see cref="PageResidency" />'s
    ///     bookkeeping to keep and not this class's to depend on. Four bytes at the front of the page
    ///     belong to that page by construction and cannot be got wrong from outside.
    /// </remarks>
    public const int HeaderSize = sizeof(int);

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

        PageSize = HeaderSize + ((int)TerrainMips.ChainSamples(source.TileSamples) * sizeof(ushort));
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

    /// <summary>How many arrivals were thrown away because their tile changed under them.</summary>
    /// <remarks>
    ///     ⚠ <b>Expected to be small and non-zero while somebody is sculpting, and zero otherwise.</b>
    ///     A number that keeps climbing on a terrain nobody is editing means something is calling
    ///     <see cref="TerrainMap.Resolve" /> on a tile that is not dirty, or invalidating far more than
    ///     a stroke touched — either way the streamer is re-reading tiles it already had.
    /// </remarks>
    public long StaleArrivals { get; private set; }

    /// <summary>Whether a tile's chain is in the pool.</summary>
    /// <param name="tileX">Its X index.</param>
    /// <param name="tileZ">Its Z index.</param>
    /// <returns>Whether it is.</returns>
    public bool IsResident(int tileX, int tileZ) => resident.Contains((tileZ * tilesX) + tileX);

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>The revision is taken before the read and not after it.</b> What the stamp has to mean
    ///     is "no recomposite of this tile has happened since the first sample was looked at" — so it
    ///     must be the number from before that sample, and a stamp taken afterwards would be the
    ///     counter sampled at the far end of a tear and then declared current. That is not a smaller
    ///     version of the bug; it is the bug with a check in front of it that always passes.
    /// </remarks>
    public async ValueTask<int> LoadAsync(PageKey key, Memory<byte> destination, CancellationToken cancellation) {
        var tileX = key.Index % tilesX;
        var tileZ = key.Index / tilesX;

        var revision = source.RevisionOf(tileX, tileZ);
        var read = await source.ReadAsync(tileX, tileZ, destination[HeaderSize..], cancellation).ConfigureAwait(false);

        BinaryPrimitives.WriteInt32LittleEndian(destination.Span, revision);

        return HeaderSize + read;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>A tile whose composite moved while the load was in flight is refused, and this is the
    ///     only place that can tell.</b> The read runs on a thread-pool thread and the terrain is
    ///     rewritten on the frame's — by <c>TerrainRenderer.Upload</c>, by
    ///     <see cref="TerrainStreamer.Update" />'s own prepare, and by every editor tool — so a chain
    ///     that spans a recomposite is a mixture of the ground before the stroke and after it, and one
    ///     that merely predates a recomposite is the ground before the stroke entire. Both are wrong
    ///     and both look the same from here, which is why one comparison rules out both.
    ///     <see cref="Place" /> runs on the frame thread, so any resolve that overlapped the read has
    ///     finished and bumped by the time this asks — which is what lets a single counter stand in
    ///     for a lock nobody can afford on <see cref="TerrainMap.Resolve" />.
    /// </remarks>
    public bool Place(PageKey key, int slot, ReadOnlySpan<byte> bytes) {
        if (pending.Count >= MaxPending || bytes.Length < HeaderSize) {
            return false;
        }

        var tileX = key.Index % tilesX;
        var tileZ = key.Index / tilesX;

        if (BinaryPrimitives.ReadInt32LittleEndian(bytes) != source.RevisionOf(tileX, tileZ)) {
            // Nothing is kept and nothing is marked resident, so the grid asks for the tile again next
            // frame and reads it against the composite as it now is. What the tile loses is a frame of
            // fine levels, drawn from the pinned tail meanwhile — the same bargain as a full pool.
            StaleArrivals++;

            return false;
        }

        bytes.CopyTo(pool.AsSpan(slot * PageSize));
        lengths[slot] = bytes.Length;

        pending.Add((tileX, tileZ, slot, bytes.Length));
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
    /// <remarks>
    ///     The chain and not the page: the revision is the pool's bookkeeping and has already done its
    ///     work by the time anything is drained, so what a caller is handed is the same run of levels
    ///     it would have got from a source it read itself.
    /// </remarks>
    public void Drain(TerrainTileHandler into) {
        ArgumentNullException.ThrowIfNull(into);

        foreach (var (tileX, tileZ, slot, length) in pending) {
            into(tileX, tileZ, pool.AsSpan((slot * PageSize) + HeaderSize, length - HeaderSize));
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
    readonly ITerrainTileSource source;
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
        this.source = source;

        // ⚠ The page and not the chain, because the pool's page carries a revision in front of its
        // levels. Budgeting by the chain would leave the budget a few bytes per slot short of what
        // PageResidency measures residency in, and the arithmetic that says "this many tiles fit"
        // would then refuse the last one for ever.
        var pageBytes = ((int)TerrainMips.ChainSamples(description.TileSamples) * sizeof(ushort))
            + TerrainTilePages.HeaderSize;

        // ⚠ At least one slot, and never more than the terrain has tiles. A pool larger than the
        // world is slots that can never be filled, and the budget is then a number that describes
        // nothing — see PageResidency's own clamp of the budget to the pool.
        var slots = Math.Clamp((int)Math.Min(int.MaxValue, budget / Math.Max(1, pageBytes)), 1, description.TileCount);

        pages = new(source, slots);
        residency = new(pages, Math.Max(pageBytes, (long)slots * pageBytes));

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
    ///     <para>
    ///         ⚠ <b>Serviced here rather than by the caller, because a grid that requests and nothing
    ///         that services is a queue that grows for ever</b> — which is exactly the state the grid
    ///         was left in when it had no consumer at all.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The source is prepared before anything is dispatched, and that ordering is the
    ///         invariant <see cref="ITerrainTileSource.ReadAsync" /> is written against.</b> Every load
    ///         <see cref="PageResidency.Service" /> starts runs on a thread-pool thread, so a source
    ///         that brought itself up to date in the read would be mutating shared state off the frame.
    ///         Doing it here means it happens once per frame on the thread that owns the terrain,
    ///         whatever the caller did or did not resolve beforehand.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Preparing says nothing about the frames a load spends in flight, which is the
    ///         other half and is <see cref="TerrainTilePages.Place" />'s.</b> This makes the source
    ///         current at the moment loads are dispatched; an edit landing two frames later rewrites
    ///         tiles that are still being read, and no amount of resolving beforehand can reach them.
    ///         What does is the revision each page carries, checked when it arrives.
    ///     </para>
    /// </remarks>
    public int Update(ReadOnlySpan<StreamingSource> sources, int maxLoads = 8) {
        ObjectDisposedException.ThrowIf(disposed, this);

        source.Prepare();

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
