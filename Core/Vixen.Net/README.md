# Vixen.Net

Networking. Optional: a game that never references it pays nothing, and nothing below `Vixen.Engine`
is allowed to reference it.

Spec: [docs/plan/16-networking.md](../../docs/plan/16-networking.md).

## What is here so far

Everything from the wire up to the policy. Lag compensation is the one item of the phase that is not
built, and it is blocked on Phase 8 rather than owed by this package; see the roadmap.

```
Vixen.Net              Channel · ConnectionId · DisconnectReason · Tick
Vixen.Net.Transport    ITransport · ITransportEvents · NetworkSimulation
Vixen.Net.Messaging    PacketWriter · PacketReader · BitWriter · BitReader · QuantizeRange · MathCodec
Vixen.Net.Time         TickRate · TickManager · RoundTripEstimator
Vixen.Net.Sessions     NetworkSession · NetworkPlayer · PlayerId · ISessionAuthenticator
Vixen.Net.Replication  NetworkId · [Replicated] · [Quantize] · ReplicationServer/Client
Vixen.Net.Rpc          [ServerRpc] · [ClientRpc] · RpcRouter · NetworkOwnership · RpcManifest
Vixen.Net.Rules        NetworkRules · NetworkRulesRegistry
Vixen.Net.Motion       NetworkTransform · SnapshotBuffer · OwnerSmoothing
Vixen.Net.Diagnostics  BandwidthLedger · SnapshotInspector · NetworkMetrics
```

Plus the transports — `Local` (in-process), `Udp`, `WebSocket`, and `Composite` (several at once,
so one server takes both desktop and browser clients) — the build half (`Vixen.Net.Generators`), the
export half of the metrics (`Vixen.Net.Telemetry`), and the fuzz harness (`Vixen.Net.Fuzz`), each in
their own package with their own README.

**[`Samples/08-Multiplayer`](../../Samples/08-Multiplayer) is all of it at once** — eight players,
server-authoritative movement and shooting, over either transport, ending in a convergence check that
exits non-zero when a client disagrees with the server. It is the shortest route to seeing how these
pieces are meant to be wired together.

## Three rules the transport contract is built on

**One object holds both halves.** A transport has a server half and a client half; either, both or
neither may be running. A listen server — one process that hosts and plays — is the same transport
with both halves started and a loopback between them, so host mode is not a second code path through
every layer above it.

**Nothing is delivered outside `Poll`.** No callback arrives on a socket thread and no handler runs
between two systems in a frame. That is what lets replication be ordinary code at a known point in
the schedule rather than code that has to be thread-safe against a network thread.

**Time is a parameter, not a reading.** `Poll(elapsed, events)` is *told* how much time has passed.
Every time-dependent behaviour — retries, timeouts, the latency `NetworkSimulation` injects — is then
a pure function of the calls made to the transport, so a test that wants to observe a 200 ms round
trip does it in a loop instead of in 200 ms, and observes the same thing every run.

```csharp
var transport = new LocalTransport(network);
transport.StartServer();

// once a frame
transport.Poll(frameTime.Delta, this);   // `this` implements ITransportEvents
transport.SendToClient(connection, payload, Channel.Unreliable);
```

## Channels

Four, because "reliable or not" is the wrong question twice over. Everything that derives behaviour
from a channel — the reliability layer, the delta encoder, the simulation — asks
`IsReliable`/`IsOrdered`/`MayDrop`/`MayDuplicate` rather than writing a `switch` of its own, so a
fifth channel would be one enum member and one line.

| Channel | Arrives | In order | Notes |
|---|---|---|---|
| `Reliable` | yes | yes | The expensive one: a loss stalls everything behind it. |
| `ReliableUnordered` | yes | no | Independent events — chat, pickups, most RPCs. |
| `Unreliable` | no | no | State that supersedes itself: transforms, animation. |
| `Sequenced` | no | yes | Unreliable, but an old one is discarded rather than applied. |

## NetworkSimulation

