# Vixen.Live.Realm

A shard, as the process carrying it sees itself: a normal Vixen application built as a dedicated
server, plus the six pieces that make it a realm.

Spec: [docs/plan/27-mmo-framework.md](../../docs/plan/27-mmo-framework.md) § The realm.

## The one line

```csharp
// Program.cs
return RealmApp.Run<QueensdaleRealm>(args);
```

```csharp
public sealed class QueensdaleRealm : Realm {
    protected override void OnRealmInitialise() {
        // Host.Session is doc 16's server. Replication, RPC and interest are wired here, exactly as
        // they would be in a listen server — a realm is not a different kind of server.
    }

    protected override TransferReadiness ReadinessOf(RealmPlayer player) =>
        player.IsInBossFight ? TransferReadiness.Blocked : TransferReadiness.Ready;
}
```

The process is launched with one argument — `--realm-spec shard=…;map=…;port=…` — which
`Vixen.Live.Placement.Process` (or the Docker or Kubernetes backend) writes and `RealmSpec.TryRead`
reads. A process handed no spec says so on standard error and exits `2`, which is distinguishable
from a crash and not worth a launcher retrying.

## What is in it

| | |
|---|---|
| `Realm` | the `Game` a shard is. Seals four host hooks and offers realm-shaped ones in their place |
| `RealmApp` | reads the spec, builds the application, starts the signal reader, runs |
| `RealmHost` | the state machine: `Starting → Ready → Draining → Stopping → Stopped` |
| `RealmDirectory` | **the only place a control plane is ever called** |
| `PlayerAdmission` | the door — a ticket, a capacity check, and the two identities joined |
| `MapLifetime` | is the map up, and taking it down again |
| `RealmHeartbeat` | the two-second sample, and the p99 the fleet is watched by |

## Four decisions worth knowing about

**Orleans is asked, not awaited — and that is a threading rule, not an Orleans rule.** A grain call is
a network round trip with a scheduler in front of it; a frame that awaits one has a p99 in
milliseconds and a p99.9 in seconds. `RealmDirectory.Ask` posts, the realm keeps simulating, and
`Drain` applies the answer on the realm's thread at a defined point in a later frame. L0 has no
orchestrator and the class is still the right shape, because what it enforces is where the callback
runs. This is `ISessionAuthenticator`'s pattern with a bigger surface, for the reason doc 16 already
recorded.

**The map is `AppConfig.StartupScene`, not a loader of its own.** `Realm.OnConfigure` points the host
at `RealmSpec.Key.Map` and lets it do what it already does — including surviving a map that will not
open. `MapLifetime` answers the one question the host does not: *is it up yet*, which is what
separates `Starting` from `Ready`. Doc 27 § The scene-management join is otherwise entirely made of
pieces that already existed: `NetworkSceneId` is the hash of the scene's *name*, so a client that has
loaded the map already agrees with the realm about what the props are before a packet arrives.

**Admission is an HMAC, not a round trip.** `PlayerAdmission` is an `ISessionAuthenticator` that never
answers `Pending`: the ticket is self-contained and the cluster key is already in the process. That is
the property ADR-020 was designed for — a transfer's second session opens in the time it takes to hash
a hundred bytes, which is what lets it overlap with the player still playing on the first.

**Nothing is force-disconnected by draining.** `Drain` stops arrivals and quiesces the map; the players
already on it leave at moments `ReadinessOf` approved of, and the shard stops once it has been empty
for the idle grace. The grace is not zero: a player who was moved out may have a reconnect in flight,
and a shard that vanished the instant its population hit zero would turn a lost packet into a lost
session.

## The stdio lifecycle, and what replaces it

L0 has no orchestrator, and a realm still needs a control plane. The smallest one that is not a lie is
the process's own standard streams — `RealmSignals`: the realm writes `vixen-realm ready <endpoint>`,
the launcher writes `vixen-realm drain`. Every one of ADR-019's three backends can already read and
write them.

It is a **lifecycle channel and must not become a management API**. Nothing player-specific, nothing
per-tick, nothing that needs an answer. What replaces it in L1 is grain calls through
`RealmDirectory`, not a bigger version of this.

## What is owed

The transfer protocol (doc 27 § Transfer, milestone L2) — tickets are minted and checked here, and
nothing yet hands one out. `PlayerAdmission` records the lease epoch a ticket named and no lease is
acquired against it, because there is no `IPlayerGrain` to acquire one from until L1.

## See also

- [`Vixen.Live.Abstractions`](../Vixen.Live.Abstractions/README.md) — the spec, the ticket, the signals.
- [`Vixen.Live.Placement.Process`](../Vixen.Live.Placement.Process/README.md) — what launches this.
- [doc 17](../../docs/plan/17-app-heads-and-shipping.md) § Build variants — what `BuildVariant.Server`
  turns off.
