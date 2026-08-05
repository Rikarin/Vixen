# Vixen.Gameplay.Pvp

Arenas, battlegrounds and duels over four composable objective types, so that a new battleground is a
map plus a `.vxdef`.

Spec: [docs/plan/28](../../docs/plan/28-gameplay-framework.md) § PvP, part of **G6**.

## State

**Built: four objective kinds, capture with contest, per-tick and on-capture scoring, win conditions,
rounds and forfeits. 25 tests.** Matchmaking is G6's third part and is owed.

| | |
|---|---|
| `PvpMapDefinition` · `PvpObjectiveDefinition` · `PvpObjectiveKind` · `MatchKind` | What a designer authors. |
| `PvpMap` · `PvpObjective` · `PvpLibrary` | Compiled once, with a `Problems` list. |
| `PvpMatch` · `ObjectiveState` · `MatchOutcome` · `PvpRefusal` | One match. |
| `PvpModule` | One definition type and three tags. |

## The four things worth knowing before reading the code

### Progress is one signed meter, not two

Taking a point back has to push the owner's meter down to nothing and then push its own up. Two
separate per-team meters would let a point flip the instant the last defender dies, because the
attackers' meter had been filling the whole time they were being held off. One meter is what makes a
point take time to lose as well as to gain.

### A contested objective is frozen, not slowed

Not "the bigger group wins slowly" — frozen, in both directions. The alternative makes head-count the
whole game, and it makes standing on a point you already hold worth doing, which is how a battleground
turns into everybody sitting still.

⚠ **Contesting freezes the capture and not the scoring.** You keep the points until the flag actually
flips, which is what makes defending worth anything.

### Score ticks for holding and pays once for taking

Both, because they are different mechanics: resource control is won by holding more for longer, and a
flag is won by taking it. A map uses whichever of the two numbers it sets, and the library reports a
map that sets neither as a problem.

⚠ **The clock is checked after the score**, so reaching the winning number on the tick the clock
expires is a win rather than a draw — the same rule the dynamic-event director has.

⚠ **A draw is a real outcome.** Out of rounds with nobody holding a majority ends drawn; inventing a
tiebreak here would be inventing one every game then has to use.

### It has no combat in it

An objective is a meter and a score. What kills the person standing on it is `Vixen.Gameplay.Combat`'s
business, and the spine's `Pvp → Combat` edge is deliberately not taken. Flagging is a tag, which is
the kernel's.

⚠ **`Occupy` is told who is standing there** rather than working it out. Deciding who is inside a
capture radius needs the physics scene, and a PvP library that owned that would be a second interest
query.

## What is owed

- **Matchmaking**, G6's third part: `MatchTicket`, pools as tag-and-range queries, `IMatchFunction`,
  `IMatchEvaluator`, and the two rating models — Elo and a TrueSkill-family Bayesian one — behind
  `IRatingModel`. Doc 28's named tests for it are rating against published reference sequences, a
  party never being split, and queue times bounded under synthetic arrival traces.
- **World-PvP flagging.** `PvpModule` declares `Pvp.Flagged` and nothing sets it. The rule worth
  having is that flagging is instant and unflagging is delayed, or you attack somebody and walk away.
- **Payload movement.** `PvpObjectiveKind.Payload` is authored and scores like a capture point; a
  track with a position on it needs the scene, which is the same boundary `Occupy` sits on.
