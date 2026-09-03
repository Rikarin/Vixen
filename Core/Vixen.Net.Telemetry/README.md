# Vixen.Net.Telemetry

The metrics endpoint a dedicated server is expected to have, over OpenTelemetry.

## The split, which is the point of there being two packages

The **instrumentation** lives in `Vixen.Net`, in `NetworkMetrics`, and depends on nothing.
`System.Diagnostics.Metrics` is not a third option alongside OpenTelemetry and Prometheus — it *is*
OpenTelemetry's metrics API in .NET. The BCL types are the specification's API surface; the SDK is only
needed to export. So a server that already has an OpenTelemetry pipeline gets every metric below by
adding one name to its meter list:

```csharp
builder.AddMeter(NetworkMetrics.MeterName);   // "Vixen.Net"
```

The **export** lives here, because a game that never runs a dedicated server must not carry an
exporter, a protobuf serializer and an HTTP client to link a build that only ever plays offline.

## Why it pushes

A game server is not a web service with a stable address. It is one of a fleet, started and stopped per
match, on a port an orchestrator chose, often behind NAT, and frequently shorter-lived than a scrape
interval. Everything Prometheus's pull model is good at depends on the target being findable and
long-lived, and a match server is neither.

OTLP inverts that: the server needs to know one address — usually a sidecar on localhost — and the
collector is the thing with a stable name that a Prometheus can then scrape, if that is what the
organisation runs. Choosing OTLP here does not choose the backend; it declines to.

## Using it

```csharp
using var telemetry = NetworkTelemetry.Start(
    new TelemetryOptions {
        ServiceName = "arena-server",
        ServiceVersion = ThisBuild.Version,
        ServiceInstanceId = matchId,
        Attributes = new Dictionary<string, object> { ["deployment.region"] = "eu-west-1" }
    }
);

telemetry.Metrics.Session = session;
telemetry.Metrics.Replication = replication;
telemetry.Metrics.Rpc = router;
telemetry.Metrics.Ledger = ledger;
telemetry.Metrics.Transport = session.Transport;   // the loss counters, when the transport keeps any
telemetry.Metrics.Client = replicationClient;     // on a client — the three below that are its own

// …and once a tick, from the loop that owns those five:
telemetry.Metrics.Sample();
telemetry.Metrics.RecordTick(tookThisTick);
```

Everything in `TelemetryOptions` has a default and most deployments set none of them: the SDK reads
`OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_SERVICE_NAME` and the rest of the standard environment on its own,
which is what lets one image run in three environments.

## The `Sample()` call, and why it is not automatic

Observable instruments are called back on the SDK's collection thread. A session, a replication server
and a router are single-threaded frame code, so a callback that walked `Session.Players` would
eventually walk it while a player was joining — an exception on a background thread, in a process whose
entire job is to stay up.

`Sample()` runs on the frame's thread, reads all four, and copies what it finds into a struct. The
callbacks read the struct. Thread-safe by construction rather than by locking, at the cost of a few
dozen bytes of copying a tick and one line in the loop.

## What is published

| Metric | Kind | What it answers |
|---|---|---|
| `vixen.net.players` | gauge | how many are actually connected |
| `vixen.net.players.awaiting_reconnect` | gauge | how many are holding a seat and cannot be sent to |
| `vixen.net.tick` | gauge | what tick it is |
| `vixen.net.tick.duration` | histogram, s | **how often a tick goes over budget** |
| `vixen.net.rtt.mean` / `.worst` | gauge, s | the link, and the player complaining about it |
| `vixen.net.jitter.worst` | gauge, s | what is sizing somebody's interpolation buffer |
| `vixen.net.bandwidth` | counter, By | everything the ledger accounted for |
| `vixen.net.snapshot.records` | counter, tagged `kind` | delta against whole — the ratio, not two series |
| `vixen.net.snapshot.suppressed` | counter | what retransmission backoff is saving |
| `vixen.net.snapshot.size` | histogram, By | whether the budget is clipping snapshots |
| `vixen.net.rpc.calls` | counter, tagged `outcome` | accepted, and each of the six refusals |
| `vixen.net.datagrams.sent` | counter | reliable datagrams sent once — the denominator the next row needs |
| `vixen.net.datagrams.retransmitted` | counter | what went again: an **upper bound** on outbound loss, not a count of it |
| `vixen.net.datagrams.expected` | counter | inbound sequences past the ack window, which either came or did not |
| `vixen.net.datagrams.lost` | counter | how many did not — **observed** inbound loss, over the row above |
| `vixen.net.client.entities` | gauge | what interest management left a client holding |
| `vixen.net.client.snapshots.rejected` | counter | snapshots a client could not decode — two peers disagreeing about a wire format |
| `vixen.net.client.snapshots.stale` | counter | snapshots that arrived after a newer one; reordering, normal in small amounts |

