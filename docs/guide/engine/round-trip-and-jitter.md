---
title: Round trip and jitter
slug: engine/round-trip-and-jitter
kind: guide
area: Networking
summary: The RFC 6298 filter every latency number in the engine comes out of — a smoothed round trip, a smoothed deviation beside it, and why the deviation is the one that sizes buffers.
api: [T:Vixen.Net.Time.RoundTripEstimator]
tags: [networking, latency, jitter, time, diagnostics]
since: 0.1
status: stable
related: [engine/networked-players, engine/network-sessions, engine/measuring-loss, editor/network-panel]
---

## What it is

`RoundTripEstimator` is two exponentially weighted averages and a sample count.

| | |
|---|---|
| `RoundTrip` | The smoothed round trip. |
| `OneWay` | Half of it — how old the newest thing from the server is. |
| `Jitter` | The smoothed *deviation*: how far a sample typically falls from that average. |
| `SampleCount` | How many samples have gone in. |
| `HasSamples` | Whether anything has been measured at all. |

`Add(TimeSpan)` takes one measured round trip. `Reset()` forgets everything, for a reconnect to a
different server.

The filter is the one TCP has used since RFC 6298, at the standard weights of 1/8 for the average and
1/4 for the deviation. That is a deliberate refusal to invent: those constants have been load-bearing
in every TCP stack for thirty years, and a netcode-specific pair chosen by eye would be both worse and
unjustifiable.

The first sample has nothing to average against, so it seeds `RoundTrip` at itself and `Jitter` at
**half** of itself rather than at zero — RFC 6298's own seeding, and it means a link is not assumed
perfectly steady until it has been watched long enough to know.

⚠ **`Jitter` is a mean absolute deviation, not a standard deviation and not a variance**, despite the
word "variance" appearing in the type's own remarks. It is in the same units as the round trip, which
is what lets `RoundTrip + 4 × Jitter` be a timeout and `Jitter × 2` be a buffer depth.

## What it is for

**The deviation is the number that matters, and that is the whole argument for keeping a filter
instead of a last-sample-wins field.** A steady 200 ms link is easy — everything downstream just aims
further ahead. A link that swings between 40 ms and 90 ms is not, because there is no single lead that
is right at both ends of the swing. Only the second number can tell those two links apart, and a raw
sample cannot tell either of them from a single unlucky packet.

Four things in this engine read one, and they are worth listing because it is easy to assume the
estimator is a diagnostic:

* **The client's clock.** `TickManager.RoundTrip` is fed by `NetworkSession`'s ping reply. Its
  `LeadTicks` is `OneWay + 2 × Jitter + 1`, so a client that is estimating badly sends input that
  arrives after the tick it was for; its `InterpolationDelayTicks` is `2 × Jitter + 1`, so it also
  decides how far *behind* the server the renderer interpolates.
* **The UDP retransmission timeout.** `UdpTransport` computes `RoundTrip + 4 × Jitter` per connection
  — RFC 6298's formula with RFC 6298's estimator — clamped between its configured floor and ceiling.
  This is the one with the sharpest failure: an estimate that lags a lengthening link resends
  datagrams that were merely late, which is exactly the reading that inflates `Retransmitted / Sent`
  in [packet loss](measuring-loss.md).
* **Per-player latency on a server.** `NetworkPlayer.RoundTrip` is one estimator per player, fed from
  the pong that closes each ping.
* **The meter and the panel.** `NetworkMetrics` publishes `vixen.net.rtt.mean`, `vixen.net.rtt.worst`
  and `vixen.net.jitter.worst` — the mean over connected players and the worst anybody has; the
  editor's [network panel](../editor/network-panel.md) draws round trip and jitter as trends.

### Why the engine does not publish a rate or a percentile

`NetworkMetrics` publishes the mean as a **gauge** rather than the samples as a histogram, and says so
in its own remarks: the value it reads is *already smoothed*, so a histogram of it would report the
filter's distribution rather than the network's. The spread that matters is published beside it as the
jitter. Whoever wants percentiles wants the raw samples, and those are the caller's — `Add` is where
they were.

### What it cannot tell you

* **Which direction is slow.** A round trip is a sum. Nothing in it separates the way out from the
  way back, and an asymmetric link reads as a symmetric one at the average of the two.
