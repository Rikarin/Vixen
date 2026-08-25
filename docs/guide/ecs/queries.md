---
title: Entity queries
slug: ecs/queries
kind: guide
area: ECS
summary: Iterating the entities that have a given set of components.
api: [T:Vixen.Ecs.QueryDescription, T:Vixen.Ecs.World, T:Vixen.Ecs.Chunk]
tags: [ecs, iteration, performance]
since: 0.1
status: stable
related: [ecs/components, ecs/system-order]
---

## What it is

A query is a description of a set of components, and iterating it visits every entity that has them.
`QueryDescription` is the description; `World` is what you ask; `Chunk` is what an answer is made of.

## What it is for

An entity-component-system stores components in columns rather than in objects, so "every entity with
a position and a velocity" is not a list anybody keeps — it is a question about which archetypes
match. A query is how you ask it once and iterate the answer without allocating, and it is the only
way to read component data in bulk.

You do not want a query when you have an entity in hand and want one component from it: that is a
direct lookup, and going through a query to reach it is slower and reads worse.

## Using it

Describe the set, then iterate. `WithAll` is the common case; `WithAny` and `WithNone` narrow it
further, and `WithChanged` limits the iteration to chunks written since a version you remember.

```csharp compile
using Vixen.Core;
using Vixen.Ecs;

[Component]
public struct Position {
    public float X;
    public float Y;
}

[Component]
public struct Velocity {
    public float X;
    public float Y;
}

public static class Movement {
    public static void Step(World world, float delta) {
        var moving = new QueryDescription().WithAll<Position, Velocity>();

        foreach (var chunk in world.Chunks(moving)) {
            var positions = chunk.Values<Position>();
            var velocities = chunk.ReadValues<Velocity>();

            for (var index = 0; index < chunk.Count; index++) {
                positions[index].X += velocities[index].X * delta;
                positions[index].Y += velocities[index].Y * delta;
            }
        }
    }
}
```

Four forms exist over the same primitive, and they differ in what they cost rather than in what they
can express: chunks give you a `Span` per column, the delegate forms give you one entity at a time,
and the struct-visitor form inlines the body into the loop.

⚠ **Adding, removing, creating or destroying inside one of these loops moves rows between chunks —
the storage the spans point at.** Record it instead and let it apply where nothing is iterating; see
[structural change during iteration](structural-changes.md).

## Examples

The stress-test sample builds a world and iterates it every frame:

{{ snippet Samples/04-EcsStressTest/Program.cs#docs:query }}

## See also

- [Components](ecs/components) — what a query is a query *of*.
- [Structural change during iteration](structural-changes.md) — how to spawn or destroy from inside
  one of these loops without invalidating it.
- [Reading a frame's order without running it](system-order.md) — which system reaches these loops
  first, and how to find out from an assembly.
