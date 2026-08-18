// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Vixen.Ai.Ecs;
using Vixen.Core;
using Vixen.Ecs;
using Xunit;

namespace Vixen.Ai.Tests;

/// <summary>
///     P6's first exit criterion: the pear test — the reference scenario every GOAP implementation is
///     demonstrated with.
/// </summary>
/// <remarks>
///     A hungry agent, a pear on the ground and two actions: picking one up needs a pear to be there
///     and increases what it is carrying; eating one needs something carried and decreases hunger. The
///     plan is <c>PickUpPear</c> then <c>EatPear</c>, and it is found <b>backwards</b> — from the goal,
///     through the effect that serves its condition, to an action that can run now.
///
///     ⚠ <b>The whole matching rule is a direction.</b> "Eating <i>reduces</i> hunger" is a fact about
///     the action that stays true while a designer tunes the numbers, and it is all the resolver needs
///     to know which action can serve which condition. The alternative — full symbolic world states
///     with arbitrary predicates — is what makes classic GOAP both slow and impossible to author.
/// </remarks>
public class GoapPearTests {
    [Fact]
    public void AHungryAgentPlansToPickUpAPearAndThenEatIt() {
        var pears = new Pantry { OnGround = 1, Carried = 0, Hunger = 80 };
        var domain = Orchard(pears);
        var planner = new GoapPlanner(domain);
        var plan = new GoapPlan();

        Assert.Equal(PlanFailure.None, planner.Resolve(GoapHarness.Context(), plan));

        Assert.Equal(Symbol.Intern("not-hungry"), plan.Goal);
        Assert.Equal(["pick-up-pear", "eat-pear"], Names(domain, plan));
    }

    /// <summary>With a pear already in hand there is nothing to pick up, and the plan is one step.</summary>
    [Fact]
    public void APearAlreadyCarriedMakesThePlanOneStep() {
        var pears = new Pantry { OnGround = 0, Carried = 1, Hunger = 80 };
        var planner = new GoapPlanner(Orchard(pears));
        var plan = new GoapPlan();

        Assert.Equal(PlanFailure.None, planner.Resolve(GoapHarness.Context(), plan));
        Assert.Single(plan.Steps.ToArray());
    }

    /// <summary>⚠ A met goal is told apart from a failure, or an agent eats for ever.</summary>
    [Fact]
    public void AnAgentThatIsNotHungryHasNothingToPlan() {
        var pears = new Pantry { OnGround = 1, Carried = 1, Hunger = 10 };
        var planner = new GoapPlanner(Orchard(pears));
        var plan = new GoapPlan();

        Assert.Equal(PlanFailure.AlreadyMet, planner.Resolve(GoapHarness.Context(), plan));
        Assert.Equal(0, plan.Count);
    }

    /// <summary>No pears anywhere: nothing this agent can do leads to the goal.</summary>
    [Fact]
    public void AnEmptyOrchardIsUnreachableRatherThanEndless() {
        var pears = new Pantry { OnGround = 0, Carried = 0, Hunger = 80 };
        var planner = new GoapPlanner(Orchard(pears));
        var plan = new GoapPlan();

        Assert.Equal(PlanFailure.Unreachable, planner.Resolve(GoapHarness.Context(), plan));
    }

    /// <summary>⚠ A capability mask is per agent, so one domain serves an agent that cannot bend down.</summary>
    [Fact]
    public void AnAgentWithoutTheCapabilityCannotUseTheAction() {
        var pears = new Pantry { OnGround = 1, Carried = 0, Hunger = 80 };
        var domain = Orchard(pears);
        var planner = new GoapPlanner(domain);
        var plan = new GoapPlan();
        var without = GoapCapabilities.All.Without(0);

        Assert.Equal(
            PlanFailure.Unreachable,
            planner.Resolve(GoapHarness.Context(), plan, capabilities: without)
        );
    }

