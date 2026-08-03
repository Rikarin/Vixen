// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Platform.Tests;

public class PlatformEventBufferTests {
    [Fact]
    public void EventsComeOutInTheOrderTheyWentIn() {
        var buffer = new PlatformEventBuffer();

        for (var index = 0; index < 10; index++) {
            buffer.Post(PlatformEvent.Keyboard(PlatformEventKind.KeyDown, 1, index, Key.A, KeyModifiers.None));
        }

        var drained = buffer.Drain();

        Assert.Equal(10, drained.Length);

        for (var index = 0; index < 10; index++) {
            Assert.Equal(index, drained[index].Timestamp);
        }
    }

    [Fact]
    public void ADrainTakesEverythingAndLeavesNothing() {
        var buffer = new PlatformEventBuffer();
        buffer.Post(PlatformEvent.Application(PlatformEventKind.Quit, 0));

        Assert.Equal(1, buffer.PendingCount);
        Assert.Equal(1, buffer.Drain().Length);
        Assert.Equal(0, buffer.PendingCount);
        Assert.Equal(0, buffer.Drain().Length);
    }

    /// <summary>
    ///     The double buffering is only correct if a drain hands back a buffer the producer has
    ///     stopped writing to. Posting between two drains must not disturb the span the first one
    ///     returned.
    /// </summary>
    [Fact]
    public void PostingAfterADrainDoesNotDisturbWhatTheDrainReturned() {
        var buffer = new PlatformEventBuffer();
        buffer.Post(PlatformEvent.Keyboard(PlatformEventKind.KeyDown, 1, 100, Key.A, KeyModifiers.None));

        var first = buffer.Drain();

        for (var index = 0; index < 50; index++) {
            buffer.Post(PlatformEvent.Keyboard(PlatformEventKind.KeyDown, 2, index, Key.B, KeyModifiers.None));
        }

        Assert.Equal(1, first.Length);
        Assert.Equal(100, first[0].Timestamp);
        Assert.Equal(Key.A, first[0].Key);
    }

    [Fact]
    public void TheBufferGrowsToFitAFrameOfInput() {
        var buffer = new PlatformEventBuffer();

        for (var index = 0; index < 1000; index++) {
            Assert.True(buffer.Post(PlatformEvent.Application(PlatformEventKind.DisplaysChanged, index)));
        }

        Assert.Equal(1000, buffer.Drain().Length);
    }

    /// <summary>
    ///     A consumer that stops draining — a hung frame, a modal resize loop on Windows — must cost
    ///     a bounded amount of memory and say that it lost something, rather than growing until the
    ///     process dies.
    /// </summary>
    [Fact]
    public void AFullBufferDropsAndSaysSoRatherThanGrowingForever() {
        var buffer = new PlatformEventBuffer();

        for (var index = 0; index < PlatformEventBuffer.Capacity; index++) {
            Assert.True(buffer.Post(PlatformEvent.Application(PlatformEventKind.DisplaysChanged, index)));
        }

        Assert.False(buffer.Post(PlatformEvent.Application(PlatformEventKind.Quit, 0)));
        Assert.Equal(1, buffer.DroppedCount);
        Assert.Equal(PlatformEventBuffer.Capacity, buffer.Drain().Length);
    }

    [Fact]
    public void ClearingThrowsAwayBothHalves() {
        var buffer = new PlatformEventBuffer();
        buffer.Post(PlatformEvent.Application(PlatformEventKind.Quit, 0));
        buffer.Drain();
        buffer.Post(PlatformEvent.Application(PlatformEventKind.LowMemory, 0));

        buffer.Clear();

        Assert.Equal(0, buffer.PendingCount);
        Assert.Equal(0, buffer.Drain().Length);
    }

    /// <summary>
    ///     Android's lifecycle callbacks arrive on the UI thread and a browser's on the JS thread,
    ///     so posting has to be safe from anywhere even where today's backend only uses one thread.
    /// </summary>
    [Fact]
    public void PostingFromManyThreadsLosesNothing() {
        var buffer = new PlatformEventBuffer();
        const int threads = 8;
        const int each = 500;

        Parallel.For(
            0,
            threads,
            thread => {
                for (var index = 0; index < each; index++) {
                    buffer.Post(PlatformEvent.Application(PlatformEventKind.DisplaysChanged, thread));
                }
            }
        );

        Assert.Equal(threads * each, buffer.Drain().Length);
        Assert.Equal(0, buffer.DroppedCount);
    }
}