* **What happened in the last second.** An exponential filter has no window, so a spike that has
  passed is still in the average, decaying. That is the point for a timeout and a nuisance for a
  report; a graph of the output over time — which is what the editor panel keeps — is the way to see
  the shape.
* **Anything at all before the first sample.** `RoundTrip` and `Jitter` are both `TimeSpan.Zero` until
  `HasSamples` is true, and zero is a plausible-looking answer. Check `HasSamples`; a loopback really
  does measure zero, so the flag and the value are not interchangeable.

## Using it

The engine already owns one everywhere it needs one, so the common case is reading rather than
constructing:

```csharp no-compile="a fragment; `session` is a started NetworkSession"
// On a client: the clock's own estimator, fed by the session's ping.
if (session.Clock.RoundTrip is { HasSamples: true } link) {
    var lead = session.Clock.LeadTicks;               // OneWay + 2 × Jitter + 1
    var behind = session.Clock.InterpolationDelayTicks; // 2 × Jitter + 1
}

// On a server: one per player.
foreach (var player in session.Players) {
    if (player.RoundTrip.HasSamples) {
        var rtt = player.RoundTrip.RoundTrip;
        var jitter = player.RoundTrip.Jitter;
    }
}
```

Feeding one is three lines, and the only rule is that a sample must be a *measured* round trip rather
than a guess or a configured latency:

```csharp compile
using Vixen.Net.Time;

public static class Latency {
    public static (TimeSpan RoundTrip, TimeSpan Jitter) Watch(params TimeSpan[] samples) {
        var estimator = new RoundTripEstimator();

        foreach (var sample in samples) {
            estimator.Add(sample);
        }

        return (estimator.RoundTrip, estimator.Jitter);
    }
}
```

`Add` refuses a negative sample with `ArgumentOutOfRangeException` and **allows zero**, because a
loopback really is that fast and a test should be able to say so.

⚠ **Reset on reconnect, not on disconnect.** The estimator carries no notion of a connection, so a
session that reuses one across a reconnect to a *different* server starts the new link at the old
one's average and takes about a dozen samples to forget it — during which the tick lead and the
retransmission timeout are both sized for a link that is not there.

## Examples

**Reading how steady a link is, rather than how fast.** The two numbers answer different questions and
the second is the one a player feels:

```csharp no-compile="a fragment; `link` is a RoundTripEstimator with samples"
// 200 ms steady and 65 ms swinging are the same "average ping" to a player and
// completely different links to everything downstream of this.
var steadiness = link.RoundTrip == TimeSpan.Zero
    ? 0d
    : link.Jitter.TotalMilliseconds / link.RoundTrip.TotalMilliseconds;
```

**A timeout, the way `UdpTransport` builds one.** The average plus four deviations is the point past
which a datagram is far more likely lost than late, and the clamp is what stops a first sample on a
loopback from producing a timeout of zero:

```csharp no-compile="a fragment; `link` is the connection's estimator"
var timeout = link.HasSamples
    ? Math.Clamp(
        link.RoundTrip.TotalSeconds + (4 * link.Jitter.TotalSeconds),
        minimum.TotalSeconds,
        maximum.TotalSeconds)
    : initial.TotalSeconds;
```

**Why the first sample is seeded at half.** Feed one 100 ms sample and the estimator reports 100 ms
round trip and 50 ms jitter — not 100 and 0. Anything sizing a buffer from it is generous until the
link has proved itself steady, which is the safe direction to be wrong in on the first frame after a
handshake.

## See also

* [Network sessions](network-sessions.md) — what measures the round trips this smooths, and how often
* [Measuring packet loss](measuring-loss.md) — why an estimate that lags a lengthening link inflates the resend share
* [The network panel](../editor/network-panel.md) — both numbers as a thirty-second trend, per player
* [Networked players](networked-players.md) — the prediction and interpolation the lead and delay are for
* [`TickManager`](/docs/api/vixen.net.time/tickmanager) — the clock whose lead and interpolation delay are computed from these two numbers
* [`UdpTransport`](/docs/api/vixen.net.transport.udp/udptransport) — one estimator per connection, driving the retransmission timeout
