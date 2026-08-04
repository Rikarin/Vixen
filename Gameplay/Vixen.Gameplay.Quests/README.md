# Vixen.Gameplay.Quests

Quests as stages of objectives, dynamic events as the same machine with the scope moved, and the ten
shipped objective types over one seam.

Spec: [docs/plan/28](../../docs/plan/28-gameplay-framework.md) § Quests and § Dynamic events, the
second half of **G3**.

## State

**Built: the kernel event bus, quests with stages and objectives, the ten shipped objective types,
realm-scoped dynamic events with contribution tiers, participant scaling and success/failure chains.
54 tests, plus 22 in [`Editor/Vixen.Editor.Gameplay.Quests`](../../Editor/Vixen.Editor.Gameplay.Quests/README.md).**

| | |
|---|---|
| `GameplayEvent` · `GameplayEventFilter` · `GameplayEventBus` | ⚠ **In the kernel, not here** — see below. |
| `QuestDefinition` · `QuestStageDefinition` · `QuestObjectiveDefinition` · `QuestRewardDefinition` | What a designer authors. |
| `IQuestObjective` · `QuestObjectives` · `QuestObjectiveRegistry` | The seam and the ten shipped types. |
| `ObjectiveTemplate` · `StageTemplate` · `QuestTemplate` · `QuestReward` · `QuestLibrary` | Compiled once, with a `Problems` list. |
| `ObjectiveTracker` | The counter both a quest stage and a dynamic event are made of. |
| `QuestJournal` · `ActiveQuest` · `QuestStatus` · `QuestRefusal` | One character's quests. |
| `DynamicEventDefinition` · `DynamicEventTemplate` · `DynamicEventInstance` · `ContributionTier` | Realm-scoped, with tiers and scaling. |
| `DynamicEventDirector` | What runs them and walks their chains. |
| `QuestModule` | Two definition types, no stats, and the verbs. |

## The event bus went into the kernel, and that is the decision worth reading first

Doc 28's dependency spine says that where two features genuinely need to meet — loot from a raid
encounter, a quest counting a kill — they meet **"through tags and events rather than through a
reference"**. Tags were built in G0. Events were not, which left the sentence half true.

An objective counts kills, pickups, crafts and purchases: `Combat`'s, `Inventory`'s, `Crafting`'s and
`Economy`'s. A quest library that referenced any of them would be the horizontal edge the spine
forbids, and referencing all four would make a game with quests carry an auction house. Putting the
bus in *this* library instead would only move the problem: `Combat` would then reference `Quests` to
post a kill.

So `GameplayEvent`, `GameplayEventFilter` and `GameplayEventBus` are the kernel's, and this library is
their first subscriber. Achievements and collections (G8) will be the second.

⚠ **An empty verb range matches nothing, never everything.** A filter is built from a name a designer
wrote, and `RangeOf` answers an empty range for a name the content does not have. The other reading
turns one typo into an objective that completes on the first thing that happens anywhere.
`GameplayEventFilter.EveryVerb` is a thing a caller has to write down.

⚠ **A subscription made during a dispatch does not see the event being dispatched.** This is not an
edge case, it is the main path: the last kill of a stage completes it, which cancels that stage's
subscriptions and takes out the next stage's, *inside the handler the bus is currently calling*. Let
that new subscription see the current event and the kill that ended stage one is also the kill that
starts stage two — which is doc 28's "no objective completes twice", broken.
`TheNextStageDoesNotCountTheEventThatEndedTheLast` is the test, and it only catches the bug because
its two stages count the *same* verb.

## The other things worth knowing before reading the code

### A requirement context is several objects, and the kernel grew a way to say so

