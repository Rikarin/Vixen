// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Vixen.Core;

using Vixen.Engine.Coroutines;
using Xunit;

namespace Vixen.Engine.Tests;

/// <summary>
///     The half of the coroutine design that is a number rather than a behaviour.
/// </summary>
/// <remarks>
///     <para>
///         Phase 2's stress sample runs ten thousand frames with <b>zero</b> gen-0 collections, and
///         that result is only worth having if it survives the feature everyone will use. A coroutine
///         is an <c>async</c> method, and an <c>async</c> method boxes its state machine the first
///         time it suspends — a thousand entities each running one is a thousand boxes per restart,
///         plus a continuation delegate each, and the criterion is gone the day anyone writes
///         gameplay code.
///     </para>
///     <para>
///         So it is measured here rather than reasoned about. These tests drive the scheduler
///         directly instead of through <c>EngineLoop</c>, so that what they measure is the coroutine
///         machinery and not a frame's worth of everything else.
///     </para>
/// </remarks>
public sealed class CoroutineAllocationTests {
    const int WarmUpFrames = 200;
    const int MeasuredFrames = 2_000;

    static readonly TimeSpan Sixtieth = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 60);

    [Fact]
    public void ALoopingCoroutineAllocatesNothingPerFrame() {
        var scheduler = new CoroutineScheduler();
        var time = GameTime.Zero;
        var resumes = 0;

        scheduler.BeginFrame(time = time.Advance(Sixtieth));
        scheduler.Run(Body());

        Assert.Equal(0, Measure(Frame));
        Assert.Equal(WarmUpFrames + MeasuredFrames, resumes);
        return;

        void Frame() {
            scheduler.BeginFrame(time = time.Advance(Sixtieth));
            scheduler.Drain(ResumePoint.Update);
        }

        async Coroutine Body() {
            while (true) {
                await scheduler.NextFrame();
                resumes++;
            }
        }
    }

    /// <summary>
    ///     A thousand of them at once, because the per-wait bookkeeping is a struct in a list and a
    ///     list that has to grow is a list that allocates. Once it has grown, it must not again.
    /// </summary>
    [Fact]
    public void AThousandLoopingCoroutinesAllocateNothingPerFrame() {
        var scheduler = new CoroutineScheduler();
        var time = GameTime.Zero;
        var resumes = 0;

        scheduler.BeginFrame(time = time.Advance(Sixtieth));

        for (var index = 0; index < 1_000; index++) {
            scheduler.Run(Body());
        }

        Assert.Equal(0, Measure(Frame));
        Assert.Equal(1_000 * (WarmUpFrames + MeasuredFrames), resumes);
        return;

        void Frame() {
            scheduler.BeginFrame(time = time.Advance(Sixtieth));
            scheduler.Drain(ResumePoint.Update);
        }

        async Coroutine Body() {
            while (true) {
                await scheduler.NextFrame();
                resumes++;
            }
        }
    }

    /// <summary>
    ///     The harder case, and the one that needs the pooled builder: a coroutine started and
    ///     finished every frame, so the <c>async</c> prologue runs again each time rather than once.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         In a <b>Release</b> build this is exactly zero, which is the claim: the state machine
    ///         comes out of <see cref="PoolingAsyncValueTaskMethodBuilder" />'s pool and everything
    ///         the scheduler adds — the waiting entry, the bookkeeping object, the continuation
    ///         delegate — is either a struct in a reused list or an object off a free list. The same
    ///         coroutine written as a plain <c>async ValueTask</c> costs 160 bytes a start, which is
    ///         what the builder is worth.
    ///     </para>
    ///     <para>
    ///         In a <b>Debug</b> build it is 88 bytes a start and cannot be less, because the C#
    ///         compiler emits an async method's state machine as a <b>class</b> rather than a struct
    ///         so that the debugger can inspect it. Every start of every <c>async</c> method in the
    ///         process allocates one, pool or no pool; a method that never suspends at all allocates
    ///         one. That is the compiler's choice about debuggability, and a test that failed over it
    ///         would be reporting the wrong thing — so the Debug assertion is a ceiling of one state
    ///         machine, which still catches the scheduler leaking an object per start.
    ///     </para>
    /// </remarks>
    [Fact]
    public void StartingACoroutineEveryFrameCostsNothingBeyondItsStateMachine() {
        var scheduler = new CoroutineScheduler();
        var time = GameTime.Zero;
        var finished = 0;

        scheduler.BeginFrame(time = time.Advance(Sixtieth));

        var perStart = Measure(Frame) / (double)MeasuredFrames;

        // Started before BeginFrame and resumed by the drain after it, so each pass starts one and
        // finishes it.
        Assert.Equal(WarmUpFrames + MeasuredFrames, finished);

#if DEBUG
        Assert.InRange(perStart, 0d, 128d);
#else
        Assert.Equal(0d, perStart);
#endif

        return;

        void Frame() {
            scheduler.Run(Body());
            scheduler.BeginFrame(time = time.Advance(Sixtieth));
            scheduler.Drain(ResumePoint.Update);
        }

        async Coroutine Body() {
            await scheduler.NextFrame();
            finished++;
        }
    }

    /// <summary>A timed wait is the same shape as a frame wait, and must cost the same nothing.</summary>
    [Fact]
    public void ATimedWaitAllocatesNothingPerFrame() {
        var scheduler = new CoroutineScheduler();
        var time = GameTime.Zero;
        var loops = 0;

        scheduler.BeginFrame(time = time.Advance(Sixtieth));
        scheduler.Run(Body());

        Assert.Equal(0, Measure(Frame));
        Assert.True(loops > 0);
        return;

        void Frame() {
            scheduler.BeginFrame(time = time.Advance(Sixtieth));
            scheduler.Drain(ResumePoint.Update);
        }

        async Coroutine Body() {
            while (true) {
                await scheduler.Seconds(0.05f);
                loops++;
            }
        }
    }

    /// <summary>
    ///     Runs the frame a few hundred times to let every list reach its size and every pool fill,
    ///     then measures what a few thousand more cost.
    /// </summary>
    /// <param name="frame">One frame.</param>
    /// <returns>Bytes allocated on this thread over the measured frames.</returns>
    static long Measure(Action frame) {
        for (var index = 0; index < WarmUpFrames; index++) {
            frame();
        }

        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var index = 0; index < MeasuredFrames; index++) {
            frame();
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
