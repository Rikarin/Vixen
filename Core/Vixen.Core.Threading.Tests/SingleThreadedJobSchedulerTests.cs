// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Xunit;

namespace Vixen.Core.Threading.Tests;

/// <summary>
///     The scheduler with no workers at all — the shape a browser tab without
///     <c>SharedArrayBuffer</c> is stuck with, and the one <c>docs/plan/10 § Cross-platform
///     discipline</c> requires every subsystem to work under.
/// </summary>
/// <remarks>
///     These assert the properties that stop being free once nothing is running in the background:
///     that the graph is still a graph, that dependency order still holds, that a parallel-for still
///     visits every index, and that teardown still drains. The rest of
///     <see cref="JobSchedulerTests" /> covers behaviour that is the same either way; duplicating it
///     here would be duplicating it, and the CI leg that runs the whole suite at
///     <c>workerCount == 0</c> is what covers that.
/// </remarks>
public class SingleThreadedJobSchedulerTests {
    [Fact]
    public void ZeroWorkersIsAllowedAndSaysSo() {
        using var scheduler = new JobScheduler(0);

        Assert.Equal(0, scheduler.WorkerCount);
        Assert.True(scheduler.IsSingleThreaded);
        Assert.False(scheduler.IsWorkerThread);
    }

    [Fact]
    public void ANegativeWorkerCountIsStillRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new JobScheduler(-1));

    [Fact]
    public void WorkersAreNotSingleThreaded() {
        using var scheduler = new JobScheduler(1);
        Assert.False(scheduler.IsSingleThreaded);
    }

    [Fact]
    public void AScheduledJobRunsOnTheCompletingThread() {
        using var scheduler = new JobScheduler(0);
        var ran = new int[1];

        var handle = scheduler.Schedule(new RecordThreadJob(ran));
        scheduler.Complete(handle);

        Assert.Equal(Environment.CurrentManagedThreadId, ran[0]);
    }

    [Fact]
    public void NothingRunsUntilSomebodyCompletes() {
        using var scheduler = new JobScheduler(0);
        var counter = new StrongBox<int>();

        var handle = scheduler.Schedule(new IncrementJob(counter));

        // The contract, stated as a test because it is the one real behavioural difference: with
        // no workers there is nothing to pick the job up, so it has provably not run yet. Sleeping
        // to "give it a chance" would be asserting the opposite of what this mode promises.
        Assert.Equal(0, counter.Value);
        Assert.Equal(1, scheduler.OutstandingJobs);

        scheduler.Complete(handle);

        Assert.Equal(1, counter.Value);
        Assert.Equal(0, scheduler.OutstandingJobs);
    }

    [Fact]
    public void DependencyOrderHoldsWithoutWorkers() {
        using var scheduler = new JobScheduler(0);
        var clock = new StrongBox<int>();
        var stamps = new int[4];

        var a = scheduler.Schedule(new StampJob(stamps, 0, clock));
        var b = scheduler.Schedule(new StampJob(stamps, 1, clock), a);
        var c = scheduler.Schedule(new StampJob(stamps, 2, clock), a);
        var d = scheduler.Schedule(new StampJob(stamps, 3, clock), [b, c]);

        scheduler.Complete(d);

        Assert.True(stamps[0] < stamps[1], $"Expected {stamps[0]} to precede {stamps[1]}.");
        Assert.True(stamps[0] < stamps[2], $"Expected {stamps[0]} to precede {stamps[2]}.");
        Assert.True(stamps[1] < stamps[3], $"Expected {stamps[1]} to precede {stamps[3]}.");
        Assert.True(stamps[2] < stamps[3], $"Expected {stamps[2]} to precede {stamps[3]}.");
    }

    [Fact]
    public void CompletingOneJobRunsWhateverElseIsReady() {
        using var scheduler = new JobScheduler(0);
        var counter = new StrongBox<int>();

        // Unrelated work, scheduled first. Complete() takes ready items in queue order rather than
        // hunting for the one it wants, which is what keeps a browser frame's graph moving.
        var unrelated = scheduler.Schedule(new IncrementJob(counter));
        var wanted = scheduler.Schedule(new IncrementJob(counter));

        scheduler.Complete(wanted);

        Assert.Equal(2, counter.Value);
        Assert.True(scheduler.IsCompleted(unrelated));
    }

    [Fact]
    public void ParallelForVisitsEveryIndexExactlyOnce() {
        using var scheduler = new JobScheduler(0);

        foreach (var length in (int[]) [1, 2, 7, 63, 64, 65, 1000, 10_007]) {
            var visits = new int[length];
            scheduler.ParallelFor(new VisitJob(visits), length);

            for (var index = 0; index < length; index++) {
                Assert.Equal(1, visits[index]);
            }
        }
    }

    [Fact]
    public void ParallelForHonoursAnExplicitBatchSizeWithoutWorkers() {
        using var scheduler = new JobScheduler(0);
        const int length = 1000;
        var written = new int[length];

        scheduler.ParallelFor(new WriteIndexJob(written), length, 37);

        for (var index = 0; index < length; index++) {
            Assert.Equal(index + 1, written[index]);
        }
    }

    [Fact]
    public void ParallelForOverNothingCompletesWithoutRunning() {
        using var scheduler = new JobScheduler(0);
        var visits = new int[1];

        scheduler.ParallelFor(new VisitJob(visits), 0);

        Assert.Equal(0, visits[0]);
        Assert.Equal(0, scheduler.OutstandingJobs);
    }

    [Fact]
    public void ThePickedBatchSizeIsOneBatchWhenNobodyCanSteal() {
        using var scheduler = new JobScheduler(0);
        var threads = new int[64];

        scheduler.ParallelFor(new RecordThreadPerIndexJob(threads), threads.Length);

        // One participant, so the four-batches-per-participant split buys nothing and is not taken.
        // Observable only as "one thread ran all of it", which is also the property that matters.
        Assert.All(threads, thread => Assert.Equal(Environment.CurrentManagedThreadId, thread));
    }

    [Fact]
    public void AFailureStillReachesWhoeverCompletes() {
        using var scheduler = new JobScheduler(0);

        var handle = scheduler.Schedule(new ThrowingJob());
        var exception = Assert.Throws<JobExecutionException>(() => scheduler.Complete(handle));

        Assert.IsType<InvalidOperationException>(exception.InnerException);
    }

    [Fact]
    public void AJobWhoseDependencyThrewIsSkippedRatherThanRun() {
        using var scheduler = new JobScheduler(0);
        var flags = new bool[1];

        var failing = scheduler.Schedule(new ThrowingJob());
        var dependent = scheduler.Schedule(new FlagJob(flags, 0), failing);

        Assert.Throws<JobExecutionException>(() => scheduler.Complete(dependent));
        Assert.False(flags[0]);
    }

    [Fact]
    public void DisposeDrainsWorkNobodyCompleted() {
        var counter = new StrongBox<int>();
        var scheduler = new JobScheduler(0);

        for (var index = 0; index < 100; index++) {
            scheduler.Schedule(new IncrementJob(counter));
        }

        Assert.Equal(0, counter.Value);

        // The reason Dispose drains rather than abandons: a job writes into memory the scheduler
        // does not own, and with no workers there was never anybody else to finish it.
        scheduler.Dispose();

        Assert.Equal(100, counter.Value);
    }

    [Fact]
    public void RunningOutOfSlotsIsPaidForByTheThreadThatAsks() {
        using var scheduler = new JobScheduler(0);
        var counter = new StrongBox<int>();

        // More jobs than the ring holds, none of them completed. Each Schedule past the limit has
        // to execute an already-scheduled one to free the slot it wants — which is the only thing
        // that can happen here, because nothing else is running.
        for (var index = 0; index < JobScheduler.MaxJobsInFlight * 2; index++) {
            scheduler.Schedule(new IncrementJob(counter));
        }

        Assert.True(counter.Value > 0, "Scheduling past the ring should have run something.");
    }

    [Fact]
    public void AJobMayScheduleAndCompleteMoreWorkFromInsideItself() {
        using var scheduler = new JobScheduler(0);
        var counter = new StrongBox<int>();

        scheduler.Complete(scheduler.Schedule(new NestedJob(scheduler, counter, 3)));

        // 1 + 2 + 4 + 8: a binary fork three deep, every level of it run by the one thread.
        Assert.Equal(15, counter.Value);
    }

    /// <summary>Records which thread ran it.</summary>
    struct RecordThreadJob(int[] ran) : IJob {
        public void Execute() => ran[0] = Environment.CurrentManagedThreadId;
    }

    /// <summary>Records which thread ran each index.</summary>
    struct RecordThreadPerIndexJob(int[] threads) : IJobParallelFor {
        public void Execute(int index) => threads[index] = Environment.CurrentManagedThreadId;
    }

    /// <summary>Forks twice per level, completing both halves before returning.</summary>
    struct NestedJob(JobScheduler scheduler, StrongBox<int> counter, int depth) : IJob {
        public void Execute() {
            counter.Value++;

            if (depth == 0) {
                return;
            }

            var left = scheduler.Schedule(new NestedJob(scheduler, counter, depth - 1));
            var right = scheduler.Schedule(new NestedJob(scheduler, counter, depth - 1));
            scheduler.Complete(left);
            scheduler.Complete(right);
        }
    }
}