Netcode developed on localhost is netcode that has never been tested. This wraps any transport and
injects latency, jitter, loss and duplication — with reordering falling out of jitter, because that
is how it actually happens.

```csharp
var transport = new NetworkSimulation(new LocalTransport(network), NetworkSimulationProfile.Mobile, seed: 1);
```

Two things make it worth trusting:

- **It respects the channel.** A `Reliable` payload is delayed, never lost, never duplicated and
  never overtaken. A `Sequenced` one may be lost but not reordered. The simulation only does what the
  real world is allowed to do to that channel, so the layer above is exercised against its contract
  rather than against a violation of it.
- **It replays.** The seed is a required constructor argument, the delays are spent against the
  virtual clock `Poll` advances, and the random draws happen in a fixed order per send. The same
  seed, profile and sequence of calls produce the same deliveries on every machine — which is what
  makes "the bug that only happens at 20 % loss" a test rather than an anecdote.

Profiles: `Perfect`, `Lan`, `Broadband`, `Mobile`, `Awful`. `Latency` is one way, so wrapping both
ends gives a round trip of twice it.

## The tick

One fixed-tick clock, shared with the ECS `FixedUpdate` phase — networking does not get a loop of its
own. Every packet is stamped with a `Tick`; wall-clock time never appears in one.

`Tick` wraps, and its comparisons know it: order is signed distance, `(int)(a - b)`, which is right
across the wrap. There is deliberately no `<` operator and no `IComparable`, because modular
comparison is not a total order — with three ticks spread far enough apart, A is after B, B is after
C, and C is after A — and a type that looked sortable would eventually be sorted.

`TickManager` turns frame deltas into whole ticks, and on a client keeps them lined up with the
server's. It **drifts rather than jumps**: an error of a few ticks scales the tick *length* by up to
10 % until it is worked off, so nothing keyed by tick — input, interpolation, the history the server
rewinds — moves under anyone. Past a second of error it snaps instead and says so through
`SnapCount`, because a 10 % correction would take minutes. The client aims ahead of the server by
half a round trip plus a jitter margin so its input lands in time; `InterpolationTick` is the
opposite, and is what the motion layer should draw.

## The packet codec

`PacketWriter` and `PacketReader` are `ref struct`s over caller-owned spans. Little-endian on every
platform, so the bit-exactness gate in CI has something to assert.

The reader **never throws**. Every read returns a `bool`, and the first failure is sticky, so a
decoder reads a whole message as straight-line code and checks once at the end. That is a security
property rather than a convenience: inbound bytes come from a machine we do not control, an exception
escaping a decoder is a denial of service, and a `try`/`catch` around the receive path is how a
parser bug becomes exploitable. It never allocates on a length it was told, never indexes outside its
span, and never believes a length field — every blob and string read takes a cap from the caller.

The writer's mirror of that: running out of room sets `Overflowed` and `TryFinish` refuses to hand
over a truncated packet. A bandwidth spike is ordinary in a frame loop, and the right answer is to
shed the packet rather than unwind the stack.

## The session

`NetworkSession` sits between the transport, which knows about connections and bytes, and everything
above, which wants players and ticks. It owns exactly three things — the handshake, the clock and the
player list — and hands every payload it does not understand to an `ISessionMessageHandler`.

```csharp
var session = new NetworkSession(new LocalTransport(network), new SessionOptions {
    ProtocolVersion = 3,
    ContentHash = catalog.Hash,
    MaxPlayers = 8,
});

session.StartHost();                       // or StartServer / StartClient / StartOffline
var ticks = session.Update(frame.Elapsed, this);   // once a frame; returns fixed ticks owed
```

Four decisions worth knowing:

- **Nothing is dispatched before the handshake finishes.** A payload from a connection that has not
  been accepted is dropped — not queued, not delivered late. Everything above may assume its peer
  agreed on the protocol version and the content hash and was let in by the authenticator.
- **A player is not a connection.** `PlayerId` survives a drop; `ConnectionId` does not. A dropped
  player stays in the list with `IsConnected` false for the reconnect window, holding their id and
  their slot, and a client that comes back with its issued token resumes as the same player. A new
  token is issued on every connect, so a leaked one is worth at most one reconnection.
