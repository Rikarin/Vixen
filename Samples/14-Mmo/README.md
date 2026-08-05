# 14-Mmo

A World of Warcraft-shaped vertical slice: four maps, three classes, a five-player dungeon, a
ten-a-side battleground, and a guild with a hall — authored as content against **every one of
[doc 28](../../docs/plan/28-gameplay-framework.md)'s twenty libraries** and run on
[doc 27](../../docs/plan/27-mmo-framework.md)'s fleet.

Spec: doc 28 § Cost's *"a social builder"* row, and doc 27's soak — three maps, eight realms, five
hundred connections, thirty minutes, a rolling upgrade in the middle.

## State

**Built: the content, the seven projects and the composition. 112 definition files, four maps,
61 tests.**

**Owed:** the soak (**#37**), the UI (**#38**).

| | Sees | |
|---|---|---|
| `Assets/**` | — | 112 `.vxdef` and friends, and four `.vxscene` layouts. |
| `Mmo.Contracts` | everybody | `[Replicated]` components, broadcasts, the gate's DTOs, the map names. No Orleans, no engine, AOT-clean. |
| `Mmo.Shared` | client, realm | The composition, the compiled libraries, and the rules both ends run. |
| `Mmo.Cluster` | realm, orchestrator, gate | One grain the sample adds: `IWorldEventGrain`. |
| `Mmo.Realm` | — | Composes all twenty libraries, holds the four bridges, runs `RealmApp.Run<MmoRealm>`. |
| `Mmo.Gate` | — | Region, maps, version. Forty lines, and that is the point. |
| `Mmo.Orchestrator` | — | A silo. The game's grains need no registration beyond being referenced. |
| `Mmo.Client` | — | Headless: sign in, pick a character, get a ticket, connect. |
| `Mmo.Content.Tests` | — | The real importer over the real tree, every library's `Problems`, and the cross-references. |
| `Mmo.Realm.Tests` | — | That twenty libraries actually compose. |

⚠ **The reference graph is doc 27 § The three assemblies a game writes, and the absences are the
load-bearing part.** `Mmo.Contracts` has no Orleans, so ADR-017 is mechanical rather than remembered
— the client cannot reach a grain because the types are not in an assembly it links. `Mmo.Gate` has
no `Mmo.Shared`, because a login service should not load twenty gameplay libraries. `Mmo.Orchestrator`
has no `Mmo.Shared` either: a grain that needed the gameplay libraries would be a grain doing
simulation, which is the one thing the control plane is not for.

## The maps

| | Kind | What it is for |
|---|---|---|
| **Greenmarch** | public | The starter valley. Levels 1–8, two boar camps, a forge, the first two quests. |
| **Thornwood** | public | Levels 8–20, and **the transfer target** — quest 03 crosses the border, which is what makes L2's handover a thing a player does rather than a test. |
| **Barrowdeep** | `Instance` | Five players, three encounters, a daily lockout on normal and a weekly one on heroic. |
| **Ravensford** | `Match` | Ten a side, three capture points and a payload. Doubles as a three-a-side arena. |

⚠ **The four scenes are named roots and transforms only, and that is deliberate.** Every mesh
reference in a `.vxscene` is a guid minted by `vixen import` and recorded in a committed `.meta`, so
a scene cannot name a model this sample does not ship. What the fleet needs from a map is *where
things are*; the game reads these roots by name and puts its own components on them.

## What each library gets

| Library | Where |
|---|---|
| `Items` | Four rarities, an affix pool, ten items. `Storied` rolls three affixes and only the Barrow King drops one. |
| `Inventory` | ⚠ **No definitions, and that is not a gap** — a container is sized by what the game hands it, and `items/wardens-pack` is where the number lives. |
| `Loot` | Six tables, one nested inside another, and a pity policy on the Barrow King. |
| `Combat` | Nine abilities across three classes: a threat opener, a cast, a channel, a cone, a ground target. |
| `Shooting` | One hitscan rifle with falloff, penetration, spread and a recoil pattern — so the lag-compensation path is live. |
| `Progression` | A 1–20 curve, three talent trees, three specialisations, two professions, two reputations. |
| `Quests` | A five-step chain across both public maps, a daily, and two dynamic events that chain into each other. |
| `Social` | Party, raid, battleground team, and a four-rank guild charter. |
| `Chat` | Say, party, guild, whisper, trade — two realm-routed and three over the gate. |
| `Economy` | Three currencies (one account-scoped and capped, one decaying), two vendors. |
| `Instances` | Barrowdeep, two difficulties, three encounters, one of them a gate. |
| `Pvp` | Ravensford and an arena variant on the same scene. |
| `Interaction` | Ore, herbs and a forge — two nodes and a station. |
| `Crafting` | Three recipes, one per `RecipeSource`: known, taught and **discovered**. |
| `Exploration` | Two charts, nine points of interest, a 64×48 and a 64×64 fog grid. |
| `Travel` | Two waypoints, a flight path, an instance entrance and a hearthstone. |
| `Movement` | A ground mount, a flying mount, and Ravensford's four-seat payload waggon. |
| `Ai` | Two spawn camps with leashes — see below. |
| `Housing` | A guild freehold and four pieces of furniture. |
| `Collections` | Eight collectibles and five achievements, one of which cascades off the others. |

## What writing the content found

### `Vixen.Gameplay.Ai` had a leash with nowhere to author one

`LeashDefinition` is `[DataContract]`-shaped and looks like content, but it is not a `Definition`,
so it has no address — and nothing in the library referenced one. Doc 28's AI section pairs leashing
with spawn tables and the two had never met: a spawn table said what lived in a camp and nothing said
how far it could be pulled from it. Every camp in a game had to be leashed from code.

`SpawnTableDefinition.Leash` is that seam. It goes on the **table** because a leash is about a place
and the table is the only thing in the library that names one, and it compiles to **one definition
with a `Leash` per mob** — a camp of eight sharing one leash would have eight mobs give up the moment
the first of them did.

The library also gained the check the sample would have wanted: a tether that is not inside its break
is one radius wearing two names, which is exactly the flicker two radii exist to prevent.

### A composition is what makes content readable at all

⚠ **The content test composes the twenty modules before it reads a single file, and it has to.** A
definition's `!Tag` is resolved through `SerializerRegistry`, which is filled by a **module
initializer** in each library's own assembly — and a module initializer runs when the assembly
*loads*. A project that `ProjectReference`s twenty libraries and never touches a type from nineteen
of them gets nineteen assemblies the runtime never loaded, and every file fails to import with
*"nothing in this build claims the name"* about a type sitting in the build output.

The fix is not to touch a type per assembly. It is to declare the composition, which a game has to do
anyway: `Use<TModule>()` has a `new()` constraint, so the constructor call is emitted at the call
site and the assembly is a hard compile-time dependency rather than something a trimmer can decide
nobody used.

### And the composition's tags have to reach the catalog

⚠ Most tags get into the table because a definition mentions them. **A tag only *code* knows never
does** — and `Event.Kill` is the one that matters, because it is the verb a Kill objective counts,
`QuestModule` declares it, and no quest file mentions it anywhere. Without seeding the catalog from
`Composition.Tags`, every objective in the game compiles to one that nothing can ever advance. The
library says so, which is the only reason it was noticed.

### Three things the libraries refused, and were right to

- **A whisper routed through the realm.** The person being whispered may be on another shard; a realm
  cannot reach them. `ChatRoute.Gate` is the only legal answer for `Direct`.
- **A station with a respawn timer.** The forge never runs out, so there is nothing to respawn — and
  the default is a timer, so it has to be zeroed on purpose.
- **A leash whose tether equals its break.** See above.

### Nothing checks a cross-library address reference, and structurally nothing can

⚠ A loot entry names an **item**, a vendor row names an item and a currency, a recipe names items and
a profession, a quest reward names four kinds of thing — and not one of those references is validated
by any library. Doc 28's spine allows only `Items` and `Combat` to be depended on, so
`Vixen.Gameplay.Loot` has no way to ask whether `items/marchguard-plate` is anything; it checks a
nested *table* because a table is its own.

⚠ And a `DefId` cannot report the difference. It hashes the address, so **an id for nothing is
indistinguishable from an id for something** — a misspelt reference resolves to a perfectly good
number for a definition that does not exist, and the failure is a null in whatever code path first
needs it, at whatever hour a player first kills the thing that was supposed to drop it.

`Mmo.Content.Tests/ReferenceTests.cs` is that check, in the only place it can currently be written:
the only project in the repository that compiles every library against one catalog. **It is the piece
of this sample most worth copying.** Task #42 is the engine-side version.

### What `Mmo.Shared` turned out not to need

Doc 27 frames the shared assembly as where the predicted step and the damage formula live. Writing it
found that **most of that is already shared**, because the gameplay libraries are linked by both
ends: `AbilityTemplate.BaseAmount` *is* the damage formula and `RequirementSet.IsMetBy` *is* what
greys a button out and refuses a packet. Wrapping either here would create exactly the second
implementation the assembly exists to prevent.

What is left is the arithmetic the game owns — how fast a mount is, which attribute a class spends,
how a level and a stat become a health bar — and it is there because nothing in the engine could have
guessed it.

⚠ One more thing moved for a reason worth knowing: the **map names** are in `Mmo.Contracts`, not
`Mmo.Shared`. The gate needs them and the orchestrator needs them, and neither simulates anything —
so the *names* are wire vocabulary and the *ids* are the simulation's. A `DefId` in Contracts would
drag `Vixen.Gameplay` into a login service.

## Running the check

```bash
dotnet test Samples/14-Mmo/Mmo.Content.Tests/Mmo.Content.Tests.csproj
```

```bash
dotnet test Samples/14-Mmo/Mmo.Realm.Tests/Mmo.Realm.Tests.csproj
```

It imports the tree through `DefinitionImporter` — the same code the editor and `vixen import` run —
and asserts every library's `Problems` list is empty. ⚠ **Every assertion is on an empty list rather
than a count**, because asserting a count means the next real problem is absorbed by an off-by-one
somebody updates without reading.

It also asserts the coverage claim above: a sample that says it exercises twenty libraries and
exercises seventeen is worse than one that says seventeen, because somebody reads the list, does not
find the example they came for, and concludes the library does not work.

## See also

- [`docs/plan/28`](../../docs/plan/28-gameplay-framework.md) — what the twenty libraries are.
- [`docs/plan/27`](../../docs/plan/27-mmo-framework.md) — the fleet the realm half will run on.
- [`docs/guide/live/gameplay-bridge`](../../docs/guide/live/gameplay-bridge.md) — where the two meet.
