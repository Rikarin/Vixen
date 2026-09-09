---
title: Measuring packet loss
slug: engine/measuring-loss
kind: guide
area: Networking
summary: What the transport counts about datagrams that did not arrive — four cumulative totals, why the outbound pair is an upper bound and the inbound pair is an observation, how the peer's inbound counters come back over the wire to make it a measurement, and what it costs per packet.
api: [T:Vixen.Net.Transport.TransportLoss, T:Vixen.Net.Transport.LinkReport]
tags: [networking, transport, diagnostics, loss, metrics]
since: 0.2
status: preview
related: [editor/network-panel, engine/networked-players, engine/network-sessions, engine/round-trip-and-jitter, live/writing-a-realm]
---

## What it is

`TransportLoss` is four running totals, read off `ITransport.Loss`:

| | |
|---|---|
| `Sent` | Reliable datagrams handed to the socket **for the first time**. |
| `Retransmitted` | Datagrams sent again because no acknowledgement came in time. |
| `Expected` | Inbound sequences that have passed out of the acknowledgement window, and so can no longer arrive. |
| `Missing` | How many of those never did. |

Two pairs, one per direction, and they are **not** the same kind of number. `Missing / Expected` is
loss that was *observed*: the far end numbers its datagrams consecutively, so a gap that has fallen
out of the window is a datagram that was sent and did not come. `Retransmitted / Sent` is loss that
was *inferred from a consequence*, and it reads high — one lost datagram that takes three attempts
counts three, a lost *acknowledgement* resends a datagram that arrived, and a round trip that
lengthens faster than the estimator follows resends one that was merely late.

`ITransport.Loss` is `null` on a transport that counts none, which is most of them: an in-process one
never loses anything and has no sequence numbers to notice a gap in, and one over a stream has a
stack underneath it hiding the packets entirely. `UdpTransport` numbers its own datagrams, so
`UdpTransport` is the transport that can answer.

⚠ **Null is the honest answer and zero is not.** A transport that cannot count losses has not told
anybody there are none.

## What it is for

Three questions, and they are asked in different places.

* **"Is this player's connection bad?"** — the editor's [network panel](../editor/network-panel.md)
  draws both shares as lanes on its graph, beside round trip and jitter. That is the one a person
  looks at while somebody is complaining.
* **"Is the fleet's loss climbing?"** — `NetworkMetrics` publishes all four as cumulative counters,
  so an OpenTelemetry collector differences them the way it differences everything else.
* **"Did my change help?"** — the totals are on the transport itself, so a soak test can print them
  at the end of a run without an editor or a collector anywhere near it.

### What the numbers are *of*, and why that choice

**Datagrams, not messages and not bytes.** A datagram is what the network drops: a router discards a
whole packet, so a payload split into eight fragments is eight chances to lose something and one
message is not one trial. Bytes would weight the answer by how large the payloads happened to be,
which is a fact about the game rather than about the link.

**Only reliable traffic, on the outbound pair.** Nothing retransmits an unreliable datagram, so the
denominator has to be drawn from the same population as the numerator. A denominator that also
counted unreliable snapshots, acknowledgements and keep-alives would fall whenever a game sent more
of those, and the reader would watch the resend share change while the link stood still.

**All four channels, on the inbound pair.** A receiver notices a gap whatever the channel promised,
so inbound loss covers the unreliable traffic too — which is where loss actually hurts, and the half
the outbound pair can say nothing about.

### What inbound loss cannot see

* **Datagrams lost before the first one arrived on a channel.** There is no gap without a sequence
  either side of it.
* **A peer that sends nothing.** Silence has no sequence numbers, and a channel a game never uses
  contributes nothing to either total.
* **Anything the network dropped that was never a numbered datagram**: a handshake, an
  acknowledgement, a keep-alive.
