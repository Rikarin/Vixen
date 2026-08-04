# Vixen.Ai

What an agent does next. Three ways of deciding — a behaviour tree, a utility set, a GOAP plan — over
one action surface, one blackboard, one memory model and one governor.

Spec: [docs/plan/37](../../docs/plan/37-ai-behaviour-trees-utility-and-goap.md), which amends
[28](../../docs/plan/28-gameplay-framework.md) § AI by moving the three planners off the gameplay
spine and into `Core/`.

## State

**P0 — the substrate — is built and tested. 71 tests, and both of P0's exit criteria are
numbers rather than opinions: ten thousand agents step in a frame that allocates *zero* bytes, and the
governor's schedule is asserted to be a pure function of the tick and the agent's index.** No planner
exists yet; an agent runs one action, which is what P1's behaviour tree, P5's utility set and P6's
GOAP resolver each replace with a choice.

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
| `Agents/AgentRandom` | Stateless randomness keyed on the agent, the stream and what the number is for. |
| `Agents/IAgentGovernor` | Who thinks this tick. `RoundRobinGovernor` (budget with a floor) and `UnboundedGovernor`. |
| `Ecs/AiAgent` | A handle, an index, a seed and four small fields. Nothing that varies in size, and nothing replicated. |
| `Ecs/AiSystem` | Joins agents to their memory and their board, asks the governor, steps whoever it named. |
| `Diagnostics/AgentDebugRecord` | What an agent decided and why, in the one shape all three planners fill. Off by default. |

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

## Nothing here is replicated, and nothing ever will be

AI runs on the authority. A client planning from an interest-filtered, interpolated view of the world
would reach different conclusions, and reconciling two planners is not a thing anybody has made work.
What crosses the wire is what the agent's actions *write* — a navigation destination, a move intent,
an animation state — all of which are replicated by their own subsystems. A client sees an NPC walk to
the door; it has no blackboard, no tree and no plan.

The one channel that must eventually cross is the debugger's, so that the editor can inspect a running
dedicated server, and it is gated behind the same switch doc 13's remote inspector uses. That is why
`AgentDebugRecorder` is off by default.

## What is owed

P1 onwards, in doc 37's order: the behaviour-tree compiler and stepper, the node editor, perception,
the world-facing nodes, utility, GOAP, the debug overlay, environment queries, and a second
implementation of every seam. `IAgentGovernor`'s distance-LOD implementation lands with perception,
which is the phase that has positions to read.
