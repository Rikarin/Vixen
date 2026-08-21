// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Graphics.Tests;

/// <summary>What a backend gets for free, and what a point means before any device is involved.</summary>
/// <remarks>
///     <b>The contract three of the six backends rely on.</b> OpenGL and WebGPU have one queue and
///     nothing to synchronise against anything, so neither implements the wait-value submit at all —
///     they take the interface's defaults. That makes those defaults load-bearing: a backend that
///     inherits them must report the capability as absent and refuse the call with a reason, rather
///     than silently doing nothing and leaving a caller believing its frame is ordered.
/// </remarks>
public sealed class TimelinePointTests {
    /// <summary>A default point is the one every queue has already passed.</summary>
    [Fact]
    public void TheDefaultPointIsNone() {
        Assert.True(default(TimelinePoint).IsNone);
        Assert.True(TimelinePoint.None.IsNone);
        Assert.Equal(TimelinePoint.None, default);

        // Counted from one, so the first submission's point is distinguishable from "no point".
        Assert.False(new TimelinePoint(QueueKind.Compute, 1).IsNone);
    }

    /// <summary>A point is its queue and its value, and two queues at one value are not equal.</summary>
    /// <remarks>
    ///     ⚠ The queue is half the identity. A point compared on its value alone would make the
    ///     compute queue's third submission equal to the graphics queue's third, which is the
    ///     confusion a single device-wide counter would have institutionalised.
    /// </remarks>
    [Fact]
    public void APointIsItsQueueAndItsValue() {
        Assert.Equal(new(QueueKind.Compute, 3), new TimelinePoint(QueueKind.Compute, 3));
        Assert.NotEqual(new(QueueKind.Graphics, 3), new TimelinePoint(QueueKind.Compute, 3));
        Assert.NotEqual(new(QueueKind.Compute, 4), new TimelinePoint(QueueKind.Compute, 3));
    }

    /// <summary>A backend that implements nothing extra reports no timeline.</summary>
    /// <remarks>
    ///     ⚠ Held as the interface, and it has to be: a default interface member is not visible on
    ///     the concrete class. That is the property that keeps a backend from shadowing one by
    ///     accident, and it means every caller reaches these the way the render graph does.
    /// </remarks>
    [Fact]
    public void TheDefaultIsNoTimeline() {
        ICommandSubmitter submitter = new PlainSubmitter();
        Assert.False(submitter.HasTimeline);
    }

    /// <summary>And refuses the wait-value submit with a reason rather than ignoring it.</summary>
    /// <remarks>
    ///     ⚠ <b>Refusing beats returning <see cref="TimelinePoint.None" />.</b> A caller handed None
    ///     would go on to submit its dependent work with a wait that enforces nothing, which is a
    ///     half-synchronised frame that looks correct on the machine it was written on. The throw is
    ///     the only answer that cannot be mistaken for success.
    /// </remarks>
    [Fact]
    public void TheDefaultRefusesAWaitValueSubmit() {
        ICommandSubmitter submitter = new PlainSubmitter();

        var failure = Assert.Throws<NotSupportedException>(
            () => submitter.Submit([], [new(QueueKind.Graphics, 1)])
        );

        Assert.Contains("Transfer", failure.Message, StringComparison.Ordinal);
        Assert.Contains("HasTimeline", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>A backend with one queue and no way to order anything against anything.</summary>
    sealed class PlainSubmitter : ICommandSubmitter {
        public QueueKind Kind => QueueKind.Transfer;

        public void Submit(ReadOnlySpan<ICommandList> lists) { }

        public void WaitIdle() { }
    }
}