- **Authentication is asked, not awaited.** `ISessionAuthenticator` may answer `Pending` and be asked
  again next update, with a timeout. The obvious design is `Task<bool>`; the reason it is not is the
  frame loop — a completion on a thread-pool thread would make every layer it touches thread-safe for
  the sake of an event that happens twice a minute.
- **Host mode is not a special case.** `StartHost` starts both halves of one transport, and the
  host's own client half does the same handshake through the loopback that a remote client does over
  a socket. `StartOffline` is mechanically identical and differs only in what the game means by it:
  single player is a one-player multiplayer game, and there is no offline path to rot.

## Replication

The server turns its world into a snapshot per connection, once a tick:

```csharp
replication.Capture(world, session.Tick);         // read and encode what changed — once
foreach (var player in session.Players) {
    if (replication.TryWriteSnapshot(world, player.Id, session.Tick, buffer, out var snapshot)) {
        session.SendToPlayer(player.Id, snapshot, Channel.Unreliable);
    }
}
```

Five things carry it.

**Capture once, copy many.** Reading a component, quantizing it and packing it happens once a tick;
a connection's snapshot is a copy of those bits for the values it does not already have. Fifty
players cost fifty memcpys and one encode.

**Two filters, cheap then exact.** The ECS's per-chunk change versions say which chunks are worth
looking at — that is the structural reason for having built an ECS with them — and a hash of the
encoded value says which entities in those chunks actually differ from what a connection has
*acknowledged*.

**Acknowledged, not sent.** A value that was sent may be in a packet that never arrived, so nothing
enters a connection's baseline until an ack for its tick comes back. The consequence is the one that
is easy to get wrong: on loss the next snapshot is computed against the older baseline, so the client
gets the *current* value rather than a retransmission of a value that is stale by now.

**A record is a difference where it can be.** Every value's last few encodings are kept, and a
record says which of them it was measured from; the receiver applies it to that one rather than to
whatever it happens to be holding. That last part is the whole of the correctness argument — a client
may have applied values it has not managed to acknowledge, so "the value you have" and "the value I
last heard you had" are different things, and only one of them is safe to difference against. A
component opts in by declaring its wire layout, which the generator emits for it:

```csharp
public ReadOnlySpan<WireLane> Lanes => Layout;   // 16 bits, 16 bits, a flag, …
```

Everything else follows from bits: `DeltaCodec` writes one bit per field saying whether it changed and
a two-bit selector choosing how big the difference is, so a position that moved a few centimetres
costs six bits where it cost sixteen. Declaring no layout means every record goes whole, which is
correct and is what a variable-length encoding must do.

**The budget sheds, it does not truncate.** Records go out in priority order and the writer is
rewound if one would take the snapshot over budget, so a snapshot is always a whole number of
complete records. What was shed was never acknowledged, so it goes in the next one — a shed and a
loss take the same path out.

⚠ **The world's version must not advance between a write and the capture that should see it.**
Advance–write–capture, or write–capture–advance. Advancing in between puts the write on the far side
of the comparison and the client never learns about it, with nothing reporting an error.

Components declare themselves, and `Vixen.Net.Generators` writes the code:

```csharp
[Replicated(Channel = Channel.Unreliable, Priority = 10)]
struct Position { [Quantize(-1000f, 1000f, 16)] public float X, Y, Z; }
```

## Remote calls

The handler keeps its name; the sender gets its own, reached through a generated `Rpc` accessor:

```csharp
public sealed partial class Player : IRpcObject {
    [ServerRpc(RequireOwnership = true, Channel = Channel.Reliable)]
    void TakeDamage(int amount) => _health -= amount;

    [ClientRpc(Target = RpcTarget.Observers)]
    void PlayHitEffect(float at, [Quantize(0f, 1f, 8)] float intensity) { … }

    void OnHit(int damage) => Rpc.TakeDamage(damage);   // ← obviously a packet
}
```

