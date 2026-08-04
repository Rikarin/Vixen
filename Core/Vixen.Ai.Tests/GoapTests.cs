// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Ai.Diagnostics;
using Vixen.Ai.Ecs;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Xunit;

namespace Vixen.Ai.Tests;

public class GoapDomainTests {
    /// <summary>
    ///     ⚠ The graph is built once, here, and not per resolve. Which action's effect can serve which
    ///     action's condition is a fact about the action set.
    /// </summary>
    [Fact]
    public void TheGraphIsBuiltFromTheDirectionOfAnEffect() {
        var wood = new GoapWorldKey(0);
        var fire = new GoapWorldKey(1);
        var keys = new GoapWorldKeys(
            new(Symbol.Intern("wood"), GoapWorldSources.Constant(0)),
            new(Symbol.Intern("fire"), GoapWorldSources.Constant(0))
        );

        var gather = new GoapAction(Symbol.Intern("gather"), 0, [], new GoapEffect(wood, Increases: true));
        var burn = new GoapAction(Symbol.Intern("burn"), 1, [], new GoapEffect(wood, Increases: false));
        var light = new GoapAction(
            Symbol.Intern("light"),
            2,
            [new(wood, GoapComparison.Greater, 0)],
            new GoapEffect(fire, Increases: true)
        );

        var domain = new GoapDomain(Symbol.Intern("camp"), keys, [gather, burn, light], []);

        // Lighting needs *more* wood, so gathering serves it and burning does not — which is the whole
        // matching rule, and it is one bit.
        Assert.Equal([0], domain.Servers(2, 0).ToArray());
        Assert.Equal(1, domain.EdgeCount);
    }

    [Fact]
    public void AConditionIsCheckedAgainstTheProjectedWorld() {
        Span<int> world = [5, 0];

        Assert.True(new GoapCondition(new(0), GoapComparison.Greater, 4).Holds(world));
        Assert.False(new GoapCondition(new(0), GoapComparison.Greater, 5).Holds(world));
        Assert.True(new GoapCondition(new(0), GoapComparison.GreaterOrEqual, 5).Holds(world));
        Assert.True(new GoapCondition(new(1), GoapComparison.LessOrEqual, 0).Holds(world));

        // ⚠ An invalid key never holds, rather than reading as zero. A condition on a key nobody
        // declared is an authoring mistake, and the safe direction is an action that never runs.
        Assert.False(new GoapCondition(GoapWorldKey.Invalid, GoapComparison.Less, 99).Holds(world));
    }

    [Fact]
    public void TheHighestPriorityUnmetGoalIsTheOneChosen() {
        var hunger = new GoapWorldKey(0);
        var danger = new GoapWorldKey(1);
        var keys = new GoapWorldKeys(
            new(Symbol.Intern("hunger"), GoapWorldSources.Constant(90)),
            new(Symbol.Intern("danger"), GoapWorldSources.Constant(0))
        );

        var domain = new GoapDomain(
            Symbol.Intern("villager"),
            keys,
            [],
            [
                new(Symbol.Intern("eat"), [new(hunger, GoapComparison.Less, 20)]),
                new(Symbol.Intern("flee"), [new(danger, GoapComparison.Less, 1)], Priority: 5)
            ]
        );

        var snapshot = new GoapSnapshot(domain);

        snapshot.Take(GoapHarness.Context());

        // ⚠ Fleeing is more important and is already true, so it is not a candidate at all. Planning
        // for the highest-priority goal regardless is what makes an agent keep eating because
        // "not hungry" is its most important goal.
        Assert.Equal(0, snapshot.Wanted());
    }

    [Fact]
    public void AWorldKeyReadsThroughTheBlackboardWhenThatIsWhereItLives() {
        var layout = new BlackboardLayoutBuilder()
            .Add("ammo", BlackboardValueType.Int)
            .Add("hurt", BlackboardValueType.Bool)
            .Add("health", BlackboardValueType.Float)
            .Build();

        var board = new Blackboard(layout);
        var context = GoapHarness.Context(board);

        board.SetInt(new(0), 7);
        board.SetBool(new(1), true);
        board.SetFloat(new(2), 41.9f);

        Assert.Equal(7, new BlackboardWorldSource(new(0)).Read(in context));
        Assert.Equal(1, new BlackboardWorldSource(new(1)).Read(in context));

        // ⚠ Truncated. GOAP reasons about counts and thresholds, and a search that depended on the
        // fractional part of a health value would re-plan on every frame it drifted.
        Assert.Equal(41, new BlackboardWorldSource(new(2)).Read(in context));

        // An unset key reads as zero rather than throwing, which is what makes a half-wired domain
        // openable.
        Assert.Equal(0, new BlackboardWorldSource(BlackboardKey.Invalid).Read(in context));
    }
}

