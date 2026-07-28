// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Vixen.Testing;
using Xunit;

namespace Vixen.Core.Threading.Tests;

public class JobSchedulerTests {
    [Fact]
    public void AScheduledJobRuns() {
        using var scheduler = new JobScheduler(2);
        var counter = new StrongBox<int>();

        scheduler.Complete(scheduler.Schedule(new IncrementJob(counter)));

        Assert.Equal(1, counter.Value);
    }

    [Fact]
    public void TheNullHandleIsAlreadyComplete() {
        using var scheduler = new JobScheduler(2);
        var handle = default(JobHandle);

        Assert.True(handle.IsNull);
        Assert.True(handle.IsCompleted);
        Assert.True(scheduler.IsCompleted(handle));

        // And completing it is a no-op rather than a wait for something that will never happen.
        handle.Complete();
    }

    [Fact]
    public void CompleteThroughTheHandleFindsItsOwnScheduler() {
        using var scheduler = new JobScheduler(2);
        var counter = new StrongBox<int>();

        var handle = scheduler.Schedule(new IncrementJob(counter));
        handle.Complete();

        Assert.Equal(1, counter.Value);
        Assert.True(handle.IsCompleted);
    }

    [Fact]
    public void ADependentJobRunsAfterWhatItDependsOn() {
        using var scheduler = new JobScheduler(4);
        var clock = new StrongBox<int>();
        var stamps = new int[2];

        var first = scheduler.Schedule(new StampJob(stamps, 0, clock));
        var second = scheduler.Schedule(new StampJob(stamps, 1, clock), first);
        scheduler.Complete(second);

        Assert.True(stamps[0] < stamps[1], $"Expected {stamps[0]} to precede {stamps[1]}.");
    }

    [Fact]
    public void ADiamondRunsItsMiddleInParallelAndItsJoinLast() {
        using var scheduler = new JobScheduler(4);
        var clock = new StrongBox<int>();
        var stamps = new int[4];

        var a = scheduler.Schedule(new StampJob(stamps, 0, clock));
        var b = scheduler.Schedule(new StampJob(stamps, 1, clock), a);
        var c = scheduler.Schedule(new StampJob(stamps, 2, clock), a);
        var d = scheduler.Schedule(new StampJob(stamps, 3, clock), [b, c]);
        scheduler.Complete(d);

        Assert.True(stamps[0] < stamps[1]);
        Assert.True(stamps[0] < stamps[2]);
        Assert.True(stamps[1] < stamps[3]);
        Assert.True(stamps[2] < stamps[3]);
    }

    [Fact]
    public void DependingOnAJobThatHasAlreadyFinishedIsFree() {
        using var scheduler = new JobScheduler(2);
        var counter = new StrongBox<int>();

        var first = scheduler.Schedule(new IncrementJob(counter));
        scheduler.Complete(first);

        // `first` is finished, and its slot may already have been reissued. The edge has to resolve
        // to "satisfied" rather than to whatever occupies the slot now.
        var second = scheduler.Schedule(new IncrementJob(counter), first);
        scheduler.Complete(second);

        Assert.Equal(2, counter.Value);
    }

    [Fact]
    public void CombineWaitsForEveryHandle() {
        using var scheduler = new JobScheduler(4);
        var counter = new StrongBox<int>();
        var handles = new JobHandle[32];

        for (var index = 0; index < handles.Length; index++) {
            handles[index] = scheduler.Schedule(new SpinJob(counter, 500));
        }

        JobHandle.Combine(handles).Complete();

        Assert.Equal(handles.Length, counter.Value);
    }

    [Fact]
    public void CombiningNothingIsTheNullHandle() {
        using var scheduler = new JobScheduler(2);
        Assert.True(JobHandle.Combine([]).IsNull);
        Assert.True(JobHandle.Combine([default, default]).IsNull);
        Assert.Equal(0, scheduler.OutstandingJobs);
    }

    [Fact]
    public void ParallelForVisitsEveryIndexExactlyOnce() {
        using var scheduler = new JobScheduler(4);

        foreach (var length in (int[]) [1, 2, 7, 63, 64, 65, 1000, 10_007]) {
            var visits = new int[length];
            scheduler.ParallelFor(new VisitJob(visits), length);

            for (var index = 0; index < length; index++) {
                Assert.Equal(1, visits[index]);
            }
        }
    }

