<!--
SPDX-FileCopyrightText: Copyright (c) Rikarin
SPDX-License-Identifier: Apache-2.0
-->

# 37 — AI: behaviour trees, utility and GOAP

Three ways of deciding what an agent does next, one surface for what it then does, and a node editor
for the one of the three that is genuinely a graph.

[28](28-gameplay-framework.md) § AI names all three and puts them in `Vixen.Gameplay.Ai`, on the
gameplay spine, depending on `Vixen.Gameplay.Combat`. **That placement is wrong and this document
moves them.** A behaviour tree is not a gameplay feature in the sense tags, items and loot tables
are: it is a scheduler over a tree, it has no opinion about what an ability is, and a stealth game
with no inventory needs it exactly as much as an MMO does. What is left behind in
`Vixen.Gameplay.Ai` after the move — threat, aggro, leashing, spawn tables, dialogue state — is the
part that really is game rules, and it will reference this library rather than contain it.

⚠️ **Amends [28](28-gameplay-framework.md); extends [04](04-ecs-and-scripting.md),
[11](11-editor.md), [13](13-diagnostics.md) and [20](20-editor-parity.md).** It is a separate file
rather than a section in 28 for the reason [26](26-virtual-cameras.md) and
[34](34-move-sets-and-pose-constraints.md) are: the placement argument is an argument, the node
editor is an editor project with its own surface, and the thing being built sits under 28 rather
than inside it.

**The claim this document has to earn.** A designer authors an encounter as a tree in the editor,
watches it run with the live branch highlighted and the blackboard beside it, and changes a decorator
without a programmer and without a recompile. A second designer tunes a village's ambient population
by dragging response curves, and nobody authored a graph at all. A third gives a handful of agents
goals and an action set and gets a sequence nobody wrote. All three kinds of agent are ticked by one
system, share one perception model, and are debugged through one overlay. If that fails, the honest
answer is what every project does anyway: one hand-written state machine per game, and no tooling.

