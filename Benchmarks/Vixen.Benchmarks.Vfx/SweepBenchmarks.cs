// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using BenchmarkDotNet.Attributes;
using Vixen.Core.Mathematics;
using Vixen.Core.Threading;
using Vixen.Vfx;

namespace Vixen.Benchmarks.Vfx;

/// <summary>
///     One frame of a running effect, on one thread and on the scheduler's.
/// </summary>
/// <remarks>
///     <para>
///         The question this exists to answer is where <c>VfxSystem.ParallelThreshold</c> should sit.
///         A dispatch and a barrier cost a few microseconds whatever they carry, so below some
///         particle count the parallel path is slower — and the count depends on how expensive the
///         graph is, which is why both a cheap graph and an expensive one are measured.
///     </para>
///     <para>
///         The systems are warmed to their working population in <c>[GlobalSetup]</c>, so what is
///         timed is a frame of a running effect rather than one of a starting one.
///     </para>
///     <para>
///         <b>The particles are given a lifetime no run can reach.</b> A harness calls the method
///         under test millions of times, and every call advances the effect's clock by a frame, so a
///         lifetime of any plausible length expires part-way through the first iteration — after
///         which the sweep is over an empty buffer and the benchmark reports a few nanoseconds for
///         work it is no longer doing. <see cref="Cleanup" /> checks the population afterwards
///         rather than trusting that, because that failure is silent and looks like a good result.
///     </para>
/// </remarks>
[MemoryDiagnoser]
public class SweepBenchmarks {
    /// <summary>A lifetime long enough that no number of invocations reaches it. See the remarks.</summary>
    const float Forever = 1e9f;

    JobScheduler scheduler = null!;
    VfxSystem cheapSerial = null!;
    VfxSystem cheapParallel = null!;
    VfxSystem heavySerial = null!;
    VfxSystem heavyParallel = null!;

    /// <summary>How many particles are alive.</summary>
    [Params(256, 1024, 4096, 16384, 65536)]
    public int Count { get; set; }

    /// <summary>Gravity and an integration: about the least a graph can do per particle.</summary>
    static VfxCompiledGraph Cheap(int capacity) =>
        VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(capacity)],
            [
                new(VfxOpcode.PositionInBox, new Vector4(-8f, -8f, -8f, 0f)) { B = new(8f, 8f, 8f, 0f) },
                new(VfxOpcode.SetVelocity, Vector4.Zero),
                new(VfxOpcode.SetLifetime, new Vector4(Forever, Forever, 0f, 0f))
            ],
            [new(VfxOpcode.Gravity, new Vector4(0f, -9.81f, 0f, 0f)), new(VfxOpcode.Integrate)],
            capacity
        );

    /// <summary>Three fields, one of them three octaves of curl noise. About the most.</summary>
    static VfxCompiledGraph Heavy(int capacity) =>
        VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(capacity)],
            [
                new(VfxOpcode.PositionInBox, new Vector4(-8f, -8f, -8f, 0f)) { B = new(8f, 8f, 8f, 0f) },
                new(VfxOpcode.SetVelocity, Vector4.Zero),
                new(VfxOpcode.SetSize, new Vector4(0.1f, 0.4f, 0f, 0f)),
                new(VfxOpcode.SetColour, Vector4.One),
                new(VfxOpcode.SetLifetime, new Vector4(Forever, Forever, 0f, 0f))
            ],
            [
                new(VfxOpcode.Attract, new Vector4(0f, 5f, 0f, 4f)) { B = new(20f, 0f, 0f, 0f) },
                new(VfxOpcode.Vortex, new Vector4(0f, 0f, 0f, 3f)) { B = new(0f, 1f, 0f, 20f) },
                new(VfxOpcode.Turbulence, new Vector4(0.3f, 0.3f, 0.3f, 6f)) { B = new(0.5f, 3f, 0f, 0f) },
                new(VfxOpcode.Integrate),
                new(VfxOpcode.SizeOverLife, new Vector4(0.4f, 0f, 0f, 0f)),
                new(VfxOpcode.ColourOverLife, Vector4.One) { B = new(1f, 0.2f, 0f, 0f) }
            ],
            capacity
        );

    [GlobalSetup]
    public void Setup() {
        scheduler = new();

        cheapSerial = Warm(new(Cheap(Count)));
        cheapParallel = Warm(new(Cheap(Count)) { Scheduler = scheduler, ParallelThreshold = 1 });
        heavySerial = Warm(new(Heavy(Count)));
        heavyParallel = Warm(new(Heavy(Count)) { Scheduler = scheduler, ParallelThreshold = 1 });
    }

    [GlobalCleanup]
    public void Cleanup() {
        Check(cheapSerial, nameof(cheapSerial));
        Check(cheapParallel, nameof(cheapParallel));
        Check(heavySerial, nameof(heavySerial));
        Check(heavyParallel, nameof(heavyParallel));

        cheapSerial.Dispose();
        cheapParallel.Dispose();
        heavySerial.Dispose();
        heavyParallel.Dispose();
        scheduler.Dispose();
    }

    [Benchmark(Baseline = true)]
    public void CheapSerial() => cheapSerial.Step(1f / 60f);

    [Benchmark]
    public void CheapParallel() => cheapParallel.Step(1f / 60f);

    [Benchmark]
    public void HeavySerial() => heavySerial.Step(1f / 60f);

    [Benchmark]
    public void HeavyParallel() => heavyParallel.Step(1f / 60f);

    /// <summary>Fails the run if a system swept fewer particles than it was asked to.</summary>
    /// <remarks>
    ///     A population that drains part-way through does not fail, it just gets faster, and the
    ///     result is a plausible-looking table measuring nothing. Better to lose the run.
    /// </remarks>
    void Check(VfxSystem system, string name) {
        if (system.Count != Count) {
            throw new InvalidOperationException(
                $"{name} finished with {system.Count} of {Count} particles: the measurement is not of a full sweep."
            );
        }
    }

    /// <summary>Steps a system until its burst has landed, so the frame timed is a running one.</summary>
    static VfxSystem Warm(VfxSystem system) {
        system.Step(1f / 60f);
        system.Step(1f / 60f);

        return system;
    }
}
