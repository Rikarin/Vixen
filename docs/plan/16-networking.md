# 16 — Networking and Multiplayer

In scope for 1.0 (Q7). Reference implementation: **[PurrNet](https://github.com/PurrNet/PurrNet)** (MIT,
614★, actively developed — last push 2026-07-23), studied from source. Per Q9, PurrNet is a *reference*,
not a compatibility target: its good ideas are re-derived, its Unity-specific and IL-weaving mechanics
are not.

`Vixen.Net` is an optional package. A single-player game that never references it pays nothing, and the
layer rules ([00](00-vision-and-principles.md)) forbid anything below `Vixen.Engine` from depending on it.

## What PurrNet gets right, and is worth taking

Read from `Assets/PurrNet/Runtime/`:

| PurrNet concept | Verdict |
|---|---|
| **Pluggable transports** (`UDPTransport`, `WebTransport`, `PurrTransport` relay, `LocalTransport`, `CompositeTransport`, plus a `FragmentationLayer` and `NetworkSimulation` layer) | **Take wholesale.** The `Local` (no socket) and `Composite` transports especially — `Local` makes single-player, host mode, and *unit tests* use the identical code path, which is the single best testability decision in their design. |
| **`NetworkRules`** — per-object policy for who may spawn, despawn, call RPCs, observe, and modify | **Take, and centre the design on it.** This is PurrNet's best idea. It replaces the usual "server-authoritative or bust" rigidity with a declarative policy, so a co-op game and a competitive shooter use the same engine with different rules rather than different code. |
| **Transparent spawn/despawn** — `Instantiate`/`Destroy` on a networked prefab replicates automatically | **Take the ergonomics.** Our deterministic content build ([08](08-asset-pipeline-and-addressables.md)) makes the prefab-ID problem easier than it is for them. |
| **`NetworkModule`** — composable, nestable units of networked state/logic; SyncVars are built from them | **Take.** Building the built-ins out of the same primitive users get is the right discipline and proves the primitive. |
| **Delta compression + `BitPacker`** (`DeltaModule`, `DeltaMessager`, and a `DeltaPackerAnalysis` tool) | **Take.** Bit-level packing with per-field quantization, and *tooling to analyse it*. |
| **`NetworkLOD`** — update rate degrades with distance/importance | **Take.** Cheap, and the difference between 20 and 200 players. |
| **`ColliderRollback`** — server-side rewind of collider history for hit validation | **Take.** Lag compensation is not optional for anything with aiming. |
| **Awaitable RPCs** returning `Task<T>` | **Take**, with correlation IDs and timeouts — and as `Task<T>` after all. `ValueTask<T>` earns its keep when a result is often already available; this one never is, since it is a network round trip, so the completion source is allocated either way and the wrapper buys nothing. What it would cost is real: a `ValueTask` may be consumed once, so asking three questions and awaiting them together becomes a hazard the compiler warns about. |
| **Reconnect identity** (`Cookies`) | **Take.** Session resumption is always wanted and always retrofitted painfully. |
| **Bandwidth profiler + telemetry** (`Profiler`, `Telemetry`, `ProfileBandwidth`) | **Take.** Folds into [13](13-diagnostics.md) rather than being a separate tool. |
| Coroutine RPCs (`IEnumerator`) | **Reject.** Vixen has no coroutines by decision ([04](04-ecs-and-scripting.md)); `async`/`await` on a frame-synchronous scheduler covers it with a real debugger and real exceptions. |
| **Mono.Cecil IL post-processing** (`Codegen/PostProcessor.cs`, `MonoCecilInstaller.cs`, `GenerateSerializersProcessor`, `GenerateRPCManifestProcessor`, …) | **Reject — banned by ADR-002.** This is the one structural thing we cannot copy, and it has a real API consequence. See below. |

## Client-side prediction, and a calibration this document got wrong

> **Corrected July 2026.** This section previously read "PurrNet does not have client-side
> prediction", based on reading `Assets/PurrNet/Runtime/` at the time. **That is no longer true, and
> the argument built on top of it has to stand on its own merits instead.** PurrNet now ships
> **PurrDiction**: client-side prediction with genuine rollback and resimulation — predicted
> identities, predicted modules, snapshot save/reconcile against verified server frames
> (`ReadState`, `Rollback(tick)`), automatic history participation so modules do not hand-manage
> buffers, and a view layer that interpolates presentation from the last verified state toward the
> latest predicted one. It is distributed as a separate Asset Store package rather than in the core.
>
> The original text is left described rather than deleted because *why* it was wrong matters: a
> competitor comparison is a fact with a shelf life, and this one was load-bearing for a scope
> decision. Anything in this document that reads "X does not have Y" should be treated as dated from
> the moment it is written.

