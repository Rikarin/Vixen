# Vixen.Gameplay.Interaction

One channelled-interaction system with different definitions: mining a node, opening a chest, reading
a book, flipping a lever.

Spec: [docs/plan/28](../../docs/plan/28-gameplay-framework.md) § Interaction, part of **G7**.

## State

**Built: interactables, channels with interruption, shared and per-player node instancing, respawn,
and a reproducible yield seed. 15 tests.** G7's other four parts are owed — see below.

| | |
|---|---|
| `InteractableDefinition` · `Interactable` · `InteractionLibrary` | What a designer authors, compiled with a `Problems` list. |
| `InteractionNode` · `Channel` · `InteractionResult` | One node in the world. |
| `InteractionInstancing` · `InterruptOn` · `InteractionRefusal` | The three policies and the answers. |
| `InteractionModule` | One definition type and two tags. |

## The four things worth knowing before reading the code

### A shared node is claimed for the duration of a channel

That is the whole reason `InteractionNode` exists rather than a counter on the definition. Without a
claim, two players who start mining the same rock at the same moment both finish and it yields twice —
this library's version of the duplication bug the container algebra exists to prevent. The claim is
released on completion, on interruption, and on the claimant walking away.

### Interruption consumes nothing

A node spent by an interrupted channel would make being attacked while gathering cost the node as well
as the time, and a player who cancels by accident lose the rock. `Disturb` takes what happened and the
definition says whether that stops it, so a lever can be authored to ignore both.

### Per-player instancing is a policy, not a second kind of node

One rock, one respawn timer, and everybody gets their own go at it — Guild Wars 2's answer to
node-stealing. `RemainingFor` is per player when the definition says so and shared otherwise; nothing
above has to know which.

### Respawn counts from the completion that emptied it

Not from the last attempt. A timer a failed channel could restart is a node somebody keeps out of the
world by standing next to it starting and cancelling, and there is a test that does exactly that ten
times in a row.

⚠ **`respawnsAt` is infinity until something empties it**, so "has its timer run out" is one
comparison and a node that was never depleted cannot be spuriously refilled.

## What it deliberately does not do

**It never resolves what a node yields.** `Yields` is an address — usually a loot table's — and
`InteractionResult` carries it with a seed derived from the node, the player and when the channel
started, so the roll is reproducible from a log. Resolving it needs `Vixen.Gameplay.Loot`, and a game
with doors would otherwise carry a loot evaluator.

**It does not know where anything is.** Whether somebody is close enough to reach a node is the scene's
answer, the same boundary `PvpMatch.Occupy` sits on.

## What is owed

G7 is six libraries and this is one of them. Still owed, tracked as a task:

- **`Vixen.Gameplay.Movement`** — mounts and vehicles as one `IVehicle` with seats, which doc 28 says
  is where doc 16's parent-relative replication stops being optional.
- **`Vixen.Gameplay.Travel`** — portals, waypoints, taxis, all resolving to doc 27's `RequestTransfer`.
- **`Vixen.Gameplay.Exploration`** — points of interest and a revealed-area bitmap per character.
- **`Vixen.Gameplay.Ai`** — leashing, spawn tables and dialogue, on [37](../../docs/plan/37-ai-behaviour-trees-utility-and-goap.md)'s
  planners. Threat and aggro are already `Vixen.Gameplay.Combat`'s.
