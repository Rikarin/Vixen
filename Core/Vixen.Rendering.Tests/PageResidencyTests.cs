// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Vixen.Rendering;
using Xunit;

namespace Tests;

/// <summary>
///     The residency service: requests in, bytes in the pool, least-recently-used out.
/// </summary>
/// <remarks>
///     <para>
///         Improvement 6 of <c>docs/plan/22-virtualized-geometry.md</c>, and the reason it is phase 2 rather
///         than phase 7: geometry pages want this, and so do texture mip tails and the virtual shadow
///         map's pages. What is tested here is the part that is common to all three, against a store
///         that is not geometry — because a service that only works for the thing it was written for
///         is the thing improvement 6 says not to build.
///     </para>
///     <para>
///         The properties that matter are the ones whose failure is invisible: a budget that is a
///         target rather than a ceiling, an eviction order that picks the page being drawn, and a
///         pinned page that is not.
///     </para>
/// </remarks>
public class PageResidencyTests {
    /// <summary>A store that is a dictionary of byte arrays: no device, no geometry, no I/O.</summary>
    sealed class FakeStore : IPageStore {
        readonly Dictionary<PageKey, int> placed = [];

        public int PageSize { get; init; } = 1024;
        public int SlotCount { get; init; } = 8;

        /// <summary>How many more placements the sink will take, or -1 for a sink with no ceiling.</summary>
        /// <remarks>
        ///     <see cref="TerrainTilePages.MaxPending" /> and <c>MeshletPagePool</c>'s staging ceiling in
        ///     their smallest form: a store that says no, and says so honestly when asked in advance.
        /// </remarks>
        public int Ceiling { get; set; } = -1;

        /// <summary>Whether <see cref="CanPlace" /> lies, to check who pays for the lie.</summary>
        public bool HidesTheCeiling { get; set; }

        public int Refusals { get; private set; }

        /// <summary>How long a read takes, so the exit criterion's synthetic delay has a knob.</summary>
        public TimeSpan Delay { get; set; }

        public List<PageKey> Loaded { get; } = [];
        public List<PageKey> Evicted { get; } = [];

        public IReadOnlyDictionary<PageKey, int> Placed => placed;

        public async ValueTask<int> LoadAsync(PageKey key, Memory<byte> destination, CancellationToken cancellation) {
            if (Delay > TimeSpan.Zero) {
                await Task.Delay(Delay, cancellation).ConfigureAwait(false);
            }

            cancellation.ThrowIfCancellationRequested();

            // Recognisable bytes, so a page placed in the wrong slot is a wrong number.
            destination.Span.Fill((byte)(key.Index & 0xFF));

            lock (Loaded) {
                Loaded.Add(key);
            }

            return PageSize;
        }

        public bool CanPlace(PageKey key, int bytes) => HidesTheCeiling || Ceiling != 0;

        public bool Place(PageKey key, int slot, ReadOnlySpan<byte> bytes) {
            Assert.Equal(PageSize, bytes.Length);

            if (Ceiling == 0) {
                Refusals++;

                return false;
            }

            if (Ceiling > 0) {
                Ceiling--;
            }

            placed[key] = slot;

            return true;
        }

        public void Evict(PageKey key, int slot) {
            Assert.Equal(slot, placed[key]);
            placed.Remove(key);
            Evicted.Add(key);
        }
    }

    /// <summary>Runs frames until everything asked for has arrived, or the patience runs out.</summary>
    /// <remarks>
    ///     ⚠ <b>A deadline in wall-clock time, not a count of frames.</b> Two hundred frames of
    ///     <see cref="Thread.Sleep(int)" />(1) is two hundred milliseconds on an idle machine and an
    ///     unknown number on a busy one: the loads run on the thread pool, and a CI runner with
    ///     several test processes on it can leave a fifty-millisecond store delay unscheduled for
    ///     longer than the loop was ever willing to wait. That is what failed
    ///     <c>A_slow_load_does_not_block_the_frame</c> on one leg and nothing else — a test whose
    ///     subject is that a load takes time, giving up because the load took time.
    ///
    ///     Nothing waits the whole of this in the ordinary case; it returns on the frame the work
    ///     lands.
    /// </remarks>
    static void Settle(PageResidency residency, int frames = 200) {
        var waited = Stopwatch.StartNew();
        var patience = TimeSpan.FromSeconds(30);

        for (var frame = 0; frame < frames || waited.Elapsed < patience; frame++) {
            residency.Service();

            if (residency.PendingRequests == 0 && residency.Loading == 0) {
                residency.Service();
                return;
            }

            Thread.Sleep(1);
        }
    }

