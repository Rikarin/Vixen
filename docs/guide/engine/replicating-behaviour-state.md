---
title: Replicating behaviour state
slug: engine/replicating-behaviour-state
kind: guide
area: Networking
summary: SyncVar, SyncList and the sweep that puts a behaviour's changes on the wire without anything having to remember to.
api: [T:Vixen.Net.Engine.SyncVar`1, T:Vixen.Net.Engine.NetworkModule, T:Vixen.Net.Engine.NetworkBehaviour, T:Vixen.Net.Engine.SyncList`1, T:Vixen.Net.Engine.ISyncList, T:Vixen.Net.Engine.SyncStateVersion, T:Vixen.Net.Engine.SyncListVersion, T:Vixen.Net.Engine.SyncStateReplicator`1, T:Vixen.Net.Engine.SyncListReplicator`1, T:Vixen.Net.Engine.SyncStateSweepSystem]
tags: [networking, replication, behaviours, syncvar]
since: 0.1
status: preview
related: [engine/networked-players, engine/networked-prefabs, ecs/system-order]
---

## What it is

The behaviour-facing half of replication. A `NetworkModule` is a unit of networked state; a
`SyncVar<T>` is a field in one; a `NetworkBehaviour` is a `Behavior` that has one. Declare them in a
constructor, assign them like ordinary properties, and the values arrive on every client that can see
the object.

`SyncList<T>` is the same idea for a collection, and it travels differently on purpose — see below.

Underneath it is the same mechanism a `[Replicated]` struct uses. `SyncStateReplicator<T>` and
`SyncListReplicator<T>` are ordinary component replicators, so behaviour state joins the pipeline at
exactly the place an ECS-native component does and gets the same delta encoding, the same
per-connection baselines, the same priority shedding and the same per-field bandwidth attribution.

`SyncStateSweepSystem` is the piece that makes it feel automatic: once a frame it looks at the
behaviours that changed and tells the ECS, which is what a capture reads.

## What it is for

A game whose networked state is naturally a script's state — a score, a health bar, an inventory, a
match phase. The ECS-native style is better for anything a system already walks in bulk, and a
transform is emphatically one of those: use `NetworkTransform` and the
[bridge](engine/networked-players) for position, not a `SyncVar<Vector3>`.

The trade is per-object dispatch against convenience, and it is the same trade `Behavior` itself makes
against `ISystem`.

## Using it

Declare the state, then the behaviour that holds it. Everything is declared in a constructor, and
that is not a style rule: the layout is what both ends agree about without ever exchanging it, so a
field declared later would shift every lane after it and desynchronise what is already in flight.

```csharp compile
using Vixen.Ecs;
using Vixen.Engine.Behaviors;
using Vixen.Engine.Frames;
using Vixen.Net.Engine;
using Vixen.Net.Replication;

public sealed class Vitals : NetworkModule {
    public SyncVar<byte> Health { get; }

    public SyncVar<bool> Alive { get; }

    public Vitals() {
        Health = Declare(new SyncVar<byte>(100), nameof(Health));
        Alive = Declare(new SyncVar<bool>(true), nameof(Alive));
    }
}

public sealed class AvatarState : NetworkModule {
    public SyncVar<int> Score { get; }

    public Vitals Vitals { get; }

    public AvatarState() {
        Score = Declare(new SyncVar<int>(0), nameof(Score));
        Vitals = Nest(new Vitals(), nameof(Vitals));
    }
}

public sealed class Avatar : NetworkBehaviour {
    public AvatarState Sync { get; } = new();

    protected override NetworkModule Build() => Sync;

    protected override void Update() {
        if (IsServer) {
            Sync.Score.Value += 1;
        }
    }
}

public static class AvatarNetworking {
    /// <summary>Everything a server needs for the behaviour above to reach its clients.</summary>
    public static void Wire(EngineLoop loop, ReplicationRegistry registry) {
        registry.Register(new SyncStateReplicator<Avatar>(loop.Behaviors));

        // Without this, `Score` above moves on the server and nothing ever leaves it.
        loop.Add(new SyncStateSweepSystem(loop.Behaviors));
    }
}
```

