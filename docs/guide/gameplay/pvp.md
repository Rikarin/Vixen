---
title: Arenas, battlegrounds and objectives
slug: gameplay/pvp
kind: guide
area: Gameplay
summary: Four composable objective types with one signed capture meter, so a contested point is frozen rather than slowly losing, and a new battleground is a map plus a .vxdef.
api: [T:Vixen.Gameplay.Pvp.MatchKind, T:Vixen.Gameplay.Pvp.PvpObjectiveKind, T:Vixen.Gameplay.Pvp.PvpRefusal, T:Vixen.Gameplay.Pvp.MatchOutcome, T:Vixen.Gameplay.Pvp.PvpObjectiveDefinition, T:Vixen.Gameplay.Pvp.PvpMapDefinition, T:Vixen.Gameplay.Pvp.PvpObjective, T:Vixen.Gameplay.Pvp.PvpMap, T:Vixen.Gameplay.Pvp.PvpLibrary, T:Vixen.Gameplay.Pvp.ObjectiveState, T:Vixen.Gameplay.Pvp.PvpMatch, T:Vixen.Gameplay.Pvp.PvpModule]
tags: [gameplay, pvp, arena, battleground, objectives, mmo]
since: 0.1
status: preview
related: [gameplay/instances, gameplay/combat, gameplay/tags, live/matchmaking]
---

## What it is

A **`PvpMap`** is a match's shape: how many teams, how big, what it takes to win, how long it runs, and
its objectives. A **`PvpMatch`** is one being played. There are four **`PvpObjectiveKind`**s — capture
point, payload, flag return and resource control — and the list is meant to stay short.

## What it is for

Doc 28: *"a small set of composable node types with scoring and win conditions, so a new battleground
is a map plus a `.vxdef`"*. Every battleground anybody has shipped is these four arranged differently.

## Using it

Compile a `PvpLibrary`, make a `PvpMatch`, `Join` people to teams, tell it who is standing on each
objective with `Occupy`, and `Tick` it.

⚠ **`Occupy` is told who is there rather than working it out.** Deciding who is inside a capture radius
needs the physics scene, and a PvP library that owned that would be a second interest query.

⚠ **Progress is one signed meter.** Taking a point back pushes the owner's meter down to nothing and
then pushes its own up; two per-team meters would flip a point the instant the last defender dies.

⚠ **A contested objective is frozen in both directions**, not slowed. Otherwise head-count is the whole
game and standing on a point you already hold is worth doing.

⚠ **Contesting freezes the capture, not the scoring** — you keep the points until the flag flips.

⚠ **The clock is checked after the score**, so reaching the winning number as time expires is a win.
⚠ **A draw is a real outcome**; inventing a tiebreak here would be inventing one for every game.

## Examples

A three-point battleground:

```yaml
# Assets/Pvp/basin.vxdef
!PvpMapDefinition
displayName: The Basin
kind: Battleground
scene: maps/basin
teams: 2
teamSize: 10
scoreToWin: 1500
timeLimit: 900
objectives:
  - { id: mine, kind: ResourceControl, captureSeconds: 8, pointsPerTick: 1, tickSeconds: 2 }
  - { id: farm, kind: ResourceControl, captureSeconds: 8, pointsPerTick: 1, tickSeconds: 2, startingOwner: 0 }
  - { id: flag, kind: FlagReturn, captureSeconds: 2, pointsOnCapture: 100 }
```

Running one:

```csharp compile
using Vixen.Gameplay;
using Vixen.Gameplay.Pvp;

static class Basin {
    public static bool Step(PvpMatch match, int objective, int red, int blue, float delta) {
        // Whoever is inside the radius is the scene's answer, not this library's.
        match.Occupy(objective, [red, blue]);

        return match.Tick(delta);
    }

    public static string Result(PvpMatch match) =>
        match.Outcome switch {
            MatchOutcome.Draw => "drawn",
            MatchOutcome.Running => "still going",
            _ => $"team {match.Winner}"
        };
}
```

Watching a point change hands:

```csharp compile
using Vixen.Gameplay.Pvp;

static class Announcer {
    public static void Listen(PvpMatch match) =>
        match.Captured += (state, team) =>
            Say(team < 0 ? $"{state.Objective.Id} is neutral" : $"team {team} took {state.Objective.Id}");

    static void Say(string what) { }
}
```

## See also

- [Instances](gameplay/instances) — G6's other half.
- [Combat](gameplay/combat) — what kills the person standing on the point.
- [Gameplay tags](gameplay/tags) — what flagging is.
