# 14-Mmo

A World of Warcraft-shaped vertical slice: four maps, three classes, a five-player dungeon, a
ten-a-side battleground, and a guild with a hall — authored as content against **every one of
[doc 28](../../docs/plan/28-gameplay-framework.md)'s twenty libraries** and run on
[doc 27](../../docs/plan/27-mmo-framework.md)'s fleet.

Spec: doc 28 § Cost's *"a social builder"* row, and doc 27's soak — three maps, eight realms, five
hundred connections, thirty minutes, a rolling upgrade in the middle.

## State

**Complete.** The content, the ten projects, the composition, the soak and the interface: 981
definitions and 19 scenes across six zones, 142 tests, a fleet that runs thirty minutes in ten seconds
and holds every budget, and a HUD written in VXML with a stylesheet generated from one theme file.

⚠ **Nothing draws the HUD and nothing connects it**, and both absences are deliberate — see
[`Mmo.Ui`](Mmo.Ui/README.md) § What is not here. A window is `Samples/02-HelloUi`'s ninety lines of
`Program.cs`; keeping it out is what lets the whole interface suite run in a third of a second on a
machine with no GPU.

| | Sees | |
|---|---|---|
| `Assets/**` | — | 981 definitions and 19 scene layouts, **generated**. |
| `Mmo.Content.Authoring` | — | The generator. Tables in, `Assets/` out. |
| `Mmo.Contracts` | everybody | `[Replicated]` components, broadcasts, the gate's DTOs, the map names. No Orleans, no engine, AOT-clean. |
| `Mmo.Shared` | client, realm | The composition, the compiled libraries, and the rules both ends run. |
| `Mmo.Cluster` | realm, orchestrator, gate | One grain the sample adds: `IWorldEventGrain`. |
| `Mmo.Realm` | — | Composes all twenty libraries **at boot, in the process**, holds the four bridges, drives the world's camps every tick, runs `RealmApp.Run<MmoRealm>`. |
| `Mmo.Gate` | — | Region, maps, version. Forty lines, and that is the point. |
| `Mmo.Orchestrator` | — | A silo. The game's grains need no registration beyond being referenced. |
| `Mmo.Client` | — | Headless: sign in, pick a character, get a ticket, connect. |
| `Mmo.Ui` | — | The interface: eight VXML components, a theme file, and a Tailwind-shaped stylesheet with only the utilities the markup uses. [Its own README](Mmo.Ui/README.md) is where the VXML traps are. |
| `Mmo.Content.Tests` | — | The real importer over the real tree, every library's `Problems`, and the cross-references. |
| `Mmo.Realm.Tests` | — | That twenty libraries actually compose. |
| `Mmo.Ui.Tests` | — | Seventy, over a real `UiDocument`, with no GPU and no window. |
| `Mmo.Soak` | — | Doc 27 and doc 28's shared exit criterion. [Its own README](Mmo.Soak/README.md) is where the findings are. |

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

Six zones (Greenmarch → Thornwood → Ashfen → Kettlerock → Saltmere → Hollowmoor), three classes,
fourteen creature families, eight factions.

| Library | | |
|---|---:|---|
| `Items` | 325 | Ten slots × three armour classes × six zones, eight weapon kinds, reagents, consumables, eighteen gems, bags. Five rarities; `Storied` rolls three affixes and sockets two gems. |
| `Combat` | 122 | Nine ability *shapes* × four ranks × three classes, level-gated, plus one signature per creature family. |
| `Quests` | 60 | An eight-step chain per zone, each step gating the next, plus a daily and a weekly. |
| **`Ai`** | **84** | **56 creatures** across four ranks (normal/elite/rare/boss) and 28 leashed camps. |
| `Loot` | 52 | A table per family, a pity table per family, a table per gathering node, salvage per armour class. |
| `Interaction` | 42 | Ore, herb and hide nodes per zone; a station per making profession per zone. |
| `Crafting` | 42 | Four professions with their own reagent lines, banded across the zones, one per `RecipeSource`. |
| **`Economy`** | **46** | **40 vendors** — general, armourer, weaponsmith, reagents, victualler, lapidary, quartermaster — and six currencies, one account-scoped, one decaying, two convertible one-way. |
| `Collections` | 56 | Mounts, pets, appearances, titles and toys per zone, with twenty achievements over them. |
| `Housing` | 31 | The freehold and five pieces of furniture per zone. |
| `Effects` | 30 | Four per class, three per zone's consumables. |
| `Progression` | 21 | 1–25 curve, three trees of **twenty nodes in three branches with two capstones**, six professions, eight reputations. |
| `Exploration` | 6 | A chart per zone with six points and a fog grid. |
| `Movement` | 13 | A ground and a flying mount per zone, and the payload waggon. |
| `Travel` | 12 | A waystone per zone, a flight path per adjacent pair, a hearthstone. |
| `Shooting` | 12 | Ballistics for every ranged weapon — hitscan, so the lag-compensation path is live. |
| `Chat` | 8 | Say, yell, party, raid, guild, officer, whisper, trade. |
| `Social` | 5 | Party, raid, warband, battleground team, and the guild charter. |
| `Instances` | 4 | A five-player dungeon per zone from the third onwards, two difficulties, three encounters. |
| `Pvp` | 4 | Three battlegrounds and an arena. |
| `Inventory` | — | ⚠ **No definitions, and that is not a gap** — a container is sized by what the game hands it, and the bag items are where the number lives. |

