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

**The client half is a single choice, not a race.** Composing servers is the useful direction — a
client knows what it is and which address it was given. Starting every inner client at once and
keeping whichever answers first is a different feature, transport fallback, and it is not smuggled in
here. ⚠ It used to be filed under the relay work; it is not any more — see Owed.

**Capabilities are the pessimistic answer to all three questions.** The smallest `MaxPayloadBytes` of
any of them, in-process only if all of them are, lossy if any of them is. A caller sizing a buffer
from this has to be able to hand it to whichever transport a given connection turns out to be on, and
it does not get to know which.

## Testing

The conformance suite runs against a composite wrapping a single in-process transport, which is the
degenerate case and exactly the one to assert: everything the contract promises has to survive the
wrapping, and any of it that does not is a bug in the wrapper. On top of that are the tests that need
two genuinely different transports — that ids do not collide, that a reply goes back out the
transport it came in on, and that the capabilities are the conservative ones.

## Owed

- ~~**A relay, and the client half that would talk to it.**~~ **Answered, not owed.** Decided
  2026-09-04: **Vixen does not operate a relay.** With no reference server a relay client can only
  speak a vendor's protocol, and there is no neutral one — so it is an addon if it is ever anything,
  the way Steam and EOS are. Recorded in doc [16](../../docs/plan/16-networking.md) § Projects, which
  until then listed `Vixen.Net.Transport.Relay/` inside `Core/` four lines above the paragraph making
  platform transports addons.
- **Transport fallback.** Start several, keep whichever answers. ⚠ **It no longer waits on the
  relay**, and the reason this package's client half is a single choice has expired with it. That
  reason was *"a race is only worth having when there is something to race against, and today the two
  client transports go to different kinds of server rather than to the same one by different routes"* —
  true of the two transports, and never true of **this** package's own server: a composite listening on
  UDP and on WebSocket 443 is one server reachable two ways, and a client racing them is a client that
  gets through a corporate firewall. What the work costs is the semantics of a race, not a decision:
  which connection wins, what happens to the loser mid-handshake, and which `TransportCapabilities` the
  layers above are told about before it resolves.
