// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Graphics.Vulkan.Tests;

/// <summary>
///     Suballocation arithmetic. An allocator's bugs surface as an out-of-memory three hours into a
///     session, which is the worst possible place to start debugging — so the arithmetic is tested
///     here, where a failure names the case.
/// </summary>
public sealed class SuballocatorTests {
    [Fact]
    public void AFreshBlockHandsOutItsWholeSelf() {
        var space = new Suballocator(1024);

        Assert.True(space.TryAllocate(1024, 1, out var offset));
        Assert.Equal(0, offset);
        Assert.Equal(1024, space.Used);
        Assert.False(space.TryAllocate(1, 1, out _));
    }

    [Fact]
    public void AllocationsAreAligned() {
        var space = new Suballocator(1024);

        Assert.True(space.TryAllocate(1, 1, out var first));
        Assert.True(space.TryAllocate(16, 256, out var second));

        Assert.Equal(0, first);
        Assert.Equal(256, second);
    }

    /// <summary>
    ///     Alignment padding stays free. Folding it into the allocation is the easy shortcut, and on a
    ///     block whose resources have mixed alignments it leaks a little memory per allocation and
    ///     never gives it back.
    /// </summary>
    [Fact]
    public void AlignmentPaddingIsReusable() {
        var space = new Suballocator(1024);

        Assert.True(space.TryAllocate(1, 1, out _));
        Assert.True(space.TryAllocate(16, 256, out _));
        Assert.True(space.TryAllocate(200, 1, out var padding));

        Assert.Equal(1, padding);
    }

    /// <summary>
    ///     Without coalescing, a block cycled through a few thousand allocations is a list of
    ///     thousands of adjacent free runs that together could hold anything and individually can hold
    ///     nothing — fragmentation that is entirely bookkeeping rather than real.
    /// </summary>
    [Fact]
    public void AdjacentFreeRunsCoalesce() {
        var space = new Suballocator(1024);

        Assert.True(space.TryAllocate(256, 1, out var a));
        Assert.True(space.TryAllocate(256, 1, out var b));
        Assert.True(space.TryAllocate(256, 1, out var c));

        space.Free(a, 256);
        space.Free(c, 256);
        space.Free(b, 256);

        Assert.Equal(1024, space.LargestFreeRun);
        Assert.True(space.IsEmpty);
    }

    [Fact]
    public void CoalescingWorksInEveryFreeOrder() {
        foreach (var order in (int[][]) [[0, 1, 2], [2, 1, 0], [1, 0, 2], [1, 2, 0], [0, 2, 1], [2, 0, 1]]) {
            var space = new Suballocator(768);
            var offsets = new long[3];

            for (var index = 0; index < 3; index++) {
                Assert.True(space.TryAllocate(256, 1, out offsets[index]));
            }

            foreach (var index in order) {
                space.Free(offsets[index], 256);
            }

            Assert.True(space.IsEmpty);
            Assert.Equal(768, space.LargestFreeRun);
        }
    }

    [Fact]
    public void AHoleIsReused() {
        var space = new Suballocator(1024);

        Assert.True(space.TryAllocate(256, 1, out _));
        Assert.True(space.TryAllocate(256, 1, out var middle));
        Assert.True(space.TryAllocate(256, 1, out _));

        space.Free(middle, 256);

        Assert.True(space.TryAllocate(256, 1, out var reused));
        Assert.Equal(middle, reused);
    }

    [Fact]
    public void AnOversizedRequestFails() {
        var space = new Suballocator(64);

        Assert.False(space.TryAllocate(65, 1, out _));
        Assert.False(space.TryAllocate(0, 1, out _));
        Assert.Equal(0, space.Used);
    }

    /// <summary>
    ///     The property that matters over a long session: allocate and free in a churning pattern
    ///     thousands of times and the block still ends up entirely free and able to hand out all of
    ///     itself. A leak of one byte per cycle would fail this and would take hours to notice in a
    ///     running engine.
    /// </summary>
    [Fact]
    public void ChurnLeavesNothingBehind() {
        var space = new Suballocator(1 << 20);
        var live = new List<(long Offset, long Size)>();
        var sizes = new[] { 64L, 128L, 192L, 256L, 320L, 1024L, 4096L };
        var alignments = new[] { 1L, 16L, 256L };

        for (var step = 0; step < 4000; step++) {
            var size = sizes[step % sizes.Length];
            var alignment = alignments[step % alignments.Length];

            if (space.TryAllocate(size, alignment, out var offset)) {
                Assert.Equal(0, offset % alignment);
                live.Add((offset, size));
            }

            // Free about half of what is live, oldest first, so that holes of varying sizes appear
            // and are reused rather than the pattern degenerating into a stack.
            if (live.Count > 8 && step % 3 == 0) {
                space.Free(live[0].Offset, live[0].Size);
                live.RemoveAt(0);
            }
        }

        foreach (var (offset, size) in live) {
            space.Free(offset, size);
        }

        Assert.True(space.IsEmpty);
        Assert.Equal(0, space.Used);
        Assert.Equal(1 << 20, space.LargestFreeRun);
    }

    /// <summary>Allocations never overlap, which no amount of "it looked right" establishes.</summary>
    [Fact]
    public void LiveAllocationsNeverOverlap() {
        var space = new Suballocator(8192);
        var live = new List<(long Offset, long Size)>();

        for (var step = 0; step < 200; step++) {
            var size = 16 + step % 97;

            if (!space.TryAllocate(size, 1 << step % 5, out var offset)) {
                space.Free(live[0].Offset, live[0].Size);
                live.RemoveAt(0);
                continue;
            }

            foreach (var (other, otherSize) in live) {
                Assert.True(
                    offset + size <= other || other + otherSize <= offset,
                    $"[{offset}, {offset + size}) overlaps [{other}, {other + otherSize})."
                );
            }

            live.Add((offset, size));
        }
    }

    [Theory]
    [InlineData(0, 256, 0)]
    [InlineData(1, 256, 256)]
    [InlineData(256, 256, 256)]
    [InlineData(257, 256, 512)]
    [InlineData(100, 1, 100)]
    [InlineData(100, 0, 100)]
    public void AlignRoundsUp(long value, long alignment, long expected) =>
        Assert.Equal(expected, Suballocator.Align(value, alignment));
}
