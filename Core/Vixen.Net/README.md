# Vixen.Net

Networking. Optional: a game that never references it pays nothing, and nothing below `Vixen.Engine`
is allowed to reference it.

Spec: [docs/plan/16-networking.md](../../docs/plan/16-networking.md).

## What is here so far

Everything up to and including the session. Replication, interest management and the RPC generator
land on top of these and are not built yet; see the roadmap for what is owed.

```
Vixen.Net              Channel · ConnectionId · DisconnectReason · Tick
Vixen.Net.Transport    ITransport · ITransportEvents · NetworkSimulation
Vixen.Net.Messaging    PacketWriter · PacketReader · BitWriter · BitReader · QuantizeRange
Vixen.Net.Time         TickRate · TickManager · RoundTripEstimator
Vixen.Net.Sessions     NetworkSession · NetworkPlayer · PlayerId · ISessionAuthenticator
Vixen.Net.Replication  NetworkId · [Replicated] · [Quantize] · ReplicationServer/Client
```

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
replication.Capture(world);                       // read and encode what changed — once
foreach (var player in session.Players) {
    if (replication.TryWriteSnapshot(world, player.Id, session.Tick, buffer, out var snapshot)) {
        session.SendToPlayer(player.Id, snapshot, Channel.Unreliable);
    }
}
```

Four things carry it.

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

## Testing

The contract's own tests are in `Vixen.Net.Tests`, and the interesting one is
`TransportConformance` — an abstract suite asserting everything `ITransport` promises. It is run
there against `Vixen.Net.Transport.Local` and against the simulation wrapped around it; every other
transport's test project inherits the same suite. A transport is substitutable or it is nothing, and
the way to keep that true is to make the contract executable.
