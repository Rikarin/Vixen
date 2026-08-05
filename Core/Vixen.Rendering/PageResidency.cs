// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using Vixen.Rendering.Diagnostics;

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
///         6 of <c>docs/plan/22-virtualized-geometry.md</c>. Unreal runs Nanite streaming, virtual texture
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
///     <para>
///         ⚠ <b>Two threading contracts, and they are not the same one.</b>
///         <see cref="LoadAsync" /> is called on thread-pool threads and several calls are in flight
///         at once — <see cref="PageResidency.Service" />'s <c>maxLoads</c> is exactly how many — so an
///         implementation of it has to be safe under concurrency with itself and with the frame.
///         <see cref="Place" /> and <see cref="Evict" /> are called from
///         <see cref="PageResidency.Service" />, on the caller's own thread, and need no
///         synchronisation at all. Writing the whole interface to the second contract is the mistake
///         this paragraph exists to stop: a load that fills one buffer the store owns looks correct
///         until two pages are wanted in the same frame, and then it hands one page another page's
///         bytes — which surfaces as corrupt <em>content</em> rather than as a threading fault.
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
    /// <remarks>
    ///     ⚠ <b>Invoked concurrently, on thread-pool threads, and never on the frame's.</b> Up to
    ///     <c>maxLoads</c> of these are outstanding at any moment and the service serialises none of
    ///     them, so an implementation may not write to state it shares with another load or with the
    ///     frame — a scratch buffer belonging to the store is the usual way to get this wrong.
    ///     <paramref name="destination" /> is the one thing that is per call; anything else that has to
    ///     be written wants a lock, as <c>StreamMeshletPageSource</c>'s per-blob gate is.
    /// </remarks>
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

    /// <summary>Whether a placement would be taken, asked before anything is evicted for it.</summary>
    /// <param name="key">Which page.</param>
    /// <param name="bytes">How many bytes it would be given.</param>
    /// <returns>Whether the <see cref="Place" /> that follows would take them.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A refusal costs a frame; a refusal <em>after</em> an eviction costs a page.</b>
    ///         Making room for an arriving page evicts a resident one, so a store that says no once the
    ///         slot has been found has spent another page's residency on a placement that never
    ///         happened — and the page it spent was the least recently used, not the least wanted.
    ///         Asked first, the eviction never happens and <see cref="Place" />'s bargain stays what
    ///         its own remarks promise: nothing is lost but a frame.
    ///     </para>
    ///     <para>
    ///         Defaulted to true, because a sink with no ceiling has nothing to say. Only a store that
    ///         can refuse needs to answer, and the answer has to hold for the <see cref="Place" /> that
    ///         follows it immediately on the same thread — nothing else runs in between.
    ///     </para>
    /// </remarks>
    bool CanPlace(PageKey key, int bytes) => true;
}

/// <summary>A pinned working set larger than the pool that has to hold it.</summary>
/// <remarks>
///     <para>
///         Its own type rather than an <see cref="InvalidOperationException" />, on
///         <c>CompositorBindingException</c>'s reasoning: the caller can do something about this one,
///         and what it can do is a number in a constructor.
///     </para>
///     <para>
///         <b>Thrown from <see cref="PageResidency.Pin" /> and never from
///         <see cref="PageResidency.Service" />.</b> A pin is a load-time act — a mesh's root page, as
///         it registers — so this lands on whoever loaded the content, with a stack that names it,
///         before any frame has run. The same condition discovered a frame at a time would be an
///         exception out of the render loop, which stops the application for something that was
///         decided when the pool was sized.
///     </para>
/// </remarks>
public sealed class PageBudgetException : Exception {
    /// <summary>Creates the exception.</summary>
    /// <param name="key">The page whose pin did not fit.</param>
    /// <param name="pinned">How many pages would have been pinned, counting this one.</param>
    /// <param name="capacity">How many the budget holds.</param>
    public PageBudgetException(PageKey key, int pinned, int capacity)
        : base(
            $"Pinning {key} would make {pinned} pinned page(s) against a pool that holds {capacity}. "
            + "A pinned page is never evicted, so a pinned working set larger than the pool is a set "
            + "that can never all be resident — and every source past the pool's size would draw "
            + "nothing, for ever, with every counter reading healthy. Raise the pool's slot count to "
            + $"at least {pinned}, or pin fewer pages."
        ) {
        Key = key;
        Pinned = pinned;
        Capacity = capacity;
    }

