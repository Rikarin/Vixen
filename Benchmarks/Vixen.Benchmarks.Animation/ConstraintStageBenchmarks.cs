// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using BenchmarkDotNet.Attributes;
using Vixen.Animation;
using Vixen.Animation.Constraints;
using Vixen.Animation.Ecs;
using Vixen.Animation.Moves;
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
    World few = null!;
    World crowded = null!;
    AnimationSystem bareSystem = null!;
    AnimationSystem emptySystem = null!;
    AnimationSystem solvingSystem = null!;
    AnimationSystem fewSystem = null!;
    AnimationSystem crowdedSystem = null!;

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
        few = Crowd(nameof(few), skeleton, walk, run, goals: 2, shapes: 8);
        crowded = Crowd(nameof(crowded), skeleton, walk, run, goals: 2, shapes: 120);

        bareSystem = new();
        emptySystem = new() { Scheduler = DefaultConstraintScheduler.Shared };
        solvingSystem = new() { Scheduler = DefaultConstraintScheduler.Shared };
        fewSystem = new() { Scheduler = DefaultConstraintScheduler.Shared };
        crowdedSystem = new() { Scheduler = DefaultConstraintScheduler.Shared };
    }

    [GlobalCleanup]
    public void Cleanup() {
        bare.Dispose();
        empty.Dispose();
        solving.Dispose();
        few.Dispose();
        crowded.Dispose();
    }

    [Benchmark(Baseline = true)]
    public void Bare() => bareSystem.Run(bare, 1f / 60f);

    [Benchmark]
    public void Empty() => emptySystem.Run(empty, 1f / 60f);

    [Benchmark]
    public void Solving() => solvingSystem.Run(solving, 1f / 60f);

    /// <summary>Two goals, one of them a surface contact, on a body carrying eight proxy shapes.</summary>
    /// <remarks>
    ///     The pair to compare is this and <see cref="SolvingWithManyShapes" />, not either of them
    ///     against <see cref="Solving" /> — a surface frame costs more to resolve than a world point
    ///     whatever the shape count, and that difference is the goal's, not the set's.
    /// </remarks>
    [Benchmark]
    public void SolvingWithFewShapes() => fewSystem.Run(few, 1f / 60f);

    /// <summary>The same two goals, on a body carrying a hundred and twenty.</summary>
    /// <remarks>
    ///     ⚠ <b>Has to match <see cref="SolvingWithFewShapes" />.</b> Proxy shapes are posed lazily —
    ///     the stage walks the active goals, collects the shapes their frames name, and poses only
    ///     those — and this is the wall-clock half of that claim. The exact half is the unit test
    ///     asserting that two were posed out of a hundred and twenty-two.
    /// </remarks>
    [Benchmark]
    public void SolvingWithManyShapes() => crowdedSystem.Run(crowded, 1f / 60f);

    World Crowd(
        string name,
        Skeleton skeleton,
        AnimationClip walk,
        AnimationClip run,
        int goals,
        int shapes = 0
    ) {
        var world = new World($"{nameof(ConstraintStageBenchmarks)}-{name}");

        for (var index = 0; index < Characters; index++) {
            var animator = Rigs.Character(skeleton, walk, run);

            animator.Parameters.SetFloat("Speed", index % 7);

            if (goals > 0) {
                animator.PoseProcessors.Add(Constrained(skeleton, goals, shapes));
            }

            world.Create(new AnimatorComponent { Value = animator });
        }

        return world;
    }

    static ConstraintStack Constrained(Skeleton skeleton, int goals, int shapes) {
        var stack = new ConstraintStack(skeleton);
        var tip = skeleton.JointCount - 1;

        if (shapes > 0) {
            stack.Shapes = new(Shapes(skeleton, shapes));
        }

        // The same goal count either way, so the two rows compare. With shapes, one of them is
        // expressed against a surface rather than against a world point — which is the only reason a
        // shape gets posed at all.
        for (var index = shapes > 0 ? 1 : 0; index < goals; index++) {
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

        if (shapes > 0) {
            // One of the hundred and twenty is named; the rest are there to not be posed.
            stack.Add(
                new PositionGoal {
                    Effector = tip,
                    Chain = new(tip - 2, tip),
                    Goal = new SurfaceFrame(SurfaceCoordinate.On("shape-0", SurfacePoint.Side)),
                    EaseIn = 0f
                }
            );
        }

        return stack;
    }

    static ProxyShapeSet Shapes(Skeleton skeleton, int count) {
        var shapes = new ProxyShape[count];

        for (var index = 0; index < count; index++) {
            shapes[index] = new() {
                Name = Symbol.Intern($"shape-{index}"),
                Kind = ShapeKind.Capsule,
                Joint = index % skeleton.JointCount,
                Dimensions = ShapeParams.Capsule(0.05f, 0.1f)
            };
        }

        return ProxyShapeSet.Of("Body", null, shapes);
    }
}
