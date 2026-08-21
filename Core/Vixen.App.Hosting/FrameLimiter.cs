// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;

namespace Vixen.App;

/// <summary>Holds the loop to a frame rate without burning a core doing it.</summary>
/// <remarks>
///     <para>
///         Sleeping is imprecise — the OS guarantees only that it will wake you no <em>earlier</em>
///         than asked, and on Windows the timer granularity has historically been 15 ms — so
///         sleeping the whole remainder overshoots and produces visible judder. Spinning the whole
///         remainder is precise and heats the machine. This does both: sleeps until close, then
///         spins the last of it.
///     </para>
///     <para>
///         Temporary in the sense that matters: once there is a swapchain, presentation paces a
///         windowed frame and this becomes the cap for when vsync is off or there is no window at
///         all — a dedicated server's tick rate, or a tool's. It does not become dead code.
///     </para>
/// </remarks>
sealed class FrameLimiter {
    /// <summary>
    ///     How close to the deadline to stop sleeping and start spinning.
    /// </summary>
    /// <remarks>
    ///     Two milliseconds is more than a normal 1 ms sleep overshoots and less than a frame at any
    ///     rate worth capping, so the spin is short and the sleep does the work.
    /// </remarks>
    static readonly long SpinThreshold = Stopwatch.Frequency / 500;

    long deadline;

    /// <summary>How many times a wait has slept, and how many times it has spun.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ These exist so that a test can state this class's properties without a wall-clock
    ///         threshold. "Ten thousand unlimited frames are free", "the first wait after a reset does
    ///         not stall the caller" and "the limiter sleeps rather than burning a core" are all claims
    ///         about <em>what the limiter did</em>; asserting them as an upper bound in milliseconds
    ///         turns each one into a reading of how much CPU the test host was given, which is a
    ///         different quantity that happens to correlate on an idle machine.
    ///     </para>
    ///     <para>
    ///         Cumulative and never reset — <see cref="Reset" /> forgets the deadline, not the tally —
    ///         so a caller that wants a delta takes one.
    ///     </para>
    /// </remarks>
    public long Sleeps { get; private set; }

    /// <inheritdoc cref="Sleeps" />
    public long Spins { get; private set; }

    /// <summary>Waits until the next frame is due.</summary>
    /// <param name="framesPerSecond">The rate to hold, or <c>0</c> to return immediately.</param>
    public void Wait(int framesPerSecond) {
        if (framesPerSecond <= 0) {
            deadline = 0;
            return;
        }

        var period = Stopwatch.Frequency / framesPerSecond;
        var now = Stopwatch.GetTimestamp();

        if (deadline == 0) {
            deadline = now + period;
            return;
        }

        while (true) {
            var remaining = deadline - Stopwatch.GetTimestamp();

            if (remaining <= 0) {
                break;
            }

            if (remaining > SpinThreshold) {
                Sleeps++;
                Thread.Sleep(1);
            } else {
                Spins++;
                Thread.SpinWait(64);
            }
        }

        deadline += period;

        // A frame that overran by more than a whole period must not leave a debt the loop then
        // sprints to repay: catching up by running several frames back to back is worse than the
        // hitch that caused it, and on a rate change it would run flat out until the debt cleared.
        var current = Stopwatch.GetTimestamp();

        if (deadline < current) {
            deadline = current + period;
        }
    }

    /// <summary>Forgets the current deadline, so the next wait starts a fresh period.</summary>
    public void Reset() => deadline = 0;
}
