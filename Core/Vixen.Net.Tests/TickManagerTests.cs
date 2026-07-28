// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Net.Time;
using Xunit;

namespace Vixen.Net.Tests;

/// <summary>The clock: whole ticks out of frame deltas, and clock sync that corrects without jumping.</summary>
public sealed class TickManagerTests {
    [Fact]
    public void AFrameDeltaBecomesWholeTicks() {
        var manager = new TickManager(TickRate.Default);

        Assert.Equal(3, manager.Advance(TimeSpan.FromMilliseconds(100)));
        Assert.Equal(new Tick(3), manager.Current);
        Assert.Equal(3, manager.TotalTicks);

        // The remainder is banked rather than lost: three more of these owe three more ticks and the
        // hundredths of a tick left over each time add up to one of them.
        Assert.Equal(3, manager.Advance(TimeSpan.FromMilliseconds(100)));
        Assert.Equal(0, manager.Advance(TimeSpan.Zero));
    }

    [Fact]
    public void ABankedRemainderEventuallyBuysATick() {
        var manager = new TickManager(new TickRate(30));
        var ticks = 0;

        // Ten milliseconds is less than a third of a tick, so two out of three of these owe nothing.
        for (var i = 0; i < 300; i++) {
            ticks += manager.Advance(TimeSpan.FromMilliseconds(10));
        }

        // Three seconds at thirty a second, give or take the tick length not dividing a second
        // exactly. Nothing is lost to the remainder.
        Assert.InRange(ticks, 89, 90);
        Assert.Equal(0, manager.DroppedTicks);
    }

    [Fact]
    public void AStallDropsItsDebtVisiblyRatherThanSpiralling() {
        var manager = new TickManager(new TickRate(30), maxTicksPerAdvance: 8);

        var owed = manager.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(8, owed);
        Assert.Equal(22, manager.DroppedTicks);
        Assert.Equal(new Tick(8), manager.Current);
    }

    [Fact]
    public void AServerClockNeverDrifts() {
        var manager = new TickManager(TickRate.Default);

        manager.Advance(TimeSpan.FromSeconds(1));

        Assert.False(manager.IsSynchronized);
        Assert.Equal(1.0, manager.DriftScale);
        Assert.Equal(0, manager.TickError);
        Assert.Equal(0, manager.SnapCount);
    }

    [Fact]
    public void TheFirstSynchronizeSnaps_AndIsNotCountedAsOne() {
        var manager = new TickManager(TickRate.Default);

        manager.Synchronize(new Tick(1000), TimeSpan.Zero);

        Assert.True(manager.IsSynchronized);
        Assert.Equal(0, manager.SnapCount);
        Assert.Equal(new Tick(1000), manager.EstimatedServerTick);
        Assert.Equal(manager.TargetTick, manager.Current);
        Assert.Equal(0, manager.TickError);
    }

    [Fact]
    public void ASmallErrorIsDriftedAway_AndNothingJumps() {
        var manager = new TickManager(TickRate.Default);
        manager.Synchronize(new Tick(1000), TimeSpan.Zero);

        // Five ticks behind — far enough to matter, well inside the snap threshold of thirty.
        manager.Synchronize(new Tick(1005), TimeSpan.Zero);

        Assert.Equal(5, manager.TickError);
        Assert.True(manager.DriftScale < 1.0, "Being behind should shorten the tick, not lengthen it.");

        var before = manager.Current;

        for (var i = 0; i < 300; i++) {
            manager.Advance(TimeSpan.FromMilliseconds(10));
        }

        Assert.InRange(manager.TickError, -1, 1);
        Assert.Equal(0, manager.SnapCount);
        Assert.True(manager.Current.IsAfter(before));

        // And once it has caught up it stops running fast.
        Assert.InRange(manager.DriftScale, 0.97, 1.03);
    }

    [Fact]
    public void RunningAheadIsCorrectedTheOtherWay() {
        var manager = new TickManager(TickRate.Default);
        manager.Synchronize(new Tick(1000), TimeSpan.Zero);

        manager.Synchronize(new Tick(995), TimeSpan.Zero);

        Assert.Equal(-5, manager.TickError);
        Assert.True(manager.DriftScale > 1.0, "Being ahead should lengthen the tick.");

        for (var i = 0; i < 300; i++) {
            manager.Advance(TimeSpan.FromMilliseconds(10));
        }

        Assert.InRange(manager.TickError, -1, 1);
        Assert.Equal(0, manager.SnapCount);
    }

    [Fact]
    public void AnErrorTooLargeToDriftAway_SnapsAndSaysSo() {
        var manager = new TickManager(TickRate.Default);
        manager.Synchronize(new Tick(1000), TimeSpan.Zero);

        // A suspended app, a breakpoint, a laptop lid: a thousand ticks of error that a ten percent
        // correction would take five minutes to work off.
        manager.Synchronize(new Tick(2000), TimeSpan.Zero);

        Assert.Equal(1, manager.SnapCount);
        Assert.Equal(0, manager.TickError);
        Assert.Equal(manager.TargetTick, manager.Current);
    }

