# Vixen.Gameplay.Progression

Levels, talents, specialisations, professions and reputation — definitions plus one durable record,
with every rule a requirement query.

Spec: [docs/plan/28](../../docs/plan/28-gameplay-framework.md) § Progression, the first half of **G3**.

## State

**Built: XP curves, levels, talent trees with server-side validation, specialisations, profession
skill lines and faction reputation. 34 tests.** Quests are G3's other half.

| | |
|---|---|
| `ExperienceCurveDefinition` · `ExperienceCurve` | A table *and* a formula, because a designer wants both. |
| `TalentTreeDefinition` · `TalentNodeDefinition` · `TalentPrerequisiteDefinition` | A DAG of nodes with ranks, costs, row gates and prerequisites. |
| `TalentTree` · `TalentNode` · `TalentAllocation` · `TalentVerdict` | The compiled tree, what somebody took, and whether that is legal. |
| `SpecialisationDefinition` · `Specialisation` | One of a set, with its own requirements. |
| `ProfessionDefinition` · `ReputationDefinition` · `RankedTrack<T>` | One number and the ranks it passes through, twice. |
| `ProgressionLibrary` | Everything compiled once, with a `Problems` list. |
| `ProgressionState` · `ExperienceGain` | The durable record — and an `IRequirementContext`. |
| `ProgressionModule` | Five definition types and no stats. |

## The five things worth knowing before reading the code

### A talent allocation is validated whole, not click by click

Doc 28 is explicit: *"a client-built talent tree is a client-chosen power level"*. So the client sends
an allocation and the server checks it from scratch — one pass over a few dozen nodes, when somebody
respecs rather than every frame, and the only form of the check that survives a patch changing what a
node costs.

⚠ **That forces every rule to be a property of the allocation.** "Five points anywhere in this tree"
is a total and checks fine. "You must have taken A before B" is a property of a *sequence*, is not
expressible here, and is not missed — it is unverifiable after a respec anyway.

⚠ **`Allocate` copies.** A client that kept its copy and edited it must not change what the server
accepted; `AnAllocationIsCopiedIntoTheStateRatherThanAliased` is the test.

### A row gate counts the rows above it, not the whole tree

This is the ambiguity that two failing tests surfaced, and it is worth being exact about. A gate of
three means *three points spent in earlier rows*. Counting the whole tree lets the point being spent
**on** a row be the point that opens it, so a three-point gate is really a two-point one.

The obvious phrasing — "points spent before this node" — is a property of a sequence and therefore
uncheckable from a finished allocation. **"Points on nodes with a lower gate"** is the same rule as a
property of the allocation, and it is why the gate is a *number* rather than a row index.

### A rank multiplies the value; it does not repeat the modifier

Five separate +2 % modifiers from one source cannot be told apart on removal, and in the
multiplicative bucket they compose to something other than +10 % — a balance difference nobody
authored. Three ranks of a +2 % node is one +6 % modifier.

### The same string names a track's tag and its number

`Profession.Smithing` is both *has the profession* and *the skill in it*. A designer learns one name
per track rather than two, and a requirement cannot ask about a value whose tag does not exist.
`ADocExampleRequirementResolvesAgainstTheState` runs doc 28's own example — `Level >= 5`,
`HasTag(Profession.Smithing)`, `Faction.Ebonhawke >= 21000` — against a live state.

⚠ **A track's ranks are sorted at compile time and searched from the top.** A search from the bottom
gets the right answer only when the ranks happen to be authored in order, and gets it silently wrong
when a designer inserts one out of order — which the test content does deliberately.

### Gear score is a number a game sets

Doc 28 lists it under Progression, and averaging an item level needs the inventory. A progression
library that depended on containers would make a game with no items unable to have levels, so
`GearScore` is a float the game computes and this exposes to requirements.

## Two things it deliberately does not decide

**How many talent points a level is worth.** "One per level after ten", "one every other level" and
"points from quests" are all games that ship, so `TalentPoints` is set rather than derived.

**What happens at the level cap.** Experience past it is reported as `Wasted` rather than banked:
banking it would mean a cap raised in a patch instantly levelling everybody who had been grinding at
the old one, which is a decision a game makes deliberately or not at all.

## Reading a character back

`Seat`, `SeatSkill`, `SeatStanding`, `SeatTalents` and `SeatSpecialisation` are the unchecked door
storage comes in through, and `HousePlot.Assign` is the precedent. Each exists for a specific failure
the checked path causes on login rather than for symmetry:

⚠ **`SetLevel` zeroes the experience.** That is right for a boost and wrong for a load — restoring
with it throws away everything earned towards the next level, every single time.

⚠ **`Allocate` re-validates the build.** A patch that moves a node's prerequisite would silently wipe
every character who had taken it, on login, with no refund and no message. A game that wants them
respecced does it as a migration that also gives the points back.

⚠ **`Train` clamps to today's cap.** Clamping on load makes a patch that lowers one destroy the
difference for everybody permanently; the next `Train` clamps, which is late enough to be reversible.
A skill in a profession this build has never heard of is kept for the same reason.

`Skills`, `Standings` and `Allocations` are the way back out, in id order so two realms holding the
same character write the same bytes.

## What is owed

- **Quests**, which are G3's other half and where most of the XP comes from.
- **Levelling effects.** Gaining a level usually applies something — a heal to full, a stat bump, an
  unlock. `ExperienceGain` reports the levels and the caller applies the effects; there is no hook
  here because the effects are the kernel's and the choice is a game's.
- **Respec cost.** `Allocate` replaces an allocation and charges nothing; what a respec costs is a
  currency transaction, which is G5's.
- **Account-wide progression.** Collections, achievements and account-bound currencies are G8's and
  live in a grain rather than on a character.
