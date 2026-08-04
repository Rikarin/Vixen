# Vixen.Ai.Perception

What an agent knows about the world without being told. Five senses, a perceived list that remembers
where something was and how long ago, and three bounds that keep a thousand of them inside a frame.

Spec: [docs/plan/37](../../docs/plan/37-ai-behaviour-trees-utility-and-goap.md) § D15 and § P3.

## State

**Built and tested — 38 tests.** Both of P3's exit criteria are numbers rather than opinions. Five
hundred listeners against five hundred sources, every one of them sensing on the same tick:

| | Examined | Measured |
|---|---|---|
| With the broad phase | **7 960** | 2.10 ms |
| Without it | **250 000** — `listeners × sources`, by construction | 5.71 ms |

and the sight tests trace against a real `Vixen.Physics` world rather than a mock, because a mock
cannot catch either of the two things that actually go wrong: a ray that starts inside the listener's
own collider and reports itself as the blocker, and a ray that reaches the target and reports the
*target's* collider as one. Both make an agent that can never see anything, and both look correct
from a mock's point of view.

## Why this is a second assembly

`Vixen.Ai` may not reference `Vixen.Engine` or `Vixen.Physics`, and perception needs both — where
things are, and what is between them. A game that wants behaviour trees without a physics solver
links `Vixen.Ai` and stops. `PerceptionLayeringTests` asserts the reference list in both directions.

| | |
|---|---|
| `Senses/AiSense` · `SenseMask` | Sight, hearing, damage, touch, team. Unreal's five, minus prediction — which is a query, not a sense. |
| `Senses/PerceptionConfig` | Everything one *kind* of agent senses with. Shared by every agent of that kind, exactly the way a `BehaviorTreeTemplate` is. |
| `Ecs/AiPerception` | An entity that notices things: a config index, a slot, a team and a countdown. |
| `Ecs/AiStimuliSource` | An entity that can be noticed. Scene-placeable, and the first of the three bounds. |
| `Perceived/PerceivedTarget` · `PerceivedTargets` | What a listener knows and has not forgotten, with the sense, the last known location and the age. |
| `BroadPhase/StimuliGrid` | A uniform grid over the sources, rebuilt every frame. |
| `Seams/IPerceptionFilter` | Who may perceive whom — everyone, by team, or a lambda. |
| `Seams/IBlackboardBinding` | How a pass reaches a tree: the target/location/age triple, or a count and a flag. |
| `Seams/IOcclusionTester` | What stops sight — a `Vixen.Physics` raycast, or nothing at all. |
| `Seams/IPerceptionGovernor` | How often one listener senses — a fixed rate, or distance LOD in three bands. |
| `Ecs/PerceptionSystem` | The pass: gather, broad phase, radius, cone, trace, events, relay, bind. |
| `Nodes/PerceptionNodes` | The `PerceivedTarget` decorator, the `NearestPerceived` service and the `MakeNoise` task, and how a `.vxbt` builds them. |
| `Diagnostics/PerceptionSnapshots` | Adds what an agent can sense to a snapshot of what it is thinking — doc 37 § D20's fourth row. |

## The five things worth knowing before reading the code

### The lose-sight radius is a separate, larger radius, and leaving it out makes targets flicker

With one radius, a target loitering on the boundary is found and lost several times a second — and
each of those is a blackboard write, and each write aborts every branch whose decorator observes it.
It is the first thing every hand-rolled implementation gets wrong, it looks like a bug in the
behaviour tree rather than in the sense, and it costs one field.
`TheLoseSightRadiusIsWhatStopsTheFlicker` walks a target over six positions and counts: **five
changes of mind against one.**

### The tests run in a fixed order, and the order is the cost model

Filter, then radius, then cone, then trace. The last one is a physics raycast and everything above it
exists to stop it happening; `NothingOutsideTheConeIsEverTraced` asserts three candidates and one
trace.

### Hearing and damage are events; sight and touch are states

An entity is continuously visible, so sight is *sampled*. An entity is not "audible" — an event is —
so a hearing sense that sampled would have to sample the exact frame the shot happened, which at 4 Hz
means hearing one shot in six. Events are kept for `EventMemory` seconds and each listener consumes
the ones it has not seen.

⚠ **Consumed by sequence number, not by clock.** The clock only advances inside a step, so an event
reported *after* a pass in the same frame carries exactly the clock that pass recorded — and a
listener comparing clocks decides it has already heard a gunshot that has not happened yet.

### A relay is one shout, not a memory sync

An ally that perceives something tells the agents near it, so a squad reacts together without anybody
writing squad code. Two bounds on it, and both are load-bearing:

- **A relay is never relayed**, or a line of guards passes a sighting down the level one hop a pass
  and the whole map wakes several seconds later with nobody having seen anything.
- **One target per ally, the freshest.** Copying an ally's whole current list makes the relay cost
  `listeners × allies × targets`, which measured at **more than twice the entire rest of the pass** at
  five hundred agents — the one place D15's three bounds did not reach.

### The grid is two-dimensional, and it is not the physics broad phase

Cells over the vertical axis as well would triple the cells a query walks for a level where every
agent is within a few metres of the same height; the distance test is still in three dimensions, so a
tall level costs a longer chain rather than a wrong answer.

And it is over the *stimuli sources*, not over Jolt's bodies — a noise, a camera, a marker and a
corpse are all perceivable and none of them has a collider, so a physics query would be a broad phase
over the wrong set whose cost is the level's collision geometry. The physics world is still where the
occlusion trace goes, which is the expensive half.

## Reading

- [The guide page](../../docs/guide/ai/perception.md) — configuring a sense, and what reaches a tree.
- [Vixen.Ai](../Vixen.Ai/README.md) — the planner these senses feed.
- [docs/plan/37 § D15](../../docs/plan/37-ai-behaviour-trees-utility-and-goap.md) — why the schedule
  is the design.
