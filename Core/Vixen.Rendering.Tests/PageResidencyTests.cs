// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
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

        public bool Place(PageKey key, int slot, ReadOnlySpan<byte> bytes) {
            Assert.Equal(PageSize, bytes.Length);
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