    /// <summary>Every line the service wrote, with the id it wrote it under.</summary>
    sealed class CaptureLogger : ILogger {
        public List<(int Id, LogLevel Level, string Message)> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Lines.Add((eventId.Id, logLevel, formatter(state, exception)));
    }

    /// <summary>Waits for the loads in flight to land, without servicing anything.</summary>
    /// <remarks>
    ///     <see cref="Settle" />'s opposite, and what a test about <em>placement</em> needs: servicing
    ///     between two arrivals would let the queue evict and reserve in between, which is exactly the
    ///     interleaving the assertion is trying to hold still.
    /// </remarks>
    static void Await(PageResidency residency) {
        var waited = Stopwatch.StartNew();

        while (residency.Loading > 0 && waited.Elapsed < TimeSpan.FromSeconds(30)) {
            Thread.Sleep(1);
        }
    }

    [Fact]
    public void A_requested_page_becomes_resident() {
        var store = new FakeStore();
        using var residency = new PageResidency(store, 8 * 1024);

        residency.Request(new(0, 3));
        Settle(residency);

        Assert.True(residency.IsResident(new(0, 3)));
        Assert.True(residency.TryGetPlacement(new(0, 3), out var placement));
        Assert.Equal(placement.Slot * (long)store.PageSize, placement.Offset);
        Assert.Equal(1, residency.Loads);
    }

    /// <summary>A page asked for by six views, or every frame until it arrives, is one load.</summary>
    [Fact]
    public void Repeated_requests_are_one_load() {
        var store = new FakeStore();
        using var residency = new PageResidency(store, 8 * 1024);

        for (var i = 0; i < 6; i++) {
            residency.Request(new(0, 1));
        }

        Settle(residency);

        for (var i = 0; i < 6; i++) {
            residency.Request(new(0, 1));
            residency.Service();
        }

        Assert.Equal(1, residency.Loads);
        Assert.Single(store.Loaded);
    }

    /// <summary>
    ///     The budget is a ceiling, not a target.
    /// </summary>
    /// <remarks>
    ///     The criterion phase 2 is judged on, in its smallest form. A manager that treats its budget
    ///     as something to aim at reports a number nobody can plan against — and the failure is not a
    ///     crash but an allocation on a device that had exactly as much memory as it said it had.
    /// </remarks>
    [Fact]
    public void The_budget_is_never_exceeded() {
        var store = new FakeStore { SlotCount = 32 };
        using var residency = new PageResidency(store, 4 * 1024);

        for (var i = 0; i < 32; i++) {
            residency.Request(new(0, i));
            Settle(residency);

            Assert.True(
                residency.ResidentBytes <= residency.Budget,
                $"{residency.ResidentBytes} bytes resident against a budget of {residency.Budget}."
            );
        }

        Assert.Equal(4, residency.ResidentPages);
        Assert.True(residency.Evictions > 0);
    }

    /// <summary>A budget above what the pool holds is clamped, rather than promised and broken.</summary>
    [Fact]
    public void A_budget_larger_than_the_pool_is_the_pool() {
        var store = new FakeStore { SlotCount = 4 };
        using var residency = new PageResidency(store, 1024 * 1024);

        Assert.Equal(4L * store.PageSize, residency.Budget);
    }

