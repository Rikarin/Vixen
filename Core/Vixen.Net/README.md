# Vixen.Net

Networking. Optional: a game that never references it pays nothing, and nothing below `Vixen.Engine`
is allowed to reference it.

Spec: [docs/plan/16-networking.md](../../docs/plan/16-networking.md).

## What is here so far

Everything from the wire up to the policy, plus interest management and client-side prediction.

```
Vixen.Net              Channel · ConnectionId · DisconnectReason · Tick
Vixen.Net.Transport    ITransport · ITransportEvents · NetworkSimulation
Vixen.Net.Messaging    PacketWriter · PacketReader · BitWriter · BitReader · QuantizeRange · MathCodec
                       BroadcastRouter
Vixen.Net.Time         TickRate · TickManager · RoundTripEstimator
Vixen.Net.Sessions     NetworkSession · NetworkPlayer · PlayerId · ISessionAuthenticator
Vixen.Net.Replication  NetworkId · [Replicated] · [Quantize] · ReplicationServer/Client
                       NetworkSpawn · InterestChain · InterestGrid · IReplicationRate
Vixen.Net.Rpc          [ServerRpc] · [ClientRpc] · RpcRouter · NetworkOwnership · RpcManifest
Vixen.Net.Rules        NetworkRules · NetworkRulesRegistry
Vixen.Net.Motion       NetworkTransform · SnapshotBuffer · OwnerSmoothing
Vixen.Net.Prediction   IPredictedInput · InputLog · InputBuffer · ClientPrediction
                       PredictionHistory · TickLeadController · PredictionSmoother
Vixen.Net.Diagnostics  BandwidthLedger · SnapshotInspector · NetworkMetrics
```

Plus the transports — `Local` (in-process), `Udp`, `WebSocket`, and `Composite` (several at once,
so one server takes both desktop and browser clients) — the build half (`Vixen.Net.Generators`), the
export half of the metrics (`Vixen.Net.Telemetry`), lag compensation (`Vixen.Net.Physics`), the
plug-and-play components (`Vixen.Net.Engine`, `Vixen.Net.Animation`, `Vixen.Net.Audio`), and the fuzz
harness (`Vixen.Fuzz`), each in their own package with their own README and its own **Owed**.

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

**Spawning is one of these components, not a message beside them.** `NetworkSpawn` — a prefab id, a
scene id and an owner — sits at the top of the priority list, so it reaches exactly the connections
the interest resolver returns, is re-sent until acknowledged and then never again, arrives for a
player who joins an hour in, and precedes every state record about the same entity. A spawn on its own
route would have needed a second answer to interest, to loss and to late joiners, and three mechanisms
that can disagree about who may see what is how objects end up on one screen and not another.
Despawning needed nothing new at all: leaving the interest set already means "drop it", so
destruction and walking over the horizon are the same mechanism. The half that has to see a `Prefab`
lives in `Vixen.Net.Engine`.

## Interest

Who is told about what. `InterestChain` is a **source** of candidates and a list of **rules** asked in
order, where the first definite answer wins — which is what doc 16's "scene scope → explicit overrides
→ distance grid" ordering has to mean for an override to be one.

**Most rules say `Undecided` most of the time, and that is what makes a chain work.** A scene rule
knows an object in a level you have not loaded is hidden; it knows nothing about whether one in a
level you *have* loaded is close enough to matter. Saying so — rather than voting "observed" and
forcing every later rule to be able to overrule it — is what lets rules be written independently.

**The grid is a source, not a rule, and that is where the scaling is.** A rule filters what it is
given, so a chain of rules over ten thousand objects and two hundred players is two million questions
a tick whatever the rules then say. `InterestGrid` buckets the world once and answers each player from
the cells around them. It reads exactly like a filter — "is this within range" — and writing it as one
produces something that passes every test and scales like the thing it replaced.

**It leaves with hysteresis, which is not polish.** Leaving the observed set and being destroyed are
the same thing to a client, so an object at the boundary is not "flickering" — it is being destroyed
and recreated, every tick, with whatever the game hangs off a spawn.

The fallback is `Observed`, so a chain with no rules is what a new project already had, and adding a
rule can only ever *hide* things — the direction in which mistakes get noticed rather than debugged.

## Rate, which doc 16 puts in the chain and cannot go there

That document lists the resolvers as "scene scope → explicit visibility overrides → distance grid →
**LOD rate reduction**". The last is not a filter. Leaving the observed set means "drop this object",
so an LOD written as a rule would despawn and respawn every distant object on every tick it skipped —
a bug that looks like the feature working.