**Read [Part 2 — the decisions](#part-2--the-decisions) before the phases.** Half of what is
valuable here is the shape of the runtime — the template/instance split, the abort range test, one
scorer with two hosts — and the phases are only the order those get built in.

---

## The rows this touches

Five, and one of them is a debt to a document rather than to the code.

### `Vixen.Navigation` ✅ — "voxel bake, tiled mesh, A\* + funnel, crowd + RVO, sliced/jobbed queries"

The floor this stands on, and it is already the right shape. `NavPathQueue` runs searches **a slice
at a time against a frame budget**, on the caller's thread or on jobs, precisely so that a crowd
changing its mind costs a budget rather than a search each. The GOAP resolver is the same problem
with a different graph and gets the same treatment, from the same argument — see
[D16](#d16--the-planner-is-a-job-and-the-budget-is-per-frame-not-per-agent).

⚠ Its one owed row — *"navmesh baked from a compiled scene"* — is **not** a blocker here. An agent
that cannot path is an agent whose `MoveTo` task fails, which is a result a tree already has to
handle, and every test in this document bakes its mesh in the test.

### `Vixen.Ecs` ✅ — systems, phases, declared access, jobs

Nothing new is needed. The agent system is a `SystemBase` in `SystemPhase.Update` declaring its
access, its `Update` returns a `JobHandle`, and per-agent state is a component. What this document
does add is a *rule about what may be in that component*, which [D3](#d3--one-asset-many-agents-and-the-state-is-a-byte-range) is.

### `Vixen.Editor.NodeGraph` ✅ — the node-graph framework

The behaviour-tree editor uses its **canvas, search, inspector, command stack and diagnostics**, and
**not** its document model. [D19](#d19--the-canvas-is-shared-the-document-is-not) is that decision
and the reasoning is `Vixen.Editor.AnimationGraph`'s, applied to a structure that is a tree rather
than a state machine and fails the model's rules for different reasons.

### `Vixen.Animation` ✅ — `Symbol`

`Symbol` — *"an interned name: four bytes that compare as fast as an integer"*, hashed rather than
indexed **because an index assigned in first-seen order is not the same number on two machines** —
is exactly the type a blackboard key, a gameplay-relevant tag and a GOAP world key each want, and it
lived in `Vixen.Animation.Moves` because move sets needed it first. **P0 lifted it to `Vixen.Core`**
— two four-byte interned-name types in one engine is the duplication this repository avoids
everywhere else.

Its remarks transferred nearly verbatim; the one change is that the collision paragraph now names
both callers rather than `MoveSet` alone, because a `<see cref>` from `Vixen.Core` to an assembly
above it cannot resolve. A-R8's type-forward turned out to be unnecessary: `Symbol` was still in
`PublicAPI.Unshipped.txt`, so the move is a baseline rewrite and a `using` sweep, which is exactly
what "cheapest now" meant.

### [28](28-gameplay-framework.md) ⬜ — `Vixen.Gameplay.Ai`

Unbuilt, and this document is what it becomes. See
[What this changes in doc 28](#what-this-changes-in-doc-28) for the exact split; the summary is that
the three planners, the blackboard, perception and the environment query leave, and threat, aggro,
leashing, spawn tables and dialogue stay.

---

## Part 1 — the argument

### Why this is Core and not Gameplay

Doc 28's structure is a **spine**: everything depends on `Vixen.Gameplay`, which holds tags,
definitions, attributes, effects and requirements, and each feature hangs off it. That structure is
right for what it holds. It is wrong for this, and the tell is in doc 28's own dependency line —
*"`Combat` is depended on by `Pvp`, `Instances`, `Ai`"*.

A behaviour tree that depends on combat is a behaviour tree that cannot run a stealth patrol, a
shopkeeper, a companion following a player through a puzzle, a formation of ships, or a squad in a
game with no abilities at all. The dependency is not there because a tree needs an ability; it is
there because doc 28 was going to ship *encounter* nodes — cast this, taunt that — in the same
assembly as the tree that runs them. Those are two things:

| | Belongs where | Why |
|---|---|---|
| A composite that runs children until one fails | **Core** | Contains no game concept. Identical in every game ever shipped |
| A blackboard key holding a target entity | **Core** | Ditto |
| A sight cone with an occlusion trace | **Core** | Physics and geometry |
| A task that casts ability `Fireball` | **Gameplay** | Names a definition, a cooldown and a resource |
| A threat table with taunt and leashing | **Gameplay** | An MMO combat rule with no meaning in a stealth game |
| A spawn table with respawn timers | **Gameplay** | Definitions and drop rules |

Doc 28 already wrote the sentence that settles it: *"the engine-side ambition is deliberately
bounded: **a planner and a perception model, not a behaviour library.** What a mob does is the
game's."* A planner and a perception model is a description of an engine subsystem. Leaving it on
the gameplay spine means a game that wants the first sentence has to take the second.

⚠ **This is a layer move, and that is what makes it enforceable.** Doc 28's tree puts
`Vixen.Gameplay.*` under `Core/`, which would have made this document's central rule uncheckable —
`CheckArchitecture` derives a project's layer from its top-level directory, so two projects in `Core/`
can reference each other freely and the boundary would have had to be asserted by a hand-written
assembly test that somebody eventually deletes.

**The gameplay framework moves to a `Gameplay/` folder of its own, referencing `Core/` in one
direction and nothing referencing it back.** With that, the rule this whole document exists to
establish is one line beside the three that are already there:

```csharp
// Core sits below Gameplay: an engine that cannot be used without a threat table and an
// item definition is not an engine. The AI libraries are the case this was added for —
// docs/plan/37 § Why this is Core and not Gameplay.
if (layer == "Core" && LayerOfProject(projects, reference) is "Gameplay") { … }
```

⚠ **This document does not own that relocation.** It is [02](02-repository-layout.md)'s tree and
[28](28-gameplay-framework.md) § Library structure that record it, and both still show `Core/`. What
this document owns is the consequence: `Vixen.Ai` sits in `Core/` on `Vixen.Core.*`, `Vixen.Ecs` and
`Vixen.Engine`, `Vixen.Gameplay.Ai` sits in `Gameplay/` on top of it, and the gate refuses the edge
in the wrong direction. If the relocation does not happen, the fallback is the assembly test in
[Testing](#testing) — weaker, because it is a test somebody can delete rather than a layer somebody
would have to argue for.

### Why three, and not one

The three are not three implementations of one idea; they answer three different questions, and the
question a game is asking is a property of the agent rather than of the game.

| | The question it answers | What it is good at | What it is bad at |
|---|---|---|---|
| **Behaviour tree** | *"What is the highest-priority thing I can do right now?"* | Authored, inspectable, deterministic. A designer can read the tree and predict the agent. Boss phases, scripted encounters, anything a level designer must be able to tune | The cross-product. Twenty conditions is a tree nobody can hold in their head, and a reactive condition costs a decorator on every branch it can interrupt |
| **Utility scoring** | *"Of everything I could do, which is most worth doing?"* | Open-ended action sets, graceful degradation, tuning by curve rather than by structure. Ambient populations, creature packs, Sims-shaped agents | Explaining itself. "Why did it do that" is a number, and a designer cannot point at a branch |
| **GOAP** | *"What sequence gets me from here to a goal?"* | Sequences nobody authored, and agents that recover when the world changes underneath them | Cost. A search per agent per replan, and an action set that grows the graph superlinearly |

Unreal ships the first and the third of these arguments — behaviour trees plus, since 5.0,
**StateTree**, which merges behaviour-tree *selection* with state-machine *transitions* precisely
because a large tree stops being readable. Unity's Behavior package ships the first with a graph
that is deliberately not a strict tree. The best-known open GOAP implementation for Unity,
[crashkonijn/GOAP](https://github.com/crashkonijn/GOAP), ships the third with a job-scheduled
resolver, a capability model and a plan *viewer* rather than a plan editor. Nobody ships one of the
three and claims it covers the others, and the reason is in the table.

Doc 28 already made the per-archetype choice the right way: *"the choice is per-archetype"*. This
document keeps that and adds the mechanism that makes it cheap — [D2](#d2--three-planners-one-action).

### Why not StateTree, and why not a fourth

StateTree is the strongest argument against building a plain behaviour tree, and it was considered.
It is not built here, for two reasons and one of them is timing:

- **Its value is proportional to tree size, and its cost is a second mental model.** A StateTree
  state is simultaneously a selector and a state with tasks and transitions; that is genuinely more
  compact for a large agent and genuinely harder to explain. The engine has no shipped AI at all
  today. Building the thing every reference implements first, and every designer already knows, is
  what makes the editor testable against expectations somebody already has.
- **Nothing in the design forecloses it.** A StateTree is a different *arrangement* over the same
  three primitives this document builds: a compiled template with an execution order, per-agent
  memory, and condition observers with a priority abort. If it is built, it is built on P0 and P1's
  substrate as a second front end, next to the behaviour tree rather than instead of it.

⚠ **Hierarchical task networks are deliberately absent.** An HTN and GOAP overlap almost entirely in
what they are for and differ in whether the decomposition is authored or searched. Shipping both is
two planners for one job, and doc 28 asked for GOAP by name.

### What the references got right, and what this takes from each

| From | Taken | Left |
|---|---|---|
| **Unreal — event-driven trees** | The whole execution model: a tree that is *not* traversed every frame, decorators that register as observers on the data they read, and aborts driven by an execution index. This is the single most valuable idea in any of the references | UE's node instancing fallback; see [D3](#d3--one-asset-many-agents-and-the-state-is-a-byte-range) |
| **Unreal — the four node kinds** | Composite / task / decorator / service. Forty years of behaviour-tree literature has two kinds; the other two are what make a UE tree readable, and every one of the reference node lists in [Part 3](#part-3--the-node-library) is one of the four | — |
| **Unreal — EQS** | "Where is the best place to stand" as a separate, scored, reusable asset. [D14](#d14--an-environment-query-is-the-utility-scorer-with-a-different-host) folds its scoring onto the utility scorer's | Its separate node-graph editor; an EQS is a list, and it is authored as one |
| **Unreal — the gameplay debugger** | One overlay, keyed categories, live blackboard values, the active branch, the perception cones, the EQS spheres. [D20](#d20--one-debug-surface-and-it-runs-in-a-shipped-build) | — |
| **Unity Behavior** | The observer-abort *scope* rule, which is stricter and better specified than UE's: an observer affects only the siblings under its own parent composite, and the parent restarts. And node authoring where the node's *description* is the thing an author reads | Non-tree joins and merging branches. A join makes the abort scope unanswerable, which Unity's own troubleshooting page is largely about |
| **crashkonijn/GOAP** | The decomposition — goals, actions, sensors, world keys, target keys, capabilities per agent type — and the job-scheduled resolver over a priority queue. And the plan **viewer** rather than editor | Its Unity-shaped configuration surface; and the target-per-action rule is kept but generalised, see [D12](#d12--a-goap-action-has-a-target-and-that-is-what-keeps-the-graph-small) |
| **IAUS / utility theory** | The axis — one input, one curve, four parameters — the multiplicative combination with the zero rule, the compensation for consideration count, weight buckets, and inertia | The "infinite axis" marketing. It is a scored list |

---

## Part 2 — the decisions

### D1 — the blackboard is a compiled key table, not a dictionary

Every reference has a blackboard and every reference makes it a named-key store. The naive
implementation is a `Dictionary<string, object>`, which costs a hash and a box per read, allocates,
and cannot be observed cheaply.

**A blackboard *layout* is authored once and compiled; a blackboard *instance* is a byte range.** The
`.vxbb` asset is a list of `(name, type, sync)` rows; compiling it assigns each key an **index**, an
offset and a size, so a key reference in a node is a `ushort` and a read is a span slice. The name
survives as a `Symbol` for diagnostics and for the editor's picker, and nothing in a frame reads it.

Six types, and the list is closed: `Bool`, `Int`, `Float`, `Vector3`, `Entity`, `Symbol`. Everything
else is one of those — a "class" key is a `Symbol`, a rotation is three floats or an entity to look
at, an object reference is an `Entity`. Closing the list is what makes a key sixteen bytes at worst,
a comparison a switch rather than a virtual call, and an inspector that can draw every key with no
extension point.

**A write bumps a per-key version and notifies that key's observers.** The observer list is per key
*index*, so notification is an array lookup, and the version is what lets a decorator that is *not*
an observer answer "has this changed since I last looked" without storing the value. Both are needed:
observers drive aborts, versions drive services that only recompute when something moved.

⚠ **A key is "set" or "unset" independently of its value**, because `Is Set` is the single commonest
decorator in every reference and `Entity.Invalid`/`0`/`Vector3.Zero` are all legal values somebody
means. One bit per key in a bitmask beside the values, and clearing a key is a write like any other.

### D2 — three planners, one action

The three planners are **three ways of choosing**; what gets chosen is one thing.

```csharp
public interface IAgentAction {
    void Start(in AgentContext context, Span<byte> state);
    ActionStatus Tick(in AgentContext context, Span<byte> state, float delta);
    void Abort(in AgentContext context, Span<byte> state);
}
```

`ActionStatus` is `Running`, `Succeeded`, `Failed`. A behaviour-tree **task**, a utility **action**
and a GOAP **action** are all this, which is what makes doc 28's sentence — *"an encounter can mix
them"* — true rather than aspirational:

- a GOAP action may **be** a behaviour tree (its `Tick` runs a sub-tree to completion),
- a behaviour-tree task may **be** a utility set ("do the most sensible ambient thing until
  something interrupts"),
- and a project that writes one `MoveToTask` gets it in all three.

⚠ **The action does not own its state.** `Span<byte>` into the agent's memory block, for
[D3](#d3--one-asset-many-agents-and-the-state-is-a-byte-range)'s reason: an action object is shared
by every agent running that asset, so a field on it is a field a thousand agents write to. This is
the mistake every hand-rolled behaviour tree makes, it is invisible until the second agent exists,
and making the interface take the span is the only arrangement where it cannot be made.

### D3 — one asset, many agents, and the state is a byte range

Unreal's behaviour-tree component holds `TArray<uint8>` of instance memory and hands each node a
window into it, with the tree asset itself immutable and shared. That is the correct design for an
ECS engine and it is taken directly.

A `.vxbt` compiles to a `BehaviorTreeTemplate`: **a flat array of nodes in depth-first pre-order**,
each carrying its parent, its first child, its child count, its decorator and service ranges, and
its memory offset and size. The template is immutable, shared, and has no per-agent field anywhere.
An agent's state is `BehaviorTreeMemory` — a handle into a pooled block sized by the template's
total — held in a `[Component]` that is a handle and an asset reference and nothing else.

Three things fall out and each is worth the arrangement on its own:

- **Pre-order index *is* priority.** Node 4 is higher priority than node 9 because it is earlier in
  the walk, which is what "left to right, top to bottom" means. No separate priority field, and no
  way for the two to disagree.
- **A subtree is a contiguous range.** Each node also stores `LastDescendant`, so *"is node X inside
  node Y's subtree"* is `Y.Index <= X.Index <= Y.LastDescendant` — two comparisons, which is what
  makes [D6](#d6--an-abort-is-a-range-test-and-it-happens-at-a-safe-point)'s abort affordable at a
  thousand agents.
- **A thousand agents on one tree is one allocation each, of a size known at load.** Nothing is
  allocated during a tick.

⚠ **Unreal has an escape hatch this does not: node instancing**, where a node that cannot fit its
state in a plain memory struct gets a real object per agent. It exists because Blueprint nodes hold
UObject references. Vixen has no equivalent problem and adding the hatch would mean every node's
memory access has two paths for the life of the engine. A node that needs a reference stores an
`Entity` or an `AssetId` in its memory, both of which are values.

### D4 — the four node kinds, and why decorators are attached rather than wired

**Composite, task, decorator, service** — Unreal's decomposition, kept whole.

A **composite** has ordered children and a rule for walking them. A **task** is a leaf and is an
`IAgentAction`. A **decorator** is a condition attached to a node that gates entry into it and may
observe. A **service** is attached to a composite and ticks on an interval for as long as that
branch is active, which is where perception updates, target selection and blackboard maintenance go.

Two kinds would have been enough to *express* every tree — a condition is a task that returns
success or failure, and a service is a task under a parallel. Four is what makes a tree *readable*,
and the difference shows up on the canvas: with two kinds, the tree that says "chase the player,
while checking every 0.5 s whether he is still visible, and give up if he stops being" is nine nodes
in four levels. With four it is one task with a decorator and a service on it, and it fits on one
box.

**A decorator and a service are attached, not connected.** They are drawn as stacked rows on the
node they belong to, in the document they are lists on that node, and there is no edge. This is
Unreal's arrangement and the reason is structural rather than visual: an attachment is *always*
exactly one edge to *exactly one* parent, can never be shared, and has no meaningful position of its
own. An edge that can only ever be one thing is a wire the author has to draw to say nothing.

⚠ **Decorator order on a node is significant and is authored.** They evaluate top to bottom and the
first failure stops the rest, so putting the cheap test above the trace is the author's decision and
the editor must let it be dragged.

### D5 — child order is authored data, not an X coordinate

Unreal derives a composite's child order — which is to say the entire priority ordering of the tree —
from the **horizontal position of the child nodes on the canvas**. It is a defensible choice: it
matches what the author sees, and it needs nothing in the file.

**Vixen stores the order.** A composite's children are an ordered list in the document; position is
still authored data and still saved, and it is only a position.

The reason is that deriving it makes three ordinary gestures dangerous. Auto-layout re-derives
positions, so *laying out the graph can silently reorder the tree*. Dragging a node six pixels left
to line it up with its sibling can change which of two branches wins. And a merge that resolves two
positions can produce a tree whose behaviour neither author wrote, with a diff showing only
coordinates. All three are silent, and all three change what the agent does.

The cost is that the canvas must **show** the order and offer a way to change it: an execution-index
badge on every node (which Unreal also draws, from its derived order), and reordering as a command —
dragging a child onto the gap between two siblings, or ↑/↓ on the selection. That is one gesture to
build against three classes of silent corruption.

### D6 — an abort is a range test, and it happens at a safe point

This is the mechanism that makes an event-driven tree work, and it is where hand-rolled
implementations go wrong.

A decorator declares `ObserverAborts`: `None`, `Self`, `LowerPriority`, or `Both` — Unreal's four,
which Unity's Behavior package independently arrived at. When it is not `None`, the decorator
registers on the blackboard keys it reads. A write to one of those keys re-evaluates the decorator,
and if the result *changed*:

- **`Self`** — if the decorator now fails and its own subtree is running, abort the running node.
  Formally: abort if the active node's index is inside `[node.Index, node.LastDescendant]`.
- **`LowerPriority`** — if the decorator now passes and the active node is *after* its subtree, abort
  the active node and re-enter from the decorator's own node. Formally: abort if
  `active.Index > node.LastDescendant`.
- **`Both`** — both tests.

Two integer comparisons per registered observer per changed key. That is the whole reason
[D3](#d3--one-asset-many-agents-and-the-state-is-a-byte-range)'s pre-order layout exists.

⚠ **Unity's scope rule is adopted and Unreal's is not.** In Unity Behavior an observer *"affects only
the siblings of their immediate parent composite"* and the parent composite restarts, re-evaluating
from child zero. Unreal's abort reaches further up the tree, which is more powerful and is the
subject of most of the confusion in its forums — a decorator two levels above the running task
aborting a branch it does not visibly contain. The scoped rule is what makes the editor able to
*draw* what a decorator can interrupt, which is P7's abort-scope overlay, and a rule you can draw is
a rule an author can predict.

⚠ **An abort never happens inside `Tick`.** A blackboard write during a task's own tick — which is
the ordinary case, since tasks write their results — would otherwise destroy the state of the thing
currently executing. Notifications enqueue a *pending abort* on the agent, and the tree services them
at the top of the next step, before any node updates. Unity documents the same one-frame latency and
the same cause. It is a real cost and it is stated in the guide rather than hidden: **a condition
that becomes false during a task's tick takes effect at the start of the next tick.**

### D7 — a tick is not a traversal

A classic behaviour tree walks from the root every frame. An event-driven one keeps the **active
node** and the path to it, and does nothing at all when nothing has changed: the active task ticks,
active services tick if their interval elapsed, and pending aborts are serviced. A tree whose agent
is walking across a courtyard costs one `MoveTo.Tick` and a service every 0.4 s.

That is Unreal's central claim for its tree — *"the Behavior Tree passively listens for events"* —
and the measurable consequence is the exit criterion in [P1](#p1--behaviour-trees-runtime--12-em):
**a thousand idle agents on a ten-node tree cost less than a thousand agents on a one-node tree
does under a traversing implementation.**

⚠ **Services are the pressure valve and they need a random deviation.** An interval alone means every
agent spawned in the same frame ticks its service in the same frame for ever, which turns a 0.5 s
perception update into a spike every thirty frames. Unreal's service carries `Interval` and
`RandomDeviation` for exactly this and both are kept — with the deviation drawn from the agent's own
seeded stream, not a shared one, for [D18](#d18--determinism-is-a-property-of-the-decision-not-of-the-schedule)'s reason.

### D8 — a utility axis is one input, one curve, four parameters

The utility half is the Infinite Axis shape, and the shape is small enough to state completely.

A **consideration** is one normalised input in `[0,1]`, one curve, and four parameters `m`, `k`, `b`,
`c` — slope, exponent, vertical shift, horizontal shift. Six curve kinds:

| Curve | Form | For |
|---|---|---|
| `Linear` | `m(x − c) + b` | "more is better", proportionally |
| `Polynomial` | `m(x − c)^k + b` | `k > 1` late-rising, `k < 1` early-rising. Covers quadratic and its rotation |
| `Logistic` | `m / (1 + e^(−k(x − c))) + b` | a threshold: "urgent below half health" |
| `Logit` | the inverse — `m·ln(x / (1 − x))` shifted | diminishing returns |
| `Gaussian` | `m·e^(−(x − c)² / (2k²)) + b` | a sweet spot: "ten metres is the right range" |
| `Sampled` | `CurveEvaluation.Evaluate` over authored keys | when no formula is the shape a designer wants |

`Sampled` is not a grudging seventh option. *The Sims* uses a piecewise curve for hunger because no
formula gives "ignore it entirely, then suddenly care", and the engine already has the machinery:
`Vixen.Core`'s `CurveSample`/`TangentMode`/`CurveEvaluation.Evaluate`, and
`Vixen.Ui.Controls.Advanced.CurveEditor` to draw it. The response-curve editor this needs is a
control that exists.

**Scores combine as a weighted geometric mean, and any zero is a veto.**

```
score(action) = weight × ( Π consideration_i ) ^ (1/n)
```

The naive product is what everybody writes first and it is wrong in a way that is hard to see: with
every term in `[0,1]`, an action with six considerations is *structurally* worse than an identical
action with three, so adding a consideration to tune an action quietly demotes it. The geometric mean
is the standard compensation and it makes the count irrelevant. The **zero rule** survives the mean —
a single zero factor makes the product zero — and that is what expresses "never, under any
circumstances", which a weighted sum cannot.

`weight` is the bucket: 1 for ambient, 2–3 for important, 5 for emergency. It is a multiplier rather
than a hard bucket ordering because a hard ordering means an emergency action with a zero-scoring
consideration blocks everything below it; a multiplier degrades.

### D9 — picking is a policy, and so is not changing your mind

Highest-scoring is one selection rule and it is not always the right one. `IUtilitySelector`, with
four shipped:

- `Highest` — deterministic, correct for anything a designer must be able to predict.
- `WeightedRandom` — score as weight. Natural-looking and occasionally stupid.
- `TopWeightedRandom(n)` or `TopWeightedRandom(fraction)` — weighted random among the best few, which
  is the one most games actually want.
- `Bucketed` — dual utility: actions are grouped, the best group is chosen first and only its members
  are considered. A guard being shot at never scores "drink coffee".

⚠ **Inertia is not optional and it is not the selector's job.** An agent re-scoring every frame with
two actions at 0.51 and 0.49 oscillates, and oscillation is the single most visible failure mode of a
utility agent. Three mechanisms, all on the *set* rather than on the selector, because they are about
the running action and the selector does not know there is one: a **commitment bonus** added to the
action currently running, a **cooldown** per action after it ends, and a **decision interval** so
scoring does not happen every frame at all. The default set has a commitment bonus and a 0.2 s
interval, because the default that oscillates is a default that makes the feature look broken.

### D10 — GOAP plans backwards, over conditions and effects

A goal is a set of **conditions**: `(worldKey, comparison, value)` with comparison in
`{<, ≤, >, ≥}`. An action declares conditions it needs and **effects** it has, an effect being a
world key and a direction — this action *increases* `AmmoCount`, that one *decreases* `Hunger`.

The resolver chains backwards: a condition wanting a key **greater** matches actions with a
**positive** effect on that key; a condition wanting it **smaller** matches actions with a negative
one. That is the whole matching rule, it is crashkonijn's, and it is worth stating because the
alternative — full symbolic world states with arbitrary predicates — is what makes classic GOAP
implementations both slow and impossible to author.

**The search is A\* over the action graph, from goal to satisfied.** Nodes are partial plans, the
edge cost is the action's `BaseCost` plus a distance term to its target, and the heuristic is the
count of unsatisfied conditions. The graph itself — which action's effect can serve which action's
condition — is **built once when the agent type is configured**, not per resolve; only the costs and
the condition evaluations are per agent.

⚠ **The search is bounded and the bound is reported.** A GOAP search is exponential in depth and the
engine must not hang on a badly authored action set. `GoapSettings` carries a node budget and a depth
limit; exceeding either produces `PlanFailure.BudgetExhausted` naming the goal, which the debugger
shows and a test asserts. The shipped defaults target doc 28's stated scale: *"the few dozen agents
where emergent behaviour is the point, not the thousand critters."*

### D11 — the plan is a sequence, but only the head is committed

A resolver returns a sequence. An agent that *follows* the sequence is an agent that walks into a
door that closed after the plan was made. An agent that re-plans every frame is a search per agent
per frame.

`IReplanPolicy`, with crashkonijn's three controller shapes as the shipped implementations —
`Reactive` (re-plan when the current action ends or fails), `Proactive` (re-plan on an interval as
well, so a better plan can be discovered), `Manual` (the game says when) — and the rule that the
**plan's tail is advisory**: it is kept, it is what the debugger draws, and every step re-checks the
next action's conditions before starting it rather than trusting the plan that produced it.

### D12 — a GOAP action has a target, and that is what keeps the graph small

crashkonijn's FAQ makes the case and it is right: an action is performed **at a position**, and
movement is not modelled as actions in the graph. The alternative — a `MoveTo(x)` action per
destination — makes the graph a function of the world's contents.

So an action declares a `TargetKey`, resolved by a **target sensor** to a position or an entity, plus
a stopping distance and a `MoveMode` of `MoveThenPerform` or `PerformWhileMoving`. The agent's
movement is [29](29-players-and-possession.md)'s `MoveIntent` and
`Vixen.Navigation`'s `NavigationDestination`, unchanged; the planner produces a target and the
existing movement stack gets there.

⚠ **The distance cost is a straight line by default, not a path length.** A path query per candidate
action per resolve is a nav search per edge of the search graph, which is the cost of the whole
system in one line. `IActionCostModel` is the seam; the shipped alternative uses
`NavMeshQuery`'s **hierarchical** query, which is the cheap one, and the guide says plainly what it
costs.

### D13 — sensors are how the world reaches the blackboard, and there are two kinds ✅ *P1 and P9*

Both GOAP and utility need to read the world, and both need it cheap. crashkonijn's split is taken
whole because it is the right one:

| | Local — per agent | Global — per agent type |
|---|---|---|
| **World value** (an int or a float on a key) | `IWorldSensor` — "how hungry am I" | `IGlobalWorldSensor` — "is it night" |
| **Target** (a position or an entity) | `ITargetSensor` — "the nearest apple *to me*" | `IGlobalTargetSensor` — "the town square" |

A global sensor runs **once per type per pass** rather than once per agent, which is the difference
between one query and a thousand for "is it night". They are also what a behaviour tree's *services*
are — a service that updates a blackboard key on an interval is a local sensor with a schedule — so
there is one implementation and two front ends.

**Built.** `IWorldSensor` and `UpdateBlackboardService` in P1; the other three interfaces, the shipped
implementations of each and the `SensorSet` that runs them in [P9](#p9--the-seams-twice-and-the-sample--07-em-).

⚠ **A global's answer is cached at the top of the pass**, so two agents standing beside each other
cannot see different weather — a sensor asked per agent would let the clock advance mid-pass. And
globals are applied **before** locals, so that "how far am I from the fire" can read the fire rather
than last pass's answer, once, for ever.

⚠ **Locals run for the agents the governor named and not for every agent.** A sensor is a read of the
world on behalf of a decision, so an agent that is not deciding has no use for a fresh one. The
globals still run once a step, because their whole point is that they cost the same whoever is
thinking.

⚠ **The third front end is GOAP's.** `GoapTargetSensors.Add(key, ITargetSensor)` lets a domain name a
target sensor directly, which is what makes "one implementation and two front ends" true of three.

### D14 — an environment query is the utility scorer with a different host

Unreal's EQS answers "where should I stand" by generating candidate points, running scored tests over
them and taking the best. Utility scoring answers "what should I do" by generating candidate actions,
running scored considerations over them and taking the best.

**Those are the same machine.** A test's `TestPurpose` (filter / score / both), its scoring equation
(linear, square, inverse linear, square root — which are `Polynomial` at four values of `k`), its
clamping and its normalisation are the consideration pipeline from
[D8](#d8--a-utility-axis-is-one-input-one-curve-four-parameters) with points substituted for actions.

So `IScoredCandidateSet<T>` is the shared abstraction, the curves are shared, the editor's curve
preview is shared, and the environment query is a **list** asset — generators, then tests, in order —
rather than a second node graph. That is also what Unreal's EQS editor is, once you look past the
fact that it is drawn on a graph canvas: a root with a fixed list of children and no wiring
decisions.

### D15 — perception is a system, and its cost is a schedule

The five senses are Unreal's, minus one: **sight, hearing, damage, touch, team**. Prediction is left
out — it is a query, not a sense, and it belongs to whatever is aiming.

An `AiPerception` component declares configured senses; an `AiStimuliSource` component makes an
entity perceivable, per sense, with a team affiliation. Results land in a **perceived-targets** list
and, through a configured mapping, in blackboard keys: the target entity, its last known location,
and the age of the stimulus. That last one is what makes "search where he was" expressible without
a game writing memory management.

⚠ **Sight is O(listeners × sources) and the schedule is the whole design.** Three things bound it,
all of them mandatory rather than tuning: a broad-phase query from `Vixen.Physics` rather than a scan
(radius first, cone second, occlusion trace last and only for what survived), an **update rate per
listener with a random deviation**, and distance-based rate reduction so the agents behind the player
sense at 4 Hz. Unreal's own answer is the same three and Mass's crowd work is the same argument at
larger scale.

⚠ **A lose-sight radius is a separate, larger radius, and leaving it out makes targets flicker.**
It is the first thing every implementation gets wrong and it costs one field.

### D16 — the planner is a job, and the budget is per-frame, not per-agent

`AiSystem` runs in `SystemPhase.Update` and declares its access. Within it:

- Behaviour-tree steps are **per agent and cheap**, and parallelise over chunks. A tree step touches
  that agent's memory and that agent's blackboard, and nothing else — which is what makes the
  declared access honest.
- Utility scoring is **per agent and bounded**, and runs on the same pass.
- GOAP resolves are **expensive and unbounded**, so they do not run on the frame that asked for them.
  A resolve is queued, scheduled onto jobs, and consumed when it completes — exactly
  `Vixen.Navigation`'s `NavPathQueue` arrangement, for exactly its reason.

Above all three, a **governor**: a per-frame budget of agent updates, spent by a round-robin with a
guaranteed floor per agent, reporting what it gave up. This is doc 34's `ConstraintGovernor`, and its
lesson is taken with it — ⚠ *"spending the budget on the most important characters in order gave the
first thirty-seven everything and stranded the rest"*. An agent that misses its slot ticks later, not
never.

⚠ **`Symbol` writes and observer notifications are the parallelism hazard, not the tree walk.** A
service writing a blackboard key that another agent's decorator observes is a cross-agent edge. The
answer is that **a blackboard instance is owned by one agent** and a shared blackboard is a distinct
thing — `SharedBlackboard`, written only in a single-threaded phase, read freely. Unity's shared-vs-
instance variable split is the same distinction and exists for the same reason.

### D17 — AI runs on the authority, and the client is never told the tree

Doc 28 says all three planners *"run on the realm only"*, and doc 16's model makes that the only
coherent placement: a client that planned would plan from an interest-filtered, interpolated view of
the world and reach different conclusions, and reconciling two planners is not a thing anybody has
made work.

So: **nothing in `Vixen.Ai` is `[Replicated]`.** What crosses the wire is the *result* — the
components the agent's actions write, which are already replicated by their own subsystems:
`NavigationDestination`, `MoveIntent`, animation state, whatever the game's own actions set. A client
sees an NPC walk to the door; it has no blackboard, no tree and no plan.

⚠ **The one thing that must cross is a debug channel, and it must be off by default.** The editor's
AI debugger has to work against a running dedicated server, which means a request/response for one
agent's tree state — gated behind the same switch doc 13's remote inspector uses, and never present
in a shipping server build.

### D18 — determinism is a property of the decision, not of the schedule

A replay of the same tick with the same inputs must make the same choice. Four rules, three of which
the engine has already paid for elsewhere:

- **Names are `Symbol` hashes**, not table indices — `Symbol`'s own remarks make the argument, and
  doc 16 makes a divergence a desync rather than a curiosity.
- **Ties break on index**, always: the lower execution index, the lower action index, the lower
  entity id. Never on a float comparison and never on enumeration order.
- **Random draws come from a seeded stream keyed on the agent**, the way `VfxRandom` keys a
  particle's randomness on its identifier rather than its slot. A weighted-random selector reading
  `Random.Shared` is a desync per NPC per second.
- ⚠ **The governor's budget changes *when* an agent decides, and that is a real hole.** An amortised
  scheduler is time-dependent by construction, so a determinism test has to fix the budget, and a
  replay has to reproduce the schedule. The round-robin is therefore a pure function of the tick
  number and the agent's index — not of arrival order, not of a queue — which makes it reproducible
  as long as the population is. Stated here rather than discovered later, because this is the kind of
  thing that surfaces as a desync six months in.

### D19 — the canvas is shared, the document is not

`Vixen.Editor.AnimationGraph`'s README says why a state machine is not on `Vixen.Editor.NodeGraph`,
in three bullets. A behaviour tree fails two of the three for different reasons, and the exercise is
worth doing rather than assuming:

| `NodeGraphModel`'s rule | A state machine | A behaviour tree |
|---|---|---|
| An edge carries a typed value | ✗ nothing on the edge | ✗ nothing on the edge, but `PortKind.Flow` already exists for "an edge that means *after*" |
| An input takes one edge | ✗ four transitions arrive at one state | ✓ a child has exactly one parent |
| No cycles | ✗ cycles by construction | ✓ a tree |
| — | — | ✗ **a composite's children are ordered**, and `Edges` is an unordered list |
| — | — | ✗ **decorators and services attach**, and the model has no notion of an attachment |

So the model does not fit — but it fails on two additions rather than on three fundamentals, and the
temptation is to add ordered edges and attachments to `NodeGraphModel`. That is refused: neither the
shader graph nor the VFX graph nor the compositor has any use for either, and a framework that grows
a feature for one consumer grows a feature every consumer's tests have to consider.

**What is shared is everything above the model**: `NodeCanvas` and its wire arithmetic, `NodeSearch`
and the ranked search-to-create popup, `NodeInspector`'s row layout, `NodeDiagnostic`, the command
stack with its merging, and the clipboard's fragment shape. `BehaviorTreeAsset` /
`BehaviorTreeModel` are their own, with `Vixen.Editor.Ai` in the same relationship to
`Vixen.Editor.NodeGraph` that `Vixen.Editor.AnimationGraph` is.

⚠ **Five things have to be added to `NodeCanvas`, and they are additive.** Stacked attachment rows on
a node; a badge in the node header; a top-down layered layout (the existing `NodeGraphLayout` is
left-to-right by longest path, which is right for dataflow and wrong for a tree); reorder-drop
between siblings; and a runtime overlay layer. The first, second and fifth are what the *shader*
graph would want for a live preview and a validation badge too, so they are not a tax.

### D20 — one debug surface, and it runs in a shipped build

Unreal's answer to "why did my AI do that" is one key that opens one overlay with numbered
categories: navigation, general, behaviour tree with a live blackboard, EQS with scored spheres,
perception with drawn cones. It is the single most-used AI feature in the engine and it works in a
packaged build.

Vixen gets the same thing, and it is one surface for all three planners because
[D2](#d2--three-planners-one-action) made the agent one shape:

| Shows | Behaviour tree | Utility | GOAP |
|---|---|---|---|
| **What it is doing** | the active path, node by node | the chosen action and its score | the plan, and where in it |
| **Why** | the last result of every decorator on the path | every candidate's considerations, factor by factor | the goal, and the conditions still unmet |
| **Its data** | the blackboard, live | the same | the same, plus the world-key projection |
| **Its senses** | the perceived list and the cones, drawn | " | " |

Drawn through `DebugDraw`, which means it is testable with no window — doc 34's `ConstraintGizmos`
precedent — and available in a debug build of a game rather than only in the editor. The editor
panels in [P7](#p7--the-debugger--08-em) are a richer view over the same records, not a second
implementation.

---

## Part 3 — the node library

*"The node editor is mandatory as well as basic nodes for most common nodes."* This is the list, with
Unreal's as the reference. Every row is either shipped, or absent with a reason.

⚠ **"Shipped" means <i>authorable</i>, and for three rows it did not.** `Composite`,
`ConditionalLoop` and `RunUtilitySet` had runtime classes and tests from P1 and P5 and no
`BehaviorNodeSchema` entry — so the compiler refused them, the search popup never offered them, and a
✅ here was about a class rather than about a feature. Found by reading this table against the schema
after P9, fixed in the same pass, and `AuthorableNodeTests` now asserts every name in this section
against the shipped table so it cannot happen again.

### Composites — `Vixen.Ai` ✅ *all five, P1*

| Node | Semantics |
|---|---|
| `Selector` | children left to right until one **succeeds**; fails if all fail |
| `Sequence` | children left to right until one **fails**; succeeds if all succeed |
| `Parallel` | one main task plus a background branch, with `FinishMode` of `Immediate` (abort the branch) or `Delayed` (let it finish). Unreal's `SimpleParallel`, under the name people look for |
| `RandomSelector` | a selector over a shuffled order, with per-child weights. From the agent's seeded stream, per [D18](#d18--determinism-is-a-property-of-the-decision-not-of-the-schedule) |
| `Priority` | a selector that re-evaluates from child zero every step rather than resuming. Explicit, because "does a selector resume" is the question every implementation answers differently and silently |

⚠ **`Parallel` runs one *main* task, not N branches**, which is Unreal's restriction and it is kept.
True N-way parallelism makes the abort scope in [D6](#d6--an-abort-is-a-range-test-and-it-happens-at-a-safe-point)
ill-defined — two branches whose decorators want to abort each other — and every engine that offered
it has a page explaining why it does not do what people expect.

### Decorators — `Vixen.Ai` ✅ *all fifteen: thirteen in P1, `PerceivedTarget` in P3, `DoesPathExist` in P4*

| Node | What it tests | Observes |
|---|---|---|
| `Blackboard` | a key is set / not set / compares to a constant | ✓ |
| `CompareEntries` | two keys against each other | ✓ |
| `Composite` ✅ | AND / OR / NOT over other decorators, so a condition is not a branch. ⚠ Its operands are the attachment's nested rows, one level deep — an expression tree of arbitrary depth is a thing the generated inspector cannot draw | ✓ |
| `Cooldown` | this branch has not run for *n* seconds | — |
| `TagCooldown` / `SetTagCooldown` | a named cooldown shared across a tree — Unreal's pair | ✓ / — |
| `TimeLimit` | fails the branch after *n* seconds | — |
| `Loop` | repeat *n* times, or until failure, or forever with a timeout | — |
| `ConditionalLoop` ✅ | repeat while a key condition holds — one nested decorator, which is the condition | — |
| `ForceSuccess` / `ForceFailure` | override the result | — |
| `Inverter` | invert it | — |
| `RandomChance` | pass with probability *p*, from the seeded stream | — |
| `Cone` / `KeepInCone` | a location is inside a cone from the agent; the second keeps testing | ✓ |
| `IsAtLocation` | within an acceptance radius, 2D or 3D | ✓ |
| `PerceivedTarget` | this sense currently perceives something, or perceived it recently enough — `Vixen.Ai.Perception` ✅ | ✓ |
| `DoesPathExist` ✅ | a path exists, by raycast / budgeted / full — `Vixen.Ai.Nodes`. ⚠ Budgeted stands in for hierarchical; there is no coarse graph to read | ✓ |

⚠ **`Composite` matters more than it looks.** Without it, "attack if he is visible **and** I have
ammo **and** I am not fleeing" is three decorators whose failure semantics compose but whose *abort*
semantics do not, or a branch per combination. Unreal added it late and its own documentation warns
it costs more than the C++ equivalent; here it compiles to a small expression tree over key indices
and is cheap.

### Services — `Vixen.Ai`, `Vixen.Ai.Perception`, `Vixen.Ai.Nodes` ✅ *all four: `UpdateBlackboard` in P1, `NearestPerceived` in P3, `DefaultFocus` in P4, `RunQuery` in P8*

| Node | Does |
|---|---|
| `UpdateBlackboard` | runs an `IWorldSensor` on an interval into a key |
| `NearestPerceived` ✅ | writes the nearest currently-perceived target of a sense into a key. ⚠ Nearest, not freshest — that is what the binding does, and "shoot whichever one is about to reach me" and "react to what just happened" are different questions |
| `DefaultFocus` ✅ | keeps the agent's focus pointed at a key's entity — Unreal's, whose value is that everything downstream reads one place. ⚠ And clears it when the key is unset, which is the half people leave out |
| `RunQuery` ✅ | runs an environment query on a schedule and writes the best result to a key — [P8](#p8--environment-queries--10-em-). ⚠ Named `KeepQueryResult` in the schema, because a schema entry has one slot and the task below is the other form. It clears the key when nothing survived: a stale destination walks an agent confidently to a spot that stopped being cover two seconds ago |

All four take `Interval` and `RandomDeviation`, per [D7](#d7--a-tick-is-not-a-traversal).

### Tasks — split by what they need ✅ *the `Vixen.Ai` ones in P1, `MakeNoise` in P3, the six `Vixen.Ai.Nodes` ones in P4, `RunUtilitySet` in P5, `RunQuery` in P8*

| Node | Assembly | Does |
|---|---|---|
| `Wait` / `WaitBlackboardTime` | `Vixen.Ai` | a fixed time, or one from a key |
| `FinishWith` | `Vixen.Ai` | succeed or fail immediately — the branch terminator |
| `SetBlackboardValue` / `ClearBlackboardValue` | `Vixen.Ai` | write a constant or another key |
| `RunSubtree` / `RunSubtreeDynamic` | `Vixen.Ai` | push another `.vxbt`; the dynamic form takes it from a key |
| `RunUtilitySet` ✅ | `Vixen.Ai` | run a utility set as a task until interrupted — [D2](#d2--three-planners-one-action) made this possible. ⚠ It never finishes on its own: a set is a standing judgement, not a procedure with an end. ⚠ A file *names* a set and the game registers the compiled object, the way `PlaySound` and `DoesPathExist` already do |
| `Log` ✅ | `Vixen.Ai` | into the visual log, so a tree can narrate itself. ⚠ Into `AgentDebugRecorder` and not a second ring — [P7](#p7--the-debugger--08-em-) reads the log rather than adding one |
| `MoveTo` ✅ | `Vixen.Ai.Nodes` | to a key's position or entity, over the navmesh, with an acceptance radius and an optional path-observing abort |
| `MoveDirectlyToward` ✅ | `Vixen.Ai.Nodes` | in a straight line, ignoring navigation |
| `Patrol` ✅ | `Vixen.Ai.Nodes` | a route, forward / ping-pong / loop |
| `RotateToward` ✅ | `Vixen.Ai.Nodes` | face a key, at a rate |
| `PlayAnimation` ✅ | `Vixen.Ai.Nodes` | a clip or a move-set query, and wait for it |
| `PlaySound` ✅ | `Vixen.Ai.Nodes` | one shot, optionally waiting |
| `MakeNoise` ✅ | `Vixen.Ai.Perception` | emit a hearing stimulus. ⚠ Once, on the first tick, and it remembers — a task kept running for a frame or two would otherwise be a footstep that reads as a stampede |
| `RunQuery` ✅ | `Vixen.Ai.Nodes` | run an environment query now and write its result. ⚠ It finishes in one tick and *fails* when nothing survived, which is what lets a selector fall through to the branch that does not need an answer — take cover, or if there is none, run |

### Not shipping, and why

- **A task that casts an ability, applies an effect or checks a gameplay tag.** Those name doc 28's
  definitions and belong to `Vixen.Gameplay.Ai`, which is what that package becomes. Unreal ships
  `Check Gameplay Tag Condition` because its gameplay tags are in the engine; Vixen's are not, and
  putting the node here would be the exact dependency this document removed.
- **`PushPawnAction`.** Unreal's pawn-action stack is a second decision system that predates its
  behaviour trees. There is one here.
- **Scene/GameObject nodes** — Unity Behavior's `Instantiate Object`, `Load Scene`, `Add Force`. Those
  are general scripting, not decision-making, and Unity's own comparison page draws that line:
  Behavior answers *"what should this do next"*, Visual Scripting answers *"how is this
  implemented"*. A task that spawns a prefab is a task a game writes in four lines.

---

## Part 4 — the seams

Everything a project will want to replace, and the rule from doc 34's P9 applies: **each of these
gets a second implementation in the repository**, differing in shape rather than in numbers, because
a one-implementation interface is an interface nobody has checked is an interface.

✅ **Enforced by `SeamTests` since [P9](#p9--the-seams-twice-and-the-sample--07-em-)**, in
`Core/Vixen.Ai.Nodes.Tests` — the only test project that can see all four shipped assemblies at once.
It found three gaps on its first run, and every ✅ below is now a count rather than a claim.

| Seam | What it decides | Shipped |
|---|---|---|
| `IAgentAction` ✅ | what an action *is* | every task, every GOAP action, every utility action |
| `IUtilityInput` ✅ | where a consideration's number comes from | blackboard key, distance to target, a constant, a delegate — and, since P9, the **perceived count** and the **nearest perceived** in `Vixen.Ai.Perception`, which are the two implementations that read neither a key nor a lambda. ⚠ They are there and not here because this assembly may not see a sense |
| `IResponseCurve` ✅ | input → score | the six of [D8](#d8--a-utility-axis-is-one-input-one-curve-four-parameters), plus a delegate for the shape that is game logic rather than a curve |
| `IUtilitySelector` ✅ | which of the scored actions wins | the four of [D9](#d9--picking-is-a-policy-and-so-is-not-changing-your-mind) |
| `IWorldSensor` ✅, `ITargetSensor` ✅, `IGlobalWorldSensor` ✅, `IGlobalTargetSensor` ✅ | how the world reaches the blackboard | delegates and constants in `Vixen.Ai`, the clock; nearest-with-component, distance-to-nearest, centre-of and count-of in `Vixen.Ai.Nodes`; the perceived count and the nearest perceived in `Vixen.Ai.Perception`. **The other three added in [P9](#p9--the-seams-twice-and-the-sample--07-em-)** — § D13 promised four kinds and P1 shipped one |
| `IOcclusionTester` ✅ | what stops sight | a `Vixen.Physics` raycast; open sightlines. **Added in [P3](#p3--perception--08-em)** — the trace was assumed to be a direct physics call |
| `IPerceptionGovernor` ✅ | how often one listener senses | fixed rate; distance LOD in three bands. **Added in P3**, because `IAgentGovernor.Plan` sees a tick and a population and must not grow a position |
| `IReplanPolicy` ✅ | when GOAP thinks again | reactive, proactive, manual |
| `IActionCostModel` ✅ | what an action costs to reach | flat, straight-line, and a navmesh query in `Vixen.Ai.Nodes`. ⚠ Budgeted rather than hierarchical — Vixen bakes no coarse graph, as P4 already recorded |
| `IAgentGovernor` ✅ | who gets ticked this frame | round-robin with a floor; unbounded (for tests). ⚠ Distance LOD is `IPerceptionGovernor`'s, above |
| `IPerceptionFilter` ✅ | who may perceive whom | team affiliation, always, a delegate |
| `IBlackboardBinding` ✅ | how a sense's result becomes keys | the target/location/age triple; a count and a flag, which names no target at all |
| `IQueryGenerator` ✅, `IQueryTest` ✅ | environment queries | grid, circle, donut, cone, current location, composite and a delegate; entities-with-component in `Vixen.Ai.Nodes`. Distance and dot, plus trace, overlap, path length and navmesh projection in `Vixen.Ai.Nodes`, and a delegate. **Added in [P8](#p8--environment-queries--10-em-)** |
| `IScoredCandidateSet<T>` ✅ | what "the same scorer" means | `UtilitySet` and `EnvironmentQuery`, which is [D14](#d14--an-environment-query-is-the-utility-scorer-with-a-different-host) made checkable rather than stated |
| `IFactorSource` ✅ | how the shared scorer reads a candidate's factors | a utility action's considerations, streamed and stopped at the first zero; and `FactorSpan` over a query's collected factors, because filtering and scoring are interleaved down one list and a query cannot stream. **Second one added in P9** |
| `IGoapWorldSource` ✅ | where a GOAP world key's number comes from | a delegate, a constant, a blackboard key |
| `IGoapTargetSensor` ✅ | where a GOAP action happens | a delegate, and — since P9 — one of § D13's own target sensors, which is what "one implementation and two front ends" was always claiming |

---

## Part 5 — the editor

`Editor/Vixen.Editor.Ai`, registered in `StandardEditors` like every other asset editor, one document
per asset kind.

### The behaviour-tree editor — the mandatory one

| Panel | What it is |
|---|---|
| **Canvas** | the tree, top-down. Composites as boxes with an ordered child row, decorators stacked above a node and services below it — Unreal's arrangement, because it is the one that reads |
| **Execution index** | a badge on every node's header. It is the priority order, per [D5](#d5--child-order-is-authored-data-not-an-x-coordinate), and an author who cannot see it cannot reason about aborts |
| **Blackboard** | the key list: add, rename, delete, retype, with the type picker restricted to [D1](#d1--the-blackboard-is-a-compiled-key-table-not-a-dictionary)'s six. A rename rewrites every reference in the open document, which is why keys are referenced by index in the compiled form and by name in the file |
| **Inspector** | the selected node's settings, generated from its declaration the way doc 34's `GoalKindSchema` generates a goal's — so a `Cooldown` shows a duration and a `Blackboard` decorator shows a key picker and a comparison, with no per-node editor code |
| **Search-to-create** | `NodeSearch`'s ranked popup, filtered by what may attach where: dropping on a composite's child row offers composites and tasks, dropping on a node's decorator strip offers decorators |
| **Diagnostics** | `NodeDiagnostic`s from the compiler, clickable to the node |
| **Abort scope** | ⚠ selecting a decorator with an observer **draws the region it can interrupt**, shaded. This is the payoff for [D6](#d6--an-abort-is-a-range-test-and-it-happens-at-a-safe-point)'s scoped rule: the rule is drawable, so it is teachable |

**Live, in play mode** ✅ *P7 and the P9 sweep*: the active path highlighted, each node tinted by its
last result, the blackboard panel showing live values, and **breakpoints on nodes** — Unreal has them
and they are the difference between reading a tree and debugging one.

⚠ **P7 built the panel and left the canvas, and the sweep after P9 is what noticed.** The agent
debugger listed the active path as text and the tree it was about was drawn beside it, untinted —
which looked finished from the panel that existed. `BehaviorTreeProjection.Live` is the missing half:
four accents, and they are four different facts — `active` is the node running now, `path` is what is
open above it, and `succeeded` / `failed` are what a node last returned. An **aborted** node records
`failed`, because "why did the thing I was watching stop" is the question the tinting exists to
answer.

⚠ **The per-node results are off until a panel asks.** `BehaviorTreeInstance.Trace` allocates one
array the first time it is turned on and never again, and it is deliberately *not* in the memory
block — the block is sized at load for every agent in the game and this is wanted by one of them at a
time.

⚠ **A breakpoint's scope is the abort scope**, which is why the two are described together: a
breakpoint on a composite stops when anything inside it becomes the active node, so the region the
overlay already shades is exactly the region a breakpoint catches.

### The utility editor — a table and a curve, not a graph

A utility set is a **list of actions, each with a list of considerations**. It has no edges, and
drawing it on a canvas would be a canvas whose wires all run from a column of inputs to a column of
actions and carry nothing.

So: a two-pane table — actions on the left with their live scores as bars, the selected action's
considerations on the right — and under it `Vixen.Ui.Controls.Advanced.CurveEditor` showing the
selected consideration's response with the **current input value marked on it**. That last detail is
the whole tool: "why is this scoring 0.2" is answered by seeing where on the curve the agent is
sitting. In play mode the bars and the marker are live for the selected agent.

⚠ **The live half was claimed by P5 and built in the P9 sweep.** `UtilitySetView.Follow` takes the
debugger's model; the bars become the agent's own scores and the readings table becomes the agent's
own inputs, so the curve says where *it* is sitting. ⚠ **Only the winner's factors are live**, and
that is the snapshot's shape rather than an oversight: a capture records every candidate's score and
only the chosen one's axes, because scoring every action's every axis for a panel would be the cost
the decision interval exists to avoid.

### The GOAP editor — an authored table, and a derived graph

Goals, actions, world keys and target keys are authored as **tables**; conditions and effects are
rows on an action.

The **graph is derived and read-only**, and this is the point at which "the node editor is mandatory"
has to be answered honestly: crashkonijn ships a *GraphViewer*, not a graph editor, and that is
correct. The edges of a GOAP graph are not authored — they are *computed* from which effects satisfy
which conditions. Drawing them by hand would be authoring the same fact twice, and the two copies
would disagree the first time somebody edited a condition.

So the viewer shows the derived graph, and in play mode it shows the live search over it: the chosen
goal, the plan, each node's condition states from current world data, and the actions that were
considered and rejected with why. It is drawn on `NodeCanvas` and it has no command stack — which
`NodeGraphView` already supports, since *"no stack means read-only"*.

⚠ **P6 built the plan highlight; the condition states and the rejections landed in the P9 sweep.** A
condition is drawn `holds`, `unmet`, or **nothing at all** — three states, because "nobody is running
this domain" drawn as "false" would tell an author every condition was failing when in fact nothing
had asked. And `GoapPlanner.Traced` is where a search writes down what it turned down and why:
conditions unmet, not capable, already in the chain, too deep. ⚠ **Null by default**, so a resolve
running on a worker thread inside a per-frame budget pays one reference check per rejection rather
than an allocation and a write per node for a panel nobody has open.

### The environment-query editor ✅ *P8*

A list — generators, then tests in order — with each test's purpose, curve and clamping inline, and a
**preview in the scene view**: the generated points, green through red by score, filtered ones
crossed out, with a table of per-test contributions for the selected point. Unreal's testing pawn,
minus the pawn; the preview runs from the editor's own selection.

⚠ **The running order is a number on every test row, and reordering is a gesture.** It is the one
thing this editor has that the utility table does not, and it is the one that matters: a filtering
test rejects a point and everything below it is skipped, so where a trace sits in the list is the
difference between four hundred raycasts and forty.

⚠ **The curve control is the utility editor's, unchanged.** § D14: a test's scoring equation *is* a
consideration's response curve, so an author who has tuned one has tuned the other. The preview is
drawn by `QueryPreview` in `Vixen.Ai.Diagnostics` rather than by the editor, so the same call draws an
authoring run and a running agent's last query.

### Shared ✅ *P7*

The **agent inspector**, in the scene view: select an entity with an `AiAgent` and get its planner,
its current action, its blackboard and its perceived list, with a button that opens the running asset
in the editor already scrolled to the active node.

⚠ **The button <i>reports</i> what to open rather than opening it**, through an event carrying the
asset's name and the live node's index. This panel knows an agent's `Symbol` and where it is in its
tree; it does not know where the project keeps its files or which host owns the tab strip, and a view
that reached for a document service would be a view that cannot be tested without one.

⚠ **It is `AgentDebugModel` plus `AgentDebuggerView`, and the model holds no `Control`.** Which agent
is selected, what the log says, what is visibly wrong with it and whether a breakpoint is set are all
asserted by tests that stand up no window — the bargain `BehaviorTreeModel` already makes. And it
takes its picture either from a local `AiSystem` or from one that arrived over
[D17](#d17--ai-runs-on-the-authority-and-the-client-is-never-told-the-tree)'s channel, which is what
makes debugging a dedicated server the same tool rather than a second one written later and worse.

---

## Part 6 — the phases

Ten, ~9.6 engineer-months. P0–P2 are the spine; nothing after P2 blocks anything else except P8 on
P5.

✅ **All ten are built.** Every exit criterion in this section is a measured number rather than an
opinion, and the deviations from what each phase said it would do are recorded against the phase that
made them rather than collected here.

### P0 — the substrate — 0.8 em ✅

`Symbol` lifted to `Vixen.Core`. The blackboard: layout compilation, the six types, set/unset bits,
per-key versions and observer lists, `SharedBlackboard`. `IAgentAction`, `AgentContext`,
`ActionStatus`. The `AiAgent` component, the memory pool, `AiSystem` with declared access, and
`IAgentGovernor` with the round-robin and the floor. The debug record type all three planners fill.

**Exit:** a hand-built agent with one action runs under the governor at 10 000 entities with **zero
steady-state allocation**, measured, and the governor's schedule is a pure function of tick and
index — asserted, not assumed.

**Built.** `Core/Vixen.Ai`, 71 tests, both exit criteria met and measured. Four things are worth
recording because they were decisions rather than transcription:

- ⚠ **A version bumps only when the value actually changed**, which [D1](#d1--the-blackboard-is-a-compiled-key-table-not-a-dictionary)
  did not say and the testing table's *"a version increases iff a value changed"* did. The looser
  reading is the one somebody writes first and it destroys [D7](#d7--a-tick-is-not-a-traversal): a
  service writing its result every tick would abort every decorator observing it, for ever.
- ⚠ **The memory pool is paged, not one growable arena**, and that is a correctness fix rather than
  an optimisation. A doubling arena moves every byte in it, silently invalidating every
  `Span<byte>` a caller holds — which for a system that resolves a block and then ticks an action is
  a use-after-free with no symptom until it has one.
- ⚠ **A governed agent is handed the time since *it* last ticked, not the frame's.** Nothing above
  said so, and without it [D16](#d16--the-planner-is-a-job-and-the-budget-is-per-frame-not-per-agent)'s
  budget runs every `Wait(2 s)` at eight seconds and does it silently. It costs one float add per
  agent per frame, which is nothing beside a decision.
- ⚠ **`MaximumInterval` outranks `Budget`, and the report says when it did.** Doc 34's governor
  lesson is about *fairness* within a budget; this is the case above it — a population that cannot
  fit inside the interval gets a wider window and an over-budget plan, because an agent that reacts
  eight seconds late is a bug report about the AI rather than a saving.

`Vixen.Ai` references `Vixen.Core`, `Vixen.Core.Mathematics`, `Vixen.Core.Threading` and
`Vixen.Ecs` — **not** `Vixen.Engine`, which the reference set above names because P4's world-facing
tasks want it and those live in `Vixen.Ai.Nodes`. `AiLayeringTests` asserts both halves: nothing
above `Core/`, and nothing outside those four. It is the fallback [Testing](#testing) describes and
is meant to be deleted by the commit that adds the gate line.

### P1 — behaviour trees, runtime — 1.2 em ✅

The compiler from `BehaviorTreeAsset` to `BehaviorTreeTemplate`: pre-order layout, `LastDescendant`,
memory offsets, decorator and service ranges. The stepper: active node, event-driven ticking,
deferred aborts, the three observer modes and the scope rule. Every composite, every decorator and
every service and task in [Part 3](#part-3--the-node-library) that lives in `Vixen.Ai`. Subtrees.

**Exit:** 1 000 idle agents on a 10-node tree cost **less than a per-frame traversal costs for 1 000
agents on a 1-node tree**, measured both ways in the same benchmark; and an abort-ordering property
test over randomly generated trees asserts that the node that ends up active is always the
lowest-index runnable one.

**Built.** Both exit criteria met, in one test each. A thousand agents on a ten-node tree visit
**zero** nodes across sixty settled frames, against 60 000 for a traversal of the *one*-node tree
beside it in the same test — and the same population allocates zero bytes a frame. Five composites,
thirteen decorators, the `UpdateBlackboard` service over `IWorldSensor`, eight tasks, and both
subtree forms. 144 tests.

⚠ **The exit criterion's wording and [D6](#d6--an-abort-is-a-range-test-and-it-happens-at-a-safe-point)'s
scope rule are in tension, and the property test is what found it.** *"The lowest-index runnable
one"* is Unreal's reach; the scoped rule adopted two paragraphs earlier is Unity's, under which a
decorator reaches the siblings under its own parent composite and no further. So a condition that
becomes true **deep inside a branch the agent has already walked past does not pull it back** — that
composite is not open and nothing there is listening. This is the documented cost of the drawable
rule rather than a defect, and the property test is exact about it: a second instance walks the same
blackboard from scratch, and where the two disagree the test **requires the disagreement to be
explained** by the scope — a disagreement about a direct child of a shared composite fails. Stated
here because a reader of the exit criterion alone would expect the wider rule.

Four more things were decisions rather than transcription:

- ⚠ **A static subtree is spliced at compile time, not pushed at run time.** Unreal keeps a pushed
  instance; splicing is what preserves *"pre-order index is priority"* across the boundary, so a
  decorator in the parent can abort a branch inside the child and the range test still means
  something. `RunSubtreeDynamic` names its tree from a key, cannot be spliced, and pays exactly that
  price — it gets an instance of its own and is opaque to the parent.
- ⚠ **A continuous condition fails its branch; an observer restarts its scope.** The two look alike
  and are not. A time limit, a tag cooldown and `KeepInCone` go false with nobody writing a key, so
  they are re-tested each step — and restarting the parent from child zero would walk straight back
  into the branch, because the decorator's clock resets when the branch ends. That is an infinite
  re-entry, and it is the shape a `TimeLimit` takes. Failing the branch lets the composite *resume*,
  which is what "fails the branch after *n* seconds" means.
- ⚠ **A step descends until something is actually running, under a bounded number of transitions.**
  One node per step reads as correct and is not: under a governor at one tick in sixteen, a sequence
  of three instant tasks would take three-quarters of a second to write three blackboard keys. The
  bound is what stops a forever-loop over instant tasks from hanging the frame instead, and hitting
  it is reported rather than swallowed.
- ⚠ **A service runs on entry, inside the descent, rather than on the next pass.** Zeroing its timer
  looks equivalent and is not — services are ticked before the descent, so a branch just chosen would
  spend its whole first step deciding on data an interval old.

⚠ **And one bug worth recording, because it looked like it worked.** `AiSystem` decided whether an
agent had already joined by testing its memory handle — but a tree agent has none, since the block is
sized by the template and owned by the instance. Every tree agent therefore looked like a stranger on
every step, re-joined, and had its instance reset: the tree restarted from the root sixty times a
second while behaving plausibly. The idle-cost criterion is what caught it, which is the argument for
having written it as a number.

### P2 — the node editor — 1.5 em ✅

`BehaviorTreeModel`, the document, the importer, and the editor: canvas with attachments and badges,
top-down layout, reorder-drop, the blackboard panel, the generated inspector, search-to-create, the
diagnostics list, the abort-scope overlay. The five `NodeCanvas` additions.

**Exit:** a tree of thirty nodes is authored end to end with no text editing, saved, reopened
identically — a save/load/save round trip is a no-op in the diff, `NodeGraphAsset`'s rule — and the
`CheckDocs` page for it is written from the editor rather than from the code.

**Built.** `Core/Vixen.Ai`'s `BehaviorTreeContent` and `BehaviorNodeSchema`,
`Editor/Vixen.Editor.Ai`, `Editor/Vixen.Editor.AssetEditors/Ai`, the `.vxbt` importer, and the canvas
additions. The exit criterion is one test: thirty nodes put there by thirty gestures — nodes,
decorators, a service, five keys, a reorder and a layout — saved, reopened, and asserted equal both as
text and as a walk. The guide page is written from the editor.

⚠ **The `.vxbt` is a type name and a bag of named strings, not a polymorphic tree.** A discriminated
hierarchy in a text asset is a tag people have to get right by hand, and binding one needs reflection
at load, which ADR-002 rules out. `BehaviorNodeSchema` is what resolves the name — and it lives in
`Vixen.Ai` rather than in the editor, because a game loading a tree at run time needs the same table.
It carries the label, the category, the per-field tooltip and the default, which is what lets the
inspector be generated the way doc 34's `GoalKindSchema` generates a goal's and what lets a project's
own node appear in the popup without this repository knowing about it.

⚠ **The artefact is the tree's data, not a compiled template.** A `BehaviorTreeTemplate` holds live
decorator objects and indices into a registry a game builds at start-up, so there is nothing there to
write bytes for. What the importer writes is the content; `BehaviorTreeContentCompiler` is the one
direction from it to an asset, and the asset then goes through the *same* compiler a tree built in
code does — a file cannot produce a template a hand-built tree could not.

⚠ **Five additions were asked for; four landed on the canvas, one landed here, and a sixth was
needed.** Attachment rows, a header badge and an overlay layer are `NodeCanvas`'s, and reorder-drop is
the canvas reporting a drop that the editor turns into a reorder — because a canvas cannot know that a
tree's child order is a list where a dataflow graph's is nothing at all. **The top-down layout is
`BehaviorTreeLayout` in `Vixen.Editor.Ai`, not `NodeGraphLayout`**: a tree layout needs the tree, and
a longest-path layout over the projection would take sibling order from the wires rather than from the
list that holds it. The sixth is `NodeCanvas.Orientation` — a wire in a dataflow graph leaves the
right edge and in a tree it leaves the bottom, which is an anchor and a curve rather than a rotation.

⚠ **Undo is a snapshot, and the first `Do` installs nothing.** A tree is tens of nodes, so a snapshot
is a few kilobytes of strings and *every* gesture is undoable by construction — a reparent, a
reorder, a key rename that rewrote forty references — with no chance of an inverse that puts back
four of the five things it changed. The subtlety cost a hung test: installing a copy on the first
`Do` changes nothing about the document's value and everything about its **identity**, so every node
a caller was holding points into an orphaned tree and the next edit through it goes nowhere,
silently.

⚠ **And one trap worth naming because it is a language rule rather than a design one.** A record
struct's `new()` is the *zero* value; its primary-constructor defaults only apply when somebody names
the constructor. `BehaviorLayoutOptions` had them and a caller who passed nothing got a row height of
zero, which stacks a whole tree on one line and reads as the layout being broken.

### P3 — perception — 0.8 em ✅

The five senses, `AiStimuliSource`, affiliation, the lose-sight radius, the perceived list with
stimulus age, `IBlackboardBinding`, and the rate governor with distance LOD.

**Exit:** 500 listeners and 500 sources hold a frame budget with the broad phase and miss it without
it — both numbers recorded — and a sight test asserts occlusion against real `Vixen.Physics`
geometry rather than against a mock.

**Built.** `Core/Vixen.Ai.Perception`, a second assembly rather than a folder in `Vixen.Ai`: this is
the half that needs `Vixen.Engine` for where things are and `Vixen.Physics` for what is between them,
and a game that wants trees without a solver links `Vixen.Ai` and stops.
`PerceptionLayeringTests` asserts the list in both directions. 38 tests.

**Both numbers, on the machine that ran them, in Release** — five hundred listeners against five
hundred sources with every one of them sensing on the same tick:

| | Examined | Measured | 4 ms budget |
|---|---|---|---|
| With the broad phase | **7 960** | 2.10 ms | held |
| Without it | **250 000** — `listeners × sources`, by construction | 5.71 ms | missed |

⚠ **The budget is recorded and the *work* is asserted, which is a deliberate softening of the exit
criterion.** A millisecond threshold is a different number on every machine and this repository is
Debug locally and Release in CI, so it would not even be one number here — P1's cost test settled the
same question the same way. What the test asserts is the claim [D15](#d15--perception-is-a-system-and-its-cost-is-a-schedule)
actually makes: a scan examines the product by construction and a grid examines a number set by the
query radius and the local density. Both times go into the message whether it passes or fails, so the
figure above comes out of a run rather than out of this document.

⚠ **The broad phase is over the stimuli sources, not the physics broad phase, and D15 says
otherwise.** A source is not necessarily a body — a noise, a camera, a marker and a corpse are all
perceivable and none of them has a collider — so a Jolt query would be a broad phase over the wrong
set, whose cost is the level's collision geometry rather than the handful of things worth looking for,
and whose results then need mapping back to entities and filtering down again. The physics world is
still where the occlusion trace goes, which is the expensive half. The grid is also **two-dimensional**,
over X and Z: cells in Y would triple the cells a query walks for a level where everybody is within a
few metres of the same height, and the distance test is still in three dimensions so a tall level
costs a longer chain rather than a wrong answer.

⚠ **Two seams were added that [Part 4](#part-4--the-seams) does not list, and one it does list moved.**
`IOcclusionTester` because the document assumed the trace was a direct physics call, which makes
`SightSettings.Occlusion` a flag with one meaning and puts a `PhysicsWorld` in the constructor of a
system a gridless game has no use for — and a game with smoke, fog or one-way glass needs sight
blocked by things that are not collision geometry. `IPerceptionGovernor` because Part 4 files
distance LOD against `IAgentGovernor`, whose `Plan` is handed a tick and a population and nothing
else: `AgentSchedule` is eight bytes on purpose, distance needs a position per listener, and that
interface should not grow one.

⚠ **The team relay needed a fourth bound the document does not name.** Copying an ally's whole current
list makes it cost `listeners × allies × targets`, which measured at **more than twice the entire rest
of the pass** at five hundred agents — 6.3 ms of an 8.8 ms frame. It is now one target per ally, the
freshest, and that is the better model as well as the cheaper one: an ally shouts *"contact, north"*,
which is one thing, rather than synchronising its memory with everybody in earshot. The other bound
was already there and is load-bearing: **a relay is never relayed**, or a line of guards passes a
sighting down the level one hop a pass and the whole map wakes several seconds later with nobody
having seen anything.

⚠ **An event is consumed by sequence number, not by clock, and the clock version is wrong in a way a
test caught.** The clock only advances inside a step, so an event reported *after* a pass in the same
frame carries exactly the clock that pass recorded — and a listener comparing clocks decides it has
already heard a gunshot that has not happened yet. The first version of the loudness test failed on
precisely that.

⚠ **And a defect in P0 that this phase found, in the one place nothing had checked.**
`AgentRandom.Hash(entity, seed, salt)` combined the entity with the seed by `^`, and every caller in
the engine seeds a stream with `AgentRandom.SeedOf(entity)` — which for a freshly created entity *is*
`Hash(id)`. So `Hash(id) ^ Hash(id)` was zero and **every agent in the world drew the same number
from its supposedly private stream**: one shuffled selector picked the same child in a thousand
agents, and a jittered interval put the whole population on one frame while looking like it had spread
them. Found by spreading forty listeners over ten frames and watching all forty land on frame five.
`AgentsSeededFromTheirOwnEntitiesDoNotAllDrawTheSameNumber` is the regression.

⚠ **A node whose implementation is in another assembly needs a factory, which `Vixen.Ai` did not
have.** [Part 3](#part-3--the-node-library) files `PerceivedTarget`, `NearestPerceived` and
`MakeNoise` under `Vixen.Ai.Perception`, and `BehaviorNodeSchema` lives in `Vixen.Ai` so that a game
loading a tree and an editor authoring one read the same declarations — but `Vixen.Ai` cannot
construct a type it cannot reference, so the schema could describe a node the compiler would refuse.
`BehaviorTreeResolver.AddDecorator`/`AddService`/`AddTask` and `BehaviorBuildContext` close that, and
the shipped nodes are matched first so a project cannot shadow one and quietly change what every
existing file means.

### P4 — nodes over the world — 0.6 em ✅

`Vixen.Ai.Nodes`: movement, rotation, patrol, path existence, animation, sound. The only assembly
with wide references, and nothing depends on it.

**Exit:** an agent patrols a baked navmesh, notices the player, chases and gives up — as a test with
no window, asserting positions.

**Built.** `Core/Vixen.Ai.Nodes`, 30 tests, and the exit criterion is one of them.
`NodeLayeringTests` asserts both halves of the reference rule — the list this may reference, and that
**no loaded assembly references it**, which is what makes the wide list containable.

⚠ **The exit criterion is authored rather than driven, and that is what makes it worth having.** The
tree is a `.vxbt` compiled through `BehaviorTreeContentCompiler`, the route is a `PatrolRoute`
component, the sight is a `PerceptionConfig`, and the only thing the test does per frame is move the
player and step three systems. A failure is therefore a failure of the whole chain — sense, key,
abort, node, crowd — which is the chain P0 to P4 exist to make work together. **"Gives up" is one
`Blackboard` decorator over one float key**: no timer, no branch holding a remembered position, and
nothing in the tree that knows what a sense is.

⚠ **There is no hierarchical path query and `PathTest.Budgeted` stands in for one.** Part 3's row
names Unreal's three modes; a hierarchical query reads a coarse graph baked beside the mesh, Vixen
bakes no such graph, and a second navigation structure kept in step with the first is a bad trade for
one decorator. A search stopped at a node budget answers the same question with the same shape of
cost and is wrong only in the direction that makes an agent give up rather than walk into a dead end.

⚠ **`PatrolRoute` and `AiFocus` are components, which the phase list does not mention.** A route is
the level's data and a tree is an asset's: one `.vxbt` runs every guard in the game and each carries
the corridor it walks, so points on the task would mean a tree per route. `AiFocus` is Unreal's
focus, and its value is that everything downstream — a rotation, an aim offset, a head-look, a camera
— reads one place instead of each taking its own key.

⚠ **The move-set half of `PlayAnimation` is the state machine's, not a second node.** What plays is
the state's motion, which may be a clip, a blend tree or a `MoveSetMotion` picking from a move set. A
task that reached past the state machine into a move set would be a second way to drive an animator,
and the two would disagree about what is playing.

⚠ **And a defect in P2 that this phase found.** `BehaviorTreeContentCompiler` shared an action
between two identical tasks *within one compile* and re-registered it across compiles — so a game
compiling every `.vxbt` it ships against one resolver crashed at start-up on the second tree that
contained a `Wait(1)`. The action key was always meant to be the sharing mechanism; it now consults
the registry as well as the build's own table.

### P5 — utility — 0.9 em ✅

Considerations, the six curves, the weighted geometric mean with the zero rule, the four selectors,
inertia (commitment bonus, cooldowns, decision interval), the `.vxutility` asset, and the table +
curve editor.

**Exit:** the oscillation test — two actions tuned to within 2 % of each other, and an agent that
switches **fewer than 3 times in 60 s** with the defaults and more than 50 times with inertia
disabled. And the compensation test: adding a neutral consideration to an action does not change its
rank.

**Built**, in `Core/Vixen.Ai` beside the tree rather than in an assembly of its own — a utility set
reads a blackboard and chooses an action and needs nothing the tree does not. 27 tests here and 8
over the editor. **Both exit criteria measured: 0 switches with the defaults against 120 with inertia
disabled**, over sixty seconds of two actions a hair apart, one of them crossing the other once a
second.

⚠ **The compensation test is not the obvious one, and writing it exposed that.** Adding a
consideration scoring 1.0 to a geometric mean *raises* the score, so "adding a neutral consideration
leaves the score unchanged" is false and was never the claim. What compensation means is that
**the count is irrelevant**: six axes at 0.6 score exactly what one at 0.6 does, so an action is not
demoted for being tuned. The test says that three ways — the count, the rank against an action with
fewer axes, and the rank after adding a neutral one — and every one of them fails under a plain
product.

⚠ **Inertia is on the set and not on the selector, and the state is a struct for a reason.** A utility
set has two hosts: `AiSystem`, where it is the agent's whole planner and the memory is a managed
object beside the slot, and `RunUtilitySetTask`, where it is a leaf of a behaviour tree and
everything has to live in the `Span<byte>` that task was given. `UtilityState` is a plain struct so
that one implementation serves both — two would grow different inertia, which is the sort of
divergence nobody notices until an agent behaves differently depending on which planner is on top.

⚠ **`AiAgent.Tree` became `AiAgent.Asset`.** Three planners are three libraries but an agent runs
exactly one of them, and which is what `Planner` says — so a field per planner would be two that are
always meaningless, in a component whose whole rule is that it is a handle and a few numbers.

⚠ **The bucketed selector is a rank, not a group of the best-scoring action.** Taking the winner's
bucket and then the best inside it is `Highest` wearing a hat — the group would be chosen by exactly
the comparison the group exists to avoid making. What it does instead is take the **highest bucket
with anything scoring above zero at all**, which is what makes a guard being shot at unable to score
"drink coffee".

⚠ **A utility action names its task out of `BehaviorNodeSchema`, through the same factories and into
the same registry.** That is doc 37 § D2 made checkable rather than merely intended: two files that
both say `Wait(2)` share one action whether one of them is a tree and the other a set. It needed one
new seam — `BehaviorTreeContentCompiler.TryResolveTask` — and the resolver grew an input table beside
its sensor table.

⚠ **And a flaky test fixed in passing**, in P0's blackboard suite. `AWriteFromAnotherThreadIsRefused`
used `Task.Run`; xUnit runs a test on the thread pool, an `await` hands that thread back, and the pool
is then free to schedule the queued work item onto the very thread that opened the scope — at which
point the owner check is satisfied and nothing throws. It failed about one run in three for a reason
that had nothing to do with the blackboard.

### P6 — GOAP — 1.3 em ✅

World-key projection, conditions and effects, the graph builder, the A\* resolver on jobs with the
node budget, target keys and sensors, `MoveMode`, `IActionCostModel`, `IReplanPolicy`, capabilities
per agent type, and the derived plan viewer.

**Exit:** the pear test — the reference scenario every GOAP implementation is demonstrated with —
plus a **budget** test: an action set authored to blow the node limit fails with
`PlanFailure.BudgetExhausted` naming the goal, in bounded time, rather than hanging. And 64 agents
replanning on a 40-action set inside a stated frame budget.

**Built**, in `Core/Vixen.Ai` beside the other two planners, with the `.vxgoap` asset, the importer
and the derived viewer. 23 tests here and 8 over the editor. **All three exit criteria measured**: the
pear test plans `pick-up-pear` then `eat-pear` backwards from the goal; a 24-action tangle where every
action's condition can be served by every other fails with `BudgetExhausted` naming the goal after
exactly its 200 nodes; and 64 agents on a 40-action domain are answered four per step, sixteen steps,
with the frame's cost recorded rather than asserted for P3's reason.

⚠ **A search reads a snapshot and never the world, and that is what makes a resolve a job.** § D16
puts resolves on jobs because they are expensive and unbounded — and a search that reached into a
`World` or a `Blackboard` from a worker thread would be a data race the scheduler cannot see. So the
world keys are projected, the targets are sensed and the costs are computed at **submit**, on the
thread that owns the agent, and what crosses to the job is a few arrays of numbers. That also makes
§ D10's "the graph is built once" literally true of everything: the graph at construction, the costs
and conditions at submit, and nothing at all during the search.

⚠ **A plan is a chain, so an action with two unmet conditions is served one at a time.** The
alternative is a hyper-graph search over conjunctions, and it is not needed: § D11 commits only the
head, the head is by construction runnable now, and running it changes the world the next resolve
plans from. Stating it as a limitation would be stating § D11 as a limitation.

⚠ **An action may not appear twice in one chain**, or a domain where two actions serve each other's
conditions is an infinite descent — and the budget would report exhaustion for a domain with a
perfectly good two-step plan in it. `TwoActionsThatServeEachOtherStillResolve` is that test.

⚠ **And a bug this phase found in P5's own path, which P0 had too.** `AiAgent.Action` is zero until
something sets it, so a planner whose first resolve has not landed yet ran **whichever action happens
to be registered first**, every frame, and it looked exactly like a plan. `AiSystem` now asks the
planner whether it chose anything at all and runs nothing when it did not; the utility path returns
the same answer, so a set that vetoes everything before its agent has ever acted is an agent that
does nothing rather than one that does action zero.

⚠ **Two additions the phase list does not name.** `GoapCapabilities` needed a field on `AiAgent` — a
domain per capability set is a graph rebuild per permutation — and `BehaviorTreeResolver` grew a
world-source table beside its sensor and input tables, which is the third of three and the point at
which that type is plainly the game's resolution table for AI content rather than a tree's.

### P7 — the debugger — 0.8 em ✅

The `DebugDraw` overlay with its categories, the editor panels over the same records, breakpoints,
the visual log, and doc 13's remote channel for a dedicated server.

**Exit:** an agent misbehaving in a headless test is diagnosed from the recorded log alone, and the
overlay is asserted by a test with no window.

**Built**, in a new `Core/Vixen.Ai.Diagnostics` for the overlay, in `Core/Vixen.Ai/Diagnostics` for
everything that needs no engine, and in `Vixen.Editor.Ai` / `Vixen.Editor.AssetEditors` for the
panel. 14 tests here, 9 over the overlay and 6 over the panel. **Both exit criteria measured**: an
agent with its inertia turned off is reported as `Flapping` from a recorder the world and the system
have already gone out of scope around, and an agent stuck on one failing action is named along with
the action; and the overlay's whole surface — labels, cones, both sight radii, the range cap, the
count cap and the selected-agent exemption — is asserted against a bare `DebugDraw` with no device
anywhere in the file.

⚠ **The three planners agree on a shape before anything looks at them, and that is what makes "one
debug surface" mean something.** § D20 asks for one overlay rather than three, and the way to fail at
that is to write one class with three branches in every method. `AiAgentSnapshot` is a flat list of
`AiDebugRow` — a section, a name, a formatted value, a number and whether it is the live one — and an
active tree path, a table of scored candidates, a plan's steps, a blackboard and a perceived list are
*all* of that shape. So the overlay draws one kind of line, the panel builds one kind of table, and
the wire writes one kind of record. Five shapes would have been five of each.

⚠ **A capture is a picture and not a view**, which is what lets the same object serve a panel that
redraws next frame, a socket, and a test comparing two instants. It holds no `World`, no `Blackboard`
and no template: strings and numbers, taken on the thread that owns the agent — the rule
`GoapSnapshot.Take` already follows, for the same reason.

⚠ **A debugger must not move the bug, and that is not free to arrange.** Photographing a utility agent
means re-scoring its set, and the obvious call — `Choose` — advances the decision clock and starts
cooldowns, so an overlay left on would change what the agent did. The capture goes through `Score`,
which takes the state by `ref readonly`, and reads a GOAP plan rather than re-resolving one.
`TakingASnapshotDoesNotChangeWhatTheAgentDecides` is ten captures and an unchanged decision count.

⚠ **A breakpoint's scope rule is the abort rule**, deliberately. A breakpoint on a composite stops
when anything *inside* it becomes the active node, which is § D6's containment test and the region
P2's editor already shades. One rule an author can see beats two they have to remember apart. And a
breakpoint stops the *agent*, not the game: there is no world to freeze from `Vixen.Ai`, and freezing
one would be the wrong tool — what somebody wants is the one agent held with its state intact while
the level carries on.

⚠ **The visual log is the recorder, and there is no second ring.** P1's `LogTask` already writes into
`AgentDebugRecorder` with a comment saying P7 would build the log over the same records, and building
a parallel `AiLog` with its own entries would have made that comment false and given a debugging
session two places to look. What P7 added is the *reading*: `AiDiagnosis` over the ring, and the
panel's list.

⚠ **The diagnosis reports symptoms and never causes**, because a debugger that guesses is one people
learn to disbelieve. Five symptoms, each a shape in the record stream rather than a fact about a tree
— which is what lets one reader serve all three planners and is what makes "diagnosed from the
recorded log alone" a criterion rather than an aspiration. Every finding carries the count it is built
from and the ticks it spans. The thresholds are arguments rather than constants, because whether four
switches in a window is a bug depends on the window and on the game.

⚠ **A fifth assembly, and the argument is the one that made the third and fourth.** `DebugDraw` is
`Vixen.Engine`'s and `Vixen.Ai` depends on no engine, so the drawing is `Core/Vixen.Ai.Diagnostics`.
It references perception as well, because § D20's table has a *senses* row for all three planners and
the cones belong beside the active path rather than in a second overlay. The namespace stays
`Vixen.Ai.Diagnostics`, which `Vixen.Ai` had already opened: one surface, one namespace, and which
assembly a type lands in is a packaging fact rather than something a caller should think about.

⚠ **The remote channel is built as far as doc 13 has built the transport, and no further.** § D17's
one exception is a request and a response for one agent's state, gated behind the same switch doc 13's
remote inspector uses. `AiDebugChannel` is that — the message pair, the wire format, the version
check, and a reader that refuses every prefix of a well-formed message rather than reading past a
length prefix. What it does *not* do is open a socket: `InspectorProtocol` lives in
`Editor/Vixen.Editor.Debugger` and has no build-side host yet, so wiring this into that transport is
doc 13's row and not this one's. The channel is testable and correct in isolation, which is the part
doc 37 owed.

⚠ **And a defect in P5's and P6's own editors, found by constructing a view for the first time.**
`Button.Text` sets the *button's* text, and a button already has a label child — so
`Build.Text = "Compile"` throws "node has children and cannot also measure itself" the moment the view
is created under a UI document. `UtilitySetView`, `GoapDomainView` and `BehaviorTreeView` all had it,
because until P7 no test had ever built one of those views; `Label` is the property that was meant.
All three are fixed here.

### P8 — environment queries — 1.0 em ✅

Generators (grid, circle, donut, cone, entities-with-component, current location, composite), tests
(distance, dot, trace, pathfinding, overlap, project, tag) over
[D14](#d14--an-environment-query-is-the-utility-scorer-with-a-different-host)'s shared scorer, the
`.vxquery` asset, the list editor and the scene preview.

**Exit:** "the best cover point with line of sight to the target" is one authored query, and the same
scorer object serves it and a utility set — asserted by construction, not by comment.

**Built**, in `Core/Vixen.Ai/Queries` for everything that is arithmetic, `Core/Vixen.Ai.Nodes` for the
four tests and the generator that need the world, `Core/Vixen.Ai.Diagnostics` for the preview, and the
`.vxquery` asset with its importer and its list editor. 16 tests here, 6 over the world-facing half, 4
over the preview and 9 over the editor. **The exit criterion is measured on both halves**: an
eight-metre grid with a sight filter, a reach filter and two scoring tests picks the nearest point
that can see the target and rejects everything behind the wall; and the shared scorer is asserted
three ways — both hosts implement `IScoredCandidateSet<T>`, one `IResponseCurve` **instance** scores an
action and a point in the same test with `Assert.Same` on both sides, and `UtilityScoring.Combine`
forwards to `CandidateScoring.Combine` rather than repeating it.

⚠ **"The same scorer" had to be made true rather than asserted about.** Before this phase there was
one mean, in `UtilityScoring`, and writing a second one for points would have made § D14 false the
moment somebody tuned one of them. So the combining code moved to `CandidateScoring` and everything
forwards to it, `UtilityAction.Score` included — which meant rewriting the streaming "stop at the
first zero unless the detail was asked for" loop as a generic over a `ref struct` reader, so that a
utility action and a query point pay nothing for sharing it.

⚠ **`IScoredCandidateSet<T>`'s factor counts are per candidate and not per set**, which is the one
place the two hosts genuinely differ: every point in a query runs the same test list, and every action
in a utility set has its own considerations. An abstraction shaped like the query would have made the
utility set implement it by lying.

⚠ **A test's purpose is Unreal's three and the distinction is load-bearing.** "Must have line of
sight" and "prefer more cover" are the same reading used two ways, and a pipeline with only scoring
turns the first into a zero that any other test can outvote — an agent standing in the open because
the spot was otherwise excellent. A reading of `NaN` filters rather than scoring zero, because "there
is no path to here" and "the path to here is long" are different facts.

⚠ **Test order is the file's and the runtime does not reorder it.** A filtering test rejects a point
and everything below it is skipped, so a four-hundred-point grid with a trace at the top is four
hundred raycasts and the same list with a distance filter first is a few dozen. A runtime that
reordered would make a query's cost unpredictable and its behaviour depend on a heuristic nobody can
see — so the editor shows the running order on every row and makes reordering the one gesture the
utility table does not have.

⚠ **A weight pulls a factor toward one rather than multiplying the score**, which is not the obvious
implementation and is the only one that survives the geometric mean. A multiplier of 2 is not in
`[0,1]`, and one of 0.5 would be a permanent half-veto on an otherwise perfect point — the mean's
whole property, that the count of factors is irrelevant, would be gone.

⚠ **`Vixen.Ai.Nodes` grew a reference to `Vixen.Physics`, and `NodeLayeringTests` caught it before I
wrote it down.** The two most useful tests a query has are "can I see the target from here" and "is
there anything solid beside me", both of which are a physics query. The list is still contained
because nothing depends on that assembly, and a game that wants queries with no solver still gets the
generators, the distance test and the dot test out of `Vixen.Ai`.

⚠ **Four of doc 37's seven tests landed and three did not, for three different reasons.** `Trace`,
`Overlap`, `PathLength` and `OnNavMesh` are built; `Dot` is in `Vixen.Ai` beside `Distance`;
**pathfinding is `PathLength` and its cost is stated rather than hidden** — a search per point, which
is what a filter above it exists to make affordable, and its corridor length stands in for a funnelled
one because a funnel per point would be a second search per point. **`Tag` is not shipping**, and it
is [Part 3](#not-shipping-and-why)'s standing answer: a gameplay tag is doc 28's, the `Core/` ⇸
`Gameplay/` rule means the test cannot compile here, and a project registers its own by name in three
lines.

⚠ **And the `default(BlackboardKey)` trap, for the third time in this document.** `QueryBinding`'s
optional keys were plain `BlackboardKey`s, so a binding that named no entity key named key *zero* —
which in this node meant clearing the very key it had just written, one line after writing it. They
are `BlackboardKey?` now, the way perception's bindings became in P3. The test that caught it is the
one that asserts the service clears a stale answer.

### P9 — the seams twice, and the sample — 0.7 em ✅

Every interface in [Part 4](#part-4--the-seams) with a second implementation differing in shape, and
`SeamTests` asking the assemblies rather than trusting review — doc 34's P9, verbatim. Plus a sample:
one level, three agent kinds, one of each planner, sharing one perception model.

**Exit:** the sample's three agents are visibly different and share every system, and the seam test
fails if any interface has one implementation.

**Built.** `SeamTests` in `Core/Vixen.Ai.Nodes.Tests` — the only test project that can see all four
shipped assemblies, so a seam test cannot pass by not looking — a theory over **twenty-one**
interfaces, plus the sensor taxonomy this phase finished and the sample. 65 tests in that project now.
**Both exit criteria measured**: the seam theory fails on any interface with one implementation, and
it did, three times, on the day it was written; and the village's guard closes on the intruder, its
villager backs away to a refuge a *global* sensor told it about, and its scavenger ignores the
intruder and gets on with two-step plans — all three stepped by one `AiSystem`, choosing out of one
`AgentActionRegistry`, sensing through one `PerceptionSystem` with one config, reading one
`SensorSet` and walking one navmesh.

⚠ **The seam test found three gaps on its first run, which is exactly what it is for.**
`IGoapTargetSensor` had one implementation; `IFactorSource` had one; and § D13's taxonomy had one
interface of four. None of those was visible in review — the Part 4 table had ✅ against rows whose
second implementation was a delegate wrapping the first.

⚠ **§ D13 promised four sensor kinds and P1 shipped one, so P9 finished it.** `ITargetSensor`,
`IGlobalWorldSensor` and `IGlobalTargetSensor` are built, with a `SensorSet` that runs the globals
once a pass and the locals per agent, wired into `AiSystem` before the planner. **That ordering is the
whole point**: a global's answer is cached at the top of the pass, so two agents standing beside each
other cannot see different weather, and globals are applied before locals so that "how far am I from
the fire" can read the fire.

⚠ **Sensors run for the agents the governor named and not for every agent.** A sensor is a read of the
world on behalf of a decision, so an agent that is not deciding has no use for a fresh one — paying
for a thousand to serve the sixteen that will think is the cost § D16 exists to refuse. The globals
still run once a step, because their whole point is that they cost the same whoever is thinking.

⚠ **The bridge between § D13 and § D12 is one class and it was the missing second implementation.**
`GoapTargetSensors.Add(key, ITargetSensor)` lets a domain name one of the taxonomy's target sensors
directly — "the nearest apple to me" is one search whether a tree writes it to a key, a consideration
measures its distance, or a plan's action goes there, which is what § D13's "one implementation and
two front ends" was always claiming.

⚠ **A sample as a test rather than a `Samples/` project, and that is a deviation worth naming.** What
the exit criterion measures is that three agents are *visibly different* and *share every system*, and
both of those are statements about positions and object identity rather than about pixels. A graphical
sample would need a level, art and a renderer this document does not own, and it would assert none of
it. A `Samples/` entry remains a good addition on top of this rather than instead of it.

⚠ **And the sample deleted a symptom P7 had shipped.** `AiSymptom.NeverFinishes` reported an agent
that had run one action for the whole window — and a patrol between two waypoints, a `MoveTo` across a
courtyard and every other long action in a working game is exactly that. The log has no notion of
*progress*, so it cannot tell a guard walking its beat from one stuck against a wall. Two of the
village's three perfectly-behaved agents were reported, which is the failure P7's own healthy-agent
test warned about and dodged by using an action that finished every four ticks. It is gone.

---

## Testing

✅ **Every row has a test.** Two of them were written in the sweep after P9 — the plan named them and
nothing asserted them — and both are marked below with what reading of the row actually holds.

| Area | The test that matters |
|---|---|
| **Blackboard** | Property tests over random write/read/observe sequences: a version increases iff a value changed; an observer fires iff it registered; set/unset is independent of value |
| **Tree execution** | **The abort-ordering property test.** Randomly generated trees with randomly placed observers, driven by random blackboard writes, asserting the active node is always the lowest-index runnable node. This is the one that finds the bugs |
| **Tree determinism** ✅ | The same tree, the same input sequence, on two `World`s with different creation order, produces the identical sequence of active nodes. ⚠ **Read literally this asks for something [D18](#d18--determinism-is-a-property-of-the-decision-not-of-the-schedule) does not promise** — a stream is keyed on the agent's *own* entity, so two agents with different ids may legitimately draw differently, and a test that demanded otherwise would demand that the seed do nothing. The useful reading, and the one `BehaviorTreeDeterminismTests` asserts, is that an agent's decisions do not depend on **what else exists or in what order**: one world with forty other entities in it and one without walk the same tree the same way |
| **Template/instance** | 100 agents on one template, each driven to a different state, all asserted independently — the test that fails if any node keeps state on itself |
| **Node library** | A table-driven case per node: inputs, memory before, result, memory after |
| **Utility** | The compensation test (a neutral consideration does not change rank), the zero-veto test, the oscillation test, and curve evaluation against hand-computed values at the parameter extremes |
| **GOAP** ✅ | Plans compared against hand-solved optimal sequences on small graphs; the budget test; an unreachable goal fails rather than searching for ever; a mid-plan world change produces a different, still-valid plan. ⚠ **The last of those was the half a "throw the stale head away" test does not reach** and was written in the sweep after P9: discarding a broken plan is the safety property, and making a *new and correct* one out of the world as it now is, unprompted, is what the planner is for |
| **Perception** | Occlusion against real geometry; the lose-sight hysteresis asserted by walking a target out and back; affiliation filtering |
| **Scheduling** | Zero steady-state allocation across a whole frame of 10 000 agents, under `Measured`; and the governor's fairness — no agent starves over 1 000 frames |
| **Layering** | ⚠ **`Core/` must not reference `Gameplay/`** — the single rule this whole document exists to establish, and once the gameplay spine moves out of `Core/` it is one line in `Build.ArchitectureRules.cs` rather than a test. Until it does, the same rule as an assembly test over `Vixen.Ai*`'s references, deleted in the same commit that adds the gate line |
| **Editor** | Round-trip: author → save → load → save is a no-op in the diff. Compiler golden tests: a document compiles to a stable template dump. The abort-scope overlay against hand-computed ranges |

---

## Risks

| | Risk | Severity | Mitigation |
|---|---|---|---|
| A-R1 | **Abort semantics are subtly wrong and nobody notices for a year.** This is what happens to every behaviour-tree implementation, and the symptom is "the AI sometimes gets stuck" | ~~**High**~~ **Medium, after P1** | The property test is built and runs two hundred generated trees against a from-scratch oracle, and it earned its keep immediately: it is what surfaced the tension between this document's own exit criterion and its scope rule (recorded at [P1](#p1--behaviour-trees-runtime--12-em-)). The abort-scope overlay landed in P2 and [P7](#p7--the-debugger--08-em-)'s breakpoints reuse the same containment test, so the rule an author can *see* and the rule the runtime applies are one rule |
| A-R2 | **GOAP is too slow to ship and quietly gets cut** | **High** | The node budget is in the design rather than added after; the resolve is jobbed and sliced on `NavPathQueue`'s existing pattern; the exit criterion is a number. And doc 28 already scoped it — dozens of agents, not thousands |
| A-R3 | **The node editor is a year of work.** Three reference editors have a decade in them each | Medium | P2 is 1.5 em because the framework underneath is built and tested: canvas, search, inspector rows, commands with merging, clipboard, diagnostics. Five additions to `NodeCanvas` is the actual new surface. ⚠ If P2 overruns, it overruns on polish, and a tree is still authorable |
| A-R4 | **Three planners is three half-built things** | Medium | They share [D2](#d2--three-planners-one-action)'s action surface, [D1](#d1--the-blackboard-is-a-compiled-key-table-not-a-dictionary)'s blackboard, [D13](#d13--sensors-are-how-the-world-reaches-the-blackboard-and-there-are-two-kinds)'s sensors, [D16](#d16--the-planner-is-a-job-and-the-budget-is-per-frame-not-per-agent)'s governor and [D20](#d20--one-debug-surface-and-it-runs-in-a-shipped-build)'s debugger. The unshared part is one scorer and one search. If a phase must be cut, cut **P6**: a game without GOAP has two planners; a game without the blackboard has none |
| A-R5 | **Perception cost scales as a product and someone ships a 1 000-NPC village** | Medium | The three bounds in [D15](#d15--perception-is-a-system-and-its-cost-is-a-schedule) are mandatory, not tuning, and P3's exit criterion measures both sides |
| A-R6 | **A desync from AI, six months into a networked project** | Medium | [D17](#d17--ai-runs-on-the-authority-and-the-client-is-never-told-the-tree) — nothing replicates, so there is no second planner to disagree with. [D18](#d18--determinism-is-a-property-of-the-decision-not-of-the-schedule) — and the governor hole is named there rather than left to be found |
| A-R7 | **The engine grows a behaviour library.** A `CastAbility` node arrives, then a `Flee` node, then a threat model | ~~Medium~~ **Low, once `Gameplay/` is its own layer** | The `Core/` ⇸ `Gameplay/` layer rule is the enforcement, and it is the strongest form available: the node cannot compile, so nobody has to notice it in review. [Part 3](#part-3--the-node-library)'s *"not shipping, and why"* is the standing answer for the ones the compiler cannot catch, and doc 28's sentence is the policy — a planner and a perception model, not a behaviour library |
| A-R8 | ~~**`Symbol` moving to `Vixen.Core` breaks `Vixen.Animation`'s public surface**~~ | ~~Low~~ **Spent** | Done in P0, and it cost less than the row predicted: the type was still *unshipped*, so no type-forward was needed — a baseline rewrite, a `using` sweep across 37 files, and one `<see cref>` that had to stop naming `MoveSet` because `Vixen.Core` cannot see it |

---

## Where it stops, stated plainly

- **No StateTree, no HTN, no utility-driven *trees*.** Three paradigms, as doc 28 named them. The
  substrate would carry a StateTree and does not.
- **No learned or trained behaviour.** Nothing here fits a model, tunes weights from play data or
  ships an inference runtime.
- **No group or squad coordination beyond a shared blackboard.** Formations, role assignment,
  cover-slot arbitration and flanking are genuinely valuable and genuinely a *game's* — they are
  built on a `SharedBlackboard` and a coordinating agent, both of which exist here, and the engine
  supplies neither policy.
- **No dialogue, no barks, no schedules.** A daily routine is a utility set with a time-of-day input;
  a bark is a task. What the engine does not ship is the content model for either.
- **No smart objects.** Unreal's are an interaction reservation system with an annotation on the
  world, and doc 28 § Interaction already owns that shape. When it is built, an AI action that claims
  one is four lines and belongs to `Vixen.Gameplay.Interaction`.
- **No animation-driven decision-making.** The planner picks an action; doc 34's move-set query picks
  the clip. Those meet through a blackboard key and no further.
- **No client-side AI at all**, including for cosmetic agents. A crowd that only needs to look busy
  is `Vixen.Vfx` or an animation, not an agent, and the moment it becomes an agent it is the
  server's.

---

## What this changes in doc 28

[28](28-gameplay-framework.md) § AI keeps its three-row table — it is the right table, and this
document's [Part 1](#part-1--the-argument) is its expansion. What changes is the placement, and one
package splits in two **across the layer boundary** — which is only expressible because doc 28's
spine is moving out of `Core/` into a `Gameplay/` folder of its own:

| Doc 28 said | Now |
|---|---|
| `Core/Vixen.Gameplay.Ai` — *"GOAP + utility + behaviour trees, perception, aggro, spawning"* | **`Core/Vixen.Ai`** (+ `.Perception`, `.Nodes`, `.Diagnostics`) — the three planners, the blackboard, the action surface, perception, the governor, the environment query. ⚠ **Four assemblies and not the `.Generators` this row first guessed at**: the query generators are arithmetic over an origin and live in `Vixen.Ai` beside the tests they feed, the two that need a transform or a mesh live in `.Nodes`, and the assembly that had to be split out was the *debugger*, because `DebugDraw` is `Vixen.Engine`'s. On `Vixen.Core.*`, `Vixen.Ecs`, `Vixen.Engine` and nothing else. **`Gameplay/Vixen.Gameplay.Ai`** survives, shrunk: threat, aggro, leashing, patrol definitions, spawn tables with respawn timers, NPC dialogue and vendor state. It references `Vixen.Ai` and `Vixen.Gameplay.Combat` |
| *"`Combat` is depended on by `Pvp`, `Instances`, `Ai`"* | Still true of `Vixen.Gameplay.Ai`. **Not** true of `Vixen.Ai` — and with the two in different layers this is a build failure rather than a review comment, which is the point |
| The whole spine under `Core/` | `Gameplay/`, referencing `Core/` in one direction. ⚠ **Recorded by [02](02-repository-layout.md) and 28, not by this document** — but it is the thing that turns [Part 1](#part-1--the-argument)'s argument from a convention into a gate, so it is worth naming what depends on it |
| `Vixen.Editor.Gameplay.Ai` — *"behaviour/GOAP graph, same host"* | **`Vixen.Editor.Ai`** — the model, the layout and the projection — plus `Vixen.Editor.AssetEditors/Ai` for the document, the view and the factory. ⚠ **The split is this repository's convention rather than this document's plan**: every other graph editor here puts its model and compiler in a project of their own and its panel in `AssetEditors`, which is what lets the model be tested with no editor in the way. `Vixen.Editor.Gameplay` keeps the definition-shaped surfaces |
| Milestone **G7** — *"AI (three planners, perception, aggro, spawning); interaction and gathering; crafting; mounts and vehicles; travel; exploration"*, 3.5 em for all six | G7's AI line shrinks to aggro, spawning and encounter scripting, and **depends on this document's P0–P6** rather than containing them. ⚠ **The engine-wide total rises, and that is the correction, not a side effect.** Three planners, a perception model and a node editor were never going to fit in a share of 3.5 em split six ways — doc 28's estimate was for the gameplay half of the line and this document prices the other half at 9.6 em |
| *"encounter scripting on the AI library's behaviour trees"* (§ Instances) | Unchanged, and now a reference across a clean boundary rather than a package that contains both |

⚠ **Doc 28's § AI paragraph should be read as this document's summary once this is built**, and the
sentence it ends on — *"the engine-side ambition is deliberately bounded: a planner and a perception
model, not a behaviour library"* — is [Part 3](#part-3--the-node-library)'s cut list and A-R7's
policy, restated. Nothing about the ambition changed. Only where it lives.
