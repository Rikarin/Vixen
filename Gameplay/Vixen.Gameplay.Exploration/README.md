# Vixen.Gameplay.Exploration

Points of interest, map completion, and a revealed-area bitmap per character per map.

Spec: [docs/plan/28](../../docs/plan/28-gameplay-framework.md) § Exploration, part of **G7**.

## State

**Built: maps with points, discovery with requirements, completion, and fog with save and restore.
16 tests.**

| | |
|---|---|
| `MapDefinition` · `PointOfInterestDefinition` · `PointKind` | What a designer authors. |
| `MapChart` · `PointOfInterest` · `ExplorationLibrary` | Compiled once, with a `Problems` list. |
| `ExplorationRecord` | One character's map. |
| `ExplorationModule` | One definition type and two tag roots. |

## Why it is its own library

Doc 28 gives the reason and it is a good one: *"its state is a bitmap per character per map and
nothing else wants that shape"*. A quest counter is an integer, an inventory is a list, a reputation is
a number. This is thousands of bits that compress well and are read as a whole, and putting it
anywhere else makes that shape somebody else's problem.

## The four things worth knowing before reading the code

### Completion counts only what a designer marked as counting, and it is computed rather than stored

⚠ **`counts` is opt-in.** A patch that adds a point to a finished map would otherwise un-complete it
for everybody who had it at a hundred per cent, which is the one thing a completion number must never
do. Computing it from the chart means the number *moves* when content changes — which is honest, and
is why the opt-in matters.

⚠ **Completion is announced once**, on the discovery that finishes it, rather than every time anybody
asks.

### Fog is revealed and never re-hidden

There is deliberately no way to un-reveal a cell. A map that could go back to fog is a map a bug can
erase, and nobody has ever wanted the feature.

⚠ **The reveal is a square, not a circle.** The difference is invisible under a fog texture and a
circle costs a multiply per cell on the one call in this library that happens every time anybody moves.

### What finding something unlocks is a tag

That is how `Vixen.Gameplay.Travel` asks whether a waypoint is available without either library
referencing the other — the spine forbids the edge, and the tag is better anyway: a waypoint can then
be unlocked by finding it, by finishing a quest, or by buying it.

⚠ **A waypoint point with no tag is a reported problem**, because nothing could ever be unlocked by
finding it.

### The fog grid is bounded and the library says so

A four-thousand-square grid is sixteen million bits per character per map. `Compile` reports anything
over a million cells rather than letting it reach a save file.

## What is owed

- **A vista's "you have to climb here" rule**, which is a scene question — `PointKind.Vista` is
  authored and this library only knows it was found.
- **Account-wide discovery.** Doc 28 puts collections in `Live.Progression.Cluster` and a map is
  per character today; whether finding a landmark on one character reveals it on another is a game's
  decision and nothing here makes it.
- **Durability**, on the same terms as everything else: task **#27**'s bridge.
