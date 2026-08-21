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

⚠ **The last four are four counters and not two ratios**, for the reason every counter here is
cumulative: a number that has already been divided cannot be re-aggregated across a fleet. And
⚠ **a transport that counts nothing leaves them at zero**, because a cumulative counter has no way to
say "not measured" and registering them conditionally would make the scrape schema depend on which
transport a server happened to run. All four flat at zero on a server that is plainly sending is a
transport that does not count — not a clean link. See
[measuring packet loss](../../docs/guide/engine/measuring-loss.md).

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

## Owed

- **Traces.** Only metrics are wired. A span per tick, or per handshake, is the other half of what
  OpenTelemetry is for, and the handshake is the one with enough steps to be worth a trace — protocol,
  content hash, authenticator, admission. Nothing in this package prevents it; nothing in it does it.
- **Logs.** The engine has its own sink — `Vixen.Core.Diagnostics`' ring buffer, which the editor
  console and the crash reporter read — and bridging it to OTLP is a separate decision from this one.
- **Something to look at.** A Grafana dashboard as a committed JSON file would make the table above
  actionable rather than a list. It is a small piece of work and belongs with whatever ships the
  container image ([12](../../docs/plan/12-build-ci-and-testing.md)).
- **The client half.** `ReplicationClient`'s rejected and stale snapshot counts are the numbers that
  say a player is having a bad time, and nothing publishes them — a client is not usually scraped, so
  it wants a different route out than this one.
