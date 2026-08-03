// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation.Constraints;
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
    readonly GroupSink sink = new();
    Animator[] animators = [];
    ConstraintStack[] stacks = [];
    int count;
    int placedCount;
    int stackCount;

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

    /// <summary>What is solved together, and when in the frame.</summary>
    /// <remarks>
    ///     ⚠ <b>The default plans nothing at all, and that is what the stage costs.</b> With no
    ///     constraint stacks in the world the pre-evaluation pass does not run; with stacks but the
    ///     default scheduler it is one virtual call that returns, and every stack solves itself
    ///     inside its own animator's processor pass exactly as it would have without any of this.
    ///     The hook is here because it decides <see cref="ConstraintStack" />'s shape, and deciding
    ///     afterwards would mean opening up a type everything else already depends on.
    /// </remarks>
    public IConstraintScheduler Scheduler { get; set; } = DefaultConstraintScheduler.Shared;

    /// <summary>How many constraint stacks the last run found.</summary>
    public int LastStackCount => stackCount;

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
        PreEvaluate(deltaTime);
        PlanPoseGroups();
        Evaluate(deltaTime, jobs);
        SolvePoseGroups(deltaTime);
        Publish(world);
    }

    /// <summary>The stage before any character has a pose.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Why the frame needs a stage here and not only grouping later.</b> Two characters
    ///         whose goals reference each other cannot be solved independently without one of them
    ///         seeing last frame's pose of the other. Grouping them in the pose stage does not fix
    ///         that: the pose stage runs inside <see cref="IPoseProcessor" />, after every member has
    ///         already mixed its own layers against a stale view of the others. A group solved here
    ///         publishes to <see cref="ConstraintStack.Published" /> <em>before</em> anybody
    ///         evaluates, which is the only point in the frame where that is worth anything.
    ///     </para>
    /// </remarks>
    void PreEvaluate(float deltaTime) {
        for (var index = 0; index < stackCount; index++) {
            stacks[index].Unpublish();
            stacks[index].Scheduled = false;
        }

        if (stackCount == 0) {
            return;
        }

        sink.Reset(stacks, stackCount);
        Scheduler.PlanPreEvaluation(stacks.AsSpan(0, stackCount), sink);

        if (!sink.IsEmpty) {
            sink.Solve(deltaTime, publish: true);
        }
    }

    /// <summary>Works out who is solved together after evaluation, before anybody evaluates.</summary>
    /// <remarks>
    ///     ⚠ <b>Planned before the evaluation pass and solved after it, which is not the same as
    ///     planning it after.</b> A stack a group claims must not also solve itself inside its own
    ///     animator's processor pass, and that pass is part of evaluation — so the claim has to be in
    ///     place before evaluation starts or the correction is applied twice on the frame a group
    ///     forms. Grouping is a question about who references whom, which a scheduler can answer
    ///     without seeing a pose.
    /// </remarks>
    void PlanPoseGroups() {
        if (stackCount == 0) {
            return;
        }

        sink.Reset(stacks, stackCount);
        Scheduler.PlanPose(stacks.AsSpan(0, stackCount), sink);
        sink.Claim();
    }

    /// <summary>The groups a scheduler wanted solved once every character has a pose.</summary>
    /// <remarks>
    ///     A stack no group claims solved itself in its own animator's processor pass, which has
    ///     already run by the time this does. That is the ordinary path and the only one the default
    ///     scheduler ever takes.
    /// </remarks>
    void SolvePoseGroups(float deltaTime) {
        if (stackCount > 0 && !sink.IsEmpty) {
            sink.Solve(deltaTime, publish: false);
        }
    }

    void Gather(World world) {
        count = 0;
        stackCount = 0;
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
                CollectStacks(animator);
            }
        }
    }

    /// <summary>Finds the constraint stacks an animator is carrying.</summary>
    /// <remarks>
    ///     A type test per processor per animator, which is what "the empty stage is not measurable"
    ///     has to survive. It is done in the gather rather than in its own pass because this loop is
    ///     already walking every animator on this thread, and a second walk to find nothing would be
    ///     the measurable part.
    /// </remarks>
    void CollectStacks(Animator animator) {
        var processors = animator.PoseProcessors;

        for (var index = 0; index < processors.Count; index++) {
            if (processors[index] is not ConstraintStack stack) {
                continue;
            }

            if (stackCount == stacks.Length) {
                Array.Resize(ref stacks, Math.Max(8, stacks.Length * 2));
            }

            stack.Owner = animator;
            stacks[stackCount++] = stack;
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

    /// <summary>Collects a scheduler's groups into flat ranges, and then solves them.</summary>
    /// <remarks>
    ///     ⚠ <b>The members are copied into one array and the groups are ranges over it</b>, so a
    ///     scheduler that hands out a stack-allocated span is not quietly holding a pointer into a
    ///     frame that has returned — which is the failure a sink taking a span invites and the reason
    ///     it is worth spelling out. Both arrays keep their capacity between frames.
    /// </remarks>
    sealed class GroupSink : IConstraintGroupSink {
        readonly List<(int Start, int Count)> ranges = [];
        ConstraintStack[] members = [];
        ConstraintStack[] all = [];
        bool[] claimed = [];
        int filled;
        int total;

        public bool IsEmpty => ranges.Count == 0;

        public void Reset(ConstraintStack[] stacks, int count) {
            ranges.Clear();
            filled = 0;
            all = stacks;
            total = count;

            if (claimed.Length < count) {
                claimed = new bool[Math.Max(8, count)];
            }

            Array.Clear(claimed, 0, count);
        }

        public void Add(ReadOnlySpan<ConstraintStack> group) {
            if (group.IsEmpty) {
                return;
            }

            if (members.Length < filled + group.Length) {
                Array.Resize(ref members, Math.Max(8, Math.Max(members.Length * 2, filled + group.Length)));
            }

            group.CopyTo(members.AsSpan(filled));
            ranges.Add((filled, group.Length));
            filled += group.Length;
        }

        /// <summary>Tells every claimed stack to leave itself to the group.</summary>
        public void Claim() {
            for (var index = 0; index < total; index++) {
                claimed[index] = false;
            }

            foreach (var (start, length) in ranges) {
                for (var offset = 0; offset < length; offset++) {
                    var member = members[start + offset];

                    for (var index = 0; index < total; index++) {
                        if (ReferenceEquals(all[index], member)) {
                            claimed[index] = true;
                            break;
                        }
                    }
                }
            }

            for (var index = 0; index < total; index++) {
                all[index].Scheduled = claimed[index];
            }
        }

        /// <summary>Solves every group, in the order the scheduler declared them.</summary>
        /// <remarks>
        ///     Inline rather than across the job scheduler, deliberately. A group exists because its
        ///     members depend on each other, so the members of one group cannot run concurrently;
        ///     running <em>groups</em> concurrently would be worth doing and is not worth doing
        ///     against a default that produces none. When a real scheduler ships, this is the line
        ///     that changes.
        /// </remarks>
        public void Solve(float deltaTime, bool publish) {
            foreach (var (start, length) in ranges) {
                var group = new ConstraintGroup(members, start, length);

                for (var offset = 0; offset < length; offset++) {
                    var member = members[start + offset];

                    if (member.Owner is not { } animator) {
                        continue;
                    }

                    member.Solve(animator.Pose.Bones, deltaTime, group);

                    if (publish) {
                        member.Publish(animator.Pose.Bones);
                    }
                }
            }
        }
    }
}