    /// <summary>
    ///     Least recently <em>used</em>, not least recently loaded.
    /// </summary>
    /// <remarks>
    ///     The distinction <see cref="PageResidency.Touch" /> exists for, and the one whose absence
    ///     is worst where it matters most: without it the pages a frame draws are exactly the pages
    ///     it evicts, so the pool thrashes hardest on the geometry closest to the camera.
    /// </remarks>
    [Fact]
    public void The_least_recently_used_page_is_the_one_evicted() {
        var store = new FakeStore { SlotCount = 8 };
        using var residency = new PageResidency(store, 2 * 1024);

        residency.Request(new(0, 0));
        residency.Request(new(0, 1));
        Settle(residency);

        Assert.Equal(2, residency.ResidentPages);

        // Page 0 was loaded first and is used now, so page 1 is the older of the two by use.
        residency.Touch(new(0, 0));

        residency.Request(new(0, 2));
        Settle(residency);

        Assert.True(residency.IsResident(new(0, 0)));
        Assert.False(residency.IsResident(new(0, 1)));
        Assert.True(residency.IsResident(new(0, 2)));
    }

    /// <summary>A pinned page survives any amount of pressure, and its slot is not offered.</summary>
    [Fact]
    public void A_pinned_page_is_never_evicted() {
        var store = new FakeStore { SlotCount = 8 };
        using var residency = new PageResidency(store, 2 * 1024);

        residency.Pin(new(0, 0));
        Settle(residency);

        Assert.Equal(1, residency.PinnedPages);

        for (var i = 1; i < 12; i++) {
            residency.Request(new(0, i));
            Settle(residency);

            Assert.True(residency.IsResident(new(0, 0)), $"The pinned page went at request {i}.");
        }
    }

    /// <summary>
    ///     When everything resident is pinned, a request is refused rather than the budget broken.
    /// </summary>
    /// <remarks>
    ///     The counter that says the budget is too small for the scene rather than that the manager
    ///     is broken. A frame with a positive <see cref="PageResidency.Rejections" /> drew something
    ///     coarser than it asked for — which is the designed behaviour, and is still worth being able
    ///     to see.
    /// </remarks>
    [Fact]
    public void A_request_that_cannot_be_satisfied_is_refused_rather_than_granted() {
        var store = new FakeStore { SlotCount = 8 };
        using var residency = new PageResidency(store, 2 * 1024);

        residency.Pin(new(0, 0));
        residency.Pin(new(0, 1));
        Settle(residency);

        Assert.Equal(2, residency.ResidentPages);

        residency.Request(new(0, 2));
        Settle(residency);

        Assert.False(residency.IsResident(new(0, 2)));
        Assert.Equal(2, residency.ResidentPages);
        Assert.True(residency.Rejections > 0);
        Assert.True(residency.ResidentBytes <= residency.Budget);
    }

    /// <summary>Unpinning gives the page back to the eviction order.</summary>
    [Fact]
    public void Unpinning_makes_a_page_evictable_again() {
        var store = new FakeStore { SlotCount = 8 };
        using var residency = new PageResidency(store, 1024);

        residency.Pin(new(0, 0));
        Settle(residency);

        residency.Unpin(new(0, 0));
        Assert.Equal(0, residency.PinnedPages);

        residency.Request(new(0, 1));
        Settle(residency);

        Assert.False(residency.IsResident(new(0, 0)));
        Assert.True(residency.IsResident(new(0, 1)));
    }

    /// <summary>
    ///     A frame's I/O is bounded, so a camera turning to face a city does not queue a minute of it.
    /// </summary>
    [Fact]
    public void A_frame_starts_at_most_the_loads_it_is_allowed() {
        var store = new FakeStore { SlotCount = 64, Delay = TimeSpan.FromMilliseconds(20) };
        using var residency = new PageResidency(store, 64 * 1024);

        for (var i = 0; i < 64; i++) {
            residency.Request(new(0, i));
        }

        residency.Service(maxLoads: 4);

        Assert.True(residency.Loading <= 4, $"{residency.Loading} loads in flight after a budget of four.");
        Assert.Equal(60, residency.PendingRequests);
    }

