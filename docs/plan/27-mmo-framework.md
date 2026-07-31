<!--
SPDX-FileCopyrightText: Copyright (c) Rikarin
SPDX-License-Identifier: Apache-2.0
-->

# 27 — MMO Framework: Orchestration, Realms and Transfer

⚠️ **Extends [16](16-networking.md) and [17](17-app-heads-and-shipping.md).** Doc 16 built a
server-authoritative session between *one* server and its clients. This document is what happens when
there is more than one server, they come and go while people are playing, and a player walks from one
to another without noticing.

The gameplay libraries that sit on top — items, quests, combat, guilds, the rest — are
[28](28-gameplay-framework.md). This document is the substrate they run on. Read this one first: 28's
authority model is a consequence of the three planes decided here.

**Read § Cost before § Milestones.** This is, honestly, the size of a second engine. The milestones
are ordered so that stopping after any of them leaves something a real game can ship on, rather than
an abandoned branch.

## What this is

Guild Wars 2 is the shape, and it is worth being precise about *which* of its properties are being
copied, because two of the famous ones are not what people think they are.

| Property | What it actually is | Taken? |
|---|---|---|
| **Megaserver** — you are never on "a server", you are placed on the best instance of a map | A placement service that scores candidate map instances against a player's party, guild, language and the instance's fill, spawns new ones under load and retires empty ones | ✅ the whole of § Placement |
| **No realm queue, no server select** | A consequence of the above, not a feature of its own | ✅ falls out |
| **Seamless movement inside a map** | The map is **one process**. It is not seamless *across* processes — it is seamless because there is no seam | ✅ and this is the calibration that makes the rest tractable — see below |
| **Map-to-map travel** | A real transfer between processes, with a loading screen whose length is content streaming, not handshaking | ✅ § Transfer, minus most of the loading screen |
| **Join your friend's instance** | The same transfer, initiated by a social action instead of by a portal | ✅ falls out of the same protocol |
| **Instanced content** (story, dungeons, fractals, raids) | A map instance with an access list and a lifetime tied to its party | ✅ § Shard kinds |
| **Overflow / spillover** | A map instance spawned above the intended count, drained first | ✅ falls out of placement + drain |

**The calibration that matters, stated once.** A single continuous map is one realm process. Splitting
one map across several processes — so that a player crosses an invisible seam mid-run and two
processes have to agree about an arrow in flight — is a genuinely harder problem than everything else
here put together, it is not what Guild Wars 2 does, and it is **P2**. What makes that acceptable is
that the transfer protocol in § Transfer is written so an intra-map seam is the same mechanism with a
different trigger and a ghosting layer added; the P2 work is the ghosting, not a redesign. § Intra-map
seams says what that costs and why it is deferred.

So "smoothly travel through large zones" is answered by making a map as large as one process can
carry — which for this engine's replication budget is thousands of entities and low hundreds of
players, measured, not guessed ([`Samples/09-NetworkSoak`](../../Samples/09-NetworkSoak) already holds
100 connections and 5 000 entities at 75.2 kbit/s and a p99 tick of 2.4 ms) — and by making the
transfer between maps cost a preload rather than a reconnect.

## Where Vixen already is

More of this exists than "we have no MMO framework" suggests. Nothing below needs to be built; all of
it needs to be *used*.

| Piece | State | Where |
|---|---|---|
| Server-authoritative session, handshake carrying protocol version **and content hash** | ✅ | [`NetworkSession`](../../Core/Vixen.Net/Sessions) |
| `PlayerId` surviving a dropped `ConnectionId`; reconnect window; server-issued opaque tokens | ✅ | `NetworkSession` — **the transfer ticket is this mechanism with a different issuer** |
| Replication with per-connection ack'd baselines, field-level delta, priority shedding | ✅ | [`ReplicationServer`](../../Core/Vixen.Net/Replication) |
| `IComponentReplicator` — a component's wire encoding, generated | ✅ | `Vixen.Net.Generators` — **the handoff payload reuses it verbatim** |
| Interest as a *source* plus rules, with hysteresis on leaving | ✅ | `InterestChain`, `InterestGrid` |
| `NetworkSceneId` = hash of the scene's **name**; baked ids for scene-placed objects | ✅ | [`NetworkScenes`](../../Core/Vixen.Net.Engine/NetworkScenes.cs) |
| Prefab id = hash of the **address**, so content and wire agree without a hand-kept list | ✅ | `NetworkSpawner` |
| Client-side prediction: input log, jitter buffer, rollback, server-steered tick lead | ✅ | `Vixen.Net.Prediction` |
| Lag compensation — pose ring, rewind scope | ✅ | `Vixen.Net.Physics` |
| Addressables: catalog, `BuildHash`, remote bundles with ranges/resume/CRC, **catalog overlay updates that never throw** | ✅ | [`Vixen.Assets`](../../Core/Vixen.Assets) |
| Content server | ✅ | [`Tools/Vixen.ContentServer`](../../Tools/Vixen.ContentServer) |
| Headless platform host + `Vixen.Graphics.Null` + `BuildVariant.Server` | ✅ | `Vixen.Platform.Headless`, `Tools/Vixen.App` |
| Out-of-process play: the editor already launches and supervises player processes | ✅ | `Editor/Vixen.Editor.SceneView` `PlayerSessions` — **the process placement backend is this, generalised** |
| Metrics as a `System.Diagnostics.Metrics` meter, OTLP export split into its own package | ✅ | `Vixen.Net.Telemetry` |
| Navmesh, crowd, tiled bake, dynamic obstacles | ✅ | [`Vixen.Navigation`](../../Core/Vixen.Navigation) |
| `SceneManager` — additive load/unload, `SceneTag`, entity adoption | ✅ | `Core/Vixen.Engine/Scenes` |
| **`SceneCompiler`** (`.vxscene` → runtime asset) | ✅ | `Editor/Vixen.Editor.Assets/Scenes` — **built**, and with it the boot path that opens one: a content build writes a `SceneManifest` beside its catalog and the host loads the first entry. A realm loads a compiled scene rather than the prefab list this document was written against |

## The three planes

Everything in this document follows from separating three kinds of traffic that are usually conflated,
and never letting the slow ones touch the fast one.

```
┌─ Service plane ── HTTPS / WSS, request-response, 100 ms is fine ────────────┐
│  Client ↔ Gate      login · characters · catalog · social · chat(global)    │
│                     auction · mail · matchmaking · store · support          │
├─ Control plane ── Orleans, in-cluster TCP, never on a frame path ───────────┤
│  Gate ↔ Orchestrator ↔ Realm                                                │
│  placement · shard lifecycle · player leases · transfer tickets · upgrades  │
├─ Data plane ── UDP, direct, 20–60 Hz, no intermediary ──────────────────────┤
│  Client ↔ Realm     everything doc 16 already does                          │
└─────────────────────────────────────────────────────────────────────────────┘
```

