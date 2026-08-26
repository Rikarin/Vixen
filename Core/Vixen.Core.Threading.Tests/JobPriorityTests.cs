// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Xunit;

namespace Vixen.Core.Threading.Tests;

/// <summary>
///     The two tiers: that frame work is preferred, that background work is not thereby starved, and
///     that a burst of long background jobs cannot occupy the whole pool.
/// </summary>
/// <remarks>
///     <para>
///         <b>Most of these run at <c>workerCount == 0</c> on purpose.</b> The take rule is the
///         property under test and with no workers the completing thread is the only taker, so the
///         order it takes things in is decided rather than raced. The same assertions with four
///         workers would be assertions about which thread happened to get there first, which is a
///         different and much weaker claim.
///     </para>
///     <para>
///         ⚠ <b>Nothing here is bounded by a clock.</b> The starvation property is expressed as work
///         completed against work completed — so many background jobs finished for so many frame
///         jobs finished — because a bound in milliseconds measures how busy the machine is and
///         passes or fails on that. The one <c>Wait</c> with a timeout below is bounding an
///         <em>impossibility</em>, not a speed: if the reservation is not there, the thing it waits
///         for can never happen at all.
///     </para>
/// </remarks>
public class JobPriorityTests {
    [Fact]
    public void TheDefaultTierIsTheOneThatCannotBeStarved() {
        // A caller that has not thought about it gets Frame, and `default` is Frame — so a struct
        // holding a priority it never set has not quietly opted into being deferred.
        Assert.Equal(JobPriority.Frame, default);
    }

    /// <summary>Frame work runs first even when the background work was scheduled first.</summary>
    [Fact]
    public void FrameWorkIsTakenBeforeBackgroundWorkThatWasQueuedEarlier() {
        using var scheduler = new JobScheduler(0);
        var clock = new StrongBox<int>();
        var stamps = new int[16];
        var background = new JobHandle[8];

        for (var index = 0; index < 8; index++) {
            background[index] = scheduler.Schedule(
                new StampJob(stamps, index, clock),
                priority: JobPriority.Background
            );
        }

        var frame = default(JobHandle);

        for (var index = 8; index < 16; index++) {
            frame = scheduler.Schedule(new StampJob(stamps, index, clock));
        }

        // Completing the last frame job runs every frame job — and, if the tiers mean anything, not
        // one of the eight background jobs that were queued before them.
        scheduler.Complete(frame);

        for (var index = 0; index < 8; index++) {
            Assert.Equal(0, stamps[index]);
        }

        for (var index = 8; index < 16; index++) {
            Assert.True(stamps[index] > 0, $"Frame job {index} did not run.");
        }

        // The control: the background jobs are runnable, and were only waiting. Without this the
        // assertion above would also pass on a scheduler that had simply dropped them.
        foreach (var handle in background) {
            scheduler.Complete(handle);
        }

        for (var index = 0; index < 8; index++) {
            Assert.True(stamps[index] > stamps[15], $"Background job {index} ran before the frame work.");
        }
    }

    /// <summary>
    ///     A stream of frame work that never stops does not stop background work from running.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The bound is work, not time.</b> The claim is "at least one background job finishes
    ///         for every <c>Share</c> frame jobs that finish", and both sides of that are counters
    ///         the jobs themselves increment. A version of this written as "background progress
    ///         within N milliseconds" would be measuring how many cores the CI machine had free.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The upper bound is half the test.</b> Strict priority with no fairness share
    ///         starves the background tier, and a scheduler with no priority at all runs the
    ///         background jobs <i>first</i> — they were scheduled first. Only a scheduler that does
    ///         both things lands strictly between nought and all of them.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ASteadyStreamOfFrameWorkStillLetsBackgroundWorkThrough() {
        // One background job for every `Share` frame jobs is the floor asserted. The scheduler's own
        // share is finer than this; the slack is what stops the test failing on an off-by-one in
        // where the count happened to start.
        const int Share = 128;
        const int FrameJobs = 1024;
        const int BackgroundJobs = 256;

