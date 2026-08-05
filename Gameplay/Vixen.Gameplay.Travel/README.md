# Vixen.Gameplay.Travel

Portals, waypoints, taxis, summons and instance doors — the fiction over doc 27's one transfer
mechanism.

Spec: [docs/plan/28](../../docs/plan/28-gameplay-framework.md) § Travel, part of **G7**.

## State

**Built: five kinds over one mechanism, unlocks, fares, requirements, and the order a realm carries
out. 14 tests.**

| | |
|---|---|
| `TravelPointDefinition` · `TravelKind` · `TravelPoint` · `TravelLibrary` | What a designer authors, compiled. |
| `Travelling` · `TransferOrder` · `TravelRefusal` | Whether they may, and what to hand the realm. |
| `TravelModule` | One definition type and no tags of its own. |

## It adds the fiction and nothing else

Doc 28 is unusually explicit: *"every one of them resolves to `RequestTransfer`, and the **only** thing
this library adds is the fiction: the cost, the unlock, the requirement query, and the UI. That is the
payoff of doc 27's protocol being one mechanism — a game adds a new way to travel by authoring a
definition."*

So the whole output of this library is a **`TransferOrder`**. Nothing here moves a player; anything
that did would be a second transfer protocol, which is precisely what doc 27's being one mechanism was
for.

⚠ **It does not take the fare either.** The order carries what is owed and the caller's ledger moves
it — for the general reason every other cost in this framework works that way, and for the specific
one that a fare taken here and a transfer that then fails is a player who paid to stay where they were.

## The three things worth knowing before reading the code

### An unlock is a tag, not a reference to exploration

A waypoint is unlocked by finding it, and doc 28's spine forbids `Travel → Exploration`. A tag granted
by a point of interest and asked about here is the same requirement algebra as everything else — and
it is better than the edge would have been, because a waypoint can then be unlocked by a quest, a
purchase or anything else that grants a tag. It also means a game with portals does not carry a fog
bitmap.

### The unlock is checked before the requirements

"You have not found this yet" is the answer a player needs. "You are not level thirty" is noise when
they cannot see the waypoint at all.

### The kinds change nothing

Five values on an enum, and the machinery does not branch on any of them. `TravelKind` is what a
client draws and what a designer thinks in. If a fourth kind of thing ever needs different *behaviour*,
that is the moment to notice that this library had stopped being only the fiction.

## What is owed

- **The realm side.** Handing a `TransferOrder` to `RequestTransfer`, charging the fare through the
  ledger, and reconciling a failure is task **#27**'s bridge.
- **Taxi routes as routes.** `Seconds` is a duration and a real taxi is a path with waypoints along it;
  what flies the griffon is the scene's and no part of it is here.
- **"Join my friend."** `TravelKind.Summon` is authored and the destination is a map — resolving it to
  *where a particular player is* needs the party grain, which doc 28's spine puts out of reach and
  doc 27's placement already answers.
