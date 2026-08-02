// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using BenchmarkDotNet.Attributes;
using Vixen.Animation;
using Vixen.Animation.Constraints;
using Vixen.Animation.Ecs;
using Vixen.Core.Mathematics;
using Vixen.Ecs;

namespace Vixen.Benchmarks.Animation;

/// <summary>What the constraint stage costs a game that is not using it.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The number the stage has to justify itself with.</b> The frame gained a pass before
///         any animator evaluates, and it ships doing nothing — so the honest question is not how fast
///         the pass is but whether it is <em>visible at all</em> against the same crowd without it.
///         <see cref="Bare" /> is that build: a hundred characters, no constraint stacks, so the pass
///         finds nothing and returns. It is the baseline, and <see cref="Empty" /> — the same crowd
///         with a scheduler installed that plans nothing — is what has to match it.
///     </para>
///     <para>
///         <see cref="Solving" /> is what a game that actually uses it pays: two goals per character,
///         resolved, arbitrated and solved every frame. Not a cost the other two share, and reported
///         beside them so the difference between "the hook" and "the feature" is a number rather than
///         an argument.
///     </para>
/// </remarks>
[MemoryDiagnoser]
public class ConstraintStageBenchmarks {
    World bare = null!;
    World empty = null!;
    World solving = null!;
    AnimationSystem bareSystem = null!;
    AnimationSystem emptySystem = null!;
    AnimationSystem solvingSystem = null!;

    /// <summary>How many animated characters are in the world.</summary>
    [Params(100)]
    public int Characters { get; set; }

    [GlobalSetup]
    public void Setup() {
        var skeleton = Rigs.Humanoid();
        var walk = AnimationClip.Create(Rigs.Clip(skeleton, "Walk", 1.2f, 30), skeleton);
        var run = AnimationClip.Create(Rigs.Clip(skeleton, "Run", 0.8f, 30), skeleton);

        bare = Crowd(nameof(bare), skeleton, walk, run, goals: 0);
        empty = Crowd(nameof(empty), skeleton, walk, run, goals: 0);
        solving = Crowd(nameof(solving), skeleton, walk, run, goals: 2);

        bareSystem = new();
        emptySystem = new() { Scheduler = DefaultConstraintScheduler.Shared };
        solvingSystem = new() { Scheduler = DefaultConstraintScheduler.Shared };
    }

    [GlobalCleanup]
    public void Cleanup() {
        bare.Dispose();
        empty.Dispose();
        solving.Dispose();
    }

    [Benchmark(Baseline = true)]
    public void Bare() => bareSystem.Run(bare, 1f / 60f);

    [Benchmark]
    public void Empty() => emptySystem.Run(empty, 1f / 60f);

    [Benchmark]
    public void Solving() => solvingSystem.Run(solving, 1f / 60f);

    World Crowd(string name, Skeleton skeleton, AnimationClip walk, AnimationClip run, int goals) {
        var world = new World($"{nameof(ConstraintStageBenchmarks)}-{name}");

        for (var index = 0; index < Characters; index++) {
            var animator = Rigs.Character(skeleton, walk, run);

            animator.Parameters.SetFloat("Speed", index % 7);

            if (goals > 0) {
                animator.PoseProcessors.Add(Constrained(skeleton, goals));
            }

            world.Create(new AnimatorComponent { Value = animator });
        }

        return world;
    }

    static ConstraintStack Constrained(Skeleton skeleton, int goals) {
        var stack = new ConstraintStack(skeleton);
        var tip = skeleton.JointCount - 1;

        for (var index = 0; index < goals; index++) {
            var joint = tip - (index * 4);

            stack.Add(
                new PositionGoal {
                    Effector = joint,
                    Chain = new(joint - 2, joint),
                    Goal = new WorldFrame(new Vector3(0.3f, 1.1f + index, 0.4f)),
                    EaseIn = 0f
                }
            );
        }

        return stack;
    }
}
