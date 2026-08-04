# Vixen.Gameplay.Instances

Dungeons and raids: difficulty tiers, encounters with gates and checkpoints, and lockouts whose resets
are absolute boundaries.

Spec: [docs/plan/28](../../docs/plan/28-gameplay-framework.md) § Instances, part of **G6**.

## State

**Built: difficulties with scalars and requirements, encounters with gates and checkpoints, runs with
attempts and wipes, and lockouts behind a store seam. 27 tests.** Matchmaking is G6's third part and
is owed.

| | |
|---|---|
| `InstanceDefinition` · `DifficultyDefinition` · `EncounterDefinition` · `LockoutPolicyDefinition` | What a designer authors. |
| `Instance` · `Difficulty` · `LockoutPolicy` · `InstanceLibrary` | Compiled once, with a `Problems` list. |
| `InstanceRun` · `EncounterStatus` · `InstanceRefusal` | One group's run. |
| `Lockout` · `ILockoutStore` · `MemoryLockoutStore` | The durable, fleet-wide half. |
| `InstanceModule` | One definition type and no stats. |

## The five things worth knowing before reading the code

### A reset is an absolute boundary, not a timer

"Weekly" means the same instant for everybody. A duration from whenever somebody happened to enter
gives every player their own schedule, and a guild that raids on Wednesdays finds half its roster
locked at a different hour each week. `LockoutPolicy.NextResetAfter` is the only place a reset time
comes from.

### The lockout is issued on the first defeat, not on entry

Somebody who walked in, watched the group fall apart and left has not used their week; somebody who
killed the first boss has.

⚠ **Issued once and extended never.** A second kill on the same run must not push the reset out —
otherwise a raid that goes long is a raid that locks its members out further.

⚠ **Everybody in the party is locked, not just whoever swung.**

⚠ **One locked-out member refuses the whole group.** Letting the rest in leaves somebody standing at
the door while their party clears without them, and a group that can enter short is a group that
summons the locked member inside.

### The difficulty is chosen once and there is no way to change it

A lockout is per `(instance, difficulty)`, so a group that could switch halfway would have one lockout
covering two — the shape of every "clear it on normal then swap to heroic" exploit. `InstanceRun`
deliberately has no setter, and a test asserts that.

### A wipe resets what was being fought and nothing else

A boss that is dead stays dead; that is what makes a raid night's progress progress. What a wipe costs
is the attempt, which `AttemptsOn` counts. `Checkpoint` is the furthest *checkpoint* fight beaten —
which is not the same as the furthest fight beaten, because a definition can say `isCheckpoint: false`
for a trash pull that should not become a respawn point.

### It contains no scripting and no combat

An encounter here is an id, a display name, two flags, and the **address** of the behaviour tree that
scripts it. Doc 28 puts encounter scripting on `Core/Vixen.Ai`, and doc 28's spine allows
`Instances → Combat` — which is not taken, because nothing in a lockout or a difficulty needs an
ability. A difficulty's scalars are numbers a spawner multiplies by rather than modifiers: putting
them in the attribute algebra would let a dispel remove heroic mode.

## What is owed

- **Matchmaking**, which is G6's third part and doc 27's `Live/Vixen.Live.Matchmaking` — the dungeon
  finder that fills a group before any of this is reached.
- **Durability.** `MemoryLockoutStore` is for tests and single-process games; doc 28 puts the real one
  in `Live.Instances.Cluster` *"because they are fleet-wide"*, which is task **#27**'s bridge.
- **The raid calendar** — a scheduling grain plus notifications, which is fleet-wide for the world
  boss's reason and belongs beside the lockout store rather than here.