    /// <summary>The orchard: two actions, one goal, three world keys read out of a mutable pantry.</summary>
    internal static GoapDomain Orchard(Pantry pantry) {
        var keys = new GoapWorldKeys(
            new(Symbol.Intern("pears-on-ground"), GoapWorldSources.From((in AgentContext _) => pantry.OnGround)),
            new(Symbol.Intern("pears-carried"), GoapWorldSources.From((in AgentContext _) => pantry.Carried)),
            new(Symbol.Intern("hunger"), GoapWorldSources.From((in AgentContext _) => pantry.Hunger))
        );

        var ground = new GoapWorldKey(0);
        var carried = new GoapWorldKey(1);
        var hunger = new GoapWorldKey(2);

        var pickUp = new GoapAction(
            Symbol.Intern("pick-up-pear"),
            0,
            [new(ground, GoapComparison.Greater, 0)],
            new GoapEffect(carried, Increases: true)
        );

        var eat = new GoapAction(
            Symbol.Intern("eat-pear"),
            1,
            [new(carried, GoapComparison.Greater, 0)],
            new GoapEffect(hunger, Increases: false)
        );

        var goal = new GoapGoal(Symbol.Intern("not-hungry"), [new(hunger, GoapComparison.Less, 20)]);

        return new(Symbol.Intern("orchard"), keys, [pickUp, eat], [goal]);
    }

    internal static string[] Names(GoapDomain domain, GoapPlan plan) =>
        [.. plan.Steps.ToArray().Select(step => domain[step].Name.ToString())];

    internal sealed class Pantry {
        public int OnGround;
        public int Carried;
        public int Hunger;
    }
}

/// <summary>
///     P6's second exit criterion: an action set authored to blow the node limit fails with
///     <see cref="PlanFailure.BudgetExhausted" /> naming the goal, in bounded time, rather than
///     hanging.
/// </summary>
/// <remarks>
///     ⚠ <b>A GOAP search is exponential in depth and the engine must not hang on a badly authored
///     action set.</b> That is not a hypothetical: a domain where every action's condition can be
///     served by every other action is one an author can produce by accident in an afternoon, and
///     without a bound it is a frozen game with no error in the log.
/// </remarks>
public class GoapBudgetTests {
    const int Actions = 24;

    [Fact]
    public void ADomainThatCannotBeSearchedFailsNamingItsGoalInBoundedTime() {
        var domain = Tangle();
        var planner = new GoapPlanner(domain, new() { NodeBudget = 200, DepthLimit = 16 });
        var plan = new GoapPlan();
        var clock = Stopwatch.StartNew();
        var failure = planner.Resolve(GoapHarness.Context(), plan);

        clock.Stop();

        Assert.Equal(PlanFailure.BudgetExhausted, failure);
        Assert.Equal(Symbol.Intern("impossible"), plan.Goal);
        Assert.Equal(200, plan.Expanded);

        // ⚠ A hang detector, not a performance bound, and the ceiling is deliberately absurd.
        // `plan.Expanded == 200` above is what actually proves the search was bounded: it stopped at
        // the node budget exactly, so the time follows from it on any machine. This only catches the
        // failure the summary names — a search that never returns at all.
        //
        // It used to assert 250 ms, which is a performance claim wearing a hang check's clothes, and
        // it failed on a loaded `ubuntu-latest` while every deterministic assertion above passed. A
        // shared CI runner can lose a quarter-second to scheduling alone, so a bound that tight
        // reports the runner's mood rather than the planner's.
        Assert.True(
            clock.Elapsed.TotalSeconds < 30,
            $"the search took {clock.Elapsed.TotalMilliseconds:0.000} ms for a {Actions}-action tangle, "
            + "which is long enough that it is not returning rather than merely being slow."
        );
    }

    /// <summary>And the depth limit, which is the other half of the bound.</summary>
    [Fact]
    public void AChainLongerThanTheDepthLimitIsReportedAsSuch() {
        var domain = Ladder(12);
        var planner = new GoapPlanner(domain, new() { NodeBudget = 4096, DepthLimit = 4 });
        var plan = new GoapPlan();

        Assert.Equal(PlanFailure.DepthExceeded, planner.Resolve(GoapHarness.Context(), plan));

        // The same ladder with room to climb it resolves, so the limit is what refused it.
        var deeper = new GoapPlanner(domain, new() { NodeBudget = 4096, DepthLimit = 16 });

        Assert.Equal(PlanFailure.None, deeper.Resolve(GoapHarness.Context(), plan));
        Assert.Equal(12, plan.Count);
    }