The reference implementation rewrites IL so that one name means both "send this" and "run this".
ADR-002 bans that and NativeAOT would not survive it — and the constraint pushed the design somewhere
better, because transparent RPC hides latency and bandwidth at the call site. One line more ceremony,
and the call site says what it costs.

**Nothing a packet says about who sent it is believed.** The sender is what the session says it is —
the connection the bytes arrived on — and it is what ownership and the rate limit are checked against.
A handler that wants to know who called it takes an `in RpcContext` first parameter, which the router
fills in; a handler that took the caller's id as an ordinary argument would be asking the caller who
they are.

Six checks before anything runs, each of which is a counter for the diagnostics panel: the indices
must name a call in the manifest, the direction must be one this peer accepts, the object must be
registered, ownership must hold if the call asks for it, the connection must be inside its rate limit,
and the arguments must decode and leave nothing behind.

Ids are hashes of the declaring type and the signature, so adding a method does not renumber the
others; the wire carries the position in a manifest ordered by those hashes, and `ManifestHash` is the
one number two peers compare in the handshake.

## Motion

A client draws the world **behind** the server, at `TickManager.InterpolationTick`, far enough back
that the snapshots bracketing the moment being drawn have already arrived. `SnapshotBuffer` is that
delay, and the delay is what buys the interpolation something to interpolate between.

```csharp
buffer.Add(new TransformSample(tick, position, rotation));
if (buffer.TrySample(clock.InterpolationTick, clock.Alpha, out var at)) { … }
```

Four behaviours, each with a counter:

- **Interpolate** between the two samples bracketing the target — the ordinary case.
- **Extrapolate** past the newest, from the velocity of the last two, **clamped**: a player who
  stopped a second ago should not still be crossing the map on everybody else's screen.
- **Snap** when two consecutive samples are further apart than a walk — a respawn is not a very fast
  run through everything in between.
- **Hold** when there is nothing to work with, rather than guessing.

Rotation is held rather than extrapolated. A position that overshoots comes back with the next
snapshot and reads as momentum; a rotation that overshoots reads as a stumble.

**Owner-side smoothing** is the other half, and a different problem: the owner *simulates* their
object rather than interpolating it, which is why a local player feels responsive. When the server
corrects them, the simulation takes it immediately — so the next physics step and everything the
server will judge run from the right place — and `OwnerSmoothing` hands the camera the error as an
offset that decays over a few frames. What the player sees glides; what the game computes is already
right.

`NetworkTransform` is the component that travels: a quantized position, a rotation packed
smallest-three, and a teleport counter. 88 bits, against the 224 the two values occupy in memory.

**Smallest-three** is worth stating because it is exact rather than approximate: a unit quaternion's
largest component can always be recovered from the other three, and those three are in ±1/√2 *because
they have to be*. Two bits say which one was dropped, and the sender flips the whole quaternion so
the dropped one is positive — `q` and `-q` being the same rotation is what removes the sign bit.

## Rules

Who may do what is a declaration rather than a `switch`:

```csharp
router.Rules.Default = NetworkRules.ServerAuthoritative;
router.Rules.Set(vehicle, NetworkRules.OwnerAuthoritative with {
    OnOwnerDisconnect = DisconnectBehaviour.TransferToServer,
});
```

A co-operative game and a competitive shooter want different answers to every question here, and
without this they get them by being different engines. With it they are the same engine with
different rules, and relaxing server authority is a reviewable decision somebody wrote down.

**Rules never grant a client more than the code asked for.** Where a rule and an attribute both have
an opinion, the stricter wins: an `[ServerRpc(RequireOwnership = true)]` stays an owner's call however
permissive the object's rules are. That is why `CallServerRpc` defaults to `Everyone` — it means "the
rules add nothing", not "anybody may call anything". Safety out of the box comes from the attribute,
which requires ownership unless a method says otherwise; the rule is the knob that *tightens* it.

