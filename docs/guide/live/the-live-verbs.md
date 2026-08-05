---
title: The live verbs, at three in the morning
slug: live/the-live-verbs
kind: guide
area: Live
summary: The four `vixen live` subcommands that reach a running cluster — what each one prints, why the operations sit behind an interface, and what has to exist before any of them can connect.
api: [T:Vixen.Cli.ILiveOperations, T:Vixen.Cli.ClusterLiveOperations, T:Vixen.Cli.LiveRunner]
tags: [live, mmo, cli, live-ops, diagnostics]
since: 0.1
status: preview
related: [live/the-cluster, live/upgrading-a-fleet, live/admission-and-health, live/shards-and-specs]
---

## What it is

Four subcommands under `vixen live`, over five operations on a running fleet.

| Verb | What it answers |
|---|---|
| `live status --map --region [--version]` | Every shard of that map: state, population, hard cap, endpoint. |
| `live drain --shard [--reason]` | Move a shard's players out. Nobody is disconnected. |
| `live explain --player --map --region [--version]` | Why that player went to that shard — the map's own record of the decision. |
| `live upgrade --region [--version]` | What the region is aiming at and how far it has got; with `--version`, where to aim next. |

Every verb also takes `--cluster`, which names the Orleans cluster to reach.

Three types, one per job. `ILiveOperations` is the five questions — `ShardsAsync`, `DrainAsync`,
`ExplainAsync`, `RolloutAsync`, `SetTargetAsync`. `ClusterLiveOperations` answers them over an
`IClusterClient` — a `GetGrain` and a call, against `IMapGrain`, `IShardGrain` and `IFleetGrain`, with
`Keys` spelling every grain key so that two spellings of one identity cannot become two fleets.
`LiveRunner` is the printing: one static method per verb, each taking the `TextWriter` it writes to
and returning the process's exit code.

⚠ **`up`, `down` and `upgrade --content` are missing rather than stubbed.** Doc 27 § Diagnostics lists
six verbs and four of them are here. `up` and `down` stand a fleet up, which is a hosting story doc 17
owns and which needs something to deploy. `upgrade --content` cannot answer honestly until the content
build records a shape per address — `ContentDiff` refuses every entry whose shape it does not know, so
the verb would say *"this needs a drain"* every time, which teaches an operator to stop reading it. A
verb that parses and then apologises is worse than one that is not there, because a script can only
discover the second kind.

## What it is for

Doc 27 § Diagnostics asks for the same operations the fleet dashboard performs, in a terminal,
*because 3 a.m.* — reachable from a machine with no browser and no VPN into whatever hosts the panel.
That is the whole brief, and it is why these are verbs on the CLI a project already has installed
rather than a second tool.

The interface exists for a different reason: **it is the seam that makes the verbs testable without a
cluster.** What is worth asserting about a command like this is its formatting and its argument
handling — that an unparseable shard id is refused before anything is drained, that a map with no
shards is not an error, that `upgrade` prints how to undo itself — and none of that needs a silo.
`Vixen.Cli.Tests` drives the real parser with a fake `ILiveOperations` behind it, so the suite runs in
milliseconds on every push instead of against a cluster somebody has to stand up first. It is the
same shape `IFleetDirectory` takes in the gate and `IClusterApi` takes in the Kubernetes backend.

⚠ **Every `LiveRunner` method takes its writer rather than reaching for `Console`.** A command whose
behaviour can only be checked by running a process is one whose behaviour is not checked, and that
rule is what lets the tests above assert on the exact text an operator will read.

## Using it

```bash
vixen live status --map maps/queensdale --region eu --version 0.1.0+00000000c0ffee
```

```
SHARD                                  STATE        POP   CAP  ENDPOINT
9e0f3f1c-6f27-4a2c-9c5a-1f1f2a0b8e11   Ready        180   220  eu-1.realm.example:30011
c41b7a90-2d3e-4d7a-8a6b-77d0c2b41a55   Ready         94   220  eu-1.realm.example:30012
2f6d5b18-9a04-4c31-b0e2-5c8b6f0d3a77   Draining      12   220  eu-2.realm.example:30007
```

Rows are ordered by state, so what is taking arrivals is at the top and what is on its way out is at
the bottom. `CAP` is the hard cap — the population above which a shard admits nobody — and not the
soft cap placement steers by; see [shards, keys and specs](shards-and-specs.md) for why there are two
numbers.

⚠ **`--version` is part of the identity being asked about, not a filter over it.** A map grain is
keyed by map, region *and* version, so `--map maps/queensdale --region eu` with no version asks about
the fleet whose key carries no version at all — which during a rollout is none of the two that are
actually running. Omitting it is right on a cluster that has never been given a target and misleading
on one that has.

⚠ **A map with no shards is not a failure.** It exits zero and names the map you typed, because that
is what every map looks like before anybody plays it — and because an operator who typed the name
wrong wants to see the name they typed:

```
No shards for maps/nowhere [eu] no version.
```

### What has to exist before any of this connects

`ClusterLiveOperations` needs one thing: an `IClusterClient` already connected to the cluster the
grains live in. It does not build one, and it does not own the one it is given — clustering is
deliberately not chosen for you, the same way `UseVixenOrchestrator` leaves membership to the caller.
A localhost silo wants `UseLocalhostClustering`; a deployment wants whichever of AdoNet, Redis, Azure
Storage or Kubernetes it runs on, plus the `ClusterId` and `ServiceId` that silo was started with. Two
clients with different ids are two clusters, however close together they happen to be running.

