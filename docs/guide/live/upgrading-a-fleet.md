---
title: Upgrading a fleet
slug: live/upgrading-a-fleet
kind: concept
area: Live
summary: Whether a content update can be applied to a running world — proven by the build rather than asserted by a human — and the rolling upgrade for when it cannot.
api: [T:Vixen.Live.Orchestration.ContentDiff, T:Vixen.Live.Orchestration.CatalogProjection, T:Vixen.Live.Orchestration.ContentEntry, T:Vixen.Live.Orchestration.ContentDelta, T:Vixen.Live.Orchestration.ContentChange, T:Vixen.Live.Orchestration.Rollout, T:Vixen.Live.Orchestration.RolloutState, T:Vixen.Live.Orchestration.RolloutPolicy, T:Vixen.Live.Orchestration.RolloutDecision]
tags: [live, mmo, upgrades, live-ops]
since: 0.1
status: preview
related: [live/placing-players, live/shards-and-specs, live/transferring-players]
---

## What it is

Two kinds of change, and conflating them is why live updates are usually a maintenance window.

**Content-only** — a new item, a new quest, a rebalance. The catalog's `BuildHash` changes and no
assembly does. `ContentDiff` decides whether a running realm can take it.

**Build** — an assembly changed. `Rollout` moves the fleet across, one drain at a time.

## What it is for

*"Adding a new item should be about releasing an addressable update"* is the requirement, and
`ContentDiff` is what makes it safe to believe. Doc 27 puts it as **"'additive' is proven by the
build, not asserted by a human"** — the gate is a tool refusing to apply a non-additive diff live,
*with the reason*, rather than applying it and finding out.

⚠ **The classifier is deliberately pessimistic, and the asymmetry is the whole safety argument.**
Calling a non-additive change additive means a live reload that corrupts a running world; calling an
additive change non-additive means a drain nobody needed. The first is unrecoverable and the second
costs an evening, so anything it cannot decide is **not** additive.

## Using it

```csharp no-compile="the catalogs come from `vixen content build`"
var deltas = ContentDiff.Compare(running.Entries, published.Entries);

if (ContentDiff.IsAdditive(deltas)) {
    await registry.ReloadAsync();          // live, no restart, nobody notices
} else {
    foreach (var blocker in ContentDiff.Blockers(deltas)) {
        log.UpgradeBlocked(blocker);       // WITH the reason — this is the part that gets left out
    }
}
```

A tool that says *"this needs a drain"* and not *"because `items/greatsword` changed shape"* makes the
operator diff two catalogs by hand at three in the morning.

## Examples

**What is additive, and what is not:**

| Change | Verdict | Why |
|---|---|---|
| a new address | ✅ | nothing live can refer to an address that did not exist |
| a definition's numbers moved | ✅ | a realm re-reads a definition table |
| a definition's **shape** moved | ❌ | anything already holding one now holds the wrong thing |
| a prefab changed | ❌ | it is baked into entities that already exist |
| a scene changed | ❌ | a realm is currently simulating it |
| an address changed kind | ❌ | two different things wearing one name |
| **anything removed** | ❌ | see below |

⚠ **A removal is never additive, even of something nothing is using.** Whether an address is in use is
a question about every entity in every world in the fleet, and this compares two files. A classifier
that guessed would be guessing about the case that deletes a player's sword.

⚠ **One blocking change makes the whole update non-additive.** There is no partial apply: a catalog is
one `BuildHash`, so applying the additive half would leave the fleet on a content version that never
existed.

**The rolling upgrade**, driven from the fleet grain:

```csharp no-compile="IFleetGrain's timer is what drives this"
var decision = rollout.Observe(await fleet.Shards(), clock.Now);

foreach (var shard in decision.Drain) {
    await cluster.GetGrain<IShardGrain>(shard.Value).Drain("rolling to " + rollout.Target);
}
```

⚠ **Every step a rollout produces is a *drain*.** It never kills anything, because a drain moves
players out at safe moments and doc 27 is explicit that nothing is force-disconnected. A rollout that
could disconnect would be the one live-ops action able to undo that promise.

**Rolling back is the same call with the old pair** — nothing about the mechanism is directional:

```csharp no-compile="at three in the morning, this is the whole procedure"
rollout.PointAt(previousVersion, clock.Now);
```

⚠ **An entry with no recorded shape is never additive, whatever its kind.** A `CatalogEntry` carries
an address, a content id, a bundle and a size — nothing that says whether a definition gained a field.
Treating "no schema" as "schema unchanged" would call a layout change to a definition additive, which
is the unrecoverable direction. **Until the content build emits a schema hash per address, no content
update is applicable live**, and that is the correct state for it to be in.

## The three bounds on fragmentation

Version-filtered placement means players on the old catalog can only meet players on the old catalog.
That is fine for an hour and corrosive for a day.

- **`RolloutPolicy.Grace`** — past it, old-version shards stop being created at all and a client that
  has not updated meets the gate's update flow instead of a shard.
- **The gate pushes the update**, so a client on its service-plane socket is told a new catalog exists
  the moment it is published.
- **`Spread` is a metric, not a surprise** — the fraction of shards not on the target, and the number
  a rollout is watched by. It is complete at exactly zero: stopping at 2 % would leave shards on the
  old build for ever, and *for ever* is how a fleet ends up running four versions.

`RolloutPolicy.DrainWidth` is the number doc 27 implies and does not name. Draining every old shard at
once asks every player in the region to transfer inside one window — a thundering herd against
new-version shards that have not finished starting, which presents as a rollout that *made the game
unplayable* rather than as a capacity mistake.

⚠ **A rollback restarts the grace.** Without that it inherits the elapsed grace of the rollout it is
undoing, putting the fleet straight into `Forcing` against the version everybody is already on.

## See also

- [Placing players](placing-players.md) — the version filter this is the other half of.
- [Transferring players](transferring-players.md) — how a drained shard's players actually move.
- [docs/plan/27](https://github.com/Rikarin/Vixen/blob/master/docs/plan/27-mmo-framework.md)
  § Upgrades — the two kinds of change, and the live-ops hazard the bounds exist for.
