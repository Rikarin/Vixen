---
title: Maps, discovery and travel
slug: gameplay/travel
kind: guide
area: Gameplay
summary: A revealed-area bitmap per character per map, completion that only counts what a designer opted in, and five ways of getting somewhere that all resolve to one transfer.
api: [T:Vixen.Gameplay.Exploration.PointKind, T:Vixen.Gameplay.Exploration.PointOfInterestDefinition, T:Vixen.Gameplay.Exploration.MapDefinition, T:Vixen.Gameplay.Exploration.PointOfInterest, T:Vixen.Gameplay.Exploration.MapChart, T:Vixen.Gameplay.Exploration.ExplorationLibrary, T:Vixen.Gameplay.Exploration.ExplorationRecord, T:Vixen.Gameplay.Exploration.ExplorationModule, T:Vixen.Gameplay.Travel.TravelKind, T:Vixen.Gameplay.Travel.TravelRefusal, T:Vixen.Gameplay.Travel.TravelPointDefinition, T:Vixen.Gameplay.Travel.TravelPoint, T:Vixen.Gameplay.Travel.TransferOrder, T:Vixen.Gameplay.Travel.TravelLibrary, T:Vixen.Gameplay.Travel.Travelling, T:Vixen.Gameplay.Travel.TravelModule]
tags: [gameplay, exploration, map, travel, waypoints, mmo]
since: 0.1
status: preview
related: [gameplay/requirements, gameplay/economy, gameplay/tags, gameplay/movement]
---

## What it is

A **`MapChart`** is what is on a map worth finding; an **`ExplorationRecord`** is one character's
discoveries and their fog. A **`TravelPoint`** is a portal, a waypoint, a taxi, a summon or an
instance door, and **`Travelling`** says whether somebody may use one.

## What it is for

Doc 28: exploration is *"points of interest, map discovery with a revealed-area bitmap per character,
vistas, waypoint unlocks and completion percentages"*, and travel is the client-facing half of doc 27's
transfer — *"the only thing this library adds is the fiction"*.

## Using it

Compile both libraries. Call `Reveal` as somebody moves and `Discover` when they reach something; call
`Travelling.Order` and hand what comes back to the realm.

⚠ **Completion counts only points marked `counts`, and is computed rather than stored** — otherwise a
patch that adds one un-completes a finished map for everybody.

⚠ **Fog is revealed and never re-hidden**, and the reveal is a square: the difference is invisible
under a fog texture and a circle costs a multiply per cell on the hottest call here.

⚠ **A waypoint's unlock is a tag**, so travel never references exploration — and a waypoint can
therefore be unlocked by a quest or a purchase too.

⚠ **The unlock is checked before the requirements**: "you have not found this" is the useful answer.

⚠ **Nothing here moves a player or takes a fare.** `Order` produces a `TransferOrder`; doc 27's
`RequestTransfer` and the caller's ledger do the rest — a fare taken before a transfer that fails is a
player who paid to stay put.

## Examples

A map and a waypoint that unlocks from it:

```yaml
# Assets/Maps/queensdale.vxdef
!MapDefinition
displayName: Queensdale
columns: 64
rows: 64
tag: Completion.Queensdale
points:
  - { id: ascalon, kind: Landmark, tag: Discovered.Queensdale.Ascalon }
  - { id: camp, kind: Waypoint, tag: Discovered.Queensdale.Camp }
  - { id: cache, kind: Cache, tag: Discovered.Queensdale.Cache, counts: false }

# Assets/Travel/camp-waypoint.vxdef
!TravelPointDefinition
displayName: Camp waypoint
kind: Waypoint
to: maps/queensdale
unlockedBy: Discovered.Queensdale.Camp    # the tag the point of interest grants
currency: currency/gold
cost: 150
```

Walking around and finding things:

```csharp compile
using Vixen.Gameplay;
using Vixen.Gameplay.Exploration;

static class Walking {
    public static int Moved(ExplorationRecord record, MapChart map, int column, int row) =>
        record.Reveal(map, column, row, radius: 3);

    public static float Arrived(ExplorationRecord record, MapChart map, PointOfInterest point, IRequirementContext who) {
        // Grants the point's tag, which is what unlocks the waypoint over in Travel.
        record.Discover(map, point, who);

        return record.CompletionOf(map);
    }
}
```

Going somewhere:

```csharp compile
using Vixen.Gameplay;
using Vixen.Gameplay.Travel;

static class Waypoints {
    public static TransferOrder? Use(
        TravelPoint waypoint,
        PlayerId who,
        DefId here,
        IRequirementContext context,
        IReadOnlyDictionary<uint, long> purse
    ) =>
        // An order, not a transfer, and the fare is owed rather than taken.
        Travelling.Order(waypoint, who, here, context, purse, out var order) == TravelRefusal.None ? order : null;
}
```

## See also

- [Requirements](gameplay/requirements) — what gates a point and a waypoint.
- [Currencies and trade](gameplay/economy) — what moves the fare an order names.
- [Gameplay tags](gameplay/tags) — what an unlock is.