| Rule | Enforced by |
|---|---|
| `CallServerRpc` | `RpcRouter.Receive`, before dispatch |
| `ChangeOwner` | `RpcRouter.TryTransferOwnership` |
| `OnOwnerDisconnect` | `NetworkRulesRegistry.OnOwnerLeft` |
| `Spawn`, `Despawn`, `Write` | declared and answered; **no enforcement point yet** — nothing can spawn or write from a client |

The authoring shape is a `.vxnetrules` asset referenced per prefab. That is the asset pipeline's half
and is not built; `NetworkRulesRegistry` is what it will be loaded into, and it already answers the
questions that asset will answer.

## What goes over a session

The session carries opaque bytes. Three things want to put bytes there — replication, remote calls,
and the game's own messages — so one `PayloadKind` byte goes in front, and each keeps its own decoder:

```csharp
public void OnMessage(PlayerId from, Channel channel, ReadOnlySpan<byte> payload) {
    if (!NetworkPayload.TryUnwrap(payload, out var kind, out var inner)) return;
    if (kind == PayloadKind.Rpc) router.Receive(from, inner);
    …
}
```

`SessionRpcTransport` is the sending half of that. It is a class of its own rather than the session
implementing `IRpcTransport` directly, because wiring the two together without the marker would be a
connection that looked right and mixed three streams into one.

## Diagnostics

"Thirty kilobits a second" is not an actionable number. `BandwidthLedger` answers the question that
is — **what is eating it** — in four ways: which component type, which *field* of it, which remote
call, and which connection. Attach one and it is a dictionary increment per record; leave it off and
it is a null check.

```csharp
replication.Ledger = ledger;
router.Ledger = ledger;      // one ledger, so one report covers state and calls together
```

The per-field breakdown is the one worth having, and it falls out of delta encoding rather than
costing anything: the encoder is already walking the lanes, so each field's cost is a subtraction.
It is what tells you a component is carrying a field your game never changes — in `Samples/08`, the
Y axis of every position and one component of every rotation cost exactly their one "unchanged" bit,
which is the report saying the arena is flat.

`SnapshotInspector` is the other half: it takes a snapshot apart into its records — which object,
which component, whole or a difference, which baseline, how many bits — and **applies none of it**.
That is what makes it usable on a recorded capture, on a snapshot the client rejected, and on a live
connection's traffic. A packet inspector is this call plus somewhere to put the answer.

`NetworkMetrics` is the third: the same numbers, published as a `System.Diagnostics.Metrics` meter
called `Vixen.Net`, for the process nobody is sitting in front of. **That is the OpenTelemetry metrics
API rather than an alternative to it** — the BCL types are the specification's API surface and the SDK
is only needed to export — so this file depends on nothing, and a server that already has a pipeline
reads it by naming the meter. [`Vixen.Net.Telemetry`](../Vixen.Net.Telemetry) is the export half, kept
separate so a game that never runs a dedicated server does not link an exporter.

The one call it needs is `Sample()`, once a tick, from the loop that owns the session. Observable
instruments are called back on the collector's thread, and everything worth reporting lives in
single-threaded frame code — so the game pushes a reading and the callbacks read that, rather than a
background thread walking a player list while somebody is joining it.

**Owed:** the editor panel — connections, replicated objects, ownership, interest sets, a live RPC
log — which is [13](../../docs/plan/13-diagnostics.md)'s to host and has nothing to hang off yet.
Everything it would show is in these types.

## Testing

The contract's own tests are in `Vixen.Net.Tests`, and the interesting one is
`TransportConformance` — an abstract suite asserting everything `ITransport` promises. It is run
there against `Vixen.Net.Transport.Local` and against the simulation wrapped around it; every other
transport's test project inherits the same suite. A transport is substitutable or it is nothing, and
the way to keep that true is to make the contract executable.

The other executable claim is [`Vixen.Net.Fuzz`](../Vixen.Net.Fuzz): nine targets over every decode
path a peer can reach, nine million cases on every build, holding each of them to three promises —
nothing throws, nothing amplifies, nothing is retained. It found four defects on its first run,
including one packet that crashed a client and one that made it keep a player record per packet.
