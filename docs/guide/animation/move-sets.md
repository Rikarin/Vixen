---
title: Move sets
slug: animation/move-sets
kind: guide
area: Animation
summary: A movement vocabulary as a flat table, and picking from it with a scored query instead of a graph.
api: [T:Vixen.Animation.Moves.MoveSet, T:Vixen.Animation.Moves.MoveEntry, T:Vixen.Animation.Moves.MoveQuery, T:Vixen.Animation.Moves.IMoveSelector, T:Vixen.Animation.Moves.IMoveScorer, T:Vixen.Animation.Moves.IGaitModel, T:Vixen.Animation.Moves.ITransitionPolicy, T:Vixen.Animation.Moves.MoveSetMotion, T:Vixen.Animation.Moves.MoveSetContent]
tags: [animation, locomotion, move-sets, selection]
since: 0.1
status: stable
related: [animation/pose-constraints, animation/variation-harness]
---

## What it is

A **move set** is a flat table of moves. A row is a clip, some facets saying what it is for, and some
numbers saying what it does. There is no container per style, no nesting, and no graph: `style=injured`
has exactly the same structural standing as `this one turns left`.

Picking a move is a **query**: required facets filter, preferred facets and numeric proximity score,
and the winner is retimed within the range it admits.

## What it is for

The thing a locomotion graph stops being able to express. A graph with a walk, a run, a sprint and
their starts, stops and turns is manageable; add three stances and two injury states and it is a
cross-product nobody authors twice. A table with facets is the same content with the combinatorics
taken out of the structure.

You do not want it for a hand-built sequence of specific states — a boss's scripted phases, a door's
open-and-close. That is a state machine, and `Vixen.Editor.AnimationGraph` is still the right tool.

## Using it

Author a `.vxmoveset`:

```yaml
name: locomotion
entries:
  - name: walk
    clip: Assets/Anim/Walk.vxanim
    speed: 1.4
    minRate: 0.85
    maxRate: 1.15
    footPhase: 0.12
    facets:
      - { key: role, value: loop }
      - { key: style, value: neutral }
  - name: run
    clip: Assets/Anim/Run.vxanim
    speed: 3.6
    minRate: 0.8
    maxRate: 1.2
    facets:
      - { key: role, value: loop }
rules:
  - from: [{ key: role, value: idle }]
    to: [{ key: role, value: loop }]
    duration: 0.18
    sync: ClosestFoot
```

`role` is the one reserved key. Everything else is your project's; `role` is not, because the
transition rules and the phase sync both read it, and a set that spells it `looping` gets no answer
from either. The importer refuses an invented role for exactly that reason.

`minRate` and `maxRate` are what let a set carry one clip per gait rather than one every 0.4 m/s. A
walk usually reads correctly ±15 %; a stop whose weight lands on a specific frame survives no
retiming at all, and a move that says so is one the selector will not stretch.

At runtime, a `MoveSetMotion` is an ordinary `Motion` — put one in a state and layers, masks and
events are unchanged:

```csharp
var motion = new MoveSetMotion(set) {
    Gait = new BipedGaitModel { LegLength = 0.92f },
};

layer.StateMachine.Add(new AnimationState("locomotion", motion));
```

## Why did it pick that clip

Open the set in the editor and type the query into the filter box. It is **the same query the runtime
builds**, handed to the same scorer, so what the table shows is what the game would pick — in score
order, with the breakdown beside it: matched preferences, numeric proximity, penalties.

An empty breakdown means something too: a move with no terms is one that nothing counted against.

The same answer is available in code:

```csharp
foreach (var entry in MoveExplanations.Explain(set, query)) {
    Console.WriteLine(entry);          // "run: 0.42 at 0.94×", or "walk: not eligible, does not say role=loop"
}
```

## Coverage

Turn **Coverage** on in the editor, or call `MoveCoverage.Sweep`. It walks the query space the facet
vocabulary and the numeric range describe and reports the regions where the set falls back — no
injured stop, nothing above 4 m/s. Not an error, and not a build failure: the thing to look at before
shipping, because those are exactly the inputs nobody plays.

## Overlays

A set may name others as bases. The overlay composes **at bake**, matching on the move's name, so an
injured set is three clips over a hundred rather than a hundred and three. The editor shows the
composed table with the rows a set replaces struck through and named with the file they came from —
hiding them is how somebody spends an afternoon editing a row that no longer has any effect.

## Seams

Four, and each has a second implementation in `Vixen.Animation.Tests` proving the shape is not the
default's shape wearing a mask:

| Seam | The default | What else fits |
|---|---|---|
| `IMoveSelector` | filter, score, pick, retime | a table-driven chooser that never calls the scorer; a feature-vector matcher |
| `IMoveScorer` | preferences, proximity, repeat penalty | extra terms from game state — a cooldown, a combat system's preference |
| `IGaitModel` | a forward-facing biped | a vehicle, where speed is signed and turn rate is a function of it |
| `ITransitionPolicy` | an ordered rule list, first match wins | a pairwise table, or a question put to another system |

## Examples

Selecting by hand, without an animator:

```csharp
var query = new MoveQuery {
    Required = FacetSet.Of(MoveRole.Facet(MoveRole.Loop)),
    Preferred = [new(Facet.Of("style", "injured"), 2f)],
    Numeric = new() { Speed = 3.4f },
};

var chosen = QueryMoveSelector.Shared.Choose(set, query, DefaultMoveScorer.Shared);

if (chosen.HasMove) {
    var entry = set[chosen.Index];
    // entry.Motion plays at chosen.PlaybackRate
}
```

Selection is deterministic by construction: ties break on `MoveKey`, which is an FNV-1a hash of the
name and therefore the same on every machine. That is what lets a networked game replicate the chosen
key rather than the pose.

## See also

- [Pose constraints](animation/pose-constraints) — the other half of doc 34, and independent of this one
- [The variation harness](animation/variation-harness) — knowing when a marked-up clip is finished
