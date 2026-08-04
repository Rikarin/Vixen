# Vixen.Live.Placement.Process

Realms as child processes. ADR-019's third backend — the one that always answers, and the one that
makes the rest of the design an ordinary unit test.

Spec: [docs/plan/27-mmo-framework.md](../../docs/plan/27-mmo-framework.md) § ADR-019, § Testing.

## Using it

```csharp
using var placement = new ProcessPlacement(new ProcessPlacementOptions {
    Executable = "dotnet",
    Arguments  = ["MyGame.Realm.dll"],
    Ports      = new PortPool(7800, 7899),
    Host       = "127.0.0.1"
});

var instance = await placement.StartAsync(new RealmSpec {
    Shard    = ShardId.New(),
    Key      = new("maps/queensdale", "eu", version),
    Capacity = new(SoftCap: 100, HardCap: 120)
}, cancellation);                                    // endpoint left unbound: the pool chooses

await foreach (var change in placement.WatchAsync(cancellation)) {
    // Started → Ready → Stopped, or Lost.
}

await placement.StopAsync(instance.Id, StopMode.Drain, cancellation);
```

## Three decisions worth knowing about

**The port is chosen before the process exists.** A client is told where to go by *placement*, not by
the realm, so a realm that bound port zero and reported back would leave a window in which the
orchestrator holds a shard it cannot address. `PortPool` allocates, the spec carries the answer, and
the realm is told. The pool is round-robin rather than lowest-free: a realm that has just stopped may
still have datagrams in flight toward it, and its successor on the same port would receive them.

**The lifecycle channel is the process's own stdio.** `RealmSignals` is the whole vocabulary — the
realm writes `vixen-realm ready <endpoint>`, the launcher writes `vixen-realm drain`. It is not a
management API and must not become one; the moment stdio carries request-response, somebody has
written an RPC layer with no framing, no versioning and no authentication. What replaces it is L1's
grain calls, not a bigger version of this.

**An exit nobody asked for is `Lost`, not `Stopped`.** Doc 27 § Health makes recovery a *placement*
rather than a resurrection, and the two events are what tell those apart downstream. A shard whose
last player left and which exited zero is `Stopped`; a crash is `Lost`.

## Why `IRealmProcessHost` exists

Doc 27 § Testing wants randomised kill/restart/partition sequences asserting that no shard is left in
a state with no owner. A test cannot ask an operating system to kill a process at an exact moment, and
a test that starts eight real ones is a test nobody runs on every push. So the process is a seam:
`SystemProcessHost` is the production path, and the test project's `FakeProcessHost` starts nothing.
Everything above the seam — pool, lifecycle, events, reconciliation — is the same code either way.

## What it deliberately does not do

Survive its launcher. `Dispose` kills everything it started, because a launcher that exited leaving
eight realms holding UDP ports is the thing that makes a developer reboot. A deployment that wants
realms to outlive the process that placed them wants the Kubernetes backend, where an owner reference
says so explicitly and the garbage collector honours it.

## See also

- [`Vixen.Live.Abstractions`](../Vixen.Live.Abstractions/README.md) — `IRealmPlacement`, `RealmSpec`,
  `RealmSignals`.
- [`Vixen.Live.Realm`](../Vixen.Live.Realm/README.md) — the other end of every one of these signals.
