// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Vixen.Core.Threading;
using Vixen.Ecs.Systems;
using Xunit;

namespace Vixen.Ecs.Tests;

/// <summary>
///     The ECS end of the job system's safety declarations: what a system declared reaches the
///     scheduler, and the scheduler refuses a schedule that lets two conflicting systems' jobs run
///     at once.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The bug this catches is a system that drops its handle.</b> <c>Update</c> returning
///         <c>dependency</c> instead of the handle for the work it just scheduled compiles, type
///         checks, and produces a phase whose <c>Complete</c> loop waits for nothing — the job runs
///         on into the next system's turn, and the runner's conflict graph, which is about systems
///         rather than about jobs, cannot see it. Nothing else in this repository catches it.
///     </para>
///     <para>
///         Every test here uses <c>new JobScheduler(0)</c>, so a scheduled job stays in flight until
///         something completes it and the overlap is a property of the program rather than of the
///         machine's load.
///     </para>
/// </remarks>
public sealed class SystemAccessSafetyTests {
    public SystemAccessSafetyTests() {
        ComponentRegistry.Of<Position>();
        ComponentRegistry.Of<Health>();
    }

    [Fact]
    public void ASystemsDeclarationReachesTheScheduler() {
        Assert.SkipWhen(!JobScheduler.SafetyChecksEnabled, "Compiled out; needs DEBUG or VIXEN_JOB_SAFETY.");

        using var scheduler = new JobScheduler(0);
        using var world = new World();
        using var runner = new SystemRunner(world, scheduler);

        runner.Add(new SchedulingWriterSystem(new(), drop: false));
        runner.RunPhase(SystemPhase.Update, default);

        // ⚠ The first thing to check, and the reason it is public. Without it a run in which the
        // runner never declared anything is indistinguishable from a run in which it declared
        // everything and found no conflict — and this whole mechanism reports success on the day it
        // does not run.
        Assert.Equal(1, scheduler.DeclaredJobsScheduled);
    }

    [Fact]
    public void ASystemThatDropsItsHandleIsCaughtByTheNextConflictingSystem() {
        Assert.SkipWhen(!JobScheduler.SafetyChecksEnabled, "Compiled out; needs DEBUG or VIXEN_JOB_SAFETY.");

        using var scheduler = new JobScheduler(0);
        using var world = new World();
        using var runner = new SystemRunner(world, scheduler);
        var counter = new StrongBox<int>();

        // Both write Position, so the graph orders the second after the first — by making the
        // second's job depend on whatever handle the first returned. The first returns `dependency`,
        // so there is no edge and the two jobs may run together.
        runner.Add(new SchedulingWriterSystem(counter, drop: true))
            .Add(new SecondSchedulingWriterSystem(counter));

        var failure = Assert.Throws<InvalidOperationException>(
            () => runner.RunPhase(SystemPhase.Update, default)
        );

        Assert.Contains("conflict", failure.Message, StringComparison.Ordinal);
        Assert.True(scheduler.AccessComparisons >= 1);
    }

    [Fact]
    public void ReturningTheHandleIsWhatMakesTheSameTwoSystemsLegal() {
        Assert.SkipWhen(!JobScheduler.SafetyChecksEnabled, "Compiled out; needs DEBUG or VIXEN_JOB_SAFETY.");

        using var scheduler = new JobScheduler(0);
        using var world = new World();
        using var runner = new SystemRunner(world, scheduler);
        var counter = new StrongBox<int>();

        runner.Add(new SchedulingWriterSystem(counter, drop: false))
            .Add(new SecondSchedulingWriterSystem(counter));

        runner.RunPhase(SystemPhase.Update, default);

        Assert.Equal(2, counter.Value);
        Assert.Equal(2, scheduler.DeclaredJobsScheduled);
    }

    [Fact]
    public void TwoSystemsThatWriteDifferentComponentsAreNotAConflict() {
        Assert.SkipWhen(!JobScheduler.SafetyChecksEnabled, "Compiled out; needs DEBUG or VIXEN_JOB_SAFETY.");

        using var scheduler = new JobScheduler(0);
        using var world = new World();
        using var runner = new SystemRunner(world, scheduler);
        var counter = new StrongBox<int>();

        // The one that drops its handle writes Position; the one after it writes Health. Disjoint,
        // so the graph gives the second no edge either — and the detector must agree, or it is a
        // predicate that fires on everything and proves nothing.
        runner.Add(new SchedulingWriterSystem(counter, drop: true)).Add(new SchedulingHealerSystem(counter));

        runner.RunPhase(SystemPhase.Update, default);

        Assert.True(scheduler.AccessComparisons >= 1);
    }

    [Fact]
    public void AnUndeclaredSystemIsDeclaredAsTouchingEverything() {
        // The system graph already reads an empty access as conflicting with everything, so the
        // declaration handed to the scheduler has to say the same thing. JobAccess.None would say
        // the opposite — "not declared, not policed" — and would make exactly the systems the graph
        // is most cautious about the ones the safety system ignores.
        Assert.True(SystemAccess.None.JobAccess.IsEverything);
        Assert.False(SystemAccess.None.JobAccess.IsUndeclared);
    }

    [Fact]
    public void ADeclaredSystemsAccessTravelsComponentForComponent() {
        var access = SystemAccess.Declare().Read<Health>().Write<Position>().Build();

        Assert.Equal([ComponentType<Position>.Id.Value], access.JobAccess.Writes);
        Assert.Equal(
            [.. new[] { ComponentType<Health>.Id.Value, ComponentType<Position>.Id.Value }.Order()],
            access.JobAccess.Reads
        );
    }

    // ---------------------------------------------------------------- systems under test

    struct CountingJob(StrongBox<int> counter) : IJob {
        public void Execute() => Interlocked.Increment(ref counter.Value);
    }

    [Writes(typeof(Position))]
    sealed class SchedulingWriterSystem(StrongBox<int> counter, bool drop) : SystemBase {
        public override JobHandle Update(in SystemContext context, JobHandle dependency) {
            var handle = context.Jobs!.Schedule(new CountingJob(counter), dependency);

            // `drop` is the defect, spelled out: the work is scheduled and the runner is told
            // nothing about it.
            return drop ? dependency : handle;
        }
    }

    [Writes(typeof(Position))]
    sealed class SecondSchedulingWriterSystem(StrongBox<int> counter) : SystemBase {
        public override JobHandle Update(in SystemContext context, JobHandle dependency) =>
            context.Jobs!.Schedule(new CountingJob(counter), dependency);
    }

    [Writes(typeof(Health))]
    sealed class SchedulingHealerSystem(StrongBox<int> counter) : SystemBase {
        public override JobHandle Update(in SystemContext context, JobHandle dependency) =>
            context.Jobs!.Schedule(new CountingJob(counter), dependency);
    }
}
