# Vixen.Live.Realm.Cluster

The realm's half of the control plane: an Orleans client, and the wiring that turns a shard's
lifecycle into grain calls **posted through `RealmDirectory` and never awaited on a frame path**.

Spec: [docs/plan/27-mmo-framework.md](../../docs/plan/27-mmo-framework.md) § ADR-016, § ADR-018.

## Using it

```csharp
public sealed class QueensdaleRealm : Vixen.Live.Realms.Realm {
    RealmCluster? cluster;

    protected override void OnRealmInitialise() {
        var client = new HostBuilder()                       // your own, however you configure it
            .UseOrleansClient(orleans => orleans.UseLocalhostClustering())
            .Build();

        cluster = new RealmCluster(Host, new ClusterGrains(client.Services.GetRequiredService<IClusterClient>()));
    }

    protected override void OnRealmUpdate(GameTime time) => cluster?.Update(time.UnscaledElapsed);
}
```

That is the whole of it. Everything else happens by subscription: the shard reports ready, heartbeats
carry population and tick p99, admitted players have their lease taken, and released players give it
back.

## Why it is a project of its own

Doc 27's layout does not list one, and the reason to add it is the one `Vixen.Net.Telemetry` was split
out for — *"so an offline game links no protobuf serializer"* — applied a tier up.

Doc 27 § Cost's **L0 is "a dedicated server with a lifecycle"**, and such a realm has no orchestrator
to talk to. Folding this into `Vixen.Live.Realm` would put a cluster framework into every realm binary
that ships, including the ones that never join a cluster — and § The scene-management join names shard
start-up time as the thing that makes elastic scaling possible.

A realm that *is* orchestrated references this and pays for it, which is ADR-018's design rather than a
concession.

## Three things that are load-bearing

**Nothing here awaits.** Doc 27 M1 names a grain call reaching the frame path as the single way the
design fails — *"it will not look like a bug, it will look like occasional stutter"*. Every call goes
through `RealmDirectory.Ask`: the realm posts, keeps simulating, and applies the answer on its own
thread at a defined point in a later frame. `RealmClusterTests` asserts it directly — twenty frames
against a cluster answering in 250 ms still take under 200 ms in total.

**The realm learns what to do from replies it was already collecting.** Draining arrives in the answer
to a heartbeat; a lost lease arrives in the answer to a renewal. **Nothing in the cluster calls into a
realm**, which means a realm needs no inbound port, no inbound authentication and no firewall rule
beyond the one its players use.

**Losing a lease is survivable, and not noticing is not.** ADR-021: a realm that has been superseded
keeps simulating and stops writing durable state until the lease returns or a transfer hands the
buffered mutations to the new holder. `LeasesLost` is counted rather than only logged — a realm losing
leases it did not give up is either a transfer storm or a cluster that thinks the shard is dead, and
both look like "players cannot pick anything up" from the inside.

## Why `IRealmGrains` exists

An `IClusterClient` cannot be stood up without a cluster, so a test of *"does a shard drain when told
to"* would need a silo. Behind that seam it needs four fakes returning `Task`s — and the fakes wrap the
**real** `ShardLifecycle` and `PlayerLeaseState`, because a fake that answered differently from the
orchestrator would be a test of nothing. That is the whole reason those state machines are plain
classes with the grains as adapters.

## See also

- [`Vixen.Live.Cluster`](../Vixen.Live.Cluster/README.md) — the grains this calls.
- [`Vixen.Live.Realm`](../Vixen.Live.Realm/README.md) — the shard this wires up.
- [docs/guide/live/the-cluster](../../docs/guide/live/the-cluster.md) — the written half.