```csharp no-compile="the host that hands a client to `vixen live` is a game's own; the CLI's factory hook is internal today"
var host = new HostBuilder()
    .UseOrleansClient(orleans => orleans.UseLocalhostClustering())
    .Build();

await host.StartAsync();

ILiveOperations operations = new ClusterLiveOperations(host.Services.GetRequiredService<IClusterClient>());
```

⚠ **The published `vixen` tool has no cluster client wired into it, and every `live` verb throws
without one.** The command resolves its operations through a factory that only the CLI assembly and
its tests can set, so today the only caller that supplies one is the test suite. A `live` verb run
without it throws an `InvalidOperationException` that says *"No cluster client is configured"* and
points at the host application as the place to fix it. Until that hook is public, these verbs are
reachable from a host that links `Vixen.Cli` and calls `LiveRunner` with its own
`ClusterLiveOperations` — a real limitation, and not a gap in this page.

## Examples

### Moving a region to a new build, and watching it get there

```bash
vixen live upgrade --region eu --version 0.2.0+00000000deadbeef
```

```
eu is now rolling to 0.2.0+00000000deadbeef. Old-version shards drain as their players reach safe moments; roll back with the same command and the old pair.
```

Asking with no `--version` reads instead of writes:

```bash
vixen live upgrade --region eu
```

```
eu is aiming at 0.2.0+00000000deadbeef; 25.0 % of its shards are not there yet.
```

That percentage is the version spread, and zero is the end of the rollout. A region nobody has aimed
anywhere says so in the same place:

```
eu has no target set, so every shard stays on whatever it started with.
```

⚠ **Rolling back is `upgrade` with the old pair, and the command says so in its own output.** Nothing
about the mechanism is directional — a target is a target — and at three in the morning that sentence
is the entire procedure, which is precisely when it should not have to be remembered. See
[upgrading a fleet](upgrading-a-fleet.md) for what the fleet does with the target once it has it.

### Draining one shard by hand

```bash
vixen live drain --shard 2f6d5b18-9a04-4c31-b0e2-5c8b6f0d3a77 --reason "node is being replaced"
```

```
2f6d5b18-9a04-4c31-b0e2-5c8b6f0d3a77 is draining: node is being replaced. Nobody is disconnected — players leave at safe moments, and the shard stops when the last one has gone.
```

⚠ **The sentence after the shard id is the point of the verb.** *Drain* suggests something more
violent than it is: nothing is force-disconnected, players leave at moments the game approves of, and
a shard with a raid in it can hold out until the hard deadline. An operator who expected the shard to
stop within seconds needs to know that before they type it, not after — and `--reason` is free-form
because it lands in the log and the fleet view, where the next person to look is not you.

### Answering a placement complaint

```bash
vixen live explain \
  --player 6c2a55d1-3f4b-4a19-9e77-0b1d2c3a9f31/0b7e14aa-51c2-4d6e-8b0f-7a3c9e2b42c8 \
  --map maps/queensdale --region eu
```

```
2026-08-05 02:41:07Z — Placed
placed on 9e0f3f1c-6f27-4a2c-9c5a-1f1f2a0b8e11 at eu-1.realm.example:30011, scoring 2.4
  c41b7a90-2d3e-4d7a-8a6b-77d0c2b41a55 scored 1.1 — fill +0.6, guild +0.5
  2f6d5b18-9a04-4c31-b0e2-5c8b6f0d3a77 excluded: NotReady
```

One line per candidate: the filter that excluded each one and the score of each survivor. Without it,
placement complaints are unanswerable.

⚠ **"Nothing is held" and "they were refused" do not read the same, on purpose.** The map keeps the
last decision per player, bounded, so an empty answer is a sentence saying so — either the fleet never
placed them here, or later placements have pushed theirs out. Those two send an operator to two
different places next, and an answer that conflated them would send them to the wrong one.

### The two arguments that never reach the cluster

```bash
vixen live drain --shard the-big-one
```

```
That is not a shard id.
```

Exit code 2 — `ExitCode.UsageError`, which is how the rest of this CLI already says *"I was invoked
wrong"* rather than *"what you pointed me at is wrong"*. A script can tell those apart, and that is
the reason there are two. The same holds for a `--player` that is not an `account/character` pair, and
the cluster is never touched in either case. Parsing failures are
deliberately *silent* in the argument binder and *loud* in `LiveRunner`: one place knows how to talk
to a person, rather than an exception thrown out of a binder that does not.

### Testing a verb without a cluster

```csharp no-compile="the fake fleet and the internal factory hook both live in Vixen.Cli.Tests"
sealed class FakeFleet : ILiveOperations {
    public Task<IReadOnlyList<ShardReport>> ShardsAsync(ShardKey key, CancellationToken cancellation) =>
        Task.FromResult(Shards);

    // …the other four, each recording what it was asked…
}
```

Assertions then read like the terminal does — that `drain` printed *"Nobody is disconnected"*, that a
bad shard id left `Drained` at `ShardId.None`, that `upgrade` with a version told the reader how to
undo it. That is the entire argument for the interface: those are the properties worth defending, and
none of them is about Orleans.

## See also

- [The cluster](the-cluster.md) — the grains these five operations call, and the keys they are
  addressed by.
- [Upgrading a fleet](upgrading-a-fleet.md) — what a target does once `upgrade` has moved it.
- [Admission, health and the control plane](admission-and-health.md) — the shard states in the
  `STATE` column, and what draining actually waits for.
- [Shards, keys and specs](shards-and-specs.md) — `ShardKey`, `RealmVersion` and the two capacity
  numbers.
- [docs/plan/27](https://github.com/Rikarin/Vixen/blob/master/docs/plan/27-mmo-framework.md)
  § Diagnostics and operations.
