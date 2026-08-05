# Vixen.Gameplay.Ai

Leashing and spawn tables. What is left of doc 28's AI section once everything else found its home.

Spec: [docs/plan/28](../../docs/plan/28-gameplay-framework.md) § AI, part of **G7**.

## State

**Built: leashing with two radii and a patience clock, and spawn tables with deterministic jitter.
23 tests, and G7 with them.**

| | |
|---|---|
| `LeashDefinition` · `Leash` · `LeashState` · `LeashBehaviour` · `LeashVerdict` | How far a mob may be pulled. |
| `SpawnTableDefinition` · `SpawnEntryDefinition` · `SpawnTable` · `SpawnLibrary` | What lives somewhere. |
| `Spawner` · `SpawnOrder` | One camp. |
| `GameplayAiModule` | One definition type. |

## It is much smaller than doc 28's cost row implies, and that is the finding

Doc 28 § Cost says G7's AI is *"aggro, spawning and encounter scripting only, on doc 37's P0–P6"*. By
the time G7 arrived, most of that had already been built somewhere better:

| Doc 28 named | Where it actually is |
|---|---|
| Threat, aggro, taunt | `Vixen.Gameplay.Combat`'s `ThreatTable`, built at **G2** |
| Planners, blackboard, action surface, perception | `Core/Vixen.Ai`, built per doc 37 |
| Encounter scripting | An address on `Vixen.Gameplay.Instances`' `EncounterDefinition`, pointing at a behaviour tree |
| **Leashing, spawn tables** | **Here** |
| Dialogue | Owed — see below |

So this library references neither `Core/Vixen.Ai` nor `Vixen.Gameplay.Combat`: a leash is a distance
and a clock, and a spawn table is a weighted pick.

## The three things worth knowing before reading the code

### A leash has two radii, and that is the reason it is a type

One radius makes a mob standing on the boundary flicker between chasing and resetting once per frame,
because the player is moving and the comparison keeps changing sides. The **tether** is where it starts
worrying and the **break** is where it gives up; nothing happens in between, and coming back inside the
*tether* — not merely inside the break — is what clears it.

⚠ **Patience is the third case**, and without it a mob can be kited round a pillar for ever at exactly
tether-plus-one.

⚠ **`HealsOnReset` is true by default.** A mob that keeps its damage across a reset gets whittled down
over a dozen pulls by one player who never has to win a fight, which is the oldest exploit in the
genre.

### A respawn timer starts at the death, not at the tick that noticed it

A server that fell behind would otherwise repopulate faster than one that did not — a difference
players feel and nobody can explain.

⚠ **The cap counts what is alive, not what has been spawned.** Counting spawns makes a camp that has
been cleared twice permanently empty.

⚠ **Jitter is not decoration.** A camp wiped in one pull comes back as one wave on a fixed timer for
ever, and every pull after the first is the same pull. The jitter is deterministic per spawner, so a
replay still matches.

### It says what to spawn and never where

Placing something needs the scene and a navigation mesh. The boundary every library in this framework
sits on, and the same one `PvpMatch.Occupy` and `InteractionNode` are on.

## What is owed

- **NPC dialogue and vendor state**, which doc 28 lists here. It is a graph with tag-gated options,
  which means the host worth reusing is `Vixen.Editor.Gameplay.Quests`' chain projection rather than a
  new one — and that is a reason to build it beside the quest editor rather than in a hurry here.
- **Patrols.** A route is a list of positions, which is the scene's, and what this library would add
  is the clock — worth doing when something needs it.