    [Fact]
    public void ParallelForHonoursAnExplicitBatchSize() {
        using var scheduler = new JobScheduler(4);
        const int length = 1000;
        var written = new int[length];

        // A batch size that does not divide the length: the last batch is short, and an off-by-one
        // in the range arithmetic shows up as a hole at one end or an overrun at the other.
        scheduler.ParallelFor(new WriteIndexJob(written), length, 37);

        for (var index = 0; index < length; index++) {
            Assert.Equal(index + 1, written[index]);
        }
    }

    [Fact]
    public void ParallelForOverNothingCompletesWithoutRunning() {
        using var scheduler = new JobScheduler(2);
        var visits = new int[1];

        scheduler.ParallelFor(new VisitJob(visits), 0);

        Assert.Equal(0, visits[0]);
        Assert.Equal(0, scheduler.OutstandingJobs);
    }

    [Fact]
    public void ParallelForRejectsANegativeLength() {
        using var scheduler = new JobScheduler(2);
        Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.ParallelFor(new VisitJob([]), -1));
    }

    [Fact]
    public void ParallelForActuallyUsesMoreThanOneThread() {
        using var scheduler = new JobScheduler(4);
        const int length = 4096;
        var threads = new int[length];
        using var twoArrived = new ManualResetEventSlim();

        // The batches rendezvous rather than racing. Simply counting distinct thread ids afterwards
        // asserted something the scheduler does not promise: work stealing lets the calling thread
        // drain every batch before a worker reaches one, which is not a bug and is exactly what
        // happens on a machine busy running the rest of the suite. Holding each batch until a second
        // thread arrives tests what was meant — that the work *can* be spread — and is not a race.
        scheduler.ParallelFor(new RecordThreadJob(threads, new(), twoArrived), length, 16);

        var distinct = new HashSet<int>(threads);
        Assert.True(distinct.Count > 1, "Every index ran on one thread, so nothing was parallel.");
    }

    [Fact]
    public void AJobThatThrowsSurfacesAtComplete() {
        using var scheduler = new JobScheduler(2);
        var handle = scheduler.Schedule(new ThrowingJob());

        var thrown = Assert.Throws<JobExecutionException>(() => scheduler.Complete(handle));
        var inner = Assert.IsType<InvalidOperationException>(thrown.InnerException);
        Assert.Equal("The job threw on purpose.", inner.Message);
    }

    [Fact]
    public void AJobThatThrowsDoesNotTakeTheWorkerWithIt() {
        using var scheduler = new JobScheduler(2);
        var counter = new StrongBox<int>();

        for (var round = 0; round < 50; round++) {
            Assert.Throws<JobExecutionException>(() => scheduler.Complete(scheduler.Schedule(new ThrowingJob())));
        }

        // If the throwing jobs had killed the workers, this would hang rather than fail.
        scheduler.Complete(scheduler.Schedule(new IncrementJob(counter)));
        Assert.Equal(1, counter.Value);
    }

    [Fact]
    public void AFailedDependencySkipsItsDependentsRatherThanFeedingThemNothing() {
        using var scheduler = new JobScheduler(2);
        var flags = new bool[1];

        var failing = scheduler.Schedule(new ThrowingJob());
        var dependent = scheduler.Schedule(new FlagJob(flags, 0), failing);

        Assert.Throws<JobExecutionException>(() => scheduler.Complete(dependent));
        Assert.False(flags[0], "The dependent ran even though the job producing its input threw.");
    }

    [Fact]
    public void AFailureIsStillReportableAfterTheSlotHasBeenReused() {
        using var scheduler = new JobScheduler(2);
        var counter = new StrongBox<int>();
        var failed = scheduler.Schedule(new ThrowingJob());

        // Churn the ring right past the slot the failing job used, so nothing about it survives on
        // the slot itself. The handle still has to be able to say what happened.
        for (var index = 0; index < JobScheduler.MaxJobsInFlight * 2; index++) {
            scheduler.Complete(scheduler.Schedule(new IncrementJob(counter)));
        }

        Assert.Throws<JobExecutionException>(() => scheduler.Complete(failed));
    }

    [Fact]
    public void AFailureReachesADependentScheduledBeforeItHappened() {
        using var scheduler = new JobScheduler(2);
        var flags = new bool[1];
        var gate = new ManualResetEventSlim(false);

        // The other order from the test above: the graph is complete before the failure occurs, so
        // the edge exists and the failure travels along it rather than being looked up.
        var failing = scheduler.Schedule(new GatedThrowingJob(gate));
        var dependent = scheduler.Schedule(new FlagJob(flags, 0), failing);
        gate.Set();

        Assert.Throws<JobExecutionException>(() => scheduler.Complete(dependent));
        Assert.False(flags[0], "The dependent ran even though the job producing its input threw.");
    }

    [Fact]
    public void OneFailingBatchFailsTheWholeParallelJob() {
        using var scheduler = new JobScheduler(4);
        var handle = scheduler.ScheduleParallel(new ThrowingParallelJob(500), 1000, 16);

        Assert.Throws<JobExecutionException>(() => scheduler.Complete(handle));
    }

    [Fact]
    public void AHandleFromAnotherSchedulerIsRejectedRatherThanIgnored() {
        using var first = new JobScheduler(2);
        using var second = new JobScheduler(2);
        var counter = new StrongBox<int>();

        var foreign = first.Schedule(new SpinJob(counter, 10_000));

        Assert.Throws<ArgumentException>(() => second.Complete(foreign));
        var job = new IncrementJob(counter);
        Assert.Throws<ArgumentException>(() => second.Schedule(in job, foreign));

        first.Complete(foreign);

        // The rejected schedule must not have consumed a slot on the way out.
        Assert.Equal(0, second.OutstandingJobs);
    }

    [Fact]
    public void CombiningAcrossSchedulersIsRejected() {
        using var first = new JobScheduler(2);
        using var second = new JobScheduler(2);
        var counter = new StrongBox<int>();

        var a = first.Schedule(new SpinJob(counter, 10_000));
        var b = second.Schedule(new SpinJob(counter, 10_000));

        Assert.Throws<ArgumentException>(() => JobHandle.Combine([a, b]));

        first.Complete(a);
        second.Complete(b);
    }

    [Fact]
    public void AJobCannotWaitForItself() {
        Assert.SkipWhen(!JobScheduler.SafetyChecksEnabled, "Compiled out; needs DEBUG or VIXEN_JOB_SAFETY.");

        using var scheduler = new JobScheduler(2);
        var caught = new Exception?[1];
        var handleBox = new JobHandle[1];
        var ready = new ManualResetEventSlim(false);

        // The job waits for its own handle, which the test hands it through a shared array.
        var job = new SelfWaitingJob(handleBox, ready, caught);
        handleBox[0] = scheduler.Schedule(in job);
        ready.Set();
        scheduler.Complete(handleBox[0]);

        Assert.IsType<InvalidOperationException>(caught[0]);
    }

    [Fact]
    public void JobsMayScheduleMoreJobs() {
        using var scheduler = new JobScheduler(4);
        var counter = new StrongBox<int>();

        // A job that schedules children and completes them is the shape every recursive algorithm
        // in the engine takes: it only works if a worker waiting for its children keeps working.
        var root = scheduler.Schedule(new ForkJob(scheduler, counter, 3));
        scheduler.Complete(root);

        // 1 + 2 + 4 + 8 nodes for depth 3.
        Assert.Equal(15, counter.Value);
    }

    /// <summary>
    ///     A thread that is running no job can complete a job in any slot, including the first.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The self-completion guard reads a thread-static holding the slot the thread is
    ///         executing. A thread that has never executed a work item — the main thread, in every
    ///         application — has the default value in it, and the default value was <c>0</c>, which
    ///         is a real slot index. So <c>Complete</c> on a job that happened to live in slot 0
    ///         threw "a job cannot complete itself" at a caller that was doing nothing of the kind.
    ///     </para>
    ///     <para>
    ///         It hid for two reasons. The free list is last-in-first-out, so slot 0 is the
    ///         <i>thousand-and-twenty-fourth</i> one handed out and an ordinary test never reaches
    ///         it; and the guard is compiled out of release builds, which is what CI runs. It
    ///         surfaced as an intermittent failure of the test below, on a machine loaded enough
    ///         that the slot ring actually ran dry. Renting every slot before completing any is what
    ///         makes it certain instead.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AJobInAnySlotCanBeCompletedByAThreadRunningNothing() {
        using var scheduler = new JobScheduler(2);
        using var gate = new ManualResetEventSlim(false);
        var counter = new StrongBox<int>();
        var handles = new JobHandle[JobScheduler.MaxJobsInFlight];
        var slots = new HashSet<int>();

        // Gated, so nothing finishes and nothing returns a slot: every index is rented exactly once.
        for (var index = 0; index < handles.Length; index++) {
            handles[index] = scheduler.Schedule(new GatedIncrementJob(gate, counter));
            slots.Add(handles[index].Index);
        }

        Assert.Contains(0, slots);
        Assert.Equal(handles.Length, slots.Count);

        gate.Set();

        foreach (var handle in handles) {
            scheduler.Complete(handle);
        }

        Assert.Equal(handles.Length, counter.Value);
    }

    [Fact]
    public void MoreJobsThanTheRingHoldsStillCompletes() {
        using var scheduler = new JobScheduler(2);
        var clock = new StrongBox<int>();
        const int count = JobScheduler.MaxJobsInFlight * 3;
        var stamps = new int[count];
        var handle = default(JobHandle);

        // A chain three times longer than the slot ring. Scheduling has to make room by running
        // what is already scheduled, rather than failing or deadlocking.
        for (var index = 0; index < count; index++) {
            handle = scheduler.Schedule(new StampJob(stamps, index, clock), handle);
        }

        scheduler.Complete(handle);

        for (var index = 1; index < count; index++) {
            Assert.True(stamps[index - 1] < stamps[index], $"Job {index} ran before job {index - 1}.");
        }
    }

    /// <summary>
    ///     After the last <c>Complete</c> returns, the scheduler is idle — not idle a few
    ///     instructions later.
    /// </summary>
    /// <remarks>
    ///     Repeated, because the first version of this ran once and passed for a week before failing
    ///     on a loaded machine. A slot used to go back on the free list *after* its job's completion
    ///     became visible, so a waiter could be released while the scheduler still counted the job
    ///     as outstanding. Twenty rounds is enough for that window to be hit reliably rather than
    ///     occasionally.
    /// </remarks>
    [Fact]
    public void EverySlotComesBackWhenTheWorkIsDone() {
        using var scheduler = new JobScheduler(4);
        var handles = new JobHandle[500];

        for (var round = 0; round < 20; round++) {
            var counter = new StrongBox<int>();

            for (var index = 0; index < handles.Length; index++) {
                handles[index] = scheduler.Schedule(new SpinJob(counter, 100));
            }

            JobHandle.Combine(handles).Complete();

            Assert.Equal(handles.Length, counter.Value);
            Assert.Equal(0, scheduler.OutstandingJobs);
        }
    }

    [Fact]
    public void DisposeDrainsWhatIsStillOutstanding() {
        var counter = new StrongBox<int>();
        var scheduler = new JobScheduler(4);

        for (var index = 0; index < 200; index++) {
            scheduler.Schedule(new SpinJob(counter, 2000));
        }

        scheduler.Dispose();

        Assert.Equal(200, counter.Value);
    }

    /// <summary>Scheduling allocates nothing, in steady state, and steady state is the claim.</summary>
    /// <remarks>
    ///     <para>
    ///         Two one-time costs, and only the first is obvious. The first job of a type allocates
    ///         its payload array and registers its profiling key. The second is 56 bytes on the
    ///         scheduling thread, somewhere inside the BCL synchronisation this sits on, reached
    ///         only when a job is made ready by the scheduling thread and the workers have parked.
    ///         Probing it over two thousand iterations produced one occurrence, occasionally two —
    ///         so it is lazy initialisation and not a per-schedule cost, and the steady-state claim
    ///         holds.
    ///     </para>
    ///     <para>
    ///         Which is why the warm-up is the measurement, run twice with the second read. A
    ///         cheaper warm-up was tried and does not reach it; a test that passes or fails on
    ///         whether the workers happened to park during the run is worse than no test.
    ///     </para>
    /// </remarks>
    [Fact]
    public void SchedulingAllocatesNothing() {
        using var scheduler = new JobScheduler(2);
        var counter = new StrongBox<int>();
        var warmup = new IncrementJob(counter);
        scheduler.Complete(scheduler.Schedule(in warmup));

        // The second one-time cost is only reachable by doing the thing being measured, so the
        // warm-up is the measurement, run twice and read the second time — which is exactly one
        // warm-up pass and one measured one.
        var rounds = 0;
        var allocated = Measured.Bytes(Chain, warmUp: 1, passes: 1);

        // Counted rather than predicted: a non-zero reading is measured again, so the chain may have
        // run more than the two passes asked for.
        Assert.Equal((64 * rounds) + 1, counter.Value);
        Assert.Equal(0, allocated);

        return;

        void Chain() {
            var handle = default(JobHandle);

            for (var index = 0; index < 64; index++) {
                handle = scheduler.Schedule(in warmup, handle);
            }

            scheduler.Complete(handle);
            rounds++;
        }
    }

    /// <summary>
    ///     A random DAG, run repeatedly. The graph is where the concurrency bugs are: an edge added
    ///     while its dependency is finishing, a counter that reaches zero twice, a slot reissued
    ///     under a handle somebody still holds. None of those reproduce on a chain of two.
    /// </summary>
    [Fact]
    public void ARandomGraphRespectsEveryEdge() {
        using var scheduler = new JobScheduler(Math.Max(2, Environment.ProcessorCount - 1));
        var random = new Random(20260726);

        for (var round = 0; round < 20; round++) {
            const int count = 400;
            var stamps = new int[count];
            var clock = new StrongBox<int>();
            var handles = new JobHandle[count];
            var edges = new List<(int From, int To)>();
            var dependencies = new List<JobHandle>();

            for (var index = 0; index < count; index++) {
                dependencies.Clear();
                var edgeCount = random.Next(0, 4);

                for (var edge = 0; edge < edgeCount && index > 0; edge++) {
                    var from = random.Next(index);
                    dependencies.Add(handles[from]);
                    edges.Add((from, index));
                }

                handles[index] = scheduler.Schedule(new StampJob(stamps, index, clock), dependencies.ToArray());
            }

            JobHandle.Combine(handles).Complete();

            Assert.Equal(count, clock.Value);

            foreach (var (from, to) in edges) {
                Assert.True(stamps[from] < stamps[to], $"Round {round}: {to} ran before {from}.");
            }
        }
    }

    struct GatedThrowingJob(ManualResetEventSlim gate) : IJob {
        public void Execute() {
            gate.Wait();
            throw new InvalidOperationException("The job threw on purpose.");
        }
    }

    struct RecordThreadJob(
        int[] threads,
        ConcurrentDictionary<int, bool> announced,
        ManualResetEventSlim twoArrived
    ) : IJobParallelFor {
        public void Execute(int index) {
            var thread = Environment.CurrentManagedThreadId;
            threads[index] = thread;

            if (announced.TryAdd(thread, true) && announced.Count >= 2) {
                twoArrived.Set();
            }

            // Hold the batch until a second thread has shown up, so the caller cannot legitimately
            // drain every batch itself. Bounded, because a machine with one usable core would
            // otherwise hang here rather than fail.
            twoArrived.Wait(TimeSpan.FromSeconds(5));
        }
    }

    struct SelfWaitingJob(JobHandle[] handle, ManualResetEventSlim ready, Exception?[] caught) : IJob {
        public void Execute() {
            ready.Wait();

            try {
                handle[0].Complete();
            } catch (InvalidOperationException exception) {
                caught[0] = exception;
            }
        }
    }

    struct ForkJob(JobScheduler scheduler, StrongBox<int> counter, int depth) : IJob {
        public void Execute() {
            Interlocked.Increment(ref counter.Value);

            if (depth == 0) {
                return;
            }

            var left = scheduler.Schedule(new ForkJob(scheduler, counter, depth - 1));
            var right = scheduler.Schedule(new ForkJob(scheduler, counter, depth - 1));
            scheduler.Complete(left);
            scheduler.Complete(right);
        }
    }
}
