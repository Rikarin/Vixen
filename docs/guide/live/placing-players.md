---
title: Placing players
slug: live/placing-players
kind: concept
area: Live
summary: The megaserver as a function — the hard filters, the score, the explanation, and the hysteresis that decides when a map grows a shard.
api: [T:Vixen.Live.Orchestration.PlacementDirector, T:Vixen.Live.Orchestration.PlacementRequest, T:Vixen.Live.Orchestration.ShardCandidate, T:Vixen.Live.Orchestration.PlacementWeights, T:Vixen.Live.Orchestration.PlacementDecision, T:Vixen.Live.Orchestration.PlacementOutcome, T:Vixen.Live.Orchestration.PlacementFilter, T:Vixen.Live.Orchestration.CandidateVerdict, T:Vixen.Live.Orchestration.ScoreTerm, T:Vixen.Live.Orchestration.MapFleet, T:Vixen.Live.Orchestration.FleetPolicy, T:Vixen.Live.Orchestration.FleetAction, T:Vixen.Live.Orchestration.FleetActionKind]
tags: [live, mmo, placement, megaserver, orchestration]
since: 0.1
status: preview
related: [live/shards-and-specs, live/placing-realms]
---

## What it is

There is no server select and no realm queue. A player asks for a map and the megaserver puts them on
the best instance of it — where "best" is a score a game authors. `PlacementDirector` is that score;
`MapFleet` is what decides the map needs another instance, or one fewer.

## What it is for

Everything Guild Wars 2 is famous for at this layer falls out of one scoring function rather than
being a feature beside it. "Join your friend's instance" is a party term weighted at ten thousand.
"Overflow" is a shard spawned above the intended count and drained first. "No queue" is a consequence
of both. Writing them as separate mechanisms is how an MMO ends up with four ways to end up somewhere,
three of which disagree.

## Using it

```csharp no-compile="the grain that supplies the candidates is milestone L1's next slice"
var director = new PlacementDirector();          // or PlacementWeights.Parse(yourVxplacement)

var decision = director.Place(request, candidates);

if (decision.Outcome == PlacementOutcome.Placed) {
    // decision.Shard and decision.Endpoint are everything the gate hands the client.
}
```

### The hard filters come first, and each has its own name

A candidate that fails one is not scored at all:

| | |
|---|---|
| `Map`, `Region` | a different map, or a different latency zone |
| `Build`, `Content` | ADR-022's version pair. `Content` is the one a client that has not fetched the catalog update hits |
| `NotReady` | starting, draining or gone. **Only `Ready` is a placement candidate** |
| `Full` | at its hard cap |
| `Access` | an instance's access list does not admit them |

They are separate values rather than one "excluded" because the whole use of the answer is telling
somebody why they did not end up with their guild — and *"the shard your guild is on is running last
week's build"* and *"the shard your guild is on is full"* are different conversations.

### Then a score, and every term is named

```csharp no-compile="the weights a game authors; shown as the score they produce"
party      10 000     // a party member is present — effectively a hard pull
guild         400     // per member, capped at five
friends       200     // per friend, capped at five
locale        300     // the shard is speaking their language
fill          250     // fill is in the healthy band, 40–80 % of the soft cap
overfull  −40/pt      // above 80 %, falling away steeply
age          −100     // the shard is past its maximum age
antiflap   −5 000     // they were just moved off this shard
```

⚠ **The party term outweighing everything else put together is the mechanism, not a preference.** A
party pull that could lose to a full guild would be a separate join feature waiting to be written.

⚠ **Preferring a shard that is already half full is what makes consolidation possible.** A score that
spread players evenly would leave a map that is emptying with a lot of lonely shards and no way to
merge them.

## Examples

### Why did I not end up with my guild?

```csharp no-compile="what `vixen live explain <player>` prints"
Console.WriteLine(decision.Explain());

// placed on 0f8fad5b-… at 10.0.0.4:7777, scoring 2450
//   0f8fad5b-… scored 2450 — guild +2000, locale +300, fill +250
//   7b1c9e02-… scored -5000 — antiflap -5000
//   3a44f180-… excluded: Content
```

Doc 27 § Diagnostics is blunt about why this is not optional: *without it, placement complaints are
unanswerable*. Every candidate gets a verdict whether or not it was scored.

### When does a map grow?

`MapFleet` watches one map. It spawns when there is no shard at all, when every shard is at its soft
cap, or when the recent arrival rate projected over the lead time exceeds the free space — debounced,
because two hundred people zoning in at once must not produce twenty shards. It merges when two or
more shards have been under a quarter of their soft cap for two minutes, draining the emptiest.

```csharp no-compile="one observation of one map; the grain that owns the fleet is L1's next slice"
fleet.Arrived(now);                                   // every zone-in feeds the projection

var action = fleet.Observe(now, candidates);          // Spawn, Drain, or None
```

⚠ **The asymmetry is the design.** Spawning at 100 % and merging below 25 %, with a dwell before any
merge, is what stops the fleet oscillating — the same lesson as `InterestChain`'s leave-hysteresis, at
a different scale.

⚠ **A cyclical map settles on its peak, not on its trough.** A world boss that fills a map every three
minutes keeps the shards between events, and that is correct: a fleet that collapsed every trough
would spend the next peak refusing people while shards load.

### Two things the traces found

The test project simulates doc 27's three traffic shapes — flash crowd, slow bleed, sawtooth — through
half an hour in milliseconds, and both of these were real defects in the first version:

- **A rate measured over the nominal window makes a burst read as a trickle.** Ten arrivals a second
  read as 0.17/s until a sixty-second window fills, so the fleet spawned *after* saturation and
  refused twenty of two hundred players. The rate is now measured over the span the arrivals actually
  landed in, with a floor so that a party arriving in one instant is not mistaken for a rate.
- **Resetting the merge dwell after each drain makes a cyclical map leak shards.** Once a merge has
  finished, no new evidence is needed — the map has already been quiet for the dwell. What guards
  against draining too fast is that only one merge is in flight at a time.

## See also

- [Shards, keys and specs](shards-and-specs) — what a candidate is.
- [Placing realms](placing-realms) — what a `Spawn` turns into.
- [docs/plan/27](https://github.com/Rikarin/Vixen/blob/master/docs/plan/27-mmo-framework.md) § Placement.
