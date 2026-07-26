// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using BenchmarkDotNet.Attributes;
using Vixen.Core.Threading;

namespace Vixen.Benchmarks.Jobs;

/// <summary>
///     The same loop three ways. <c>Parallel.For</c> is the thing a reasonable person would reach for
///     instead of this library, so it is the number that has to be beaten — or, if it is not beaten,
///     the number that says so.
/// </summary>
[MemoryDiagnoser]
public class ParallelForBenchmarks {
    float[] input = null!;
    float[] output = null!;
    JobScheduler scheduler = null!;

    /// <summary>How many elements the loop covers.</summary>
    /// <remarks>
    ///     The small end is deliberate. A thousand elements of cheap work is where the dispatch
    ///     overhead is comparable to the work, and where a parallel loop can lose to a serial one —
    ///     which is worth knowing before something schedules one per frame.
    /// </remarks>
    [Params(1024, 65_536, 1_048_576)]
    public int Length { get; set; }

    [GlobalSetup]
    public void Setup() {
        scheduler = new(Math.Max(1, Environment.ProcessorCount - 1));
        input = new float[Length];
        output = new float[Length];

        for (var index = 0; index < Length; index++) {
            input[index] = index * 0.001f;
        }
    }

    [GlobalCleanup]
    public void Cleanup() => scheduler.Dispose();

    [Benchmark(Baseline = true)]
    public void Serial() {
        for (var index = 0; index < input.Length; index++) {
            output[index] = Transform(input[index]);
        }
    }

    [Benchmark]
    public void ParallelFor() {
        var local = input;
        var target = output;
        System.Threading.Tasks.Parallel.For(0, local.Length, index => target[index] = Transform(local[index]));
    }

    [Benchmark]
    public void JobParallelFor() => scheduler.ParallelFor(new TransformJob(input, output), Length);

    /// <summary>Enough arithmetic per element to be worth a thread, and not so much as to hide the dispatch.</summary>
    internal static float Transform(float value) => MathF.Sqrt(value * value + 1f) * MathF.Sin(value);

    struct TransformJob(float[] input, float[] output) : IJobParallelFor {
        public void Execute(int index) => output[index] = Transform(input[index]);
    }
}
