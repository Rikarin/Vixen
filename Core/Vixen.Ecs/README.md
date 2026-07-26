# Vixen.Ecs

The archetype entity-component-system. Entities are generation-checked handles; components live
struct-of-arrays in 16 KB chunks grouped by the exact set of components their entities have.

Spec: [docs/plan/04-ecs-and-scripting.md](../../docs/plan/04-ecs-and-scripting.md) § Layer 1.

## The shape of it

```
World
 ├── Archetype  (one per unique component set)
 │     ├── Signature: sorted ComponentTypeId[]  +  Mask: BitSet
 │     ├── Chunk[]   — one byte[], entity column first, then one column per component
 │     │     └── Versions[] — per column, the world version it was last written at
 │     └── Edges: add/remove → Archetype
 ├── EntityInfo[] — archetype, chunk, row, version
 └── ManagedComponentStore<T> — for components that are, or contain, a reference
```

## Iterating

Four forms, all over the same primitive.

```csharp
var moving = new QueryDescription()
    .WithAll<Position, Velocity>()
    .WithAny<Player, Npc>()
    .WithNone<Frozen>();

// 1. Chunks — the primitive. A Span per column, contiguous, SIMD it if you like.
foreach (var chunk in world.Chunks(moving)) {
    var positions = chunk.Values<Position>();
    var velocities = chunk.ReadValues<Velocity>();
    for (var i = 0; i < chunk.Count; i++) { positions[i].X += velocities[i].X * dt; }
}

// 2. Delegate, per entity.
world.Query(moving, static (ref Position p, ref Velocity v) => p.X += v.X);

// 3. Delegate, with the entity handle.
world.QueryWithEntity(moving, static (Entity e, ref Position p) => { });

// 4. Struct visitor — no delegate to dispatch through, so the body inlines into the loop.
var sum = default(SumSpeed);
world.ForEach<SumSpeed, Velocity>(moving, ref sum);
```

Arities 1–16 of the last three, plus `WithAll`/`WithAny`/`WithNone`/`WithChanged`, are emitted by
`Vixen.Ecs.Generators`. Two thousand lines whose only variable is a number, and every arity
type-checks whether or not the body is right — which is exactly the code a human should not be
writing sixteen times.

**Change filtering** is per chunk, per component:

```csharp
var moved = new QueryDescription().WithChanged<LocalTransform>();
foreach (var chunk in world.Chunks(moved, since: lastSeen)) { … }
lastSeen = world.Version;
```

`since` is *strictly* after, and that is the contract: a system remembers `world.Version` when it
finishes, the scheduler advances the version at the sync point, and the next run sees what changed
in between — but never its own writes from last time, which at-or-after would hand back for ever.
`WithChanged<T>` also requires `T`, because filtering on a change to something the entity does not
have would match everything.

## Decisions worth knowing about

**Three storage classes, not one.** A plain struct lives inline in the chunk. A tag
(`ITagComponent`) occupies a bit in the mask and no memory at all. Anything that is or contains a
reference lives in a per-type store and the chunk holds a four-byte handle — because a chunk is a
`byte[]` and the garbage collector cannot see references inside one. A `struct` with a `string` in
it is managed; that is the case people forget.

**Tags are declared, not inferred.** An empty C# struct still measures one byte, so "has no fields"
cannot be read from a size. Inferring it would mean a struct that grows a field silently changes
storage class — code that compiles, runs, and loses data. `ITagComponent` says it out loud, and a
type that implements it and then grows a field fails at registration.

**`Get` marks the chunk dirty; `Read` does not.** Handing out a `ref` is treated as a write whether
or not one happens, because there is no way to find out afterwards. That makes "a system that writes
nothing must not mark chunks dirty" something the call site declares rather than something a
convention hopes for.

**The world id is in the handle.** Passing an entity from the editor's world to the play world is a
real mistake with no other way to detect it: both slots exist, and the versions agree far more often
than anyone expects.

**Removal fills the hole from the tail chunk**, not just from within the row's own chunk. Without
that, a world that creates and destroys in waves keeps every chunk it ever needed, each half empty,
and pays for all of them in every query for ever. `SurvivorsStayPackedIntoAsFewChunksAsTheyNeed`
is the test; it is invisible to any correctness assertion.

## What is deliberately not here

**Thread safety.** A world is single-threaded for structural change by design. Reads and component
writes parallelise across chunks under the scheduler's declarations; a lock here would tax every one
of those to buy safety in a case the design already rules out.

**Component type ids are never persisted.** They are assigned in first-touch order, so they are
stable within a process and meaningless outside one. A serialised world names component types by
their `[DataContract]` alias and sorts by that.

Licensed under Apache-2.0.
