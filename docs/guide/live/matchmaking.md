---
title: Matchmaking, queues and ratings
slug: live/matchmaking
kind: guide
area: Live
summary: Open Match's model without its deployment — a ticket is a party so "never split" is a property of the types, and two rating models the queue definition chooses between.
api: [T:Vixen.Live.Matchmaking.Rating, T:Vixen.Live.Matchmaking.IRatingModel, T:Vixen.Live.Matchmaking.EloRatingModel, T:Vixen.Live.Matchmaking.BayesianRatingModel, T:Vixen.Live.Matchmaking.MatchTicket, T:Vixen.Live.Matchmaking.MatchPool, T:Vixen.Live.Matchmaking.MatchProposal, T:Vixen.Live.Matchmaking.IMatchFunction, T:Vixen.Live.Matchmaking.IMatchEvaluator, T:Vixen.Live.Matchmaking.HighestQualityEvaluator, T:Vixen.Live.Matchmaking.Matchmaker]
tags: [live, matchmaking, rating, elo, trueskill, queue, mmo]
since: 0.1
status: preview
related: [gameplay/pvp, gameplay/instances, gameplay/tags]
---

## What it is

A **`MatchTicket`** is a party waiting. A **`MatchPool`** is which of them a queue will consider. An
**`IMatchFunction`** is the game's code that proposes matches, an **`IMatchEvaluator`** settles two
proposals that want the same ticket, and a **`Matchmaker`** runs the cycle. **`IRatingModel`** ships
twice: Elo and a TrueSkill-family Bayesian model.

## What it is for

Filling a battleground, an arena or a dungeon group. Doc 28 takes Open Match's separation of
*filtering, proposing, evaluating and allocating* and leaves its Kubernetes deployment behind.

## Using it

Build a `Matchmaker` with a pool and your own `IMatchFunction`; `Enqueue` tickets; `Cycle` on a timer;
handle `Matched` by calling doc 27's placement.

⚠ **Allocation is not here.** `IMapGrain.Place` is doc 27's, and a second allocator is what this must
not become — two of them disagree about capacity and the disagreement is a shard nobody can join.

⚠ **A ticket is a party, never a player.** That is how "a party is never split" is a property of the
types rather than a rule: nothing downstream is ever handed one member of a group.

⚠ **`default(MatchPool)` admits nobody.** A positional record struct's parameter defaults belong to
its constructor, not to `default` — use `MatchPool.Everybody` or pass null.

⚠ **The evaluator's oldest-first tie-break is what bounds a queue time**, and so is the widening band,
which uses the *wider* of two tickets' bands so a long-waiting player can still meet a newcomer.

⚠ **Both rating models refuse a free-for-all** rather than approximating one. Elo rates a team as the
*mean* of its players, not the sum.

## Examples

A queue with a filtered pool:

```csharp compile
using Vixen.Gameplay;
using Vixen.Live.Matchmaking;

static class Queues {
    public static Matchmaker Ranked(IMatchFunction function, GameplayTagTable tags) =>
        // A pool is a tag query and a range — the same requirement algebra as everything else.
        new(
            function,
            new MatchPool(GameplayTagQuery.Resolve(tags, all: ["Mode.Ranked"]), Region: 0, MaximumLatency: 90f),
            wideningPerSecond: 25d
        );
}
```

Handing an accepted match to placement:

```csharp compile
using Vixen.Live.Matchmaking;

static class Director {
    public static void Wire(Matchmaker queue) =>
        // The only allocator is doc 27's. This is where a caller reaches it.
        queue.Matched += proposal => Place(proposal);

    static void Place(MatchProposal proposal) { }
}
```

Rating a result:

```csharp compile
using Vixen.Live.Matchmaking;

static class Scores {
    public static IReadOnlyList<Rating[]> Settle(IRatingModel model, Rating[] winners, Rating[] losers) =>
        // Ranks are finishing places, lowest first; equal numbers are a draw.
        model.Update([winners, losers], [0, 1]);
}
```

## See also

- [PvP](gameplay/pvp) — one of its two callers.
- [Instances](gameplay/instances) — the other.
- [Gameplay tags](gameplay/tags) — what a pool filters on.
