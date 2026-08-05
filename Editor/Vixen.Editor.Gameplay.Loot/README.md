# Vixen.Editor.Gameplay.Loot

The authoring half of a loot table: an editable model over the definition, the flattened outline a
tree view draws, and a drop simulator that runs the **shipped evaluator** rather than an
approximation of it.

Spec: [docs/plan/28](../../docs/plan/28-gameplay-framework.md) § Library structure —
*"a loot table editor + a drop simulator that runs the real code"* — and the last line of **G1**.

## State

**Built: the model, the outline and the simulator. 21 tests.** The view is owed — this is the half
that can be asserted, and the half a view would otherwise reimplement.

| | |
|---|---|
| `LootTableModel` | The definition, edited in place. Add, remove, move, edit, rolls, pity, snapshot, restore. |
| `LootProblem` · `Validate` | Everything the content build refuses, shown while typing and attributed to a row. |
| `LootOutline` · `LootOutlineRow` | The tree flattened depth-first, with each row's authored share of its table's pick. |
| `LootSimulator` · `LootSimulation` | Ten thousand kills of the real evaluator, as rates a designer can read. |
| `LootItemStatistics` · `LootPityStatistics` | What each item did, and what the run of bad luck did. |

## The four things worth knowing before reading the code

### It runs `LootEvaluator`, and that is the whole point

A simulator with its own arithmetic is a second set of odds, and the one a designer balances against
is the one that is wrong. `TheSimulationAgreesWithTheEvaluatorEventForEvent` rolls both over the same
two thousand events and compares the totals exactly.

⚠ **The comparison only holds when both are given a pity store.** A pity row that drops consumes an
extra draw from the stream, so the same events with and without a store are genuinely different
rolls. That is also why a simulation starts from a *fresh* store: one that inherited a live player's
bad luck would report a rate nobody else will ever see.

### The document *is* the definition

There is no editor-side model of a table to keep in step with the runtime's — the bargain
`Vixen.Editor.Ai` makes, for the same reason. Every gesture is one operation and one `Changed`, so an
undo stack is snapshot-in, snapshot-out.

⚠ **The snapshot is a deep copy.** The rows are mutable classes the YAML binder needs settable, so a
record's shallow `with` would hand the undo stack the very list the next edit mutates.
`ASnapshotIsDeepEnoughToUndoARowEdit` is the test.

⚠ **Moving a row is a real edit, not a sort.** The evaluator runs independent rows in authored order,
so reordering them changes what every recorded event id produces. An editor that presented it as
tidying would be lying.

### `EmptyEvents` is the number a designer least expects to see

A table whose weighted rows are all conditional drops **nothing** on an ordinary kill, and the
authored file looks perfectly reasonable. The simulation reports it as a first-class figure rather
than leaving it to be inferred from a table of rates that add up to less than one.

### `GuaranteeHeld` is a bug report, not a balance figure

A drought longer than the pity policy's guarantee is either a content mistake — a ramp that never
starts, which `Validate` also catches — or an evaluator that stopped honouring the policy. Neither is
something a designer should have to notice by reading rates.

The pity figures are counted by a store that wraps `MemoryPityStore` and tallies what it is told,
rather than inferred from the drops. ⚠ **The two are different questions**: whether a pity row dropped
is visible in the result, but whether the evaluator counted an *attempt* is not — a row excluded by
its conditions is not a miss, and counting it as one would report a drought the policy never promised
anything about.

## The share is the authored intent, and it ignores conditions

`LootOutline` gives each weighted row its share of its table's pick, so a designer sees what the table
was *written* to do beside what the simulation says it does — a gap between them is a condition nobody
accounted for, or a bug.

⚠ **A conditional row's share is what it would be if the row were in**, and every other row's share is
computed as though it were. That is true of no actual kill, so the row carries a `Conditional` flag
and a view that hid it would be showing a number for a kill that never happens.

## What is owed

- **The view.** A tree of rows beside a rates table and a pity curve, in `Vixen.Editor.AssetEditors`
  where the other asset editors' views live. Everything it needs to draw is here and asserted;
  what is left is markup.
- **A `.vxdef` document and undo wiring.** `UtilitySetDocument` is the shape — the YAML end plus an
  `EditorDocument` undo stack over `Snapshot`/`Restore`.
- **Simulating a whole drop, not a table.** A designer eventually wants "what does this boss give a
  party of five over a week", which is the distribution plus participation and lockouts — G3 and G6.
