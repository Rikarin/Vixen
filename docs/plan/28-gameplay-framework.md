<!--
SPDX-FileCopyrightText: Copyright (c) Rikarin
SPDX-License-Identifier: Apache-2.0
-->

# 28 — Gameplay Framework

⚠️ **Extends [27](27-mmo-framework.md), [08](08-asset-pipeline-and-addressables.md) and
[16](16-networking.md).** Doc 27 is the substrate — processes, placement, transfer, persistence. This
is what runs on it: the opinionated, modular library set that turns "we have a networked engine" into
"a designer added a legendary sword before lunch".

**The claim this document has to earn.** Adding an item, a quest, a recipe, a vendor or a loot table
is a content edit and a `vixen content build`, with no code and no server restart. Adding a new *kind*
of thing — a new objective type, a new effect, a new currency sink — is an afternoon, in the game's
own assembly, without forking the engine. Everything below is arranged around making both of those
true, and § Where it stops is honest about where they are not.

## The shape of the whole thing

Every feature here decomposes the same four ways, and the decomposition is the framework:

| Layer | What it is | Where it lives | Who owns it |
|---|---|---|---|
| **Definition** | Authored, immutable, addressable content. `ItemDefinition`, `QuestDefinition`, `AbilityDefinition` | `.vxdef` YAML → the content build → the catalog | designers |
| **State** | What is true right now. ECS components on the realm, durable rows behind `IPlayerGrain` | `Vixen.Gameplay.*` components + `Live.Persistence` | the realm and the grain |
| **Rule** | The system that changes state according to definitions | `Vixen.Gameplay.*` systems | the realm, authoritatively |
| **Contract** | What crosses a wire | `MyGame.Contracts` ([27](27-mmo-framework.md) § Contracts) | generated |

The value of writing it down is that it answers, once, the four questions every feature otherwise
answers differently: *where does the data live, who may change it, what does the client see, and what
does a designer edit.*

### Definitions, and the id trick that makes this cheap

A `DefId` is the stable hash of an **address** — the same construction doc 16 uses for prefab ids and
`NetworkSceneId`, and for the same reason: it is a pure function of authored content, so no peer has
to be told it and no registry has to be maintained by hand.

```yaml
# Assets/Items/flamebrand.vxitem
!ItemDefinition
displayName: Flamebrand
rarity: Legendary
slot: MainHand
itemLevel: 80
tags: [ Item.Weapon.Sword, Item.Soulbound.OnEquip, Item.Source.Raid ]
stats:
  Power: 251
  Precision: 179
effects:
  - !OnHit { chance: 0.15, apply: effects/burning, stacks: 3 }
icon: icons/flamebrand
prefab: prefabs/weapons/flamebrand
```

```csharp
// Anywhere, on either end:
DefId id = DefId.From("items/flamebrand");        // a hash — no lookup, no allocation, no registry
ItemDefinition def = defs.Get<ItemDefinition>(id); // resolved through Vixen.Assets, ref-counted
```

Three consequences worth stating, because each removes a whole category of MMO bug:

- **The wire carries a `DefId`, never a definition.** Both ends resolve it from content they have
  already agreed on — the session handshake compares catalog `BuildHash` before anything is dispatched
  ([16](16-networking.md) § Security). A client cannot be told about an item it does not have.
- **A renamed address is a content break, and it is loud.** Same property doc 16 records for renaming
  a replicated component. `ContentDiff` ([27](27-mmo-framework.md) § Upgrades) classifies it as
  non-additive, so it drains rather than silently orphaning every stack in every bank.
- **Definitions are ordinary addressable assets**, so they get ref-counted handles, label and glob
  loading, remote bundles, and catalog-overlay updates — all of `Vixen.Assets`, already built, with no
  gameplay-specific loading path. ADR-013 was right and this is where it pays.

`.vxdef` is one importer over YAML type tags (ADR-005), so `!ItemDefinition`, `!QuestDefinition` and a
game's own `!MyCustomDefinition` all reach a strongly-typed C# record through the generated type
registry with nothing registered by hand. The extension is cosmetic — `.vxitem`, `.vxquest`, `.vxdef`
all route to the same importer; the *tag* is the discriminator.

### Tags — the primitive everything else is built on

One hierarchical, interned, comparison-free tag type, and it is the highest-leverage thing in this
document:

```csharp
readonly record struct GameplayTag(uint Id);          // "Damage.Fire.Burn"

tag.Matches("Damage.Fire")      // true  — a parent matches its children
tag.Matches("Damage.Fire.Burn") // true
tag.Matches("Damage.Frost")     // false
```

