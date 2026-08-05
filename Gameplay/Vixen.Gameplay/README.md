# Vixen.Gameplay

The kernel doc 28's twenty libraries are built out of: hierarchical tags, definition ids that need no
registry, one modifier algebra with one fixed evaluation order, one effect type with a stacking
policy, requirements, a reproducible random stream, and the module seam a game composes its rules
out of.

Spec: [docs/plan/28](../../docs/plan/28-gameplay-framework.md) § The kernel. This is **G0**, and doc 28
is explicit that it is the one milestone that is not optional — everything above it is definitions and
rules over these types, and a game that skips it writes them badly six times.

## State

**G0 is built and tested: 117 tests here and five over the `.vxdef` importer.** What is not here is
everything above it — items, inventory, loot, combat, quests and the rest are G1 onwards, and each is
its own package beside this one.

| | |
|---|---|
| `Tags/GameplayTag` | Four bytes: a pre-order index into a baked table. Not a hash — see below. |
| `Tags/GameplayTagRange` | A resolved prefix, as `[start, end)`. `Contains` is the two integer comparisons doc 28 promises. |
| `Tags/GameplayTagTable` | The baked tree: names, symbols, parents, subtree ends, and a build hash. Numbering is a pure function of the name *set*. |
| `Tags/GameplayTagSet` | What something has right now, **counted** — two grants need two revokes. Sorted, so a prefix query is a binary search. |
| `Tags/GameplayTagQuery` | All of · any of · none of. Three lists, resolved once, and no expression tree. |
| `Definitions/DefId` | The FNV-1a of an address — the same construction `NetworkPrefabId` uses, asserted by a test that computes both. |
| `Definitions/Definition` | The authored layer: a record with an address stamped on by the catalog, and `CollectTags` so the build can bake the tag table. |
| `Definitions/DefinitionCatalog` | Every definition a build knows plus the tag table baked out of them, immutable, with a build hash over addresses and tags. |
| `Definitions/DefinitionRegistry` | The swappable holder. `Reload` applies an additive change live and refuses the two that cannot be — see below. |
| `Definitions/DefinitionSerialization` | Self-describing bytes: the type's alias, then the payload. The only artefact in the engine whose reader does not know its type. |
| `Attributes/AttributeId` · `Modifier` | One stat type for every number in every library, and three modifier buckets. |
| `Attributes/AttributeLayout` | The compiled stat table: defaults, clamps, rounding. `BlackboardLayout`'s arrangement for its reason. |
| `Attributes/AttributeSet` | The algebra: fixed order, dirty-flagged per stat, removal by source, canonical modifier order. |
| `Effects/EffectDefinition` · `EffectTemplate` | The authored effect and its resolved form. Buff, debuff, DoT, stun, aura, shield, stance, mount — one type with a policy. |
| `Effects/EffectSet` | Every effect on one thing, owning its tags and its modifiers for the effect's lifetime. |
| `Requirements/Requirement` · `RequirementSet` | A tag query plus a numeric predicate, evaluated by the same code on the client and on the realm. |
| `Requirements/GameplaySubject` | Stats, tags and effects, which always travel together. |
| `Random/GameplayRandom` | PCG-XSH-RR, seeded per event, resumable from a stored state, with a pinned stream. |
| `Modules/IGameplayModule` · `GameplayConfig` | What a game composes. The kernel arrives through the same seam a game's own module does. |

The `.vxdef` importer is in
[`Editor/Vixen.Editor.Assets/Gameplay`](../../Editor/Vixen.Editor.Assets/Gameplay/DefinitionImporter.cs):
one importer, six extensions, and the YAML type tag doing the deciding — doc 28 G-Q1, settled in
favour of one.

## The five things worth knowing before reading the code

### A tag id is a bake, and a `DefId` is a hash — the difference is the whole tag design

A `DefId` is FNV-1a over an address, so no peer has to be told it and no registry has to be
maintained. A `GameplayTag` cannot work that way, because the feature that makes tags worth having is
that *fire resistance reduces `Damage.Fire.*`* is two integer comparisons, and a hash has no ordering
to test against. So tags are numbered by a pre-order walk of the tag tree, siblings in ordinal order,
and a tag's descendants occupy a contiguous range.

Three consequences, all deliberate, all stated because getting them wrong is silent:

- **The numbering is a pure function of the name set**, not of the order they were added.
  `NumberingIsAPureFunctionOfTheSetAndNotOfTheOrder` builds the same vocabulary three ways.
- **Adding a tag renumbers the ones after it.** Both ends of a wire must therefore hold the same
  table, which they do because the handshake compares the catalog's build hash before anything is
  dispatched. `GameplayTagTable.BuildHash` is what a table contributes to that.
- **Durable state must never hold an index.** It holds `SymbolOf`'s hash of the name, which survives
  a renumbering. `ASymbolSurvivesARenumberingAndAnIndexDoesNot` is the test, and it is the reason
  `Symbol` is on the table at all.

⚠ **The sort is on the segment, not on the qualified name.** Sorting `"A-x"` and `"A.b"` as whole
strings puts the hyphen before the dot and therefore puts a *different root tag* inside `A`'s range —
`A` would then match something that is not under it. `ASiblingWhoseNameSortsBelowADotDoesNotSplitASubtree`
is the regression, and it is the kind of thing that surfaces two years later as one boss being immune
to one damage type.

