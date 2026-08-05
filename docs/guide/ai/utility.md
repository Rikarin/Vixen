---
title: Utility
slug: ai/utility
kind: guide
area: AI
summary: Considerations, the six response curves, the weighted geometric mean with its zero rule, the four selectors, and the inertia that stops an agent flapping.
api: [T:Vixen.Ai.ResponseCurveKind, T:Vixen.Ai.IResponseCurve, T:Vixen.Ai.ResponseCurve, T:Vixen.Ai.DelegateResponseCurve, T:Vixen.Ai.IUtilityInput, T:Vixen.Ai.UtilityReading, T:Vixen.Ai.UtilityInputs, T:Vixen.Ai.BlackboardUtilityInput, T:Vixen.Ai.DistanceUtilityInput, T:Vixen.Ai.UtilityConsideration, T:Vixen.Ai.UtilityScoring, T:Vixen.Ai.UtilityAction, T:Vixen.Ai.UtilityState, T:Vixen.Ai.UtilityMemory, T:Vixen.Ai.UtilitySet, T:Vixen.Ai.UtilitySetLibrary, T:Vixen.Ai.IUtilitySelector, T:Vixen.Ai.UtilitySelectors, T:Vixen.Ai.RunUtilitySetTask, T:Vixen.Ai.UtilityInputKind, T:Vixen.Ai.UtilitySelectorKind, T:Vixen.Ai.UtilitySetContent, T:Vixen.Ai.UtilityActionContent, T:Vixen.Ai.UtilityConsiderationContent, T:Vixen.Ai.UtilityCurveKeyContent, T:Vixen.Ai.UtilitySetContentCompiler, T:Vixen.Editor.Ai.UtilitySetModel, T:Vixen.Editor.AssetEditors.Ai.UtilitySetDocument, T:Vixen.Editor.AssetEditors.Ai.UtilitySetView, T:Vixen.Editor.AssetEditors.Ai.UtilitySetEditorFactory, T:Vixen.Editor.Assets.Ai.UtilitySetImporter, T:Vixen.Editor.Assets.Ai.UtilitySetImportSettings, T:Vixen.Ai.DistanceUtilityInput.PositionLookup]
tags: [ai, utility, considerations, curves]
since: 0.1
status: stable
related: [ai/behaviour-trees, ai/blackboard, ai/perception, ai/goap, ai/debugger, ai/environment-queries]
---

## What it is

A **utility set** is a list of things an agent might do, each scored out of the world, with the best
one chosen. A score is built from **considerations** — one normalised input, one curve — and the
considerations are combined by a weighted geometric mean in which any zero is a veto.

It is the second of doc 37's three planners, and it produces the same thing the other two do: an
`IAgentAction` index. A project writes `MoveToTask` once and gets it in a tree, in a set and in a
[GOAP plan](goap.md).

## What it is for

The judgement half of a character. A behaviour tree is good at a *procedure* — patrol, then
investigate, then return — and bad at "of these fifteen things, which is best right now"; a utility
set is the other way round. A Sim deciding what to do next, a squad member picking a target, a
shopkeeper choosing which customer to serve: none of those is a tree of conditions and all of them
are a table of scores.

You do *not* want it for a sequence. "Open the door, walk through, close it" scored fifteen times a
second is a set that has to be told not to change its mind, which is a tree wearing a costume.

## Using it

Three pieces: an input, a curve, and an action to hang them on.

```csharp compile
using Vixen.Ai;
using Vixen.Core;

public static class Villagers {
    public static UtilitySet Build(BlackboardKey hunger, BlackboardKey danger, ushort eat, ushort flee) =>
        new(
            Symbol.Intern("villager"),
            new UtilityAction(
                Symbol.Intern("eat"),
                eat,
                new UtilityConsideration(
                    Symbol.Intern("hungry"),
                    new BlackboardUtilityInput(hunger, 0f, 100f),
                    ResponseCurve.Threshold(0.5f)
                )
            ),
            new UtilityAction(
                Symbol.Intern("flee"),
                flee,
                new UtilityConsideration(
                    Symbol.Intern("afraid"),
                    new BlackboardUtilityInput(danger),
                    ResponseCurve.Identity
                )
            ) {
                Weight = 5f,
                Bucket = 5
            }
        ) {
            Selector = UtilitySelectors.Bucketed
        };
}
```

An agent runs it by naming it: `AiAgent.Scoring(index)`, where the index is what
`AiSystem.Sets.Add` returned. Everything else — the join, the governor, the per-agent memory, the
debug record — is the same code a behaviour-tree agent goes through.

### The six curves

| Curve | Form | For |
|---|---|---|
| `Linear` | `m(x − c) + b` | "more is better", proportionally |
| `Polynomial` | `m(x − c)^k + b` | `k > 1` rises late, `k < 1` rises early |
| `Logistic` | `m / (1 + e^(−k(x − c))) + b` | a threshold: "urgent below half health" |
| `Logit` | the inverse | diminishing returns |
| `Gaussian` | `m·e^(−(x − c)² / 2k²) + b` | a sweet spot: "ten metres is the right range" |
| `Sampled` | authored keys | when no formula is the shape you want |

