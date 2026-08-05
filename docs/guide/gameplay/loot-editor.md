---
title: Authoring a loot table
slug: gameplay/loot-editor
kind: guide
area: Gameplay
summary: An editable model over the definition, a flattened outline for a tree view, and a drop simulator that runs the shipped evaluator.
api: [T:Vixen.Editor.Gameplay.Loot.LootTableModel, T:Vixen.Editor.Gameplay.Loot.LootProblem, T:Vixen.Editor.Gameplay.Loot.LootOutline, T:Vixen.Editor.Gameplay.Loot.LootOutlineRow, T:Vixen.Editor.Gameplay.Loot.LootSimulator, T:Vixen.Editor.Gameplay.Loot.LootSimulation, T:Vixen.Editor.Gameplay.Loot.LootItemStatistics, T:Vixen.Editor.Gameplay.Loot.LootPityStatistics]
tags: [gameplay, loot, editor, simulation, balance]
since: 0.1
status: preview
related: [gameplay/loot, gameplay/items, gameplay/randomness]
---

## What it is

Three things a loot-table editor needs and one of them is the interesting one.

- `LootTableModel` — the definition, edited in place, with every gesture as one operation and one
  `Changed`, plus a `Validate` that says everything the content build would refuse.
- `LootOutline` — the tree flattened depth-first, with each weighted row's share of its table's pick.
- `LootSimulator` — ten thousand kills through the **shipped** `LootEvaluator`, reported as rates.

## What it is for

Balancing a table without shipping it. A designer changes a weight and sees what it did, in the same
arithmetic the realm will run — which is doc 28's actual requirement: *"simulated in the editor with
the real evaluator"*.

A simulator with its own arithmetic is a second set of odds, and the one a designer balances against
is the one that is wrong. That is the whole argument for the evaluator being a library.

## Using it

Wrap the definition in a model, run the simulator after each edit, and draw the outline beside its
results — the outline is what the table was *written* to do and the simulation is what it does, and a
gap between them is a condition nobody accounted for.

⚠ **A simulation starts from a fresh pity store.** One that inherited a live player's bad luck would
report a rate nobody else will ever see. It also means the simulator and a hand-written loop only
agree if both are given a store: a pity row that drops consumes an extra draw from the stream.

⚠ **`EmptyEvents` is the figure to look at first.** A table whose weighted rows are all conditional
drops nothing on an ordinary kill, and the authored file looks perfectly reasonable.

⚠ **`GuaranteeHeld` is a bug report, not a balance figure.** A drought longer than the pity policy's
guarantee is a ramp that never starts or an evaluator that stopped honouring the policy.

⚠ **A conditional row's share is true of no actual kill** — it is computed as though every conditional
row were present — so the row carries a `Conditional` flag and a view must show it.

⚠ **Moving a row is a real edit.** The evaluator runs independent rows in authored order, so
reordering changes what every recorded event id produces. It is not a sort.

## Examples

Editing, validating, and previewing after each gesture:

```csharp compile
using System.Collections.Generic;
using Vixen.Editor.Gameplay.Loot;
using Vixen.Gameplay;
using Vixen.Gameplay.Items;
using Vixen.Gameplay.Loot;

static class Authoring {
    public static LootSimulation Preview(
        LootLibrary loot,
        ItemLibrary items,
        LootTableModel model,
        DefId table
    ) {
        model.Edit(0, entry => entry.Weight = 4f);

        // Everything the content build would refuse, attributed to a row so it can be highlighted.
        IReadOnlyList<LootProblem> problems = model.Validate();

        _ = problems;

        // The same evaluator the realm runs, over a run somebody else can reproduce.
        return LootSimulator.Run(loot, loot.Get(table), items, events: 10000, firstEventId: 1);
    }
}
```

Reading the result the way a table view would:

```csharp compile
using System;
using Vixen.Editor.Gameplay.Loot;

static class Report {
    public static void Write(LootSimulation simulation, Action<string> line) {
        line($"{simulation.EmptyEvents} of {simulation.Events} kills dropped nothing");

        foreach (var item in simulation.Items) {
            line($"{item.Name}: {item.RateOver(simulation.Events):P2} of kills, {item.PerEvent(simulation.Events):F2} each");
        }

        if (simulation.Pity is { } pity && !pity.GuaranteeHeld) {
            line($"a drought of {pity.LongestDrought} exceeded the guarantee of {pity.Guarantee}");
        }
    }
}
```

Drawing the tree beside it:

```csharp compile
using System;
using Vixen.Editor.Gameplay.Loot;
using Vixen.Gameplay.Items;
using Vixen.Gameplay.Loot;

static class Tree {
    public static void Write(LootLibrary loot, LootTable table, ItemLibrary items, Action<string> line) {
        foreach (var row in LootOutline.Of(loot, table, items)) {
            var share = row.Chance > 0f ? $"{row.Chance:P1} chance" : $"{row.Share:P1} of the pick";

            line($"{new string(' ', row.Depth * 2)}{row.Label} — {share}{(row.Conditional ? " (conditional)" : "")}");
        }
    }
}
```

## See also

- [Loot tables](gameplay/loot) — the evaluator this previews, and the rules it enforces.
- [Items](gameplay/items) — where the names in the report come from.
- [Gameplay randomness](gameplay/randomness) — why a simulation is reproducible at all.