⚠ **`CreatureDefinition` is the sample's own type, and doc 28 has no equivalent.** Every *part* of a
fight is in the libraries and nothing is a fighter — see below.

## What writing the content found

### There was nothing alive in it

The first pass authored 112 definitions, passed every test, and had **no creatures at all**. Four
spawn tables named `creatures/boar` and friends; none of those addresses existed. Every `Kill`
objective waited on a tag — `Creature.Beast.Boar`, `Creature.Undead` — that **no definition granted**,
so they compiled clean and could never advance.

It was not an authoring oversight. ⚠ **Doc 28's twenty libraries have no creature type.** `Ai` says
what spawns and how far it may be pulled, `Combat` says what an ability does, `Loot` says what drops
and `Items` says what the drop is — and nothing says *"a level 6 boar with 240 health, this tag,
these abilities, this table"*. The gap is structural: such a type needs `Items`, `Combat`, `Loot` and
`Ai` at once, and the spine allows only `Items` and `Combat` to be depended on, so it cannot live in
any existing library. It lives in `Mmo.Shared` here, because **a game may reference all twenty** —
the spine is a rule about the libraries rather than about their users. Task **#45** is whether the
engine should grow a `Vixen.Gameplay.Encounters` to hold it.

`CreatureLibrary` is where the join is checked, and it can be checked *only* here: it takes the other
libraries rather than the catalog, so a creature casting an ability that does not exist is a content
problem rather than a null at run time.

### And the check that should have caught it had a hole

`ReferenceTests` verified loot → item, vendor → item, recipe → profession, quest → reward and five
more. It did not verify `SpawnEntryDefinition.Creature`. The one reference site pointing at the
living world was the one missing — in the file the README called the most copyable thing here.

It now covers spawns, encounter scripts, affix pools, event chains, Collect targets and creature
casts, and it is mutation-verified. But the lesson is the general one, and it is #42's argument:
**a reference check is only as good as its enumeration of reference sites**, and a hand-maintained
list will always be missing the field somebody added last week.

`CoverageTests` had the same shape of problem: it asserted `> 0` per library, which passed for months
while the world was empty. It asserts a **floor** now — 300 items, 100 abilities, 50 quests, 40
vendors — because "one authored file" and "exercised at the scale the README claims" are different
claims.

### The content is generated, and the exemplars are not

981 files is not something to hand-type, and a real MMO's content is authored in tooling over tables
and exported. `Mmo.Content.Authoring` is that pipeline with the tooling left out:
`Tools/Vixen.UnicodeTableGen` is the precedent for a generator whose output this repository commits.

Every emitted file says so in its header. The tables — zones, classes, slots, rarities, families,
professions, factions, vendor kinds — are the part worth reading, and they are one file.

⚠ Three generator bugs the content test caught, all of which would have shipped: four professions
refining the same ore (the crafting library refuses two *discovered* recipes with the same inputs,
because only one could ever be found); nine dungeons and battlegrounds naming scenes nobody wrote;
and a `Vendors` loop closing one YAML level too many, which the writer now refuses with the tag name
rather than throwing on a negative string length somewhere unhelpful.

### A game's own definition type needs the generators

⚠ `CreatureDefinition` compiled fine, had no type descriptor, and `GameplayConfig.Build` refused it
with *"no `.vxdef` can name it"* — **about a type in the project being built**. Analyzers do not flow
through a `ProjectReference`, so `Mmo.Shared` has to name the reflection and serialization generators
explicitly. `13-ThirdPersonShooter`'s csproj warns about this for scene components; it is the same
trap from the other direction, and a shipped game gets them from the SDK package instead.

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

```bash
dotnet run -c Release --project Samples/14-Mmo/Mmo.Soak
```

It imports the tree through `DefinitionImporter` — the same code the editor and `vixen import` run —
and asserts every library's `Problems` list is empty. ⚠ **Every assertion is on an empty list rather
than a count**, because asserting a count means the next real problem is absorbed by an off-by-one
somebody updates without reading.

It also asserts the coverage claim above: a sample that says it exercises twenty libraries and
exercises seventeen is worse than one that says seventeen, because somebody reads the list, does not
find the example they came for, and concludes the library does not work.

## The content build

