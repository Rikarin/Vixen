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

⚠ **Linking prepends, and `SetParentAfter` exists because undo cannot live with that.** Putting a new
child at the head is the right default — it is the O(1) one — and it means undoing a delete or a
reparent returns the entity to the *front* of its old parent's children rather than to where it was.
A user who moves the third of five children and presses Ctrl+Z has not undone anything.

**The position is recorded as a neighbour, not an index.** `PreviousSiblingOf` before the move,
`SetParentAfter` to put it back. An index would have to be counted from the head, be invalidated by
every insertion in front of it, and mean nothing once a sibling was itself deleted; the entity that
used to be in front survives all three, and is what the list already stores.

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

## Coroutines

```csharp
protected override void Start() => Run(OpenDoor());

async Coroutine OpenDoor() {
    await Seconds(0.5f);
    while (Angle < 90f) { Angle += 60f * Time.DeltaSeconds; await NextFrame(); }
    await Until(() => PlayerIsInside);
    await Seconds(2f);
}
```

`async`/`await` rather than Unity's `IEnumerator`, which is doc 04's call and the right one: real
stack traces, breakpoints across the `await`, working `try`/`finally` and `using`, and exceptions
that propagate. Alongside `await NextFrame()` there are `Seconds`, `UnscaledSeconds`, `Until`,
`While`, `Coroutine.WhenAll` and `StopCoroutines()`.

**The scheduler is a list per resume point, drained on the loop thread, and nothing else.** No timer,
no thread pool, no synchronisation context. Resumption order is the order the waits were made, which
is what lets a coroutine be as deterministic as a system — the swap-back removal used everywhere else
in this codebase is exactly wrong here, so the drain compacts in order instead.

**Nothing resumes in the frame it suspended in**, `await Seconds(0f)` included. A zero wait that
completed synchronously would turn `while (true) await Seconds(0f);` into a hang, and people write
that.

**Zero allocation per start**, measured: `Coroutine`'s method builder forwards to
`PoolingAsyncValueTaskMethodBuilder`, the waiting entries are structs in reused lists, and the
bookkeeping object comes off a free list. A Release build allocates **0 bytes** per start against 160
for the same method written as a plain `async ValueTask`. (A Debug build costs 88 — the C# compiler
emits an async state machine as a *class* there so the debugger can inspect it, which every `async`
method in the process pays regardless.)

**Cancellation is per-owner.** Destroying a behaviour, or calling `StopCoroutines()`, cancels
everything it has suspended — including a coroutine several `await`s deep, which no per-coroutine
handle could reach, because a nested coroutine's continuation is held by its caller's state machine
rather than by the scheduler. Cancelling throws into the coroutine rather than abandoning it, so
`finally` blocks run.

Four resume points — `Update`, `LateUpdate`, `FixedStep`, `EndOfFrame` — and `FixedStep` ticks with
the steps rather than the frames, so a frame owing three steps resumes a waiter three times.

Awaiting a real `Task` takes the coroutine off the loop thread, where it must not touch the world;
`await ResumeOnLoop()` is the way back, and it is the only wait that may be made from another thread.
Every other wait reads the clock and the frame counter, so making one off-thread throws rather than
racing.

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

**An instance's children come out in the order they were captured.** Linking prepends — O(1), which
is why the child list is intrusive — so instantiating in capture order reversed every child list,
invisibly, until draw order or a script's walk over its children depended on it. Both the prefab and
the compiled-scene load link back to front, and a test holds each of them to it.

## Compiled scenes and prefabs

```csharp
var asset = Serializer.Read<SceneAsset>(chunk);   // what a content build wrote
var scene = asset.Load(scenes);                   // additive, tagged, unloadable
```

A `.vxscene` is YAML because a person merges it. A `SceneAsset` is a chunk because a player loads it
on a frame budget, and `SceneCompiler` in `Vixen.Editor.Assets` is the only thing that has ever seen
both. What lives here is the runtime half: the format, and turning it into a world.

**Entities are grouped into blocks by archetype, and a block is one `CreateMany`.** A two-thousand
entity level of six shapes is six archetype lookups and six bulk creates rather than two thousand of
each — doc 08's "archetype-ordered blobs for bulk world load", concretely. Within a block, each
component is a column: every entity's value for one component, back to back, in the order the load
walks them.

**The archetype is the column *names*, rebuilt at load.** A dense component id is assigned in the
order a process first touches a type, so it means nothing outside the process that assigned it; a
component's `[DataContract]` alias is what survives, and `SceneComponentRegistry` is what turns one
back into a write into a chunk. A scene naming something this build does not have **fails to load and
says which name** rather than producing an enemy that is quietly missing its `Health`.

**What a scene may name is `[Component]` plus `[DataContract]`, and nothing else is asked of
anybody.**

```csharp
[Component]                     // the ECS may attach it
[DataContract]                  // it can be described and turned into bytes
public struct Health { public int Value; }
```

