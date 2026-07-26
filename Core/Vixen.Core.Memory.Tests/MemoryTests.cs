// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using CsCheck;
using Vixen.Core;
using Xunit;

namespace Vixen.Core.Memory.Tests;

/// <summary>
///     Native arrays and the allocators. The properties that matter here are the ones a GPU driver
///     will punish rather than report: alignment actually honoured, two allocations never
///     overlapping, and a freed region genuinely becoming available again.
/// </summary>
public class MemoryTests {
    [Fact]
    public unsafe void A_native_array_is_aligned_to_what_was_asked_for() {
        foreach (var alignment in new[] { 16, 32, 64, 256, 4096 }) {
            using var array = new NativeArray<float>(100, alignment);
            Assert.Equal(0, (nint)array.Pointer % alignment);
            Assert.Equal(100, array.Length);
            Assert.Equal(400, array.ByteLength);
        }
    }

    [Fact]
    public void A_native_array_rejects_an_alignment_that_is_not_a_power_of_two() {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NativeArray<int>(4, 24));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NativeArray<int>(4, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NativeArray<int>(-1));
    }

    [Fact]
    public void A_native_array_reads_and_writes_through_its_span() {
        using var array = NativeArray<int>.Zeroed(8);

        Assert.Equal(new int[8], array.AsSpan().ToArray());

        array.AsSpan().Fill(7);
        Assert.Equal(7, array[3]);

        array[3] = 42;
        Assert.Equal(42, array.AsSpan()[3]);
        Assert.Equal(new[] { 7, 42, 7 }, array.AsSpan(2, 3).ToArray());
        Assert.Equal(32, array.AsBytes().Length);
    }

    [Fact]
    public void A_native_array_can_be_built_from_existing_data() {
        using var array = NativeArray<int>.From([1, 2, 3, 4]);

        Assert.Equal(new[] { 1, 2, 3, 4 }, array.AsSpan().ToArray());

        var sum = 0;
        foreach (var value in array) {
            sum += value;
        }

        Assert.Equal(10, sum);
    }

    [Fact]
    public void An_empty_native_array_holds_no_memory_and_is_safe_to_use() {
        var empty = NativeArray<int>.Empty;

        Assert.True(empty.IsEmpty);
        Assert.Equal(0, empty.Length);
        Assert.Equal(0, empty.AsSpan().Length);
        Assert.True(new NativeArray<int>(0).IsEmpty);

        // Disposing one that never allocated is a no-op rather than a crash.
        empty.Dispose();
        empty.Dispose();
    }

    [Fact]
    public void A_native_array_is_registered_with_the_leak_tracker_while_it_lives() {
        Assert.SkipUnless(LeakTracker.IsSupported, "Leak tracking is compiled out of this build.");

        LeakTracker.Reset();
        var before = LeakTracker.LiveCount;

        var array = new NativeArray<int>(16, name: "test allocation");
        Assert.Equal(before + 1, LeakTracker.LiveCount);

        // Which is what turns "the profiler says we are leaking" into an allocation site.
        Assert.Contains(LeakTracker.Snapshot(), report => report.Description == "test allocation");

        array.Dispose();
        Assert.Equal(before, LeakTracker.LiveCount);
        LeakTracker.Reset();
    }

    [Fact]
    public unsafe void An_arena_hands_out_distinct_aligned_ranges() {
        using var arena = new ArenaAllocator(4096);

        var first = (byte*)arena.Allocate(100, 16);
        var second = (byte*)arena.Allocate(100, 64);
        var third = (byte*)arena.Allocate(100, 16);

        Assert.Equal(0, (nint)first % 16);
        Assert.Equal(0, (nint)second % 64);
        Assert.Equal(0, (nint)third % 16);

        // Distinct and non-overlapping, in order.
        Assert.True(second >= first + 100);
        Assert.True(third >= second + 100);
        Assert.Equal(300, arena.BytesAllocated);
    }

    [Fact]
    public void An_arena_hands_out_typed_spans() {
        using var arena = new ArenaAllocator(4096);

        var span = arena.AllocateZeroed<int>(64);
        Assert.Equal(64, span.Length);
        Assert.Equal(new int[64], span.ToArray());

        span[10] = 5;
        Assert.Equal(5, span[10]);
        Assert.Equal(0, arena.Allocate<int>(0).Length);
    }

    [Fact]
    public void Resetting_an_arena_reclaims_everything_and_keeps_the_blocks() {
        using var arena = new ArenaAllocator(4096);

        for (var i = 0; i < 100; i++) {
            arena.Allocate<byte>(256);
        }

        var reserved = arena.BytesReserved;
        var blocks = arena.BlockCount;
        Assert.True(arena.BytesAllocated >= 25_600);

        arena.Reset();
        Assert.Equal(0, arena.BytesAllocated);

        // Same memory, handed out again — which is why a steady workload stops calling the system
        // allocator after the first few frames.
        for (var i = 0; i < 100; i++) {
            arena.Allocate<byte>(256);
        }

        Assert.Equal(reserved, arena.BytesReserved);
        Assert.Equal(blocks, arena.BlockCount);
        Assert.True(arena.PeakBytesAllocated >= arena.BytesAllocated);
    }

    [Fact]
    public unsafe void An_arena_scope_rewinds_to_where_it_opened() {
        using var arena = new ArenaAllocator(4096);
        arena.Allocate<byte>(100);
        var before = arena.BytesAllocated;

        void* inner;
        using (arena.Push()) {
            inner = arena.Allocate(1000, 16);
            Assert.True(arena.BytesAllocated > before);
        }

        Assert.Equal(before, arena.BytesAllocated);

        // And the next allocation reuses the scope's memory, which is the point of rewinding.
        Assert.Equal((nint)inner, (nint)arena.Allocate(1000, 16));
    }

    [Fact]
    public void An_arena_serves_an_allocation_larger_than_its_block_size() {
        // The block size is a tuning knob, not a ceiling.
        using var arena = new ArenaAllocator(1024);
        var span = arena.Allocate<byte>(100_000);

        Assert.Equal(100_000, span.Length);
        span[99_999] = 1;
        Assert.Equal(1, span[99_999]);
    }

    [Fact]
    public void A_disposed_arena_refuses_to_allocate() {
        var arena = new ArenaAllocator(1024);
        arena.Dispose();

        Assert.Throws<ObjectDisposedException>(() => arena.Allocate<byte>(16));
        Assert.Throws<ObjectDisposedException>(() => arena.Push());

        // Disposing twice is a no-op, not a double free.
        arena.Dispose();
    }

    [Fact]
    public void The_thread_local_arenas_are_independent_per_thread() {
        FrameArena.Release();

        var main = FrameArena.Frame;
        FrameArena.Frame.Allocate<byte>(64);
        Assert.Equal(64, FrameArena.Frame.BytesAllocated);

        ArenaAllocator? other = null;
        long otherAllocated = -1;

        var thread = new Thread(() => {
                other = FrameArena.Frame;
                otherAllocated = FrameArena.Frame.BytesAllocated;
                FrameArena.Release();
            }
        );

        thread.Start();
        thread.Join();

        Assert.NotSame(main, other);
        Assert.Equal(0, otherAllocated);

        FrameArena.ResetFrame();
        Assert.Equal(0, FrameArena.Frame.BytesAllocated);
        FrameArena.Release();
    }

    [Fact]
    public void A_buddy_allocator_rounds_to_powers_of_two_and_frees_back_to_whole() {
        var allocator = new BuddyAllocator(1024, 64);

        Assert.True(allocator.TryAllocate(100, 1, out var offset));
        Assert.True(allocator.TryGetSize(offset, out var size));

        // 100 bytes occupies 128: the internal fragmentation the structure trades for O(log n)
        // merging with no search.
        Assert.Equal(128, size);
        Assert.Equal(128, allocator.AllocatedBytes);

        Assert.True(allocator.Free(offset));
        Assert.Equal(0, allocator.AllocatedBytes);
        Assert.Equal(1024, allocator.LargestFreeBlock);
        Assert.False(allocator.Free(offset));
    }

    [Fact]
    public void A_buddy_allocator_satisfies_alignment_by_choosing_a_larger_block() {
        var allocator = new BuddyAllocator(1 << 20, 64);

        Assert.True(allocator.TryAllocate(64, 4096, out var offset));
        Assert.Equal(0, offset % 4096);

        Assert.True(allocator.TryAllocate(64, 4096, out var second));
        Assert.Equal(0, second % 4096);
        Assert.NotEqual(offset, second);
    }

    [Fact]
    public void A_buddy_allocator_reports_exhaustion_rather_than_overcommitting() {
        var allocator = new BuddyAllocator(256, 64);

        Assert.True(allocator.TryAllocate(256, 1, out _));
        Assert.False(allocator.TryAllocate(64, 1, out var offset));
        Assert.Equal(0, offset);
        Assert.False(allocator.TryAllocate(1024, 1, out _));
        Assert.Equal(0, allocator.LargestFreeBlock);
    }

    [Fact]
    public void Fragmentation_shows_up_as_a_largest_free_block_below_the_free_total() {
        var allocator = new BuddyAllocator(1024, 64);

        Assert.True(allocator.TryAllocate(64, 1, out var first));
        Assert.True(allocator.TryAllocate(64, 1, out _));
        Assert.True(allocator.TryAllocate(64, 1, out var third));
        Assert.True(allocator.TryAllocate(64, 1, out _));

        // Freeing two blocks that are not buddies leaves the space split, so plenty is free and
        // none of it is contiguous enough for a large request. This is the number worth watching.
        allocator.Free(first);
        allocator.Free(third);

        // Two of four 64-byte blocks still held, so 896 free — and the largest contiguous run is
        // far smaller than that.
        Assert.Equal(896, allocator.FreeBytes);
        Assert.True(allocator.LargestFreeBlock < allocator.FreeBytes);
        Assert.False(allocator.TryAllocate(768, 1, out _));
    }

    [Fact]
    public void Buddy_allocations_never_overlap_and_stay_inside_the_region() =>
        // The property a driver punishes rather than reports: two resources sharing bytes is
        // corruption that shows up as garbled geometry somewhere else entirely.
        Gen.Select(Gen.Int[0, 2], Gen.Int[1, 4000]).Array[1, 300].Sample(script => {
                const long total = 1 << 16;
                var allocator = new BuddyAllocator(total, 64);
                var live = new Dictionary<long, long>();

                foreach (var (operation, size) in script) {
                    if (operation == 2 && live.Count > 0) {
                        var victim = live.Keys.First();
                        Assert.True(allocator.Free(victim));
                        live.Remove(victim);
                        continue;
                    }

                    if (!allocator.TryAllocate(size, 1, out var offset)) {
                        continue;
                    }

                    Assert.True(allocator.TryGetSize(offset, out var reserved));
                    Assert.True(offset >= 0);
                    Assert.True(offset + reserved <= total);
                    Assert.True(reserved >= size);

                    foreach (var (otherOffset, otherSize) in live) {
                        Assert.True(
                            offset + reserved <= otherOffset || otherOffset + otherSize <= offset,
                            $"[{offset}, {offset + reserved}) overlaps [{otherOffset}, {otherOffset + otherSize})"
                        );
                    }

                    live[offset] = reserved;
                }

                Assert.Equal(live.Count, allocator.AllocationCount);
                Assert.Equal(live.Values.Sum(), allocator.AllocatedBytes);

                // And once everything is released the merging has to have put the region back
                // together, or the allocator leaks capacity a fragment at a time.
                foreach (var offset in live.Keys) {
                    Assert.True(allocator.Free(offset));
                }

                Assert.Equal(0, allocator.AllocatedBytes);
                Assert.Equal(total, allocator.LargestFreeBlock);
            }
        );

    [Fact]
    public void Resetting_a_buddy_allocator_returns_the_whole_region() {
        var allocator = new BuddyAllocator(1024, 64);
        allocator.TryAllocate(64, 1, out _);
        allocator.TryAllocate(64, 1, out _);

        allocator.Reset();

        Assert.Equal(0, allocator.AllocationCount);
        Assert.Equal(0, allocator.AllocatedBytes);
        Assert.Equal(1024, allocator.LargestFreeBlock);
    }
}
