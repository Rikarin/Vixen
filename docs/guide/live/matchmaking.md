---
title: Matchmaking, queues and ratings
slug: live/matchmaking
kind: guide
area: Live
summary: Open Match's model without its deployment — a ticket is a party so "never split" is a property of the types, and two rating models the queue definition chooses between.
api: [T:Vixen.Live.Matchmaking.Rating, T:Vixen.Live.Matchmaking.IRatingModel, T:Vixen.Live.Matchmaking.EloRatingModel, T:Vixen.Live.Matchmaking.BayesianRatingModel, T:Vixen.Live.Matchmaking.MatchTicket, T:Vixen.Live.Matchmaking.MatchPool, T:Vixen.Live.Matchmaking.MatchProposal, T:Vixen.Live.Matchmaking.IMatchFunction, T:Vixen.Live.Matchmaking.IMatchEvaluator, T:Vixen.Live.Matchmaking.HighestQualityEvaluator, T:Vixen.Live.Matchmaking.Matchmaker, T:Vixen.Live.Cluster.IQueueGrain, T:Vixen.Live.Cluster.QueueEntry, T:Vixen.Live.Cluster.QueueTicket, T:Vixen.Live.Cluster.QueueTicketState, T:Vixen.Live.Cluster.QueueTeam, T:Vixen.Live.Cluster.QueueMatch, T:Vixen.Live.Cluster.QueueSnapshot, T:Vixen.Live.Orchestration.QueueGrain, T:Vixen.Live.Orchestration.QueueState, T:Vixen.Live.Orchestration.IQueueMatcher, T:Vixen.Live.Orchestration.PairMatcher]
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

**`IQueueGrain`** is the same cycle as a fleet-wide single writer, one grain per queue id, with
`QueueTicket` and `QueueMatch` as its vocabulary.

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

### One queue, one writer, and the scoring stays a pure function

`Matchmaker` is testable without a silo and `IQueueGrain` is the scheduling decision on top of it —
the same relationship `PlacementDirector` has to `IMapGrain`. ⚠ **A matchmaker only exercisable
inside a silo is one nobody tests.**

Four rules the grain adds, each for a failure the pure function cannot see:

⚠ **Formed is not started.** A roster still needs a shard and allocating one can fail, so the tickets
are *held* and the caller confirms or abandons. It is a reservation, at a different scale from L2's.

⚠ **Abandoning keeps the original enqueue time.** Otherwise a ticket is punished for a failure that
was the fleet's — you wait twenty minutes, the allocation fails, and you go to the back of the queue.

⚠ **Backfill is preferred to a new match**, and `Cycle` processes backfills first for that reason. A
running game with an empty seat is a worse experience than one that has not started.

⚠ **A ticket is a party and never a player**, which is what makes *"never split a group"* a property
of the types rather than a rule somebody has to remember in the match function.

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
