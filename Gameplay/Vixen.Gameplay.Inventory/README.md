# Vixen.Gameplay.Inventory

One `IContainer`, one transaction type, and one place a duplication bug could be.

Spec: [docs/plan/28](../../docs/plan/28-gameplay-framework.md) § Inventory — *"the part that has to be
exactly right because it is where duplication bugs live"* — the second half of **G1**.

## State

**Built: containers with policies, the five operations, atomic transactions, and the conservation
oracle. 27 tests**, one of which is 20 000 randomised transactions with injected failures.

| | |
|---|---|
| `ContainerId` · `SlotRef` | A hash of a name, and the coordinate every operation is written in. |
| `ContainerPolicy` | What a container takes, whether it stacks, whether it takes bound items, and which binding trigger arriving there fires. |
| `Container` | Slots, a policy, and — for an equipment set — one tag per slot. Read-only from the outside. |
| `ContainerTransaction` | `Move` · `Swap` · `Insert` · `Add` · `Remove`. Split, merge and equip are `Move`. |
| `ContainerSet` | Every container one owner has, and the only thing allowed to change them. |
| `ContainerResult` · `ContainerChange` · `ContainerFailure` | What happened, what moved, and why not. |
| `InventoryModule` | No definition types; a bag is a rule, not content. |

## The four things worth knowing before reading the code

### The container is read-only and the set is the only writer

`Container` hands out its slots as a `ReadOnlySpan`. That is not politeness: a mutation outside a
transaction is a mutation that cannot be rolled back, and a half-applied two-step move has either
destroyed an item or duplicated one. Everything goes through
`ContainerSet.Apply`, which snapshots every container a transaction touches **before the first
step** and restores all of them if any step fails.

Snapshot-and-restore rather than an undo log, deliberately: an undo log is a second implementation of
the same operations, with its own bugs, and the thing it is protecting is the one thing that must not
have any.

### The conservation oracle found a real bug on its first run

Doc 28 § Testing calls it "the important one". It is: 20 000 transactions of one to four random
operations each, over four containers, with most of them failing — asserting that the total item
count moves by exactly what the reported changes said arrived from or went to nowhere, and that a
refused transaction changes nothing at all.

⚠ **It immediately found that a stack dragged onto itself was destroyed.** The merge path writes the
destination and then the source; for one slot those are two writes to the same slot, and the second
is `stack - count`. Dragging a stack onto itself is something a player does by accident several times
an hour, and no amount of reading the code had noticed. `AStackDraggedOntoItselfIsNotDestroyed` is the
named regression; the oracle is what would catch the next one.

The oracle also asserts that the run was worth having — over a thousand transactions applied *and*
over a thousand refused, so a change that made everything fail could not pass it quietly.

### All of it or none of it, including `Add`

`Add` puts an item anywhere in a container that will take it, filling existing stacks first. If the
whole quantity does not fit, **nothing** goes in. Putting 150 of 200 ore in the bag and dropping the
rest is how "you looted it and it vanished" happens, and the caller is the only thing that knows
whether the right answer is to mail the remainder, leave it in the corpse or refuse the loot.

### Binding is a trigger, not a flag

A bag fires `OnPickup`; an equipment set fires `OnEquip`. An item binds when its own policy is the
trigger the container it arrived in fires, and is untouched otherwise. A single "binds on insert"
flag would bind a bind-on-equip sword the moment it was looted — which is the difference between an
item a player can sell and one they cannot.

⚠ **A bound stack and an unbound stack of one item do not merge**, because merging them would bind
the unbound half. Stack compatibility is definition, seed, durability *and* binding.

## Why a move onto an occupied slot is refused rather than swapped

A UI drag onto an occupied slot means "swap" and a scripted move means "put it there". Making `Move`
guess would make the scripted case silently do something else, so `Move` reports `Occupied` and says
to swap; the UI issues `Swap`. Two names for two intentions.

## What is owed

- **Per-instance extras.** Sockets, a transmog appearance and a custom name are per-copy data of
  variable size, which a sixteen-byte `ItemInstance` deliberately cannot hold. The side table belongs
  here, because the container is what owns the instance — but nothing needs it until G8's transmog
  and G1's gem-socketing UI exist.
- **The ledger.** `ContainerResult.Changes` is what a ledger entry is written from, and doc 28 wants
  one whenever a mutation crosses an ownership boundary. The kernel does not know what an owner is;
  wiring the changes into doc 27's ledger is `Vixen.Live.Persistence`'s side of G5.
- **Currencies are not items** and do not live in a container. They are G5's, with their own caps and
  their own conservation test.
- **Capacity by weight or by volume.** Slots only, for now. A weight model is a policy field and a
  second capacity check, and no shipped container needs one yet.