    /// <summary>
    ///     ⚠ Without the no-repeats rule, two actions that serve each other are an infinite descent —
    ///     and the budget would report exhaustion for a domain with a good two-step plan in it.
    /// </summary>
    [Fact]
    public void TwoActionsThatServeEachOtherStillResolve() {
        var flag = new GoapWorldKey(0);
        var other = new GoapWorldKey(1);
        var keys = new GoapWorldKeys(
            new(Symbol.Intern("a"), GoapWorldSources.Constant(0)),
            new(Symbol.Intern("b"), GoapWorldSources.Constant(1))
        );

        var up = new GoapAction(Symbol.Intern("up"), 0, [new(other, GoapComparison.Greater, 0)], new GoapEffect(flag, true));
        var down = new GoapAction(Symbol.Intern("down"), 1, [new(flag, GoapComparison.Greater, 0)], new GoapEffect(other, true));
        var goal = new GoapGoal(Symbol.Intern("want-a"), [new(flag, GoapComparison.Greater, 0)]);
        var planner = new GoapPlanner(new(Symbol.Intern("loop"), keys, [up, down], [goal]));
        var plan = new GoapPlan();

        Assert.Equal(PlanFailure.None, planner.Resolve(GoapHarness.Context(), plan));
        Assert.Equal(["up"], GoapPearTests.Names(planner.Domain, plan));
    }

    /// <summary>Every action's condition can be served by every other action, and none can run.</summary>
    static GoapDomain Tangle() {
        var definitions = new GoapKeyDefinition[Actions];
        var actions = new GoapAction[Actions];

        for (var index = 0; index < Actions; index++) {
            definitions[index] = new(Symbol.Intern($"k{index}"), GoapWorldSources.Constant(0));
        }

        for (var index = 0; index < Actions; index++) {
            var conditions = new GoapCondition[Actions];

            for (var other = 0; other < Actions; other++) {
                conditions[other] = new(new GoapWorldKey((ushort)other), GoapComparison.Greater, 0);
            }

            actions[index] = new(
                Symbol.Intern($"a{index}"),
                (ushort)index,
                conditions,
                new GoapEffect(new GoapWorldKey((ushort)index), Increases: true)
            );
        }

        var goal = new GoapGoal(Symbol.Intern("impossible"), [new(new GoapWorldKey(0), GoapComparison.Greater, 0)]);

        return new(Symbol.Intern("tangle"), new(definitions), actions, [goal]);
    }

    /// <summary>A chain of length <paramref name="steps" />, each rung served only by the one below it.</summary>
    static GoapDomain Ladder(int steps) {
        var definitions = new GoapKeyDefinition[steps + 1];
        var actions = new GoapAction[steps];

        for (var index = 0; index <= steps; index++) {
            var value = index == steps ? 1 : 0;

            definitions[index] = new(Symbol.Intern($"rung{index}"), GoapWorldSources.Constant(value));
        }

        for (var index = 0; index < steps; index++) {
            actions[index] = new(
                Symbol.Intern($"climb{index}"),
                (ushort)index,
                [new(new GoapWorldKey((ushort)(index + 1)), GoapComparison.Greater, 0)],
                new GoapEffect(new GoapWorldKey((ushort)index), Increases: true)
            );
        }

        var goal = new GoapGoal(Symbol.Intern("top"), [new(new GoapWorldKey(0), GoapComparison.Greater, 0)]);

        return new(Symbol.Intern("ladder"), new(definitions), actions, [goal]);
    }
}

/// <summary>
///     P6's third exit criterion: 64 agents replanning on a 40-action set inside a stated frame
///     budget.
/// </summary>
/// <remarks>
///     ⚠ <b>The budget is recorded and the <i>work</i> is asserted</b>, for the reason P3's cost test
///     gives: this repository builds Debug locally and Release in CI, so a millisecond threshold is
///     not one number. What is asserted is the claim doc 37 § D16 makes — <b>a resolve does not run on
///     the frame that asked for it</b>, and the frame's planning cost is the resolves-per-step times
///     the node budget, whatever the population is doing.
/// </remarks>
public class GoapThroughputTests {
    const int Agents = 64;
    const int Actions = 40;

    /// <summary>The figure doc 37 § P6 records for this frame, in milliseconds.</summary>
    const double Budget = 2.0;

