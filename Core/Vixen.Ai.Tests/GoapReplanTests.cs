// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Xunit;

namespace Vixen.Ai.Tests;

/// <summary>
///     Doc 37's Testing table: a mid-plan world change produces a different, still-valid plan.
/// </summary>
/// <remarks>
///     ⚠ <b>The other half of § D11, and the half a "throw the stale head away" test does not
///     reach.</b> Discarding a plan whose head stopped being runnable is the safety property; making a
///     <i>new</i> and <i>correct</i> plan out of the world as it now is, without being told anything,
///     is what the planner is for. An agent that only ever threw plans away would look exactly like
///     one that worked, until it never got anywhere.
/// </remarks>
public class GoapReplanTests {
    [Fact]
    public void AWorldThatChangesMidPlanProducesADifferentPlanThatStillWorks() {
        var pears = new GoapPearTests.Pantry { OnGround = 1, Carried = 0, Hunger = 80 };
        var domain = GoapPearTests.Orchard(pears);
        var planner = new GoapPlanner(domain);
        var plan = new GoapPlan();
        var context = GoapHarness.Context();

        using (context.World) {
            // Nothing carried: two steps, pick up then eat.
            Assert.Equal(PlanFailure.None, planner.Resolve(in context, plan));
            Assert.Equal(["pick-up-pear", "eat-pear"], GoapPearTests.Names(domain, plan));

            // Somebody hands the agent a pear half-way through. The plan is now one step, and it is a
            // *different* plan rather than the same one with a step crossed off.
            pears.Carried = 1;

            Assert.Equal(PlanFailure.None, planner.Resolve(in context, plan));
            Assert.Equal(["eat-pear"], GoapPearTests.Names(domain, plan));

            // And the head of the new plan is runnable right now, which is what makes it valid —
            // § D11's whole arrangement is that the head is committed and nothing else is.
            Assert.True(Runnable(domain, plan.Head, in context), "the new plan's head cannot run.");

            // The orchard is stripped and the pear is eaten: there is nothing left to plan, and that
            // is reported rather than searched for ever.
            pears.Carried = 0;
            pears.OnGround = 0;

            Assert.Equal(PlanFailure.Unreachable, planner.Resolve(in context, plan));
            Assert.Equal(0, plan.Count);
        }
    }

    /// <summary>
    ///     ⚠ And a cheaper route appearing mid-plan is taken. A planner that only re-planned when its
    ///     current plan <i>broke</i> would walk past the shortcut.
    /// </summary>
    [Fact]
    public void ACheaperRouteAppearingMidPlanIsTheOneTakenNext() {
        var pears = new GoapPearTests.Pantry { OnGround = 1, Carried = 0, Hunger = 80 };
        var domain = GoapPearTests.Orchard(pears);
        var planner = new GoapPlanner(domain);
        var plan = new GoapPlan();
        var context = GoapHarness.Context();

        using (context.World) {
            planner.Resolve(in context, plan);

            var before = plan.Cost;

            pears.Carried = 1;
            planner.Resolve(in context, plan);

            Assert.True(plan.Cost < before, $"the shorter plan cost {plan.Cost} against {before}.");
        }
    }

    static bool Runnable(GoapDomain domain, int action, ref readonly AgentContext context) {
        Span<int> world = stackalloc int[64];

        domain.Keys.Project(in context, world);

        foreach (var condition in domain[action].Conditions) {
            if (!condition.Holds(world[..domain.Keys.Count])) {
                return false;
            }
        }

        return true;
    }
}