public class GoapCostTests {
    [Fact]
    public void TheStraightLineModelAddsTheDistanceAndTheFlatOneDoesNot() {
        var action = new GoapAction(Symbol.Intern("go"), 0, []) { BaseCost = 2f };
        var target = new GoapTarget(true, new(0f, 0f, 10f), Entity.Null);
        var context = GoapHarness.Context();

        Assert.Equal(3f, ActionCostModels.StraightLine(0.1f).Cost(in context, action, Vector3.Zero, in target), 3);
        Assert.Equal(2f, ActionCostModels.Flat.Cost(in context, action, Vector3.Zero, in target), 3);
    }

    /// <summary>
    ///     ⚠ Floored at one. The heuristic counts unmet conditions, and it is admissible only while an
    ///     action costs at least as much as it claims to remove.
    /// </summary>
    [Fact]
    public void ACostIsNeverBelowOne() {
        var free = new GoapAction(Symbol.Intern("free"), 0, []) { BaseCost = 0f };
        var context = GoapHarness.Context();

        Assert.Equal(1f, ActionCostModels.Flat.Cost(in context, free, Vector3.Zero, GoapTarget.None), 3);
        Assert.Equal(1f, ActionCostModels.StraightLine().Cost(in context, free, Vector3.Zero, GoapTarget.None), 3);
    }

    /// <summary>A cheaper chain wins, which is what makes this A* rather than a breadth-first walk.</summary>
    [Fact]
    public void TheCheaperOfTwoRoutesIsTheOnePlanned() {
        var goalKey = new GoapWorldKey(0);
        var keys = new GoapWorldKeys(new GoapKeyDefinition(Symbol.Intern("done"), GoapWorldSources.Constant(0)));
        var cheap = new GoapAction(Symbol.Intern("cheap"), 0, [], new GoapEffect(goalKey, true)) { BaseCost = 1f };
        var dear = new GoapAction(Symbol.Intern("dear"), 1, [], new GoapEffect(goalKey, true)) { BaseCost = 9f };
        var goal = new GoapGoal(Symbol.Intern("want"), [new(goalKey, GoapComparison.Greater, 0)]);
        var planner = new GoapPlanner(new(Symbol.Intern("two-ways"), keys, [dear, cheap], [goal]));
        var plan = new GoapPlan();

        Assert.Equal(PlanFailure.None, planner.Resolve(GoapHarness.Context(), plan, costs: ActionCostModels.Flat));
        Assert.Equal(["cheap"], GoapPearTests.Names(planner.Domain, plan));
    }
}

public class ReplanPolicyTests {
    [Fact]
    public void ReactiveThinksAgainWhenThereIsNothingToDoOrTheStepEnded() {
        var policy = ReplanPolicies.Reactive;

        Assert.True(policy.ShouldReplan(new(HasPlan: false, false, false, 0f, 0, false)));
        Assert.True(policy.ShouldReplan(new(HasPlan: true, Finished: true, false, 0f, 2, false)));
        Assert.True(policy.ShouldReplan(new(HasPlan: true, false, Failed: true, 0f, 2, false)));
        Assert.False(policy.ShouldReplan(new(HasPlan: true, false, false, 99f, 2, false)));
    }

    [Fact]
    public void ProactiveAlsoThinksAgainOnItsInterval() {
        var policy = ReplanPolicies.Proactive(2f);

        Assert.False(policy.ShouldReplan(new(HasPlan: true, false, false, 1.9f, 2, false)));
        Assert.True(policy.ShouldReplan(new(HasPlan: true, false, false, 2.1f, 2, false)));
    }

    /// <summary>⚠ Manual still re-plans with no plan at all, or an agent stands there for ever.</summary>
    [Fact]
    public void ManualWaitsToBeAskedButNotWhenItHasNothingToDo() {
        var policy = ReplanPolicies.Manual;

        Assert.False(policy.ShouldReplan(new(HasPlan: true, Finished: true, false, 99f, 2, false)));
        Assert.True(policy.ShouldReplan(new(HasPlan: true, false, false, 0f, 2, Asked: true)));
        Assert.True(policy.ShouldReplan(new(HasPlan: false, false, false, 0f, 0, false)));
    }
}