Hierarchical because that is what lets a designer write a rule at the altitude they mean it: *fire
resistance* reduces `Damage.Fire.*`; *immune to control* blocks `Effect.Control.*`; *this quest counts
undead* means `Creature.Undead.*`. Interned to a `uint` because it is compared on the damage path,
sits in replicated components, and goes on the wire. Prefix matching is a range test on an id assigned
by a pre-order walk of the tag tree, so `Matches` is two integer comparisons, not a string operation.

Tags are how the framework stays opinionated without being closed: requirements, immunities, loot
conditions, quest objectives, effect stacking, chat gating, matchmaking eligibility, achievement
criteria and interaction filters are all *tag queries*, and a game adds a tag by writing one in a
`.vxdef`.

### The `IGameplayModule` seam

Each library is a module the game composes. Nothing is implicit; there is no scanning; the registration
is a source-generated list.

```csharp
public sealed class MyRealm : Realm {
    protected override void OnConfigure(RealmConfig config) => config
        .Use<CombatModule>()
        .Use<InventoryModule>(o => o.BagSlots = 5)
        .Use<QuestModule>()
        .Use<MyGuildRankModule>();          // the game's own, same interface
}
```

A module declares its components, its systems and their phase, its definition types, its RPC surface,
and its durable schema. The engine's modules and a game's own module are the same kind of object —
the discipline doc 16 took from `NetworkModule` ("build the built-ins out of the same primitive users
get"), applied one level up.

---

## Library structure

```
Gameplay/                               # ── a top level of its own; see below ──
├── Vixen.Gameplay/                     # KERNEL — tags, defs, attributes, effects, requirements, RNG
├── Vixen.Gameplay.Generators/          #   DefId constants, definition codecs, module registration
│
├── Vixen.Gameplay.Items/               # definitions, instances, affixes, rarity, durability, sockets
├── Vixen.Gameplay.Inventory/           # containers, bags, equipment, banks, capacity, split/merge
├── Vixen.Gameplay.Loot/                # tables, weights, conditions, pity, personal/group, salvage
├── Vixen.Gameplay.Combat/              # abilities, casting, GCD, cooldowns, buffs, damage, threat, death
├── Vixen.Gameplay.Shooting/            # hitscan, projectiles, spread, recoil, ammo, reload, penetration
├── Vixen.Gameplay.Progression/         # XP, levels, talents, specialisations, professions, reputation
├── Vixen.Gameplay.Quests/              # quests, objectives, stages, dynamic events, world bosses
├── Vixen.Gameplay.Ai/                  # ⚠ SHRUNK by doc 37: threat, aggro, leashing, spawn tables, dialogue.
│                                       #   The three planners, the blackboard, the action surface and
│                                       #   perception left for Core/Vixen.Ai — which is built. This
│                                       #   references it rather than containing it.
├── Vixen.Gameplay.Interaction/         # interactables, gathering, channelled use, containers, doors
├── Vixen.Gameplay.Crafting/            # recipes, stations, quality, discovery
├── Vixen.Gameplay.Movement/            # mounts, vehicles, seats, swimming, flight, gliding, water craft
├── Vixen.Gameplay.Travel/              # portals, waypoints, teleports, taxi, join-friend — doc 27's client half
├── Vixen.Gameplay.Social/              # parties, squads, teams, guilds, ranks, friends, presence
├── Vixen.Gameplay.Chat/                # channels, routing, moderation, rate limits
├── Vixen.Gameplay.Economy/             # currencies, vendors, trade, auction, mail, price model
├── Vixen.Gameplay.Instances/           # dungeons, raids, difficulty, lockouts, encounters, schedules
├── Vixen.Gameplay.Pvp/                 # arenas, battlegrounds, objectives, scoring, rounds, flagging
├── Vixen.Gameplay.Exploration/         # points of interest, map discovery, vistas, world map
├── Vixen.Gameplay.Housing/             # plots, decoration placement, permissions, persistence
├── Vixen.Gameplay.Collections/         # pets, mounts owned, skins/transmog, titles, toys, cosmetics
└── Vixen.Gameplay.*.Tests/             # ADR-014 — siblings, one per library

Live/                                   # doc 27 § Repository layout
├── Vixen.Live.Social.Cluster/          # guild + party grains
├── Vixen.Live.Economy.Cluster/         # auction, mail, trade escrow, the ledger's gameplay face
├── Vixen.Live.Instances.Cluster/       # lockouts, raid calendar, instance allocation
├── Vixen.Live.Progression.Cluster/     # account-wide collections, achievements, currencies
└── Vixen.Live.Matchmaking/             # already in doc 27 — Pvp and Instances are its callers

Editor/
├── Vixen.Editor.Gameplay/              # definition inspectors, tag picker, the balance table view
├── Vixen.Editor.Gameplay.Loot/         # loot table editor + a drop simulator that runs the real code
├── Vixen.Editor.Gameplay.Quests/       # quest/event graph on Vixen.Editor.NodeGraph
└── Vixen.Editor.Gameplay.Ai/           # behaviour/GOAP graph, same host

Samples/
└── 14-Mmo/                             # the vertical slice: two maps, combat, loot, a quest, a guild,
                                        # an auction, a dungeon, and a transfer between the two maps
                                        # (13 became 13-ThirdPersonShooter while this was unwritten)
```

**Why `Gameplay/` rather than `Core/Vixen.Gameplay*`.** These libraries are engine-side runtime code by
every test that matters — they run in the frame, a client links them, a phone runs the client — so
they carry the same profile `Core/` does: packable, AOT- and trim-clean, documented, API-baselined.
What the separate top level buys is that **a game must be able to decline all of it**, visibly. Folded
into `Core/`, "the engine" would silently mean "the engine, and an inventory system, and a threat
table"; as a folder, twenty-odd packages nobody referenced are twenty-odd packages a single-player
racing game never sees. It also gives the layer rule something to be expressed against —
`Gameplay/` sits on `Core/` and may not reference `Editor/`, `Tools/` or `Live/`, which is what stops
"items and quests" from becoming undeployable without an orchestrator. Enforced in
[`Build.ArchitectureRules.cs`](../../build/Build.ArchitectureRules.cs); the folder is already known to
the build's globs, the RUNTIME profile in `Directory.Build.props` and the documentation graph's scope,
so the first library to land here needs no build work.

**Why this many packages rather than one `Vixen.Gameplay`.** The same reason `Vixen.Net.Physics` and
`Vixen.Net.Audio` are separate from `Vixen.Net`: an extraction shooter links `Combat`, `Shooting`,
`Loot`, `Matchmaking` and `Pvp` and should not carry an auction house; a social builder links
`Housing`, `Collections`, `Economy` and `Chat` and should not carry a threat table. The layer rules
already make this checkable, and the trimmer already removes what is not referenced.

**The dependency spine is shallow on purpose.** Everything depends on `Vixen.Gameplay`. `Items` is
depended on by `Inventory`, `Loot`, `Economy`, `Crafting`, `Collections`. `Combat` is depended on by
`Pvp`, `Instances`, `Ai`. Nothing else is allowed a horizontal edge — where two features genuinely
need to meet (loot dropping from a raid encounter), they meet through tags and events rather than
through a reference, which is what keeps any one of them removable.

---

## The kernel

`Vixen.Gameplay` is small and it is where the opinions live.

### Attributes and the modifier algebra

Every stat in every one of the features above is one type:

```csharp
readonly record struct AttributeId(uint Value);     // Power, Health, MoveSpeed, CritChance, …

// Evaluation order, fixed, and this is the whole of the opinion:
//   base  →  +flat  →  ×(1 + Σ additive%)  →  ×Π(1 + multiplicative%)  →  clamp  →  round
```

**A fixed evaluation order is the feature.** Every game that leaves it open gets a balance team
arguing about whether two 50 % buffs are 100 % or 125 %, in different answers per ability, forever.
Additive percentages sum and multiplicative ones compose; a designer picks which bucket a modifier is
in and the arithmetic is never in question.

Modifiers are values with a source, so removal is exact rather than a subtraction that drifts:

```csharp
attrs.Add(new Modifier(AttributeId.Power, ModifierOp.AddPercent, 0.15f, source: buffHandle));
attrs.RemoveBySource(buffHandle);        // exact, order-independent, no float residue
```

Recomputation is dirty-flagged per attribute and batched per frame, and the *result* is what
replicates — a client is told a number, not a list of modifiers it would have to re-derive. What the
client *is* told about, separately, is the buff icons, because those are presentation.

### Effects

Buff, debuff, damage-over-time, cooldown, crowd control, aura, shield and stance are one type with a
policy, not eight systems:

| Field | Meaning |
|---|---|
| `Duration` | finite, infinite, or until a condition |
| `Period` | tick interval for periodic effects |
| `Stacking` | `None` · `Refresh` · `Extend` · `StackTo(n)` · `Independent` |
| `Modifiers` | what it does to attributes |
| `GrantedTags` / `BlockedTags` | what having it means, and what it prevents |
| `Immunities` | tag queries it makes the target immune to |
| `CancelOn` | tag query — damage, movement, death, cast |

Everything with a duration in this document is an effect: a mount is an effect that grants
`State.Mounted` and swaps a model; a resurrection sickness, a crafting station's attunement, a PvP
flag, a raid buff, a quest's timed escort — all of them. One replication path, one save path, one
inspector, one set of stacking bugs to fix once.

### Requirements and costs

The other reusable primitive, and it is why "can I do this" is never bespoke:

```csharp
// A requirement is a tag query plus a numeric predicate, evaluated identically on both ends.
requires: [ Level >= 80, HasTag(Profession.Smithing), NotHasTag(State.InCombat), Currency.Gold >= 500 ]
```

Used by abilities, recipes, vendors, quests, instances, mounts, housing permissions, and matchmaking
eligibility. **Evaluated on the client for the UI and on the realm for the truth** — the same code in
`MyGame.Shared`, which is what makes a greyed-out button and a rejected request agree.

---

## The features

Each is a sketch, not a specification — the specification is the library's own README when it is
built. What each entry has to establish is *what is authored, what is authoritative, and what is
genuinely new work*.

### Items, inventory, loot

**Items.** A definition plus an instance. The instance is deliberately small — `DefId`, a stack count,
a durability, an affix roll seed and a bound-state — because a bank of ten thousand items is a real
number and an instance carrying a materialised stat block is fifty times the memory for data that is
a pure function of the seed. Rolled affixes are `(affixDefId, roll)` pairs regenerated from the seed;
the stat block is computed on equip.

**Inventory.** The container algebra, and it is the part that has to be exactly right because it is
where duplication bugs live. One `IContainer` — bags, equipment slots, bank tabs, guild bank tabs,
mail attachments, trade windows, vendor buyback, loot windows are all containers with different
policies. Every mutation is a **transaction over a set of containers**: move, split, merge, swap,
equip are `ContainerTransaction`s that either apply entirely or not at all, are validated
server-side against capacity, binding, and slot type, and are recorded in the ledger
([27](27-mmo-framework.md) § Persistence) when they cross an ownership boundary. The client's copy is
optimistic and reconciled from the authoritative result — the same pattern as prediction, one layer up.

**Loot.** A table is a tree of weighted entries with tag conditions, and it is authored, previewed and
simulated in the editor with the real evaluator. Pity is a first-class field rather than a game's
private counter: `PityPolicy { attemptsBefore, rampPerAttempt, guaranteedAt }`, persisted per
(player, table), because it is durable state and because a pity counter that resets on a realm crash
is a support ticket. Personal loot, group loot, need/greed and master looter are policies on the drop,
not different code paths. **The RNG is the kernel's deterministic stream seeded per drop event**, so a
drop is reproducible from its event id — which is what makes "the log says you rolled a 3" answerable.

### Combat and shooting

`Vixen.Gameplay.Combat` is abilities on top of kernel effects: cast time, channel, global cooldown,
charges, resource costs, targeting mode (self, target, ground, cone, direction), and a damage pipeline
that is a fixed sequence of taggable stages — `Compute → Crit → Mitigate → Absorb → Apply → React` —
so a game inserts a rule at a named point rather than replacing the pipeline.

Threat, aggro tables, taunt, and death/resurrection are here because a raid needs them and because
every game that adds threat later adds it wrong.

`Vixen.Gameplay.Shooting` is the FPS half and it is where doc 16's lag compensation earns itself:
hitscan and projectile weapons, spread and recoil patterns, ammunition and reload state, penetration
and falloff. The hit path is:

```
client fires → predicted locally (Vixen.Net.Prediction, already built)
             → hit claim RPC with the client's tick
             → server rewinds colliders to that tick   (Vixen.Net.Physics ColliderRollback — built)
             → validates: line of sight, range, cooldown, ammo, the tick within the RTT window
             → applies through the damage pipeline
```

Nothing in that chain is new networking. What is new is the weapon model and the claim's validation
rules, and the claim is exactly the kind of expensive call doc 16's `Vixen.Net` README names as owed
work — *"a cost budget for rewinds"*, since a rewound claim costs far more than an ordinary RPC and
the rate limiter counts them the same. This library is the reason to close that.

### Progression, quests and events

**Progression.** XP curves, levels, gear score, talent trees (a DAG of nodes with point costs and
prerequisites, validated server-side because a client-built talent tree is a client-chosen power
level), class specialisations, profession skill lines, reputation/faction ranks. All of it is
definitions plus a durable record; the rules are requirement queries.

**Quests.** A quest is stages; a stage is objectives; an objective is a *type* plus parameters. The
engine ships the types every game needs — `Kill`, `Collect`, `Reach`, `Interact`, `Escort`, `Survive`,
`Deliver`, `Discover`, `Craft`, `Spend` — and a game adds one by implementing `IQuestObjective` with a
generated codec. Objectives subscribe to tag-filtered gameplay events rather than polling, so
"kill 10 undead in Queensdale" is a subscription with a tag query and a scene filter and costs nothing
when nothing dies.

**Dynamic events and world bosses** are the Guild Wars 2 shape and they are the same machine as a
quest with the scope moved: an event is realm-scoped rather than player-scoped, has participation
tracking (contribution tiers rather than tap-ownership), scales its difficulty by participant count,
and has success/failure *both* leading somewhere — a failed escort starts the "retake the camp" event.
That last property is what makes an event chain feel alive and it is a graph, so it is authored in
`Vixen.Editor.Gameplay.Quests` on the existing node-graph host. World bosses are events with a
schedule; the schedule lives in `Live.Instances.Cluster` because it is fleet-wide, not shard-wide.

### AI

Built on `Vixen.Navigation`, which is done and fast. Three planners, because one does not fit
everything, and the choice is per-archetype:

| | Use |
|---|---|
| **Behaviour tree** | scripted encounters, boss phases — authored, inspectable, deterministic |
| **Utility scoring** | ambient NPCs and creature packs — cheap, tunable, no authored graph |
| **GOAP** | the brief's explicit ask: agents with goals and an action set, planning a sequence. Expensive; for the few dozen agents where emergent behaviour is the point, not for the thousand critters |

All three drive one `IAgentAction` surface, so an encounter can mix them, and all three run on the
realm only. Perception (sight cones with occlusion via `Vixen.Physics`, hearing as tagged events),
aggro, leashing, patrols, spawn tables with respawn timers, and NPC dialogue/vendor state complete it.

The engine-side ambition is deliberately bounded: **a planner and a perception model, not a
behaviour library.** What a mob does is the game's.

### Interaction, crafting, movement, travel

**Interaction** is the grinding loop: an interactable is a component with a tag, a requirement, a
duration and a result. Mining a node, smelting at a forge, opening a chest, reading a book, flipping a
lever and picking a herb are one channelled-interaction system with different definitions.
Interruption on damage or movement, contested nodes, and per-player node instancing (GW2's answer to
node-stealing) are policies on the definition.

**Crafting** is recipes over that: inputs, station tag requirement, output, quality roll, discovery
(a recipe learned by experiment rather than by purchase), and skill gain. Nothing here is technically
hard; the value is in it being *the same* system as gathering and using the same requirement algebra.

**Movement** is mounts and vehicles: ground, flying, aquatic, submarine, boat, car. One `IVehicle` with
seats, a driver, passengers, a control mapping, and physics config — a mount is a single-seat vehicle
whose model is a creature, which collapses two systems people usually write twice. Networked through
`Vixen.Net.Physics`'s existing rigid-body authority: **the driver predicts, passengers interpolate the
vehicle and are parented to it** — and this is where doc 16's owed *parent-relative replication* stops
being optional, because a passenger replicating world coordinates fights the vehicle's own.

**Travel** is the client-facing half of [27](27-mmo-framework.md) § Transfer: a portal volume, a
waypoint the player unlocks and pays to use, a taxi route, a "join my party" action, an instance
entrance. Every one of them resolves to `RequestTransfer`, and the *only* thing this library adds is
the fiction: the cost, the unlock, the requirement query, and the UI. That is the payoff of doc 27's
protocol being one mechanism — a game adds a new way to travel by authoring a definition.

### Social, chat

**Social.** Party (small, ad-hoc), squad/raid (large, with subgroups and roles), team (a match-scoped
grouping), guild (persistent, ranked, with permissions and a bank). Party and squad state is a grain
because it outlives any one shard and drives placement ([27](27-mmo-framework.md) § Placement's
dominant score term). Guild is a grain with durable state and a permission matrix that is a tag query
per action, so a game adds a guild permission by adding a tag.

**Chat.** Routed by audience, as doc 27 decided:

| Channel | Path | Why |
|---|---|---|
| say / yell / emote / zone | realm, `Channel.ReliableUnordered` | spatial — the realm already knows who is nearby, and `InterestGrid` already answers it |
| party / squad | realm if co-located, gate otherwise | a party spans realms during a transfer |
| guild / whisper / global / trade | gate over WSS | the recipient may be anywhere, or offline |

Moderation is a pipeline of `IChatFilter`s (rate limit, mute list, blocklist, length cap, a
game-supplied word filter), applied server-side before fan-out, with the rejection reason returned to
the sender. Rate limiting reuses `RpcRouter`'s per-connection limiter rather than inventing a second
one.

### Economy

The part where correctness is not negotiable, and every mechanism traces to
[27](27-mmo-framework.md) § Persistence's ledger.

| | |
|---|---|
| **Currencies** | definitions with caps, decay, account-vs-character scope, and conversion rules. Gold, tokens, marks, karma are all one type |
| **Vendors** | a stock list with requirement queries, limited stock with a restock timer, buyback, and dynamic pricing hooks |
| **Player trade** | a two-sided escrow with a confirm-lock: both parties confirm, any change re-opens both confirmations, and the swap is one ledger transaction. **The confirm-lock is not UI polish** — it is the mechanism that makes the classic swap-at-the-last-moment scam impossible |
| **Auction house** | a grain per market with an order book, listings with deposits and durations, bids or buyouts, settlement into mail, and a fee that is the primary currency sink |
| **Mail** | with attachments and cash-on-delivery; the delivery mechanism for auction settlement, so it must exist before the auction does |
| **Price model** | an optional `IMarketModel` over recorded trades — moving-average pricing for NPC buy orders, so a game can have prices that respond to supply without writing an economy simulation |

**Every one of those is a ledger transaction with an idempotency key.** A duplicated auction
settlement, a retried mail claim, a trade whose confirmation packet arrives twice — all no-ops the
second time, by construction rather than by a check somebody remembered to write.

### Instances, PvP, matchmaking

**Instances** are doc 27's `Instance` shard kind with gameplay on top: difficulty tiers as definition
variants, lockouts (per character or per account, weekly or daily, with a reset schedule) in
`Live.Instances.Cluster` because they are fleet-wide, encounter scripting on the AI library's
behaviour trees, checkpoints, wipe/reset, and a raid calendar that is a scheduling grain plus
notifications.

**PvP** is arenas (small, symmetric, round-based), battlegrounds (larger, objective-based — capture
points, payload, flag return, resource control), duels, and world-PvP flagging. Objectives are a small
set of composable node types with scoring and win conditions, so a new battleground is a map plus a
`.vxdef`.

**Matchmaking** — `Live.Matchmaking`, with [Open Match](https://github.com/googleforgames/open-match)
as the design reference the brief names.

> **What Open Match is, and why it is a reference rather than a dependency.** It is Go, deployed as a
> set of Kubernetes services with Redis underneath, and integrated over gRPC. Taking it as a
> dependency means a Kubernetes-and-Go requirement in a .NET framework that must also run in Docker
> and as a bare process ([27](27-mmo-framework.md) ADR-019) — which is the same objection that made
> Kubernetes a *backend* there rather than the architecture. What is worth taking is its **model**,
> which is genuinely the right decomposition and is what gets adopted:
>
> | Open Match concept | Taken as |
> |---|---|
> | **Ticket** — a player or party enters a queue with attributes | `MatchTicket` — a grain-held record with tags, rating, latency samples and party membership |
> | **Pool** — a filter over tickets | a tag + range query, the same requirement algebra as everything else |
> | **Match function** — game-specific logic proposing matches from pools | `IMatchFunction` — the game's code, called with a pool snapshot, returning proposals |
> | **Evaluator** — resolves conflicting proposals | `IMatchEvaluator`, default: highest quality, ties broken by oldest ticket |
> | **Director** — allocates a server for an accepted match | `IQueueGrain` → `IMapGrain.Place` — doc 27's placement, with no second allocator |
>
> The separation of *filtering*, *proposing*, *evaluating* and *allocating* is the insight, and it is
> free to adopt. The Kubernetes deployment topology is not.

Rating ships as two implementations behind `IRatingModel`: **Elo** (one number, transparent, right for
1v1 and for games whose players will ask how it works) and a **Bayesian skill model of the
TrueSkill family** — mean and variance per player, updated by a factor-graph pass, which is what
handles teams, parties, uneven sizes and new players honestly. The framework does not pick; the queue
definition does. Party-aware and role-aware matching, backfill for a player who leaves, and a
latency-band constraint (which is doc 27's region filter reused) complete it.

### Exploration, housing, collections

**Exploration** — points of interest, map discovery with a revealed-area bitmap per character, vistas,
waypoint unlocks, and completion percentages. Small, and the reason it is its own library is that its
state is a bitmap per character per map and nothing else wants that shape.

**Housing** — plots (a `Persistent` shard, doc 27 § Shard kinds), decoration placement with snapping
and surface rules reusing [24](24-blockout-tools.md)'s in-viewport manipulation grammar rather than a
second gizmo stack, permission tiers, visitor access, and durable furniture state. Guild housing is
the same thing with a `IGuildGrain` owner and a permission matrix instead of a single owner.
**Hibernation is what makes this affordable** — ten thousand houses are ten thousand rows, not ten
thousand processes.

**Collections** — pets, mounts owned, skins/transmog wardrobe, titles, toys, cosmetics. All
account-wide, all durable, all in `Live.Progression.Cluster`, and all one mechanism: a set of unlocked
`DefId`s with an unlock source recorded. Transmog additionally needs an appearance override on the
item instance, which is one field and one visual-resolution rule.

---

## Authority — who decides what

The table that stops every feature answering this question its own way. **The client is a renderer and
an input device.** Where it computes, it computes to *predict* or to *display*, never to decide.

| Feature | Realm decides | Grain decides | Client may predict |
|---|---|---|---|
| Movement, mounts, vehicles | position, collision, state | — | yes — owner prediction, already built |
| Abilities, damage, effects | everything | — | cast start, cooldown UI, hit feedback |
| Shooting | hit validity, damage | — | fire, recoil, tracer, impact |
| Inventory moves | validity, capacity, binding | durable result | optimistically, reconciled |
| Loot rolls | the roll (seeded, logged) | pity counters | nothing |
| Quests, events | objective progress | completion, rewards | display only |
| XP, levels, talents | award events | the durable record | display only |
| Trade, auction, mail | the request | **the transaction** | display only |
| Guild, party | membership effects | membership | display only |
| Chat (spatial) | fan-out | — | local echo, corrected |
| Matchmaking | nothing | everything | display only |
| Housing edits | placement validity | durable layout | placement preview |

The single rule underneath: **anything durable is decided by a grain, because a grain is a single
writer** ([27](27-mmo-framework.md) ADR-021). Anything volatile and fast is decided by the realm.
Anything the player must feel instantly is predicted by the client and corrected.

---

## Adding an item in minutes — the end-to-end walk

The claim, traced through pieces that exist:

| Step | What happens | Built? |
|---|---|---|
| 1 | Designer writes `Assets/Items/flamebrand.vxitem` and drops an icon and a prefab beside it | — |
| 2 | Adds `items/flamebrand` to a loot table's `.vxdef` and to a vendor's stock, both by address | — |
| 3 | `vixen content build` — incremental, deterministic, content-hash bundle names | ✅ `Vixen.Editor.Assets` |
| 4 | Publish: changed bundles + the new catalog to the content server | ✅ `Tools/Vixen.ContentServer` |
| 5 | `ContentDiff` classifies the change as **additive** (new addresses, a changed loot table, no schema change) | ⬜ doc 27 § Upgrades |
| 6 | Realms reload their definition registry live; **no restart, no drain** | ⬜ `IDefinitionRegistry.Reload` |
| 7 | Clients fetch the catalog overlay — on next launch, or hot, since the update path never throws | ✅ `Vixen.Assets.ContentUpdate` |
| 8 | `DefId.From("items/flamebrand")` resolves on both ends. The wire already carried it | ⬜ the kernel |
| | **No code was written and no process restarted.** | |

**A quest is the same walk** — `.vxquest`, objectives referencing existing objective types, rewards by
address. A recipe, a vendor, a battleground, an NPC, an event chain: the same walk.

### Where it stops, stated plainly

- **A new objective type, a new effect behaviour, a new damage-pipeline stage, a new currency sink is
  code.** One class in the game's own assembly, a generated codec, a module registration. An
  afternoon, not minutes — and it is a **build** update ([27](27-mmo-framework.md) § Upgrades), so it
  rolls out rather than reloads.
- **A changed replicated component layout is a wire break.** Doc 16 already says renaming a replicated
  component is one; this is the same rule reaching gameplay. `ContentDiff` catches it and it drains.
- **Removing content is never additive.** An address that stacks in ten thousand banks cannot be
  deleted live. Deprecate, drain, then delete.

---

## Testing

| Area | Test |
|---|---|
| Tags | Prefix-match property tests against a string oracle; id assignment stable across builds (it is on the wire) |
| Attributes | Evaluation-order property tests; `RemoveBySource` is exact and order-independent; no float residue after add/remove cycles |
| Effects | Every stacking policy against a table of hand-computed cases; refresh/extend/expiry under tick skips |
| **Inventory** | **The conservation oracle**, and it is the important one: randomised transaction sequences with injected failures, asserting item count and currency are conserved across all containers, always. Runs alongside doc 27's fleet-level conservation test |
| Loot | Distribution tests over large samples against declared weights; pity reaches its guarantee exactly; a drop is reproducible from its event id |
| Combat | Damage-pipeline golden cases; requirement evaluation identical on client and realm (one assembly, asserted by running both) |
| Shooting | Replay a recorded scenario: hit claims validate identically regardless of injected latency inside the window — doc 16's lag-compensation test, extended to weapons |
| Quests/events | State-machine property tests: no objective completes twice; event chains reach a terminal state; scaling is monotone in participants |
| Economy | Idempotency under duplicate delivery for every transaction kind; the trade confirm-lock rejects every last-moment swap in a randomised adversarial sequence |
| Matchmaking | Rating models against published reference sequences; a party is never split; queue times bounded under synthetic arrival traces |
| Definitions | Round-trip every `.vxdef` type; the generated codecs pinned by snapshot tests; **a corpus of real definitions that must survive a catalog rebuild byte-identically** |
| Modules | A game composing an arbitrary subset links, boots and trims; every library's absence is survivable by every other |
| Vertical | `Samples/14-Mmo` — the exit criterion for the whole document |

---

## Cost

| # | Milestone | Deliverable | EM |
|---|---|---|---|
| **G0** | **Kernel** | Tags, `DefId`, `.vxdef` + importer + generators, attributes, modifiers, effects, requirements, RNG, `IGameplayModule` | 2.5 |
| **G1** | **Things** | Items, the container algebra, loot tables + pity + the editor simulator | 3.0 |
| **G2** | **Fighting** | Abilities, casting, cooldowns, damage pipeline, threat, death; shooting with the rewind budget | 3.5 |
| **G3** | **Doing** | Progression, talents, professions, reputation; quests, objectives, dynamic events, world bosses, the graph editor | 4.0 |
| **G4** | **Together** | Parties, squads, guilds, ranks, friends, presence; chat with its three routes and moderation | 1.5 |
| **G5** | **Trading** | Currencies, vendors, trade escrow, auction, mail, price model — all on the ledger | 3.0 |
| **G6** | **Competing** | Instances, lockouts, encounters, raid calendar; arenas, battlegrounds, objectives; matchmaking with both rating models | 3.5 |
| **G7** | **The world** | AI — ⚠ **aggro, spawning and encounter scripting only, on [37](37-ai-behaviour-trees-utility-and-goap.md)'s P0–P6** rather than containing the planners; interaction and gathering; crafting; mounts and vehicles; travel; exploration | 3.5 |
| **G8** | **Owning** | Housing and decoration; collections, transmog, titles, achievements | 1.0 |
| | **Total** | | **25.5** |

With [27](27-mmo-framework.md)'s **16.0**, the whole framework is **≈ 41.5 EM** — near enough the size
of the engine it sits on ([14](14-roadmap.md): ≈ 48). That is the honest number and it is why both
documents are ordered so that stopping is a decision rather than an abandonment.

**Where to stop, if stopping.** G0 is not optional — everything else is definitions and rules over it,
and a game that skips it writes it badly six times. After that the tracks are genuinely independent
and a game takes what its genre needs:

| A game like | Takes |
|---|---|
| an extraction shooter | G0, G2, G1, G6 — and doc 27 stops at L1 |
| an arena / MOBA | G0, G2, G6 — doc 27 stops at L1 |
| a survival co-op | G0, G1, G2, G7 — doc 27 stops at L2 |
| a social builder | G0, G4, G5, G8 — doc 27 needs L3 |
| **an MMORPG** | all of it | |

---

## Risks and open questions

| # | Risk | Severity | Mitigation |
|---|---|---|---|
| G-R1 | **Opinionated becomes a straitjacket.** Every game wants its damage formula, its stacking rule, its loot policy | High | The composition seams are named per feature — pipeline stages, `IMatchFunction`, `IRatingModel`, `IMarketModel`, `IChatFilter`, `IQuestObjective`, `IContainer` policies — and the built-ins are written *through* those seams, so the extension point is the one the engine itself uses |
| G-R2 | **Item duplication through a container transaction** | High | Transactional containers, the conservation oracle in CI, the ledger as the audit trail. Same posture as doc 27's M2 and the same test harness |
| G-R3 | **Twenty libraries is twenty READMEs, twenty test projects and twenty public API baselines** | Medium | Real cost, accepted for removability. The shallow dependency spine is what keeps it from becoming twenty *coupled* libraries |
| G-R4 | **Definition schema churn breaks live content** | Medium | `.vxdef` versioning with a generated migration chain, exactly as ADR-005 specifies for `.meta`; `ContentDiff` refuses a non-additive live apply |
| G-R5 | **The client and realm rules drift**, so prediction mispredicts constantly and it looks like jitter | Medium | `MyGame.Shared` is one assembly both link; doc 16's `MispredictionCount` is the number that catches drift; requirement evaluation is asserted identical by running both in one test |
| G-R6 | **Scope inflation** — every feature here has a version that takes a year | Medium | Each entry's "the engine-side ambition is bounded" line. A planner, not a behaviour library. A price model, not an economy simulation |

| # | Open question | Recommendation |
|---|---|---|
| G-Q1 | One `.vxdef` importer with type tags, or an importer per extension? | **One.** ADR-005's type tag *is* the discriminator; extensions are cosmetic and get editor associations |
| G-Q2 | Does the kernel ship a UI layer for these features? | **No, and this is worth being firm about.** `Vixen.Ui` plus the data model is the answer; a shipped inventory window is a shipped art style. Ship them in `Samples/14-Mmo` as copyable reference instead |
| G-Q3 | Are achievements their own library or part of Collections? | **Collections.** An achievement is an unlock with criteria, criteria are tag queries, and the state shape is identical |
| G-Q4 | Is combat's damage pipeline replaceable wholesale, or only extensible? | **Extensible, with named stages.** A wholesale replacement gets a game a pipeline with none of the tested edge cases and no way back |
| G-Q5 | Should `Vixen.Gameplay` ship an authored "starter ruleset" (a working RPG out of the box)? | **In `Samples/14-Mmo`, not in the library.** A default ruleset in the engine becomes the ruleset everyone ships, and then it is an API |
