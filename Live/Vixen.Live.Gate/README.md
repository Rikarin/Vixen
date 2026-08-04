# Vixen.Live.Gate

The service plane. Sign in, characters, the catalog a client checks before it plays, and the one call
that turns *"I want to play"* into **an endpoint and a signed ticket**.

Spec: [docs/plan/27-mmo-framework.md](../../docs/plan/27-mmo-framework.md) § The three planes, ADR-022.

## Using it

```csharp
builder.Services.AddVixenGate(gate => {
    gate.Version = new("0.1.0", catalog.BuildHash);       // ADR-022, and it filters before the cluster is asked
    gate.Content = "https://content.example/catalog";
    gate.Region  = "eu";
    gate.Maps.Add("maps/queensdale");
});

// The four things the engine deliberately does not choose for you.
builder.Services.AddSingleton<IPersistence>(_ => new SqlPersistence(NpgsqlDataSource.Create(…), true));
builder.Services.AddSingleton<IFleetDirectory>(services => new ClusterFleetDirectory(services.GetRequiredService<IClusterClient>()));
builder.Services.AddSingleton(new TransferTicketSigner(clusterKey));   // realms hold this one too
builder.Services.AddSingleton(new GateTokenSigner(gateKey));           // and this one they must not
builder.Services.AddSingleton<IAccountAuthority, MyStudioAuthority>();

app.UseWebSockets();
app.MapVixenGate();
```

## The route the whole milestone exists for

`POST /v1/play` — and the **order of its checks is the design**:

1. **Content version**, because *"fetch the update"* is a different conversation from *"no"*, and only
   the gate knows enough to have it. Placement would refuse a mismatched client anyway (ADR-022's
   filter); answering `UpdateRequired` instead is the difference between a rolling upgrade and a
   maintenance window.
2. **The map is one this gate serves.** A map address arrives from a client and `IMapGrain` is keyed
   by it, so an unfiltered gate lets a stranger create a fleet for whatever they type.
3. **Ownership before existence**, so probing character ids tells a stranger nothing.
4. **Suspension**, so a banned account never costs the cluster a grain call.
5. **Placement**, and only then the ticket.

⚠ **A ticket is minted only for `Placed`.** A shard that is still starting has no endpoint, and a
ticket naming one that does not answer is a client retrying against a socket instead of asking again.

⚠ **The gate predicts the lease epoch; it does not take the lease.** Acquiring is the receiving
realm's call — a gate that acquired would take the lease off whoever holds it for everybody who merely
opened the character screen. So a ticket that is never redeemed costs nothing, and a stale one is
superseded on arrival.

## Two keys, two token types, and why they are not one

| | Admits | Checked by | Lives for |
|---|---|---|---|
| `GateToken` | one **account** to the gate | the gate | hours |
| `TransferTicket` | one **character** to one shard | a realm | a minute or two |

Sharing a key would mean a realm could mint gate sessions; making them one type would mean a realm
could be handed something that authorises reading an account's character list. They are separate for
the same reason ADR-017 splits the assemblies.

⚠ **`GateToken` is stateless and therefore not revocable before it expires.** That is the trade a
stateless token always makes, and the bound on it is the lifetime. Suspension is checked against the
*account* on every request that matters, so a banned account stops being able to play immediately even
though its token still parses.

## There is no credential store here

`IAccountAuthority` turns whatever your provider understands — a Steam session ticket, an OIDC id
token, an EOS auth token — into a handle. Nothing in the engine reads a credential.

`DevelopmentAuthority` trusts whatever it is told, says so, and **is not registered by default**: a
gate with no authority refuses every sign-in, which is loud, rather than accepting every sign-in,
which is not. Same judgement `RealmHost.DevelopmentSigner` made about a missing cluster key.

## The socket

`ServicePlane` is the client's second connection — one UDP session to its realm, one WSS here. Guild
and whisper chat, party invites, a published catalog, a draining shard.

⚠ **It is allowed to be down, and every message on it is allowed to be lost.** Nothing a player is
waiting on travels here; a push is a hint to go and ask. Anything a client *sends* is treated as a
ping, because a socket that also carried commands would need its own authorisation, rate limiting and
closed-set deserialization — the whole security surface doc 16 built once already.

Authenticated by the `Authorization` header, never a query string: a token in a URL is written to
every access log and proxy cache between here and the player.

## What a test can and cannot say

`GateService` holds every decision and has no ASP.NET in it; `GateEndpoints` reads a header, calls one
method and writes the answer. **If a rule ever appears in the endpoints file, it is in the wrong
file.** `IFleetDirectory` is the same seam one tier down, so the 31 tests here run without a web host
and without a silo.

## See also

- [docs/guide/live/the-service-plane](../../docs/guide/live/the-service-plane.md) — the written half.
- [`Vixen.Live.Persistence`](../Vixen.Live.Persistence/README.md) — the accounts and characters behind it.
- [`Vixen.Live.Cluster`](../Vixen.Live.Cluster/README.md) — `IMapGrain.Place`, which this asks.