        using var scheduler = new JobScheduler(0);
        var frameDone = new StrongBox<int>();
        var backgroundDone = new StrongBox<int>();
        var background = new JobHandle[BackgroundJobs];

        for (var index = 0; index < BackgroundJobs; index++) {
            background[index] = scheduler.Schedule(
                new IncrementJob(backgroundDone),
                priority: JobPriority.Background
            );
        }

        // The control at the other end: with nobody completing anything, nothing has run — so a
        // non-zero reading later is the fairness share and not the queue draining itself.
        Assert.Equal(0, backgroundDone.Value);

        for (var round = 0; round < FrameJobs; round++) {
            scheduler.Complete(scheduler.Schedule(new IncrementJob(frameDone)));
        }

        Assert.Equal(FrameJobs, frameDone.Value);

        Assert.True(
            backgroundDone.Value >= FrameJobs / Share,
            $"{frameDone.Value} frame jobs finished and only {backgroundDone.Value} background ones "
            + $"did, which is fewer than the one per {Share} the share promises."
        );

        Assert.True(
            backgroundDone.Value < BackgroundJobs,
            $"All {BackgroundJobs} background jobs finished, so frame work was not preferred at all."
        );

        // And the counter can reach the top, so the bound above is a real bound rather than a
        // ceiling the harness could never have crossed.
        foreach (var handle in background) {
            scheduler.Complete(handle);
        }

