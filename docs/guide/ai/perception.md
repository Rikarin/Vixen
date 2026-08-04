---
title: Perception
slug: ai/perception
kind: guide
area: AI
summary: The five senses, what an agent remembers about them, and the three bounds that keep a village of them inside a frame.
api: [T:Vixen.Ai.Perception.AiSense, T:Vixen.Ai.Perception.SenseMask, T:Vixen.Ai.Perception.Senses, T:Vixen.Ai.Perception.SightSettings, T:Vixen.Ai.Perception.HearingSettings, T:Vixen.Ai.Perception.TouchSettings, T:Vixen.Ai.Perception.DamageSettings, T:Vixen.Ai.Perception.TeamSettings, T:Vixen.Ai.Perception.PerceptionConfig, T:Vixen.Ai.Perception.PerceptionLibrary, T:Vixen.Ai.Perception.PerceivedTarget, T:Vixen.Ai.Perception.PerceivedTargets, T:Vixen.Ai.Perception.StimuliGrid, T:Vixen.Ai.Perception.StimulusEvent, T:Vixen.Ai.Perception.PerceptionStats, T:Vixen.Ai.Perception.PerceptionParticipant, T:Vixen.Ai.Perception.IPerceptionFilter, T:Vixen.Ai.Perception.PerceptionPredicate, T:Vixen.Ai.Perception.PerceptionFilters, T:Vixen.Ai.Perception.TeamPerceptionFilter, T:Vixen.Ai.Perception.DelegatePerceptionFilter, T:Vixen.Ai.Perception.IBlackboardBinding, T:Vixen.Ai.Perception.TargetLocationAgeBinding, T:Vixen.Ai.Perception.PerceivedCountBinding, T:Vixen.Ai.Perception.IOcclusionTester, T:Vixen.Ai.Perception.OpenSightlines, T:Vixen.Ai.Perception.PhysicsOcclusion, T:Vixen.Ai.Perception.IPerceptionGovernor, T:Vixen.Ai.Perception.FixedRateGovernor, T:Vixen.Ai.Perception.DistanceLodGovernor, T:Vixen.Ai.Perception.PerceivedTargetDecorator, T:Vixen.Ai.Perception.NearestPerceivedService, T:Vixen.Ai.Perception.MakeNoiseTask, T:Vixen.Ai.Perception.PerceptionNodes, T:Vixen.Ai.Perception.Ecs.AiPerception, T:Vixen.Ai.Perception.Ecs.AiStimuliSource, T:Vixen.Ai.Perception.Ecs.PerceptionSystem]
tags: [ai, perception, senses, sight, hearing]
since: 0.1
status: stable
related: [ai/behaviour-trees, ai/blackboard, ai/authoring-a-tree, ai/world-nodes, ai/utility]
---

## What it is

**Perception** is how an agent finds out that something is there without anybody telling it. Five
senses — sight, hearing, damage, touch and what an ally reports — over entities that opted in to
being perceivable, producing a per-listener list of what is known and how long ago it was known.

It lives in `Vixen.Ai.Perception`, which is a second assembly rather than a folder in `Vixen.Ai`,
because it needs `Vixen.Engine` for where things are and `Vixen.Physics` for what is between them. A
game that wants behaviour trees without a physics world links `Vixen.Ai` and stops.

## What it is for

The loop every stealth game, shooter and survival game has: a guard on a patrol notices the player,
chases, loses sight, searches where the player *was*, gives up. Every piece of that is here — the
cone, the lose-sight radius, the last known location and the stimulus age — so a tree branches on
keys instead of a game writing its own memory management.

You do *not* want it for a trigger volume, a scripted ambush or a boss that always knows where you
are. Those are one line each, and a sense that is really a script is a sense whose settings mean
nothing.

## Using it

Three things: a `PerceptionConfig` per kind of agent, an `AiPerception` on whoever notices, and an
`AiStimuliSource` on whoever can be noticed.

