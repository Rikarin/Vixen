# Vixen.Live.Placement.Docker

Realms as containers, over a hand-written Engine API client.

Spec: [docs/plan/27-mmo-framework.md](../../docs/plan/27-mmo-framework.md) § ADR-019.

## Using it

```csharp
using var placement = new DockerPlacement(new DockerPlacementOptions {
    Image = "mygame/realm:0.1.0",
    Owner = "queensdale",                 // so two orchestrators can share one daemon
    Host  = "10.0.0.4",                   // what a player can reach; this backend cannot guess it
    Ports = new PortPool(7800, 7899)
});
```

Everything above `IRealmPlacement` is unchanged from the process backend: a port from a pool, the
encoded `RealmSpec` on the command line, `Ready` from the realm's own stdout, `Lost` for an exit
nobody asked for.

⚠ **There is no `Executable` here, because the image is one.** `ProcessPlacement` is told what to run;
this backend is told which image to run, and the image says what it runs — `ENTRYPOINT
["./YourGame.Realm"]`, which the template's Dockerfile ends in. The spec and `Arguments` are
*appended* to it, exactly as a pod's `args` are appended to the same image's entrypoint. An image that
genuinely cannot carry one can be told what to run through `Entrypoint`, which replaces the image's
the way Kubernetes' `command` does.

## Why the client is hand-written

ADR-019: the Engine API surface needed here is a handful of calls, a `SocketsHttpHandler` with a
`ConnectCallback` reaches a unix socket in about twenty lines, and `Docker.DotNet` has not been pushed
to in a year. It is the same judgement `Vixen.Navigation` made about Recast and `Vixen.Ui.Text` made
about ICU — and unlike those two, this surface really is small.

Two of the eight are not part of starting anything. `InspectEntrypointAsync` is what lets a backend
say *what a container would exec* instead of finding out from a daemon (below), and `PullAsync` is
there because an orchestrator rolling a new `BuildVersion` onto a node that has never run it needs
some way to get the image there — when it pulls is a policy this backend deliberately does not have
an opinion about, so it is exposed and not called.

The one piece that is not ordinary HTTP is the **log framing**: a container created without a TTY has
its stdout and stderr multiplexed, each chunk prefixed with `[stream, 0, 0, 0, length…]`.
`DockerFrames` is thirty lines and turns that back into text lines, and the test's fake daemon emits
genuinely framed output so the demultiplexer is exercised rather than stepped over.

## Five decisions worth knowing about

**The image's entrypoint is honoured, and the backend checks that there is one.** `Cmd` is the
realm's *arguments*; Docker execs `ENTRYPOINT` with `Cmd` appended, so an image with no entrypoint
promotes `--realm-spec` to the program and the daemon answers `exec: "--realm-spec": executable file
not found in $PATH` — from a container that already exists, one placement at a time. So the image is
asked what it runs before a port is even rented: `ProbeAsync` refuses a backend whose image would
exec a flag, `StartAsync` refuses the placement, and a start the daemon refuses anyway is re-thrown
with the argv attached. The daemon's own message names a program and never says which one it was
handed.


**There is no stdin, and there does not need to be.** `Placement.Process` writes `vixen-realm drain`
to a realm because doc 27 § Cost's **L0 has no control plane to say it over**. A Docker deployment is
an L1 deployment by construction — it has an orchestrator, and a realm learns to drain from the reply
to its own heartbeat. So `StopMode.Drain` here is only the deadline: wait, then stop. Attaching a
writable stream would mean hijacking the connection to build a second way of saying something the
cluster already says.

⚠ The corollary: **an unorchestrated Docker deployment cannot drain politely.** If you want L0's
lifecycle without a cluster, use `Placement.Process`.

**A realm container never restarts itself.** `RestartPolicy: no`, because doc 27 § Health makes
recovery a *placement* rather than a resurrection — a container the daemon brought back would be a
shard the cluster had already written off, returning with no players and no map.

**Disposing leaves the containers running**, which is the opposite of `ProcessPlacement` and
deliberate. A child process that outlived its launcher is an orphan holding a UDP port; a container
that outlives the orchestrator which created it is a shard still serving players, and the daemon will
still be there when the orchestrator comes back. The labels are how it finds them: `ListAsync` asks
the *daemon* rather than an in-memory dictionary, because the whole use of that call is reconciling
after a restart.

**Not a TTY.** With one, the daemon merges stdout and stderr into a single unframed stream and a
realm's ready line becomes a substring match on everything it ever prints.

## What a test can and cannot say

`DockerEngine` takes an `HttpMessageHandler`, so the tests assert what the client *sends* and how it
reads the framed stream back, with no daemon involved. What they cannot assert is that a real Engine
accepts this dialect — doc 27 § Testing puts that on the nightly leg alongside Kubernetes, which is
the same shape as the platform matrix.

## See also

- [`Vixen.Live.Placement.Process`](../Vixen.Live.Placement.Process/README.md) — the same interface with
  no daemon, and the one that keeps stdin.
- [docs/guide/live/placing-realms](../../docs/guide/live/placing-realms.md) — the written half.
