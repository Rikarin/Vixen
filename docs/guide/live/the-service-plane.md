---
title: The service plane
slug: live/the-service-plane
kind: concept
area: Live
summary: The gate — sign in, characters, the catalog check, and the one call that turns "I want to play" into an endpoint and a signed ticket.
api: [T:Vixen.Live.Gate.GateService, T:Vixen.Live.Gate.GateEndpoints, T:Vixen.Live.Gate.GateOptions, T:Vixen.Live.Gate.GateHost, T:Vixen.Live.Gate.GateLog, T:Vixen.Live.Gate.GateToken, T:Vixen.Live.Gate.GateTokenSigner, T:Vixen.Live.Gate.TokenStatus, T:Vixen.Live.Gate.GateAnswer`1, T:Vixen.Live.Gate.IAccountAuthority, T:Vixen.Live.Gate.AuthorityResult, T:Vixen.Live.Gate.DevelopmentAuthority, T:Vixen.Live.Gate.IFleetDirectory, T:Vixen.Live.Gate.ClusterFleetDirectory, T:Vixen.Live.Gate.ServicePlane, T:Vixen.Live.Gate.IGateSubscriber, T:Vixen.Live.Gate.WebSocketSubscriber, T:Vixen.Live.SignInRequest, T:Vixen.Live.SignInResponse, T:Vixen.Live.CharacterSummary, T:Vixen.Live.CharacterList, T:Vixen.Live.CreateCharacterRequest, T:Vixen.Live.CatalogResponse, T:Vixen.Live.PlayRequest, T:Vixen.Live.PlayResponse, T:Vixen.Live.PlayStatus, T:Vixen.Live.GateProblem, T:Vixen.Live.GateEvent, T:Vixen.Live.GateJson, T:Vixen.Live.RealmVersionJsonConverter, T:Vixen.Live.Client.GateClient, T:Vixen.Live.Client.GateOutcome`1, T:Vixen.Live.Client.GateConnection, T:Vixen.Live.Client.IGateSocket, T:Vixen.Live.Client.WebSocketGateSocket]
tags: [live, mmo, gate, http, websocket]
since: 0.1
status: preview
related: [live/durable-state, live/transfer-tickets, live/placing-players]
---

## What it is

One of the three planes doc 27 separates. The client holds **two** connections in steady state: a UDP
session to the realm simulating it, and an HTTPS/WSS connection to the gate. The gate is the second
one.

It does five things: says what the fleet is running, turns a credential into a session, lists and
makes characters, and — the one the milestone exists for — answers *"put me somewhere"* with an
endpoint and a signed ticket.

Both halves are here. `Vixen.Live.Gate` is the ASP.NET side; `Vixen.Live.Client` is the typed client
side, and it is the only assembly of this milestone a game client links (ADR-017).

## What it is for

**Nothing on this plane is on a frame path.** A hundred milliseconds is fine here, which is exactly
why the things that need it live here rather than on the realm: login, the character list, the
catalog, and anything whose recipient may be on another realm, offline, or on another continent.

The routing question doc 27 answers at length is *why there is no gateway on the hot path*. This is
the other half of that answer: real-time traffic goes straight from the client to its realm, and
everything that does not need to be fast comes here instead. What the client learns from the gate is
*an endpoint and a ticket*, and from then on the packet path is doc 16's.

## Using it

```csharp no-compile="the cluster client, the database and the authority are the deployment's"
builder.Services.AddVixenGate(gate => {
    gate.Version = new("0.1.0", catalog.BuildHash);
    gate.Content = "https://content.example/catalog";
    gate.Region  = "eu";
    gate.Maps.Add("maps/queensdale");
});

app.UseWebSockets();
app.MapVixenGate();
```

⚠ **`AddVixenGate` deliberately registers almost nothing.** `IPersistence`, `IFleetDirectory`, the two
signers and every `IAccountAuthority` are the caller's, because each is a decision only a deployment
can make: which database, which cluster, where the secrets live, and who is allowed to say who
somebody is. A gate assembled with none of them fails at construction rather than at the first
sign-in.

## Examples

**The client's whole sequence**, in four calls. `Vixen.Live.Client` is the typed half, and it is the
one assembly of this milestone a game client links:

```csharp no-compile="the credential comes from whatever authority the deployment configured"
var gate = new GateClient(new HttpClient { BaseAddress = new("https://gate.example/v1/") });

var catalog = await gate.CatalogAsync(cancellation);            // before signing in: a launcher can ask too
await gate.SignInAsync("steam", sessionTicket, cancellation);   // the token is held, never written to disk
var characters = await gate.CharactersAsync(cancellation);

var play = await gate.EnterAsync(
    new PlayRequest(characters.Value!.Characters[0].Character, "maps/queensdale", catalog.Value!.Version, "en-GB", default, default),
    attempts: 5,
    cancellation
);
```