```csharp compile
using Vixen.Ai;
using Vixen.Ai.Perception;
using Vixen.Ai.Perception.Ecs;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Transforms;

public static class Guards {
    public static PerceptionSystem Watching(World world, BlackboardKey target, BlackboardKey age) {
        var system = new PerceptionSystem();

        var config = system.Configs.Add(
            new PerceptionConfig {
                Senses = SenseMask.Sight | SenseMask.Hearing,
                Sight = new() { Radius = 22f, LoseSightRadius = 30f, ConeDegrees = 100f },
                Filter = PerceptionFilters.Hostiles,
                Binding = new TargetLocationAgeBinding(SenseMask.Sight | SenseMask.Hearing, target, age: age)
            }
        );

        world.Create(AiPerception.Sensing(config, team: 1), LocalTransform.At(new Vector3(0f, 0f, 8f)));
        world.Create(AiStimuliSource.Perceivable(team: 2), LocalTransform.Identity);

        return system;
    }
}
```

⚠ **A listener does not have to be an agent.** A camera, a trap and a trigger all want to notice
things without deciding anything; the blackboard binding is the only part that needs an `AiAgent`,
and it is skipped when there is not one.

### The senses

| Sense | What it is | How it is found |
|---|---|---|
| **Sight** | inside a radius, inside a cone, with nothing solid in the way | sampled every pass |
| **Hearing** | a noise somebody made, inside a radius scaled by its loudness | reported — `ReportNoise` |
| **Damage** | was hurt by it. No radius, no cone, nothing in the way | reported — `ReportDamage` |
| **Touch** | within a small radius, through anything | sampled every pass |
| **Team** | an ally within range perceived it and said so | derived, after the other four |

⚠ **Hearing and damage are events; sight and touch are states.** An entity is continuously visible,
so sight can be asked at any moment. An entity is not "audible" — an *event* is — so a hearing sense
that sampled would have to sample the exact frame the shot happened, which at 4 Hz is one shot in
six. Events are kept for `PerceptionSystem.EventMemory` seconds and each listener consumes what it
has not seen, so a slow listener hears every shot a little late rather than hearing one in six.

⚠ **Damage goes through the filter regardless of team.** An agent shot by its own side has to notice,
or friendly fire is invisible to the AI and a squad walks through its own grenades.

### The lose-sight radius

`SightSettings` has two radii and the second one is not a refinement:

```yaml
Radius: 22          # how far it notices something
LoseSightRadius: 30 # how far something already seen can get before it is lost
```

⚠ **With one radius, a target loitering on the boundary is found and lost several times a second.**
Each of those is a blackboard write, and each write aborts every branch whose decorator observes it —
so the symptom is a guard that stutters between patrolling and chasing, and it reads as a bug in the
behaviour tree rather than in the sense. It is the first thing every hand-rolled implementation gets
wrong and it costs one field.

### What a pass costs, and the three bounds on it

Sight is O(listeners × sources), and the schedule is the whole design. Three things bound it and all
three are mandatory rather than tuning:

1. **A broad phase.** `StimuliGrid` is a uniform grid over the sources, rebuilt every frame — every
   source moves every frame, so the incremental update a tree needs to earn its structure is the case
   that never happens. Five hundred listeners against five hundred sources examine **7 960** sources
   instead of **250 000**.
2. **A per-listener update rate, with a random deviation.** ⚠ A deviation of zero puts every agent
   spawned in the same frame on the same tick for ever, which is a frame that costs the whole
   population.
3. **Distance LOD.** `DistanceLodGovernor` stretches the interval in three bands; with the shipped
   0.1 s interval the far band lands on 4 Hz.

`PerceptionSystem.LastStats` reports what the last frame actually cost, and it is a deliverable
rather than a diagnostic — a perception system that quietly stopped noticing things is a frame budget
met by an AI nobody agreed to.

### What reaches a tree

