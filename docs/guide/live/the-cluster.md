---
title: The cluster
slug: live/the-cluster
kind: concept
area: Live
summary: The four grains the orchestrator is made of, the lease that makes item duplication unrepresentable, and why the client cannot reach any of it.
api: [T:Vixen.Live.Cluster.IAccountGrain, T:Vixen.Live.Cluster.AccountHoldings, T:Vixen.Live.Cluster.AccountUnlock, T:Vixen.Live.Cluster.IGuildGrain, T:Vixen.Live.Cluster.GuildRecord, T:Vixen.Live.Cluster.GuildMember, T:Vixen.Live.Cluster.GuildWrite, T:Vixen.Live.Cluster.GuildOutcome, T:Vixen.Live.Cluster.IInstanceGrain, T:Vixen.Live.Cluster.InstanceRecord, T:Vixen.Live.Cluster.InstanceBinding, T:Vixen.Live.Cluster.InstanceWrite, T:Vixen.Live.Cluster.InstanceOutcome, T:Vixen.Live.Orchestration.AccountGrain, T:Vixen.Live.Orchestration.AccountState, T:Vixen.Live.Orchestration.GuildGrain, T:Vixen.Live.Orchestration.GuildState, T:Vixen.Live.Orchestration.InstanceGrain, T:Vixen.Live.Orchestration.InstanceState, T:Vixen.Live.Realms.RealmCluster, T:Vixen.Live.Realms.IRealmGrains, T:Vixen.Live.Realms.ClusterGrains, T:Vixen.Live.Realms.RealmClusterOptions, T:Vixen.Live.Orchestration.OrchestratorHost, T:Vixen.Live.Orchestration.OrchestratorOptions, T:Vixen.Live.Cluster.IMapGrain, T:Vixen.Live.Cluster.IShardGrain, T:Vixen.Live.Cluster.IPlayerGrain, T:Vixen.Live.Cluster.IFleetGrain, T:Vixen.Live.Cluster.Keys, T:Vixen.Live.Cluster.PlaceRequest, T:Vixen.Live.Cluster.PlaceResult, T:Vixen.Live.Cluster.PlaceStatus, T:Vixen.Live.Cluster.ShardHeartbeat, T:Vixen.Live.Cluster.ShardReport, T:Vixen.Live.Cluster.PlayerLease, T:Vixen.Live.Orchestration.MapGrain, T:Vixen.Live.Orchestration.MapOptions, T:Vixen.Live.Orchestration.MapCoordinator, T:Vixen.Live.Orchestration.ShardGrain, T:Vixen.Live.Orchestration.ShardLifecycle, T:Vixen.Live.Orchestration.HealthOptions, T:Vixen.Live.Orchestration.PlayerGrain, T:Vixen.Live.Orchestration.PlayerLeaseState, T:Vixen.Live.Orchestration.LeaseOptions, T:Vixen.Live.Orchestration.FleetGrain, T:Vixen.Live.Cluster.PlayerKeyConverter, T:Vixen.Live.Cluster.PlayerKeySurrogate, T:Vixen.Live.Cluster.RealmEndpointConverter, T:Vixen.Live.Cluster.RealmEndpointSurrogate, T:Vixen.Live.Cluster.RealmInstanceIdConverter, T:Vixen.Live.Cluster.RealmInstanceIdSurrogate, T:Vixen.Live.Cluster.RealmVersionConverter, T:Vixen.Live.Cluster.RealmVersionSurrogate, T:Vixen.Live.Cluster.ShardCapacityConverter, T:Vixen.Live.Cluster.ShardCapacitySurrogate, T:Vixen.Live.Cluster.ShardIdConverter, T:Vixen.Live.Cluster.ShardIdSurrogate, T:Vixen.Live.Cluster.ShardKeyConverter, T:Vixen.Live.Cluster.ShardKeySurrogate]
tags: [live, mmo, orleans, grains, orchestration]
since: 0.1
status: preview
related: [live/placing-players, live/shards-and-specs, live/the-live-verbs]
---

## What it is

The orchestrator is a set of grains, each a single-writer for exactly one question. Orleans hosts
them, and **no packet a player is waiting on passes through any of it**.

Four are the substrate's own — a map's shards (`IMapGrain`), a shard's life (`IShardGrain`), a
character's lease (`IPlayerGrain`) and a region's register (`IFleetGrain`). Three more are the
aggregates doc 27 named and left to doc 28 to define, and they landed once the gameplay libraries
existed to say what they held:

| | Keyed by | What it is the single writer for |
|---|---|---|
| `IAccountGrain` | account | Unlocks and points that belong to the *account* rather than a character. |
| `IGuildGrain` | guild id | The roster, the ranks and what a member may do to another. |
| `IInstanceGrain` | instance id | A saved dungeon or raid: who is bound, what is dead, and when it resets. |

⚠ **`IAccountGrain` is not in doc 27's own grain table, and finding that out was the point of
building doc 28's collections.** A mount earned on one character is owned by all of them, and there
is no key on `IPlayerGrain` that can own that — it is keyed by account *and* character. The
alternative is five characters writing the same rows at once, which is the one thing the
single-writer discipline exists to prevent. It knows nothing about collectibles: its vocabulary is
an address, a source and an order.

## What it is for

The problem the orchestrator solves is the one virtual actors are actually good at: thousands of
small, independently-addressable, single-threaded-by-construction pieces of coordination state,
spread over a cluster that changes size, where the hard requirement is that *exactly one* place
decides a given question at a given moment.

That single-threading is not a performance property. It is the correctness property:

- **`IPlayerGrain` taking one turn at a time is what makes item duplication unrepresentable.** Two
  realms cannot both hold lease epoch *n*, because acquiring is a grain turn.
- **`IMapGrain` taking one turn at a time is what makes doc 27's twenty-shards failure impossible.**
  Two hundred people zoning in at once are two hundred turns, so the fleet cannot decide twice, in
  parallel, that it is short of capacity.

Writing either by hand means writing a distributed lock service, a membership protocol and a
placement director — which is Orleans.

## Using it

```csharp no-compile="a gate's half of a placement; the silo host that stands these up is owed"
var map = cluster.GetGrain<IMapGrain>(Keys.ForMap(key));

var result = await map.Place(new PlaceRequest(player, key, party, guild, "en-GB", cameFrom));

switch (result.Status) {
    case PlaceStatus.Placed:   /* mint a ticket for result.Endpoint */ break;
    case PlaceStatus.Starting: /* show a short wait and ask again   */ break;
    case PlaceStatus.Refused:  /* result.Reason says why            */ break;
}
```

⚠ **`Starting` is an answer rather than an error.** A client told "starting" shows a progress bar; a
client told "refused" shows a failure. Conflating them is how an elastic fleet's ordinary behaviour
becomes a support ticket.

⚠ **A grain key is an identity, and two spellings of one identity are two grains.** `Keys` is the one
place a key is written, so a gate asking for `maps/queensdale|eu|…` and an orchestrator asking for
`maps/queensdale|EU|…` cannot become two fleets for one map, each unaware of the other.

## Examples

### The lease, which is the whole of ADR-021

```csharp no-compile="the realm side of a lease; RealmDirectory is what posts these"
var lease = await player.AcquireLease(myShard);      // always granted — see below

// …every durable write names the epoch it was made under…

if (!(await player.RenewLease(myShard, lease.Epoch)).Granted) {
    // Superseded. Keep simulating, and buffer durable mutations until it comes back or the
    // transfer hands them over.
}
```

⚠ **Acquiring always succeeds, and that is the design.** A transfer must be able to take the lease
from a realm that has crashed, and nothing in the cluster can tell a crashed realm from a slow one.
So the epoch moves, the previous holder finds out on the renewal it was making anyway, and a late
write naming the old epoch is a no-op rather than a conflict to resolve.

### The heartbeat's reply

```csharp no-compile="the realm's two-second sample, and what it learns from sending it"
var state = await shard.Heartbeat(new ShardHeartbeat(population, p99, mean, blocked, now));

if (state == ShardState.Draining) {
    host.Drain();
}
```

A realm learns it should be draining from the answer to a heartbeat it was sending anyway — so
nothing in the control plane ever needs to call *into* a realm. That is an entire direction of
connectivity, authentication and firewall rules that does not have to exist.

### Grains are adapters; the logic is plain classes

Every grain in `Vixen.Live.Orchestrator` is a few lines over a plain class — `MapCoordinator`,
`ShardLifecycle`, `PlayerLeaseState`, `AccountState`, `GuildState`, `InstanceState`. The grain supplies the one property the logic cannot give
itself, that it is never re-entered; the logic is a state machine a test constructs and drives.
Writing the state machine inside the grain would make it untestable without a silo, which is how a
coordination layer ends up with no tests at all.

