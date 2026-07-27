# Vixen.Engine

The layer a game is written against. `Vixen.Ecs` is the storage and the schedule; this is the
vocabulary — transforms, scenes, prefabs, the frame loop, and the `Behavior` API.

Spec: [docs/plan/04-ecs-and-scripting.md](../../docs/plan/04-ecs-and-scripting.md) § Layer 3.

## Transforms and hierarchy

```csharp
var parent = Hierarchy.CreateTransform(world, LocalTransform.At(new(10, 0, 0)));
var child  = Hierarchy.CreateTransform(world, LocalTransform.At(new(0, 5, 0)));
Hierarchy.SetParent(world, child, parent);
```

Five components describe the relationship: `Parent`, `Child` (the head of a list), `Sibling` (the
intrusive doubly-linked list itself), `HierarchyDepth`, and the `LocalTransform` / `WorldTransform`
pair. `Hierarchy` is the only supported way to change the shape — every one of those components can
be written directly, and everything that does will eventually produce a list that loops back on
itself or a depth that disagrees with the parent chain.

Adding or removing a child is O(1) with no allocation, which is what makes reparenting cheap enough
to do in gameplay code rather than only at load.

**`TransformSystem` touches nothing that did not move.** It starts from the chunks whose
`LocalTransform` column has been written since it last ran, and walks down from there; a frame in
which nothing moved visits nothing. Reparenting and creation are caught for free, because both move
the entity to a different archetype and an archetype move stamps every column of the destination
row — neither needs a dirty flag.

**One deviation from doc 04, named rather than approximated.** That design splits archetypes by
`HierarchyDepth` so every level is a sequential sweep over chunks. A component's *value* takes no
part in its archetype here, and making it do so means shared components, which the ECS does not
have. So depth 0 — the roots, which *are* an archetype question (`WithNone<Parent>`) — is a
sequential sweep over spans, and the levels below are walked through the child lists into reused
per-depth buckets. The cost is random access below the roots; the work is still one visit per moved
entity, and a steady state allocates nothing (`ASteadyStateSceneAllocatesNothing` pins that). Adding
shared components later would make the lower levels sweeps too without changing anything a caller
sees.

## The frame

```csharp
using var loop = new EngineLoop(jobs: scheduler);
loop.Add(new MySystem());

while (running) { loop.Frame(stopwatch.Elapsed(), timeScale); }
```

The loop owns no clock. `Frame` is *told* how much time passed, which is what makes a replay from an
input log possible at all: the same sequence of deltas produces the same sequence of frames, with no
reference to a wall clock anywhere.

`FixedStepAccumulator` turns a variable frame delta into a whole number of simulation steps, and
**counts in ticks rather than seconds**. `TimeSpan` is exact in ticks and approximate in `double`
seconds; a hundred milliseconds against a sixtieth-of-a-second step divides to 5.999… in floating
point and owes five steps instead of six. (`TimeSpan.FromSeconds(1d / 60d)` has its own version of
this: it rounds to the nearest millisecond and hands back *seventeen*.)

The catch-up clamp is the part that matters. A frame that took a second owes sixty steps; running
all of them makes the next frame take a second too, which owes sixty more. Clamping discards the
debt, and `DroppedSteps` says so out loud.

## Behaviours

```csharp
sealed class PlayerController : Behavior {
    protected override void Update() => Position += Transform.Forward * Speed * Time.DeltaSeconds;
}

loop.Behaviors.Add(entity, new PlayerController());
```

`Awake` → `OnEnable` → `Start`, with `Start` a frame behind `Awake` so that everything in a batch is
constructed before anything looks up a sibling. Enabling, disabling and destroying are all queued
and applied at the drain, so a behaviour cannot re-enter the loop that is walking it.

**Behaviours are bucketed by concrete type** in contiguous `T[]`s, with the enabled ones in a prefix,
so the update loop is monomorphic and stops at the boundary — a thousand disabled behaviours cost
nothing.

Doc 04 has a generator emit a dispatch method per behaviour type to get that. It is not needed:
`BehaviorBucket<T>` is closed at the `Add<T>` call site where the concrete type is already known, and
its loop is the same monomorphic walk over the same contiguous array. The generator is still owed for
the `[Inspector]` metadata the editor needs, which genuinely cannot be had another way.

## Scenes and prefabs

Several scenes share one world, additively, each unloadable on its own — because a world per scene
means every system runs once per scene and no query can see across them. Membership is a `SceneTag`
component, so unloading is a query and a destroy rather than a list that drifts out of step with the
world.

A `Prefab` is held as a **world of its own**. That gives the capture nothing to serialise and nothing
to reinterpret — the components are already laid out exactly as they will be in the target — so
instantiating is one `CreateMany` per distinct archetype and a row copy each, which is what doc 04
means by "one archetype write per archetype, not entity-at-a-time". It also means a prefab can be
inspected and edited with the same API as anything else.

**The hierarchy is rebuilt, not remapped.** `Parent`/`Child`/`Sibling` hold entity handles, and a
handle copied into another world names a slot in the world it came from. The capture records the tree
as indices and instantiation re-parents, so nothing has to know which fields of which components are
handles. Managed components are copied *by reference*: a hundred instances of a prefab share one
mesh, which is the point of them being managed at all.

## Cameras

A camera is a component, so an entity can be one and a scene can have any number without the engine
holding a list. `CameraMath` derives the view and projection from it and the entity's world
transform — **reverse-Z in both projection modes**, because the rest of the engine clears depth to 0
and tests `GREATER`, and a projection that disagreed would render a picture that is correct except
that everything is behind everything else.

Licensed under Apache-2.0.