    [Fact]
    public void SixtyFourAgentsReplanningOnAFortyActionSetStayInsideTheBudget() {
        var domain = Wide();
        var queue = new GoapPlanQueue(domain, new() { NodeBudget = 256, DepthLimit = 8 }, capacity: 128);
        var context = GoapHarness.Context();
        var plan = new GoapPlan();
        var tickets = new GoapPlanRequest[Agents];

        for (var index = 0; index < Agents; index++) {
            tickets[index] = queue.Submit(in context);

            Assert.False(tickets[index].IsNull, $"the queue refused agent {index}.");
        }

        // ⚠ Sixty-four agents asked at once and the frame runs four of them. That is the whole point:
        // an agent that changed its mind cannot spend the frame, and the rest are answered on the
        // frames after it.
        var clock = Stopwatch.StartNew();

        queue.Update(4);
        clock.Stop();

        Assert.Equal(4, queue.LastResolves);
        Assert.True(queue.LastExpanded <= 4 * 256, $"four resolves expanded {queue.LastExpanded} nodes.");

        var report = $"four resolves of a {Actions}-action domain in {clock.Elapsed.TotalMilliseconds:0.000} ms; "
            + $"{queue.LastExpanded} nodes; recorded budget {Budget:0.000} ms.";

        Assert.True(queue.TryTakeResult(tickets[0], plan), report);
        Assert.Equal(PlanFailure.None, plan.Failure);

        // And the rest are still waiting rather than lost.
        Assert.Equal(GoapRequestState.Waiting, queue.GetState(tickets[60]));

        var frames = 1;

        while (queue.GetState(tickets[Agents - 1]) == GoapRequestState.Waiting && frames < 64) {
            queue.Update(4);
            frames++;
        }

        Assert.Equal(GoapRequestState.Ready, queue.GetState(tickets[Agents - 1]));
        Assert.Equal(16, frames);
    }

    /// <summary>A queue that has run out refuses rather than growing.</summary>
    [Fact]
    public void AFullQueueRefusesAndCancellingGivesTheSlotBack() {
        var queue = new GoapPlanQueue(Wide(), capacity: 2);
        var context = GoapHarness.Context();
        var first = queue.Submit(in context);

        queue.Submit(in context);

        Assert.True(queue.Submit(in context).IsNull);
        Assert.True(queue.Cancel(first));
        Assert.False(queue.Submit(in context).IsNull);
    }

    /// <summary>Forty actions over twenty keys, fourteen of which are already true.</summary>
    /// <remarks>
    ///     ⚠ Tuned so the shortest plan is six steps and the depth limit is eight. A domain the search
    ///     cannot finish would measure the <i>budget</i> rather than the throughput, which is what the
    ///     other exit criterion is for — and this one is supposed to be sixty-four agents getting
    ///     answers.
    /// </remarks>
    static GoapDomain Wide() {
        var definitions = new GoapKeyDefinition[20];
        var actions = new GoapAction[Actions];

        for (var index = 0; index < definitions.Length; index++) {
            var value = index >= 6 ? 1 : 0;

            definitions[index] = new(Symbol.Intern($"k{index}"), GoapWorldSources.Constant(value));
        }

        for (var index = 0; index < Actions; index++) {
            // Each action needs one key and provides another, in a ring — which is a graph with real
            // branching rather than a chain the search walks straight down.
            actions[index] = new(
                Symbol.Intern($"a{index}"),
                (ushort)index,
                [new(new GoapWorldKey((ushort)((index + 1) % definitions.Length)), GoapComparison.Greater, 0)],
                new GoapEffect(new GoapWorldKey((ushort)(index % definitions.Length)), Increases: true)
            ) {
                BaseCost = 1f + (index % 3)
            };
        }

        var goal = new GoapGoal(Symbol.Intern("want-zero"), [new(new GoapWorldKey(0), GoapComparison.Greater, 0)]);

        return new(Symbol.Intern("wide"), new(definitions), actions, [goal]);
    }
}

/// <summary>An agent context with nothing in it, for a domain whose sources read no world.</summary>
static class GoapHarness {
    public static AgentContext Context(Blackboard? blackboard = null) {
        var entity = new Entity(11, 1, 0);

        return new(
            new World("goap-test"),
            entity,
            blackboard ?? new Blackboard(BlackboardLayout.Empty),
            null,
            GameTime.Zero,
            AgentRandom.SeedOf(entity)
        );
    }
}
