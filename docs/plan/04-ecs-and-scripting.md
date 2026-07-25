# 04 — ECS and Scripting

Your brief: "Support ECS natively and probably base the 'mono behavior' style scripting on top of it
if it's possible." It is possible, it is the right architecture, and Unity itself is converging on it.
This document specifies both layers and the seam between them.

## Layer 1 — `Vixen.Ecs`, the archetype core

Design derived from Arch (ADR-004), which is itself derived from Unity DOTS and flecs. The parts that
matter:

### Storage

```
World
 ├── Archetype  (a unique component-type set)
 │     ├── ComponentTypes: BitSet + sorted ComponentTypeId[]
 │     ├── Chunk[]         (16 KB each, SoA)
 │     │     ├── Entity[]                  ← always present
 │     │     ├── Array<T0>, Array<T1>, ... ← one contiguous array per component type
 │     │     ├── Count, Capacity
 │     │     └── ChangeVersion[]           ← per-component-type write version
 │     └── Edges: Dictionary<ComponentTypeId, Archetype>  (add/remove graph)
 └── EntityStore: EntityInfo[]  (archetype ref, chunk index, row index, version)
```

- **`Entity` is `readonly record struct Entity(int Id, int Version, short WorldId)`** — 8 bytes,
  copyable, comparable, hashable. Stale references are detected by version mismatch, never by a crash.
- **Chunk size is 16 KB** (Arch uses this; it fits L1 on most targets after accounting for the entity
  array). Configurable per archetype for pathological component sizes.
- **Archetype transitions** go through the edge graph: adding component `T` looks up
  `archetype.Edges[T]` (cached) instead of recomputing a type-set hash. This is the single biggest
  perf difference between a naive and a good archetype ECS.
- **Managed components** (anything not `unmanaged`) are supported but discouraged: they live in a
  per-world `ManagedComponentStore` (a `ChunkedArray<object>` with a free list), and the chunk stores
  an `int` handle. They exist because `Behavior`, `Mesh`, `Material`, and `Texture` references are
  reference types and pretending otherwise would be dogma.
- **Zero-size tag components** consume no chunk memory, only a bit in the archetype mask.

### Queries

```csharp
// Declarative description
var query = new QueryDescription()
    .WithAll<Position, Velocity>()
    .WithAny<Player, Npc>()
    .WithNone<Frozen>();

// Generated, inlined iteration — no delegate, no boxing
world.Query(query, static (ref Position p, ref Velocity v) => p.Value += v.Value * dt);
```

- Query matching is a `BitSet` test against each archetype's mask, cached per query in a
  `MatchedArchetypes` list invalidated only when a new archetype is created.
- `Vixen.Ecs.Generators` emits strongly-typed enumerators and `IForEach<T0..Tn>` struct-visitor
  overloads for arities 1–16, replacing Arch's T4 templates with an incremental generator.
- **`ref struct` enumerators with `allows ref struct`** so a query can hand out `Span<T>` slices of a
  chunk and the caller can SIMD over them directly — the path the renderer's culling uses.
- **Change filtering.** `.WithChanged<Position>(sinceVersion)` skips chunks whose `ChangeVersion` for
  that component predates the caller's last-seen version. This is what makes "only update transforms
  that moved" and "only re-layout UI whose props changed" cheap, and it is why Vixen's ECS needs to
  own this rather than adopt one that does not model it.

### Structural change safety

Adding/removing components during iteration invalidates chunks. Two mechanisms:

1. **`CommandBuffer`** — records `Create`/`Destroy`/`Add`/`Set`/`Remove` into a thread-local buffer,
   played back at a system-graph sync point. Jobs may only mutate through it.
2. **`EntityCommandBuffer.ParallelWriter`** — per-job-index sub-buffers merged deterministically by
   sort key, so parallel playback is reproducible.

Direct structural mutation on the main thread outside iteration is allowed and fast; the analyzer
flags it inside a query body.

### Events and hooks

Component added/removed/set events exist behind a compile-time flag (`VIXEN_ECS_EVENTS`), as in Arch,
because the branch costs something in the inner loop. The engine itself does **not** rely on them —
it uses change versions. They exist for editor tooling and user code.

### Determinism and persistence

- Entity IDs are dense and reused; **persistent identity** for saving/loading and for editor
  references is a separate `GuidComponent` (or an external `Guid → Entity` map maintained by
  `Vixen.Engine`). Never serialise a raw `Entity`.