Vixen's model for 1.0 is *server-authoritative + snapshot interpolation + lag compensation*. That is
the right architecture for the large majority of games — co-op, MOBA-lite, survival, social,
turn-based, most shooters at casual latency — and it is **not** rollback netcode in the Quantum/GGPO
sense.

**The case for deferring prediction, argued on its own.** It is not that nobody else has it; it is
that prediction is the single most expensive correctness surface in netcode, every predicted system
must be resimulable and therefore deterministic, and shipping it half-built is worse than not
shipping it — a game that predicts movement but not the interactions movement causes feels *less*
consistent than one that predicts nothing. The server-authoritative model is complete and correct at
every point on its own road; prediction is a second road that has to be finished before it is worth
starting.

Vixen is unusually well placed to add it later:

- The ECS is fixed-step and deterministic, with an input-log replay test already in
  [04](04-ecs-and-scripting.md).
- **World snapshots are cheap chunk copies** — a capability already required for play-in-editor
  ([11](11-editor.md)).
- Snapshot + input log + resimulate *is* rollback. The primitives arrive for other reasons.

So client-side prediction is **P2, explicitly designed for, not implemented** — the tick loop and
snapshot APIs are shaped to accept it without restructuring. Estimated +2 EM when wanted.

## The IL-weaving problem, and a better answer

PurrNet weaves IL so that this single method does two things depending on where it runs:

```csharp
[ServerRPC] void TakeDamage(int amount) { _health -= amount; }   // PurrNet: call it anywhere
```

The weaver rewrites the body's prologue into "if I am not the authority, serialize the arguments and
send; otherwise run the body". We cannot do that — ADR-002 bans IL post-processing, and it would break
NativeAOT on iOS regardless.

**Rejected workarounds:** `partial` method pairs (`TakeDamage` declaration + `TakeDamage_Impl`) — two
names for one concept; naming conventions (`Foo_Body`) — fragile and undiscoverable; C# interceptors
(`[InterceptsLocation]`) — would reproduce PurrNet's ergonomics exactly, but the feature is still
experimental with a churning API, so it is a **Phase-9 spike, not a foundation**.

**Adopted design — make the network hop visible at the call site:**

```csharp
public sealed partial class Player : Behavior
{
    private int _health;

    // The handler. Runs only where authority says it may.
    [ServerRpc(RequireOwnership = true, Channel = Channel.Reliable)]
    private void TakeDamage(int amount) => _health -= amount;

    [ClientRpc(Target = RpcTarget.Observers, Channel = Channel.Unreliable)]
    private void PlayHitEffect(Vector3 at, [Quantize(0f, 1f, bits: 8)] float intensity) { … }

    private void OnHit(int dmg)
    {
        Rpc.TakeDamage(dmg);        // ← generated sender. Obviously a network call.
        Rpc.PlayHitEffect(pos, 0.5f);
    }
}
```

The generator emits a nested `Rpc` accessor per type with one strongly-typed sender per handler, plus
the serializers, the dispatch table, and a stable RPC id (a hash of declaring type + method signature,
so adding a method does not renumber the others and a version mismatch is detected rather than
misrouted).

This is **one line more ceremony than PurrNet and materially better code**: reading
`Rpc.TakeDamage(dmg)` tells you a packet is being sent, where PurrNet's `TakeDamage(dmg)` does not.
Transparent RPC is a well-known readability and performance trap — it hides latency and bandwidth at
the call site. Making it explicit is a feature, and the AOT constraint pushed us somewhere better.

Everything PurrNet's weaver generates becomes an incremental source generator in
`Vixen.Net.Generators`: RPC senders and manifest, serializers, **delta** serializers, quantizers,
`IEquatable`/duplicate helpers, and the networked-type registry.

## Architecture