`Vixen.Engine.Generators` emits a `[ModuleInitializer]` per assembly declaring every such type, so a
game's components reach the registry — and the inspector, the Add Component menu, the `.vxscene` and
the compiled scene — with no registration call anywhere. The engine's own `Camera` arrives by exactly
that route; it used to be a hand-written call in a static constructor, which was the only thing that
made the engine's components different from a game's.

⚠ **The conjunction is what keeps a handle out of a scene.** `PhysicsBody` carries `[Component]` and
not `[DataContract]`, because a body handle means nothing outside the world that issued it — so it is
excluded by construction, with no denylist and no opt-out attribute. A component that answers only
"the ECS may attach it" is a bridge's own bookkeeping, and that is exactly the thing a scene file must
never carry.

⚠ **A module initializer runs when its assembly loads.** A component nothing has referenced yet is not
declared yet, which is why the editor loads a project's game assemblies eagerly and why a scene naming
a component from an assembly the game never touches fails at the load rather than silently.

**The transform is three columns of the scene's own, not a component anybody can name**, because
every entity in a scene has one and the alternative is a file that says two different things about
where an entity is. **Names are a table on the asset and never a component** — the editor's argument
for holding them in a map is unchanged, and thirty bytes per entity in every chunk of a shipping
build is still the wrong place for them; a build that wants the bytes back turns them off in the
importer's settings.

A `PrefabAsset` is the same content with one root, and `ToPrefab()` builds the template by
instantiating it once into a staging world and capturing that. It costs one instantiation, once, per
prefab asset — against a second capture format for the same components with its own way of being
subtly different.

## Cameras

A camera is a component, so an entity can be one and a scene can have any number without the engine
holding a list. `CameraMath` derives the view and projection from it and the entity's world
transform — **reverse-Z in both projection modes**, because the rest of the engine clears depth to 0
and tests `GREATER`, and a projection that disagreed would render a picture that is correct except
that everything is behind everything else.

## Debug geometry

```csharp
draw.Line(contact, contact + normal, Color4.Red, seconds: 2f);
draw.Capsule(feet, head, radius, Color4.Green);
draw.Frustum(new BoundingFrustum(camera.ViewProjection), Color4.Yellow);
draw.Arrow(position, position + velocity, Color4.Cyan);
draw.Text(position, name, Color4.White, size: 0.2f);
```

Immediate mode: a call site owns nothing, creates nothing and disposes nothing, and removing the
investigation means removing the call. Lines, rays, arrows, boxes (axis-aligned and oriented),
spheres, circles, capsules, cones, frustums, crosses, axes, world labels, and screen-space lines,
rectangles, fills and text.

Every shape is lines, including the round ones and the lettered ones — a sphere is three rings and
reads as a sphere, where a mesh would mean a second pipeline plus a depth decision plus a lighting
decision for geometry whose whole job is to be unmistakably not part of the scene. Text is
`DebugFont`'s strokes for the same reason, and because a debug overlay has to work in the frame where
the font atlas is the thing that is broken.

**This is the accumulator, not the renderer.** `Vixen.Engine.Renderer` is the other half: it drains
the three lists once a frame into two line draws. That split is what lets `Vixen.Physics`,
`Vixen.Navigation` and `Vixen.Audio` produce debug geometry without linking a graphics API.

`DebugDrawSystem` ages the geometry in `PostRender` — after the draining, or a line asked for during
a frame would never be seen.

## Diagnostic overlays

```csharp
var overlays = new DiagnosticOverlays();
overlays.Add(new FrameStatsOverlay());
overlays.Add(new FrameGraphOverlay());
overlays.Add(new LogOverlay(sink));
overlays.Add(new ConsoleOverlay(commands));
overlays.RegisterCommands(commands);

loop.Add(new DiagnosticOverlaySystem(overlays, draw) { Viewport = target.Size });
```

The toggleable panels [docs/plan/13](../../docs/plan/13-diagnostics.md) § Diagnostic overlays asks
for, drawn out of `DebugDraw`'s screen-space list — so they are present **in every build**, with no
editor attached, no interface running and no font asset resident. A panel is a background fill, a
border, a title and rows of text, all of it line segments.

`IDiagnosticOverlay` is the seam. The four whose numbers are already in `Vixen.Core.Diagnostics` live
here — frame stats with a frame-time graph, a mini flame chart off the profiler's rings, the tail of
the log ring, and a console. The rest belong to the subsystems that own their data: `Vixen.Physics`
draws colliders and contacts into the accumulator directly, and `Vixen.Audio` registers an
`AudioOverlay`.

⚠ **The console does not read a keyboard.** `Type`, `Backspace`, `Submit` and the history moves are
pushed in by the host, because which device produces a character — and whether the game should stop
seeing it — is a platform's question. `IsCapturingInput` is what a host polls to know that typing
`reload` must not also make the player reload.

Licensed under Apache-2.0.