    /// <summary>
    ///     The newest request is serviced first, because a request is a statement about a frame.
    /// </summary>
    /// <remarks>
    ///     What keeps a camera turning quickly from spending its bandwidth on where it used to be
    ///     looking. Order matters here and nowhere else in the service, which is why it is the one
    ///     thing about the queue that is asserted.
    /// </remarks>
    [Fact]
    public void The_newest_request_is_serviced_first() {
        var store = new FakeStore { SlotCount = 64 };
        using var residency = new PageResidency(store, 64 * 1024);

        residency.Request(new(0, 10));
        residency.Request(new(0, 20));
        residency.Request(new(0, 30));

        residency.Service(maxLoads: 1);

        // The rest of the queue is dropped, so what settles is the one load that was started rather
        // than everything the loop would have got round to.
        residency.ClearRequests();
        Settle(residency);

        Assert.Equal([new PageKey(0, 30)], store.Loaded);
    }

    /// <summary>A queue about somewhere the camera is not can be thrown away whole.</summary>
    [Fact]
    public void Requests_can_be_dropped_without_touching_what_is_resident() {
        var store = new FakeStore { SlotCount = 8 };
        using var residency = new PageResidency(store, 8 * 1024);

        residency.Request(new(0, 0));
        Settle(residency);

        residency.Request(new(0, 1));
        residency.Request(new(0, 2));
        residency.ClearRequests();

        Assert.Equal(0, residency.PendingRequests);

        residency.Service();
        Settle(residency);

        Assert.True(residency.IsResident(new(0, 0)));
        Assert.False(residency.IsResident(new(0, 1)));
    }

    /// <summary>
    ///     A load that takes real time does not block, and lands when it lands.
    /// </summary>
    /// <remarks>
    ///     The synthetic I/O delay the phase-2 exit criterion asks for, in its smallest form: the
    ///     service is asked for a page, answers immediately that it does not have one, and has it
    ///     some frames later. Everything above it — the residency-aware cut — is written against that
    ///     and not against a load that happens to be instant.
    /// </remarks>
    [Fact]
    public void A_slow_load_does_not_block_the_frame() {
        var store = new FakeStore { Delay = TimeSpan.FromMilliseconds(50) };
        using var residency = new PageResidency(store, 8 * 1024);

        residency.Request(new(0, 0));

        var started = Environment.TickCount64;
        residency.Service();

        Assert.True(Environment.TickCount64 - started < 40, "Service waited for the load.");
        Assert.False(residency.IsResident(new(0, 0)));

        Settle(residency);
        Assert.True(residency.IsResident(new(0, 0)));
    }

    /// <summary>
    ///     Pinning more pages than the pool holds is refused by name, not discovered a frame at a time.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The silent permanent failure, reproduced at four slots instead of five hundred and
    ///     twelve.</b> The pinned loop used to take the key off the queue before finding out whether it
    ///     could be placed, then <c>continue</c> without counting anything: <c>pinned</c> still held the
    ///     key, <c>resident</c> did not, and nothing in the engine pins twice — the only call site is a
    ///     mesh's registration. So the fifth mesh drew nothing, for the life of the process, with
    ///     <see cref="PageResidency.Rejections" /> reading zero and every other counter reading healthy.
    /// </remarks>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The ≥<c>slots</c> scenario, at four slots instead of five hundred and twelve.</b> A
    ///         mesh's root page is pinned at registration, so a scene of more virtualized meshes than the
    ///         pool has slots used to produce meshes that drew nothing for ever — and the default is 512,
    ///         which a real level reaches.
    ///     </para>
    ///     <para>
    ///         Thrown from <see cref="PageResidency.Pin" /> rather than counted in
    ///         <see cref="PageResidency.Service" /> because a pin is a load-time act: this lands on the
    ///         thread that registered the mesh, with the two numbers that fix it, before a frame has run.
    ///         The same condition raised mid-frame would stop the application for something decided when
    ///         the pool was sized.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Pinning_more_pages_than_the_pool_holds_says_so() {
        var store = new FakeStore { SlotCount = 4 };
        using var residency = new PageResidency(store, 4 * 1024);

