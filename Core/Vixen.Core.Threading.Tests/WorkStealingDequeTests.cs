// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Core.Threading.Tests;

public class WorkStealingDequeTests {
    [Fact]
    public void CapacityRoundsUpToAPowerOfTwo() {
        Assert.Equal(16, new WorkStealingDeque(9).Capacity);
        Assert.Equal(8, new WorkStealingDeque(8).Capacity);
    }

    [Fact]
    public void PopReturnsTheMostRecentlyPushedItem() {
        var deque = new WorkStealingDeque(8);
        deque.TryPush(1);
        deque.TryPush(2);
        deque.TryPush(3);

        Assert.True(deque.TryPop(out var first));
        Assert.Equal(3, first);
        Assert.True(deque.TryPop(out var second));
        Assert.Equal(2, second);
        Assert.True(deque.TryPop(out var third));
        Assert.Equal(1, third);
        Assert.False(deque.TryPop(out _));
    }

    [Fact]
    public void StealReturnsTheLeastRecentlyPushedItem() {
        var deque = new WorkStealingDeque(8);
        deque.TryPush(1);
        deque.TryPush(2);
        deque.TryPush(3);

        Assert.True(deque.TrySteal(out var first));
        Assert.Equal(1, first);
        Assert.True(deque.TrySteal(out var second));
        Assert.Equal(2, second);
    }

    [Fact]
    public void PushFailsWhenFullRatherThanOverwriting() {
        var deque = new WorkStealingDeque(4);

        for (var item = 0; item < 4; item++) {
            Assert.True(deque.TryPush(item));
        }

        Assert.False(deque.TryPush(99));

        // And the items that were already there are untouched.
        Assert.True(deque.TryPop(out var top));
        Assert.Equal(3, top);
    }

    [Fact]
    public void IndicesWrapWithoutLosingItems() {
        var deque = new WorkStealingDeque(4);

        // Several laps of the ring, so the mask arithmetic is exercised rather than assumed.
        for (var round = 0; round < 100; round++) {
            Assert.True(deque.TryPush(round));
            Assert.True(deque.TrySteal(out var stolen));
            Assert.Equal(round, stolen);
        }
    }

    [Fact]
    public void EmptyDequeYieldsNothingToEitherEnd() {
        var deque = new WorkStealingDeque(4);
        Assert.False(deque.TryPop(out _));
        Assert.False(deque.TrySteal(out _));
        Assert.Equal(0, deque.ApproximateCount);
    }

    /// <summary>
    ///     The property that matters: under contention every item comes out exactly once. A deque
    ///     that lost the occasional item would pass every test above and drop a job every few
    ///     million, which is the failure mode this structure has to be trusted not to have.
    /// </summary>
    [Fact]
    public void EveryItemIsTakenExactlyOnceUnderContention() {
        const int capacity = 1024;
        const int itemCount = 200_000;
        const int thiefCount = 4;

        var deque = new WorkStealingDeque(capacity);
        var taken = new int[itemCount];
        var start = new ManualResetEventSlim(false);
        var pushingDone = false;

        var thieves = new Thread[thiefCount];

        for (var index = 0; index < thiefCount; index++) {
            thieves[index] = new(() => {
                    start.Wait();

                    while (true) {
                        if (deque.TrySteal(out var item)) {
                            Interlocked.Increment(ref taken[item]);
                            continue;
                        }

                        if (Volatile.Read(ref pushingDone) && deque.ApproximateCount == 0) {
                            // One more sweep: ApproximateCount can read empty while an item is
                            // mid-flight between the owner's two writes.
                            if (!deque.TrySteal(out var last)) {
                                return;
                            }

                            Interlocked.Increment(ref taken[last]);
                        }
                    }
                }
            ) { IsBackground = true };

            thieves[index].Start();
        }

        start.Set();

        var next = 0;

        while (next < itemCount) {
            if (deque.TryPush(next)) {
                next++;
                continue;
            }

            // Full: the owner works its own end, which is exactly what a real worker does.
            if (deque.TryPop(out var item)) {
                Interlocked.Increment(ref taken[item]);
            }
        }

        while (deque.TryPop(out var item)) {
            Interlocked.Increment(ref taken[item]);
        }

        Volatile.Write(ref pushingDone, true);

        foreach (var thief in thieves) {
            Assert.True(thief.Join(TimeSpan.FromSeconds(30)), "A thief did not finish.");
        }

        while (deque.TryPop(out var item)) {
            Interlocked.Increment(ref taken[item]);
        }

        var missing = 0;
        var duplicated = 0;

        foreach (var count in taken) {
            if (count == 0) {
                missing++;
            } else if (count > 1) {
                duplicated++;
            }
        }

        Assert.Equal(0, missing);
        Assert.Equal(0, duplicated);
    }
}
