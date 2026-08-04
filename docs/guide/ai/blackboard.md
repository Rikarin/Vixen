---
title: The blackboard
slug: ai/blackboard
kind: guide
area: AI
summary: An agent's data as a compiled key table — six types, byte ranges, per-key versions and observers.
api: [T:Vixen.Ai.Blackboard, T:Vixen.Ai.BlackboardLayout, T:Vixen.Ai.BlackboardLayoutBuilder, T:Vixen.Ai.BlackboardKey, T:Vixen.Ai.BlackboardKeyDefinition, T:Vixen.Ai.BlackboardValueType, T:Vixen.Ai.IBlackboardObserver, T:Vixen.Ai.BlackboardObserverHandle, T:Vixen.Ai.SharedBlackboard, T:Vixen.Ai.SharedBlackboard.Scope]
tags: [ai, blackboard, agents, determinism]
since: 0.1
status: stable
related: [ai/agents, ai/behaviour-trees, core/symbols]
---

## What it is

A **blackboard** is where an agent keeps what it knows: the target, the last place it saw one, whether
it is alerted, how far away the noise was.

It comes in two halves. A **layout** is the shape — a list of named, typed keys — compiled once, with
each key assigned an index and a byte range. A **blackboard** is one agent's instance of that shape: a
byte array, a bit per key saying whether it has been set, a version per key, and the observers
watching each one.

## What it is for

Everything an agent decides with, and everything the three planners share. A behaviour-tree decorator
reads a key; a utility consideration takes its number from one; a GOAP world state is projected from
them. Perception writes into them, and a game's own systems write into them too — which is the point:
the blackboard is the seam between "what the world is doing" and "what this agent thinks about it".

You do *not* want it as a general per-entity store. A key is six types wide on purpose, it is not
replicated, and it is not saved. Data that belongs to the entity rather than to its decision-making is
a component.

## Using it

Build a layout once, then give every agent an instance of it. `AiSystem` does the second half for
you — you hand it the layout and it creates a board per agent as they join.

```csharp compile
using Vixen.Ai;
using Vixen.Core;

public static class GuardData {
    public static Blackboard Create(Entity someEntity) {
        var layout = new BlackboardLayoutBuilder()
            .Add("target", BlackboardValueType.Entity)
            .Add("lastKnownPosition", BlackboardValueType.Vector3)
            .Add("alertLevel", BlackboardValueType.Float)
            .Add("stance", BlackboardValueType.Symbol)
            .Build();

        // Resolve keys once, at load, not per tick. A key is a ushort; a name is a hash lookup.
        var target = layout.Key("target");
        var stance = layout.Key("stance");

        var board = new Blackboard(layout);

        board.SetEntity(target, someEntity);
        board.SetSymbol(stance, Symbol.Intern("crouched"));

        if (board.IsSet(target)) {
            Console.WriteLine(board.GetEntity(target));
        }

        return board;
    }
}
```

### The six types, and why there are only six

`Bool`, `Int`, `Float`, `Vector3`, `Entity`, `Symbol`. Everything a game wants is one of those: a
state name is a `Symbol`, a rotation is a direction or an entity to look at, an object reference is an
`Entity`, a count is an `Int`.

Closing the list is what makes a key twelve bytes at worst, a comparison a switch rather than a
virtual call, and an inspector able to draw every key there is with no extension point. The type that
is missing — an arbitrary object — is the escape hatch that would turn a compiled table back into a
dictionary.

### Set-ness is not a value

⚠ A key is **set** or **unset** independently of what it holds, because `false`, `0`, the zero vector
and `Entity.Null` are all values somebody means. `Clear` unsets a key and zeroes its bytes, so a stale
entity id cannot be read through a missing `IsSet` check.

### Versions and observers answer different questions

A write bumps the key's version and notifies its observers — **but only when the value actually
changed.** Writing the same number is not a change, and if it were, every service that writes its
result each tick would abort every decorator observing it, for ever.

- **Observers** drive aborts. Something that must interrupt a running branch cannot poll for it.
- **Versions** drive everything that only wants to recompute when something moved — a cached path, a
  scorer, a service on an interval — without keeping a copy of the value to compare against.

⚠ **An observer is told; it does not act.** The notification arrives during somebody else's write,
which is very often the running task writing its own result. Enqueue work and service it at the top of
the next step. Aborting from inside a notification destroys the state of the thing currently
executing.

### Sharing

One agent owns one blackboard. That is what makes stepping agents in parallel safe — a step touches
this agent's memory and this agent's board and nothing else.

Data a group shares is a `SharedBlackboard`, which is a distinct type so that sharing is a decision
somebody made rather than something that happened. It is written inside a scope, on the thread that
opened it, in a single-threaded phase; a write anywhere else throws.

```csharp no-compile="a fragment; the squad's layout and its rally point are the game's"
var squad = new SharedBlackboard(squadLayout);

using (squad.BeginWrite()) {
    squad.Values.SetVector3(objective, rallyPoint);
}

// Read freely for the rest of the frame.
var where = squad.Values.GetVector3(objective);
```

## Examples

A condition that watches a key and asks to be re-evaluated when it moves — the shape a behaviour-tree
decorator takes:

```csharp compile
using Vixen.Ai;

public sealed class HasTarget : IBlackboardObserver {
    readonly BlackboardKey key;

    BlackboardObserverHandle registration = BlackboardObserverHandle.Null;

    public HasTarget(BlackboardKey key) => this.key = key;

    public bool Dirty { get; private set; }

    public void Enter(Blackboard board) => registration = board.AddObserver(key, this);

    public void Exit(Blackboard board) {
        board.RemoveObserver(registration);
        registration = BlackboardObserverHandle.Null;
    }

    // ⚠ Records that something moved. It does not act on it — the stepper does, next step.
    public void OnBlackboardChanged(Blackboard board, BlackboardKey changed) => Dirty = true;

    public bool Evaluate(Blackboard board) => board.IsSet(key);
}
```

Recomputing only when something moved, without keeping a copy of the value:

```csharp no-compile="a fragment; the board, the key and what recomputing means are the caller's"
var seen = board.VersionOf(distance);

// … later, on some interval …
if (board.VersionOf(distance) != seen) {
    seen = board.VersionOf(distance);
    Recompute(board.GetFloat(distance));
}
```

## See also

- [Agents and actions](agents.md) — what reads a blackboard, and the system that hands one to every
  agent.
- [Symbols](../core/symbols.md) — what a key name is, and why it is a hash rather than an index.
