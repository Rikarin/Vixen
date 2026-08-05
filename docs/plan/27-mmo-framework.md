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
Gameplay/                               # ── NEW TOP LEVEL: doc 28's gameplay libraries ──
├── Vixen.Gameplay*/                    # engine-side runtime, and a game may decline all of it

Live/                                   # ── NEW TOP LEVEL: the online service layer ──
├── Vixen.Live.Abstractions/            # ✅ ShardId · ShardKey · RealmSpec · TransferTicket · endpoints
├── Vixen.Live.Abstractions.Tests/      # ✅ no Orleans, no engine, no ASP.NET. The client may see this
├── Vixen.Live.Cluster/                 # grain INTERFACES only (Microsoft.Orleans.Sdk)
├── Vixen.Live.Cluster.Tests/
├── Vixen.Live.Orchestrator/            # grain implementations, placement director, heuristics, upgrades
├── Vixen.Live.Orchestrator.Tests/
├── Vixen.Live.Placement.Kubernetes/    # KubernetesClient 19.0.2
├── Vixen.Live.Placement.Kubernetes.Tests/
├── Vixen.Live.Placement.Docker/        # hand-written Engine API client
├── Vixen.Live.Placement.Docker.Tests/
├── Vixen.Live.Placement.Process/       # ✅ Process.Start — dev, CI, and small deployments
├── Vixen.Live.Placement.Process.Tests/ # ✅
├── Vixen.Live.Realm/                   # ✅ the realm host: game loop + Vixen.Net (+ Orleans client at L1)
├── Vixen.Live.Realm.Tests/             # ✅
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
└── 14-Mmo/                             # the vertical slice, and the exit criterion
```

**Why two new top levels rather than more of `Core/` or `Tools/`.** The `Live/` projects are not
engine runtime — they run with no renderer, no window, no game loop in three of the four cases, and a
client must never link them. They are not tools either: a tool is something a developer runs, and
these are shipped and operated. `Gameplay/` is the same argument from the other side: doc 28's
libraries *are* runtime and carry the same profile `Core/` does, and the separate folder is what makes
"an inventory system" visibly a layer somebody chose rather than something the engine grew. A
single-player racing game references none of it.

Both give the layer rule something to be expressed against, and it is enforced in
[`Build.ArchitectureRules.cs`](../../build/Build.ArchitectureRules.cs) alongside the `Vixen.Ui` ⇸
`Vixen.Engine` one:

- Nothing in `Core/`, `Gameplay/`, `Platform/`, `Editor/` or `Raven/` may reference `Live/`.
- `Gameplay/` may not reference `Editor/`, `Tools/` or `Live/`.
- `Live/` may not reference `Editor/`.
- `Tools/` → `Live/` is deliberately *unconstrained*: `vixen live` is § Diagnostics' own requirement,
  and a CLI that operates the fleet has to link it. The layering here is not a total order, and
  pretending otherwise would have made the CLI the exception instead.

**One wart, named rather than hidden.** `Vixen.Live.Realm` needs the application host, which is
`Tools/Vixen.App`. So `Live/` sits above `Tools/Vixen.App`, which is a `Live → Tools` reference the
existing layer check would flag. Two ways out: allow-list that one edge, or move `Vixen.App` into
`Core/` where an application host arguably belongs anyway. **Recommendation: move it**, as a separate
change with its own reasoning, and allow-list the edge in the meantime so this work is not blocked on
that argument. ✅ **Allow-listed**, as `AllowedUpwardReferences` — a *pair* rather than a project name,
so a second `Live/` → `Tools/` reference fails until somebody decides it should not.

**`Samples/14-Mmo` became `14-Mmo`.** Thirteen was taken by `13-ThirdPersonShooter` between this
document being written and the work starting.

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
| `IAccountGrain` ✅ | account | ⚠ **Not in this table as written, and [28](28-gameplay-framework.md)'s G8 is what showed it was missing.** A collection is *account-wide* — a mount earned on one character is owned by all of them — and there is no key on `IPlayerGrain` that can own that, because it is keyed by account *and character*. The alternative is five characters writing the same rows at once, which is the one thing the single-writer discipline exists to prevent. It knows nothing about collectibles: the vocabulary is an address, a source and an order, which is all doc 28's mechanism is |
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

⚠ **The catalog does not record a shape, so none of this is reachable yet, and that is the one thing
this section assumes without saying.** `ContentDiff` can tell a rebalance from a reshape only if it
knows what shape each address had, and a `CatalogEntry` carries an address, a content id, a bundle, a
provider, a size and its dependencies — nothing about the layout of the thing at that address.
Treating "no schema recorded" as "schema unchanged" would classify a definition that gained a field as
a *modification*, and a modification of a definition is **additive** — a live reload under a world
full of entities built against the old layout, which is the unrecoverable direction.

So an entry whose shape is unknown is never additive whatever its kind, and the consequence is worth
stating plainly rather than discovering: **until the content build emits a schema hash per address, no
content update can be applied live.** That is the correct state for it to be in — the alternative was
a classifier that said yes on data it could not read — and it makes the schema hash a prerequisite of
this whole path rather than a refinement of it.

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
| Scale | **`Samples/14-Mmo` soak**: 8 realms, 3 maps, 500 connections, 30 minutes, continuous transfers, a rolling upgrade mid-run. Budgets: bandwidth, tick p99, allocation, zero conservation violations |

`Vixen.Live.Placement.Process` is to this document what `Vixen.Net.Transport.Local` is to doc 16 — the
backend that makes the whole system an ordinary unit test rather than an integration environment.
Everything above except the two backend legs runs in one process, on a laptop, deterministically.

---

## Cost

Engineer-months, calibrated against [14](14-roadmap.md)'s sizing — one experienced .NET engineer, full
time.

| # | Milestone | Deliverable | EM |
|---|---|---|---|
| **L0** | **Foundations** ✅ | `Live.Abstractions`, `Live.Realm` host, `Placement.Process`, the six-project template, one map, one shard, no orchestrator intelligence. **A dedicated server with a lifecycle.** | 3.0 |
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

## L0, as built

The state of the tree is [`docs/overview.md`](../overview.md) § 1.13 and it wins on disagreement; what
belongs here is the handful of places where **building L0 changed what this document says**, and why.

**Three projects, 133 tests, and one gate rule.**
[`Vixen.Live.Abstractions`](../../Live/Vixen.Live.Abstractions/README.md),
[`Vixen.Live.Placement.Process`](../../Live/Vixen.Live.Placement.Process/README.md) and
[`Vixen.Live.Realm`](../../Live/Vixen.Live.Realm/README.md), each with a test project, plus the
`Live/`–`Gameplay/` layer rules in
[`Build.ArchitectureRules.cs`](../../build/Build.ArchitectureRules.cs). The written half is
[`docs/guide/live`](../guide/live).

**`RealmApp.Run<TRealm>`, not `VixenApp.RunRealm<TRealm>`.** § The realm writes the one-liner as a
member of `VixenApp`, and it cannot be one: `VixenApp` is in `Tools/Vixen.App`, which sits *below*
`Live/`, so adding a member there would need `Vixen.App` to reference `Vixen.Live.Realm` — the layer
rule in the wrong direction — and a static class cannot be extended from outside. The entry point
moved rather than the layering, and it mirrors the original call for call. M-Q4's recommendation to
move `Vixen.App` into `Core/` would let the original name come back.

**The lifecycle channel is stdio, and that is a deliberate L0 answer rather than a stub.** A realm with
no orchestrator still needs a control plane, and the smallest one that is not a lie is the process's
own standard streams: `RealmSignals` is four lines — the realm writes `vixen-realm ready <endpoint>`,
the launcher writes `vixen-realm drain`. Every one of ADR-019's three backends can already read and
write them, which is why the same mechanism serves `Process`, Docker and a pod. It is a **lifecycle
channel and must not become a management API**: nothing player-specific, nothing per-tick, nothing
that needs an answer. What replaces it at L1 is grain calls through `RealmDirectory`, not a larger
version of it.

**`RealmDirectory` was built at L0 although there is nothing to call.** ADR-016's rule is the way this
design fails (M1), and the thing that enforces it is not Orleans — it is *where the callback runs*. So
the type exists now, with `Ask`/`Drain` and a fault count, and L1 supplies the grain call as the
delegate body without the drain changing. Its tests assert the property that matters: the answer is
applied on the thread that drained, and a callback that throws does not take the rest of the queue
with it.

**`MapLifetime` is much thinner than § The scene-management join implies, because the host already did
the work.** `AppConfig.StartupScene` opens the map before `OnInitialise`, reports its own failures and
survives them; a realm that loaded it a second way would be a second code path for content failures,
tested half as often. What was actually missing is the question *is it up yet* — which is what
separates `Starting` from `Ready`, and which nothing in the host answers.

**`IRealmPlacement` gained `ListAsync`.** ADR-019's prose says "start, stop, list, watch" and its code
sketch has four methods. The prose was right: an orchestrator that restarts has grain state saying
which shards should exist and no memory of which processes do, and reconciling those two is the
other half of why a Kubernetes realm is a labelled, owner-referenced `Pod` rather than an anonymous
one.

**An unbound endpoint may name no host either.** § Repository layout assumed the orchestrator knows the
node and the backend picks the port. Placing onto a Kubernetes scheduler it cannot know the node,
let alone its external address — so `RealmEndpoint.IsUnbound` is *"no port"*, `default` is a request
rather than nonsense, and the backend fills in whatever the spec left out.

**A shard with no cluster key signs its own.** `RealmHost.DevelopmentSigner` derives one from the spec,
and is documented as not being a security mechanism: everything it is derived from travels in plain
text on a command line. What it buys is that a deployment which forgot to configure a key gets a fleet
that refuses everybody — which is loud — rather than one that admits anybody, which is not.

**`dotnet new vixen-mmo` scaffolds five of the eight projects, and the three it leaves out are the
ones it cannot reference.** § The three assemblies a game writes calls the reference graph the reason
the template exists — *"getting this graph wrong on day one is the kind of mistake that is discovered
in month six"* — so the template is `.Contracts`, `.Shared`, `.Realm`, `.Client` and `.Content`, with
that graph asserted by reading the project files. `.Cluster`, `.Orchestrator` and `.Gate` each need a
package that does not exist until L1 or L3, and a template pinning a package nobody publishes is
worse than no template at all: it fails at the one moment a person has no context to debug it. That
is the same judgement `vixen-plugin` waited on, and the template grows when they land.

It is also the first *multi-project* template, which cost two changes to the template gate:
`TemplateCompiler` compiles a multi-project template as one library — four `Main` methods are each
the only one in their own assembly, and the gate is about API drift rather than entry points — and
the "one project file, named after the project" assertion became "every project file, in a directory
named after it". What the compile gate consequently cannot see is a missing project reference, so
that is asserted separately by reading the csproj files, which is where the graph is written down
anyway.

**What L0 still owes.** `Samples/14-Mmo`, which § Testing scopes as the whole document's soak — eight
realms, three maps, five hundred connections, a rolling upgrade mid-run — and which is therefore
honestly an L4 artefact rather than an L0 one.

## L1, in progress

Taken in three slices, because 4.0 EM is not one change: **the director**, then the grains, then the
container backends. The first has landed.

**Placement is a pure function, and it exists before the cluster does.** `Vixen.Live.Orchestrator`
holds the hard filters, the score, the explanation and `MapFleet`'s hysteresis, and references no
Orleans at all. That ordering is deliberate rather than incidental: the intelligence is a function of
numbers and a small state machine, so § Testing's property tests — a party is never split, a shard
above its hard cap is never chosen, scoring is total and deterministic — run 45 000 randomised fleets
in under a second, and the grains that will host it are a scheduling decision on top rather than a
rewrite.

**The affinity terms arrive as counts.** How many of a player's friends are on a shard is a question
only the thing holding the fleet's roster can answer, so `ShardCandidate` carries counts and
`IMapGrain` will compute them. Scoring never touches a database, which is what makes the property
tests possible at all.

**Every placement explains itself, and § Diagnostics' `placement explain` is therefore free.** Each
candidate gets a verdict naming either the filter that excluded it or the terms that made up its
score. § Placement lists the filters as one line of pseudocode; they are seven distinct values here,
because "the shard your guild is on is running last week's build" and "the shard your guild is on is
full" are different conversations with the same player.

**Two defects the simulated traces found, and both are now policy fields with reasons.**
§ Testing asks for flash-crowd, slow-bleed and sawtooth traces asserting the shard count does not
oscillate; writing them found that the first implementation was wrong twice.

- *The arrival rate was diluted by its own window.* Counting arrivals over a nominal sixty seconds
  makes ten a second read as 0.17/s until the window fills, so the fleet spawned **after** saturation
  rather than before it — twenty of two hundred players refused while capacity they had been promised
  was still loading. The rate is now measured over the span the arrivals landed in, and
  `MinimumRateSpan` is the floor that stops the opposite mistake: a party of ten arriving together is
  not ten a second.
- *Resetting the merge dwell after each drain made a cyclical map leak shards.* A map that spawns
  every cycle and merges once every two minutes grows a shard per cycle and never gives it back. What
  is actually needed is one merge **in flight** at a time — a drained shard's players have not moved
  yet, so the fill it is about to relieve has not recovered — and once that merge has finished no new
  evidence is needed, because the map has already been quiet for the dwell.

Neither is a tuning question, and both are the kind of thing that is discovered in production rather
than in a unit test unless somebody writes the traces. § Testing was right to ask for them.

**One more knob than § Placement lists: `MaxShards`.** Every elastic system wants a number that says
"if this is where we have got to, something is wrong and a human should hear about it". Reaching it
stops the spawning rather than raising an error, because a map at its ceiling is still a map full of
people playing.

### Slice two — the cluster contract and the grains

**Orleans 10.2.2 is still current, re-verified against `api.nuget.org` rather than taken from
ADR-016.** The SDK ships `analyzers/dotnet/cs/Orleans.CodeGenerator.dll` — a Roslyn incremental
generator, not an IL weaver — so ADR-002 survives unchanged, and the package is confined to `Live/`
by `CheckArchitecture` rather than by discipline.

**Four grains, not eight, and the four that are missing are missing for two different reasons.**
`IMapGrain`, `IShardGrain`, `IPlayerGrain` and `IFleetGrain` are here.

> ⚠ **Five, since [28](28-gameplay-framework.md)'s G8, and the fifth is one this document never
> listed.** `IAccountGrain` — see § Grains. The reasoning below was right about which of doc 27's own
> eight belonged here; what it could not know is that the grain table's *keys* were incomplete.
> `IPlayerGrain` is keyed by account **and** character, and doc 28's collections are account-wide, so
> nothing in the table could own them. That is a gap a milestone above found in the substrate rather
> than the other way round, which is the ordering this pair of documents is supposed to have. `IPartyGrain` turns out not to
be needed for placement at all — the map keeps its occupants' party and guild ids, so counting them is
local and the social-graph query never happens on the control plane. `IGuildGrain`, `IQueueGrain` and
`IInstanceGrain` belong to features in [28](28-gameplay-framework.md) rather than to this substrate,
and declaring an interface nobody implements is a promise rather than a contract.

**Every grain is an adapter over a plain class, and that is the pattern rather than an accident.**
`MapCoordinator`, `ShardLifecycle` and `PlayerLeaseState` are state machines a test constructs and
drives; the grain supplies the one property they cannot give themselves, which is that they are never
re-entered. Writing the state machines inside the grains would make them untestable without a silo,
which is how a coordination layer ends up with no tests at all — and § Testing asks for randomised
kill/restart sequences and a conservation oracle, neither of which anybody writes against a cluster.

**ADR-017 cost a file, and the file is worth naming.** `Vixen.Live.Abstractions` is what a client
transitively references, so it cannot carry `[GenerateSerializer]`; the vocabulary therefore crosses a
grain call through Orleans **surrogates** declared in `Vixen.Live.Cluster`. The alternative — a second
`ShardId` with Orleans attributes on it — is two types that mean the same thing and drift, which is
the failure the three-assembly split exists to prevent. A type added to the vocabulary and not to
`Surrogates.cs` fails at the first grain call that carries it, so every one of them is round-tripped
through a real serializer in a test.

**That test found a latent bug that had nothing to do with Orleans.** `default(RealmEndpoint)` and
`new RealmEndpoint("", 0)` both print "nowhere" and compared *unequal*, because a struct's property
initialisers do not run for `default`. Two entries in a `HashSet` for one place, and a shard key is a
dictionary key in every fleet the orchestrator holds. The four string-carrying value types now have
hand-written equality that normalises.

**A heartbeat's reply is how a realm learns it should drain.** `IShardGrain.Heartbeat` returns the
shard's state, so nothing in the control plane ever calls *into* a realm — an entire direction of
connectivity, authentication and firewall rules that does not have to exist. § Health describes the
heartbeat as a report; making it a poll as well is free and removes a whole subsystem.

**`PlaceStatus.Starting` is an answer rather than an error**, because a client told "starting" shows a
progress bar and a client told "refused" shows a failure. Conflating them is how an elastic fleet's
ordinary behaviour becomes a support ticket.

**The realm's Orleans client is a project this document does not list, and the precedent for adding
one is in doc 16.** `Vixen.Net.Telemetry` was split out "so an offline game links no protobuf
serializer"; `Vixen.Live.Realm.Cluster` is that argument a tier up. § Cost's L0 is *a dedicated server
with a lifecycle*, and such a realm has no orchestrator to talk to — folding the cluster client into
`Vixen.Live.Realm` would put a cluster framework into every realm binary that ships, including the
ones that never join a cluster, and § The scene-management join names shard start-up time as the thing
that makes elastic scaling possible. A realm that *is* orchestrated references it and pays for it,
which is ADR-018's design rather than a concession.

**M1 is now asserted rather than only ruled.** Every call in `RealmCluster` is posted through
`RealmDirectory`, and the test that matters runs twenty frames against a cluster answering in 250 ms
and requires the twenty frames to take under 200 ms in total. The rule was always going to be obeyed
on the day it was written; what this buys is that breaking it later fails a test rather than producing
occasional stutter nobody can attribute.

**The heartbeat's reply removed a subsystem.** § Health describes the heartbeat as a report and §
Drain describes the orchestrator moving players out — which reads as the control plane calling *into*
a realm. It does not have to: `IShardGrain.Heartbeat` returns the shard's state, so a realm learns it
should be draining from the answer to a message it was sending anyway. That is an entire direction of
connectivity, authentication and firewall rules that never has to exist, and it is free.

**A map ticks itself.** § Placement's spawn and merge heuristics need observing on a cadence, and the
obvious hosts for that are a background service walking every map or an Orleans reminder. It is a
grain timer instead: a service would make one thread the serialisation point for every fleet decision
in a region, which is the bottleneck per-map keying exists to avoid; and a reminder is for work that
must survive deactivation, which this must not — a map nobody has asked about for hours has no fleet
worth observing, and its shards' own idle grace has already retired them.

### Slice three — the container backends

**`Placement.Docker` is written and `Placement.Kubernetes` is not.** ADR-019's claim about the
Engine API held up: six calls, a `SocketsHttpHandler` with a `ConnectCallback`, and no package. The
one piece that is not ordinary HTTP is the log framing — a container without a TTY multiplexes stdout
and stderr behind eight-byte headers — and that is thirty lines. `Docker.DotNet` would have saved
none of it.

**The stdio lifecycle turns out to be an L0 mechanism, not a placement mechanism.** § Drain reads as
though every backend needs a way to say "drain" to a realm. It does not: a realm learns to drain from
the reply to its own heartbeat (slice two), so only a deployment with *no orchestrator* needs the
stdin channel — which is `Placement.Process` and nothing else. Docker therefore reads a realm's logs
and never writes to it, and `StopMode.Drain` is only the deadline. The corollary is a named
limitation rather than a gap: an unorchestrated Docker deployment cannot drain politely, and should
use `Placement.Process`.

**Disposing the Docker backend leaves the containers running**, which is the opposite of the process
backend and deliberate. An orphaned child process holds a UDP port for nobody; a container that
outlives the orchestrator which created it is a shard still serving players — and ADR-019's labels
are how the next orchestrator finds it. `ListAsync` asks the daemon rather than a dictionary for
exactly that reason.

**`PortPool` moved to `Vixen.Live.Abstractions`.** It started beside the process backend and the
second backend wanted it; ADR-019's Kubernetes `hostPort` range will be the third. Copying it would
have been three implementations of one range allocator.

**`Placement.Kubernetes` is built, and ADR-019's package judgement was right in both directions.**
The Engine API surface is six calls and was hand-written; the Kubernetes object model is generated
from an OpenAPI spec that changes every quarter and `KubernetesClient` 19.0.2 (verified current) does
it. What this project adds on top is a six-method seam — a fake of `IKubernetes` is not something
anybody writes, and behind the seam every decision the ADR argues about is asserted on every push.

**One thing the ADR does not mention, and it is the interesting one: this is the only backend that
overrules the realm about where it is.** Everywhere else the realm's own word about where it bound
wins, because it is the one holding the socket. In Kubernetes the realm's view is inside the pod's
network namespace, so it is exactly the address a player cannot use — the client-facing endpoint is
the node's `ExternalIP` and the `hostPort`, and it is not knowable at `StartAsync` because the
scheduler has not placed the pod yet. So `Started` carries no endpoint and `Ready` carries the real
one.

**The spec a pod is handed names `0.0.0.0`.** Writing the tests caught this: an empty host produces a
`RealmSpec` that `TryDecode` refuses, so the realm would exit with "this process is not a realm" and
the pod would look like a bad image. The spec's endpoint is a binding hint for the process; the
client-facing address is the placement event's.

**What L1 still owes.** The `.vxplacement` importer —
`PlacementWeights.Parse` reads one at boot, and turning it into an addressable asset with an inspector
is editor-side work that belongs with doc 11 rather than here. The gate that would call
`IMapGrain.Place` on a player's behalf is L3's.

## L3, in progress

⚠ **Taken before L2, deliberately, and it is the one milestone ordering this document did not
anticipate.** § Cost orders transfer before the gate because a persistent world needs map travel; what
that ordering misses is that *nothing mints a ticket yet*. `TransferTicket` and its signer have existed
since L0 and every realm checks one on admission, but the issuer is the orchestrator on a player's
behalf — and "on a player's behalf" is a gate. L2's overlap protocol is a second session opened by a
client that has one, so building it against a fleet nobody can log into would mean testing the
protocol through a stub of the thing L3 builds anyway. The dependency runs the other way round from
the numbering, and L3 is not blocked on L2 in any part.

### Slice one — durable state and the ledger

**The world has accounts, and that is what makes conservation checkable.** § Persistence says every
movement of value is a ledger row; it does not say what a loot drop moves value *from*. If the answer
is "nowhere" then every faucet and every sink is an exception to the sum-to-zero rule, and a rule with
exceptions cannot be a database constraint. So a drop is a transfer out of `world/loot`, a sale is a
transfer into `world/vendor`, and the invariant becomes total: **every intent's deltas sum to zero,
per asset, always**. The cost is a handful of named accounts whose balances go steadily negative, and
that is not a defect — `world/loot`'s balance is exactly how much of an asset has entered the economy,
which is the number [28](28-gameplay-framework.md) § Economy's dashboard is built to show and which no
other schema gives for free.

**An intent carries several movements, and that is not a convenience.** A trade takes a sword off one
character and puts gold on another. Two appends means a crash between them is a lost sword, and no
amount of retrying fixes it because the retry's idempotency key already exists. One intent, applied
whole or not at all, is the only shape that is safe.

**The lease reaches the database as a fence, and the fence is the `where` clause.** ADR-021 says a
realm may only mutate durable state while it holds the lease; what makes that true rather than
intended is that `lease_epoch` lives on the row it fences and is compared in the same statement that
writes it. Reading the epoch and then writing would be the same check with the race in the middle —
and the race is precisely the transfer being guarded. A realm that lost its lease mid-combat keeps
simulating, its buffered writes arrive late, and the database declines them without anybody having to
notice in time.

**The idempotency key is a primary key rather than something the application remembers**, so a
duplicate delivery loses an insert. § Persistence's *"derived from the operation rather than
generated"* is the load-bearing half and worth restating as a failure: a key minted per attempt is a
different key on the retry, so the retry is a second trade. ⚠ **A replay must be recognised before the
balance check**, not after — the case that proves it is a retry arriving once the character can no
longer afford what they already paid.

**`Applied` and `Replayed` are both success and the caller must not tell them apart.** That is the
whole point of the key: the caller cannot know whether its first attempt reached the database and does
not have to.

**Balances are a projection, and `ReconcileAsync` is what makes a cached aggregate safe to believe.**
§ Testing asks for a conservation oracle in CI; offering the same question as an *operation* costs
nothing and is worth more, because a fleet that has been up for a month wants the answer a nightly job
can get cheaply. That is the shape `vixen live` will call at L4.

**No database driver, and it is the same judgement ADR-019 made about Docker pointed the other way.**
`SqlPersistence` takes a `System.Data.Common.DbDataSource`; the deployment constructs
`NpgsqlDataSource.Create(…)` and hands it over. The SQL is PostgreSQL and does not pretend to be
portable (M-Q3), but a game engine pinning a driver's version, TLS story and pooling configuration for
every game that links it buys nothing — pooling and tracing are configured where the deployment
already configures them.

**`AccountRecord` has a handle and no password, and there is not going to be one.** An engine that
shipped a credential store would ship a liability its authors do not operate: hashing parameters that
age, breach response, reset, MFA, recovery. What it can honestly own is the mapping from *whatever
your authority calls this person* to the account the world knows; everything upstream is the
deployment's, and the seam is slice two's `IAccountAuthority`. Same position doc 16 took on Steam and
EOS, and M-Q1 restated.

**`IGuildRepository` is not built, and the reason is slice two's from L1.** § Persistence names it, but
a repository's single-writer discipline comes from the grain that owns the aggregate — and
`IGuildGrain` belongs to [28](28-gameplay-framework.md) rather than to this substrate. A repository
with no owner would be a table anything may write, which is the one thing this layer exists to prevent.
It lands with the grain.

**What a test can say about SQL, which is less than it looks and more than nothing.** Every semantic
above is asserted against `MemoryPersistence` — this tier's `Vixen.Net.Transport.Local` — including
the duplication oracle at four thousand operations across eight lanes with duplicate deliveries, stale
epochs and overdrafts mixed in, which finishes in a hundred milliseconds. Against a database that is a
test nobody runs, which is how a persistence layer ends up with no tests at all. Whether PostgreSQL
accepts the statements is the nightly leg's question, beside `kind` and Docker.

### Slice two — the gate

**The order of `POST /v1/play`'s checks is the design, and it is why that route is one method rather
than a pipeline of filters.** Content version first, because *"fetch the update"* is a different
conversation from *"no"* and only the gate knows enough to have it — placement would refuse a
mismatched client anyway (ADR-022's filter), and answering `UpdateRequired` instead is the difference
between a rolling upgrade and a maintenance window. Then the map, then ownership before existence so
that probing character ids tells a stranger nothing, then suspension so a banned account never costs
the cluster a grain call, and the lease epoch last because it is the only step that changes anything
observable twice.

**`PlayStatus` has four values and the two easy ones to omit are the two that matter.** § Placement's
`PlaceStatus.Starting` already survived slice two of L1 for this reason; `UpdateRequired` is its
sibling and is ADR-022 reaching the client. A client that renders either as a failure turns ordinary
behaviour — an elastic fleet spawning, a fleet mid-rollout — into a support ticket.

**The gate predicts the lease epoch; it does not take the lease.** ADR-021 has the receiving realm
acquire, and it must stay that way: a gate that acquired would take the lease off whoever holds it for
everybody who merely opened the character screen. So the number in a ticket is what the realm *will*
ask for, an unredeemed ticket costs nothing, and a stale one is superseded on arrival — which is the
same property that makes a replayed ticket harmless.

**Two token types and two keys, which § The three planes implies and does not say.** A `GateToken`
admits one *account* to the gate for hours and is checked by the gate; a `TransferTicket` admits one
*character* to one shard for a minute and is checked by a realm. Sharing a key would let a realm mint
gate sessions; making them one type would let a realm be handed something that authorises reading an
account's character list. ⚠ The gate token is stateless and therefore **not revocable before it
expires** — its lifetime is the whole of its bound, so suspension is checked against the account on
every request that matters rather than against the token.

**`GateOptions.Maps` is a closed list, and an unfiltered gate is a real hole rather than a tidiness
one.** A map address arrives from a client and `IMapGrain` is keyed by it, so a gate that passed
anything through would let a stranger create a fleet for whatever they typed and watch the
orchestrator try to start it. Empty means "any", which is offered for a single-map game and is a
decision rather than a default.

**`IAccountAuthority` is the seam, and the engine ships no credential store.** § Persistence is silent
on login and the silence is the right answer: hashing parameters that age, breach response, reset,
MFA and recovery are a liability the engine's authors do not operate, and every deployment already has
something that answers *which account is this*. `DevelopmentAuthority` trusts whatever it is told,
says so, and is **not registered by default** — a gate with no authority refuses everybody, which is
loud. That is `RealmHost.DevelopmentSigner`'s judgement again.

**The wire shapes live in `Vixen.Live.Abstractions`, not in the gate.** § The three assemblies a game
writes puts the gate's DTOs in something both ends reference, and this is the engine's own half of
that: two copies of `PlayResponse` would be two shapes that drift, and the drift presents as a client
that cannot log in after a server deploy. The client is a NativeAOT binary, so the JSON is
source-generated — which is also why `RealmVersion` crosses as its one canonical string rather than
growing a second spelling as an object.

**The service-plane socket is a push channel and nothing else.** Anything a client sends up it is
treated as a ping, because a socket that also carried commands would need its own authorisation, rate
limiting and closed-set deserialization — the whole security surface doc 16 built once already. ⚠ It
is **allowed to be down and every message on it is allowed to be lost**: a push is a hint to go and
ask, and anything that would be wrong to lose is a request instead. It authenticates on the
`Authorization` header and refuses a query-string token, which a browser client would need the
`Sec-WebSocket-Protocol` convention for; a game client is not a browser, and a second way in before
something needs it is a second way in.

**`GateService` holds every decision and has no ASP.NET in it**, with `GateEndpoints` reading a header,
calling one method and writing the answer. That is the grains-over-state-machines pattern a third
time, and it is what makes the 31 tests here run without a web host and `IFleetDirectory` what makes
them run without a silo. If a rule ever appears in the endpoints file, it is in the wrong file.

### Slice three — the client half

**Nothing throws for a refusal, and that is a decision about which code gets written.** A gate saying
*"that name is taken"* or *"fetch the update"* is an ordinary answer; making it an exception makes the
happy path the only path anybody implements, and every one of those answers is a screen a real game
has to draw. Even a dropped connection is reported rather than thrown — on a phone, the network going
away is not exceptional.

**`Unreachable` is a separate answer from a refusal because the two want different pixels.** *"The
gate said no"* is a sentence to show the player; *"the gate did not answer"* is a spinner and a retry.
A client that showed the first for the second sends people to a support forum over dropped Wi-Fi.
There is a third: a non-2xx that is not a `GateProblem` at all is reported as `unexplained`, because a
gate always explains itself and anything else on these routes is an intermediary — a proxy, a load
balancer, a hotel captive portal — which is more use to know than whatever HTML it sent.

**`EnterAsync` waits out `Starting` and hands `UpdateRequired` straight back, and the asymmetry is the
whole design of the helper.** A shard coming up needs nothing from the game but patience, and the wait
is the gate's own `RetryAfter` rather than a number chosen here — how long a shard takes is a property
of the fleet. Fetching a catalog is not patience: it is the asset system doing work the game must
decide to do, on a connection the player may be paying for. A helper that quietly downloaded a
gigabyte would be a helper nobody could trust.

**The socket being down is a normal state, not an error state.** § The three planes says nothing a
player is waiting on travels here, so `GateConnection` reconnects with backoff forever and says
nothing about it — and `ListenAsync` therefore never completes on its own, because a loop that stopped
when the enumeration did would stop the first time a train went into a tunnel. ⚠ **Nothing is
replayed across a reconnect and nothing needs to be**: a push is a hint to go and ask, so a design
that queued missed events would be one where the queue's depth eventually matters. An unreadable frame
is skipped rather than fatal, so a newer gate saying something newer is not a client update.

**The second project in `Live/` to turn the AOT and trim analysers back on.** § Repository layout says
this one links neither Orleans nor ASP.NET hosting; making it trim-clean is what turns that from an
intention into something the build checks, and the reason is the same as `Vixen.Live.Abstractions`' —
a game client is an iOS NativeAOT binary. `IGateSocket` is a seam so a test needs no server, and so a
platform whose WebSocket is not `ClientWebSocket` — a console SDK, a browser build — can supply its
own without this assembly knowing.

**What L3 still owes.** `Vixen.Live.Matchmaking`, which § Cost lists under this milestone and which
[28](28-gameplay-framework.md) § Matchmaking owns most of: `IQueueGrain` was already left undeclared
at L1 for that reason, and a queue with no game to match players for is a promise rather than a
contract. The `Fleet` panel and `vixen live` are L4's.

## L2, as built

⚠ **Built after L3, and the reason is in L3's opening: nothing minted a ticket until a gate did.**

**The protocol is three state machines and the source owns the decision.** § The overlap's seven
timestamps become `SourceTransfer`'s phases, and the source realm is the only thing that can decide
nothing happened — because it is the one that still owns the player. A deadline on the target would
be a decision made by the realm that does not yet have the authority to make it.

**`StillOurs` is the property everything rests on**, and it is true in every phase but the last. A
realm that stopped simulating at t2 would give the player three minutes of standing still while their
map loaded, which is the failure the overlap exists to avoid.

**Aborting a *committed* transfer is refused rather than tolerated.** § The overlap says the lease
epoch is the boundary; the state machine makes it one by saying no. A source that "un-committed"
would claim a player two realms now believe in, which is the duplication this design has no other way
to express — and it is the only place in the protocol that refuses rather than shrugs.

**Two things the document implies and does not say.**

- *A reservation is capacity spent before anybody has connected.* Without it a map at 99 % could
  promise the same last slot to twenty players in flight and refuse nineteen at the door — each after
  loading the map. It therefore has to expire, and a reservation whose ticket has expired goes with
  it: the client can no longer be admitted, so the slot is being held for nobody.
- *Dormancy is what stops the player existing twice.* Between t3 and t5 they have a session on the
  target with no ownership, no input and no camera. A target that spawned them live at t3 would put
  two of them in the world for the length of the overlap.

**`ClientReady` is reported by the client, not by the target.** The target knows it admitted somebody;
only the client knows whether its own map finished loading and its first snapshot arrived. Moving a
player whose target is still a loading screen is precisely what the overlap exists to prevent.

**The ticket's own expiry is checked before the phase deadline**, because a client arriving with an
expired ticket is refused at the door — so waiting out the rest of the window is waiting for something
that cannot happen.

**The codec is in `Vixen.Live.Realm`, not in `Vixen.Live.Transfer`**, and the direction of that
reference is the decision: the codec is the half that needs a `World`, an `Entity` and a bit writer,
and keeping the protocol assembly free of all three is what lets a gate and an orchestrator reason
about a transfer without linking an ECS. Nothing is lost, because a handoff travels realm to realm
over the control plane and **the client never sees one**.

**A payload that does not read cleanly is refused rather than half-applied.** The source has not
committed — it is waiting for the acknowledgement that can no longer be sent — so refusing is a
transfer that did not happen. Applying half of one is the only way this design could produce a player
who is somewhere with the wrong body. An unknown type id is refused for a duller reason: the records
are not length-prefixed, so there is nowhere to skip *to*.

**Writing the codec's tests found the contract that matters.** `IComponentReplicator.Apply` must
*add* the component when it is absent, and a replicator that only `Set`s works for every snapshot —
where the client already spawned the entity from a prefab — and throws on the one path a handoff
takes, because the entity a player arrives on is bare.

**The oracle needed a join that did not exist, and `RealmTransfers` is it.** § Testing asks for three
realms in one process with players walking a loop between them, and the reason that could not be
written against L2 slice one is that nothing drove a `SourceTransfer` from a frame:
<c>SourceTransfer</c> knows the protocol and nothing about a tick, `RealmHost` has the tick and
nothing about the protocol. `RealmTransfers` is where the two meet, stepped once per update and
**before the heartbeat** — a sample taken either side of a commit would report a population no realm
ever had.

**The finished transfers are returned from `Step` rather than swept.** A committed one means despawn
the player and an aborted one means do not, which are the two things a realm cannot be allowed to get
wrong; handing them back makes it the caller's decision, once, at a defined point in the frame.

**What the oracle actually asserts is not "the transfers worked".** It is, after *every* step of
fifteen hundred across three seeds: every traveller is resident on exactly one realm, and the world's
total is zero. Double entry means zero is the only correct answer, so duplication and loss are both a
non-zero sum. There is a second run where transfers are started faster than they can finish and half
are killed mid-flight, because a fleet where transfers merely *usually* work is one that duplicates
items rarely — which is worse than one that fails loudly.

**`RealmHostOptions` gained `TransferDeadlines`,** which the test needed and a game wants anyway: the
overlap deadline is how long a client gets to download and load the target's map while still playing
here, so it is a content decision rather than a constant. A game whose maps are two gigabytes wants
longer than one whose maps are two hundred megabytes.

**What L2 still owes, and why it is not a small job.** § Testing specifies the end-to-end leg *"with
`NetworkSimulation`"* and asks for *bounded prediction resets*. The simulation is now installed on
every realm's transport in the fleet — and that is scaffolding rather than the leg, because **no
client connects to it**. The fleet drives `SourceTransfer` and `ClientTransfer` directly, which is
what makes the oracle exhaustive and fast, and it also means the only traffic on the wire is a session
with no peers: a loss profile changes nothing, and an assertion under one would pass whatever the
network did.

⚠ **A test that cannot fail is worse than a missing one, because it reports the leg as covered.** So
the loss assertions are deliberately absent rather than written green.

What the real leg needs is a `NetworkSession` per traveller, admitted through the handshake with its
ticket, and a second one opened to the target during the overlap. The prize is bigger than the loss
profile: at that point residency stops being the harness's bookkeeping and becomes
`RealmHost.Admission`'s own answer minus whoever the `TransferBoard` still holds as `Reserved` or
`Dormant` — so the oracle would assert against what a realm actually believes rather than against what
the harness remembered to update.

## L4, in progress

**§ Upgrades' one sentence — *"'additive' is proven by the build, not asserted by a human"* — is
`ContentDiff`, and the classifier is deliberately pessimistic.** Calling a non-additive change
additive means a live reload that corrupts a running world; calling an additive change non-additive
means a drain nobody needed. The first is unrecoverable and the second costs an evening, so anything
it cannot decide is not additive.

**A removal is never additive, even of something nothing is using.** Whether an address is in use is a
question about every entity in every world in the fleet, and this compares two files. § Upgrades lists
"a removed address" among the non-additive cases without saying why it cannot simply be checked; this
is why.

**`Blockers` exists because the document says *with the reason*.** That is the part that gets left
out, and a tool which says "this needs a drain" but not "because `items/greatsword` changed shape"
makes the operator diff two catalogs by hand at three in the morning.

**A rollout never kills anything.** Every step it produces is a `Drain`, and § Drain's readiness rules
do the rest. A rollout that could disconnect would be the one live-ops action able to undo the
promise that nothing is force-disconnected.

**Two numbers § Upgrades implies and does not name.** `DrainWidth`, because draining every old shard
at once asks every player in a region to transfer inside one window — a thundering herd against
new-version shards that have not finished starting, which presents as a rollout that *made the game
unplayable* rather than as a capacity mistake. And *emptiest first*, because a shard with four people
finishes its drain in a minute and gives its capacity back, where the busiest would hold a slot in the
width for an hour.

**A rollback has to restart the grace, and the tests found it.** § Upgrades says rolling back is
"making the *old* pair the target" and that "nothing about the mechanism is directional" — which is
true except for one thing. Without restarting the grace, a rollback inherits the elapsed grace of the
rollout it is undoing and puts the fleet straight into `Forcing` against the version everybody is
already on, turning a rollback into an outage.

**What L4 still owes**, and it is most of the milestone by wall-clock: the `Fleet` panel and the
transfer trace (§ Diagnostics, and doc 13's editor work), `placement explain` as a surfaced view
rather than as the `PlacementDecision.Explain()` that already produces it, the `vixen live` verbs, and
`Samples/14-Mmo` — which § Testing scopes as eight realms, three maps, five hundred connections and a
rolling upgrade mid-run, and which is the exit criterion for the whole document rather than a sample.

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
| M9 | **A fleet nobody has run at scale is fiction** | Medium | `Samples/14-Mmo` is an exit criterion, not a sample; the numbers in it are budgets |

| # | Open question | Recommendation |
|---|---|---|
| M-Q1 | Ship a relay/proxy transport in-box, or leave it to addons? | **Addon**, consistent with doc 16's Steam/EOS position — but `ITransport` and the endpoint-as-data property are the seam, and both are in L0 |
| M-Q2 | Does the gate host chat, or is chat its own service? | **The gate**, until it is not. One WSS connection is the client's whole service plane; splitting it is a scaling decision to make with numbers |
| M-Q3 | Postgres only, or an abstraction with two implementations from day one? | **One implementation behind an interface.** Two implementations from day one is two half-tested ones, which is ADR-012's argument in a different domain |
| M-Q4 | Move `Tools/Vixen.App` into `Core/`? | **Yes, separately.** Allow-list the `Live → Tools` edge now so this work is not blocked on that argument |
| M-Q5 | Region/latency zones — modelled by the engine or by the game? | **Engine**, as an opaque string with a hard placement filter. Every game has them and none of them mean the same thing |
