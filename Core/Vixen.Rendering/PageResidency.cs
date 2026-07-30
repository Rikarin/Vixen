// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Rendering;

/// <summary>Which page of which thing.</summary>
/// <param name="Source">
///     What the page belongs to: a mesh, a texture, a shadow atlas. Assigned by whoever registered
///     it — the residency service never interprets it.
/// </param>
/// <param name="Index">Which page of that source.</param>
/// <remarks>
///     Two integers rather than an object, because there are hundreds of thousands of them and they
///     are compared, hashed and sorted every frame. A handle would be an allocation and a
///     dereference per comparison, for an identity that is already two numbers.
/// </remarks>
public readonly record struct PageKey(int Source, int Index);

/// <summary>Where a resident page's bytes ended up.</summary>
/// <param name="Slot">Which slot of the pool holds it.</param>
/// <param name="Offset">Where that slot starts, in bytes.</param>
public readonly record struct PagePlacement(int Slot, long Offset);

/// <summary>What a residency service needs of whoever owns the memory.</summary>
/// <remarks>
///     <para>
///         The seam that keeps <see cref="PageResidency" /> from being geometry-shaped — improvement
///         6 of <c>docs/virtualized-geometry.md</c>. Unreal runs Nanite streaming, virtual texture
///         streaming and the shadow page pool as three systems with three budgets and three eviction
///         policies; Vixen has none of them yet, which is an advantage exactly once. What is
///         <em>common</em> to all three is a request queue, a byte budget and an eviction order, and
///         what differs is where the bytes go and how they are read — so those two are here and
///         nothing else is.
///     </para>
///     <para>
///         Loading is asynchronous because the asset system's I/O is, and because the alternative is
///         a frame that blocks on a disk. Placing is synchronous because it is a copy into memory the
///         pool already owns.
///     </para>
/// </remarks>
public interface IPageStore {
    /// <summary>How many bytes one page occupies in the pool.</summary>
    int PageSize { get; }

    /// <summary>How many slots the pool has.</summary>
    int SlotCount { get; }

    /// <summary>Reads a page's bytes.</summary>
    /// <param name="key">Which page.</param>
    /// <param name="destination">Where to put them; at least <see cref="PageSize" /> bytes.</param>
    /// <param name="cancellation">Cancelled when the service is disposed, or the page is no longer wanted.</param>
    /// <returns>How many bytes were read, which may be short for the last page of a source.</returns>
    ValueTask<int> LoadAsync(PageKey key, Memory<byte> destination, CancellationToken cancellation);

    /// <summary>Puts a loaded page's bytes into a slot.</summary>
    /// <param name="key">Which page.</param>
    /// <param name="slot">Which slot it was given.</param>
    /// <param name="bytes">What was read.</param>
    /// <returns>Whether the bytes were taken.</returns>
    /// <remarks>
    ///     <b>A sink may be full, and saying so is not an error.</b> A pool that stages through host
    ///     memory has a fixed amount of it and reclaims it when the frame's copies are recorded, so a
    ///     frame that streams more than that has to be told to stop rather than to write off the end.
    ///     The service treats a refusal as it treats a budget it cannot meet: the slot goes back, the
    ///     page stays absent, and the frame draws something coarser. Nothing is lost but a frame,
    ///     because the request is demand-driven and the next frame asks again.
    /// </remarks>
    bool Place(PageKey key, int slot, ReadOnlySpan<byte> bytes);

    /// <summary>Says a page is no longer resident, and its slot is about to be reused.</summary>
    /// <param name="key">Which page.</param>
    /// <param name="slot">The slot it is giving up.</param>
    /// <remarks>
    ///     There is nothing to erase — the next <see cref="Place" /> overwrites the slot — so this is
    ///     the client's chance to stop pointing at it. A cluster whose page has gone has to become
    ///     un-drawable <em>before</em> the slot holds something else, which is a frame boundary the
    ///     service cannot see and the client can.
    /// </remarks>
    void Evict(PageKey key, int slot);
}

