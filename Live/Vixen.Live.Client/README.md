# Vixen.Live.Client

The client's half of the service plane: the four calls a game makes to a gate, and the WebSocket it
keeps open.

Spec: [docs/plan/27-mmo-framework.md](../../docs/plan/27-mmo-framework.md) § The three planes, ADR-017.

## Using it

```csharp
var gate = new GateClient(new HttpClient { BaseAddress = new("https://gate.example/v1/") });

var catalog = await gate.CatalogAsync(cancellation);          // before signing in — a launcher can ask too
await gate.SignInAsync("steam", sessionTicket, cancellation); // the token is held, never written to disk
var characters = await gate.CharactersAsync(cancellation);

var play = await gate.EnterAsync(
    new PlayRequest(characters.Value!.Characters[0].Character, "maps/queensdale", catalog.Value!.Version, "en-GB", default, default),
    attempts: 5,
    cancellation
);
```

and the socket, which reconnects by itself:

```csharp
await using var stream = new GateConnection(new("wss://gate.example/v1/stream"), gate);

await foreach (var message in stream.ListenAsync(cancellation)) {
    switch (message.Kind) {
        case "catalog":  await assets.UpdateAsync(); break;   // a push is a hint to go and ask
        case "draining": await Replace(await gate.PlayAsync(…)); break;
    }
}
```

## Nothing here throws for a refusal

A gate saying *"that name is taken"* or *"fetch the update"* is an ordinary answer. Turning it into an
exception makes the happy path the only path anybody writes, and every one of those answers is a
screen a real game has to draw.

⚠ **`GateOutcome.Unreachable` is separate from a refusal, because the two want different pixels.**
"The gate said no" is a sentence to show the player; "the gate did not answer" is a spinner and a
retry. A client that showed the first for the second sends people to a support forum over dropped
Wi-Fi. Even `HttpRequestException` is caught and reported this way — on a phone, the network going
away is not exceptional.

A non-2xx answer that is *not* a `GateProblem` is reported as `unexplained`: a gate always explains
itself, so anything else on these routes is an intermediary — a proxy, a load balancer, a captive
portal — and saying so is more use than repeating its HTML.

## `EnterAsync` waits for `Starting` and does not act on `UpdateRequired`

The asymmetry is the point. **A shard coming up needs nothing from the game but patience**, and the
wait is the gate's own `RetryAfter` — how long a shard takes is a property of the fleet, and a client
that guessed would either hammer it or feel slow.

**Fetching a catalog is not patience.** It is the game's own asset system doing work it must decide to
do, on a connection the player may be paying for. A helper that quietly downloaded a gigabyte would be
a helper nobody could trust, so `UpdateRequired` is handed straight back.

## The socket is allowed to be down

Doc 27 is explicit that nothing a player is waiting on travels here, so a client that showed a modal
when it dropped would be showing a modal for a lost whisper. `GateConnection` reconnects with backoff,
forever, and says nothing about it. `ListenAsync` therefore **never completes on its own** — it ends
when the caller cancels, because a socket closing is a reconnect rather than an end.

⚠ **Nothing is replayed across a reconnect, and nothing needs to be.** A push is a hint to go and ask:
`catalog` means fetch the catalog, `draining` means ask the gate where to play. A design that queued
missed events would be one where the queue's depth eventually matters.

An unreadable frame is skipped, so a newer gate saying something newer is not a client update.

## AOT and trim clean, and that is checked

The second project in `Live/` to turn the analyzers back on — the other is `Vixen.Live.Abstractions` —
because this one is linked into the game client and a game client is an iOS NativeAOT binary. It
references neither Orleans nor ASP.NET hosting (ADR-017), and the only things in it are `HttpClient`,
`ClientWebSocket` and a source-generated JSON context.

`IGateSocket` is a seam so a test needs no server, and so a platform whose WebSocket is not
`ClientWebSocket` — a console SDK, a browser build — can supply its own.

## See also

- [docs/guide/live/the-service-plane](../../docs/guide/live/the-service-plane.md) — the written half,
  and the gate's side of every one of these calls.
- [`Vixen.Live.Gate`](../Vixen.Live.Gate/README.md) — what answers them.