    /// <inheritdoc />
    public PageBudgetException() { }

    /// <inheritdoc />
    public PageBudgetException(string message) : base(message) { }

    /// <inheritdoc />
    public PageBudgetException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>The page whose pin did not fit.</summary>
    public PageKey Key { get; }

    /// <summary>How many pages would have been pinned.</summary>
    public int Pinned { get; }

    /// <summary>How many the budget holds.</summary>
    public int Capacity { get; }
}

/// <summary>
///     One page-residency service: requests in, bytes in the pool, least-recently-used out.
/// </summary>
/// <remarks>
///     <para>
///         <b>Improvement 6 of <c>docs/plan/22-virtualized-geometry.md</c>, and the reason it is built in
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
///         <b>Requests are demand-driven and serviced newest first; the remainder stays queued.</b>
///         What asks for a page is the traversal, per frame, from what it actually wanted to draw — so
///         a request from three frames ago is about a camera that has moved, and <see cref="Service" />
///         reads the queue from the newest end and stops at its load budget. What it does <em>not</em>
///         do is throw the rest away, because a stack is only ever read from the top: an old request
///         costs nothing until the queue has drained, and the frame that drains it is by definition a
///         frame with I/O to spare. Dropping it instead would lose every page whose only asker asks
///         once, which is the failure mode pinning exists to rule out and is not worth reintroducing
///         for requests.
///     </para>
///     <para>
///         What a queue may not do is grow without bound, so it is capped at
///         <see cref="MaxPendingRequests" /> and the oldest go first — and a camera that jumped has a
///         queue about somewhere it is not, which is <see cref="ClearRequests" />' business. The
///         service cannot see a cut; whoever moved the camera can.
///     </para>
///     <para>
///         <b>Pinned pages are never evicted and never counted against the request queue.</b> A
///         mesh's root page is pinned, which is what makes an object draw at its coarsest level
///         rather than not at all — the guarantee the whole degradation story rests on. Pinned bytes
///         <em>do</em> count against the budget, because they are bytes — and a pinned working set
///         larger than the budget is a pool that cannot hold what it has been promised, which
///         <see cref="Pin" /> refuses by name with <see cref="PageBudgetException" /> rather than
///         discovering one page at a time in a frame that reports nothing.
///     </para>
/// </remarks>
public sealed class PageResidency : IDisposable {
    /// <summary>How long between two reports of the same refusal, in milliseconds.</summary>
    /// <remarks>
    ///     A refusal is a per-frame event and a log line per frame is a log nobody reads and a frame
    ///     nobody can profile. Five seconds is long enough that a steady stream reads as a handful of
    ///     lines over a session and short enough that the first one arrives while somebody is still
    ///     looking at the thing that caused it.
    /// </remarks>
    const long ReportInterval = 5_000;

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

    /// <summary>How many pages the budget holds, which is the ceiling on the pinned working set.</summary>
    readonly int budgetPages;

    long clock;
    long reportedRefusals;
    long reportedRejections;
    long reportedPinRefusals;
    long reportedAt;
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
        budgetPages = (int)(Budget / store.PageSize);

        // Four queues' worth of pool, floored at something a small pool cannot trip over. The cap is
        // not a policy about staleness — the stack already serves the newest first — it is the bound
        // that keeps a session that never drains from holding every page it ever asked about.
        MaxPendingRequests = Math.Max(1024, store.SlotCount * 4);

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

    /// <summary>
    ///     How many times a <em>pinned</em> page was queued and could not be given a slot.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Not the same reading as <see cref="Rejections" /> and not the same severity.</b> A
    ///     rejection is a frame drawn coarser; this is a page that something is relying on being
    ///     resident, and the request stays queued rather than being dropped so that the next frame
    ///     tries again. <see cref="Pin" />'s budget check is what makes this unreachable by
    ///     arithmetic — the counter is here because "unreachable" is a claim, and a claim about a
    ///     silent failure is worth a number.
    /// </remarks>
    public long PinRefusals { get; private set; }

