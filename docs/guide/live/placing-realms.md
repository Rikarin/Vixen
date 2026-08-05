---
title: Placing realms
slug: live/placing-realms
kind: guide
area: Live
summary: Starting, watching and stopping realm processes — and the backend that makes a fleet an ordinary unit test.
api: [T:Vixen.Live.IRealmPlacement, T:Vixen.Live.PlacementProbe, T:Vixen.Live.RealmInstance, T:Vixen.Live.StopMode, T:Vixen.Live.PlacementEvent, T:Vixen.Live.PlacementEventKind, T:Vixen.Live.RealmSignals, T:Vixen.Live.Placement.ProcessPlacement, T:Vixen.Live.Placement.ProcessPlacementOptions, T:Vixen.Live.Placement.IRealmProcessHost, T:Vixen.Live.Placement.IRealmProcessHandle, T:Vixen.Live.Placement.RealmProcessRequest, T:Vixen.Live.Placement.SystemProcessHost, T:Vixen.Live.PortPool, T:Vixen.Live.Placement.DockerPlacement, T:Vixen.Live.Placement.DockerPlacementOptions, T:Vixen.Live.Placement.DockerEngine, T:Vixen.Live.Placement.DockerPing, T:Vixen.Live.Placement.DockerContainer, T:Vixen.Live.Placement.DockerCreate, T:Vixen.Live.Placement.KubernetesPlacement, T:Vixen.Live.Placement.KubernetesPlacementOptions, T:Vixen.Live.Placement.IClusterApi, T:Vixen.Live.Placement.ClusterApi, T:Vixen.Live.Placement.PodIdentity]
tags: [live, mmo, placement, process, testing]
since: 0.1
status: preview
related: [live/shards-and-specs, live/writing-a-realm]
---

## What it is

`IRealmPlacement` is how a realm process comes into existence, whatever is running underneath:
Kubernetes, Docker, or `Process.Start`. Five methods — probe, start, stop, list, watch — and nothing
above the interface knows which backend answered.

`ProcessPlacement` is the third of those backends, and the one that always answers.

## What it is for

The interface is small because everything above it reasons about *shards*, and the only thing it
needs from the world below is that a process with a given `RealmSpec` exists, can be told to stop, and
can be watched. Probing in order and using the first backend that answers is what keeps the design
from being tied to one deployment target: Docker and a bare process have to work as well as a cluster.

