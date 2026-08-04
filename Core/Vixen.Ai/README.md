# Vixen.Ai

What an agent does next. Three ways of deciding — a behaviour tree, a utility set, a GOAP plan — over
one action surface, one blackboard, one memory model and one governor.

Spec: [docs/plan/37](../../docs/plan/37-ai-behaviour-trees-utility-and-goap.md), which amends
[28](../../docs/plan/28-gameplay-framework.md) § AI by moving the three planners off the gameplay
spine and into `Core/`.

## State

**Doc 37 is finished: P0 to P9, all ten phases, built and tested. 233 tests here, 54 over the editor,
38 over [Vixen.Ai.Perception](../Vixen.Ai.Perception/README.md), 65 over
[Vixen.Ai.Nodes](../Vixen.Ai.Nodes/README.md) and 13 over
[Vixen.Ai.Diagnostics](../Vixen.Ai.Diagnostics/README.md), and every exit criterion is a number
rather than an opinion:** ten thousand agents step in a frame that allocates
*zero* bytes; the governor's schedule is asserted to be a pure function of the tick and the agent's
index; and a thousand agents on a ten-node tree visit **zero** nodes across sixty settled frames,
against 60 000 for a per-frame traversal of the one-node tree measured beside it in the same test.

**All three planners are here**, and they choose from one action registry. A behaviour tree is good at
a *procedure*, a utility set at a *judgement*, and a GOAP domain at a *combination* nobody enumerated
— and `RunUtilitySetTask` is the join between the first two.

