---
title: GOAP
slug: ai/goap
kind: guide
area: AI
summary: World keys, conditions and effects, the bounded backwards search, and why only the head of a plan is ever committed.
api: [T:Vixen.Ai.GoapWorldKey, T:Vixen.Ai.GoapComparison, T:Vixen.Ai.GoapCondition, T:Vixen.Ai.GoapEffect, T:Vixen.Ai.IGoapWorldSource, T:Vixen.Ai.GoapReading, T:Vixen.Ai.GoapWorldSources, T:Vixen.Ai.BlackboardWorldSource, T:Vixen.Ai.GoapKeyDefinition, T:Vixen.Ai.GoapWorldKeys, T:Vixen.Ai.GoapMoveMode, T:Vixen.Ai.IGoapTargetSensor, T:Vixen.Ai.GoapTargetSensors, T:Vixen.Ai.GoapTargetLookup, T:Vixen.Ai.GoapAction, T:Vixen.Ai.GoapGoal, T:Vixen.Ai.GoapDomain, T:Vixen.Ai.GoapDomainLibrary, T:Vixen.Ai.PlanFailure, T:Vixen.Ai.GoapSettings, T:Vixen.Ai.GoapTarget, T:Vixen.Ai.IActionCostModel, T:Vixen.Ai.ActionCostModels, T:Vixen.Ai.GoapCapabilities, T:Vixen.Ai.GoapPlan, T:Vixen.Ai.GoapPlanner, T:Vixen.Ai.GoapSnapshot, T:Vixen.Ai.GoapPlanRequest, T:Vixen.Ai.GoapRequestState, T:Vixen.Ai.GoapPlanQueue, T:Vixen.Ai.ReplanContext, T:Vixen.Ai.IReplanPolicy, T:Vixen.Ai.ReplanPolicies, T:Vixen.Ai.GoapMemory, T:Vixen.Ai.GoapSourceKind, T:Vixen.Ai.GoapKeyContent, T:Vixen.Ai.GoapConditionContent, T:Vixen.Ai.GoapEffectContent, T:Vixen.Ai.GoapActionContent, T:Vixen.Ai.GoapGoalContent, T:Vixen.Ai.GoapDomainContent, T:Vixen.Ai.GoapDomainContentCompiler, T:Vixen.Ai.Nodes.NavigationCostModel, T:Vixen.Ai.Nodes.GoapWiring, T:Vixen.Editor.Ai.GoapGraphProjection, T:Vixen.Editor.AssetEditors.Ai.GoapDomainDocument, T:Vixen.Editor.AssetEditors.Ai.GoapDomainView, T:Vixen.Editor.AssetEditors.Ai.GoapDomainEditorFactory, T:Vixen.Editor.Assets.Ai.GoapDomainImporter, T:Vixen.Editor.Assets.Ai.GoapDomainImportSettings]
tags: [ai, goap, planning, search]
since: 0.1
status: stable
related: [ai/behaviour-trees, ai/utility, ai/blackboard, ai/debugger, ai/sensors]
---

## What it is

**GOAP** is the third of the three planners: an agent is given *goals* rather than behaviour, and a
search works out a sequence of actions that would satisfy one. A goal is a set of conditions over
**world keys**; an action declares conditions it needs and **effects** it has, and the resolver chains
backwards from the goal.

It produces the same thing the other two do — an `IAgentAction` index — so a project writes
`MoveToTask` once and gets it in a tree, in a utility set and in a plan.

## What it is for

The characters whose interest is that you cannot predict them. A shopkeeper who fetches a ladder
because the thing you asked for is on a high shelf; a survivor who lights a fire because it is cold
and there is wood; anything where the *combination* is the content and enumerating the combinations
by hand is the thing you are trying not to do.

You do *not* want it for a guard patrol. A procedure is a behaviour tree, a judgement is a utility
set, and a plan is neither — reaching for a search when the answer is three nodes of a selector buys
a node budget, a re-plan policy and a queue for nothing.

## Using it

Three tables: world keys, actions and goals.

```csharp compile
using Vixen.Ai;
using Vixen.Core;

public static class Orchard {
    public static GoapDomain Build(ushort pickUp, ushort eat) {
        var keys = new GoapWorldKeys(
            new(Symbol.Intern("pears-on-ground"), GoapWorldSources.Constant(1)),
            new(Symbol.Intern("pears-carried"), GoapWorldSources.Constant(0)),
            new(Symbol.Intern("hunger"), GoapWorldSources.Constant(80))
        );

        var ground = new GoapWorldKey(0);
        var carried = new GoapWorldKey(1);
        var hunger = new GoapWorldKey(2);

        return new(
            Symbol.Intern("orchard"),
            keys,
            [
                new GoapAction(
                    Symbol.Intern("pick-up-pear"),
                    pickUp,
                    [new(ground, GoapComparison.Greater, 0)],
                    new GoapEffect(carried, Increases: true)
                ),
                new GoapAction(
                    Symbol.Intern("eat-pear"),
                    eat,
                    [new(carried, GoapComparison.Greater, 0)],
                    new GoapEffect(hunger, Increases: false)
                )
            ],
            [new GoapGoal(Symbol.Intern("not-hungry"), [new(hunger, GoapComparison.Less, 20)])]
        );
    }
}
```

An agent runs it by naming it: `AiAgent.Planning(index)`, where the index is what
`AiSystem.Domains.Add` returned.

### The matching rule is a direction

A condition wanting a key **greater** is served by an action with a **positive** effect on that key;
one wanting it **smaller** by a negative one. That is the whole of it.

