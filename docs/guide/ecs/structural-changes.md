---
title: Structural change during iteration
slug: ecs/structural-changes
kind: guide
area: ECS
summary: Creating, destroying, adding and removing while a loop is walking the chunks — recorded into a command buffer and played back where nothing is iterating.
api: [T:Vixen.Ecs.CommandBuffer, T:Vixen.Ecs.CommandBuffer.ParallelWriter, T:Vixen.Ecs.CommandKind]
tags: [ecs, command-buffer, jobs, determinism, spawning]
since: 0.1
status: stable
related: [ecs/queries, ecs/components]
---

## What it is

`CommandBuffer` records structural change — create, destroy, add, remove, set — and applies it later,
at a moment when nothing is iterating. `CommandKind` is the five verbs it records;
`CommandBuffer.ParallelWriter` is the view a job records through.

A system does not build one. `SystemContext.Commands` is already there, and the runner plays it back
at the end of every phase.

## What it is for

Adding or removing a component moves an entity's row from one archetype's chunk to another's. That is
the whole trick an archetype ECS is built on, and it is also why the change cannot happen while a loop
is walking those chunks: the `Span<T>` the loop is indexing is a window onto storage the move
reshuffles.

The engine's answer is not to detect that and complain. It is to make the mutation happen somewhere it
cannot hurt:

> Decide during the loop, apply after it.

So `World` stays the strict, immediate, main-thread API — and a job, which may not touch `World`
structurally at all, gets exactly one way to change anything: record it.

You do not want a buffer when you have the world to yourself and nothing is iterating — level load,
a tool, a test's setup. `World.Create` and `World.Add` there are shorter, apply immediately, and tell
you when you are wrong.

## Using it

### The sync point is the phase boundary, and that is the whole schedule

`SystemRunner` plays back its buffer at the end of each phase, and once more before the first phase so
that whatever `Initialize` recorded is in the world before anything runs.

Two consequences worth holding on to:

- **A system does not see the structural change another system in the same phase recorded.** They are
  both looking at the world as it was when the phase began. If one system must see the other's spawn,
  they belong in different phases — that is what a phase is.
- **The entity you recorded is not there on the next line.** `world.IsAlive(placeholder)` is false, and
  reading a component off one throws `EntityNotFoundException`. A placeholder carries a negative id
  precisely so that `World` can refuse it rather than index whatever shares the slot.

```csharp no-compile="a fragment inside an Update; `context` is the system's own"
// Decided now, applied at the end of the phase.
context.Commands.Destroy(entity);
```

### ⚠ The buffer is lenient exactly where `World` is strict

That asymmetry is deliberate, and it is the part that surprises people who learned `World` first:

| Call | `World` | `CommandBuffer` |
|---|---|---|
| `Add<T>` on an entity that has one | Throws | Overwrites |
| `Remove<T>` of a component it does not have | Throws | Does nothing |
| `Destroy` of an entity already destroyed | Throws | Does nothing |
| Any command naming an entity an earlier command in the same playback destroyed | — | Skipped |
| `Set<T>` of a component the entity does not have | Throws | **Throws, at playback** |

A recorder runs during iteration and *cannot look at the world* to find out whether its change is
redundant — and two systems both deciding to remove the same tag, or to destroy the same entity, is
ordinary rather than exceptional. A caller that can look uses `World` and gets told when it is wrong.

⚠ **`Set` is the one that still throws, and it throws far from where it was recorded.** The exception
is an `InvalidOperationException` naming the command and the component, wrapping the
`ComponentNotFoundException` that actually happened — because the stack at playback is the runner's
and says nothing about which system recorded it. If you cannot tell which one did, `Add` is the
lenient verb: it writes the value whether or not the component was there.

### ⚠ The sort key is what makes a parallel spawn reproducible

`AsParallelWriter()` hands out a struct with one extra parameter on every call: the index of the work
item being processed. Playback sorts by that key first, then by channel, then by the order the channel
recorded — so the result does not depend on how the scheduler happened to distribute the work across
threads.

⚠ **Commands sharing a sort key from different threads have an unspecified order between them.**
Passing a constant, or a counter of your own, is how a fixed-step simulation stops being reproducible
— and it stops being reproducible silently, on a machine with a different core count than yours.

### Reaching what you spawned

`Resolve` answers what a placeholder became. It is valid **from the end of a playback until the start
of the next one** — long enough for the system that recorded the spawn to pick the entity up in the
next phase, and no longer.

```csharp no-compile="two phases apart; `spawned` is the placeholder Create handed back"
var entity = context.Commands.Resolve(spawned);

if (!entity.IsNull) {
    // The Create was applied. Null means it was culled — an earlier command in the same playback
    // destroyed it, which is legal and is not an error.
}
```

## Examples

**Deciding a destruction inside the loop that found it.** This is the shape of nearly every use: read
in the chunk walk, record on the buffer, and let the phase boundary do the moving.

```csharp compile
using Vixen.Core;
using Vixen.Core.Threading;
using Vixen.Ecs;
using Vixen.Ecs.Systems;

[Component]
public struct Health {
    public int Value;
}

public sealed class ReapSystem : SystemBase, IDeclaredAccess {
    static readonly QueryDescription Living = new QueryDescription().WithAll<Health>();

    public SystemAccess Access { get; } = SystemAccess.Declare().Read<Health>().Build();

    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        // Nothing scheduled may still be writing what this is about to read.
        dependency.Complete();

        foreach (var chunk in context.World.Chunks(Living)) {
            var health = chunk.ReadValues<Health>();
            var entities = chunk.Entities;

            for (var index = 0; index < chunk.Count; index++) {
                if (health[index].Value <= 0) {
                    // Destroying here instead would invalidate `health` and `entities` mid-loop.
                    context.Commands.Destroy(entities[index]);
                }
            }
        }

        return dependency;
    }
}
```

**Spawning from a job, with the components already on the entity.** A placeholder is usable in later
commands on the same buffer, so an entity is never created bare and then patched — it arrives at
playback with its whole archetype, which is one chunk move instead of three.

```csharp compile
using Vixen.Core;
using Vixen.Ecs;

[Component]
public struct Position {
    public float X;
    public float Y;
}

/// <summary>A tag: no data, so `ITagComponent` says the storage is a bit and not a column.</summary>
[Component]
public struct Spawned : ITagComponent;

public static class Spawning {
    /// <summary>`item` is the index of the work item — the thing that makes playback reproducible.</summary>
    public static Entity Emit(CommandBuffer.ParallelWriter commands, int item, Position at) {
        var entity = commands.Create(item);

        commands.Add(item, entity, at);
        commands.Add<Spawned>(item, entity);

        return entity;
    }
}
```

**Outside a runner**, a buffer is a plain object over a world: record, then `Playback()`. `Count` is
what is recorded across every channel and `Clear()` throws it away unapplied — which is what a
rollback is, and the reason playback and discard are two methods rather than one.

## See also

- [Entity queries](queries.md) — the iteration this exists to protect.
- [Components](components.md) — what an add or a remove moves an entity between.
- `Core/Vixen.Ecs/README.md` — the archetype storage, and why a structural change is a row move.
