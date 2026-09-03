---
title: Handshake traces
slug: engine/handshake-traces
kind: guide
area: Networking
summary: A span per handshake, so a connection that failed says which step it died at.
api: [T:Vixen.Net.Diagnostics.NetworkActivity]
tags: [networking, observability, opentelemetry, tracing, dedicated-server]
since: 0.1
status: preview
related: [engine/network-sessions, engine/measuring-loss, engine/round-trip-and-jitter]
---

## What it is

A session emits one **span per handshake**, on both sides of it, under the source name
`Vixen.Net`. `System.Diagnostics.Activity` is OpenTelemetry's tracing API in .NET, so
`Core/Vixen.Net` takes no dependency to produce them — the same arrangement `NetworkMetrics`
already has.

## What it is for

The question metrics cannot answer. A counter of refusals is a number; the thing anybody actually
wants to know is **which step** this player's connection died at, and how long the step before it
took. A handshake has four of them — the request parsing, the protocol version, the content hash,
and an authenticator that is often a network call to somebody else — and each fails differently.

You do not want a span per tick. A tick is one number asked sixty times a second, which is what
`vixen.net.tick.duration` is a histogram for; a trace of it would be a trace backend's bill.

## Turning it on

If your server already has an OpenTelemetry pipeline, add the source:

```csharp
builder.AddSource(NetworkActivity.SourceName);   // "Vixen.Net"
```

If it does not, `Vixen.Net.Telemetry` builds one, and traces are on by default:

```csharp
using var telemetry = NetworkTelemetry.Start(
    new TelemetryOptions { ServiceName = "arena-server", IncludeTraces = true }
);
```

`TraceSampleRatio` is 1.0 — every handshake. ⚠ That is the wrong default for a web service and the
right one here: a match server handshakes a few dozen times a match, and a sampled trace of an event
that rare is missing exactly the connection somebody is asking about. A fleet large enough for it to
add up sets the ratio, and then it is a decision somebody made rather than one they inherited.

**Off costs nothing measurable.** `ActivitySource.StartActivity` returns null when no listener is
registered, and the session is written to be handed that null all the way through.

## What a span says

| | |
|---|---|
| `vixen.net.role` | `server` or `client` |
| `vixen.net.connection` | the transport's connection id, server side |
| `vixen.net.player` | who they turned out to be, on success |
| `vixen.net.handshake.outcome` | `admitted`, `resumed`, `refused`, `connection_lost`, `session_stopped` |
| `vixen.net.handshake.refusal` | which refusal, when it was one |

The **events on the span are the steps that passed** — `request_read`, `protocol_agreed`,
`content_agreed`, `authenticated` — so the last event names where the handshake got to. A span with
`request_read` and nothing after it, tagged `ProtocolMismatch`, is a client from the build before
this one.

⚠ **A refusal is `Error` status even though most refusals are the server working correctly.** A
protocol mismatch during a rollout is not a fault and is still the single most useful thing a backend
can be asked to show all of; the refusal tag is what tells the ordinary ones from the alarming ones.

## The span outlives the call that started it

An authenticator may answer `Pending` and be asked again on a later frame — which means the
interesting handshake, the slow one, is exactly the one that does not fit inside a single call. The
span is therefore carried on the pending request and ended by whichever of admission, refusal,
timeout, a dropped connection or the session stopping gets there first.

⚠ **That is why every one of those endings is accounted for rather than just the happy one.** An
`Activity` nobody stops is never exported — it is not a wrong span, it is *no* span, which reads
exactly like a handshake that never happened. A shutdown that lost every in-flight handshake would
look like a server nobody was connecting to.

## What is not there yet

**The two sides are two roots.** A handshake carries no trace context, so a backend joins the
client's span and the server's by time and address rather than by parentage. Propagating it would be
a field in the connect request, which is a wire change and is not one this made.
