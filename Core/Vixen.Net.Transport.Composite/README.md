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
keeping whichever answers first is a different feature, transport fallback, and it belongs with the
relay work rather than being smuggled in here.

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

- **A relay, and the client half that would talk to it.** Doc [16](../../docs/plan/16-networking.md)
  asks for "rendezvous + relay client", and a relay client with no relay server is untestable and
  unshippable — so building it is a decision about scope rather than a piece of work waiting to be
  done. Do we host one? Is it in-box, or an addon the way Steam and EOS are? That wants an answer
  before code.
- **Transport fallback**, which belongs with it. Start several, keep whichever answers. **This
  package's client half is deliberately a single choice rather than a race**, and that is why: a race
  is only worth having when there is something to race against, and today the two client transports go
  to different kinds of server rather than to the same one by different routes.