* **Loss on the way *out*.** The far end acknowledges what it received and says nothing about what it
  did not. Its own inbound counters are the measurement of this end's outbound loss — and the session
  now carries them back, which is what [the report below](#the-peers-half-observed-outbound-loss) is.

⚠ **A sequence is judged when it falls out of the window, and not when the gap appears.** A gap that
is a moment old may be a datagram in flight; counting it immediately would report every reordering as
a loss and never take it back. The window is thirty-two sequences deep, so reordering inside it is
invisible here — which is the point — and the newest thirty-three sequences are in neither total yet.

### What it costs

The hot path pays **one increment per reliable datagram sent** and, per datagram received, a handful
of integer operations and a single `PopCount` — inside the bookkeeping that already runs to
de-duplicate the sequence. Nothing allocates and nothing locks. The walk over connections happens
only when somebody reads `Loss`, which is a few times a second for a panel and once a tick for a
meter, and it is the same walk `RetransmitCount` already made.

## The peer's half: observed outbound loss

⚠ **A sender cannot measure its own loss, so the only honest outbound number is one the peer says.**
That is `LinkReport`, and it is a wire message rather than a counter: once a
`SessionOptions.PingInterval`, beside the ping that already runs at that cadence, each end sends the
peer its own inbound totals for *that link*.

| | |
|---|---|
| `NetworkPlayer.ObservedOutbound` | What this player says it missed of what the server sent it. Read on a server. |
| `NetworkSession.ObservedOutbound` | What the server says it missed of what this client sent. Read on a client. |

```csharp no-compile="`session` is a server session"
foreach (var player in session.Players) {
    if (player.ObservedOutbound is { Expected: > 0 } report) {
        var lost = (double) report.Missing / report.Expected;
    }
}
```

⚠ **Null is not a clean link, and it is null two ways.** A peer whose transport counts nothing sends
no report at all — every in-process and every stream transport — and a peer that has only just joined
has not sent its first yet. It is also cleared when a player drops, because the counters described a
connection and a reconnecting player is a new link that has counted nothing.

⚠ **It is a separate type rather than a fifth field on `TransportLoss`, deliberately.** Those four
totals are this machine's own bookkeeping; this one is a measurement made by a different machine,
arriving a round trip late, and absent until the peer chooses to speak. Folding it in beside
`Retransmitted` would hide all three of those.

⚠ **Per link, which is why `ITransport.LossFor` exists.** `ITransport.Loss` adds every connection and
both halves together — the granularity a meter samples at — so a server that sent *that* to eight
players would tell each of them what it missed from all eight. `LossFor(connection)` asks the same
four questions of one link, and returns `null` from a transport that cannot attribute them and from a
connection that has gone.

**Compatibility.** `LinkReport` is a new message value rather than a longer `Pong`, and the
difference is that both dispatch switches already drop an unknown message without comment: a peer
built before this ignores the packet and loses only the measurement. Nobody has to bump
`SessionOptions.ProtocolVersion` for it. Lengthening `Pong` would have been the shape that breaks,
and it would have broken as clock drift rather than as a missing counter.

## Using it

Read it off the transport, or off a session's:

```csharp no-compile="`transport` is a UdpTransport the game constructed"
if (transport.Loss is { } loss) {
    // Two shares, both from one reading. Neither is published pre-divided, on purpose.
    var resent = loss.Sent == 0 ? 0 : (double) loss.Retransmitted / loss.Sent;
    var lost = loss.Expected == 0 ? 0 : (double) loss.Missing / loss.Expected;
}
```

[`NetworkSession.Transport`](network-sessions.md) is the transport a session is running on, so
anything holding a session already has this — which is how the editor panel draws its lanes without
the host wiring anything.

To publish them, hand the meter the transport beside everything else it samples:

```csharp no-compile="the server's own wiring; `metrics` is a NetworkMetrics"
metrics.Session = session;
metrics.Transport = session.Transport;

// Once a tick, from the loop that owns those objects.
metrics.Sample();
```

That registers `vixen.net.datagrams.sent`, `…retransmitted`, `…expected` and `…lost`, all as
cumulative counters.

⚠ **They are totals and never rates or shares**, which is `NetworkMetrics`'s rule and not a
simplification: a number that has already been differenced cannot be re-aggregated across three
servers, and a *lifetime* ratio is an average over the whole uptime — the number that hides the
thirty seconds somebody is asking about. Whoever has two readings does the division.

⚠ **A transport that counts nothing leaves those four counters at zero**, because there is no way for
a cumulative counter to say "not measured" and registering them conditionally would make a fleet's
scrape schema depend on which transport each server happened to be running. Read them beside
`vixen.net.datagrams.sent`: all four flat at zero on a server that is plainly sending is a transport
that does not count, not a clean link.

## Examples

**A soak that reports what the link did to it.** The counters are cumulative, so the whole run is one
subtraction:

```csharp no-compile="a fragment; `transport` is the UdpTransport under the session"
var before = transport.Loss ?? default;

// … run the soak …

var after = transport.Loss ?? default;
var lost = after.Missing - before.Missing;
var expected = after.Expected - before.Expected;

Console.WriteLine($"{lost} of {expected} inbound datagrams did not arrive.");
```

**Simulated loss is not measured loss, and that is deliberate.**
`NetworkSimulation` forwards `Loss` from the transport it wraps rather than reporting what it threw
away — `DroppedPayloadCount` is loss you *asked* for, and publishing it here would make a profile's
`LossChance` come back as an observation. A payload the simulation drops never reaches the transport
below it, so no sequence is spent on it and no gap appears downstream: the simulation models a link
that never carried the payload at all.

**Where the two directions disagree.** On a symmetric link, `Retransmitted / Sent` runs above
`Missing / Expected` — that is the expected shape rather than a defect, for the three reasons in
[What it is](#what-it-is). A resend share far above the inbound loss share on a link whose inbound
loss is near zero is worth reading as a [round-trip estimate](round-trip-and-jitter.md) that has
fallen behind, not as an asymmetric network.

## See also

* [The network panel](../editor/network-panel.md) — both shares as lanes, differenced from these totals
* [`UdpTransport`](/docs/api/vixen.net.transport.udp/udptransport) — where the counting happens: the sender remembers, the receiver judges
* [`NetworkMetrics`](/docs/api/vixen.net.diagnostics/networkmetrics) — the meter, and why nothing in it is a rate
* [`NetworkSimulation`](/docs/api/vixen.net.transport/networksimulation) — loss you ask for, reproducibly, which is the other half of testing against a bad link
* [`LinkReport`](/docs/api/vixen.net.transport/linkreport) — what the peer says it missed, which is the only outbound loss anybody observes