### An unknown prefix matches nothing, never everything

`RangeOf` on a tag the content does not have returns an empty range. The other reading — an unknown
prefix matching everything — is how a misspelling makes a boss immune to all damage, and it is
invisible in review because the rule reads correctly. So an `any` list of unknown tags fails, a `none`
list of unknown tags passes, and both are the safe direction.

### Doc 28's walk was optimistic about one row, and `Reload` is where that is honest

Step 6 of *Adding an item in minutes* says "realms reload their definition registry live; **no
restart, no drain**". That is true for adding an item, retuning a loot table, changing a number. It is
**not** true when the change introduces a tag, because a new tag renumbers the table and every
`GameplayTag` already sitting in a component, an effect, or a packet in flight is an index into the
old numbering. `DefinitionRegistry.TryReload` refuses that and says so; it is a *build* update in doc
27 § Upgrades' sense, which rolls out rather than reloads.

It also refuses a catalog that has *lost* an address, which is doc 28's own rule — "removing content
is never additive" — enforced rather than remarked.

### Removal is by source, and the value is recomputed rather than subtracted

Undoing a buff by adding its negation is how a stat ends up at 99.9997 after ten cycles of a proc.
`RemoveBySource` deletes the modifiers and the value is derived from the survivors, so a thousand
add/remove cycles land on the byte-identical number they started from — which
`RemoveBySourceIsExactAndLeavesNoResidue` asserts a thousand times.

⚠ **Modifiers are kept in a canonical order rather than in arrival order**, and that is about
prediction rather than tidiness. Float addition is not associative, so a client that applied a trinket
before a raid buff and a realm that applied them the other way round compute numbers that differ in
the last bit — which doc 16's `MispredictionCount` reports every frame and a player sees as jitter.
Sorting on (stat, bucket, source, value) costs one insertion memmove and removes the class;
`TheResultDoesNotDependOnTheOrderTheModifiersArrivedIn` shuffles six modifiers two hundred ways and
compares the *bits*.

### A periodic effect counts ticks from elapsed time, not from a remainder

The obvious implementation — add the delta to an accumulator, fire while it exceeds the period,
subtract — loses a tick to rounding often enough that a six-second bleed with a two-second period does
five ticks' damage on some casts and six on others. This counts `floor(elapsed / period)` against how
many have been emitted, and expiry pays out whatever the last partial step rounded away.
`APeriodicEffectTicksExactlyAsOftenAsItsDurationBuys` runs four duration/period pairs at 1/60 of a
second, which divides none of them.

⚠ **`Extend` grows the instance's duration and does not wind its clock back.** Winding `Elapsed`
backwards looks equivalent and re-pays every period between the new position and the old one, which is
a damage-over-time effect that does double damage to anyone who refreshes it.

## What is opinionated, and where the seam is

| Opinion | The seam, when a game disagrees |
|---|---|
| One evaluation order for modifiers | None, and that is the point. A game picks a bucket per modifier. |
| Three modifier buckets, no `Override` | A polymorph is a large `MultiplyPercent` and a clamp on the layout. Doc 28 G-Q6. |
| Effects are one type with a policy | Five policies cover what doc 28 asks for; a sixth is a change to this library, on purpose. |
| Requirements are a conjunction | A disjunction is a `GameplayTagQuery`'s `any` list. |
| Tags are hierarchical and interned | A game adds a tag by writing one in a `.vxdef`, or `builder.Tag(…)` for one only code knows. |
| The kernel reports, it does not act | An effect's periodic tick is an `EffectEvent`; what it *does* is the combat library's. |

## What is owed

- ~~**The runtime load path.**~~ **Built**, as
  [`Vixen.Gameplay.Content`](../Vixen.Gameplay.Content/README.md) — a separate assembly precisely
  because of the reference list above: the kernel links no asset system, and every gameplay library
  depends on the kernel. Doc 28 § Definitions' third consequence also turned out to be wrong about
  the shape: definitions are copied out of their bundle and held whole rather than ref-counted
  individually, because a `DefId` that sometimes resolves is worse than one that never does.
- ~~**`Vixen.Gameplay.Generators`.**~~ **Built**, and it is not a generators project. Doc 28's
  library list gave it three jobs; two — definition codecs and the type registry a `!Tag` resolves
  through — were already `Vixen.Core.Serialization.Generator`'s and `Vixen.Core.Reflection.Generator`'s,
  which this library simply references. The third, `DefId` constants for authored addresses, is
  `AddressConstants` in `Vixen.Editor.Assets`, written by `vixen import` before the compiler runs:
  the address list is a property of the *content* build, and a Roslyn generator sees the compilation
  and nothing else.
- **An ECS component.** Nothing here goes on an entity yet: `GameplaySubject` holds three growable
  collections and an archetype ECS stores fixed-size rows, so what goes on an entity is a handle into
  whatever owns these. Which component that is belongs to the library that needs it first.
- **Everything above G0.** Items, inventory, loot, combat, shooting, progression, quests, the rest.