public class GoapAgentTests {
    /// <summary>A planning agent through the whole system: it runs the head, and then the next step.</summary>
    [Fact]
    public void TheSystemRunsThePlansHeadAndThenTheStepAfterIt() {
        var pears = new GoapPearTests.Pantry { OnGround = 1, Carried = 0, Hunger = 80 };
        var registry = new AgentActionRegistry();
        var pickUp = new Finishing(after: 2, () => { pears.OnGround--; pears.Carried++; });
        var eat = new Finishing(after: 2, () => { pears.Carried--; pears.Hunger = 5; });

        registry.Register("pick-up", pickUp, Finishing.Size);
        registry.Register("eat", eat, Finishing.Size);

        var system = new AiSystem(registry, BlackboardLayout.Empty) {
            Goap = new() { NodeBudget = 128, DepthLimit = 8 }
        };

        var domain = system.Domains.Add(GoapPearTests.Orchard(pears));
        using var world = new World("goap-agent");
        var entity = world.Create(AiAgent.Planning(domain));

        for (var frame = 0; frame < 40; frame++) {
            system.Step(world, Frame(frame));
        }

        Assert.Equal(1, pickUp.Runs);
        Assert.Equal(1, eat.Runs);
        Assert.Equal(5, pears.Hunger);

        var memory = system.PlanningOf(world.Get<AiAgent>(entity))!;

        Assert.True(memory.Plans >= 2, $"the agent made {memory.Plans} plans.");
    }

    /// <summary>
    ///     ⚠ Only the head is committed, and it is re-checked against the live world. A plan is a
    ///     picture of a world that has moved on, and a head that is no longer runnable is thrown away
    ///     rather than walked into.
    /// </summary>
    [Fact]
    public void AHeadThatIsNoLongerRunnableIsThrownAwayRatherThanStarted() {
        var pears = new GoapPearTests.Pantry { OnGround = 1, Carried = 0, Hunger = 80 };
        var registry = new AgentActionRegistry();
        var pickUp = new Finishing(after: 100, () => { });
        var eat = new Finishing(after: 1, () => pears.Hunger = 5);

        registry.Register("pick-up", pickUp, Finishing.Size);
        registry.Register("eat", eat, Finishing.Size);

        var system = new AiSystem(registry, BlackboardLayout.Empty);
        var domain = system.Domains.Add(GoapPearTests.Orchard(pears));
        using var world = new World("goap-stale");

        world.Create(AiAgent.Planning(domain));

        for (var frame = 0; frame < 6; frame++) {
            system.Step(world, Frame(frame));
        }

        Assert.Equal(1, pickUp.Starts);

        // Somebody else took the pear. The head cannot run, so the plan goes rather than the agent
        // standing there picking up nothing.
        pears.OnGround = 0;

        for (var frame = 6; frame < 20; frame++) {
            system.Step(world, Frame(frame));
        }

        Assert.Equal(1, pickUp.Runs + pickUp.Aborts);
        Assert.Equal(0, eat.Starts);
    }

    [Fact]
    public void TheDebugRecordSaysWhichPlannerDecided() {
        var pears = new GoapPearTests.Pantry { OnGround = 1, Carried = 0, Hunger = 80 };
        var registry = new AgentActionRegistry();

        registry.Register("pick-up", new Finishing(100, () => { }), Finishing.Size);
        registry.Register("eat", new Finishing(100, () => { }), Finishing.Size);

        var system = new AiSystem(registry, BlackboardLayout.Empty);

        system.Debug.Enabled = true;

        var domain = system.Domains.Add(GoapPearTests.Orchard(pears));
        using var world = new World("goap-debug");

        var entity = world.Create(AiAgent.Planning(domain));

        for (var frame = 0; frame < 8; frame++) {
            system.Step(world, Frame(frame));
        }

        Assert.True(system.Debug.TryGetLatest(entity, out var record));
        Assert.Equal(AiPlanner.Goap, record.Planner);
    }

    static GameTime Frame(int index) =>
        new(TimeSpan.FromSeconds(index * 0.1), TimeSpan.FromSeconds(0.1), TimeSpan.FromSeconds(0.1), index, 1f);

    /// <summary>An action that succeeds after a few ticks and changes the world when it does.</summary>
    sealed class Finishing(int after, Action effect) : IAgentAction {
        public int Starts;
        public int Runs;
        public int Aborts;

        public static int Size => Marshal.SizeOf<int>();

        public void Start(in AgentContext context, Span<byte> state) => Starts++;

        public ActionStatus Tick(in AgentContext context, Span<byte> state, float delta) {
            ref var ticks = ref MemoryMarshal.AsRef<int>(state);

            if (++ticks < after) {
                return ActionStatus.Running;
            }

            Runs++;
            effect();

            return ActionStatus.Succeeded;
        }

        public void Abort(in AgentContext context, Span<byte> state) => Aborts++;
    }
}