/// <summary>
///     One page-residency service: requests in, bytes in the pool, least-recently-used out.
/// </summary>
/// <remarks>
///     <para>
///         <b>Improvement 6 of <c>docs/virtualized-geometry.md</c>, and the reason it is built in
///         phase 2 rather than phase 7.</b> Geometry pages want this; so do texture mip tails
///         (<c>docs/plan/08</c>) and the virtual shadow map's pages (phase 7). Building it with one
///         consumer in view is how it becomes geometry-shaped and the other two grow their own — and
///         then there are three budgets to tune, which means there is no budget at all.
///     </para>
///     <para>
///         <b>The budget is a hard ceiling, and that is the criterion the phase is judged on.</b>
///         A scene four times over budget has to hold the budget, which means a request that cannot
///         be satisfied without evicting something is either satisfied by evicting something or not
///         satisfied. It is never satisfied by going over — the point of a pool is that its size is
///         known in advance, and a manager that treats its budget as a target is a manager that
///         reports a number nobody can plan against.
///     </para>
///     <para>
///         <b>Requests are demand-driven, and stale ones are dropped rather than serviced.</b> What
///         asks for a page is the traversal, per frame, from what it actually wanted to draw — so a
///         request from three frames ago is about a camera that has moved. <see cref="Service" />
///         takes the newest requests first and forgets the rest, which is what keeps a camera turning
///         quickly from queueing a minute of I/O for geometry that is already behind it.
///     </para>
///     <para>
///         <b>Pinned pages are never evicted and never counted against the request queue.</b> A
///         mesh's root page is pinned, which is what makes an object draw at its coarsest level
///         rather than not at all — the guarantee the whole degradation story rests on. Pinned bytes
///         <em>do</em> count against the budget, because they are bytes.
///     </para>
/// </remarks>
public sealed class PageResidency : IDisposable {
    readonly IPageStore store;
    readonly CancellationTokenSource cancellation = new();

    /// <summary>Every resident page, and the slot it is in.</summary>
    readonly Dictionary<PageKey, Entry> resident = [];

    /// <summary>Slots nothing is using, newest first — so a slot just freed is reused first.</summary>
    readonly Stack<int> free = new();

    /// <summary>
    ///     What has been asked for and not yet loaded, most recently asked first.
    /// </summary>
    /// <remarks>
    ///     A list used as a stack rather than a queue, because the newest request is the one about
    ///     the camera as it is now. It is also deduplicated on insert: a page every view wants is one
    ///     load, and a traversal that asks sixty times a second would otherwise queue sixty.
    /// </remarks>
    readonly List<PageKey> requests = [];

    readonly HashSet<PageKey> requested = [];

    /// <summary>Pages that must be resident, whether or not they are yet.</summary>
    /// <remarks>
    ///     Kept apart from <see cref="resident" /> because a page can be pinned before it arrives —
    ///     which is what lets a caller pin a mesh's root page at load time rather than having to wait
    ///     for the first frame that needed it.
    /// </remarks>
    readonly HashSet<PageKey> pinned = [];
    readonly HashSet<PageKey> loading = [];
    readonly Lock gate = new();

    /// <summary>Loads that have come back and are waiting for a slot on the host's thread.</summary>
    readonly Queue<(PageKey Key, byte[] Bytes, int Length)> arrived = new();

    long clock;
    bool disposed;

    /// <summary>Creates a service over a pool.</summary>
    /// <param name="store">Where the bytes come from and go.</param>
    /// <param name="budget">
    ///     How many bytes may be resident. Clamped to what the pool can hold, because a budget
    ///     above the pool's capacity is a promise the pool cannot keep.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="store" /> is null.</exception>
    public PageResidency(IPageStore store, long budget) {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budget);

        this.store = store;

        var capacity = (long)store.SlotCount * store.PageSize;
        Budget = Math.Min(budget, capacity);

