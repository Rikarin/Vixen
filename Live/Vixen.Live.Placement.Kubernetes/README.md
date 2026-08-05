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
        // Command = ["/opt/runtime/launch"]         // only for an image with no ENTRYPOINT of its own
    },
    new ClusterApi(ClusterApi.Connect()!)
);
```

## The image supplies the program, and this backend can only half-check that

`args` are **appended** to the image's entrypoint; they never replace it. That is the property that
makes a pod's command the same string a container's `Cmd` and `Process.Start` carry — `RealmSpec`'s
whole premise — and it is why a realm image ends in `ENTRYPOINT ["./YourGame.Realm"]`, as the
template's Dockerfile does. An image with **no** entrypoint promotes the first argument to the
program, and the kubelet then reports
`CreateContainerError: exec: "--realm-spec": executable file not found in $PATH` from a pod that
already exists and is holding a `hostPort`. Set `Command` for an image that genuinely cannot carry an
entrypoint — a shared runtime with the realm mounted into it — and it replaces the image's, exactly as
Docker's `Entrypoint` option does.

⚠ **Only half of "would this actually exec" is answerable from a cluster, and the code says which
half.** The Docker backend asks the daemon what an image's entrypoint is before it accepts a
placement. There is no equivalent here: the *registry* knows an image's entrypoint, and an API server
does not until a kubelet has pulled it. So:

- **Checked, in `ProbeAsync` and before `StartAsync` rents a port:** a configured `Command` whose
  first word begins with `-`. That is always a misconfiguration — flags belong in `Arguments`.
- **Not checked, and the probe says so rather than implying otherwise:** whether an image with no
  `Command` carries an entrypoint. The probe's detail reads *"with `--realm-spec …` appended to
  whatever entrypoint it carries"*, which is the true statement.
- **Discovered, in the `Lost` event's detail:** the kubelet's own container-status reason, carried
  into the event verbatim. This is the half that matters most, because it is the one that names the
  program that was not found.

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

## Four things that differ from the other backends

**A pod's log cannot be followed until its container has started**, and the API server refuses with a
400 rather than an empty stream. Both other backends can attach to output the moment they create the
thing; here the follow is re-attached while the pod is still on its way up, and only a pod that is
running, gone, or stuck for a reason that will not resolve — `CreateContainerError`, `ErrImagePull`
and the rest — ends the wait. Reading that first refusal as the end of the pod would report every
realm `Lost` milliseconds after placing it.

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