So rate lives on `ReplicationServer.Rate`, where skipping a record already means "not this tick": it
is the same thing the bandwidth budget does when it sheds, and it takes the same path out — nothing
was acknowledged, so it goes in the next snapshot. `DistanceReplicationRate` is the banded
implementation, phased **by object id** so distant objects spread across the ticks instead of arriving
together on every fourth one. An object the connection does not hold yet is never rate-limited, so a
reduced rate slows updates without delaying anything's appearance.

## Predicted input

The half of client-side prediction that has to exist first: a client's inputs reaching the server
*before* the tick they are for. `IPredictedInput<T>` is a game-defined struct with a `static abstract`
codec — the same shape `IBroadcast<T>` uses, and for the same reason: both ends get the same encoding
at compile time and nothing reflects at run time.

**Every packet carries the last several ticks.** A lost input is not a lost update that the next
packet supersedes — it is a tick the server simulates differently from the client that predicted it,
and nothing afterwards repairs the divergence. So `InputLog<T>` sends a short run rather than one
input, which costs a few bytes and removes the failure entirely for any loss shorter than the
redundancy. There is a test that drops three consecutive packets and asserts the server lost nothing,
and one that sets the redundancy to two and asserts that it *does* lose something — because a
constant is only meaningful if exceeding it does what the number says.

**The log is trimmed by acknowledgement, not by age**, because it is two things at once: what goes on
the wire, and what a rollback replays. Trimming by age would throw away exactly the inputs a slow
acknowledgement still needs.

`InputBuffer<T>` is the server's jitter buffer, and its counters are a control signal rather than
diagnostics. `Depth` against `TargetDepth` is what the server reports back so a client can adjust how
far ahead it runs — starving means "run further ahead", growing means "you are paying input latency
you do not need to". A starved tick **repeats the last input rather than zeroing it**: a player
holding forward would otherwise stop dead for one tick on the server while their own client predicted
them still moving, which turns a dropped packet into a guaranteed correction.

## Prediction

Three lines a tick, and all the subtlety is in what they mean:

```csharp
prediction.Step(world, tick, input);          // record the input, simulate, record the result
// … a snapshot for tick T arrives and ReplicationClient applies it …
prediction.Reconcile(world, confirmed: T);    // agree and carry on, or replay from the server's state
```

**Predicted state is exactly replicated state**, and that is a definition rather than a limitation. A
field the server never sends is a field no snapshot can contradict, so there is nothing to reconcile
it against. `PredictionHistory` records through the same `IComponentReplicator` the server writes
with, which means a frame of history and a snapshot are the same bytes describing the same thing, and
comparing them is a span comparison rather than a per-component equality nobody wrote.

**Comparing in the encoded domain gets the tolerance right for free.** A prediction that differs from
the server in the last bit of a float is a difference below what the wire can express — the server's
value arrived quantized — so the two encode identically and no rollback happens. Comparing floats
instead would roll back on very nearly every snapshot, and the cost would look like the feature
working. The flip side: a restore comes back through the codec, so it snaps the world onto the wire's
lattice. Bounded by one quantization step, non-accumulating, and the same lattice the server is on.

**Agreement is the common case and it is the cheap one** — a byte comparison and a copy, no
simulation. `ResimulatedTickCount` is the price of the feature and should sit near zero on a
connection that is behaving. `MispredictionCount` is the number that says whether the simulation is
actually deterministic: a predicted step that reads anything outside the world and the input
mispredicts on *every* snapshot even with no packet loss at all, and it looks like jitter rather than
like a bug.

**Disagreement replays from the server's state**, not from the guess. That is what makes the
correction converge — nudging the present toward the server's value is the tempting alternative, and
it does not, because the error it corrects was produced by ticks it is not redoing.

**What is predicted comes from the rules**, not from a second notion of ownership. `PredictedOwnershipSystem`
tags what `NetworkRules.Write` says this client may decide — the same question the rigid bodies and the
animators ask — and untags it when somebody else takes it. Two notions of "mine" is how the two come to
disagree, and the day they do, a client predicts something the server overrules on every tick. With no
rules, nothing is predicted: predicting by default would mean a game that never configured this
predicting the whole map against a server that overrules all of it.

**How far ahead to run is the server's answer, not the client's.** A client can measure a round trip,
and a round trip is a good estimate of the wrong thing — what matters is whether its input reached the
server *before* the tick it was for, which is a fact about the server's buffer. `PredictionHealthReporter`
sends that back as a broadcast (deltas, not lifetime totals, and every thirtieth tick rather than every
tick), and `TickLeadController` turns it into `TickManager.LeadBias`. It moves **one tick at a time and
never on one report**, because changing the lead moves every input not yet sent — and it is asymmetric
on purpose: starvation is corrected quickly and depth given up slowly, because being too far ahead
costs a little input latency and being too far behind costs corrections the player sees.