```
┌─ Application ──────────────────────────────────────────────────────────┐
│  Behaviors with [ServerRpc]/[ClientRpc] · [Replicated] components      │
│  SyncVar<T> / SyncList<T> · NetworkModule                              │
├─ Replication ──────────────────────────────────────────────────────────┤
│  Snapshot builder (per-connection baselines + acks) · delta encoder    │
│  Interest management (scene · distance grid · LOD · explicit)          │
│  Interpolation buffers · lag-compensation history                      │
├─ Session ──────────────────────────────────────────────────────────────┤
│  Topology: Server | Client | Host | Offline   NetworkRules             │
│  Players · ownership · authentication · reconnect tokens               │
│  TickManager: fixed tick, clock sync, RTT/jitter estimation            │
├─ Messaging ────────────────────────────────────────────────────────────┤
│  Channels (Reliable · Unreliable · ReliableUnordered · Sequenced)      │
│  Fragmentation · ordering · ack/nack · congestion · BitPacker          │
├─ Transport (ITransport) ───────────────────────────────────────────────┤
│  Udp · WebSocket · Local(in-proc) · Relay · Composite                  │
│  + NetworkSimulation decorator (latency/jitter/loss/duplication)       │
└────────────────────────────────────────────────────────────────────────┘
```

### Projects

```
Core/
├── Vixen.Net/                        # session, tick, channels, replication, rules, interest
├── Vixen.Net.Generators/             # RPC senders, serializers, delta, registry
├── Vixen.Net.Tests/
├── Vixen.Net.Transport.Udp/          # reliable+unreliable over UDP
├── Vixen.Net.Transport.WebSocket/    # incl. the browser path via Vixen.Platform.Web's ISocket
├── Vixen.Net.Transport.Local/        # in-process; powers host mode, offline, and every test
├── Vixen.Net.Transport.Relay/        # rendezvous + relay client (NAT traversal)
└── Vixen.Net.Transport.*.Tests/
```

Steam/EOS/platform transports are **addons**, not in-box — they carry SDK dependencies and licensing we
do not want in the core. The `ITransport` surface is the contract; PurrNet's addon layout
(`Addons/Steam`, `Addons/UTP`, `Addons/Nakama`, `Addons/Edgegap`) is the right precedent.

### Tick and time

The backbone. One fixed-tick clock shared with the ECS scheduler's `FixedUpdate` phase
([04](04-ecs-and-scripting.md)) — networking does not get its own loop.

- Monotonic `Tick` (uint, wrap-safe comparisons) stamped on every packet.
- Client estimates server tick from RTT + jitter with a smoothed offset and a small adaptive buffer;
  drift corrected by adjusting tick length by ±small percentages rather than by jumping.
- `Tick` is the vocabulary for everything: snapshots, input, lag-compensation history, interpolation
  targets. Wall-clock time never appears in a packet.

### Identity, spawning, ownership

- `NetworkId` = `readonly record struct(uint Value)` component on replicated entities; server allocates,
  clients never invent.
- **Prefab IDs come from the asset pipeline**: a networked prefab's id is its asset GUID's stable hash
  ([08](08-asset-pipeline-and-addressables.md)). No hand-maintained "network prefab list" to desync —
  this is a direct win from the deterministic content build, and it is where our design is simpler than
  PurrNet's.
- Scene-placed networked objects get ids baked at content-build time, identical across all peers because
  the build is deterministic (CI already gates this).
- Ownership: per-entity `Owner` (connection id or server), transferable, with `NetworkRules` deciding
  who may transfer. Ownership changes are events users can react to.
- **`NetworkRules` is a policy asset** (`.vxnetrules`) referenced per prefab or set globally: who may
  spawn/despawn, who may call each RPC kind, who may write which replicated fields, who observes what,
  what happens to owned objects on disconnect (destroy / transfer to server / persist).

### State replication — the ECS synergy

Two authoring styles, one mechanism underneath.

```csharp
// 1. ECS-native: the renderer-grade path
[Replicated(Channel = Channel.Unreliable, SendRate = 20)]
struct Position { [Quantize(-1000f, 1000f, bits: 16)] public Vector3 Value; }

// 2. Behavior-facing: the convenient path
private readonly SyncVar<int> _score = new(0);
private readonly SyncList<ItemId> _inventory = [];
```

**Dirty tracking is free.** The replication system queries `.WithChanged<T>(sinceTick)` using the ECS
**per-chunk change versions already specified** in [04](04-ecs-and-scripting.md). Replication needs no
dirty-flag bookkeeping of its own, and an entity that did not change costs nothing to consider. This is
the main structural reason to have built our own ECS (ADR-004) rather than adopting one without change
versions.

Snapshot pipeline per connection, per tick:

```
for each connection:
  observers   = InterestManager.Resolve(connection)          # scene · grid · LOD · explicit
  baseline    = last tick this connection acknowledged
  changed     = ECS query over observers .WithChanged(baseline)
  payload     = DeltaEncode(changed, baseline) → BitPacker    # quantized, bit-packed
  send(payload, Channel.Unreliable, tick)                      # acks drive baseline advance
```

