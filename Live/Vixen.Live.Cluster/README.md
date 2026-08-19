# Vixen.Live.Cluster

The grain interfaces, the records they exchange, and nothing else. **The one `Live/` assembly a game
client must never reference.**

Spec: [docs/plan/27-mmo-framework.md](../../docs/plan/27-mmo-framework.md) § ADR-016, § ADR-017,
§ Grains.

## Why it is its own assembly

ADR-017 says the client never speaks Orleans, for three reasons any one of which is sufficient: a
cluster client is a *peer* of the cluster, so handing one to an untrusted machine makes every grain
interface a public API reachable by an attacker; Orleans is a multi-megabyte trim-hostile server
framework and an iOS client is NativeAOT; and grain interfaces change with the orchestrator, so a
client holding them makes every orchestrator change a client update.

Making that mechanical rather than a rule somebody remembers is what this project is for: **the client
physically cannot reach a grain, because the types are not in an assembly it references.**
[`Build.ArchitectureRules.cs`](../../build/Build.ArchitectureRules.cs) enforces both halves — no
project outside `Live/` may reference an Orleans package, and neither may
`Vixen.Live.Abstractions`.

## The surrogates, and why they are worth their file

`Vixen.Live.Abstractions` is the assembly a client *does* transitively reference, so it cannot carry
`[GenerateSerializer]`. Orleans's answer to exactly that problem is a surrogate — a shadow struct it
knows how to write, plus a converter — and `Surrogates.cs` holds one per vocabulary type.

The alternative is a second `ShardId` declared with Orleans attributes: two types that mean the same
thing and drift, which is the failure the three-assembly split exists to prevent.

⚠ **A type added to the vocabulary and not to `Surrogates.cs` fails at the first grain call that
carries it, not at compile time.** `ClusterSerializationTests` round-trips every one of them through
a real Orleans serializer for that reason — and it is what caught `default(RealmEndpoint)` and
`new RealmEndpoint("", 0)` comparing unequal, which was a latent bug anywhere either was a dictionary
key.

## The eight grains

| | |
|---|---|
| `IMapGrain` | one map's shards: placement, and the spawn/merge heuristics. Keyed by map, region and version |
| `IShardGrain` | one realm process's life. `Requested → Starting → Ready → Draining → Stopping → Stopped` |
| `IPlayerGrain` | the lease — ADR-021, and the reason item duplication is not expressible |
| `IAccountGrain` | one **account's** collection — a grain doc 27 § Grains does not have, and G8 is what showed was missing |
| `IGuildGrain` | one guild's roster and ranks — declared now that G4 has built the feature it is a contract for |
| `IInstanceGrain` | one saved instance: its roster, its fleet-wide lockout, and what is dead in it |
| `IQueueGrain` | one queue's tickets and the matches it has formed — doc 28's "grain-held record" |
| `IFleetGrain` | a region's register, the rollout target, and where a stuck drain escalates to |

Doc 27 § Grains lists eight, and the one that is still not here is `IPartyGrain`: it is not needed for
placement, because the map keeps its occupants' party ids and counting is therefore local.
`IGuildGrain`, `IQueueGrain` and `IInstanceGrain` were absent for the same reason for a while — they
belong to features in [doc 28](../../docs/plan/28-gameplay-framework.md) rather than to the substrate,
and declaring an interface nobody implements is a promise rather than a contract — so each was
declared here only once doc 28 had built the feature behind it. All three are implemented in
[`Vixen.Live.Orchestrator`](../Vixen.Live.Orchestrator/README.md) now, and read by
`Vixen.Live.Gameplay`'s `SocialBridge` and `LockoutBridge`.

## Two shapes worth knowing about

**A heartbeat's *reply* is how a realm learns it should be draining.** `IShardGrain.Heartbeat` returns
the shard's state, so the control plane never needs to call *into* a realm — a whole direction of
connectivity, authentication and firewall rules that does not have to exist.

**`PlaceStatus.Starting` is an answer, not an error.** A client told "starting" shows a short wait; a
client told "refused" shows a failure. Conflating them is how an elastic fleet's ordinary behaviour
becomes a support ticket.

## See also

- [`Vixen.Live.Orchestrator`](../Vixen.Live.Orchestrator/README.md) — what implements these.
- [`Vixen.Live.Abstractions`](../Vixen.Live.Abstractions/README.md) — the vocabulary they carry.