`ProcessPlacement` in particular is what makes the rest of the fleet testable. It is to
[doc 27](https://github.com/Rikarin/Vixen/blob/master/docs/plan/27-mmo-framework.md) what `Vixen.Net.Transport.Local` is to doc 16: placement
scoring, spawn and merge hysteresis, drain and rolling upgrades all become ordinary unit tests against
a fleet that starts in milliseconds.

## Using it

```csharp no-compile="a fleet's launcher — the orchestrator that drives one is milestone L1"
using var placement = new ProcessPlacement(new ProcessPlacementOptions {
    Executable = "dotnet",
    Arguments  = ["MyGame.Realm.dll"],
    Ports      = new PortPool(7800, 7899),
    Host       = "127.0.0.1"
});

var probe = await placement.ProbeAsync(cancellation);       // always available; says what it would launch
var instance = await placement.StartAsync(spec, cancellation);

await foreach (var change in placement.WatchAsync(cancellation)) {
    // Started → Ready → Stopped, or Lost.
}
```

⚠ **`StartAsync` returning is `Starting`, not `Ready`.** The shard becomes a placement candidate when
the realm itself says it has loaded its map, which arrives as a `PlacementEventKind.Ready`. A backend
that blocked until then would make a slow map load look like a failed start.

⚠ **An exit nobody asked for is `Lost`, not `Stopped`.** A shard whose last player left and which
exited zero ended the way it was supposed to. A crash did not, and recovery from it is a *placement*
rather than a resurrection — the shard is gone and its volatile state with it.

### Stopping

```csharp no-compile="both halves of a stop, shown together"
await placement.StopAsync(instance.Id, StopMode.Drain, cancellation);      // minutes, and that is right
await placement.StopAsync(instance.Id, StopMode.Immediate, cancellation);  // seconds
```

`Drain` is patient by default — fifteen minutes, matching doc 27's hard deadline — because a raid
finishing is what draining politely means. A launcher whose patience is shorter than the readiness
rules it is waiting on turns every drain into a kill.

Stopping something that is already gone is not an error. Every backend races with the process it
manages, and making callers tell "it was not there" from "it would not stop" would have them write the
same retry loop three times.

## Examples

### The port is chosen before the process exists

A client is told where to go by *placement*, not by the realm — so a realm that bound port zero and
reported back would leave a window in which the orchestrator holds a shard it cannot address.
`PortPool` allocates, the spec carries the answer, and the realm is told.

```csharp no-compile="the pool a launcher hands to ProcessPlacement"
var ports = new PortPool(7800, 7899);

ports.TryRent(out var port);      // round-robin, not lowest-free
ports.Return(port);               // when the realm stops
```

Round-robin matters: a realm that has just stopped may still have datagrams in flight toward it, and a
new realm on the same port would receive them — which presents as one shard occasionally seeing
packets meant for another.

### The lifecycle channel is stdio

`RealmSignals` is the whole vocabulary a realm and its launcher share. The realm writes
`vixen-realm ready <endpoint>`, the launcher writes `vixen-realm drain`.

```csharp no-compile="both ends of the signal vocabulary, which are in different processes"
Console.Out.WriteLine(RealmSignals.FormatReady(endpoint));    // the realm

RealmSignals.TryReadReady(line, out var endpoint);            // the launcher
RealmSignals.ReadCommand(line);                               // the realm, reading stdin
```

⚠ **It is a lifecycle channel and must not become a management API.** Nothing player-specific,
nothing per-tick, nothing that needs an answer — the moment stdio carries request-response, somebody
has written an RPC layer with no framing, no versioning and no authentication. What replaces it is
grain calls through `RealmDirectory`, not a bigger version of this.

Ordinary logging goes to the same stream, which is why every signal carries the prefix: a realm's own
output cannot be mistaken for one, and a human reading a container's logs can see the lifecycle among
the noise.

### The Docker backend, and the one thing it does differently

```csharp no-compile="the same interface, with a daemon doing the starting"
using var placement = new DockerPlacement(new DockerPlacementOptions {
    Image = "mygame/realm:0.1.0",
    Owner = "queensdale",             // two orchestrators can share one daemon
    Host  = "10.0.0.4",               // what a player can reach; the backend cannot guess it
    Ports = new PortPool(7800, 7899)
});
```

ADR-019 asks for a hand-written Engine API client rather than a package, and the surface really is a
handful of calls over a unix socket. The one piece that is not ordinary HTTP is the **log framing** —
a container without a TTY has stdout and stderr multiplexed behind eight-byte headers — which is what
lets a realm's `vixen-realm ready` line be told from something it wrote to stderr.

⚠ **The realm image supplies the program, and the spec is appended to it.** There is no `Executable`
option here as there is on `ProcessPlacement`, because the image already answers that question:

```dockerfile
ENTRYPOINT ["./YourGame.Realm"]
```

Docker execs the entrypoint with the container's `Cmd` appended, so what runs is
`./YourGame.Realm --realm-spec "shard=…"` — the same string a pod's `args` and `Process.Start` carry,
which is the property `RealmSpec` exists for. An image with **no** entrypoint makes `--realm-spec`
itself the program, and the daemon's answer is `exec: "--realm-spec": executable file not found in
$PATH` from a container that already exists. So the backend asks the image what it runs before it
rents a port: `ProbeAsync` reports a backend whose image would exec a flag as unavailable, and
`StartAsync` refuses. For an image that genuinely cannot carry an entrypoint — a shared runtime with
the realm mounted into it — set `Entrypoint`, which replaces the image's the way Kubernetes' `command`
does.

⚠ **There is no stdin, and there does not need to be.** `Placement.Process` writes `vixen-realm drain`
because L0 has no control plane to say it over. A Docker deployment is an L1 deployment by
construction: it has an orchestrator, and a realm learns to drain from the reply to its own heartbeat.
`StopMode.Drain` here is only the deadline. The corollary is worth knowing: an *unorchestrated* Docker
deployment cannot drain politely, and should use `Placement.Process`.

⚠ **Disposing a `DockerPlacement` leaves the containers running**, which is the opposite of
`ProcessPlacement`. An orphaned child process is holding a UDP port for nobody; a container that
outlives the orchestrator which created it is a shard still serving players, and the labels are how
the next orchestrator finds it.

### The Kubernetes backend, and the two decisions in it

```csharp no-compile="the backend an orchestrator running inside a cluster uses"
using var placement = new KubernetesPlacement(
    new KubernetesPlacementOptions {
        Image     = "mygame/realm:0.1.0",
        Namespace = "queensdale",
        Ports     = new PortPool(30000, 30099),      // the node range your firewall opened
        Self      = PodIdentity.FromEnvironment()    // the downward API
    },
    new ClusterApi(ClusterApi.Connect()!)
);
```

⚠ **A `Pod`, not a `Deployment`.** Realms are not fungible replicas: each has an identity, a map, a
population, a version and a lifetime that ends when the last player leaves. A Deployment's controller
would restart a realm that exited on purpose, and its rolling update is the wrong shape for a drain —
which moves *players*, not pods. An **owner reference** gives the one thing the controller was wanted
for, garbage collection, and is deliberately not a *controller* reference: a realm is owned, not
managed.

⚠ **`hostPort`, not a `Service` per pod.** A Service per realm puts kube-proxy and conntrack between
the player and the simulation — the gateway problem in a different hat — and consumes a cluster IP per
shard. The cost is that the node port range is cluster configuration, and it is this design's one
prerequisite.

**The address is not known when `StartAsync` returns, and cannot be**: the scheduler has not placed the
pod, so there is no node and no external IP. It arrives with `Ready`. This is also the one backend
that *overrules* the realm about where it is — everywhere else the realm's own word wins, and here the
realm's view is inside the pod's network namespace, which is exactly the address a player cannot use.

### Why the process is a seam

`IRealmProcessHost` and `IRealmProcessHandle` exist so a test can run a fleet with no processes in it.
A test cannot ask an operating system to kill a process at an exact moment, and a test that starts
eight real ones is a test nobody runs on every push. `SystemProcessHost` is the production path;
everything above the seam — pool, lifecycle, events, reconciliation — is the same code either way.

## See also

- [Shards, keys and specs](shards-and-specs) — what `StartAsync` is handed.
- [Writing a realm](writing-a-realm) — the other end of every one of these signals.
- [docs/plan/27](https://github.com/Rikarin/Vixen/blob/master/docs/plan/27-mmo-framework.md) § ADR-019, § Testing.