### The routing question, answered

> *"How should the network be routed? Real-time events must be as fast as possible (FPS games) but a
> player can move between instances."*

**Real-time traffic goes straight from the client to the realm that is simulating it. There is no
gateway, no proxy and no relay on the hot path.** The client learns an endpoint and a ticket from the
gate, opens a UDP session to that endpoint, and from then on the packet path is exactly doc 16's.

The tempting alternative — a persistent client↔gateway connection with the gateway forwarding to
whichever realm currently owns the player — is what most "MMO architecture" diagrams show, and it is
the wrong trade here. Its single advantage is that a transfer needs no client-visible reconnect. What
it costs, permanently, for every player:

- **A hop.** Best case inside one datacentre that is 0.3–2 ms each way; realistically 2–10 ms, and if
  the gateway is not co-located with the realm it is unbounded. On a 60 Hz shooter that is between
  a quarter and a full tick of added latency on *every* input, paid so that a transfer that happens
  every twenty minutes is smoother.
- **A bandwidth wall.** The soak measures 75 kbit/s per connection. Five thousand concurrent players
  is 375 Mbit/s in and 375 Mbit/s out through one tier that now has to be scaled, sharded and
  load-balanced on its own — and a UDP load balancer with per-player affinity is its own distributed
  system.
- **A shared failure domain.** A gateway crash disconnects everyone behind it, including players whose
  realm is perfectly healthy.
- **A second copy of the security surface.** Every check doc 16 lists — size caps, rate limits,
  ownership, closed-set deserialization — either moves to the gateway or is done twice.

And the advantage it buys is small once the transfer protocol is written properly: § Transfer opens
the second session *before* the first one closes, so the reconnect is not a gap the player can see. We
are paying a one-off cost at the transfer, in the background, instead of a permanent cost on every
packet.

**When a proxy is genuinely warranted, and why the design already admits one.** DDoS scrubbing,
IPv4 exhaustion, hostile mobile carriers that drop UDP, and console platform requirements are all real
reasons to put something in front of a realm. The seam for that already exists and it is
`ITransport`: `Vixen.Net.Transport.Relay` is doc 16's owed transport, and `Composite` lets one realm
accept direct and relayed clients at once. Because the client learns *"an endpoint and a ticket"* from
the gate and nothing above the transport knows the difference, swapping a direct endpoint for a
relay-allocated one is a placement decision, not an architecture change. That is the property worth
protecting, and it is why the endpoint is data rather than configuration.

**Chat is routed by audience, not by being chat.** `/say`, `/yell`, `/party` and zone chat are
spatial or session-scoped, so they are realm traffic on `Channel.ReliableUnordered` and they cost
nothing extra. Guild, whisper, global and cross-map party chat are service-plane traffic on the
client's WSS connection to the gate, because the recipient may be on another realm, offline, or on
another continent. A game that routed all chat through the realm would need realm-to-realm messaging;
a game that routed all of it through the gate would pay a round trip to say "hello" to the person
standing next to them. See [28](28-gameplay-framework.md) § Chat.

**The client holds two connections in steady state**: one UDP session to its realm, one WSS to the
gate. During a transfer it briefly holds three. That is the whole of the client's network topology.

---

## Architecture Decision Records

Continuing [01](01-technology-decisions.md)'s register.

### ADR-016 — Orleans is the control plane, and only the control plane

**Decision.** [Microsoft Orleans](https://github.com/dotnet/orleans) 10.2.2 hosts the orchestrator:
placement, shard lifecycle, player leases, transfer tickets, matchmaking queues, guild and auction
state. **No packet a player is waiting on passes through a grain call.**

**Rationale.** The problem the orchestrator solves is the one virtual actors are actually good at:
thousands of small, independently-addressable, single-threaded-by-construction pieces of coordination
state, distributed over a cluster that changes size, where the hard requirement is that *exactly one*
place decides a given question at a given moment. `PlayerGrain` being single-threaded is not a
performance property, it is the correctness property that makes item duplication across a transfer
impossible to express (§ Leases). Writing that by hand means writing a distributed lock service, a
membership protocol and a placement director, which is Orleans.

**Verified at plan time** (`api.nuget.org`, `10.2.2` current):

| Fact | Value |
|---|---|
| Latest stable | 10.2.2 |
| TFM | ships a `net10.0` dependency group |
| Codegen | `Microsoft.Orleans.CodeGenerator` ships `analyzers/dotnet/cs/Orleans.CodeGenerator.dll` — **a Roslyn source generator, not an IL weaver** |
| Clustering providers | `Clustering.AdoNet`, `Clustering.Redis`, `Clustering.AzureStorage`, …, plus `Hosting.Kubernetes` for pod-aware membership |
| Licence | MIT |

**ADR-002 is satisfied, and it was worth checking rather than assuming.** Orleans's serializer and
grain-reference proxies are generated by a Roslyn incremental generator; nothing rewrites IL after
compile. Orleans is *not* NativeAOT-clean end to end, and does not need to be: it appears only in
`Live/`, which runs JIT on a server we control ([17](17-app-heads-and-shipping.md) § Trimming policy
already has the editor in the same position). Enforced by `CheckArchitecture`: no `Core/` or
`Platform/` project may reference an Orleans package.

**The rule that makes this safe, stated as a rule because it is the way this design fails.** A grain
call is a network round trip with a scheduler in front of it. A frame that awaits one has a p99
measured in milliseconds and a p99.9 measured in seconds. So:

> **Orleans is asked, not awaited.** A realm never blocks a tick on a grain call. It posts a request,
> continues simulating, and applies the answer at a defined phase of a later frame.

This is not a new pattern in this repository — `ISessionAuthenticator` is already shaped exactly this
way, answering `Pending` and being asked again next update, and doc 16 records *why*: "a completion on
a thread-pool thread would make every layer it touches thread-safe for the sake of an event that
happens twice a minute." `RealmDirectory` (§ The realm) is that pattern with a bigger surface: one
inbox, one outbox, drained at `SystemPhase.PreUpdate`, and an analyzer that fails the build on an
`await` of an `IGrain` inside a system body.

**Rejected alternatives.**

| | |
|---|---|
| **Realms as Orleans silos** | See ADR-018. |
| **Akka.NET** | Comparable actor model; Orleans's virtual-actor addressing (a grain always exists, activation is the runtime's problem) removes exactly the lifecycle bookkeeping this workload is made of, and it is a Microsoft-supported .NET-first stack with a source-generator codegen story that survives ADR-002 unchanged. |
| **A bespoke coordinator over Redis** | Where every one of these systems starts, and where the distributed-lock bugs live. Cheaper to write and much more expensive to be right. |
| **Kubernetes as the only coordinator** (operators + CRDs) | Ties the design to one deployment target, which the brief explicitly rules out — Docker and bare process must work too. Kubernetes becomes a *placement backend* instead (ADR-019). |

