# Vixen.Net.Transport.Composite

Several transports, listening at once, behind one.

Spec: [docs/plan/16-networking.md](../../docs/plan/16-networking.md) § Projects.

## What it is for

One server accepting more than one kind of client. A desktop build should be on UDP and a browser
build cannot be; a game that wants both has otherwise to run two servers with two worlds, or pick one
and make somebody suffer for it.

```csharp
var server = new CompositeTransport([
    new UdpTransport(new UdpDatagramSocketFactory(), new() { ListenEndPoint = new(IPAddress.Any, 7777) }),
    new WebSocketTransport(new SystemWebSocketFactory(), new() { ListenAddress = new("ws://0.0.0.0:7778/") }),
]);
```

The session, replication and RPC layers see a single transport and never learn that half their
players arrived over TCP.

## Connection ids are rewritten, and that is the whole of the difficulty

Each inner transport numbers its own connections from one, so two of them will hand out the same
number for different players inside the first second. This one hands out ids of its own and keeps a
map both ways.

That matters because *everything* above keys by connection: the session's player table, ownership,
per-connection replication baselines. None of them check for a collision, because with one transport
there cannot be one. `ClientsOnDifferentTransports_AreNumberedApart` is the test that says so.

## Two decisions worth knowing about

**The client half is a single choice unless it is asked to be a race.** Composing servers is the
useful direction — a client that knows what it is and which address it was given wants the one it was
told to, which is what the ordinary constructor gives it. `CompositeTransport.Racing` is the other
case, and it is one server rather than two: a composite listening on UDP and on WebSocket 443 is
reachable two ways, and a client racing them is a client that gets through a corporate firewall that
drops UDP.

```csharp
var client = CompositeTransport.Racing([udp, webSocket], stagger: TimeSpan.FromMilliseconds(250));
```

Three things about it are worth knowing before reading the code.

- **The winner is the first transport-level connect, which is not the first completed handshake.** A
  handshake is `NetworkSession`'s — `ConnectRequest` and `ConnectAccepted` — and an `ITransport` sees
  connections, disconnections and opaque bytes. The stronger transport-observable signal is *first
  inbound data*, which is what catches a middlebox that accepts the connection and drops the payload;
  it is a different signal rather than an expensive version of this one, and it is not built.
- ⚠ **Nothing above ever learns that a race happened.** Only the winner's connect is reported; a
  loser's connect, bytes and disconnect are swallowed and the candidate is stopped. That is what makes
  this implementable without touching the session: a pure client is driven to `SessionState.Stopped`
  by a disconnect, and its client arm starts a fresh handshake on every connect. The one failure a
  race does report is every route failing, which is one `OnDisconnected` with the last reason.
- ⚠ **The stagger is counted in the `elapsed` each `Poll` is handed and never from a clock**, like
  everything else in this layer whose behaviour depends on time. A failed candidate does not wait it
  out — the next route starts the moment the failure is observed, because a stagger exists to avoid a
  wasted attempt and not to make a dead route cost latency.

**Capabilities are the pessimistic answer to all three questions.** The smallest `MaxPayloadBytes` of
any of them, in-process only if all of them are, lossy if any of them is. A caller sizing a buffer
from this has to be able to hand it to whichever transport a given connection turns out to be on, and
it does not get to know which. ⚠ **Including on a racing client, before and after the race**: a
buffer sized while the race is unresolved is sized for every candidate including the losers, and the
answer does not widen when a winner emerges, because a buffer already handed out cannot be told to
grow.

## Testing

The conformance suite runs against a composite wrapping a single in-process transport, which is the
degenerate case and exactly the one to assert: everything the contract promises has to survive the
wrapping, and any of it that does not is a bug in the wrapper. On top of that are the tests that need
two genuinely different transports — that ids do not collide, that a reply goes back out the
transport it came in on, and that the capabilities are the conservative ones.

`CompositeClientRaceTests` covers the race, and the shape of it is worth copying: every assertion is
about *which poll* something happened on rather than how long it took, and one test asserts that the
winner's own disconnect **is** forwarded — because three of its neighbours assert a disconnect did not
arrive, and a composite that forwarded no client disconnect at all would pass all three.

## Owed

- ~~**A relay, and the client half that would talk to it.**~~ **Answered, not owed.** Decided
  2026-09-04: **Vixen does not operate a relay.** With no reference server a relay client can only
  speak a vendor's protocol, and there is no neutral one — so it is an addon if it is ever anything,
  the way Steam and EOS are. Recorded in doc [16](../../docs/plan/16-networking.md) § Projects, which
  until then listed `Vixen.Net.Transport.Relay/` inside `Core/` four lines above the paragraph making
  platform transports addons.
- ~~**Transport fallback.**~~ **Built**, `CompositeTransport.Racing`
  ([#518](https://github.com/Rikarin/Vixen/issues/518)). Two of the three questions that issue posed
  turned out to rest on premises that do not hold, and the audit that found that is worth keeping:

  - ⚠ *"first transport-level connect, or first completed handshake? The second is the useful one and
    the expensive one."* **The second is not available at this layer at all**, so it is not the
    expensive version of the same question. See the decision above for the signal that is.
  - ⚠ *"`CompositeTransport` already rewrites connection ids, so the map is the place."* **The map is
    the server path only.** `Router.OnConnected` passes a `TransportRole.Client` connection through
    unrewritten, deliberately. The loser is torn down by stopping its inner client half and dropping
    its events — no id ever reaches the map, and none has to be taken back out of it.
  - **Pessimistic capabilities before the race resolves: confirmed, and it was already true.** What
    was owed there was a sentence rather than a behaviour, and it is in the decision above.

  ⚠ **And the fourth question, in `Vixen.Net` rather than here, turned out not to block this.** The
  client arm of `NetworkSession` assumes exactly one connect per client lifetime — and it still does.
  A race that reports only its winner never tests that assumption, because nothing above is told a
  losing attempt existed and so nothing above needs telling that it failed. The half of it that was a
  real defect on its own terms — a second `OnConnected` leaking the first handshake span — is fixed
  ([#1213](https://github.com/Rikarin/Vixen/issues/1213)); the half that was a design question, a
  session-level notion of an *attempt* that can fail, has no caller and is not built.
