// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Platform.Tests;

/// <summary>
///     The finger bookkeeping both mobile platforms need. Tested here rather than on a device
///     because it is the half of touch handling that is arithmetic rather than UIKit — which is the
///     reason it lives in this assembly at all.
/// </summary>
public sealed class TouchTrackerTests {
    readonly TouchTracker tracker = new();

    [Fact]
    public void AFingerGetsAnIdAndKeepsItForTheWholeTouch() {
        Assert.True(tracker.TryBegin(0xDEAD, new(10, 10), out var down));
        Assert.True(tracker.TryMove(0xDEAD, new(15, 10), out var moved, out _));
        Assert.True(tracker.TryEnd(0xDEAD, out var up));

        Assert.Equal(down, moved);
        Assert.Equal(down, up);
    }

    /// <summary>
    ///     The ids are small and dense because code downstream indexes arrays by them — a UITouch
    ///     pointer or a monotonic counter would not do.
    /// </summary>
    [Fact]
    public void IdsStartAtZeroAndCountUp() {
        Assert.True(tracker.TryBegin(100, default, out var first));
        Assert.True(tracker.TryBegin(200, default, out var second));
        Assert.True(tracker.TryBegin(300, default, out var third));

        Assert.Equal(0, first);
        Assert.Equal(1, second);
        Assert.Equal(2, third);
    }

    /// <summary>
    ///     And a released id is reused, lowest first. Without this an hour of play walks the ids off
    ///     the end of anything sized by <see cref="TouchTracker.MaximumTouches" />.
    /// </summary>
    [Fact]
    public void AReleasedIdIsHandedToTheNextFinger() {
        tracker.TryBegin(100, default, out _);
        tracker.TryBegin(200, default, out var second);
        tracker.TryEnd(100, out var freed);

        Assert.True(tracker.TryBegin(300, default, out var reused));

        Assert.Equal(0, freed);
        Assert.Equal(0, reused);
        Assert.Equal(1, second);
    }

    /// <summary>
    ///     The delta is derived from the last position this tracker saw, because Android's
    ///     MotionEvent carries no delta and UIKit's is only readable while the touch object lives.
    /// </summary>
    [Fact]
    public void TheDeltaIsMeasuredFromTheLastReportRatherThanFromTheStart() {
        tracker.TryBegin(1, new(100, 100), out _);

        tracker.TryMove(1, new(110, 100), out _, out var first);
        tracker.TryMove(1, new(115, 90), out _, out var second);

        Assert.Equal(new Vector2(10, 0), first);
        Assert.Equal(new Vector2(5, -10), second);
    }

    /// <summary>
    ///     A second down for a finger already down means the platform swallowed the up — a gesture
    ///     recogniser taking over mid-sequence does this. Allocating a second id would leave the
    ///     first one stuck down for the life of the process.
    /// </summary>
    [Fact]
    public void ASecondDownForTheSameFingerIsRefusedRatherThanGivenAnotherId() {
        Assert.True(tracker.TryBegin(7, default, out _));

        Assert.False(tracker.TryBegin(7, default, out var repeated));
        Assert.Equal(-1, repeated);
        Assert.Equal(1, tracker.Count);
    }

    [Fact]
    public void MovingOrEndingAFingerThatIsNotDownIsRefused() {
        Assert.False(tracker.TryMove(99, default, out _, out _));
        Assert.False(tracker.TryEnd(99, out _));
        Assert.False(tracker.TryGetPosition(99, out _));
    }

    /// <summary>
    ///     An eleventh finger is palm contact, not input. Refused rather than allowed to grow the
    ///     table, so nothing downstream has to defend against an id it has no room for.
    /// </summary>
    [Fact]
    public void TheEleventhFingerIsRefused() {
        for (var finger = 0; finger < TouchTracker.MaximumTouches; finger++) {
            Assert.True(tracker.TryBegin(finger, default, out _));
        }

        Assert.False(tracker.TryBegin(1000, default, out _));
        Assert.Equal(TouchTracker.MaximumTouches, tracker.Count);
    }

    /// <summary>
    ///     Cancellation has to name every finger it dropped, because the caller turns each into a
    ///     TouchUp. An application that is never told a finger lifted keeps drawing the line it was
    ///     dragging — which is what an incoming call mid-gesture would otherwise leave behind.
    /// </summary>
    [Fact]
    public void CancellingNamesEveryFingerItDropped() {
        tracker.TryBegin(10, default, out _);
        tracker.TryBegin(20, default, out _);
        tracker.TryBegin(30, default, out _);
        tracker.TryEnd(20, out _);

        Assert.Equal([0, 2], tracker.Clear());
        Assert.Equal(0, tracker.Count);

        // And the ids are free again, so the next gesture starts from zero.
        Assert.True(tracker.TryBegin(40, default, out var next));
        Assert.Equal(0, next);
    }
}