### ADR-017 — The client never speaks Orleans

**Decision.** The game client links `Vixen.Net` and HTTP. It does not link Orleans, does not hold a
cluster client, and does not know a grain exists. Everything it needs from the control plane arrives
through the gate (service plane) or through its realm (data plane).

**Rationale.** Three separate reasons, any one of which is sufficient:

- **Trust.** A cluster client is a peer of the cluster. Handing one to an untrusted machine means
  every grain interface is a public API reachable by an attacker, and the closed-set deserialization
  posture doc 16 established has a hole the shape of Orleans's own serializer.
- **Size and AOT.** Orleans is a server framework. Linking it into an iOS NativeAOT client is a
  multi-megabyte, trim-hostile problem for a benefit that is zero.
- **Versioning.** Grain interfaces change with the orchestrator. If clients hold them, every
  orchestrator change is a client update, which is precisely the coupling § Upgrades exists to avoid.

**Consequence — and it is the reason § Contracts is split in two.** Grain interfaces live in an
assembly the client cannot reference, enforced by an architecture rule rather than by discipline.

### ADR-018 — A realm is an Orleans *client*, not a silo

**Decision.** The realm process hosts a Vixen game loop and connects to the Orleans cluster as a
client. It hosts no grains.

**Rationale.** This is the "probably an Orleans client" in the brief, and it is right, for reasons
worth writing down because the alternative is superficially attractive (one process type, grains
co-located with the simulation, no client hop).

- **Silos want to be homogeneous and long-lived; realms are neither.** A realm is version-tagged,
  spawned under load, drained on a schedule and killed. A silo joining and leaving triggers cluster
  membership churn and grain rebalancing, so a busy evening of shard scaling becomes continuous
  reactivation of unrelated grains across the cluster.
- **The two scheduling models are incompatible.** Orleans owns its threads and schedules grain turns
  on the thread pool. Vixen's frame is a fixed-step loop on one thread with a budget measured in
  single-digit milliseconds. Hosting both in one process means the runtime that must never be starved
  shares a pool with a runtime that will happily saturate it.
- **A silo's failure blast radius is not the realm's.** If a realm crashes with a raid boss in it,
  what should be lost is that fight. If it were a silo, what is lost is also every grain the cluster
  had placed there — other people's guilds, other people's auctions.
- **The client hop is not on any hot path anyway**, by ADR-016's rule.

**Cost, accepted.** Realm → grain calls cross the process boundary. Measured in the low milliseconds
in-cluster, which is fine for everything on the control plane and forbidden on the data plane, which
is the same line ADR-016 already draws.

### ADR-019 — Placement is an interface with three backends, selected by probe

**Decision.** `IRealmPlacement` — start, stop, list, watch — with `Kubernetes`, `Docker` and `Process`
implementations. The orchestrator probes in that order at startup and uses the first that answers,
overridable by configuration.

```csharp
public interface IRealmPlacement {
    ValueTask<PlacementProbe> ProbeAsync(CancellationToken ct);
    ValueTask<RealmInstance>  StartAsync(RealmSpec spec, CancellationToken ct);
    ValueTask                 StopAsync(RealmInstanceId id, StopMode mode, CancellationToken ct);
    IAsyncEnumerable<PlacementEvent> WatchAsync(CancellationToken ct);   // started · ready · lost
}
```

| Backend | Probe | Creates | Address the client is given |
|---|---|---|---|
| **Kubernetes** | in-cluster service-account token, or a `KUBECONFIG` that answers `/version` | one **`Pod`** per realm, owner-referenced to the orchestrator's own pod | node external IP + `hostPort` from a per-node range |
| **Docker** | Engine API reachable on `/var/run/docker.sock` or `npipe://./pipe/docker_engine` | one container per realm, labelled with the realm id | host IP + published UDP port |
| **Process** | always succeeds | `Process.Start` with a port from a pool | loopback or LAN IP + port |