⚠ **The last four are four counters and not two ratios**, for the reason every counter here is
cumulative: a number that has already been divided cannot be re-aggregated across a fleet. And
⚠ **a transport that counts nothing leaves them at zero**, because a cumulative counter has no way to
say "not measured" and registering them conditionally would make the scrape schema depend on which
transport a server happened to run. All four flat at zero on a server that is plainly sending is a
transport that does not count — not a clean link. See
[measuring packet loss](../../docs/guide/engine/measuring-loss.md).

⚠ **The last three are a client's and they go out the same way, which corrects what this file used to
say.** It said a client "wants a different route out than this one" — and the argument behind that was
that a client is not scrapeable, which is true and is precisely why this package *pushes*. The route
it already has is the one a client needs. What genuinely differs is volume: a hundred thousand clients
exporting every fifteen seconds is a decision about interval, sampling and whether a game wants to
receive that at all, and those are deployment settings rather than a second exporter.

Three decisions in that table are worth stating.

**The tick is a histogram, not a gauge.** The question a dedicated server is asked is not what a tick
costs on average — it is how often one goes over budget, and a mean cannot be asked that. `Samples/09`
makes the same argument for asserting on its p99.

**Refusals are tagged, not separate instruments.** Refusals are normal traffic: a client whose object
was despawned a tick ago is refused and is not misbehaving. What matters is which refusal is climbing,
and that is a ratio one tagged series answers and seven separate ones make somebody compute.

**Nothing is differenced here.** Everything sourced from a running total is a cumulative counter, so a
missed scrape loses resolution rather than data, and a rate can still be re-aggregated across three
servers. A metric that arrives pre-differenced cannot be.

## Runtime metrics

`IncludeRuntimeMetrics` is on by default and adds CPU, GC, thread pool and exception counts. A server's
own numbers say what happened and almost never why; the answer is usually a collection, a starved
thread pool, or an exception being thrown in a loop, and none of those is visible from the networking
metrics alone. The soak in `Samples/09` is the same lesson learnt the expensive way — the worst tick in
that run *was* a garbage collection.

## Traces

A span per handshake, under the same name as the meter, and on by default. See
[handshake traces](../../docs/guide/engine/handshake-traces.md) for what one carries.

The handshake is the thing worth a span and a tick is not: a tick is one number asked sixty times a
second, which is what `vixen.net.tick.duration` is a histogram for. A handshake is four steps that
each fail differently, at a rate a trace backend can afford, and it answers the one question no
counter can — *which* step this player's connection died at.

`TraceSampleRatio` is 1.0. ⚠ That is the wrong default for a web service and the right one here: a
match server handshakes a few dozen times a match, and a sampled trace of an event that rare is
missing exactly the connection somebody is asking about.

Metrics and traces are two providers sharing one resource, because they are two signals with two
pipelines in the SDK. `Flush` asks both — and asks both before returning either answer, since a
metrics pipeline that cannot reach the collector is the ordinary case this whole type is written
around, and short-circuiting would lose the spans for exactly the shutdown somebody is investigating.

## The dashboard

`dashboards/vixen-net.json`, packed with this package and copied to the output of anything that
references it. Import it into Grafana against a Prometheus fed by your collector.

⚠ **A dashboard is the observability artefact most likely to be quietly wrong, because nothing fails
when it is.** A renamed metric leaves a panel drawing "No data", which is what a healthy quiet server
looks like — so the panel that would have told you about an outage is the one that stops working
first, silently. `DashboardCoverageTests` is the answer: it derives the Prometheus name of every
instrument from the **live meter** — dots to underscores, the unit as a suffix, `_total` on a
monotonic counter — and fails in both directions. An instrument nothing draws is a number nobody will
look at; a series the dashboard draws that no instrument publishes is a permanently empty panel.

## Owed

- **Logs.** The engine has its own sink — `Vixen.Core.Diagnostics`' ring buffer, which the editor
  console and the crash reporter read — and bridging it to OTLP is a separate decision from this one:
  it means this package taking a dependency on `Vixen.Core.Diagnostics`, and it means deciding whether
  a game's own log categories go to a collector by default. The shape is settled — a
  `LogRecordSink` that writes into an OpenTelemetry logger provider — and the decision is not.
