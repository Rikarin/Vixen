// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ai.Ecs;
using Vixen.Core;
using Vixen.Core.Threading;
using Vixen.Ecs;
using Vixen.Ecs.Systems;
using Xunit;

namespace Vixen.Ai.Tests;

/// <summary>
///     The scheduled half of <see cref="GoapPlanQueue" />, which nothing had ever executed.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b><c>GoapPlanQueue.Scheduler</c> was assigned by no production caller, no test and no
///         benchmark.</b> #456 filed it as one of four seams "assigned only by benchmarks and tests";
///         for this one the tests and the benchmarks did not assign it either, so the whole
///         <c>ScheduleParallel</c> branch — and the planner arithmetic underneath it — had never run
///         anywhere at all. That is the reason the defect below survived review.
///     </para>
///     <para>
///         <b>The defect.</b> The batch is <c>resolves</c>, the caller's number, and the planner count
///         is the queue's. Scheduling one job per search over <c>planners[index % planners.Length]</c>
///         is safe only while the searches run one after another; with a scheduler and a
///         <c>resolves</c> above <c>parallelSearches</c> it put two <em>concurrent</em> searches on
///         one planner, which is one node pool and one open list.
///     </para>
///     <para>
///         <b>Why the assertion is on lanes and not on a corrupt plan.</b> A shared planner is a data
///         race, so a test that ran the old code and looked at the answers would be asking a
///         scheduler to lose a coin toss — green on the run that happened not to overlap. The lane
///         count is the invariant instead: a lane owns a planner, so there may never be more lanes
///         than planners, and that is decided before a single job is queued.
///     </para>
/// </remarks>
public sealed class GoapPlanQueueSchedulerTests {
    /// <summary>
    ///     However large a <c>resolves</c> the caller passes, the queue never schedules more jobs
    ///     than it has planners.
    /// </summary>
    [Fact]
    public void MoreResolvesThanPlannersStillNeverPutsTwoSearchesOnOnePlanner() {
        var pantry = new GoapPearTests.Pantry { OnGround = 4, Carried = 0, Hunger = 80 };
        using var jobs = new JobScheduler(4);
        var queue = new GoapPlanQueue(GoapPearTests.Orchard(pantry), capacity: 16, parallelSearches: 2) {
            Scheduler = jobs
        };

        var context = GoapHarness.Context();
        var tickets = new GoapPlanRequest[8];

        for (var index = 0; index < tickets.Length; index++) {
            tickets[index] = queue.Submit(in context);

            Assert.False(tickets[index].IsNull, $"the queue refused request {index}.");
        }

        queue.Update(tickets.Length);

        // The whole batch ran — the fix partitions the work, it does not drop any of it.
        Assert.Equal(tickets.Length, queue.LastResolves);

        // And it ran on two lanes rather than eight jobs, because there are two planners.
        Assert.Equal(2, queue.PlannerCount);
        Assert.Equal(queue.PlannerCount, queue.LastLanes);
        Assert.True(
            queue.LastLanes <= queue.PlannerCount,
            $"{queue.LastLanes} lanes over {queue.PlannerCount} planners shares a node pool."
        );

        // Every one of them came back, and came back planned rather than empty.
        var plan = new GoapPlan();

        foreach (var ticket in tickets) {
            Assert.True(queue.TryTakeResult(ticket, plan));
            Assert.Equal(PlanFailure.None, plan.Failure);
            Assert.Equal(["pick-up-pear", "eat-pear"], GoapPearTests.Names(queue.Domain, plan));
        }
    }

    /// <summary>
    ///     A queue with no scheduler reports one lane, which is what says the counter tracks the
    ///     branch actually taken rather than the arithmetic beside it.
    /// </summary>
    /// <remarks>
    ///     The instrument check. A <c>LastLanes</c> that were simply
    ///     <c>Math.Min(batch, planners.Length)</c> would read 2 here as well and would then be green
    ///     against a queue that never scheduled anything.
    /// </remarks>
    [Fact]
    public void AQueueWithNoSchedulerRunsOnOneLane() {
        var pantry = new GoapPearTests.Pantry { OnGround = 4, Carried = 0, Hunger = 80 };
        var queue = new GoapPlanQueue(GoapPearTests.Orchard(pantry), capacity: 16, parallelSearches: 2);
        var context = GoapHarness.Context();

        for (var index = 0; index < 8; index++) {
            queue.Submit(in context);
        }

        queue.Update(8);

        Assert.Equal(8, queue.LastResolves);
        Assert.Equal(1, queue.LastLanes);
    }

    /// <summary>
    ///     The scheduled path gives the same plans as the inline one, for the same requests.
    /// </summary>
    /// <remarks>
    ///     A search reads a snapshot taken at <c>Submit</c> and writes only its own slot, so the
    ///     answer cannot depend on which thread ran it — and this is what says the lane partitioning
    ///     did not silently reorder which request got which plan.
    /// </remarks>
    [Fact]
    public void TheScheduledPathAgreesWithTheInlineOne() {
        Assert.Equal(Resolve(scheduled: false), Resolve(scheduled: true));

        return;

        static int[] Resolve(bool scheduled) {
            var pantry = new GoapPearTests.Pantry { OnGround = 4, Carried = 0, Hunger = 80 };
            using JobScheduler? jobs = scheduled ? new(4) : null;
            var queue = new GoapPlanQueue(GoapPearTests.Orchard(pantry), capacity: 16, parallelSearches: 3) {
                Scheduler = jobs
            };

            var context = GoapHarness.Context();
            var tickets = new GoapPlanRequest[7];

            for (var index = 0; index < tickets.Length; index++) {
                tickets[index] = queue.Submit(in context);
            }

            queue.Update(tickets.Length);

            var plan = new GoapPlan();
            var counts = new int[tickets.Length];

            for (var index = 0; index < tickets.Length; index++) {
                Assert.True(queue.TryTakeResult(tickets[index], plan));

                counts[index] = plan.Count;
            }

            return counts;
        }
    }

    /// <summary>
    ///     <c>AiSystem</c> hands its queues the runner's scheduler, which is the wiring #456 asked
    ///     for.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Taken from the context, so it arrives on <c>Update</c> and not on <c>Step</c>.</b>
    ///     <c>Step</c> is the seam a tool without a runner uses and a tool without a runner has no
    ///     scheduler to hand over; the second half of this asserts that a queue reached that way is
    ///     left alone rather than given a null.
    /// </remarks>
    [Fact]
    public void TheRunnersSchedulerReachesEveryPlanQueue() {
        using var world = new World("goap-wiring");
        using var jobs = new JobScheduler(2);
        var pantry = new GoapPearTests.Pantry { OnGround = 4, Carried = 0, Hunger = 80 };
        using var system = new AiSystem(new AgentActionRegistry(), BlackboardLayout.Empty);

        system.Domains.Add(GoapPearTests.Orchard(pantry));

        var queue = system.Queue(0);

        Assert.Null(queue.Scheduler);

        // Step is the runner-less seam, and it must leave the seam alone rather than clearing it.
        system.Step(world, GameTime.Zero);

        Assert.Null(queue.Scheduler);

        system.Update(new SystemContext(world, GameTime.Zero, jobs, new CommandBuffer(world)), default);

        Assert.Same(jobs, queue.Scheduler);
    }
}
