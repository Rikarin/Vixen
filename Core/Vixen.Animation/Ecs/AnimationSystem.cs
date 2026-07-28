// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Threading;
using Vixen.Ecs;
using Vixen.Ecs.Systems;
using Vixen.Engine.Transforms;

namespace Vixen.Animation.Ecs;

/// <summary>
///     Runs every animator once a frame, across the job scheduler, and puts root motion where it was
///     asked to.
/// </summary>
/// <remarks>
///     <para>
///         In <see cref="SystemPhase.Animation" />, which the ECS defines as "after logic has decided
///         what is playing" and before <see cref="SystemPhase.LateUpdate" /> and the transform pass.
///         That ordering is what makes root motion work: the delta is written to
///         <see cref="LocalTransform" /> here, and <c>TransformSystem</c> in
///         <see cref="SystemPhase.PreRender" /> composes the world matrix from it afterwards.
///     </para>
///     <para>
///         <b>Gather, evaluate in parallel, then publish.</b> Evaluating an animator is a graph walk
///         over a hundred joints and touches nothing outside that animator, which makes it the ideal
///         shape for the scheduler — but it is reached through a managed component, so the ECS hands
///         it over one entity at a time and the world must not be touched from a worker. The three
///         phases separate those concerns: the gather reads the world on this thread and produces a
///         flat array; the evaluation runs across the workers and reads nothing but the array; the
///         publish writes transforms back on this thread. Nothing in the middle phase can see an
///         <see cref="Entity" />, which is what makes it safe rather than merely untested.
///     </para>
///     <para>
///         Below <see cref="ParallelThreshold" /> animators — or with no scheduler, which is what a
///         test and a headless tool have — it runs inline. Scheduling three characters costs more
///         than animating them.
///     </para>
///     <para>
///         <b>One animator per entity.</b> Two entities sharing an <see cref="Animator" /> was
///         already wrong — it would be stepped twice a frame — and in parallel it is a data race.
///         Nothing checks for it, the same way nothing checks that two entities do not share a
///         transform.
///     </para>
///     <para>
///         <b>Two queries rather than one and a test.</b> Whether an entity has a
///         <see cref="LocalTransform" /> is an archetype question, so asking it per entity would be
///         paying per frame for something the archetype already answered. An animator on an entity
///         with no transform is a legitimate thing — a UI rig, a test — and it simply does not get
///         the root-motion branch.
///     </para>
/// </remarks>
[UpdateInGroup(SystemPhase.Animation)]
public sealed class AnimationSystem : SystemBase, IDeclaredAccess {
    /// <summary>How many animators there have to be before the scheduler is worth its overhead.</summary>
    /// <remarks>
    ///     <para>
    ///         Measured rather than guessed, and higher than it looks like it should be.
    ///         <c>Benchmarks/Vixen.Benchmarks.Animation</c>'s crowd case on a ten-core M1 Max makes
    ///         sixteen characters roughly <b>two and a half times slower</b> scheduled than inline —
    ///         waking workers for a few hundred microseconds of work costs more than the work — and
    ///         a hundred and twenty-eight several times faster. Thirty-two sits above the loss and
    ///         below the win.
    ///     </para>
    ///     <para>
    ///         Machine-dependent, obviously: the crossover moves with core count, with how quickly
    ///         the OS parks and wakes a thread, and with how heavy the graphs are. A game that knows
    ///         its own numbers should re-measure, which is what the benchmark is for.
    ///     </para>
    /// </remarks>
    public const int ParallelThreshold = 32;

    readonly QueryDescription placed = new QueryDescription()
        .WithAll<AnimatorComponent, LocalTransform>();

    readonly QueryDescription unplaced = new QueryDescription()
        .WithAll<AnimatorComponent>()
        .WithNone<LocalTransform>();

    readonly List<Entity> entities = [];
    Animator[] animators = [];
    int count;
    int placedCount;

    /// <inheritdoc />
    /// <remarks>
    ///     Declared rather than attributed, for the reason <c>TransformSystem</c> gives: naming a
    ///     component type in a generic call is what assigns it an id, and an attribute can only look
    ///     one up.
    /// </remarks>
    public SystemAccess Access { get; } = SystemAccess.Declare()
        .Read<AnimatorComponent>()
        .Write<LocalTransform>()
        .Write<RootMotionResult>()
        .Build();

    /// <summary>How many animators the last run evaluated.</summary>
    public int LastEvaluatedCount => count;

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        Run(context.World, context.Time.DeltaSeconds, context.Jobs);
        return dependency;
    }

    /// <summary>Updates every animator in a world.</summary>
    /// <param name="world">The world.</param>
    /// <param name="deltaTime">How much time has passed, in seconds.</param>
    /// <param name="jobs">The scheduler, or <see langword="null" /> to run inline.</param>
    /// <remarks>Public so a test, a tool or an editor can step animation without a runner.</remarks>
    public void Run(World world, float deltaTime, JobScheduler? jobs = null) {
        ArgumentNullException.ThrowIfNull(world);

        Gather(world);
        Evaluate(deltaTime, jobs);
        Publish(world);
    }

    void Gather(World world) {
        count = 0;
        entities.Clear();

        Collect(world, placed);
        placedCount = count;
        Collect(world, unplaced);
    }

    void Collect(World world, QueryDescription query) {
        foreach (var chunk in world.Chunks(query)) {
            var found = chunk.Entities;

            for (var index = 0; index < chunk.Count; index++) {
                var animator = world.Read<AnimatorComponent>(found[index]).Value;

                if (animator is null) {
                    continue;
                }

                if (count == animators.Length) {
                    Array.Resize(ref animators, Math.Max(16, animators.Length * 2));
                }

                animators[count++] = animator;
                entities.Add(found[index]);
            }
        }
    }

    void Evaluate(float deltaTime, JobScheduler? jobs) {
        if (jobs is null || count < ParallelThreshold) {
            for (var index = 0; index < count; index++) {
                animators[index].Update(deltaTime);
            }

            return;
        }

        // Batch size left to the scheduler. It aims for four batches per thread, which is the right
        // shape here: animators cost wildly different amounts — one plays a clip, the next evaluates
        // a two-dimensional tree under three layers — so the stealing is what evens them out.
        jobs.ParallelFor(new UpdateJob(animators, deltaTime), count);
    }

    void Publish(World world) {
        for (var index = 0; index < count; index++) {
            var entity = entities[index];
            var animator = animators[index];

            if (world.Has<RootMotionResult>(entity)) {
                world.Get<RootMotionResult>(entity).Delta = animator.LastRootMotion;
            }

            if (index >= placedCount
                || animator.RootMotion is not RootMotionMode.Apply
                || animator.LastRootMotion.IsZero) {
                continue;
            }

            ref var local = ref world.Get<LocalTransform>(entity);

            // The delta is in the character's own frame, so it composes onto the local transform the
            // same way a child's transform composes onto its parent's: motion first, then where the
            // character already was. Row-vector order — Conventions.md.
            var moved = BoneTransform.Concatenate(
                animator.LastRootMotion.ToTransform(),
                new(local.Position, local.Rotation, local.Scale)
            );

            local.Position = moved.Translation;
            local.Rotation = moved.Rotation;
        }
    }

    /// <summary>One animator's update, as work the scheduler can hand to any thread.</summary>
    readonly struct UpdateJob(Animator[] animators, float deltaTime) : IJobParallelFor {
        public void Execute(int index) => animators[index].Update(deltaTime);
    }
}