Nested modules are flattened into the outer one's layout with their path as their name, so the
bandwidth report says `AvatarState.Vitals.Health` without anything having to reconstruct it.

### The sweep, and where it has to run

A `SyncVar`'s value lives in a managed field the ECS cannot see, so assigning one cannot on its own
put the entity in the next capture. Something has to touch a component in a chunk, and that is what
`SyncStateVersion` — and `SyncListVersion` beside it — are for: counters nobody reads the value of,
whose *write* is the whole point.

`SyncStateSweepSystem` is what does the touching. It runs in `SystemPhase.LateUpdate`, ordered
`[UpdateAfter]` the last behaviour pass, and it asks each networked behaviour whether its module is
dirty or any of its lists has anything pending.

⚠ **A capture must come after that phase, in the same tick.** The system cannot express this itself,
because `ReplicationServer.Capture` is not a system — a server decides for itself when its tick is. A
server that runs `EngineLoop.Frame` and then captures is already right. A server that captures inside
`SystemPhase.FixedUpdate`, where `NetworkTransformCaptureSystem` lives, would be reading a mark that
has not happened yet and would ship every behaviour change one frame late — which presents as
interpolation feeling slightly heavy, not as a bug. Such a server calls `Sweep` itself instead:

```csharp no-compile="a fragment; `sweep`, `replication` and `world` are the server's own"
sweep.Sweep(world);
replication.Capture(world, tick);
```

`MarkChanged()` and `MarkListsChanged()` remain public and remain the raw path. A behaviour that wants
its change on the wire *now*, before the sweep, still calls one — the sweep does not make either
harder to reach, and marking twice in a frame costs two increments of a counter.

### Lists go whole, and that is deliberate

The delta packer rests on a **fixed** lane layout. A list is variable-length, so it would fail that
check on every send; worse, lane-by-lane differencing is actively wrong for a list, because inserting
at the front shifts every element and a one-item insert would difference as "all of it changed".

So a `SyncList<T>` goes whole, on the reliable channel, on the tick it changes. That makes a late
joiner, a reconnect, a lost snapshot and an interest change the same case: here is the list. The cost
is bandwidth proportional to the list on the tick it changes, which is the right trade for an
inventory and the wrong one for something that changes every tick.

The operation log is still there and still drives `SyncList<T>.Changed` — "one item was inserted at
index three" is exactly what a UI wants to bind to, and exactly not what a receiver should be sent.

## Examples

A list appended from ordinary behaviour code, with nothing marking it by hand:

```csharp no-compile="a fragment; the behaviour and its registration are as above"
public sealed class Backpack : NetworkBehaviour {
    public SyncList<int> Items { get; }

    readonly BackpackState state = new();

    public Backpack() => Items = DeclareList(new SyncList<int>(), nameof(Items));

    protected override NetworkModule Build() => state;

    public void PickUp(int item) => Items.Add(item);
}
```

Registered with `registry.Register(new SyncListReplicator<Backpack>(loop.Behaviors))`, and swept by
the same system. State and lists are marked on **different** components, so a score changing does not
re-send an inventory and an inventory changing does not re-send a score.

What the sweep costs is worth knowing before a game has a thousand of these. Its query is
`BehaviorRef` **and** `NetworkId` together, so it visits networked entities carrying behaviours and
nothing else — a scene of props costs it one archetype test. Per visited behaviour it is a field-by-
field dirty check with an early exit, so the pass is O(networked behaviours × fields) per frame.
`LastVisitedCount` is that number, and a value far above the count of things that actually change is a
game whose behaviours are networked and need not be.

## See also

- [networked players](engine/networked-players) — transforms, ownership and prediction, which are not this.
- [networked prefabs](engine/networked-prefabs) — how the object carrying these behaviours comes to exist on a client.
- [system order](ecs/system-order) — phases, `[UpdateAfter]`, and what a dropped edge does.
- `Core/Vixen.Net.Engine/README.md` — the wire's side of all of it, and what the package still owes.