| | |
|---|---|
| `Blackboard/BlackboardValueType` | The six kinds a key may hold. The list is closed, and that is what keeps a key twelve bytes and an inspector extension-free. |
| `Blackboard/BlackboardLayout` | A compiled key table: names resolved to indices, indices to aligned byte ranges. Built by `BlackboardLayoutBuilder`, which refuses a duplicate name *and* a symbol collision. |
| `Blackboard/Blackboard` | One agent's instance. A byte range, a set/unset bit per key, a version per key, and an intrusive observer list per key. |
| `Blackboard/IBlackboardObserver` | What a decorator registers as, so a tree that has nothing to react to does nothing at all. |
| `Blackboard/SharedBlackboard` | The board a group shares, writable only inside a scope on the thread that opened it. |
| `Actions/IAgentAction` | `Start` / `Tick` / `Abort` over a `Span<byte>`. The one thing all three planners choose. |
| `Actions/AgentActionRegistry` | Every action by index, with the state size each one needs. What a compiled asset resolves a task to. |
| `Agents/AgentMemoryPool` | Per-agent state carved out of pages that never move, on a free list per size. |
| `Agents/AgentRandom` | Stateless randomness keyed on the agent, the stream and what the number is for. ⚠ The entity and the seed mix with `+` and not `^` — see below. |
| `Agents/IAgentGovernor` | Who thinks this tick. `RoundRobinGovernor` (budget with a floor) and `UnboundedGovernor`. |
| `Ecs/AiAgent` | A handle, an index, a seed and four small fields. Nothing that varies in size, and nothing replicated. |
| `Ecs/AiSystem` | Joins agents to their memory and their board, asks the governor, steps whoever it named. |
| `Diagnostics/AgentDebugRecord` | What an agent decided and why, in the one shape all three planners fill. Off by default. |
| `Diagnostics/AiAgentSnapshot` | One agent's whole state as rows of strings — what the overlay draws, the panel tabulates and the wire carries. |
| `Diagnostics/AiSnapshots` | Fills one from a running agent. The three planner branches, and the only three there are. |
| `Diagnostics/AiBreakpoints` | Where a running tree stops. Scoped by the same containment test an abort uses. |
| `Diagnostics/AiDiagnosis` | What is visibly wrong with an agent, read out of the recorded log and nothing else. |
| `Diagnostics/AiDebugChannel` | The one thing that crosses a wire: a request and a response for one agent, off by default. |
| `BehaviorTrees/BehaviorTreeCompiler` | An authored tree to a flat array of nodes in depth-first pre-order, with `LastDescendant`, byte ranges and a diagnostic list. Splices static subtrees. |
| `BehaviorTrees/BehaviorTreeTemplate` | The compiled tree: immutable, shared, with no per-agent field anywhere. |
| `BehaviorTrees/BehaviorTreeInstance` | One agent running one tree — the active node, its byte block, and the observers that wake it. |
| `BehaviorTrees/BehaviorDecorator` · `BehaviorService` | The two attachment kinds, both shared across agents and both keeping per-agent state in a span. |
| `BehaviorTrees/Nodes/` | Five composites, thirteen decorators, the `UpdateBlackboard` service over `IWorldSensor`, and eight tasks. |
| `BehaviorTrees/BehaviorTreeContent` | A tree as a file: keys, a node tree with attachments, and where the boxes sit. What a `.vxbt` holds and what a game loads. |
| `BehaviorTrees/BehaviorNodeSchema` | The node library declared once — label, category, slot, and each field's kind, tooltip and default. What generates the inspector and fills the search popup. |
| `BehaviorTrees/BehaviorTreeContentCompiler` | Data in, live decorators and registered actions out, against a `BehaviorTreeResolver` a game fills. |
| `Utility/ResponseCurve` | The six shapes of doc 37 § D8, as four parameters a file holds and an editor draws. |
| `Utility/IUtilityInput` | Where a consideration's number comes from, normalised to `[0,1]` before the curve sees it. |
| `Utility/UtilityAction` · `UtilityScoring` | One thing an agent might do, and the weighted geometric mean with the zero rule. |
| `Utility/UtilitySet` · `UtilityState` | The set, and the inertia that stops an agent flapping. One state shape, two hosts. |
| `Utility/IUtilitySelector` | Which of the scored actions wins: highest, weighted random, top-weighted, bucketed. |
| `Utility/RunUtilitySetTask` | A whole set as a behaviour-tree leaf — the join D2 exists to make possible. |
| `Utility/UtilitySetContent` | A set as a file: actions, each with considerations. A list, because a set has no edges. |
| `Utility/CandidateScoring` | The one place factors become a score and one wins. `IScoredCandidateSet<T>` is what makes "the same scorer" checkable. |
| `Queries/QueryPoint` · `QueryResults` | A candidate answer to "where should I stand", and a run's points, scores and factors. |
| `Queries/IQueryGenerator` | What makes the candidates: grid, circle, donut, cone, current location, composite. Bounded at 4096 points. |
| `Queries/IQueryTest` · `QueryTest` | One reading, normalised, clamped, curved, and used to filter or to score or both. |
| `Queries/EnvironmentQuery` | Generators, then tests in order. A list, because that is what an EQS graph canvas actually holds. |
| `Queries/QueryContent` | A query as a file, and the compiler that turns one into a query. |
| `Goap/GoapWorld` | World keys, conditions and effects. An effect is a *direction*, which is what makes a domain authorable. |
| `Goap/GoapDomain` | Actions, goals, and the graph between them — built once, when the domain is configured. |
| `Goap/GoapPlanner` | The bounded A\* backwards from goal to satisfied. Searches a snapshot and never the world. |
| `Goap/GoapSnapshot` | Everything one resolve needs, taken off the world in one go. What makes a resolve a job. |
| `Goap/GoapPlanQueue` | Resolves, queued and run a budget at a time — `NavPathQueue`'s arrangement for its reason. |
| `Goap/IReplanPolicy` | When an agent thinks again: reactive, proactive, manual. |
| `Goap/GoapDomainContent` | A domain as a file: three tables. The graph is derived, not authored. |
| `Sensors/ISensors` | doc 37 § D13's four kinds — a number or a target, per agent or once a pass — and the delegate and constant forms of each. |
| `Sensors/SensorSet` | The pass: globals once, cached; then locals, per agent. What makes "is it night" one query rather than a thousand. |
| `BehaviorTrees/BehaviorNodeFactory` | How a node whose implementation is in another assembly gets built. `BehaviorBuildContext` resolves a key *name* to the index the runtime uses; the shipped nodes are matched first. ⚠ An action is shared across compiles as well as within one — see below. |

## The four things worth knowing before reading the code

### An action never owns its state

```csharp
ActionStatus Tick(in AgentContext context, Span<byte> state, float delta);
```

One action object is shared by every agent running the asset it belongs to, so a field on the action
is a field a thousand agents write to. That is the mistake every hand-rolled behaviour tree makes; it
is invisible until the second agent exists, and the symptom is two guards sharing one patrol index.
Taking the span is the only arrangement in which the mistake cannot be made, and
`AHundredAgentsOnOneActionHoldAHundredIndependentStates` is the test that fails if anybody adds a
field anyway.

### A version bumps only when a value actually changed

Writing the same number is not a change. If it were, every service that writes its result each tick
would abort every decorator observing it, for ever — which is the difference between an event-driven
tree and a tree that ticks itself to death. `AVersionIncreasesExactlyWhenSomethingChanged` is a
property test over five thousand random writes, clears and reads.

Set-ness is separate from the value, because `false`, `0`, the zero vector and the null entity are all
things somebody means, and `Is Set` is the commonest decorator there is.

### The memory pool is paged, and that is not an optimisation