Doc 28's own example — `[ Level >= 80, HasTag(Profession.Smithing), NotHasTag(State.InCombat),
Currency.Gold >= 500 ]` — asks four questions of four owners. There is no single object that answers
all four, and building the journal is where that stopped being theoretical: a quest chain is
`HasTag(Quest.Completed.Prologue)`, and the tag is the journal's while the level is a
`ProgressionState`'s.

`IRequirementContext` gained `HasTag(GameplayTagRange)` with a default implementation over the
existing `Tags`, so nothing that had one set changed, and `CompositeRequirementContext` puts several
side by side. First answer wins for a value, because a value is a fact about one owner; any context
suffices for a tag, because having `State.InCombat` from an effect and from a zone rule are the same
fact to a rule.

### The ten shipped types are nine verbs, one clock and one level

Worth admitting rather than dressing up. Nine of the types differ only in which verb they wait for.
`Collect` differs in one bit — it is a **level**, so selling nine of ten ore makes "have ten ore"
untrue again, while ten undead stay killed. `Survive` differs in counting seconds.

The variety lives in the *filter*, which a designer writes: "kill ten undead in Queensdale" and "kill
the Shatterer" are both `Kill`. Saying so in the seam is what makes a game's eleventh type five lines
rather than a copy of `Kill` with nine methods.

### Completion is latched, and progress is not

Progress moves both ways — a level falls, a rescaled event wants more than it did a minute ago — but
*finished* is a one-way door. Without the latch, selling an ore un-finishes a stage that has already
advanced.

### Contribution rather than tap-ownership

Deciding who "gets" an event by first hit, most damage or killing blow is what makes a world boss a
race and a passer-by an intruder. Everyone who did enough gets their tier, and one player's reward is
not another's loss. `Contribute` is also how a game credits work no objective counts — healing,
reviving, repairing a wall — which is most of what a support player does.

⚠ **Scaling is monotone by construction**, because doc 28's test for it is that it is: one clamped
linear term, no table and no per-band override. A shape a designer can bend is one that can bend
downwards, and an event that got *easier* when a tenth player arrived is a mechanic for griefing it.

⚠ **Rescaling raises and never lowers.** A requirement that fell when somebody logged out would let an
objective complete because a player left.

### The clock is checked after the objectives

An event whose final objective completes on the same tick its duration runs out **succeeded**. The
work was done, and checking the clock first would fail an event a player had just finished, which
reads as the server cheating.

### A chain is a graph with cycles, and nothing here tries to prevent that

The camp being lost, retaken and lost again *is* the content. What is bounded instead is a single
resolution: an ending starts its successors and stops, with `MaximumChainDepth` as a backstop against
content whose events all end the instant they begin.

⚠ **An event already running is not started again.** Two failures both branching to "retake the camp"
is ordinary authoring; the other reading gives two instances on one realm, each with half the
participants and its own rewards.

## What this library deliberately does not do

**It never pays a reward.** `TurnIn` reports what is owed and the caller pays it. Doc 28's authority
table puts quest rewards in the grain, and a journal that handed out an item would be a realm deciding
a durable question.

**It never resolves an address.** A reward is `(DefId, count)`. Resolving one to an `ItemInstance`
needs `Vixen.Gameplay.Items`; moving currency needs the economy. A game with quests would carry both.

**It does not know what time it is.** A world boss's schedule is authored here and enacted in
`Live.Instances.Cluster`, because a schedule one realm decides is a boss every shard sees differently.

## What is owed

- **Durability.** A journal is in memory. Which stage a character is on, and an event's contributions,
  are durable state — task **#27**, `Live/Vixen.Live.Gameplay`, and the same shape `IPityStore` has.
- **Quest sharing.** `Shareable` is authored and nothing reads it; handing a quest to a party member
  needs the party, which is G4's.
- **Daily and weekly resets.** `QuestRepeat` is authored and `Once` is the only value the journal
  enforces, because a reset schedule is fleet-wide and belongs with the world boss's.
- **`Vixen.Editor.Gameplay.Quests`' view.** The model, the chain walk and the canvas projection are
  built; drawing them in the editor shell is owed, exactly as the loot editor's view is.
