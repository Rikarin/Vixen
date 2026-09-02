<!--
SPDX-FileCopyrightText: Copyright (c) Rikarin
SPDX-License-Identifier: Apache-2.0
-->

# 15 — AI Village

Three agents, one of each planner, deciding about one intruder — and the first `Samples/` project in
this repository that references `Vixen.Ai` at all.

```
dotnet run --project Samples/15-AiVillage
```

`--vixen-frames N` stops after N frames and prints what everybody decided. The intruder's script runs
for 21 seconds, so roughly 1 450 frames covers the whole thing:

```
dotnet run --project Samples/15-AiVillage -- --vixen-headless --vixen-frames 1450
```

## Why it exists

`grep -rl "Vixen.Ai" Samples/` was empty. Eleven ✅ rows in [`docs/overview.md`](../../docs/overview.md)
and 32 KB of design prose in [doc 37](../../docs/plan/37-ai-behaviour-trees-utility-and-goap.md) rested
on a stack whose every runtime consumer was a `*.Tests` assembly. Doc 37's own P9 says so — its sample
is a test, *"stated as a deviation"* — and adds that a `Samples/` entry *"remains a good addition on
top of this rather than instead of it"*. This is that addition.

**A stack that runs and decides nothing is the failure to expect**, and it is the one a frame counter
cannot see. So the evidence here is a decision *log*, and it records transitions rather than states:
"the guard is patrolling" is true of a guard that has never done anything else and of one that has
just given up a chase.

### And it is the first thing in the tree that declares its frame

`IntruderSystem` carries `[GameSystem]`. That attribute shipped with a generator, a registry and two
hosts and had no application on a class anywhere — every hit in the repository was prose in a doc
comment — which is the "built but never fed" shape at the level of an authoring convention.

⚠ **It was chosen because it is representative, not because it is easy.** Its dependency is an
`Entity`, which is a struct: the one shape that declared itself and was then permanently
unsatisfiable, because `ServiceRegistry.Add<T>` is `where T : class`. `ServiceRegistry.AddValue` is
what closed that, and adopting a system whose dependency was already a class would have exercised
only what already worked.

So `Village.Register` puts the *intruder* in the registry and the three engine systems in the loop;
`VixenApplication` builds the fourth out of the declaration as soon as `OnInitialise` returns, and
says so — `Declared systems: 1 added — IntruderSystem.` ⚠ Marking a system **and** going on calling
`loop.Add` for it would run it twice; nothing dedupes.

## What it does

| | Planner | What it decides |
|---|---|---|
| **Guard** | behaviour tree | Walks a two-point beat; a decorator observing the `age` key aborts the patrol and chases whatever was seen recently. |
| **Villager** | utility set | Scores *flee* against *rest*. The threat reading is perception-backed; the refuge is a **global** sensor's answer, cached once a pass. |
| **Scavenger** | GOAP domain | Plans `collect` then `deposit` backwards from "delivered", and ignores the intruder entirely. |

One `AiSystem`, one `AgentActionRegistry`, one `BlackboardLayout`, one `PerceptionSystem` with one
config, one `SensorSet` and one navmesh baked from four vertices at start-up. What differs between the
three is the planner and nothing else — and the villager's *rest* and the scavenger's *wait* are the
same registered `WaitTask`, which is doc 37 § D2's payoff in the sample rather than in a comment.

## The log a full run prints

```
frame     1 ·   0.02s · guard     (BehaviorTree) <none> → patrol,  intruder  50.9 m
frame     1 ·   0.02s · villager  (Utility)      <none> → pause,   intruder  38.4 m
frame     1 ·   0.02s · scavenger (Goap)         <none> → pause,   intruder  28.8 m
frame     2 ·   0.03s · scavenger (Goap)         pause  → collect, intruder  28.8 m
frame   406 ·   6.97s · villager  (Utility)      pause  → flee,    intruder  11.2 m
frame   435 ·   7.45s · guard     (BehaviorTree) patrol → chase,   intruder  13.7 m
frame   632 ·  10.73s · villager  (Utility)      flee   → pause,   intruder  13.2 m
frame  1170 ·  19.70s · guard     (BehaviorTree) chase  → patrol,  intruder  17.6 m
…
25 change(s) of mind in 24.4 s — guard 3, villager 3, scavenger 19
```

Read the distances rather than the names. The guard picks up the chase at **13.7 m** — inside its
14 m sight radius — and drops it at **17.6 m**, outside the 16 m *lose-sight* radius that exists so a
target on the boundary does not flicker. The villager runs at 11.2 m and settles once it has reached
the refuge. The scavenger's nineteen changes are all `collect`/`deposit` and none of them are about
the intruder.

## What is on screen

The overlay, and nothing else. `AiOverlaySystem` is doc 37 § D20's in-game debugger — the active
tree path, the scored candidates, the plan's steps, the blackboard and the perception cones, drawn
through `DebugDraw`. It was built with nine tests and **registered by no application**; this sample is
the first thing in the repository to construct one.

