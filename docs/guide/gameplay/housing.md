---
title: Player housing
slug: gameplay/housing
kind: guide
area: Gameplay
summary: Plots, furniture, a budget, a snap that the client and the realm both call, and a five-rung permission ladder that a ban beats outright — with no clock anywhere in it, which is what makes ten thousand houses ten thousand rows.
api: [T:Vixen.Gameplay.Housing.HouseSurface, T:Vixen.Gameplay.Housing.HouseAction, T:Vixen.Gameplay.Housing.HouseTier, T:Vixen.Gameplay.Housing.HousingRefusal, T:Vixen.Gameplay.Housing.HouseOwner, T:Vixen.Gameplay.Housing.FurnitureDefinition, T:Vixen.Gameplay.Housing.PlotDefinition, T:Vixen.Gameplay.Housing.Furniture, T:Vixen.Gameplay.Housing.Plot, T:Vixen.Gameplay.Housing.HousingLibrary, T:Vixen.Gameplay.Housing.Placement, T:Vixen.Gameplay.Housing.HousePlot, T:Vixen.Gameplay.Housing.HousingModule]
tags: [gameplay, housing, decoration, permissions, mmo]
since: 0.1
status: preview
related: [gameplay/collections, gameplay/social, gameplay/requirements]
---

## What it is

A **`PlotDefinition`** is a kind of house: how much fits in it, what surfaces it has, how finely it
snaps, and how much standing each thing you can do in one takes. A **`FurnitureDefinition`** is
something you put in it. **`HousePlot`** is one house — who may do what, and what is down.

## What it is for

Everything a housing feature needs that is not geometry: a budget that keeps ten thousand plots
loadable, a snap the client's preview and the realm's validity check both call, a permission ladder
with visitors and bans, and a layout that survives a patch.

⚠ **Nothing in this library has a clock.** No method takes a `now`. That is not an omission — it is
what makes a plot hibernate, and hibernation is what makes housing affordable. Anything that ages is
the caller's, as a timestamp compared on load rather than a process that keeps running.

Geometry is deliberately absent. Whether a point is on a wall, whether two chairs intersect, whether
a table is inside the room: all three need the scene, and this library is *told* which surface the
caller found rather than working it out.

## Using it

Compile once, make a plot per house, and place:

```csharp compile
using Vixen.Core.Mathematics;
using Vixen.Gameplay;
using Vixen.Gameplay.Housing;

static class Moving {
    public static HousePlot In(DefinitionCatalog catalog, PlayerId owner) {
        var library = HousingLibrary.Compile(catalog);
        var plot = library.FindPlot(DefId.From("housing/cottage"))!;

        return new(library, plot, HouseOwner.Of(owner));
    }

    public static Placement? Put(HousePlot house, Furniture what, Vector3 where, float yaw, PlayerId who) =>
        house.Place(who, what, HouseSurface.Floor, where, yaw, out var placed) == HousingRefusal.None
            ? placed
            : null;
}
```

`Place` never throws and never partially applies. Every no is a `HousingRefusal` with a reason:
`OutOfBudget`, `TooMany`, `WrongSurface`, `Forbidden`, `Banned`, `Requirements`.

⚠ **Snapping happens before the checks and the snapped value is what is stored.** A caller that
validated the raw point and stored the rounded one has a house whose furniture drifts every time
somebody logs in. Call `plot.Snap` and `plot.SnapYaw` for the client's preview so both ends agree.

⚠ **`HousePlot` reports and never holds.** `Remove` hands back the `DefId` that came out; whether it
goes to a bag, to the mail or nowhere is the caller's, exactly as with a quest's rewards.

### Standing

Five rungs — `None`, `Visitor`, `Guest`, `Resident`, `Owner` — and four verbs. A plot's definition
says how much standing each verb takes, and the house's `Openness` says what a stranger counts as.

⚠ **A ban is not a rung and must never become one.** On a house open to the public everybody is at
the bottom, so a ban expressed as the bottom does nothing at all. `Ban` is a separate set and beats
the ladder outright.

⚠ **An owner cannot be banned or demoted**, and **nobody may grant standing at or above their own.**

### Guild halls

A `HouseOwner` is a player or a `Guid`. A guild plot has no implicit owner, so `Grant` cannot
bootstrap it: use **`Assign`**, which is unchecked and belongs to the authority rather than to a
player. The guild's rank matrix is applied wholesale by whoever holds the guild and arrives already
resolved. `Bar` and `Open` are its siblings, and all three are also what loads a save.

## Examples

A house with a forge in it grants a tag, which is how a recipe reaches it without either library
knowing about the other — and loading a save is deliberately not a replay:

```csharp compile
using Vixen.Core.Mathematics;
using Vixen.Gameplay;
using Vixen.Gameplay.Housing;

static class Furnishing {
    public static GameplayTagSet Forge(HousePlot house, Furniture forge, PlayerId who, IRequirementContext context) {
        var tags = new GameplayTagSet();

        // The tag lands in `tags` while the forge is down, and a recipe's requirement asks about it.
        house.Place(who, forge, HouseSurface.Floor, Vector3.Zero, 0f, out _, context, tags);

        return tags;
    }

    public static bool Load(HousePlot house, IEnumerable<Placement> saved) {
        house.Restore(saved);

        // A patch may have lowered the budget since. The layout still loads; what to do about it is
        // the game's decision, not this library's.
        return house.Free >= 0;
    }
}
```

## See also

- [Collections, achievements and transmog](collections.md) — the other half of G8.
- [Guilds and groups](social.md) — where a guild hall's permission matrix comes from.
- [Requirements](requirements.md) — what gates a piece of furniture.
