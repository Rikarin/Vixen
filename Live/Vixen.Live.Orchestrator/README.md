# Vixen.Live.Orchestrator

The megaserver's intelligence: which shard a player is put on, why, and when a map should grow one or
give one back.

Spec: [docs/plan/27-mmo-framework.md](../../docs/plan/27-mmo-framework.md) § Placement.

## Using it

```csharp
var director = new PlacementDirector(PlacementWeights.Parse(File.ReadAllText("queensdale.vxplacement")));

var decision = director.Place(request, candidates);

if (decision.Outcome == PlacementOutcome.Placed) {
    // decision.Shard, decision.Endpoint — and decision.Explain() when somebody asks why.
}
```

and the fleet, once per observation:

```csharp
var fleet = new MapFleet(key);

fleet.Arrived(now);                          // every zone-in feeds the projection

switch (fleet.Observe(now, candidates).Kind) {
    case FleetActionKind.Spawn: /* IRealmPlacement.StartAsync */ break;
    case FleetActionKind.Drain: /* IShardGrain → Draining        */ break;
}
```

## Four decisions worth knowing about

**It is a pure function of numbers, and the grain that knows the roster supplies them.** How many of a
player's friends are on a shard is a question only the thing holding the fleet's roster can answer, so
`ShardCandidate` carries *counts* and the director scores them. That split is what makes the scoring
property-testable: doc 27 § Testing's three properties — a party is never split, a shard above its
hard cap is never chosen, scoring is total and deterministic — run 45 000 randomised fleets in under a
second.

**Every placement explains itself, and it is not optional.** Doc 27 § Diagnostics: *"without it,
placement complaints are unanswerable"*. Each candidate gets a `CandidateVerdict` naming either the
filter that excluded it or the terms that made up its score, and `Explain()` prints it. This costs a
handful of small objects per zone-in, on the control plane, which is not a budget anything here runs
against.

**Ties break on the shard id.** Not for fairness — for determinism. A placement that depended on the
order candidates happened to be enumerated in would make every property test flaky and every player
complaint unreproducible.

**The weights are a `.vxplacement` a game authors.** The defaults are Guild Wars 2's shape and a
starting point rather than an answer: a battleground wants fill to dominate, a social hub wants
locale to. What the engine owns is that the terms exist and that scoring is total whatever they say.

## Two things the simulation found

`FleetSimulation` in the test project runs doc 27 § Testing's three traces — flash crowd, slow bleed,
sawtooth — through half an hour of traffic in milliseconds. Both of these were real defects in the
first version and are now the reason two policy fields exist:

**The arrival rate is measured over the span arrivals landed in, not over the nominal window.**
Dividing by a sixty-second window makes ten people a second read as 0.17/s until the window fills, so
the fleet spawns *after* saturation instead of before it. The flash-crowd trace refused twenty of two
hundred players on that arithmetic and refuses none on this one. `MinimumRateSpan` is the floor that
stops the opposite failure: a party of ten arriving in one instant is not ten a second.

**One merge in flight, rather than a dwell that resets after each.** A drained shard's players have
not moved yet at the next observation, so draining a second one on that evidence would empty the map
into one shard in seconds — but once the first merge *has* finished, no new evidence is needed. The
first version reset the dwell instead, and the sawtooth trace found what that costs: a map that spawns
every cycle and merges once every two minutes grows a shard per cycle and never gives it back.

## What is deliberately not here yet

**The grains.** Doc 27 lists this project as "grain implementations, placement director, heuristics,
upgrades", and what exists is the middle two. They exist first because they are a pure function and a
small state machine — testable on a laptop in milliseconds — and the grains that will host them are a
scheduling decision on top rather than a rewrite. There is no Orleans reference in this project and
there will be exactly one when `Vixen.Live.Cluster` lands.

**The `.vxplacement` importer.** `PlacementWeights.Parse` reads one at boot; turning it into an
addressable asset with an inspector is editor-side work.

## See also

- [`Vixen.Live.Abstractions`](../Vixen.Live.Abstractions/README.md) — `ShardKey`, `ShardCapacity`.
- [docs/guide/live/placing-players](../../docs/guide/live/placing-players.md) — the written half.