There are no meshes, no materials and no content build. A sample that drew three capsules would have
shown the agents moving and left the overlay exactly as unreached as it was.

Two things a project copying this needs to know, both of which look like the overlay being broken:

- **`config.Graphics.Overlays = true` is the switch.** It is off by default, and it is what builds
  the `DebugDraw` *and* the compositor node that drains it. Without it `AiOverlaySystem` writes lines
  into an accumulator that does not exist.
- **`AiOverlayStyle.Default` culls by distance.** It is `Agent | Shapes` within 40 m of `Viewpoint`,
  and a viewpoint left at the origin with the village 40 m away draws nothing at all. This sample uses
  `AiOverlayStyle.Everything` and sets `Viewpoint` to the camera.

⚠ **Do not add `DebugDrawSystem`.** It ages the accumulator in `PostRender`, which under
`VixenApplication` runs before the GPU frame is recorded — so it deletes every line one call before
the node draws it, with every counter still reading correct. `AppGraphics.AdvanceDebug` does the
ageing at the only point in the frame where it is right.

## What it is honest about

- **The picture is not the evidence; the log is.** `--vixen-capture` is wired the way every other
  sample's is, and the run reports `DrawnAgents` and `DrawnRows` so a headless run says whether the
  overlay produced geometry — 3 agents and ~30 rows on a real device. **The written PNG has not been
  verified**: this sample was developed in an environment where opening a window is not allowed, and
  the capture path wants a presentable surface.
- **`IsVisible` follows `config.Headless` here, and does not in samples 03, 12 and 13.**
  `AppConfig.Apply` reads the command line *before* `OnConfigure`, so a game that assigns
  `IsVisible = true` unconditionally puts a window on the screen during a run that asked for none.
  Every other sample in this tree does exactly that.
- **One diagnosed symptom is expected**, and it is the scavenger's. `AiDiagnosis` counts action
  changes over whatever the recorder's ring holds and compares that to an absolute threshold of four
  — not to a rate — so an agent that alternates two actions correctly trips `Flapping` on any
  sufficiently long run. Doc 37 § P7 calls the thresholds *"arguments rather than constants, because
  whether four switches in a window is a bug depends on the window"*; there is no window, and a game
  cannot supply one. A symptom against the guard or the villager would be a real one, and the test
  suite asserts there is none.
- **Nothing here is replicated and nothing ever will be** — doc 37 § D17. The village is
  single-process on purpose.
- **The camera does not move**, so this is not a streaming or a residency sample.

## What building it found

Two defects, neither visible in review, both found by running the stack rather than reading it.

**A behaviour-tree agent reported whatever action was registered first.** `AiSystem.Advance` hands a
tree agent to `BehaviorTreeInstance.Step` and returns before the `Action` field the other two planners
maintain — correctly, since the tree owns which task is running. But `AiSnapshots.Take` filled
`Snapshot.Action` from that field for *every* planner, so the overlay's and the panel's "what is it
doing" read `NameOf(0)` for every tree agent alive. It is doc 37 § P6's trap — *a planner that has
chosen nothing must run nothing* — in its reporting form, and it survived because zero is a valid
registry index and the answer therefore always looked like an answer. The existing tree-snapshot test
registers *one* action, so index zero was the right answer by accident; P7's overlay test reads its
readout off a *utility* agent. Here it read as a guard that was visibly chasing an intruder while
reporting that it was waiting.

**A zeroed `NavigationDestination` is the world origin.** It is a `Vector3` and a version, with no
"has one" flag — so an agent whose planner has not issued a destination walks to (0, 0, 0). The
villager, whose highest-scoring action is a `Wait` until it perceives something, set off for the
corner on frame one and arrived long before the intruder was ever in range. Nothing errored, every
system did its job, and the symptom was an agent that "ignored" a threat it could no longer see.
`Spawn` seeds the destination to the agent's own position.

*A sample that prints its numbers is a test with a human in the loop.*

## The tests

`AiVillage.Agent.Tests` links this project's source rather than referencing it — a game project would
drag `Vixen.App`, a desktop platform and a Vulkan backend into a suite that stands up no window — and
runs the village on a real `EngineLoop`.

That last part is the reason the suite exists beside `VillageSampleTests` in `Vixen.Ai.Nodes.Tests`,
which asserts the same shape over a hand-written stepping loop. **`PerceptionSystem` carries
`[UpdateBefore(typeof(AiSystem))]` and until this sample nothing had ever asked a scheduler to honour
it**, because every caller in the repository called `Step` by hand in the order it wanted. A
declaration nothing reads is a comment; `The_engine_runs_perception_before_the_planners` reads it.

## See also

- [doc 37](../../docs/plan/37-ai-behaviour-trees-utility-and-goap.md) — the design, and P9's note that
  a `Samples/` entry was owed.
- [`Core/Vixen.Ai/README.md`](../../Core/Vixen.Ai/README.md) — the traps, with their symptoms.
- [`Core/Vixen.Ai.Diagnostics/README.md`](../../Core/Vixen.Ai.Diagnostics/README.md) — the overlay.
