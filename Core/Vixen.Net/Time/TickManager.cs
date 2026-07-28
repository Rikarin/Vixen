// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Net.Time;

/// <summary>
///     The clock everything else is stated in: turns frame deltas into whole ticks, and on a client,
///     keeps those ticks lined up with the server's.
/// </summary>
/// <remarks>
///     <para>
///         On a server this is an accumulator and nothing more — time in, whole ticks out, the same
///         shape as the engine's fixed step. On a client it is also the clock-sync loop, and that is
///         where the interesting decision is.
///     </para>
///     <para>
///         <b>Drift, do not jump.</b> A client that discovers it is three ticks behind could set its
///         tick counter three higher, and everything keyed by tick — input, interpolation, the
///         history the server rewinds — would jump with it. Instead the tick <i>length</i> is scaled
///         by up to <see cref="MaxDriftRatio" />, so the client runs a few percent fast until it has
///         caught up and then goes back to normal. Nobody sees a correction that takes a second and
///         moves nothing.
///     </para>
///     <para>
///         <b>Jump anyway when drifting would take too long.</b> Joining, resuming from a breakpoint
///         or coming back from a suspended app leaves an error that a 10 % correction would take
///         minutes to work off. Past <see cref="SnapThresholdTicks" /> the clock snaps, and says so
///         through <see cref="SnapCount" /> rather than doing it quietly.
///     </para>
///     <para>
///         <b>The client runs ahead, the renderer runs behind.</b> <see cref="Current" /> is aimed at
///         the server's tick plus a lead of half the round trip and a jitter margin, so input sent
///         now arrives just before the server needs it. <see cref="InterpolationTick" /> is the
///         opposite: the server's tick minus the same margin, which is the tick the motion layer
///         should be drawing, because the snapshot for it has already arrived.
///     </para>
/// </remarks>
public sealed class TickManager {
    /// <summary>
    ///     How large an error saturates the drift correction. Small on purpose: past a few ticks of
    ///     error there is nothing to gain from a gentler response, and inside it the correction eases
    ///     off as the error closes rather than overshooting.
    /// </summary>
    const int DriftResponseTicks = 4;

    long accumulated;
    double estimateFraction;
    Tick estimatedServerTick;

    /// <summary>How often this clock ticks.</summary>
    public TickRate Rate { get; }

    /// <summary>The tick the simulation is on.</summary>
    public Tick Current { get; private set; }

    /// <summary>The most ticks one <see cref="Advance" /> may run before the rest of the debt is dropped.</summary>
    public int MaxTicksPerAdvance { get; }

    /// <summary>How far the tick length may be scaled to correct drift, as a fraction.</summary>
    public double MaxDriftRatio { get; }

    /// <summary>How large an error stops being corrected by drifting and is snapped instead.</summary>
    public int SnapThresholdTicks { get; }

    /// <summary>Round trip and jitter, measured from the samples handed to <see cref="Synchronize" />.</summary>
    public RoundTripEstimator RoundTrip { get; } = new();

    /// <summary>Whether this clock has ever been told what the server's tick is.</summary>
    public bool IsSynchronized { get; private set; }

    /// <summary>Ticks run since this clock was made.</summary>
    public long TotalTicks { get; private set; }

    /// <summary>Ticks dropped to the catch-up clamp — a stall, visibly rather than quietly.</summary>
    public long DroppedTicks { get; private set; }

    /// <summary>How many times the clock has given up on drifting and snapped.</summary>
    public long SnapCount { get; private set; }

    /// <summary>What the server's tick is believed to be right now.</summary>
    public Tick EstimatedServerTick => estimatedServerTick;

    /// <summary>
    ///     How far ahead of the server this clock aims to be, so that input sent now arrives in time:
    ///     half a round trip, plus a margin for jitter, plus one.
    /// </summary>
    public int LeadTicks =>
        IsSynchronized ? Math.Max(0, Rate.ToTicks(RoundTrip.OneWay) + Rate.ToTicks(RoundTrip.Jitter * 2) + 1 + LeadBias)
            : 0;

