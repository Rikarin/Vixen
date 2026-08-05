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

        Assert.Contains("HasTimestampQueries", refused.Message, StringComparison.Ordinal);
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
