// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay.Items;

/// <summary>Every item a build knows, compiled once against a catalog's tag table.</summary>
/// <remarks>
///     <para>
///         The item half of what a <see cref="DefinitionCatalog" /> holds, resolved: names to tags,
///         stat names to <see cref="AttributeId" />s, affix pool addresses to the affixes in them.
///         Built once at load and shared, because a bank window resolving an address per row is a
///         string lookup per row per frame.
///     </para>
///     <para>
///         ⚠ <b>An address that names nothing is a warning rather than a failure.</b> An item whose
///         rarity or affix pool was not built is an item that still exists in ten thousand banks, and
///         a realm that refuses to start over it helps nobody at three in the morning. What it gets
///         instead is no rarity and no affixes, and a line in <see cref="Problems" /> that the content
///         build fails on.
///     </para>
/// </remarks>
public sealed class ItemLibrary {
    readonly Dictionary<uint, ItemTemplate> items;
    readonly Dictionary<uint, AffixTemplate> affixes;
    readonly Dictionary<uint, ItemRarityTemplate> rarities;
    readonly string[] problems;

    ItemLibrary(
        Dictionary<uint, ItemTemplate> items,
        Dictionary<uint, AffixTemplate> affixes,
        Dictionary<uint, ItemRarityTemplate> rarities,
        string[] problems
    ) {
        this.items = items;
        this.affixes = affixes;
        this.rarities = rarities;
        this.problems = problems;
    }

    /// <summary>A library with nothing in it.</summary>
    public static ItemLibrary Empty { get; } = Compile(DefinitionCatalog.Empty);

    /// <summary>How many items it holds.</summary>
    public int Count => items.Count;

    /// <summary>Every item, in address order.</summary>
    public IEnumerable<ItemTemplate> All =>
        items.Values.OrderBy(item => item.Definition.Address, StringComparer.Ordinal);

    /// <summary>What did not resolve. Empty in a build the content pipeline accepted.</summary>
    public IReadOnlyList<string> Problems => problems;

    /// <summary>Compiles every item, affix and rarity in a catalog.</summary>
    /// <param name="catalog">The definitions.</param>
    /// <returns>The library.</returns>
    public static ItemLibrary Compile(DefinitionCatalog catalog) {
        ArgumentNullException.ThrowIfNull(catalog);

        var tags = catalog.Tags;
        var problems = new List<string>();

        var rarities = new Dictionary<uint, ItemRarityTemplate>();

        foreach (var definition in catalog.OfType<ItemRarityDefinition>()) {
            rarities.Add(definition.Id.Value, new(definition, tags.Resolve(definition.Tag)));
        }

        var affixes = new Dictionary<uint, AffixTemplate>();

        foreach (var definition in catalog.OfType<AffixDefinition>()) {
            affixes.Add(
                definition.Id.Value,
                new(
                    definition,
                    [.. definition.Stats.Select(Compile)],
                    [.. definition.Tags.Select(tags.Resolve)],
                    [.. definition.RequiredItemTags.Select(tags.RangeOf)]
                )
            );
        }

        var pools = new Dictionary<uint, AffixPoolDefinition>();

        foreach (var definition in catalog.OfType<AffixPoolDefinition>()) {
            pools.Add(definition.Id.Value, definition);
        }

        var items = new Dictionary<uint, ItemTemplate>();

        foreach (var definition in catalog.OfType<ItemDefinition>()) {
            ItemRarityTemplate? rarity = null;

            if (definition.Rarity.Length > 0
                && !rarities.TryGetValue(DefId.From(definition.Rarity).Value, out rarity)) {
                problems.Add($"'{definition.Address}' has rarity '{definition.Rarity}', which is not in this build.");
            }

            if (definition.Slot.Length > 0 && !tags.TryResolve(definition.Slot, out _)) {
                problems.Add($"'{definition.Address}' goes in slot '{definition.Slot}', which is not a tag here.");
            }

            items.Add(
                definition.Id.Value,
                new(
                    definition,
                    [.. definition.Tags.Select(tags.Resolve)],
                    tags.Resolve(definition.Slot),
                    rarity,
                    [.. definition.Stats.Select(stat => Modifier(stat))],
                    Pool(definition, pools, affixes, problems)
                )
            );
        }

        return new(items, affixes, rarities, [.. problems]);
    }

    /// <summary>Finds an item.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>Its template, or null.</returns>
    public ItemTemplate? Find(DefId id) => items.GetValueOrDefault(id.Value);

    /// <summary>Finds an item, and refuses to carry on without it.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>Its template.</returns>
    /// <exception cref="DefinitionNotFoundException">This build has no such item.</exception>
    public ItemTemplate Get(DefId id) =>
        items.TryGetValue(id.Value, out var item)
            ? item
            : throw new DefinitionNotFoundException(
                $"{id} is not an item this build knows. Either the content was not built, or the peer "
                + "that sent it is running a different one."
            );

    /// <summary>Finds an item by the address it was authored at.</summary>
    /// <param name="address">The address — <c>items/flamebrand</c>.</param>
    /// <returns>Its template, or null.</returns>
    public ItemTemplate? Find(string address) => Find(DefId.From(address));

    /// <summary>Finds an affix.</summary>
    /// <param name="id">Its id, as a <see cref="RolledAffix" /> names it.</param>
    /// <returns>Its template, or null.</returns>
    public AffixTemplate? FindAffix(DefId id) => affixes.GetValueOrDefault(id.Value);

    /// <summary>Finds a rarity.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>Its template, or null.</returns>
    public ItemRarityTemplate? FindRarity(DefId id) => rarities.GetValueOrDefault(id.Value);

    static AffixStat Compile(ItemStatDefinition stat) =>
        new(
            AttributeId.From(stat.Attribute),
            stat.Op,
            stat.Value,
            stat.Maximum > stat.Value ? stat.Maximum : stat.Value
        );

    static Modifier Modifier(ItemStatDefinition stat) =>
        new(AttributeId.From(stat.Attribute), stat.Op, stat.Value, ModifierSource.None);

    static AffixTemplate[] Pool(
        ItemDefinition definition,
        Dictionary<uint, AffixPoolDefinition> pools,
        Dictionary<uint, AffixTemplate> affixes,
        List<string> problems
    ) {
        if (definition.AffixPools.Count == 0) {
            return [];
        }

        var collected = new List<AffixTemplate>();
        var seen = new HashSet<uint>();

        foreach (var address in definition.AffixPools) {
            if (!pools.TryGetValue(DefId.From(address).Value, out var pool)) {
                problems.Add($"'{definition.Address}' rolls from affix pool '{address}', which is not in this build.");

                continue;
            }

            foreach (var affixAddress in pool.Affixes) {
                var id = DefId.From(affixAddress);

                if (!affixes.TryGetValue(id.Value, out var affix)) {
                    problems.Add($"Affix pool '{address}' names '{affixAddress}', which is not in this build.");

                    continue;
                }

                // An affix in two of an item's pools is one affix, not two chances at it.
                if (seen.Add(id.Value)) {
                    collected.Add(affix);
                }
            }
        }

        // Address order, so that the pool a seed picks from is a property of the content rather than
        // of how a designer sorted a list.
        collected.Sort(
            static (left, right) =>
                string.CompareOrdinal(left.Definition.Address, right.Definition.Address)
        );

        return [.. collected];
    }
}