        for (var i = 0; i < 4; i++) {
            residency.Pin(new(i, 0));
        }

        Settle(residency);

        Assert.Equal(4, residency.ResidentPages);
        Assert.Equal(4, residency.PinnedPages);

        var refused = Assert.Throws<PageBudgetException>(() => residency.Pin(new(4, 0)));

        Assert.Equal(new(4, 0), refused.Key);
        Assert.Equal(5, refused.Pinned);
        Assert.Equal(4, refused.Capacity);

        // The numbers a person needs are in the sentence, not only in the properties.
        Assert.Contains("5", refused.Message, StringComparison.Ordinal);
        Assert.Contains("4", refused.Message, StringComparison.Ordinal);

        // And the refusal left nothing behind: the fifth mesh is not half-pinned.
        Assert.Equal(4, residency.PinnedPages);
        residency.Service();
        Assert.Equal(4, residency.ResidentPages);

        // Which is what makes the counter below unreachable rather than merely unobserved: a pinned
        // page can only fail to find a slot if the pinned set is larger than the pool, and it cannot
        // become larger than the pool.
        Assert.Equal(0L, residency.PinRefusals);
    }

    /// <summary>
    ///     A pin recorded while its page was already resident still counts against the pool.
    /// </summary>
    /// <remarks>
    ///     The hole the budget check would otherwise have: <see cref="PageResidency.Pin" /> used to
    ///     return early for a resident page without recording the key, so a service whose pins all
    ///     arrived after their pages would have counted none of them and refused nothing.
    /// </remarks>
    [Fact]
    public void A_pin_taken_after_the_page_arrived_counts_against_the_pool() {
        var store = new FakeStore { SlotCount = 3 };
        using var residency = new PageResidency(store, 3 * 1024);

        for (var i = 0; i < 3; i++) {
            residency.Request(new(i, 0));
            Settle(residency);
            residency.Pin(new(i, 0));
        }

        Assert.Equal(3, residency.PinnedPages);
        Assert.Throws<PageBudgetException>(() => residency.Pin(new(3, 0)));
    }

    /// <summary>
    ///     Nothing is evicted for a page the store was never going to take.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Speculative eviction.</b> Making room evicts the least recently used page — a page
    ///     something is drawing — and a store that then refuses has spent it on a placement that never
    ///     happened. The frame loses two pages instead of one, and the one it lost was the useful one.
    ///     <see cref="IPageStore.CanPlace" /> is asked first so the eviction does not happen at all.
    /// </remarks>
    [Fact]
    public void A_store_that_will_refuse_costs_no_eviction() {
        var store = new FakeStore { SlotCount = 2, Delay = TimeSpan.FromMilliseconds(20) };
        using var residency = new PageResidency(store, 2 * 1024);

        residency.Request(new(0, 0));
        residency.Request(new(0, 1));
        Settle(residency);

        Assert.Equal(2, residency.ResidentPages);
        Assert.Equal(0L, residency.Evictions);

        // One placement left in the sink, and two pages on their way to it — so the second to arrive
        // finds a full pool and a store that will say no.
        store.Ceiling = 1;

        residency.Request(new(0, 2));
        residency.Request(new(0, 3));
        residency.Service(maxLoads: 2);

        // Making room for the *request* is the budget doing its job, and is not what this is about.
        var toStart = residency.Evictions;
        Assert.Equal(1L, toStart);

        Await(residency);
        residency.Service(maxLoads: 0);

        Assert.Equal(1L, residency.Rejections);

        // ⚠ No second eviction. Without the question asked in advance, making room for the refused
        // placement would have taken the least recently used page — one the frame was drawing — and
        // put nothing in its place, leaving one page resident instead of two.
        Assert.Equal(toStart, residency.Evictions);
        Assert.Equal(2, residency.ResidentPages);
        Assert.True(residency.ResidentBytes <= residency.Budget);
    }

    /// <summary>A store whose advance answer is looser than its refusal still refuses safely.</summary>
    /// <remarks>
    ///     <see cref="IPageStore.CanPlace" /> is a contract, and a store that breaks it gets the old
    ///     behaviour rather than a broken pool: the eviction is spent, the page is not placed, the
    ///     counter says so and the budget still holds. Asserting it keeps that fallback honest — and
    ///     asserting the eviction it costs is what says the advance question is worth asking.
    /// </remarks>
    [Fact]
    public void A_store_that_hides_its_ceiling_pays_for_the_lie_and_nothing_else_does() {
        var store = new FakeStore { SlotCount = 2, Delay = TimeSpan.FromMilliseconds(20), HidesTheCeiling = true };
        using var residency = new PageResidency(store, 2 * 1024);

        residency.Request(new(0, 0));
        residency.Request(new(0, 1));
        Settle(residency);

        store.Ceiling = 1;

        residency.Request(new(0, 2));
        residency.Request(new(0, 3));
        residency.Service(maxLoads: 2);

        Await(residency);
        residency.Service(maxLoads: 0);

        Assert.Equal(1L, residency.Rejections);
        Assert.True(store.Refusals > 0, "The store was never actually asked to place it.");

        // The eviction the honest store did not have to pay, which is the whole of the difference.
        Assert.Equal(2L, residency.Evictions);
        Assert.Equal(1, residency.ResidentPages);
        Assert.True(residency.ResidentBytes <= residency.Budget);
    }

    /// <summary>
    ///     Dropping a page gives its slot straight back, pinned or not.
    /// </summary>
    /// <remarks>
    ///     What a level unload needs and <see cref="PageResidency.Unpin" /> does not give: unpinning
    ///     hands the page to the eviction order, where it waits to be the least recently used of a pool
    ///     it no longer belongs to. The content is gone, so the room should go to whatever is loaded
    ///     next rather than be earned by it.
    /// </remarks>
    [Fact]
    public void Dropping_a_pinned_page_returns_its_slot_and_lets_a_new_one_be_pinned() {
        var store = new FakeStore { SlotCount = 2 };
        using var residency = new PageResidency(store, 2 * 1024);

        residency.Pin(new(0, 0));
        residency.Pin(new(1, 0));
        Settle(residency);

        Assert.Equal(2, residency.PinnedPages);

        // The pool is full of pinned pages, so a third registration is refused — which is exactly the
        // state a level unload has to be able to get out of.
        Assert.Throws<PageBudgetException>(() => residency.Pin(new(2, 0)));

        Assert.True(residency.Drop(new(0, 0)));

        Assert.Equal(1, residency.PinnedPages);
        Assert.Equal(1, residency.ResidentPages);
        Assert.False(residency.IsResident(new(0, 0)));
        Assert.Contains(new PageKey(0, 0), store.Evicted);

        // The slot came back to the pool, so the next level's mesh registers and draws.
        residency.Pin(new(2, 0));
        Settle(residency);

        Assert.True(residency.IsResident(new(2, 0)));
        Assert.Equal(2, residency.PinnedPages);

        // Dropping something that is not there is not an error, and does not double-count.
        Assert.False(residency.Drop(new(9, 9)));
        Assert.Equal(2, residency.PinnedPages);
    }

    /// <summary>
    ///     A pin whose request went away is asked for again, because nothing else will ask.
    /// </summary>
    /// <remarks>
    ///     The queue renews itself — a traversal re-asks for every cut it still wants, every frame — and
    ///     a pin does not: the only caller pins once, at registration. So a pin whose request was cleared
    ///     by a camera cut has to be found by the service or it is lost for the life of the process.
    /// </remarks>
    [Fact]
    public void A_pin_survives_the_queue_being_thrown_away() {
        var store = new FakeStore { SlotCount = 4 };
        using var residency = new PageResidency(store, 4 * 1024);

        residency.Pin(new(0, 0));
        residency.Request(new(0, 1));

        // A camera cut: the queue is about somewhere the camera is not. The pin is not about a camera.
        residency.ClearRequests();
        Settle(residency);

        Assert.True(residency.IsResident(new(0, 0)), "The pinned page was lost with the queue.");
        Assert.False(residency.IsResident(new(0, 1)));
    }

    /// <summary>
    ///     The queue is capped, and the oldest go — the newest are what a frame is about.
    /// </summary>
    /// <remarks>
    ///     The class doc used to claim <see cref="PageResidency.Service" /> "forgets the rest" and it
    ///     kept every one of them. Keeping them is right — a stack is read from the top, so an old
    ///     request costs nothing until a frame has drained everything newer — but an unbounded queue is
    ///     not, so the doc now describes the cap and this asserts it.
    /// </remarks>
    [Fact]
    public void The_queue_is_bounded_and_the_oldest_are_what_go() {
        var store = new FakeStore { SlotCount = 4, Delay = TimeSpan.FromMilliseconds(50) };
        using var residency = new PageResidency(store, 4 * 1024) { MaxPendingRequests = 8 };

        for (var i = 0; i < 40; i++) {
            residency.Request(new(0, i));
        }

        residency.Service(maxLoads: 0);

        Assert.Equal(8, residency.PendingRequests);
        Assert.Equal(32, residency.StaleRequests);

        // What survived is the newest end, so the next frame's loads are about where the camera is.
        residency.Service(maxLoads: 1);
        Settle(residency);

        Assert.Contains(new PageKey(0, 39), store.Loaded);
        Assert.DoesNotContain(new PageKey(0, 0), store.Loaded);
    }

    /// <summary>
    ///     Refusals reach a log with an id, once, and a healthy frame writes nothing at all.
    /// </summary>
    /// <remarks>
    ///     <see cref="PageResidency.Rejections" /> was incremented in three places and read nowhere
    ///     outside a test, and there was no logger in the file or in any store — so the one signal that
    ///     says "the budget is too small for this scene" was a number nobody was looking at. It is a
    ///     warning rather than a counter for the reason every line in <c>RenderingLog</c> is one: a
    ///     frame that draws and quietly draws less than it was asked for is invisible to every
    ///     exception path.
    /// </remarks>
    [Fact]
    public void Refusals_are_logged_once_and_a_healthy_frame_logs_nothing() {
        var store = new FakeStore { SlotCount = 2 };
        var log = new CaptureLogger();
        using var residency = new PageResidency(store, 2 * 1024) { Logger = log };

        residency.Pin(new(0, 0));
        residency.Pin(new(0, 1));
        Settle(residency);

        // Nothing is wrong, so nothing is said — including across the hundred-odd frames Settle ran.
        Assert.Empty(log.Lines);

        residency.Request(new(0, 2));
        Settle(residency, frames: 4);

        Assert.True(residency.Rejections > 0);

        var line = Assert.Single(log.Lines);

        Assert.Equal(4001, line.Id);
        Assert.Equal(LogLevel.Warning, line.Level);

        // The numbers that tell somebody which fix this is: the pool is full and all of it is pinned.
        Assert.Contains("2", line.Message, StringComparison.Ordinal);

        // ⚠ And once, not once a frame. A refusal is a per-frame event, so a line per refusal is a log
        // nobody reads attached to a frame nobody can profile.
        for (var frame = 0; frame < 50; frame++) {
            residency.Request(new(0, 2));
            residency.Service();
        }

        Assert.True(residency.Rejections > 1);
        Assert.Single(log.Lines);
    }

    /// <summary>Disposal cancels what is in flight rather than leaving it to land on nothing.</summary>
    [Fact]
    public void Disposal_cancels_the_loads_in_flight() {
        var store = new FakeStore { Delay = TimeSpan.FromMilliseconds(50) };
        var residency = new PageResidency(store, 8 * 1024);

        residency.Request(new(0, 0));
        residency.Service();
        residency.Dispose();

        Thread.Sleep(100);

        Assert.DoesNotContain(new PageKey(0, 0), store.Loaded);
    }
}