    /// <summary>An adjustment to the lead, in ticks, for something that measured better than a guess.</summary>
    /// <remarks>
    ///     <para>
    ///         The lead above is computed from a round trip this end estimated, and it is a good
    ///         estimate of the wrong thing: what actually matters is whether the client's input is in
    ///         the server's hands <i>before</i> the tick it is for, which only the server can see.
    ///         <c>InputBuffer</c> measures it — depth, starvation, lateness — and
    ///         <c>TickLeadController</c> is what turns those into this.
    ///     </para>
    ///     <para>
    ///         <b>Kept separate from the estimate rather than replacing it.</b> The round trip is
    ///         still the right starting point and the right answer when nothing is measuring; a bias
    ///         is an adjustment somebody can inspect, reason about and clamp, where a lead written
    ///         over wholesale is one nobody can tell from a broken estimator.
    ///     </para>
    /// </remarks>
    public int LeadBias { get; set; }

    /// <summary>The tick the renderer should be interpolating towards — behind the server, not ahead.</summary>
    public Tick InterpolationTick => estimatedServerTick.Subtract(InterpolationDelayTicks);

    /// <summary>How far behind the server the interpolation target sits.</summary>
    public int InterpolationDelayTicks => IsSynchronized ? Rate.ToTicks(RoundTrip.Jitter * 2) + 1 : 0;

    /// <summary>Where <see cref="Current" /> is trying to be.</summary>
    public Tick TargetTick => IsSynchronized ? estimatedServerTick.Add(LeadTicks) : Current;

    /// <summary>How far <see cref="Current" /> is from where it should be. Positive means behind.</summary>
    public int TickError => TargetTick.Subtract(Current);

    /// <summary>
    ///     What the tick length is currently being multiplied by. One when there is nothing to
    ///     correct, below one while catching up, above one while waiting for the server to catch up.
    /// </summary>
    public double DriftScale { get; private set; } = 1.0;

    /// <summary>How far through the current tick the last <see cref="Advance" /> left us, from 0 to 1.</summary>
    public float Alpha => (float)(accumulated / (double)StepTicks);

    long StepTicks {
        get {
            var scaled = (long)(Rate.Duration.Ticks * DriftScale);

            return scaled < 1 ? 1 : scaled;
        }
    }

    /// <summary>Creates a clock.</summary>
    /// <param name="rate">How often it ticks.</param>
    /// <param name="maxTicksPerAdvance">
    ///     The catch-up clamp. A frame that took a second owes thirty ticks; running all of them
    ///     makes the next frame take a second too, which is the spiral the clamp exists to break.
    /// </param>
    /// <param name="maxDriftRatio">How far the tick length may be scaled to correct drift.</param>
    /// <param name="snapThresholdTicks">
    ///     How large an error is snapped rather than drifted away. Defaults to one second's worth.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">An argument is outside its range.</exception>
    public TickManager(
        TickRate rate,
        int maxTicksPerAdvance = 8,
        double maxDriftRatio = 0.1,
        int? snapThresholdTicks = null
    ) {
        if (!rate.IsValid) {
            // The int rather than the struct: an exception's own message should not depend on the
            // invalid value being renderable.
            throw new ArgumentOutOfRangeException(
                nameof(rate),
                rate.TicksPerSecond,
                "A tick rate of zero is not a rate."
            );
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(maxTicksPerAdvance, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxDriftRatio, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxDriftRatio, 0.5);

        var snap = snapThresholdTicks ?? rate.TicksPerSecond;
        ArgumentOutOfRangeException.ThrowIfLessThan(snap, 1, nameof(snapThresholdTicks));

        Rate = rate;
        MaxTicksPerAdvance = maxTicksPerAdvance;
        MaxDriftRatio = maxDriftRatio;
        SnapThresholdTicks = snap;
    }

    /// <summary>Banks elapsed time and reports how many ticks to run.</summary>
    /// <param name="elapsed">Time since the last call. Negative is refused.</param>
    /// <returns>How many ticks to run, from zero to <see cref="MaxTicksPerAdvance" />.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="elapsed" /> is negative.</exception>
    public int Advance(TimeSpan elapsed) {
        ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, TimeSpan.Zero);

        AdvanceServerEstimate(elapsed);
        UpdateDrift();

        accumulated += elapsed.Ticks;
        var step = StepTicks;
        var owed = accumulated / step;

        if (owed > MaxTicksPerAdvance) {
            DroppedTicks += owed - MaxTicksPerAdvance;
            accumulated -= owed * step;
            owed = MaxTicksPerAdvance;
        } else {
            accumulated -= owed * step;
        }

        Current = Current.Add((int)owed);
        TotalTicks += owed;

        return (int)owed;
    }

