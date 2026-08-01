---
title: Components
slug: ecs/components
kind: guide
area: ECS
summary: The data an entity has, and what makes a type one.
api: [T:Vixen.Core.ComponentAttribute, T:Vixen.Core.DataContractAttribute]
tags: [ecs, components, scenes]
since: 0.1
status: stable
related: [ecs/queries, engine/world-serialisation, engine/players-and-possession]
---

## What it is

A component is a plain struct carrying data, marked with `[Component]`. The ECS stores every
component of one type in a column, so an entity is not an object that owns its data — it is a row
number, and its components are what the columns at that row hold.

## What it is for

Storing game state so that iterating it is fast. A hundred thousand entities with a position each is
one contiguous array of positions, which the CPU reads at memory speed and the compiler can
vectorise. The same data as fields on a hundred thousand objects is a hundred thousand cache misses.

You do not want a component for something only one thing in the world has — a camera setting, the
frame's time. That is a service or a singleton, and putting it in a column of length one costs an
archetype for nothing.

## Using it

`[Component]` alone is enough for the ECS to store the type. **With `[DataContract]` beside it, the
type also becomes something a scene can place** — it appears in the Add Component menu, it serialises
into a `.vxscene`, and the inspector draws it. The two attributes answer two different questions, and
the pair is what makes a component authored rather than internal.

```csharp compile
using Vixen.Core;

// The ECS may attach this, and nothing else can: no scene places it, no inspector shows it.
[Component]
public struct Spin {
    public float Radians;
}

// A scene can place this one, and the inspector will draw both fields.
[Component]
[DataContract]
public struct Buoyancy {
    public float Density;
    public float DragCoefficient;
}
```

Keep a component small. Its size decides how many rows fit in a 16 KB chunk, and a component that
carries two things a system never reads together is two components.

## Examples

A component with a reference in it cannot live in a column, so it goes to the managed store instead —
which is slower, and worth knowing you have asked for:

```csharp no-compile="the managed-component store is not part of the packable surface yet"
[Component]
public struct Named {
    public string Label;   // a reference: this component is stored managed, not in the chunk
}
```

## See also

- [Entity queries](ecs/queries) — how the data gets read.
- [Saving and restoring a world](engine/world-serialisation) — why `[DataContract]` is what decides
  whether a component survives being written down.
