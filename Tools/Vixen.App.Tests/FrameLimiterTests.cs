// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Xunit;

namespace Vixen.App.Tests;

public class FrameLimiterTests {
    [Fact]
    public void AnUnlimitedRateDoesNotWait() {
        var limiter = new FrameLimiter();
        var started = Stopwatch.GetTimestamp();

        for (var frame = 0; frame < 10_000; frame++) {
            limiter.Wait(0);
        }

        Assert.True(
            Stopwatch.GetElapsedTime(started) < TimeSpan.FromMilliseconds(200),
            "Ten thousand unlimited frames should be free."
        );
    }

    [Fact]
    public void ARateIsActuallyHeld() {
        var limiter = new FrameLimiter();
        limiter.Wait(200);

        var started = Stopwatch.GetTimestamp();

        for (var frame = 0; frame < 20; frame++) {
            limiter.Wait(200);
        }

        var elapsed = Stopwatch.GetElapsedTime(started);

        // Twenty frames at 200 Hz is 100 ms. The lower bound is what matters — it says the limiter
        // waited at all — and the upper one is loose because a shared CI runner oversleeps.
        Assert.InRange(elapsed, TimeSpan.FromMilliseconds(80), TimeSpan.FromSeconds(2));
    }

    /// <summary>
    ///     A frame that overran must not leave a debt the loop then sprints to repay. Catching up by
    ///     running several frames back to back is worse than the hitch that caused it — it is how a
    ///     stall becomes a burst of teleporting movement — so the deadline is reset rather than
    ///     accumulated.
    /// </summary>
    [Fact]
    public void AnOverrunDoesNotBecomeABurstOfFreeFrames() {
        var limiter = new FrameLimiter();
        limiter.Wait(100);

        // A 50 ms hitch against a 10 ms period: five frames' worth of debt.
        Thread.Sleep(50);

        var started = Stopwatch.GetTimestamp();

        for (var frame = 0; frame < 3; frame++) {
            limiter.Wait(100);
        }

        // Without the reset the first three frames after the hitch would all return instantly.
        Assert.True(
            Stopwatch.GetElapsedTime(started) > TimeSpan.FromMilliseconds(15),
            "The frames after an overrun ran back to back instead of being paced."
        );
    }

    [Fact]
    public void ResettingStartsAFreshPeriod() {
        var limiter = new FrameLimiter();
        limiter.Wait(1);
        limiter.Reset();

        var started = Stopwatch.GetTimestamp();
        limiter.Wait(1);

        // The first wait after a reset establishes the deadline rather than sleeping to it, so a
        // one-frame-per-second rate must not stall the caller for a second on the way in.
        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromMilliseconds(200));
    }
}