### The grain decides ordering; the caller decides permission

⚠ **`IGuildGrain` re-checks less than you might expect and exactly as much as it must.** A charter's
permissions are tags on compiled content, so *"may this officer kick"* is the realm's question and it
answers it with the same code the client greys the button out with. What no local check can win is
the **race** — two officers demoting each other at once — so the grain re-checks the one part that is
arithmetic: rank is an integer, and you may not act on somebody at or above your own.

⚠ **The one exception is a handover**, and it exists because the rule above would otherwise make a
guild unfixable: promoting somebody *to* rank zero moves the current leader down one, in the same
turn, because two leaders is a state no rule in the interface could resolve afterwards.

⚠ **A realm can only name the members connected to it**, so what it sends here is an *operation* and
never a roster — see [the gameplay bridge](gameplay-bridge.md). That is also why every method takes a
`by`: a diff of two rosters cannot say who moved anybody.

### A lockout is fleet-wide, and that is why it is a grain

⚠ Doc 28 is direct about it: *"a lockout one shard knew about is a lockout a player evades by
zoning"*. There is exactly one place that decides whether somebody is saved to an instance.

⚠ **Progress belongs to the instance and not to each player**, so somebody bound late inherits the
bosses that are already down. Per-player progress is a raid re-killing its first boss for every
latecomer, which is both the exploit and the tedium the mechanic exists to prevent.

⚠ **Binding cannot be undone and there is deliberately no method for it** — that is what a lockout
*is*. What ends one is the reset, which the caller computes as an absolute boundary and hands over as
`Expires`; a timer from when somebody entered makes every player's reset drift to wherever their
first run fell. And **closing an instance releases nobody**, or disbanding would be how a group runs
a raid twice.

### What the client cannot see

`Vixen.Live.Cluster` is the assembly a game client must never reference, and the build checks it
rather than trusting it. The consequence is that `Vixen.Live.Abstractions` — which a client *does*
reference — cannot carry Orleans's serializer attributes, so the cluster assembly holds a
**surrogate** per vocabulary type instead. A type added to the vocabulary and not to `Surrogates.cs`
fails at the first grain call that carries it, which is why every one of them is round-tripped
through a real serializer in a test.

### The realm's side, which never waits

```csharp no-compile="the realm's whole control-plane wiring, in a game's own realm class"
protected override void OnRealmInitialise() =>
    cluster = new RealmCluster(Host, new ClusterGrains(clusterClient));

protected override void OnRealmUpdate(GameTime time) => cluster?.Update(time.UnscaledElapsed);
```

⚠ **Nothing in `RealmCluster` awaits a grain.** Doc 27 M1 names a grain call reaching the frame path
as the single way this design fails — *"it will not look like a bug, it will look like occasional
stutter"* — so every call is posted through `RealmDirectory` and its answer applied on the realm's own
thread in a later frame. It is asserted rather than described: twenty frames against a cluster
answering in 250 ms take under 200 ms in total.

A realm with no cluster is a realm, not a broken one. `RealmSpec.ClusterEndpoint` being empty is doc
27 § Cost's L0 — a dedicated server with a lifecycle and no orchestrator — and `Vixen.Live.Realm.Cluster`
is a separate package so that such a realm does not link a cluster framework it never joins.

### Standing the orchestrator up

```csharp no-compile="an orchestrator's Program.cs, which is a host like any other application's"
var builder = Host.CreateApplicationBuilder(args);

builder.UseDevelopmentCluster(new OrchestratorOptions("dev", "queensdale", maps, fallback));

await builder.Build().RunAsync();
```

⚠ **Clustering is deliberately not chosen for you.** ADR-016 lists the providers — AdoNet, Redis,
Azure Storage, Kubernetes — and picking one would tie the engine to a deployment target the brief
keeps open. `UseVixenOrchestrator` configures the grains and leaves membership to the caller;
`UseDevelopmentCluster` is the localhost answer for a laptop and is named for what it is.

## See also

- [Placing players](placing-players) — what `IMapGrain` hosts.
- [Shards, keys and specs](shards-and-specs) — the vocabulary the surrogates carry.
- [docs/plan/27](https://github.com/Rikarin/Vixen/blob/master/docs/plan/27-mmo-framework.md) § ADR-016, § ADR-021, § Grains.
