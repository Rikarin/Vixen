---
title: Component defaults
slug: ecs/component-defaults
kind: guide
area: ECS
summary: Saying what a freshly added component holds, for the types whose zero is not a usable start.
api: [T:Vixen.Ecs.IDefaultComponent`1]
tags: [ecs, components, editor, scenes]
since: 0.1
status: stable
related: [ecs/components, ecs/queries, engine/world-serialisation]
---

## What it is

A component is a struct in a column, and a column is zeroed memory. So a component you add starts as
all-zeroes, always — there is no constructor to run, and a field initializer will not run either.

For most components that is exactly right. For a few it is not: zero intensity is a black light, a
zero far plane is a degenerate projection, and a `VirtualCamera` at zero is *disabled* as well as
lensless, so it neither renders nor looks broken.

`IDefaultComponent<TSelf>` is how such a component says what a fresh one should hold instead:

```csharp compile
using Vixen.Core;
using Vixen.Ecs;

[Component]
[DataContract]
public struct Health : IDefaultComponent<Health> {
    public float Current;
    public float Maximum;

    static Health IDefaultComponent<Health>.DefaultValue => new() { Current = 100f, Maximum = 100f };
}
```

Add that component in the editor now and it arrives at 100/100 rather than at 0/0.

## What it is for

Sparing whoever adds your component the job of filling in values that were never really optional.
A component whose zero is useless is one that looks broken the moment it is added, and the reader
has to work out that it is not.

It is **opt-in, and a component that says nothing pays nothing**. There is no registration to write,
no attribute to remember, and nothing at all is stored for the types that keep their zero — which is
most of them. Do not reach for this out of tidiness: if zero is a sensible starting value, say
nothing and it stays the starting value.

Some zeroes are deliberately meaningful and must keep their meaning. A blend radius of zero is a hard
edge; a grass range of zero means "take the project's setting". Declaring a default for a component
like that would silently take an answer away from whoever wrote the file.

> ⚠ **Do not use a field initializer for this.** `public float Maximum = 100f;` compiles, and it runs
> on `new T()` and never on any path where the ECS hands back a row — so the value appears on some
> paths and vanishes on others with nothing in the source saying which. That has cost this engine two
> long debugging sessions. `IDefaultComponent` is honoured in one statable place and ignored
> everywhere else on purpose.

## Using it

**Where the value is used.** Wherever something creates a component *from nothing* and is about to
hand it to a person:

- The editor's **Add Component** menu.
- `world.AddDefault<T>(entity)`, when you want it in code.

**Where it is not used**, and each for a reason:

| Path | What you get | Why |
|---|---|---|
| `world.Add<T>(entity)` | zeroes | The ECS's contract is that it hands back storage. Honouring a default here would make an unconstrained call ask, per type, on every structural change, on behalf of components that never opted in. `AddDefault<T>` is the one that asks, and its constraint means the compiler resolves it rather than the run time. |
| Loading a scene | whatever was saved | Every field is in the file, so the starting value is overwritten regardless. Consulting a default here would make an old file change meaning. |
| A hand-written `.vxscene` that omits a field | zero for that field | An authored document is a statement about what the component holds. An omitted field means zero, as it always has. The editor writes every field when it saves, so a scene it produced round-trips exactly. |

**Reuse the value you already have.** If your type already has a factory, implement the interface
explicitly and point at it, rather than growing a second public name for the same thing:

```csharp compile
using Vixen.Core;
using Vixen.Ecs;

[Component]
[DataContract]
public struct Thruster : IDefaultComponent<Thruster> {
    public float Thrust;
    public float Fuel;

    /// <summary>A thruster with a full tank.</summary>
    public static Thruster Full => new() { Thrust = 250f, Fuel = 100f };

    static Thruster IDefaultComponent<Thruster>.DefaultValue => Full;
}
```

The member is called `DefaultValue` and not `Default` because an interface member by that name is one
some languages cannot implement. Your own factory can be called whatever you like — most of the
engine's are called `Default`.

Nothing else is required. The engine's component generator already runs in any project that declares
components; it sees the interface at compile time and wires it up, so your game's component reaches
the editor exactly the way the engine's own do. Implementing the interface does not change your
component's size, its layout, or what a saved scene holds — the member is static.

## Examples

Asking for the default in code, which is the runtime counterpart of the Add Component menu:

```csharp no-compile="needs a live world and a component declared in the calling project"
// Zeroed, like every other Add: this is the ECS handing back storage.
world.Add<Health>(entity);

// The value the type declares. Only compiles for a component that declares one.
world.AddDefault<Health>(entity);
```

A component that declares nothing keeps its zero, and cannot be named by `AddDefault` at all:

```csharp compile
using Vixen.Core;
using Vixen.Ecs;

// No interface, no default, no cost. `world.Add<Velocity>(entity)` gives 0,0 — which is exactly
// what a velocity should start at.
[Component]
[DataContract]
public struct Velocity {
    public float X;
    public float Y;
}
```

## See also

- [Components](ecs/components) — what makes a type one, and what the two attributes each claim.
- [Entity queries](ecs/queries) — how the data gets read once it is there.
- [Saving and restoring a world](engine/world-serialisation) — why a load starts from what was
  written rather than from a default.
