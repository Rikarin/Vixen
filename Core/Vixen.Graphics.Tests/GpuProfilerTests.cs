// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics.Null;
using Xunit;

namespace Vixen.Graphics.Tests;

/// <summary>Recording a frame's regions, and reading them back without waiting for the GPU.</summary>
/// <remarks>
///     ⚠ <b>What is asserted is the <i>bookkeeping</i>, and nothing else could be against a backend
///     with no clock.</b> Which pool a frame writes into, that the writes land in pairs, that a
///     resolve reports the frame it was told about and that a device with no timestamps is refused
///     outright — all of those are decisions with answers. Whether a real driver's numbers are right
///     is a question only a real driver can answer.
/// </remarks>
public sealed class GpuProfilerTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true });

    [Fact]
    public void ADeviceWithoutTimestampsIsRefusedRatherThanTimingNothing() {
        using NullDevice limited = new(new() { Features = GraphicsDeviceFeatures.Minimum });

        var refused = Assert.Throws<NotSupportedException>(() => new GpuProfiler(limited));

        // ⚠ CanTimeFrames rather than HasTimestampQueries, and the premise moved because the second
        // one turned out not to be the whole question — see the test below.
        Assert.Contains("CanTimeFrames", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>A device with timestamps and no period is refused too.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is the configuration that used to produce a whole panel of zeros.</b>
    ///         <c>GpuTimestamps.ToNanoseconds</c> returns <c>0</c> for a period of <c>0</c>, so every
    ///         scope, every aggregate and the frame itself read as taking no time — and a frame of
    ///         zero milliseconds draws as a GPU doing nothing rather than as a device that cannot
    ///         say. The constructor asked about the queries and never about the period (#1168).
    ///     </para>
    ///     <para>
    ///         A lying number is worse than a missing one, which is why this is a refusal and not a
    ///         fallback: <c>GpuTimelineView.Unavailable</c> is already the seam for "there is a
    ///         reason there is no timeline", and it prints the reason.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ADeviceWithTimestampsAndNoPeriodIsRefusedRatherThanReportingZeros() {
        using NullDevice mute = new(new() {
            Features = GraphicsDeviceFeatures.Minimum with { HasTimestampQueries = true, TimestampPeriod = 0f }
        });

        Assert.True(mute.Features.HasTimestampQueries, "the fixture is not the configuration this is about.");
        Assert.False(mute.Features.CanTimeFrames);

        var refused = Assert.Throws<NotSupportedException>(() => new GpuProfiler(mute));

        Assert.Contains("period", refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CanTimeFrames", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>And a device with both is not refused, which is the other half.</summary>
    /// <remarks>
    ///     ⚠ Without it the two refusals above are satisfied by a constructor that refuses
    ///     everything, and the editor would silently have no GPU panel on any machine.
    /// </remarks>
    [Fact]
    public void ADeviceWithBothIsAccepted() {
        Assert.True(device.Features.CanTimeFrames);

        using GpuProfiler profiler = new(device);

        Assert.True(profiler.Period > 0f);
    }

    [Fact]
    public void EachRegionWritesAPairOfTimestamps() {
        using GpuProfiler profiler = new(device);
        using var list = device.BeginCommandList();

        profiler.BeginFrame(list, 12);

        var shadows = profiler.Begin(list, "shadows");
        profiler.Close(list, shadows);

        var ui = profiler.Begin(list, "ui");
        profiler.Close(list, ui);

        list.Finish();
        device.GraphicsQueue.Submit([list]);

        Assert.Equal(4, device.Recorder!.CountOf(RecordedCommandKind.WriteTimestamp));
        Assert.Equal(1, device.Recorder.CountOf(RecordedCommandKind.ResetQueries));
    }

    /// <summary>The frame it reports is the one <c>BeginFrame</c> was told about.</summary>
    [Fact]
    public void AResolvedFrameCarriesItsIndexAndItsScopeNames() {
        using GpuProfiler profiler = new(device);

        Record(profiler, 12, "shadows", "ui");

        // ⚠ Cycled until the pool being read is the one that was written. The profiler holds one
        // pool per frame in flight plus one, and asks about the oldest — which is exactly the delay
        // a real device imposes and the reason `Resolve` reports false rather than waiting.
        Assert.True(Drain(profiler));

        Assert.Equal(12, profiler.Latest.FrameIndex);
        Assert.Equal(2, profiler.Latest.Scopes.Count);
        Assert.Equal("shadows", profiler.Latest.Scopes[0].Name);
        Assert.Equal("ui", profiler.Latest.Scopes[1].Name);
    }

    [Fact]
    public void ScopeDurationsAreConvertedThroughTheDevicesPeriod() {
        using GpuProfiler profiler = new(device);

        Record(profiler, 1, "ui");
        Assert.True(Drain(profiler));

        var scope = Assert.Single(profiler.Latest.Scopes);

        Assert.True(profiler.Latest.MillisecondsOf(scope) > 0d);
        Assert.Equal(device.Features.TimestampPeriod, profiler.Latest.Period);
    }

    /// <summary>Nesting is what a debug group gives, and it is carried on the scope.</summary>
    [Fact]
    public void NestedRegionsCarryTheirLevel() {
        using GpuProfiler profiler = new(device);
        using var list = device.BeginCommandList();

        profiler.BeginFrame(list, 3);

        var outer = profiler.Begin(list, "frame");
        var inner = profiler.Begin(list, "shadows");

        profiler.Close(list, inner);
        profiler.Close(list, outer);

        list.Finish();
        device.GraphicsQueue.Submit([list]);

        Assert.True(Drain(profiler));

        Assert.Equal(0, profiler.Latest.Scopes[0].Level);
        Assert.Equal(1, profiler.Latest.Scopes[1].Level);
    }

    /// <summary>
    ///     ⚠ Running out of capacity drops the region rather than throwing. A renderer that stopped
    ///     drawing because a diagnostic panel was open is a much worse trade than a missing bar.
    /// </summary>
    [Fact]
    public void RunningOutOfCapacityDropsTheRegionRatherThanThrowing() {
        using GpuProfiler profiler = new(device, scopeCapacity: 2);
        using var list = device.BeginCommandList();

        profiler.BeginFrame(list, 1);

        Assert.NotNull(profiler.Begin(list, "one"));
        Assert.NotNull(profiler.Begin(list, "two"));
        Assert.Null(profiler.Begin(list, "three"));
    }

    [Fact]
    public void ClosingARegionThatWasDroppedIsHarmless() {
        using GpuProfiler profiler = new(device, scopeCapacity: 1);
        using var list = device.BeginCommandList();

        profiler.BeginFrame(list, 1);
        profiler.Begin(list, "one");

        var dropped = profiler.Begin(list, "two");
        profiler.Close(list, dropped);

        Assert.Null(dropped);
    }

    /// <summary>A frame that recorded nothing has nothing to resolve.</summary>
    [Fact]
    public void AFrameWithNoRegionsResolvesToNothing() {
        using GpuProfiler profiler = new(device);
        using var list = device.BeginCommandList();

        profiler.BeginFrame(list, 1);

        Assert.False(profiler.Resolve());
        Assert.Empty(profiler.Latest.Scopes);
    }

    /// <summary>Reporting the same frame twice would read as the timeline freezing.</summary>
    [Fact]
    public void AResolvedFrameIsNotReportedASecondTime() {
        using GpuProfiler profiler = new(device);

        Record(profiler, 5, "ui");
        Assert.True(Drain(profiler));

        for (var attempt = 0; attempt < 8; attempt++) {
            Assert.False(profiler.Resolve());
        }
    }

    /// <summary>
    ///     ⚠ A region opened and never closed comes back unmeasured, and the recorder is the only
    ///     thing that can say so.
    /// </summary>
    /// <remarks>
    ///     The Null device's readings are a synthetic counter — every slot in the range answers,
    ///     written or not — so the unclosed region's end reading is a perfectly plausible number.
    ///     That is precisely the shape WebGPU has on real hardware, where there is no query reset and
    ///     an untouched slot resolves to whatever was there. Reading the ticks cannot tell the two
    ///     apart; only <see cref="GpuProfiler.Close" /> having run can.
    /// </remarks>
    [Fact]
    public void ARegionThatWasNeverClosedComesBackUnmeasured() {
        using GpuProfiler profiler = new(device);
        using var list = device.BeginCommandList();

        profiler.BeginFrame(list, 7);

        profiler.Close(list, profiler.Begin(list, "closed"));
        profiler.Begin(list, "abandoned");

        list.Finish();
        device.GraphicsQueue.Submit([list]);

        Assert.True(Drain(profiler));
        Assert.Equal(2, profiler.Latest.Scopes.Count);

        // Both halves: the closed one must still say it was measured, or the flag would be a
        // predicate that is false for everything and no timeline would ever draw.
        Assert.True(profiler.Latest.Scopes[0].Measured);
        Assert.False(profiler.Latest.Scopes[1].Measured);

        // And the reading it came back with was not zero, which is the whole point — a test on the
        // ticks would have called this scope measured.
        Assert.True(profiler.Latest.Scopes[1].BeginTicks > 0);
    }

    /// <summary>
    ///     ⚠ One unmeasured scope reading zero used to pin the frame's origin there, which reported
    ///     the device's absolute clock as the frame time.
    /// </summary>
    [Fact]
    public void AnUnmeasuredScopeDoesNotPinTheFramesOrigin() {
        GpuFrame frame = new(
            1,
            [
                new("shadows", 0, 1_000, 1_100),
                new("never written", 0, 0, 0, Measured: false)
            ],
            1f
        );

        Assert.Equal(1_000ul, frame.BeginTicks);

        // 100 ticks at a nanosecond each, and not the 1_100 the absolute clock would have given.
        Assert.Equal(0.0001d, frame.Milliseconds, 9);

        Assert.Equal(0f, frame.Fraction(1_000));
        Assert.Equal(1f, frame.Fraction(1_100));
    }

    /// <summary>
    ///     The other direction: a pair that genuinely read the same number is a measurement, and
    ///     dropping it would be the ambiguity again with the sign flipped.
    /// </summary>
    /// <remarks>
    ///     <c>ICommandList.WriteTimestamp</c> records bottom-of-pipe, so a pair around one small draw
    ///     on a deeply pipelined GPU legitimately reads as zero ticks.
    /// </remarks>
    [Fact]
    public void AMeasuredScopeThatTookNoTimeIsStillPartOfTheFrame() {
        GpuFrame frame = new(
            1,
            [
                new("tiny draw", 0, 500, 500),
                new("shadows", 0, 1_000, 1_100)
            ],
            1f
        );

        Assert.Equal(500ul, frame.BeginTicks);
        Assert.Equal(0.0006d, frame.Milliseconds, 9);
        Assert.Equal(0d, frame.MillisecondsOf(frame.Scopes[0]));
    }

    /// <summary>
    ///     ⚠ And a measured reading that happens to be zero is a reading, so the flag cannot be
    ///     stood in for by testing the ticks against zero.
    /// </summary>
    /// <remarks>
    ///     A GPU timestamp's zero point means nothing — it is a free-running counter whose origin is
    ///     the driver's business — so nothing forbids a device from handing back a small number, and a
    ///     rule that read <c>BeginTicks == 0</c> as "never written" would throw away the earliest pass
    ///     in the frame on the day one did. That is the ambiguity again, one level up, which is
    ///     exactly what <see cref="GpuScope.Measured" /> exists to avoid.
    /// </remarks>
    [Fact]
    public void AMeasuredReadingOfZeroIsStillTheFramesOrigin() {
        GpuFrame frame = new(
            1,
            [
                new("first", 0, 0, 40),
                new("shadows", 0, 1_000, 1_100)
            ],
            1f
        );

        Assert.Equal(0ul, frame.BeginTicks);
        Assert.Equal(0.0011d, frame.Milliseconds, 9);
    }

    /// <summary>An unmeasured scope has no duration however far apart its two numbers are.</summary>
    [Fact]
    public void AnUnmeasuredScopeHasNoDuration() {
        GpuScope measured = new("ui", 0, 10, 90);
        GpuScope unmeasured = measured with { Measured = false };

        Assert.Equal(80ul, measured.DurationTicks);
        Assert.Equal(0ul, unmeasured.DurationTicks);
    }

    [Fact]
    public void PoolsAreReturnedWhenItIsDisposed() {
        var before = device.LiveResourceCount;

        using (GpuProfiler profiler = new(device)) {
            Assert.True(device.LiveResourceCount > before);
        }

        Assert.Equal(before, device.LiveResourceCount);
    }

    void Record(GpuProfiler profiler, int frame, params string[] names) {
        using var list = device.BeginCommandList();

        profiler.BeginFrame(list, frame);

        foreach (var name in names) {
            profiler.Close(list, profiler.Begin(list, name));
        }

        list.Finish();
        device.GraphicsQueue.Submit([list]);
    }

    /// <summary>Runs empty frames until the recorded one comes back.</summary>
    /// <remarks>
    ///     ⚠ <b>It has to run <i>frames</i>, not just call <see cref="GpuProfiler.Resolve" />.</b>
    ///     The pool being read is the one furthest from the one being written, and which that is
    ///     only moves when a frame begins — so a loop that resolved repeatedly without beginning a
    ///     frame would ask about the same empty pool forever. That is the real shape of the thing:
    ///     a submission is not readable until the frames in flight ahead of it have gone by.
    ///     Bounded, so a bug that never resolves fails the test rather than hanging the run.
    /// </remarks>
    bool Drain(GpuProfiler profiler) {
        for (var attempt = 0; attempt < 8; attempt++) {
            if (profiler.Resolve()) {
                return true;
            }

            Record(profiler, -1);
        }

        return false;
    }

    public void Dispose() => device.Dispose();
}