⚠ **Nothing on `GateClient` throws for a refusal**, and `GateOutcome.Unreachable` is separate from
one: *"the gate said no"* is a sentence to show and *"the gate did not answer"* is a spinner and a
retry, and a client that showed the first for the second sends people to a support forum over dropped
Wi-Fi.

⚠ **`EnterAsync` waits out `Starting` and hands `UpdateRequired` straight back.** A shard coming up
needs nothing from the game but patience; fetching a catalog is the asset system doing work it must
decide to do, on a connection the player may be paying for.

**And then the four answers it has to be able to render:**

```csharp no-compile="continues the snippet above"
switch (play.Value!.Status) {
    case PlayStatus.Placed:
        await session.ConnectAsync(play.Value.Endpoint, play.Value.Ticket);  // doc 16's handshake, carrying the ticket
        break;
    case PlayStatus.Starting:
        await Task.Delay(play.Value.RetryAfter);                             // a wait, NOT a failure
        goto retry;
    case PlayStatus.UpdateRequired:
        await assets.UpdateAsync(catalog.Value.Content);                     // ADR-022's routing decision
        goto retry;
    case PlayStatus.Refused:
        Show(play.Value.Reason);                                             // the map's own sentence
        break;
}
```

⚠ **`Starting` is not an error and `UpdateRequired` is not a rejection.** A client that renders
`Starting` as a failure turns an elastic fleet's ordinary behaviour into a support ticket; one that
renders `UpdateRequired` as a failure turns a rolling upgrade back into a maintenance window. Both are
the two easiest cases to get wrong, and both are why the enum has four values instead of two.

**Signing in without an identity provider**, for a laptop and for tests:

```csharp no-compile="never register this outside development"
builder.Services.AddSingleton<IAccountAuthority, DevelopmentAuthority>();
```

⚠ **`DevelopmentAuthority` trusts whatever it is told** — the credential *is* the handle, so anyone
who can reach the gate can sign in as anybody. It is not registered by default, and that is the
point: a gate with no authority refuses every sign-in, which is loud, rather than accepting every
sign-in, which is not.

## The socket

`ServicePlane` is where the gate pushes: a catalog that has been published, a shard about to drain,
guild and whisper chat, a party invite.

```csharp no-compile="what a live-ops action does after publishing a catalog"
await plane.TellEveryoneAsync(new("catalog", version.ToString(), DateTimeOffset.UtcNow));
```

The client's side is `GateConnection`, which reconnects by itself and says nothing about it:

```csharp no-compile="a push is a hint to go and ask"
await using var stream = new GateConnection(new("wss://gate.example/v1/stream"), gate);

await foreach (var message in stream.ListenAsync(cancellation)) {
    switch (message.Kind) {
        case "catalog":  await assets.UpdateAsync(); break;
        case "draining": await Replace(await gate.PlayAsync(request, cancellation)); break;
    }
}
```

⚠ **`ListenAsync` never completes on its own** — it ends when the caller cancels. A socket closing is
a reconnect rather than an end, so a loop that stopped when the enumeration did would stop the first
time a train went into a tunnel. Nothing is replayed across a reconnect and nothing needs to be, and
an unreadable frame is skipped so that a newer gate saying something newer is not a client update.

⚠ **This socket is allowed to be down and every message on it is allowed to be lost.** Nothing a
player is waiting on travels here — that is the data plane — and anything that would be wrong to lose
is a request the client makes rather than a push it receives. **A push is a hint to go and ask.**
Anything a client sends is treated as a ping, because a socket that also carried commands would need
its own authorisation, rate limiting and closed-set deserialization: the whole security surface doc 16
built once already.

It is authenticated by the `Authorization` header and never by a query string — a token in a URL is
written to every access log and proxy cache between the gate and the player.

## Two token types, and why they are not one

|  | Admits | Checked by | Lives for |
|---|---|---|---|
| `GateToken` | one **account** to the gate | the gate | hours |
| [`TransferTicket`](transfer-tickets.md) | one **character** to one shard | a realm | a minute or two |

Sharing a key would let a realm mint gate sessions. Making them one type would let a realm be handed
something that authorises reading an account's character list. They are separate for the same reason
ADR-017 splits the assemblies.

⚠ **A `GateToken` is stateless, so it cannot be revoked before it expires** — its lifetime is the whole
of its bound, which is why it is hours rather than weeks. Suspension is checked against the *account*
on every request that matters, so a banned account stops being able to play at once even though its
token still parses.

## See also

- [Durable state and the ledger](durable-state.md) — the accounts and characters behind it.
- [Transfer tickets](transfer-tickets.md) — what `POST /v1/play` mints, and what a realm does with it.
- [Placing players](placing-players.md) — the score that decides which shard the answer names.
- [docs/plan/27](https://github.com/Rikarin/Vixen/blob/master/docs/plan/27-mmo-framework.md)
  § The routing question — why there is no gateway on the hot path.
