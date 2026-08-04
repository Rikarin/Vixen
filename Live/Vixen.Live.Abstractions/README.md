# Vixen.Live.Abstractions

The vocabulary the three planes share, and the only `Live/` assembly a game client may reference.

Spec: [docs/plan/27-mmo-framework.md](../../docs/plan/27-mmo-framework.md) § Repository layout.

## What is in it

| | |
|---|---|
| `ShardId`, `RealmInstanceId`, `PlayerKey` | who and what, durably |
| `ShardKey`, `RealmVersion`, `ShardCapacity`, `ShardKind`, `ShardState` | what makes two shards interchangeable, and where one is in its life |
| `RealmEndpoint` | where a client opens its session — data, never configuration |
| `RealmSpec` | everything a realm process is told at boot, in one string |
| `TransferTicket`, `TransferTicketSigner`, `TicketStatus` | a player's signed permission to be admitted |
| `IRealmPlacement` and friends | how a realm process comes into existence, whatever runs underneath |
| `RealmSignals` | the four lines a realm and its launcher say over stdio |

## The three things that are load-bearing

**No dependencies at all, and that is the specification.** Doc 27 writes this project's constraint as
"no Orleans, no engine, no ASP.NET. The client may see this." A reference to `Vixen.Net` would be
defensible and is still not taken: a gate, a support tool, a CLI and an orchestrator all read these
types, and none of them wants a replication codec in order to print a shard id. The csproj turns AOT
and trim analysis back *on* — the Live profile turns them off for the rest of the tier — because the
client that transitively references this is an iOS NativeAOT binary.

**`RealmSpec` crosses a process boundary as one argument or one environment variable.** That is what
lets the three placement backends of ADR-019 differ in nothing but how they set a string: a pod
template, a container command, and `Process.Start` all say the same sentence.

```csharp
var spec = new RealmSpec {
    Shard = ShardId.New(),
    Key = new("maps/queensdale", "eu", new("0.1.0", contentHash)),
    Endpoint = new("10.0.0.4", 0),          // unbound: the backend chooses the port
    Capacity = new(SoftCap: 100, HardCap: 120)
};

var arguments = spec.ToCommandLine();       // --realm-spec shard=…;map=…;port=0;…
```

and on the other side of the boundary:

```csharp
if (!RealmSpec.TryRead(args, environment: null, out var spec, out var why)) {
    Console.Error.WriteLine($"This process is not a realm: {why}.");
    return 2;
}
```

The encoding is `key=value;key=value` with three characters escaped, hand-written rather than JSON,
for the reason `KeyValueText` records: the serializer that would make JSON a one-liner is the one
that does not survive trimming, and eleven scalar fields are readable in a process listing — which is
where a spec actually gets debugged.

**A ticket is checked by the realm that receives it, against a key the client has never seen.**
`TransferTicket` is ADR-020's "`NetworkSession`'s reconnect token with a different issuer": the client
is a courier and can neither read anything it did not already know nor forge one.

```csharp
using var signer = new TransferTicketSigner(clusterKey);   // ≥ 32 bytes, or it refuses

var ticket = signer.Sign(new TransferTicket {
    Player = player, Target = shard, Endpoint = endpoint,
    LeaseEpoch = epoch + 1, Expires = now + TimeSpan.FromSeconds(30)
});

// …on the receiving realm, having decoded what the client presented:
var status = signer.Validate(presented, myShard, DateTimeOffset.UtcNow);
```

`Validate` checks signature, then expiry, then shard, in that order, so everything after the first
check is a statement about a ticket this cluster actually issued.

## What is deliberately not here

Placement *scoring* — the megaserver's weights, the spawn and merge hysteresis — is the orchestrator's
(`Vixen.Live.Orchestrator`, L1) and not a contract. This assembly says what a shard *is* so that
several processes can agree; it does not say which one a player should be sent to, because that answer
is a `.vxplacement` asset a game authors rather than a constant an engine ships.

## See also

- [`Vixen.Live.Placement.Process`](../Vixen.Live.Placement.Process/README.md) — the backend that makes
  a fleet an ordinary unit test.
- [`Vixen.Live.Realm`](../Vixen.Live.Realm/README.md) — what reads a `RealmSpec` and becomes a shard.
- [docs/guide/live](../../docs/guide/live) — the written half.
