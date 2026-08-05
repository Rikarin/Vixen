# Vixen.Gameplay.Crafting

Recipes over the interaction system: stations are tag queries, discovery is an exact match, and skill
gain falls off across a band.

Spec: [docs/plan/28](../../docs/plan/28-gameplay-framework.md) § Crafting, part of **G7**.

## State

**Built: recipes with stations, skills and quality, three ways of coming by one, discovery by
experiment, and skill gain that decays. 17 tests.**

| | |
|---|---|
| `RecipeDefinition` · `RecipeItemDefinition` · `RecipeSource` | What a designer authors. |
| `Recipe` · `RecipeItem` · `CraftingLibrary` | Compiled once, with a `Problems` list. |
| `Crafter` · `CraftingResult` · `CraftingRefusal` | One character's crafting. |
| `CraftingModule` | One definition type and no tags of its own. |

## "The same system as gathering" means the same shape, not the same code

Doc 28 says crafting is *"recipes over that"* and that *"the value is in it being the same system as
gathering and using the same requirement algebra"*. ⚠ **This library does not reference
`Vixen.Gameplay.Interaction`.** What it borrows is the shape: a station is a **tag query**, which is
the kernel's, so a forge that happens to be an `Interactable` and a forge that is a fixed prop both
satisfy one recipe. Taking the dependency would have made a game with a crafting bench carry a node
respawn timer.

It does not reference `Vixen.Gameplay.Progression` either — a skill is a number an
`IRequirementContext` answers, and `ProgressionState` is one.

## The four things worth knowing before reading the code

### Discovery is an exact match, not a superset

Matching a subset would mean throwing everything in the pot discovers every recipe at once, which is a
button rather than experimentation. The inputs are sorted by id at compile time and reduced to one
signature string, so the order somebody adds them in does not matter and the counts do.

⚠ **Two discoverable recipes with the same ingredients is a reported problem**, because only one of
them could ever be found.

### Skill gain falls linearly to nothing across a band

A recipe teaches `skillGain` at `skillRequired` and nothing at `skillCap`. ⚠ **Falling off rather than
stopping at a cliff** matters: a cliff makes the last point before it the only thing worth making, and
everybody makes exactly that until the number changes.

### The quality roll is reproducible from the attempt

Seeded from `(attempt, recipe)`, so "the log says it came out ordinary" is answerable — the same
property the loot library gives a drop.

### It consumes nothing and produces nothing

`Craft` returns a `CraftingResult` saying what to take and what to give; the caller's containers move
it. By now that is the framework's rule rather than this library's decision — `QuestJournal.TurnIn`,
every `EconomyIntent` and `InteractionResult` all work the same way.

⚠ **Discovering something teaches it and consumes nothing.** Whether a failed experiment costs the
ingredients is a game's decision, made by the caller with the containers in hand.

## What is owed

- **A crafting queue.** Making two hundred bars is two hundred calls; batching is a UI affordance with
  a server-side counterpart, and neither is here.
- **Critical successes that produce a *different* item**, rather than the one quality bit. The hook is
  `QualityChance`; what a better result *is* is a game's, and a second output list would be the
  cheapest way to say it.
- **Recipe unlock by reputation or by rank**, which is a `RequirementSet` away — the field exists and
  nothing populates it from a vendor.