**A Pod, not a Deployment or a StatefulSet.** Realms are not fungible replicas: each has an identity,
a map, a population, a version and a lifetime that ends when the last player leaves. A Deployment's
controller would restart a realm that exited on purpose, and its rolling update is the wrong shape for
§ Upgrades — draining a realm means moving *players*, not terminating a pod when a readiness probe
flips. An owner reference on the orchestrator's pod gives the one thing the controller was wanted for:
garbage collection if the orchestrator itself is destroyed. This is what
[Agones](https://agones.dev) does, and Agones is a legitimate fourth backend for anyone already
running it — the interface admits it and we do not ship it.

**`hostPort`, not a `Service` per pod.** A `Service` per realm adds kube-proxy to the UDP path,
consumes a cluster IP per shard, and puts conntrack between the player and the simulation — the
gateway problem in a different hat. `hostPort` plus the node's external address is the standard
answer for game servers, and the port range per node is the only piece of cluster configuration this
design requires.

**Dependencies.** `KubernetesClient` 19.0.2 (Apache-2.0, generated from the OpenAPI spec, actively
maintained — re-deriving it would be absurd). **Docker gets a hand-written client**: the Engine API
surface needed here is six calls over a unix socket, `SocketsHttpHandler` +
`UnixDomainSocketEndPoint` reaches it in about two hundred lines, and `Docker.DotNet` has not been
pushed to in a year. That is the same judgement `Vixen.Navigation` made about Recast and
`Vixen.Ui.Text` made about ICU, applied to a much smaller surface.

### ADR-020 — A transfer is a client-driven reconnect behind a signed ticket, not a socket migration

**Decision.** The client opens a second session to the target realm while the first is still
authoritative, and the source realm hands over at a chosen tick. Nothing migrates a socket, forwards
packets, or proxies a connection.

**Rationale.** Socket migration and connection forwarding both require an intermediary that outlives
the realm, which is the gateway rejected above. A second session costs one handshake — already built,
already fuzzed, already carrying a protocol version and a content hash — and it is *overlapped* with
the player continuing to play, so its latency is hidden rather than paid.

**The ticket is `NetworkSession`'s reconnect token with a different issuer.** Doc 16 already
established server-issued, opaque, expiring tokens that let a `PlayerId` survive a dropped
`ConnectionId`. A transfer ticket is the same object minted by the orchestrator instead of by the
source session, naming the target realm, the player, the lease epoch and an expiry, signed with a
cluster key the realms hold and the client does not. The client is a courier; it cannot read or forge
one.

### ADR-021 — Durable state moves by lease, not by value

**Decision.** A player's persistent state — inventory, currency, progression — is owned by
`PlayerGrain` and lives in the database behind it. A realm holds a **lease**: exclusive, epoch-numbered,
renewed by heartbeat, revoked on transfer or on death. The handoff payload carries the lease epoch and
the volatile simulation state. **It does not carry the inventory.**

**Rationale.** This is the single decision that makes item duplication unrepresentable, and every MMO
that got it wrong got it wrong the same way: state serialised into a transfer message, a packet lost,
a retry, two copies. With a lease:

- Acquisition is a grain call, and a grain takes one turn at a time. Two realms cannot both hold epoch
  *n*; the second gets `n+1` and the first is told its lease is dead.
- A realm that dies mid-transfer does not take the state with it — the lease simply expires and the
  next holder reads the same rows.
- A duplicate `CommitTransfer` is idempotent: it names an epoch, and an epoch already superseded is a
  no-op rather than a second grant.
- Every write goes through one writer, so the ledger ([28](28-gameplay-framework.md) § Economy) has a
  total order per player without a distributed transaction.

**Cost.** A realm cannot mutate durable state without holding the lease, so a lease loss mid-combat
must be survivable: the realm keeps simulating, buffers durable mutations as ledger intents, and
either flushes them when the lease returns or hands them to the new holder in the transfer. `Volatile
state is the realm's; durable state is the grain's` is the sentence to remember.

### ADR-022 — A shard's version is a first-class field, and mismatched peers never meet

**Decision.** Every realm instance is tagged `(BuildVersion, ContentHash)`. Placement filters on both.
A client whose catalog `BuildHash` does not match a shard's is never placed on it — it is placed on a
shard that matches, or told to update.

**Rationale.** Doc 16's handshake already rejects a content-hash mismatch. Making it a *placement
filter* instead of only a *rejection* turns a hard failure into a routing decision, and that is the
whole of the incremental-upgrade story: new shards come up on the new version, old ones drain, and a
client that has not fetched the addressable update yet keeps playing on an old shard until it does.
See § Upgrades for the live-ops hazard this creates (population fragmentation) and how it is bounded.

---

## Repository layout

Four kinds of thing, and only one of them is new.

```
Core/
├── Vixen.Gameplay*/                    # doc 28 — engine-side gameplay libraries
│
Live/                                   # ── NEW TOP LEVEL: the online service layer ──
├── Vixen.Live.Abstractions/            # RealmId · ShardKey · RealmSpec · TransferTicket · endpoints
├── Vixen.Live.Abstractions.Tests/      #   no Orleans, no engine, no ASP.NET. The client may see this
├── Vixen.Live.Cluster/                 # grain INTERFACES only (Microsoft.Orleans.Sdk)
├── Vixen.Live.Cluster.Tests/
├── Vixen.Live.Orchestrator/            # grain implementations, placement director, heuristics, upgrades
├── Vixen.Live.Orchestrator.Tests/
├── Vixen.Live.Placement.Kubernetes/    # KubernetesClient 19.0.2
├── Vixen.Live.Placement.Kubernetes.Tests/
├── Vixen.Live.Placement.Docker/        # hand-written Engine API client
├── Vixen.Live.Placement.Docker.Tests/
├── Vixen.Live.Placement.Process/       # Process.Start — dev, CI, and small deployments
├── Vixen.Live.Placement.Process.Tests/
├── Vixen.Live.Realm/                   # the realm host: game loop + Vixen.Net + Orleans client
├── Vixen.Live.Realm.Tests/
├── Vixen.Live.Transfer/                # the handoff protocol — realm side and client side
├── Vixen.Live.Transfer.Tests/
├── Vixen.Live.Gate/                    # ASP.NET Core: login, characters, catalog, WSS service plane
├── Vixen.Live.Gate.Tests/
├── Vixen.Live.Client/                  # the CLIENT half: gate HTTP/WSS + transfer participation
├── Vixen.Live.Client.Tests/            #   links neither Orleans nor ASP.NET hosting
├── Vixen.Live.Persistence/             # repository, migrations, ledger, idempotency keys
├── Vixen.Live.Persistence.Tests/
├── Vixen.Live.Matchmaking/             # tickets, pools, rating, backfill — Open Match as reference
└── Vixen.Live.Matchmaking.Tests/

Tools/
└── Vixen.Cli/                          # gains `vixen live up · down · status · drain · upgrade`

Samples/
└── 13-Mmo/                             # the vertical slice, and the exit criterion
```

**Why a new top level rather than more of `Core/` or `Tools/`.** These projects are not engine runtime
— they run with no renderer, no window, no game loop in three of the four cases, and a client must
never link them. They are not tools either: a tool is something a developer runs, and these are
shipped and operated. `Live/` is the honest name for the tier, and giving it its own folder makes the
layer rule expressible: **nothing in `Core/`, `Platform/`, `Editor/` or `Raven/` may reference
`Live/`, and `Live/` may not reference `Editor/`.** One more rule in
[`Build.ArchitectureRules.cs`](../../build/Build.ArchitectureRules.cs), alongside the `Vixen.Ui` ⇸
`Vixen.Engine` one it already enforces.

**One wart, named rather than hidden.** `Vixen.Live.Realm` needs the application host, which is
`Tools/Vixen.App`. So `Live/` sits above `Tools/Vixen.App`, which is a `Live → Tools` reference the
existing layer check would flag. Two ways out: allow-list that one edge, or move `Vixen.App` into
`Core/` where an application host arguably belongs anyway. **Recommendation: move it**, as a separate
change with its own reasoning, and allow-list the edge in the meantime so this work is not blocked on
that argument.

### The three assemblies a game writes, and who may see them

The brief asks for shared contracts between client, realm and orchestrator. There are three, not one,
and the split is load-bearing rather than tidy:

| Assembly | Contains | Referenced by | Constraints |
|---|---|---|---|
| `MyGame.Contracts` | `[Replicated]` components, RPC interfaces, `IBroadcast` types, `DefId`s, the gate's DTOs | **client, realm, orchestrator, gate** | `net10.0`, trim-clean, AOT-safe, no `Vixen.Engine`, **no Orleans** |
| `MyGame.Cluster` | grain interfaces, grain-facing records | **realm, orchestrator, gate** | may reference `Microsoft.Orleans.Sdk` and `MyGame.Contracts` |
| `MyGame.Shared` | gameplay rules that both ends run — the predicted step, damage formulae, validation | **client, realm** | no Orleans, no ASP.NET; this is where "the client predicts what the server will do" is made literal |

`MyGame.Contracts` carrying no Orleans is ADR-017 made mechanical: the client physically cannot reach
a grain because the types are not in an assembly it references. `MyGame.Shared` existing separately is
what stops the classic drift where the client's prediction and the server's simulation are two
implementations of one rule — doc 16's `MispredictionCount` is the number that catches it, and one
assembly is what prevents it.

`dotnet new vixen-mmo` scaffolds all six projects (`.Client`, `.Realm`, `.Orchestrator`, `.Gate`,
`.Contracts`, `.Cluster`, `.Shared`, `.Content`) with the references already correct, because getting
this graph wrong on day one is the kind of mistake that is discovered in month six.

---

## The orchestrator

### Grains

Small, and each one is a single-writer for exactly one question.

| Grain | Key | Owns |
|---|---|---|
| `IMapGrain` | map address (`maps/queensdale`) | the set of shards for this map; placement scoring; spawn/merge decisions |
| `IShardGrain` | `ShardId` (guid) | one realm instance: state machine, population, heartbeat, endpoint, version |
| `IPlayerGrain` | account + character | durable state, the realm lease, the transfer state machine, session identity |
| `IPartyGrain` | party id | membership, the "join my friend" target, party-aware placement |
| `IGuildGrain` | guild id | roster, ranks, bank, guild hall shard |
| `IQueueGrain` | queue id | matchmaking tickets and pools ([28](28-gameplay-framework.md) § Matchmaking) |
| `IInstanceGrain` | instance id | a dungeon/raid/story instance: access list, lockout, lifetime |
| `IFleetGrain` | singleton per region | capacity, node budget, version rollout, the drain schedule |

`IShardGrain`'s state machine is the spine:

```
Requested → Starting → Ready → Draining → Stopping → Stopped
                 ↓        ↓        ↓
               Failed ← Lost ← (missed heartbeats)
```

- **Requested → Starting** — `IRealmPlacement.StartAsync` returned; nothing may be placed yet.
- **Starting → Ready** — the realm connected to the cluster, loaded its map, and reported an endpoint.
  Only now is it a placement candidate.
- **Ready → Draining** — no new placements; existing players are moved at safe moments (§ Drain).
- **→ Lost** — three heartbeats missed. Players' `IPlayerGrain`s are told, leases expire, clients are
  handed a new shard through the gate. **Recovery is a placement, not a resurrection**: the shard is
  gone and its volatile state with it.

### Placement — the megaserver

`IMapGrain.PlaceAsync(PlacementRequest)` answers with a shard and an endpoint. It is a score over
candidates, and the scoring is where a game expresses what "together" means to it.

**Hard filters** (a candidate that fails one is not scored):

```
shard.Version   == request.BuildVersion
shard.Content   == request.ContentHash          # ADR-022
shard.State     == Ready                        # never Draining, never Starting
shard.Region    == request.Region               # latency zone
shard.Population < shard.HardCap
shard.Access.Admits(request.Player)             # instanced content: the access list
```

**Score** (weights are a `.vxplacement` asset the game authors, not constants in the engine):

| Term | Default weight | Note |
|---|---|---|
| party or squad member present | 10 000 | effectively a hard pull — this is what "join your friend" means without a separate mechanism |
| guild member present | 400 | per member, capped |
| friend present | 200 | per friend, capped |
| same language / locale | 300 | GW2's most-underrated placement term |
| fill in the healthy band (40–80 %) | 250 | **prefers filling shards** — this is what makes consolidation possible |
| fill above 80 % | −(fill − 80) × 40 | falls away steeply, so the last 20 % is reserved for parties |
| shard age past `maxAge` | −100 | biases toward retiring old shards |
| recently transferred *away from* this shard | −5 000 | anti-flap: a player just moved off it should not be sent back |

**Spawn** when every candidate is above `softCap`, or none exists, or the arrival rate over the last
window predicts saturation within `spawnLeadTime`. Debounced by `spawnCooldown`, because two hundred
people zoning in at once must not produce twenty shards.

**Merge** when two or more shards for a map are each below `mergeThreshold` (default 25 % of
`softCap`) for `mergeDwell` (default 120 s). The lowest-population shard is marked `Draining`. The
dwell and the asymmetry — spawn at 90 %, merge at 25 % — are hysteresis, and without them the fleet
oscillates. This is the same lesson as `InterestChain`'s leave-hysteresis, at a different scale.

### Drain — evicting politely

A drained shard moves its players out; it does not disconnect them. The whole quality of this feature
is in *when*.

```csharp
public enum TransferReadiness { Ready, Soon, Blocked }
```

The realm answers for each player, and the game supplies the predicate — the engine ships a default
and does not pretend to know:

| State | Readiness | Why |
|---|---|---|
| idle, standing, walking | `Ready` | move now |
| in a conversation, at a vendor, mid-crafting | `Soon` | finish the interaction first |
| in combat | `Soon`, escalating to `Ready` at `combatGrace` (default 90 s) | a fight is not a licence to hold a shard open forever |
| in a scripted encounter, a boss fight, a story step, a match | `Blocked` until it ends or `hardDeadline` (default 15 min) | this is the one that makes drain acceptable to players |
| AFK past `afkGrace` | `Ready` | |

A shard reaching `hardDeadline` with `Blocked` players escalates to `IFleetGrain`, which is a live-ops
alert rather than a kill. **Nothing is force-disconnected by drain**; the escalation path ends in a
human or in a scheduled maintenance window.

### Health

Every realm heartbeats its `IShardGrain` every 2 s with population, tick p99, allocation rate, GC
counts, replication bandwidth and its own readiness census. That is not new telemetry — every one of
those numbers is already a `System.Diagnostics.Metrics` instrument in `Vixen.Net.Telemetry` and
[13](13-diagnostics.md), and the heartbeat is a sample of the meter rather than a second measurement
system. A shard whose tick p99 exceeds its budget for a sustained window stops being a placement
candidate before it stops being playable, which is the difference between a fleet that degrades and
one that falls over.

---

## The realm

A realm is a normal Vixen application ([17](17-app-heads-and-shipping.md) Model B) built with
`BuildVariant.Server`: headless host, `Vixen.Graphics.Null`, server content profile. What
`Vixen.Live.Realm` adds:

```csharp
return VixenApp.RunRealm<MyRealm>(args);      // the one-liner, mirroring VixenApp.Run<TGame>
```

| Piece | Job |
|---|---|
| `RealmHost` | boot: read `RealmSpec` from argv/env, connect the Orleans client, load the map, report `Ready` |
| `RealmSpec` | map address, shard id, version pair, endpoint, caps, placement weights, seed |
| `RealmDirectory` | **the only place a grain is called.** Request in, answer out, drained at `PreUpdate` |
| `RealmClock` | the tick, and the epoch a transfer rebases against |
| `PlayerAdmission` | ticket validation, lease acquisition, spawn, and the reverse |
| `RealmHeartbeat` | the 2 s sample |
| `MapLifetime` | load, ready, quiesce, unload — the scene-management join |

### The scene-management join

A shard is a map, and a map is content. The chain, using pieces that already exist:

```
RealmSpec.Map = "maps/queensdale"            # an addressable ADDRESS (ADR-013 — there is no other kind)
        ↓
assets.LoadAsync<SceneAsset>("maps/queensdale")
        ↓
sceneManager.Create("queensdale") → SceneHandle       # Core/Vixen.Engine/Scenes
        ↓
NetworkSceneId.From("queensdale")                     # hash of the NAME — already how the wire says it
        ↓
scene-placed networked objects get baked ids          # already derived, no message needed
        ↓
navmesh, spawn tables, event graph load as the scene's dependencies
```

Three things fall out, and all three are properties the engine already has rather than things this
document is asking for:

- **Client and realm agree on scene identity without being told.** `NetworkSceneId` is a hash of the
  scene name and the baked object ids are derived from position in the scene's own ordering, so a
  client that has loaded the map already knows what the props are before a packet arrives.
- **A shard's content identity is its catalog `BuildHash`**, which is the same number the session
  handshake already compares and the same number placement filters on (ADR-022). One number, three
  uses, no second registry.
- **The server content profile is a group membership question.** [17](17-app-heads-and-shipping.md)
  already specifies it: skip textures, audio and shader permutations. A realm's bundle is a small
  fraction of the client's, which is what makes shard start-up fast enough for elastic scaling.

✅ **`SceneCompiler` has landed, so this is no longer blocked.** It was written down as waiting on the
repository's largest known blocker; a `.vxscene` now compiles to a `SceneAsset` chunk an address
resolves to, and `SceneManager` loads one into a world additively. A realm loads a compiled scene. The
prefab-list shape this section was written against is no longer needed and should not be built.

### Shard kinds

One mechanism, four configurations. All of them are `IShardGrain`s; they differ only in access and
lifetime.

| Kind | Access | Lifetime | Placement |
|---|---|---|---|
| **Public** | anyone | while populated, plus `idleGrace` | scored (§ Placement) |
| **Instance** | an access list (a party, a guild, a raid roster) | tied to the group, plus a reconnect window | a fresh shard per group |
| **Match** | a matchmaker's roster | one match | allocated by `IQueueGrain` |
| **Persistent** | an owner and their permissions | long-lived, hibernated when empty and rehydrated on entry | one per owner — housing, guild halls |

**Hibernation is the interesting one and it is nearly free.** A persistent shard with nobody in it
writes its durable state through `IPlayerGrain`/`IGuildGrain` and stops. Entering it is a placement
that spawns it. The only new requirement is that a persistent map's *authored* state — a house's
furniture ([28](28-gameplay-framework.md) § Housing) — is durable rather than volatile, which the
lease model already handles.

---

## Transfer

The protocol. Everything in this document that says "seamless" is this section.

### The overlap

```
t0   Player crosses a portal, clicks "join party", or their shard begins draining
     Source realm A asks RealmDirectory: IPlayerGrain.RequestTransfer(target)

t1   Orchestrator: IMapGrain.Place → shard B (or spawns one)
     IPlayerGrain mints a TransferTicket { player, shardB, leaseEpoch+1, expiry, sig }
     B is told to expect this player and PRE-RESERVES a slot

t2   A sends PrepareTransfer{ endpoint(B), ticket, contentHash(B), tickEpoch(B) } to the client
     ── the player is still playing on A, still simulating, still shooting ──

t3   Client opens a SECOND NetworkSession to B and handshakes
     B validates the ticket, admits the player as DORMANT: no ownership, no input, no camera
     B streams initial interest — the client loads the map, warms bundles, builds the scene
     ── still playing on A ──

t4   Client reports TransferReady to A (it has the map, the session and the first snapshot)
     A picks a commit tick and asks B to acquire the lease at epoch n+1
     IPlayerGrain grants n+1; A's lease is now dead — this is the atomic moment

t5   A sends RealmHandoff{ volatile state, at tick T } to B over the CONTROL plane
     B applies it, acks
     A sends CommitTransfer{ atTick T } to the client and despawns the player at T

t6   Client switches its active session to B. Prediction rebases (§ below).
     A closes the old session. Done.
```

**Five properties, each of which is a failure mode designed out.**

**Nothing exists twice.** A is authoritative until t4 and B is authoritative after; the lease epoch is
the boundary and a grain turn is what makes it atomic. There is no window in which two realms both
believe they own the player, so there is no window in which two realms both write their inventory.

**The failure path is "the transfer did not happen".** If B never becomes ready, if the ticket
expires, if the client cannot reach B, if `RealmHandoff` is not acked before the deadline — A never
commits, keeps the player, releases the ticket, and tells the orchestrator. The player saw nothing.
That asymmetry is deliberate: every abort leaves the player where they already were, which is always a
valid state.

**The loading screen is a preload.** t3 is where the map is fetched and loaded, and it overlaps with
play. For cached content it is invisible; for a first visit it is a progress bar that runs while the
player is still walking around. This is the whole reason the second session opens early instead of
after the first one closes.

**The handoff payload reuses the replication codec.** `RealmHandoff` is written with the same
`IComponentReplicator` the server writes snapshots with. That is not a convenience — it means a
component that replicates transfers with no extra code, the encoding is already fuzzed and already
pinned by the wire corpus, and doc 16's bit-exactness gate covers realm-to-realm agreement for free.
Components that should transfer but not replicate (server-only state) declare
`[Replicated(Transfer = TransferPolicy.Always)]` and are written to the handoff and never to a
snapshot.

**Durable state is not in the payload** (ADR-021). Inventory, currency and progression stay in the
database; the payload carries the lease epoch and volatile simulation state — position, velocity,
buffs with their remaining durations, cooldowns, combat state, the animation graph's position. If the
payload is lost, nothing durable is at risk.

### Tick rebasing, and the honest cost

A and B run independent clocks. `TickManager` on the client is synchronised to A. At t6 it must be
synchronised to B, and the two are not related.

`PrepareTransfer` carries B's tick estimate so the client can pre-sync during the overlap — by t6 the
client's estimate of B's clock is already converged, and the switch is a pointer change rather than a
resync. What cannot be carried over:

- **`ClientPrediction`'s history is cleared.** Rolling back across a realm boundary is meaningless —
  the state to replay from belongs to a simulation that no longer owns this player.
- **`InputLog` is cleared and re-armed** from B's first snapshot.
- **`SnapshotBuffer`s are dropped**; motion holds for one interpolation delay and then interpolates
  normally.

**So the visible cost of a transfer is one interpolation delay of extra smoothing and one prediction
reset**, roughly 100–150 ms of slightly softer local response, once, at a moment the player initiated.
That is the price, it is stated rather than hidden, and `TransferMetrics` reports it —
`OverlapDuration`, `CommitLatency`, `PredictionResetCount`, `AbortReason` — because a transfer that
degrades is one that stops being seamless quietly.

### Intra-map seams — what P2 costs and why it is deferred

Splitting one continuous map across two processes needs everything above **plus**:

- **A handover band**: a strip of the map both realms simulate, so a player near the seam is known to
  both before they cross.
- **Ghosting**: A forwards read-only proxies of entities near the seam to B and vice versa, so an
  arrow fired across the seam exists on the far side, and a player at the seam sees the people on the
  other side of it.
- **Cross-seam interaction arbitration**: which realm adjudicates a hit whose shooter is in A and
  whose target is in B. This is the hard part, it is a distributed-consensus problem with a 16 ms
  budget, and every wrong answer is either a lost hit or a double hit.
- **Seam-aware interest**: `InterestChain` gaining a rule that admits ghosted entities, and
  `InterestGrid` gaining cells it does not own.

That is a document's worth of work with a real risk of being subtly wrong forever, in exchange for
maps larger than one process can carry. The 1.0 answer is to make one process carry a large map — the
soak says that is thousands of entities and low hundreds of players, which is a Guild Wars 2 map — and
to revisit this when a game exists that has actually outgrown it. **What is bought now, for nearly
nothing, is that the transfer protocol above is already the mechanism**: an intra-map seam is
`PrepareTransfer` triggered by a volume instead of by a portal, plus ghosting. The P2 work is
additive.

---

## Upgrades

Two kinds of change, and conflating them is why live updates are usually a maintenance window.

### Content-only — the "add an item" path

The catalog `BuildHash` changes; no assembly does. This is [28](28-gameplay-framework.md)'s entire
premise: a new item, a new quest, a new loot table, a rebalance, are all `.vxitem`/`.vxquest`/`.vxdef`
edits that produce a new catalog.

```
vixen content build  →  new catalog, new BuildHash, changed bundles only
        ↓
publish to the content server (Tools/Vixen.ContentServer — built)
        ↓
clients fetch the catalog overlay on next launch or hot           (Vixen.Assets ContentUpdate — built)
        ↓
realms: additive diff?  ── yes ──►  IDefinitionRegistry.Reload — live, no restart
                        ── no  ──►  new-version shards, old ones drain (below)
```

**"Additive" is proven by the build, not asserted by a human.** `ContentDiff` compares two catalogs and
classifies each change: a new address is additive; a changed definition whose fields are all
tolerated-live (numbers, text, references to things that already exist) is additive; a removed address,
a changed schema, a changed prefab used by live entities, or a changed replicated component layout is
**not**. Non-additive means a drain. The gate is `vixen live upgrade --content` refusing to apply a
non-additive diff live, with the reason, rather than applying it and finding out.

That is the "adding a new item should be about releasing an addressable update" requirement, made
literal: for the additive case the realm reloads a definition table and the client fetches a bundle,
and neither restarts.

### Build — the rolling upgrade

An assembly changed. `IFleetGrain` runs it:

1. Register the new `(BuildVersion, ContentHash)` pair as `Rollout.Target`.
2. Spawn new-version shards for the busiest maps first, sized to current demand.
3. Old-version shards → `Draining`. Placement stops sending anyone new (ADR-022's filter does this
   with no extra code — a new client's content hash simply does not match).
4. Players move out through § Drain's readiness rules. A raid finishes. A world boss finishes.
5. When an old shard empties, it stops. When the last one stops, the rollout is complete.
6. Roll back by making the *old* pair the target. Nothing about the mechanism is directional.

**The live-ops hazard this creates, named because it is not obvious.** Version-filtered placement
fragments the population: players on the old catalog can only meet players on the old catalog. On a
big map that is fine for an hour and corrosive for a day. Three bounds:

- **A rollout deadline.** Past `rolloutGrace` (default 24 h), old-version shards stop being created
  at all, and a client that has not updated is sent to the gate's update flow instead of to a shard.
- **The gate pushes the update.** A client on a WSS connection is told a new catalog exists the moment
  it is published, and `Vixen.Assets`'s overlay update is designed to be applied without a restart.
- **Fragmentation is a metric**, not a surprise: `Fleet.VersionSpread` is the fraction of players not
  on the target, and it is the number the rollout is watched by.

---

## Persistence

`Vixen.Live.Persistence`, behind `IPlayerRepository` / `IGuildRepository` / `ILedger`. ADO.NET against
PostgreSQL is the default and the only one shipped; the interface is the seam.

**Three rules, all of which exist because of MMO-specific failure modes:**

- **Single writer per aggregate.** Enforced by ADR-021's lease, not by a database lock. A row is only
  ever written by the grain that owns it.
- **Every movement of value is a ledger row**, append-only, with an idempotency key derived from the
  operation rather than generated: `(playerId, operationKind, operationId)`. A retried trade, a
  retried mail claim, a retried auction settlement writes nothing the second time. This is what makes
  [28](28-gameplay-framework.md)'s trade, auction and mail safe, and it is what makes support
  ("what happened to my sword") answerable.
- **Grain state is coordination, not gameplay.** `IPlayerGrain` persists its lease epoch and its
  transfer state through Orleans grain storage; it persists inventory and currency through the
  repository. Mixing them means gameplay data whose schema is Orleans's serializer's business, whose
  migrations are grain-storage migrations, and which cannot be queried by anything else — including
  the support tool, the economy dashboard and the analytics job.

---

## Diagnostics and operations

The whole of this folds into [13](13-diagnostics.md) rather than being a parallel system, for the same
reason doc 16's did.

| | |
|---|---|
| **Fleet view** | shards per map, state, population, version, tick p99, node. A table and a timeline |
| **Transfer trace** | one span per transfer, with the seven timestamps of § The overlap. `AbortReason` histogram |
| **Placement explain** | why *this* player went to *that* shard: the filter that excluded each candidate and the score of each survivor. Without it, placement complaints are unanswerable |
| **Version spread** | § Upgrades' fragmentation metric, per map |
| **Ledger query** | by player, by item, by time — the support tool |
| **`vixen live`** | `up`, `down`, `status`, `drain <shard>`, `upgrade`, `explain <player>` — the same operations the dashboard performs, in a terminal, because 3 a.m. |

Traces are OTLP through `Vixen.Net.Telemetry`'s existing exporter. Metrics are new instruments on
existing meters. The editor gains one `Fleet` panel, which is the networking panel doc 16 already owes
with a second tab.

---

## Testing

The strategy that makes this testable is the same one that made doc 16 testable: **the boring backend
is the one everything is tested against.**

| Area | Test |
|---|---|
| Placement | Property tests over the scoring function: a party is never split; a shard above `hardCap` is never chosen; scoring is total and deterministic for a given fleet |
| Spawn/merge hysteresis | Simulated arrival/departure traces (flash crowd, slow bleed, sawtooth) asserting the shard count does not oscillate and converges within N windows |
| Shard lifecycle | Randomised kill/restart/partition sequences leave no shard in a state with no owner and no player with a lease on a dead shard |
| Leases | **The duplication oracle.** Randomised concurrent transfers, aborts and crashes; assert total item count and total currency across the whole fleet is conserved, every time, over thousands of operations. This is the test the whole design exists to pass |
| Transfer | End-to-end over `Vixen.Net.Transport.Local` with `NetworkSimulation` — three realms in one process, players walking a loop between them; assert no duplicate spawn, no lost entity, no state divergence, bounded prediction resets |
| Transfer failure | Every abort path, injected: target never ready, ticket expired, handoff lost, source dies at t5, client dies at t3. Every one leaves the player playable |
| Placement backends | `Process` in CI on every push. `Docker` and `Kubernetes` (kind) on the nightly leg — the same shape as the platform matrix |
| Upgrades | A rollout from version A to B with players in flight, asserting nobody is disconnected and `VersionSpread` reaches zero |
| Content diff | Property tests over the additive classifier — a corpus of catalog pairs with the expected verdict, including the ones that must be rejected |
| Ledger | Idempotency under duplicate delivery; conservation under concurrency; the support query returns the causal chain |
| Scale | **`Samples/13-Mmo` soak**: 8 realms, 3 maps, 500 connections, 30 minutes, continuous transfers, a rolling upgrade mid-run. Budgets: bandwidth, tick p99, allocation, zero conservation violations |

`Vixen.Live.Placement.Process` is to this document what `Vixen.Net.Transport.Local` is to doc 16 — the
backend that makes the whole system an ordinary unit test rather than an integration environment.
Everything above except the two backend legs runs in one process, on a laptop, deterministically.

---

## Cost

Engineer-months, calibrated against [14](14-roadmap.md)'s sizing — one experienced .NET engineer, full
time.

| # | Milestone | Deliverable | EM |
|---|---|---|---|
| **L0** | **Foundations** | `Live.Abstractions`, `Live.Realm` host, `Placement.Process`, the six-project template, one map, one shard, no orchestrator intelligence. **A dedicated server with a lifecycle.** | 3.0 |
| **L1** | **Orchestrator** | Orleans cluster, the eight grains, placement scoring, spawn/merge hysteresis, health, drain readiness, `Placement.Kubernetes` + `.Docker` | 4.0 |
| **L2** | **Transfer** | Tickets, the overlap protocol, leases, handoff codec reuse, prediction rebase, every abort path, the duplication oracle | 3.0 |
| **L3** | **Gate and persistence** | ASP.NET gate, login/characters/catalog, WSS service plane, repositories, the ledger, idempotency | 3.5 |
| **L4** | **Live-ops** | Content diff + live reload, rolling upgrades, fleet view, placement explain, `vixen live`, the soak | 2.5 |
| | **Total** | | **16.0** |

Plus [28](28-gameplay-framework.md)'s **≈ 25.5 EM** for the gameplay libraries, which is where most of
the work is.

**Where to stop, if stopping.** Each milestone is shippable:

- **After L0** you have a dedicated-server game with an operable lifecycle. Most co-op games want
  nothing more.
- **After L1** you have elastic capacity and megaserver placement — a session-based game (arenas,
  battlegrounds, extraction shooters) is complete here, and never needs L2 at all.
- **After L2** you have a persistent world with map travel. This is the smallest thing that is
  honestly an MMO.
- **L3** is what makes it a *product* — accounts, characters, an economy that survives a crash.
- **L4** is what makes it operable by people who are not the people who wrote it, and it is the one
  most often deferred and most regretted.

---

## Risks and open questions

| # | Risk | Severity | Mitigation |
|---|---|---|---|
| M1 | **A grain call reaches the frame path.** The single way this design fails, and it will not look like a bug — it will look like occasional stutter | High | ADR-016's rule; `RealmDirectory` as the only call site; an analyzer that fails the build on `await` of an `IGrain` inside a system body; the tick p99 in the heartbeat |
| M2 | **Item duplication across a transfer.** Unrecoverable reputationally | High | ADR-021's lease; the conservation oracle in CI; the ledger as the audit trail |
| M3 | **This is a second engine.** 41.5 EM against the engine's own 48 | High | The L0–L4 and G0–G8 ordering, each shippable; the "where to stop" paragraphs are not decoration |
| M4 | ~~**`SceneCompiler` is upstream of all of it**~~ | — | **Retired.** It is built, and so is the boot path that opens what it produced. L0 builds against a compiled scene; the prefab-list stand-in this row planned for is not needed |
| M5 | **Kubernetes UDP addressing** — `hostPort` needs a node port range, node external IPs, and a firewall the operator controls | Medium | Documented as the one cluster prerequisite; `Placement.Process` and `.Docker` need none of it; Agones named as the escape hatch for anyone already running it |
| M6 | **Population fragmentation during rollouts** | Medium | § Upgrades' three bounds; `VersionSpread` as the watched metric |
| M7 | **Orleans is a large dependency in a repository that has re-derived almost everything** | Medium | Confined to `Live/` by an architecture rule; the client never links it; MIT; the alternative is writing a distributed lock service and a membership protocol, which is the thing this repo would be wrong to re-derive |
| M8 | **Intra-map seams are deferred and someone will want them** | Medium | Named as P2 with its four-part cost; the transfer protocol is written so the P2 work is additive |
| M9 | **A fleet nobody has run at scale is fiction** | Medium | `Samples/13-Mmo` is an exit criterion, not a sample; the numbers in it are budgets |

| # | Open question | Recommendation |
|---|---|---|
| M-Q1 | Ship a relay/proxy transport in-box, or leave it to addons? | **Addon**, consistent with doc 16's Steam/EOS position — but `ITransport` and the endpoint-as-data property are the seam, and both are in L0 |
| M-Q2 | Does the gate host chat, or is chat its own service? | **The gate**, until it is not. One WSS connection is the client's whole service plane; splitting it is a scaling decision to make with numbers |
| M-Q3 | Postgres only, or an abstraction with two implementations from day one? | **One implementation behind an interface.** Two implementations from day one is two half-tested ones, which is ADR-012's argument in a different domain |
| M-Q4 | Move `Tools/Vixen.App` into `Core/`? | **Yes, separately.** Allow-list the `Live → Tools` edge now so this work is not blocked on that argument |
| M-Q5 | Region/latency zones — modelled by the engine or by the game? | **Engine**, as an opaque string with a hard placement filter. Every game has them and none of them mean the same thing |
