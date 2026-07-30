// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Graphics.Null.Tests;

/// <summary>
///     The timestamp-query surface, on the backend that can be asked about it without a GPU.
/// </summary>
/// <remarks>
///     ⚠ <b>What is asserted here is the <i>shape</i>, and nothing else could be.</b> A device with
///     no GPU has no clock, so the readings are synthetic — the point of the tests is that a write
///     lands where it was aimed, that the bounds are checked on the CPU rather than left undefined,
///     and that a device reporting no timestamps refuses to hand out a pool instead of handing out
///     one that measures nothing. Whether a real driver's numbers are right is a question only a
///     real driver can answer.
/// </remarks>
public sealed class QueryPoolTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true });

    [Fact]
    public void ADeviceWithoutTheCapabilityRefusesAPoolRatherThanFakingOne() {
        using var limited = new NullDevice(new() { Features = GraphicsDeviceFeatures.Minimum });

        var refused = Assert.Throws<NotSupportedException>(
            () => limited.CreateQueryPool(new(QueryKind.Timestamp, 8, "frame"))
        );

        Assert.Contains("HasTimestampQueries", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>A timestamp is one of the two commands every API allows inside a render pass.</summary>
    /// <remarks>
    ///     The whole reason for the exception: a pass's cost is a pair of writes around its draws,
    ///     and a backend that refused one inside a pass would only be able to time the gaps between
    ///     passes.
    /// </remarks>
    [Fact]
    public void ATimestampIsAllowedInsideARenderPass() {
        var pool = device.CreateQueryPool(new(QueryKind.Timestamp, 4, "frame"));

        var target = device.CreateTextureView(
            device.CreateTexture(new(PixelFormat.Rgba8UNorm, 8, 8, TextureUsage.ColourTarget))
        );

        using var list = device.BeginCommandList();

        list.ResetQueries(pool, 0, 4);
        list.BeginRenderPass(new([new(target)]));
        list.WriteTimestamp(pool, 0);
        list.Draw(3);
        list.WriteTimestamp(pool, 1);
        list.EndRenderPass();
        list.Finish();

        device.GraphicsQueue.Submit([list]);

        var written = device.Recorder!.OfKind(RecordedCommandKind.WriteTimestamp);

        Assert.Equal(2, written.Count);
        Assert.Equal(0, written[0].B);
        Assert.Equal(1, written[1].B);
    }

    /// <summary>A reset is not, for the reason every copy is not.</summary>
    [Fact]
    public void AResetInsideARenderPassIsRefused() {
        var pool = device.CreateQueryPool(new(QueryKind.Timestamp, 4, "frame"));

        var target = device.CreateTextureView(
            device.CreateTexture(new(PixelFormat.Rgba8UNorm, 8, 8, TextureUsage.ColourTarget))
        );

        using var list = device.BeginCommandList();
        list.BeginRenderPass(new([new(target)]));

        Assert.Throws<InvalidOperationException>(() => list.ResetQueries(pool, 0, 4));
    }

    /// <summary>An empty reset records nothing rather than a command that means nothing.</summary>
    [Fact]
    public void AnEmptyResetIsNotRecorded() {
        var pool = device.CreateQueryPool(new(QueryKind.Timestamp, 4, "frame"));

        using var list = device.BeginCommandList();
        list.ResetQueries(pool, 0, 0);
        list.Finish();

        device.GraphicsQueue.Submit([list]);

        Assert.Empty(device.Recorder!.OfKind(RecordedCommandKind.ResetQueries));
    }

    /// <summary>
    ///     Reading past the end is caught here rather than left to a driver, because Vulkan's answer
    ///     to it is undefined rather than an error.
    /// </summary>
    [Fact]
    public void ReadingPastTheEndOfAPoolIsRefused() {
        var pool = device.CreateQueryPool(new(QueryKind.Timestamp, 4, "frame"));

        Span<ulong> readings = stackalloc ulong[3];

        Assert.Throws<ArgumentOutOfRangeException>(
            () => {
                Span<ulong> destination = new ulong[3];
                device.TryResolveQueries(pool, 2, destination);
            }
        );

        Assert.True(device.TryResolveQueries(pool, 1, readings));
    }

    /// <summary>
    ///     A pair subtracts to something positive, which is the only property of a synthetic reading
    ///     worth relying on — and the one that lets a caller's arithmetic be tested at all.
    /// </summary>
    [Fact]
    public void APairOfReadingsIsMonotonic() {
        var pool = device.CreateQueryPool(new(QueryKind.Timestamp, 2, "frame"));

        Span<ulong> readings = stackalloc ulong[2];

        Assert.True(device.TryResolveQueries(pool, 0, readings));
        Assert.True(readings[1] > readings[0]);

        Assert.True(
            GpuTimestamps.ToMilliseconds(readings[1] - readings[0], device.Features.TimestampPeriod) > 0d
        );
    }

    /// <summary>A pool is a resource, and a resource that is not returned is a leak.</summary>
    [Fact]
    public void APoolIsCountedAndReturned() {
        var before = device.LiveResourceCount;
        var pool = device.CreateQueryPool(new(QueryKind.Timestamp, 4, "frame"));

        Assert.Equal(before + 1, device.LiveResourceCount);

        device.Destroy(pool);
        Assert.Equal(before, device.LiveResourceCount);
    }

    /// <summary>Nothing to convert without a period, and zero is the honest answer.</summary>
    [Fact]
    public void ADeviceWithNoPeriodConvertsToZero() {
        Assert.Equal(0d, GpuTimestamps.ToNanoseconds(1000, 0f));
        Assert.Equal(0d, GpuTimestamps.ToMilliseconds(1000, 0f));
    }

    public void Dispose() => device.Dispose();
}
