// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Xunit;

namespace Vixen.App.Tests;

/// <summary>
///     What the limiter did, counted — and only what is irreducibly a duration, timed.
/// </summary>
/// <remarks>
///     ⚠ <b>Three of the assertions here used to be upper bounds in wall-clock time on work that is
///     fast when it is actually scheduled</b>, which is the shape that made
///     <c>RealmClusterTests</c> the repo's one live failure: the number such an assertion reads is
///     the machine's spare CPU, not the property in the test's name. Twelve of these hosts run at
///     once under <c>nice -n 20</c> put <see cref="ARateIsActuallyHeld" /> at 3.9 s, 4.8 s and 2.7 s
///     against its two-second ceiling, and <see cref="ResettingStartsAFreshPeriod" /> over its
///     200 ms one, with the limiter working perfectly throughout.
///     <para>
///         So the claims are counted instead. <see cref="FrameLimiter.Sleeps" /> and
///         <see cref="FrameLimiter.Spins" /> are the same number on every machine, and every "this
///         did not wait" below is a statement about them. The one thing a count cannot express is
///         that holding a rate takes real time, and that is asserted as a <em>lower</em> bound —
///         the one direction a loaded machine cannot break, because load only ever makes an elapsed
///         time bigger.
///     </para>
/// </remarks>
public class FrameLimiterTests {
    /// <summary>How many frames <see cref="ARateIsActuallyHeld" /> paces, and at what rate.</summary>
    const int Frames = 20;

    const int Rate = 200;

    [Fact]
    public void AnUnlimitedRateDoesNotWait() {
        var limiter = new FrameLimiter();

        for (var frame = 0; frame < 10_000; frame++) {
            limiter.Wait(0);
        }

        // ⚠ Counted, not timed. This used to assert that ten thousand calls came back inside
        // 200 ms; they come back in 0.03 ms, so every millisecond of that bound above the first
        // was scheduling, and a thread on this machine under load has been measured losing 149 ms
        // in one go. The property the test is named for — an unlimited rate does not wait — is
        // exactly "took no sleeps and no spins", and that is true on a machine of any speed.
        Assert.Equal(0L, limiter.Sleeps);
        Assert.Equal(0L, limiter.Spins);

        // And the control, because a counter nobody increments would satisfy the two lines above
        // just as well as a limiter that does not wait.
        Assert.True(Ticked().Sleeps > 0, "the counter these tests read never moves");
    }

    [Fact]
    public void ARateIsActuallyHeld() {
        var limiter = new FrameLimiter();
        limiter.Wait(Rate);

        var started = Stopwatch.GetTimestamp();

        for (var frame = 0; frame < Frames; frame++) {
            limiter.Wait(Rate);
        }

        var elapsed = Stopwatch.GetElapsedTime(started);

        // Twenty frames at 200 Hz is 100 ms. This one is a duration and nothing else — a rate that
        // is held is time that passed — but it is a *lower* bound, which is the direction that
        // survives a shared machine: a busy runner can only make it longer.
        Assert.True(
            elapsed >= TimeSpan.FromMilliseconds(80),
            $"twenty frames at {Rate} Hz took {elapsed.TotalMilliseconds:0.0} ms, so the limiter did not wait"
        );

        // ⚠ A hang detector, not a budget, and the ceiling is deliberately absurd. It was two
        // seconds, which reads generous and is not: twelve concurrent hosts under `nice -n 20`
        // produced 2.7 s, 3.9 s and 4.8 s here for a correct limiter. Oversleeping is what the OS
        // is permitted to do, so no shared machine can be told an upper bound on a sleep; what is
        // caught here is only a limiter that never comes back at all.
        Assert.True(
            elapsed < TimeSpan.FromSeconds(60),
            $"twenty frames at {Rate} Hz took {elapsed.TotalSeconds:0.0} s, which is not slow but stuck"
        );

        // ⚠ And the claim no elapsed time can make: it *slept*. A limiter that spun the whole
        // remainder holds the rate exactly as well, passes both bounds above, and burns a core
        // doing it — which is the thing this class's own summary promises not to do, and which
        // nothing in this file used to check. Half the frames rather than all twenty, because a
        // wait that arrives to find its deadline already past is correct and takes neither.
        Assert.True(
            limiter.Sleeps >= Frames / 2,
            $"{Frames} paced frames slept {limiter.Sleeps} times and spun {limiter.Spins} — the limiter is "
            + "spinning the remainder rather than sleeping through it"
        );
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
        // A lower bound again, so a loaded machine cannot make this red.
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

        limiter.Wait(1);

        // The first wait after a reset establishes the deadline rather than sleeping to it, so a
        // one-frame-per-second rate must not stall the caller for a second on the way in.
        //
        // ⚠ Counted, not timed. The old assertion was `elapsed < 200 ms` over a correct value of
        // roughly zero against a broken value of a whole second — a fivefold margin over a quantity
        // whose entire range above zero is the scheduler's, and it went red under a parallel run.
        // Neither wait above waits, so neither takes a sleep or a spin, on any machine.
        Assert.Equal(0L, limiter.Sleeps);
        Assert.Equal(0L, limiter.Spins);

        Assert.True(Ticked().Sleeps > 0, "the counter this test reads never moves");
    }

    /// <summary>
    ///     A limiter that has just held a rate, used as the control by the tests above that assert a
    ///     counter is <i>zero</i>.
    /// </summary>
    /// <remarks>
    ///     ⚠ Without it, replacing a flaky clock threshold with a count would have made those tests
    ///     unable to fail at all — a counter that is never incremented reads zero just as convincingly
    ///     as a limiter that never waits, and that is worse than the flake, because the flake at least
    ///     reported something. This is the instrument being checked before it is believed.
    /// </remarks>
    static FrameLimiter Ticked() {
        var limiter = new FrameLimiter();

        // A hundredth of a second, twice: the first call establishes the deadline and the second
        // finds a full period in front of it, which is far more than the two milliseconds at which
        // the limiter stops sleeping and starts spinning. Nothing here is timed — the sleep is read
        // off the counter, and a slow machine only makes the sleep longer.
        limiter.Wait(100);
        limiter.Wait(100);

        return limiter;
    }
}
