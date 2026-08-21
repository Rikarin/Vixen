---
title: Network sessions
slug: engine/network-sessions
kind: guide
area: Networking
summary: The layer between a transport, which knows about connections and bytes, and a game, which wants players and ticks — the handshake, the clock, the player list, and why a player is not a connection.
api: [T:Vixen.Net.Sessions.NetworkSession]
tags: [networking, sessions, handshake, players, reconnect, ticks]
since: 0.1
status: stable
related: [engine/networked-players, engine/round-trip-and-jitter, engine/measuring-loss, editor/network-panel]
---

## What it is

`NetworkSession` owns exactly three things — **the handshake, the clock, and the player list** — and
hands every payload it does not understand to an `ISessionMessageHandler`.

It sits on an `ITransport` and is driven by one call a frame:

```
transport  →  NetworkSession  →  the game
bytes,        players,           spawning, replication,
connections   ticks, RTT         remote calls
```

| | |
|---|---|
| `Update(elapsed, messages)` | Poll, finish handshakes, measure round trips, retire lost players, advance the clock. Returns how many fixed ticks the caller owes. |
| `StartServer` / `StartClient` / `StartHost` / `StartOffline` | Which half or halves to run. |
| `Players`, `LocalPlayer`, `TryGetPlayer` | Who is in it. |
| `Tick`, `Clock` | What tick it is, and — on a client — the clock being kept in step with the server's. |
| `Transport` | The transport underneath, which is where [`Loss`](measuring-loss.md) hangs. |
| `SendToServer` / `SendToPlayer` / `SendToAll` | Payloads, on a `Channel`. |
| `Kick` | Removes a player and closes the connection. No reconnect window: a kick is final. |
| `PlayerJoined`, `PlayerConnectionChanged`, `PlayerLeft`, `Connected`, `Rejected`, `Disconnected` | Six events, and the whole of the session's outward vocabulary. |

Single-threaded, like everything else that runs in the frame. `Update` is where all of it happens.

## What it is for

**Nothing above this layer should have to know what a connection is.** Replication, remote calls,
player possession and the editor's diagnostics all speak in `PlayerId`s and `Tick`s; the transport
speaks in connection ids and datagrams. This is the one place the two meet, and the three guarantees
it makes are what let everything above it stay simple.

**Nothing is dispatched before the handshake finishes.** A payload from a connection that has not been
accepted is dropped — not queued, not delivered late. So a handler being called is proof the peer
agreed on the protocol version and the content hash and was let in by the authenticator. The content
hash is the useful half in practice: `Samples/08-Multiplayer` folds its component registry and RPC
manifest hashes into `SessionOptions.ContentHash`, so a peer built against different replicated
components is refused at the handshake rather than at the first packet that means two different things
to the two ends.

**A player is not a connection.** When a connection drops, the player it carried stays in the list
with `NetworkPlayer.IsConnected` false for the length of `SessionOptions.ReconnectWindow` — 30 seconds
by default — holding their id and their slot. A client that comes back with the token it was issued
resumes as the same player. That is the whole of reconnect support, and it is here rather than bolted
on later because retrofitting it means changing what every layer above means by "player".

**Host mode is not a special case.** `StartHost` starts both halves of one transport, and the host's
own client half does the same handshake through the loopback that a remote client does over a socket.
`StartOffline` is mechanically the same thing on a transport nobody else can reach. There is no
offline path to rot, which is the failure this shape exists to prevent.

### What it measures, and what it does not

The session pings each peer every `SessionOptions.PingInterval` and closes the loop when the pong
comes back. On a server that feeds `NetworkPlayer.RoundTrip`, one
[`RoundTripEstimator`](round-trip-and-jitter.md) per player; on a client it goes into
`Clock.Synchronize`, which is what makes `Clock.LeadTicks` and `Clock.InterpolationDelayTicks` mean
anything.

⚠ **Loss is the transport's, not the session's.** `Transport.Loss` is `null` on a transport that counts
none, which is most of them — see [measuring packet loss](measuring-loss.md). The session exposes the
transport rather than re-counting, which is why anything holding a session can draw both.