- World serialisation walks archetypes in a canonical order (sorted component-type-id sequence) so a
  saved scene is byte-identical across platforms.
- A fixed-step world can be checkpointed and replayed from an input log — the basis for the
  determinism tests and for netcode later.

## Layer 2 — the system scheduler

```csharp
public interface ISystem
{
    void Initialize(SystemContext context);
    JobHandle Update(in SystemContext context, JobHandle dependency);
    void Dispose();
}
```

- Systems declare component read/write sets via `[Reads(typeof(Position))]` /
  `[Writes(typeof(Velocity))]`, or — better — the generator infers them from the queries in the
  system's body and emits the declaration. Explicit attributes override.
- The scheduler builds a DAG from those sets: two systems with disjoint write sets run concurrently;
  a write-after-read edge becomes a `JobHandle` dependency, not a barrier.
- **Ordered phases** give predictability where it is needed:
  `EarlyUpdate → Input → FixedUpdate* → Update → Animation → LateUpdate → PreRender → Render → PostRender`.
  Within a phase, order is the dependency DAG; between phases, a hard sync point.
  `FixedUpdate` runs 0..n times per frame from an accumulator with a max-catch-up clamp.
- `[UpdateBefore]`/`[UpdateAfter]`/`[UpdateInGroup]` for explicit ordering, validated at graph build
  time with a cycle error naming the participants.
- The whole graph is dumped as a DOT/Mermaid diagram from the CLI (`vixen doctor systems`) — an
  underrated debugging aid that costs nothing.

## Layer 3 — `Behavior`, the MonoBehaviour-shaped API

This is the API 95% of users touch. It must feel like Unity while being ECS underneath.

```csharp
public sealed partial class PlayerController : Behavior
{
    [Inspector] public float Speed = 5f;
    [Inspector] public Prefab? BulletPrefab;

    private Transform _transform = null!;

    protected override void Awake()   => _transform = Get<Transform>();
    protected override void Update()
    {
        var move = Input.GetAxis2D("Move");
        _transform.Position += new Vector3(move.X, 0, move.Y) * Speed * Time.Delta;
        if (Input.GetButtonDown("Fire") && BulletPrefab is not null)
            Instantiate(BulletPrefab, _transform.Position, _transform.Rotation);
    }
}
```

### How it maps down

| Behaviour concept | ECS reality |
|---|---|
| A `Behavior` instance | A managed component `BehaviorRef` on the entity holding a handle into the world's `BehaviorStore` |
| `Get<T>()` for a component | Generated typed accessor reading the entity's chunk row — a `ref T` return, no dictionary lookup after the first call caches archetype+row |
| `Transform`, `Rigidbody`, … as "components you get" | Thin `ref struct` façades over the underlying ECS component data, so `_transform.Position += …` writes the chunk directly and bumps its change version |
| `Update()` | `BehaviorUpdateSystem` iterates a **flat, order-sorted array of behaviours grouped by concrete type**, calling a generated dispatch method per group |
| `Awake/OnEnable/Start/OnDisable/OnDestroy` | Lifecycle queues drained at defined phase points; `Start` deferred to the frame after `Awake`, exactly like Unity, because users depend on that ordering |
| `[Inspector]` fields | Generated metadata for the editor's property drawers + generated serializer |
| Coroutines | **Not** Unity-style `IEnumerator` coroutines. Instead: `await NextFrame()`, `await Seconds(2f)`, `await Until(() => x)` on a frame-synchronous `VixenTaskScheduler` — async/await with a custom scheduler gives the same ergonomics with a real debugger, cancellation, and exception propagation |

### Making it fast

The naive version — `foreach (var b in allBehaviors) b.Update()` — is a virtual call per entity plus
a cache miss per entity, which is precisely the thing ECS exists to avoid. Instead:

1. `Vixen.Ecs.Generators` sees `partial class PlayerController : Behavior` overriding `Update`, and
   emits into a per-assembly `BehaviorDispatch` table: a non-virtual, direct-call
   `static void Update_PlayerController(Span<PlayerController> batch)` loop.
2. `BehaviorStore` keeps behaviours **bucketed by concrete type** in contiguous arrays. Iteration is
   `for (int i = 0; i < batch.Length; i++) batch[i].Update();` on a monomorphic type — the JIT
   devirtualises and the instances are contiguous.