    /// <summary>
    ///     Tells the clock what tick the server was on when it sent something, and how long the round
    ///     trip took.
    /// </summary>
    /// <param name="serverTick">The tick the server stamped on the packet.</param>
    /// <param name="roundTrip">The round trip measured for that exchange.</param>
    /// <remarks>
    ///     The first sample snaps, because a client that has just connected has no clock worth
    ///     preserving. Later ones snap only past <see cref="SnapThresholdTicks" />; inside it they
    ///     adjust the drift and let <see cref="Advance" /> do the work.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="roundTrip" /> is negative.</exception>
    public void Synchronize(Tick serverTick, TimeSpan roundTrip) {
        RoundTrip.Add(roundTrip);

        // The stamp describes the server as it was one trip ago, so what it is now is that plus the
        // half of the trip we have not seen.
        estimatedServerTick = serverTick.Add(Rate.ToTicks(RoundTrip.OneWay));
        estimateFraction = 0;

        var wasSynchronized = IsSynchronized;
        IsSynchronized = true;

        var error = TargetTick.Subtract(Current);

        // Written as two comparisons rather than Math.Abs, and that is a fix rather than a style.
        // The error is a modular tick distance, so it takes every value an int can hold — including
        // int.MinValue, which a server tick exactly half the tick space away produces. Math.Abs
        // throws OverflowException on that one value, and this is reached from a Pong and from a
        // ConnectAccepted, both of which carry a tick straight off the wire. One packet, one crash
        // on the frame's own thread; found by the packet fuzzer, which is what it is for.
        if (!wasSynchronized || error > SnapThresholdTicks || error < -SnapThresholdTicks) {
            Current = TargetTick;
            accumulated = 0;

            if (wasSynchronized) {
                SnapCount++;
            }
        }

        UpdateDrift();
    }

    /// <summary>Sets the tick directly and forgets any drift correction in progress.</summary>
    /// <param name="tick">The tick to start from.</param>
    /// <remarks>For a server choosing where to start, and for a client that has just reconnected.</remarks>
    public void Reset(Tick tick) {
        Current = tick;
        estimatedServerTick = tick;
        estimateFraction = 0;
        accumulated = 0;
        DriftScale = 1.0;
        IsSynchronized = false;
        RoundTrip.Reset();
    }

    void AdvanceServerEstimate(TimeSpan elapsed) {
        if (!IsSynchronized) {
            return;
        }

        // The server's clock is the reference, so the estimate advances at the nominal rate. Only
        // ours is allowed to be scaled — that is the whole mechanism.
        estimateFraction += elapsed.Ticks / (double)Rate.Duration.Ticks;
        var whole = (int)Math.Floor(estimateFraction);

        if (whole != 0) {
            estimatedServerTick = estimatedServerTick.Add(whole);
            estimateFraction -= whole;
        }
    }

    void UpdateDrift() {
        if (!IsSynchronized) {
            DriftScale = 1.0;

            return;
        }

        // Behind (a positive error) means shorter ticks, so more of them fit in a second.
        var response = Math.Clamp(TickError / (double)DriftResponseTicks, -1.0, 1.0);
        DriftScale = 1.0 - (response * MaxDriftRatio);
    }
}