**Hiding the correction is a presentation problem**, and `PredictionSmoother` is the wiring for it:
`ClientPrediction.Corrections` reports what the last reconciliation moved, and the smoother keeps an
`OwnerSmoothing` per object so a player and the vehicle they are driving can be corrected by different
amounts. The simulation takes the correction at once and the picture catches up — blending the
*simulation* instead would mean predicting on from a position the server has already disagreed with.
Past a snap distance nothing is hidden, because the object did not drift, it was moved.

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
log. ⚠ **It used to be owed on there being nowhere to put it, and that is no longer true**:
`Vixen.Editor.Ui` registers panels and `Vixen.Editor.App` runs them. Everything the panel would show
is already public in these types, so what is left is the panel.

## Testing

The contract's own tests are in `Vixen.Net.Tests`, and the interesting one is
`TransportConformance` — an abstract suite asserting everything `ITransport` promises. It is run
there against `Vixen.Net.Transport.Local` and against the simulation wrapped around it; every other
transport's test project inherits the same suite. A transport is substitutable or it is nothing, and
the way to keep that true is to make the contract executable.

The other executable claim is [`Vixen.Fuzz`](../Vixen.Fuzz): twelve targets over every decode
path a peer can reach — down to the datagram and the HTTP upgrade, which are parsed before anything
has authenticated — eleven million cases on every build, holding each of them to three promises —
nothing throws, nothing amplifies, nothing is retained. It found four defects on its first run,
including one packet that crashed a client and one that made it keep a player record per packet.

And `Vixen.Net.Tests/Wire` is the third: the encoders run against **committed bytes**, one hex line
per named case. Two peers that encode the same value differently do not disagree, they desync — a
difference is measured against a capture the receiver also holds, so one machine rounding a quantized
level one step differently corrupts every difference after it, silently, for the rest of the match.
The gate is the CI matrix rather than a job of its own: `ci.yml` already runs the tests on Linux,
Windows and macOS — three operating systems and two architectures — so asserting against committed
bytes *is* bit-exactness across all three.

What makes it hold is worth knowing, because it is what a red build would mean has stopped being
true: every arithmetic step on the wire path is IEEE-754 and correctly rounded. `QuantizeRange` works
in `double` with nothing but `+ - * /`; the two normalisations the rotation codec leans on are
`1f / MathF.Sqrt(x)`. No transcendental, no fused multiply-add — C# never contracts one — and no
reciprocal estimate. `UPDATE_GOLDEN=1` regenerates the listings, and the diff is the review.

It pins a game's own components too, not only the engine's — including the registry index each type
gets, which is a function of the type *name*, because types are ordered by hashed id so that two
builds agree without agreeing on start-up order. **Renaming a replicated component is a wire break**,
and this is where that shows up.

## Owed

Where the other packages' `Owed` sections are about their own subject, these are the core's. Anything
that belongs to a transport, to the generators, to lag compensation or to a plug-and-play component is
in that package's README; the roadmap has the whole of Phase 9 in one place.

- **Predicted spawns.** A client cannot predict an object into existence — a projectile it fired is
  the case everybody hits first. It needs an id space a client may allocate in and a reconciliation
  that matches its guess to the server's real spawn, which is the largest thing left in prediction.
- **The predicted step is a delegate, not the scheduler.** `PredictedStep<T>` is a callback the game
  supplies. What it should be is a re-entrant run of `SystemPhase.FixedUpdate`, so "what is simulated"
  and "what is replayed" cannot drift apart — and that wants the scheduler to be re-entrant, which it
  is not.
- **`NetworkTransform` per-axis enable and parent-relative replication.** A door that only rotates
  pays for a position; a crate on a moving ship replicates world coordinates that fight the ship's.
  Both are on the component and neither is built.
- **`ResendDelayTicks` should be the connection's measured round trip.** The session keeps a
  `RoundTripEstimator` per player and `ReplicationServer` does not see it, so one figure stands in for
  every connection. Measured on the soak: four ticks gave 137 kbit/s a client and five gave 80, which
  is how much this is worth getting right per connection rather than once.
- **A cost budget for rewinds.** The RPC rate limiter counts calls, and a lag-compensated hit claim
  costs far more than an ordinary one. The limiter is the right place and it does not yet know that
  some calls are dearer than others.
- **Interest rules a game writes.** The chain takes any `IInterestRule`, and the team, room and
  fog-of-war resolvers doc 16 names are deliberately not shipped — each is a game's own idea of who
  may see what.
- **Generated encoders in the bit-exactness corpus.** Their *source* is pinned by
  `Vixen.Net.Generators.Tests` and every arithmetic primitive they emit is pinned by `Wire`, so what
  is uncovered is the composition rather than either half. Closing it means referencing the generator
  from that test project.
