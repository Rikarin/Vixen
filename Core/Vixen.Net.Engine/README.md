# Vixen.Net.Engine

Where `Vixen.Net` and `Vixen.Engine` meet. `SyncVar`, `SyncList` and `NetworkModule` — the
behaviour-facing authoring style over the same replication mechanism.

Spec: [docs/plan/16-networking.md](../../docs/plan/16-networking.md) § State replication.

## Why this is a package of its own

`Vixen.Net` and `Vixen.Engine` are siblings: both sit on `Vixen.Core` and `Vixen.Ecs`, and **neither
references the other**. Networking is optional and nothing below the engine is allowed to depend on
it. So a type that has to see both a `Behavior` and a `NetworkId` lives above both, which is here.

## Two authoring styles, one mechanism underneath

```csharp
// ECS-native
[Replicated] struct Position { [Quantize(-1000f, 1000f, 16)] public Vector3 Value; }

// Behavior-facing
sealed class PlayerState : NetworkModule {
    public SyncVar<int> Score { get; }
    public VitalsModule Vitals { get; }

    public PlayerState() {
        Score = Declare(new SyncVar<int>(0), nameof(Score));
        Vitals = Nest(new VitalsModule(), nameof(Vitals));
    }
}
```

**A `SyncVar` gets delta encoding, per-connection baselines, priority shedding and per-field
bandwidth attribution without a line of code here doing any of it.** A field declares the fixed lanes
it occupies; a module's lanes are its fields' lanes end to end; a lane layout is exactly what
`DeltaCodec` needs. `SyncStateReplicator<T>` is an ordinary `IComponentReplicator`, so behaviour state
joins the pipeline at the same place a `[Replicated]` struct does.

The join to the ECS's change versions is `SyncStateVersion`, a counter component. A behaviour's state
lives in managed fields the ECS cannot see, so `MarkChanged()` touches something in a chunk — that
write is the whole point, and nothing reads the number.

`NetworkModule` is the primitive and `SyncVar` is a field in one, which is doc 16's instruction rather
than a preference: *"building the built-ins out of the same primitive users get is the right
discipline and proves the primitive."* Modules nest, and a nested one's fields are flattened into the
outer one's layout with their path as their name — so the bandwidth report says
`PlayerState.Vitals.Health`.

## SyncList does not use the delta packer, and stretching it to would be wrong

That machinery rests on a **fixed** lane layout — the server checks that declared lanes add up to what
was written and falls back to whole records when they do not. A list is variable-length, so it would
fail that check on every send: correct, and useless. Worse, lane-by-lane differencing is *actively
wrong* for a list, because inserting at the front shifts every element and a one-item insert would
difference as "all of it changed".

So a list replicates as **what happened to it** — append, insert, remove, replace, clear. Ops travel
reliably and in order, which is what makes per-connection differencing unnecessary: everyone receives
every op exactly once, so nobody needs telling which they missed. A late joiner gets the list whole
once and ops from then on. That is doc 16's "reliable-eventual semantics for `SyncVar`-style state"
taken literally.

## Owed

- **The sync system.** `MarkChanged()` is called by hand today. A system that walks dirty modules once
  a frame and marks them is a few lines and wants the engine's scheduler, which is where it will go.
- **`SyncList` in the snapshot.** The op log is built and tested, but its ops are not yet carried by
  `ReplicationServer` — a list needs a variable-length record kind beside the fixed-lane one, which is
  a wire-format addition rather than a design question.
- **The `NetworkTransform` bridge**, which is the other thing this package exists to host: the system
  that copies between `Vixen.Net.Motion`'s transform and the engine's hierarchy.
- **Codecs beyond the built-in set.** `SyncCodecs.Register` is the door; only the types the generator
  already understands are through it.
