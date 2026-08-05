// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Rendering;
using Xunit;

namespace Tests;

/// <summary>
///     Phase 7's page lifecycle, and improvement 6's second consumer.
/// </summary>
/// <remarks>
///     <para>
///         <b><see cref="PageResidency" /> was built in phase 2 with three consumers in view</b> —
///         geometry pages, texture mip tails and shadow pages — precisely so that it would not come out
///         geometry-shaped and the other two would not grow their own budgets. This is the first time
///         that claim can be tested rather than asserted, and the shadow pages are the case that tests
///         it hardest: <b>there is nothing to load</b>. A shadow page's content is <em>rendered</em>,
///         so the service's request queue, byte budget, eviction order and counters are the whole of
///         what it contributes, and everything about reading bytes is unused.
///     </para>
///     <para>
///         So there is no device anywhere in this file, exactly as there is none in
///         <c>PageResidencyTests</c>. What is being checked is a policy.
///     </para>
/// </remarks>
public class VirtualShadowPageTests {
    static (VirtualShadowPages Pages, PageResidency Residency) Pool(int pagesPerSide = 2) {
        var pages = new VirtualShadowPages(pagesPerSide);

        return (pages, new(pages, (long)pages.SlotCount * VirtualShadowPages.PageBytes));
    }

    static PageKey Key(int page) => new(VirtualShadowPages.Source, page);

    /// <summary>Runs the service until nothing is in flight.</summary>
    /// <remarks>
    ///     <c>PageResidencyTests</c>' <c>Settle</c>, and needed for the same reason: a load is
    ///     asynchronous even when it reads nothing, so the arrival is placed on a later call. What a
    ///     frame does is exactly this once per frame, which is why the tests below drive it that way
    ///     rather than reaching past it.
    /// </remarks>
    static void Settle(PageResidency residency, int maxLoads = 32) {
        // A stopwatch rather than an iteration count, which is `PageResidencyTests.Settle`'s shape and
        // is the difference between a test and a flake: a load completes on the thread pool, and a
        // fixed number of one-millisecond frames is a bet about how busy the machine is.
        var waited = System.Diagnostics.Stopwatch.StartNew();

        while (waited.Elapsed < TimeSpan.FromSeconds(30)) {
            residency.Service(maxLoads);

            if (residency.PendingRequests == 0 && residency.Loading == 0) {
                residency.Service(maxLoads);
                return;
            }

            Thread.Sleep(1);
        }

        Assert.Fail("The residency service never settled.");
    }

    /// <summary>
    ///     A page appears in the table when it has been drawn, and not when it was allocated.
    /// </summary>
    /// <remarks>
    ///     <b>The distinction that keeps a shading pass off a slot nobody has written.</b> A slot just
    ///     handed over holds whatever the last page left in it, so a table published on allocation has a
    ///     lookup reading another part of the world's depth — a shadow of something that is not there,
    ///     in the right place, at a plausible depth. Absent until drawn makes the failure "unshadowed
    ///     for a frame", which is the direction a shadow is allowed to be wrong in and is what
    ///     <c>ClusteredShading.Shadow</c>'s fall-through to the cascades covers.
    /// </remarks>
    [Fact]
    public void A_page_is_published_when_it_is_drawn_and_not_when_it_is_allocated() {
        var (pages, residency) = Pool();
        using var owned = residency;

        residency.Request(Key(7));
        Settle(residency);

        // Allocated: a slot exists, the page is owed a draw, and the table still says nothing.
        Assert.True(pages.TryGetAllocation(7, out var slot));
        Assert.Contains(7, pages.Pending);
        Assert.Equal(VirtualShadowMap.PageAbsent, pages.Table[7]);
        Assert.False(pages.TryGetSlot(7, out _));

        Assert.Equal([7], pages.TakePending());
        Assert.True(pages.Drawn(7));

        Assert.True(pages.TryGetSlot(7, out var published));
        Assert.Equal(slot, published);
        Assert.Empty(pages.Pending);
    }

