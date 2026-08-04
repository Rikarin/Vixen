---
title: Agents and actions
slug: ai/agents
kind: guide
area: AI
summary: One action surface for three planners, per-agent state as a byte range, and a governor that decides who thinks this frame.
api: [T:Vixen.Ai.IAgentAction, T:Vixen.Ai.ActionStatus, T:Vixen.Ai.AgentContext, T:Vixen.Ai.AgentActionRegistry, T:Vixen.Ai.AgentMemoryPool, T:Vixen.Ai.AgentMemoryHandle, T:Vixen.Ai.AgentRandom, T:Vixen.Ai.IAgentGovernor, T:Vixen.Ai.AgentSchedule, T:Vixen.Ai.RoundRobinGovernor, T:Vixen.Ai.UnboundedGovernor, T:Vixen.Ai.Ecs.AiAgent, T:Vixen.Ai.Ecs.AiSystem, T:Vixen.Ai.Diagnostics.AgentDebugRecord, T:Vixen.Ai.Diagnostics.AgentDebugRecorder, T:Vixen.Ai.Diagnostics.AiPlanner]
tags: [ai, agents, actions, scheduling, determinism]
since: 0.1
status: stable
related: [ai/blackboard, ai/behaviour-trees]
---

## What it is

An **agent** is an entity with an `AiAgent` component. `AiSystem` finds it, gives it a block of memory
and a blackboard, asks a **governor** whether it may think this frame, and if so runs its
**action**.

An action is `Start` / `Tick` / `Abort` over a `Span<byte>` of that agent's own memory. A
behaviour-tree task, a utility action and a GOAP action are all the same interface — which is what
lets a project write one `MoveToTask` and get it in all three.

## What it is for

Everything an agent *does*, as opposed to how it decides. Writing a destination, playing an animation,
waiting, claiming a chair, firing.

You do not write an `IAgentAction` for something an ECS system already does better across the whole
population. An action is per agent and runs on the agent's schedule; a system runs over chunks. If the
work has no decision in it, it is a system.

## Using it

Register the actions, build a layout, construct the system, spawn agents.

```csharp no-compile="a fragment; the world is the game's and WaitAction is the class below"
var actions = new AgentActionRegistry();
var wait = actions.Register("wait", new WaitAction(), stateSize: sizeof(float));

var layout = new BlackboardLayoutBuilder()
    .Add("target", BlackboardValueType.Entity)
    .Build();

var ai = new AiSystem(actions, layout) {
    Governor = new RoundRobinGovernor { Budget = 256, MaximumInterval = 16 }
};

var guard = world.Create(AiAgent.Running(wait));
```

`AiAgent.Memory` and `AiAgent.ScheduleIndex` are the system's own bookkeeping; a game sets `Action`
and `Enabled` and reads `Status`.

### Writing an action

```csharp compile
using System.Runtime.InteropServices;
using Vixen.Ai;

public sealed class WaitAction : IAgentAction {
    public void Start(in AgentContext context, Span<byte> state) {
        // The span is zeroed before Start, so there is usually nothing to do here.
    }

    public ActionStatus Tick(in AgentContext context, Span<byte> state, float delta) {
        ref var waited = ref MemoryMarshal.AsRef<float>(state);

        waited += delta;

        return waited >= 2f ? ActionStatus.Succeeded : ActionStatus.Running;
    }

    public void Abort(in AgentContext context, Span<byte> state) {
        // Undo whatever this told the rest of the world to do. Waiting told it nothing.
    }
}
```

⚠ **Never put per-agent state in a field.** One action object is shared by every agent running it, so
a field is a field a thousand agents write to. The bug is invisible until the second agent exists and
the symptom is two guards sharing one patrol index. That is what the span is for, and it is why the
interface takes one.

⚠ **`delta` is not `context.Time.DeltaSeconds`.** It is the time since *this agent* last ticked, which
under a governor is not the frame's step. An action that reaches for the frame's delta instead runs
every timer at a quarter speed the moment the population grows past the budget, and does it silently.

Agents are stepped in parallel over chunks, so an action must touch only its span and its own agent.

### The governor

`RoundRobinGovernor` has two numbers:

- **`Budget`** — how many agents may think in one tick, in the ordinary case.
- **`MaximumInterval`** — the most ticks an agent may wait for its turn.

The floor outranks the budget. A population that cannot fit inside the interval at the budgeted width
gets a wider window and an `AgentSchedule.OverBudget` plan, because an agent that reacts eight seconds
late is not a saving — it is a bug report about the AI being broken. Read `ai.LastSchedule` to find out
what the number you set actually bought.

⚠ **`Plan` must be a pure function of the tick and the population.** An amortised scheduler is
time-dependent by construction, so the hole is bounded by making the schedule reproducible: given the
same tick and the same population, every machine picks the same agents. Not arrival order, not a
queue, not a priority sort on a float. A custom governor that breaks this breaks replay and, over a
network, produces a desync.

`UnboundedGovernor` ticks everybody every frame — right for a dozen agents, and the control a budgeted
governor is measured against.

### Randomness

`AgentRandom` is stateless and keyed on *who is asking, on which stream, for what*. A selector reading
`Random.Shared` is a desync per NPC per second.

```csharp no-compile="a fragment; the context is the one an action is handed"
// The salt is what the number is for. Two uses on one agent must not agree with each other.
var roll = context.Random(salt: 3);
```

### Debugging

`AiSystem.Debug` is an `AgentDebugRecorder`: a ring of `AgentDebugRecord`s saying what each agent
decided and why, in the one shape all three planners fill. It is **off by default** — nothing about AI
crosses the wire, and a recorder that was on by default would be data waiting for somebody to add a
transport.

```csharp no-compile="a fragment; the system is the one built above"
ai.Debug.Enabled = true;
ai.Debug.Capacity = 4096;

// … after the thing went wrong …
var records = new AgentDebugRecord[4096];
var count = ai.Debug.CopyTo(records);
```

## Examples

Stepping the system by hand, which is what a headless test does:

```csharp no-compile="a fragment; the world, the clock and the guard are the test's"
var ai = new AiSystem(actions, layout) { Governor = new UnboundedGovernor() };

for (var frame = 0; frame < 60; frame++) {
    ai.Step(world, time);
    time = time.Advance(TimeSpan.FromSeconds(1 / 60.0));
}

// The board is the system's, keyed on the agent's schedule slot.
var board = ai.BlackboardOf(world.Read<AiAgent>(guard));
```

Renting per-agent state outside the ECS — what a planner does when a template says how many bytes a
whole tree costs:

```csharp no-compile="a fragment; the template and what a step does are the planner's"
var pool = new AgentMemoryPool();
var handle = pool.Rent(template.TotalStateSize);

if (pool.TryResolve(handle, out var state)) {
    Step(state);
}

pool.Return(handle);
```

The span stays valid while the pool grows — blocks are carved out of pages that never move — and it
stops being valid when the block is returned.

## See also

- [The blackboard](blackboard.md) — what an agent decides with, and what an action reads through
  `AgentContext`.
- [Behaviour trees](behaviour-trees.md) — the first of the three planners, and what chooses an action
  for an agent that runs one.
