// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using BenchmarkDotNet.Attributes;
using Vixen.Core.Threading;

namespace Vixen.Benchmarks.Jobs;

/// <summary>
///     What it costs to hand one piece of work to another thread and get it back. This is the number
///     that decides how small a job is allowed to be.
/// </summary>
[MemoryDiagnoser]
public class SchedulingOverheadBenchmarks {
    JobScheduler scheduler = null!;
    int[] sink = null!;

    [GlobalSetup]
    public void Setup() {
        scheduler = new(Math.Max(1, Environment.ProcessorCount - 1));
        sink = new int[1];
    }

    [GlobalCleanup]
    public void Cleanup() => scheduler.Dispose();

    /// <summary>The floor: the same work, called directly.</summary>
    [Benchmark(Baseline = true)]
    public void Inline() {
        var job = new TouchJob(sink);
        job.Execute();
    }

    /// <summary>Schedule one job and wait for it.</summary>
    [Benchmark]
    public void ScheduleAndComplete() => scheduler.Complete(scheduler.Schedule(new TouchJob(sink)));

    /// <summary>The same shape on the thread pool, which is the thing this is instead of.</summary>
    [Benchmark]
    public void TaskRunAndWait() {
        var sink1 = sink;
        Task.Run(() => sink1[0]++).Wait();
    }

    /// <summary>
    ///     Schedule a hundred independent jobs, then wait once. The realistic shape: scheduling is
    ///     supposed to be cheap enough that a frame can queue its whole graph before it waits.
    /// </summary>
    [Benchmark]
    public void ScheduleHundredThenComplete() {
        Span<JobHandle> handles = stackalloc JobHandle[100];
        var job = new TouchJob(sink);

        for (var index = 0; index < handles.Length; index++) {
            handles[index] = scheduler.Schedule(in job);
        }

        foreach (var handle in handles) {
            scheduler.Complete(handle);
        }
    }

    /// <summary>A chain of a hundred jobs: every one waits for the one before it.</summary>
    [Benchmark]
    public void ChainOfHundred() {
        var job = new TouchJob(sink);
        var handle = default(JobHandle);

        for (var index = 0; index < 100; index++) {
            handle = scheduler.Schedule(in job, handle);
        }

        scheduler.Complete(handle);
    }

    struct TouchJob(int[] sink) : IJob {
        public void Execute() => sink[0]++;
    }
}
