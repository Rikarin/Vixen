// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Gameplay.Items;

namespace Vixen.Gameplay.Loot;

/// <summary>Every loot table and salvage recipe a build knows, compiled once.</summary>
public sealed class LootLibrary {
    readonly Dictionary<uint, LootTable> tables;
    readonly Dictionary<uint, SalvageRecipe> salvageByItem;
    readonly SalvageRecipe[] salvageByTag;
    readonly string[] problems;

    LootLibrary(
        Dictionary<uint, LootTable> tables,
        Dictionary<uint, SalvageRecipe> salvageByItem,
        SalvageRecipe[] salvageByTag,
        string[] problems
    ) {
        this.tables = tables;
        this.salvageByItem = salvageByItem;
        this.salvageByTag = salvageByTag;
        this.problems = problems;
    }

    /// <summary>A library with nothing in it.</summary>
    public static LootLibrary Empty { get; } = Compile(DefinitionCatalog.Empty);

    /// <summary>How many tables it holds.</summary>
    public int Count => tables.Count;

    /// <summary>Every table, in address order.</summary>
    public IEnumerable<LootTable> All =>
        tables.Values.OrderBy(table => table.Definition.Address, StringComparer.Ordinal);

    /// <summary>What did not resolve, and what a row said that cannot be true at once.</summary>
    public IReadOnlyList<string> Problems => problems;

    /// <summary>Compiles every table and salvage recipe in a catalog.</summary>
    /// <param name="catalog">The definitions.</param>
    /// <returns>The library.</returns>
    public static LootLibrary Compile(DefinitionCatalog catalog) {
        ArgumentNullException.ThrowIfNull(catalog);

        var tags = catalog.Tags;
        var problems = new List<string>();
        var tables = new Dictionary<uint, LootTable>();

        foreach (var definition in catalog.OfType<LootTableDefinition>()) {
            var rows = new LootEntry[definition.Entries.Count];

            for (var index = 0; index < rows.Length; index++) {
                var entry = definition.Entries[index];

                if (entry.Weight > 0f && entry.Chance > 0f) {
                    problems.Add(
                        $"'{definition.Address}' row {index} has both a weight and a chance. A row is "
                        + "either one of the weighted picks or an independent roll, and one that is "
                        + "both means two different things at once."
                    );
                }

                if (entry.Item.Length > 0 == entry.Table.Length > 0) {
                    problems.Add(
                        $"'{definition.Address}' row {index} names "
                        + (entry.Item.Length > 0 ? "both an item and a table." : "neither an item nor a table.")
                    );
                }

                rows[index] = new(
                    entry,
                    DefId.From(entry.Item),
                    DefId.From(entry.Table),
                    RequirementSet.Compile(entry.Conditions, tags)
                );
            }

            tables.Add(definition.Id.Value, new(definition, rows));
        }

        var byItem = new Dictionary<uint, SalvageRecipe>();
        var byTag = new List<SalvageRecipe>();

        foreach (var definition in catalog.OfType<SalvageDefinition>()) {
            var table = DefId.From(definition.Table);

            if (!tables.ContainsKey(table.Value)) {
                problems.Add($"'{definition.Address}' salvages into '{definition.Table}', which is not a table here.");
            }

            var recipe = new SalvageRecipe(definition, table, [.. definition.ItemTags.Select(tags.RangeOf)]);

            if (definition.Item.Length > 0) {
                byItem[DefId.From(definition.Item).Value] = recipe;
            } else {
                byTag.Add(recipe);
            }
        }

        // Address order, so which recipe wins for an item matching two of them is a property of the
        // content rather than of the order a catalog enumerated.
        byTag.Sort(
            static (left, right) =>
                string.CompareOrdinal(left.Definition.Address, right.Definition.Address)
        );

        foreach (var table in tables.Values) {
            foreach (var entry in table.Entries) {
                if (entry.Table.IsSome && !tables.ContainsKey(entry.Table.Value)) {
                    problems.Add(
                        $"'{table.Definition.Address}' rolls nested table '{entry.Definition.Table}', "
                        + "which is not in this build."
                    );
                }
            }
        }

        return new(tables, byItem, [.. byTag], [.. problems]);
    }

    /// <summary>Finds a table.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>It, or null.</returns>
    public LootTable? Find(DefId id) => tables.GetValueOrDefault(id.Value);

    /// <summary>Finds a table by the address it was authored at.</summary>
    /// <param name="address">The address.</param>
    /// <returns>It, or null.</returns>
    public LootTable? Find(string address) => Find(DefId.From(address));

    /// <summary>Finds a table, and refuses to carry on without it.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>It.</returns>
    /// <exception cref="DefinitionNotFoundException">This build has no such table.</exception>
    public LootTable Get(DefId id) =>
        Find(id) ?? throw new DefinitionNotFoundException($"{id} is not a loot table this build knows.");

    /// <summary>What breaking an item down gives back.</summary>
    /// <param name="item">The item.</param>
    /// <returns>The table it salvages into, or null.</returns>
    /// <remarks>
    ///     An exact address wins over a tag match, because a recipe naming one item is a designer
    ///     saying "this one is different" and a tag rule is the default it is different from.
    /// </remarks>
    public LootTable? SalvageFor(ItemTemplate item) {
        ArgumentNullException.ThrowIfNull(item);

        if (salvageByItem.TryGetValue(item.Id.Value, out var exact)) {
            return Find(exact.Table);
        }

        foreach (var recipe in salvageByTag) {
            if (recipe.Matches(item)) {
                return Find(recipe.Table);
            }
        }

        return null;
    }

    /// <summary>A compiled salvage recipe.</summary>
    sealed class SalvageRecipe(SalvageDefinition definition, DefId table, GameplayTagRange[] itemTags) {
        public SalvageDefinition Definition { get; } = definition;

        public DefId Table { get; } = table;

        public bool Matches(ItemTemplate item) {
            if (itemTags.Length == 0) {
                return false;
            }

            foreach (var range in itemTags) {
                if (!item.HasTagUnder(range)) {
                    return false;
                }
            }

            return true;
        }
    }
}