        Assert.Equal(BackgroundJobs, backgroundDone.Value);
    }

    /// <summary>A burst of background work never occupies every worker at once.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Measured from inside the jobs, not from outside.</b> "One worker stayed free" is a
    ///         claim about a moment, and the honest way to check a moment is to have the work itself
    ///         count how many of its own kind were running when it started. The first draft of this
    ///         instead scheduled a burst and then timed how long a frame job took to be picked up,
    ///         and it passed with the reservation removed — the burst had not reached the workers
    ///         yet when the frame job was queued, so it measured a race and called it a property.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Polled rather than completed, and that is the point.</b> A thread inside
    ///         <c>Complete</c> is a participant and participants are deliberately not under the
    ///         reservation, so completing here would add a fourth runner and the peak would be a
    ///         measurement of the harness. <c>IsCompleted</c> never runs a work item.
    ///     </para>
    /// </remarks>
    [Fact]
    public void NoMoreThanTheReserveAllowsRunBackgroundWorkAtOnce() {
        const int Workers = 4;

        using var scheduler = new JobScheduler(Workers);
        var live = new StrongBox<int>();
        var peak = new StrongBox<int>();

        // One batch per index and enough of them that every worker has ample opportunity to be
        // inside the tier at the same time as the others.
        var handle = scheduler.ScheduleParallel(
            new ConcurrencyProbeJob(live, peak, 2000),
            4000,
            1,
            priority: JobPriority.Background
        );

        while (!scheduler.IsCompleted(handle)) {
            Thread.Yield();
        }

        scheduler.Complete(handle);

        // The control: the probe does move, and more than one worker did reach the tier — so the
        // bound below is a bound and not a number the harness could never have exceeded.
        Assert.True(
            peak.Value >= 2,
            "Only one thread was ever in the background tier at once, so nothing here was bounded."
        );

        Assert.True(
            peak.Value <= Workers - 1,
            $"{peak.Value} of {Workers} workers were inside a background job at once, and one was "
            + "supposed to be held back for whatever the frame needs next."
        );
    }

    /// <summary>Waiting on background work is slower than waiting on the same work in the frame tier.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>This is why no existing call site can simply be relabelled.</b> Every
    ///         <c>JobScheduler</c> caller in the tree today is <c>ParallelFor</c> or
    ///         <c>ScheduleParallel(…).Complete()</c> — the calling thread is blocked on the very
    ///         batches it scheduled. Putting those in <see cref="JobPriority.Background" /> does not
    ///         make the call cheap; it makes the waiting thread drain every unrelated frame item it
    ///         can reach <i>first</i>, and only then run the thing it is waiting for. The tier is not
    ///         a no-op on work somebody is blocked on, it is a pessimisation, and a consumer has to
    ///         be one that keeps its handle rather than one that completes it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Order, not duration.</b> The assertion is that eight later-queued frame jobs
    ///         carry lower stamps than the background job the caller asked for, which is a fact about
    ///         the take order. Timing the two calls would be timing the machine. At
    ///         <c>workerCount == 0</c> the completing thread is the only taker, so the order is
    ///         decided rather than raced, and eight is far below the fairness share's 64 — so nothing
    ///         here is the share letting the background job through early.
    ///     </para>
    /// </remarks>
    [Fact]
    public void WaitingOnBackgroundWorkRunsUnrelatedFrameWorkFirst() {
        const int FrameJobs = 8;

        using var scheduler = new JobScheduler(0);
        var clock = new StrongBox<int>();
        var stamps = new int[FrameJobs + 1];

        // The one the caller is about to block on, in the tier that says "not first".
        var waited = scheduler.Schedule(new StampJob(stamps, 0, clock), priority: JobPriority.Background);

        // Frame work queued afterwards and joined to it by nothing: no edge, no shared state. A
        // caller waiting for `waited` has no reason to want any of these run first.
        for (var index = 1; index <= FrameJobs; index++) {
            scheduler.Schedule(new StampJob(stamps, index, clock));
        }

        scheduler.Complete(waited);

        Assert.True(stamps[0] > 0, "The job the caller waited for never ran.");

        for (var index = 1; index <= FrameJobs; index++) {
            Assert.True(
                stamps[index] > 0,
                $"Frame job {index} never ran, so the wait was not lengthened by it and this test "
                + "asserts nothing."
            );

            Assert.True(
                stamps[index] < stamps[0],
                $"Frame job {index} ran after the background job the caller was waiting for, so "
                + "waiting on the background tier cost nothing here."
            );
        }
    }

    [Fact]
    public void ABackgroundParallelForStillVisitsEveryIndexExactlyOnce() {
        using var scheduler = new JobScheduler(4);
        const int length = 5000;
        var visits = new int[length];

        scheduler.ParallelFor(new VisitJob(visits), length, 0, JobPriority.Background);

        for (var index = 0; index < length; index++) {
            Assert.Equal(1, visits[index]);
        }

        Assert.Equal(0, scheduler.OutstandingJobs);
    }

    /// <summary>An edge outranks a tier, because an edge is about inputs and a tier is about haste.</summary>
    [Fact]
    public void AFrameJobStillWaitsForTheBackgroundJobItDependsOn() {
        using var scheduler = new JobScheduler(0);
        var clock = new StrongBox<int>();
        var stamps = new int[2];

        var producer = scheduler.Schedule(new StampJob(stamps, 0, clock), priority: JobPriority.Background);
        var consumer = scheduler.Schedule(new StampJob(stamps, 1, clock), producer);

        scheduler.Complete(consumer);

        Assert.True(stamps[0] > 0, "The background dependency never ran.");
        Assert.True(stamps[0] < stamps[1], "The frame job ran before the background job it depends on.");
    }

    [Fact]
    public void ABackgroundJobThatThrowsStillSurfacesAtComplete() {
        using var scheduler = new JobScheduler(2);
        var handle = scheduler.Schedule(new ThrowingJob(), priority: JobPriority.Background);

        var thrown = Assert.Throws<JobExecutionException>(() => scheduler.Complete(handle));
        Assert.IsType<InvalidOperationException>(thrown.InnerException);
    }

    [Fact]
    public void DisposeDrainsTheBackgroundTierToo() {
        var counter = new StrongBox<int>();
        var scheduler = new JobScheduler(2);

        for (var index = 0; index < 200; index++) {
            scheduler.Schedule(new SpinJob(counter, 500), priority: JobPriority.Background);
        }

        scheduler.Dispose();

        Assert.Equal(200, counter.Value);
    }
}