        for (var slot = store.SlotCount - 1; slot >= 0; slot--) {
            free.Push(slot);
        }
    }

    /// <summary>How many bytes may be resident at once.</summary>
    public long Budget { get; }

    /// <summary>How many are.</summary>
    public long ResidentBytes => (long)resident.Count * store.PageSize;

    /// <summary>How many pages are.</summary>
    public int ResidentPages => resident.Count;

    /// <summary>How many are pinned, and therefore cannot be evicted to make room.</summary>
    public int PinnedPages { get; private set; }

    /// <summary>How many pages have been loaded since this was created.</summary>
    public long Loads { get; private set; }

    /// <summary>How many have been evicted.</summary>
    public long Evictions { get; private set; }

    /// <summary>
    ///     How many requests were dropped because nothing could be evicted to make room.
    /// </summary>
    /// <remarks>
    ///     The counter that says the budget is too small for the scene rather than that the manager
    ///     is broken. A frame with a positive number here drew something coarser than it asked for,
    ///     which is the designed behaviour and is still worth being able to see.
    /// </remarks>
    public long Rejections { get; private set; }

    /// <summary>How many loads are in flight.</summary>
    public int Loading {
        get {
            lock (gate) {
                return loading.Count;
            }
        }
    }

    /// <summary>How many requests are waiting.</summary>
    public int PendingRequests => requests.Count;

    /// <summary>Whether a page's bytes are in the pool.</summary>
    /// <param name="key">Which page.</param>
    public bool IsResident(PageKey key) => resident.ContainsKey(key);

    /// <summary>Where a resident page is, if it is.</summary>
    /// <param name="key">Which page.</param>
    /// <param name="placement">Its slot and byte offset.</param>
    public bool TryGetPlacement(PageKey key, out PagePlacement placement) {
        if (resident.TryGetValue(key, out var entry)) {
            placement = new(entry.Slot, (long)entry.Slot * store.PageSize);
            return true;
        }

        placement = default;
        return false;
    }

    /// <summary>
    ///     Says a page was used this frame, so it is not the one evicted next.
    /// </summary>
    /// <param name="key">Which page.</param>
    /// <remarks>
    ///     Separate from <see cref="Request" /> on purpose: a page that is already resident is not
    ///     requested, and if using it did not also refresh it then the pages a frame actually draws
    ///     would be exactly the ones it evicts. That is not a subtle failure — it is a pool that
    ///     thrashes hardest on the geometry closest to the camera.
    /// </remarks>
    public void Touch(PageKey key) {
        if (resident.TryGetValue(key, out var entry)) {
            resident[key] = entry with { Used = ++clock };
        }
    }

    /// <summary>Asks for a page, if it is not already resident or on its way.</summary>
    /// <param name="key">Which page.</param>
    /// <remarks>
    ///     Idempotent within a frame and across frames: a page asked for by six views is one load,
    ///     and one asked for every frame until it arrives is still one.
    /// </remarks>
    public void Request(PageKey key) {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (resident.ContainsKey(key) || !requested.Add(key)) {
            return;
        }

        lock (gate) {
            if (loading.Contains(key)) {
                requested.Remove(key);
                return;
            }
        }

        requests.Add(key);
    }

    /// <summary>Pins a page, so it is loaded and then never evicted.</summary>
    /// <param name="key">Which page.</param>
    /// <remarks>
    ///     What a mesh's root page gets. Pinning something not yet resident requests it and marks it
    ///     so that it is pinned when it arrives, which is what lets a caller pin at load time rather
    ///     than having to wait for the first frame that needed it.
    /// </remarks>
    public void Pin(PageKey key) {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (resident.TryGetValue(key, out var entry)) {
            if (!entry.Pinned) {
                resident[key] = entry with { Pinned = true };
                PinnedPages++;
            }

            return;
        }

        pinned.Add(key);
        Request(key);
    }

    /// <summary>Unpins a page, making it evictable again.</summary>
    /// <param name="key">Which page.</param>
    public void Unpin(PageKey key) {
        pinned.Remove(key);

        if (resident.TryGetValue(key, out var entry) && entry.Pinned) {
            resident[key] = entry with { Pinned = false };
            PinnedPages--;
        }
    }

    /// <summary>
    ///     Services the queue: places what has arrived, and starts what there is room for.
    /// </summary>
    /// <param name="maxLoads">
    ///     How many loads may be started this call. A ceiling on I/O per frame rather than on the
    ///     queue: a camera that turns to face a city asks for everything at once, and issuing all of
    ///     it would spend the frame's bandwidth on pages the next frame will not want either.
    /// </param>
    /// <returns>How many pages became resident.</returns>
    /// <remarks>
    ///     Placing happens before starting, so a page that arrives and a page that is wanted compete
    ///     for the same slots in the order they were decided — and so that the budget is checked
    ///     against what is really resident rather than against what was resident a frame ago.
    /// </remarks>
    public int Service(int maxLoads = 8) {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(maxLoads);

        var placed = Place();

        // Pinned first, whatever order they were asked in. A pinned page is the floor of the whole
        // degradation story — it is what an object draws when nothing else has arrived — so leaving
        // it behind the newest request means an object that is invisible until the queue drains,
        // which is the one outcome pinning exists to rule out.
        for (var i = requests.Count - 1; i >= 0 && maxLoads > 0; i--) {
            if (!pinned.Contains(requests[i])) {
                continue;
            }

            var key = requests[i];
            requests.RemoveAt(i);
            requested.Remove(key);

            if (resident.ContainsKey(key) || !Reserve(key, out _)) {
                continue;
            }

            Start(key);
            maxLoads--;
        }

        // Newest first: what the traversal asked for most recently is what the camera is looking at
        // now. The rest are dropped rather than kept, because a request is a statement about a frame.
        for (var i = requests.Count - 1; i >= 0 && maxLoads > 0; i--) {
            var key = requests[i];
            requests.RemoveAt(i);
            requested.Remove(key);

            if (resident.ContainsKey(key)) {
                continue;
            }

            if (!Reserve(key, out _)) {
                Rejections++;
                continue;
            }

            Start(key);
            maxLoads--;
        }

        return placed;
    }

    /// <summary>Drops every request that has not been started, without touching what is resident.</summary>
    /// <remarks>
    ///     What a view change calls. The queue is about a camera, and a camera that jumped has a
    ///     queue about somewhere it is not.
    /// </remarks>
    public void ClearRequests() {
        requests.Clear();
        requested.Clear();
    }

    /// <summary>
    ///     Whether a slot can be found for a page, evicting the least recently used if it must.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The budget is checked before the pool, because it is the smaller of the two by
    ///         construction and because it is the number a caller chose. A pool with free slots and a
    ///         full budget evicts, which is the behaviour that makes the budget mean something.
    ///     </para>
    ///     <para>
    ///         <b>Least recently <em>used</em>, not least recently loaded.</b> The distinction is the
    ///         whole reason <see cref="Touch" /> exists: a page loaded an hour ago and drawn every
    ///         frame since is the last thing to evict, and one loaded this frame for a camera that
    ///         has already turned away is the first.
    ///     </para>
    /// </remarks>
    bool Reserve(PageKey key, out int slot) {
        var wanted = (long)(resident.Count + 1) * store.PageSize;

        while (wanted > Budget || free.Count == 0) {
            if (!Oldest(out var victim)) {
                slot = -1;
                return false;
            }

            var entry = resident[victim];
            resident.Remove(victim);
            store.Evict(victim, entry.Slot);
            free.Push(entry.Slot);
            Evictions++;

            wanted = (long)(resident.Count + 1) * store.PageSize;
        }

        slot = free.Peek();
        _ = key;

        return true;
    }

    /// <summary>The least recently used unpinned page, or false when every resident page is pinned.</summary>
    bool Oldest(out PageKey key) {
        key = default;

        var oldest = long.MaxValue;
        var found = false;

        foreach (var (candidate, entry) in resident) {
            if (entry.Pinned || entry.Used >= oldest) {
                continue;
            }

            oldest = entry.Used;
            key = candidate;
            found = true;
        }

        return found;
    }

    /// <summary>Starts a load. The continuation runs off the host's thread and only enqueues.</summary>
    /// <remarks>
    ///     Nothing about the pool is touched from the loading thread — the arrival is a queue and the
    ///     slot is chosen in <see cref="Place" />, on the thread that owns the frame. A slot reserved
    ///     before the load finished would be a slot held out of use for the length of a disk read,
    ///     which at eight loads a frame is most of the pool.
    /// </remarks>
    void Start(PageKey key) {
        lock (gate) {
            if (!loading.Add(key)) {
                return;
            }
        }

        var buffer = new byte[store.PageSize];

        _ = Task.Run(
            async () => {
                try {
                    var read = await store.LoadAsync(key, buffer, cancellation.Token).ConfigureAwait(false);

                    lock (gate) {
                        arrived.Enqueue((key, buffer, read));
                    }
                } catch (OperationCanceledException) {
                    // Disposal, or a page nobody wants any more. Neither is an error, and the
                    // `loading` entry is removed below either way so a later request can retry.
                } finally {
                    lock (gate) {
                        loading.Remove(key);
                    }
                }
            },
            CancellationToken.None
        );
    }

    /// <summary>Puts what has arrived into slots, and answers how many made it.</summary>
    int Place() {
        var placed = 0;

        while (true) {
            (PageKey Key, byte[] Bytes, int Length) next;

            lock (gate) {
                if (arrived.Count == 0) {
                    return placed;
                }

                next = arrived.Dequeue();
            }

            // Resident already, because two requests raced or the page was loaded and evicted and
            // loaded again while this one was in flight. The bytes are the same bytes.
            if (resident.ContainsKey(next.Key)) {
                continue;
            }

            if (!Reserve(next.Key, out var slot)) {
                Rejections++;
                continue;
            }

            if (!store.Place(next.Key, slot, next.Bytes.AsSpan(0, next.Length))) {
                // The sink is full for this frame. The slot was never taken, so there is nothing to
                // give back; what the page loses is a frame, and the next one asks again.
                Rejections++;
                continue;
            }

            free.Pop();

            var isPinned = pinned.Contains(next.Key);
            resident[next.Key] = new(slot, ++clock, isPinned);

            if (isPinned) {
                PinnedPages++;
            }

            Loads++;
            placed++;
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        cancellation.Cancel();
        cancellation.Dispose();

        resident.Clear();
        requests.Clear();
        requested.Clear();
        pinned.Clear();
        free.Clear();
    }

    /// <param name="Slot">Which slot of the pool holds it.</param>
    /// <param name="Used">The clock reading when it was last touched.</param>
    /// <param name="Pinned">Whether it may be evicted.</param>
    readonly record struct Entry(int Slot, long Used, bool Pinned);
}