A single growable arena would move every byte in it when it doubled, silently invalidating every span
a caller was holding. A system that resolves a block and then ticks an action would have a
use-after-free with no symptom until it had one. Pages are allocated once and never move, so growth is
a new page. `ASpanSurvivesTheAllocationOfMorePages` is the test.

### An agent gets its own delta, not the frame's

Under a governor an agent updated one tick in four would otherwise run every timer at a quarter speed,
silently: a `Wait(2 s)` that takes eight looks like a design decision until somebody measures it. So
every agent accumulates elapsed time every frame — one float add — and spends the accumulation on the
frames the governor gives it a turn.

### A step is not a traversal, and the difference is the whole design

A classic behaviour tree walks from the root every frame. This one keeps the active node and does
nothing at all when nothing has changed. `BehaviorTreeCostTests` measures both shapes in one test,
which is the only honest way to state a claim like that: a wall-clock threshold would be a different
number on every machine.

An abort is two integer comparisons against the decorated node's pre-order range — which is what the
flat layout is *for* — and it is serviced at the top of the next step rather than when the key was
written, because a task writes its own results during its tick and tearing it down from inside that
write would destroy the state of the thing currently executing.

⚠ **A decorator reaches the siblings under its own parent composite and no further.** That is Unity's
rule rather than Unreal's, taken because it is the one the editor can *draw*. Its cost: a condition
that becomes true deep inside a branch the agent has already walked past does not pull it back. Doc
37's exit criterion is worded for the wider rule, and the property test is exact about the difference
— see the P1 note in the plan document.

## Why this is not `Vixen.Gameplay.Ai`

Doc 28 put the three planners on the gameplay spine, depending on `Vixen.Gameplay.Combat`. A behaviour
tree that depends on a combat package cannot run a stealth patrol, a shopkeeper, a companion following
a player through a puzzle, or a squad in a game with no abilities at all — and doc 28 itself wrote the
sentence that settles it: *"the engine-side ambition is deliberately bounded: a planner and a
perception model, not a behaviour library."*

A composite that runs children until one fails contains no game concept and is identical in every game
ever shipped. A task that casts `Fireball` names a definition, a cooldown and a resource. Those are
two things, and the second belongs to `Vixen.Gameplay.Ai` — which survives, shrunk to threat, aggro,
leashing, spawn tables and dialogue, and references this rather than containing it.

⚠ **The enforcement is meant to become a build rule.** `CheckArchitecture` derives a project's layer
from its top-level directory, so the rule becomes one line beside the three already there once the
gameplay spine moves out of `Core/` into a `Gameplay/` folder of its own —
[doc 02](../../docs/plan/02-repository-layout.md)'s tree and doc 28 § Library structure own that move.
Until then it is `AiLayeringTests`, which is weaker on purpose and is meant to be deleted in the same
commit that adds the gate line.

## A count of considerations must not change a score

Scores combine as a **weighted geometric mean**, and the naive product is what everybody writes
first. With every term in `[0,1]`, an action with six considerations is *structurally* worse than an
identical action with three — so adding one to tune an action quietly demotes it, and the demotion is
invisible because every individual number still looks right. The `n`th root makes the count
irrelevant, and `TheCountOfConsiderationsDoesNotChangeTheScore` is the test.

⚠ **The zero rule survives the mean, and that is the point of using a product at all.** One zero
factor makes the whole thing zero, which is how "never, under any circumstances" is said. A weighted
sum cannot say it — a veto is outvoted by enough enthusiasm elsewhere, which is how an agent ends up
drinking coffee while on fire.

## A planner that has chosen nothing must run nothing

`AiAgent.Action` is zero until something sets it, so a planner whose first decision has not landed
would run **whichever action happens to be registered first**, every frame — and it looks exactly
like a plan. `AiSystem` asks the planner whether it chose anything at all and runs nothing when it
did not. P6 found it; P5 and P0 both had it.

## A resolver outlives a compile

An action is keyed on its type *and every one of its field values*, because two `Wait`s with
different durations are two actions — an action object carries its own settings and is shared by
every agent running it. That key was consulted only within one compile, so a game compiling every
`.vxbt` it ships against one resolver **threw on the second tree that contained a `Wait(1)`**.
Sharing across compiles is what the key was always for; `TwoTreesWithTheSameTaskShareOneRegisteredAction`
is the regression, and P4 is what found it.

## A seed and an entity must not cancel

