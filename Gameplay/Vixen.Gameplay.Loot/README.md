# Vixen.Gameplay.Loot

A tree of weighted rows with conditions, pity as a durable field the engine owns, and a roll that is
reproducible from the id of the event that caused it.

Spec: [docs/plan/28](../../docs/plan/28-gameplay-framework.md) § Loot, the third part of **G1**.

## State

**Built: tables, weights, conditions, nested tables, independent rows, pity, the four distributions
and salvage. 23 tests.** The editor's table view and drop simulator are owed — they run *this*
evaluator rather than a second one, which is the point of it being a library.

| | |
|---|---|
| `LootTableDefinition` · `LootEntryDefinition` | What a designer authors: rolls, rows, and a pity policy. |
| `PityPolicyDefinition` | `{ AttemptsBefore, RampPerAttempt, GuaranteedAt }` — doc 28's three fields. |
| `SalvageDefinition` | What breaking an item down gives back: a loot table with an item on the front. |
| `LootTable` · `LootEntry` | The compiled forms, with conditions resolved to a `RequirementSet`. |
| `LootLibrary` | Every table and recipe a build knows, with a `Problems` list the content build fails on. |
| `LootContext` | What a row's conditions are evaluated against — the kill's tags and named numbers. |
| `PityKey` · `IPityStore` · `MemoryPityStore` | Whose run of bad luck, on which table, and where it is remembered. |
| `LootEvaluator` | The roll. Also what the editor's simulator runs. |
| `LootDistribution` | Personal · Group · NeedGreed · MasterLooter, as policies on the drop. |

## The four things worth knowing before reading the code

### A roll is reproducible from the event id, so the evaluation order is a contract

The stream is seeded from `(eventId, player)` and nothing else. That is doc 28's requirement — *"the
log says you rolled a 3"* has to be answerable a year later — and it means the *order* the evaluator
does things in is part of the format:

1. independent rows, in the order they were authored;
2. then the weighted picks, `Rolls` of them;
3. a nested table is rolled where its row sits;
4. within a row, the count is drawn before the affix seed.

Reordering any of that changes what every recorded event id produces. `ADropIsReproducibleFromItsEventId`
pins the property; this paragraph is why the code looks the way it does.

### A row is either weighted or independent, and they are different mechanisms

A `Weight` puts a row in the pick — exactly one weighted row wins per roll. A `Chance` is rolled on
its own, in addition to the pick: *"and always drops two tokens"* and *"and has a 1 % chance of the
mount"*. A row that sets both means two things at once, so `LootLibrary.Compile` reports it and the
content build fails.

### A row whose conditions fail is *absent*, not skipped

The remaining weights renormalise over what is left. That is what a designer writing "only on Heroic"
means — the alternative is a table that quietly drops nothing one time in five on a normal kill, and
nobody would notice for a month. `ARowWhoseConditionsFailIsAbsentRatherThanSkipped` measures both
difficulties on the same events and asserts the normal-kill rate is exactly zero and the heroic rate
is exactly the renormalised one.

Conditions are the kernel's `RequirementSet`, evaluated against a `LootContext` — so a loot condition,
a vendor's stock condition and an ability's requirement are the same algebra, and a designer learns it
once.

### Pity is per (player, table), and the guarantee is a promise the test checks

`PityChance` is flat until `AttemptsBefore`, then rises by `RampPerAttempt`, and is `1` at
`GuaranteedAt`. The counter increments on a roll where no pity row dropped and resets when one does.

⚠ **Per (player, table) is doc 28's key and not the obvious one.** Per row would let a player bank
misses on a table they never intend to farm; per player alone would make one unlucky raid night
guarantee a drop from a different boss.

⚠ **`IPityStore` is an interface because the counter must be durable.** A pity counter that resets on
a realm crash is a support ticket, and durable means a grain — which `Gameplay/` may not reference.
The realm supplies one; a test and the editor's preview supply `MemoryPityStore`.

`ARunOfBadLuckIsRememberedAndADropForgetsIt` runs four hundred kills and asserts no run ever exceeded
the guarantee, which is the only form of that claim worth having.

## Distributions are policies, not code paths

Personal rolls the same table once per participant, with the participant in the seed. Group,
need/greed and master looter roll it **once** and produce one window; they differ only in who may
take what out of it afterwards, which is a flow rather than an evaluation and belongs to whatever owns
the window. `EveryOtherDistributionRollsOnceIntoOneWindow` asserts all three produce byte-identical
drops to a plain roll of the same event.

## What is owed

- **The editor's table view and drop simulator** — doc 28 names `Editor/Vixen.Editor.Gameplay.Loot`,
  and the requirement is that the simulator runs `LootEvaluator` rather than an approximation of it.
- **Currency drops.** A drop of gold is not an item, and currencies are G5's.
- **Contribution-weighted drops.** A dynamic event's participation tiers change *whose* table is
  rolled, which is G3's; the evaluator already takes the player, so nothing here changes.
- **The ledger.** A drop that crosses into a player's bags is a ledger entry, which is
  `Vixen.Gameplay.Inventory`'s change list plus doc 27's persistence.
