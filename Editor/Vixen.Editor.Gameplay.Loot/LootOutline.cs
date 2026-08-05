// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Gameplay;
using Vixen.Gameplay.Items;
using Vixen.Gameplay.Loot;

namespace Vixen.Editor.Gameplay.Loot;

/// <summary>One row of a loot tree, flattened for a list a tree view draws.</summary>
/// <param name="Depth">How deep it sits. Zero is the table the outline was taken of.</param>
/// <param name="Table">Which table it belongs to.</param>
/// <param name="Row">Which row of that table, or −1 for the table's own header.</param>
/// <param name="Label">What to write on it.</param>
/// <param name="Share">
///     Its share of its table's weighted pick, from zero to one — or zero for a header and for an
///     independent row, whose <see cref="Chance" /> is the number that matters.
/// </param>
/// <param name="Chance">Its own chance, for an independent row, or zero.</param>
/// <param name="Conditional">Whether conditions decide if it is in the table at all.</param>
/// <param name="IsTable">Whether it rolls a nested table rather than dropping an item.</param>
public readonly record struct LootOutlineRow(
    int Depth,
    DefId Table,
    int Row,
    string Label,
    float Share,
    float Chance,
    bool Conditional,
    bool IsTable
);

/// <summary>Flattens a loot tree into the rows a tree view draws.</summary>
/// <remarks>
///     <para>
///         <b>The authored intent, beside the simulator's observed rates.</b> A designer needs both:
///         the share says what the table was <em>written</em> to do and the simulation says what it
///         does, and a gap between them is either a condition nobody accounted for or a bug.
///     </para>
///     <para>
///         ⚠ <b>The share ignores conditions, and the row says so.</b> Whether a conditional row is
///         in the table depends on a kill this outline does not have, so its share is what it would
///         be if it were — and every other row's share is computed as though every conditional row
///         were present. A view that showed the share without the flag would be showing a number that
///         is right for no actual kill.
///     </para>
/// </remarks>
public static class LootOutline {
    /// <summary>Flattens a table.</summary>
    /// <param name="loot">Where nested tables come from.</param>
    /// <param name="table">Which table.</param>
    /// <param name="items">Where item names come from, or null to label with ids.</param>
    /// <returns>The rows, in authored order, depth-first.</returns>
    public static IReadOnlyList<LootOutlineRow> Of(LootLibrary loot, LootTable table, ItemLibrary? items = null) {
        ArgumentNullException.ThrowIfNull(loot);
        ArgumentNullException.ThrowIfNull(table);

        var rows = new List<LootOutlineRow>();

        Walk(loot, table, items, rows, 0, []);

        return rows;
    }

    static void Walk(
        LootLibrary loot,
        LootTable table,
        ItemLibrary? items,
        List<LootOutlineRow> rows,
        int depth,
        HashSet<uint> open
    ) {
        // The same bound the evaluator has, and for the same reason: a cycle in a loot tree is a
        // content bug, and an outline that recursed for ever would hang the editor rather than the
        // realm. `open` catches the cycle exactly; the depth catches a tree that is merely absurd.
        if (depth >= LootEvaluator.MaximumDepth || !open.Add(table.Id.Value)) {
            return;
        }

        var total = 0f;

        foreach (var entry in table.Entries) {
            total += entry.Weight;
        }

        rows.Add(
            new(
                depth,
                table.Id,
                -1,
                table.Definition.DisplayName is { Length: > 0 } name ? name : table.Definition.Address,
                0f,
                0f,
                false,
                true
            )
        );

        for (var index = 0; index < table.Entries.Length; index++) {
            var entry = table.Entries[index];
            var nested = entry.Table.IsSome ? loot.Find(entry.Table) : null;

            rows.Add(
                new(
                    depth + 1,
                    table.Id,
                    index,
                    Label(entry, nested, items),
                    total > 0f ? entry.Weight / total : 0f,
                    entry.Chance,
                    entry.Conditions.Count > 0,
                    entry.Table.IsSome
                )
            );

            if (nested is not null) {
                Walk(loot, nested, items, rows, depth + 1, open);
            }
        }

        open.Remove(table.Id.Value);
    }

    static string Label(LootEntry entry, LootTable? nested, ItemLibrary? items) {
        if (entry.Table.IsSome) {
            return nested?.Definition.DisplayName is { Length: > 0 } name ? name : entry.Definition.Table;
        }

        var item = items?.Find(entry.Item)?.Definition.DisplayName;

        return item is { Length: > 0 } ? item : entry.Definition.Item;
    }
}
