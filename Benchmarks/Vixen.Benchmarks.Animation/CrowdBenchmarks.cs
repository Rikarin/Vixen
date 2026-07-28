// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using BenchmarkDotNet.Attributes;
using Vixen.Animation;
using Vixen.Animation.Ecs;
using Vixen.Core.Threading;
using Vixen.Ecs;

namespace Vixen.Benchmarks.Animation;

/// <summary>
///     A crowd of animated characters through <see cref="AnimationSystem" />, inline and across the
///     job scheduler.
/// </summary>
/// <remarks>
///     <para>
///         The number that says whether the parallel path is worth having. Each character evaluates
///         a blend tree over sixty-four joints and touches nothing outside itself, so the ceiling is
///         the core count — and the floor is set by the gather and publish phases, which stay on one
///         thread because they are the ones that touch the world.
///     </para>
///     <para>
///         Sixteen characters is deliberately near the bottom: it is where the scheduling overhead
///         is comparable to the work, and where a parallel loop can lose. That is worth knowing
///         before a game with a dozen NPCs pays for it every frame.
///     </para>
/// </remarks>
[MemoryDiagnoser]
public class CrowdBenchmarks {
    World world = null!;
    AnimationSystem system = null!;
    JobScheduler scheduler = null!;

    /// <summary>How many animated characters are in the world.</summary>
    /// <remarks>
    ///     Sixteen is below the crossover, a hundred and twenty-eight is above it, and a thousand is
    ///     a crowd. <see cref="AnimationSystem.ParallelThreshold" /> is set from these numbers, so
    ///     changing them means changing it.
    /// </remarks>
    [Params(16, 128, 1024)]
    public int Characters { get; set; }

    [GlobalSetup]
    public void Setup() {
        var skeleton = Rigs.Humanoid();
        var walk = AnimationClip.Create(Rigs.Clip(skeleton, "Walk", 1.2f, 30), skeleton);
        var run = AnimationClip.Create(Rigs.Clip(skeleton, "Run", 0.8f, 30), skeleton);

        world = new(nameof(CrowdBenchmarks));
        system = new();
        scheduler = new(Math.Max(1, Environment.ProcessorCount - 1));

        for (var index = 0; index < Characters; index++) {
            var animator = Rigs.Character(skeleton, walk, run);

            // Spread the parameter across the tree, so some characters blend two motions and others
            // play one — which is what makes the batches uneven and the stealing matter.
            animator.Parameters.SetFloat("Speed", index % 7);
            world.Create(new AnimatorComponent { Value = animator });
        }
    }

    [GlobalCleanup]
    public void Cleanup() {
        scheduler.Dispose();
        world.Dispose();
    }

    [Benchmark(Baseline = true)]
    public void Inline() => system.Run(world, 1f / 60f);

    [Benchmark]
    public void Scheduled() => system.Run(world, 1f / 60f, scheduler);
}