An `IBlackboardBinding` is the join. Perception writes through `Blackboard.Set*`, so every decorator
observing those keys gets its abort for free — a target appearing *is* the mechanism that interrupts
a patrol, with nothing in the perception pass knowing a behaviour tree exists.

`TargetLocationAgeBinding` writes the default triple: who, where they were, and how long ago.

⚠ **The target key stays set after the target is lost, and the age key is how a tree tells.** Clearing
it would make "chase him" and "search where he was" two branches over two keys, and the second would
need its own copy of the position and its own timer — which is the hand-written memory management
this exists to remove. A branch that wants a live target tests `age < 0.5`; a branch that wants to
search tests `age > 0.5`; both read one key.

`PerceivedCountBinding` is the other shape: a flag and a number, naming no target at all. That is
what a turret, an alarm or a "you are outnumbered" branch actually reads.

### The three nodes

`PerceptionNodes.Register` adds them to a schema and teaches a resolver to build them, which is how a
`.vxbt` can name a node whose implementation is in another assembly:

| Node | Slot | Does |
|---|---|---|
| `PerceivedTarget` | decorator | this sense perceives something, or perceived it recently enough |
| `NearestPerceived` | service | writes the nearest currently-perceived target into a key |
| `MakeNoise` | task | emits a hearing stimulus where the agent is |

⚠ **`PerceivedTarget` reads the perceived list but *observes* a key**, and that pairing is the whole
trick. A perceived list changing is not an event a tree can see; only a blackboard write is. So the
binding writes the key, the key's observers interrupt the branch, and the decorator then answers the
finer question — which sense, how stale — that a key could not carry.

## Examples

Wiring occlusion to a real physics world, and slowing down the agents nobody is looking at:

```csharp no-compile="a fragment; the physics world and the player are the game's"
system.Occlusion = new PhysicsOcclusion(physics, PhysicsLayerMask.All);
system.Governor = new DistanceLodGovernor { NearRadius = 20f, FarRadius = 55f };
system.Focus = player.Position;
```

A guard that chases what it can see and searches where it last saw it — one key, two branches:

```yaml
keys:
  - { name: target, type: Entity }
  - { name: seen, type: Vector3 }
  - { name: age, type: Float }
root:
  name: Brain
  type: Selector
  children:
    - name: Chase
      type: Wait
      decorators:
        - type: Blackboard
          fields: { Key: age, Test: Less, Value: "0.5", Aborts: Both }
      fields: { Seconds: "3" }
    - name: Search
      type: Wait
      decorators:
        - type: Blackboard
          fields: { Key: target, Test: IsSet, Aborts: LowerPriority }
      fields: { Seconds: "6" }
    - { name: Patrol, type: Wait, fields: { Seconds: "5" } }
```

Reading a listener's list directly, which is what a debug overlay does:

```csharp no-compile="a fragment; the system and the entity come from the running world"
if (system.PerceivedBy(world, guard) is { } perceived) {
    foreach (var target in perceived.Targets) {
        overlay.Line(
            guard,
            target.LastKnownLocation,
            target.Current ? Colour.Red : Colour.Amber,
            $"{target.Sense} {target.AgeAt(system.Clock):0.0} s"
        );
    }
}
```

A filter a game writes itself, for factions that are a table rather than a byte:

```csharp compile
using Vixen.Ai.Perception;

public static class Factions {
    public static IPerceptionFilter Hostile(bool[,] table) {
        ArgumentNullException.ThrowIfNull(table);

        return PerceptionFilters.Where(
            (in PerceptionParticipant listener, in PerceptionParticipant source, AiSense sense) =>
                listener.Entity != source.Entity && table[listener.Team, source.Team]
        );
    }
}
```

## See also

- [Behaviour trees](behaviour-trees.md) — what the keys a sense writes actually interrupt.
- [The blackboard](blackboard.md) — the six key types, and why a version bumps only on a real change.
- [Authoring a behaviour tree](authoring-a-tree.md) — adding a node type of your own, which is what
  the three nodes here do.
