---
title: Reading a frame's order without running it
slug: ecs/system-order
kind: guide
area: ECS
summary: The order a set of systems will run in, worked out from their types alone — and the ordering attributes that turn out to do nothing.
api: [T:Vixen.Ecs.Systems.SystemPlan, T:Vixen.Ecs.Systems.SystemPlacement]
tags: [ecs, systems, scheduling, tooling, diagnostics]
since: 0.1
status: stable
related: [ecs/queries, ecs/components, core/job-priorities]
---

## What it is

`SystemGraph.Plan` answers the question `SystemGraph.Build` answers, from `Type` objects instead of
from systems. It returns a `SystemPlan`: a `SystemPlacement` for every system — its phase and its
position in that phase — plus `Unsatisfied`, a list of the ordering attributes that had no effect.

It is the same topological sort. `Build` and `Plan` share it deliberately, so a tool cannot report an
order the runner will not produce.

## What it is for

A frame's order is decided by attributes spread across as many files as there are systems, and two of
the ways it goes wrong are invisible from any one of them.

The first is that `[UpdateBefore]` and `[UpdateAfter]` are **dropped without a word** when they name a
system the graph does not have. That is correct behaviour for a runner — a build that registers half
its systems should still boot on the other half — but it means a renamed system, or one somebody
forgot to add, leaves an attribute that reads as though it works and does nothing.

The second is subtler: **ordering only ever applies within a phase.** An `[UpdateBefore]` pointing at a
system in another phase is not an error and is not honoured; the phase order already decided the
question, and the attribute is either redundant or impossible.

`SystemPlan.Unsatisfied` reports both, and tells them apart. `vixen doctor systems` is what prints it
for a real project.

> The order is answerable about types. The parallel schedule is not.

That line is the whole of why `SystemPlan` exists as a second shape rather than as a mode of
`SystemGraph`. Every input to the sort — the phase, the edges — is metadata the compiler already baked
into the type. `SystemAccess` is not: a system may implement `IDeclaredAccess` and compute what it
touches in its constructor. So a `SystemPlacement` carries no access and a `SystemPlan` has no
`DependsOn` edges, because the honest answer to "what runs concurrently?" without an instance is *I
cannot tell you*, and an undeclared access conflicts with everything.

You want `Build` whenever you have the systems — that is every runtime path. You want `Plan` when you
have an assembly and no game: a tool, a test, an editor listing a project's frame before Play.

## Using it

Hand it the system types in registration order. It groups them by `[UpdateInGroup]`, sorts each phase,
and hands back the placements in phase order:

```csharp no-compile="a fragment; the system types are whichever ones you have"
var plan = SystemGraph.Plan([typeof(HaulSystem), typeof(ScrapSystem)]);

foreach (var phase in plan.Phases) {
    foreach (var placement in plan.InPhase(phase)) {
        Console.WriteLine($"{phase} {placement.Order + 1}: {placement.Name}");
    }
}

foreach (var problem in plan.Unsatisfied) {
    Console.WriteLine(problem);   // "…'s [UpdateAfter(typeof(X))] does nothing: …"
}
```

Ties break on registration order, exactly as they do in `Build`, so a set with no constraints comes
back in the order you passed it.

A cycle throws `InvalidOperationException` with every system in it named — the same refusal `Build`
raises, because it is the same sort. Catch it: a diagnostic that crashes on the one input it was
written to diagnose is worse than none.

## Examples

**Two systems, one constraint, and one attribute that does nothing.** `HaulSystem` is passed first and
runs second; `StraySystem` names a system that was never passed, so its constraint is dropped and it is
reported.

```csharp compile
using System;
using Vixen.Core.Threading;
using Vixen.Ecs.Systems;

public sealed class GhostSystem : SystemBase {
    public override JobHandle Update(in SystemContext context, JobHandle dependency) => dependency;
}

[UpdateInGroup(SystemPhase.Update)]
public sealed class ScrapSystem : SystemBase {
    public override JobHandle Update(in SystemContext context, JobHandle dependency) => dependency;
}

[UpdateInGroup(SystemPhase.Update)]
[UpdateAfter(typeof(ScrapSystem))]
public sealed class HaulSystem : SystemBase {
    public override JobHandle Update(in SystemContext context, JobHandle dependency) => dependency;
}

[UpdateInGroup(SystemPhase.LateUpdate)]
[UpdateAfter(typeof(GhostSystem))]
public sealed class StraySystem : SystemBase {
    public override JobHandle Update(in SystemContext context, JobHandle dependency) => dependency;
}

public static class FrameReport {
    public static void Write() {
        var plan = SystemGraph.Plan([typeof(HaulSystem), typeof(ScrapSystem), typeof(StraySystem)]);

        // ScrapSystem first, though HaulSystem was passed first: the attribute won.
        foreach (var placement in plan.InPhase(SystemPhase.Update)) {
            Console.WriteLine($"{placement.Order}: {placement.Name}");
        }

        // "StraySystem's [UpdateAfter(typeof(GhostSystem))] does nothing: no GhostSystem is in this set…"
        foreach (var problem in plan.Unsatisfied) {
            Console.WriteLine(problem);
        }
    }
}
```

**Reading it for a whole project.** `vixen doctor systems --assembly <path>` loads a built game
assembly, takes the systems its `[GameSystem]` declarations registered, and prints exactly this. It
also names any `ISystem` in the assembly that carries no declaration — which either is added by hand
or never runs, and no tool can tell which.

## See also

- [Queries](queries.md) — what a system does once the runner reaches it.
- [Components](components.md) — the data a system's access is declared over.
- [Job priorities](../core/job-priorities.md) — the other half of "the parallel schedule is not
  answerable": which tier a scheduled job goes in, and what defers behind a frame.