    /// <summary>How many queued requests were dropped for being older than the queue's cap.</summary>
    /// <remarks>
    ///     A steadily rising number is a queue nothing drains: more distinct pages are being asked
    ///     for each frame than the pool could hold even if every load succeeded.
    /// </remarks>
    public long StaleRequests { get; private set; }

    /// <summary>
    ///     How many requests may wait before the oldest are dropped.
    /// </summary>
    /// <remarks>
    ///     The queue is serviced newest-first, so its tail is only ever read by a frame with nothing
    ///     newer to do — which makes a cap a bound on memory rather than a policy about staleness.
    ///     Defaults to four times the pool's slot count, floored at 1024.
    /// </remarks>
    public int MaxPendingRequests { get; set; }

    /// <summary>
    ///     Where refusals are reported, or null for a service nobody is watching.
    /// </summary>
    /// <remarks>
    ///     Settable rather than a constructor argument, on <c>GpuClusterVisibility.Residency</c>'s
    ///     terms: the pool is built where the device is and the logger arrives from the host. Nothing
    ///     is logged per frame — see <see cref="Service" />, which compares two longs when the frame
    ///     is healthy and formats nothing.
    /// </remarks>
    public ILogger? Logger { get; set; }

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
    /// <exception cref="PageBudgetException">
    ///     The pinned working set would be larger than the budget holds.
    /// </exception>
    /// <remarks>
    ///     <para>
    ///         What a mesh's root page gets. Pinning something not yet resident requests it and marks
    ///         it so that it is pinned when it arrives, which is what lets a caller pin at load time
    ///         rather than having to wait for the first frame that needed it.
    ///     </para>
    ///     <para>
    ///         <b>The budget is checked here, and here is the only place it can be checked usefully.</b>
    ///         A pinned page is never evicted, so once the pinned set is as large as the pool every
    ///         further pin is a page that can never be resident — and because the only caller pins once,
    ///         at registration, nothing would ever ask again. Refusing at the pin puts the failure on
    ///         the thread that loaded the content, with the two numbers that fix it, before a frame has
    ///         run; the alternative is a mesh that draws nothing for the life of the process while
    ///         every counter reads healthy. That is the one shape of wrongness this service is not
    ///         allowed to have.
    ///     </para>
    ///     <para>
    ///         ⚠ The key is recorded even when the page is already resident, so that
    ///         <see cref="pinned" /> is exactly the pinned working set rather than the part of it that
    ///         happened to be pinned early. Counting it any other way makes the check above ignore
    ///         whichever pins arrived after their page did.
    ///     </para>
    /// </remarks>
    public void Pin(PageKey key) {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (pinned.Add(key) && pinned.Count > budgetPages) {
            var attempted = pinned.Count;
            pinned.Remove(key);

            throw new PageBudgetException(key, attempted, budgetPages);
        }

        if (resident.TryGetValue(key, out var entry)) {
            if (!entry.Pinned) {
                resident[key] = entry with { Pinned = true };
                PinnedPages++;
            }

            return;
        }

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
    ///     Gives a page's slot back now: unpinned, unqueued, evicted if it was resident.
    /// </summary>
    /// <param name="key">Which page.</param>
    /// <returns>Whether it had been resident.</returns>
    /// <remarks>
    ///     <para>
    ///         What an unload calls, and the difference between it and <see cref="Unpin" /> is what a
    ///         level teardown needs. Unpinning hands the page back to the eviction order, where it
    ///         waits to be the least recently used of a pool it no longer belongs to; this takes the
    ///         slot back immediately, because the content is gone and the next thing loaded should get
    ///         the room rather than earn it.
    ///     </para>
    ///     <para>
    ///         ⚠ <see cref="IPageStore.Evict" /> is called, so a client pointing at the slot stops
    ///         before something else is written into it — the same frame-boundary contract an ordinary
    ///         eviction has, and the reason this is not simply a dictionary removal.
    ///     </para>
    /// </remarks>
    public bool Drop(PageKey key) {
        ObjectDisposedException.ThrowIf(disposed, this);

        pinned.Remove(key);

        if (requested.Remove(key)) {
            requests.Remove(key);
        }

        if (!resident.Remove(key, out var entry)) {
            return false;
        }

        if (entry.Pinned) {
            PinnedPages--;
        }

        store.Evict(key, entry.Slot);
        free.Push(entry.Slot);
        Evictions++;

        return true;
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

        Renew();

        // Pinned first, whatever order they were asked in. A pinned page is the floor of the whole
        // degradation story — it is what an object draws when nothing else has arrived — so leaving
        // it behind the newest request means an object that is invisible until the queue drains,
        // which is the one outcome pinning exists to rule out.
        for (var i = requests.Count - 1; i >= 0 && maxLoads > 0; i--) {
            var key = requests[i];

            if (!pinned.Contains(key)) {
                continue;
            }

            if (resident.ContainsKey(key)) {
                requests.RemoveAt(i);
                requested.Remove(key);

                continue;
            }

            if (!Reserve(key, out _)) {
                // ⚠ Counted and kept, never dropped. A pin is a standing promise and nothing renews
                // one — the only caller pins at registration and never again — so a pinned page taken
                // off this queue is a page nothing will ever ask for again, and whatever was relying
                // on it draws nothing for the life of the process with every counter reading healthy.
                // Pin's own budget check is what stops this being reachable; this is what stops it
                // being silent if it ever is.
                PinRefusals++;
                continue;
            }

            requests.RemoveAt(i);
            requested.Remove(key);

            Start(key);
            maxLoads--;
        }

        // Newest first: what the traversal asked for most recently is what the camera is looking at
        // now. The tail is kept rather than dropped, because a stack is read from the top — an old
        // request costs nothing until a frame has drained everything newer, and that frame has the
        // bandwidth for it by definition.
        for (var i = requests.Count - 1; i >= 0 && maxLoads > 0; i--) {
            var key = requests[i];

            // The loop above's, including the ones it could not place and deliberately left queued.
            if (pinned.Contains(key)) {
                continue;
            }

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

        Trim();
        Report();

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

    /// <summary>Puts back any pinned page that is neither resident nor on its way.</summary>
    /// <remarks>
    ///     <b>The queue renews requests; nothing renews a pin.</b> A traversal asks again for every
    ///     cut it still wants, every frame, so an ordinary request that falls off the queue is asked
    ///     for again a frame later and nothing is lost. A pin is asked for once, at registration — so
    ///     a load that was cancelled, or a request the cap trimmed, leaves a page that is promised to
    ///     be resident and that nobody is going to mention again. Finding it here is what makes
    ///     "pinned" mean what <see cref="Pin" /> says it means rather than "requested once, hopefully".
    /// </remarks>
    void Renew() {
        if (pinned.Count == 0) {
            return;
        }

        lock (gate) {
            foreach (var key in pinned) {
                if (resident.ContainsKey(key) || requested.Contains(key) || loading.Contains(key)) {
                    continue;
                }

                requested.Add(key);
                requests.Add(key);
            }
        }
    }

    /// <summary>Drops the oldest queued requests once the queue has grown past its cap.</summary>
    /// <remarks>
    ///     From the front, which is the old end: the stack is serviced from the back. A pinned page
    ///     trimmed here is put back by <see cref="Renew" /> on the next call, which is why the cap can
    ///     be a flat number rather than something that has to reason about what is pinned.
    /// </remarks>
    void Trim() {
        var excess = requests.Count - Math.Max(1, MaxPendingRequests);

        if (excess <= 0) {
            return;
        }

        for (var i = 0; i < excess; i++) {
            requested.Remove(requests[i]);
        }

        requests.RemoveRange(0, excess);
        StaleRequests += excess;
    }

    /// <summary>Says what has been refused, at most once every <see cref="ReportInterval" />.</summary>
    /// <remarks>
    ///     ⚠ <b>Two longs and a compare when the frame is healthy</b>, which is the only reason this
    ///     can sit in <see cref="Service" /> at all. Nothing is formatted, nothing is boxed and no
    ///     level is queried until a refusal has actually happened — and then at most one line per
    ///     five seconds per kind, because a refusal is a per-frame event and a line per frame is a log
    ///     nobody reads attached to a frame nobody can profile.
    /// </remarks>
    void Report() {
        var refusals = Rejections + PinRefusals;

        if (refusals == reportedRefusals || Logger is null) {
            return;
        }

        var now = Environment.TickCount64;

        if (reportedAt > 0 && now - reportedAt < ReportInterval) {
            return;
        }

        var rejections = Rejections - reportedRejections;
        var pins = PinRefusals - reportedPinRefusals;

        reportedAt = now;
        reportedRefusals = refusals;
        reportedRejections = Rejections;
        reportedPinRefusals = PinRefusals;

        if (pins > 0) {
            RenderingLog.PinnedPageRefused(Logger, pins, PinnedPages, budgetPages);
        }

        if (rejections > 0) {
            RenderingLog.PagesRefused(Logger, rejections, resident.Count, budgetPages, PinnedPages);
        }
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
    ///     <para>
    ///         ⚠ <b>Known limitation: a candidate can never lose.</b> There is no way to say "this page
    ///         is not worth what it would displace" — if anything unpinned is resident, the request is
    ///         satisfied by evicting it, whatever the two are. For an LRU keyed off what a frame drew
    ///         that is right, because the frame's own behaviour supplies the ordering. For any policy
    ///         with an <em>external</em> ordering — distance, screen size, importance — it is not: with
    ///         a stationary camera and more wanted pages than slots, every frame evicts a full pool's
    ///         worth and reloads it, the resident set alternating between two disjoint sets for ever. A
    ///         reclaim that compares the candidate against its victim and refuses when the victim wins
    ///         is what such a policy needs, and it is why <c>GrassResidency</c> keeps its own rather
    ///         than sharing this one. <see cref="Pin" /> is not a substitute: pinning the working set
    ///         makes <see cref="Oldest" /> return false permanently, which turns thrashing into
    ///         nothing loading at all.
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
    ///     <para>
    ///         Nothing about the pool is touched from the loading thread — the arrival is a queue and
    ///         the slot is chosen in <see cref="Place" />, on the thread that owns the frame. A slot
    ///         reserved before the load finished would be a slot held out of use for the length of a
    ///         disk read, which at eight loads a frame is most of the pool.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A store that does no I/O still pays for all of this, and two of the three do.</b>
    ///         <c>VirtualShadowPages</c> returns zero — its content is rendered, not read — and
    ///         <c>FoliageCellPages</c> returns a constant, and both are charged a thread-pool dispatch
    ///         and a <see cref="IPageStore.PageSize" />-byte array per page to compute nothing: sixteen
    ///         a frame for the shadow pages at a 64 KB page, sixty-four a frame for the foliage. The
    ///         fix is a synchronous fast path — a defaulted <c>TryLoad(PageKey, Span&lt;byte&gt;, out
    ///         int)</c> on <see cref="IPageStore" />, a scratch buffer reused across calls, and the
    ///         placement body shared with <see cref="Place" /> so the two cannot diverge on what they
    ///         count. It is not built here because it changes <em>when</em> a page becomes resident:
    ///         placing runs before starting, so a synchronous store's page lands in the same
    ///         <see cref="Service" /> call rather than the next, and that is a timing change worth
    ///         landing on its own rather than inside a defect fix.
    ///     </para>
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

            // ⚠ Asked before the slot is found, not after. Reserve evicts to make room, so a store
            // that refuses once it has a slot has cost the pool a resident page — the least recently
            // used one, which is a page something was drawing — to place nothing. The frame would
            // lose two pages instead of one, and neither the counter nor the store would say so.
            if (!store.CanPlace(next.Key, next.Length)) {
                Rejections++;
                continue;
            }

            if (!Reserve(next.Key, out var slot)) {
                Rejections++;
                continue;
            }

            if (!store.Place(next.Key, slot, next.Bytes.AsSpan(0, next.Length))) {
                // The sink is full for this frame after all — a store whose CanPlace is looser than
                // its Place. The slot was never taken, so there is nothing to give back; what the
                // page loses is a frame, and the next one asks again.
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
