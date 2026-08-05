# Vixen.Live.Matchmaking

Open Match's model without its deployment: tickets that are parties, pools as tag-and-range queries,
a match function the game writes, and two rating models it chooses between.

Spec: [docs/plan/28](../../docs/plan/28-gameplay-framework.md) § Instances, PvP, matchmaking — the
third part of **G6**, and the one that belongs in `Live/`.

## State

**Built: `MatchTicket`, `MatchPool`, `IMatchFunction`, `IMatchEvaluator` with its oldest-first
default, the widening band, and both rating models. 28 tests, and G6 with them.**

| | |
|---|---|
| `Rating` · `IRatingModel` | Two numbers even for Elo, so nothing downstream learns which model a queue uses. |
| `EloRatingModel` | One number, and anybody can check it. |
| `BayesianRatingModel` | A TrueSkill-family mean and variance. |
| `MatchTicket` · `MatchPool` · `MatchProposal` | Open Match's ticket and pool. |
| `IMatchFunction` · `IMatchEvaluator` · `HighestQualityEvaluator` | Propose, then resolve. |
| `Matchmaker` | One queue: snapshot, propose, evaluate, remove. |

## What was taken from Open Match, and what was not

Doc 28 is explicit that the **separation** is the insight and the Kubernetes-and-Go topology is not —
the same objection doc 27 ADR-019 already made about the substrate. So:

| Open Match | Here |
|---|---|
| Ticket | `MatchTicket` — a party, with tags, a rating and latency samples |
| Pool | `MatchPool` — a tag query and a range, over the gameplay requirement algebra |
| Match function | `IMatchFunction` — the game's code, given a pool snapshot |
| Evaluator | `IMatchEvaluator`, default highest quality with ties to the oldest ticket |
| Director | **Not here.** `IMapGrain.Place` is doc 27's, and a second allocator is what this must not become |

`Matchmaker.Matched` is where a caller hands an accepted match to placement.

## The five things worth knowing before reading the code

### A ticket is a party, never a player

That is how doc 28's *"a party is never split"* becomes a property of the **types** rather than a rule
somebody has to remember. A solo player is a party of one, and nothing downstream is ever handed one
member of a group, so nothing downstream can separate them.

### `default(MatchPool)` is not `MatchPool.Everybody`, and that cost a debugging session

A positional record struct's parameter defaults belong to its **constructor**. `default` zeroes every
field — which for this type means a rating band of exactly zero and a maximum latency of nothing: a
pool that admits nobody. The first `Matchmaker` took `MatchPool pool = default` and every queue built
with it silently refused every ticket. `Everybody` is now spelled out and the parameter is nullable.

### The evaluator's tie-break is what bounds a queue time

Quality alone starves a ticket nothing pairs well with: it loses to a better proposal every cycle and
waits for ever. Oldest-first is doc 28's named default for exactly that reason.

⚠ **So is the widening band**, which is on the queue rather than in the match function because every
mode needs it and none of them should have to write it. ⚠ **It uses the *wider* of two tickets'
bands, not the narrower** — somebody who has waited ten minutes must be matchable with a newcomer, or
they can only ever be paired with somebody equally starved. `WithoutAWideningBandTheEndsOfTheLadderStarve`
is the test that shows what happens otherwise.

### Both rating models refuse a free-for-all rather than approximating one

Elo is a two-sided formula and the Bayesian model here is the two-team closed form. The full TrueSkill
handles any number of teams with a factor graph over the whole ranking; shipping the closed form
honestly is better than shipping an approximation of the general one that nobody can check. **A queue
with three sides is a real gap.**

⚠ **Elo rates a team as the mean of its players, not the sum.** A sum makes a five-player team five
times as strong as one player and matches it against nobody.

⚠ **The Bayesian model adds a little uncertainty back before every update.** Without it a rating
converges to a variance of nearly nothing and can never move again, so somebody who improves is stuck
at what they used to be.

### The numerics are there so the reference figures come out right

`Gaussian` uses Cody's rational erfc and Acklam's quantile with a Halley refinement. That is what makes
`ThePublishedTrueSkillOneOnOneFiguresComeOut` land on 29.396 ± 7.171 to three decimal places rather
than nearly — and doc 28 § Testing asks for rating models *"against published reference sequences"*,
which is only a test if the published numbers actually come out.

## What is owed

- **Free-for-all ratings**, above.
- **The grain.** Doc 28 says a ticket is *"a grain-held record"* and this is an in-memory queue; the
  `IQueueGrain` that holds one per queue definition is doc 27's shape and is not built.
- **Backfill** for a player who leaves a match in progress — doc 28 names it and nothing here does it.
- **Role-aware matching.** Roles are tags and a pool can filter on them, but "two tanks and three
  damage" is a shape a match function has to know, and no shipped one does. The `Teams` function in
  the tests is a fixed-size filler and is deliberately test-only.