3. Behaviour types opting into `[BehaviorJob]` get their batch dispatched across the job system
   instead (with the read/write safety check), giving parallel `Update` for the types that can take
   it.
4. `[SkipIfDisabled]` buckets split active/inactive so disabled behaviours cost nothing.

This is measurably slower than a pure ECS system and dramatically faster than Unity's
MonoBehaviour path. It is the honest trade: convenience where users want it, `ISystem` where they
need throughput. Both are first-class and documented as such.

### The rule that keeps this coherent

**Component data lives in ECS. Behaviour holds no state that isn't either a component or private
scratch.** Enforced by an analyzer: a public/`[Inspector]` field on a `Behavior` is either a
blittable value (serialised into a generated component struct) or an asset/prefab reference (a
managed handle). A `Behavior` may not hold a `List<Entity>` of "its children" — it asks the hierarchy.
Without this rule, the ECS below becomes decoration and the whole design collapses into Unity 2010.

## Transforms and hierarchy

Its own section because it is the subsystem everyone gets wrong.

```
struct LocalTransform  { Vector3 Position; Quaternion Rotation; Vector3 Scale; }   // authored
struct WorldTransform  { Matrix4x4 Value; }                                        // derived
struct Parent          { Entity Value; }
struct Child           { Entity First; }        // intrusive linked list
struct Sibling         { Entity Next; Entity Prev; }
struct HierarchyDepth  { short Value; }         // tag component; archetype-splits by depth
```

- The hierarchy is an intrusive linked list, so adding/removing a child is O(1) with no allocation.
- **`HierarchyDepth` splits archetypes by depth**, so `TransformSystem` iterates depth 0, then 1, then
  2 — each level fully parallel within itself, with a job dependency between levels. No recursion, no
  sorting per frame, no `Dictionary` traversal. This is the DOTS approach and it is the correct one.
- Only dirty subtrees update: writing `LocalTransform` bumps the chunk's change version; the system
  queries `.WithChanged<LocalTransform>()` and propagates a dirty flag down. A static scene costs
  nothing.
- `Transform` (the user-facing façade) exposes `LocalPosition`/`Position`/`LocalRotation`/`Rotation`/
  `LossyScale`/`Forward`/`Right`/`Up`/`LookAt`/`TransformPoint`, reading `WorldTransform` and writing
  `LocalTransform` with an inverse-parent multiply — same semantics as Unity, no surprises.

## Scenes, prefabs, and the editor seam

- A **Scene** is a serialised set of entities + components + behaviour state, referenced
  addressably. Multiple scenes load additively into one `World`; each entity carries a
  `SceneTag` component so unload is a query-and-destroy.
- A **Prefab** is a serialised entity subtree with a compiled "instantiate" plan: a flat list of
  (archetype, component blob) that `Instantiate` bulk-creates in one archetype write per archetype —
  not entity-at-a-time. Prefab instantiation of a 200-entity prefab should be one-digit microseconds.
- **Prefab variants/overrides** follow Unity's model: an instance stores a sparse override list
  (property path → value) against its source prefab, so editing the prefab propagates. This is
  genuinely hard and is scheduled explicitly in the roadmap rather than assumed.
- Editor mutations go through `IWorldCommand` objects on the undo/redo stack, so the editor's
  entity manipulation and the runtime's `CommandBuffer` share the same mutation vocabulary.

## Tests

| Area | Test |
|---|---|
| Archetype transitions | Property test: random add/remove sequences leave component values intact and archetype masks correct |
| Query correctness | Brute-force oracle: `WithAll/Any/None` results compared against a naive linear scan over all entities |
| Change versions | A system that writes nothing must not mark chunks dirty; a system that writes must mark exactly its chunks |
| Structural safety | `CommandBuffer` parallel playback is deterministic across 100 runs with randomised job scheduling |
| Hierarchy | Randomised reparent/destroy sequences vs. a reference tree implementation; depth invariants hold |
| Behavior lifecycle | Golden ordering test: `Awake` all → `OnEnable` all → `Start` all, `Start` deferred one frame, destroy ordering, disable during iteration |
| Determinism | Two worlds fed identical input logs for 10 000 fixed steps produce identical serialised state |
| Performance | Ported Arch benchmarks; must match or beat Arch 2.1 on create/destroy/get/set/iterate (ADR-004) |