    /// <summary>
    ///     A drawn page stays drawn, which is the whole of why this is cheaper than a cascade.
    /// </summary>
    /// <remarks>
    ///     Cascades redraw a level's worth of static geometry four times a frame because they can make
    ///     no claim about what is still true. A page whose slot it still holds, whose level's projection
    ///     did not move — the page snap is what makes that usual rather than rare — and whose casters did
    ///     not move, holds depths that are still correct. The steady state is an empty pending list, and
    ///     that is asserted here rather than described.
    /// </remarks>
    [Fact]
    public void A_drawn_page_is_not_owed_another_draw() {
        var (pages, residency) = Pool();
        using var owned = residency;

        residency.Request(Key(3));
        Settle(residency);
        pages.TakePending();
        pages.Drawn(3);

        // Asked for again, frame after frame, exactly as the marking pass asks every frame.
        for (var frame = 0; frame < 8; frame++) {
            residency.Request(Key(3));
            residency.Touch(Key(3));
            Settle(residency);

            Assert.Empty(pages.Pending);
            Assert.True(pages.TryGetSlot(3, out _));
        }
    }

    /// <summary>An invalidated page leaves the table before it is redrawn.</summary>
    /// <remarks>
    ///     What a moved caster costs. Unpublished as well as re-queued, because a stale page is one
    ///     whose depths are wrong — leaving it readable until the redraw is a frame of exactly the
    ///     shadow the invalidation is about, which is an object's shadow standing in the air after the
    ///     object has walked away.
    /// </remarks>
    [Fact]
    public void An_invalidated_page_is_unpublished_and_owed_a_draw() {
        var (pages, residency) = Pool();
        using var owned = residency;

        residency.Request(Key(1));
        Settle(residency);
        pages.TakePending();
        pages.Drawn(1);

        Assert.True(pages.Invalidate(1));

        Assert.False(pages.TryGetSlot(1, out _));
        Assert.Contains(1, pages.Pending);

        // And the slot is still this page's — an invalidation is about the depths, not the allocation.
        Assert.True(pages.TryGetAllocation(1, out _));

        // A page nothing allocated cannot be invalidated, which is what stops a stale index queueing a
        // draw into a slot that belongs to somebody else.
        Assert.False(pages.Invalidate(9999));
        Assert.False(pages.Invalidate(-1));
    }

    /// <summary>
    ///     Eviction clears the table before the slot is handed on.
    /// </summary>
    /// <remarks>
    ///     The failure this prevents is the sharpest one in the system: a slot reused while a shading
    ///     pass still points at it under the old virtual page reads an unrelated part of the world's
    ///     depth. The service cannot see the frame boundary and the store can, which is why
    ///     <see cref="IPageStore.Evict" /> exists at all.
    /// </remarks>
    [Fact]
    public void Eviction_clears_the_table_and_the_pending_list() {
        // Four slots, and five pages asked for: the fifth evicts the least recently used.
        var (pages, residency) = Pool();
        using var owned = residency;

        for (var page = 0; page < 4; page++) {
            residency.Request(Key(page));
        }

        Settle(residency);
        pages.TakePending(16);

        for (var page = 0; page < 4; page++) {
            pages.Drawn(page);
            Assert.True(pages.TryGetSlot(page, out _));
        }

        // Page zero is the oldest touch, so it is the one that goes.
        for (var page = 1; page < 4; page++) {
            residency.Touch(Key(page));
        }

        residency.Request(Key(4));
        Settle(residency);

        Assert.False(pages.TryGetSlot(0, out _));
        Assert.False(pages.TryGetAllocation(0, out _));
        Assert.DoesNotContain(0, pages.Pending);

        Assert.True(pages.TryGetAllocation(4, out _));
        Assert.Contains(4, pages.Pending);
    }

    /// <summary>The budget is a ceiling, not a target — the service's own criterion, here.</summary>
    /// <remarks>
    ///     Restated for this consumer because the consequence differs: a geometry pool over budget is
    ///     memory nobody planned for, and a shadow pool over budget is an atlas with more pages in it
    ///     than it has slots — which is two virtual pages reading one physical one, at two unrelated
    ///     places in the world.
    /// </remarks>
    [Fact]
    public void The_atlas_never_holds_more_pages_than_it_has_slots() {
        var (pages, residency) = Pool(pagesPerSide: 4);
        using var owned = residency;

        for (var page = 0; page < VirtualShadowMap.MaxPages; page += 7) {
            residency.Request(Key(page));
        }

        Settle(residency);

        Assert.True(residency.ResidentPages <= pages.SlotCount);
        Assert.True(residency.ResidentBytes <= residency.Budget);

        // And every published page names a slot that exists — the assertion the budget is *for*.
        var used = new HashSet<int>();

        for (var page = 0; page < VirtualShadowMap.MaxPages; page++) {
            if (!pages.TryGetAllocation(page, out var slot)) {
                continue;
            }

            Assert.InRange(slot, 0, pages.SlotCount - 1);
            Assert.True(used.Add(slot), $"Slot {slot} is claimed by two virtual pages at once.");
        }
    }

