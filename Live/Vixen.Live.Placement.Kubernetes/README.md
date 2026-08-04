# Vixen.Live.Placement.Kubernetes

Realms as pods. **One `Pod` per shard — not a Deployment, not a StatefulSet** — owner-referenced to
the orchestrator, addressed by node external IP and a `hostPort`.

Spec: [docs/plan/27-mmo-framework.md](../../docs/plan/27-mmo-framework.md) § ADR-019.

## Using it

```csharp
using var placement = new KubernetesPlacement(
    new KubernetesPlacementOptions {
        Image     = "mygame/realm:0.1.0",
        Namespace = "queensdale",
        Owner     = "eu-fleet",
        Ports     = new PortPool(30000, 30099),      // the node range your firewall opened
        Self      = PodIdentity.FromEnvironment()    // the downward API; null outside a cluster
    },
    new ClusterApi(ClusterApi.Connect()!)
);
```

## The two decisions this project exists to implement

**A Pod, not a Deployment.** Realms are not fungible replicas: each has an identity, a map, a
population, a version, and a lifetime that ends when the last player leaves. A Deployment's controller
would restart a realm that exited on purpose, and its rolling update is the wrong shape for
§ Upgrades — draining a realm means moving *players*, not terminating a pod when a readiness probe
flips.

An **owner reference** gives the one thing the controller was wanted for: garbage collection if the
orchestrator itself is destroyed. It is deliberately **not** a controller reference — a realm is
*owned*, not *managed*, and a controller reference invites something to reconcile it.

**`hostPort`, not a `Service` per pod.** A Service per realm puts kube-proxy and conntrack between the
player and the simulation — the gateway problem in a different hat, which § The routing question
spends a page rejecting — and consumes a cluster IP per shard.

⚠ **The node port range is this design's one cluster prerequisite** (doc 27 M5). It has to be open on
the nodes' firewall and nothing here can check that; a realm on a closed port is a shard that reports
ready and that nobody can reach.

## Three things that differ from the other backends

**The address is not known at `StartAsync`, and cannot be.** The scheduler has not placed the pod, so
there is no node and therefore no external IP. `Started` carries no endpoint; the address is computed
when the realm reports ready and the node is known — `ExternalIP`, falling back to `InternalIP`,
falling back to the pod's own `hostIP` if RBAC does not allow reading `Node` objects.

**This is the one backend that overrules the realm about where it is.** Everywhere else, the realm's
own word about where it bound wins. Here the realm's view is inside the pod's network namespace, so it
is exactly the address a player *cannot* use.

**The spec a pod receives names `0.0.0.0`, not the client-facing host.** A pod cannot know its node's
external address, and the realm binds every interface anyway. Getting this wrong is not subtle *once
you look*: an empty host makes a `RealmSpec` that `TryDecode` refuses, so the realm exits with "this
process is not a realm" and the pod looks like a bad image. The test suite caught exactly that.

## What a test can and cannot say

`IClusterApi` is a six-method seam over `IKubernetes`, which is the whole generated API and not
something anybody fakes. Behind it, everything ADR-019 argues about — the Pod shape, the `hostPort`,
the owner reference not being a controller reference, the address fallbacks, `Succeeded` versus
`Failed` — is asserted on every push. Whether a real API server accepts these objects is the nightly
`kind` leg's question, which doc 27 § Testing already puts there.

## See also

- [`Vixen.Live.Placement.Docker`](../Vixen.Live.Placement.Docker/README.md) — the same interface, with
  a hand-written client rather than a package, and why the line is drawn there.
- [docs/guide/live/placing-realms](../../docs/guide/live/placing-realms.md) — the written half.