- Per-connection baselines with ack-driven advance; on loss the next delta is computed against the older
  baseline rather than resent verbatim. Ring of N recent snapshots bounds memory.
- Reliable-eventual semantics for `SyncVar`-style state, unreliable-delta for transforms.
- Bandwidth budget per connection with priority-based shedding — low-priority objects skip ticks before
  high-priority ones do.

### Interest management

Composable resolvers, in evaluation order: scene scope → explicit visibility overrides → distance grid
→ LOD rate reduction. Users can add resolvers (team-based, portal/room-based, fog-of-war). Default for a
new project is "everything in the loaded scenes", so a prototype works before anyone thinks about it,
which is a deliberate ergonomics choice.

### Motion: interpolation, extrapolation, smoothing

- Snapshot buffer per replicated entity, interpolated at `serverTick - interpolationDelay`.
- Extrapolation on starvation, clamped, with a visible-snap threshold rather than unbounded drift.
- `NetworkTransform` with per-axis enable, quantization, rotation compression (smallest-three),
  teleport detection, and parent-relative replication.
- Owner-side smoothing so the local player never sees their own input interpolated.

### Lag compensation

Server keeps a ring of transform/collider history keyed by tick. A hit claim from a client at tick T is
validated against the world as that client plausibly saw it (`T` clamped to its measured RTT window).
Jolt's shape-cast APIs make the rewound query straightforward; the history ring is the work.

## Security posture

Stated explicitly, because "supported multiplayer" without this is a liability:

- **Server-authoritative by default.** `NetworkRules` can relax it, and doing so is an explicit,
  reviewable decision in an asset rather than an accident.
- Every inbound packet is validated: size caps, rate limits per connection per message type, RPC id
  must exist, argument bounds, ownership and rules checked **before** dispatch, string/collection length
  caps.
- Never deserialize into an arbitrary type from the wire — the generated registry is a closed set, and
  polymorphic payloads resolve only within declared allow-lists. (This is the classic remote-code-
  execution vector in game netcode and it is excluded by construction.)
- Connection handshake carries a protocol version + content hash; mismatches are rejected with a clear
  reason rather than producing corrupt state.
- Reconnect tokens are opaque, expiring, and server-issued.
- DTLS/WSS for transports that support it; the plan does **not** claim a bespoke crypto layer.

## Diagnostics

Folds into [13](13-diagnostics.md) rather than being a parallel system:

- Bandwidth attribution per object, per component type, per RPC — the "what is eating my 30 KB/s"
  question, answered. PurrNet ships equivalents (`ProfileBandwidth`, `DeltaPackerAnalysis`) and they are
  clearly load-bearing in practice.
- Packet inspector with tick timeline; RTT/jitter/loss graphs; per-connection state.
- **`NetworkSimulation` transport decorator** — inject latency, jitter, loss, duplication, reordering.
  On by default in dev builds with a modest profile, because netcode developed on localhost is netcode
  that breaks on release.
- Editor panel: connections, replicated objects, ownership, interest sets, live RPC log.

## Testing

The `Local` transport is what makes most of this ordinary unit testing rather than integration pain.

| Area | Test |
|---|---|
| Transport | Fragmentation/reassembly property tests; ack/nack under randomised loss/reorder; congestion behaviour; `NetworkSimulation` reproducibility with a fixed seed |
| Serializers/delta | Round-trip property tests over generated types; delta-then-apply equals full state; quantization error within declared bounds; **bit-exact across platforms** (CI gate, same as content determinism) |
| RPC generator | Snapshot tests on generated senders/manifest; id stability when methods are added/reordered; version-mismatch detection; rules enforcement (a client calling a server-only RPC is rejected, not executed) |
| Replication | N-client in-process sessions over `Local`; after M ticks every client's replicated state equals the server's; loss injection at 5/20/50 % still converges |
| Interest | Oracle test: resolver output vs. brute-force distance computation; objects entering/leaving interest spawn/despawn exactly once |
| Ownership | Randomised transfer/disconnect sequences leave no orphaned or double-owned entities |
| Lag compensation | Replay a recorded scenario: hit claims validate identically regardless of injected latency within the window |
| Security | A fuzzing corpus over the packet reader (`SharpFuzz`, alongside the parsers in [12](12-build-ci-and-testing.md)); malformed/hostile packets must be rejected without exception escape or allocation spike |
| Determinism | Server + client input logs replayed twice produce identical state — the prerequisite for adding prediction later |
| Scale | Synthetic 100-connection soak with 5 000 replicated entities: bandwidth, CPU, and allocation budgets held for 30 minutes |