    [Fact]
    public void TheClientRunsAheadOfTheServer_AndDrawsBehindIt() {
        var manager = new TickManager(TickRate.Default);

        manager.Synchronize(new Tick(1000), TimeSpan.FromMilliseconds(100));

        Assert.Equal(TimeSpan.FromMilliseconds(100), manager.RoundTrip.RoundTrip);
        Assert.Equal(TimeSpan.FromMilliseconds(50), manager.RoundTrip.OneWay);

        // Ahead by half a trip plus a jitter margin, so input lands just in time.
        Assert.True(manager.LeadTicks > 0);
        Assert.True(manager.TargetTick.IsAfter(manager.EstimatedServerTick));

        // Behind by the jitter margin, because the snapshot for that tick has already arrived.
        Assert.True(manager.InterpolationTick.IsBefore(manager.EstimatedServerTick));
        Assert.Equal(
            manager.EstimatedServerTick.Subtract(manager.InterpolationDelayTicks),
            manager.InterpolationTick
        );
    }

    [Fact]
    public void TheServerEstimateKeepsRunningBetweenSamples() {
        var manager = new TickManager(TickRate.Default);
        manager.Synchronize(new Tick(1000), TimeSpan.Zero);

        for (var i = 0; i < 30; i++) {
            manager.Advance(TimeSpan.FromMilliseconds(33.3333));
        }

        // A second of wall clock is thirty ticks of server, whether or not it said anything.
        Assert.InRange(manager.EstimatedServerTick.Subtract(new Tick(1000)), 29, 31);
    }

    [Fact]
    public void ResetPutsTheClockBackToWhereItIsTold() {
        var manager = new TickManager(TickRate.Default);
        manager.Synchronize(new Tick(1000), TimeSpan.FromMilliseconds(80));
        manager.Advance(TimeSpan.FromSeconds(1));

        manager.Reset(new Tick(5));

        Assert.Equal(new Tick(5), manager.Current);
        Assert.False(manager.IsSynchronized);
        Assert.Equal(1.0, manager.DriftScale);
        Assert.False(manager.RoundTrip.HasSamples);
    }

    [Fact]
    public void TimeRunningBackwards_Throws() {
        var manager = new TickManager(TickRate.Default);

        Assert.Throws<ArgumentOutOfRangeException>(() => manager.Advance(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void ATickRateOfNothing_IsNotAClock() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new TickManager(default));

    [Fact]
    public void TheEstimatorSettlesOnASteadyLink() {
        var estimator = new RoundTripEstimator();

        for (var i = 0; i < 50; i++) {
            estimator.Add(TimeSpan.FromMilliseconds(60));
        }

        Assert.Equal(60, estimator.RoundTrip.TotalMilliseconds, 1);
        Assert.Equal(30, estimator.OneWay.TotalMilliseconds, 1);
        Assert.True(estimator.Jitter < TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public void TheEstimatorReportsAnUnsteadyLinkAsUnsteady() {
        var steady = new RoundTripEstimator();
        var swinging = new RoundTripEstimator();

        for (var i = 0; i < 50; i++) {
            steady.Add(TimeSpan.FromMilliseconds(60));
            swinging.Add(TimeSpan.FromMilliseconds(i % 2 == 0 ? 20 : 100));
        }

        // Much the same average, and nothing like the same link. That difference is the whole reason
        // the buffers are sized from the variance rather than from the mean.
        Assert.InRange(swinging.RoundTrip.TotalMilliseconds, 50, 70);
        Assert.True(steady.Jitter < TimeSpan.FromMilliseconds(1));
        Assert.True(swinging.Jitter > TimeSpan.FromMilliseconds(20));
    }

    /// <summary>A server tick half the tick space away is a snap, not an exception.</summary>
    /// <remarks>
    ///     <para>
    ///         Found by the packet fuzzer. A tick error is a modular distance and therefore takes
    ///         every value an <see cref="int" /> can hold, including <see cref="int.MinValue" /> —
    ///         and <c>Math.Abs</c> is the one function that throws on exactly that value. The tick
    ///         in question arrives straight off the wire in a <c>Pong</c> and in a
    ///         <c>ConnectAccepted</c>, so it was one packet, one <c>OverflowException</c>, on the
    ///         frame's own thread.
    ///     </para>
    ///     <para>
    ///         Two synchronisations, because the first one snaps unconditionally and never asks how
    ///         large the error is. It is the second that compares, which is why a single hostile
    ///         packet had to arrive after a handshake — and why this is a sequence rather than a
    ///         corpus entry.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AServerTickHalfTheSpaceAway_SnapsRatherThanOverflowing() {
        var manager = new TickManager(TickRate.Default);
        manager.Reset(default);
        manager.Synchronize(new(0), TimeSpan.Zero);

        var before = manager.SnapCount;
        manager.Synchronize(new(0x8000_0000), TimeSpan.Zero);

        Assert.Equal(before + 1, manager.SnapCount);
        Assert.Equal(manager.TargetTick, manager.Current);
    }

    [Fact]
    public void ANegativeSample_Throws() {
        var estimator = new RoundTripEstimator();

        Assert.Throws<ArgumentOutOfRangeException>(() => estimator.Add(TimeSpan.FromMilliseconds(-1)));
    }
}