`AgentRandom.Hash(entity, seed, salt)` combines the entity with the seed by `+` rather than by `^`,
and that is a fix rather than a preference. Every caller in the engine seeds a stream with
`AgentRandom.SeedOf(entity)`, which for a freshly created entity *is* `Hash(id)` — so `Hash(id) ^ seed`
was `Hash(id) ^ Hash(id)`, which is **zero for every agent in the world**. Every guard drew the same
number from its supposedly private stream: one shuffled selector picked the same child a thousand
times, and a jittered interval put the whole population on one frame while looking like it had spread
them. P3 found it by spreading forty listeners over ten frames and watching all forty land on frame
five; `AgentsSeededFromTheirOwnEntitiesDoNotAllDrawTheSameNumber` is the regression.

## Nothing here is replicated, and nothing ever will be

AI runs on the authority. A client planning from an interest-filtered, interpolated view of the world
would reach different conclusions, and reconciling two planners is not a thing anybody has made work.
What crosses the wire is what the agent's actions *write* — a navigation destination, a move intent,
an animation state — all of which are replicated by their own subsystems. A client sees an NPC walk to
the door; it has no blackboard, no tree and no plan.

The one channel that must eventually cross is the debugger's, so that the editor can inspect a running
dedicated server, and it is gated behind the same switch doc 13's remote inspector uses. That is why
`AgentDebugRecorder` is off by default.

## The same scorer, made checkable

Doc 37 § D14 claims that "where should I stand" and "what should I do" are the same machine. That is
either checkable or it is a remark somebody contradicts in six months by writing a second mean.

So there is one mean, in `CandidateScoring.Combine`, and `UtilityScoring.Combine` forwards to it.
There is one streaming scorer — "stop at the first zero unless the detail was asked for" — written as
a generic over a `ref struct` reader, so a utility action and a query point share it and neither
allocates for the privilege. And `UtilitySet` and `EnvironmentQuery` both implement
`IScoredCandidateSet<T>`, so a table, an overlay or a preview is written once and drives either.

⚠ **Its factor counts are per candidate rather than per set**, which is the one place the two hosts
genuinely differ: every point in a query runs the same test list, and every action in a set has its
own considerations. An abstraction shaped like the query would have made the set implement it by
lying.

## Every seam is implemented twice, and a test says so

Doc 37 § Part 4 says each of its interfaces gets a second implementation differing in shape, because
a one-implementation interface is one nobody has checked is an interface. That rule is enforced in
`SeamTests` in `Vixen.Ai.Nodes.Tests` — the only test project that sees all four shipped assemblies —
as a theory over twenty-one interfaces.

⚠ **It found three gaps the first time it ran**, none of which was visible in review: two rows whose
"second implementation" was a delegate wrapping the first, and a taxonomy that had shipped one of its
four members. A review catches this on the day an interface is added and never again; the assemblies
can be asked every build.

## What is owed

Nothing of doc 37. What is left is doc 28's `Vixen.Gameplay.Ai` — threat, aggro, leashing, spawn
tables, dialogue state — which references this rather than containing it.

⚠ **Distance LOD is not `IAgentGovernor`'s**, which is a change P3 made to doc 37's Part 4. `Plan` is
handed a tick and a population and nothing else — `AgentSchedule` is eight bytes on purpose, and a
plan that enumerated its agents would allocate once a frame — so distance, which needs a position per
agent, is `IPerceptionGovernor`'s in `Vixen.Ai.Perception`.

**Perception and the world-facing nodes are built**, in
[Vixen.Ai.Perception](../Vixen.Ai.Perception/README.md) and
[Vixen.Ai.Nodes](../Vixen.Ai.Nodes/README.md). Both are separate assemblies because they need what
this one may not reference — `Vixen.Engine` and `Vixen.Physics` for the senses, and navigation,
animation and audio for the nodes.

The `.vxbt` file, its importer and the canvas are built — `Editor/Vixen.Editor.Ai` is where the
authoring half lives, and its README is where the split from `Vixen.Editor.NodeGraph` is argued. Its
*live* half is built too: `AgentDebugModel` is the active path, the live blackboard, the recorded log
and the breakpoints, over the same `AiAgentSnapshot` the runtime overlay draws.

## A debugger must not move the bug

Taking a picture of an agent re-scores its utility set, reads its tree's memory block and projects its
GOAP world keys — all of which are things the agent itself does, and any of which could change what it
decides next. So `AiSnapshots.Take` goes through `UtilitySet.Score`, which takes the state by
`ref readonly`, rather than through `Choose`, which advances the decision clock and starts cooldowns;
and it reads a plan rather than re-resolving one. `TakingASnapshotDoesNotChangeWhatTheAgentDecides`
is that, asserted: ten captures, and the decision count does not move.

The same rule is why a breakpoint stops the *agent* and not the game. There is no world to freeze
from `Vixen.Ai`, and freezing one would be the wrong tool anyway — what somebody wants is the one
agent held with its state intact while everything around it carries on.
