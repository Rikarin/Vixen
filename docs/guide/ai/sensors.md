---
title: Sensors
slug: ai/sensors
kind: guide
area: AI
summary: How the world reaches a blackboard — four kinds, and the difference between one query and a thousand.
api: [T:Vixen.Ai.SensorTarget, T:Vixen.Ai.ILocalWorldSensor, T:Vixen.Ai.ITargetSensor, T:Vixen.Ai.IGlobalWorldSensor, T:Vixen.Ai.IGlobalTargetSensor, T:Vixen.Ai.WorldReading, T:Vixen.Ai.TargetSearch, T:Vixen.Ai.Sensors, T:Vixen.Ai.SensorSet, T:Vixen.Ai.Nodes.WorldSensors, T:Vixen.Ai.Perception.Sensors.PerceptionInputs]
tags: [ai, sensors, blackboard, perception]
since: 0.1
status: stable
related: [ai/blackboard, ai/behaviour-trees, ai/utility, ai/goap, ai/perception]
---

## What it is

A **sensor** is how a fact about the world becomes a number or a place on an agent's
[blackboard](blackboard.md), so that a tree's decorator, a utility set's consideration and a GOAP
domain's world key all read one measurement taken once.

There are four kinds, and the split is the whole design:

| | Local — per agent | Global — per world, once a pass |
|---|---|---|
| **A number** | `ILocalWorldSensor` — "how hungry am I" | `IGlobalWorldSensor` — "is it night" |
| **A place or a thing** | `ITargetSensor` — "the nearest apple *to me*" | `IGlobalTargetSensor` — "the town square" |

## What it is for

Keeping the measurement out of the decision. A behaviour tree that computed a distance inside a
decorator would compute it again in the next decorator, and a utility set would compute it a third
time — so the number is measured once, written to a key, and everything reads the key.

⚠ **A sensor writes keys; it never decides anything.** A sensor that chose an action would be a
fourth planner, and the whole arrangement of this library is that there are exactly three.

## Using it

One set, added to the system, run for whoever is thinking:

```csharp compile
using Vixen.Ai;
using Vixen.Core;
using Vixen.Core.Mathematics;

public static class Village {
    public static SensorSet Build(BlackboardKey night, BlackboardKey square, BlackboardKey hunger) =>
        new SensorSet()
            .AddGlobal(night, Sensors.TimeOfDay(dayLength: 600f))
            .AddGlobalTarget(square, BlackboardKey.Invalid, Sensors.Landmark(new Vector3(0f, 0f, 0f)))
            .Add(hunger, Sensors.World((in AgentContext context) => context.Blackboard.GetFloat(hunger) + 0.01f));
}
```

### One query against a thousand

⚠ **This is the entire reason there are four kinds rather than two.** "Is it night" asked per agent is
a thousand identical queries for a thousand villagers; asked once a pass it is one. A `SensorSet` runs
its globals in `Begin` and its locals in `Apply`, and `AiSystem` calls the first once a step and the
second for the agents the governor named.

⚠ **A global's answer is cached at the top of the pass and never re-read.** An agent late in the pass
must see the same night as one early in it — a sensor asked per agent would let the clock advance
mid-pass and give two agents standing beside each other different weather, which is the class of bug
nobody looks for.

⚠ **Globals are applied before locals**, so a local sensor may read one. "How far am I from the fire"
needs the fire, which is a global target; the other order would make that sensor read last pass's
answer, once, for ever.

### A target is a place *and* a thing, and "nothing" is neither

`SensorTarget` carries a position, an entity and a `Found` flag. "The nearest apple" is an entity that
has a position, "the town square" is a position that is not an entity, and "there is no apple" is
neither — which a zero vector cannot say and `Entity.Null` can only half say.

⚠ **A target sensor that finds nothing *clears* its keys.** A key still holding the apple that was
eaten is an agent walking confidently to where an apple used to be, and it is invisible because the
key still looks perfectly reasonable.

### One sensor, several front ends

A local world sensor is also what a behaviour tree's `UpdateBlackboard` **service** runs — a service
that updates a key on an interval is a local sensor with a schedule. And a target sensor can be
registered under a [GOAP](goap.md) target key, so "the nearest apple to me" is one search whether a
tree writes it to a key, a consideration measures its distance, or a plan's action goes there.

### What ships, and where

`Vixen.Ai` ships delegates and constants, and deliberately nothing that reads a transform — it cannot
see a position, a collider or an inventory, and a library of half-guesses about what a game means by
"hungry" is the behaviour library this must not become.

| Assembly | Sensors |
|---|---|
| `Vixen.Ai` | `World`, `Constant`, `Target`, `Place`, `GlobalWorld`, `TimeOfDay`, `GlobalTarget`, `Landmark` |
| `Vixen.Ai.Nodes` | `Nearest<T>`, `DistanceToNearest<T>`, `CentreOf<T>`, `CountOf<T>`, `NearestOnNavMesh` |
| `Vixen.Ai.Perception` | `CountSensor`, `NearestSensor`, and the two utility inputs beside them |

## Examples

Wiring a village: two landmarks for everybody, one search each:

```csharp no-compile="a fragment; Scrap and the keys are the game's own"
agents.Sensors = new SensorSet()
    .AddGlobalTarget(refuge, BlackboardKey.Invalid, Sensors.Landmark(Refuge))
    .AddGlobalTarget(depot, BlackboardKey.Invalid, Sensors.Landmark(Depot))
    .AddTarget(scrap, scrapEntity, WorldSensors.Nearest<Scrap>());
```

The perception-backed ones, which are the reason `IUtilityInput` is a seam rather than a key reader:

```csharp no-compile="a fragment; the perception system is the game's"
var threat = PerceptionInputs.NearestPerceived(perception, SenseMask.Sight, range: 14f);

sensors.Add(nearbyCount, PerceptionInputs.CountSensor(perception));
sensors.AddTarget(seen, seenEntity, PerceptionInputs.NearestSensor(perception));
```

⚠ **Nothing sensed reads as *far*, not as near.** With the [zero rule](utility.md), "how close is the
threat" inverted is a veto — and an agent that treated an empty perceived list as a threat at zero
metres would flee from nothing for ever.

## See also

- [The blackboard](blackboard.md) — where a sensor writes.
- [Perception](perception.md) — the senses, which are the other way the world reaches a key.
- [Utility](utility.md) and [GOAP](goap.md) — the two planners that read what a sensor wrote.