**Not wired, and what stops it is a decision about addresses rather than an MSBuild import.** No
project here imports `Vixen.Sdk.targets` or sets `VixenProjectDirectory`, so `Mmo.vxproj`'s 987
definitions are never imported by the pipeline and `MmoAddresses.cs` is hand-written. Its own remarks
say a real game generates that file. Running the real pipeline over this tree is what showed why
nobody had.

**The addresses the pipeline produces are not the addresses this content is authored against.**
`BuildPlanner.AddressOf` returns the project-relative path verbatim — `Assets/Items/rarities/fine.vxitem`
— and every cross-reference in the tree is written the other way (`rarity: items/rarities/fine`),
because `Mmo.Content.Authoring` invented that scheme and only `Mmo.Content.Tests`' own helper
implements it. A `DefId` is a hash of the address string, so the two schemes agree about nothing:

```
generated       Assets/Maps/greenmarch.vxdef      Assets/Currencies/gold.vxdef
hand-written    maps/greenmarch                   currencies/gold
```

All fourteen constants in `MmoAddresses` differ, and so does every reference inside every file. It is
not a formatting difference and it cannot be papered over: the generated file is nested classes
(`Addresses.Maps.Greenmarch.Address`) where `MmoAddresses` is flat, so "swapping them is deleting a
file" is not true either.

⚠ **And one address names two assets.** `MmoMaps.Greenmarch` is `maps/greenmarch`, which is both the
`MapDefinition` the exploration library compiles *and* the startup scene `Realm.OnConfigure` hands the
engine as `Spec.Key.Map`. A content catalog holds one asset per address, so whichever the sample keeps,
the other needs a different one.

### The three options

| | What it costs | What it buys |
|---|---|---|
| **A sidecar per definition** — `addressable.address` in each of the 987 `.meta`, written by `Mmo.Content.Authoring` | 987 sidecars restating the path; a new file that forgets one lands at the wrong address silently | Nothing else changes. It is the documented mechanism — *"worth doing where the address is a contract"* — and this address is a contract. **Verified working**: a scratch build of the whole tree with these sidecars produces exactly the authored addresses |
| **Re-author the tree against pipeline addresses** — regenerate all 987 files with `Assets/…​.vxdef` references, delete `MmoAddresses` for the generated file | A very large diff; every address then embeds the file extension, which contradicts doc 28 G-Q1's *"the extension is cosmetic and the type tag decides"* — renaming `.vxdef` to `.vxitem` would change a `DefId` and therefore durable state | The sample stops carrying a second address convention, and the generated constants become the only ones |
| **A group-level address convention** — `.vxgroup` gains something like an Assets-relative, extension-stripped style, applied to the definitions group | An engine change with an owner and a design argument | Solves it for every game, not just this sample, and is the only option that removes the extension from a definition's identity |

The first is the one that unblocks the sample today; the third is the one worth arguing about.

### What a real build of this tree already proves

With the sidecars applied in a scratch copy, `vixen content build` produces 1 000 addresses in two
bundles, and the real `Mmo.Realm` process — `RealmApp.Run<MmoRealm>` with a `--realm-spec` — starts:

```
info Vixen.App                Content mounted from /app/Content: 1000 addresses.
info Vixen.Samples.Mmo.Realm  Composed 22 module(s) over 987 definition(s) from 1000 address(es); 28 camp(s) standing.
info Vixen.Samples.Mmo.Realm  Spawned 168 order(s) across 28 camp(s); 168 alive at t=0.0s.
```

Identical on a second run, because `WorldSpawns` is seeded from the shard's identity.

⚠ **`Mmo.Content.Authoring` writes lowercase paths and the committed tree is mixed-case** —
`Assets/Items`, `Assets/Maps`, `Assets/Pvp` beside `Assets/abilities`, `Assets/instances`. The tool
emits `maps/…​`, and it only ever ran on a case-insensitive filesystem. Re-running it on Linux would
create a second, lowercase copy of six directories rather than overwrite the first.

⚠ **Nothing joins a camp to a map.** A `SpawnTableDefinition` names its entries, its cap and its
leash; no map definition lists its camps and no table names a map. So `MmoRealm` drives every table in
the build, which is right for a sample with one shard and wrong for a fleet. The fix is a field, and
it is content work.

⚠ **`VixenVariant=Server` does nothing to the content.** `Vixen.Sdk.targets` emits a
`Vixen.App.BuildVariant` assembly attribute and nothing in the content build reads the property, so
the `vixen-mmo` Dockerfile's claim that a server publish strips textures, audio and shader
permutations is false and a shard image ships full client content. Already recorded as 🟡 in
[`docs/overview.md`](../../docs/overview.md); wiring the content build here does not change it.

## See also

- [`docs/plan/28`](../../docs/plan/28-gameplay-framework.md) — what the twenty libraries are.
- [`docs/plan/27`](../../docs/plan/27-mmo-framework.md) — the fleet the realm half will run on.
- [`docs/guide/live/gameplay-bridge`](../../docs/guide/live/gameplay-bridge.md) — where the two meet.