    /// <summary>Drawing is budgeted per frame, newest first.</summary>
    /// <remarks>
    ///     <see cref="PageResidency.Service" />'s trade, one layer up. A camera that turns to face a city
    ///     allocates every page it can see at once, and drawing all of them in one frame spends that
    ///     frame's whole caster budget on pages the next frame may not want either. What is left over is
    ///     not lost — the page is resident and empty, so the marking pass asks again and it is drawn a
    ///     frame or two later, which costs a soft edge rather than a stall.
    /// </remarks>
    [Fact]
    public void The_draws_are_budgeted_and_the_newest_go_first() {
        var (pages, residency) = Pool(pagesPerSide: 4);
        using var owned = residency;

        for (var page = 0; page < 8; page++) {
            residency.Request(Key(page));
        }

        Settle(residency, 16);

        var first = pages.TakePending(3);

        Assert.Equal(3, first.Length);
        Assert.Equal(5, pages.Pending.Count);

        // Newest first, so the pages the most recent allocation named are drawn before the older ones.
        Assert.DoesNotContain(first[0], pages.Pending);

        var rest = pages.TakePending(16);

        Assert.Equal(5, rest.Length);
        Assert.Empty(pages.Pending);
        Assert.Equal(8, first.Concat(rest).Distinct().Count());
    }

    /// <summary>A backlog beyond the budget lands, and within its own arithmetic.</summary>
    /// <remarks>
    ///     The claim the budget's amortisation rests on: a burst of allocations larger than one
    ///     frame's draws is not dropped, it is drained — none of it sampleable before its draw, all of
    ///     it sampleable within ceiling-of-marked-over-budget frames. A backlog that outlives that
    ///     bound is a queue something is re-queueing into faster than the budget drains it, which is
    ///     the stall <c>VirtualShadowRenderer.LightSnapDegrees</c> exists to prevent.
    /// </remarks>
    [Fact]
    public void A_backlog_beyond_the_budget_lands_within_its_own_frames() {
        var (pages, residency) = Pool(pagesPerSide: 8);
        using var owned = residency;

        for (var page = 0; page < 24; page++) {
            residency.Request(Key(page));
        }

        Settle(residency, 32);

        // Allocated, every one — and not one of them sampleable until its draw lands.
        for (var page = 0; page < 24; page++) {
            Assert.True(pages.TryGetAllocation(page, out _));
            Assert.False(pages.TryGetSlot(page, out _));
        }

        var frames = 0;

        while (pages.Pending.Count > 0) {
            Assert.True(++frames <= 3, "24 pages at 8 a frame outlived the 3 frames they arithmetic to.");

            foreach (var page in pages.TakePending(8)) {
                Assert.True(pages.Drawn(page));
            }
        }

        Assert.Equal(3, frames);

        for (var page = 0; page < 24; page++) {
            Assert.True(pages.TryGetSlot(page, out _));
        }
    }

    /// <summary>A marked page the frame dropped between take and draw is owed again.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The leak <see cref="VirtualShadowPages.Owe" /> exists to close.</b>
    ///         <see cref="VirtualShadowPages.TakePending" /> forgets what it hands out, and the paths
    ///         between it and <see cref="VirtualShadowPages.Drawn" /> can lose a page — a build that
    ///         bailed before recording, a level count that shrank under a taken page. The page is then
    ///         allocated, unpublished, and unreachable: <see cref="PageResidency.Request" /> refuses a
    ///         resident page, so the marks that keep it alive in the eviction order can never queue it
    ///         a draw. Touched forever, drawn never — a slot spent on nothing, for as long as
    ///         something looks at it.
    ///     </para>
    ///     <para>
    ///         The heal is the mark itself: <c>VirtualShadowAtlas.ServiceMarks</c> owes every marked
    ///         page that is allocated and unpublished, which turns the stall back into a draw the next
    ///         budget services.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_marked_page_the_frame_dropped_is_owed_again() {
        var (pages, residency) = Pool();
        using var owned = residency;