⚠ **An effect is a direction and not an amount, and that is what makes GOAP authorable.** "Eating
reduces hunger by 40" makes every plan a simulation of arithmetic nobody can predict and makes the
graph depend on numbers a designer tunes. "Eating reduces hunger" stays true while the numbers move.

⚠ **There is no equality comparison, and that is not an omission.** An equality has no direction, so
nothing could ever be said to serve it — a condition the resolver could match only by accident and a
graph edge it could never build.

### The graph is built once

Which action's effect can serve which action's condition is a fact about the **action set**, so it is
computed when the domain is constructed and never inside a search. What is per agent is the condition
evaluations and the costs.

### The search is bounded, and the bound is reported

⚠ **A GOAP search is exponential in depth and the engine must not hang on a badly authored action
set.** `GoapSettings` carries a node budget and a depth limit; exceeding either produces
`PlanFailure.BudgetExhausted` or `PlanFailure.DepthExceeded` **naming the goal**.

| Failure | What it means |
|---|---|
| `AlreadyMet` | the goal is true. Not a failure, and worth telling apart from one |
| `Unreachable` | nothing this agent can do leads there |
| `BudgetExhausted` | the search ran out of nodes |
| `DepthExceeded` | every chain hit the depth limit |

⚠ **A plan is a chain, so an action with two unmet conditions is served one at a time — and that is
correct rather than a simplification.** Only the head is committed: the head is by construction
runnable now, running it changes the world, and the next resolve plans from what the world then is.

### Only the head is committed

An agent that *follows* a sequence walks into a door that closed after the plan was made; one that
re-plans every frame is a search per agent per frame. So the tail is **advisory** — it is kept, it is
what the viewer draws, and the head's conditions are re-checked against the live world before it
starts.

`IReplanPolicy` decides when to think again: `Reactive` (the step ended or there is no plan),
`Proactive(interval)` (that, and on an interval, so a better plan can be found), `Manual` (the game
says when — and still re-plans with nothing to do, or an agent stands there for ever).

### Resolves do not run on the frame that asked for them

`GoapPlanQueue` is `NavPathQueue`'s arrangement for its reason. ⚠ **The world is read at `Submit`, on
the thread that owns the agent** — what reaches the search is a `GoapSnapshot`, a few arrays of
numbers, so a resolve may run on a worker thread without touching a `World` or a `Blackboard` from it.

⚠ **The frame's planning cost is `ResolvesPerStep × NodeBudget`.** Neither number bounds anything on
its own, and a project raising one should know it is raising the product.

### An action happens somewhere

An action declares a target **key**, resolved by a sensor to a position or an entity, plus a stopping
distance and a `GoapMoveMode`. ⚠ **Movement is not modelled as actions in the graph** — a `MoveTo(x)`
per destination makes the graph a function of the world's contents. The planner produces a target and
the existing movement stack gets there.

⚠ **The distance cost is a straight line by default, not a path length.** A path query per candidate
action per resolve is a nav search per edge of the search graph. `NavigationCostModel` in
`Vixen.Ai.Nodes` is the one that asks the mesh, and it is a navigation query per action per resolve —
affordable at a few dozen agents and not affordable for a crowd.

### Capabilities are per agent

`GoapCapabilities` is a mask over the domain's actions, carried on the `AiAgent`. ⚠ **A domain per
capability set would be a graph rebuild per permutation** — a wounded guard and a healthy one share
one graph and plan differently.

## Examples

A domain as a file. The tables are authored and the graph is not:

```yaml
version: 1
name: Orchard
blackboard:
  - { name: carried, type: Int }
keys:
  - { name: pears-on-ground, source: Registered, from: pears-nearby }
  - { name: pears-carried, source: Blackboard, from: carried }
  - { name: hunger, source: Registered, from: hunger }
actions:
  - name: PickUpPear
    task: MoveTo
    fields: { Key: pear }
    target: nearest-pear
    conditions: [{ key: pears-on-ground, comparison: Greater, value: 0 }]
    effects: [{ key: pears-carried, increases: true }]
  - name: EatPear
    task: Wait
    fields: { Seconds: "2" }
    conditions: [{ key: pears-carried, comparison: Greater, value: 0 }]
    effects: [{ key: hunger, increases: false }]
goals:
  - name: NotHungry
    priority: 1
    conditions: [{ key: hunger, comparison: Less, value: 20 }]
```

Wiring it up — the world sources and the target sensors are the game's:

```csharp no-compile="a fragment; Hunger and the pear query are the game's own"
resolver.AddWorldSource("hunger", GoapWorldSources.From((in AgentContext c) => c.World.Get<Hunger>(c.Entity).Value));
resolver.AddWorldSource("pears-nearby", GoapWorldSources.From((in AgentContext c) => orchard.CountNear(c.Entity)));

var sensors = GoapWiring.Sensors();

sensors.Add(Symbol.Intern("nearest-pear"), GoapWiring.FromKey(pearKey));

GoapDomainContentCompiler.TryCompile(content, resolver, out var diagnostics, out var domain);
```

Asking for a plan directly, which is what a test or a tool does:

```csharp no-compile="a fragment; the context comes from a running agent"
var planner = new GoapPlanner(domain, new() { NodeBudget = 256, DepthLimit = 6 });
var plan = new GoapPlan();

if (planner.Resolve(in context, plan) == PlanFailure.None) {
    // plan.Head is runnable now; plan.Steps is what the viewer draws.
}
```

## See also

- [Behaviour trees](behaviour-trees.md) — the planner for a procedure, and where a plan is not one.
- [Utility](utility.md) — the planner for a judgement, and the one whose actions these share.
- [The blackboard](blackboard.md) — where a world key usually projects from.
