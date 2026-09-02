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

## Changing structure while iterating

Adding or removing a component moves an entity's row between chunks, which invalidates the very span
the loop that asked for it is walking. The answer is not to detect that — it is to record the change
somewhere it cannot happen yet.

```csharp
var buffer = new CommandBuffer(world);

world.QueryWithEntity(dying, (Entity e, ref Health h) => {
    if (h.Value <= 0) { buffer.Destroy(e); }
});

buffer.Playback();          // at the sync point, when nothing is iterating
```

`Create()` hands back a **placeholder** — a negative id the world refuses, usable in later commands
on the same buffer, resolved to the entity playback actually creates.

For jobs, `buffer.AsParallelWriter()` gives one channel per thread and every call takes a sort key.
Playback orders by sort key first, so **the result is a function of the work and not of how the
scheduler distributed it** — which is what a fixed-step simulation, a replay and a rollback all stand
on. The sort key must identify the work item; commands sharing one across threads have no defined
order between them.

**The buffer is lenient where `World` is strict.** `Add` overwrites instead of refusing, `Remove` and
`Destroy` do nothing if there is nothing to do, and a command naming an entity that an earlier
command destroyed is skipped. A recorder runs during iteration and cannot look at the world to find
out whether its change is redundant; a caller that *can* look uses `World` and gets told when it is
wrong.

## Systems

```csharp
[UpdateInGroup(SystemPhase.FixedUpdate)]
[Reads(typeof(Velocity))]
[Writes(typeof(Position))]
sealed class IntegrateSystem : SystemBase {
    public override JobHandle Update(in SystemContext context, JobHandle dependency) =>
        context.Jobs!.ScheduleParallel(new Integrate(context.World), count, 0, dependency);
}

using var runner = new SystemRunner(world, jobs);
runner.Add(new IntegrateSystem()).Add(new CollisionSystem());
runner.RunPhase(SystemPhase.FixedUpdate, time);
```

`Update` returns a handle instead of waiting. The runner has already worked out which systems
conflict, so a system that returns promptly lets every non-conflicting system after it start
immediately, and **a phase costs its critical path rather than its sum**.

Conflict is decided from the declared access: read against read is not one, write against anything
is, and a write implies a read so "only writes X" and "only reads X" are never mistaken for
disjoint. **A system that declares nothing conflicts with everything** — the only safe reading of "I
did not say".

**Or the access is read out of the body.** `[InferAccess]` on a partial system class asks
`Vixen.Engine.Generators` to walk that class's own query calls and emit the other half of it,
implementing `IDeclaredAccess` — which is the interface rather than the attributes because
`Declare().Write<Position>()` *assigns* `Position` its component id, where an attribute can only look
one up.

⚠ **It is opt-in, and where the direction is not knowable it errs towards writing.** `Values<T>` is a
write and `ReadValues<T>` a read, exactly as `Get` and `Read` are; but the delegate and visitor forms
take every component by `ref` whether or not the body assigns through one, so their type arguments
are all inferred as writes. Over-declaring costs parallelism; under-declaring is a data race. A
`WithNone<T>` filter is neither — an entity that matched it has no `T` for anyone to race over. An
explicit `[Reads]`/`[Writes]` on the same class overrides the inference and the generator says so
(`VXS0410`) rather than emitting a declaration nothing reads, and a class it could infer nothing from
is told (`VXS0411`) rather than left silently undeclared.

**The same declaration is handed to the job scheduler.** For the length of a system's `Update` the
runner opens a `JobAccessScope` carrying that system's access, so every job the system schedules
carries it too and the scheduler refuses a schedule that lets two conflicting systems' jobs run at
once. It costs nothing in a release build — the whole mechanism is under `DEBUG || VIXEN_JOB_SAFETY`
— and one declaration feeds both the ordering and the check, because two statements of the same
thing would agree everywhere except where it matters.

⚠ **What that catches is a system that drops its handle.** Returning `dependency` instead of the
handle for the work just scheduled leaves the runner waiting for nothing, so the job runs on into the
next system's turn — and the conflict graph, which is about systems and not about jobs, cannot see
it. `Vixen.Core.Threading/README.md` § The safety system has the rest, including why a clean run and
a run that never checked anything have to be told apart by a counter.

A phase is bracketed: the world version moves on, the systems run, their work is completed, the
command buffer is played back. Completing before playback is not a detail — a structural change
moves rows between chunks, and a job still walking one would be walking overwritten memory.

`runner.Graph.ToDot()` and `.ToMermaid()` dump the schedule. The fixed-step accumulator is *not*
here: how many times `FixedUpdate` runs in a frame is the game loop's decision.

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

**A destroyed handle can be given back, and only when nothing has happened to the slot.**
`TryRecreate` is what undoing a delete needs: `Create` hands out whatever slot is free, so a redo
would produce a *different* handle and every reference still holding the old one would be quietly
addressing nothing.

It is allowed only when the slot's version is *exactly* one past the requested one — meaning the
entity was destroyed and nothing has been issued since. That is the whole safety argument, and the
restriction is not conservatism: if the slot had since been created as `(id, 4)` and destroyed again,
restoring `(id, 3)` would let the next destroy-and-create hand out `(id, 4)` a second time, to a
third entity. A handle naming two entities across its life is precisely what the version prevents,
and rewinding further would reintroduce it. `A_slot_that_was_taken_in_the_meantime_is_refused_for_ever`
is that test.

⚠ **So it can fail, and a caller needs an answer.** One other `Create` is enough to take the slot,
because the free list is last-in-first-out. The answer is a stable identity of the caller's own to
remap — which is why the editor's `SceneDocument` keeps one per entity.

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

**Serialising a world, which lives in `Vixen.Engine` rather than here.** `WorldSerializer` is the
one that writes a whole world down, and it is up a layer because everything it needs is: the binders
that turn a component name into a chunk write are `SceneComponentRegistry`'s, and this assembly
references no serializer on purpose. A seam here filled from up there would be a second way to say
the same thing, with the layer boundary holding it up.

⚠ **What that serialiser cannot do is a fact about this assembly, and is worth knowing here.** An
`Entity` is a slot, a generation and a world id, so it cannot be written down and read back meaning
the same thing — `World.CopyComponentsFrom` says the same and leaves the fix-up to its caller.
`Parent`, `Child` and `Sibling` are therefore never written: the hierarchy travels as a table of
indices and the links are rebuilt. A component of a game's own that stores an `Entity` is not
covered, because nothing generic can tell which of a struct's four-byte fields are handles, and
`TryRecreate` does not help — it needs a slot the world has already issued and destroyed, which a
world being restored into has not.

Licensed under Apache-2.0.