⚠ **`Sampled` is not a grudging seventh option.** *The Sims* uses a piecewise curve for hunger because
no formula gives "ignore it entirely, then suddenly care", and the engine already has the machinery:
`CurveEvaluation` samples it and the editor's curve control draws it.

⚠ **Normalisation is the input's job, not the curve's.** A curve whose domain were "0 to whatever this
game's maximum health is" could not be drawn, could not be shared, and would have to be re-authored
the day somebody changed the maximum.

### Scores combine as a geometric mean, and any zero is a veto

```
score(action) = weight × ( Π consideration ) ^ (1/n)
```

⚠ **The naive product is what everybody writes first and it is wrong in a way that is hard to see.**
With every term in `[0,1]`, an action with six considerations is *structurally* worse than an
identical action with three — so adding a consideration to tune an action quietly demotes it, and the
demotion is invisible because every individual number still looks right. The `n`th root makes the
count irrelevant.

⚠ **The zero rule survives the mean, and that is the point of using a product at all.** One zero
factor makes the whole thing zero, which is how "never, under any circumstances" is said. A weighted
*sum* cannot say it: a veto is outvoted by enough enthusiasm elsewhere, which is how an agent ends up
drinking coffee while on fire.

`Weight` is the bucket — 1 for ambient, 2–3 for important, 5 for emergency — and it is a multiplier
rather than a hard ordering, because a hard ordering means an emergency action with one zero-scoring
consideration blocks everything below it.

### Picking is a policy

| Selector | What it does |
|---|---|
| `Highest` | the best one. Deterministic, and right for anything a designer must predict |
| `WeightedRandom` | score as weight. Natural-looking and occasionally stupid |
| `TopWeightedRandom(n)` | weighted random among the best few — the one most games want |
| `Bucketed` | dual utility: the highest bucket with anything in it, then the best inside it |

⚠ **`Bucketed` is what stops a guard being shot at from scoring "drink coffee".** With one flat list a
very good ambient action beats a merely adequate emergency one, and the weights that would prevent
that have to outrank *any* combination below them — a hard ordering by the back door.

### Inertia is not optional

An agent re-scoring every frame with two actions at 0.51 and 0.49 oscillates, and **oscillation is the
single most visible failure mode of a utility agent**. Three mechanisms, all on the set rather than on
the selector, because they are about the action that is *running*:

- a **commitment bonus** added to the running action's score — 0.15 by default;
- a **cooldown** per action after it ends;
- a **decision interval** so scoring does not happen every frame at all — 0.2 s by default.

⚠ **The bonus is applied after the veto.** An action whose condition has genuinely gone false cannot
hold on to itself; commitment is for a score that wobbled, not for one that stopped being true.

Measured: two actions within 2 % of each other, over sixty seconds — **fewer than 3 switches with the
defaults, more than 50 with inertia turned off.**

### Running a set inside a tree

`RunUtilitySetTask` is the join between the two planners: a tree handles the parts of a character that
are a procedure, and a set handles the part that is a judgement.

⚠ **It never finishes on its own.** A set is a standing judgement rather than a procedure with an end,
so it stays `Running` and is meant to be aborted by a decorator above it — the same shape as a
`Patrol` under a perception decorator. It fails only when the whole set is vetoed.

## Examples

A set as a file. Actions name their tasks out of the same node library a behaviour tree uses:

```yaml
version: 1
name: Villager
keys:
  - { name: hunger, type: Float }
  - { name: danger, type: Float }
selector: Bucketed
commitmentBonus: 0.2
decisionInterval: 0.25
actions:
  - name: Flee
    task: Wait
    fields: { Seconds: "3" }
    weight: 5
    bucket: 5
    considerations:
      - { name: afraid, input: Blackboard, key: danger, curve: Logistic, exponent: 12, centre: 0.4 }
  - name: Eat
    task: Wait
    fields: { Seconds: "2" }
    cooldown: 10
    considerations:
      - { name: hungry, input: Blackboard, key: hunger, curve: Logistic, exponent: 10, centre: 0.5 }
      - { name: safe, input: Blackboard, key: danger, slope: -1, shift: 1 }
```

Registering an input a file can name, the way a sensor is registered:

```csharp no-compile="a fragment; Hunger is the game's own component"
resolver.AddInput(
    "hunger",
    UtilityInputs.From((in AgentContext context) => context.World.Get<Hunger>(context.Entity).Fraction)
);

UtilitySetContentCompiler.TryCompile(content, resolver, out var diagnostics, out var set);
```

⚠ **A consideration whose input or key does not resolve scores zero**, which vetoes its action. That
is the safe direction — an unfinished set is an agent that does nothing rather than one that does the
wrong thing enthusiastically — and the compiler says which one it was. The importer fails the build
on a key that is not on the blackboard, because a typo there is an action that silently never runs.

## See also

- [Behaviour trees](behaviour-trees.md) — the other planner, and what `RunUtilitySet` sits inside.
- [The blackboard](blackboard.md) — where a consideration's number usually comes from.
- [Perception](perception.md) — what writes the keys a consideration reads.
