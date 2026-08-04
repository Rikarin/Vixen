# Vixen.Gameplay.Combat

Abilities over the kernel's effects, a damage pipeline of six named stages a game inserts rules into
rather than replaces, and a threat table — because every game that adds threat later adds it wrong.

Spec: [docs/plan/28](../../docs/plan/28-gameplay-framework.md) § Combat, the first half of **G2**.

## State

**Built: abilities, casting, channels, the global cooldown, charges, costs, requirements, targeting
rules, the six-stage pipeline with its six shipped rules, threat and taunt. 44 tests.** Shooting is
the other half of G2 and is its own library.

| | |
|---|---|
| `AbilityDefinition` · `DamageDefinition` · `AbilityCostDefinition` | What a designer writes. |
| `AbilityTemplate` · `AbilityLibrary` | The compiled forms, with effects resolved to one template each. |
| `AbilityCaster` | Timing and eligibility: cast, channel, GCD, charges, costs, blocks, range. |
| `AbilityTarget` · `AbilityEvent` · `AbilityFailure` | What it was aimed at, what happened, and why not. |
| `DamageStage` · `DamageEvent` · `IDamageRule` · `DamagePipeline` | `Compute → Crit → Mitigate → Absorb → Apply → React`. |
| `BaseDamageRule` … `ThreatRule` | The six shipped rules — ordinary `IDamageRule`s, nothing privileged. |
| `CombatAttributes` | Which stats the shipped rules read, because a game's stats are its own. |
| `CombatResolver` · `AbilityHit` | A completed ability applied to what it hit. |
| `ThreatTable` · `ThreatEntry` | Who a creature is angry with, and a taunt that actually works. |
| `CombatModule` | One definition type, five stats and three tag roots. |

## The five things worth knowing before reading the code

### The pipeline is extensible and never replaceable, and the built-ins go through the same door

Doc 28 G-Q4 decided this: a wholesale replacement gets a game a pipeline with none of the tested edge
cases and no way back. So a game adds an `IDamageRule` at a named stage with an explicit `Order`, and
the six shipped rules are *also* `IDamageRule`s at named stages with explicit orders —
`TheShippedPipelineIsSixOrdinaryRules` asserts there is nothing else in there.

⚠ **Absorb after mitigate is not arbitrary.** A shield that soaked the pre-mitigation number would be
worth several times its face value against an armoured target and nothing against a naked one, which
is not what "absorbs 500 damage" means to anyone.

⚠ **A cancelled hit stops between stages, not between rules.** A rule that cancels has said "this
does not happen"; its peers in the same stage still run, because they are deciding the same question
and one of them may be about cancellation. Nothing after the stage runs.

### `DamageEvent` is a mutable struct and one of its members cannot be a property

A raid is thousands of hits a second, so the event is a struct passed by `ref` and the whole pipeline
allocates nothing. That much is ordinary.

⚠ **`Random` has to be a *field*.** Through a property getter, `hit.Random.Chance(…)` mutates a copy
of the stream and throws the advance away — so the crit rule and a game's proc rule would draw the
same number, for ever, and nothing would report it. Once one member must be a field the rest follow,
because a struct that is half fields and half properties invites the mistake to be made again.
`.editorconfig` carries the scoped CA1051 exemption with that reason.

### Health is a base value; a buff is a modifier

Taking damage is not something that gets undone when the thing that caused it expires, so
`HealthRule` writes the base rather than adding a modifier. The same is true of a shield being spent
and a resource being paid. `HealthComesOffAsABaseValueRatherThanAModifier` asserts the target ends up
with **no modifiers at all** after being hit.

### Costs are paid on completion, and a channel pays per tick

Paying up front means an interrupted cast has spent the resource, which every game then refunds by
hand and gets wrong for channels. Paying at the end means an interrupt refunds nothing because it
took nothing. A channel pays each tick, which is the only reading of "it costs mana per second" that
survives being interrupted halfway — and a channel that runs out mid-way stops rather than going into
debt.

⚠ **The channel's ticks are counted from elapsed time against ticks already emitted**, for
`EffectSet`'s reason: accumulate-and-subtract loses a tick to rounding often enough that a six-second
drain does two ticks in some casts and three in others.

⚠ **A silence ends a cast already in flight**, not just the next one. A three-second cast that
finished after its caster was silenced two seconds in is the classic version of this bug.

### A taunt is not a large threat number

Giving the taunter the top score plus a margin makes the taunt fail the moment somebody out-damages
the margin. `Taunt` marks them as *forced* for a duration — nothing outdamages that — and also lifts
them to the current highest so the boss is not handed straight back when it ends.

⚠ **Ties break on the attacker's number rather than on insertion order**, so two players on identical
threat do not swap the boss back and forth every time either of them lands a hit. Same shape as the
per-object light churn doc 19 records.

## What it deliberately does not know

**Where anything is.** `AbilityTarget` carries a distance the caller computed, and resolving a cone
into a list of victims is the caller's. Positions are `Vixen.Engine`'s, and a combat library that
needed a scene could not be tested without one, could not run in a headless simulation, and would put
a renderer in a realm's dependency graph. What this validates is the *rule* — that a targeted ability
has a target, and that the number it was given is inside its range.

**What an item is.** A weapon reaches an ability through the attributes it granted on equip, which is
what lets a game with no inventory have combat.

## A refusal is ordered by how long it will last

Several are usually true at once — a silenced player who just pressed something is silenced *and* on
the global cooldown. The check runs longest-lived first, so the message is the one still true in a
second; sorting it the other way produces a button that blames the global cooldown for four seconds
of silence.

## What is owed

- **Death and resurrection.** `HealthRule` reports `Killed` and `CombatModule` declares `State.Dead`,
  but nothing applies it: what dying *does* — a corpse, a release timer, a resurrection sickness, a
  spirit healer — is a game's, and the shipped part is the effect the game applies. A `DeathRule` at
  the React stage is three lines in a game's own assembly, which is the seam working.
- **An encounter.** Threat is a table per creature; who is in a fight together, and therefore who a
  heal makes threat with, is `Vixen.Gameplay.Instances`' (G6). `DamageEvent.Threat` is a figure on the
  event for exactly that reason.
- **Item effects with triggers.** Doc 28's item example authors `!OnHit { chance: 0.15, apply: … }`.
  The trigger vocabulary — on hit, on being hit, on crit, on kill — is a React-stage rule and belongs
  here, but the *authoring* is `Vixen.Gameplay.Items`', so it lands when the two meet.
- **Shooting**, which is G2's other half and doc 16's lag compensation earning itself.