⚠ **`Update`'s return value is a tick count, not a frame count.** It is the same number the engine's
fixed-step accumulator would give, except that on a client it is the one being corrected towards the
server's clock — so it runs slightly fast while catching up and slightly slow while waiting. Ignoring
it and stepping the simulation once a frame is how a client drifts.

## Using it

A server is a transport, options, and a call a frame:

```csharp no-compile="a fragment; `transport` is an ITransport the game constructed"
var session = new NetworkSession(
    transport,
    new SessionOptions { MaxPlayers = 8, ContentHash = manifestHash },
    authenticator: null,      // everybody, if null
    ownsTransport: true);     // disposing the session disposes the transport

session.PlayerJoined += player => joining.Add(player.Id);
session.PlayerLeft += (player, reason) => leaving.Add(player.Id);

session.StartServer();

// Once a frame. `this` is an ISessionMessageHandler.
var ticks = session.Update(elapsed, this);

for (var i = 0; i < ticks; i++) {
    Step();
}
```

⚠ **Act on the events inside the step, not where they arrive.** `PlayerJoined` fires during `Update`,
which is before the ticks it returned have run. A player spawned from the handler is spawned before the
advance and is invisible to everybody until the next thing about them changes — so the samples collect
ids into a list and drain it in `Step`.

A client is the same call with a different start:

```csharp no-compile="a fragment; `token` is what an earlier session left in ReconnectToken"
if (!token.IsEmpty) {
    session.PresentReconnectToken(token);   // before connecting, not after
}

session.StartClient();
```

A token the server does not recognise — expired, already used, or from a different server — is **not**
an error and does not refuse the connection. It gets a new player id, which is exactly what "your seat
was given away" should feel like.

⚠ **Save `ReconnectToken` while the session is alive.** `Stop` clears it, along with the player list
and the reconnect table, so a token read after stopping is empty.

⚠ **`Dispose` disposes the transport only if you said it owns it.** The constructor's `ownsTransport`
defaults to false, so a session over a transport the game keeps for something else leaves it running.

## Examples

**Reading the link, without wiring anything.** Everything the editor's panel draws is reachable from a
session, which is why `NetworkView` measures nothing of its own:

```csharp no-compile="a fragment; `session` is a started NetworkSession"
var worst = TimeSpan.Zero;

foreach (var player in session.Players) {
    if (player.RoundTrip.HasSamples && player.RoundTrip.RoundTrip > worst) {
        worst = player.RoundTrip.RoundTrip;
    }
}

// And the other direction's numbers, from the transport rather than from here.
var loss = session.Transport.Loss;
```

**Four topologies, one code path.** `IsServer` and `IsClient` are properties of the topology rather
than four branches, and `Host` and `Offline` are both:

| | `IsServer` | `IsClient` |
|---|---|---|
| `StartServer` | ✅ | |
| `StartClient` | | ✅ |
| `StartHost` | ✅ | ✅ |
| `StartOffline` | ✅ | ✅ |

So a game that asks `IsServer` before running authority code and `IsClient` before predicting gets
single player, listen server and dedicated server out of the same lines.

**A relay that never learns what a connection is.** `Samples/10-VoiceChat` is a whole application over
this type: an `ISessionMessageHandler` whose `OnMessage` receives a payload from a `PlayerId`, then
walks `Players` and calls `SendToPlayer` for everybody except the talker — which is a rule about
players, expressible because this layer has already turned connections into them. No connection ids, no
handshake handling, no reconnect logic anywhere in it.

## See also

* [Round trip and jitter](round-trip-and-jitter.md) — what the ping feeds, and everything downstream of it
* [Measuring packet loss](measuring-loss.md) — the other half of "how is this link", and why it lives on the transport
* [Networked players](networked-players.md) — giving a `PlayerId` a body
* [The network panel](../editor/network-panel.md) — a session's bandwidth, link and last snapshot, in the editor
* [`NetworkPlayer`](/docs/api/vixen.net.sessions/networkplayer) — who is in the list, and what `IsConnected` false means
* [`TickManager`](/docs/api/vixen.net.time/tickmanager) — the clock `Update` advances
* [`ITransport`](/docs/api/vixen.net.transport/itransport) — what a session runs on