        residency.Request(Key(5));
        Settle(residency);

        // The drop: taken for a frame whose draws never recorded.
        Assert.Equal([5], pages.TakePending());

        // The stall: allocated, unpublished, and no longer queued — and the mark's request is
        // refused because the page is resident, so without Owe this state is permanent.
        Assert.True(pages.TryGetAllocation(5, out _));
        Assert.False(pages.TryGetSlot(5, out _));
        Assert.Empty(pages.Pending);

        residency.Request(Key(5));
        Settle(residency);
        Assert.Empty(pages.Pending);

        // The heal: the mark owes the page its draw, and the next budget lands it.
        Assert.True(pages.Owe(5));
        Assert.Equal([5], pages.TakePending());
        Assert.True(pages.Drawn(5));
        Assert.True(pages.TryGetSlot(5, out _));
    }

    /// <summary>An owed mark is drawn before the backlog, wherever it sat in the queue.</summary>
    /// <remarks>
    ///     <see cref="VirtualShadowPages.TakePending" /> drains newest first, so what Owe does with a
    ///     page already queued matters: moved to the newest end, what a pixel asked for this frame
    ///     outranks a backlog about where the camera and the light used to be. Without the move, a
    ///     refit's re-queue of a thousand stale pages would starve the dozen a frame is actually
    ///     looking at — a budget spent entirely on shadows nobody samples.
    /// </remarks>
    [Fact]
    public void An_owed_mark_outranks_the_stale_backlog() {
        var (pages, residency) = Pool(pagesPerSide: 4);
        using var owned = residency;

        for (var page = 0; page < 6; page++) {
            residency.Request(Key(page));
        }

        Settle(residency, 16);
        Assert.Equal(6, pages.Pending.Count);

        // Whatever order the queue settled in, the marked page is drawn first.
        Assert.True(pages.Owe(3));
        Assert.Equal(3, pages.TakePending(1)[0]);
    }

    /// <summary>A published page is not owed a redraw, or the map is a cascade again.</summary>
    /// <remarks>
    ///     The marks name every page the frame samples, drawn or not — the marking pass knows nothing
    ///     about residency. An Owe that re-queued published pages would redraw the whole visible set
    ///     every frame, which is the exact cost a virtual map exists to stop paying. What re-queues a
    ///     published page is <see cref="VirtualShadowPages.Invalidate" /> alone, and only for cause.
    /// </remarks>
    [Fact]
    public void A_published_page_is_not_owed_a_redraw() {
        var (pages, residency) = Pool();
        using var owned = residency;

        residency.Request(Key(2));
        Settle(residency);
        pages.TakePending();
        pages.Drawn(2);

        Assert.False(pages.Owe(2));
        Assert.Empty(pages.Pending);
        Assert.True(pages.TryGetSlot(2, out _));

        // And a page nothing allocated has nothing to owe.
        Assert.False(pages.Owe(9999));
        Assert.False(pages.Owe(-1));
    }

    /// <summary>The store answers the interface without a device or a byte of geometry in it.</summary>
    /// <remarks>
    ///     <b>The claim improvement 6 is actually making</b>, stated as an assertion: the same residency
    ///     service serves a consumer whose pages are drawn rather than read, and nothing shadow-shaped
    ///     had to be added to it. A load that reads nothing and completes immediately is the honest
    ///     answer for a page whose content is produced inside a frame, not a stub.
    /// </remarks>
    [Fact]
    public async Task Loading_a_shadow_page_reads_nothing_and_completes() {
        var pages = new VirtualShadowPages(2);

        Assert.Equal(0, await pages.LoadAsync(Key(0), new byte[VirtualShadowPages.PageBytes], CancellationToken.None));

        Assert.Equal(VirtualShadowMap.PageTexels * VirtualShadowMap.PageTexels * 4, pages.PageSize);
        Assert.Equal(4, pages.SlotCount);
        Assert.Equal(2 * VirtualShadowMap.PageTexels, pages.AtlasTexels);
        Assert.Equal(VirtualShadowMap.MaxPages, pages.Table.Length);
    }
}
